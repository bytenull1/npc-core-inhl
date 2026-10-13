using System;

namespace NPC.Core
{
    /// <summary>
    /// The game's moments every NPC mod needs, raised by NPC.Core's own patches so no mod patches these
    /// methods itself. A handler that throws is logged and skipped; the others still run.
    /// docs/invariants.md#one-patch-per-game-hook
    /// </summary>
    public static class NpcEvents
    {
        /// <summary>
        /// Every GameManager.FixedUpdate, after NPC.Core's own upkeep: exactly while a game scene is loaded.
        /// </summary>
        public static event Action? Tick;

        /// <summary>
        /// GameManager.Start, before the game wakes the player: true on a new game (worldTime 0), which is
        /// the game's own test. docs/game-model.md#a-new-game
        /// </summary>
        public static event Action<bool>? GameStarting;

        /// <summary>
        /// SceneLoader.LoadGame read a save: its name. Not raised when the game refused the file.
        /// </summary>
        public static event Action<string>? SaveLoaded;

        /// <summary>
        /// A new scene is coming: drop every scene cache. Raised before GameStarting and SaveLoaded.
        /// </summary>
        public static event Action? WorldReset;

        internal static void RaiseTick() => Raise(Tick, "Tick", handler => ((Action)handler)());

        internal static void RaiseGameStarting(bool newGame) =>
            Raise(GameStarting, "GameStarting", handler => ((Action<bool>)handler)(newGame));

        internal static void RaiseSaveLoaded(string saveName) =>
            Raise(SaveLoaded, "SaveLoaded", handler => ((Action<string>)handler)(saveName));

        internal static void RaiseWorldReset() => Raise(WorldReset, "WorldReset", handler => ((Action)handler)());

        /// <summary>
        /// Each handler on its own: one mod's exception must not starve the next mod's handler.
        /// docs/invariants.md#mod-code-never-throws-into-the-game
        /// </summary>
        private static void Raise(Delegate? multicast, string name, Action<Delegate> invoke)
        {
            if (multicast == null) return;

            foreach (Delegate handler in multicast.GetInvocationList())
            {
                try
                {
                    invoke(handler);
                }
                catch (Exception ex)
                {
                    FailureLog.Report(handler, $"{name} handler {handler.Method.DeclaringType?.FullName}.{handler.Method.Name}", ex);
                }
            }
        }
    }
}
