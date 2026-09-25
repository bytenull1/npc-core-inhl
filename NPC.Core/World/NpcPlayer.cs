using System.Collections.Generic;
using FMODUnity;
using Space;
using UnityEngine;

namespace NPC.Core.World
{
    /// <summary>
    /// The player as NPCs see it: where it is, what it sounds like, and the colliders every NPC body
    /// passes through. docs/invariants.md#npcs-never-block-each-other
    /// </summary>
    public static class NpcPlayer
    {
        private static Player? _controllerOwner;
        private static CharacterController? _controller;
        private static readonly List<Collider> PlayerColliders = [];

        /// <summary>
        /// The player, or null outside a game scene.
        /// </summary>
        public static Player? Pilot
        {
            get
            {
                GameManager gm = GameManager.Instance;
                return gm != null && gm.PlayerShip != null ? gm.PlayerShip.Pilot : null;
            }
        }

        /// <summary>
        /// The player's CharacterController, or null outside a game scene.
        /// </summary>
        public static CharacterController? Controller
        {
            get
            {
                Player? pilot = Pilot;
                if (pilot == null) return null;

                if (pilot != _controllerOwner || _controller == null)
                {
                    _controllerOwner = pilot;
                    _controller = pilot.GetComponent<CharacterController>();
                }
                return _controller;
            }
        }

        /// <summary>
        /// The player's footstep events (0 ship floor, 1 station floor), for an NPC that should sound like
        /// the player: pass them as <see cref="Agents.NpcBody.FootstepEvents"/>. Null before there is a player.
        /// docs/game-model.md#footstep-sounds
        /// </summary>
        public static EventReference[]? FootstepEvents()
        {
            Player? pilot = Pilot;
            if (pilot == null || pilot.Controller == null) return null;

            return GameInternals.CameraAnimatorAccess.GetFootstepEvents(pilot.Controller.CameraAnimator);
        }

        /// <summary>
        /// The player's controller and head/foot triggers ignore this NPC's controller and solid parts.
        /// Returns how many pairs were not ignored yet.
        /// </summary>
        internal static int IgnoreNpc(INpc npc)
        {
            if (!CollectPlayerColliders()) return 0;

            int ignored = 0;
            foreach (Collider player in PlayerColliders)
            {
                ignored += NpcRegistry.Ignore(player, npc.Controller);
                foreach (Collider part in npc.SolidParts()) ignored += NpcRegistry.Ignore(player, part);
            }
            return ignored;
        }

        private static bool CollectPlayerColliders()
        {
            PlayerColliders.Clear();
            CharacterController? cc = Controller;
            if (cc != null) PlayerColliders.Add(cc);

            Player? pilot = Pilot;
            if (pilot != null && pilot.TryGetComponent(out PlayerController controller))
            {
                AddTrigger(GameInternals.PlayerControllerAccess.GetHeadTrigger(controller));
                AddTrigger(GameInternals.PlayerControllerAccess.GetFootTrigger(controller));
            }
            return PlayerColliders.Count > 0;
        }

        private static void AddTrigger(HeadTrigger? trigger)
        {
            if (trigger != null && trigger.TryGetComponent(out Collider collider)) PlayerColliders.Add(collider);
        }
    }
}
