using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace NPC.Core
{
    /// <summary>
    /// An NPC of any mod, as the others see it. Register it with <see cref="NpcRegistry"/> for as long as
    /// it exists: that is what lets NPCs of different mods pass through each other, leave alone what
    /// another is working on and keep each other's rooms loaded. docs/invariants.md#several-npc-mods
    /// </summary>
    public interface INpc
    {
        /// <summary>
        /// The mod it belongs to, as its log lines name it: "YourBuddy".
        /// </summary>
        string ModName { get; }

        /// <summary>
        /// Unique among its mod's NPCs: "Buddy 2". Also its log source, "YourBuddy:Buddy 2".
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Where its lines go while it acts: <see cref="NpcRegistry.LogSourceFor"/>.
        /// </summary>
        ManualLogSource LogSource { get; }

        Transform Transform { get; }

        /// <summary>
        /// Its body's controller, or null when it has none.
        /// </summary>
        CharacterController? Controller { get; }

        bool IsDead { get; }

        /// <summary>
        /// Loaded and running. False while parked with an unloaded station or ship: a parked NPC holds
        /// nothing loaded and is no one's obstacle.
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// Its own body, or its ragdoll once dead. Every probe treats it as a body, never as floor or wall.
        /// docs/invariants.md#npcs-never-block-each-other
        /// </summary>
        bool IsOwnBody(Transform t);

        /// <summary>
        /// Colliders another NPC's controller must not bump: an item blocker, the ragdoll once dead.
        /// </summary>
        IEnumerable<Collider> SolidParts();

        /// <summary>
        /// Whether what it is doing now is about `t`, so no other NPC takes it. Derived from its live
        /// state, never stored: docs/invariants.md#one-npc-per-target
        /// </summary>
        bool Holds(Transform t);

        /// <summary>
        /// Tracked in this room, which keeps it loaded: docs/invariants.md#rooms-stay-loaded-for-every-npc
        /// </summary>
        bool TracksRoom(Room room);
    }
}
