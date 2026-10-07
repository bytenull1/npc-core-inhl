namespace NPC.Core.Agents
{
    internal static class GoalProgressWindow
    {
        // A changed target supplies a new baseline, not evidence of progress or failure.
        internal static int Evaluate(bool targetChanged, float distanceClosed) =>
            targetChanged ? 0 : distanceClosed > .35f ? 1 : -1;
    }
}
