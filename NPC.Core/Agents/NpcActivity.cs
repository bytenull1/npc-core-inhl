namespace NPC.Core.Agents
{
    /// <summary>
    /// What the agent does when a walk stalls: docs/agent.md#4-recovery
    /// </summary>
    public enum NpcRecovery
    {
        /// <summary>
        /// Nothing: standing, holding, or a walk the brain watches itself.
        /// </summary>
        None,

        /// <summary>
        /// Give up on the waypoint underway and go on to the next: <see cref="NpcAgent.SkipWaypoint"/>.
        /// </summary>
        SkipWaypoint,

        /// <summary>
        /// Drop the plan and stand for a moment before the next <see cref="NpcAgent.Wander"/> pick.
        /// </summary>
        DropPlanAndPause,

        /// <summary>
        /// Hand the stall to the brain: <see cref="INpcBrain.OnWalkAbandoned"/>.
        /// </summary>
        EndWalk,

        /// <summary>
        /// A <see cref="NpcAgent.Pursue"/>: bar the waypoint it cannot reach, replan, and step off toward the player.
        /// </summary>
        Pursuit
    }

    /// <summary>
    /// The kind of walk the brain is on, read live by the agent. docs/agent.md#4-recovery
    /// </summary>
    public readonly struct NpcActivity(NpcRecovery recovery, bool idleWatch, bool spaceOut, bool leavesDoorways)
    {
        public readonly NpcRecovery Recovery = recovery;

        /// <summary>
        /// Standing still above its own floor plane is a stall: docs/invariants.md#idle-above-the-floor-plane-is-a-stall
        /// </summary>
        public readonly bool IdleWatch = idleWatch;

        /// <summary>
        /// Standing inside another NPC, it steps aside: docs/invariants.md#npcs-never-block-each-other
        /// </summary>
        public readonly bool SpaceOut = spaceOut;

        /// <summary>
        /// Standing in a doorway it owes a close, it steps clear. A walk that goes on through it clears it anyway.
        /// </summary>
        public readonly bool LeavesDoorways = leavesDoorways;
    }
}
