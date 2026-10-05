using NPC.Core.Navigation;
using UnityEngine;
using static NPC.Core.Agents.ReachTask;

namespace NPC.Core.Agents
{
    /// <summary>
    /// A walk that ends by doing something at an object: plan to the nearest node with a probed straight
    /// walk to a stand point, then walk it into reach. docs/agent.md#6-walking-into-reach
    /// </summary>
    public sealed partial class NpcAgent
    {
        private const int ReachMaxReplans = 2;
        /// <summary>
        /// A node further than this from the target is not considered for the last stretch.
        /// </summary>
        private const float ReachNodeRadius = 10f;
        private const float ReachEyeHeight = 1.5f;
        private const float ReachApproachTimeout = 8f;
        private const float ReachGiveUpDelay = 60f;

        /// <summary>
        /// Plans the walk into reach. Null with the plan, or why there is none.
        /// </summary>
        public string? PlanReach(ReachTask task, out NavPath plan)
        {
            NavPath chosen = default;
            Vector3 start = FloorUnderNpc();
            bool hasApproach = false, blockedByDoor = false;
            bool reachable = FindReachNode(task, node =>
            {
                hasApproach = true;
                NavPath? found = NavGraph.FindPath(start, node, mayGoOutside: task.MayGoOutside);
                blockedByDoor |= NavGraph.LastPathBlockedByDoor;
                if (found is not { Count: > 0 } route || (route[route.Count - 1] - node).sqrMagnitude > .25f) return false;
                chosen = route;
                return true;
            });
            plan = chosen;
            if (reachable) return null;
            if (!hasApproach) return "no nav node near it has a clear walk to it";
            return blockedByDoor ? "a door I cannot open is in the way" : "there is no path to it";
        }

        /// <summary>
        /// The nearest active node that can walk straight to a stand point in front of the target
        /// and see the target from there. Fills the task's Node and StandPoint. The graph then routes
        /// to that node; only the last stretch is a straight walk, and it has been probed.
        /// </summary>
        public static bool FindReachNode(ReachTask task) => FindReachNode(task, null);

        private static bool FindReachNode(ReachTask task, System.Func<Vector3, bool>? reachable)
        {
            Vector3 targetPos = task.TargetPoint;
            NavGraph.CollectActiveNodes(ReachNodeBuffer);

            float DistanceSq(Vector3 node)
            {
                Vector3 delta = targetPos - node;
                delta.y = 0f;
                return delta.sqrMagnitude;
            }
            ReachNodeBuffer.Sort((a, b) => DistanceSq(a).CompareTo(DistanceSq(b)));
            foreach (Vector3 node in ReachNodeBuffer)
            {
                // An Outdoor node is not a stand point for a walk that may not go outside.
                if (!task.MayGoOutside && NavGraph.TypeAt(node) == NodeType.Outdoor) continue;

                Vector3 toTarget = targetPos - node;
                toTarget.y = 0f;
                float flatSq = toTarget.sqrMagnitude;
                if (flatSq >= ReachNodeRadius * ReachNodeRadius || flatSq < 0.0001f) continue;

                float nodeFloor = NavGraph.FloorAt(node);
                float rise = targetPos.y - nodeFloor;
                if (rise < -task.ReachBelow || rise > ReachHeight) continue;

                Vector3 back = -toTarget / Mathf.Sqrt(flatSq);
                foreach (float standOff in task.StandOffs)
                {
                    Vector3 stand = new(targetPos.x + back.x * standOff, nodeFloor, targetPos.z + back.z * standOff);
                    if (!task.StandAllowed(stand)) continue;
                    if (!NavProbe.WalkLos(node, stand, FollowSameLevelDeltaY)) continue;

                    Vector3 eye = new(stand.x, nodeFloor + ReachEyeHeight, stand.z);
                    if (!NavProbe.CanSee(eye, targetPos, task.Own, out _)) continue;
                    if (reachable != null && !reachable(node)) break;

                    task.Node = node;
                    task.StandPoint = stand;
                    return true;
                }
            }
            return false;
        }

        public bool InReach(ReachTask task)
        {
            Vector3 targetPos = task.TargetPoint;
            Vector3 toTarget = targetPos - transform.position;
            float floorY = FloorUnderNpc().y;
            float rise = targetPos.y - floorY;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > task.Reach * task.Reach || rise < -task.ReachBelow || rise > ReachHeight) return false;

            if (!task.StandAllowed(transform.position)) return false;
            // Not through a wall.
            Vector3 eye = new(transform.position.x, floorY + ReachEyeHeight, transform.position.z);
            return NavProbe.CanSee(eye, targetPos, task.Own, out _);
        }

        /// <summary>
        /// The last steps into reach. False means the caller is done this frame: still walking
        /// (`move` is the step), or the task ended.
        /// </summary>
        public bool StepIntoReach(ReachTask task, out Vector3 move, out bool wantMove)
        {
            move = Vector3.zero;
            wantMove = false;
            if (InReach(task)) return true;

            if (task.ApproachSince < 0f && !ArrivedAtReachNode(task)) return false;

            if (task.ApproachSince < 0f) task.ApproachSince = Time.time;
            if (Time.time - task.ApproachSince > ReachApproachTimeout)
            {
                task.Defer(ReachGiveUpDelay);
                string why = $"could not get within reach of {task.Name} " +
                             $"({Vector3.Distance(transform.position, task.TargetPoint):0.0}m)";
                NpcLog.Log.LogInfo($"[ai] Could not get within reach of {task.Name} " +
                    $"({Vector3.Distance(transform.position, task.TargetPoint):0.0}m) - giving up, retrying in {ReachGiveUpDelay:0}s");
                if (!task.Recover(why)) brain.OnWalkAbandoned(why);

                return false;
            }

            // The stand point first, walked from the node; then the target.
            Vector3 toStand = task.StandPoint - transform.position;
            toStand.y = 0f;
            Vector3 target = toStand.sqrMagnitude > ReachStandArrival * ReachStandArrival ? task.StandPoint : task.TargetPoint;
            Vector3 toTarget = target - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f) return false;

            wantMove = true;
            currentMoveTarget = target;
            hasMoveTarget = true;
            move = toTarget.normalized * WalkSpeed;
            return false;
        }

        /// <summary>
        /// The straight stretch starts at the node only. A plan that ran out short of it (stuck
        /// recovery skips waypoints) is planned again, or the walk would cut through walls.
        /// </summary>
        private bool ArrivedAtReachNode(ReachTask task)
        {
            Vector3 toNode = task.Node - transform.position;
            toNode.y = 0f;
            if (toNode.sqrMagnitude <= ReachNodeArrival * ReachNodeArrival) return true;

            NavPath? plan = task.Replans < ReachMaxReplans
                ? NavGraph.FindPath(FloorUnderNpc(), task.Node, mayGoOutside: task.MayGoOutside)
                : null;
            task.Replans++;
            if (plan is { Count: > 0 } route && (route[route.Count - 1] - task.Node).sqrMagnitude <= .25f)
            {
                NpcLog.Log.LogInfo($"[ai] Route to {task.Name} ended {toNode.magnitude:0.0}m short of node " +
                                   $"{task.Node:0.0} - planning again ({task.Replans} of {ReachMaxReplans})");
                CommitPlan(plan.Value, false);
                return false;
            }

            task.Defer(ReachReplanDelay);
            NpcLog.Log.LogInfo($"[ai] Could not get back to node {task.Node:0.0} for {task.Name} - " +
                               $"retrying in {ReachReplanDelay:0}s");
            string why = $"could not get back to the node for {task.Name}";
            if (!task.Recover(why)) brain.OnWalkAbandoned(why);

            return false;
        }
    }
}
