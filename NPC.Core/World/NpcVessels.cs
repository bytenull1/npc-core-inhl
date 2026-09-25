using NPC.Core.Navigation;
using UnityEngine;

namespace NPC.Core.World
{
    /// <summary>
    /// Which vessel the floor under a point belongs to. Tri-state on purpose:
    /// docs/invariants.md#aboard-is-answered-by-the-floor
    /// </summary>
    public enum FloorOwnership
    {
        Unknown = 0,
        PlayerShip = 1,
        Elsewhere = 2
    }

    /// <summary>
    /// Vessels as an NPC stands on them: whose floor this is, and which transform something standing on it
    /// has to ride. The game moves the world, not the ship:
    /// docs/game-model.md#2a-the-ship-never-moves---the-world-moves-around-it
    /// </summary>
    public static class NpcVessels
    {
        /// <summary>
        /// Which vessel owns whatever is underfoot (NavGraph.TryVesselOf). Far more reliable than the
        /// game's tracked room. docs/game-model.md
        /// </summary>
        public static FloorOwnership FloorOwner(Vector3 worldPos) => FloorOwner(worldPos, out _, out _);

        /// <summary>
        /// The same answer, plus which vessel it is and the transform anything standing on that floor has
        /// to ride. `owner` is null only when no floor was found at all; an `anchor` of null means the
        /// player ship, which never moves. The probe goes through NavProbe so this cannot answer
        /// differently from the height probe: docs/invariants.md#one-probe-basis
        /// </summary>
        public static FloorOwnership FloorOwner(Vector3 worldPos, out string? owner, out Transform? anchor)
        {
            owner = null;
            anchor = null;
            GameManager gm = GameManager.Instance;
            SpaceShip? ship = gm != null ? gm.PlayerShip : null;
            if (ship == null) return FloorOwnership.Unknown;

            if (!NavProbe.TryFloorCollider(worldPos, out Collider? floor) || floor == null) return FloorOwnership.Unknown;

            if (NavGraph.TryVesselOf(floor.transform, out SpaceShip? vessel, out SpaceObject? spaceObject))
            {
                if (vessel != null)
                {
                    bool mine = vessel == ship;
                    owner = mine ? NavGraph.ShipOwner : vessel.gameObject.name;
                    anchor = mine ? null : vessel.transform;
                    return mine ? FloorOwnership.PlayerShip : FloorOwnership.Elsewhere;
                }
                // TryVesselOf returned true without a ship, so it found a SpaceObject.
                owner = spaceObject!.gameObject.name;
                // The container the game deactivates when the station optimizes, so an NPC parked there
                // is frozen and restored by the game's own lifecycle.
                anchor = InteriorOf(spaceObject);
                return FloorOwnership.Elsewhere;
            }

            // Real floor, no vessel above it: world geometry. Not a vessel we can name, so ownership
            // stays Unknown - but it still rides the world container.
            owner = NavGraph.WorldOwner;
            // ship is only set when gm is.
            anchor = gm!.WorldObjects;
            return FloorOwnership.Unknown;
        }

        /// <summary>
        /// Which vessel a scene object belongs to, by the same rule the floor probe uses. Null when it
        /// belongs to no vessel at all.
        /// </summary>
        public static string? OwnerOfTransform(Transform start)
        {
            if (!NavGraph.TryVesselOf(start, out SpaceShip? vessel, out SpaceObject? spaceObject)) return null;

            // TryVesselOf returned true without a ship, so it found a SpaceObject.
            if (vessel == null) return spaceObject!.gameObject.name;

            SpaceShip? ship = GameManager.Instance != null ? GameManager.Instance.PlayerShip : null;
            return ship != null && vessel == ship ? NavGraph.ShipOwner : vessel.gameObject.name;
        }

        /// <summary>
        /// The transform an owner's occupants ride, or null for the player ship.
        /// </summary>
        public static Transform? AnchorForOwner(string? owner)
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || string.IsNullOrEmpty(owner) || owner == NavGraph.ShipOwner) return null;

            if (owner == NavGraph.WorldOwner) return gm.WorldObjects;

            foreach (SpaceObject spaceObject in Object.FindObjectsOfType<SpaceObject>())
            {
                if (spaceObject == null || spaceObject.gameObject.name != owner) continue;

                return InteriorOf(spaceObject);
            }
            return null;
        }

        /// <summary>
        /// Where a station keeps its interior: the container the game switches off when the station
        /// unloads, which is not under the station itself. Falls back to the object's own transform.
        /// docs/invariants.md#a-station-owns-its-interior-by-reference
        /// </summary>
        public static Transform InteriorOf(SpaceObject spaceObject)
        {
            Transform? contentParent = GameInternals.SpaceObjectAccess.GetContentParent(spaceObject);
            return contentParent != null ? contentParent : spaceObject.transform;
        }
    }
}
