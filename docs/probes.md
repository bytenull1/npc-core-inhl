# NavProbe - the physics layer

`NavProbe.cs` owns every navigation probe: floor heights, line of sight, and which colliders count
as solid. Nothing else may raycast for navigation - [one-probe-basis](invariants.md#one-probe-basis).

---

## API

| Member | Answers |
|---|---|
| `TryFloorHeight(p, out floorY)` | how high is the ground under `p`, and is there any |
| `FloorHeight(p)` | same, falling back to `p.y` |
| `TryFloorCollider(p, out collider)` | *what* that ground is - how `NpcVessels.FloorOwner` names the vessel |
| `WalkLos(a, b, maxDeltaY)` | can a body walk this line - knee height, fine samples, ramps ignored |
| `GroundIsContinuous(a, b, maxStep)` | does the floor under this line ever break |
| `HitIsWalkableGround(hit, dir, feetY, stepOffset, slopeLimit)` | will the controller simply walk over this |
| `ThinLos(a, b, maxDeltaY)` | can a body walk this line, with a deck-change cap |
| `CanSee(from, to, target, out shutGate)` | can an **eye** see along this line; shut gates block it. Not for navigation ([sight-stops-at-a-shut-door](invariants.md#sight-stops-at-a-shut-door)) |
| `DistanceToSegment(p, a, b)` | 3D point-to-segment distance |
| `IsDoorGeometryNearOpenGate(collider, hitPoint)` | should whisker steering push through this |
| `ProbeLayers` | layers the walker's body collides with; an agent's own body probes use `BodyMaskFor` of its controller's layer ([probe-what-the-body-collides-with](invariants.md#probe-what-the-body-collides-with)) |
| `Gates` | cached gate list - use it instead of `FindObjectsOfType` |
| `SegmentCrossesGate(gate, a, b, pad)` | does this stretch pass *through* that gate |
| `InvalidateGates()` | force a gate rescan |
| `DescribeGates()` | one line per gate: its carve-out and why |

### How a hit is judged

`IsEdgeProbeIgnorable` (private) filters every hit. The first check that answers wins:

```
hit collider
 ├─ null ............................................ ignore
 ├─ IsBodyCollider: the NPC or the pilot ........... ignore
 ├─ parent Docker / Airlock / Gate ................... ignore
 └─ HitIsGateOpening(hitPoint, requireOpenGate)
     EnsureFrameGates, once per frame: alive, active, OpeningOf(gate).IsPassage
     for each frame gate:
       requireOpenGate and not fully open ........... next gate
       |hit.y - gate.y| >= GateOpeningHalfHeight .... next gate
       LocalAxes: |depth| >= GateOpeningHalfDepth ... next gate
                  across = |dot(delta, right)|
       else:      across = XZ distance to the gate
       across >= HalfWidth .......................... next gate
       chord given and it crosses the gate's plane
         outside the opening ........................ next gate (audited)
       else ......................................... ignore (audited)
     no gate matched ................................ SOLID
```

The **chord** is the whole line a sight probe is testing, passed only by `WalkLos` and `ThinLos`.
Without it the hit point alone decides, which lets a line clip a doorway's jamb and cross the wall
beside it unseen ([a-doorway-is-crossed-not-grazed](invariants.md#a-doorway-is-crossed-not-grazed)).

| Caller | Entry point | Notes |
|---|---|---|
| `WalkLos`, `ThinLos` | `IsEdgeProbeIgnorable` + chord | `requireOpenGate: false` - doors open at walk time |
| `TryFloor` | `IsBodyCollider`, then `IsPassableInterface` | no gate-frame rule ([floors-ignore-the-gate-frame-rule](invariants.md#floors-ignore-the-gate-frame-rule)); the highest solid hit is the fallback ([doorway-floor-survives-the-filter](invariants.md#doorway-floor-survives-the-filter)) |
| `TryAnyLayerFloor` | `IsBodyCollider` | bodies only |
| `CanSee` | `IsBodyCollider`, then `IsDoorGeometryNearOpenGate` | shut gates checked before the cast |
| whiskers | `IsDoorGeometryNearOpenGate` | `requireOpenGate: true`; without it the NPC circles open doorways |

---

## Which layers a probe sees

`ProbeLayers` comes from the game's collision matrix - the row for the player's
`CharacterController` layer. An agent's own body probes take the row for its own controller's layer
(`BodyMaskFor`) ([probe-what-the-body-collides-with](invariants.md#probe-what-the-body-collides-with)).

For `Player` that is `Default`, `Water`, `EntitySensor`, `Sensor`, `Furniture`, `Preview`,
`Walkable`. Left out: `Interactable` (click targets - lockers' outer collider, buttons, pin panels,
the hatch lid), `Grabbable`, `SpaceObject`, `Presser`, `Decal`, `PlaceRestriction`, `UI`,
`Ignore Raycast`. Solid furniture always has a collider on `Furniture` too.

With no NPC spawned (node editor), the mask falls back to the player's layer, then `Player` by
name, then `DefaultRaycastLayers` minus `PlaceRestriction`. A row without `Default` is not a body's
row and is rejected.

`debug_level 2` logs the resolved mask once per layer - start here when the NPC walks through
something or stops at nothing:

```
[probe] probe mask: body on layer 'Player' (3) is stopped by Default, Water, EntitySensor,
        Sensor, Furniture, Preview, Walkable; walks through TransparentFX, Ignore Raycast, ...
```

---

## Floor probing

`TryFloorHeight` casts down from `p + 2 m` over 6 m and takes the **highest** acceptable surface at
or below `p`:

- a body is never floor, and neither is a collar, airlock or door leaf. The gate-frame rule is not
  applied ([floors-ignore-the-gate-frame-rule](invariants.md#floors-ignore-the-gate-frame-rule));
- if the filter rejects everything, the highest solid non-body hit is used
  ([doorway-floor-survives-the-filter](invariants.md#doorway-floor-survives-the-filter));
- if nothing was hit, it casts once more across every layer
  ([a-missing-floor-is-a-last-resort-not-an-answer](invariants.md#a-missing-floor-is-a-last-resort-not-an-answer));
- `false` means *no floor to judge by*, not "floor at `p.y`". Don't act on it.

This is the reference height for every vertical comparison ([floor-to-floor](invariants.md#floor-to-floor)).

> **The probe is not the authority on where the NPC stands.** In a doorway it can answer two
> storeys down. A grounded controller is the ground truth -
> [a-grounded-NPC-stands-on-its-own-feet](invariants.md#a-grounded-npc-stands-on-its-own-feet).

---

## Walkable ground

A hit is not automatically an obstacle. `HitIsWalkableGround` asks whether the controller can
traverse it, using the NPC's own `stepOffset` and `slopeLimit`
([walkable-ground-is-not-an-obstacle](invariants.md#walkable-ground-is-not-an-obstacle)):

- **a slope** within `slopeLimit` - every staircase;
- **a step** no higher than `stepOffset`, with the tread above clear (this stops a wall's base
  passing as a step).

| Probe | Band above feet | Reach | Blind to slopes below |
|---|---|---|---|
| whisker `BodyBlocked` | 0.25 m to the body's top ([whiskers-are-body-shaped](invariants.md#whiskers-are-body-shaped)) | 0.45–0.95 m | 14.7° |
| `TryAutoJump` shin ray | 0.25 m | 0.70 m | 19.7° |
| `LogBlockingCollider` | same as the whiskers | 0.95 m | 14.7° |

A diagnostic must see at least what the probe it explains sees. Stuck reports print `rise=` and
`slope=` per hit and name any walkable ground they ignored.

The whisker band is horizontal, so from a landing it hits the underside of the flight above. That is
why whiskers, auto-jump and the stuck sidestep are off on stair legs
([a-stair-leg-is-walked-not-improvised](invariants.md#a-stair-leg-is-walked-not-improvised)).

---

## Line of sight

`ThinLos` is **thin and floor-hugging**: it samples the line and casts between eye points 1 m above
the local floor at each sample. A straight chord would cut through stairs and slopes. The
`maxDeltaY` cap compares the **floors** at the ends, never the endpoints themselves
([floor-to-floor](invariants.md#floor-to-floor)).

Walls, tall railings and tall furniture block it; anything under 1 m does not. The finish leg and
short clear goal therefore add knee-height `WalkLos`
([an-endpoint-must-be-walkable-not-merely-visible](invariants.md#an-endpoint-must-be-walkable-not-merely-visible)).

### Sight is a different question

`ThinLos` and `WalkLos` forgive a gate's opening whether or not it is shut. `CanSee` is one straight
cast, blocked by any gate that is not open, forgiving only frame trim in a fully open doorway. Never
use it for routing ([sight-stops-at-a-shut-door](invariants.md#sight-stops-at-a-shut-door);
caller: [fear.md §2](https://github.com/bytenull1/yourbuddy-inhl/blob/main/docs/fear.md#2-seeing-the-monster)).

---

## The gate-frame rule

A hit counts as *doorway*, not *wall*, when the contact point lies inside the gate's **opening**, in
the gate's own frame:

| Axis | Bound | Value |
|---|---|---|
| local x - across | `openingHalfWidth` | measured; 0.85 m for a stock door |
| local y - vertical | `GateOpeningHalfHeight` | 2.5 m |
| local z - through the wall | `GateOpeningHalfDepth` | 1.0 m |

Sight lines and whiskers use it; floor rays never do
([floors-ignore-the-gate-frame-rule](invariants.md#floors-ignore-the-gate-frame-rule)).

Only gates with `gateAnchors` **and** a leaf to slide count. `ElectricityPanelGate` (anchors: two
empty placeholders and itself) carves nothing. Never test leaf *size*.

Rules: [gate-frame-hit-point](invariants.md#gate-frame-hit-point),
[gate-carveout-is-the-opening](invariants.md#gate-carveout-is-the-opening).

### Where the opening comes from

`Gate.OpenRoutine` slides anchors 0–1 along local x and 2–3 along local y by `openedWidth`, so the
gate's transform is the passage frame. Half-width = the span the leaves cover while shut, plus
`GateOpeningMargin`. When open, a side leaf's stroke is subtracted, so the answer is the same either
way.

- **Ship door:** `[DoorL, DoorR]`, two 0.562 m half-leaves that part sideways.
- **Station door:** `[Empty, Empty, Door]`, one 1.125 m leaf that slides **up**; `openedWidth` is
  1.70 m of vertical travel, not a width.

`MeasureOpening` runs once per gate, only while it is settled (not mid-animation). The result is kept
until the gate set changes, stored as scalars in the gate's frame, never as world bounds.

Fallbacks, all permissive: anchors that are not the gate's children → a cylinder of the same width;
`gateAnchors` unreadable → every gate a 0.85 m cylinder passage. Half-width is clamped to
`[GateOpeningRadius, MaxGateFootprintRadius]`. Every door measures 0.5625 m and lands on the 0.85 m
floor; the `SellStation` shutter (1.06 m) is the only exception.

Docking collars and airlocks are handled by hierarchy: any hit with a `Docker`, `Airlock` or `Gate`
parent is ignored outright.

### The audit

The rule only ever makes probes more permissive, so the risk is a real wall inside an opening.
At `debug_level 2`:

- every ignore is logged, **deduped per collider × gate pair** (a global throttle hid the bug):

  ```
  [probe] Gate-frame rule ignoring a hit on 'wall_long_door' 1,10m across gate
          'Door01RightCore' (opening 0,85m) - if that point is wall rather than
          doorway, this gate's opening is too wide.
  ```

- every chord-test rejection is logged the same way, and names the doorway the NPC will now walk
  around rather than through:

  ```
  [probe] Gate-frame rule keeping a hit on 'BlockWall' 0,62m across gate 'Door02'
          (opening 0,85m): the line only grazes the jamb and crosses the wall elsewhere.
  ```

- a **gate inventory** is dumped after each gate rescan, and by `npc_gates`:

  ```
  [probe] gate 'Core_M01/Structure/wall_long_door/Door01LeftCore' pos(1,57,0,00,0,63)
          2 leaves 1,44m tall -> opening 0,85m slot depth 1,00m
  [probe] gate 'Core_M01/Core_M01Content/Breaker/ElectricityPanelGate' pos(0,00,0,54,-1,25)
          3 leaves 0,00m tall -> no leaves, not a door - carves nothing
  ```

  `N leaves` is the anchor count; leaf height is for diagnosis only.

### Reading a doorway complaint

Start with `npc_gates`.

| Symptom | Look for | Meaning |
|---|---|---|
| walks **through** a wall | `FindPath: goal is …m and clear`, then the audit naming that wall | some gate's opening reaches it |
| paces at a wall **beside** a doorway, on a plan whose leg crosses it diagonally | a `keeping a hit` line for that wall and gate | the chord test is doing its job; the plan predates it, or the leg is genuinely the only one |
| paces in front of an **open** doorway | `Blocked by '…' … distance 0,3` at the door; waypoints beyond it barred | that gate carves nothing, or too little |

A door reported with `0 leaves` means the passage test misfired. A passage that still fails needs a
wider `GateOpeningMargin` or `GateOpeningHalfDepth`, never a looser passage test. Docking-collar
problems belong to the `Docker`/`Airlock` early-out, not this rule.

---

## Buffers

Every query uses `*NonAlloc` on shared 64-hit buffers. A full buffer drops hits arbitrarily, and 8
was not enough in the docking corridor. `WarnIfTruncated` logs a full buffer
([probe-buffers-must-not-truncate](invariants.md#probe-buffers-must-not-truncate)).

---

## Caches

- **Gate list:** 5 s TTL, plus `InvalidateGates()` on scene load and dock change (these change the
  *set* of gates). A timed rescan waits for the frame's sweep budget; a forced one does not
  ([scene-sweeps-are-budgeted](invariants.md#scene-sweeps-are-budgeted)).
- **Gate openings:** measured once, kept until `InvalidateGates()`. Stored relative to the gate,
  never as world bounds - the world moves around the ship.
- **`FrameGates`:** each gate's world placement, for **one frame** only. It moves native property
  reads (`transform.position`, `FullyOpened`, …) out of the per-hit loop, which was the mod's largest
  self cost. One frame is the longest a world position may be held
  ([never-cache-node-world-positions](invariants.md#never-cache-node-world-positions)). Sound only
  because every caller runs from `Update`, never from the fixed step.
- **Collider kinds:** whether a collider is a body (`IsBodyCollider`) or a passable interface
  (`IsPassableInterface`), per instance, for one frame. Both walk the hierarchy, and one plan asks
  about the same deck and walls hundreds of times.
- **Floor answers:** `TryFloor`'s result per exact position, for one frame. A plan re-floors its
  start for every entry candidate. Keys are exact, never quantised: a rounded key could hand a point
  the floor of its neighbour across a stair edge. Skipped in the fixed step, where a physics step would separate the probe from `Update`.
