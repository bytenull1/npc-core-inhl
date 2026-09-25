using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using NPC.Core.Navigation;
using NPC.Core.World;
using Space;
using UnityEngine;

namespace NPC.Core
{
    /// <summary>
    /// The console commands NPC.Core owns: the nav graph, the gate inventory, the node editor, the
    /// shared debug level and the AI overrides. docs/reference.md#2-console-commands
    /// </summary>
    internal static class CoreCommands
    {
        private const string Owner = "NPC.Core";

        internal static void Register()
        {
            // What the doorway carve-out decided for every gate, and from what. docs/probes.md
            NpcConsole.Register(Owner, "npc_gates", delegate
            {
                foreach (string line in NavProbe.DescribeGates()) NpcConsole.Print(line);
            });
            NpcConsole.Register(Owner, "npc_node", Node);
            NpcConsole.Register(Owner, "node_editor", delegate
            {
                NodeEditor? editor = NpcCorePlugin.NodeEditor;
                if (editor == null)
                {
                    NpcConsole.Print("Node editor not available");
                    return;
                }
                editor.Active = !editor.Active;
                NpcConsole.Print("Node editor: " + (editor.Active ? "ON" : "OFF") +
                                 " - " + KeyName(NpcCorePlugin.ConfigEditorToggleKey) + "=toggle, " +
                                 KeyName(NpcCorePlugin.ConfigEditorPlaceKey) + "/Numpad0=place, " +
                                 KeyName(NpcCorePlugin.ConfigEditorDeleteKey) + "=remove, " +
                                 KeyName(NpcCorePlugin.ConfigEditorLinksKey) + "=connections, " +
                                 KeyName(NpcCorePlugin.ConfigEditorForceLinkKey) + "=link (2 presses), " +
                                 KeyName(NpcCorePlugin.ConfigEditorClearLinksKey) + "=clear the node's links, " +
                                 KeyName(NpcCorePlugin.ConfigEditorTypeKey) + "=node type, " +
                                 KeyName(NpcCorePlugin.ConfigEditorSaveKey) + "=save");
            });
            NpcConsole.Register(Owner, "debug_level", delegate (string[] args)
            {
                if (args.Length < 1 || !int.TryParse(args[0], out int level))
                {
                    NpcConsole.Print("Usage: debug_level <0-3> (current: " + NpcLog.Level + ")");
                    return;
                }
                NpcLog.Level = Mathf.Clamp(level, 0, 3);
                NpcConsole.Print("Debug level: " + NpcLog.Level +
                                 (NpcLog.Level == 0 ? " (quiet)" :
                                  NpcLog.Level == 1 ? " (normal)" :
                                  NpcLog.Level == 2 ? " (thinking)" : " (obstacle)"));
            });

            // Debug only, and deliberately not persisted: docs/reference.md#2-console-commands
            NpcConsole.Register(Owner, "ai_disable", delegate (string[] args)
            {
                string target = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
                // "buddy" is what YourBuddy's players typed before NPC.Core owned the command.
                if (target == "buddy") target = "npc";
                if (target != "npc" && target != "monster" && target != "all")
                {
                    NpcConsole.Print("Usage: ai_disable [npc|monster|all] [on|off] - now: " + NpcMonster.Describe());
                    return;
                }

                bool wasOn = target == "monster" ? NpcMonster.MonsterDisabled
                    : target == "npc" ? NpcMonster.NpcsDisabled
                    : NpcMonster.NpcsDisabled && NpcMonster.MonsterDisabled;
                bool on = Toggle(args, 1, wasOn);

                if (target != "monster") NpcMonster.NpcsDisabled = on;

                if (target != "npc") NpcMonster.MonsterDisabled = on;

                NpcMonster.Apply();
                NpcConsole.Print("AI: " + NpcMonster.Describe());
            });
            NpcConsole.Register(Owner, "ai_notarget", delegate (string[] args)
            {
                NpcMonster.NoTarget = Toggle(args, 0, NpcMonster.NoTarget);
                NpcMonster.Apply();
                NpcConsole.Print("AI: " + NpcMonster.Describe());
            });
        }

        /// <summary>
        /// "on"/"off"/"true"/"false" at args[index], or a flip when it is absent.
        /// </summary>
        private static bool Toggle(string[] args, int index, bool current)
        {
            if (args.Length <= index) return !current;

            string value = args[index];
            if (bool.TryParse(value, out bool parsed)) return parsed;

            if (value.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;

            if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;

            return !current;
        }

        private static void Node(string[] args)
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "add")
            {
                Player? player = NpcPlayer.Pilot;
                if (player == null || player.Controller == null)
                {
                    NpcConsole.Print("No player");
                    return;
                }
                NavGraph.AddNode(player.Controller.CachedTransform.position);
                NpcConsole.Print("Node added at your position (total " + NavGraph.NodeCount + ")");
            }
            else if (sub == "count")
            {
                NpcConsole.Print("Nav nodes: " + NavGraph.DescribeNodes());
            }
            else if (sub == "clear")
            {
                NavGraph.Clear();
                NavGraph.Save();
                NpcConsole.Print("Nav graph cleared");
            }
            else if (sub == "save")
            {
                NavGraph.Save();
                NpcConsole.Print("Nav graph saved (" + NavGraph.NodeCount + " nodes)");
            }
            else if (sub == "list")
            {
                List<Vector3> nodes = NavGraph.GetAllNodesWorld();
                NpcConsole.Print("Nav nodes (" + nodes.Count + "):");
                for (int i = 0; i < nodes.Count; i++) NpcConsole.Print("  #" + i + ": " + nodes[i].ToString("0.00"));
            }
            else if (sub == "remove" || sub == "delete")
            {
                if (args.Length < 2 || !int.TryParse(args[1], out int idx))
                {
                    NpcConsole.Print("Usage: npc_node remove <index>");
                    return;
                }
                NpcConsole.Print(NavGraph.RemoveNode(idx) ? "Node #" + idx + " removed" : "Invalid index");
            }
            else if (sub == "link")
            {
                if (args.Length < 3 || !int.TryParse(args[1], out int aIdx) || !int.TryParse(args[2], out int bIdx))
                {
                    NpcConsole.Print("Usage: npc_node link <a> <b>");
                    return;
                }
                if (aIdx < 0 || aIdx >= NavGraph.NodeCount || bIdx < 0 || bIdx >= NavGraph.NodeCount || aIdx == bIdx)
                {
                    NpcConsole.Print("Invalid index");
                    return;
                }
                NpcConsole.Print(NavGraph.ToggleLink(aIdx, bIdx)
                    ? "Link created: #" + aIdx + " <-> #" + bIdx
                    : "Link removed: #" + aIdx + " <-> #" + bIdx);
            }
            else if (sub == "unlink")
            {
                if (args.Length < 2 || !int.TryParse(args[1], out int uIdx))
                {
                    NpcConsole.Print("Usage: npc_node unlink <index> - remove every manual link of a node");
                    return;
                }
                if (uIdx < 0 || uIdx >= NavGraph.NodeCount)
                {
                    NpcConsole.Print("Invalid index");
                    return;
                }
                NpcConsole.Print("Node #" + uIdx + ": " + NavGraph.ClearLinks(uIdx) + " link(s) removed");
            }
            else if (sub == "type")
            {
                if (args.Length < 3 || !int.TryParse(args[1], out int tIdx))
                {
                    NpcConsole.Print("Usage: npc_node type <index> <ground|stair>");
                    return;
                }
                NodeType type = args[2].ToLowerInvariant() switch
                {
                    "ground" => NodeType.Ground,
                    "stair" or "stairs" => NodeType.Stair,
                    _ => (NodeType)(-1)
                };
                if ((int)type < 0)
                {
                    NpcConsole.Print("Unknown type '" + args[2] + "' (ground|stair)");
                    return;
                }
                if (tIdx < 0 || tIdx >= NavGraph.NodeCount)
                {
                    NpcConsole.Print("Invalid index");
                    return;
                }
                NavGraph.SetNodeType(tIdx, type);
                NpcConsole.Print("Node #" + tIdx + " is now " + type);
            }
            else if (sub == "bundled")
            {
                NpcConsole.Print("Nav graph sources: " + NavGraph.DescribeBundle());
            }
            else if (sub == "unfork")
            {
                if (args.Length < 2)
                {
                    NpcConsole.Print("Usage: npc_node unfork <owner> - drop your nodes for that ship or " +
                                     "station and go back to the ones shipped with NPC.Core");
                    return;
                }
                NpcConsole.Print(NavGraph.UnforkOwner(args[1])
                    ? "'" + args[1] + "' handed back to the bundled graph - restart to load it"
                    : "'" + args[1] + "' is not one of yours (see 'npc_node bundled')");
            }
            else
            {
                NpcConsole.Print("Usage: npc_node add | count | clear | save | list | remove <index> | " +
                                 "link <a> <b> | unlink <index> | type <index> <ground|stair> | " +
                                 "bundled | unfork <owner>");
            }
        }

        private static string KeyName(ConfigEntry<KeyboardShortcut> entry)
        {
            KeyboardShortcut shortcut = entry.Value;
            return shortcut.MainKey != KeyCode.None ? shortcut.MainKey.ToString() : "<unset>";
        }
    }
}
