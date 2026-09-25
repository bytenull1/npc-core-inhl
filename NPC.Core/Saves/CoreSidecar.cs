using System;
using Newtonsoft.Json;
using NPC.Core.World;

namespace NPC.Core.Saves
{
    /// <summary>
    /// NPC.Core's own sidecar, "&lt;save&gt;.npccore": what every NPC shares, the door codes. Written only
    /// while there is something in it. docs/architecture.md#4-files-on-disk
    /// </summary>
    internal static class CoreSidecar
    {
        private const string Extension = "npccore";

        private sealed record Data
        {
            /// <summary>
            /// Door codes the player told any NPC: docs/invariants.md#door-knowledge-is-shared
            /// </summary>
            public int[]? KnownPinCodes { get; init; }
        }

        /// <summary>
        /// Codes read with a save, learned once its scene is up: a new GameManager empties the code set.
        /// </summary>
        private static int[]? _pendingCodes;

        internal static void Register()
        {
            NpcSaves.RegisterSidecar("NPC.Core", Extension, Write);
            NpcEvents.SaveLoaded += Read;
            NpcEvents.GameStarting += newGame =>
            {
                if (newGame) _pendingCodes = null;
            };
        }

        private static string? Write(string saveName)
        {
            if (NpcDoors.KnownCodes.Count == 0) return null;

            return JsonConvert.SerializeObject(new Data { KnownPinCodes = [.. NpcDoors.KnownCodes] }, Formatting.Indented);
        }

        private static void Read(string saveName)
        {
            _pendingCodes = null;
            string? contents = NpcSaves.Read(saveName, Extension);
            if (contents == null) return;

            try
            {
                _pendingCodes = JsonConvert.DeserializeObject<Data>(contents)?.KnownPinCodes;
            }
            catch (Exception ex)
            {
                NpcLog.Core.LogWarning($"[save] Failed to parse '{NpcSaves.PathOf(saveName, Extension)}': {ex.Message}");
            }
        }

        /// <summary>
        /// From the shared tick: the saved codes, once the loaded game's scene is ready.
        /// </summary>
        internal static void Tick()
        {
            if (_pendingCodes == null) return;

            GameManager gm = GameManager.Instance;
            if (gm == null || !gm.SceneFullyLoaded || NpcPlayer.Pilot == null) return;

            foreach (int code in _pendingCodes) NpcDoors.LearnCode(code);
            NpcLog.Core.LogInfo($"[save] {_pendingCodes.Length} door code(s) restored");
            _pendingCodes = null;
        }
    }
}
