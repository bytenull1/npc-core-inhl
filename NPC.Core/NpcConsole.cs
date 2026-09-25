using System;
using System.Collections.Generic;

namespace NPC.Core
{
    /// <summary>
    /// Console commands for every NPC mod, installed into the game's console by one patch. A name is
    /// never overwritten: one the game or another mod already has is refused and logged. Prefix yours
    /// with your mod's word (`buddy_follow`). docs/invariants.md#one-owner-per-console-command
    /// </summary>
    public static class NpcConsole
    {
        private sealed class Command(string owner, Action<string[]> run)
        {
            public readonly string Owner = owner;
            public readonly Action<string[]> Run = run;
        }

        private static readonly Dictionary<string, Command> Commands = new(StringComparer.Ordinal);
        private static readonly List<string> Order = [];
        private static ConsoleMenu? _console;
        private static Dictionary<string, Action<string[]>>? _table;

        /// <summary>
        /// Adds `name` for `owner` (a mod name, for the log). Registered before the console exists, it is
        /// installed when it opens; after, at once. False when the name is taken.
        /// </summary>
        public static bool Register(string owner, string name, Action<string[]> run)
        {
            if (Commands.TryGetValue(name, out Command? existing))
            {
                NpcLog.Core.LogWarning($"[mod] Console command '{name}' already belongs to {existing.Owner} - {owner}'s is not registered");
                return false;
            }
            Commands[name] = new Command(owner, run);
            Order.Add(name);
            if (_table != null) Install(name, Commands[name], _table);

            return true;
        }

        /// <summary>
        /// Registers `alias` for `owner` running the same as `target`, which must be registered already.
        /// A mod keeps its players' old command names this way.
        /// </summary>
        public static bool Alias(string owner, string alias, string target)
        {
            if (Commands.TryGetValue(target, out Command? command)) return Register(owner, alias, command.Run);

            NpcLog.Core.LogWarning($"[mod] Console alias '{alias}' of {owner}: no command '{target}' to run");
            return false;
        }

        /// <summary>
        /// Whether the game's console is open: overlays step aside and hotkeys stay quiet while it is.
        /// </summary>
        public static bool IsOpen
        {
            get
            {
                try
                {
                    GameManager gm = GameManager.Instance;
                    PlayerOverlay? overlay = gm != null && gm.GameCanvas != null ? gm.GameCanvas.PlayerOverlay : null;
                    return overlay != null && overlay.ConsoleMenu != null && overlay.ConsoleMenu.Opened;
                }
                catch
                {
                    return false; // safe fallback
                }
            }
        }

        /// <summary>
        /// Prints to the game console that last opened. Silent when there is none.
        /// </summary>
        public static void Print(string text)
        {
            GameInternals.ConsoleMenuAccess.Print(_console, text);
        }

        /// <summary>
        /// From the ConsoleMenu.Init postfix: every command registered so far goes into this console.
        /// </summary>
        internal static void Attach(ConsoleMenu console)
        {
            Dictionary<string, Action<string[]>>? table = GameInternals.ConsoleMenuAccess.GetCommands(console);
            if (table == null) return;

            _console = console;
            _table = table;
            Dictionary<string, List<string>> byOwner = [];
            foreach (string name in Order)
            {
                Command command = Commands[name];
                if (!Install(name, command, table)) continue;

                if (!byOwner.TryGetValue(command.Owner, out List<string>? names)) byOwner[command.Owner] = names = [];
                names.Add(name);
            }
            foreach (KeyValuePair<string, List<string>> entry in byOwner)
            {
                entry.Value.Sort(StringComparer.OrdinalIgnoreCase);
                Print(entry.Key + " commands registered (" + entry.Value.Count + "): " + string.Join(", ", entry.Value));
            }
        }

        private static bool Install(string name, Command command, Dictionary<string, Action<string[]>> table)
        {
            if (table.TryGetValue(name, out Action<string[]> present) && present != command.Run)
            {
                NpcLog.Core.LogWarning($"[mod] Console command '{name}' of {command.Owner} is taken by the game or a mod outside NPC.Core - not installed");
                return false;
            }
            table[name] = command.Run;
            return true;
        }
    }
}
