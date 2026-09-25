# Contributing to NPC.Core

Bug reports, nav-graph fixes and code changes are all welcome.

---

## Reporting a bug

Open an issue on the [issue tracker](https://github.com/bytenull1/npc-core-inhl/issues). Say which NPC
mod you saw it with - a bug in how an NPC decides what to do belongs to that mod; a bug in how it finds
its way, or in two mods getting in each other's way, belongs here.

1. **What happened, and where.** Station or ship, room, what the NPC was doing.
2. **A log.** In the game console `debug_level 2`, reproduce the problem, then take
   `BepInEx/LogOutput.log`. Add a plain-text note where things went wrong.
3. **Positions, for navigation.** Where you stood, where the NPC stood, and which nav nodes are nearby
   (`node_editor` shows them).

More on logs: [docs/logging.md](docs/logging.md).

---

## Setting up

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) and own a copy of the game with
   [BepInEx 5](https://github.com/BepInEx/BepInEx/releases).
2. Copy the reference DLLs from your game into `NPC.Core/lib/` - the list is in
   [NPC.Core/lib/README.md](NPC.Core/lib/README.md). They are not redistributed.
3. Build: `dotnet build NPC.Core/NPC.Core.csproj -c Release`
4. Copy `NPC.Core/bin/Release/netstandard2.1/NPC.Core.dll` to `BepInEx/plugins/`, with a mod that uses
   it. Clone [YourBuddy](https://github.com/bytenull1/yourbuddy-inhl) beside this repository and it
   builds against your checkout.

Optional, for reading the game's own code and scene: [decompiled/README.md](decompiled/README.md).
The decompile is the game developer's code and must never be committed.

---

## Making a change

- **Keep changes small** and focused on one problem.
- **Build clean.** Debug and Release must both build; every warning is an error.
- **Run the checks** CI runs: `python tools/doccheck.py` and `python tools/bundle_nodegraph.py --check`.
- **Mind the public API.** Mods compile against it ([docs/api.md](docs/api.md)); a changed signature
  breaks them. The build lists every public member in `NPC.Core/PublicAPI.*.txt`
  ([api.md §9](docs/api.md#9-changing-the-api)). Rebuild YourBuddy against your change before you open
  a pull request.
- **Test in the game.** For navigation, test stairs and doorways on both the ship and a station.
- **Follow the code style** in [AGENTS.md §3](AGENTS.md#3-code-conventions), and update the docs that
  own the subject ([AGENTS.md §4](AGENTS.md#4-documentation-rules)).

### Changing the shipped nav graph

1. Edit the graph in game (F8 editor, see the [README](README.md#the-node-editor)). It saves to
   `BepInEx/config/NPC.Core/nodegraph.json`.
2. Regenerate the bundled copy - this also bumps `BundleVersion`, without which no one gets the update:
   `python tools/bundle_nodegraph.py`
3. Rebuild and commit `NPC.Core/Resources/nodegraph.bundled.json`.
