# NPC.Core

A shared library for NPC mods of *Isolated Inhale* (BepInEx 5). The game has no navmesh and no NPC
navigation; NPC.Core provides it, and keeps several NPC mods from getting in each other's way.

- **Navigation:** a hand-placed node graph for the ship and every station, shipped ready-made, with A\*
  that handles stairs, decks, doors it cannot open, docking and the ship's upgrades.
- **Probes:** floor heights, walkability and line of sight that understand doorways and airlocks.
- **A walking agent:** put it on a body your mod built and it follows routes, steers round obstacles,
  opens and closes doors, rides stations, stays out of space, breathes and can die; your mod decides
  where it goes.
- **One registry for every NPC mod:** NPCs of different mods pass through each other, leave alone what
  another is working on, keep each other's rooms loaded, and log under their own names.
- **The world, shared:** which doors NPCs may open, door codes the player gave them, rooms kept loaded,
  one catch by the Breathless at a time, carryable bodies, NPCs on the lifecare terminal, and a talk
  window opened with Interact.
- **Saves:** each mod's state in its own file beside the game's save, deleted with it. The game's save
  is never touched, so uninstalling leaves it intact.
- **Tools:** an in-game node editor, console commands, and one debug level for every NPC mod.

NPC.Core adds no NPC by itself. Install it for a mod that needs it, such as
[YourBuddy](https://github.com/bytenull1/yourbuddy-inhl).

---

## Installation

1. Install **[BepInEx 5](https://github.com/BepInEx/BepInEx/releases)** for Isolated Inhale (Windows x64).
2. Put `NPC.Core.dll` into `BepInEx/plugins/`, next to the NPC mods that use it. One copy serves them all.
3. Launch the game. The configuration file is `BepInEx/config/com.bytenull1.npccore.cfg`.

Coming from YourBuddy 1.0.x: your own nav graph and editor keys are carried over on first start.

---

## Configuration

| Setting | Section | Default | Description |
|---|---|---|---|
| `DebugLevel` | Debug | `1` | Log verbosity of every NPC mod: `0` warnings only, `1` normal, `2` path planning and gate audits, `3` per-frame obstacle reports. Also `debug_level`. |
| `BundledGraph` | Navigation | `true` | Use the ready-made nav graph for any ship or station you have not edited yourself. |
| `EditorToggleKey` … `EditorSaveKey` | NodeEditor | F8, Insert, Delete, L, K, U, T, F6 | The node editor's keys. |

## Console commands

| Command | Arguments | Description |
|---|---|---|
| `npc_node` | `add`, `count`, `clear`, `save`, `list`, `remove <index>`, `link <a> <b>`, `unlink <index>`, `type <index> <ground\|stair>`, `bundled`, `unfork <owner>` | Manage the nav graph. `link` toggles a link; `bundled` shows which ships and stations use the shipped graph; `unfork` hands one back to it. |
| `npc_gates` | – | Every gate in the scene and the doorway carve-out it was given. |
| `node_editor` | – | Toggle the node editor overlay. |
| `debug_level` | `<0-3>` | Log verbosity of every NPC mod. |
| `ai_disable` | `[npc\|monster\|all] [on\|off]` | Switch off the AI of every NPC, of the Breathless, or both. Not saved; a save load clears it. |
| `ai_notarget` | `[on\|off]` | The Breathless ignores you; NPCs can still be caught. Not saved. |

## The node editor

NPCs walk a node graph. **NPC.Core ships one for the ship and every station.** The moment you edit
anything on a ship or station, that one becomes yours: it is saved to
`BepInEx/config/NPC.Core/nodegraph.json` (about 20 seconds after a change) and updates no longer change
it. `npc_node bundled` shows which are yours; `npc_node unfork <owner>` returns one to the shipped graph
(after a restart).

| Key | Action |
|-----|--------|
| `F8` | Toggle the editor overlay |
| `Insert` / `Numpad0` | Place a node at your position |
| `Delete` | Remove the nearest node on your deck |
| `L` | Toggle connection lines |
| `K` | Link two nodes - press once at one node, again at another (again on a linked pair removes it) |
| `U` | Remove every link of the nearest node |
| `T` | Ground <-> **Stair** node |
| `F6` | Save nodes |

Nodes are never connected automatically. A link always works, at any distance and with no line of
sight, so it is also how NPCs cross ramps, steep stairs and railings. Grey nodes belong to a station you
are not docked to, or a ship room not built yet.

---

## For mod authors

Read [docs/api.md](docs/api.md): how to depend on NPC.Core, register your NPCs, and plan routes. The
rules that keep NPC mods compatible are in [docs/invariants.md](docs/invariants.md#several-npc-mods).
[samples/WanderingNpc](samples/WanderingNpc/README.md) is a complete small mod to start from.

## Building from source

1. Install the [.NET SDK](https://dotnet.microsoft.com/download).
2. Copy the reference DLLs from your game into `NPC.Core/lib/` - the list is in
   [NPC.Core/lib/README.md](NPC.Core/lib/README.md). They are not redistributed.
3. `dotnet build NPC.Core/NPC.Core.csproj -c Release`
4. Copy `NPC.Core/bin/Release/netstandard2.1/NPC.Core.dll` (and `NPC.Core.xml`, for mod authors) to
   `BepInEx/plugins/`. Close the game first - it locks the file.

Contributing: [CONTRIBUTING.md](CONTRIBUTING.md). Technical documentation: [docs/](docs/README.md).
