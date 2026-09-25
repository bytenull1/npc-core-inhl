using FMODUnity;
using UnityEngine;

namespace NPC.Core.Agents
{
    /// <summary>
    /// Who an agent is: its mod, a name unique in that mod, and a number that staggers its work and settles
    /// who steps aside. docs/agent.md#1-attaching-an-agent
    /// </summary>
    public sealed record NpcIdentity(string ModName, string Name, int Number);

    /// <summary>
    /// The parts of a body the mod built that the agent drives. The root needs a CharacterController.
    /// </summary>
    public sealed class NpcBody
    {
        /// <summary>
        /// Shown and made physical on death; parented to the body until then.
        /// </summary>
        public GameObject? Ragdoll { get; init; }

        /// <summary>
        /// The ragdoll's main body, pushed on death.
        /// </summary>
        public Rigidbody? RagdollBody { get; init; }

        /// <summary>
        /// Hidden on death.
        /// </summary>
        public GameObject? AnimatedModel { get; init; }

        /// <summary>
        /// Solid for thrown items, ignored by every controller: docs/invariants.md#npcs-never-block-each-other
        /// </summary>
        public Collider? ItemBlocker { get; init; }

        /// <summary>
        /// Step sounds: 0 on a ship floor, 1 on a station floor. None: a silent walker.
        /// <see cref="World.NpcPlayer.FootstepEvents"/> gives the player's. docs/game-model.md#footstep-sounds
        /// </summary>
        public EventReference[]? FootstepEvents { get; init; }
    }
}
