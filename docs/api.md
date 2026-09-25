# API - building an NPC mod on NPC.Core

What a mod may use, and the rules that keep several NPC mods working side by side. Everything listed
here is `public`; anything else is internal and may change.

---

## 1. Depending on NPC.Core

NPC.Core is its own BepInEx plugin. Reference its DLL, never ship it:

```xml
<Reference Include="NPC.Core">
  <HintPath>lib\NPC.Core.dll</HintPath>
  <Private>false</Private>
</Reference>
```

```csharp
[BepInPlugin("com.you.mymod", "MyMod", "1.0.0")]
[BepInDependency(NpcCorePlugin.Guid, NpcCorePlugin.Version)]
public sealed class MyModPlugin : BaseUnityPlugin { }
```

BepInEx then loads NPC.Core first, and refuses to load your mod without it or with an older one
([one-core-instance](invariants.md#one-core-instance)). `NPC.Core.xml` next to the DLL gives your IDE
these docs. [samples/WanderingNpc](../samples/WanderingNpc/README.md) is a whole mod in two files.

---

## 2. Registering an NPC - `NPC.Core`

An NPC is whatever GameObject your mod builds. NPC.Core does not spawn bodies. It needs to know every
NPC, through `INpc`:

| Member | What NPC.Core asks it for |
|---|---|
| `ModName`, `Name` | log source `ModName:Name`; `Name` is unique within your mod |
| `LogSource` | from `NpcRegistry.LogSourceFor(ModName, Name)` |
| `Transform`, `Controller` | position; the controller other NPCs pass through |
| `IsDead`, `IsActive` | a parked or dead NPC holds no room and is no one's obstacle |
| `IsOwnBody(t)` | probes treat it as a body, never floor or wall |
| `SolidParts()` | colliders other NPCs' controllers must not bump (an item blocker, the ragdoll once dead) |
| `Holds(t)` | what it is working on, so no other NPC takes it |
| `TracksRoom(room)` | the room it is in, which stays loaded |

`NpcRegistry`:

| Member | Use |
|---|---|
| `Register(npc)` / `Unregister(npc)` | before the GameObject activates / on despawn and `OnDestroy`; both idempotent |
| `All`, `Snapshot()`, `IndexOf(npc)` | every NPC of every mod, in registration order |
| `IsGone(npc)` | null, or a Unity object already destroyed |
| `Registered`, `Unregistered` | events |
| `Acting(npc)` | `using NpcRegistry.ActingScope _ = NpcRegistry.Acting(npc);` around every entry into NPC code ([npc-code-runs-acting](invariants.md#npc-code-runs-acting)) |
| `ActingNpc`, `ActingLog` | the NPC whose code is running, and its log source; null outside |
| `IsNpcBody(t)` | any NPC's body |
| `TakenByAnother(t, me)` | another NPC holds `t` ([one-npc-per-target](invariants.md#one-npc-per-target)) |
| `AnotherNpcWhere(test, me)` | another living, loaded NPC stands where `test` says |
| `OtherTrackedIn(room, me)` | keep the room loaded ([rooms-stay-loaded-for-every-npc](invariants.md#rooms-stay-loaded-for-every-npc)) |
| `IgnoreBodies(a, b)`, `IgnoreAllBodies(npc)` | from `OnEnable`, `Start` and on death; the player's colliders included, and restored once a second after a switch-off ([npcs-never-block-each-other](invariants.md#npcs-never-block-each-other)) |

`NpcLog.Log` writes to the acting NPC's source, else NPC.Core's; `NpcLog.Level` is the one debug level
every NPC mod shares ([logging.md](logging.md)).

`NpcConsole.Register(owner, name, run)` adds a console command; `NpcConsole.Print` answers in the
console; `NpcConsole.Alias` keeps an old name; `NpcConsole.IsOpen` tells overlays and hotkeys to step
aside ([one-owner-per-console-command](invariants.md#one-owner-per-console-command)).

`SceneScan.MayRescan` and `SceneScan.ThisFrame<T>()` budget whole-scene sweeps for every mod at once
([scene-sweeps-are-budgeted](invariants.md#scene-sweeps-are-budgeted)).

`NpcEvents` raises the game's moments from NPC.Core's own patches, so no mod patches these methods
([one-patch-per-game-hook](invariants.md#one-patch-per-game-hook)). A handler that throws is logged and
skipped; the other mods' handlers still run.

| Event | When |
|---|---|
| `Tick` | every `GameManager.FixedUpdate`: exactly while a game scene is loaded |
| `GameStarting(newGame)` | `GameManager.Start`, before the game wakes the player; `newGame` is the game's own test ([game-model.md](game-model.md#a-new-game)) |
| `SaveLoaded(saveName)` | `SceneLoader.LoadGame` accepted a save; read your sidecar here, act once the scene is ready |
| `WorldReset` | a new scene is coming, before either of the two above: drop your scene caches |

---

## 3. Navigation - `NPC.Core.Navigation`

The walkable network is one hand-placed graph, shared by every NPC mod ([navigation.md](navigation.md)).

| Member | Use |
|---|---|
| `NavGraph.FindPath(start, goal, cameFrom?, avoidEntry?)` | a `NavPath`, or null. `start` is the floor under your NPC, never its body ([the-start-point-is-already-a-floor](invariants.md#the-start-point-is-already-a-floor)) |
| `NavGraph.LastPathBlockedByDoor` | the last search failed on a door no NPC can open: wait, do not wander |
| `NavGraph.CanReachEntry(start, waypoint)` | the one entry predicate; re-check a plan with it ([one-entry-predicate](invariants.md#one-entry-predicate)) |
| `NavGraph.WaypointIsOnAReachableDeck` | whether an old plan is still worth walking |
| `NavGraph.RandomNode(owner?)`, `CollectActiveNodes`, `NearestActiveNodeOwner` | destinations |
| `NavGraph.TryVesselOf(t, out ship, out spaceObject)` | which vessel a scene object belongs to |
| `NavGraph.NodeCount`, `GetNodeWorld(i)`, `GetNodeOwner(i)`, `GetNodeType(i)`, `IsNodeActive(i)` | read the graph: a node's world position, its owner (`NavGraph.ShipOwner`, or a station), `NodeType.Ground` or `Stair`, and whether its vessel is here and built. Only NPC.Core edits it |
| `NavPath` | `Waypoints`, `IsForced(i)` (arrived by a drawn link), `FloorY(i)` (the deck under it) |
| `NavProbe.TryFloorHeight`, `FloorHeight`, `TryFloorCollider` | the floor under a point ([probes.md](probes.md#floor-probing)) |
| `NavProbe.ThinLos`, `WalkLos`, `GroundIsContinuous` | walkability of a line |
| `NavProbe.CanSee(from, to, target, out shutGate)` | an eye's line of sight; shut doors block it |
| `NavProbe.HitIsWalkableGround` | whether a hit is ground the controller walks over |
| `NavProbe.DistanceToSegment(p, a, b)` | distance from a point to a segment |
| `NavProbe.ProbeLayers`, `BodyMaskFor(layer)`, `CollisionMaskFor(layer)` | what the walker, a body on `layer`, or any collider on `layer` collides with |
| `StationRooms.Current`, `TryResolve`, `Join` | the docked station's rooms by name (`Entry`: `Name`, `Keys`, `Nodes`), what a player's words name, and names for a reply |

The graph assumes a walker the player's size that collides with what the player collides with: put
your NPC's controller on the player's layer.

---

## 4. The world - `NPC.Core.World`

What every NPC shares about the station and the ship. Game background: [game-model.md](game-model.md).

**Vessels.** The game moves the world, not the ship
([game-model.md §2a](game-model.md#2a-the-ship-never-moves---the-world-moves-around-it)).

| Member | Use |
|---|---|
| `NpcVessels.FloorOwner(pos, out owner, out anchor)` | which vessel's floor this is (`FloorOwnership`: `PlayerShip`, `Elsewhere`, `Unknown`), its owner name, and the transform to parent an NPC standing there to ([aboard-is-answered-by-the-floor](invariants.md#aboard-is-answered-by-the-floor)) |
| `NpcVessels.OwnerOfTransform(t)` | which vessel a scene object belongs to |
| `NpcVessels.AnchorForOwner(owner)` | the transform an owner's occupants ride; null for the player ship |
| `NpcVessels.InteriorOf(spaceObject)` | where a station keeps its interior |

**Doors** ([doors.md](doors.md)).

| Member | Use |
|---|---|
| `NpcDoors.MayOpen(gate)` / `BlocksRouting(gate)` | open it yourself? route around it? ([opening-is-not-passing](invariants.md#opening-is-not-passing)) |
| `NpcDoors.WhyClosedToPlayer(gate)` | why a detector-driven door is shut to the player, for your log |
| `NpcDoors.IsAirlockGate`, `IsPasswordGate`, `PinPanelFor`, `RoomIsAirlockChamber` | what kind of door or room |
| `NpcDoors.LearnCode`, `KnownCodes`, `AnyKnownDoorMatches` | door codes the player gave any NPC; saved by NPC.Core |
| `NpcDoors.Detectors`, `RoomsOf`, `DoorOf`, `TryInnerSide`, `Optimizes`, `DetectorsVersion` | doorway sensors, for room tracking |
| `NpcDoors.NoteClosedBy(gate, npc)`, `WasClosedByNpc(gate, out by)` | right after your NPC calls `Gate.Close` ([npc-closes-must-not-move-the-player](invariants.md#npc-closes-must-not-move-the-player)); whether an NPC closed it just now |
| `NpcDoors.ImpassableCount` | doors routing is avoiding, for a "waiting" line |
| `INpcDoorUser` | implement it on an NPC that closes doors: `OnCloseFailed(gate)` (the game undid a close; act only if you stand in it), `OnDoorClosedAway(doorway)` (release the rooms your close left loaded) |

**Rooms.** `NpcRooms.AddKeeper(IRoomKeeper)` (and `RemoveKeeper`) keeps rooms loaded that your NPCs need but do not stand
in; `NpcRooms.ReasonToKeep(room)` is what your NPC asks before it switches a room off
([a-kept-room-stays-loaded](invariants.md#a-kept-room-stays-loaded)). `SellPens.InAFencedPen(point,
except, margin)` and `SellStationClearance` keep an NPC out of sell station pens
([a-fenced-sell-station-is-not-somewhere-to-stand](invariants.md#a-fenced-sell-station-is-not-somewhere-to-stand));
`SellPens.HoldsSellStation(room)`; `ReachTask.StandAllowed` uses `SellStationClearance` by default.

**The Breathless.** It never sees an NPC; a mod whose NPC can be caught detects the catch itself.

| Member | Use |
|---|---|
| `NpcMonster.CatchingAnother(me)`, `BeginCatch`, `EndCatch`, `Catching` | one catch at a time ([one-catch-at-a-time](invariants.md#one-catch-at-a-time)) |
| `NpcMonster.NpcsDisabled` | `ai_disable npc`: switch your NPC's AI off while set |
| `NpcMonster.MonsterDisabled` | `ai_disable monster`: no catch, no fear |
| `NpcMonster.NoTarget` | `ai_notarget`: the monster may not target the player |
| `NpcMonster.PlayerIgnored` | `ai_notarget` or a disabled monster; a catch that borrowed the aggressor restores it only while this is false ([the-ai-overrides-have-one-owner](invariants.md#the-ai-overrides-have-one-owner)) |

**The player, bodies and the terminal.**

| Member | Use |
|---|---|
| `NpcPlayer.Pilot`, `Controller` | the player and its `CharacterController`; null outside a game scene |
| `NpcPlayer.FootstepEvents()` | the player's step sounds, for `NpcBody.FootstepEvents` |
| `NpcCorpse` | add it to a loose ragdoll and call `Init(npc, rigidbody)`: the player can carry it, as they carry the helper robot's |
| `INpcLifeform` | implement it and the lifecare terminal shows your NPC while `ShowsOnLifecare`; `IsAboardPlayerShip()` from the floor, `TrackedRoomName` for the log ([lifecare.md](lifecare.md)) |
| `INpcHider.Occupies(spot)`, `NpcHiding.OccupiedByNpc(spot)` | an NPC inside a hiding spot; the game will not mount the player into it |

---

## 5. Saves - `NPC.Core.Saves`

`NpcSaves.RegisterSidecar(owner, extension, write)` adds a file `<save>.<extension>` beside every save.
`write(saveName)` returns its contents, or null for none, which deletes an old one. NPC.Core writes it
after the game writes the save, and deletes and prunes it with its save
([one-sidecar-per-mod](invariants.md#one-sidecar-per-mod)). Read yours with
`NpcSaves.Read(saveName, extension)` on `NpcEvents.SaveLoaded`, and apply it once the scene is ready.
`NpcSaves.PathOf` names the file for your log. NPC.Core's own is `.npccore`.

---

## 6. Talking - `NPC.Core.Interaction`

`NpcInteraction.Register(conversation)` makes an NPC talkable through NPC.Core's window
([interaction.md](interaction.md)). It is dropped when the NPC unregisters.

| `INpcConversation` | |
|---|---|
| `Npc` | the NPC talked to |
| `CanTalk` | your conditions; NPC.Core already checks it lives and is loaded |
| `Title`, `Greeting` | the window's title; said the first time it opens on this NPC |
| `Commands` | the commands page, each sent as if typed |
| `Answer(text)` | the NPC's reply, run inside its acting scope |
| `SetOpen(open)` | hold the NPC still facing the player while open |

`NpcInteraction.IsOpen` tells hotkeys to stay quiet.

---

## 7. Walking - `NPC.Core.Agents`

`NpcAgent.Attach(root, identity, body, settings, brain)` makes a body your mod built into an NPC that
walks the graph, opens doors, loads rooms, rides vessels, breathes and dies; your `INpcBrain` says
where to go. Register the agent, not the brain. The whole contract: [agent.md](agent.md).

| Member | Use |
|---|---|
| `NpcIdentity(ModName, Name, Number)` | who it is; `Number` staggers its work and settles who steps aside |
| `NpcBody` | the ragdoll, animated model, item blocker and step sounds |
| `NpcAgentSettings` | what it may do, read live: speed, doors, space, mortality, suit, corpse, lifecare, debug lines |
| `INpcBrain` | the hooks the agent calls ([agent.md §3](agent.md#3-the-brain)) |
| `NpcActivity`, `NpcRecovery` | the kind of walk, for stall recovery ([agent.md §4](agent.md#4-recovery)) |
| `NpcAgent.Pursue`, `Wander`, `Stay`, `HeadAlongPlan`, `SimpleAdvance` | the walks ([agent.md §5](agent.md#5-walks)) |
| `NpcAgent.CommitPlan`, `AdvancePlan`, `DropPlan`, `Plan`, `HasPlanLeft`, `HasLeftStairLeg`, `WaypointOverdue` | a plan the brain chose |
| `NpcAgent.PlanReach`, `StepIntoReach`, `InReach`, `FindReachNode`, `ReachTask` | walking up to something to use it ([agent.md §6](agent.md#6-walking-into-reach)) |
| `NpcAgent.Asleep`, `CurrentOwner`, `RideOwner`, `DescribeSurroundings`, and the rest | [agent.md §5](agent.md#5-walks) lists every member a brain uses |
| `NpcAgent.Hands` (`NpcHands`) | one held item ([agent.md §7](agent.md#7-hands)) |

---

## 8. What NPC.Core owns

Mods must not duplicate these, or two copies of the same state fight:

- the nav graph and its files, `BepInEx/config/NPC.Core/`, and the node editor with its keys
  (F8, Insert, Numpad0, Delete, L, K, U, T, F6);
- the console commands `npc_*`, `node_editor`, `debug_level`, `ai_disable`, `ai_notarget`;
- the game's Interact key for NPCs, and the input handover to the UI
  ([one-interact-owner](invariants.md#one-interact-owner));
- the sidecar extension `npccore`;
- the Harmony patches on `GameManager.FixedUpdate` and `Start`, `SceneLoader.LoadGame`,
  `ConsoleMenu.Init`, `EntryDetector.DoorCheckForEnter`, `Gate.FailClose`, `Room.SetContentEnabled`,
  `SaveParser.WriteSaveFile` / `DeleteSaveFile` / `ClearSaveFiles` / `SyncSavePreviews`,
  `BreathlessController.PlayerEntered` / `DetectItem`, `BreathlessAggressor.DetectItem`,
  `LifecareDisplay.UpdatePlayerIcon` / `UpdateLifeformsCount`, `HidingSpot.Interact`, `Docker.Undock`,
  `StorymodeShipBuilder.SetShipLevel` and `SpaceShip.SetContentEnabled`
  ([one-patch-per-game-hook](invariants.md#one-patch-per-game-hook)).

---

## 9. Changing the API

`NPC.Core/PublicAPI.Shipped.txt` lists every public member of a released version,
`PublicAPI.Unshipped.txt` what was added since. The build fails on a public member missing from
them (RS0016) or listed but gone (RS0017), so a change to what mods build against is always a visible
diff.

- **Adding:** list the member in `Unshipped` (the IDE fix does it, or
  `dotnet format analyzers NPC.Core/NPC.Core.csproj --diagnostics RS0016`), and document it here.
- **Changing or removing** a shipped member breaks every mod built against it. Do it only with a new
  major version, and say so in the release notes.
- **Releasing:** move `Unshipped` into `Shipped`.
- Not meant for mods? Make it `internal`. Public is a promise.
