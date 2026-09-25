# Doors

`World/NpcDoors.cs` is what every NPC knows about doors, plus the `Doors` patch group in `Patches.cs`
(§1-5). `Agents/NpcAgent.Doors.cs` is how an agent opens, waits for and closes them (§6-10). Read the
game's door model first: [game-model.md §3](game-model.md#3-gate-the-door-state-machine) - most of
the difficulty comes from it.

What a door *is* to an NPC is one answer for all of them
([door-knowledge-is-shared](invariants.md#door-knowledge-is-shared)).

---

## 1. Two different questions

"May an NPC **open** it?" and "should routing go **around** it?" are different
([opening-is-not-passing](invariants.md#opening-is-not-passing)).

| Gate | `NpcDoors.MayOpen` - open it? | `NpcDoors.BlocksRouting` - avoid it? |
|---|---|---|
| already open | yes | no |
| `Gate.Locked` | no | **yes** |
| driven by detectors, but none is switched on, or every one needs a suit the player lacks ([an-npc-opens-only-what-the-player-could](invariants.md#an-npc-opens-only-what-the-player-could)) | no | **yes** |
| airlock door, docking hatch (`IsAirlockGate`) | no - venting risk | **no** - the player opens these |
| `ElectricityPanelGate` | no ([keep-electricitypanelgate-excluded](invariants.md#keep-electricitypanelgate-excluded)) | no - a wall panel |
| pin-code door | only with a known code | **yes**, unless the code is known |
| anything else | yes | no |

`WhyClosedToPlayer` says in words why a detector-driven door is shut to the player. An agent's
`HandleDoors` asks the first question, path search the second; at a door closed to the player the agent
logs why, at most every 5 s:

```
[ai] Not opening 'Door02' - its doorway sensor is switched off, so you cannot walk it open either
[ai] Not opening 'Door02' - it opens only for someone in a suit, and you have none
```

---

## 2. Doorway sensors

`NpcDoors.Detectors` is every active `EntryDetector`, swept every 5 s and after a scene change. A
switched-off detector is left out of it but still counts for
[an-npc-opens-only-what-the-player-could](invariants.md#an-npc-opens-only-what-the-player-could).

| Member | Answers |
|---|---|
| `RoomsOf(detector, out inner, out outer)` | the two rooms a doorway joins |
| `DoorOf(detector)` | its gate, or null for an archway |
| `TryInnerSide(detector, pos, out inner)` | which side a point is on, by the game's own test for the player |
| `Optimizes(detector)` | whether the game switches the far room off once the door shuts |
| `DetectorsVersion` | counts the sweeps: rebuild a list you derive from them when it changes, on your next read |

`TryInnerSide` is a plane. It is sound only for the doorway's two rooms: most of YardHallway lies on
YardCryo's side of the YardCryo door.

### Password doors

The player gives a code through a mod's dialog or command; the mod calls `NpcDoors.LearnCode`. Every NPC
of every mod knows every code. The codes belong to the running game: a new `GameManager` starts them
empty. NPC.Core saves them in `<save>.npccore` and restores them once the loaded scene is ready
([architecture.md §4](architecture.md#4-files-on-disk)).

`PinPanelFor(gate)` gives the gate's panel, which is also where the NPC must stand to use it. An NPC
opens a pin-code door only through that panel
([an-npc-only-knows-codes-it-was-told](invariants.md#an-npc-only-knows-codes-it-was-told)).
`AnyKnownDoorMatches` tells a mod whether a code it was just given opens anything. How an agent uses
the panel: §6.

---

## 3. Routing around what no NPC can open

`NpcDoors.SegmentBlockedByDoor` is bound to `NavGraph.SegmentBlockedByDoor` at plugin load. `FindPath`
then rejects any stretch that passes **through** a blocking gate's own volume, padded by
`DoorBlockPadding` - in seeding, fallback, expansion and the finish leg, Force and Priority edges
included ([locked-doors-block-edges](invariants.md#locked-doors-block-edges)). It tests the volume, not a
radius: a radius also blocked the corridor running past a shut airlock.

The blocking set is rebuilt at most once a second. With no route that avoids the door,
`NavGraph.LastPathBlockedByDoor` is set and the agent stands still and says so
([stand-still-when-door-blocked](invariants.md#stand-still-when-door-blocked)); `ImpassableCount` is how
many doors were in the way:

```
[ai] No route that avoids a door I cannot open - waiting here (1 closed to me)
```

---

## 4. Closes an NPC blocked

The game never retries a failed close ([game-model.md §3](game-model.md#3-gate-the-door-state-machine)),
so a door an NPC stood in stays open for the rest of the session. NPC.Core's `Gate.FailClose` postfix
calls `INpcDoorUser.OnCloseFailed` on every registered NPC that implements it. Each must ignore a gate it
is not standing in, so only the one in the doorway takes the close over.

An agent never closes a door it did not open - that would shut doors behind the player - except this
one. Its `NoteCloseFailed` arms a close when it is within `DoorwaySelfRadius` and the gate is not an
airlock or password gate. It must not re-arm a gate already pending
([fail-close-must-not-rearm](invariants.md#fail-close-must-not-rearm)).

When agent A closes a door on agent B, B takes the close over and steps out. Another NPC in the
doorway is a blocker like any other: A's close waits for it
(`Waiting to close 'Door02': 'YourBuddy Buddy 2' ... is in the doorway`).

---

## 5. Closes away from the player

`EntryDetector` re-files the player whenever a door it knows closes, from anywhere
([game-model.md](game-model.md#entrydetector-re-files-the-player-when-a-door-closes)). So an NPC calls
`NpcDoors.NoteClosedBy(gate, itself)` right after `Gate.Close`, and the `Doors` patch group keeps the
close from moving the player ([npc-closes-must-not-move-the-player](invariants.md#npc-closes-must-not-move-the-player)):

1. The `DoorCheckForEnter` prefix: for a gate an NPC closed within `NpcCloseWindow`, with the player
   beyond `PlayerAtDoorwayRadius`, hide the detector's remembered player. Item re-parenting still runs.
2. The postfix puts the player back. Away from the player the game switched no room off either, so the
   closer's `INpcDoorUser.OnDoorClosedAway(detector)` runs: the NPC that shut the door puts back what
   it loaded. Every other NPC keeps its own room through `INpc.TracksRoom`
   ([rooms-stay-loaded-for-every-npc](invariants.md#rooms-stay-loaded-for-every-npc)).

At the doorway the game does its normal thing for the player, and NPC.Core stays out of it.

---

## 6. Opening

Every 0.2 s `HandleDoors` casts 1.2 m ahead at chest height. If it hits a `Gate` that `MayOpen`
allows, the agent opens it, waits for `FullyOpened` (5 s timeout) and arms a close-behind. Airlock and
docking gates it never opens; it waits for the player. `NpcAgentSettings.CanOpenDoors` off stops all of
this.

Opening a gate also enables the room content on both sides, so the agent never walks into an
unloaded room.

### Which rooms an NPC loads

The game switches rooms on and off as the player passes doorways: both sides while the door is
open, only the player's side once it shuts. An agent follows the same door rule
([an-npc-loads-rooms-by-the-door-rule](invariants.md#an-npc-loads-rooms-by-the-door-rule)):

| Room | Loaded when |
|---|---|
| the room the agent is in | always; kept on if the game switches it off |
| the room behind the nearest doorway (within 3.5 m) | only while that door is open |
| both rooms of a door | the agent opens it |
| any room its brain asks for | `NpcAgent.LoadRoom`: YourBuddy's errands, and every room holding a sell station |

The room the agent is in is the side of the nearest doorway it last stood on (`TryInnerSide`),
the game's own test for the player. An agent that has never crossed a doorway (spawned, restored from a
save) takes the nearest one at any range, once, on its own vessel, so it has a room from the start.

It switches rooms off when a door it shut finishes its close, away from the player (§5,
`ReleaseRoomsAt`). Both rooms of that doorway are checked, whoever switched them on: a save restores
every room as it was saved ([game-model.md](game-model.md#which-rooms-are-loaded-is-saved)). Checking
both matters when an agent walks through two rooms in a row. The first door can finish closing only
after it has left the second room, and the room between is re-checked once its last door shuts. A room
stays on when:

- the agent is in it: its tracked room, or the side of this doorway it has just come in by;
- another NPC is tracked in it (`Keeping room 'X' loaded: <name> is in it`);
- the player's room is it;
- the player is outside on a spacewalk (`SpaceStation.OnStationExit` shows every room then);
- a mod keeps it ([a-kept-room-stays-loaded](invariants.md#a-kept-room-stays-loaded));
- any other doorway into it has an open door, or no door;
- that doorway is one the game keeps loaded on both sides (`optimize` off, as at the docking airlock).

The side test only counts while the tracked room is one of this doorway's two rooms (§2).

A room it leaves with the door open stays on until someone shuts that door, as for the player. Ship
rooms are never switched off this way: they have no doorway sensors. A room an agent never walks out
of keeps the state the save gave it.

Each switch-on and switch-off is logged at level 1, with how many rooms around the doorways are on;
switch-ons at most once per room per 5 s. Every station door is named `Door02`, so lines name the
room across instead. Level 2 adds why a room stayed on:

```
[ai] Loaded room 'YardCryo' (opening the door to 'YardLibrary') - 7 rooms loaded
[ai] Unloaded room 'YardCryo' (door to 'YardLibrary' shut) - 6 rooms loaded
[ai] Keeping room 'YardLibrary' loaded: its door to 'YardCryo' is open
[ai] Keeping room 'YardHallway' loaded: you are in it
```

### At a password door

At a shut pin-code door whose code it knows (§2), the agent walks within `PinPanelReachDist` of the
panel and calls `PinCode.ForceValidate()`. The door opens through the game's own wiring
([an-npc-only-knows-codes-it-was-told](invariants.md#an-npc-only-knows-codes-it-was-told)), so the
close-behind below applies to it too.

---

## 7. Closing behind itself

`pendingDoorCloses` is a list of doors the agent owes a close
([pending-closes-are-a-list](invariants.md#pending-closes-are-a-list)), worked in slow phase 3.

- **Dropped** when the gate is destroyed, already shut, locked, `CanOpenDoors` is off, or 90 s have
  passed since arming.
- **Not fired** until the agent has crossed to the far side
  ([close-only-what-you-walked-through](invariants.md#close-only-what-you-walked-through)).
- **Deferred** when the doorway is occupied:

| Situation | Response |
|---|---|
| the agent is within `DoorwaySelfRadius` | `StepOutOfDoorway`, retry in 1 s; never force it ([never-force-a-close-into-the-npc](invariants.md#never-force-a-close-into-the-npc)) |
| something else is in the opening | log the blocker (throttled), retry in 1.5 s; after `DoorDeferGiveUp`, close anyway |

Deferral tracks `DeferredSince` and never touches `ArmedAt`
([no-permanent-deferral](invariants.md#no-permanent-deferral)).

Occupancy uses the gate's own `AntiCrasher` bounds, on that sensor's layer row
([occupancy-asks-the-anticrasher](invariants.md#occupancy-asks-the-anticrasher)), falling back to a
1.2 m sphere only if reflection fails. It skips the gate's own children and the agent.

Each close is reported with `NoteClosedBy` (§5).

**Why retry at all:** `Gate.CloseRoutine` re-enables the `AntiCrasher` triggers, and a trigger that
already overlaps a body fires `FailClose → Open`. So: check clearance, then retry every 3 s, up to
ten times.

---

## 8. Getting out of the way

`StepOutOfDoorway` walks the agent 1.8 m clear for 1.5 s (4 s cooldown), only when it is not already
walking somewhere (`NpcActivity.LeavesDoorways`). Without it an agent standing near the player could
hold its own door open forever.

The step-off works in every walk ([step-off-applies-in-every-mode](invariants.md#step-off-applies-in-every-mode)).
The step-off target is shared with obstacle recovery, the no-route step-off and a brain's own
`StepOff`; the last writer wins, which is fine - all of them just want to move briefly.

---

## 9. What a healthy capture looks like

```
[ai] Opening door 'Door02'
[ai] Standing in 'Door02' - stepping clear so it can close      (only if it was in the way)
[ai] Closing door 'Door02' behind itself (attempt 1)
```

If the close never happens, look for the blocker line:

```
[ai] Waiting to close 'Door02': 'PinCodePanel' (layer 'Interactable') is in the doorway
```

---

## 10. Known limitations

- **Doors an agent did not open are not closed**, except the case in §4.
- **Owed closes are lost on despawn, death or load.** Those doors stay open. Not worth saving.
- **An agent must reach a pin panel.** A door with a panel only on the far side cannot be opened from
  the near side.
