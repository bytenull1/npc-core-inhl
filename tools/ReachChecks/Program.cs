using System;
using NPC.Core.Agents;
using NPC.Core.Navigation;
using UnityEngine;

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
NavGraph.Nodes = [];
Check(agent.PlanReach(new TestTask(), out _) == "no nav node near it has a clear walk to it",
    "missing approach is distinct from missing route and stale door failure");
Console.WriteLine($"Passed {checks} approach-planning checks against production NpcAgent.Reach.cs.");

sealed class TestTask() : ReachTask(new Vector3(10, .5f, 0), new Transform())
{
    public override string Name => "test item";
    public override void Defer(float seconds) { }
}
