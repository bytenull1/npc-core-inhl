using System;
using System.Collections.Generic;
using NPC.Core.Navigation;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NPC.Core.World
{
    /// <summary>
    /// An NPC that opens and closes doors, told what the game did to a door it closed. Optional beside
    /// <see cref="INpc"/>. docs/doors.md
    /// </summary>
    public interface INpcDoorUser
    {
        /// <summary>
        /// A gate's AntiCrasher just undid a close (Gate.FailClose), and the game never retries one.
        /// Every door user hears it; only the one standing in that gate should act. docs/doors.md#4-closes-an-npc-blocked
        /// </summary>
        void OnCloseFailed(Gate gate);

        /// <summary>
        /// A close this NPC issued (<see cref="NpcDoors.NoteClosedBy"/>) has finished, away from the
        /// player: the game switched no room off there, so the rooms of `doorway` are this NPC's to
        /// release. docs/invariants.md#npc-closes-must-not-move-the-player
        /// </summary>
        void OnDoorClosedAway(EntryDetector doorway);
    }

    /// <summary>
    /// What every NPC knows about the station's doors, once for all of them: which it may open, which
    /// routing goes around, the door codes the player gave, the doorway sensors and airlocks, and which
    /// NPC closed a door. docs/invariants.md#door-knowledge-is-shared
    /// </summary>
    public static class NpcDoors
    {
        /// <summary>
        /// How near a doorway the player has to be for a door an NPC closed there to be allowed to
        /// decide which room they are in.
        /// </summary>
        private const float PlayerAtDoorwayRadius = 2.5f;
        /// <summary>
        /// How long a close stays an NPC's: Gate.OnClosed fires at the end of the animation.
        /// </summary>
        private const float NpcCloseWindow = 6f;
        private const float SceneCacheTtl = 5f;
        private const float ImpassableGatesTtl = 1f;
        /// <summary>
        /// Cheap first pass before the gate's volume is tested at all.
        /// </summary>
        private const float DoorBroadPhaseRadius = 3f;
        /// <summary>
        /// Body clearance added to a gate's volume, so a route cannot thread an NPC through a door leaf's edge.
        /// </summary>
        private const float DoorBlockPadding = 0.3f;

        // Doorway sensors, the switched-off ones included (a sealed room's doors have one).
        private static List<EntryDetector>? _detectors;
        private static readonly Dictionary<Gate, List<EntryDetector>> DetectorsByGate = [];
        private static float _detectorsRefreshAt;
        private static List<Airlock>? _airlocks;
        private static float _airlocksRefreshAt;
        // Gates opened by a pin-code panel, mapped to that panel: it carries the code and where to stand.
        private static readonly Dictionary<Gate, DoorPinCode> PasswordGates = [];
        private static float _passwordGatesRefreshAt;
        private static readonly List<Gate> ImpassableGates = [];
        private static float _impassableGatesRefreshAt;

        // Door codes the player told any NPC; every NPC knows them all. docs/invariants.md#an-npc-only-knows-codes-it-was-told
        private static readonly HashSet<int> CodeSet = [];
        // The game the codes were told in, so a save written from the menu cannot take the last game's.
        private static GameManager? _codesIn;

        private readonly struct NpcClose(Gate gate, float at, INpc by)
        {
            public readonly Gate Gate = gate;
            public readonly float At = at;
            public readonly INpc By = by;
        }
        private static readonly List<NpcClose> NpcClosedGates = [];

        /// <summary>
        /// Counts the sweeps of the doorway sensors: a mod that derives a list from <see cref="Detectors"/>
        /// rebuilds it when this changes, on its own next read - never while it is being iterated.
        /// </summary>
        public static int DetectorsVersion { get; private set; }

        /// <summary>
        /// A new scene: every cache refills on first use, which skips the SceneScan budget. The codes
        /// reset with their GameManager instead.
        /// </summary>
        internal static void ResetScene()
        {
            _detectors = null;
            DetectorsByGate.Clear();
            _detectorsRefreshAt = 0f;
            _airlocks = null;
            _airlocksRefreshAt = 0f;
            PasswordGates.Clear();
            _passwordGatesRefreshAt = 0f;
            ImpassableGates.Clear();
            _impassableGatesRefreshAt = 0f;
        }

        // ------------------------------------------------------------------
        // Doorway sensors: docs/game-model.md#2-there-are-no-room-volumes
        // ------------------------------------------------------------------

        /// <summary>
        /// The active doorway sensors (EntryDetector), swept every 5 s. Read only.
        /// </summary>
        public static IReadOnlyList<EntryDetector> Detectors
        {
            get
            {
                RefreshDetectors();
                // RefreshDetectors always leaves the cache set: a first fill skips the budget.
                return _detectors!;
            }
        }

        /// <summary>
        /// The rooms a doorway joins: the game's inner and outer side.
        /// </summary>
        public static void RoomsOf(EntryDetector detector, out Room? inner, out Room? outer)
        {
            inner = GameInternals.EntryDetectorAccess.GetInnerRoom(detector);
            outer = GameInternals.EntryDetectorAccess.GetOuterRoom(detector);
        }

        /// <summary>
        /// The gate in a doorway, or null for an open archway.
        /// </summary>
        public static Gate? DoorOf(EntryDetector detector) => GameInternals.EntryDetectorAccess.GetDoor(detector);

        /// <summary>
        /// Whether the game switches the far room off once this doorway's door shuts. False when unreadable,
        /// which keeps both rooms loaded.
        /// </summary>
        public static bool Optimizes(EntryDetector detector) => GameInternals.EntryDetectorAccess.GetOptimize(detector);

        /// <summary>
        /// Which side of a doorway a point is on, by the test EntryDetector.TriggerCheckForEnter uses for the
        /// player. False when the detector cannot be read. A plane: sound only for the doorway's two rooms.
        /// </summary>
        public static bool TryInnerSide(EntryDetector detector, Vector3 pos, out bool inner)
        {
            inner = false;
            Transform? rotationRef = GameInternals.EntryDetectorAccess.GetRotationReference(detector);
            if (rotationRef == null) return false;

            float z = rotationRef.InverseTransformPoint(pos).z;
            inner = GameInternals.EntryDetectorAccess.GetReverseSide(detector) ? z < 0f : z > 0f;
            return true;
        }

        private static void RefreshDetectors()
        {
            if (_detectors != null && Time.time < _detectorsRefreshAt) return;
            if (!SceneScan.MayRescan(_detectors == null)) return;

            // Switched-off detectors too: a sealed room's doors have one. docs/invariants.md#an-npc-opens-only-what-the-player-could
            _detectors = [];
            _detectorsRefreshAt = Time.time + SceneCacheTtl;

            DetectorsByGate.Clear();
            foreach (EntryDetector detector in Object.FindObjectsOfType<EntryDetector>(true))
            {
                if (detector.gameObject.activeInHierarchy) _detectors.Add(detector);

                Gate? door = GameInternals.EntryDetectorAccess.GetDoor(detector);
                if (door == null) continue;

                if (!DetectorsByGate.TryGetValue(door, out List<EntryDetector>? list)) DetectorsByGate[door] = list = [];
                list.Add(detector);
            }
            DetectorsVersion++;
        }

        // ------------------------------------------------------------------
        // Opening and passing: docs/doors.md#1-two-different-questions
        // ------------------------------------------------------------------

        /// <summary>
        /// May an NPC open this gate itself? Door handling only. This is not the routing question:
        /// docs/invariants.md#opening-is-not-passing
        /// </summary>
        public static bool MayOpen(Gate gate)
        {
            if (gate == null || !gate.gameObject.activeInHierarchy) return false;

            if (gate.Opened) return true;

            if (gate.Locked || WhyClosedToPlayer(gate) != null) return false;
            // Airlocks cycle themselves; opening one risks venting.
            if (IsAirlockGate(gate)) return false;
            // docs/invariants.md#keep-electricitypanelgate-excluded
            if (gate.gameObject.name.IndexOf("ElectricityPanelGate", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
            DoorPinCode? panel = PinPanelFor(gate);
            if (panel == null) return true;

            return CodeKnown(GameInternals.DoorPinCodeAccess.GetPinCode(panel));
        }

        /// <summary>
        /// Should the path search route around this gate? Only one that will still be shut when the NPC
        /// arrives: locked, closed to the player, or a pin door no NPC has the code for.
        /// docs/invariants.md#opening-is-not-passing
        /// </summary>
        public static bool BlocksRouting(Gate gate)
        {
            if (gate == null || !gate.gameObject.activeInHierarchy) return false;

            if (gate.Opened) return false;

            if (gate.Locked) return true;
            // An airlock or docking gate is the only way between ship and station and the player opens
            // it. Refusing to open one is not a reason to refuse to walk through it.
            if (IsAirlockGate(gate)) return false;
            if (WhyClosedToPlayer(gate) != null) return true;

            DoorPinCode? panel = PinPanelFor(gate);
            if (panel == null) return false;

            return !CodeKnown(GameInternals.DoorPinCodeAccess.GetPinCode(panel));
        }

        /// <summary>
        /// Why the player could not walk this gate open, or null when they could. A gate its doorway
        /// detectors drive opens only through one that is switched on, and needs a suit if that one does.
        /// docs/invariants.md#an-npc-opens-only-what-the-player-could
        /// </summary>
        public static string? WhyClosedToPlayer(Gate gate)
        {
            RefreshDetectors();
            if (!DetectorsByGate.TryGetValue(gate, out List<EntryDetector>? detectors)) return null;

            bool driven = false;
            bool suitOnly = false;
            foreach (EntryDetector detector in detectors)
            {
                if (detector == null) continue;

                driven = true;
                if (!detector.gameObject.activeInHierarchy) continue;
                if (GameInternals.PlayerDetectorAccess.GetHelmetRequired(detector) != true || !PlayerLacksSuit()) return null;

                suitOnly = true;
            }
            if (!driven) return null;

            return suitOnly
                ? "it opens only for someone in a suit, and you have none"
                : "its doorway sensor is switched off, so you cannot walk it open either";
        }

        private static bool PlayerLacksSuit()
        {
            Space.Player? player = NpcPlayer.Pilot;
            return player != null && player.EquipmentSystem != null && !player.EquipmentSystem.SuitEquipped;
        }

        /// <summary>
        /// How many gates the path search routes around right now, for an NPC's "waiting" line.
        /// </summary>
        public static int ImpassableCount => ImpassableGates.Count;

        /// <summary>
        /// Gates no NPC can currently get through, for the path search. Short TTL: lock and open state
        /// both change during play.
        /// </summary>
        private static void RefreshImpassableGates()
        {
            if (Time.time < _impassableGatesRefreshAt) return;

            _impassableGatesRefreshAt = Time.time + ImpassableGatesTtl;
            ImpassableGates.Clear();
            foreach (Gate gate in NavProbe.Gates)
            {
                if (BlocksRouting(gate)) ImpassableGates.Add(gate);
            }
        }

        /// <summary>
        /// True when a->b passes through a gate no NPC can open; a corridor running past a shut airlock
        /// door has to stay routable. Bound once as NavGraph.SegmentBlockedByDoor.
        /// docs/invariants.md#locked-doors-block-edges
        /// </summary>
        internal static bool SegmentBlockedByDoor(Vector3 a, Vector3 b)
        {
            RefreshImpassableGates();
            if (ImpassableGates.Count == 0) return false;

            foreach (Gate gate in ImpassableGates)
            {
                if (gate == null) continue;

                if (FlatDistanceToSegment(gate.transform.position, a, b) > DoorBroadPhaseRadius) continue;

                if (NavProbe.SegmentCrossesGate(gate, a, b, DoorBlockPadding)) return true;
            }
            return false;
        }

        private static float FlatDistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector2 pf = new(p.x, p.z);
            Vector2 af = new(a.x, a.z);
            Vector2 bf = new(b.x, b.z);
            Vector2 ab = bf - af;
            float lenSq = ab.sqrMagnitude;
            if (lenSq < 0.0001f) return (pf - af).magnitude;

            float t = Mathf.Clamp01(Vector2.Dot(pf - af, ab) / lenSq);
            return (pf - (af + ab * t)).magnitude;
        }

        // ------------------------------------------------------------------
        // Airlocks
        // ------------------------------------------------------------------

        /// <summary>
        /// An airlock's outer or inner door, or a docking hatch: never opened by an NPC, always routable.
        /// </summary>
        public static bool IsAirlockGate(Gate gate)
        {
            RefreshAirlocks();
            if (_airlocks == null) return false;

            foreach (Airlock airlock in _airlocks)
            {
                if (GameInternals.AirlockAccess.GetOuterDoor(airlock) == gate) return true;

                if (GameInternals.AirlockAccess.GetInnerDoor(airlock) == gate) return true;

                if (GameInternals.AirlockAccess.GetHatch(airlock) == gate) return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a room is an airlock's chamber, which is never a safe spot. docs/known-issues.md
        /// </summary>
        public static bool RoomIsAirlockChamber(Room? room)
        {
            if (room == null) return false;

            RefreshAirlocks();
            if (_airlocks == null) return false;

            foreach (Airlock airlock in _airlocks)
            {
                Room? connected = GameInternals.AirlockAccess.GetConnectedRoom(airlock);
                if (connected == room) return true;
            }
            return false;
        }

        private static void RefreshAirlocks()
        {
            if (_airlocks != null && Time.time < _airlocksRefreshAt) return;
            if (!SceneScan.MayRescan(_airlocks == null)) return;

            _airlocks = [.. Object.FindObjectsOfType<Airlock>()];
            _airlocksRefreshAt = Time.time + SceneCacheTtl;
        }

        // ------------------------------------------------------------------
        // Password doors: docs/doors.md#password-doors
        // ------------------------------------------------------------------

        /// <summary>
        /// The pin panel wired to this gate, or null when it is not a password door. The whole map is
        /// rebuilt every 5 s - the set of panels only changes with the scene.
        /// </summary>
        public static DoorPinCode? PinPanelFor(Gate gate)
        {
            RefreshPasswordGates();
            return PasswordGates.GetValueOrDefault(gate);
        }

        /// <summary>
        /// True for gates unlocked through a pin code, whatever the NPCs know.
        /// </summary>
        public static bool IsPasswordGate(Gate gate) => PinPanelFor(gate) != null;

        private static void RefreshPasswordGates()
        {
            if (Time.time < _passwordGatesRefreshAt || !SceneScan.MayRescan(_passwordGatesRefreshAt <= 0f)) return;

            PasswordGates.Clear();
            foreach (DoorPinCode pin in Object.FindObjectsOfType<DoorPinCode>(true))
            {
                Gate? wired = GameInternals.DoorPinCodeAccess.GetWiredGate(pin);
                if (wired != null) PasswordGates[wired] = pin;
            }
            _passwordGatesRefreshAt = Time.time + SceneCacheTtl;
        }

        /// <summary>
        /// Records a door code the player gave an NPC. True when it is new.
        /// </summary>
        public static bool LearnCode(int code) => Codes().Add(code);

        /// <summary>
        /// The codes the NPCs have been told in the running game.
        /// </summary>
        public static IReadOnlyCollection<int> KnownCodes => Codes();

        /// <summary>
        /// True when a known code opens at least one password door in the scene.
        /// </summary>
        public static bool AnyKnownDoorMatches()
        {
            RefreshPasswordGates();
            foreach (KeyValuePair<Gate, DoorPinCode> entry in PasswordGates)
            {
                if (CodeKnown(GameInternals.DoorPinCodeAccess.GetPinCode(entry.Value))) return true;
            }
            return false;
        }

        /// <summary>
        /// The code set of the running game: a new GameManager starts it empty.
        /// </summary>
        private static HashSet<int> Codes()
        {
            if (_codesIn == GameManager.Instance) return CodeSet;

            CodeSet.Clear();
            _codesIn = GameManager.Instance;
            return CodeSet;
        }

        private static bool CodeKnown(int? code) => code.HasValue && Codes().Contains(code.Value);

        // ------------------------------------------------------------------
        // Closes an NPC issued: docs/invariants.md#npc-closes-must-not-move-the-player
        // ------------------------------------------------------------------

        /// <summary>
        /// Records that an NPC - not the player, not the game - issued a close on this gate, so the
        /// EntryDetector patch can tell the two apart. Call it right after Gate.Close.
        /// </summary>
        public static void NoteClosedBy(Gate gate, INpc by)
        {
            if (gate == null) return;

            for (int i = NpcClosedGates.Count - 1; i >= 0; i--)
            {
                if (NpcClosedGates[i].Gate == gate || NpcClosedGates[i].Gate == null ||
                    Time.time - NpcClosedGates[i].At > NpcCloseWindow)
                {
                    NpcClosedGates.RemoveAt(i);
                }
            }
            NpcClosedGates.Add(new NpcClose(gate, Time.time, by));
        }

        /// <summary>
        /// True when an NPC closed this gate within the last NpcCloseWindow; `by` is that NPC, or null
        /// once it is gone.
        /// </summary>
        public static bool WasClosedByNpc(Gate gate, out INpc? by)
        {
            by = null;
            if (gate == null) return false;

            bool found = false;
            for (int i = NpcClosedGates.Count - 1; i >= 0; i--)
            {
                if (NpcClosedGates[i].Gate == null || Time.time - NpcClosedGates[i].At > NpcCloseWindow)
                {
                    NpcClosedGates.RemoveAt(i);
                    continue;
                }
                if (NpcClosedGates[i].Gate != gate) continue;

                found = true;
                INpc closer = NpcClosedGates[i].By;
                by = NpcRegistry.IsGone(closer) ? null : closer;
            }
            return found;
        }

        /// <summary>
        /// The player really is at this doorway: the game's own re-filing is then right.
        /// </summary>
        internal static bool PlayerAtDoorway(Space.Player player, Gate gate) =>
            (player.Controller.CachedTransform.position - gate.transform.position).sqrMagnitude <=
            PlayerAtDoorwayRadius * PlayerAtDoorwayRadius;
    }
}
