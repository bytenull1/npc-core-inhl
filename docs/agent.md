# The walking agent - `NpcAgent`

`Agents/`: an NPC that walks the nav graph on a body your mod built. It plans and follows routes,
steers, hops low furniture, opens and closes doors ([doors.md §6-10](doors.md#6-opening)), loads rooms,
rides vessels, stays out of space unless suited, floats and flies in zero gravity (§8), breathes, can be
caught and dies. It does not decide where to go: its **brain** does, through `INpcBrain`.

NPC.Core never builds a body. YourBuddy's is the player prefab cloned and stripped
(`YourBuddyPlugin.SpawnBuddy`); a mod with its own locomotion can skip the agent and implement `INpc`
itself ([api.md §2](api.md#2-registering-an-npc---npccore)).

| File | Owns |
|---|---|
| `NpcAgent.cs` | state, `Attach`, the faces NPC.Core sees (`INpc`, `INpcDoorUser`, `INpcLifeform`, `INpcHider`), `Update` / `SlowUpdate`, `FloorUnderNpc`, `GroundPos` |
| `NpcAgent.Navigation.cs` | the walks (§5): `Pursue`, `Wander`, `Stay`, the plan API, stair legs, step-off, spacing, idle watch |
| `NpcAgent.Obstacles.cs` | body probes, whisker steering, auto-jump, `ApplyMovement`, stuck and no-progress recovery (§4), `EmergencyUnstick`, blocker diagnostics |
| `NpcAgent.Doors.cs` | opening, pin panels, close-behind, `NoteCloseFailed`, `DoorwayBlocker`, `StepOutOfDoorway` |
| `NpcAgent.Environment.cs` | room tracking, loading and release, floor plane, vessels (`UpdateOwnerAnchor`, park, rebuild, undock), the side of the airlocks, space protection, air, the catch, `Die` |
| `NpcAgent.Flight.cs` | zero gravity (§8): `Gravity`, `Floating`, the flights, the flight frame, recovery |
| `NpcAgent.Presentation.cs` | footsteps, animator parameters, debug lines, facing |
| `NpcAgent.Reach.cs`, `ReachTask.cs` | the walk into reach (§6) |
| `NpcAgent.Carry.cs`, `NpcHands.cs` | the hands (§7) |
| `INpcBrain.cs`, `NpcActivity.cs` | the brain's contract (§3, §4) |
| `NpcAgentSettings.cs`, `NpcBody.cs` | what it may do; the body and who it is (§1) |

The docking, ship rebuild and ship unload patches (`Patches.cs`) reach every agent through
`NpcRegistry` ([an-npc-rides-its-own-floor](invariants.md#an-npc-rides-its-own-floor),
[an-unloaded-ship-parks-the-npc](invariants.md#an-unloaded-ship-parks-the-npc)).

---

## 1. Attaching an agent

```csharp
GameObject root = BuildBody();          // yours; needs a CharacterController; keep it inactive
MyBrain brain = root.AddComponent<MyBrain>();   // implements INpcBrain
NpcBody body = new() { ItemBlocker = blocker, Ragdoll = ragdoll, FootstepEvents = NpcPlayer.FootstepEvents() };
NpcAgent agent = NpcAgent.Attach(root, new NpcIdentity("MyMod", "Guard", 1), body, new NpcAgentSettings(), brain);
NpcRegistry.Register(agent);            // before activating: OnEnable finds the others to pass through
root.SetActive(true);
```

The agent **is** the NPC: register it, not the brain. Its log source is `ModName:Name`; everything it
calls on the brain runs inside its acting scope. It unregisters itself in `OnDestroy`.

**`NpcIdentity`**: `Name` is unique in your mod. `Number` staggers the slow phases and the first replan
(`Number % 4`), so NPCs spawned together do not spend the same frames, and settles who steps aside:
the lower number, then the mod that attached first.

**`NpcBody`**, every part optional:

| Part | Used for |
|---|---|
| `Ragdoll`, `RagdollBody` | shown and pushed on death; made carryable by `NpcCorpse`. Put its colliders on `Grabbable`, as the player's ragdoll is: `Default` collides with neither `Default` nor `Walkable`, so the corpse falls through the floor |
| `AnimatedModel` | hidden on death |
| `ItemBlocker` | a solid collider for thrown items, ignored by every NPC's controller and the player's; switched off on death |
| `FootstepEvents` | step sounds, `0` ship floor and `1` station floor; `NpcPlayer.FootstepEvents()` gives the player's. None: a silent walker ([game-model.md](game-model.md#footstep-sounds)) |

The body can be anything with a `CharacterController`. Its own probes (whiskers, auto-jump, stuck checks)
see what that controller's layer collides with; the graph is still walked as the player walks it
([probe-what-the-body-collides-with](invariants.md#probe-what-the-body-collides-with)). NPC.Core keeps
the player, the other NPCs and the body's own controller passing through its controller and solid parts
([npcs-never-block-each-other](invariants.md#npcs-never-block-each-other)).

**`NpcAgentSettings`**, virtual and read live, so an override can return a config entry:

| Setting | Default | Off means |
|---|---|---|
| `MoveSpeed` | 3.5 | the starting `NpcAgent.MoveSpeed`, which a mod may change later |
| `CanOpenDoors` | on | opens no door, owes no close |
| `KeepOutOfSpace` | on | not teleported back from open space |
| `Mortal` | on | neither the air nor the monster kills it |
| `CanBeCaught`, `BreathesAir` | on | the monster, or the air, leaves it alone |
| `SuitTemperatureResistance` | 100 | the suit it feels the air through ([game-model.md](game-model.md#atmosphere-kills-by-the-players-rule)) |
| `Suited` | off | the air kills it, and `KeepOutOfSpace` teleports it back when it is outside or its room or air is gone. On, it wears an isolated suit: flat 2200/2200 whatever the room, space included, and it may be outside. A fall 25 m below the player still rescues it, unless it floats ([game-model.md](game-model.md#atmosphere-kills-by-the-players-rule)) |
| `SpeedFactor` | 1 | multiplies `MoveSpeed` into `NpcAgent.WalkSpeed`, the speed every walk uses: a worn suit's `MovementSpeedModifier`, as the game slows the player (Space_Suit 0.4) |
| `CanJump` | on | never auto-jumps, as a player in an isolated suit cannot ([a-fence-is-not-hopped](invariants.md#a-fence-is-not-hopped)) |
| `MayGoOutside` | off | routing never enters an [Outdoor node](navigation.md#node-types). On, follow and goto may; flee, hide and errand plans still never do ([navigation.md](navigation.md#node-types)) |
| `CarryableCorpse` | on | no `NpcCorpse` on the ragdoll |
| `ShowOnLifecare` | on | not drawn on the lifecare terminal; a drawn icon goes at the next scan ([lifecare.md](lifecare.md)) |
| `DebugVisuals` | off | debug lines, and the level-3 obstacle report |

`Asleep` stops the agent as `ai_disable` does, gravity kept: YourBuddy's buddy in its cryo capsule.
It still closes the doors it left with `LeaveDoors` ([doors.md §7](doors.md#7-closing-behind-itself)).

---

## 2. The frame

### Update

```
dead, or no player          → nothing
ai_disable or Asleep        → gravity only; Asleep, the doors it left close
SlowUpdate                  → one phase, then brain.SlowPhase(phase)
being caught                → hold still
brain.OverrideMovement      → the brain's own move, applied as it is
Floating                    → the flight frame instead of all below (§8)
brain.Steer                 → desired velocity; HeadAlongPlan marks a stair leg
     ↓
stuck sidestep                                not on a stair leg
     ↓
brain.Constrain(desired)                      e.g. not toward the monster
     ↓
HandleDoors(desired)                          may open a gate or hold position
     ↓
TryAutoJump(desired)                          on the raw direction, before steering; not on a stair leg
     ↓
SteerAroundObstacles(desired)                 skipped mid-jump and on a stair leg
     ↓
TrySpaceOut                                   only when not moving
     ↓
UpdateIdleRecovery → UpdateStuckDetection → ApplyMovement → UpdateAnimation → UpdateDebugVisuals
```

Auto-jump reads the raw direction because steering would turn it away from the obstacle it should
hop; steering pauses mid-jump so it cannot cancel the hop. `walkingStairLeg` is reset before `Steer`
and set only by a plan walk ([a-stair-leg-is-walked-not-improvised](invariants.md#a-stair-leg-is-walked-not-improvised)).
`LateUpdate` moves a held item after the body has moved.

Atmosphere damage is not a phase: `AtmosphereTick` listens to `GameManager.OnTick` while the agent is
enabled ([game-model.md](game-model.md#atmosphere-kills-by-the-players-rule)), and only once phase 0
has read the air ([air-is-read-before-it-is-counted](invariants.md#air-is-read-before-it-is-counted)).

### Slow phases

`SlowUpdate` runs every 0.06 s, one phase per call, so each phase runs every ~0.24 s. **Don't add a
fifth phase** - it slows every phase to ~0.30 s; add work to an existing one. It runs before
`Update`'s catch and override early-outs, so its work must guard against both.

| Phase | The agent | then a brain, such as YourBuddy's |
|---|---|---|
| 0 | floor plane and owner anchor (not while floating), room tracking, environment | loads the rooms that hold a sell station |
| 1 | airlock side, open-space state and protection; floating while the world moves: pulled aboard | - |
| 2 | the Breathless catch | fear |
| 3 | door close-behind | the decider, the life-support check |

---

## 3. The brain

`INpcBrain` is what the agent asks. A brain is usually a `MonoBehaviour` on the same GameObject, and
drives the agent through its public members (§5).

| Member | Called | Should |
|---|---|---|
| `SlowPhase(phase, player)` | after the agent's phase | the brain's periodic work |
| `OverrideMovement(player, out move, out want)` | before `Steer` | true to move the body itself: a conversation holds still, climbing into a closet is a teleport |
| `Steer(player, out want)` | every frame | the step: usually one of the walks |
| `Constrain(desired, ref want)` | after the sidestep | the last word before doors and steering |
| `Activity` | wherever the agent needs the kind of walk | §4 |
| `ActivityName` | the level-3 obstacle report | the mode's name |
| `TryIdleFacing(player)` | standing still | turn with `FacePoint` / `FacePlayer`, or do nothing |
| `OnWalkAbandoned(why)` | an `EndWalk` stall, or a `ReachTask` that gave up | end or re-plan the walk |
| `Holds(t)` | for `INpc.Holds`, alive only | what its current task is about ([one-npc-per-target](invariants.md#one-npc-per-target)) |
| `Sheltered` | before a catch | true when out of the monster's reach |
| `OnInterrupted(why)` | death, parking, despawn, ship rebuild | let go of anything that holds the body in place |
| `OnDied()` | after `IsDead` turned true | its own state |
| `OnShipRebuilt()` | before the plan is dropped | end walks to old world points; true if one ended |

A brain that implements `INpcHider` answers the hiding-spot patch through the agent.

---

## 4. Recovery

`NpcActivity`, read live, tells the agent what kind of walk it is on:

| Field | Means |
|---|---|
| `Recovery` | what a stalled walk does (below) |
| `IdleWatch` | standing still above its floor plane is a stall ([idle-above-the-floor-plane-is-a-stall](invariants.md#idle-above-the-floor-plane-is-a-stall)) |
| `SpaceOut` | standing inside another NPC, it steps aside ([npcs-never-block-each-other](invariants.md#npcs-never-block-each-other)) |
| `LeavesDoorways` | standing in a doorway it owes a close, it steps clear ([doors.md §8](doors.md#8-getting-out-of-the-way)) |

**Stuck** is moving less than 0.07 m per 0.6 s while it wants to move: after 1.2 s it logs the
blocker, and each window it may sidestep 90° (never off a ledge, never on a stair leg). **No
progress** is not closing 0.35 m on the target per 0.6 s, a window that passed a waypoint not counted
([progress-is-measured-to-one-target](invariants.md#progress-is-measured-to-one-target)): after 1.2 s it logs the blocker and the inputs of
the waypoint's reached test, its speed and heading, a sidestep, and what the last door ray hit (`[ai] No progress for …`), after 3 s
it first climbs out of a sell station's pen
([a-fenced-sell-station-is-not-somewhere-to-stand](invariants.md#a-fenced-sell-station-is-not-somewhere-to-stand)),
then:

| `NpcRecovery` | Stuck 2.4 s | No progress 3 s |
|---|---|---|
| `SkipWaypoint` | with a plan: skip the waypoint ([a-skipped-waypoint-is-not-an-arrival](invariants.md#a-skipped-waypoint-is-not-an-arrival)) | the same |
| `DropPlanAndPause` | drop the plan, 1 s pause | drop it, 1.5 s pause |
| `EndWalk` | with a plan: `brain.OnWalkAbandoned("stuck")` | `OnWalkAbandoned("no progress toward it")` |
| `Pursuit` | - | bar the waypoint ([avoid-entry-bars-the-whole-search](invariants.md#avoid-entry-bars-the-whole-search)) and replan now; from the 3rd time, unstick from under a low ceiling; the 4th, step off toward the player |
| `None` | - | - |

---

## 5. Walks

A brain's `Steer` returns one of these, or its own step. Each checks the step-off first
([step-off-applies-in-every-mode](invariants.md#step-off-applies-in-every-mode)).

| Walk | Does |
|---|---|
| `Pursue(goal)` | to a goal that moves, replanning as it goes; straight at it with no plan; stands still when only a door it cannot open is in the way. The brain decides when it has arrived |
| `Wander(owner, avoid)` | to random nodes of `owner` (null: any), a short pause between; `avoid` turns nodes down |
| `Stay()` | nothing but the step-off |
| `HeadAlongPlan(speed)` | a plan the brain committed, after `AdvancePlan`, while `HasPlanLeft` |
| `SimpleAdvance()` | a committed plan straight from waypoint to waypoint, without the stair or deck rules; planned again from here when the NPC can no longer walk to its waypoint ([a-blocked-stretch-is-planned-again](invariants.md#a-blocked-stretch-is-planned-again)) |
| `TryStepOff(out v)` | the step-off under way, if any |

Floating, `Pursue` flies to its goal and `Stay` and `Wander` hover (§8), so a brain that never
thinks about space still does something sane out there.

The plan: `CommitPlan(plan, fresh)` walks it from the top (`fresh` forgets a skipped waypoint),
`AdvancePlan` passes reached waypoints ([waypoint-advance-is-dual](invariants.md#waypoint-advance-is-dual)),
`HasLeftStairLeg` says the plan is no longer walkable ([off-the-flight-is-off-the-plan](invariants.md#off-the-flight-is-off-the-plan)),
`WaypointOverdue` says the waypoint underway is taking too long ([give-up-per-waypoint](invariants.md#give-up-per-waypoint)),
`SkipWaypoint`, `DropPlan` forgets it and what walking it taught, `ReleasePlan` only lets it go (a
Pursue that arrived). `Plan`, `PlanIndex`, `HasPlanLeft` and `SkippedWaypoint` read it.

Other members a brain uses: `SetMoveTarget` / `ClearMoveTarget` / `MoveTarget`, `StepOff(target,
seconds)` / `CancelStepOff`, `BodyBlocked`, `StepsOffALedge`, `DetourAngles`, `FloorUnderNpc`,
`FloorUnderPlayer`, `GroundPos`, `OriginToFeet`, `WalkSpeed`, `TeleportTo`, `FacePoint` / `FacePlayer`,
`LoadRoom`, `OnMyVessel`, `IsAboardPlayerShip`, `IsPlayerInSpace`, `LifeInDanger` (the air is
killing it now: threat lethal, or the death counter still up), `IsOutside` / `SetOutside` (the
side of the airlocks and its gravity, [an-airlock-is-crossed-by-its-cycle](invariants.md#an-airlock-is-crossed-by-its-cycle)), `FeltTemperature`, `Die`,
`EnsureDebugVisuals`, `IsBeingCaught`, `WasMoving`, `HasMoveTarget`; `CurrentOwner` (the vessel frame
it rides) and `RideOwner(owner, anchor)` (a restored NPC starts in its saved frame);
`DescribeSurroundings` (room, air and threat, for a HUD). Constants: `FollowSameLevelDeltaY`, the deck
gap that stops a follower parking under the player
([follow-arrival-is-level-aware](invariants.md#follow-arrival-is-level-aware)).

### Pursue's planning loop

```
every NavPathRecalcInterval (0.22 s):
    is the plan still valid?   goal drift · entry stretch · on route · stair leg
        ↓ no
    FindPath(start = FloorUnderNpc(), goal, cameFrom, avoidEntry)
        seeding → A* over cached edges → finish leg or approach node
        ↓
    replace plan; index 0 on a blocked entry
every frame:
    AdvancePlan() → steer at waypoint[index]
```

Details: [navigation.md](navigation.md).

---

## 6. Walking into reach

A `ReachTask` is something to walk up to and use: its point, its object (sight ignores its colliders),
the stand points tried in front of it, how far and how high it is used from, where it may be used from
(`StandAllowed`: never inside a sell station's fences), and what a failure does (`Defer`, `Recover`).

| `ReachTask` | |
|---|---|
| `TargetPoint`, `Own` | the point in reach and sight; the object whose colliders sight ignores |
| `Name`, `Defer(seconds)`, `Recover(why)` | yours: for the log; hold this kind of task off; take a failure over (true) |
| `Reach`, `StandOffs`, `ReachBelow`, `StandAllowed(point)` | overridable: use distance, stand points, depth below the floor, where not to stand |
| `Node`, `StandPoint` | set by `FindReachNode`; set both to where the NPC stands to use it from there |
| `ReachHeight`, `ReachReplanDelay` | how high above the floor a target may be; the hold-off after a failed plan |

1. `FindReachNode` picks the nearest active node within `ReachNodeRadius` of the target with a clear
   walk (`WalkLos`) to a stand point, from which the target is in sight (`CanSee`).
2. `PlanReach` tries these nodes in distance order, stopping at the first route that actually ends
   within 0.5 m of its node.
   Partial approach routes are rejected and another eligible node is tried. The brain walks the
   plan (YourBuddy: a Route with `SimpleAdvance`). Failure replies distinguish no clear approach,
   no path, and a door the NPC cannot open; a door failure is retained across candidate searches.
3. `StepIntoReach` walks straight on to the stand point, then the target, until `InReach`. A plan
   that ran out more than `ReachNodeArrival` short of the node is planned again, `ReachMaxReplans`
   times. Within `ReachStandArrival` of the stand point the last step is at the target. After
   `ReachApproachTimeout`, or with no plan back, the task `Defer`s, and unless it
   `Recover`s, `brain.OnWalkAbandoned` ends the walk.

**Why the route ends at a node.** Planning straight to a point by the target gave an *approach* plan
ending in the next room, and the final straight walk went through the wall. The last stretch is not
replanned, so it must be probed before it is chosen; everything before it comes from the graph.

---

## 7. Hands

`NpcHands` holds one item at a time. Picking up is `Crate.ParentItems` **without parenting to the
NPC**: the item stays under a room's content, and `LateUpdate` moves it to the hold point (`HoldHeight`
above the **feet**, `HoldForward` ahead, further for a big item) at walking pace plus `HoldMoveSpeed`.
Not parented, it never disappears with a deactivated NPC, and its save record never names it.

**Reaching out.** `ReachTo(point)` moves the held item to a point with its colliders **on**, so it
touches triggers like the player's held item does. `null` brings it back and turns them off; a speed
lowers it slowly instead. `Turn` sets facing relative to the carrier; `TurnWorld` keeps a fixed
world orientation when a placement plan depends on measured bounds. Picking up, releasing,
or calling `Turn` clears that fixed orientation.

**Putting down** (`PutDown`, `Drop`, `Release`) restores colliders, `restrictGrab`, interpolation, wakes
physics and calls `SavePosition`. Item and item blocker ignore each other for `PutDownIgnoreSeconds`.
Picking up takes the item off every item zone first ([an-item-zone-lists-only-items-in-it](invariants.md#an-item-zone-lists-only-items-in-it)).
While held, the player cannot grab it.

**Which room it belongs to.** Each frame the held item is re-owned to where the carrier stands
(`ItemParentAt`: by the floor, since the tracked room is stale aboard the ship)
([a-carried-item-belongs-to-the-room-its-carrier-is-in](invariants.md#a-carried-item-belongs-to-the-room-its-carrier-is-in)),
because the game's own re-parenting skips `restrictGrab` items. An item already inactive was destroyed
(trash can, sale) and is left alone.

**The agent puts it down** in front of itself (`[ai] Put down '…' - why`) on death, parking, despawn
and saving ([a-carried-item-is-put-down-before-a-save](invariants.md#a-carried-item-is-put-down-before-a-save));
a brain puts it down whenever its own task ends. An item deactivated by something else is forgotten
(`is gone from my hands`). `ColliderBounds` measures an item's solid colliders.

The brain's side: `PickUp(item)`, `Item`, `Point` (the hold point), `Forward` and `Extents` (how far
ahead it is held, and its size), `DistanceTo(point)`, `ReachTo`, `Turn`, `PutDown`, `Drop`, `Release`.

---

## 8. Floating

Outside with no gravity the agent floats and flies, as the player does beyond the zero-g airlocks
(the ship's, Oxygen's, Solar's, the Shipyard's). There are no nodes out there, and none of the
walking machinery runs: no doors, hops, whiskers, stairs or floor recoveries.

**Gravity.** `Gravity` is the player's `Data.gravity` rule: 9.81 inside; outside, the `ExitGravity`
of the airlock it went through (0, or 0.5 at the FuelStation). Walking multiplies it by the game's
gravity setting, as `PlayerController.Gravity` does. `Floating` is outside with no gravity. The side
and its gravity change only at an airlock
([an-airlock-is-crossed-by-its-cycle](invariants.md#an-airlock-is-crossed-by-its-cycle));
`SetOutside(value, why, gravity)` is for a mod that walked its NPC through one.

**Moving** copies `PlayerController`'s zero-g move: the velocity eases toward the step
(`FlightAcceleration`) and off without one (`FlightBrake`), up to `FlySpeed`, which is `MoveSpeed`: a
suit does not slow flight. A wall takes the velocity it stopped. Holding still in an airlock chamber
stops at once: a body drifting into a closing door fails the player's cycle (`AntiCrasher` →
`Gate.FailClose`).

```
brain.Steer → brain.Constrain → SteerInFlight → UpdateFlightRecovery → ApplyMovement → UpdateAnimation
```

`SteerInFlight` probes ahead with the body's capsule and turns sideways, then over or under, round
what is in the way; with nothing clear the controller slides along it.

| Flight | Does |
|---|---|
| `FlyFollow(player)` | after the player: sets off beyond `FlyFollowStart`, flies on down to `FlyFollowStop`, then hovers |
| `FlyTo(point, arrival)` | until its middle is within `arrival` of the point |
| `Hover()` | brakes to a stop |

**The player's trail instead of the graph.** `NpcTrail` records the player every 0.5 m while they are
outside, the airlock end first. A new walk outside starts a new trail, coming in keeps it, and the
world moving drops it. A flight goes straight at its goal when a body-wide sphere cast is clear; else
it walks the trail from the point nearest itself toward the point nearest the goal, and flies to the
last one it can see. Going the same way it never starts behind the point it already flew to: where
the trail passes near itself, the nearest point may lie on the other pass and turn it round. The path the player took is one the body fits: their capsule is 0.2 m wide.

**Recovery, a last resort.** Wanting to fly and getting nowhere for `FlightStuckSeconds`: following the
player outside, it is moved onto their trail at least `RescueBehind` behind them, where it is free and
in sight of them; following the trail elsewhere, on to the trail point it was flying to, if it can see
it. Farther than `FlightLeash` from the player outside gets the same move. Each move is a warning in
the log.

**Frames.** A floating agent rides the scene root and holds no room
([an-npc-rides-its-own-floor](invariants.md#an-npc-rides-its-own-floor),
[an-unloaded-ship-parks-the-npc](invariants.md#an-unloaded-ship-parks-the-npc)). Once the world moves,
it is pulled aboard into the ship's airlock.

| Log line | Means |
|---|---|
| `[ai] X is outside now - cycled out through 'ShipAirlock' with you (gravity 0)` | the cycle took it out |
| `[ai] X is outside with you - not parked with the ship` | the ship unloaded, and it was already out |
| `[ai] X cannot fly straight there - following your trail (point N of M)` | level 1, when it changes |
| `[ai] X was stuck floating for 4 s - moved to your trail 3.2 m behind you` | the rescue |
| `[ai] The ship moved off with it floating outside - moved to the ship airlock` | pulled aboard |
