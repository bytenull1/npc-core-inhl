using NPC.Core.World;
using UnityEngine;

namespace NPC.Core.Agents
{
    /// <summary>
    /// Something an NPC walks up to and uses: route to the nearest node with a probed straight walk to a stand
    /// point, then into reach. NpcAgent.Reach.cs does the walking. docs/agent.md#6-walking-into-reach
    /// </summary>
    public abstract class ReachTask(Vector3 targetPoint, Transform own)
    {
        protected const float ReachDist = 1.6f;
        /// <summary>
        /// How far above the NPC's floor a target may be and still be reached.
        /// </summary>
        public const float ReachHeight = 2f;
        /// <summary>
        /// Stand points tried in front of the target, nearest first, all within ReachDist.
        /// </summary>
        protected static readonly float[] ReachStandOffs = [0.8f, 1.1f, 1.4f];
        /// <summary>
        /// After a plan that failed: often the NPC stood somewhere transient, like on furniture.
        /// </summary>
        public const float ReachReplanDelay = 5f;

        /// <summary>
        /// The point that must be in reach and in sight; `Own` is the object whose colliders sight ignores.
        /// </summary>
        public readonly Vector3 TargetPoint = targetPoint;
        public readonly Transform Own = own;
        internal float ApproachSince = -1f;
        /// <summary>
        /// The node the route ends at, and the floor point beyond it the object is used from.
        /// </summary>
        public Vector3 Node;
        public Vector3 StandPoint;
        internal int Replans;

        /// <summary>
        /// Flat distance the target is used from, and the stand points tried, nearest first, within it.
        /// </summary>
        public virtual float Reach => ReachDist;
        public virtual float[] StandOffs => ReachStandOffs;
        /// <summary>
        /// How far below the NPC's floor the target point may sit: an item on the floor is barely above it.
        /// </summary>
        public virtual float ReachBelow => 0f;

        /// <summary>
        /// Whether the NPC may stand at `point` to use the target: a sell station is not used from
        /// under its gate, and nothing is done from inside one's fences.
        /// docs/invariants.md#a-fenced-sell-station-is-not-somewhere-to-stand
        /// </summary>
        public virtual bool StandAllowed(Vector3 point) => !SellPens.InAFencedPen(point, null, SellPens.SellStationClearance);

        /// <summary>
        /// "the oxygen generator", "the fridge": for log lines.
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// Holds this kind of task off for `seconds` after a failure.
        /// </summary>
        public abstract void Defer(float seconds);

        /// <summary>
        /// The walk into reach gave up. True when the task took the failure over itself - a selling
        /// run goes on to the next box, or sells what is already loaded - and the caller must then
        /// leave the route alone.
        /// </summary>
        public virtual bool Recover(string why) => false;
    }
}
