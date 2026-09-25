namespace NPC.Core.World
{
    /// <summary>
    /// The Breathless as NPCs meet it: who it is grabbing, and the debug overrides of its AI and of every
    /// NPC's (`ai_disable`, `ai_notarget`), owned in one place so a coroutine that saves and restores a
    /// flag cannot silently undo a command. Everything here is runtime component state: never
    /// Breathless.Enabled or BreathlessController.SetAggressive, both of which write into the game's own
    /// save file. docs/invariants.md#mod-state-never-enters-the-vanilla-save
    /// </summary>
    public static class NpcMonster
    {
        private static INpc? _catching;

        /// <summary>
        /// `ai_disable npc`: every NPC mod switches its AI off while this is set.
        /// </summary>
        public static bool NpcsDisabled { get; internal set; }

        /// <summary>
        /// `ai_disable monster`: the Breathless neither roams nor hunts, and no NPC is caught.
        /// </summary>
        public static bool MonsterDisabled { get; internal set; }

        /// <summary>
        /// `ai_notarget`: the monster may not target the player.
        /// </summary>
        public static bool NoTarget { get; internal set; }

        /// <summary>
        /// Something we switched off is still off, so it has to be switched back on exactly once when the
        /// override is lifted.
        /// </summary>
        private static bool _huntSuppressed;
        private static bool _idleSuppressed;

        internal static bool Any => NpcsDisabled || MonsterDisabled || NoTarget;

        /// <summary>
        /// The monster may not target the player: what the two DetectItem prefixes and the aggressor flag
        /// key off. NPCs are deliberately still fair game - the game's detectors never see them (no Player
        /// component), so their catch is each mod's own and only a full `ai_disable` stops it.
        /// A catch that borrowed the aggressor restores it only while this is false.
        /// </summary>
        public static bool PlayerIgnored => MonsterDisabled || NoTarget;

        // ------------------------------------------------------------------
        // One catch at a time: docs/invariants.md#one-catch-at-a-time
        // ------------------------------------------------------------------

        /// <summary>
        /// The NPC the Breathless is grabbing, or null. Its animator follows one target at a time.
        /// </summary>
        public static INpc? Catching => NpcRegistry.IsGone(_catching) ? null : _catching;

        /// <summary>
        /// The monster is grabbing another NPC, still loaded: `me` must not start a catch now. A parked
        /// NPC's catch stopped with it.
        /// </summary>
        public static bool CatchingAnother(INpc me)
        {
            INpc? other = Catching;
            return other != null && other != me && other.IsActive;
        }

        /// <summary>
        /// `npc` is being grabbed. Pair it with <see cref="EndCatch"/> when the catch ends, the NPC dies or
        /// it is destroyed.
        /// </summary>
        public static void BeginCatch(INpc npc) => _catching = npc;

        public static void EndCatch(INpc npc)
        {
            if (_catching == npc) _catching = null;
        }

        internal static void ResetScene() => _catching = null;

        // ------------------------------------------------------------------
        // Debug overrides: docs/reference.md#2-console-commands
        // ------------------------------------------------------------------

        /// <summary>
        /// Re-asserted on every tick, because the game turns these back on by itself. Only the suppressed
        /// state is written continuously - re-enabling every tick would fight whatever the game does with
        /// its own monster.
        /// </summary>
        internal static void Apply()
        {
            Breathless? breathless = GameManager.Instance != null ? GameManager.Instance.Breathless : null;
            if (breathless == null) return;

            // The aggressor homes on the player every FixedUpdate with no perception gate of its own, so
            // blinding it means switching it off. Its catch trigger is a UnityEvent that fires regardless;
            // that is filtered in the DetectItem prefix.
            bool hunt = PlayerIgnored;
            BreathlessAggressor aggressor = breathless.Aggressor;
            if (aggressor != null && (hunt || _huntSuppressed)) aggressor.enabled = !hunt;

            BreathlessController controller = breathless.Controller;
            if (controller != null && (MonsterDisabled || _idleSuppressed)) controller.enabled = !MonsterDisabled;

            _huntSuppressed = hunt;
            _idleSuppressed = MonsterDisabled;
        }

        /// <summary>
        /// A loaded save is a new scene with a new monster, so an override set for the last one is dropped
        /// rather than carried over. The current monster is restored first, since a load can still be
        /// refused and leave this scene running; the suppressed marks are then dropped so nothing is ever
        /// forced on in the new one.
        /// </summary>
        internal static void ResetOverrides()
        {
            if (!Any && !_huntSuppressed && !_idleSuppressed) return;

            NpcLog.Core.LogInfo("[debug] AI overrides cleared by save load (were: " + Describe() + ")");
            NpcsDisabled = false;
            MonsterDisabled = false;
            NoTarget = false;
            Apply();
            _huntSuppressed = false;
            _idleSuppressed = false;
        }

        internal static string Describe()
        {
            return "NPC AI " + (NpcsDisabled ? "OFF" : "on") +
                   ", monster AI " + (MonsterDisabled ? "OFF" : "on") +
                   ", notarget " + (NoTarget ? "ON (player only - NPCs are still hunted)" : "off");
        }
    }
}
