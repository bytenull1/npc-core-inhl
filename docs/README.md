# NPC.Core documentation

How the library works and why it is built the way it is. For installing, see the
[main README](../README.md). For contributing, see [CONTRIBUTING.md](../CONTRIBUTING.md).

| Doc | Read it for |
|---|---|
| [api.md](api.md) | **building an NPC mod**: what is public, how to register NPCs, plan routes and add commands |
| [architecture.md](architecture.md) | which file does what, caches, files on disk, what to update when you change something |
| [invariants.md](invariants.md) | **rules that must not be broken**, each with an anchor |
| [agent.md](agent.md) | the walking agent (`NpcAgent`): attaching one to a body, its brain, recovery, walks, reach, hands |
| [navigation.md](navigation.md) | node graph, A\*, following a path, stairs |
| [probes.md](probes.md) | floor heights, line of sight, doorways (`NavProbe`) |
| [doors.md](doors.md) | which doors NPCs open or route around, door codes, doorway sensors; how an agent opens and closes them |
| [game-model.md](game-model.md) | how the game itself works: rooms, the moving world, gates, the Breathless |
| [lifecare.md](lifecare.md) | NPCs on the ship's lifecare terminal |
| [interaction.md](interaction.md) | the talk window: which NPC answers Interact, and the panel |
| [logging.md](logging.md) | debug levels, log sources and tags, rules for adding logs |
| [reference.md](reference.md) | tuning constants, console commands, node editor details |
| [known-issues.md](known-issues.md) | open issues, dead ends, abandoned approaches |

Start with `api.md` to use NPC.Core, `architecture.md` for "where is it", `invariants.md` for "why is it
like this", and the topic doc for "how does it work".

**Conventions**

- Each rule is stated once, in `invariants.md`. Other docs and code link to it.
- Docs explain *why* and *what breaks if you change it*, not what the code already says.
- Docs describe the current code. No dates, status notes or history - see
  [AGENTS.md §4](../AGENTS.md#4-documentation-rules).
- `python tools/doccheck.py` checks every link and anchor, and the constants in `reference.md`
  against the code. CI runs it on every push.
