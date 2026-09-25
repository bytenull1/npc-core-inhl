using BepInEx.Logging;

namespace NPC.Core
{
    /// <summary>
    /// Where NPC code logs, and how much. One level for every NPC mod, so one `debug_level` captures a
    /// whole scene. docs/logging.md
    /// </summary>
    public static class NpcLog
    {
        private static ManualLogSource? _fallback;

        /// <summary>
        /// 0 warnings only, 1 state changes, 2 reasoning, 3 per-frame obstacle dumps: docs/logging.md#1-levels
        /// </summary>
        public static int Level
        {
            get => NpcCorePlugin.ConfigDebugLevel != null ? NpcCorePlugin.ConfigDebugLevel.Value : 1;
            set
            {
                if (NpcCorePlugin.ConfigDebugLevel != null) NpcCorePlugin.ConfigDebugLevel.Value = value;
            }
        }

        /// <summary>
        /// The acting NPC's source while its code runs, else NPC.Core's own.
        /// docs/logging.md#4-rules-for-adding-logs
        /// </summary>
        public static ManualLogSource Log => NpcRegistry.ActingLog ?? Core;

        /// <summary>
        /// NPC.Core's own source; anything logging before the plugin's Awake shares one stand-in.
        /// </summary>
        internal static ManualLogSource Core =>
            NpcCorePlugin.PluginLogger ?? (_fallback ??= Logger.CreateLogSource("NPC.Core (early)"));
    }
}
