using System;
using NPC.Core.Navigation;
using NPC.Core.World;
using Space;
using Space.Data;
using UnityEngine;

namespace NPC.Core.Agents
{
    /// <summary>
    /// Locomotion safety: body probes, whisker steering, auto-jump, movement application and stuck/oscillation recovery.
    /// </summary>
    public sealed partial class NpcAgent
    {
        private float gravity = 9.81f;
        private bool wantJump = false;
        private float jumpCooldownUntil = 0f;
        private const float JumpVelocity = 4.5f;

        private const float UnreachableWaypointHold = 6f;

        /// <summary>
        /// A sideways detour that drops further than this is a fall, not a detour. Above
        /// the 0.63 m railed platform so stepping down onto that is still allowed, well
        /// below a 1.25 m deck. Detours only:
        /// docs/invariants.md#a-detour-may-not-step-off-a-ledge
        /// </summary>
        private const float DetourLedgeDrop = 0.7f;
        /// <summary>
        /// How close above the body's top a ceiling counts as wedging it. Every ship ceiling
        /// clears the body by 0.6 m: docs/invariants.md#a-low-ceiling-is-measured-from-the-body
        /// </summary>
        private const float CeilingHeadroom = 0.1f;
        private float avoidMemoryUntil = 0f;
        private float stuckCheckTimer = 0f;
        private float stuckSeconds = 0f;
        private float lastStuckLogTime = 0f;
        private bool blockLoggedThisStuck = false;

        // Progress-toward-goal tracking: catches oscillation (running left and right),
        // where the NPC moves constantly but never gets closer to its target.
        private float goalCheckAt = 0f;
        // When the last check actually ran: a slow frame lands a check late, and the
        // no-progress clock must count the time that really passed.
        private float lastGoalCheckAt = 0f;
        private float lastGoalDist = float.MaxValue;
        private float noProgressSeconds = 0f;
        private bool noProgressLogged = false;
        // The last window's closing and whether its target was the one before, for the stall line.
        private float lastGoalClosed;
        private bool lastGoalTargetChanged;
        private Vector3 lastGoalTarget;
        private int lastGoalPlanIndex = -1;
        private int noProgressCycles = 0;

        // ------------------------------------------------------------------
        // Navigation helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Shape-aware obstacle probing: capsule casts match the NPC's body, measured
        /// from the feet. Initial overlaps are ignored - the CharacterController
        /// depenetrates those itself. docs/probes.md
        /// </summary>
        public bool BodyBlocked(Vector3 dir, float distance)
        {
            BodyCastCapsule(out Vector3 bottom, out Vector3 top, out float radius);
            int count = Physics.CapsuleCastNonAlloc(bottom, top, radius, dir, CastBuffer, distance,
                ProbeLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = CastBuffer[i];
                if (hit.distance < 0.02f)
                {
                    continue; // started overlapped, not blocking
                }

                Collider hitCollider = hit.collider;
                if (IsIgnorableCollider(hitCollider)) continue;
                // A ramp or a low step is ground, not a wall - steering it away from a
                // staircase is what stopped the NPC climbing.
                // docs/invariants.md#walkable-ground-is-not-an-obstacle
                if (IsWalkableGround(hit, dir)) continue;
                // An opened (or currently opening) gate panel is the doorway - the NPC
                // must push forward through the opening, not path around the door leaf.
                // Otherwise, the whiskers see a "wall" that oscillates it left and right.
                Gate hitGate = hitCollider.GetComponentInParent<Gate>();
                if (hitGate != null && hitGate.Opened) continue;
                // Judged at the contact point, not collider-wide: the wall that parents
                // a door ('wall_long_door') must keep blocking everywhere except in
                // the doorway itself. docs/invariants.md#gate-frame-hit-point
                if (NavProbe.IsDoorGeometryNearOpenGate(hitCollider, hit.point)) continue;

                return true;
            }
            return false;
        }

        /// <summary>
        /// The whisker capsule's sphere centres and radius: 0.25 m off the floor up to the top of
        /// the body it steers. docs/invariants.md#whiskers-are-body-shaped
        /// </summary>
        private void BodyCastCapsule(out Vector3 bottom, out Vector3 top, out float radius)
        {
            radius = cc != null ? cc.radius * 0.85f : 0.3f;
            float topCentre = cc != null ? cc.height + cc.skinWidth - radius : 1.5f;
            bottom = GroundPos(0.25f + radius);
            top = GroundPos(Mathf.Max(topCentre, 0.25f + radius));
        }

        /// <summary>
        /// Layer mask for the NPC's own body probes: what its controller collides with. The graph and floor
        /// probes stay on NavProbe's walker mask. docs/invariants.md#probe-what-the-body-collides-with
        /// </summary>
        private int ProbeLayers
        {
            get
            {
                if (cc == null) return NavProbe.ProbeLayers;

                int layer = cc.gameObject.layer;
                if (layer != probeMaskLayer)
                {
                    probeMaskLayer = layer;
                    probeMask = NavProbe.BodyMaskFor(layer);
                }
                return probeMask;
            }
        }
        private int probeMaskLayer = int.MinValue;
        private int probeMask;

        private bool IsIgnorableCollider(Collider collider)
        {
            if (collider == null) return true;
            // Capsule casts report colliders the cast starts inside (distance 0), and the
            // whisker probes originate inside the NPC's own ItemBlocker capsule - ignore
            // everything parented under the NPC itself.
            if (collider.transform.IsChildOf(transform)) return true;

            CharacterController? playerCc = NpcPlayer.Controller;
            if (playerCc != null && collider.transform.IsChildOf(playerCc.transform)) return true;

            // Another NPC is walked through, never hopped or steered around:
            // docs/invariants.md#npcs-never-block-each-other
            return NpcRegistry.IsNpcBody(collider.transform);
        }

        /// <summary>
        /// True when there is a real drop that way. Only ever asked about a sideways
        /// detour: the NPC's own route legitimately goes down steps and off the 0.63 m
        /// railed platform, and refusing its direct heading would strand it there.
        /// A floor that cannot be probed is not a ledge - that is a non-answer, not a
        /// hole. docs/invariants.md#a-detour-may-not-step-off-a-ledge
        /// </summary>
        public bool StepsOffALedge(Vector3 dir, float distance)
        {
            Vector3 flat = new(dir.x, 0f, dir.z);
            if (flat.sqrMagnitude < 0.001f) return false;

            Vector3 ahead = transform.position + flat.normalized * distance;
            if (!NavProbe.TryFloorHeight(ahead, out float there)) return false;

            return FloorUnderNpc().y - there > DetourLedgeDrop;
        }

        /// <summary>
        /// Ground the CharacterController traverses by itself, measured from the NPC's
        /// own feet and its own controller limits.
        /// docs/invariants.md#walkable-ground-is-not-an-obstacle
        /// </summary>
        private bool IsWalkableGround(RaycastHit hit, Vector3 dir)
        {
            if (cc == null) return false;

            return NavProbe.HitIsWalkableGround(hit, dir, GroundPos(0f).y, cc.stepOffset, cc.slopeLimit);
        }

        /// <summary>
        /// Capsule-cast whisker steering: progressively wider detours, remembering the
        /// last clear direction to reduce jitter. Only for dynamic obstacles and fine
        /// local detours - the node graph owns static routing. docs/navigation.md
        /// </summary>
        private Vector3 SteerAroundObstacles(Vector3 desired)
        {
            Vector3 dirFlat = new(desired.x, 0f, desired.z);
            float magnitude = dirFlat.magnitude;
            if (magnitude < 0.001f) return Vector3.zero;

            dirFlat /= magnitude;

            // Probe shorter when close to the target so doorways and tight corners don't repel us.
            float targetDist = hasMoveTarget
                ? Vector3.Distance(new Vector3(currentMoveTarget.x, 0f, currentMoveTarget.z),
                                   new Vector3(transform.position.x, 0f, transform.position.z))
                : 10f;
            float probe = Mathf.Clamp(targetDist * 0.5f, 0.45f, 0.95f);

            if (Time.time < avoidMemoryUntil && !BodyBlocked(lastAvoidDirection, probe + 0.2f) &&
                !StepsOffALedge(lastAvoidDirection, probe + 0.2f))
            {
                return lastAvoidDirection * magnitude;
            }

            if (!BodyBlocked(dirFlat, probe))
            {
                lastAvoidDirection = dirFlat;
                avoidMemoryUntil = Time.time + 0.25f;
                return dirFlat * magnitude;
            }

            foreach (float t in DetourAngleList)
            {
                Vector3 candidate = Quaternion.Euler(0f, t, 0f) * dirFlat;
                // A detour is a convenience; walking off a landing to take one is not.
                // docs/invariants.md#a-detour-may-not-step-off-a-ledge
                if (!BodyBlocked(candidate, probe + 0.2f) && !StepsOffALedge(candidate, probe + 0.2f))
                {
                    lastAvoidDirection = candidate;
                    avoidMemoryUntil = Time.time + 0.3f;
                    return candidate * magnitude;
                }
            }

            // Every detour blocked: push slowly toward the target.
            return dirFlat * (magnitude * 0.6f);
        }

        /// <summary>
        /// Level-3 obstacle report: every 0.25s while moving, list every collider the
        /// body capsule, shin ray and head ray see along the movement direction.
        /// </summary>
        private void ReportObstacles(Vector3 desired)
        {
            try
            {
                Vector3 dir = new(desired.x, 0f, desired.z);
                if (dir.sqrMagnitude < 0.001f) return;

                dir.Normalize();

                BodyCastCapsule(out Vector3 bottom, out Vector3 top, out float radius);
                int hitCount = Physics.CapsuleCastNonAlloc(bottom, top, radius, dir, CastBuffer, 1.5f,
                    ProbeLayers, QueryTriggerInteraction.Ignore);

                string report = $"[ai] Obstacles: pos={transform.position:0.00} dir={dir:0.0} mode={brain.ActivityName}:";
                int count = 0;
                for (int i = 0; i < hitCount; i++)
                {
                    RaycastHit t = CastBuffer[i];
                    if (t.distance < 0.02f) continue;

                    if (IsIgnorableCollider(t.collider)) continue;

                    count++;
                    Collider c = t.collider;
                    string path = c.gameObject.name;
                    Transform parent = c.transform.parent;
                    int depth = 0;
                    while (parent != null && depth < 4)
                    {
                        path = parent.name + "/" + path;
                        parent = parent.parent;
                        depth++;
                    }
                    Gate hitGate = c.GetComponentInParent<Gate>();
                    string gateInfo = hitGate != null ? " [gate opened=" + hitGate.Opened + " fully=" + hitGate.FullyOpened + " locked=" + hitGate.Locked + "]" : "";
                    report += $"\n  hit '{c.gameObject.name}' layer={LayerMask.LayerToName(c.gameObject.layer)} " +
                              $"dist={t.distance:0.00} {DescribeSurface(t, dir)} at {path}{gateInfo}";
                }

                bool shin = Physics.Raycast(GroundPos(0.25f), dir, out RaycastHit shinHit, 0.9f, ProbeLayers, QueryTriggerInteraction.Ignore);
                bool head = Physics.Raycast(GroundPos(1.45f), dir, out RaycastHit headHit, 1.2f, ProbeLayers, QueryTriggerInteraction.Ignore);
                report += $"\n  shinRay={(shin ? "BLOCKED by '" + shinHit.collider.gameObject.name + "' d=" + shinHit.distance.ToString("0.00") : "clear")}";
                report += $" headRay={(head ? "BLOCKED by '" + headHit.collider.gameObject.name + "' d=" + headHit.distance.ToString("0.00") : "clear")}";
                report += $" jumpArmed={wantJump} grounded={cc != null && cc.isGrounded} stairLeg={walkingStairLeg}";

                if (count > 0 || shin || head) NpcLog.Log.LogWarning(report);
            }
            catch (Exception ex)
            {
                NpcLog.Log.LogWarning($"[ai] Obstacle report failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Auto-jump: low furniture (chairs, counters, boxes) blocks the NPC at shin
        /// level but leaves headroom clear - hop over it.
        /// </summary>
        private void TryAutoJump(Vector3 desired)
        {
            wantJump = false;
            if (Time.time < jumpCooldownUntil || verticalVelocity > 0.1f) return;

            Vector3 dir = new(desired.x, 0f, desired.z);
            if (dir.sqrMagnitude < 0.01f) return;

            dir.Normalize();

            // Something solid in front of the shins that is not simply ground.
            Vector3 shinOrigin = GroundPos(0.25f);
            int shinCount = Physics.RaycastNonAlloc(shinOrigin, dir, CastBuffer, 0.7f,
                ProbeLayers, QueryTriggerInteraction.Ignore);
            bool shinBlocked = false;
            for (int i = 0; i < shinCount; i++)
            {
                if (IsIgnorableCollider(CastBuffer[i].collider)) continue;

                if (IsWalkableGround(CastBuffer[i], dir)) continue;

                shinBlocked = true;
                break;
            }
            if (!shinBlocked) return;

            // ...but clear space at head height: it is low furniture, not a wall - jump.
            Vector3 headOrigin = GroundPos(1.45f);
            if (Physics.Raycast(headOrigin, dir, 0.9f, ProbeLayers, QueryTriggerInteraction.Ignore)) return;

            // And never jump under a low ceiling
            if (Physics.Raycast(GroundPos(1.5f), Vector3.up, 1.2f, ProbeLayers, QueryTriggerInteraction.Ignore)) return;

            wantJump = true;
            jumpCooldownUntil = Time.time + 0.9f;
            jumpCommitUntil = Time.time + 0.7f;
        }


        // ------------------------------------------------------------------
        // Stuck detection: docs/agent.md#4-recovery
        // ------------------------------------------------------------------

        /// <summary>
        /// Most one stuck or no-progress check may add: two 0.6 s windows.
        /// </summary>
        private const float MaxStuckWindow = 1.2f;

        private void UpdateStuckDetection(bool wantMove)
        {
            UpdateGoalProgress(wantMove);

            stuckCheckTimer += Time.deltaTime;
            if (stuckCheckTimer < 0.6f) return;

            float moved = Vector3.Distance(new Vector3(transform.position.x, 0f, transform.position.z),
                                           new Vector3(stuckCheckPosition.x, 0f, stuckCheckPosition.z));
            // The time this window really spanned: a slow frame overshoots the 0.6 s. Capped,
            // so a load hitch is not read as seconds of being stuck.
            float elapsed = Mathf.Min(stuckCheckTimer, MaxStuckWindow);
            stuckCheckTimer = 0f;
            stuckCheckPosition = transform.position;

            if (wantMove && moved < 0.07f)
            {
                stuckSeconds += elapsed;

                if (!blockLoggedThisStuck && stuckSeconds > 1.2f)
                {
                    blockLoggedThisStuck = true;
                    LogBlockingCollider();
                }

                // 90 degrees off a stair leg is along the landing, away from the flight.
                // docs/invariants.md#a-stair-leg-is-walked-not-improvised
                if (!walkingStairLeg && Time.time >= sidestepUntil)
                {
                    Vector3 forward = hasMoveTarget
                        ? (currentMoveTarget - transform.position)
                        : transform.forward;
                    forward.y = 0f;
                    if (forward.sqrMagnitude < 0.001f) forward = transform.forward;

                    forward.Normalize();

                    float side = UnityEngine.Random.Range(0, 2) == 0 ? 90f : -90f;
                    sidestepDirection = Quaternion.Euler(0f, side, 0f) * forward;
                    // Same rule as the whisker detours: a sidestep off a landing is a fall.
                    if (StepsOffALedge(sidestepDirection, 0.9f))
                    {
                        sidestepDirection = Quaternion.Euler(0f, -side, 0f) * forward;
                    }
                    sidestepUntil = Time.time + 1.1f;
                }

                // Give up on unreachable goals after a while.
                NpcRecovery recovery = brain.Activity.Recovery;
                if (recovery == NpcRecovery.SkipWaypoint && stuckSeconds > 2.4f && navPlan != null)
                {
                    SkipWaypoint("stuck against something");
                    stuckSeconds = 0f;
                }
                else if (recovery == NpcRecovery.DropPlanAndPause && stuckSeconds > 2.4f)
                {
                    navPlan = null;
                    wanderIdleUntil = Time.time + 1f;
                    stuckSeconds = 0f;
                }
                else if (recovery == NpcRecovery.EndWalk && navPlan != null && stuckSeconds > 2.4f)
                {
                    brain.OnWalkAbandoned("stuck");
                    stuckSeconds = 0f;
                }

                if (stuckSeconds > 5f && Time.time - lastStuckLogTime > 5f &&
                    NpcLog.Level >= 1)
                {
                    lastStuckLogTime = Time.time;
                    NpcLog.Log.LogWarning($"[ai] Stuck near {transform.position} while moving to {currentMoveTarget}");
                }
            }
            else
            {
                if (moved >= 0.07f) blockLoggedThisStuck = false;
                if (!wantMove)
                {
                    stuckSeconds = 0f;
                    blockLoggedThisStuck = false;
                }
                else
                {
                    stuckSeconds = Mathf.Max(0f, stuckSeconds - elapsed);
                }
            }
        }

        /// <summary>
        /// Oscillation detector: the NPC can move constantly (left/right, in place)
        /// while never getting closer to its target.  If no goal progress is made
        /// for ~3 seconds, force a NodeGraph path recalculation or abandon the goal.
        /// </summary>
        private void UpdateGoalProgress(bool wantMove)
        {
            if (!wantMove || !hasMoveTarget || catchInProgress)
            {
                noProgressSeconds = 0f;
                noProgressCycles = 0;
                lastGoalDist = float.MaxValue;
                lastGoalCheckAt = Time.time;
                return;
            }

            if (Time.time < goalCheckAt) return;

            goalCheckAt = Time.time + 0.6f;
            // Same cap: time spent parked or in a hitch is not time spent failing to close in.
            float elapsed = Mathf.Min(Time.time - lastGoalCheckAt, MaxStuckWindow);
            lastGoalCheckAt = Time.time;

            float dist = Vector3.Distance(
                new Vector3(transform.position.x, 0f, transform.position.z),
                new Vector3(currentMoveTarget.x, 0f, currentMoveTarget.z));

            lastGoalClosed = lastGoalDist == float.MaxValue ? 0f : lastGoalDist - dist;
            lastGoalTargetChanged = (currentMoveTarget - lastGoalTarget).sqrMagnitude > 0.01f;
            lastGoalTarget = currentMoveTarget;
            // Passing a waypoint moves the target to the next one, farther by design: that window says
            // nothing either way. docs/invariants.md#progress-is-measured-to-one-target
            bool passedWaypoint = lastGoalTargetChanged && navPlan != null && navPathIndex > lastGoalPlanIndex;
            lastGoalPlanIndex = navPlan != null ? navPathIndex : -1;
            if (lastGoalDist - dist > 0.35f)
            {
                // Made real progress toward the goal.
                noProgressSeconds = 0f;
                noProgressLogged = false;
                noProgressCycles = 0;
            }
            else if (!passedWaypoint)
            {
                noProgressSeconds += elapsed;
            }

            lastGoalDist = dist;

            if (noProgressSeconds > 1.2f && !noProgressLogged)
            {
                noProgressLogged = true;
                LogBlockingCollider();
                if (NpcLog.Level >= 1) NpcLog.Log.LogInfo($"[ai] No progress for {noProgressSeconds:0.0}s: {DescribeWaypointAdvance()}");
            }

            if (noProgressSeconds > 3f)
            {
                // Penned in a sell station's fences: every way out crosses a rail the NPC cannot
                // climb, so no amount of replanning or sidestepping helps. Whatever the walk.
                // docs/invariants.md#a-fenced-sell-station-is-not-somewhere-to-stand
                if (PennedIn())
                {
                    NpcLog.Log.LogWarning(
                        $"[ai] Penned in a sell station's fences at {transform.position:0.0} with no way out - climbing out");
                    if (EmergencyUnstick())
                    {
                        noProgressSeconds = 0f;
                        noProgressCycles = 0;
                        navPlan = null;
                        hasNavPathGoal = false;
                        return;
                    }
                }
                NpcRecovery recovery = brain.Activity.Recovery;
                if (recovery == NpcRecovery.DropPlanAndPause)
                {
                    NpcLog.Log.LogInfo("[ai] No progress toward wander target, abandoning it");
                    navPlan = null;
                    wanderIdleUntil = Time.time + 1.5f;
                    noProgressSeconds = 0f;
                }
                else if (recovery == NpcRecovery.SkipWaypoint && navPlan != null)
                {
                    SkipWaypoint("no progress toward it");
                    noProgressSeconds = 0f;
                }
                else if (recovery == NpcRecovery.EndWalk)
                {
                    brain.OnWalkAbandoned("no progress toward it");
                    noProgressSeconds = 0f;
                }
                else if (recovery == NpcRecovery.Pursuit)
                {
                    noProgressCycles++;

                    // Wedged under a low ceiling: the CC cannot walk there at all.
                    if (noProgressCycles >= 3 && UnderLowCeiling())
                    {
                        if (EmergencyUnstick())
                        {
                            noProgressCycles = 0;
                            noProgressSeconds = 0f;
                            return;
                        }
                    }

                    // Moving for seconds without closing on this waypoint: bar it from
                    // the next few plans. docs/invariants.md#avoid-entry-bars-the-whole-search
                    if (hasMoveTarget)
                    {
                        unreachableWaypoint = currentMoveTarget;
                        unreachableWaypointUntil = Time.time + UnreachableWaypointHold;
                        if (NpcLog.Level >= 1)
                        {
                            NpcLog.Log.LogWarning(
                                $"[ai] Cannot reach waypoint {currentMoveTarget:0.0} - " +
                                $"excluding it from routing for {UnreachableWaypointHold:0}s");
                        }
                    }

                    // Force NodeGraph path recalculation immediately
                    navPathRecalcAt = 0f;
                    navPlan = null;
                    hasNavPathGoal = false;

                    if (noProgressCycles >= 4)
                    {
                        // After multiple failures, do a step-off walk
                        NpcLog.Log.LogInfo("[ai] Cannot reach the player right now, stepping off");
                        Player? followPlayer = NpcPlayer.Pilot;
                        if (followPlayer != null && followPlayer.Controller != null)
                        {
                            Vector3 toPlayer = followPlayer.Controller.CachedTransform.position - transform.position;
                            toPlayer.y = 0f;
                            if (toPlayer.sqrMagnitude < 0.001f) toPlayer = transform.forward;

                            toPlayer.Normalize();
                            toPlayer = Quaternion.Euler(0f, UnityEngine.Random.Range(-35f, 35f), 0f) * toPlayer;
                            followStepOffTarget = transform.position + toPlayer * 1.8f;
                            followStepOffUntil = Time.time + 2.5f;
                        }
                        noProgressSeconds = 0f;
                        noProgressCycles = 0;
                    }
                    else
                    {
                        noProgressSeconds = 0f;
                    }
                }
            }
        }

        /// <summary>
        /// The first game script on a hit or its ancestry. Static scenery has none; a
        /// prop that needs its own rule has one, and this is what to key that rule on -
        /// never a GameObject name, which is what the gate carve-out bug was made of
        /// (docs/known-issues.md#dead-ends).
        /// </summary>
        private static string OwningBehaviour(Collider collider)
        {
            for (Transform? t = collider != null ? collider.transform : null; t != null; t = t.parent)
            {
                foreach (MonoBehaviour behaviour in t.GetComponents<MonoBehaviour>())
                {
                    if (behaviour != null) return behaviour.GetType().Name;
                }
            }
            return "-";
        }

        /// <summary>
        /// How high above the feet a hit sits and how steep its surface is - the two
        /// numbers that tell a ramp from a wall, and the reason the stair stall was
        /// undiagnosable. docs/invariants.md#walkable-ground-is-not-an-obstacle
        /// </summary>
        private string DescribeSurface(RaycastHit hit, Vector3 dir)
        {
            float rise = hit.point.y - GroundPos(0f).y;
            float slope = Vector3.Angle(hit.normal, Vector3.up);
            return $"rise={rise:0.00}m slope={slope:0}deg owner={OwningBehaviour(hit.collider)}" +
                   (IsWalkableGround(hit, dir) ? " WALKABLE" : "");
        }

        /// <summary>
        /// Diagnostics: when the NPC is wedged somewhere, identify the exact collider
        /// (name, layer, full hierarchy path, components) that blocks its body.
        /// </summary>
        private void LogBlockingCollider()
        {
            if (NpcLog.Level < 1) return;

            try
            {
                Vector3 dir = hasMoveTarget ? (currentMoveTarget - transform.position) : transform.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.001f) dir = transform.forward;

                dir.Normalize();

                // Same shape and reach as the whiskers that actually steer: a probe
                // blind where they are not reports "nothing ahead" about the very
                // obstacle deflecting the NPC.
                BodyCastCapsule(out Vector3 bottom, out Vector3 top, out float radius);

                int hitCount = Physics.CapsuleCastNonAlloc(bottom, top, radius, dir, CastBuffer, 0.95f,
                    ProbeLayers, QueryTriggerInteraction.Ignore);
                Collider? blockerCollider = null;
                RaycastHit blockerHit = default;
                float blockerDistance = float.MaxValue;
                string walkable = "";
                for (int i = 0; i < hitCount; i++)
                {
                    RaycastHit t = CastBuffer[i];
                    if (t.distance < 0.02f)
                    {
                        continue; // initial overlap, not blocking
                    }

                    if (IsIgnorableCollider(t.collider)) continue;

                    if (IsWalkableGround(t, dir))
                    {
                        if (walkable.Length == 0)
                        {
                            walkable = " (walkable ground ahead: '" + t.collider.gameObject.name +
                                       "' " + DescribeSurface(t, dir) + ")";
                        }
                        continue;
                    }
                    if (t.distance < blockerDistance)
                    {
                        blockerDistance = t.distance;
                        blockerCollider = t.collider;
                        blockerHit = t;
                    }
                }

                if (blockerCollider != null)
                {
                    Collider blocker = blockerCollider;
                    string path = blocker.gameObject.name;
                    Transform parent = blocker.transform.parent;
                    while (parent != null)
                    {
                        path = parent.name + "/" + path;
                        parent = parent.parent;
                    }

                    string components = "";
                    foreach (Component component in blocker.GetComponents<Component>())
                    {
                        if (component == null) continue;

                        components += (components.Length > 0 ? ", " : "") + component.GetType().Name;
                    }

                    NpcLog.Log.LogWarning(string.Format(
                        "[ai] Blocked by '{0}' (layer '{1}') at path '{2}'; components: {3}; distance {4:0.00} {5}",
                        blocker.gameObject.name, LayerMask.LayerToName(blocker.gameObject.layer), path, components, blockerDistance,
                        DescribeSurface(blockerHit, dir)));
                }
                else
                {
                    NpcLog.Log.LogWarning(
                        "[ai] Stuck but nothing blocking ahead - AI logic issue" + walkable);
                }
            }
            catch (Exception ex)
            {
                NpcLog.Log.LogWarning($"[ai] Block diagnostic failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Movement & animation
        // ------------------------------------------------------------------

        private void ApplyMovement(Vector3 desired, bool wantMove)
        {
            // Gravity; SceneLoader/GameData are unavailable outside a game scene,
            // in which case the default multiplier applies.
            GameSettingsData? settingsData = SceneLoader.Instance != null ? SceneLoader.Instance.GameData?.Settings : null;
            gravity = 9.81f * (settingsData?.gravityMultiplier ?? 1f);

            if (wantJump && cc.isGrounded) verticalVelocity = JumpVelocity;
            else if (!cc.isGrounded) verticalVelocity -= gravity * Time.deltaTime;
            else verticalVelocity = -1f;
            wantJump = false;

            Vector3 moveDir = wantMove ? desired : Vector3.zero;
            moveDir.y = verticalVelocity;
            cc.Move(moveDir * Time.deltaTime);
        }



        /// <summary>
        /// True when something overhead reaches into the body, or sits within
        /// <see cref="CeilingHeadroom"/> of its top. docs/invariants.md#a-low-ceiling-is-measured-from-the-body
        /// </summary>
        private bool UnderLowCeiling()
        {
            float bodyTop = cc != null ? cc.height + cc.skinWidth : 1.1f;
            Vector3 origin = GroundPos(0.25f);
            int count = Physics.RaycastNonAlloc(origin, Vector3.up, CastBuffer, bodyTop + CeilingHeadroom - 0.25f,
                ProbeLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                if (!IsIgnorableCollider(CastBuffer[i].collider)) return true;
            }
            return false;
        }

        /// <summary>
        /// Last-resort escape from a genuinely unwalkable spot.
        /// </summary>
        private bool EmergencyUnstick()
        {
            Vector3 dir = Vector3.zero;
            Player? player = NpcPlayer.Pilot;
            if (player != null && player.Controller != null)
            {
                dir = player.Controller.CachedTransform.position - transform.position;
            }
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f) dir = transform.forward;

            dir.Normalize();

            float radius = cc != null ? cc.radius : 0.25f;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                Vector3 tryDir = Quaternion.Euler(0f, 90f * (attempt % 4), 0f) * dir;
                int group = attempt / 4; float distance = 1.2f + 1.3f * group;
                Vector3 landing = transform.position + tryDir * distance;
                landing.y = transform.position.y;
                // Hopping the rail into the next bay of the same pen is not an escape.
                if (SellPens.InAFencedPen(landing, null, 0f)) continue;

                Vector3 bottom = landing + Vector3.up * (originToFeet + 0.25f + radius);
                Vector3 top = landing + Vector3.up * (originToFeet + 1.4f);
                int occupied = Physics.OverlapCapsuleNonAlloc(bottom, top, radius * 0.8f, OverlapBuffer,
                    ProbeLayers, QueryTriggerInteraction.Ignore);
                bool free = true;
                for (int i = 0; i < occupied; i++)
                {
                    if (!IsIgnorableCollider(OverlapBuffer[i]))
                    {
                        free = false;
                        break;
                    }
                }

                if (free)
                {
                    // Update returns before the stuck check when cc is null.
                    cc!.enabled = false;
                    transform.position = landing;
                    cc.enabled = true;
                    verticalVelocity = 0f;
                    hasBaseFloor = false;
                    NpcLog.Log.LogWarning("[ai] Emergency unstuck performed");
                    return true;
                }
            }
            return false;
        }



        /// <summary>
        /// The NPC is in a pen and the thing it cannot get to is outside it - so it is trying to
        /// leave, not to arrive. No margin, so a spot just outside the rail does not count. A leg
        /// fetching something from inside the pen is arriving, and is left alone.
        /// docs/invariants.md#a-fenced-sell-station-is-not-somewhere-to-stand
        /// </summary>
        private bool PennedIn() =>
            SellPens.InAFencedPen(FloorUnderNpc(), null, 0f) &&
            (!hasMoveTarget || !SellPens.InAFencedPen(currentMoveTarget, null, 0f));
    }
}
