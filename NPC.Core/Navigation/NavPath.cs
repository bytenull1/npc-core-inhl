using System.Collections.Generic;
using UnityEngine;

namespace NPC.Core.Navigation
{
    /// <summary>
    /// A planned route in world space. WaypointIsForced[i]: the edge arriving at i is a graph
    /// link (docs/invariants.md#links-have-no-los). WaypointFloorY[i]: the deck under i as the
    /// search measured it, never the marker's Y (#floor-to-floor).
    /// </summary>
    public readonly record struct NavPath(IReadOnlyList<Vector3> Waypoints, IReadOnlyList<bool> WaypointIsForced,
        IReadOnlyList<float> WaypointFloorY)
    {
        public int Count => Waypoints?.Count ?? 0;
        public Vector3 this[int i] => Waypoints[i];
        public bool IsForced(int i) => WaypointIsForced != null && i < WaypointIsForced.Count && WaypointIsForced[i];
        public float FloorY(int i) => WaypointFloorY[i];
    }
}
