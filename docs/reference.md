# Reference - constants, commands, editor

This file explains what each constant *means*. The code holds the actual number; if they disagree,
the code wins - fix this file. Player-facing settings are in the [README](../README.md#configuration).

---

## 1. Tuning constants

Before changing any height threshold, read
[same-level-tolerance-ceiling](invariants.md#same-level-tolerance-ceiling).

### Pathfinding - `NavGraph.cs`

| Constant | Value | Meaning |
|---|---|---|
| `MaxSeedDist` | 60 m | how far a node may be and still be a route **entry** |
| `MaxGoalFinishDist` | 6 m | longest final straight walk to the goal - [never the same as above](invariants.md#seed-radius-is-not-finish-length) |
| `SameLevelDeltaY` | 0.5 m | **same deck?** floor to floor |
| `StairSeedRadius` | 2.5 m | how near a `Stair` node must be to enter it off-level |
| `StairEntryGroundStep` | 0.8 m | largest floor break along an off-level entry |
| `MaxStairDeltaY` | 2.0 m | climb cap for a `Stair` entry |
| `SeedCloseDist` / `SeedCloseDeltaY` | 1.25 m / 0.5 m | arm's-length entry, no line of sight needed |
| `VerticalCostFactor` | 4 | height multiplier in `GoalCost` |
| `ApproachMinGain` | 1.5 m | how much nearer an approach node must get to be worth it |
| `ApproachRouteWeight` | 0.1 | tie-break on route length among approach nodes |
| `BacktrackRadius` / `BacktrackPenalty` | 1.5 m / 3 | anti-backtrack around the last departed node |
| `AvoidEntryRadius` | 0.5 m | how near counts as *that* node for `avoidEntry` |
| `UserIdBase` | 100000 | first id for nodes you place |
| `NodeHoverTtl` | 3 s | node floor offset cache |
| `LinkShiftTolerance` | 0.1 m | how far a link's room offset may drift and still hold |
| `AnchorRetryInterval` | 2 s | retry anchoring ship nodes with no floor |

### Probes - `NavProbe.cs`

| Constant | Value | Meaning |
|---|---|---|
| `EyeHeight` | 1.0 m | line-of-sight height above the floor |
| `GroundSampleStep` | 0.35 m | floor sample spacing (a tread is ~0.3 m) |
| `KneeHeight` | 0.4 m | `WalkLos` height; low enough that railings block it |
| `MaxWalkableSlope` | 45° | flatter than this is floor |
| `GateOpeningRadius` | 0.85 m | minimum carve-out, and the fallback |
| `GateOpeningMargin` | 0.25 m | added to the leaves' span |
| `CrossingBodyRadius` | 0.2 m | a line must cross a doorway this far inside the leaves' span ([a-doorway-is-crossed-not-grazed](invariants.md#a-doorway-is-crossed-not-grazed)) |
| `MinCrossingHalfWidth` | 0.25 m | least crossing half-width, for a gate that reads narrow |
| `GateOpeningHalfDepth` | 1.0 m | half-depth through the wall |
| `GateOpeningHalfHeight` | 2.5 m | vertical half-extent |
| `MaxGateFootprintRadius` | 2.5 m | maximum carve-out |
| `AuditPairCooldown` / `AuditMaxPairs` | 10 s / 64 | audit dedupe per collider × gate pair |
| `SightGateBroadPhase` | 6 m | `CanSee` skips shut gates further than this from the line |

### Walking - `NpcAgent.cs`

See [agent.md §5](agent.md#5-walks).

| Constant | Value | Meaning |
|---|---|---|
| `NavPathRecalcInterval` | 0.22 s | minimum time between a Pursue's replans |
| `NavPathGoalDrift` | 1.5 m | goal movement that invalidates a plan |
| `NavPathOnRouteRadius` | 1.2 m | [still on the route](invariants.md#commitment-skips-on-route) |
| `WaypointReachedXZ` / `WaypointReached3D` | 0.55 / 0.65 m | [dual advance](invariants.md#waypoint-advance-is-dual) radii |
| `WalkerOriginAboveFeet` | 0.5 m | where the 3D radius is measured from: the player's origin |
| `WaypointAdvanceMaxDeltaY` | 0.5 m | same-deck cap for the XZ advance; larger differences make a stair leg |
| `StairLegLookahead` | 0.35 m | how far along a stair leg the agent aims |
| `StairLegCorridor` | 0.5 m | how far beside a stair leg still counts as on it |
| `FollowSameLevelDeltaY` | 0.5 m | deck gap above which a brain's "arrived" never holds |
| `EntryBlockedWindow` | 4 s | window for blocked-entry escalation |
| `EntryBlockedAvoidAfter` / `EntryBlockedStepOffAfter` | 4 / 8 | escalation thresholds |
| `UnreachableWaypointHold` | 6 s | how long an unwalkable waypoint stays barred |
| `JumpVelocity` | 4.5 | auto-jump impulse |
| `DetourLedgeDrop` | 0.7 m | drop that makes a sideways detour a fall |
| `CeilingHeadroom` | 0.1 m | ceiling this close above the body counts as wedging it |

Steering and auto-jump use the controller's own `stepOffset` and `slopeLimit`: an agent on YourBuddy's
body inherits the player prefab's.

### Wander and idle recovery - `NpcAgent.cs`

| Constant | Value | Meaning |
|---|---|---|
| `WanderIdleMin` / `WanderIdleMax` | 0.3 / 1.4 s | pause after arriving |
| `WanderRetryDelay` | 0.5 s | pause after a failed pick |
| `WanderPickAttempts` | 4 | destinations tried per pick |
| `WaypointBudgetBase` / `WaypointBudgetPerMetre` | 6 s / 1.2 s | time to reach one waypoint before a walk gives up ([give-up-per-waypoint](invariants.md#give-up-per-waypoint)) |
| `IdleNoRouteGiveUp` | 2.5 s | motionless while perched before stepping off |
| `IdleAboveFloorDelta` | 0.35 m | how far above its floor plane counts as perched |

### An agent at a door - `NpcAgent.cs`

See [doors.md §6-7](doors.md#6-opening).

| Constant | Value | Meaning |
|---|---|---|
| `DoorwaySelfRadius` | 1.4 m | how near a gate the agent counts as blocking it |
| `DoorwayClearRadius` | 1.2 m | fallback occupancy radius if `AntiCrasher` can't be read |
| `DoorDeferGiveUp` | 20 s | longest a close may be deferred |
| `PinPanelReachDist` | 1.6 m | how near a pin panel the agent must get |

### Walking into reach - `NpcAgent.cs`

See [agent.md §6](agent.md#6-walking-into-reach). `ReachStandOffs`, `ReachDist`, `ReachHeight` and
`ReachReplanDelay` are on `ReachTask`.

| Constant | Value | Meaning |
|---|---|---|
| `ReachStandOffs` | 0.8 / 1.1 / 1.4 m | stand points in front of the target |
| `ReachNodeRadius` | 10 m | nodes further from the target are ignored |
| `ReachEyeHeight` | 1.5 m | eye height for seeing the target |
| `ReachNodeArrival` / `ReachMaxReplans` | 1.2 m / 2 | route counts as walked this near its node; else replan this often |
| `ReachStandArrival` | 0.3 m | then turn to the target |
| `ReachDist` / `ReachHeight` | 1.6 m / 2 m | flat distance and height from which the target is used |
| `ReachApproachTimeout` | 8 s | final straight walk limit |
| `ReachGiveUpDelay` | 60 s | per unit, after giving up |
| `ReachReplanDelay` | 5 s | after no plan |

### Carrying - `NpcHands.cs`

See [agent.md §7](agent.md#7-hands).

| Constant | Value | Meaning |
|---|---|---|
| `HoldHeight` / `HoldForward` | 0.95 m / 0.45 m | held item centre: above the feet, and ahead |
| `HoldMoveSpeed` / `HoldSnapDist` | 1.5 m/s / 2 m | follow speed on top of walking; further than this it jumps |
| `HoldDropHeight` | 0.5 m | release height above the feet |
| `HoldBodyClearance` | 0.3 m | big items held at least this clear of the body |
| `PutDownIgnoreSeconds` | 1 s | put-down item and carrier ignore each other |

### Several NPCs - `NpcAgent.cs`

See [npcs-never-block-each-other](invariants.md#npcs-never-block-each-other).

| Constant | Value | Meaning |
|---|---|---|
| `NpcSpacing` | 0.6 m | an idle agent this near one that ranks before it (flat) steps aside |
| `SpacingSameDeck` | 1 m | height difference beyond which the other is on another deck |
| `SpacingStep` / `SpacingCooldown` | 0.8 m / 3 s | how far it steps, and how often at most |

### Life threat - `NpcAgent.cs`

The player's own rule ([game-model.md](game-model.md#atmosphere-kills-by-the-players-rule)). The suit an
agent feels the air through is `NpcAgentSettings.SuitTemperatureResistance`, 100 (a PilotSuit's
+1 °C) by default.

| Constant | Value | Meaning |
|---|---|---|
| `LifeThreatLethal` | 10 | summed threat at which the death counter rises |
| `DeathCounterArmed` / `DeathCounterCap` | 5 / 10 | past this, a 1-in-5 roll per tick; ceiling |
| `VacuumThreat` | 100 | no environment at all |

### Footsteps - `NpcAgent.cs`

Which sound: [game-model.md](game-model.md#footstep-sounds).

| Constant | Value | Meaning |
|---|---|---|
| `ShipFootsteps` / `StationFootsteps` | 0 / 1 | indices into `NpcBody.FootstepEvents` |
| `FootstepVolume` | 0.5 | step volume within `FootstepFullVolumeDist` of the listener |
| `FootstepFullVolumeDist` / `FootstepSilentDist` | 2 / 12 m | fades out between these, quadratically |
| `FootstepDoorHalfWidth` / `FootstepDoorHeight` | 1.5 / 2.5 m | how near a detector's centre a plane crossing counts as through its door |
| `FootstepDetectorsTtl` | 5 s | detector list cache |

### Doors - `NpcDoors.cs`

See [doors.md](doors.md).

| Constant | Value | Meaning |
|---|---|---|
| `PlayerAtDoorwayRadius` | 2.5 m | a player this near the doorway is re-filed by the game as usual |
| `NpcCloseWindow` | 6 s | how long a close stays an NPC's: `Gate.OnClosed` fires at the end of the animation |
| `SceneCacheTtl` | 5 s | doorway sensors, airlocks, pin panels |
| `ImpassableGatesTtl` | 1 s | the gates routing avoids; lock and open state change in play |
| `DoorBroadPhaseRadius` | 3 m | a gate further than this from a route stretch is not tested |
| `DoorBlockPadding` | 0.3 m | body clearance around a blocking gate's volume |

### Sell pens - `SellPens.cs`

See [a-fenced-sell-station-is-not-somewhere-to-stand](invariants.md#a-fenced-sell-station-is-not-somewhere-to-stand).

| Constant | Value | Meaning |
|---|---|---|
| `SellStationClearance` | 0.35 m | margin around the zone and fences no NPC stands in |
| `SellPenRefresh` | 10 s | how often fence footprints are re-measured |
| `PenDepthBelow` | 2 m | how far below the pen's colliders a point still counts as in it |

### Talk window - `NpcInteraction.cs`

See [interaction.md](interaction.md#1-opening-it).

| Constant | Value | Meaning |
|---|---|---|
| `TalkRange` | 2.4 m | to the NPC's controller; shorter than the player's reach |
| `LookAngle` | 20° | cone to the *nearest* point of the controller; a chest point fails close up |
| `ChestAboveFeet` | 1.1 m | where the sight ray aims; 0.6 m above a player-like origin |
| `MaxLines` | 80 | lines each conversation keeps |

### Carrying a corpse - `NpcCorpse.cs`

| Constant | Value | Meaning |
|---|---|---|
| `FollowSpeed` | 50 | how fast the body tracks the hold point, like the game's `Grabbable` |
| `CarryDrag` | 10 | damping while held; gravity off |
| `HoldDistance` / `HoldDrop` | 1.6 / 0.3 m | in front of and below the camera |
| `SnapDistance` | 1.8 m | further than this, it is dropped |
| `LookAngle` | 45° | cone to the nearest limb collider |

A capped spring against gravity made the body unliftable: it pulls one rigidbody against the whole
ragdoll's mass through the joints.

### Lifecare - `NpcLifecare.cs`

| Constant | Value | Meaning |
|---|---|---|
| `PollSeconds` | 0.1 s | the terminal poll while a lifeform is loaded ([lifecare.md §2](lifecare.md#2-what-npccore-adds)) |

---

## 2. Console commands

Registered in `CoreCommands.cs` through `NpcConsole`, which every NPC mod shares
([one-owner-per-console-command](invariants.md#one-owner-per-console-command)).

| Command | Arguments | Does |
|---|---|---|
| `npc_node` | `add`, `count`, `clear`, `save`, `list`, `remove <index>` (alias `delete`), `link <a> <b>`, `unlink <index>`, `type <index> <ground\|stair>`, `bundled`, `unfork <owner>` | edits the nav graph ([navigation.md](navigation.md#1-the-model)) |
| `npc_gates` | - | every gate and the doorway carve-out it was given ([probes.md](probes.md#the-audit)) |
| `node_editor` | - | toggles the editor (§3) |
| `debug_level` | `<0-3>` | log verbosity of every NPC mod ([logging.md](logging.md#1-levels)) |
| `ai_disable` | `[npc\|monster\|all] [on\|off]` | switches the AI of every NPC mod, the Breathless, or both off; a flip without `on`/`off`. `buddy` is taken for `npc` |
| `ai_notarget` | `[on\|off]` | the Breathless ignores the **player** only; NPCs stay fair game |

When the console opens, each owner's commands are listed once:
`NPC.Core commands registered (6): ai_disable, ai_notarget, debug_level, node_editor, npc_gates, npc_node`.
A refused name is logged as a warning naming who has it.

**`ai_notarget` is player-only on purpose.** Both monster AI components kill from an
`ObjectDetector` UnityEvent that fires even when the component is disabled. So the two `DetectItem`
prefixes suppress a `Player` detection instead, leaving item handling intact. An NPC never reaches
those handlers ([game-model.md §1](game-model.md#1-an-npc-is-not-a-player)); its catch is its mod's
own, which only `ai_disable` stops.

**Neither override touches the save.** Both avoid `Breathless.Enabled` and `SetAggressive`, which
write the player's save ([mod-state-never-enters-the-vanilla-save](invariants.md#mod-state-never-enters-the-vanilla-save)).
They live in `NpcMonster` ([the-ai-overrides-have-one-owner](invariants.md#the-ai-overrides-have-one-owner)),
are re-asserted every tick, and are cleared by a save load (`ResetOverrides`, from the `LoadGame`
postfix), restoring the current monster first in case the load is refused.

---

## 3. Node editor

Toggle with **F8** or `node_editor`. Keys are listed in the [README](../README.md#the-node-editor)
and configurable in the `NodeEditor` config section.

- **F6 saves, never F5** - F5 is the game's QuickSave.
- `U` removes every manual link of the selected node. It forks only that node's owner.
- Node colours: green normal, orange stair, red selected, magenta pending link, grey inactive
  (other station, or unbuilt ship room - cannot be selected).
- The console-open check is `NpcConsole.IsOpen`, read from `PlayerOverlay.ConsoleMenu.Opened`.
  Never reflect for it.

Nodes are saved per owner in owner-local coordinates in
`BepInEx/config/NPC.Core/nodegraph.json` (20 s after a change; F6 forces it). The shipped
graph and how the two files combine: [navigation.md §1](navigation.md#where-the-nodes-come-from).
