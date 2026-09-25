# Invariants - rules that must not be broken

Each rule is stated once, here. Code and other docs link to its anchor.
Every entry exists because breaking it caused a bug.

Format: **Rule** - what must hold. **Why** - what breaks without it. **Enforced in** - where it lives.
`NpcAgent.*` is the walking agent (`Agents/`); YourBuddy's `BuddyBehaviour` is the brain these rules
were found with.

---

## Heights and probes

### floor-to-floor

**Rule.** Every vertical comparison resolves *both* sides to the floor beneath them
(`NavProbe.TryFloorHeight`, or `NavGraph.NodeFloorY` for a node) before comparing.

**Why.** Positions have three different vertical offsets: a plan `start` is a floor point, a
body position is ~0.6 m above the feet, and a node is wherever the editor dropped it (shipped
nodes hover ~1 m above their deck). A raw `|a.y - b.y|` measures the offset, not the deck.
Violating this *inverted* the level test: nodes on the NPC's own deck were rejected, nodes a
deck below passed as "same level", and the NPC walked into a railing.

**Enforced in.** `NavProbe.TryFloorHeight`, `NavProbe.ThinLos`, `NavGraph.NodeFloorY` /
`EntryDeltaYAllowance` / `GoalCost`, `NpcAgent.FloorUnderNpc`, `AdvancePlan` (the waypoint's deck is
`NavPath.FloorY`, the one the search measured, never a fresh probe under the marker). See [navigation.md §2](navigation.md#2-nodes-are-not-on-the-floor).

### node-hover-not-height

**Rule.** Cache a node's *hover* (`node.y − floorY`), never its absolute floor height.

**Why.** The hover survives the owner moving during flight; an absolute height goes stale
every frame the world moves.

**Enforced in.** `NavGraph.NodeHover` / `NodeFloorY`.

### probe-buffers-must-not-truncate

**Rule.** Every `*NonAlloc` buffer is large enough for the busiest spot in the game, and logs
when it fills.

**Why.** A full `RaycastNonAlloc` buffer drops hits arbitrarily, not the farthest ones. With 8
slots, the docking corridor's many overlapping colliders pushed the deck itself out, giving
"no floor" at one point and a deck 2 m down a metre away.

**Enforced in.** `NavProbe.FloorHits` / `LosHits` (64), `NavProbe.WarnIfTruncated`.

### a-missing-floor-is-a-last-resort-not-an-answer

**Rule.** When the filtered floor probe finds nothing, `TryFloorHeight` casts once more across
**every** layer before reporting failure.

**Why.** Some decks (the docking corridor) have no collider the normal probe sees. "No floor"
makes each side of a comparison fall back to a different base - the NPC to its feet, a node
to its marker - which breaks [floor-to-floor](#floor-to-floor). In the corridor this rejected
every nearby node and sent the NPC back through the door it came from. The wide pass only
replaces a non-answer, so it cannot change a case that already works.

**Enforced in.** `NavProbe.TryAnyLayerFloor`, called from `TryFloorHeight`.

### unprobeable-floors-keep-the-last-hover

**Rule.** When no floor is found under a node, `NodeFloorY` uses that node's last measured
hover, else the median hover of all measured nodes (`TypicalHover`). Never the marker's own Y,
and never drop the node.

**Why.** Falling back to the marker put corridor nodes ~1 m "off deck", so they were rejected
as entries while distant station nodes passed. Dropping unprobeable nodes was worse: it removed
the docking corridor and the first node aboard. All markers are placed the same way, so the
median is a real estimate.

**Enforced in.** `NavGraph.LastKnownHover` / `TypicalHover`, read by `NodeFloorY`, and through it by
every plan's `FloorY` - including a goal that is a node - and by `NavGraph.FloorAt` (the reach-node
search).

### the-start-point-is-already-a-floor

**Rule.** `FindPath` and `CanReachEntry` take `start.y` as the start floor and never re-probe it.
Only `end` (a body point) is probed.

**Why.** Callers pass `FloorUnderNpc()`, which is already correct
([a-grounded-NPC-stands-on-its-own-feet](#a-grounded-npc-stands-on-its-own-feet)).
Re-probing in a doorway returned a deck two storeys down, so the landing below read as "same
deck" and the NPC stepped off the stairs to reach it.

**Enforced in.** `NavGraph.FindPath`, the public `CanReachEntry` overload.

### a-grounded-npc-stands-on-its-own-feet

**Rule.** `FloorUnderNpc` returns the NPC's **feet** whenever
`CharacterController.isGrounded`. The floor probe is used only while airborne. The player gets
the same rule in `FloorUnderPlayer`.

**Why.** The floor probe can answer with a surface below the one the body is on - at the
`OxygenStation` stair head it once answered the landing two storeys down
([floors-ignore-the-gate-frame-rule](#floors-ignore-the-gate-frame-rule)). The NPC stepped off
the top of the stairs, climbed back, and repeated. A grounded controller is physics reporting
what the body stands on, so it is not a [second probe basis](#one-probe-basis).

**Enforced in.** `NpcAgent.FloorUnderNpc`, `NpcAgent.FloorUnderPlayer`.
The goal floor in `FindPath` is still probed - see [known-issues.md](known-issues.md#the-goal-floor-in-findpath-is-still-probed).

### probe-what-the-body-collides-with

**Rule.** A probe sees exactly the layers its body collides with, read from the game's collision
matrix for that `CharacterController`'s layer. An agent's own body probes use its controller's row
(`NavProbe.BodyMaskFor`); graph and floor probes use the walker's, `NavProbe.ProbeLayers`, which is the
player's. Never a hand-kept list, never `DefaultRaycastLayers`.

**Why.** A probe that sees what the body walks through invents obstacles. The docking hatch lid
sits on `Interactable`, which the body ignores; the whiskers hit it and the NPC hugged the
door jamb through every airlock. A question about another collider uses *that* collider's row:
doorway occupancy asks the `AntiCrasher`'s layer (`CollisionMaskFor`), which also stops for
dropped items ([occupancy-asks-the-anticrasher](#occupancy-asks-the-anticrasher)).

**Enforced in.** `NavProbe.ProbeLayers`, `BodyLayer`, `BodyMaskFor`, `MaskFor`, `CollisionMaskFor`;
`NpcAgent.ProbeLayers`.

### whiskers-are-body-shaped

**Rule.** Every whisker capsule (`BodyBlocked`, both stuck diagnostics) comes from
`NpcAgent.BodyCastCapsule`: 0.25 m above the floor up to the top of the controller
(`height + skinWidth`), never a fixed height.

**Why.** The controller is 1.0 m tall; the capsule used to reach 1.69 m. Ungated ship doorways
have a lintel at 1.44 m, so they read as walls from every heading and the NPC paced along the
wall.

**Enforced in.** `NpcAgent.BodyCastCapsule`.

### a-low-ceiling-is-measured-from-the-body

**Rule.** `UnderLowCeiling` (the gate on `EmergencyUnstick`) looks up only to the top of the body
plus `CeilingHeadroom` (0.1 m), from 0.25 m above the feet, ignoring the NPC and player.

**Why.** The old ray reached 2.0 m above the feet, and every ship ceiling is at 1.81 m, so the
check was always true aboard and Follow teleported the NPC after three slow cycles.

**Enforced in.** `NpcAgent.UnderLowCeiling`.

### walkable-ground-is-not-an-obstacle

**Rule.** Steering avoids only what the `CharacterController` cannot traverse. A hit within
`slopeLimit` of horizontal, or a step no higher than `stepOffset` with clear space above, is
ground.

**Why.** The whisker capsule sees any slope steeper than 14.7°, and every staircase is steeper
(the `OxygenStation` first flight is 21°). The whiskers treated stairs as walls and detoured
into the railing. The NPC uses the player's own `stepOffset` and `slopeLimit`, so it can walk
anything the player can. The clearance check stops a wall's bottom edge passing as a step.

**Enforced in.** `NavProbe.HitIsWalkableGround` via `NpcAgent.IsWalkableGround`; used by
`BodyBlocked`, `TryAutoJump` and both stuck diagnostics.

### a-detour-may-not-step-off-a-ledge

**Rule.** A whisker detour or stuck sidestep may not head toward ground more than
`DetourLedgeDrop` (0.7 m) below the NPC's floor. The direct heading is never tested this way.
An unprobeable floor is not a ledge.

**Why.** Detouring around the station bot at the top of the `OxygenStation` stairs took the
NPC over the railing, two decks down. 0.7 m allows stair treads and the 0.63 m platform, and
rejects a 1.25 m deck.

**Enforced in.** `NpcAgent.StepsOffALedge` (from `SteerAroundObstacles`,
`UpdateStuckDetection`, `TryBackAway`).

### one-probe-basis

**Rule.** All navigation probes go through `NavProbe`. Never write a second raycast with its own
collider filter.

**Why.** Two filters on two sides of a comparison measure the difference between the filters -
the same bug as [floor-to-floor](#floor-to-floor). A floor-owner test once had its own
ray, missed the [wide fallback](#a-missing-floor-is-a-last-resort-not-an-answer), and hid the
NPC from the lifecare terminal.

**Enforced in.** `NavProbe`; `FloorUnderNpc` and `NpcVessels.FloorOwner` delegate to it.

### gate-frame-hit-point

**Rule.** The gate-frame carve-out judges **the point the cast touched**, never the collider as
a whole. Route hit points through `NavProbe.ContactPoint(hit, origin)` (casts that start inside
a collider report `point == zero`).

**Why.** `bounds.ClosestPoint(gatePos)` is 0 for any collider that *contains* a gate - such as
the wall that holds the door. The whole mid-ship wall vanished and the NPC walked into it.

**Enforced in.** `NavProbe.HitIsGateOpening`, `IsEdgeProbeIgnorable`.

### gate-carveout-is-the-opening

**Rule.** A gate carves out only **the opening it makes**, as a slot in the gate's own frame:
`openingHalfWidth` across (local x), `GateOpeningHalfHeight` vertical, `GateOpeningHalfDepth`
through the wall (local z). A gate with no anchors, or no leaf to slide, is not a door and
carves nothing.

**Why.**
- *Shape.* A radius around the gate origin erases wall along the wall in both directions.
  Overlapping radii erased the whole mid-ship pier.
- *Passage test.* `ElectricityPanelGate` (a breaker cabinet) has anchors `[Empty, Empty,
  itself]` - no leaf. Leaf *size* cannot separate doors from cabinets: ship leaves are
  1.44–1.54 m, station leaves 1.95 m.
- *Width.* The opening is the span the leaves cover when shut, plus `GateOpeningMargin`, clamped
  to `[GateOpeningRadius, MaxGateFootprintRadius]`. Every real door measures 0.5625 m and lands
  on the 0.85 m floor. Station doors slide one leaf *up*; reading that 1.70 m stroke as a width
  carved 3.9 m of wall from every station doorway.
- *Fallback.* If `gateAnchors` cannot be read at all (a game update), every gate keeps a 0.85 m
  cylinder and stays a passage, so doors do not all close at once.

Docking collars and airlocks do not use the carve-out: any hit with a `Docker`, `Airlock` or
`Gate` in its parents is ignored outright. Do not restore name matching, leaf-position radii or
size-based passage tests - all three were tried ([known-issues.md](known-issues.md#the-gate-carve-out)).

**Enforced in.** `NavProbe.MeasureOpening`, `HitIsGateOpening`.

### a-doorway-is-crossed-not-grazed

**Rule.** For `WalkLos` and `ThinLos`, a hit is forgiven by the carve-out only when the **whole
line under test** crosses the gate's plane inside the opening: within the clear half-width the leaves
close over, less `CrossingBodyRadius` - never within the wider carve-out. A line whose ends lie on the same
side never passes through, so its hit point alone decides. The whiskers (probes that stop in the
doorway) keep the point-only test; `TryFloor` does not use the carve-out at all
([floors-ignore-the-gate-frame-rule](#floors-ignore-the-gate-frame-rule)).

**Why.** The carve-out is wider than the hole it stands for - a station doorway is 1.25 m across
and the slot is 1.70 m - and the wall blocks beside it are 1.25 m deep. The jamb face therefore
lies *inside* the slot along its whole depth. A line that clips that face reports its one entry
hit there, Unity reports nothing for the sub-casts that start inside the block, and 1.5 m of solid
wall becomes invisible. A\* then buys a straight finish leg through the wall, and the NPC paces
in front of it. Widening the hit-point test cannot fix this: the hit point is genuinely in the
opening. Only the line's own crossing tells doorway from jamb. Judged against the 0.85 m carve-out,
a line crossing 0.7 m off-centre still passed through the wall block beside a station door (faces
at 0.62 m), so an entry seed behind the door was taken at a slant and the NPC walked into the corner.

**Enforced in.** `NavProbe.ChordCrossesOpening`, `HitIsGateOpening`, `WalkLos`, `ThinLos`.

### floors-ignore-the-gate-frame-rule

**Rule.** The floor probe (`TryFloor`) never applies the gate-frame carve-out. It skips only bodies
and passable interfaces: docking collars, airlocks and door leaves.

**Why.** The carve-out is 5 m tall and reaches 1 m either side of the wall. On a downward ray it
dropped the deck under a doorway and kept a floor lower down. Beside the `OxygenStation` top-deck
door (`Door02`) it dropped the 6.25 deck and kept the landing at 3.75, exactly 2.5 m below. The
entry probes re-floor their start point, so from the top deck they judged the stairwell below:
nodes on the NPC's own deck failed as "los", and `#406` on the landing below passed across the
railing.

**Enforced in.** `NavProbe.TryFloor`, `IsPassableInterface`.

### doorway-floor-survives-the-filter

**Rule.** When the filter rejects every hit, the floor probe falls back to the highest solid
non-body hit.

**Why.** Airlock and docking-collar floors are filtered as passable interfaces. Without the
fallback, a point on one reports "no floor".

**Enforced in.** `NavProbe.TryFloorHeight`.

---

### sight-stops-at-a-shut-door

**Rule.** Whether an NPC can *see* something is `NavProbe.CanSee` only: a straight line from the
eye, blocked by any gate that is not open, failing on any hit except bodies, the target's own
colliders, or frame trim in a fully open doorway.

**Why.** Walkability probes forgive gate openings whether or not the door is shut, re-floor each
sample and look from 1 m. As a sight test they see through closed doors. Gates are tested
explicitly because an airlock pair stands closer together than the carve-out is deep.

**Enforced in.** `NavProbe.CanSee`, from YourBuddy's `BuddyBehaviour.MonsterInSight`.


## Pathfinding

### entry-seeds-on-own-deck

**Rule.** A route entry seed must be on the NPC's own deck. The one exception: a `Stair` node
within `StairSeedRadius`, up to `MaxStairDeltaY` off-level.

**Why.** If a node one level up counts as an entry, A\* walks straight at it in one hop, and the
user's links - including `Block` and `Priority` - are never used. On its own deck the NPC must
traverse the edges the user drew through the stairs.

**Enforced in.** `NavGraph.EntryDeltaYAllowance` (`SameLevelDeltaY`).

### a-stair-entry-is-committed-once-taken

**Rule.** When a plan's first waypoint is a `Stair` node entered off-level, it counts as forced,
so commitment does not re-probe it. Line of sight only - a locked door still blocks it.

**Why.** `forced[i]` describes the edge arriving at `i`; the first node has none. A flight longer
than `StairSeedRadius` let the NPC walk out of seed range between two ticks, so every plan was
rejected on the next tick.

**Enforced in.** `NavGraph.FindPath`, where `forced[]` is built.

### an-entry-must-be-walkable-not-merely-visible

**Rule.** An off-level entry (the `Stair` exception) also needs continuous ground
(`GroundIsContinuous`: no break over `StairEntryGroundStep`, 0.8 m, sampled every 0.35 m) **and**
a clear knee-height line (`WalkLos`, 0.4 m).

**Why.** `ThinLos` looks from 1 m up and samples coarsely, so it flies over an 0.85 m railing and
dips through a stairwell. The NPC seeded the other flight's head across the well, crossed the
railing and fell two decks. Ground continuity alone still let it enter through a wall, hence
both tests. Same-deck entries and graph edges are unaffected.

**Enforced in.** `NavProbe.GroundIsContinuous`, `NavProbe.WalkLos`, from
`NavGraph.CanReachEntry`.

### one-entry-predicate

**Rule.** `FindPath` seeding and `Pursue` commitment both call
`NavGraph.CanReachEntry`. Never inline a threshold on either side.

**Why.** If commitment is stricter than seeding, every plan is rejected the tick after it is made
and the NPC replans forever without moving.

**Enforced in.** `NavGraph.CanReachEntry`.

### seed-radius-is-not-finish-length

**Rule.** `MaxSeedDist` (how far an entry may be) and `MaxGoalFinishDist` (how long the final
straight walk may be) are separate constants.

**Why.** As one 60 m value, any node with a lucky sight line ended the search in a 25 m beeline
through glass and across decks.

**Enforced in.** `NavGraph.MaxSeedDist`, `MaxGoalFinishDist`.

### a-clear-short-goal-beats-the-graph

**Rule.** Before seeding, `FindPath` returns a one-waypoint plan when the goal is within
`MaxGoalFinishDist`, on the same deck, door-clear and endpoint-clear
([an-endpoint-must-be-walkable-not-merely-visible](#an-endpoint-must-be-walkable-not-merely-visible)).

**Why.** The graph could answer a 3.7 m walk with a 17.6 m detour. This is the finish-leg
predicate anchored at the NPC, so it can beeline no further than a finish already may.

**Enforced in.** `NavGraph.FindPath`, before seeding.

### an-endpoint-must-be-walkable-not-merely-visible

**Rule.** The short clear goal and the finish leg share one predicate, `HasLineOfSight`:
`ThinLos` **and** `WalkLos` (knee height).

**Why.** `ThinLos` looks over anything under 1 m. The NPC walked into the 0.52 m sell-station
railing beside nodes that went round it. Graph edges keep `ThinLos` alone: a strict sweep on
edges rejected the links the user drew.

**Enforced in.** `NavGraph.HasLineOfSight`.

### same-level-tolerance-ceiling

**Rule.** `SameLevelDeltaY` and `WaypointAdvanceMaxDeltaY` stay **below** the shallowest real
level change in the game (the 0.63 m railed platform).

**Why.** Raise either and a deck reads as a step.

**Enforced in.** `NavGraph.SameLevelDeltaY`, `NpcAgent.WaypointAdvanceMaxDeltaY`,
`FollowSameLevelDeltaY`.

### links-have-no-los

**Rule.** Every graph edge is a link the player drew, and it skips line of sight by design. Never
add a line-of-sight check to commitment for one. Locked doors still block them
([locked-doors-block-edges](#locked-doors-block-edges)).

**Why.** Users draw them exactly where geometry blocks sight (railings, airlock collars). A "next
stretch blocked" check fired every 0.22 s mid-stair and made links useless. Runtime blockage is
the stuck detector's job.

**Enforced in.** `NavPath.IsForced`, checked before the commitment probe.

### fallback-respects-backtrack

**Rule.** The nearest-node fallback applies the same `BacktrackPenalty` as seeding.

**Why.** On raw distance it picked a node just behind the NPC over one just ahead, causing
sudden turns backwards.

**Enforced in.** `NavGraph.FindPath` nearest-node fallback.

### backtrack-memory-outlives-the-plan

**Rule.** The anti-backtrack reference is a persistent field, not `navPlan[navPathIndex - 1]`.

**Why.** An entry-blocked replan resets the index to 0, so the memory vanished exactly when a
tight replan loop needed it.

**Enforced in.** `NpcAgent.lastDepartedWaypoint`, set in `AdvancePlan` for
real nodes only.

### never-cache-node-world-positions

**Rule.** Call `ToWorld` fresh in each graph query. Never cache world positions, owner
transforms or docked state across frames.

**Why.** The game moves the world around the ship, so station node positions drift during flight
while their owner-local coordinates stay valid.

**Enforced in.** `NavGraph.FindPath`, `RebuildEdges`, via a per-call `OwnerSnapshot`.

### avoid-entry-bars-the-whole-search

**Rule.** `avoidEntry` excludes matching nodes from seeding, the nearest-node fallback **and**
neighbour expansion. Matching is identity (`AvoidEntryRadius`, 0.5 m), not proximity.

**Why.** The unreachable waypoint is usually several hops in; excluding it from seeding alone lets
A\* plan through it again. With the 1.5 m `BacktrackRadius`, barring one `OxygenStation` landing
also barred its twin 1.4 m away and deleted the stairs from the search.

**Enforced in.** `NavGraph.FindPathCore` (`excluded`).

### a-barred-waypoint-is-a-preference-not-a-wall

**Rule.** If a search with `avoidEntry` finds nothing, `FindPath` retries once without it.

**Why.** A staircase chain is linear; barring one node cuts the graph in two. The NPC then kept
an old plan pointing two decks down and ground along the railing. The escalation ladder still
ends any loop this could cause ([escalate-over-a-window](#escalate-over-a-window)).

**Enforced in.** `NavGraph.FindPath`, wrapping `FindPathCore`.

### a-stale-plan-is-worse-than-none

**Rule.** When `FindPath` returns nothing, `Pursue` keeps the previous plan only while its
current waypoint is within one flight (`MaxStairDeltaY`) of the NPC's floor.

**Why.** A plan made two decks ago walks the NPC into whatever lies between. The test is
deliberately weak; on a stair leg the exact test is
[off-the-flight-is-off-the-plan](#off-the-flight-is-off-the-plan).

**Enforced in.** `NpcAgent.Pursue` via `NavGraph.WaypointIsOnAReachableDeck`.

### opening-is-not-passing

**Rule.** "May the NPC **open** this gate?" (`NpcDoors.MayOpen`) and "should routing go **around**
it?" (`NpcDoors.BlocksRouting`) are different questions. Only a gate that will still be shut when the
NPC arrives - locked, a door the player could not walk open, a pin door without the code - blocks
routing.

**Why.** As one predicate, the NPC's refusal to open airlock and docking gates made the ship
unreachable from a station. The player opens those. `ElectricityPanelGate` never blocks routing.

**Enforced in.** `NpcDoors.BlocksRouting`, `MayOpen`; `RefreshImpassableGates` uses the former
([doors.md §1](doors.md#1-two-different-questions)).

### locked-doors-block-edges

**Rule.** A route stretch through a gate that will still be shut is rejected in seeding, the
fallback, neighbour expansion and the finish leg - **Force and Priority edges included**. The test
is the gate's own volume, never a radius.

**Why.** Without it A\* planned through locked doors and the NPC shoved at them. The Force
exemption is about sight, not doors. A radius around the origin also blocked the corridor running
past a door; `NavProbe.SegmentCrossesGate` slab-tests the gate in its local space. Nothing is cast:
this runs on every relaxed edge.

**Enforced in.** `NpcDoors.BlocksRouting` / `SegmentBlockedByDoor`,
`NavProbe.SegmentCrossesGate`, via `NavGraph.DoorBlocks`.

### stand-still-when-door-blocked

**Rule.** When `FindPath` fails *and* `LastPathBlockedByDoor` is set, the NPC holds position and
logs why (throttled). No random step-off, and Wander does not count it as a failure.

**Why.** The step-off is for an NPC wedged on furniture. For a door it cannot open it only turns
"waiting for you" into milling about.

**Enforced in.** `NpcAgent.Pursue`, `Wander`, via `LogDoorBlockedRoute`.

---

## Path following

### commitment-skips-on-route

**Rule.** The entry-stretch probe is skipped while the NPC is within `NavPathOnRouteRadius` of the
previous waypoint.

**Why.** That stretch is an edge already validated between node positions. Re-probing from the
NPC's offset position clipped railings and made it shuttle between two nodes.

**Enforced in.** `NpcAgent.IsStandingOnRoute`.

### escalate-over-a-window

**Rule.** Blocked entries are counted over a sliding time window, never as a consecutive run.

**Why.** The failure is a ping-pong with healthy ticks in between, which reset a run counter.

**Enforced in.** `NpcAgent.entryBlockedCount`, `EntryBlockedWindow`.

### drop-the-index-on-a-blocked-entry

**Rule.** `navPathIndex` is never kept across an entry-blocked or
[off-the-flight](#off-the-flight-is-off-the-plan) replan, and a kept index never points past the
waypoint being walked (`min(old, resumeAt)`).

**Why.** The index is the unreachable part. And a new plan with fewer waypoints before the matched
tail made a kept index skip ahead, steering at the goal from the foot of the flight.

**Enforced in.** `NpcAgent.Pursue` (`SameRemainingPath`).

### waypoint-advance-is-dual

**Rule.** Advance on `(distXZ < WaypointReachedXZ && sameLevel) || dist3D < WaypointReached3D`, with
`sameLevel` measured [floor to floor](#floor-to-floor) and `dist3D` from `WalkerOriginAboveFeet` above
the feet (the player's origin, which markers are placed around; a body's own origin can be anywhere), or when the NPC is already on the stair
leg leaving the waypoint. Never advance past a waypoint whose *outgoing* leg is a flight the
NPC's feet are off.

**Why.** A pure 3D test needs the NPC on the landing before it may target it (oscillation at the
stair foot). XZ without `sameLevel` eats waypoints overhead. Going down, the deck-blind 3D branch
reaches a stair node's marker (~1 m above its deck) while the feet are still on the flight above.
[off-the-flight-is-off-the-plan](#off-the-flight-is-off-the-plan) catches that advance, but only as
a recovery: Wander drops the plan, steps back and re-picks, so the NPC paused at every
`ShipyardStation` landing on the way down.

**Enforced in.** `NpcAgent.AdvancePlan`.

### a-skipped-waypoint-is-not-an-arrival

**Rule.** A Route waypoint abandoned for being unreachable marks the plan. When such a plan runs
out of waypoints, the route ends as a failure - a warning naming the goal, and an order that is
reported *not* done. Only a plan walked to its end is an arrival.

**Why.** Stuck and no-progress recovery skip the waypoint underway. Skipping the last one falls
straight into YourBuddy's `FinishRoute`, which said "the goto order is done" and cleared the order, so a
NPC that never left the wall it was pacing reported success. A recovery that hides a wrong plan
is how the [gate carve-out](known-issues.md#the-gate-carve-out) stayed hidden once already.

**Enforced in.** `NpcAgent.SkipWaypoint` / `SkippedWaypoint`, `DropPlan`, `CommitPlan(fresh)`; a brain's end of a
walk (YourBuddy's `FinishRoute`).

### a-stair-leg-is-walked-not-improvised

**Rule.** A *stair leg* is a plan stretch whose ends are more than `WaypointAdvanceMaxDeltaY` apart,
floor to floor. On one, the NPC steers `StairLegLookahead` along the leg rather than at its end,
and whisker detours, the stuck sidestep and auto-jump do not run. A waypoint counts as reached once
the NPC is on its outgoing stair leg (between its decks, past its start, within
`StairLegCorridor`).

**Why.** All three improvisers think on the flat, and at a stair head every flat answer leads off
the stairs:
- the horizontal whiskers hit the underside of the flight above and read it as a wall;
- aiming at the far end cut the corner into the railing end;
- the sidestep ran along the landing, into the wall;
- auto-jump hopped the 0.86 m railing as if it were low furniture. Heading along the leg does not
  prevent it: a wrong off-level entry is a leg across a railing, and the NPC hopped the fence to
  `#406` from the top deck ([floors-ignore-the-gate-frame-rule](#floors-ignore-the-gate-frame-rule)).

The advance half stops a mid-flight replan from sending the NPC back up to the flight head.

**Scope.** `Pursue`, `Wander` and every walk on `HeadAlongPlan` (YourBuddy's retreat and hide).
`SimpleAdvance` (YourBuddy's `buddy_goto`) keeps its own advance.
Decks come from `FindPath` (`NavPath.WaypointFloorY`), never from re-probing waypoints.

**Enforced in.** `NpcAgent.HeadingAlongPlan` (`walkingStairLeg`), `Update`,
`UpdateStuckDetection`, `AdvancePlan`.

### off-the-flight-is-off-the-plan

**Rule.** On a stair leg, the NPC's **feet** stay between the two decks the leg joins (each
widened by `WaypointAdvanceMaxDeltaY`). If not, the plan is invalid: Follow replans at once with no
kept index and no backtrack penalty, dropping the plan if nothing routes; Wander drops the plan
**and steps off toward the leg's head**; a flee's retreat replans. Applies to Force and Priority
legs too.

**Why.** Nothing else caught an NPC knocked onto the neighbouring flight: Force legs skip the
commitment probe and progress is measured flat, so it climbed the wrong flight and every step
counted as progress. Wander must also *move*, because it waits in place and would pick the same
bad leg forever. Feet, not `FloorUnderNpc`: ungrounded on treads, the floor probe can answer
with a surface below.

**Enforced in.** `NpcAgent.HasLeftStairLeg` from `Pursue`, `Wander` (`TryStepOffTowardLegHead`) and a
brain's own plan walk (YourBuddy's `UpdateFlee`); the next leg from `AdvancePlan`.

### follow-arrival-is-level-aware

**Rule.** Follow's "close enough" test compares floors, not body heights.

**Why.** Distance is XZ, so a player on the deck above reads as 0 m and the NPC parks underneath.

**Enforced in.** `NpcAgent.FollowSameLevelDeltaY`, `FloorUnderPlayer`; YourBuddy's `OnPlayersDeck`.

### step-off-applies-in-every-mode

**Rule.** Every walk checks the step-off first: `Pursue`, `Wander`, `Stay`, and a brain's own walks
through `TryStepOff` (YourBuddy's Follow, Route and Flee).

**Why.** Only Follow read it, so a wandering NPC sat in the doorway it was blocking. A flee's
back-away is a step-off too.

**Enforced in.** `NpcAgent.TryStepOff`.

### idle-above-the-floor-plane-is-a-stall

**Rule.** No movement for `IdleNoRouteGiveUp` while more than `IdleAboveFloorDelta` above its own
floor plane means stalled: drop the plan and step off.

**Why.** Every other recovery is gated on `wantMove`, so an NPC perched on furniture with no
seedable node was watched by nothing. The floor-plane test keeps it from firing on normal waiting,
and `UpdateBaseFloor` adopts a real raised deck after ~4 s.

**Enforced in.** `NpcAgent.UpdateIdleRecovery`, unless `NpcActivity.IdleWatch` is off.

### progress-is-measured-to-one-target

**Rule.** The no-progress watch compares distances to the same target only. A 0.6 s window in which
the NPC passed a waypoint neither counts as progress nor as a failure.

**Why.** The next waypoint is farther than the one just reached, so that window always "lost"
distance. Where waypoints are 1.6-3.3 m apart (the docking corridor, stairs) two such windows ran
back to back, and an NPC walking straight at its waypoint was logged as stalled and could drop its walk.

**Enforced in.** `NpcAgent.UpdateGoalProgress`.

### give-up-per-waypoint

**Rule.** A walk gives up on time only when one waypoint is overdue: `WaypointBudgetBase` plus
`WaypointBudgetPerMetre` for each metre to it, fixed when the NPC set off for it. Reaching a waypoint
starts a new clock.

**Why.** One clock for the whole walk, against the straight-line distance still to go, ran out on long
routes that kept arriving: the budget shrank as the NPC closed in. It then turned round mid-stair for a
new target and walked back down.

**Enforced in.** `NpcAgent.WaypointOverdue`: `Wander`, and YourBuddy's flee.

---

## Graph ownership

### a-ship-node-rides-its-room

**Rule.** A ship node is anchored to the room whose floor it stands on (`Node.Anchor`, or `hull`),
with where that room stood (`AnchorAt`). It is live only while that room is built and moves with
it. A manual link records the room offset it was drawn in (`ManualLink.Offset`) and is live only in
that layout.

**Why.** The ship's rooms depend on its upgrade stage. The shipped graph is drawn at stage 5; at
stage 0 most of its ship nodes stand in rooms that do not exist. Links need their own offset
because the cockpit connects to the airlock differently at stages 0–2. The room comes from the
floor collider, never the tracked room. Nodes from an old file are anchored on first load.

**Enforced in.** `NavGraph.OwnerSnapshot.IsLive` / `ShiftOf` / `WorldOf` / `LinkIsLive`,
`ToggleLink`, `SameOffset`, `TryAnchorShipNode`, `ShipLayoutSignature`.

### a-station-owns-its-interior-by-reference

**Rule.** A scene object belongs to its nearest `SpaceShip` or `SpaceObject` ancestor, or else to
the `SpaceObject` whose `contentParent` contains it. Always ask `NavGraph.TryVesselOf`.

**Why.** Station interiors are root-level objects the station only references
([game-model.md](game-model.md#a-stations-interior-is-not-under-the-station)). An ancestry walk read
every station as `world`: the NPC did not park on undock, got no room, and never wandered.

**Enforced in.** `NavGraph.TryVesselOf`, from `NpcVessels.FloorOwner` / `OwnerOfTransform`,
`UpdateEnvironment`, `TryOwnerFromTransform`.

### reflection-lives-in-gameinternals

**Rule.** Every `GetField` / `GetMethod` into a game type lives in `GameInternals.cs`, resolved once
at plugin load (`ResolveAll`), with a one-time warning naming the feature it degrades. Fields declare
their type (`Field<T>`); a retyped field counts as missing. Harmony patches are applied per feature
group, never in one `PatchAll`.

**Why.** A game update renaming a member would otherwise fail silently deep in behaviour code, and
one missing patch target would take every other patch down with it.

**Enforced in.** `GameInternals`, `NpcCorePlugin.ApplyPatches (and each mod's own)`.

---

## Doors and rooms

How NPCs use doors is explained in [doors.md](doors.md); the game's door model in
[game-model.md §3](game-model.md#3-gate-the-door-state-machine).

### an-npc-opens-only-what-the-player-could

**Rule.** A gate that doorway detectors drive opens only through one of them that is switched on,
and whose `helmetRequired` the player meets. Otherwise it is treated as locked: not opened, routed
around. A gate no detector drives is not judged by this rule.

**Why.** The player opens a door only by walking into its detector. The tutorial's first door needs
a suit; the NPC opened it, and a player who followed was locked out of the room with the suits.
The Oxygen Station's sealed rooms have their detectors switched off, and only the seal panel opens
them; the NPC opened them anyway. Both are judged by the game's state, not door names. Airlock
and docking gates are checked first, so they stay routable.

**Enforced in.** `NpcDoors.WhyClosedToPlayer`, read by `MayOpen` and `BlocksRouting`;
`RefreshDetectors` (`DetectorsByGate`, switched-off detectors included);
`GameInternals.PlayerDetectorAccess`.

### an-npc-only-knows-codes-it-was-told

**Rule.** An NPC opens a pin-code door only with `PinCode.ForceValidate()`, on the gate's own panel
(`NpcDoors.PinPanelFor`), when that panel's code is one the player gave (`NpcDoors.KnownCodes`). Never
`PinCode.Interact(null)`.

**Why.** `Interact(null)` is the monster's branch: a random guess stored in the monster's save
data. `ForceValidate` fires the panel's own `OnValidated → Gate.InvokeOpen`, so the door opens through
the game's wiring and a walker's close-behind works unchanged.

**Enforced in.** `NpcDoors.MayOpen` / `BlocksRouting` (`CodeKnown`); the walker's panel code
(`NpcAgent.TryUnlockPasswordGate`).

### keep-electricitypanelgate-excluded

**Rule.** `ElectricityPanelGate` stays on the ignore list.

**Why.** Otherwise NPCs open electrical panels.

**Enforced in.** `NpcDoors.MayOpen`.

### npc-closes-must-not-move-the-player

**Rule.** A door an NPC closes must not re-file the player's current room. An NPC records each close
it issues (`NpcDoors.NoteClosedBy`); away from the player, the rooms the game did not switch off are
the closer's to release (`INpcDoorUser.OnDoorClosedAway`).

**Why.** `EntryDetector` re-evaluates the player's side on every door close, with no distance check
([game-model.md](game-model.md#entrydetector-re-files-the-player-when-a-door-closes)). An NPC closing a
door across the ship moved the player's room, dropped their lifecare icon and loaded or unloaded rooms
around them. With the player hidden the game's own unload never runs, so the closer does it.

**Enforced in.** `Patches.Doors.EntryDetector_DoorCheckForEnter_Prefix` / `_Postfix`, gated on
`NpcDoors.WasClosedByNpc` and `PlayerAtDoorwayRadius`.

### pending-closes-are-a-list

**Rule.** An agent's `pendingDoorCloses` is a list, never a single slot.

**Why.** One slot dropped the first door when a second opened.

**Enforced in.** `NpcAgent.pendingDoorCloses`.

### no-permanent-deferral

**Rule.** A deferred close must end. Never refresh `ArmedAt` while waiting; track `DeferredSince`
and act at `DoorDeferGiveUp`.

**Why.** Refreshing `ArmedAt` defeated the 90 s cap, and a door held by something static was
deferred silently forever.

**Enforced in.** `NpcAgent.UpdateDoorCloseBehind`.

### never-force-a-close-into-the-npc

**Rule.** The give-up path does not apply while the agent itself is the blocker.

**Why.** It would fail, and the `Gate.FailClose` hook would re-arm it - a loop made of two correct
fixes. Keep stepping out; the 90 s cap ends it.

**Enforced in.** `NpcAgent.UpdateDoorCloseBehind`.

### close-only-what-you-walked-through

**Rule.** A pending close fires only once the agent is on the **opposite side** of the gate from
where it opened it. `NoteCloseFailed` entries start already crossed.

**Why.** A 3 s timer from opening shut doors in the NPC's face when it had not got through yet,
then it reopened them, forever.

**Enforced in.** `NpcAgent.HasCrossed`, in `UpdateDoorCloseBehind`.

### fail-close-must-not-rearm

**Rule.** `NoteCloseFailed` does not call `ArmDoorClose` for a gate already pending.

**Why.** Re-arming resets `ArmedAt` and `Attempts`: unlimited retries and no 90 s backstop.

**Enforced in.** `NpcAgent.NoteCloseFailed`.

### occupancy-asks-the-anticrasher

**Rule.** Doorway occupancy tests the gate's own `AntiCrasher` bounds on that sensor's layer row,
not a radius around the gate.

**Why.** A 1.2 m sphere always contained keypads, panels and the player, so the doorway was
"occupied" forever. The `AntiCrasher` *is* the game's test.

**Enforced in.** `NpcAgent.DoorwayBlocker` via `GameInternals.GateAccess`.

### an-npc-loads-rooms-by-the-door-rule

**Rule.** An agent switches a room's content on only as the game does for the player: the room it
is in, the room behind a doorway while that door is open, both rooms of a door it opens, and a room
its brain asks for. It never loads the room behind a shut door it only walks past. It switches
rooms off only as the game does when the player fully leaves one: once a door it shut has finished
closing, away from the player, **both** rooms that doorway joins go off, whoever switched them on.
A room stays on while any active NPC or the player is in it, while the player is outside, while a mod
keeps it ([a-kept-room-stays-loaded](#a-kept-room-stays-loaded)), or while another open door looks into
it. An agent's room is its tracked room. The release is done by the agent that shut the door, and
`EntryDetector`'s side test may only overrule its tracked room for the two rooms of its own doorway.
Never across a doorway the game keeps loaded (`optimize` off).

**Why.**
- It used to load both rooms at every doorway within 3.5 m, open or shut, and switch none of them
  off. A walk down a corridor left every side room loaded. The game kept running them and saved them
  as loaded (`Room.Data.enabled`), so every load brought them back.
- The game's own unload on that close never runs, because an NPC's close away from the player is
  hidden from `EntryDetector` ([npc-closes-must-not-move-the-player](#npc-closes-must-not-move-the-player)).
- "Only rooms the NPC loaded" is no test: a room restored from a save is nobody's.
- "Only the room it left, by the side test" failed twice:
  - A late close was judged from a third room. The YardCryo door's plane puts most of YardHallway on
    YardCryo's side, so it switched off YardLibrary.
  - A room kept for a door still closing was never looked at again.

**Enforced in.** `NpcAgent.UpdateRoomTracking`, `LoadRoom`, `ReleaseRoomsAt` / `ReleaseRoom` /
`ReasonToKeepLoaded` (with `NpcRegistry.OtherTrackedIn` and `NpcRooms.ReasonToKeep`),
`LoadRoomsAroundGate`, `SetForcedRoom`; the `EntryDetector` postfix, which finds the closer through
`NpcDoors.WasClosedByNpc` and calls its `INpcDoorUser.OnDoorClosedAway`.

### a-kept-room-stays-loaded

**Rule.** A room an `IRoomKeeper` gives a reason for is never switched off, by the game or by an NPC.
One `Room.SetContentEnabled` prefix asks every keeper; a walker releasing rooms asks
`NpcRooms.ReasonToKeep` too.

**Why.** A mod needs rooms loaded that no NPC stands in: `FindObjectsOfType` skips a switched-off
object, so YourBuddy's selling run found "no sell station within 80m" of four boxes. A prefix that
refuses a call is exactly what two mods must not each add ([one-patch-per-game-hook](#one-patch-per-game-hook)).

**Enforced in.** `Patches.KeptRooms`, `NpcRooms.AllowSwitch` / `ReasonToKeep`.

### a-fenced-sell-station-is-not-somewhere-to-stand

**Rule.** No NPC stands inside a sell station's fences (the `Fence*` colliders plus the item zone,
plus `SellStationClearance`), except where it works that station itself. An NPC stuck inside a pen is
moved clear, never into another.

**Why.** At the ShipyardStation the fences leave a 0.7 m pocket behind a 0.8 m rail. An NPC following
the player got caught there. The stuck detector counts not moving, and sliding along a rail is moving, so
the pen joins the no-progress check. The fence colliders are never disabled: the player would walk
through them too.

**Enforced in.** `SellPens.InAFencedPen`; `ReachTask.StandAllowed`, `NpcAgent.PennedIn` /
`EmergencyUnstick`; YourBuddy's `SellTask.StandAllowed`.

---

## Game state

### aboard-is-answered-by-the-floor

**Rule.** "Is this aboard the player ship?" is answered by the floor under it, never a tracked
room. The answer is tri-state: no floor to judge by is not "not the ship".

**Why.** The game's room is the last doorway crossed and can be stale for minutes
([game-model.md §2](game-model.md#2-there-are-no-room-volumes)).

**Enforced in.** `NpcVessels.FloorOwner`; each `INpcLifeform.IsAboardPlayerShip`.

### an-npc-rides-its-own-floor

**Rule.** An agent is parented to the frame of the floor under it - a station's `contentParent`,
the world container, or the scene root for the player ship - and every remembered position (safe
spot, a mod's save) is stored in that frame. Owners are stored by GameObject name.

**Why.** The game moves the world, not the ship. An unparented NPC stayed behind on undock, fell
into "open space" and was teleported to the player. Parenting also parks the NPC for free: the
game deactivates `contentParent` when a station unloads.

Two special cases:
- **Undock:** an agent in the undocking station's collar goes aboard instead of being parked in the
  airlock. Judged in the prefix, before the collar's colliders switch off.
- **Ship rebuild:** `SetShipLevel` moves some rooms 3.75 m. An agent aboard moves with its room; one
  whose room is gone stays and is left to space protection. The plan is dropped, the brain ends a walk
  to an old world point (`INpcBrain.OnShipRebuilt`), and the safe spot becomes the new position, or is
  forgotten when its floor is gone.

**Enforced in.** `NpcAgent.UpdateOwnerAnchor`, `NpcVessels.FloorOwner`, `RememberSafePosition` /
`TryRecallSafePosition`, `RideOwner`, `RideShipRebuild`; `Patches.Docking`, `Patches.ShipRebuild`;
YourBuddy's `BuddySaveFile.Owner` / `OwnerLocalPosition`.

### an-unloaded-ship-parks-the-npc

**Rule.** When the ship unloads for a spacewalk, every agent aboard is parked (`SetActive(false)`)
before any room goes dark, and woken when the ship loads. A parked agent keeps no room content
loaded.

**Why.** The NPC's room listener re-enabled the bridge, whose `Autopilot` trigger then unloaded the
wreck the player had gone out to - no collision outside.

**Enforced in.** `Patches.ShipContent`, `NpcAgent.ParkWithShip` / `UnparkFromShip`, the
`isActiveAndEnabled` guard in `OnForcedRoomContentChanged`.

### a-carried-item-is-put-down-before-a-save

**Rule.** A held item is never parented to the NPC, and is put down (physics, `restrictGrab`,
colliders restored, `SavePosition`) on `SaveParser.OnFileSaveInitiated` and whenever carrying ends.

**Why.** Saved while held it would keep frozen physics, a mid-air position, and - if parented - a
parent name that does not exist in a vanilla load.

**Enforced in.** `NpcHands.PickUp`, `NpcAgent.OnGameSaving`, `NpcHands.Drop` (from `Die`,
`ParkWithShip`, `OnDestroy`, and a mod's own ends of carrying).

### a-carried-item-belongs-to-the-room-its-carrier-is-in

**Rule.** A held item's parent is the `ContentParent` of the carrier's current room, re-owned through
`Grabbable.SetParent` whenever they differ, and always before `SavePosition`. An item already
inactive (`activeSelf` false) is left alone - a trash can or a sale destroyed it.

**Why.** `EntryDetector.CheckItemParents` skips items with `restrictGrab`, so a carried box kept its
old room, went inactive when that room was culled, and vanished from the NPC's hands.

**Enforced in.** `NpcHands.ReownToCarrierRoom`, from `Follow` and `Release`.

### player-icon-fix-is-one-directional

**Rule.** The `UpdatePlayerIcon` prefix may only turn the icon **on**, and only when the floor under
the player belongs to the player ship.

**Why.** It fixes a game bug ([lifecare.md §3](lifecare.md#3-the-missing-player-icon-game-bug-worked-around))
without taking over the display, so no scripted event can be masked.

**Enforced in.** `NpcLifecare.FixPlayerIcon`.

### mirror-the-vanilla-lifeform-clamp

**Rule.** `RecountLifeforms` reproduces the game's clamp, `(breathless || any temp) ? 1 : 0` plus
the player, then adds each NPC icon shown on that display.

**Why.** Summing separately over-reports during scripted events that use both.

**Enforced in.** `NpcLifecare.RecountLifeforms`.

### mod-state-never-enters-the-vanilla-save

**Rule.** NPC state goes in sidecar files registered with `NpcSaves`. Never attach a game
`SaveObject<T>` to anything a mod creates, never write `SceneLoader.Instance.SaveData`, and never
call a game method that writes save state for an NPC (`Breathless.Enabled`, `SetAggressive`).

**Why.** Uninstalling a mod must leave saves intact. That is why a corpse is carried by `NpcCorpse`,
not the game's `Grabbable` (which allocates a save ID), and why the AI overrides use component flags.

**Enforced in.** `NpcSaves`, `NpcCorpse`, `NpcMonster`, each mod's own sidecar.

### plugin-objects-outlive-the-first-scene

**Rule.** A GameObject a plugin builds at load is made with `NpcCorePlugin.PersistentObject`
(`DontDestroyOnLoad` and `HideFlags.HideAndDontSave`), and whoever needs it checks it is still alive and
makes it again if not, logging that it did.

**Why.** Objects built in a plugin's `Awake` are destroyed before the first game tick; `DontDestroyOnLoad`
alone does not keep them. The talk window and the node editor were each built once in `Awake` and were
simply missing in game. Who destroys them is not known, which is why the re-make stays even with the flags.

**Enforced in.** `NpcCorePlugin.PersistentObject` / `EnsureNodeEditor` / `NodeEditor`,
`NpcTalkWindow.Ensure`, both called from the `Tick` patch.

---

## Several NPC mods

Every NPC mod builds on one NPC.Core, and whatever NPCs must agree on lives in it.

### one-core-instance

**Rule.** `NPC.Core.dll` exists once, in `BepInEx/plugins`. A mod references it with `Private=false`
and declares `[BepInDependency(NpcCorePlugin.Guid, NpcCorePlugin.Version)]`; it never ships a copy.

**Why.** The registry, the nav graph and every scene cache are static. A second copy is a second
world: its NPCs would be invisible to the first copy's, walk into them and take their targets.

**Enforced in.** `NpcCorePlugin.Guid` and `Version`, each mod's `BepInDependency`. NPC.Core warns
at load about another `NPC.Core.dll` under `BepInEx/plugins`, and logs an error on the first tick for
any assembly that carries its own copy of NPC.Core's types (`NpcCorePlugin.CheckForMergedCopies`).

### npcs-never-block-each-other

**Rule.** Every NPC of every mod is registered with `NpcRegistry` while it exists. Every `NavProbe`
filter and an agent's own body probes (whiskers, auto-jump, stuck diagnostics, low ceiling) treat a
registered body, alive or dead, as a body - never floor or wall. The controllers and solid parts of any
two NPCs ignore each other, an NPC's controller ignores its own solid parts, and the player's controller
and head/foot triggers ignore every NPC's (`NpcRegistry.IgnoreAllBodies`). Pairs a switched-off collider
lost are restored within a second. An idle agent standing within
`NpcSpacing` of one that ranks before it (lower number, then the mod that attached first) steps
aside; the other stays.

**Why.** The probes ignore bodies. A controller that still collided with one would walk into an NPC
its whiskers cannot see, and two NPCs in one doorway would wedge each other. The player and the NPCs
pass through each other for the same reason; the player's head trigger counted an NPC's item blocker
as a ceiling and crouched the player beside it. The game switches the player's controller off for every
teleport, and an agent switches its own off to teleport and park. The body probes once skipped only the NPC's own body and
the player's, so an NPC ahead read as low furniture - shin blocked, head clear - and the one behind
jumped. Without the spacing, two NPCs that stop at the same spot stand inside each other; only one
moves, or both would step apart forever.

**Enforced in.** `NpcRegistry.IsNpcBody` / `IgnoreBodies` / `IgnoreAllBodies` (from `NpcAgent.OnEnable`,
`Start` and `Die`) / `KeepBodiesApart` (the shared tick), `NpcPlayer.IgnoreNpc`, `NavProbe.BelongsToABody`, `NpcAgent.IsIgnorableCollider`, `INpc.IsOwnBody` /
`SolidParts`, `NpcAgent.TrySpaceOut` / `RanksBefore`.

### one-npc-per-target

**Rule.** What another NPC - of any mod - is working on is not a candidate. The claim is read from
that NPC's live state (`INpc.Holds`), never stored.

**Why.** Two NPCs raced for one item. A stored claim can outlive the task that made it; a derived one
cannot.

**Enforced in.** `NpcRegistry.TakenByAnother`, each mod's `INpc.Holds`.

### rooms-stay-loaded-for-every-npc

**Rule.** A room any registered NPC is tracked in (`INpc.TracksRoom`) is not switched off by another.

**Why.** One mod would unload the room another mod's NPC stands in, and the floor content with it.

**Enforced in.** `NpcRegistry.OtherTrackedIn`.

### one-owner-per-console-command

**Rule.** Console commands go through `NpcConsole.Register`. A name already taken - by the game,
NPC.Core or another mod - is refused and logged with its owner, never overwritten. NPC.Core owns
`npc_*`, `node_editor`, `debug_level`, `ai_disable` and `ai_notarget`; a mod prefixes its own.

**Why.** The game's command table is one dictionary written by indexer: the last mod to load silently
replaced another's command.

**Enforced in.** `NpcConsole.Register` / `Install`.

### one-patch-per-game-hook

**Rule.** A game method every NPC mod needs a hook on is patched once, by NPC.Core, applied per feature
group so one missing target costs one feature.

**Why.** Two mods patching one method with a prefix that skips it, or one that swaps a private field
and restores it in a postfix, break each other depending on load order.

**Enforced in.** `NPC.Core.Patches`, `NpcCorePlugin.ApplyPatches`, `NpcEvents`.

### npc-code-runs-acting

**Rule.** Every entry into an NPC's code - a Unity message, a game event, a patch, a command - opens
`NpcRegistry.Acting(npc)`, and no scope outlives a `yield`. `NpcLog.Log` then writes to that NPC's
source, `<ModName>:<Name>`.

**Why.** Static code such as `FindPath` logs for whoever called it. Without the scope a capture with
two NPCs cannot tell whose route a line was.

**Enforced in.** `NpcRegistry.Acting` / `ActingLog`, `NpcLog.Log`.

### door-knowledge-is-shared

**Rule.** Which gates an NPC may open and which block routing, the doorway, pin panel and airlock
caches behind those answers, and the door codes are one set for every NPC of every mod, in `NpcDoors`.
`NavGraph.SegmentBlockedByDoor` is bound once, at NPC.Core's load. The caches reset on every scene
change; the codes reset with their `GameManager` and are saved in NPC.Core's own sidecar.

**Why.** Bound in each NPC's `Start` and cleared in its `OnDestroy`, the delegate belonged to one NPC:
despawning it switched door routing off, and the others planned through locked doors. Per-NPC caches
also repeated the same scene sweeps once per NPC.

**Enforced in.** `NpcDoors`, `NpcCorePlugin.Awake`, `Patches.GameStart` / `Load` (`ResetScene`),
`CoreSidecar`.

### one-catch-at-a-time

**Rule.** The Breathless grabs one NPC at a time. A mod starting a catch asks
`NpcMonster.CatchingAnother(me)` first, calls `BeginCatch` when it starts and `EndCatch` when the catch
ends, the NPC dies or it is destroyed.

**Why.** The monster's animator follows one target at a time (`Animator.Follow`): a second catch
would pull it off the first mid-grab. A parked NPC's catch stopped with it, so it does not hold the
monster.

**Enforced in.** `NpcMonster`; `NpcAgent.BreathlessCheck` / `CatchRoutine` / `Die` / `OnDestroy`.

### the-ai-overrides-have-one-owner

**Rule.** `ai_disable` and `ai_notarget` are NPC.Core's. Their flags live in `NpcMonster` and are
re-asserted every tick; a mod reads them (`NpcsDisabled`, `MonsterDisabled`, `PlayerIgnored`) and never
writes the monster's component flags itself, except a catch borrowing the aggressor, which restores it
only while `PlayerIgnored` is false. A save load clears them.

**Why.** A coroutine that saved and restored a flag silently undid a command. Two mods each owning the
flags would fight every tick.

**Enforced in.** `NpcMonster.Apply` / `ResetOverrides`, `CoreCommands`, `Patches.MonsterDebug`.

### one-interact-owner

**Rule.** NPC.Core alone listens to the game's Interact key for NPCs, and hands the player's input to
the UI and back. A mod makes an NPC talkable with `NpcInteraction.Register`; it never subscribes to
`InputHandler.OnInteract` or switches the input map itself.

**Why.** Two listeners on one press open two windows, and each switches the input map: the first to
close hands the game its input back while the other is still open.

**Enforced in.** `NpcTalkWindow.Update`, `NpcInteraction.OnInteractPressed` / `SetPlayerInUi`.

### one-sidecar-per-mod

**Rule.** Each mod keeps its state in its own sidecar, registered with `NpcSaves.RegisterSidecar`
under an extension no one else has. NPC.Core writes, deletes and prunes every sidecar with its save;
a mod only says what goes in it.

**Why.** The sidecar lifecycle patches (write, delete, clear, prune) are hooks every mod needs
([one-patch-per-game-hook](#one-patch-per-game-hook)). A shared file would need a shared format; an
extension taken twice would have one mod delete the other's state.

**Enforced in.** `NpcSaves`, `Patches.Saves`.

## Frame cost

### read-only-panels-build-on-repaint

**Rule.** An IMGUI panel that only draws returns unless `Event.current.type` is `Repaint`, and costly
text is cached behind a TTL. A panel with controls (`NpcTalkWindow`) must run on every event - cache
its strings instead.

**Why.** `OnGUI` runs twice per frame. The status HUD rebuilt its text on both and was most of the
mod's allocation, causing a GC every few hundred ms.

**Enforced in.** `NodeEditor.OnGUI`, YourBuddy's `BuddyBehaviour.OnGUI` / `StatusText`,
`NavGraph.DescribeNodes`.

### unity-name-reads-allocate

**Rule.** Never read `Object.name` inside a per-element loop that runs every frame. Resolve names
once and cache the map, keyed on something that actually changes.

**Why.** Each `name` read allocates a new string. Room and owner lookups by name were the mod's
largest allocation after the HUD. `SpaceShip.rooms` is a fixed array, so its reference is the key.

**Enforced in.** `NavGraph.ShipRoom` (`RoomsByName`), `RefreshSpaceObjects` /
`FindOwnerObject` (`SpaceObjectNames`).

### scene-sweeps-are-budgeted

**Rule.** A cache that re-runs `FindObjectsOfType` on a timer asks `SceneScan.MayRescan` first. A
sweep made for one decision reads `SceneScan.ThisFrame`. Only a first fill or a forced rescan
(`InvalidateGates`, a destroyed entry) skips the budget.

**Why.** Each sweep costs milliseconds. Expired 5 s caches could all rescan inside one `FindPath`
call, and an errand's `Count` and `TryStart` swept the scene twice in one frame. Both landed on the
mod's slowest frames.

**Enforced in.** `SceneScan`; `NavProbe.EnsureGates`, `NavGraph.RefreshSpaceObjects`,
`NpcDoors.RefreshDetectors` / `RefreshAirlocks` / `RefreshPasswordGates`, `SellPens.EnsurePens`;
`NpcAgent.RefreshEnvironments` / `TrackFootstepDoors`; YourBuddy's errand collectors.
