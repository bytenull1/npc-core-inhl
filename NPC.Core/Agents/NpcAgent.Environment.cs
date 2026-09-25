using NPC.Core.Navigation;
using NPC.Core.World;
using Space;
using Space.Data;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NPC.Core.Agents
{
    /// <summary>
    /// Room tracking and content loading, floor plane tracking, environment and space protection, atmosphere damage, Breathless catch and death.
    /// </summary>
    public sealed partial class NpcAgent
    {
        /// <summary>
        /// The player's HealthSystem.deathCounter and threat, for the NPC. docs/game-model.md#atmosphere-kills-by-the-players-rule
        /// </summary>
        private int deathCounter = 0;
        private int lifeThreatLevel = 0;
        private bool lifeThreat = false;
        private bool currentlyInSpace = false;
        private Environment? currentEnvironmentRef = null;

        // Life threat: HealthSystem.Tick's numbers. docs/game-model.md#atmosphere-kills-by-the-players-rule
        private const int LifeThreatLethal = 10;
        private const int DeathCounterArmed = 5;
        private const int DeathCounterCap = 10;
        private const int VacuumThreat = 100;
        /// <summary>
        /// The GameManager whose OnTick AtmosphereTick is subscribed to, or null.
        /// </summary>
        private GameManager? tickSource = null;
        /// <summary>
        /// This tick's threat, counted on the next: docs/game-model.md#atmosphere-kills-by-the-players-rule
        /// </summary>
        private int pendingThreat = 0;

        // Set only while ParkWithShip holds the NPC inactive for a spacewalk.
        private bool parkedWithShip = false;
        private bool hasKnownRoom = false;
        // Stored in the space of the anchor it was measured against: a world coordinate
        // recorded on a station expires the moment the world moves around the ship.
        // docs/invariants.md#an-npc-rides-its-own-floor
        private Vector3 lastValidInsideLocal = Vector3.zero;
        private string? safePositionOwner = null;
        private bool hasSafePosition = false;

        private float baseFloorMismatchTimer = 0f;

        private List<Environment>? cachedEnvironments = null;
        private float environmentsRefreshAt = 0f;

        // One load line per room this often, whatever keeps switching it on.
        private const float RoomLoadLogCooldown = 5f;
        /// <summary>
        /// How near a doorway the NPC must be for room tracking to consult it.
        /// </summary>
        private const float DoorwaySideRadius = 3.5f;
        private readonly Dictionary<int, float> roomLoadLoggedAt = [];
        private static readonly HashSet<Room> LoadedRoomSet = [];

        private static readonly WaitForSeconds CatchReleaseDelay = new(1.5f);

        // ------------------------------------------------------------------
        // Room tracking & content loading (so rooms "load" for the NPC too)
        // ------------------------------------------------------------------

        private void UpdateRoomTracking()
        {
            IReadOnlyList<EntryDetector> detectors = NpcDoors.Detectors;

            EntryDetector? best = null;
            // Until a room has ever been resolved (spawn, save restore), the nearest detector
            // at any range; after that, within DoorwaySideRadius. docs/doors.md#which-rooms-an-npc-loads
            bool bootstrap = currentRoomRef == null;
            float bestDistanceSquared = bootstrap ? float.MaxValue : DoorwaySideRadius * DoorwaySideRadius;
            Vector3 pos = transform.position;

            foreach (EntryDetector detector in detectors)
            {
                if (detector == null || !detector.gameObject.activeInHierarchy) continue;
                // An unbounded search would otherwise hand an NPC on the ship a docked
                // station's room, and the environment that comes with it.
                if (bootstrap && CurrentOwner != null &&
                    NpcVessels.OwnerOfTransform(detector.transform) != CurrentOwner)
                {
                    continue;
                }
                float distanceSquared = (detector.transform.position - pos).sqrMagnitude;
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    best = detector;
                }
            }

            if (best == null || !NpcDoors.TryInnerSide(best, pos, out bool innerSide)) return;

            NpcDoors.RoomsOf(best, out Room? innerRoom, out Room? outerRoom);
            if (innerRoom == null && outerRoom == null) return;

            // The far side only while its door is open, as EntryDetector does for the player:
            // docs/invariants.md#an-npc-loads-rooms-by-the-door-rule
            Room? target = innerSide ? innerRoom : outerRoom;
            LoadRoom(target, "the NPC is in it");
            Gate? door = NpcDoors.DoorOf(best);
            if (door == null || door.Opened) LoadRoom(innerSide ? outerRoom : innerRoom, "door open to", target);

            if (target != null)
            {
                if (target != currentRoomRef)
                {
                    currentRoomRef = target;
                    hasKnownRoom = true;
                    SetForcedRoom(target);
                }

                // Never treat the inside of an airlock as a safe position.
                if (!NpcDoors.RoomIsAirlockChamber(target)) RememberSafePosition(pos);
            }
        }


        /// <summary>
        /// Records a spot known to be inside, in the frame the NPC is riding.
        /// </summary>
        private void RememberSafePosition(Vector3 worldPos)
        {
            Transform anchor = transform.parent;
            lastValidInsideLocal = anchor != null ? anchor.InverseTransformPoint(worldPos) : worldPos;
            safePositionOwner = CurrentOwner;
            hasSafePosition = true;
        }

        /// <summary>
        /// False once the NPC rides a different frame: the stored point then names a
        /// spot in the old frame, which after an undock is an arbitrary point in vacuum.
        /// </summary>
        private bool TryRecallSafePosition(out Vector3 worldPos)
        {
            worldPos = Vector3.zero;
            if (!hasSafePosition || safePositionOwner != CurrentOwner) return false;

            Transform anchor = transform.parent;
            worldPos = anchor != null ? anchor.TransformPoint(lastValidInsideLocal) : lastValidInsideLocal;
            return true;
        }

        /// <summary>
        /// Tracks the NPC's stable floor plane.
        /// </summary>
        private void UpdateBaseFloor()
        {
            if (!Physics.Raycast(GroundPos(0.3f), Vector3.down, out RaycastHit hit, 3f, ProbeLayers, QueryTriggerInteraction.Ignore))
            {
                return;
            }

            if (!hasBaseFloor)
            {
                baseFloorY = hit.point.y;
                hasBaseFloor = true;
                baseFloorMismatchTimer = 0f;
                return;
            }

            if (Mathf.Abs(hit.point.y - baseFloorY) <= 0.12f)
            {
                baseFloorY = hit.point.y;
                baseFloorMismatchTimer = 0f;
            }
            else
            {
                baseFloorMismatchTimer += 0.25f;
                if (baseFloorMismatchTimer > 4f)
                {
                    baseFloorY = hit.point.y;
                    baseFloorMismatchTimer = 0f;
                }
            }
        }

        /// <summary>
        /// Keeps the NPC's current room content enabled.
        /// </summary>
        private void SetForcedRoom(Room room)
        {
            if (forcedRoom == room) return;

            if (forcedRoom != null) forcedRoom.OnContentStateChanged.RemoveListener(OnForcedRoomContentChanged);

            forcedRoom = room;

            if (forcedRoom != null)
            {
                forcedRoom.OnContentStateChanged.AddListener(OnForcedRoomContentChanged);
                LoadRoom(forcedRoom, "the NPC is in it");
            }
        }

        private void OnForcedRoomContentChanged(bool contentEnabled)
        {
            // A parked NPC holds nothing loaded: docs/invariants.md#an-unloaded-ship-parks-the-npc
            if (!contentEnabled && !IsDead && isActiveAndEnabled && forcedRoom != null)
            {
                using NpcRegistry.ActingScope _ = NpcRegistry.Acting(this);
                LoadRoom(forcedRoom, "switched off by the game, the NPC is in it");
            }
        }

        /// <summary>
        /// Keeps the NPC in the frame of the floor it is standing on. The game never
        /// moves the ship, it moves the world around it, so an NPC left on a station
        /// must ride that station's container - exactly as a dropped item does via
        /// SpaceObject.SetItemOwnership. The container is also what the game deactivates
        /// on undock, which is what parks the NPC in place.
        /// docs/invariants.md#an-npc-rides-its-own-floor
        /// </summary>
        private void UpdateOwnerAnchor()
        {
            NpcVessels.FloorOwner(transform.position, out string? owner, out Transform? anchor);
            // No floor to judge by: keep the frame we have rather than guess a new one.
            if (owner == null || owner == CurrentOwner) return;

            CurrentOwner = owner;
            if (transform.parent == anchor) return;

            transform.SetParent(anchor, true);
            NpcLog.Log.LogInfo("[ai] Riding '" + owner + "' now");
        }

        /// <summary>
        /// The last room crossed into, for log lines.
        /// </summary>
        public string TrackedRoomName => currentRoomRef != null ? currentRoomRef.gameObject.name : "none";

        /// <summary>
        /// Room and environment, and the life threat only while there is one, for a HUD.
        /// </summary>
        public string DescribeSurroundings()
        {
            // The room is the last EntryDetector the NPC crossed, not a volume test -
            // the game has no room volumes, so this label is sticky by design.
            string text = "Room: " + (currentRoomRef != null ? currentRoomRef.gameObject.name : "unknown");
            if (currentEnvironmentRef != null)
            {
                text += "\nEnv: " + currentEnvironmentRef.gameObject.name +
                        " (O2 " + currentEnvironmentRef.Data.Oxygen +
                        ", temp " + currentEnvironmentRef.Data.Temperature + ")";
            }
            else
            {
                text += "\nEnv: none" + (currentlyInSpace ? " [EXPOSED TO SPACE]" : "");
            }
            if (lifeThreatLevel > 0 || deathCounter > 0 || lifeThreat)
            {
                text += "\nLife threat: " + lifeThreatLevel + "/10, death " + deathCounter + "/5" + (lifeThreat ? " [!]" : "");
            }
            return text;
        }

        /// <summary>
        /// A restored NPC starts in its saved vessel's frame, before any floor is probed.
        /// </summary>
        public void RideOwner(string owner, Transform anchor)
        {
            CurrentOwner = owner;
            transform.SetParent(anchor, true);
        }

        /// <summary>
        /// Enables room content like the game does when the player enters a room. An NPC never
        /// switches one off here. docs/invariants.md#an-npc-loads-rooms-by-the-door-rule
        /// </summary>
        public void LoadRoom(Room? room, string why, Room? across = null)
        {
            if (room == null || !room.gameObject.activeInHierarchy) return;

            CustomRoom? customRoom = room as CustomRoom;
            if (customRoom != null && !customRoom.EnabledStructure) return;

            if (room.ContentEnabled) return;

            room.SetContentEnabled(true);
            LogRoomLoaded(room, why, across);
        }

        /// <summary>
        /// From the EntryDetector patch, as INpcDoorUser.OnDoorClosedAway.
        /// A door the NPC shut has finished closing, away from the player: both rooms it joins go back off
        /// unless someone is in one or can see into it, as EntryDetector does once the player has fully left
        /// a room. Both, whoever switched them on: the NPC may be rooms away by now, and a room kept for
        /// another open door is looked at again when that door shuts. docs/invariants.md#an-npc-loads-rooms-by-the-door-rule
        /// </summary>
        private void ReleaseRoomsAt(EntryDetector detector)
        {
            if (IsDead || !isActiveAndEnabled) return;

            // The game keeps both sides of this doorway loaded for the player too.
            if (!NpcDoors.Optimizes(detector)) return;

            NpcDoors.RoomsOf(detector, out Room? inner, out Room? outer);
            // Just after this crossing the tracked room can still be the one behind. The side test is a plane,
            // sound only for the two rooms that share it: from a third room it can name either side.
            Room? npcSide = null;
            if ((currentRoomRef == inner || currentRoomRef == outer) &&
                NpcDoors.TryInnerSide(detector, transform.position, out bool innerSide))
            {
                npcSide = innerSide ? inner : outer;
            }
            ReleaseRoom(inner, outer, npcSide);
            ReleaseRoom(outer, inner, npcSide);
        }

        private void ReleaseRoom(Room? room, Room? across, Room? npcSide)
        {
            if (room == null || !room.ContentEnabled) return;

            string? keep = room == npcSide ? "the NPC has just come in" : ReasonToKeepLoaded(room);
            if (keep != null)
            {
                if (NpcLog.Level >= 2)
                {
                    NpcLog.Log.LogInfo("[ai] Keeping room '" + room.gameObject.name + "' loaded: " + keep);
                }
                return;
            }

            room.SetContentEnabled(false);
            if (NpcLog.Level >= 1)
            {
                NpcLog.Log.LogInfo("[ai] Unloaded room '" + room.gameObject.name + "' (door to '" +
                                   (across != null ? across.gameObject.name : "?") + "' shut) - " +
                                   CountLoadedRooms() + " rooms loaded");
            }
        }

        /// <summary>
        /// Why a room must stay on: an NPC or the player is in it, the player is outside (a station shows
        /// every room then), a mod keeps it (a sell station's), or an open door looks into it.
        /// </summary>
        private string? ReasonToKeepLoaded(Room room)
        {
            // The forced room's listener would switch it straight back on - any NPC's.
            if (room == forcedRoom || room == currentRoomRef) return "the NPC is still tracked in it";
            if (NpcRegistry.OtherTrackedIn(room, this) is { } other) return other.Name + " is in it";

            Player? pilot = NpcPlayer.Pilot;
            if (pilot != null && pilot.CurrentRoom == room) return "you are in it";
            // SpaceStation.OnStationExit switches every room on for the view from outside.
            if (pilot != null && IsPlayerInSpace(pilot)) return "you are outside";
            // docs/invariants.md#a-kept-room-stays-loaded
            if (NpcRooms.ReasonToKeep(room) is { } kept) return kept;

            // Station rooms list no doors of their own, so the doorways are asked.
            foreach (EntryDetector detector in NpcDoors.Detectors)
            {
                if (detector == null) continue;

                NpcDoors.RoomsOf(detector, out Room? inner, out Room? outer);
                if (inner != room && outer != room) continue;

                // Named by the room across: every station door is called 'Door02'.
                Room? across = inner == room ? outer : inner;
                string acrossName = across != null ? across.gameObject.name : "?";
                Gate? door = NpcDoors.DoorOf(detector);
                if (door == null) return "its doorway to '" + acrossName + "' has no door";
                if (door.Opened) return "its door to '" + acrossName + "' is open";
            }
            return null;
        }

        /// <summary>
        /// Which room the NPC switched on and why, with how many are on around the doorways: the
        /// only measure of what it adds to the game's own loading. Deduped per room.
        /// </summary>
        private void LogRoomLoaded(Room room, string why, Room? across)
        {
            if (NpcLog.Level < 1) return;

            int id = room.GetInstanceID();
            if (roomLoadLoggedAt.TryGetValue(id, out float last) && Time.time - last < RoomLoadLogCooldown) return;

            roomLoadLoggedAt[id] = Time.time;
            NpcLog.Log.LogInfo("[ai] Loaded room '" + room.gameObject.name + "' (" + why +
                               (across != null ? " '" + across.gameObject.name + "'" : "") + ") - " +
                               CountLoadedRooms() + " rooms loaded");
        }

        /// <summary>
        /// Rooms with their content on, among those the active doorways join.
        /// </summary>
        private static int CountLoadedRooms()
        {
            LoadedRoomSet.Clear();
            foreach (EntryDetector detector in NpcDoors.Detectors)
            {
                if (detector == null) continue;

                NpcDoors.RoomsOf(detector, out Room? inner, out Room? outer);
                if (inner != null && inner.ContentEnabled) LoadedRoomSet.Add(inner);
                if (outer != null && outer.ContentEnabled) LoadedRoomSet.Add(outer);
            }
            int count = LoadedRoomSet.Count;
            LoadedRoomSet.Clear();
            return count;
        }


        // ------------------------------------------------------------------
        // Environment / space protection / mortality
        // ------------------------------------------------------------------

        private void UpdateEnvironment()
        {
            Environment? env = null;

            if (currentRoomRef != null &&
                NavGraph.TryVesselOf(currentRoomRef.transform.parent, out SpaceShip? ship, out SpaceObject? spaceObject))
            {
                // TryVesselOf returned true without a ship, so it found a SpaceObject.
                env = ship != null ? GameManager.Instance.PlayerShip.Environment : spaceObject!.GetComponentInChildren<Environment>();
            }

            if (env == null && (hasKnownRoom || IsEnclosed())) env = FindNearestEnvironment();

            if (!currentlyInSpace) currentEnvironmentRef = env;
        }

        /// <summary>
        /// True when the NPC is aboard the player's ship. The floor decides;
        /// `currentRoomRef` looks more authoritative but is only the last doorway
        /// crossed. docs/invariants.md#aboard-is-answered-by-the-floor
        /// </summary>
        public bool IsAboardPlayerShip()
        {
            SpaceShip? ship = GameManager.Instance != null ? GameManager.Instance.PlayerShip : null;
            if (ship == null) return false;

            switch (NpcVessels.FloorOwner(transform.position))
            {
                case FloorOwnership.PlayerShip: return true;
                case FloorOwnership.Elsewhere: return false;
            }

            // No floor underneath (mid-jump, mid-teleport, in a doorway over a gap):
            // fall back to the last room the NPC was tracked into.
            if (currentRoomRef != null && ship.Rooms != null)
            {
                foreach (CustomRoom room in ship.Rooms)
                {
                    if (room != null && room == currentRoomRef) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Fires 10 horizontal rays; inside any structure most of them hit geometry
        /// within 20m, in open space almost none do.
        /// </summary>
        private bool IsEnclosed()
        {
            Vector3 origin = GroundPos(0.6f);
            int hits = 0;
            for (int i = 0; i < 10; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, i * 36f, 0f) * Vector3.forward;
                if (Physics.Raycast(origin, dir, 20f, ProbeLayers, QueryTriggerInteraction.Ignore)) hits++;
            }
            return hits >= 4;
        }

        private Environment? FindNearestEnvironment()
        {
            RefreshEnvironments();
            if (cachedEnvironments == null || GameManager.Instance == null) return null;

            Environment spaceEnv = GameManager.Instance.SpaceEnvironment;
            Environment? best = null;
            float bestDistanceSquared = 45f * 45f;
            Vector3 pos = transform.position;

            foreach (Environment env in cachedEnvironments)
            {
                if (env == null || env == spaceEnv) continue;

                if (!env.gameObject.activeInHierarchy) continue;

                float distanceSquared = (env.transform.position - pos).sqrMagnitude;
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    best = env;
                }
            }
            return best;
        }

        private void UpdateSpaceState(Player? player)
        {
            GameManager gm = GameManager.Instance;
            bool inSpace =
                (currentRoomRef != null && gm.SpaceRoom != null && currentRoomRef == gm.SpaceRoom) ||
                (hasKnownRoom && currentEnvironmentRef == null) ||
                (player != null && player.Controller != null &&
                 transform.position.y < player.Controller.CachedTransform.position.y - 25f);

            currentlyInSpace = inSpace;

            if (inSpace && settings.KeepOutOfSpace) ReturnFromSpace(player);
        }

        private void ReturnFromSpace(Player? player)
        {
            Vector3 target;
            if (hasSafePosition && TryRecallSafePosition(out Vector3 safe))
            {
                target = safe;
            }
            else if (player != null && player.Controller != null && !IsPlayerInSpace(player))
            {
                target = player.Controller.CachedTransform.position +
                         player.Controller.CachedTransform.forward * 1.5f;
            }
            else
            {
                return;
            }

            if ((target - transform.position).sqrMagnitude < 0.25f) return;

            TeleportTo(target + Vector3.up * 0.1f, true);
            navPlan = null;
            NpcLog.Log.LogWarning($"[ai] {Name} reached open space, teleported back inside");
        }

        /// <summary>
        /// The player is on a spacewalk: no NPC follows them out.
        /// </summary>
        public static bool IsPlayerInSpace(Player player)
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.SpaceRoom == null) return false;

            return player.CurrentRoom == gm.SpaceRoom;
        }

        /// <summary>
        /// Moves the NPC aboard when it has no floor of its own to ride - the game
        /// does exactly this to its own free-roaming NPC on undock (Docker.Undock).
        /// </summary>
        internal void PullAboard(Vector3 position)
        {
            if (IsDead) return;
            // Back to the ship's frame, which is the scene root - the next owner probe
            // re-parents it if it turns out to be standing on something else.
            transform.SetParent(null, true);
            TeleportTo(position, true);
            navPlan = null;
            CurrentOwner = null;
            hasSafePosition = false;
            NpcLog.Log.LogWarning("[ai] Undocked with nothing underfoot - moved to the ship airlock");
        }

        /// <summary>
        /// Freezes an NPC aboard while the game unloads the player's ship for a spacewalk,
        /// before any room goes dark. docs/invariants.md#an-unloaded-ship-parks-the-npc
        /// </summary>
        internal void ParkWithShip()
        {
            if (parkedWithShip || IsDead || !gameObject.activeInHierarchy || !IsAboardPlayerShip()) return;

            parkedWithShip = true;
            Hands.Drop("the ship is unloading");
            brain.OnInterrupted("the ship is unloading");
            gameObject.SetActive(false);
            NpcLog.Log.LogInfo("[ai] Player ship unloaded for a spacewalk - parked aboard");
        }

        internal void UnparkFromShip()
        {
            if (!parkedWithShip) return;

            parkedWithShip = false;
            gameObject.SetActive(true);
            NpcLog.Log.LogInfo("[ai] Player ship loaded again - unparked");
        }

        /// <summary>
        /// After the player ship was rebuilt around an NPC aboard: moves it by `worldShift`, and
        /// forgets every world position taken in the old layout. docs/invariants.md#an-npc-rides-its-own-floor
        /// </summary>
        internal void RideShipRebuild(byte stage, Vector3 worldShift, bool floorStands, string what)
        {
            if (IsDead) return;

            // The spot it hid in belongs to a room that is being rebuilt around it.
            brain.OnInterrupted("the ship is being rebuilt");

            if (worldShift.sqrMagnitude > 0.0001f) TeleportTo(transform.position + worldShift, true);

            // A walk's goal is a world point of the old layout too, so the brain ends it rather than resumes.
            bool gotoEnded = brain.OnShipRebuilt();
            DropPlan();
            hasMoveTarget = false;
            followStepOffUntil = 0f;

            bool safeSpot = floorStands && !NpcDoors.RoomIsAirlockChamber(currentRoomRef);
            if (safeSpot) RememberSafePosition(transform.position);
            else hasSafePosition = false;

            if (NpcLog.Level < 1) return;

            NpcLog.Log.LogInfo(
                $"[ai] Ship rebuilt (stage {stage}): {what}; plan dropped, safe spot " +
                (safeSpot ? "set here" : "forgotten") + (gotoEnded ? ", goto ended" : ""));
        }

        /// <summary>
        /// Moves the body with its controller off, so nothing in between stops it. `stopFalling` also
        /// zeroes the fall speed.
        /// </summary>
        public void TeleportTo(Vector3 position, bool stopFalling)
        {
            cc.enabled = false;
            transform.position = position;
            cc.enabled = true;
            hasBaseFloor = false;
            baseFloorMismatchTimer = 0f;
            if (stopFalling) verticalVelocity = 0f;
        }

        /// <summary>
        /// The game's own tick, the one HealthSystem.Tick runs on. Subscribed while enabled, so a
        /// parked NPC takes no damage. docs/game-model.md#atmosphere-kills-by-the-players-rule
        /// </summary>
        private void OnEnable()
        {
            // Collider pairs are only ignored between active colliders: an unparked NPC asks again.
            NpcRegistry.IgnoreAllBodies(this);

            GameManager gm = GameManager.Instance;
            if (gm == null || tickSource == gm) return;

            tickSource = gm;
            gm.OnTick.AddListener(AtmosphereTick);
        }

        private void OnDisable()
        {
            if (tickSource != null) tickSource.OnTick.RemoveListener(AtmosphereTick);

            tickSource = null;
        }

        /// <summary>
        /// The player's own death rule (HealthSystem.Tick), once per game tick: a threat of
        /// LifeThreatLethal or more counts up, anything less counts down.
        /// </summary>
        private void AtmosphereTick()
        {
            if (!settings.Mortal || !settings.BreathesAir || IsDead || Asleep || NpcMonster.NpcsDisabled) return;

            using NpcRegistry.ActingScope _ = NpcRegistry.Acting(this);
            // Counted one tick late, as the player's is: the buff reaches HealthSystem through BuffBar.
            lifeThreatLevel = pendingThreat;
            pendingThreat = AtmosphereThreat(currentlyInSpace ? null : currentEnvironmentRef);
            lifeThreat = lifeThreatLevel >= LifeThreatLethal;
            if (lifeThreat) deathCounter = Mathf.Min(deathCounter + 1, DeathCounterCap);
            else if (deathCounter > 0) deathCounter--;

            TraceAtmosphere();

            if (deathCounter > DeathCounterArmed && UnityEngine.Random.Range(0, 5) == 0)
            {
                NpcLog.Log.LogWarning($"[ai] {Name} died from a deadly environment (threat {lifeThreatLevel})");
                Die(transform.forward * 2f + Vector3.up);
            }
        }

        private static readonly string[] BuffNames = ["Hunger", "Suffocation", "Hyperoxia", "Cold", "Heat", "Sleepiness", "Energy"];

        /// <summary>
        /// Level 2, once per game tick while either side is threatened: the NPC's numbers
        /// beside the player's, with what feeds the player's. docs/game-model.md#atmosphere-kills-by-the-players-rule
        /// </summary>
        private void TraceAtmosphere()
        {
            if (NpcLog.Level < 2) return;

            Player? player = NpcPlayer.Pilot;
            HealthSystem? health = player != null ? player.HealthSystem : null;
            int playerThreat = health != null ? health.CurrentTotalThreat : 0;
            byte? playerCounter = GameInternals.HealthSystemAccess.GetDeathCounter(health);
            if (lifeThreatLevel == 0 && deathCounter == 0 && playerThreat == 0 && playerCounter.GetValueOrDefault() == 0) return;

            Environment? env = currentlyInSpace ? null : currentEnvironmentRef;
            string line = $"[ai] Air tick: NPC threat {lifeThreatLevel}, death {deathCounter}/{DeathCounterArmed}" +
                          (env != null ? $" (O2 {env.Data.Oxygen / 100f:0.0}%, {env.Data.Temperature / 100f:0.0}C)" : " (space)");
            if (player != null && health != null)
            {
                Environment? playerEnv = player.CurrentEnvironment;
                line += $" | player threat {playerThreat}, death {(playerCounter.HasValue ? playerCounter.Value.ToString() : "?")}/{DeathCounterArmed}" +
                        (playerEnv != null ? $" (O2 {playerEnv.Data.Oxygen / 100f:0.0}%, {playerEnv.Data.Temperature / 100f:0.0}C)" : " (no air)") +
                        (playerEnv == env ? ", same air" : ", OTHER air") + ", buffs:";
                bool any = false;
                if (player.BuffSystem != null && player.BuffSystem.Data != null)
                {
                    foreach (BuffData buff in player.BuffSystem.Data.Buffs)
                    {
                        if (buff.time == 0) continue;

                        line += " " + (buff.id < BuffNames.Length ? BuffNames[buff.id] : "#" + buff.id) + buff.Strength;
                        any = true;
                    }
                }
                if (!any) line += " none";
                Suit? suit = player.EquipmentSystem != null ? player.EquipmentSystem.CachedSuitItem : null;
                if (suit != null) line += $", suit {(suit.Isolated ? "isolated" : "resist " + suit.TemperatureResistance)}";
            }
            NpcLog.Log.LogInfo(line);
        }

        /// <summary>
        /// The room as the NPC feels it: its suit's resistance added to the room, as Player.Temperature
        /// does for the player's. docs/game-model.md#atmosphere-kills-by-the-players-rule
        /// </summary>
        public int FeltTemperature(Environment env) => env.Data.Temperature + settings.SuitTemperatureResistance;

        /// <summary>
        /// The threat the player's atmosphere buffs set (Suffocation, Hyperoxia, Cold, Heat in
        /// Space/Player.cs), in the NPC's suit. No environment is space, and lethal.
        /// </summary>
        private int AtmosphereThreat(Environment? env)
        {
            if (env == null) return VacuumThreat;

            int oxygen = env.Data.Oxygen;
            int temperature = FeltTemperature(env);
            int threat = 0;

            if (oxygen < 600) threat += 3 * 7;
            else if (oxygen < 1950) threat += 7;
            else if (oxygen > 5000) threat += 2 * 3;
            else if (oxygen >= 3200) threat += 3;

            if (temperature < -2000) threat += 3 * 6;
            else if (temperature < 0) threat += 2 * 6;
            else if (temperature < 1400) threat += 6;
            else if (temperature > 4000) threat += 2 * 8;
            else if (temperature > 3200) threat += 8;

            return threat;
        }

        /// <summary>
        /// The Breathless ignores objects without a Player component, so we detect the
        /// catch ourselves and then reuse the monster's own grab choreography.
        /// </summary>
        private void BreathlessCheck()
        {
            if (!settings.Mortal || !settings.CanBeCaught || IsDead || catchInProgress) return;
            // The monster is already grabbing another NPC (a parked one's catch stopped with it).
            // docs/invariants.md#one-catch-at-a-time
            if (NpcMonster.CatchingAnother(this)) return;
            // Shut in a closet, it is out of reach: the walls block the monster.
            if (brain.Sheltered) return;
            // The NPC's catch is NPC.Core's own - the game's detectors never see it - so
            // ai_disable has to stop it here. ai_notarget deliberately does not:
            // it blinds the monster to the player only. docs/reference.md#2-console-commands
            if (NpcMonster.MonsterDisabled) return;

            Breathless breathless = GameManager.Instance.Breathless;
            if (breathless == null || !breathless.gameObject.activeInHierarchy) return;

            if ((breathless.transform.position - transform.position).sqrMagnitude < 2.89f)
            {
                StartCoroutine(CatchRoutine(breathless));
            }
        }

        private System.Collections.IEnumerator CatchRoutine(Breathless breathless)
        {
            catchInProgress = true;
            NpcMonster.BeginCatch(this);
            // Its own source throughout: an acting scope must not outlive a yield.
            LogSource.LogWarning($"[ai] The Breathless grabbed {Name}");
            hasMoveTarget = false;

            // Remember the monster's cloaked/visible state so it can be restored after the kill.
            bool visibilityWas = breathless.Visible;

            // Stop the monster from homing in on the player while it is busy grabbing the NPC.
            BreathlessAggressor aggressor = breathless.Aggressor;
            bool aggressorWasEnabled = aggressor != null && aggressor.enabled;
            // aggressorWasEnabled implies aggressor != null.
            if (aggressorWasEnabled) aggressor!.enabled = false;

            bool killed = false;
            void OnContact()
            {
                if (killed || IsDead) return;

                killed = true;
                using NpcRegistry.ActingScope scope = NpcRegistry.Acting(this);
                KillFromBreathless(breathless);
            }

            try
            {
                breathless.Animator.SetVisibility(true);
                breathless.Animator.Follow(transform);
                breathless.MakeAngrySound();
                breathless.PlayMovementSound();

                for (int i = 0; i < 4 && !killed; i++) breathless.Animator.Touch(transform, OnContact);
            }
            catch (Exception ex)
            {
                LogSource.LogWarning($"[ai] Catch sequence failed: {ex.Message}");
            }

            float waited = 0f;
            while (waited < 4f && !killed)
            {
                waited += Time.deltaTime;
                yield return null;
            }
            if (!killed && !IsDead)
            {
                using NpcRegistry.ActingScope _ = NpcRegistry.Acting(this);
                KillFromBreathless(breathless);
            }

            yield return CatchReleaseDelay;

            try
            {
                breathless.Animator.Unfollow();
                breathless.Animator.SetVisibility(visibilityWas);
            }
            catch (Exception ex)
            {
                // The Breathless may be destroyed mid-catch; the cleanup is best
                // effort, but it must never kill the coroutine (catchInProgress
                // is reset below and the NPC would freeze otherwise).
                LogSource.LogWarning("[ai] Breathless release cleanup failed: " + ex.Message);
            }

            // Restore to what the debug override says, not to what the flag happened to
            // be when the grab started - otherwise a catch silently re-arms the monster.
            if (aggressorWasEnabled && aggressor != null && !NpcMonster.PlayerIgnored) aggressor.enabled = true;

            catchInProgress = false;
            NpcMonster.EndCatch(this);
        }

        private void KillFromBreathless(Breathless breathless)
        {
            Vector3 away = (transform.position - breathless.transform.position).normalized;
            if (away.sqrMagnitude < 0.001f) away = Vector3.up;

            NpcLog.Log.LogWarning($"[ai] The Breathless killed {Name}");
            Die(away * 3f + Vector3.up * 2f);
        }

        /// <summary>
        /// Kills the NPC and swaps the animated model for the body's ragdoll, which NpcCorpse then
        /// makes carryable.
        /// </summary>
        public void Die(Vector3 impulse)
        {
            if (IsDead) return;

            Hands.Drop("dying");
            brain.OnInterrupted("dying");
            IsDead = true;
            brain.OnDied();
            hasMoveTarget = false;
            waitingGate = null;
            catchInProgress = false;
            // A catch that killed it frees the monster for another NPC now, not after the release delay.
            NpcMonster.EndCatch(this);
            // UpdateDoorCloseBehind returns early once IsDead, so these would sit here
            // holding stale Gate references for the rest of the scene. The doors stay
            // open either way - a corpse cannot close them.
            pendingDoorCloses.Clear();

            if (ragdollObject != null)
            {
                ragdollObject.transform.parent = null;
                ragdollObject.SetActive(true);

                GameSettingsData? settingsData = SceneLoader.Instance != null ? SceneLoader.Instance.GameData?.Settings : null;
                float gravityScale = 9.81f * (settingsData?.gravityMultiplier ?? 1f);

                foreach (Rigidbody rb in ragdollObject.GetComponentsInChildren<Rigidbody>(true))
                {
                    rb.isKinematic = false;
                    rb.detectCollisions = true;
                    rb.useGravity = gravityScale > 0.01f;
                }

                foreach (Collider col in ragdollObject.GetComponentsInChildren<Collider>(true))
                {
                    col.enabled = true;
                    // Walls and door frames are on Default: docs/agent.md#1-attaching-an-agent
                    if (!Physics.GetIgnoreLayerCollision(col.gameObject.layer, 0)) continue;

                    NpcLog.Log.LogWarning($"[ai] Ragdoll part '{col.name}' is on layer '{LayerMask.LayerToName(col.gameObject.layer)}', " +
                                          "which passes through the world - the corpse falls through the floor. Use 'Grabbable'.");
                }
                // The corpse is a body too, for NPCs and the player: docs/invariants.md#npcs-never-block-each-other
                NpcRegistry.IgnoreAllBodies(this);

                if (ragdollRigidbody != null) ragdollRigidbody.AddForce(impulse, ForceMode.Impulse);
                // The body can be picked up and carried, like the helper robot's.
                if (settings.CarryableCorpse) ragdollObject.AddComponent<NpcCorpse>().Init(this, ragdollRigidbody);
            }

            if (animatedModel != null) animatedModel.SetActive(false);

            if (cc != null) cc.enabled = false;
            if (itemBlocker != null) itemBlocker.enabled = false;

            StopFootsteps();

            HideDebugVisuals();
            NpcLog.Log.LogWarning($"[ai] {Name} died");
        }

        // ------------------------------------------------------------------
        // Scene caches
        // ------------------------------------------------------------------

        private void RefreshEnvironments()
        {
            if (cachedEnvironments != null && Time.time < environmentsRefreshAt) return;
            if (!SceneScan.MayRescan(cachedEnvironments == null)) return;

            cachedEnvironments = [.. FindObjectsOfType<Environment>()];
            environmentsRefreshAt = Time.time + 5f;
        }
    }
}
