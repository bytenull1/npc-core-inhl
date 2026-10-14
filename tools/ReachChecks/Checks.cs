using System;
using System.Globalization;
using NPC.Core.Agents;
using NPC.Core.Navigation;
using NPC.Core.World;
using UnityEngine;
using Xunit;
using Xunit.Abstractions;

// One fact: the checks share static state and run in order.
public sealed class Checks(ITestOutputHelper output)
{
    [Fact]
    public void All()
    {
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        NpcAgent agent = new();
        NavGraph.Nodes = [new(9, 1, 0), new(7, 1, 0)];
        NavGraph.Paths[9] = [new(3, 1, 0)];
        NavGraph.Paths[7] = [new(7, 1, 0)];
        TestTask task = new();
        Check(agent.PlanReach(task, out NavPath plan) == null, "reachable alternative is found");
        Check(task.Node.x == 7 && plan[plan.Count - 1].x == 7, "nearest partial route cannot replace farther complete approach");
        NavGraph.Paths[7] = null;
        Check(agent.PlanReach(new TestTask(), out _) != null, "all partial or missing routes reject the job before walking");
        NavGraph.Paths[9] = [new(9, 1, 0)];
        Check(agent.PlanReach(new TestTask(), out plan) == null && plan[0].x == 9, "complete nearest route is accepted");
        NavGraph.Paths[9] = [new(9, -2, 0)];
        Check(agent.PlanReach(new TestTask(), out _) != null, "a different deck at the same horizontal point is not arrival");
        NavGraph.Paths[9] = [new(8.6f, 1, 0)];
        Check(agent.PlanReach(new TestTask(), out _) == null, "graph endpoint rounding within half a metre is accepted");
        NavGraph.Paths[9] = [];
        Check(agent.PlanReach(new TestTask(), out _) != null, "empty routes do not start a job");
        NavGraph.Nodes = [new(7, 1, 0), new(9, 1, 0)];
        NavGraph.Paths[7] = [new(7, 1, 0)];
        NavGraph.Paths[9] = [new(9, 1, 0)];
        NavGraph.Searches = 0;
        Check(agent.PlanReach(new TestTask(), out plan) == null && plan[0].x == 9 && NavGraph.Searches == 1,
            "unsorted candidates select the nearest complete approach with one search");
        NavGraph.Paths[9] = [new(3, 1, 0)];
        NavGraph.Searches = 0;
        Check(agent.PlanReach(new TestTask(), out plan) == null && plan[0].x == 7 && NavGraph.Searches == 2,
            "partial nearest route tries the next candidate");
        NavGraph.Paths[7] = null;
        Check(agent.PlanReach(new TestTask(), out _) == "there is no path to it", "missing routes retain their failure reason");
        NavGraph.BlockedByDoor.Add(9);
        Check(agent.PlanReach(new TestTask(), out _) == "a door I cannot open is in the way",
            "door failure survives later unsuccessful searches");
        NavGraph.Paths[7] = [new(7, 1, 0)];
        Check(agent.PlanReach(new TestTask(), out _) == null, "reachable alternative wins over earlier door failure");
        NavGraph.Nodes = [new(9, 1, 0), new(7, 1, 0)];
        NavGraph.Paths[9] = [new(9, 1, 0)];
        NavGraph.Outdoor.Add(9);
        Check(agent.PlanReach(new TestTask(), out plan) == null && plan[0].x == 7, "an outdoor node is not a stand point for an indoor errand");
        NavGraph.Outdoor.Clear();
        NavGraph.Nodes = [];
        Check(agent.PlanReach(new TestTask(), out _) == "no nav node near it has a clear walk to it",
            "missing approach is distinct from missing route and stale door failure");
        bool Near(float a, float b) => MathF.Abs(a - b) < 0.001f;
        void Reset()
        {
            NavGraph.BlockedByDoor.Clear();
            NavGraph.Outdoor.Clear();
            NavGraph.Floors.Clear();
            NavProbe.Walk = (from, to) => true;
            NavProbe.Sight = (eye, target) => true;
            SellPens.Pen = point => false;
            NavGraph.Nodes = [new(9, 1, 0), new(7, 1, 0)];
            NavGraph.Paths[9] = [new(9, 1, 0)];
            NavGraph.Paths[7] = [new(7, 1, 0)];
        }

        // FindReachNode: the target is at x 10, so node 9's stand points are x 9.2, 8.9 and 8.6.
        Reset();
        TestTask probe = new();
        Check(agent.PlanReach(probe, out _) == null && probe.Node.x == 9 && Near(probe.StandPoint.x, 9.2f) && probe.StandPoint.y == 0,
            "the nearest stand point in front of the target, on the node's floor");
        NavGraph.Floors[9] = -2f;
        Check(agent.PlanReach(probe = new(), out _) == null && probe.Node.x == 7 && Near(probe.StandPoint.x, 9.2f),
            "a target more than ReachHeight above the node's floor is off its deck; a farther node stands as close");
        NavGraph.Floors[9] = .5f;
        Check(agent.PlanReach(probe = new(), out _) == null && probe.Node.x == 9, "a target level with the node's floor is in reach");
        NavGraph.Floors[9] = -1.5f;
        Check(agent.PlanReach(probe = new(), out _) == null && probe.Node.x == 9 && probe.StandPoint.y == -1.5f,
            "a target exactly ReachHeight above the floor is in reach, stood on that floor");
        NavGraph.Floors[9] = .6f;
        Check(agent.PlanReach(probe = new(), out _) == null && probe.Node.x == 7, "a target below the node's floor is off its deck");
        Check(agent.PlanReach(probe = new(below: .2f), out _) == null && probe.Node.x == 9, "ReachBelow lets a task reach below the floor");
        NavGraph.Floors.Clear();
        SellPens.Pen = point => point.x > 9;
        Check(agent.PlanReach(probe = new(), out _) == null && Near(probe.StandPoint.x, 8.9f), "a stand point in a pen is passed over for the next");
        SellPens.Pen = point => false;
        NavProbe.Walk = (from, to) => to.x < 8.8f;
        Check(agent.PlanReach(probe = new(), out _) == null && probe.Node.x == 9 && Near(probe.StandPoint.x, 8.6f),
            "a stand point with no straight walk from the node is passed over");
        NavProbe.Walk = (from, to) => from.x != 9;
        Check(agent.PlanReach(probe = new(), out _) == null && probe.Node.x == 7, "a node with no walk to any stand point is passed over");
        NavProbe.Walk = (from, to) => true;
        NavProbe.Sight = (eye, target) => eye.x < 9 && eye.y == 1.5f && target.x == 10;
        Check(agent.PlanReach(probe = new(), out _) == null && probe.Node.x == 9 && Near(probe.StandPoint.x, 8.9f),
            "the target must be in sight from eye height above the stand point");
        NavProbe.Sight = (eye, target) => true;
        NavGraph.Outdoor.Add(9);
        Check(agent.PlanReach(probe = new(mayGoOutside: true), out _) == null && probe.Node.x == 9, "an outdoor node serves a walk that may go outside");
        NavGraph.Outdoor.Clear();
        NavGraph.Nodes = [new(10, 1, 0), new(9, 1, 0)];
        NavGraph.Paths[10] = [new(10, 1, 0)];
        Check(agent.PlanReach(probe = new(), out _) == null && probe.Node.x == 9, "a node under the target gives no direction to stand in and is skipped");
        NavGraph.Nodes = [new(-1, 1, 0)];
        NavGraph.Paths[-1] = [new(-1, 1, 0)];
        Check(agent.PlanReach(new TestTask(), out _) == "no nav node near it has a clear walk to it", "a node beyond the reach radius is not considered");
        NavGraph.Nodes = [new(.5f, 1, 0)];
        NavGraph.Paths[.5f] = [new(.5f, 1, 0)];
        Check(agent.PlanReach(new TestTask(), out _) == null, "a node within the reach radius is considered");
        NavGraph.Nodes = [new(0, 1, 0)];
        NavGraph.Paths[0] = [new(0, 1, 0)];
        Check(agent.PlanReach(new TestTask(), out _) != null, "a node exactly at the reach radius is not considered");
        NavGraph.Nodes = [new(10, 1, -3)];
        NavGraph.Paths[10] = [new(10, 1, -3)];
        Check(agent.PlanReach(probe = new(), out _) == null && Near(probe.StandPoint.z, -.8f) && Near(probe.StandPoint.x, 10),
            "stand points lie towards the node on both horizontal axes");

        // Two candidates both behind doors that will not open: the reason stays the door.
        Reset();
        NavGraph.Paths[9] = null;
        NavGraph.Paths[7] = null;
        NavGraph.BlockedByDoor.Add(9);
        NavGraph.BlockedByDoor.Add(7);
        Check(agent.PlanReach(new TestTask(), out _) == "a door I cannot open is in the way", "two door-blocked candidates still report the door");

        // The level-2 miss line counts every rejection: node 9 outdoor, 8 off its deck, 7's stand points one each.
        Reset();
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        NavGraph.Nodes = [new(9, 1, 0), new(8, 1, 0), new(7, 1, 0)];
        NavGraph.Outdoor.Add(9);
        NavGraph.Floors[8] = -2f;
        SellPens.Pen = point => point.x > 9.1f;
        NavProbe.Walk = (from, to) => !Near(to.x, 8.9f);
        NavProbe.Sight = (eye, target) => false;
        NavProbe.SightBlocker = new Collider { name = "Shelf" };
        NavProbe.SightBlockY = .75f;
        NpcLog.Log.Last = null;
        Check(agent.PlanReach(new TestTask(), out _) == "no nav node near it has a clear walk to it" && NpcLog.Log.Last is { } miss &&
            miss.Contains("3 node(s) within 10m - 1 outdoor, 1 off its deck; 3 stand point(s) - 1 not allowed, 1 no walk from the node, 1 no sight of it", StringComparison.Ordinal) &&
            miss.EndsWith("1 no sight of it; nearest #0 1.0m: outdoor; #1 2.0m: rise 2.50; #2 3.0m: pen walk sight(Shelf y0.75)", StringComparison.Ordinal),
            "the miss line names each rejection and the nearest nodes' verdicts");
        Check(agent.PlanReach(new TestTask(), out _) != null && NpcLog.Log.Last is { } again &&
            again.EndsWith("1 no sight of it; nearest #0 1.0m: outdoor; #1 2.0m: rise 2.50; #2 3.0m: pen walk sight(Shelf y0.75)", StringComparison.Ordinal),
            "each search starts its miss line afresh");
        NavProbe.SightBlocker = null;

        // InReach: within Reach of the target, on a floor it can reach from, allowed to stand, in sight.
        Reset();
        agent.transform.position = new(8.5f, 0, 0);
        Check(agent.InReach(new TestTask()), "in reach 1.5 m away");
        agent.transform.position = new(8.3f, 0, 0);
        Check(!agent.InReach(new TestTask()), "out of reach 1.7 m away");
        agent.transform.position = new(8, 0, 0);
        Check(agent.InReach(new TestTask(reach: 2)), "exactly at the task's reach is in reach");
        agent.transform.position = new(8.5f, .5f, 0);
        Check(agent.InReach(new TestTask()), "a target level with the floor is in reach");
        agent.transform.position = new(8.5f, -1.5f, 0);
        Check(agent.InReach(new TestTask()), "a target exactly ReachHeight above the floor is in reach");
        agent.transform.position = new(8.5f, 0, 0);
        NavProbe.Sight = (eye, target) => eye.y == 1.5f;
        Check(agent.InReach(new TestTask()), "sight is judged from eye height above the floor");
        NavProbe.Sight = (eye, target) => true;
        agent.transform.position = new(8.5f, -1.6f, 0);
        Check(!agent.InReach(new TestTask()), "a target more than ReachHeight above the floor is out of reach");
        agent.transform.position = new(8.5f, .6f, 0);
        Check(!agent.InReach(new TestTask()) && agent.InReach(new TestTask(below: .2f)), "below the floor only within ReachBelow");
        agent.transform.position = new(8.5f, 0, 0);
        SellPens.Pen = point => true;
        Check(!agent.InReach(new TestTask()), "not from a spot it may not stand on");
        SellPens.Pen = point => false;
        NavProbe.Sight = (eye, target) => false;
        Check(!agent.InReach(new TestTask()), "not through a wall");
        NavProbe.Sight = (eye, target) => true;

        // StepIntoReach and ArrivedAtReachNode.
        TestTask step = new();
        Check(agent.PlanReach(step, out _) == null && agent.StepIntoReach(step, out _, out bool wantMove) && !wantMove,
            "in reach: done, no step");
        agent.transform.position = new(3, 0, 0);
        Check(!agent.StepIntoReach(step, out _, out wantMove) && !wantMove && agent.Commits == 1 && step.Replans == 1 && step.Deferred < 0,
            "short of the node: plans to it again");
        Check(!agent.LastCommitFresh, "the replan continues the walk, not a fresh one");
        NavGraph.Paths[9] = [new(5, 1, 0)];
        Check(!agent.StepIntoReach(step, out _, out wantMove) && !wantMove && step.ApproachSince < 0 &&
            step.Deferred == ReachTask.ReachReplanDelay && step.Replans == 2 &&
            agent.Mind.Abandoned == "could not get back to the node for test item", "no route back to the node: deferred and abandoned");
        agent.Mind.Abandoned = null;
        step.Recovers = true;
        int searches = NavGraph.Searches;
        Check(!agent.StepIntoReach(step, out _, out _) && agent.Mind.Abandoned == null && NavGraph.Searches == searches,
            "replans spent: no search, and a task that recovers keeps the walk its own");
        Reset();
        step = new();
        agent.PlanReach(step, out _);
        agent.transform.position = new(8.2f, 0, 0);
        Time.time = 100;
        Check(!agent.StepIntoReach(step, out Vector3 move, out wantMove) && wantMove && step.ApproachSince == 100 &&
            Near(move.x, agent.WalkSpeed) && Near(agent.CurrentTarget.x, 9.2f), "at the node: walks to the stand point");
        TestTask wide = new();
        agent.PlanReach(wide, out _);
        agent.transform.position = new(7.9f, 0, 0);
        Check(!agent.StepIntoReach(wide, out _, out wantMove) && wantMove && wide.ApproachSince == 100, "1.1 m from the node counts as arrived");
        NavProbe.Sight = (eye, target) => false;
        agent.transform.position = new(9.1f, 0, 0);
        Check(!agent.StepIntoReach(step, out move, out wantMove) && wantMove && agent.CurrentTarget.x == 10 && move.x > 0,
            "at the stand point: walks on to the target");
        agent.transform.position = new(10, 0, 0);
        Check(!agent.StepIntoReach(step, out move, out wantMove) && wantMove && Near(agent.CurrentTarget.x, 9.2f) && move.x < 0,
            "past the stand point: walks back to it first");
        Time.time = 108;
        agent.transform.position = new(9.1f, 0, 0);
        Check(!agent.StepIntoReach(step, out _, out wantMove) && wantMove && step.Deferred < 0, "exactly eight seconds is not yet a timeout");
        Time.time = 108.5f;
        Check(!agent.StepIntoReach(step, out _, out _) && step.Deferred == 60 &&
            agent.Mind.Abandoned is { } why && why.StartsWith("could not get within reach of test item (", StringComparison.Ordinal),
            "eight seconds without reaching it: deferred a minute and abandoned");
        Reset();
        // The no-progress watch: windows of flat distance to the move target.
        float anchor = GoalProgress.Unset;
        Check(GoalProgress.Closed(ref anchor, GoalProgress.Unset, 5f, true) && anchor == 5f, "the first window counts and anchors");
        Check(GoalProgress.Closed(ref anchor, 5f, 4.5f, false) && anchor == 4.5f, "closing more than 0.35 m is progress");
        Check(!GoalProgress.Closed(ref anchor, 4.5f, 4.3f, false) && GoalProgress.Closed(ref anchor, 4.3f, 4.1f, false),
            "slow closing adds up from the anchor");
        anchor = 2.23f;
        bool bounced = false;
        for (int i = 0; i < 6; i++)
        {
            bounced |= GoalProgress.Closed(ref anchor, 2.23f, 2.69f, false);
            bounced |= GoalProgress.Closed(ref anchor, 2.69f, 2.23f, false);
        }
        Check(!bounced, "bouncing off a shut door between 2.69 m and 2.23 m is not progress");
        anchor = 2.23f;
        Check(GoalProgress.Closed(ref anchor, 3f, 2.5f, true) && anchor == 2.5f && !GoalProgress.Closed(ref anchor, 3f, 2.9f, true),
            "a target that moved is judged on its own window and re-anchors");

        output.WriteLine($"Passed {checks} approach-planning checks against production NpcAgent.Reach.cs.");
    }
}

sealed class TestTask(bool mayGoOutside = false, float below = 0, float reach = 1.6f) : ReachTask(new Vector3(10, .5f, 0), new Transform())
{
    public float Deferred = -1;
    public bool Recovers;
    public override string Name => "test item";
    public override bool MayGoOutside => mayGoOutside;
    public override float ReachBelow => below;
    public override float Reach => reach;
    public override void Defer(float seconds) => Deferred = seconds;
    public override bool Recover(string why) => Recovers;
}
