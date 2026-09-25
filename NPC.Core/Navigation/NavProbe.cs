using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using NPC.Core.World;
using Space;
using UnityEngine;

namespace NPC.Core.Navigation
{
    /// <summary>
    /// Shared, allocation-free physics probes: whiskers, edge validation and the editor
    /// all run through here so collider filtering stays consistent. docs/probes.md
    /// Governed by #floor-to-floor and #gate-frame-hit-point in docs/invariants.md.
    /// </summary>
    public static class NavProbe
    {
        private static int _placeRestrictionLayer = -1;
        private static int _bodyLayer = -2;
        private static int _bodyMask;
        private static int _bodyMaskFrame = -1;
        private static bool _bodyLayerWarned;
        // Layers whose probe mask is in the log already.
        private static readonly HashSet<int> LoggedBodyLayers = [];

        /// <summary>
        /// Height above the floor at which line of sight is cast.
        /// </summary>
        private const float EyeHeight = 1.0f;

        /// <summary>
        /// Fallback half-width of the doorway carve-out, used when a gate's own opening
        /// cannot be read. A Gate's leaves slide 0.6m each, so the passage is ~1.2m wide.
        /// </summary>
        private const float GateOpeningRadius = 0.85f;
        /// <summary>
        /// A line through a doorway must cross this far inside the clear opening: the player's
        /// controller radius. docs/invariants.md#a-doorway-is-crossed-not-grazed
        /// </summary>
        private const float CrossingBodyRadius = 0.2f;
        /// <summary>
        /// Least crossing half-width, for a gate whose leaves read narrow.
        /// </summary>
        private const float MinCrossingHalfWidth = 0.25f;
        /// <summary>
        /// Vertical half-extent: docs/invariants.md#gate-carveout-is-the-opening.
        /// </summary>
        private const float GateOpeningHalfHeight = 2.5f;

        /// <summary>
        /// Build-mode restriction volumes around doors. Only the fallback mask needs it
        /// by name now; the physics matrix excludes it from the body's own row.
        /// </summary>
        private static int PlaceRestrictionLayer
        {
            get
            {
                if (_placeRestrictionLayer == -1) _placeRestrictionLayer = LayerMask.NameToLayer("PlaceRestriction");
                return _placeRestrictionLayer;
            }
        }

        /// <summary>
        /// Layer mask for every movement and edge probe: exactly the layers the walker's
        /// body is stopped by, read from the game's collision matrix.
        /// docs/invariants.md#probe-what-the-body-collides-with
        /// </summary>
        public static int ProbeLayers
        {
            get
            {
                // The matrix cannot change without the body's layer changing, so this
                // resolves at most once a frame and rebuilds only on a new layer.
                if (_bodyMaskFrame == Time.frameCount) return _bodyMask;

                _bodyMaskFrame = Time.frameCount;
                int layer = BodyLayer();
                if (layer != _bodyLayer)
                {
                    _bodyLayer = layer;
                    _bodyMask = BodyMaskFor(layer);
                }
                return _bodyMask;
            }
        }

        /// <summary>
        /// The walker's layer: the player's CharacterController, falling back to the layer
        /// named "Player" before there is one. The graph is walked as the player walks it,
        /// so an NPC's controller belongs on that layer. -1 when neither resolves.
        /// </summary>
        private static int BodyLayer()
        {
            Player? pilot = NpcPlayer.Pilot;
            if (pilot != null && pilot.Controller != null) return pilot.Controller.CachedTransform.gameObject.layer;

            return LayerMask.NameToLayer("Player");
        }

        /// <summary>
        /// One line per resolved body layer, naming what the probes will and will not
        /// see. The only place the collision matrix becomes visible in a capture.
        /// </summary>
        private static void LogProbeMask(int layer, int mask)
        {
            if (NpcLog.Level < 2 || !LoggedBodyLayers.Add(layer)) return;

            System.Text.StringBuilder solid = new();
            System.Text.StringBuilder through = new();
            for (int other = 0; other < 32; other++)
            {
                string name = LayerMask.LayerToName(other);
                if (string.IsNullOrEmpty(name)) continue;

                System.Text.StringBuilder target = (mask & (1 << other)) != 0 ? solid : through;
                if (target.Length > 0) target.Append(", ");

                target.Append(name);
            }
            NpcLog.Log.LogInfo(
                "[probe] probe mask: body on layer '" + LayerMask.LayerToName(layer) + "' (" + layer +
                ") is stopped by " + solid + "; walks through " + through);
        }

        /// <summary>
        /// Every layer the given one collides with. For asking a question about a
        /// collider that is not the NPC - what would trip a gate's AntiCrasher, which
        /// sees a different set than a body does.
        /// docs/invariants.md#probe-what-the-body-collides-with
        /// </summary>
        public static int CollisionMaskFor(int layer)
        {
            return layer < 0 ? ProbeLayers : MaskFor(layer);
        }

        /// <summary>
        /// The body's row, with the one sanity check only a body's row can be held to:
        /// every wall, deck and door leaf in the game is on Default, so a row without it
        /// is not a body's - we resolved the wrong layer, and probing on it would report
        /// open space everywhere. An agent's own body probes use its controller's layer here.
        /// </summary>
        public static int BodyMaskFor(int layer)
        {
            int mask = MaskFor(layer);
            LogProbeMask(layer, mask);
            if (layer < 0 || (mask & 1) != 0) return mask;

            if (!_bodyLayerWarned)
            {
                _bodyLayerWarned = true;
                NpcLog.Log.LogWarning(
                    "[probe] layer " + layer + " does not collide with Default; falling back to " +
                    "the default probe mask. Steering will avoid colliders NPCs walk through.");
            }
            return MaskFor(-1);
        }

        /// <summary>
        /// Every layer that layer collides with. A body walks through the rest, so a
        /// probe that sees them measures something the NPC will never touch.
        /// </summary>
        private static int MaskFor(int layer)
        {
            if (layer < 0)
            {
                // No body to ask yet: the old mask, which at least keeps the one
                // exclusion the matrix was known to make.
                int fallback = Physics.DefaultRaycastLayers;
                int restriction = PlaceRestrictionLayer;
                return restriction >= 0 ? fallback & ~(1 << restriction) : fallback;
            }

            int mask = 0;
            for (int other = 0; other < 32; other++)
            {
                if (!Physics.GetIgnoreLayerCollision(layer, other)) mask |= 1 << other;
            }
            return mask;
        }

        /// <summary>
        /// Added to a gate's own opening, for frame trim protruding into it.
        /// </summary>
        private const float GateOpeningMargin = 0.25f;
        /// <summary>
        /// Half-depth through the wall. Deliberately generous: it must cover the wall
        /// slab, its frame and the floor lip inside the passage.
        /// </summary>
        private const float GateOpeningHalfDepth = 1.0f;
        /// <summary>
        /// Ceiling on any carve-out, so one oddly authored gate cannot erase a wall.
        /// </summary>
        private const float MaxGateFootprintRadius = 2.5f;

        private static readonly List<Gate> CachedGates = [];
        /// <summary>
        /// Per-gate carve-out, in the gate's own frame. Every field is independent of
        /// whether the gate is open, so it is measured once and kept until the gate set
        /// changes - not on the 5 s TTL, and never as world bounds: the game moves the
        /// world around the ship.
        /// </summary>
        private static readonly Dictionary<int, GateOpening> GateOpenings = [];
        /// <summary>
        /// Per-gate volume in the gate's own local space, for the same reason.
        /// </summary>
        private static readonly Dictionary<int, Bounds> GateLocalBounds = [];
        private static float _gatesRefreshAt;
        // Set until the first scan and by InvalidateGates: that rescan may not wait for the budget.
        private static bool _gatesForced = true;
        /// <summary>
        /// The gate set as the probes need it, placed for one frame. A gate root moves with
        /// the world while the ship flies, so this never outlives the frame that built it:
        /// docs/invariants.md#never-cache-node-world-positions
        /// </summary>
        private static readonly List<GateProbe> FrameGates = [];
        private static int _frameGatesAt = -1;
        private static bool _inventoryPending = true;
        // RaycastNonAlloc truncates arbitrarily when the buffer fills - it does not keep
        // the nearest hits. Eight was too few in the docking corridor, where station,
        // ship and tube geometry overlap, and the floor itself was being dropped.
        private static readonly RaycastHit[] LosHits = new RaycastHit[64];
        private static readonly RaycastHit[] FloorHits = new RaycastHit[64];
        private static float _truncationWarnedAt = -999f;

        // One frame of per-collider filter answers and per-point floors: a plan asks about the same
        // deck, walls and start point hundreds of times. Exact keys, cleared each frame. docs/probes.md#caches
        private static readonly Dictionary<int, byte> ColliderKinds = [];
        private static readonly Dictionary<Vector3, FloorAnswer> FloorAnswers = [];
        private static int _memoFrame = -1;
        private const byte BodyAnswered = 1;
        private const byte BodyYes = 2;
        private const byte PassableAnswered = 4;
        private const byte PassableYes = 8;

        private readonly struct FloorAnswer(bool found, float floorY, Collider? collider)
        {
            public readonly bool Found = found;
            public readonly float FloorY = floorY;
            public readonly Collider? Collider = collider;
        }

        private static void EnsureMemoFrame()
        {
            if (_memoFrame == Time.frameCount) return;

            _memoFrame = Time.frameCount;
            ColliderKinds.Clear();
            FloorAnswers.Clear();
        }

        private static void EnsureGates()
        {
            if (Time.time < _gatesRefreshAt || !SceneScan.MayRescan(_gatesForced)) return;
            // Stamped before the measuring loop: anything below that probed would
            // otherwise re-enter this method forever.
            _gatesRefreshAt = Time.time + 5f;
            _gatesForced = false;
            _frameGatesAt = -1;
            CachedGates.Clear();
            GateLocalBounds.Clear();
            CachedGates.AddRange(Object.FindObjectsOfType<Gate>());
            // Openings outlive the TTL, so gates destroyed between invalidations would
            // accumulate. The loop below re-measures whatever is still live.
            if (GateOpenings.Count > CachedGates.Count * 2) GateOpenings.Clear();

            bool allMeasured = true;
            foreach (Gate gate in CachedGates)
            {
                MeasureOpening(gate);
                if (gate != null && !GateOpenings.ContainsKey(gate.GetInstanceID())) allMeasured = false;
            }
            // Held back until nothing is still mid-animation, so the dump is the truth.
            if (!_inventoryPending || !allMeasured) return;

            _inventoryPending = false;
            LogGateInventory();
        }

        /// <summary>
        /// One gate's carve-out: the opening it makes, never the size of its leaf.
        /// docs/invariants.md#gate-carveout-is-the-opening
        /// </summary>
        private readonly struct GateOpening
        {
            /// <summary>
            /// Half-width across the passage, in metres.
            /// </summary>
            internal readonly float HalfWidth;
            /// <summary>
            /// Tall enough for a body to walk through. A cupboard gate carves nothing.
            /// </summary>
            internal readonly bool IsPassage;
            /// <summary>
            /// An anchor is a direct child of the gate, so the gate's own transform is
            /// the passage frame and the carve-out can be a slot rather than a cylinder.
            /// </summary>
            internal readonly bool LocalAxes;
            /// <summary>
            /// Sliding leaves the gate has. Zero means it is not a door; this is the
            /// passage test, so the audit prints it.
            /// </summary>
            internal readonly int Anchors;
            /// <summary>
            /// Vertical extent of the leaves. Audit only - it is not the passage test.
            /// </summary>
            internal readonly float LeafHeight;
            /// <summary>
            /// Half-width the leaves close over, without margin or floor: where the jamb is.
            /// </summary>
            internal readonly float ClearHalfWidth;

            internal GateOpening(float halfWidth, bool isPassage, bool localAxes,
                int anchors, float leafHeight, float clearHalfWidth)
            {
                HalfWidth = halfWidth;
                ClearHalfWidth = clearHalfWidth;
                IsPassage = isPassage;
                LocalAxes = localAxes;
                Anchors = anchors;
                LeafHeight = leafHeight;
            }
        }

        /// <summary>
        /// One gate as a probe hit needs it: its placement, its open state and its
        /// carve-out, all read once a frame instead of once per hit. Every field is a
        /// native property read - that scan was the mod's largest self cost.
        /// </summary>
        private readonly struct GateProbe
        {
            internal readonly Gate Gate;
            internal readonly Vector3 Position;
            internal readonly Vector3 Forward;
            internal readonly Vector3 Right;
            internal readonly bool FullyOpened;
            internal readonly GateOpening Opening;

            internal GateProbe(Gate gate, Transform self, GateOpening opening)
            {
                Gate = gate;
                Position = self.position;
                Forward = self.forward;
                Right = self.right;
                FullyOpened = gate.FullyOpened;
                Opening = opening;
            }
        }

        /// <summary>
        /// The fallback carve-out for a gate that has not been measured: the fixed
        /// opening, as a cylinder, and treated as a passage.
        /// </summary>
        private static GateOpening UnmeasuredOpening =>
            new(GateOpeningRadius, true, false, 0, 0f, GateOpeningRadius);

        private static GateOpening OpeningOf(Gate gate)
        {
            return gate != null && GateOpenings.TryGetValue(gate.GetInstanceID(), out GateOpening o)
                ? o
                : UnmeasuredOpening;
        }

        /// <summary>
        /// Measures a gate's opening as the span its leaves cover when shut - that span
        /// is the gap. `gateAnchors` slide along the gate's local x (indices 0 and 1) or
        /// local y (2 and 3), so local x is across the passage and local z runs through
        /// the wall. Measured once, and only from a settled gate: mid-animation the
        /// leaves sit between the two positions. docs/invariants.md#gate-carveout-is-the-opening
        /// </summary>
        private static void MeasureOpening(Gate gate)
        {
            if (gate == null) return;

            int key = gate.GetInstanceID();
            if (GateOpenings.ContainsKey(key)) return;

            // Covers closing too: CloseRoutine clears FullyOpened and keeps Opened set
            // until the leaves are home.
            // ReSharper disable once MergeIntoPattern
            if (gate.Opened && !gate.FullyOpened) return;

            Transform self = gate.transform;
            MeasureLeaves(gate, self, out float acrossReach, out float leafHeight, out bool anyLeaf);

            Transform[]? anchors = GameInternals.GateAccess.GetGateAnchors(gate);
            int anchorCount = CountAnchors(anchors);
            bool localAxes = AnchorsAreOwnChildren(anchors, self);

            float halfWidth;
            float clearHalfWidth = GateOpeningRadius;
            if (localAxes && anyLeaf)
            {
                // Standing open, a side leaf has slid one stroke clear of the opening it
                // covers; a leaf that slides vertically never leaves it.
                float stroke = gate.FullyOpened && HasSideLeaf(anchors)
                    ? GameInternals.GateAccess.GetOpenedWidth(gate) *
                      self.TransformVector(Vector3.right).magnitude
                    : 0f;
                clearHalfWidth = acrossReach - stroke;
                halfWidth = clearHalfWidth + GateOpeningMargin;
            }
            else
            {
                halfWidth = GateOpeningRadius;
            }

            // GateOpeningRadius is a genuine floor, not just a starting value: a gate
            // whose leaves read narrow (an odd anchor, a renamed field) must never carve
            // Less than a standard doorway, or its own passage stops validating.
            halfWidth = Mathf.Clamp(halfWidth, GateOpeningRadius, MaxGateFootprintRadius);

            // No slidable leaf, no doorway: a leaf that is the gate's own transform is a
            // cabinet panel. Never a size threshold: docs/invariants.md#gate-carveout-is-the-opening
            bool isPassage = !GameInternals.GateAccess.AnchorsReadable ||
                             (anchorCount > 0 && anyLeaf);
            GateOpenings[key] = new GateOpening(halfWidth, isPassage, localAxes, anchorCount,
                leafHeight, clearHalfWidth);
        }

        /// <summary>
        /// Strict children only, and active only: a Gate is sometimes mounted on a large
        /// structural panel, and measuring that would erase a wall. Reach is measured
        /// along the gate's own across-passage axis, height in world space.
        /// </summary>
        private static void MeasureLeaves(Gate gate, Transform self, out float acrossReach,
            out float height, out bool anyLeaf)
        {
            acrossReach = 0f;
            anyLeaf = false;
            float low = 0f;
            float high = 0f;
            Vector3 origin = self.position;
            Vector3 across = self.right;
            foreach (Collider c in gate.GetComponentsInChildren<Collider>(false))
            {
                if (c.transform == self) continue;

                Accumulate(c.bounds, origin, across, ref acrossReach, ref low, ref high, ref anyLeaf);
            }
            foreach (Renderer r in gate.GetComponentsInChildren<Renderer>(false))
            {
                if (r.transform == self) continue;
                // A leaf is a mesh. Every gate also carries a CloseParticle, whose
                // renderer bounds follow the burst rather than any geometry.
                if (r is not MeshRenderer and not SkinnedMeshRenderer) continue;

                Accumulate(r.bounds, origin, across, ref acrossReach, ref low, ref high, ref anyLeaf);
            }
            height = anyLeaf ? high - low : 0f;
        }

        private static void Accumulate(Bounds bounds, Vector3 origin, Vector3 across,
            ref float acrossReach, ref float low, ref float high, ref bool any)
        {
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 p = new(
                    (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                acrossReach = Mathf.Max(acrossReach, Mathf.Abs(Vector3.Dot(p - origin, across)));
                if (!any) { low = p.y; high = p.y; any = true; }
                else { low = Mathf.Min(low, p.y); high = Mathf.Max(high, p.y); }
            }
        }

        /// <summary>
        /// How many leaves the gate has to slide - which is what makes it a door at all
        /// rather than a panel that happens to use the same component.
        /// </summary>
        private static int CountAnchors(Transform[]? anchors)
        {
            if (anchors == null) return 0;

            int count = 0;
            foreach (Transform anchor in anchors)
            {
                if (anchor != null) count++;
            }
            return count;
        }

        /// <summary>
        /// True when at least one anchor is a direct child of the gate, which is what
        /// makes the gate's own transform the passage frame.
        /// </summary>
        private static bool AnchorsAreOwnChildren(Transform[]? anchors, Transform self)
        {
            if (anchors == null) return false;

            foreach (Transform anchor in anchors)
            {
                if (anchor != null && anchor.parent == self) return true;
            }
            return false;
        }

        /// <summary>
        /// True when anchor 0 or 1 - the ones that slide across the passage - actually
        /// carries a leaf. Both are present as empty placeholders on every vertically
        /// sliding gate in the game, whose stroke is a height, not a width.
        /// </summary>
        private static bool HasSideLeaf(Transform[]? anchors)
        {
            if (anchors == null) return false;

            for (int i = 0; i < anchors.Length && i < 2; i++)
            {
                if (anchors[i] == null) continue;

                // The same geometry MeasureLeaves counts, so the two cannot disagree
                // about what a leaf is.
                if (anchors[i].GetComponentInChildren<Collider>(false) != null ||
                    anchors[i].GetComponentInChildren<MeshRenderer>(false) != null ||
                    anchors[i].GetComponentInChildren<SkinnedMeshRenderer>(false) != null)
                {
                    return true;
                }
            }
            return false;
        }


        /// <summary>
        /// True when the straight stretch a->b passes through this gate's own volume.
        /// A volume test, not a radius: a corridor running past a shut airlock door
        /// must stay routable. docs/invariants.md#locked-doors-block-edges
        /// </summary>
        internal static bool SegmentCrossesGate(Gate gate, Vector3 a, Vector3 b, float padding)
        {
            if (gate == null) return false;

            Transform t = gate.transform;
            Bounds local = LocalBoundsOf(gate, t);
            Vector3 extents = local.extents + Vector3.one * padding;
            return SegmentHitsBox(t.InverseTransformPoint(a) - local.center,
                t.InverseTransformPoint(b) - local.center, extents);
        }

        /// <summary>
        /// The gate's strict children, measured once into its own local space so the
        /// box stays valid while the world moves around the ship.
        /// </summary>
        private static Bounds LocalBoundsOf(Gate gate, Transform t)
        {
            int key = gate.GetInstanceID();
            if (GateLocalBounds.TryGetValue(key, out Bounds cached)) return cached;

            bool any = false;
            Bounds local = new(Vector3.zero, Vector3.zero);
            foreach (Collider c in gate.GetComponentsInChildren<Collider>(false))
            {
                if (c.transform == t) continue;

                Bounds w = c.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 p = new(
                        (corner & 1) == 0 ? w.min.x : w.max.x,
                        (corner & 2) == 0 ? w.min.y : w.max.y,
                        (corner & 4) == 0 ? w.min.z : w.max.z);
                    Vector3 lp = t.InverseTransformPoint(p);
                    if (any)
                    {
                        local.Encapsulate(lp);
                    }
                    else { local = new Bounds(lp, Vector3.zero); any = true; }
                }
            }
            // No child colliders: fall back to the fixed opening, so an unmeasurable
            // gate still blocks its own doorway rather than nothing at all.
            if (!any) local = new Bounds(Vector3.zero, new Vector3(GateOpeningRadius * 2f, 2f, GateOpeningRadius * 2f));

            GateLocalBounds[key] = local;
            return local;
        }

        /// <summary>
        /// Slab test: does the segment a->b touch the box centred on the origin?
        /// </summary>
        private static bool SegmentHitsBox(Vector3 a, Vector3 b, Vector3 extents)
        {
            Vector3 d = b - a;
            float tMin = 0f;
            float tMax = 1f;
            for (int axis = 0; axis < 3; axis++)
            {
                float o = a[axis];
                float dir = d[axis];
                float e = extents[axis];
                if (Mathf.Abs(dir) < 1e-6f)
                {
                    if (o < -e || o > e) return false;

                    continue;
                }
                float t1 = (-e - o) / dir;
                float t2 = (e - o) / dir;
                if (t1 > t2) (t1, t2) = (t2, t1);

                tMin = Mathf.Max(tMin, t1);
                tMax = Mathf.Min(tMax, t2);
                if (tMin > tMax) return false;
            }
            return true;
        }

        /// <summary>
        /// The cached gate list. Exposed so nothing else runs a second scene scan:
        /// docs/invariants.md#one-probe-basis
        /// </summary>
        internal static IReadOnlyList<Gate> Gates
        {
            get
            {
                EnsureGates();
                return CachedGates;
            }
        }

        /// <summary>
        /// Forces the next EnsureGates() call to rescan the scene. The 5 s TTL alone
        /// can serve a stale gate list across a scene load or dock/undock, so
        /// SceneLoader.LoadGame and the edge-cache dock-signature change call this.
        /// </summary>
        internal static void InvalidateGates()
        {
            _gatesRefreshAt = 0f;
            _gatesForced = true;
            GateOpenings.Clear();
            _inventoryPending = true;
        }

        // Gate-frame audit state: deduped per collider x gate pair, not globally. A
        // single shared throttle made the audit a lottery that floor probes always won,
        // and the one collider that mattered never appeared in a capture at all.
        // Keyed by instance, so a throttled hit reads no names (unity-name-reads-allocate).
        private static readonly Dictionary<(int Collider, int Gate, bool Graze), float> AuditLoggedAt = [];
        private const float AuditPairCooldown = 10f;
        private const int AuditMaxPairs = 64;

        /// <summary>
        /// Debug-level-2 audit naming every collider the gate-frame rule ignores and the
        /// bound that admitted it. Covers all branches on purpose. docs/probes.md
        /// </summary>
        private static void AuditGateFrameIgnore(Collider collider, Gate gate, float across, float allowed)
        {
            if (NpcLog.Level < 2 || !AuditDue(collider, gate, false)) return;

            NpcLog.Log.LogInfo(
                "[probe] Gate-frame rule ignoring a hit on '" + collider.gameObject.name + "' " +
                across.ToString("0.00") + "m across gate '" + gate.gameObject.name +
                "' (opening " + allowed.ToString("0.00") + "m) - if that point is wall " +
                "rather than doorway, this gate's opening is too wide.");
        }

        /// <summary>
        /// The other half of the audit: a hit that sits in the opening but whose line
        /// passes through the wall beside it. This is the doorway the NPC will now
        /// walk around rather than through, so it is as worth reading as an ignore.
        /// docs/invariants.md#a-doorway-is-crossed-not-grazed
        /// </summary>
        private static void AuditGateFrameGraze(Collider collider, Gate gate, float across, float allowed)
        {
            if (NpcLog.Level < 2 || !AuditDue(collider, gate, true)) return;

            NpcLog.Log.LogInfo(
                "[probe] Gate-frame rule keeping a hit on '" + collider.gameObject.name + "' " +
                across.ToString("0.00") + "m across gate '" + gate.gameObject.name +
                "' (opening " + allowed.ToString("0.00") + "m): the line only grazes " +
                "the jamb and crosses the wall elsewhere.");
        }

        /// <summary>
        /// True, and stamped, when this collider x gate pair has not been reported within AuditPairCooldown.
        /// </summary>
        private static bool AuditDue(Collider collider, Gate gate, bool graze)
        {
            (int, int, bool) key = (collider.GetInstanceID(), gate.GetInstanceID(), graze);
            if (AuditLoggedAt.TryGetValue(key, out float last) && Time.time - last < AuditPairCooldown) return false;
            // Bounded, and self-healing: the next pass re-reports whatever is still live.
            if (AuditLoggedAt.Count >= AuditMaxPairs) AuditLoggedAt.Clear();

            AuditLoggedAt[key] = Time.time;
            return true;
        }

        /// <summary>
        /// One line per gate: what the carve-out rule decided, and from what. Dumped once
        /// after each rescan of the gate set, and on demand from 'npc_gates'.
        /// </summary>
        internal static List<string> DescribeGates()
        {
            EnsureGates();
            List<string> lines = new(CachedGates.Count + 1);
            BuildGateInventory(lines);
            return lines;
        }

        private static void LogGateInventory()
        {
            if (NpcLog.Level < 2) return;

            List<string> lines = new(CachedGates.Count + 1);
            BuildGateInventory(lines);
            foreach (string line in lines) NpcLog.Log.LogInfo(line);
        }

        private static void BuildGateInventory(List<string> lines)
        {
            lines.Add("[probe] gates: " + CachedGates.Count +
                      " (opening = half-width across the passage; leaf height is diagnostic, not the passage test)");
            foreach (Gate gate in CachedGates)
            {
                if (gate == null) continue;

                GateOpening o = OpeningOf(gate);
                Vector3 p = gate.transform.position;
                lines.Add("[probe] gate '" + ScenePath(gate.transform) + "' pos(" +
                          p.x.ToString("0.00") + "," + p.y.ToString("0.00") + "," + p.z.ToString("0.00") +
                          ") " + o.Anchors + " leaves " + o.LeafHeight.ToString("0.00") + "m tall -> " +
                          (o.IsPassage
                              ? "opening " + o.HalfWidth.ToString("0.00") + "m " +
                                (o.LocalAxes ? "slot depth " + GateOpeningHalfDepth.ToString("0.00") + "m" : "cylinder, axes unconfirmed")
                              : "no leaves, not a door - carves nothing"));
            }
        }

        /// <summary>
        /// Depth-limited hierarchy path. Several gates share the name 'Door02', so the
        /// name alone cannot identify one in a log line.
        /// </summary>
        private static string ScenePath(Transform t)
        {
            string path = t.name;
            Transform parent = t.parent;
            for (int depth = 0; parent != null && depth < 3; depth++)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        /// <summary>
        /// Places every gate that can carve a wall for this frame. The whole filter that
        /// does not depend on the hit - alive, active, a passage at all - is applied here,
        /// once, instead of per probe hit.
        /// </summary>
        private static void EnsureFrameGates()
        {
            EnsureGates();
            if (_frameGatesAt == Time.frameCount) return;

            _frameGatesAt = Time.frameCount;
            FrameGates.Clear();
            foreach (Gate gate in CachedGates)
            {
                if (gate == null || !gate.gameObject.activeInHierarchy) continue;

                // A gate that opens nothing a body can walk through carves no wall.
                // docs/invariants.md#gate-carveout-is-the-opening
                GateOpening opening = OpeningOf(gate);
                if (!opening.IsPassage) continue;

                FrameGates.Add(new GateProbe(gate, gate.transform, opening));
            }
        }

        /// <summary>
        /// The whole line a line-of-sight probe is testing, so the carve-out can ask
        /// where that line crosses a gate as well as where it touched a collider.
        /// docs/invariants.md#a-doorway-is-crossed-not-grazed
        /// </summary>
        private readonly struct ProbeChord
        {
            internal readonly Vector3 From;
            internal readonly Vector3 To;

            internal ProbeChord(Vector3 from, Vector3 to)
            {
                From = from;
                To = to;
            }
        }

        /// <summary>
        /// True when the line crosses this gate's plane inside its opening, or never
        /// crosses it at all - a probe that stops short of the wall has only its hit
        /// point to be judged by. docs/invariants.md#a-doorway-is-crossed-not-grazed
        /// </summary>
        private static bool ChordCrossesOpening(ProbeChord chord, GateProbe g)
        {
            // A cylinder carve-out (an unmeasured gate) has no passage plane to cross.
            if (!g.Opening.LocalAxes) return true;

            Vector3 fromDelta = chord.From - g.Position;
            Vector3 toDelta = chord.To - g.Position;
            float fromDepth = Vector3.Dot(fromDelta, g.Forward);
            float toDepth = Vector3.Dot(toDelta, g.Forward);
            // Both ends the same side of the wall: this line does not pass through here.
            if (fromDepth * toDepth > 0f) return true;

            float span = fromDepth - toDepth;
            if (Mathf.Abs(span) < 0.0001f) return true;

            float t = fromDepth / span;
            float across = Mathf.Abs(Mathf.Lerp(Vector3.Dot(fromDelta, g.Right),
                Vector3.Dot(toDelta, g.Right), t));
            // The clear opening, not the carve-out: that reaches past the jamb into the wall.
            return across < Mathf.Max(g.Opening.ClearHalfWidth - CrossingBodyRadius, MinCrossingHalfWidth);
        }

        /// <summary>
        /// True when the point a probe touched is doorway rather than wall. `hitPoint`
        /// Must be the cast's contact point (docs/invariants.md#gate-frame-hit-point);
        /// `requireOpenGate` is what the whiskers want, edge probes pass false. `chord`
        /// is the whole line under test, which a wall grazed at its jamb fails.
        /// </summary>
        private static bool HitIsGateOpening(Collider collider, Vector3 hitPoint, bool requireOpenGate,
            ProbeChord? chord = null)
        {
            EnsureFrameGates();

            // ReSharper disable once ForCanBeConvertedToForeach
            for (int i = 0; i < FrameGates.Count; i++)
            {
                GateProbe g = FrameGates[i];
                if (requireOpenGate && !g.FullyOpened) continue;

                if (Mathf.Abs(hitPoint.y - g.Position.y) >= GateOpeningHalfHeight) continue;

                float across;
                if (g.Opening.LocalAxes)
                {
                    // A slot in the gate's own frame: the wall runs along local x, and
                    // local z runs through it. Dot products, not InverseTransformPoint,
                    // so a scaled gate still measures in metres.
                    Vector3 delta = hitPoint - g.Position;
                    if (Mathf.Abs(Vector3.Dot(delta, g.Forward)) >= GateOpeningHalfDepth) continue;

                    across = Mathf.Abs(Vector3.Dot(delta, g.Right));
                }
                else
                {
                    across = new Vector2(hitPoint.x - g.Position.x, hitPoint.z - g.Position.z).magnitude;
                }

                if (across >= g.Opening.HalfWidth) continue;

                // The hit point is in the opening, but the line it came from may only
                // have clipped the jamb on its way into the wall beside it: a thick
                // block's jamb face lies inside the carve-out along its whole depth.
                // docs/invariants.md#a-doorway-is-crossed-not-grazed
                if (chord is { } line && !ChordCrossesOpening(line, g))
                {
                    AuditGateFrameGraze(collider, g.Gate, across, g.Opening.HalfWidth);
                    continue;
                }

                AuditGateFrameIgnore(collider, g.Gate, across, g.Opening.HalfWidth);
                return true;
            }
            return false;
        }

        /// <summary>
        /// True when the hit lands in a fully open doorway, so the whiskers push through
        /// instead of orbiting it. `hitPoint` is the cast's contact point:
        /// docs/invariants.md#gate-frame-hit-point
        /// </summary>
        internal static bool IsDoorGeometryNearOpenGate(Collider collider, Vector3 hitPoint)
        {
            return HitIsGateOpening(collider, hitPoint, requireOpenGate: true);
        }

        /// <summary>
        /// True when a probe hit must not block an edge probe: bodies, build
        /// restriction volumes, passable interfaces, door leaves, and hits inside a
        /// doorway. `hitPoint` is the cast's contact point.
        /// </summary>
        private static bool IsEdgeProbeIgnorable(Collider collider, Vector3 hitPoint,
            ProbeChord? chord = null)
        {
            if (collider == null) return true;

            if (IsBodyCollider(collider) || IsPassableInterface(collider)) return true;

            // Frame proxies around a gate. Judged per hit point, so a wall that merely
            // Parents a gate keeps blocking everywhere except at its doorway.
            return HitIsGateOpening(collider, hitPoint, requireOpenGate: false, chord);
        }

        /// <summary>
        /// Docking collars, airlock assemblies and door leaves: things the NPC passes
        /// through. Unlike the gate-frame rule, this holds for the floor probe too.
        /// </summary>
        private static bool IsPassableInterface(Collider collider)
        {
            EnsureMemoFrame();
            int id = collider.GetInstanceID();
            ColliderKinds.TryGetValue(id, out byte kind);
            if ((kind & PassableAnswered) != 0) return (kind & PassableYes) != 0;

            bool passable = HasPassableAncestor(collider);
            ColliderKinds[id] = (byte)(kind | PassableAnswered | (passable ? PassableYes : 0));
            return passable;
        }

        private static bool HasPassableAncestor(Collider collider)
        {
            // Docking collars ring the hatch opening and sit right next to the
            // waypoints there; without this every route probe around a hatch fails.
            if (collider.GetComponentInParent<Docker>() != null) return true;

            // Airlock assemblies are the same passable interface when docked. Space
            // protection keeps the NPC from ever using them toward open space.
            if (collider.GetComponentInParent<Airlock>() != null) return true;

            // Door leaves are traversable: HandleDoors opens them on the way through.
            return collider.GetComponentInParent<Gate>() != null;
        }

        /// <summary>
        /// An NPC's or the player's own body. Split out of IsEdgeProbeIgnorable so
        /// the floor probe can treat the rest of that filter as a fallback, not a veto.
        /// </summary>
        private static bool IsBodyCollider(Collider collider)
        {
            if (collider == null) return true;

            EnsureMemoFrame();
            int id = collider.GetInstanceID();
            ColliderKinds.TryGetValue(id, out byte kind);
            if ((kind & BodyAnswered) != 0) return (kind & BodyYes) != 0;

            bool body = BelongsToABody(collider.transform);
            ColliderKinds[id] = (byte)(kind | BodyAnswered | (body ? BodyYes : 0));
            return body;
        }

        private static bool BelongsToABody(Transform t)
        {
            // Every NPC of every mod, alive or dead: docs/invariants.md#npcs-never-block-each-other
            if (NpcRegistry.IsNpcBody(t)) return true;

            Player? pilot = NpcPlayer.Pilot;
            return pilot != null && pilot.Controller != null && t.IsChildOf(pilot.Controller.CachedTransform);
        }

        /// <summary>
        /// Contact point for a cast hit. Physics reports point = zero with distance 0
        /// when the cast starts inside the collider; the origin is the honest answer
        /// there, and the world origin would read as "nowhere near a gate".
        /// </summary>
        private static Vector3 ContactPoint(RaycastHit hit, Vector3 origin)
        {
            return hit.distance <= 0f ? origin : hit.point;
        }

        /// <summary>
        /// A hit the CharacterController walks over rather than into, so steering must
        /// not avoid it: a surface within its slopeLimit, or a riser no taller than its
        /// stepOffset with clear space above.
        /// docs/invariants.md#walkable-ground-is-not-an-obstacle
        /// </summary>
        public static bool HitIsWalkableGround(RaycastHit hit, Vector3 dir, float feetY,
            float stepOffset, float slopeLimit)
        {
            // A cast that started overlapped reports no usable point or normal.
            if (hit.distance <= 0f) return false;

            if (Vector3.Angle(hit.normal, Vector3.up) <= slopeLimit) return true;

            float rise = hit.point.y - feetY;
            if (rise < 0f || rise > stepOffset) return false;
            // A riser, not the foot of a wall: the tread it steps onto must be clear.
            Vector3 above = new(hit.point.x, feetY + stepOffset + 0.05f, hit.point.z);
            return !Physics.Raycast(above - dir * 0.05f, dir, 0.3f, ProbeLayers,
                QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Height of the walkability line. A body is stopped by things an eye looks over:
        /// the OxygenStation railing is 0.86 m and EyeHeight is 1.0 m.
        /// </summary>
        private const float KneeHeight = 0.4f;
        /// <summary>
        /// Unity's CharacterController default, which the NPC inherits from the player
        /// prefab - the mod only ever narrows the radius. Anything flatter is floor.
        /// </summary>
        private const float MaxWalkableSlope = 45f;

        /// <summary>
        /// How finely the ground is sampled along a chord. A staircase tread is ~0.3 m
        /// deep, so anything coarser can straddle a whole flight and miss the well
        /// beside it - ThinLos samples at 1 m and cannot see a 1.5 m stairwell at all.
        /// </summary>
        private const float GroundSampleStep = 0.35f;

        /// <summary>
        /// Line of sight at knee height, sampled at GroundSampleStep and following the
        /// floor: what a body can walk through, as opposed to what an eye can see over.
        /// Surfaces flat enough to walk on are not obstacles, so treads and ramps along
        /// the way do not block it.
        /// docs/invariants.md#an-entry-must-be-walkable-not-merely-visible
        /// </summary>
        public static bool WalkLos(Vector3 a, Vector3 b, float maxDeltaY)
        {
            Vector3 kneeA = KneeAt(a);
            Vector3 kneeB = KneeAt(b);
            if (Mathf.Abs(kneeB.y - kneeA.y) > maxDeltaY) return false;

            Vector3 delta = b - a;
            float dist = delta.magnitude;
            if (dist < GroundSampleStep) return true;

            Vector3 dir = delta / dist;

            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / GroundSampleStep));
            // The whole line, not this sample: the sub-cast that grazes a jamb usually
            // stops short of the door's own plane.
            ProbeChord chord = new(kneeA, kneeB);
            Vector3 previous = kneeA;
            for (int s = 1; s <= steps; s++)
            {
                Vector3 knee = s == steps ? kneeB : KneeAt(a + dir * (GroundSampleStep * s));
                Vector3 seg = knee - previous;
                float segLen = seg.magnitude;
                if (segLen > 0.01f)
                {
                    int count = Physics.RaycastNonAlloc(previous, seg / segLen, LosHits, segLen,
                        ProbeLayers, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < count; i++)
                    {
                        if (IsEdgeProbeIgnorable(LosHits[i].collider, ContactPoint(LosHits[i], previous), chord)) continue;
                        // A tread or a ramp underfoot is not a wall.
                        if (LosHits[i].distance > 0f &&
                            Vector3.Angle(LosHits[i].normal, Vector3.up) <= MaxWalkableSlope)
                        {
                            continue;
                        }
                        return false;
                    }
                }
                previous = knee;
            }
            return true;
        }

        private static Vector3 KneeAt(Vector3 p)
        {
            TryFloorHeight(p, out float floorY);
            return new Vector3(p.x, floorY + KneeHeight, p.z);
        }

        /// <summary>
        /// True when the floor under the straight line from a to b never breaks by more
        /// than maxStep. "Can the NPC walk there", as opposed to ThinLos's "can it see
        /// there" - a chest-high sight line clears a waist-high railing, and the floor it
        /// finds beyond one is the flight below.
        /// docs/invariants.md#an-entry-must-be-walkable-not-merely-visible
        /// </summary>
        public static bool GroundIsContinuous(Vector3 a, Vector3 b, float maxStep)
        {
            Vector3 delta = b - a;
            delta.y = 0f;
            float dist = delta.magnitude;
            if (dist < GroundSampleStep) return true;

            Vector3 dir = delta / dist;

            int steps = Mathf.CeilToInt(dist / GroundSampleStep);
            // A sample with no floor at all is not a break: some decks carry no collider
            // a raycast can see, and calling that a wall would delete the docking
            // corridor. docs/invariants.md#a-missing-floor-is-a-last-resort-not-an-answer
            bool hasPrevious = TryFloorHeight(a, out float previous);
            for (int s = 1; s <= steps; s++)
            {
                Vector3 at = a + dir * Mathf.Min(dist, GroundSampleStep * s);
                if (!TryFloorHeight(at, out float here)) continue;

                if (hasPrevious && Mathf.Abs(here - previous) > maxStep) return false;

                previous = here;
                hasPrevious = true;
            }
            return true;
        }

        /// <summary>
        /// Highest non-ignorable surface at or below a position; false when there is
        /// nothing underneath, and floorY then falls back to the point's own height.
        /// The reference for every vertical comparison: docs/invariants.md#floor-to-floor
        /// </summary>
        public static bool TryFloorHeight(Vector3 p, out float floorY)
        {
            return TryFloor(p, out floorY, out _);
        }

        /// <summary>
        /// The collider the floor under a position belongs to, through the same passes
        /// as TryFloorHeight - including the wide fallback, so "which vessel is this"
        /// cannot answer differently from "how high is this".
        /// docs/invariants.md#one-probe-basis
        /// </summary>
        public static bool TryFloorCollider(Vector3 p, [NotNullWhen(true)] out Collider? floorCollider)
        {
            return TryFloor(p, out _, out floorCollider);
        }

        private static bool TryFloor(Vector3 p, out float floorY, [NotNullWhen(true)] out Collider? floorCollider)
        {
            // Not in the fixed step: a physics step between it and Update would outdate the answer.
            if (Time.inFixedTimeStep) return ProbeFloor(p, out floorY, out floorCollider);

            EnsureMemoFrame();
            if (FloorAnswers.TryGetValue(p, out FloorAnswer memo))
            {
                floorY = memo.FloorY;
                floorCollider = memo.Collider!; // stored with Found, and non-null whenever Found is
                return memo.Found;
            }
            bool found = ProbeFloor(p, out floorY, out floorCollider);
            FloorAnswers[p] = new FloorAnswer(found, floorY, floorCollider);
            return found;
        }

        private static bool ProbeFloor(Vector3 p, out float floorY, [NotNullWhen(true)] out Collider? floorCollider)
        {
            floorY = p.y;
            // Every `return true` assigns it first; flow analysis cannot tie that to hasFloor.
            floorCollider = null!;
            Collider? anySolid = null;
            bool hasFloor = false;
            float anySolidY = 0f;
            bool hasAnySolid = false;
            Vector3 floorOrigin = p + Vector3.up * 2f;
            int floorCount = Physics.RaycastNonAlloc(floorOrigin, Vector3.down, FloorHits, 6f,
                ProbeLayers, QueryTriggerInteraction.Ignore);
            WarnIfTruncated(floorCount, "floor");
            for (int i = 0; i < floorCount; i++)
            {
                if (FloorHits[i].point.y > p.y + 0.05f) continue;

                if (IsBodyCollider(FloorHits[i].collider))
                {
                    continue; // nobody stands on a person
                }

                if (!hasAnySolid || FloorHits[i].point.y > anySolidY)
                {
                    anySolidY = FloorHits[i].point.y;
                    anySolid = FloorHits[i].collider;
                    hasAnySolid = true;
                }
                // Not the gate-frame rule: it would drop a deck and keep one below it.
                // docs/invariants.md#floors-ignore-the-gate-frame-rule
                if (IsPassableInterface(FloorHits[i].collider)) continue;

                if (!hasFloor || FloorHits[i].point.y > floorY)
                {
                    floorY = FloorHits[i].point.y;
                    floorCollider = FloorHits[i].collider;
                    hasFloor = true;
                }
            }
            if (hasFloor) return true;

            // Nothing survived the filter but something solid is down there: an airlock
            // or collar floor. docs/invariants.md#doorway-floor-survives-the-filter
            if (hasAnySolid)
            {
                floorY = anySolidY;
                // Set together with hasAnySolid.
                floorCollider = anySolid!;
                return true;
            }

            // Still nothing. Some decks carry no collider a default raycast can see, and
            // "no floor" makes the two sides of a level test fall back to different bases.
            // docs/invariants.md#a-missing-floor-is-a-last-resort-not-an-answer
            return TryAnyLayerFloor(p, floorOrigin, out floorY, out floorCollider);
        }

        /// <summary>
        /// A full buffer means hits were dropped, and the dropped one may have been the
        /// floor. Throttled hard - this is a probe-hot path.
        /// </summary>
        private static void WarnIfTruncated(int count, string probe)
        {
            if (count < FloorHits.Length || Time.time - _truncationWarnedAt < 30f) return;

            _truncationWarnedAt = Time.time;
            NpcLog.Log.LogWarning(
                "[probe] " + probe + " probe filled its " + FloorHits.Length +
                "-hit buffer; results are truncated arbitrarily and a surface may be missed.");
        }

        /// <summary>
        /// Last-resort floor probe across every layer. Only reached when the filtered
        /// pass found nothing at all, so it can only replace a non-answer.
        /// </summary>
        private static bool TryAnyLayerFloor(Vector3 p, Vector3 origin, out float floorY, [NotNullWhen(true)] out Collider? floorCollider)
        {
            floorY = p.y;
            floorCollider = null;
            bool found = false;
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, FloorHits, 6f,
                ~0, QueryTriggerInteraction.Ignore);
            WarnIfTruncated(count, "wide floor");
            for (int i = 0; i < count; i++)
            {
                if (FloorHits[i].point.y > p.y + 0.05f) continue;

                if (IsBodyCollider(FloorHits[i].collider)) continue;

                if (!found || FloorHits[i].point.y > floorY)
                {
                    floorY = FloorHits[i].point.y;
                    floorCollider = FloorHits[i].collider;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>
        /// Floor height under a position, falling back to its own height.
        /// </summary>
        public static float FloorHeight(Vector3 p)
        {
            TryFloorHeight(p, out float floorY);
            return floorY;
        }

        /// <summary>
        /// EyeHeight above the floor under the given position.
        /// </summary>
        private static Vector3 EyeAt(Vector3 p)
        {
            TryFloorHeight(p, out float floorY);
            return new Vector3(p.x, floorY + EyeHeight, p.z);
        }

        /// <summary>
        /// Thin floor-hugging line of sight with a climb cap, cast segment by segment
        /// between eye points because a straight chord cuts through slopes and stairs.
        /// Walls and railings block it; low furniture does not. See docs/probes.md.
        /// </summary>
        public static bool ThinLos(Vector3 a, Vector3 b, float maxDeltaY)
        {
            // The climb cap is measured floor to floor, never between the endpoints:
            // docs/invariants.md#floor-to-floor. EyeAt already resolves each endpoint
            // to its own floor, so this costs one extra raycast.
            Vector3 eyeA = EyeAt(a);
            Vector3 eyeB = EyeAt(b);
            if (Mathf.Abs(eyeB.y - eyeA.y) > maxDeltaY) return false;

            Vector3 delta = b - a;
            float dist = delta.magnitude;
            if (dist < 0.05f) return true;

            Vector3 dir = delta / dist;

            float stepLen = Mathf.Clamp(dist / 8f, 1f, 4f);
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / stepLen));
            // The whole line, not this sample: the sub-cast that grazes a jamb usually
            // stops short of the door's own plane.
            ProbeChord chord = new(eyeA, eyeB);
            Vector3 prevEye = eyeA;
            for (int s = 1; s <= steps; s++)
            {
                Vector3 eye = s == steps ? eyeB : EyeAt(a + dir * (stepLen * s));
                Vector3 seg = eye - prevEye;
                float segLen = seg.magnitude;
                if (segLen > 0.01f)
                {
                    int count = Physics.RaycastNonAlloc(prevEye, seg / segLen, LosHits, segLen,
                        ProbeLayers, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < count; i++)
                    {
                        if (IsEdgeProbeIgnorable(LosHits[i].collider, ContactPoint(LosHits[i], prevEye), chord)) continue;

                        return false;
                    }
                }
                prevEye = eye;
            }
            return true;
        }

        /// <summary>
        /// Shut gates further than this from a sight line are not tested at all.
        /// </summary>
        private const float SightGateBroadPhase = 6f;

        /// <summary>
        /// Straight line of sight for a pair of eyes: no floor following, and any gate not
        /// open blocks it, gap or carve-out regardless. `target`'s own colliders never block.
        /// docs/invariants.md#sight-stops-at-a-shut-door
        /// </summary>
        public static bool CanSee(Vector3 from, Vector3 to, Transform? target, out Gate? shutGate)
        {
            shutGate = null;
            Vector3 delta = to - from;
            float dist = delta.magnitude;
            if (dist < 0.05f) return true;

            EnsureGates();
            // The List itself, not IReadOnlyList: foreach over the interface boxes an enumerator.
            foreach (Gate gate in CachedGates)
            {
                if (gate == null || gate.Opened || !gate.gameObject.activeInHierarchy) continue;

                if (DistanceToSegment(gate.transform.position, from, to) > SightGateBroadPhase) continue;

                if (!SegmentCrossesGate(gate, from, to, 0f)) continue;

                shutGate = gate;
                return false;
            }

            int count = Physics.RaycastNonAlloc(from, delta / dist, LosHits, dist,
                ProbeLayers, QueryTriggerInteraction.Ignore);
            WarnIfTruncated(count, "sight");
            for (int i = 0; i < count; i++)
            {
                Collider c = LosHits[i].collider;
                if (c == null || IsBodyCollider(c)) continue;

                if (target != null && c.transform.IsChildOf(target)) continue;
                // Frame trim in a fully open doorway is not a wall - the whiskers' carve-out.
                if (IsDoorGeometryNearOpenGate(c, ContactPoint(LosHits[i], from))) continue;

                return false;
            }
            return true;
        }

        /// <summary>
        /// Distance from a point to the straight segment a-b, in 3D.
        /// </summary>
        public static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            float t = lenSq < 0.0001f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / lenSq);
            return Vector3.Distance(p, a + ab * t);
        }

    }
}
