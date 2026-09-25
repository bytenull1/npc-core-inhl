namespace NPC.Core.World
{
    /// <summary>
    /// An NPC that climbs into the game's hiding spots. Optional beside <see cref="INpc"/>.
    /// </summary>
    public interface INpcHider
    {
        /// <summary>
        /// Inside `spot` now, getting out included: the game must not mount the player into it.
        /// </summary>
        bool Occupies(HidingSpot spot);
    }

    /// <summary>
    /// Hiding spots NPCs are in. An NPC is not a Player, so the game would mount you into a closet one is
    /// already in: the HidingSpot.Interact patch asks here first.
    /// </summary>
    public static class NpcHiding
    {
        /// <summary>
        /// A living NPC of any mod is inside `spot`.
        /// </summary>
        public static bool OccupiedByNpc(HidingSpot spot)
        {
            if (spot == null) return false;

            foreach (INpc npc in NpcRegistry.All)
            {
                if (npc is INpcHider hider && !NpcRegistry.IsGone(npc) && !npc.IsDead && hider.Occupies(spot)) return true;
            }
            return false;
        }
    }
}
