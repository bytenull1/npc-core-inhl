# Logging - levels, sources, rules

Logs are the main evidence for navigation bugs. Every NPC mod logs the same way, so one capture reads
the same whoever wrote the NPC.

---

## 1. Levels

`debug_level <0-3>` in the game console, or `[Debug] DebugLevel` in `com.bytenull1.npccore.cfg`. One
level for every NPC mod (`NpcLog.Level`).

| Level | Adds |
|---|---|
| 0 | warnings and errors only |
| 1 | state changes, doors, deaths, blocked colliders |
| 2 | **reasoning**: every replan and why, seed lists, route chains, gate inventory and audits |
| 3 | per-frame obstacle dumps - very noisy, only for steering problems |

**Use level 2 for navigation bugs.** Level 1 shows an NPC failing, not what it decided.

---

## 2. Sources and tags

BepInEx prefixes every line with its source. NPC.Core's own lines come from `NPC.Core`; everything an
NPC does comes from that NPC's source, `<ModName>:<Name>` - `[Info   :YourBuddy:Buddy 2] [nav] ...` -
static code such as `FindPath` included, because it runs inside the caller's acting scope
([npc-code-runs-acting](invariants.md#npc-code-runs-acting)).

| Tag | Source | Typically logs |
|---|---|---|
| `[ai]` | `NpcAgent`, `NpcHands`, the `Docking` / `ShipRebuild` / `ShipContent` patches | replans, doors, rooms loaded, stuck recovery, step-offs, blockers, footsteps, riding and parking, air, the catch and death, carried items; the level-3 obstacle report |
| `[nav]` | `NavGraph`, `StationRooms` | seeding, route chains, path failures, graph files |
| `[probe]` | `NavProbe`, `SceneScan` | probe mask, gate inventory, gate-frame audits, full buffers, scene rescans |
| `[editor]` | `NodeEditor`, `NpcCorePlugin` | node and link placement; the editor object made again after it was destroyed |
| `[internals]` | `GameInternals` | missing game members at load, then one summary line |
| `[mod]` | `NpcCorePlugin`, `NpcConsole`, `NpcEvents`, `NpcCorpse` | startup, patch groups applied or failed, a second copy of NPC.Core, commands refused, a mod's event handler that threw, picking up a body |
| `[world]` | `NpcRooms`, `NpcRegistry` | a room kept loaded against a switch-off, once per room; body collision pairs a switched-off collider lost, once per NPC (every restore at level 3) |
| `[save]` | `NpcSaves`, `CoreSidecar`, the `Load` patch | save loads, sidecars written, deleted or unreadable, door codes restored |
| `[lifecare]` | `NpcLifecare` | each scan's snapshot and the rendered icon state ([lifecare.md](lifecare.md#reading-a-capture)) |
| `[talk]` | `NpcInteraction`, `NpcTalkWindow`, `TalkSkin` | the Interact subscription, the window opening, at level 2 why an Interact press opened nothing; the game font found or missing, a missing sprite |
| `[debug]` | `NpcMonster` | AI overrides cleared by a save load |

A mod adds its own tags for its own code.

---

## 3. Capturing a log

1. `debug_level 2` in the console.
2. Reproduce the problem - and nothing else.
3. Take `BepInEx/LogOutput.log` and trim it:

   ```bash
   python tools/trimlog.py BepInEx/LogOutput.log --stats
   python tools/trimlog.py BepInEx/LogOutput.log --fold > capture.txt
   ```

4. **Add a one-line note where the behaviour changed** ("here it goes back down"). `trimlog` keeps
   free-text lines, and these notes are often the fastest route to the answer.

`trimlog` keeps NPC.Core's lines, every NPC's, and the plugin lines of the mods those NPCs belong to.
With more than one NPC it names the NPC on each line (`Mod:Name` across mods); `--npc "Buddy 2"`
keeps one.

Run `--stats` first. It shows what the log is mostly made of, and a dominant line shape is often the
finding by itself (e.g. dozens of `FindPath: no clear entry seed`).

| Mode | A 35 KB capture becomes |
|---|---|
| `--stats` | ~3.6 KB - a histogram of line shapes |
| `--fold` | ~11 KB - readable, first and last few of each shape |
| plain | ~30 KB - prefixes stripped, exact duplicates collapsed |

`--fold` keeps the **first and last** of each repeated shape because the drift between them matters:
a position that never changes across replans is a livelock; one that swings is a ping-pong.

Other flags: `--tag nav,ai` to filter by tag, `--source` to keep another plugin's lines, `--all` to
keep every source, `-` for stdin.

---

## 4. Rules for adding logs

- **Any state machine that can stall must say why**, throttled, naming the blocker and the decision.
  A silent wait looks exactly like a forgotten one.
- Log the **inputs** to a decision, not only the outcome. `via #98 -> goal` says what was chosen;
  `2 visible entry seeds: #102(1,4m, +1,0y) #98(4,3m, +0,4y)` says why.
- Print values in the units the rule uses - e.g. deck difference, not raw Y.
- Throttle anything reachable every frame (2-5 s), and dedupe by subject, never with one global
  throttle.
- One line per decision. Multi-line dumps break line-shape tools.
- **NPC code runs inside `NpcRegistry.Acting(npc)`.** A coroutine logs through the NPC's own
  `LogSource`: a scope must never outlive a `yield`. A line logged outside any scope carries NPC.Core's
  source - nameless, never misattributed.

---

## 5. Reading guides

- Routes and replans: [navigation.md §7](navigation.md#7-reading-a-findpath-capture)
- Gate-frame audits: [probes.md](probes.md#the-gate-frame-rule)
