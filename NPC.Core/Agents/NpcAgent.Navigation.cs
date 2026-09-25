using System;
using System.Collections.Generic;
using NPC.Core.Navigation;
using NPC.Core.World;
using UnityEngine;
using Random = UnityEngine.Random;

namespace NPC.Core.Agents
{
    /// <summary>
    /// The walks a brain picks from (Pursue, Wander, Stay, SimpleAdvance, a committed plan) and A* route
    /// following: docs/agent.md#5-walks
    /// </summary>
    public sealed partial class NpcAgent
    {
        private const float NavPathRecalcInterval = 0.22f;
        // A plan is kept while the goal stays within this drift and its entry stretch
        // is clear. Replanning every tick flips the first waypoint back and forth.
        private const float NavPathGoalDrift = 1.5f;
        private Vector3 navPathGoal = Vector3.zero;

        // How far off the previous waypoint still counts as walking the route; inside
        // it the commitment check is skipped.
        // docs/invariants.md#commitment-skips-on-route
        private const float NavPathOnRouteRadius = 1.2f;

        // Same-deck cap for the XZ advance branch, floor to floor. Kept in step with
        // NavGraph.SameLevelDeltaY - both answer "same deck?".
        // docs/invariants.md#waypoint-advance-is-dual
        private const float WaypointAdvanceMaxDeltaY = 0.5f;
        // Reach radii for the same two branches: XZ on one deck, 3D for anything else.
        private const float WaypointReachedXZ = 0.55f;
        private const float WaypointReached3D = 0.65f;
        /// <summary>
        /// Where the 3D radius is measured from: the player's origin, which the markers were placed around.
        /// Not the body's own origin, which can be anywhere. docs/invariants.md#waypoint-advance-is-dual
        /// </summary>
        private const float WalkerOriginAboveFeet = 0.5f;

        // A leg whose ends are on different decks is a flight of stairs, walked as drawn.
        // docs/invariants.md#a-stair-leg-is-walked-not-improvised
        /// <summary>
        /// How far along the leg, past the NPC's own projection, it aims: about one tread.
        /// </summary>
        private const float StairLegLookahead = 0.35f;
        /// <summary>
        /// How far beside a leg's line still counts as standing on that flight.
        /// </summary>
        private const float StairLegCorridor = 0.5f;

        // Anti-livelock for path commitment: bar the waypoint, then step off.
        // Counted over a sliding window, never a run:
        // docs/invariants.md#escalate-over-a-window
        private const float EntryBlockedWindow = 4f;
        private const int EntryBlockedAvoidAfter = 4;
        private const int EntryBlockedStepOffAfter = 8;
        private int entryBlockedCount = 0;
        private float entryBlockedWindowStart = 0f;
        private Vector3 lastBlockedEntry = Vector3.zero;

        // The last graph node the NPC walked away from. Lives outside the plan:
        // docs/invariants.md#backtrack-memory-outlives-the-plan
        private Vector3 lastDepartedWaypoint = Vector3.zero;
        private bool hasLastDepartedWaypoint = false;

        // Wander state. The idle windows are short on purpose: picking and planning
        // used to be separate frames with separate timers, which stacked into an NPC
        // standing still for ten seconds between destinations.
        private int wanderPickFailures = 0;
        private const float WanderIdleMin = 0.3f;
        private const float WanderIdleMax = 1.4f;
        private const float WanderRetryDelay = 0.5f;
        /// <summary>
        /// Destinations tried per pick. Each costs one A* over the active graph, so
        /// this stays small.
        /// </summary>
        private const int WanderPickAttempts = 4;

        // Idle-on-furniture watchdog. Every other recovery needs wantMove, so an NPC
        // that simply stands there had nothing watching it.
        private float idleNoRouteSeconds = 0f;
        private float idleStepOffCooldown = 0f;
        private const float IdleNoRouteGiveUp = 2.5f;
        /// <summary>
        /// How far above its own floor plane counts as "perched on something".
        /// </summary>
        private const float IdleAboveFloorDelta = 0.35f;
        /// <summary>
        /// An idle NPC this near one that ranks before it (flat, same deck) steps SpacingStep away from
        /// it, at most once per SpacingCooldown.
        /// </summary>
        private const float NpcSpacing = 0.6f;
        private const float SpacingSameDeck = 1f;
        private const float SpacingStep = 0.8f;
        private const float SpacingCooldown = 3f;
        private float spaceOutAt = 0f;

        private float doorBlockedLogAt = 0f;

        // The give-up clock of the waypoint underway: docs/invariants.md#give-up-per-waypoint
        private Vector3 clockWaypoint;
        private bool clockRunning;
        private float clockSince;
        private float clockBudget;
        private const float WaypointBudgetBase = 6f;
        private const float WaypointBudgetPerMetre = 1.2f;

        // ------------------------------------------------------------------
        // The plan, as a brain sees it
        // ------------------------------------------------------------------

        /// <summary>
        /// The plan being walked, or null.
        /// </summary>
        public NavPath? Plan => navPlan;

        /// <summary>
        /// The waypoint of <see cref="Plan"/> it is walking to.
        /// </summary>
        public int PlanIndex => navPathIndex;

        /// <summary>
        /// A plan with waypoints still ahead.
        /// </summary>
        public bool HasPlanLeft => navPlan != null && navPathIndex < navPlan.Value.Count;

        /// <summary>
        /// A waypoint of this plan was skipped rather than reached, so running out of it is not arriving:
        /// docs/invariants.md#a-skipped-waypoint-is-not-an-arrival
        /// </summary>
        public bool SkippedWaypoint => routeSkippedWaypoint;

        /// <summary>
        /// Walks `plan` from its first waypoint. `fresh` also forgets a skipped waypoint: a new walk, not
        /// a replan of the one under way.
        /// </summary>
        public void CommitPlan(NavPath plan, bool fresh)
        {
            navPlan = plan;
            navPathIndex = 0;
            if (fresh) routeSkippedWaypoint = false;
        }

        /// <summary>
        /// Forgets the plan and what was learned walking it.
        /// </summary>
        public void DropPlan()
        {
            navPlan = null;
            navPathIndex = 0;
            hasNavPathGoal = false;
            entryBlockedCount = 0;
            hasLastDepartedWaypoint = false;
            routeSkippedWaypoint = false;
        }

        /// <summary>
        /// A Pursue that has arrived: the plan goes, what it learned stays for the next one.
        /// </summary>
        public void ReleasePlan()
        {
            navPlan = null;
            entryBlockedCount = 0;
        }

        // ------------------------------------------------------------------
        // Walks
        // ------------------------------------------------------------------

        /// <summary>
        /// Walks to a goal that moves, the player say, replanning as it goes; straight at it when the graph
        /// has no plan. The brain decides when to stop: docs/agent.md#5-walks
        /// </summary>
        public Vector3 Pursue(Vector3 goal, out bool wantMove)
        {
            wantMove = false;

            // Commit to the current plan while it still holds; replanning every tick
            // flips the first waypoint back and forth. docs/navigation.md §5
            if (Time.time >= navPathRecalcAt)
            {
                navPathRecalcAt = Time.time + NavPathRecalcInterval;

                Vector3 start = FloorUnderNpc();
                bool debug = NpcLog.Level >= 2;
                string? invalidReason;
                // Captured where the condition is decided, so the replan below never
                // has to re-dereference a plan it cannot prove is still there.
                Vector3? blockedEntry = null;
                bool leftStairLeg = false;
                if (navPlan == null)
                {
                    invalidReason = "no plan";
                }
                else if (navPathIndex >= navPlan.Value.Count)
                {
                    invalidReason = "plan exhausted";
                }
                else if (!hasNavPathGoal)
                {
                    invalidReason = "plan has no goal";
                }
                // A deck test, not a sight line, so forced legs get it too; ahead of "goal
                // moved", which would keep the index. docs/invariants.md#off-the-flight-is-off-the-plan
                else if (HasLeftStairLeg(out float legLow, out float legHigh))
                {
                    invalidReason = "left the stair leg";
                    leftStairLeg = true;
                    if (NpcLog.Level >= 1)
                    {
                        NpcLog.Log.LogWarning(
                            $"[ai] Left the stair leg to {navPlan.Value[navPathIndex]:0.0}: feet {GroundPos(0f).y:0.00} " +
                            $"(probe floor {start.y:0.00}) are outside {legLow:0.00}..{legHigh:0.00} - replanning from here");
                    }
                }
                else if ((navPathGoal - goal).sqrMagnitude > NavPathGoalDrift * NavPathGoalDrift)
                {
                    invalidReason = "goal moved";
                }
                // docs/invariants.md#one-entry-predicate; skipped per
                // #commitment-skips-on-route and #links-have-no-los
                else if (!navPlan.Value.IsForced(navPathIndex) &&
                         !IsStandingOnRoute(start) &&
                         !NavGraph.CanReachEntry(start, navPlan.Value[navPathIndex]))
                {
                    invalidReason = "entry stretch blocked";
                    blockedEntry = navPlan.Value[navPathIndex];
                }
                // No "next stretch blocked" check: docs/invariants.md#links-have-no-los
                else
                {
                    invalidReason = null;
                }

                if (invalidReason != null)
                {
                    // Where the NPC departed; entry nodes near it are penalized.
                    // docs/invariants.md#backtrack-memory-outlives-the-plan
                    // Not after leaving a stair leg: its head is where the route resumes.
                    Vector3? cameFrom = leftStairLeg ? null
                        : navPlan != null && navPathIndex >= 1 && navPathIndex < navPlan.Value.Count
                            ? navPlan.Value[navPathIndex - 1]
                            : hasLastDepartedWaypoint ? lastDepartedWaypoint : null;

                    // A blocked entry that keeps coming back gets barred, so the
                    // search must offer a different way out.
                    // docs/invariants.md#escalate-over-a-window
                    bool entryBlocked = blockedEntry.HasValue;
                    Vector3? avoidEntry = null;
                    if (Time.time - entryBlockedWindowStart > EntryBlockedWindow)
                    {
                        entryBlockedWindowStart = Time.time;
                        entryBlockedCount = 0;
                    }
                    if (entryBlocked)
                    {
                        // entryBlocked is blockedEntry.HasValue.
                        lastBlockedEntry = blockedEntry!.Value;
                        entryBlockedCount++;
                        if (entryBlockedCount >= EntryBlockedAvoidAfter) avoidEntry = lastBlockedEntry;
                    }

                    // A waypoint the stuck detector proved unwalkable outranks the
                    // blocked-entry heuristic: that one is measured from the NPC's
                    // actual failure to make progress, not from a probe.
                    if (Time.time < unreachableWaypointUntil) avoidEntry = unreachableWaypoint;

                    if (debug)
                    {
                        NpcLog.Log.LogInfo(
                            $"[ai] Replanning ({invalidReason}): NPC {transform.position:0.0} -> goal {goal:0.0}, " +
                            $"start {start:0.0}, cameFrom={(cameFrom.HasValue ? cameFrom.Value.ToString("0.0") : "none")}" +
                            (entryBlocked ? $", blocked x{entryBlockedCount} in {EntryBlockedWindow:0}s" : "") +
                            (avoidEntry.HasValue ? $", avoiding {avoidEntry.Value:0.0}" : ""));
                    }

                    // Always call it, even with an empty graph: FindPath returns null
                    // straight away and clears LastPathBlockedByDoor, which the
                    // no-plan branch below reads.
                    NavPath? plan =
                        NavGraph.FindPath(start, goal, cameFrom, avoidEntry);

                    if (entryBlocked && entryBlockedCount >= EntryBlockedStepOffAfter)
                    {
                        // Even with the offending waypoint barred the graph keeps
                        // pointing through it. Walk off manually and re-plan from
                        // wherever that leaves the NPC.
                        NpcLog.Log.LogWarning(
                            $"[ai] {entryBlockedCount} blocked entries in {EntryBlockedWindow:0}s " +
                            $"near {lastBlockedEntry:0.0} - abandoning the plan and stepping off");
                        plan = null;
                        navPlan = null;
                        navPathIndex = 0;
                        routeSkippedWaypoint = false;
                        entryBlockedCount = 0;
                        entryBlockedWindowStart = Time.time;
                    }

                    if (plan is { Count: > 0 })
                    {
                        // Keep walk progress when the fresh plan continues the old route - never
                        // after a blocked entry or off the flight, never past the waypoint walked.
                        // docs/invariants.md#drop-the-index-on-a-blocked-entry
                        if (!entryBlocked && !leftStairLeg && navPlan != null &&
                            SameRemainingPath(navPlan.Value.Waypoints, navPathIndex, plan.Value.Waypoints,
                                out int resumeAt))
                        {
                            navPathIndex = Mathf.Min(navPathIndex, resumeAt);
                            navPlan = plan;
                        }
                        else
                        {
                            navPlan = plan;
                            navPathIndex = 0;
                            // A plan walked from the top has skipped nothing yet.
                            routeSkippedWaypoint = false;
                        }
                        navPathGoal = goal;
                        hasNavPathGoal = true;
                    }
                    else if (navPlan == null || navPathIndex >= navPlan.Value.Count)
                    {
                        navPlan = null;
                        hasNavPathGoal = false;

                        // A door the NPC cannot open is the whole reason there is
                        // no route: stepping off has nowhere to go.
                        // docs/invariants.md#stand-still-when-door-blocked
                        if (NavGraph.LastPathBlockedByDoor)
                        {
                            LogDoorBlockedRoute();
                            hasMoveTarget = false;
                            return Vector3.zero;
                        }

                        // Cannot route from here (e.g. wedged on furniture under a
                        // low ceiling, where every probe fails): step off toward the
                        // goal and re-plan from the new spot.
                        Vector3 toGoal = goal - transform.position;
                        toGoal.y = 0f;
                        if (toGoal.sqrMagnitude < 0.001f) toGoal = transform.forward;

                        toGoal.Normalize();
                        toGoal = Quaternion.Euler(0f, Random.Range(-30f, 30f), 0f) * toGoal;
                        followStepOffTarget = transform.position + toGoal * 1.5f;
                        followStepOffUntil = Time.time + 1.2f;
                        if (debug)
                        {
                            NpcLog.Log.LogInfo(
                            "[ai] No routable plan - stepping off toward the player");
                        }
                    }
                    // Keeping the old path beats a blind direct walk - only while it is a
                    // path from here. docs/invariants.md#a-stale-plan-is-worse-than-none
                    else if (leftStairLeg || !NavGraph.WaypointIsOnAReachableDeck(
                                 start, navPlan.Value[navPathIndex]))
                    {
                        NpcLog.Log.LogWarning(
                            $"[ai] Dropping a stale plan: {navPlan.Value[navPathIndex]:0.0} is not on a deck " +
                            $"I can reach from {start:0.0}");
                        navPlan = null;
                        hasNavPathGoal = false;
                        navPathIndex = 0;
                    }
                    // else: keep walking the old path - it beats a blind direct walk.
                }
            }

            // Follow path if available
            if (navPlan != null && navPathIndex < navPlan.Value.Count)
            {
                AdvancePlan();

                if (navPathIndex < navPlan.Value.Count)
                {
                    wantMove = true;
                    currentMoveTarget = navPlan.Value[navPathIndex];
                    hasMoveTarget = true;
                    return HeadingAlongPlan() * MoveSpeed;
                }
            }

            // Direct fallback if no node path available
            Vector3 toTarget = goal - transform.position;
            toTarget.y = 0f;
            wantMove = true;
            currentMoveTarget = goal;
            hasMoveTarget = true;
            return toTarget.normalized * MoveSpeed;
        }

        /// <summary>
        /// Motionless above its own floor plane means perched on something it cannot
        /// route from. Every other watchdog needs wantMove, so nothing else sees this.
        /// docs/invariants.md#idle-above-the-floor-plane-is-a-stall
        /// </summary>
        private void UpdateIdleRecovery(bool wantMove)
        {
            if (wantMove || catchInProgress || !brain.Activity.IdleWatch)
            {
                idleNoRouteSeconds = 0f;
                return;
            }

            idleNoRouteSeconds += Time.deltaTime;
            if (idleNoRouteSeconds < IdleNoRouteGiveUp || Time.time < idleStepOffCooldown) return;

            idleNoRouteSeconds = 0f;

            // On its own deck this is legitimate waiting - arrived, or holding for a
            // door it cannot open. Only an elevated perch is a stall.
            if (!hasBaseFloor || FloorUnderNpc().y - baseFloorY < IdleAboveFloorDelta) return;

            idleStepOffCooldown = Time.time + 6f;
            Vector3 dir = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
            followStepOffTarget = transform.position + dir * 2f;
            followStepOffUntil = Time.time + 1.2f;
            navPlan = null;
            NpcLog.Log.LogWarning(
                "[ai] Idle " + IdleNoRouteGiveUp.ToString("0.0") + "s above my own floor with no route - stepping off");
        }

        /// <summary>
        /// Says why the NPC is standing still, throttled. A silent stall is
        /// indistinguishable from a forgotten one.
        /// </summary>
        private void LogDoorBlockedRoute()
        {
            if (Time.time < doorBlockedLogAt) return;

            doorBlockedLogAt = Time.time + 10f;
            NpcLog.Log.LogInfo(
                "[ai] No route that avoids a door I cannot open - waiting here" +
                (NpcDoors.ImpassableCount > 0 ? " (" + NpcDoors.ImpassableCount + " closed to me)" : ""));
        }

        /// <summary>
        /// Holds position. Only the step-off stretch may move the NPC, so a staying
        /// NPC still clears a doorway it is blocking.
        /// docs/invariants.md#step-off-applies-in-every-mode
        /// </summary>
        public Vector3 Stay(out bool wantMove)
        {
            wantMove = false;
            if (TryStepOff(out Vector3 stepOff))
            {
                wantMove = true;
                return stepOff;
            }
            hasMoveTarget = false;
            return Vector3.zero;
        }

        /// <summary>
        /// Whether the waypoint underway has taken longer than its budget, set from the distance when the
        /// NPC set off for it. Each waypoint reached starts a new clock, so a long walk that keeps arriving
        /// never runs out. docs/invariants.md#give-up-per-waypoint
        /// </summary>
        public bool WaypointOverdue(out float budget)
        {
            budget = 0f;
            if (navPlan is not { } plan || navPathIndex >= plan.Count)
            {
                clockRunning = false;
                return false;
            }

            Vector3 waypoint = plan[navPathIndex];
            if (!clockRunning || waypoint != clockWaypoint)
            {
                clockRunning = true;
                clockWaypoint = waypoint;
                clockSince = Time.time;
                clockBudget = WaypointBudgetBase + Vector3.Distance(transform.position, waypoint) * WaypointBudgetPerMetre;
            }
            budget = clockBudget;
            return Time.time - clockSince > clockBudget;
        }

        /// <summary>
        /// Advances past every waypoint the NPC has reached, on a dual
        /// threshold - XZ on its own deck, 3D for anything above or below. Both
        /// branches must stay: docs/invariants.md#waypoint-advance-is-dual
        /// </summary>
        public void AdvancePlan()
        {
            if (navPlan == null) return;

            float floorY = FloorUnderNpc().y;
            // The stair band reads the body, not the probe: docs/invariants.md#off-the-flight-is-off-the-plan
            float feetY = GroundPos(0f).y;
            Vector3 walker = GroundPos(WalkerOriginAboveFeet);
            while (navPathIndex < navPlan.Value.Count)
            {
                Vector3 wp = navPlan.Value[navPathIndex];
                float distXZ = new Vector2(transform.position.x - wp.x, transform.position.z - wp.z).magnitude;
                float dist3DSquared = (walker - wp).sqrMagnitude;
                // Deck to deck, with the deck the search measured: a fresh probe under a marker can
                // miss and answer the marker's own Y. docs/invariants.md#floor-to-floor
                bool sameLevel = Mathf.Abs(floorY - navPlan.Value.FloorY(navPathIndex)) <= WaypointAdvanceMaxDeltaY;
                bool reached = (distXZ < WaypointReachedXZ && sameLevel) ||
                               dist3DSquared < WaypointReached3D * WaypointReached3D;

                // The leg that leaves wp, when it is a flight.
                if (TryStairLegBand(navPathIndex + 1, out float low, out float high))
                {
                    // Reached is not enough: never advance into a flight whose decks the NPC
                    // is off. docs/invariants.md#waypoint-advance-is-dual
                    if (feetY < low || feetY > high) break;

                    // ...but already walking it counts as reached, as after a mid-flight
                    // replan. docs/invariants.md#a-stair-leg-is-walked-not-improvised
                    ProjectOntoLeg(wp, navPlan.Value[navPathIndex + 1], out _, out float length,
                        out float along, out float across);
                    reached |= along >= 0f && along <= length && across <= StairLegCorridor;
                }
                if (!reached) break;

                // Only real nodes: waypoint 0 is usually the plan's own start, and
                // penalizing entries near that pushes the NPC off its own spot.
                if (NavGraph.IsNodePosition(wp))
                {
                    lastDepartedWaypoint = wp;
                    hasLastDepartedWaypoint = true;
                }
                navPathIndex++;
            }
        }

        /// <summary>
        /// The inputs of AdvancePlan's test for the waypoint underway, for a stall line: whether the NPC
        /// stands on it without it counting as reached.
        /// </summary>
        private string DescribeWaypointAdvance()
        {
            string heading = $"heading for {currentMoveTarget:0.00}" + (Time.time < followStepOffUntil ? " (a step-off)" : "");
            if (navPlan is not { } plan || navPathIndex >= plan.Count) return heading + ", no plan";

            Vector3 wp = plan[navPathIndex];
            float distXZ = new Vector2(transform.position.x - wp.x, transform.position.z - wp.z).magnitude;
            float dist3D = Vector3.Distance(GroundPos(WalkerOriginAboveFeet), wp);
            float floorY = FloorUnderNpc().y;
            float wpFloor = plan.FloorY(navPathIndex);
            string band = TryStairLegBand(navPathIndex + 1, out float low, out float high) ? $", next leg a flight {low:0.00}..{high:0.00}" : "";
            return $"{heading}, waypoint {navPathIndex + 1}/{plan.Count} {wp:0.00} " +
                   $"{distXZ:0.00}m flat (reached < {WaypointReachedXZ:0.00}) {dist3D:0.00}m 3D (< {WaypointReached3D:0.00}), " +
                   $"floor {floorY:0.00} vs waypoint floor {wpFloor:0.00} (same deck within {WaypointAdvanceMaxDeltaY:0.00}), " +
                   $"feet {GroundPos(0f).y:0.00}, grounded {(cc != null && cc.isGrounded)}{band}, " +
                   $"speed {(cc != null ? new Vector2(cc.velocity.x, cc.velocity.z).magnitude : 0f):0.00}m/s " +
                   $"{MotionAngleToTarget()} off the target, closed {lastGoalClosed:0.00}m in the last check" +
                   (lastGoalTargetChanged ? " (the target changed)" : "") +
                   (Time.time < sidestepUntil ? ", sidestepping" : "") +
                   $", door ray {Time.time - lastDoorRayTime:0.0}s ago: {lastDoorRay}";
        }

        private string MotionAngleToTarget()
        {
            if (cc == null) return "?";

            Vector3 motion = new(cc.velocity.x, 0f, cc.velocity.z);
            Vector3 toTarget = currentMoveTarget - transform.position;
            toTarget.y = 0f;
            if (motion.sqrMagnitude < 0.01f || toTarget.sqrMagnitude < 0.0001f) return "?";

            return $"{Vector3.Angle(motion, toTarget):0}deg";
        }

        /// <summary>
        /// Short unvalidated walk toward the step-off target: gets the NPC off chair
        /// seats and ledges, and out of a doorway it is holding open. Every walk checks it first:
        /// docs/invariants.md#step-off-applies-in-every-mode
        /// </summary>
        public bool TryStepOff(out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (Time.time >= followStepOffUntil) return false;

            currentMoveTarget = followStepOffTarget;
            hasMoveTarget = true;
            Vector3 toStep = followStepOffTarget - transform.position;
            toStep.y = 0f;
            if (toStep.sqrMagnitude >= 0.001f) velocity = toStep.normalized * MoveSpeed;

            return true;
        }

        /// <summary>
        /// A step-off of the brain's own, such as backing away: `seconds` toward `target`, unvalidated.
        /// </summary>
        public void StepOff(Vector3 target, float seconds)
        {
            followStepOffTarget = target;
            followStepOffUntil = Time.time + seconds;
        }

        /// <summary>
        /// A step-off under way may lead somewhere the brain no longer wants to go.
        /// </summary>
        public void CancelStepOff() => followStepOffUntil = 0f;

        /// <summary>
        /// True when the stretch the NPC is walking is the graph edge the cache
        /// already validated, not an improvised line from off the route.
        /// </summary>
        private bool IsStandingOnRoute(Vector3 start)
        {
            if (navPlan == null || navPathIndex < 1 || navPathIndex >= navPlan.Value.Count) return false;

            return (start - navPlan.Value[navPathIndex - 1]).sqrMagnitude <=
                   NavPathOnRouteRadius * NavPathOnRouteRadius;
        }

        /// <summary>
        /// True when the remaining old waypoints reappear at the tail of the new plan, which
        /// starts at `resumeAt` (same route; the final waypoint is the goal and may drift).
        /// </summary>
        private static bool SameRemainingPath(IReadOnlyList<Vector3> oldPath, int oldIndex, IReadOnlyList<Vector3> newPath,
            out int resumeAt)
        {
            int remaining = oldPath.Count - oldIndex;
            resumeAt = newPath.Count - remaining;
            if (remaining <= 0 || resumeAt < 0) return false;

            for (int i = 0; i < remaining; i++)
            {
                float tolerance = i == remaining - 1 ? 2.0f : 0.3f;
                if ((oldPath[oldIndex + i] - newPath[resumeAt + i]).sqrMagnitude > tolerance * tolerance) return false;
            }
            return true;
        }

        /// <summary>
        /// The decks stair leg i (waypoint i-1 to i) joins, widened by the same-deck
        /// tolerance; false when both ends are on one deck and it is no flight at all.
        /// </summary>
        private bool TryStairLegBand(int i, out float low, out float high)
        {
            low = high = 0f;
            if (navPlan == null || i < 1 || i >= navPlan.Value.Count) return false;

            float a = navPlan.Value.FloorY(i - 1);
            float b = navPlan.Value.FloorY(i);
            if (Mathf.Abs(a - b) <= WaypointAdvanceMaxDeltaY) return false;

            low = Mathf.Min(a, b) - WaypointAdvanceMaxDeltaY;
            high = Mathf.Max(a, b) + WaypointAdvanceMaxDeltaY;
            return true;
        }

        /// <summary>
        /// Walking a stair leg with the feet on neither of its decks nor between them: the
        /// waypoint is not reachable along it from here. Feet, not FloorUnderNpc: an
        /// ungrounded frame on the treads can probe a deck below. docs/invariants.md#off-the-flight-is-off-the-plan
        /// </summary>
        public bool HasLeftStairLeg(out float low, out float high)
        {
            float feetY = GroundPos(0f).y;
            return TryStairLegBand(navPathIndex, out low, out high) && (feetY < low || feetY > high);
        }

        /// <summary>
        /// The NPC against the flat line a->b: its unit direction and length, how far
        /// along it the NPC stands, and how far beside it.
        /// </summary>
        private void ProjectOntoLeg(Vector3 a, Vector3 b, out Vector3 direction, out float length,
            out float along, out float across)
        {
            direction = new Vector3(b.x - a.x, 0f, b.z - a.z);
            length = direction.magnitude;
            Vector3 offset = new(transform.position.x - a.x, 0f, transform.position.z - a.z);
            direction = length > 0.01f ? direction / length : Vector3.zero;
            along = Vector3.Dot(offset, direction);
            across = length > 0.01f
                ? Mathf.Abs(direction.x * offset.z - direction.z * offset.x)
                : offset.magnitude;
        }

        /// <summary>
        /// The step toward the plan's current waypoint at `speed`, which becomes the move target. Call it
        /// with <see cref="HasPlanLeft"/>, after <see cref="AdvancePlan"/>.
        /// </summary>
        public Vector3 HeadAlongPlan(float speed, out bool wantMove)
        {
            wantMove = true;
            // HasPlanLeft is the caller's contract.
            currentMoveTarget = navPlan!.Value[navPathIndex];
            hasMoveTarget = true;
            return HeadingAlongPlan() * speed;
        }

        /// <summary>
        /// Flat heading for the current waypoint. On a stair leg it aims a tread's length
        /// along the leg, not at its far end, so the NPC turns onto the flight instead of
        /// cutting the corner. docs/invariants.md#a-stair-leg-is-walked-not-improvised
        /// </summary>
        private Vector3 HeadingAlongPlan()
        {
            walkingStairLeg = false;
            if (navPlan is not { } plan)
            {
                return Vector3.zero;
            }

            Vector3 aim = plan[navPathIndex];
            walkingStairLeg = TryStairLegBand(navPathIndex, out _, out _);
            if (walkingStairLeg)
            {
                Vector3 from = plan[navPathIndex - 1];
                ProjectOntoLeg(from, aim, out Vector3 direction, out float length, out float along, out _);
                LogStairLeg(plan, from, aim);
                aim = from + direction * Mathf.Clamp(along + StairLegLookahead, 0f, length);
            }
            Vector3 heading = aim - transform.position;
            heading.y = 0f;
            return heading.normalized;
        }

        /// <summary>
        /// Once per leg, so a capture shows where the stair rule took over.
        /// </summary>
        private void LogStairLeg(NavPath plan, Vector3 from, Vector3 to)
        {
            if (NpcLog.Level < 2 || (to - loggedStairLegEnd).sqrMagnitude < 0.01f) return;

            loggedStairLegEnd = to;
            NpcLog.Log.LogInfo(
                $"[ai] Walking the stair leg {from:0.0} -> {to:0.0} " +
                $"(decks {plan.FloorY(navPathIndex - 1):0.00} -> {plan.FloorY(navPathIndex):0.00})");
        }

        /// <summary>
        /// Walks to random nodes of `owner` (null: any active node), a short breath between them. A node
        /// `avoid` accepts is not picked. docs/agent.md#5-walks
        /// </summary>
        public Vector3 Wander(string? owner, Predicate<Vector3>? avoid, out bool wantMove)
        {
            wantMove = false;

            if (TryStepOff(out Vector3 stepOff))
            {
                wantMove = true;
                return stepOff;
            }

            // Follow path to wander target
            if (navPlan != null && navPathIndex < navPlan.Value.Count)
            {
                // Advance past waypoints (same level-aware threshold as Pursue).
                AdvancePlan();

                if (navPathIndex >= navPlan.Value.Count)
                {
                    // Reached the end of the path: a short breath, not a long stare.
                    navPlan = null;
                    wanderIdleUntil = Time.time + Random.Range(WanderIdleMin, WanderIdleMax);
                    hasMoveTarget = false;
                    return Vector3.zero;
                }

                if (WaypointOverdue(out float budget))
                {
                    NpcLog.Log.LogInfo($"[ai] Wander waypoint {navPathIndex + 1}/{navPlan.Value.Count} not reached in " +
                                       $"{budget:0}s, picking a new target");
                    navPlan = null;
                    wanderIdleUntil = Time.time + WanderRetryDelay;
                    hasMoveTarget = false;
                    return Vector3.zero;
                }

                // docs/invariants.md#off-the-flight-is-off-the-plan
                if (HasLeftStairLeg(out float legLow, out float legHigh))
                {
                    // Wander has no replan-and-keep-walking path, so standing still re-plans
                    // the same unwalkable leg from the same spot forever. Walk off it first:
                    // docs/invariants.md#step-off-applies-in-every-mode
                    bool stepping = TryStepOffTowardLegHead();
                    NpcLog.Log.LogInfo(
                        $"[ai] Left the stair leg to the wander target (feet {GroundPos(0f).y:0.00}, probe floor " +
                        $"{FloorUnderNpc().y:0.00}, outside {legLow:0.00}..{legHigh:0.00}), " +
                        (stepping ? $"stepping off toward {followStepOffTarget:0.0} and picking a new one"
                                  : "picking a new one"));
                    navPlan = null;
                    wanderIdleUntil = Time.time + WanderRetryDelay;
                    hasMoveTarget = false;
                    return Vector3.zero;
                }

                return HeadAlongPlan(MoveSpeed * 0.85f, out wantMove);
            }

            if (Time.time >= wanderIdleUntil && TryStartWanderRoute(owner, avoid)) return Vector3.zero;

            hasMoveTarget = false;
            return Vector3.zero;
        }

        /// <summary>
        /// Picks a wander target and plans to it in one go, trying a few candidates.
        /// Picking and planning used to be separate frames with their own idle timers,
        /// which is how a failed pick turned into several seconds of standing still.
        /// </summary>
        private bool TryStartWanderRoute(string? owner, Predicate<Vector3>? avoid)
        {
            if (NavGraph.NodeCount == 0)
            {
                StepOffAfterWanderFailure();
                return false;
            }

            int planned = 0;
            for (int attempt = 0; attempt < WanderPickAttempts; attempt++)
            {
                Vector3 node = NavGraph.RandomNode(owner);
                if (node == Vector3.zero) break;
                if (avoid != null && avoid(node)) continue;

                planned++;

                // From the floor, like Pursue: docs/invariants.md#floor-to-floor
                NavPath? plan = NavGraph.FindPath(FloorUnderNpc(), node);
                if (plan is { Count: > 0 })
                {
                    wanderPickFailures = 0;
                    CommitPlan(plan.Value, true);
                    return true;
                }

                // docs/invariants.md#stand-still-when-door-blocked
                if (NavGraph.LastPathBlockedByDoor)
                {
                    LogDoorBlockedRoute();
                    wanderIdleUntil = Time.time + WanderRetryDelay;
                    return false;
                }
            }

            wanderIdleUntil = Time.time + WanderRetryDelay;
            // Every pick turned down by `avoid` is a choice, not a stranded NPC.
            if (planned > 0 || avoid == null) StepOffAfterWanderFailure();

            return false;
        }

        /// <summary>
        /// Walks back toward the head of the leg just abandoned - the one place the route is
        /// known to resume from. False when a step-off is already running.
        /// docs/invariants.md#step-off-applies-in-every-mode
        /// </summary>
        private bool TryStepOffTowardLegHead()
        {
            if (Time.time < followStepOffUntil) return false;

            Vector3 direction = navPlan is { } plan && navPathIndex >= 1 && navPathIndex < plan.Count
                ? plan[navPathIndex - 1] - transform.position
                : Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * transform.forward;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f) direction = transform.forward;

            followStepOffTarget = transform.position + direction.normalized * 1.5f;
            followStepOffUntil = Time.time + 1.2f;
            return true;
        }

        /// <summary>
        /// Nothing in the graph is reachable from here - most often because the NPC
        /// climbed onto furniture. Walk somewhere, anywhere, and re-plan from there.
        /// docs/invariants.md#step-off-applies-in-every-mode
        /// </summary>
        private void StepOffAfterWanderFailure()
        {
            wanderPickFailures++;
            if (wanderPickFailures < 3 || Time.time < followStepOffUntil) return;

            wanderPickFailures = 0;
            Vector3 dir = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
            followStepOffTarget = transform.position + dir * Random.Range(1.5f, 2.5f);
            followStepOffUntil = Time.time + 1.2f;
            NpcLog.Log.LogInfo("[ai] No reachable wander targets - stepping off to re-plan");
        }

        /// <summary>
        /// NPCs walk through each other, so two that stop at the same spot would stand inside each
        /// other: the one that ranks after steps aside, never both. docs/invariants.md#npcs-never-block-each-other
        /// </summary>
        private void TrySpaceOut()
        {
            if (Time.time < spaceOutAt || Time.time < followStepOffUntil) return;
            if (!brain.Activity.SpaceOut) return;

            foreach (INpc npc in NpcRegistry.All)
            {
                if (NpcRegistry.IsGone(npc) || npc is not NpcAgent other || other == this || other.IsDead ||
                    !other.RanksBefore(this) || !other.isActiveAndEnabled)
                {
                    continue;
                }

                Vector3 away = transform.position - other.transform.position;
                if (Mathf.Abs(away.y) > SpacingSameDeck) continue;

                away.y = 0f;
                if (away.sqrMagnitude > NpcSpacing * NpcSpacing) continue;

                if (away.sqrMagnitude < 0.0001f) away = transform.right;
                followStepOffTarget = transform.position + away.normalized * SpacingStep;
                followStepOffUntil = Time.time + SpacingStep / Mathf.Max(0.5f, MoveSpeed);
                spaceOutAt = Time.time + SpacingCooldown;
                if (NpcLog.Level >= 2) NpcLog.Log.LogInfo("[ai] Stepping aside for " + other.Name);
                return;
            }
        }

        /// <summary>
        /// The plan's waypoints one after the other, straight at each, without the stair and deck rules
        /// of <see cref="AdvancePlan"/>: a goto's simple advance. Zero once it steps past a waypoint.
        /// </summary>
        public Vector3 SimpleAdvance(out bool wantMove)
        {
            wantMove = false;
            if (!HasPlanLeft) return Vector3.zero;

            // HasPlanLeft checked the plan.
            Vector3 waypoint = navPlan!.Value[navPathIndex];
            Vector3 toWaypoint = waypoint - transform.position;
            toWaypoint.y = 0f;

            if (toWaypoint.sqrMagnitude < 0.2025f)
            {
                navPathIndex++;
                return Vector3.zero;
            }

            wantMove = true;
            currentMoveTarget = waypoint;
            hasMoveTarget = true;
            return toWaypoint.normalized * MoveSpeed;
        }

        /// <summary>
        /// Abandons the waypoint underway. A walk that runs out this way did not arrive:
        /// docs/invariants.md#a-skipped-waypoint-is-not-an-arrival
        /// </summary>
        public void SkipWaypoint(string why)
        {
            routeSkippedWaypoint = true;
            if (NpcLog.Level >= 1 && navPlan is { } plan && navPathIndex < plan.Count)
            {
                NpcLog.Log.LogInfo(
                    $"[ai] Skipping waypoint {navPathIndex + 1}/{plan.Count} at {plan[navPathIndex]:0.0} - {why}");
            }
            navPathIndex++;
        }
    }
}
