using System.Collections.Generic;
using UnityEngine;

namespace NPC.Core.World
{
    /// <summary>
    /// What a sell station's fences enclose, with its item zone. At the ShipyardStation that is a pocket
    /// about 0.7 m deep behind a 0.8 m rail, which an NPC cannot climb: it gets in round the open end and
    /// then has to find its way back out. docs/invariants.md#a-fenced-sell-station-is-not-somewhere-to-stand
    /// </summary>
    public static class SellPens
    {
        /// <summary>
        /// No NPC stands within this of a station's item zone, flat: under the closing gate it would trip
        /// the AntiCrasher, and inside it a player dies.
        /// </summary>
        public const float SellStationClearance = 0.35f;
        /// <summary>
        /// How often the fenced footprints are measured again: a station's fences load with its room.
        /// </summary>
        private const float SellPenRefresh = 10f;
        /// <summary>
        /// How far below a pen's colliders a point still counts as in it: a stand point is a floor point,
        /// and the reach of an arm above it.
        /// </summary>
        private const float PenDepthBelow = 2f;

        private readonly struct Pen(SellStation station, Bounds bounds)
        {
            public readonly SellStation Station = station;
            public readonly Bounds Bounds = bounds;
        }

        private static readonly List<Pen> Pens = [];
        private static float _pensAt = -1f;

        /// <summary>
        /// Whether a room's content holds a sell station.
        /// </summary>
        public static bool HoldsSellStation(Room room) =>
            room.ContentParent.GetComponentInChildren<SellStation>(true) != null;

        /// <summary>
        /// Whether a point is inside a sell station's fences, `margin` wider all round. `except` is the
        /// station whose own work is being planned - that keeps the tighter item-zone rule.
        /// docs/invariants.md#a-fenced-sell-station-is-not-somewhere-to-stand
        /// </summary>
        public static bool InAFencedPen(Vector3 point, SellStation? except, float margin)
        {
            EnsurePens();
            foreach (Pen pen in Pens)
            {
                if (pen.Station == null || pen.Station == except) continue;

                Bounds bounds = pen.Bounds;
                if (point.x > bounds.min.x - margin && point.x < bounds.max.x + margin &&
                    point.z > bounds.min.z - margin && point.z < bounds.max.z + margin &&
                    point.y > bounds.min.y - PenDepthBelow && point.y < bounds.max.y) return true;
            }
            return false;
        }

        /// <summary>
        /// Rebuilt on a timer: a station's fences load and unload with its room.
        /// </summary>
        private static void EnsurePens()
        {
            if ((_pensAt > 0f && Time.time < _pensAt) || !SceneScan.MayRescan(_pensAt <= 0f)) return;

            _pensAt = Time.time + SellPenRefresh;
            Pens.Clear();
            foreach (SellStation station in Object.FindObjectsOfType<SellStation>())
            {
                Bounds pen = default;
                bool fenced = false;
                foreach (Collider collider in station.GetComponentsInChildren<Collider>())
                {
                    // By name: the fences are what make a station a pen rather than a counter,
                    // and nothing else about them tells them apart from the console's own mesh.
                    if (collider == null || collider.isTrigger || !collider.gameObject.name.StartsWith("Fence")) continue;

                    if (!fenced) pen = collider.bounds;
                    else pen.Encapsulate(collider.bounds);

                    fenced = true;
                }
                if (!fenced) continue;

                ItemDetector? detector = GameInternals.SellStationAccess.GetItemDetector(station);
                if (detector != null && detector.TryGetComponent(out BoxCollider zone)) pen.Encapsulate(zone.bounds);

                Pens.Add(new Pen(station, pen));
            }
        }
    }
}
