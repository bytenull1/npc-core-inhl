# Navigation - node graph, A\*, path following

Files: `Navigation/NavGraph.cs` (graph and search), `Navigation/NavProbe.cs` ([probes](probes.md)),
`Navigation/StationRooms.cs` (rooms by name). Following a plan is the agent's ([agent.md](agent.md)):
`Agents/NpcAgent.Navigation.cs` (walks and following) and `NpcAgent.Obstacles.cs` (steering and recovery).
Rules live in [invariants.md](invariants.md).

---

## 1. The model

The game has no navmesh. Routing runs over a graph of **hand-placed** nodes (F8 editor, see
[reference.md](reference.md#3-node-editor)).

Every node has an `Owner`:

| Owner | Stored in | Active when |
|---|---|---|
| `"ship"` | player-ship-local space, plus the room it stands on | that room is built ([below](#ship-nodes-follow-the-ships-upgrades)) |
| station name (e.g. `ShipyardStation`) | that station's local space | the ship is docked to it |
| `"world"` | `WorldObjects` space | always |

Owner-local storage survives flight, docking and saves: the game moves the world around the ship
([never-cache-node-world-positions](invariants.md#never-cache-node-world-positions)).

Nodes are `Ground` or `Stair`. `Stair` raises the off-level entry cap (`MaxStairDeltaY`) and is the
one exception to [entry-seeds-on-own-deck](invariants.md#entry-seeds-on-own-deck).

### Ship nodes follow the ship's upgrades

The ship's rooms depend on its upgrade stage
([game-model.md](game-model.md#the-players-ship-is-rebuilt-by-its-upgrade-stage)). A ship node
remembers its room and is walkable only while that room is built
([a-ship-node-rides-its-room](invariants.md#a-ship-node-rides-its-room)). In the editor such a node
is grey and unselectable, and the readout names the room (`[ship/Core_M01]`).

The shipped graph is drawn at stage 5, plus one link for stages 0–2: cockpit `#288` → airlock
`#304`. A link you draw holds in every stage where its two rooms stand as they do now, so fix a gap
at one stage by drawing the link *at* that stage - it cannot break the others.

### Links

Nodes connect only through links the player draws (editor key `K`, or `npc_node link`). A link
is an edge regardless of line of sight or height - there is no automatic, distance-based
connection.

### Where the nodes come from

Both live in `BepInEx/config/NPC.Core/`:

| File | Written by | Lifecycle |
|---|---|---|
| `nodegraph.bundled.json` | NPC.Core, from a resource in its DLL | rewritten when the DLL's `BundleVersion` changes |
| `nodegraph.json` | F6, `npc_node save`, 20 s autosave | never touched by an update |

A graph edited while it lived in YourBuddy (`BepInEx/config/YourBuddyRoutes/nodegraph.json`) is copied
over once, while `NPC.Core/` has none; the old file is never written or deleted.

**Per owner:** your nodes replace the bundled ones for that ship or station; owners you never
touched come from the bundle. Editing a bundled owner *forks* it - the whole owner is copied into
your file and stops following updates. `npc_node bundled` shows the split; `npc_node unfork
<owner>` hands one back. Forked owners are listed explicitly, so "I want no nodes here" differs
from "I never touched this". Anything finer than per-owner would make updates unpredictable.

Your nodes are numbered from `UserIdBase` (100000) so bundle ids never collide. Regenerate the
bundle with [`tools/bundle_nodegraph.py`](../tools/bundle_nodegraph.py). It takes your forked
owners and keeps every other owner, with its links, from the current bundle: your file holds only
what you forked, and copying it alone once shipped a bundle with no ship graph. To drop an owner,
fork it and empty it.

> **A link only acts while A\* traverses edges.** If one seems ignored, check the route chains at
> `debug_level 2`. A one-hop route (`via #99 -> goal`) never used an edge - that is a seeding
> problem ([entry-seeds-on-own-deck](invariants.md#entry-seeds-on-own-deck)).

### Rooms by name

`StationRooms` lists the docked station's rooms that hold a node, by the game's own room names, and
which nodes stand in each. A room has no volume, so a node belongs to the room whose furniture is
nearest. `TryResolve` matches free text ("go to the library", a shared prefix left out) to a room;
a mod passes its own filler words, such as what players call its NPC.

---

## 2. Nodes are not on the floor

Nodes sit wherever the editor dropped them. In the shipped `ShipyardStation` graph:

| node | node Y | floor under it | hover |
|---|---|---|---|
| #95–#100 (lower walkway) | 1.00 | 0.00 | +1.00 |
| #101 (mid-ramp, `Stair`) | 1.27 | ~0.30 | ~+1.0 |
| #102–#104 (upper ledge) | 1.62 | 0.63 | ~+1.0 |

`AddNode` floor-snaps new nodes, so a graph can mix floor-level and hovering markers. The fix is to
probe each node's floor, not rewrite stored positions (which would move the user's markers).
Rule: [floor-to-floor](invariants.md#floor-to-floor).

---

## 3. Edge cache

`RebuildEdges` builds `_edges` (node id → outgoing edges) directly from the player's links: every
live node gets an entry, even an empty one, since `FindPath` treats `_edges.Keys` as the set of
nodes it may seed from or route through. It rebuilds when the graph is dirty or the dock or ship
layout changes, and clears the node hover cache.

A link needs no line of sight, height check or distance cap
([links-have-no-los](invariants.md#links-have-no-los)) - it is added exactly as drawn.

---

## 4. A\* (`FindPath`)

```
FindPath(start, end, cameFromPos?, avoidEntry?)
    start = FloorUnderNpc()       a floor point - its Y is the start floor
    end   = player / target       a body point or node, so its floor is probed
```

`start.y` is never re-probed ([the-start-point-is-already-a-floor](invariants.md#the-start-point-is-already-a-floor)).

The returned `NavPath` stores, per waypoint, whether the arriving edge is forced and the deck under
it (`WaypointFloorY`). Following code reads decks from there instead of re-probing - that is how it
finds [stair legs](#6a-stair-legs).

**Seeding tests every candidate.** Every active node within `MaxSeedDist` goes through
`CanReachEntry`; all that pass are start nodes. Testing only the nearest few trapped the NPC when
its surroundings were blocked. `CanReachEntry` ([one-entry-predicate](invariants.md#one-entry-predicate))
admits a node when either:

- it is within `SeedCloseDist` flat and on the same deck (arm's length, no line of sight needed); or
- the deck test passes ([entry-seeds-on-own-deck](invariants.md#entry-seeds-on-own-deck)) and
  `ThinLos` is clear.

**Entry cost includes height.** A seed starts at `GoalCost(start, node)`, so the NPC walks to the
*foot* of the stairs rather than scrabbling at a mid-tread.

> A flight longer than `StairSeedRadius` (2.5 m) has no seedable foot from partway along it. The
> commitment rule handles it
> ([a-stair-entry-is-committed-once-taken](invariants.md#a-stair-entry-is-committed-once-taken)),
> but a node placed mid-flight fixes it at the source.

**No seeds** → the single nearest active node by `GoalCost`, with the backtrack penalty
([fallback-respects-backtrack](invariants.md#fallback-respects-backtrack)).

**The search does not stop at the first node that sees the goal.** Each expanded node records its
total (route + validated finish); the best wins. It stops when the open set's minimum `f` can no
longer beat the best total.

**Two ways to end a route:**

| | Condition | Goal appended? |
|---|---|---|
| **Finish** | goal within `MaxGoalFinishDist`, same deck, `ThinLos` and knee-height `WalkLos` clear ([an-endpoint-must-be-walkable-not-merely-visible](invariants.md#an-endpoint-must-be-walkable-not-merely-visible)) | yes |
| **Approach** | no clear finish; the expanded node with the lowest `GoalCost`, if it beats standing still by `ApproachMinGain` | **no** |

Appending the goal after an approach would recreate the beeline
([seed-radius-is-not-finish-length](invariants.md#seed-radius-is-not-finish-length)). If nothing
beats standing still, `FindPath` returns null and Follow walks directly.

**A clear, short goal skips the graph** ([a-clear-short-goal-beats-the-graph](invariants.md#a-clear-short-goal-beats-the-graph)).

**A failed search does not keep an old plan** unless its waypoint is within one flight
([a-stale-plan-is-worse-than-none](invariants.md#a-stale-plan-is-worse-than-none)).

**Doors the NPC cannot open are rejected everywhere**, graph links included
([locked-doors-block-edges](invariants.md#locked-doors-block-edges)). The test comes in through the
`SegmentBlockedByDoor` hook, and `LastPathBlockedByDoor` tells callers why a search failed
([doors.md §3](doors.md#3-routing-around-what-no-npc-can-open)).

`GoalCost(a, b)` = flat distance + `|Δfloor| × VerticalCostFactor` (4), so a node a deck below the
target is never "close".

---

## 5. Path commitment

`Pursue` replans at most every `NavPathRecalcInterval` (0.22 s). A plan survives while:

1. it is not exhausted;
2. on a stair leg, the NPC is still between the leg's decks
   ([off-the-flight-is-off-the-plan](invariants.md#off-the-flight-is-off-the-plan));
3. the goal has not moved more than `NavPathGoalDrift`;
4. the entry stretch is OK - forced, an off-level [stair entry](invariants.md#a-stair-entry-is-committed-once-taken),
   the NPC is [still on the route](invariants.md#commitment-skips-on-route), or `CanReachEntry` passes.

There is deliberately **no** "next stretch blocked" check
([links-have-no-los](invariants.md#links-have-no-los)).

### Recovery ladder

| Trigger | Response |
|---|---|
| feet leave the current stair leg's decks | replan at once (no index, no backtrack penalty); Wander drops the plan and steps off toward the leg's head |
| entry blocked `EntryBlockedAvoidAfter` times in `EntryBlockedWindow` | replan avoiding that waypoint; if nothing routes, retry without ([a-barred-waypoint-is-a-preference-not-a-wall](invariants.md#a-barred-waypoint-is-a-preference-not-a-wall)) |
| entry blocked `EntryBlockedStepOffAfter` times | drop the plan, step off, replan |
| moving but not getting closer | `UpdateGoalProgress` bars the waypoint for `UnreachableWaypointHold` |
| no progress for 3 cycles under a low ceiling | `EmergencyUnstick` ([a-low-ceiling-is-measured-from-the-body](invariants.md#a-low-ceiling-is-measured-from-the-body)) |
| no route because of a door it cannot open | stand still and log it ([stand-still-when-door-blocked](invariants.md#stand-still-when-door-blocked)) |
| no movement at all, above its own floor plane | step off ([idle-above-the-floor-plane-is-a-stall](invariants.md#idle-above-the-floor-plane-is-a-stall)) |

All rows but the last need `wantMove`. The last catches an NPC perched on furniture that produces
no movement at all.

---

## 5a. Wander pacing

Picking a destination and planning to it happen in one call (`TryStartWanderRoute`), trying up to
`WanderPickAttempts` nodes. Idle time is `WanderIdleMin`–`WanderIdleMax` after arriving and
`WanderRetryDelay` after a failure. Three failed rounds trigger the shared step-off
([step-off-applies-in-every-mode](invariants.md#step-off-applies-in-every-mode)).

Counting: [escalate-over-a-window](invariants.md#escalate-over-a-window). Index:
[drop-the-index-on-a-blocked-entry](invariants.md#drop-the-index-on-a-blocked-entry).

---

## 6. Waypoint advance

Shared by Follow and Wander: [waypoint-advance-is-dual](invariants.md#waypoint-advance-is-dual).
Reaching a waypoint is not enough to leave it if its outgoing leg is a flight the NPC is off.

Passing a real node records it as `lastDepartedWaypoint`
([backtrack-memory-outlives-the-plan](invariants.md#backtrack-memory-outlives-the-plan)). A replan
that continues the old route keeps progress, mapped onto the new plan
([drop-the-index-on-a-blocked-entry](invariants.md#drop-the-index-on-a-blocked-entry)).

---

## 6a. Stair legs

A leg whose ends are on different decks is a flight of stairs
([a-stair-leg-is-walked-not-improvised](invariants.md#a-stair-leg-is-walked-not-improvised)):

| On a stair leg | Why |
|---|---|
| steer `StairLegLookahead` along the leg, not at its end | turning onto a flight doesn't clip the railing end |
| no whisker detours, stuck sidestep or auto-jump | whiskers see the flight overhead as a wall; a sidestep runs along the landing; a railing looks like furniture |
| a waypoint whose outgoing leg the NPC is on counts as reached | a mid-flight replan doesn't send it back up |
| a waypoint whose outgoing leg the NPC is *off* is never left | going down, the 3D advance reaches a marker from the flight above |
| leaving the leg's decks ends the plan | [off-the-flight-is-off-the-plan](invariants.md#off-the-flight-is-off-the-plan) |

Which legs exist is still decided by the user's links.

---

## 7. Reading a `FindPath` capture

At `debug_level 2`:

```
[ai] Replanning (entry stretch blocked): NPC (-8.7, 1.2, 29.2) -> goal (-5.4, 0.6, 26.9),
     start (-8.7, 0.6, 29.2), cameFrom=(-8.7, 0.6, 28.6), blocked x2 in 4s
[nav] FindPath: 72 candidates in range, 2 visible entry seeds: #102(1,4m, +1,0y) #98(4,3m, +0,4y)
[nav] FindPath: total 9,5m via #98 -> goal, cameFrom=(-9.0, 1.6, 30.5)
```

- **`NPC` vs `start`** - body vs floor under it; ~0.6 m apart.
- **`0 visible entry seeds`** on flat ground is a red flag: the deck test rejects everything.
- **`+N,Ny`** per seed - deck difference, floor to floor. ~0 on the NPC's own level.
- **the chain** - `#98 -> #99 -> #101(stair) -> goal` is healthy. A one-hop `via #98 -> goal`
  means no edge was used.
- **`cameFrom=none`** mid-burst means the backtrack memory was lost.
- **identical repeated lines with a frozen `NPC`** - a commitment livelock
  ([one-entry-predicate](invariants.md#one-entry-predicate)).

### Log coordinates are world, stored ones are owner-local

A node's stored `P` is local to its owner (`OwnerSnapshot.WorldOf` puts it through
`OwnerTransform`); every log line prints world. **The offset between the two is not the one the
scene file was authored with** - captures taken at a station disagree with the authored placement,
and with each other. Never map a capture onto scene or node coordinates by assuming it.

Two ways to anchor a capture:

- **`npc_gates`** prints every gate's live **world** position. Any gate whose local position the
  scene gives you then fixes the offset for that moment.
- **the seed distances** - `#241(1,4m, -0,1y)` is `Vector3.Distance(start, world[id])`. Four or
  five seeds fix the start point in owner-local space from a capture you already have.

Better still, prefer conclusions that need no offset at all. Node-to-node distances, which wall a
leg crosses, and `total Xm via #A -> goal` (which decomposes as `dist(start, #A) +
GoalCost(#A, goal)`) are all frame-independent.

Stair legs add:

```
[ai] Walking the stair leg (-2.6, 2.3, 26.8) -> (-2.5, 1.0, 23.6) (decks 1,25 -> -0,05)     level 2, once per leg
[ai] Left the stair leg to (-2.5, 1.0, 23.6): feet 1,98 (probe floor 1,98) are outside -0,55..1,75 - replanning from here   level 1
  ... jumpArmed=False grounded=True stairLeg=True                                            level 3 obstacle report
```

`Left the stair leg` is a recovery. More than one per flight means something keeps pushing the
NPC off; the obstacle reports around it say what.

---

## 8. The OxygenStation stairwell

The hardest level change in the game and the reference test for the stair rules.

`OxygenStation` local coordinates (world z = local z + 20.62). Markers hover ~1.0 m above their deck.
Every step of the flight is a drawn link.

| Node | local (x, y, z) | deck | role |
|---|---|---|---|
| #399 | (−2.46, 1.00, 2.93) | 0.00 | bottom |
| #400 / #401 | (−2.60, 2.25, 6.15) / (−1.19, 2.25, 6.31) | 1.25 | landing pair |
| #402 / #403 | (−1.19, 3.50, 3.98) / (−2.57, 3.50, 3.70) | 2.50 | landing pair |
| #404 / #405 | (−2.53, 4.75, 6.26) / (−1.07, 4.75, 6.36) | 3.75 | landing pair |
| #406 / #408 | (−1.02, 6.00, 3.90) / (−2.63, 6.00, 3.71) | 5.00 | landing pair |
| #409 / #410 | (−2.53, 7.25, 6.23) / (0.97, 7.25, 6.25) | 6.25 | top |

Chain `399 → 400 → 401 → … → 408 → 409 → 410`. The bottom flight is 3.22 m long for 1.25 m of rise
(21°); the others 2.3–2.6 m (26–28°).

Rules that came out of this stairwell:
[walkable-ground-is-not-an-obstacle](invariants.md#walkable-ground-is-not-an-obstacle),
[a-stair-entry-is-committed-once-taken](invariants.md#a-stair-entry-is-committed-once-taken),
[avoid-entry-bars-the-whole-search](invariants.md#avoid-entry-bars-the-whole-search),
[an-entry-must-be-walkable-not-merely-visible](invariants.md#an-entry-must-be-walkable-not-merely-visible),
[a-detour-may-not-step-off-a-ledge](invariants.md#a-detour-may-not-step-off-a-ledge),
[a-grounded-NPC-stands-on-its-own-feet](invariants.md#a-grounded-npc-stands-on-its-own-feet),
[floors-ignore-the-gate-frame-rule](invariants.md#floors-ignore-the-gate-frame-rule),
[a-barred-waypoint-is-a-preference-not-a-wall](invariants.md#a-barred-waypoint-is-a-preference-not-a-wall),
[a-stale-plan-is-worse-than-none](invariants.md#a-stale-plan-is-worse-than-none),
[a-stair-leg-is-walked-not-improvised](invariants.md#a-stair-leg-is-walked-not-improvised),
[off-the-flight-is-off-the-plan](invariants.md#off-the-flight-is-off-the-plan),
[drop-the-index-on-a-blocked-entry](invariants.md#drop-the-index-on-a-blocked-entry).

Lesson: once the plan was right, the remaining faults were the steering layer applying flat-ground
logic on stairs. Moving nodes was never needed.
