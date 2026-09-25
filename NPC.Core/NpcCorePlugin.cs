using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using NPC.Core.Interaction;
using NPC.Core.Navigation;
using NPC.Core.Saves;
using NPC.Core.World;
using UnityEngine;

namespace NPC.Core
{
    /// <summary>
    /// NPC.Core - the shared library NPC mods for Isolated Inhale build on. One copy serves every mod:
    /// depend on it with <c>[BepInDependency(NpcCorePlugin.Guid, NpcCorePlugin.Version)]</c>, never ship it
    /// inside your own DLL. See README.md and docs/api.md.
    /// </summary>
    [BepInPlugin(Guid, "NPC.Core", Version)]
    [BepInProcess("Isolated Inhale.exe")]
    public sealed class NpcCorePlugin : BaseUnityPlugin
    {
        public const string Guid = "com.bytenull1.npccore";
        public const string Version = "1.0.0";

        /// <summary>
        /// The mod NPC.Core was extracted from; its settings for what moved here are imported once.
        /// </summary>
        private const string LegacyConfigFile = "com.bytenull1.yourbuddy.cfg";

        internal static ManualLogSource? PluginLogger;
        internal static ConfigEntry<int>? ConfigDebugLevel;
        private static ConfigEntry<bool>? _configBundledGraph;
        internal static ConfigEntry<KeyboardShortcut> ConfigEditorToggleKey;
        internal static ConfigEntry<KeyboardShortcut> ConfigEditorPlaceKey;
        internal static ConfigEntry<KeyboardShortcut> ConfigEditorDeleteKey;
        internal static ConfigEntry<KeyboardShortcut> ConfigEditorLinksKey;
        internal static ConfigEntry<KeyboardShortcut> ConfigEditorForceLinkKey;
        internal static ConfigEntry<KeyboardShortcut> ConfigEditorClearLinksKey;
        internal static ConfigEntry<KeyboardShortcut> ConfigEditorTypeKey;
        internal static ConfigEntry<KeyboardShortcut> ConfigEditorSaveKey;

        /// <summary>
        /// Whether owners the player never edited come from the graph shipped in this DLL.
        /// </summary>
        internal static bool UseBundledGraph => _configBundledGraph == null || _configBundledGraph.Value;

        /// <summary>
        /// The in-game node editor (F8), made again if its object is gone; null before the plugin has loaded.
        /// </summary>
        internal static NodeEditor? NodeEditor
        {
            get
            {
                EnsureNodeEditor();
                return _nodeEditor;
            }
        }
        private static NodeEditor? _nodeEditor;
        private static bool _nodeEditorMade;
        private static bool _copiesChecked;

        /// <summary>
        /// At plugin load, on every access and from the shared tick: docs/invariants.md#plugin-objects-outlive-the-first-scene
        /// </summary>
        internal static void EnsureNodeEditor()
        {
            if (PluginLogger == null || _nodeEditor != null) return;

            if (_nodeEditorMade) NpcLog.Core.LogWarning("[editor] Node editor object was destroyed - creating it again");
            _nodeEditorMade = true;
            _nodeEditor = PersistentObject("NPC.Core NodeEditor").AddComponent<NodeEditor>();
        }

        /// <summary>
        /// A root object that outlives scene loads. Hidden and not saved, as BepInEx's HideManagerGameObject makes
        /// its own: docs/invariants.md#plugin-objects-outlive-the-first-scene
        /// </summary>
        internal static GameObject PersistentObject(string name)
        {
            GameObject go = new(name) { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            return go;
        }

        private void Awake()
        {
            PluginLogger = Logger;
            // Before the first Bind writes it: only a first run imports the old settings.
            bool firstRun = !File.Exists(Config.ConfigFilePath);

            ConfigEntry<int> debugLevel = Config.Bind("Debug", "DebugLevel", 1,
                "How much every NPC mod logs: 0 = quiet (warnings only), 1 = normal (state changes, doors, " +
                "stuck diagnostics), 2 = thinking (path planning, the gate inventory and the gate-frame audit), " +
                "3 = obstacle (per-frame obstacle reports). Can also be changed at runtime with 'debug_level'.");
            ConfigEntry<bool> bundledGraph = Config.Bind("Navigation", "BundledGraph", true,
                "Use the ready-made nav graph shipped with NPC.Core for stations and the ship. " +
                "Your own nodes always win: the moment you edit anything on a ship or station, " +
                "that one becomes yours and updates stop changing it. " +
                "'npc_node bundled' shows which is which, 'npc_node unfork <owner>' hands one back.");
            ConfigEditorToggleKey = Config.Bind("NodeEditor", "EditorToggleKey", new KeyboardShortcut(KeyCode.F8),
                "Toggles the node editor overlay. Can also be toggled with the 'node_editor' console command.");
            ConfigEditorPlaceKey = Config.Bind("NodeEditor", "EditorPlaceKey", new KeyboardShortcut(KeyCode.Insert),
                "Places a node at the player's position (Numpad0 works as an always-on alternative).");
            ConfigEditorDeleteKey = Config.Bind("NodeEditor", "EditorDeleteKey", new KeyboardShortcut(KeyCode.Delete),
                "Removes the node nearest to the player.");
            ConfigEditorLinksKey = Config.Bind("NodeEditor", "EditorLinksKey", new KeyboardShortcut(KeyCode.L),
                "Toggles rendering of node connections. Previously C, which fights the game's crouch key.");
            ConfigEditorForceLinkKey = Config.Bind("NodeEditor", "EditorForceLinkKey", new KeyboardShortcut(KeyCode.K),
                "Press once to mark the selected node, then again at another node to link them - the connection " +
                "always works and skips the hull probe (use for ramps/stair flights the probe rejects). Press again " +
                "on an already-linked pair to remove the link.");
            ConfigEditorClearLinksKey = Config.Bind("NodeEditor", "EditorClearLinksKey", new KeyboardShortcut(KeyCode.U),
                "Removes every link of the selected node at once.");
            ConfigEditorTypeKey = Config.Bind("NodeEditor", "EditorTypeKey", new KeyboardShortcut(KeyCode.T),
                "Cycles the selected node's type between Ground and Stair. A Stair node may be entered up to 2m " +
                "off-level, from right at its foot or head.");
            ConfigEditorSaveKey = Config.Bind("NodeEditor", "EditorSaveKey", new KeyboardShortcut(KeyCode.F6),
                "Saves the node graph. Never bind this to F5: that key is the game's own QuickSave.");

            ConfigDebugLevel = debugLevel;
            _configBundledGraph = bundledGraph;
            if (firstRun) ImportLegacySettings(debugLevel, bundledGraph);

            Logger.LogInfo("[mod] Initializing...");
            WarnAboutCopiedFiles();
            ApplyPatches();
            GameInternals.ResolveAll();
            CoreCommands.Register();

            // Door knowledge is the same for every NPC, so the graph asks it once:
            // docs/invariants.md#door-knowledge-is-shared
            NavGraph.SegmentBlockedByDoor = NpcDoors.SegmentBlockedByDoor;
            CoreSidecar.Register();
            NpcLifecare.Init();

            EnsureNodeEditor();
            NpcTalkWindow.Ensure();
        }

        /// <summary>
        /// Another NPC.Core.dll under plugins: BepInEx loads one, and a mod may bind to the other.
        /// docs/invariants.md#one-core-instance
        /// </summary>
        private void WarnAboutCopiedFiles()
        {
            string own = Path.GetFullPath(Info.Location);
            try
            {
                foreach (string file in Directory.GetFiles(Paths.PluginPath, "NPC.Core.dll", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFullPath(file), own, StringComparison.OrdinalIgnoreCase)) continue;

                    Logger.LogWarning("[mod] Another NPC.Core.dll at " + file + " - keep only one copy in BepInEx/plugins");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[mod] Could not look for other NPC.Core copies: " + ex.Message);
            }
        }

        /// <summary>
        /// Once, from the first tick, when every plugin has loaded: an assembly that carries its own NPC.Core
        /// types (merged in) has its own registry and graph. docs/invariants.md#one-core-instance
        /// </summary>
        internal static void CheckForMergedCopies()
        {
            if (_copiesChecked) return;

            _copiesChecked = true;
            Assembly own = typeof(NpcRegistry).Assembly;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == own) continue;

                Type? copy;
                try
                {
                    copy = assembly.GetType(typeof(NpcRegistry).FullName, false);
                }
                catch (Exception)
                {
                    continue;
                }
                if (copy == null) continue;

                NpcLog.Core.LogError("[mod] " + assembly.GetName().Name + " carries its own copy of NPC.Core: its NPCs " +
                    "cannot see other mods' NPCs, nor theirs its own. It must reference NPC.Core.dll instead");
            }
        }

        /// <summary>
        /// Carries over what a player set for the graph, the editor and logging while these lived in
        /// YourBuddy. Reads the old file without writing it.
        /// </summary>
        private void ImportLegacySettings(ConfigEntry<int> debugLevel, ConfigEntry<bool> bundledGraph)
        {
            string path = Path.Combine(Paths.ConfigPath, LegacyConfigFile);
            if (!File.Exists(path)) return;

            try
            {
                ConfigFile legacy = new(path, false) { SaveOnConfigSet = false };
                debugLevel.Value = legacy.Bind("General", "DebugLevel", debugLevel.Value).Value;
                bundledGraph.Value = legacy.Bind("Navigation", "BundledGraph", bundledGraph.Value).Value;
                Import(legacy, ConfigEditorToggleKey);
                Import(legacy, ConfigEditorPlaceKey);
                Import(legacy, ConfigEditorDeleteKey);
                Import(legacy, ConfigEditorLinksKey);
                Import(legacy, ConfigEditorForceLinkKey);
                Import(legacy, ConfigEditorClearLinksKey);
                Import(legacy, ConfigEditorTypeKey);
                Import(legacy, ConfigEditorSaveKey);
                Logger.LogInfo("[mod] Imported the debug level, bundled-graph and node editor settings from " + LegacyConfigFile);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[mod] Could not import settings from " + LegacyConfigFile + ": " + ex.Message);
            }
        }

        private static void Import(ConfigFile legacy, ConfigEntry<KeyboardShortcut> entry)
        {
            entry.Value = legacy.Bind(entry.Definition.Section, entry.Definition.Key, entry.Value).Value;
        }

        /// <summary>
        /// Applies each patch group on its own, so a game update that removes one target
        /// disables that one feature, named in the log, instead of every patch after it.
        /// </summary>
        private void ApplyPatches()
        {
            Harmony harmony = new(Guid);
            int applied = 0, total = 0;
            foreach (Type group in typeof(Patches).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (!group.IsDefined(typeof(HarmonyPatch), false)) continue;

                total++;
                try
                {
                    harmony.CreateClassProcessor(group).Patch();
                    applied++;
                }
                catch (Exception ex)
                {
                    string feature = group.GetCustomAttribute<DescriptionAttribute>()?.Description ?? group.Name;
                    Logger.LogError($"[mod] Patch group '{group.Name}' failed - {feature} disabled: {ex}");
                }
            }

            if (applied == total) Logger.LogInfo($"[mod] All {total} patch groups applied.");
            else Logger.LogWarning($"[mod] {applied} of {total} patch groups applied - see errors above.");
        }
    }
}
