# The game's own model

How *Isolated Inhale* itself works, as far as every NPC mod needs it, read from a decompile of its
assembly. Line references are to an ILSpy dump of **v0.8.9** and shift on other builds. `decompiled/`
is not in the repository - generate it locally ([`decompiled/README.md`](../decompiled/README.md)).

Several NPC bugs were really misunderstandings of this layer.

---

## 1. An NPC is not a `Player`

`PlayerDetector.OnTriggerEnter` requires a `Player` component (`PlayerDetector.cs:30-55`). An NPC built
from the player prefab has that component stripped, and any other NPC never had one. So an NPC:

- **never** trips `PlayerDetector` or `EntryDetector`;
- **never** drives the game's auto-open or auto-close;
- **never** gets a room from the game - an agent tracks one itself, with `NpcDoors.TryInnerSide`;
- **never** trips the Breathless' detectors - its catch is each mod's own ([§5](#5-the-breathless));
- **does** trip `AntiCrasher`, which tests layers, not components.

Almost every "the game ignores the NPC" symptom follows from this.

---

## 2. There are no room volumes

A room is whichever doorway sensor you last crossed. `EntryDetector.Awake` wires its enter/exit
events to `player.SetCurrentRoom(innerRoom | outerRoom)`, choosing the side by the sign of
`rotationReference.InverseTransformPoint(pos).z`.

- `Player.CurrentRoom` is **sticky**: the last doorway crossed, not where you are. It often reads
  `Front_M00`. Faithful to the game, not fixable.
- Never ask a room "am I aboard" - [aboard-is-answered-by-the-floor](invariants.md#aboard-is-answered-by-the-floor).

### `EntryDetector` re-files the player when a door closes

`Awake` also wires `door.OnClosed → DoorCheckForEnter`, with **no distance check**, and the detector's
`player` field is never cleared. Every detector the player ever crossed can reassign their room from
across the ship. Harmless in vanilla; not once NPCs close doors -
[npc-closes-must-not-move-the-player](invariants.md#npc-closes-must-not-move-the-player).

### Which rooms are loaded is saved

`Room` is a `SaveObject`: `SetContentEnabled` writes `Data.enabled`, and a load restores each room's
content exactly as saved (`Room.ApplyData`). So a room left on stays on across every load until a
doorway switches it off. Only station rooms have `EntryDetector`s; none of the ship's inner doors
has one. `Docker.Dock` resets a station to its entry room, but not on the load path.

### Docking does not run the room transition

`Docker.Dock` opens the airlock with `ship.Airlock.Open()` (`Docker.cs:71`), which skips
`Airlock.Enter()` and its `SetCurrentRoom(connectedRoom)` (`Airlock.cs:309`). So boarding from a
docked station leaves `CurrentRoom` on the station's room - even across save/load. A **vanilla** bug;
its visible effect is worked around in [lifecare.md](lifecare.md).

---

## 2a. The ship never moves - the world moves around it

Thrust is `WorldObjects.localPosition += movementDelta` (`ShipController.cs:346`); steering rotates
`WorldAnchor` (`:359`). The ship's transform is never written. Docking teleports the *world* so the
station's docking point meets the ship.

A root-level object therefore lives in the **ship's** frame and is left behind when the world moves.
The game handles this two ways, and an NPC must copy both: ride the frame of the floor it stands on
(`NpcVessels.FloorOwner` gives the transform), or be moved aboard.

| Thing | Solution | Where |
|---|---|---|
| loose items | re-parented into the station's `contentParent` | `SpaceObject.SetItemOwnership` |
| the Breathless (a root object) | teleported onto the ship on undock | `Docker.Undock`, `:111` |

### Undocking does not unload a station

`Docker.Undock` ends with `SetOptimizationLevel(Optimized)`: `contentParent.SetActive(false)` plus a
low-detail exterior (`SpaceObject.cs:69-92`). Nothing is destroyed; docking reactivates it. An NPC
parented into `contentParent` is frozen and thawed with it. Parenting cannot cover an NPC with no
floor, or one in the docking collar, whose colliders switch off with the station.

### A spacewalk unloads the player's ship

`Airlock.Exit` (`Airlock.cs:247-264`) loads the object outside, then calls
`PlayerShip.SetContentEnabled(false)`. `Airlock.Enter` reverses it; they are the only callers.

The `Autopilot` trigger is room content (`SpaceShip/Rooms/Front_M00/Front_M00Content/Autopilot`).
Reactivating it fires `OnTriggerEnter`, which sets nearby `SpaceObject`s to `Optimized`
(`Autopilot.cs:204`). So anything that turns the bridge back on mid-spacewalk unloads what the player
went out to. An NPC aboard must hold no room loaded while the ship is unloaded.

### The player's ship is rebuilt by its upgrade stage

`StorymodeShipBuilder.SetShipLevel` (`StorymodeShipBuilder.cs:54`) runs on every load, on an upgrade
purchase, and from `set_ship_level`. It switches every room's `Structure` off, resets positions, then
builds the stage:

| Stage | Rooms built | Moved to z +3.75 |
|---|---|---|
| 0 | `Front_M00` | all built |
| 1 | + `Front_R03`, `Front_L06` | all built |
| 2 | + `Core_R04`, `Core_L07` | all built |
| 3 | + `Core_M01` | none |
| 4 | + `Core_R10E`, `Core_L13E` | none |
| 5 | + `Back_R05`, `Back_L08` | none |

`Back_M02` and the other `E` rooms are never built in story mode. Rooms are 2.875 × 3.75 m cells with
identity rotation; `ShipAirlock` stays at z 3.125, so the ship grows forward from its airlock. Each
stage enables `Tier<N>Temp` walls that close doorways to unbuilt rooms.

The rebuild is one synchronous call; no frame runs in between. Rebuilding to the current stage
changes nothing. See [a-ship-node-rides-its-room](invariants.md#a-ship-node-rides-its-room).

### A station's interior is not under the station

| Object | Holds | Moves |
|---|---|---|
| `World/Objects/<Station>` - the `SpaceStation` | exterior, docking point, `Environment` | with the world |
| `StaticObjects/<Station>Parts` - a **scene root** | the whole interior: floors, walls, rooms, doors, airlocks, `Docker` | never |

`SpaceObject.contentParent` points at the `Parts` root (`NpcVessels.InteriorOf`). All four stations are
built this way; each interior sits where it will be when docked. Other `SpaceObject`s
(`DestroyedSpaceShip`, `ContainerField`, `Diamond`) keep content as children. An ancestry walk from
inside a station finds no `SpaceObject` -
[a-station-owns-its-interior-by-reference](invariants.md#a-station-owns-its-interior-by-reference).

### A new game

A new game is `SaveData.worldTime == 0` at `GameManager.Start` (`GameManager.cs:157`), which then puts
the player in the first empty cryo pod and advances `worldTime`. NPC.Core tests it in a `Start` prefix
and raises `NpcEvents.GameStarting(true)`.

---

## 3. Gate: the door state machine

`Gate : Powerable`. Read `decompiled/Gate.cs`.

**A doorway can require a suit.** `PlayerDetector.helmetRequired` fires only for a player with a suit
equipped. In `level1` exactly one detector sets it: the cryo room's exit `Door02`
(`TutorialSequence.firstDoor`). It is not `Locked` - it just has nobody to open it until the player
suits up - [an-npc-opens-only-what-the-player-could](invariants.md#an-npc-opens-only-what-the-player-could).

**A doorway's detector can be switched off.** The Oxygen Station's four connector rooms
(`OxygenConnectorRB`, `LB`, `RF`, `LF`) are `RoomSealer.targetRooms`. Their eight `Door02`s have
their `PlayerDetector` object inactive in the scene, and nothing activates it. Only the seal panel
(`RoomSealer.SwitchLock` → `Room.DoorsOpened`) opens or shuts them. They are never `Locked`.

**There is no auto-close in code.** No timer, occupancy count or `OnTriggerEnter`. Auto-close is
wired in the **scene** as UnityEvents, which is why `InvokeOpen`, `InvokeClose` and `FailClose` have
no C# callers. The driver is most likely a `PlayerDetector.onPlayerExit` edge.

**There is no retry.** `FailClose()` (`Gate.cs:203-211`) calls `Open()`, fires `OnCloseFail` and plays
a sound. Once the triggering edge is used up, nothing will ever close that door again.

**`Enabled` is inverted.** The `Opened` setter writes `Enabled = !value` (`Gate.cs:82`): `Enabled` means
**closed**, and every open/close rewrites saved `Powerable` state.

**`Close()` does nothing when unpowered** (`Gate.cs:260`). A blackout opens every door and leaves them
un-closable until power returns.

### `AntiCrasher`

```csharp
private void OnTriggerEnter(Collider collider)
{
    if (!gate.FullyOpened && gate.Opened && collider.gameObject.layer != 0)
        onCollision.Invoke();          // scene-wired to Gate.FailClose
}
```

- **Enter only.** It reacts to a *new* overlap while the door moves; it cannot say whether someone is
  still there. Occupancy must be polled.
- **`layer != 0`** is the game's "not static geometry" test. Every `Interactable` is off layer 0, so a
  radius-based occupancy test picks up keypads and panels.
- The sensors are disabled during the open animation and re-enabled at the end.

---

## 4. Reflection

Everything read from a private game member goes through a `GameInternals.cs` -
[reflection-lives-in-gameinternals](invariants.md#reflection-lives-in-gameinternals).

All game types are in the **global namespace**. Unity 2022 / netstandard2.1 has no
`[UnsafeAccessor]`, so `FieldInfo` is the right pattern.

---

## 5. The Breathless

- **Which one.** `GameManager.Breathless` (`GameManager.cs:108`) is the roaming monster, also the one
  `Docker.Undock` teleports aboard. Some scripted events use stand-ins (`BreathlessDecision`) that
  NPC.Core does not see.
- **It does not navigate.** `BreathlessController.FixedUpdate` pushes a rigidbody in a random
  direction, re-picked on a timer or when it touches something (`BreathlessController.cs:230-280`,
  `302-345`). A `BreathlessAggressor` steers at the player instead (`BreathlessAggressor.cs:90-92`).
- **How it sees.** A 10 m raycast from `raycastPoint` to the player (`BreathlessController.cs:88`,
  `175`).
- **Cloak.** `Breathless.Visible` is `Data.visible` (`Breathless.cs:52`); it swaps the material and an
  FMOD parameter.
- **Save state.** `Breathless.Enabled` and `SetAggressive` write into the player's save
  (`Breathless.cs:38`, `BreathlessController.cs:283`), so the debug overrides use component flags
  instead ([mod-state-never-enters-the-vanilla-save](invariants.md#mod-state-never-enters-the-vanilla-save)).
- **It never sees an NPC.** Its detectors need a `Player` ([§1](#1-an-npc-is-not-a-player)). A mod whose
  NPC can be caught detects the catch itself and borrows the monster's grab animation, one NPC at a
  time ([one-catch-at-a-time](invariants.md#one-catch-at-a-time)).

---

## 6. What an agent borrows from the player

An `NpcAgent` breathes and walks by the player's own rules, read from the player's code.

### Atmosphere kills by the player's rule

The player's atmosphere buffs (`Space/Player.cs`) each set a **threat** (`Suffocation` 7×,
`Hyperoxia` 3×, `Cold` 6×, `Heat` 8× strength). `HealthSystem.Tick`, once per game tick, sums them:
at **10 or more** `deathCounter` counts up (cap 10), else down; past 5, a 1-in-5 death roll per tick.

| Band | Strength → threat | Lethal alone |
|---|---|---|
| O2 < 6 % / 6–19.5 % | 3 → 21 / 1 → 7 | yes / no |
| O2 32–50 % / > 50 % | 1 → 3 / 2 → 6 | no |
| temp < −20 °C / < 0 °C / < 14 °C | 3 → 18 / 2 → 12 / 1 → 6 | yes / yes / no |
| temp 32–40 °C / > 40 °C | 1 → 8 / 2 → 16 | no / yes |

Two mild bands together can reach 10. An agent runs exactly this in `AtmosphereTick`, while
`NpcAgentSettings.Mortal` and `BreathesAir` are on.

What the player's sum has and an agent's does not (so the player can die first in the same air):

| Source | Threat | Why an agent omits it |
|---|---|---|
| `Hunger` (satiety ≤ 250) | 6 | an agent has no satiety |
| out of bounds in low gravity | 10 | an agent never goes outside |
| heartbeat > 160 / > 200 | 3 / 10 | not reachable from buffs alone |

**Suits.** `Player.Temperature` adds the suit's `TemperatureResistance`; an `isolated` suit reads
22 °C and 22 % O2 regardless.

| Suit | isolated | speed | resistance |
|---|---|---|---|
| `PilotSuit` | no | 1.0 | +100 (+1 °C) |
| `SpaceSuit`, `Space_Suit` | yes | 0.4 | unused |

An agent counts `NpcAgentSettings.SuitTemperatureResistance`, 100 by default (a PilotSuit), so 40 °C
feels like 41 °C to both (`FeltTemperature`, which a mod's own danger bands can use too). A player in
a SpaceSuit, or no suit, is not mirrored.

**The game tick.** `GameManager.FixedUpdate` fires `OnTick` once `tickDelta` reaches `tickTime`,
discarding the remainder, so a tick is ~1.02 s. An agent subscribes to `OnTick` while enabled. The
player's threat reaches `HealthSystem` one listener later, so an agent counts each tick's threat on the
**next** tick (`pendingThreat`) to stay in step. If a capture shows the two a tick apart, the listener
order changed.

`debug_level 2` prints both sides each tick while either is threatened:

```
[ai] Air tick: NPC threat 8, death 0/5 (O2 24.0%, 35.1C) | player threat 14, death 3/5 (O2 24.0%, 35.1C), same air, buffs: Hunger1 Heat1
```

`OTHER air` means they read different `Environment`s - a bug. Same threat, different counter is the
1-in-5 roll.

### Footstep sounds

- **Two events.** `CameraAnimator.footstepEvents` on the player prefab holds `0` =
  `event:/sounds/playerwalkship`, `1` = `event:/sounds/playerwalkstation` (GUIDs resolved through
  `Master.strings.bank`). An agent plays its body's (`NpcBody.FootstepEvents`); a mod passes the
  player's from `NpcPlayer.FootstepEvents()`.
- **Who switches them.** One `FootstepDetector` per station, at the door between its docker
  corridor and its interior, under `StaticObjects/<Station>Parts/Doors` (Shipyard, Fuel, Oxygen,
  Solar). Crossing it sets `1` or `0` by the side the player ends up on (`FootstepDetector.cs`,
  `CameraAnimator.SetFootsteps`), and the choice sticks until the next crossing. So the docker
  corridor, though station floor, has ship steps. `ObservingStation` has no detector.
- **Crossing, not position.** The door plane does not split a station cleanly: `FuelBackyard` and
  `FuelRefinery` lie behind it but are reached through `AirlockMain` off `FuelMain`, so the player
  keeps station steps there. An agent tracks its feet through each detector's doorway instead. Before
  its first crossing it guesses from the side of the ridden station's door.
- **Not placed in the world.** The player's instance never gets 3D attributes. An unplaced 3D event
  sounds from the world origin, which is the ship ([§2a](#2a-the-ship-never-moves---the-world-moves-around-it)).
  An agent gets no trigger events, so it places and fades each step itself
  (`NpcAgent.Presentation.cs`).
