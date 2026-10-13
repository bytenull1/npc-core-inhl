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
    public sealed class Collider { public string name = ""; }
    public static class Mathf { public static float Sqrt(float n) => MathF.Sqrt(n); }
    public static class Time { public static float time; }
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
        public static readonly HashSet<float> Outdoor = [];
        public static readonly Dictionary<float, float> Floors = [];
        public static readonly Dictionary<float, Vector3[]?> Paths = [];
        public readonly struct ActiveNode(int id, Vector3 world)
        {
            public readonly Vector3 World = world;
            public int Id => id;
            public NodeType Type => Outdoor.Contains(World.x) ? NodeType.Outdoor : NodeType.Ground;
            public float FloorY => Floors.TryGetValue(World.x, out float floor) ? floor : 0;
        }
        public static void CollectActiveNodes(List<ActiveNode> nodes)
        {
            nodes.Clear();
            for (int i = 0; i < Nodes.Length; i++) nodes.Add(new ActiveNode(i, Nodes[i]));
        }
        public static NavPath? FindPath(Vector3 from, Vector3 to, bool mayGoOutside)
        {
            Searches++;
            LastPathBlockedByDoor = BlockedByDoor.Contains(to.x);
            return Paths.TryGetValue(to.x, out Vector3[]? path) && path != null ? new NavPath(path) : null;
        }
    }
    public static class NavProbe
    {
        // Which straight walks and sight lines are clear; every one is by default.
        public static Func<Vector3, Vector3, bool> Walk = (from, to) => true;
        public static Func<Vector3, Vector3, bool> Sight = (eye, target) => true;
        public static bool WalkLos(Vector3 from, Vector3 to, float tolerance) => Walk(from, to);
        public static Collider? SightBlocker;
        public static float SightBlockY;
        public static bool CanSee(Vector3 from, Vector3 to, Transform? own, out Gate? shut) { shut = null; return Sight(from, to); }
    }
}
public sealed class Gate { public string name = ""; }
namespace NPC.Core.World
{
    public static class SellPens
    {
        public const float SellStationClearance = 1;
        public static Func<UnityEngine.Vector3, bool> Pen = point => false;
        public static bool InAFencedPen(UnityEngine.Vector3 point, object? ignored, float clearance) => Pen(point);
    }
}
namespace NPC.Core.Agents
{
    using UnityEngine;
    using NPC.Core.Navigation;
    public sealed partial class NpcAgent
    {
        private static readonly List<NavGraph.ActiveNode> ReachNodeBuffer = [];
        private const float FollowSameLevelDeltaY = .5f, ReachNodeArrival = 1.2f, ReachStandArrival = .3f;
        public readonly Transform transform = new();
        private Vector3 currentMoveTarget;
        private bool hasMoveTarget;
        private readonly Brain brain = new();
        public Brain Mind => brain;
        public int Commits;
        public bool LastCommitFresh;
        public Vector3 CurrentTarget => hasMoveTarget ? currentMoveTarget : Vector3.zero;
        public float WalkSpeed => 3.5f;
        private Vector3 FloorUnderNpc() => transform.position;
        private void CommitPlan(NavPath plan, bool fresh)
        {
            Commits++;
            LastCommitFresh = fresh;
        }
    }
    public sealed class Brain
    {
        public string? Abandoned;
        public void OnWalkAbandoned(string why) => Abandoned = why;
    }
    public static class NpcLog
    {
        public static int Level => 2;
        public static readonly Logger Log = new();
        public sealed class Logger
        {
            public string? Last;
            public void LogInfo(string message) => Last = message;
        }
    }
}
