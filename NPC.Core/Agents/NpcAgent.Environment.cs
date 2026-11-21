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
        // Until the first UpdateEnvironment, a null environment means "not read yet", not vacuum.
        private bool airSensed = false;

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
            // Outside, a station doorway a wall away is not one the NPC walked through.
            if (outside) return;

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

                if (MaySaveSpotAt(pos)) RememberSafePosition(pos);
            }
        }


        /// <summary>
        /// No safe spot in an airlock chamber, the ship's included: a cycle may vent it.
        /// </summary>
        private static bool MaySaveSpotAt(Vector3 worldPos) => NpcDoors.ChamberAt(worldPos, withShip: true) == null;

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
        /// Keeps the NPC's current room content enabled; null holds none.
        /// </summary>
        private void SetForcedRoom(Room? room)
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
        private float noFloorLoggedAt = -999f;

        private void UpdateOwnerAnchor()
        {
            NpcVessels.FloorOwner(transform.position, out string? owner, out Transform? anchor);
            // No floor to judge by: keep the frame we have rather than guess a new one.
            if (owner == null)
            {
                if (CurrentOwner != null && Time.time - noFloorLoggedAt >= 10f)
                {
                    noFloorLoggedAt = Time.time;
                    NpcLog.Log.LogInfo("[ai] No floor under " + transform.position + " - still riding '" + CurrentOwner + "'");
                }
                return;
            }
            if (owner == CurrentOwner) return;

            CurrentOwner = owner;
            string floor = NavProbe.TryFloorCollider(transform.position, out Collider? hit) ? NavProbe.ScenePath(hit.transform) : "none";
            NpcLog.Log.LogInfo("[ai] Floor '" + floor + "' is " + owner + "'s");
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
            if (customRoom != null && !customRoom.Enabled) return;

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
        /// every room then), a mod keeps it (`NpcRooms`), or an open door looks into it.
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
            airSensed = true;
        }

        /// <summary>
        /// The air it breathes: its room's environment, null in space. A brain's own danger test reads it.
        /// </summary>
        public Environment? Air => currentlyInSpace ? null : currentEnvironmentRef;

        /// <summary>
        /// True when the NPC is aboard the player's ship. The floor decides;
        /// `currentRoomRef` looks more authoritative but is only the last doorway
        /// crossed. docs/invariants.md#aboard-is-answered-by-the-floor
        /// </summary>
        public bool IsAboardPlayerShip()
        {
            SpaceShip? ship = GameManager.Instance != null ? GameManager.Instance.PlayerShip : null;
            // Floating over the hull is not aboard.
            if (ship == null || outside) return false;

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
            UpdateAirlockSide();

            GameManager gm = GameManager.Instance;
            // Left floating outside, it would ride the ship's frame wherever the ship flies:
            // docs/invariants.md#an-npc-rides-its-own-floor
            if (Floating && gm.PlayerShip != null && NpcVessels.WorldMoving())
            {
                PullAboard(NpcDoors.ChamberCenter(gm.PlayerShip.Airlock), "The ship moved off with it floating outside");
                return;
            }
            // The last trigger is a fall, not a place: an NPC 25m under the player is dropping
            // into the void even when it is allowed outside. Floating, nothing falls.
            bool fellBehind = !Floating && player != null && player.Controller != null &&
                              transform.position.y < player.Controller.CachedTransform.position.y - 25f;
            bool inSpace = outside ||
                (currentRoomRef != null && gm.SpaceRoom != null && currentRoomRef == gm.SpaceRoom) ||
                (hasKnownRoom && currentEnvironmentRef == null) ||
                fellBehind;

            currentlyInSpace = inSpace;

            // A suited NPC may be outside (a surface walk); a fall below the player is still a rescue.
            if (inSpace && settings.KeepOutOfSpace && (!settings.Suited || fellBehind)) ReturnFromSpace(player);
        }

        private void ReturnFromSpace(Player? player)
        {
            Vector3 target;
            string where = "next to the player";
            if (hasSafePosition && TryRecallSafePosition(out Vector3 safe))
            {
                target = safe;
                where = "to its safe spot";
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
            NpcLog.Log.LogWarning($"[ai] {Name} reached open space, teleported back inside {where} {target}");
            SetOutside(false, "teleported back inside");
        }

        /// <summary>
        /// Outside an airlock, on a surface or floating: the side the last cycle or chamber gave it.
        /// docs/invariants.md#an-airlock-is-crossed-by-its-cycle
        /// </summary>
        public bool IsOutside => outside;

        private bool outside;

        /// <summary>
        /// In a chamber, the NPC is on the side whose door alone stands open - what the game's cycle
        /// does to the player. Out of one, the side it had holds. The ship's airlock is left to its
        /// cycle (CycledWith): docking opens both its doors at once.
        /// </summary>
        private void UpdateAirlockSide()
        {
            inChamber = NpcDoors.ChamberAt(transform.position, withShip: true) != null;
            Airlock? chamber = NpcDoors.ChamberAt(transform.position);
            if (chamber == null || !NpcDoors.TryChamberSide(chamber, out bool toSpace)) return;

            if (outside && !toSpace) EnterThrough(chamber);
            SetOutside(toSpace, "airlock '" + chamber.gameObject.name + "' is open to " + (toSpace ? "space" : "the inside"),
                chamber.ExitGravity);
        }

        /// <summary>
        /// In some airlock's chamber, the ship's included, as of the last slow phase 1: the next cycle
        /// may shut a door on it.
        /// </summary>
        private bool inChamber;

        /// <summary>
        /// Coming in, the NPC is where Airlock.Enter puts the player: the room beside the chamber.
        /// Room tracking was off outside, and an airlock has no doorway sensor. Held again even when it is
        /// the room it went out from: going out let go of it.
        /// </summary>
        private void EnterThrough(Airlock airlock)
        {
            if (GameInternals.AirlockAccess.GetConnectedRoom(airlock) is not { } inside) return;

            currentRoomRef = inside;
            hasKnownRoom = true;
            SetForcedRoom(inside);
        }

        /// <summary>
        /// From NpcAirlocks: the player's cycle moved whoever stood in the chamber, this NPC too if it
        /// stood there. Out, only an NPC that may be outside goes; any other is left to the space rescue,
        /// or to the parking at the ship's airlock. docs/invariants.md#an-airlock-is-crossed-by-its-cycle
        /// </summary>
        internal void CycledWith(Airlock airlock, bool toSpace)
        {
            if (IsDead || !isActiveAndEnabled || NpcDoors.ChamberAt(transform.position, withShip: true) != airlock) return;

            string airlockName = airlock.gameObject.name;
            if (!toSpace)
            {
                EnterThrough(airlock);
                SetOutside(false, "cycled in through '" + airlockName + "' with you");
                return;
            }
            if (settings.KeepOutOfSpace && !settings.Suited) return;

            SetOutside(true, "cycled out through '" + airlockName + "' with you", airlock.ExitGravity);
        }

        private Transform? itemParentCache;
        private float itemParentCacheUntil;

        /// <summary>
        /// ItemParentAt for where the NPC stands, re-probed at most every 0.25 s: the hands ask each frame.
        /// </summary>
        private Transform? ItemParentHere()
        {
            if (Time.time < itemParentCacheUntil && itemParentCache != null) return itemParentCache;

            itemParentCacheUntil = Time.time + 0.25f;
            return itemParentCache = ItemParentAt(transform.position);
        }

        /// <summary>
        /// The content parent an item at `pos` belongs in, by the floor under it - not by the tracked
        /// room alone, which only changes at station doorways and is stale aboard the ship (none of
        /// its rooms has a doorway sensor) and after an undock. Aboard: the tracked room if it is a
        /// ship room, else the nearest one (the ship's rooms are never culled one by one). On a
        /// station: the tracked room if it is that station's, else the station's interior, which only
        /// goes with the whole station. docs/invariants.md#a-carried-item-belongs-to-the-room-its-carrier-is-in
        /// </summary>
        public Transform? ItemParentAt(Vector3 pos)
        {
            FloorOwnership floor = NpcVessels.FloorOwner(pos, out string? owner, out Transform? anchor);
            GameManager? gm = GameManager.Instance;
            SpaceShip? ship = gm != null ? gm.PlayerShip : null;
            Room? tracked = currentRoomRef != null && currentRoomRef.gameObject.activeInHierarchy ? currentRoomRef : null;

            if (floor == FloorOwnership.PlayerShip && ship != null && ship.Rooms != null)
            {
                Room? best = null;
                float bestSqr = float.MaxValue;
                foreach (CustomRoom room in ship.Rooms)
                {
                    if (room == null || !room.gameObject.activeInHierarchy || !room.Enabled) continue;
                    if (room == tracked) return room.ContentParent;

                    float sqr = (room.transform.position - pos).sqrMagnitude;
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        best = room;
                    }
                }
                return best != null ? best.ContentParent : null;
            }

            if (floor == FloorOwnership.Elsewhere)
            {
                if (tracked != null && NpcVessels.OwnerOfTransform(tracked.transform) == owner) return tracked.ContentParent;

                return anchor;
            }

            // No floor to judge by: the tracked room is all there is.
            return tracked != null ? tracked.ContentParent : null;
        }

        /// <summary>
        /// Moves the NPC to a side of the airlocks: for a mod that walked it through one and knows
        /// where it came out. Outside counts as space; only a suited NPC stays there. It keeps the gravity
        /// it has outside, or a surface's full gravity coming from inside.
        /// </summary>
        public void SetOutside(bool value, string why) => SetOutside(value, why, value && outside ? sideGravity : InsideGravity);

        /// <summary>
        /// The same, with the gravity outside: the ExitGravity of the airlock it went through, as
        /// Airlock.Exit sets the player's. 0 floats. Ignored inside. docs/agent.md#8-floating
        /// </summary>
        public void SetOutside(bool value, string why, float gravity)
        {
            float sideValue = value ? Mathf.Max(0f, gravity) : InsideGravity;
            if (outside == value && Mathf.Approximately(sideGravity, sideValue)) return;

            bool wasFloating = Floating;
            outside = value;
            sideGravity = sideValue;
            // docs/invariants.md#an-unloaded-ship-parks-the-npc
            if (value) SetForcedRoom(null);
            if (Floating != wasFloating) OnFloatingChanged();
            NpcLog.Log.LogInfo("[ai] " + Name + (value ? " is outside now - " : " is inside again - ") + why +
                               (value ? " (gravity " + sideValue.ToString("0.##") + ")" : ""));
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
        internal void PullAboard(Vector3 position, string why)
        {
            if (IsDead) return;
            // Back to the ship's frame, which is the scene root - the next owner probe
            // re-parents it if it turns out to be standing on something else.
            transform.SetParent(null, true);
            TeleportTo(position, true);
            navPlan = null;
            CurrentOwner = null;
            hasSafePosition = false;
            NpcLog.Log.LogWarning("[ai] " + why + " - moved to the ship airlock");
            SetOutside(false, "moved aboard");
            // In the room Airlock.Enter gives the player; never while the player is out and the ship is dark.
            SpaceShip? ship = GameManager.Instance != null ? GameManager.Instance.PlayerShip : null;
            Player? pilot = NpcPlayer.Pilot;
            if (ship != null && pilot != null && !IsPlayerInSpace(pilot)) EnterThrough(ship.Airlock);
        }

        /// <summary>
        /// Freezes an NPC aboard while the game unloads the undocked player's ship for a spacewalk,
        /// before any room goes dark. docs/invariants.md#an-unloaded-ship-parks-the-npc
        /// </summary>
        internal void ParkWithShip()
        {
            if (parkedWithShip || IsDead || !gameObject.activeInHierarchy) return;
            if (outside)
            {
                NpcLog.Log.LogInfo("[ai] " + Name + " is outside with you - not parked with the ship");
                return;
            }
            if (!IsAboardPlayerShip()) return;

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
        /// Drops navigation state from the old ship layout. docs/invariants.md#an-npc-rides-its-own-floor
        /// </summary>
        internal void OnShipLayoutChanged(bool floorStands)
        {
            if (IsDead) return;

            brain.OnInterrupted("the ship layout changed");

            // A walk's goal is a world point of the old layout too, so the brain ends it rather than resumes.
            bool gotoEnded = brain.OnShipRebuilt();
            DropPlan();
            hasMoveTarget = false;
            followStepOffUntil = 0f;

            bool safeSpot = floorStands && MaySaveSpotAt(transform.position);
            if (safeSpot) RememberSafePosition(transform.position);
            else hasSafePosition = false;

            if (NpcLog.Level < 1) return;

            NpcLog.Log.LogInfo(
                $"[ai] Ship layout changed: plan dropped, safe spot " +
                (safeSpot ? "set here" : "forgotten") + (gotoEnded ? ", goto ended" : ""));
        }

        /// <summary>
        /// Moves the body with its controller off, so nothing in between stops it. `stopFalling` also
        /// zeroes the fall speed.
        /// </summary>
        public void TeleportTo(Vector3 position, bool stopFalling)
        {
            // A room can be sold after the NPC spawns and before Start caches its controller.
            CharacterController? controller = cc != null ? cc : GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false;
            transform.position = position;
            if (controller != null) controller.enabled = true;
            hasBaseFloor = false;
            baseFloorMismatchTimer = 0f;
            if (!stopFalling) return;

            verticalVelocity = 0f;
            flightVelocity = Vector3.zero;
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
        /// The air is killing it now: its threat is lethal this tick or the next, or its death
        /// counter has not wound down yet. A brain's cue to drop everything and survive.
        /// docs/game-model.md#atmosphere-kills-by-the-players-rule
        /// </summary>
        public bool LifeInDanger => lifeThreat || pendingThreat >= LifeThreatLethal || deathCounter > 0;

        /// <summary>
        /// The player's own death rule (HealthSystem.Tick), once per game tick: a threat of
        /// LifeThreatLethal or more counts up, anything less counts down.
        /// </summary>
        private void AtmosphereTick()
        {
            if (!settings.Mortal || !settings.BreathesAir || IsDead || Asleep || NpcMonster.NpcsDisabled) return;
            // docs/invariants.md#air-is-read-before-it-is-counted
            if (!airSensed) return;

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
                          (settings.Suited ? ", suited" : "") +
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
        /// Space/Player.cs), in the NPC's suit. No environment is space, and lethal. An isolated
        /// suit reads flat 2200/2200 whatever the room, so it feels no band anywhere.
        /// docs/game-model.md#atmosphere-kills-by-the-players-rule
        /// </summary>
        private int AtmosphereThreat(Environment? env)
        {
            if (settings.Suited) return 0;
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

                // Floating, the corpse floats too.
                bool falls = sideGravity * GravityMultiplier > 0.01f;

                foreach (Rigidbody rb in ragdollObject.GetComponentsInChildren<Rigidbody>(true))
                {
                    rb.isKinematic = false;
                    rb.detectCollisions = true;
                    rb.useGravity = falls;
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
