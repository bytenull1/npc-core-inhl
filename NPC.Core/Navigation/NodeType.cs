namespace NPC.Core.Navigation
{
    /// <summary>
    /// What a graph node stands on. A Stair node may be entered off-level, from its foot or head:
    /// docs/invariants.md#entry-seeds-on-own-deck
    /// </summary>
    public enum NodeType
    {
        Ground = 0,
        Stair = 1
    }
}
