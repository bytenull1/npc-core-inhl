using NPC.Core.World;
using UnityEngine;

namespace NPC.Core.Agents
{
    /// <summary>
    /// The NPC's side of items: its hands, putting down before a save, and which vessel a candidate
    /// is on. docs/agent.md#7-hands
    /// </summary>
    public sealed partial class NpcAgent
    {
        public NpcHands Hands { get; private set; } = null!; // created in Attach, before the first frame

        private void LateUpdate()
        {
            using NpcRegistry.ActingScope _ = NpcRegistry.Acting(this);
            Hands.Follow(MoveSpeed);
        }

        /// <summary>
        /// Before the game writes the save: docs/invariants.md#a-carried-item-is-put-down-before-a-save
        /// </summary>
        private void OnGameSaving()
        {
            using NpcRegistry.ActingScope _ = NpcRegistry.Acting(this);
            Hands.Drop("the game is saving");
        }

        /// <summary>
        /// Whether a candidate belongs to the vessel the NPC is riding. Wide search radii only stay
        /// safe with this: at 30 m a docked station is well in range.
        /// A non-answer allows it - docs/invariants.md#a-missing-floor-is-a-last-resort-not-an-answer.
        /// </summary>
        public bool OnMyVessel(Transform what)
        {
            if (CurrentOwner == null || what == null) return true;

            // The object's own ancestry first; only a thing the game has not re-owned needs a probe.
            string? owner = NpcVessels.OwnerOfTransform(what);
            if (owner == null) NpcVessels.FloorOwner(what.position, out owner, out _);

            return owner == null || owner == CurrentOwner;
        }
    }
}
