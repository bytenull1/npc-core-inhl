using System;
using System.Collections.Generic;

// Deterministic graph/physics boundary; these checks do not simulate Unity collision geometry.
namespace UnityEngine
{
    public struct Vector3(float x, float y, float z)
    {
        public float x = x, y = y, z = z;
        public static Vector3 zero => new(0, 0, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector3 normalized => magnitude > 0 ? this / magnitude : zero;
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => zero - a;
        public static Vector3 operator *(Vector3 a, float n) => new(a.x * n, a.y * n, a.z * n);
        public static Vector3 operator /(Vector3 a, float n) => new(a.x / n, a.y / n, a.z / n);
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
    }
    public sealed class Transform { public Vector3 position = Vector3.zero; }
    public static class Mathf { public static float Sqrt(float n) => MathF.Sqrt(n); }
    public static class Time { public static float time => 0; }
}
namespace NPC.Core.Navigation
{
    using UnityEngine;
    public enum NodeType { Ground, Outdoor }
    public readonly struct NavPath(Vector3[] points)
    {
        public int Count => points.Length;
        public Vector3 this[int i] => points[i];
    }
    public static class NavGraph
    {
        public static Vector3[] Nodes = [];
        public static int Searches;
        public static bool LastPathBlockedByDoor;
        public static readonly HashSet<float> BlockedByDoor = [];
        public static readonly Dictionary<float, Vector3[]?> Paths = [];
        public static void CollectActiveNodes(List<Vector3> nodes) { nodes.Clear(); nodes.AddRange(Nodes); }
        public static NodeType TypeAt(Vector3 point) => NodeType.Ground;
        public static float FloorAt(Vector3 point) => 0;
        public static NavPath? FindPath(Vector3 from, Vector3 to, bool mayGoOutside)
        {
            Searches++;
            LastPathBlockedByDoor = BlockedByDoor.Contains(to.x);
            return Paths.TryGetValue(to.x, out Vector3[]? path) && path != null ? new NavPath(path) : null;
        }
    }
    public static class NavProbe
    {
        public static bool WalkLos(Vector3 from, Vector3 to, float tolerance) => true;
        public static bool CanSee(Vector3 from, Vector3 to, Transform own, out string why) { why = ""; return true; }
    }
}
namespace NPC.Core.World
{
    public static class SellPens
    {
        public const float SellStationClearance = 1;
        public static bool InAFencedPen(UnityEngine.Vector3 point, object? ignored, float clearance) => false;
    }
}
namespace NPC.Core.Agents
{
    using UnityEngine;
    using NPC.Core.Navigation;
    public sealed partial class NpcAgent
    {
        private static readonly List<Vector3> ReachNodeBuffer = [];
        private const float FollowSameLevelDeltaY = .5f, ReachNodeArrival = 1.2f, ReachStandArrival = .3f;
        public readonly Transform transform = new();
        private Vector3 currentMoveTarget;
        private bool hasMoveTarget;
        private readonly Brain brain = new();
        public Vector3 CurrentTarget => hasMoveTarget ? currentMoveTarget : Vector3.zero;
        public float WalkSpeed => 3.5f;
        private Vector3 FloorUnderNpc() => transform.position;
        private void CommitPlan(NavPath plan, bool fresh) { }
    }
    public sealed class Brain { public void OnWalkAbandoned(string why) { } }
    public static class NpcLog
    {
        public static readonly Logger Log = new();
        public sealed class Logger { public void LogInfo(string message) { } }
    }
}
