# Architecture - what lives where

Read this before opening source files, so you go straight to the two or three that matter.

---

## 1. Module map

| File | Owns | Doc |
|---|---|---|
| `NpcCorePlugin.cs` | BepInEx entry: config, the one-time import of YourBuddy's old settings, patch groups, the door hook, the node editor's and talk window's GameObjects | [api](api.md#1-depending-on-npccore) |
| `Patches.cs` | every Harmony patch, one nested class per feature: 13 groups, among them undocking, ship rebuild and ship unload for every agent | [invariants](invariants.md#one-patch-per-game-hook) |
| `NpcEvents.cs` | `Tick`, `GameStarting`, `SaveLoaded`, `WorldReset`, each handler isolated | [api](api.md#2-registering-an-npc---npccore) |
| `INpc.cs` | what NPC.Core asks of any mod's NPC | [api](api.md#2-registering-an-npc---npccore) |
| `NpcRegistry.cs` | every NPC of every mod; acting scope and log sources; bodies, claims, rooms across mods | [invariants](invariants.md#several-npc-mods) |
| `NpcLog.cs` | the shared debug level and the acting source | [logging](logging.md) |
| `NpcConsole.cs` | console commands of every mod, installed by one patch | [invariants](invariants.md#one-owner-per-console-command) |
| `CoreCommands.cs` | `npc_node`, `npc_gates`, `node_editor`, `debug_level`, `ai_disable`, `ai_notarget` | [reference](reference.md#2-console-commands) |
| `SceneScan.cs` | the scene-sweep budget, and each frame's arrays | [invariants](invariants.md#scene-sweeps-are-budgeted) |
| `GameInternals.cs` | **every** reflection accessor NPC.Core needs | [invariants](invariants.md#reflection-lives-in-gameinternals) |
| `Navigation/NavGraph.cs` | node storage and ownership, `TryVesselOf`, JSON persistence and import, edge cache, node floor cache, A\* (`FindPath`), `CanReachEntry` | [navigation](navigation.md) |
| `Navigation/NavPath.cs`, `NodeType.cs` | a planned route; node kinds | [navigation](navigation.md#4-a-findpath) |
| `Navigation/NavProbe.cs` | **every physics probe**: floors, line of sight, `CanSee`, collider filter, gate cache | [probes](probes.md) |
| `Navigation/NodeEditor.cs` | F8 editor overlay, node and link placement | [reference](reference.md#3-node-editor) |
| `Navigation/StationRooms.cs` | the docked station's rooms by name, which nodes stand in each | [navigation](navigation.md#rooms-by-name) |
| `World/NpcVessels.cs` | whose floor this is and which transform to ride (`FloorOwner`, `AnchorForOwner`, `InteriorOf`) | [game-model](game-model.md#2a-the-ship-never-moves---the-world-moves-around-it) |
| `World/NpcDoors.cs` | what every NPC knows about doors: open or route around, codes, doorway sensors, airlocks, NPC closes; `INpcDoorUser` | [doors](doors.md) |
| `World/NpcRooms.cs` | `IRoomKeeper`s: rooms the mods keep loaded | [invariants](invariants.md#a-kept-room-stays-loaded) |
| `World/SellPens.cs` | the fenced footprint of every sell station: `InAFencedPen` | [invariants](invariants.md#a-fenced-sell-station-is-not-somewhere-to-stand) |
| `World/NpcMonster.cs` | the single catch, and the `ai_disable` / `ai_notarget` flags | [invariants](invariants.md#one-catch-at-a-time) |
| `World/NpcCorpse.cs` | makes a dead NPC's ragdoll carryable, without the game's `Grabbable` | [invariants](invariants.md#mod-state-never-enters-the-vanilla-save) |
| `World/NpcLifecare.cs` | NPC icons and the count on the lifecare terminal; `INpcLifeform` | [lifecare](lifecare.md) |
| `World/NpcHiding.cs` | `INpcHider`: hiding spots an NPC is in | [api](api.md#4-the-world---npccoreworld) |
| `World/NpcPlayer.cs` | the player as NPCs see it: `Pilot`, `Controller`, its step sounds, the colliders every NPC passes through | [invariants](invariants.md#npcs-never-block-each-other) |
| `Saves/NpcSaves.cs` | every mod's sidecar: register, write, delete, prune | [invariants](invariants.md#one-sidecar-per-mod) |
| `Saves/CoreSidecar.cs` | NPC.Core's own `.npccore`: the door codes | §4 |
| `Interaction/NpcInteraction.cs` | which NPC answers Interact, the input handover, each conversation's log; `INpcConversation` | [interaction](interaction.md) |
| `Interaction/NpcTalkWindow.cs`, `TalkSkin.cs` | the one Interact listener; the talk window and how it is drawn | [interaction](interaction.md#2-the-panel) |
| `Agents/NpcAgent*.cs` | the walking agent: walks and plan following, steering and recovery, doors, rooms, vessels, space, air, the catch, death, footsteps, debug lines | [agent](agent.md) |
| `Agents/INpcBrain.cs`, `NpcActivity.cs`, `NpcAgentSettings.cs`, `NpcBody.cs` | what an agent asks its mod, what it may do, the body it drives and who it is | [agent](agent.md#1-attaching-an-agent) |
| `Agents/ReachTask.cs`, `NpcHands.cs` | something to walk up to and use; one held item | [agent](agent.md#6-walking-into-reach) |

## 2. Dependency direction

```
NpcCorePlugin ─▶ Patches ─┬─▶ NavGraph ─▶ NavProbe ─▶ NpcRegistry
                          │      └─ door hook ─▶ NpcDoors
                          ├─▶ World: NpcDoors, NpcRooms, NpcMonster, NpcLifecare, NpcHiding ─▶ NpcVessels
                          ├─▶ NpcSaves ◀─ CoreSidecar
                          ├─▶ NpcAgent (docking, rebuild, unload)
                          └─▶ NpcEvents ─▶ each mod's handlers
NpcAgent ─▶ NavGraph, NavProbe, World, NpcRegistry;  ─▶ INpcBrain (the mod's)
NodeEditor ─▶ NavGraph            NpcTalkWindow ─▶ NpcInteraction
everything ─▶ GameInternals, SceneScan, NpcLog            mods ─▶ everything public
```

`NavProbe` asks `NpcRegistry` which colliders are bodies; the patches reach a mod's NPC only through
`NpcRegistry`, the optional interfaces (`INpcDoorUser`, `INpcLifeform`, `INpcHider`,
`INpcConversation`) and `NpcAgent`, a mod's state only through `IRoomKeeper`, its sidecar callback and
an agent's `INpcBrain`. Nothing in NPC.Core knows any mod.

## 3. Caches and their lifetimes

| Cache | Where | Invalidated by |
|---|---|---|
| Gate list | `NavProbe.CachedGates` | 5 s TTL; `InvalidateGates()` on a save load |
| Gate placement per frame | `NavProbe.FrameGates` | every frame ([probes.md](probes.md#caches)) |
| Gate openings | `NavProbe.GateOpenings` | `InvalidateGates()` only |
| Node edges, id index | `NavGraph._edges`, `NodesById` | `_edgesDirty`, dock change, ship layout change |
| Node hover | `NavGraph.NodeHover` | 3 s TTL; cleared on edge rebuild |
| Space objects | `NavGraph.SpaceObjects` | 5 s TTL, or at once when one is destroyed |
| Collider kinds, floor answers | `NavProbe.ColliderKinds` / `FloorAnswers` | every frame |
| Scene arrays | `SceneScan.ThisFrame` | every frame |
| Doorway sensors (by gate), airlocks, pin panels | `NpcDoors` | 5 s TTL each; `ResetScene` on a new game or a load |
| Gates routing avoids | `NpcDoors.ImpassableGates` | 1 s TTL; `ResetScene` |
| Sell station pens | `SellPens.Pens` | 10 s TTL |
| Lifecare icons | `NpcLifecare.Icons` | re-created when a clone is lost; an NPC's goes when it unregisters |
| Environments | `NpcAgent`, per agent | 5 s TTL |
| Footstep doorways | `NpcAgent.footstepDetectors`, per agent | 5 s TTL |

TTL caches heal themselves; don't add invalidation hooks. Gates, edges and the door caches need
explicit invalidation because a dock or scene change alters the *set* of objects; `NpcEvents.WorldReset`
tells the mods at the same moment. Owner transforms and docked state are
never cached ([never-cache-node-world-positions](invariants.md#never-cache-node-world-positions)).

## 4. Files on disk

| File | Written by | Content |
|---|---|---|
| `BepInEx/config/com.bytenull1.npccore.cfg` | BepInEx | settings; the first run imports YourBuddy's old graph, editor and debug-level keys |
| `BepInEx/config/NPC.Core/nodegraph.json` | `NavGraph.Save` (F6, or 20 s after a change) | the player's own nodes and links per owner, and which owners they forked |
| `BepInEx/config/NPC.Core/nodegraph.bundled.json` | `NavGraph.LoadBundled`, from an embedded resource | the shipped graph; rewritten when `BundleVersion` changes |

| `<game save folder>/SaveFiles/<save>.npccore` | `NpcSaves`, after the game writes that save | the door codes NPCs were told; only while there are any |
| `<game save folder>/SaveFiles/<save>.<extension>` | `NpcSaves`, from each mod's callback | that mod's own state, such as YourBuddy's `.buddy` |

`nodegraph.json` is user data and **evidence** for routing questions - read it. The bundled file is a
build input: regenerate it with `tools/bundle_nodegraph.py`, never by hand. Sidecars go with their save:
NPC.Core deletes them when the game deletes or wipes saves, and prunes any whose save is gone.

## 5. Change impact - what else to update

| If you change | Also update |
|---|---|
| anything `public` | `PublicAPI.Unshipped.txt` and [api.md](api.md) ([api.md §9](api.md#9-changing-the-api)); a removal or changed signature breaks every mod built on the old one |
| `FindPath` signature or `NavPath` | `NpcAgent` and every brain that plans; each construction must fill `WaypointFloorY` |
| what counts as a reachable entry | only `NavGraph.CanReachEntry` ([one-entry-predicate](invariants.md#one-entry-predicate)) |
| whether an NPC can **see** something | only `NavProbe.CanSee` ([sight-stops-at-a-shut-door](invariants.md#sight-stops-at-a-shut-door)) |
| a `ThinLos` parameter | re-check the entry allowance and the stair seed cap |
| the gate-frame rule | keep it hit-point based and the *opening* ([gate-frame-hit-point](invariants.md#gate-frame-hit-point), [gate-carveout-is-the-opening](invariants.md#gate-carveout-is-the-opening)) |
| which layers a probe sees | only `NavProbe.ProbeLayers` / `BodyMaskFor` ([probe-what-the-body-collides-with](invariants.md#probe-what-the-body-collides-with)) |
| anything reflected from the game | an accessor in `GameInternals.cs`, never `GetField` elsewhere |
| a tuning constant | [reference.md](reference.md), and [same-level-tolerance-ceiling](invariants.md#same-level-tolerance-ceiling) |
| the shipped node graph | regenerate with `tools/bundle_nodegraph.py` **and bump `BundleVersion`**, or installs won't pick it up |
| which ship nodes are live | only `OwnerSnapshot` (`IsLive`, `WorldOf`, `LinkIsLive`) ([a-ship-node-rides-its-room](invariants.md#a-ship-node-rides-its-room)) |
| matching a scene object to its vessel | only `TryVesselOf` ([a-station-owns-its-interior-by-reference](invariants.md#a-station-owns-its-interior-by-reference)) |
| a game hook every NPC mod needs | a patch group here, never one per mod ([one-patch-per-game-hook](invariants.md#one-patch-per-game-hook)); an `NpcEvents` event or an optional interface if mods must act on it |
| which doors NPCs may open, or route around | only `NpcDoors.MayOpen` / `BlocksRouting` ([opening-is-not-passing](invariants.md#opening-is-not-passing)) |
| an optional NPC interface (`INpcDoorUser`, `INpcLifeform`, `INpcHider`, `INpcConversation`) | [api.md](api.md), and every mod that implements it |
| `INpcBrain`, `NpcActivity` or `NpcAgentSettings` | [agent.md](agent.md), [api.md](api.md), and every brain |
| how a stair leg is walked or abandoned | `NpcAgent.TryStairLegBand` is the one definition; `HeadingAlongPlan`, `HasLeftStairLeg`, `AdvancePlan`, `Update` and `UpdateStuckDetection` read it |
| the whisker capsule | only `NpcAgent.BodyCastCapsule` ([whiskers-are-body-shaped](invariants.md#whiskers-are-body-shaped)) |
| what steering treats as an obstacle | only `NavProbe.HitIsWalkableGround` ([walkable-ground-is-not-an-obstacle](invariants.md#walkable-ground-is-not-an-obstacle)) |
| a plan-invalidation reason in `Pursue` | does it fire on Force/Priority segments? Then it needs an `IsForced` guard |
| where a body's floor height comes from | only `NpcAgent.FloorUnderNpc` / `FloorUnderPlayer`; `FindPath` takes `start.y` as given ([the-start-point-is-already-a-floor](invariants.md#the-start-point-is-already-a-floor)) |
