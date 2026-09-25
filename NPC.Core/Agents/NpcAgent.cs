using System.Collections.Generic;
using BepInEx.Logging;
using NPC.Core.Navigation;
using NPC.Core.World;
using Space;
using UnityEngine;

namespace NPC.Core.Agents
{
    /// <summary>
    /// A walking NPC on a body the mod built: plans and follows routes on the nav graph, steers, hops, opens
    /// and closes doors, loads rooms, rides vessels, breathes, can be caught and dies. Where it goes is its
    /// brain's to say. docs/agent.md
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed partial class NpcAgent : MonoBehaviour, INpc, INpcDoorUser, INpcLifeform, INpcHider
    {
        // Handed over by Attach, before the first frame.
        private INpcBrain brain = null!;
        private NpcAgentSettings settings = null!;
        private GameObject? ragdollObject;
        private Rigidbody? ragdollRigidbody;
        private GameObject? animatedModel;
        private Collider? itemBlocker;

        public string ModName { get; private set; } = "";
        /// <summary>
        /// Unique in its mod; with <see cref="Number"/>, see <see cref="NpcIdentity"/>.
        /// </summary>
        public string Name { get; private set; } = "";
        public int Number { get; private set; }
        /// <summary>
        /// Where its lines go while it acts: docs/logging.md#4-rules-for-adding-logs
        /// </summary>
        public ManualLogSource LogSource { get; private set; } = null!; // set by Attach, before the first frame

        public bool IsDead { get; private set; }
        /// <summary>
        /// No AI at all, gravity kept: shut in a cryo capsule, say, until the mod wakes it.
        /// </summary>
        public bool Asleep { get; set; }
        public float MoveSpeed { get; set; } = 3.5f;
        /// <summary>
        /// Which vessel's frame it is riding ("ship", "world" or a station name).
        /// docs/invariants.md#an-npc-rides-its-own-floor
        /// </summary>
        public string? CurrentOwner { get; private set; }
        private Room? currentRoomRef = null;
        private Vector3 currentMoveTarget = Vector3.zero;
        private bool hasMoveTarget = false;

        // Movement / animation
        private CharacterController cc = null!; // set in Start; Update returns before it while null
        private Animator[] anims = [];
        private float verticalVelocity = 0f;
        private bool wasMoving = false;
        private float jumpCommitUntil = 0f;
        private float obstacleReportAt = 0f;

        // The prefab root sits at CharacterController center height (~0.66m above the
        // floor), so probe heights must be computed from the ground, not the transform.
        private float originToFeet = 0f;

        // Node-based navigation. navPlan holds the route plus which segments arrive via
        // a graph link; those skip the LOS commitment check by design.
        // docs/invariants.md#links-have-no-los
        private NavPath? navPlan = null;
        private int navPathIndex = 0;
        /// <summary>
        /// A waypoint on this plan was abandoned rather than reached, so running out of
        /// waypoints is a failure: docs/invariants.md#a-skipped-waypoint-is-not-an-arrival
        /// </summary>
        private bool routeSkippedWaypoint = false;
        private float navPathRecalcAt = 0f;
        private bool hasNavPathGoal = false;

        /// <summary>
        /// Set each frame by the walk that is following a plan; read by the local steering.
        /// </summary>
        private bool walkingStairLeg = false;
        private Vector3 loggedStairLegEnd = Vector3.zero;

        /// <summary>
        /// A Pursue's hysteresis is an XZ test, so a player one deck up reads as 0m away. This deck
        /// gap, measured floor to floor, is what stops the NPC parking underneath them.
        /// docs/invariants.md#follow-arrival-is-level-aware
        /// </summary>
        public const float FollowSameLevelDeltaY = 0.5f;

        // A waypoint the NPC demonstrably failed to reach - it kept moving without
        // closing on it. Fed to FindPath as `avoidEntry`.
        // docs/invariants.md#avoid-entry-bars-the-whole-search
        private Vector3 unreachableWaypoint = Vector3.zero;
        private float unreachableWaypointUntil = 0f;

        private float wanderIdleUntil = 0f;

        // Obstacle avoidance (simplified - NodeGraph handles most routing)
        /// <summary>
        /// Whisker detours tried in order, widest last.
        /// </summary>
        private static readonly float[] DetourAngleList = [30f, -30f, 60f, -60f, 90f, -90f];
        public static IReadOnlyList<float> DetourAngles => DetourAngleList;
        private Vector3 lastAvoidDirection = Vector3.forward;
        private Vector3 sidestepDirection = Vector3.zero;
        private float sidestepUntil = 0f;
        private Vector3 stuckCheckPosition = Vector3.zero;

        // Doors
        private Gate? waitingGate = null;
        private Vector3 followStepOffTarget = Vector3.zero;
        private float followStepOffUntil = 0f;
        // Doors the NPC opened and still owes a close to.
        // docs/invariants.md#pending-closes-are-a-list
        private sealed class PendingDoorClose
        {
            public Gate Gate = null!; // always set by its object initializer
            public float CloseAt;
            public float ArmedAt;
            public int Attempts;
            /// <summary>
            /// When the current run of deferrals began, 0 when last seen clear.
            /// docs/invariants.md#no-permanent-deferral
            /// </summary>
            public float DeferredSince;
            /// <summary>
            /// Where the NPC stood when it opened this gate, and whether it has since
            /// come out the other side.
            /// docs/invariants.md#close-only-what-you-walked-through
            /// </summary>
            public Vector3 OpenedFrom;
            public bool Crossed;
        }
        private readonly List<PendingDoorClose> pendingDoorCloses = [];

        // Monster catch sequence
        private bool catchInProgress = false;

        // Walking into reach of an object. docs/agent.md#6-walking-into-reach
        /// <summary>
        /// Within this of the stand point, the last step is at the target itself.
        /// </summary>
        public const float ReachStandArrival = 0.3f;
        /// <summary>
        /// Within this of its node the route counts as walked; further, it is planned again, at most ReachMaxReplans times.
        /// </summary>
        public const float ReachNodeArrival = 1.2f;
        private static readonly List<Vector3> ReachNodeBuffer = [];

        // Room tracking / environment / safety
        private Room? forcedRoom = null;
        private float slowTimer = 0f;
        private int slowPhase = 0;

        // Last confirmed floor plane. The NPC's own feet are not a good reference:
        // after hopping onto a counter every real floor looks "elevated". A new plane is
        // adopted only after it has stood at that level for a while.
        private float baseFloorY = 0f;
        private bool hasBaseFloor = false;

        // Pre-allocated physics buffers: the whisker/diagnostic probes run every frame,
        // so they must not allocate (use the non-allocating NonAlloc methods).
        // All physics queries happen on the main thread, so shared buffers are safe.
        private static readonly RaycastHit[] CastBuffer = new RaycastHit[32];
        private static readonly Collider[] OverlapBuffer = new Collider[16];

        /// <summary>
        /// Mods in the order they first attached an agent: who steps aside between two NPCs of the same number.
        /// </summary>
        private static readonly List<string> ModOrder = [];

        /// <summary>
        /// Makes `root` a walking NPC, driven by `brain`. Call it on an inactive root and register the agent
        /// with <see cref="NpcRegistry"/> before activating it. docs/agent.md#1-attaching-an-agent
        /// </summary>
        public static NpcAgent Attach(GameObject root, NpcIdentity identity, NpcBody body, NpcAgentSettings settings,
            INpcBrain brain)
        {
            NpcAgent agent = root.AddComponent<NpcAgent>();
            agent.Init(identity, body, settings, brain);
            return agent;
        }

        private void Init(NpcIdentity identity, NpcBody body, NpcAgentSettings agentSettings, INpcBrain npcBrain)
        {
            ModName = identity.ModName;
            Name = identity.Name;
            Number = identity.Number;
            if (!ModOrder.Contains(ModName)) ModOrder.Add(ModName);
            LogSource = NpcRegistry.LogSourceFor(ModName, Name);
            brain = npcBrain;
            settings = agentSettings;
            // NPCs spawned together would otherwise run each slow phase and replan in the same frame.
            int stagger = Number % 4;
            slowPhase = stagger;
            slowTimer = stagger * 0.015f;
            navPathRecalcAt = Time.time + stagger * 0.055f;
            ragdollObject = body.Ragdoll;
            ragdollRigidbody = body.RagdollBody;
            animatedModel = body.AnimatedModel;
            itemBlocker = body.ItemBlocker;
            footstepEvents = body.FootstepEvents;
            MoveSpeed = settings.MoveSpeed;
            Hands = new NpcHands(this, itemBlocker, GroundPos, () => currentRoomRef);
        }

        /// <summary>
        /// Whether this NPC comes before `other` when one of them must step aside: the lower number, then
        /// the mod that attached first.
        /// </summary>
        private bool RanksBefore(NpcAgent other) =>
            Number != other.Number ? Number < other.Number : ModOrder.IndexOf(ModName) < ModOrder.IndexOf(other.ModName);

        /// <summary>
        /// Its own body, or its ragdoll once that has been let go on death.
        /// </summary>
        public bool IsOwnBody(Transform t) =>
            t.IsChildOf(transform) || (ragdollObject != null && t.IsChildOf(ragdollObject.transform));

        public CharacterController? Controller => GetComponent<CharacterController>();

        /// <summary>
        /// What another NPC's controller must not bump: the item blocker, and the ragdoll once dead.
        /// docs/invariants.md#npcs-never-block-each-other
        /// </summary>
        public IEnumerable<Collider> SolidParts()
        {
            if (itemBlocker != null) yield return itemBlocker;

            if (!IsDead || ragdollObject == null) yield break;

            foreach (Collider part in ragdollObject.GetComponentsInChildren<Collider>()) yield return part;
        }

        /// <summary>
        /// Tracked in this room and able to hold it loaded: a parked NPC holds nothing.
        /// </summary>
        public bool TracksRoom(Room room) =>
            !IsDead && isActiveAndEnabled && (room == forcedRoom || room == currentRoomRef);

        // How NPCs of other mods see this one: docs/invariants.md#one-npc-per-target
        Transform INpc.Transform => transform;
        bool INpc.IsActive => isActiveAndEnabled;
        bool INpc.Holds(Transform t) => !IsDead && t != null && brain.Holds(t);

        // What NPC.Core tells it and asks it beyond INpc.
        void INpcDoorUser.OnCloseFailed(Gate gate) => NoteCloseFailed(gate);
        void INpcDoorUser.OnDoorClosedAway(EntryDetector doorway) => ReleaseRoomsAt(doorway);
        bool INpcLifeform.IsAboardPlayerShip() => IsAboardPlayerShip();
        string INpcLifeform.TrackedRoomName => TrackedRoomName;
        bool INpcLifeform.ShowsOnLifecare => settings.ShowOnLifecare;
        bool INpcHider.Occupies(HidingSpot spot) => brain is INpcHider hider && hider.Occupies(spot);

        private void Start()
        {
            using NpcRegistry.ActingScope _ = NpcRegistry.Acting(this);
            cc = GetComponent<CharacterController>();
            anims = GetComponentsInChildren<Animator>();
            SaveParser.OnFileSaveInitiated.AddListener(OnGameSaving);

            foreach (Animator a in anims)
            {
                a.enabled = true;
                a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                a.applyRootMotion = false;
                a.speed = 1f;
                a.updateMode = AnimatorUpdateMode.Normal;
                if (a.gameObject.activeInHierarchy) a.Rebind();
            }

            InitializeFootsteps();

            if (settings.DebugVisuals) EnsureDebugVisuals(true);

            stuckCheckPosition = transform.position;
            ComputeOriginToFeet();
            // Again now every collider is live; OnEnable ran during activation.
            NpcRegistry.IgnoreAllBodies(this);
        }

        /// <summary>
        /// Works out how far the transform origin floats above the ground, so probe
        /// heights (shin / body / head) can be expressed relative to the actual floor.
        /// </summary>
        private void ComputeOriginToFeet()
        {
            if (cc != null)
            {
                originToFeet = cc.center.y - cc.height * 0.5f;
                return;
            }

            if (Physics.Raycast(transform.position, Vector3.down, out RaycastHit hit, 5f, ProbeLayers, QueryTriggerInteraction.Ignore))
            {
                originToFeet = hit.point.y - transform.position.y;
            }
        }

        /// <summary>
        /// How far the feet are from the transform origin: negative, the origin sits at the capsule's centre.
        /// </summary>
        public float OriginToFeet => originToFeet;

        /// <summary>
        /// Floor point under the NPC: plans live on the walkable plane, not on the
        /// transform origin. Goes through NavProbe so every deck is measured the same way.
        /// docs/invariants.md#one-probe-basis
        /// </summary>
        public Vector3 FloorUnderNpc()
        {
            Vector3 p = transform.position;
            float feetY = p.y + originToFeet;

            // Standing on something is the answer, and a better one than any probe.
            // docs/invariants.md#a-grounded-npc-stands-on-its-own-feet
            if (cc != null && cc.isGrounded) return new Vector3(p.x, feetY, p.z);

            if (NavProbe.TryFloorHeight(p, out float floorY)) return new Vector3(p.x, floorY, p.z);
            // Nothing underneath at all (mid-jump over a gap): the feet are the honest
            // answer, never the transform origin - that floats ~0.66m up.
            return new Vector3(p.x, feetY, p.z);
        }

        /// <summary>
        /// The floor the player is standing on, by the same rule as the NPC's.
        /// A Pursue's "are we on the same deck" test runs on this, and the doorway that
        /// answers two storeys down is one the player walks through too.
        /// docs/invariants.md#a-grounded-npc-stands-on-its-own-feet
        /// </summary>
        public float FloorUnderPlayer(Transform playerTransform)
        {
            CharacterController? playerCc = NpcPlayer.Controller;
            if (playerCc != null && playerCc.isGrounded)
            {
                return playerCc.transform.position.y + playerCc.center.y - playerCc.height * 0.5f;
            }
            return NavProbe.FloorHeight(playerTransform.position);
        }

        /// <summary>
        /// A point at the given height above the NPC's feet.
        /// </summary>
        public Vector3 GroundPos(float height)
        {
            return transform.position + Vector3.up * (originToFeet + height);
        }

        /// <summary>
        /// Its current waypoint or target, for a HUD; null when it has none.
        /// </summary>
        public Vector3? MoveTarget => hasMoveTarget ? currentMoveTarget : null;

        public bool HasMoveTarget => hasMoveTarget;

        /// <summary>
        /// What it is walking toward this frame, for steering, stuck detection and the debug lines.
        /// </summary>
        public void SetMoveTarget(Vector3 target)
        {
            currentMoveTarget = target;
            hasMoveTarget = true;
        }

        public void ClearMoveTarget() => hasMoveTarget = false;

        /// <summary>
        /// Moved last frame: a walk with hysteresis keeps going down to its stop distance.
        /// </summary>
        public bool WasMoving => wasMoving;

        /// <summary>
        /// The monster has it: it does not move, and its brain should not start anything.
        /// </summary>
        public bool IsBeingCaught => catchInProgress;

        // ------------------------------------------------------------------
        // Main update
        // ------------------------------------------------------------------

        private void Update()
        {
            if (IsDead || cc == null) return;

            using NpcRegistry.ActingScope _ = NpcRegistry.Acting(this);

            GameManager gm = GameManager.Instance;
            if (gm == null || gm.PlayerShip == null) return;

            Player player = gm.PlayerShip.Pilot;

            // ai_disable, or asleep: everything off, gravity kept so it does not float.
            if (NpcMonster.NpcsDisabled || Asleep)
            {
                ApplyMovement(Vector3.zero, false);
                UpdateAnimation(Vector3.zero, false, player);
                return;
            }

            SlowUpdate(player);

            // While the monster is grabbing the NPC, it can't move.
            if (catchInProgress)
            {
                ApplyMovement(Vector3.zero, false);
                UpdateAnimation(Vector3.zero, false, player);
                return;
            }

            // A conversation, or getting into a closet: the brain moves the body itself.
            if (brain.OverrideMovement(player, out Vector3 held, out bool heldWants))
            {
                if (cc.enabled) ApplyMovement(held, heldWants);

                UpdateAnimation(held, heldWants, player);
                return;
            }

            // Where the brain wants to go this frame.
            walkingStairLeg = false;
            Vector3 desired = brain.Steer(player, out bool wantMove);

            // On a stair leg there is no sidestep, hop or detour: each of those reasons on
            // the flat and led off the flight. docs/invariants.md#a-stair-leg-is-walked-not-improvised
            if (walkingStairLeg) sidestepUntil = 0f;
            else loggedStairLegEnd = Vector3.zero;

            // Sidestep maneuver when stuck against an obstacle.
            if (wantMove && Time.time < sidestepUntil) desired = sidestepDirection * (MoveSpeed * 0.8f);

            desired = brain.Constrain(desired, ref wantMove);

            // Door handling: open closed room gates in front of us, wait for them,
            // and walk through the center of the doorway.
            desired = HandleDoors(desired, ref wantMove);

            // Auto-jump is evaluated on the raw target direction, before steering:
            // otherwise the whiskers deflect the direction first and the jump ray misses
            // the very obstacle the NPC should hop over.
            if (wantMove && !walkingStairLeg) TryAutoJump(desired);

            // While a jump is committed (and during the ascent), steer straight at the
            // obstacle instead of around it - avoidance must not cancel the hop.
            bool steeringSuspended = Time.time < jumpCommitUntil || verticalVelocity > 0.1f;
            if (wantMove && !steeringSuspended && !walkingStairLeg) desired = SteerAroundObstacles(desired);

            // Obstacle diagnostics (level 3): report everything in the NPC's path.
            if (wantMove && settings.DebugVisuals && NpcLog.Level >= 3 && Time.time >= obstacleReportAt)
            {
                obstacleReportAt = Time.time + 0.25f;
                ReportObstacles(desired);
            }

            if (!wantMove)
            {
                desired = Vector3.zero;
                TrySpaceOut();
            }

            UpdateIdleRecovery(wantMove);
            UpdateStuckDetection(wantMove);
            ApplyMovement(desired, wantMove);
            UpdateAnimation(desired, wantMove, player);
            UpdateDebugVisuals();
        }

        /// <summary>
        /// Periodic (non per-frame) logic: room tracking, environment, space safety,
        /// the catch, door closes, then the brain's share of the phase. One phase runs
        /// per call; never add a fifth, it stretches every phase (docs/agent.md#slow-phases).
        /// </summary>
        private void SlowUpdate(Player player)
        {
            slowTimer += Time.deltaTime;
            if (slowTimer < 0.06f) return;

            slowTimer = 0f;
            slowPhase = (slowPhase + 1) % 4;

            switch (slowPhase)
            {
                case 0:
                    UpdateBaseFloor();
                    UpdateOwnerAnchor();
                    UpdateRoomTracking();
                    UpdateEnvironment();
                    break;
                case 1:
                    UpdateSpaceState(player);
                    break;
                case 2:
                    BreathlessCheck();
                    break;
                case 3:
                    UpdateDoorCloseBehind();
                    break;
            }
            brain.SlowPhase(slowPhase, player);
        }

        // ------------------------------------------------------------------
        // Cleanup
        // ------------------------------------------------------------------

        private void OnDestroy()
        {
            using NpcRegistry.ActingScope _ = NpcRegistry.Acting(this);
            SaveParser.OnFileSaveInitiated.RemoveListener(OnGameSaving);
            Hands.Drop("the NPC is gone");
            brain.OnInterrupted("the NPC is gone");
            ReleaseFootsteps();

            if (forcedRoom != null) forcedRoom.OnContentStateChanged.RemoveListener(OnForcedRoomContentChanged);

            // The ragdoll gets unparented on death - clean it up with the NPC.
            if (ragdollObject != null) Destroy(ragdollObject);

            NpcMonster.EndCatch(this);

            NpcRegistry.Unregister(this);
        }
    }
}
