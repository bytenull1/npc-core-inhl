using System;
using System.Collections.Generic;
using BepInEx.Logging;
using NPC.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NPC.Core
{
    /// <summary>
    /// Every NPC of every mod, in registration order, and what one NPC asks about the others. Mods never
    /// keep a second list for these questions: an NPC of another mod would be invisible to it.
    /// docs/invariants.md#several-npc-mods
    /// </summary>
    public static class NpcRegistry
    {
        private static readonly List<INpc> Npcs = [];
        private static readonly Dictionary<string, ManualLogSource> LogSources = [];
        // The NPC whose code is running, for NpcLog.Log. docs/logging.md#4-rules-for-adding-logs
        private static INpc? _acting;

        private const float BodiesCheckInterval = 1f;
        private static float _bodiesCheckAt;
        // NPCs whose pairs were set up: a lost pair is reported, a first one is not.
        private static readonly HashSet<INpc> BodiesSetUp = [];
        // NPCs told once that a pair was lost; later restores log at level 3.
        private static readonly HashSet<INpc> BodiesReported = [];

        /// <summary>
        /// Every registered NPC, alive or dead, in registration order. Handlers for game events iterate
        /// <see cref="Snapshot"/>, because a callback may despawn.
        /// </summary>
        public static IReadOnlyList<INpc> All => Npcs;

        public static INpc[] Snapshot() => [.. Npcs];

        public static event Action<INpc>? Registered;
        public static event Action<INpc>? Unregistered;

        /// <summary>
        /// Idempotent. Register before the NPC's GameObject is activated, so its first OnEnable finds the
        /// others to pass through.
        /// </summary>
        public static void Register(INpc npc)
        {
            if (npc == null || Npcs.Contains(npc)) return;

            Npcs.Add(npc);
            Registered?.Invoke(npc);
        }

        /// <summary>
        /// Idempotent: a despawn calls it at once, OnDestroy again at the end of the frame.
        /// </summary>
        public static void Unregister(INpc npc)
        {
            if (npc == null || !Npcs.Remove(npc)) return;

            Unregistered?.Invoke(npc);
        }

        /// <summary>
        /// Where in the registration order an NPC stands, -1 when it is not registered. Staggers work
        /// between NPCs and settles who steps aside, across mods.
        /// </summary>
        public static int IndexOf(INpc npc) => Npcs.IndexOf(npc);

        /// <summary>
        /// A destroyed NPC that never unregistered (a scene unload) leaves its slot here.
        /// </summary>
        internal static void PruneDestroyed()
        {
            for (int i = Npcs.Count - 1; i >= 0; i--)
            {
                if (IsGone(Npcs[i])) Npcs.RemoveAt(i);
            }
        }

        /// <summary>
        /// Null, or a Unity object already destroyed.
        /// </summary>
        public static bool IsGone(INpc? npc) => npc == null || (npc is Object unityObject && unityObject == null);

        // ------------------------------------------------------------------
        // Log attribution: docs/logging.md#4-rules-for-adding-logs
        // ------------------------------------------------------------------

        /// <summary>
        /// One BepInEx source per NPC, "ModName:Name", kept for the session: names repeat across loads.
        /// </summary>
        public static ManualLogSource LogSourceFor(string modName, string npcName)
        {
            string key = modName + ":" + npcName;
            if (!LogSources.TryGetValue(key, out ManualLogSource source))
            {
                source = BepInEx.Logging.Logger.CreateLogSource(key);
                LogSources[key] = source;
            }
            return source;
        }

        /// <summary>
        /// The NPC whose code is running, or null.
        /// </summary>
        public static INpc? ActingNpc => _acting;

        /// <summary>
        /// The source <see cref="NpcLog.Log"/> writes to while an NPC's code runs, or null.
        /// </summary>
        public static ManualLogSource? ActingLog => _acting != null ? _acting.LogSource : null;

        /// <summary>
        /// Marks `npc` as the one running until the scope is disposed. Wrap every entry point into NPC
        /// code - a Unity message, a game event, a patch, a command - and never hold one across a yield.
        /// </summary>
        public static ActingScope Acting(INpc? npc)
        {
            ActingScope scope = new(_acting);
            _acting = npc;
            return scope;
        }

        public readonly struct ActingScope(INpc? previous) : IDisposable
        {
            public void Dispose() => _acting = previous;
        }

        // ------------------------------------------------------------------
        // What one NPC asks about the others: docs/invariants.md#several-npc-mods
        // ------------------------------------------------------------------

        /// <summary>
        /// Any NPC's own body, a corpse included: docs/invariants.md#npcs-never-block-each-other
        /// </summary>
        public static bool IsNpcBody(Transform t)
        {
            if (t == null) return false;

            foreach (INpc npc in Npcs)
            {
                if (!IsGone(npc) && npc.IsOwnBody(t)) return true;
            }
            return false;
        }

        /// <summary>
        /// Another NPC's current work holds this: docs/invariants.md#one-npc-per-target
        /// </summary>
        public static bool TakenByAnother(Transform what, INpc me)
        {
            foreach (INpc npc in Npcs)
            {
                if (!IsGone(npc) && npc != me && npc.Holds(what)) return true;
            }
            return false;
        }

        /// <summary>
        /// Another living, loaded NPC stands where `test` says.
        /// </summary>
        public static bool AnotherNpcWhere(Func<Vector3, bool> test, INpc me)
        {
            foreach (INpc npc in Npcs)
            {
                if (IsGone(npc) || npc == me || npc.IsDead || !npc.IsActive) continue;
                if (test(npc.Transform.position)) return true;
            }
            return false;
        }

        /// <summary>
        /// Another NPC tracked in this room, which keeps it loaded.
        /// docs/invariants.md#rooms-stay-loaded-for-every-npc
        /// </summary>
        public static INpc? OtherTrackedIn(Room room, INpc me)
        {
            foreach (INpc npc in Npcs)
            {
                if (!IsGone(npc) && npc != me && npc.TracksRoom(room)) return npc;
            }
            return null;
        }

        /// <summary>
        /// The body colliders of two NPCs ignore each other, a dead one's ragdoll included. Both must be
        /// enabled and active for IgnoreCollision, so call it again from OnEnable and on death.
        /// Returns how many pairs were not ignored yet. docs/invariants.md#npcs-never-block-each-other
        /// </summary>
        public static int IgnoreBodies(INpc a, INpc b)
        {
            if (IsGone(a) || IsGone(b) || a == b) return 0;

            CharacterController? aCc = a.Controller;
            CharacterController? bCc = b.Controller;
            int ignored = Ignore(aCc, bCc);
            foreach (Collider part in b.SolidParts()) ignored += Ignore(aCc, part);
            foreach (Collider part in a.SolidParts()) ignored += Ignore(bCc, part);
            return ignored;
        }

        /// <summary>
        /// <see cref="IgnoreBodies"/> between `npc` and every other registered NPC; its controller and its own
        /// solid parts; the player's colliders and `npc`. Returns how many pairs were not ignored yet.
        /// </summary>
        public static int IgnoreAllBodies(INpc npc)
        {
            if (IsGone(npc)) return 0;

            CharacterController? cc = npc.Controller;
            int ignored = 0;
            foreach (Collider part in npc.SolidParts()) ignored += Ignore(cc, part);
            foreach (INpc other in Npcs) ignored += IgnoreBodies(npc, other);
            return ignored + NpcPlayer.IgnoreNpc(npc);
        }

        /// <summary>
        /// Once a second, from the shared tick: a collider switched off (a teleport, a park) loses its ignored
        /// pairs, and nothing else asks again.
        /// </summary>
        internal static void KeepBodiesApart()
        {
            if (Time.time < _bodiesCheckAt) return;

            _bodiesCheckAt = Time.time + BodiesCheckInterval;
            foreach (INpc npc in Npcs)
            {
                int restored = IgnoreAllBodies(npc);
                // Another mod's INpc that never called IgnoreAllBodies is set up here, silently.
                if (BodiesSetUp.Add(npc) || restored == 0) continue;

                using ActingScope _ = Acting(npc);
                if (BodiesReported.Add(npc))
                {
                    NpcLog.Log.LogInfo($"[world] {restored} body collision pair(s) were lost (a collider switched off) - ignoring them again");
                }
                else if (NpcLog.Level >= 3)
                {
                    NpcLog.Log.LogInfo($"[world] Body collision pairs restored: {restored}");
                }
            }
        }

        /// <summary>
        /// A new scene: the old NPCs are gone.
        /// </summary>
        internal static void ResetScene()
        {
            BodiesSetUp.Clear();
            BodiesReported.Clear();
        }

        /// <summary>
        /// 1 when the pair was not ignored yet and now is, else 0.
        /// </summary>
        internal static int Ignore(Collider? a, Collider? b)
        {
            if (a == null || b == null || !a.enabled || !b.enabled) return 0;
            if (!a.gameObject.activeInHierarchy || !b.gameObject.activeInHierarchy) return 0;
            if (Physics.GetIgnoreCollision(a, b)) return 0;

            Physics.IgnoreCollision(a, b);
            return 1;
        }
    }
}
