namespace NPC.Core.Agents
{
    /// <summary>
    /// Whether a no-progress window closed in on the move target. A target that stayed put is measured
    /// from the distance at its last real progress, so bouncing off a door is not progress.
    /// docs/invariants.md#progress-is-measured-to-one-target
    /// </summary>
    internal static class GoalProgress
    {
        internal const float MinClose = 0.35f;
        internal const float Unset = float.MaxValue;

        /// <summary>
        /// `last` is the previous window's distance, `anchor` the one at the last progress (Unset to start).
        /// A target that moved is judged on this window alone and re-anchors.
        /// </summary>
        internal static bool Closed(ref float anchor, float last, float dist, bool targetChanged)
        {
            if (targetChanged || anchor == Unset)
            {
                anchor = dist;
                return last == Unset || last - dist > MinClose;
            }
            if (anchor - dist <= MinClose) return false;

            anchor = dist;
            return true;
        }
    }
}
