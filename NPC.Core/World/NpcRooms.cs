using System.Collections.Generic;

namespace NPC.Core.World
{
    /// <summary>
    /// A reason of one mod's to keep rooms loaded that no NPC stands in, such as a room its NPCs work in.
    /// Asked on every switch-off, by the game or an NPC: keep it cheap.
    /// </summary>
    public interface IRoomKeeper
    {
        /// <summary>
        /// Why `room` must stay loaded, for the log, or null when this keeper does not need it.
        /// </summary>
        string? ReasonToKeep(Room room);
    }

    /// <summary>
    /// Rooms the mods keep loaded. One Room.SetContentEnabled patch refuses every switch-off a keeper
    /// objects to, the game's own included. docs/invariants.md#a-kept-room-stays-loaded
    /// </summary>
    public static class NpcRooms
    {
        private static readonly List<IRoomKeeper> Keepers = [];
        private static readonly HashSet<int> Logged = [];

        public static void AddKeeper(IRoomKeeper keeper)
        {
            if (keeper != null && !Keepers.Contains(keeper)) Keepers.Add(keeper);
        }

        public static void RemoveKeeper(IRoomKeeper keeper) => Keepers.Remove(keeper);

        /// <summary>
        /// The first keeper's reason to keep `room` loaded, or null when none has one.
        /// </summary>
        public static string? ReasonToKeep(Room room)
        {
            if (room == null) return null;

            foreach (IRoomKeeper keeper in Keepers)
            {
                if (keeper.ReasonToKeep(room) is { } reason) return reason;
            }
            return null;
        }

        /// <summary>
        /// From the SetContentEnabled prefix: false refuses a switch-off.
        /// </summary>
        internal static bool AllowSwitch(Room room, bool state)
        {
            if (state || Keepers.Count == 0 || room == null) return true;

            string? reason = ReasonToKeep(room);
            if (reason == null) return true;

            if (Logged.Add(room.GetInstanceID()))
            {
                NpcLog.Log.LogInfo("[world] Keeping room '" + room.gameObject.name + "' loaded: " + reason +
                                   " (logged once per room)");
            }
            return false;
        }
    }
}
