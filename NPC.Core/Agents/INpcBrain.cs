using Space;
using UnityEngine;

namespace NPC.Core.Agents
{
    /// <summary>
    /// The mod's side of an <see cref="NpcAgent"/>: it decides, the agent walks. The agent calls these from its
    /// own Update and slow phases, inside the NPC's acting scope. docs/agent.md#3-the-brain
    /// </summary>
    public interface INpcBrain
    {
        /// <summary>
        /// After the agent's own work in slow phase 0-3, each every ~0.24 s: docs/agent.md#slow-phases
        /// </summary>
        void SlowPhase(int phase, Player player);

        /// <summary>
        /// True when the brain moves the body itself this frame: `move` is applied as it is, with no mode
        /// step, doors or steering. A conversation holds still; climbing into a closet is a teleport.
        /// </summary>
        bool OverrideMovement(Player player, out Vector3 move, out bool wantMove);

        /// <summary>
        /// This frame's step: usually one of the agent's walks (Pursue, Wander, Stay, HeadAlongPlan, SimpleAdvance).
        /// </summary>
        Vector3 Steer(Player player, out bool wantMove);

        /// <summary>
        /// The last word on the step before doors and steering see it, such as not walking toward a threat.
        /// </summary>
        Vector3 Constrain(Vector3 desired, ref bool wantMove);

        /// <summary>
        /// Read live wherever the agent needs to know what kind of walk this is.
        /// </summary>
        NpcActivity Activity { get; }

        /// <summary>
        /// For the level-3 obstacle report.
        /// </summary>
        string ActivityName { get; }

        /// <summary>
        /// Standing still: which way to face, through <see cref="NpcAgent.FacePoint"/> or
        /// <see cref="NpcAgent.FacePlayer"/>. Doing nothing keeps the current facing.
        /// </summary>
        void TryIdleFacing(Player player);

        /// <summary>
        /// The walk under way cannot go on: an <see cref="NpcRecovery.EndWalk"/> recovery, or a
        /// <see cref="ReachTask"/> that gave up without recovering.
        /// </summary>
        void OnWalkAbandoned(string why);

        /// <summary>
        /// <see cref="INpc.Holds"/> for a living NPC.
        /// </summary>
        bool Holds(Transform t);

        /// <summary>
        /// Out of the monster's reach, such as shut in a closet: it is not caught.
        /// </summary>
        bool Sheltered { get; }

        /// <summary>
        /// Death, parking, a despawn or a ship rebuild: drop anything that holds the body in place now.
        /// </summary>
        void OnInterrupted(string why);

        /// <summary>
        /// After <see cref="NpcAgent.IsDead"/> turned true.
        /// </summary>
        void OnDied();

        /// <summary>
        /// The player ship was rebuilt around the NPC, before the agent drops its plan. True when this
        /// ended a walk to a world point of the old layout, for the log line.
        /// </summary>
        bool OnShipRebuilt();
    }
}
