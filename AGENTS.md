# AGENTS.md - rules for AI agents in this repository

**NPC.Core** is a BepInEx 5 library plugin for *Isolated Inhale* v0.8.9 (Unity 2022.1.20f1) that NPC
mods build on: navigation, probes, and the state several NPC mods must share. The game has **no navmesh
and no AI navigation**; everything is custom. Its reference user is
[YourBuddy](https://github.com/bytenull1/yourbuddy-inhl).

Human contributors: see [CONTRIBUTING.md](CONTRIBUTING.md). The rules below apply to people too.

---

## 1. Where knowledge lives

**Docs for where and why, code for what.**

- To find code: `docs/architecture.md`, or the topic doc from `docs/README.md`, then grep and read
  only the methods you need. Don't read whole files or whole docs to orient yourself.
- Before changing behaviour: read the topic doc and the `invariants.md` entries it links. Code does
  not show why; a deliberate choice looks like a bug.
- Code is the truth for what happens now, docs for what should. When they disagree, say so: code
  that breaks an invariant is a bug; a description that drifted gets fixed in the same change (§4).

- **[docs/README.md](docs/README.md)** - map of every doc. Load only the ones your task needs.
- **[docs/architecture.md](docs/architecture.md)** - which two or three files matter. Start there
  instead of reading the codebase.
- **[docs/api.md](docs/api.md)** - the public surface other mods compile against.
- **[docs/invariants.md](docs/invariants.md)** - rules that must not be broken, each with an anchor.
- **`decompiled/`, `assetripper/`** - local, read-only copies of the game's code and assets (not in
  the repo). [docs/game-sources.md](docs/game-sources.md) says which answers what, how to generate
  them, and how to read the scene (`tools/unityscene.py`).
- **`LAST_SESSION.md`** - local, uncommitted hand-off: what the last session changed and what is
  still unverified.
- **[YourBuddy](https://github.com/bytenull1/yourbuddy-inhl)** - the reference mod, with its own
  `AGENTS.md`. When a change spans both, push NPC.Core first: YourBuddy links to it.

**One fact, one home.** Each rule is stated once, in `docs/invariants.md`. Code and other docs link
to it. If you are about to explain something that already exists, link it instead.

---

## 2. Debugging

Work as a research engineer, not a generator of plausible fixes.

1. **Evidence first.** Read code, saved data (`nodegraph.json`, the scene) and logs before proposing
   a mechanism. Quote real values: coordinates, node ids, log lines, `file:line`.
2. **Cross-check** a load-bearing measurement two independent ways. Re-verify claims you did not
   derive yourself, including your own earlier ones.
3. **Keep the layers apart:** symptom, mechanism, root cause, separate contributing defects, and what
   you still cannot prove. When one cause explains most evidence, look for what it does *not* explain.
4. **Prefer one invariant to many exceptions.** State the broken rule in one sentence. If you add a
   special case, say why the general rule can't cover it.
5. **Narrow changes.** For each change name the seam (file, method), why it is the narrowest safe
   place, what you deliberately left alone, and how it interacts with other changes in the batch.
   Two correct fixes can form a loop.
6. **Verification is separate from implementation.** Never call a fix confirmed without a test or a
   capture. Give the exact log line that should appear next run, and the one that would prove the
   fix wrong. If you cannot run the game, say so.
7. **Observability.** Any state machine that can stall must log why, throttled
   ([docs/logging.md §4](docs/logging.md#4-rules-for-adding-logs)).
8. **No conclusion without a trace:** code path, measurement, cause-and-effect chain, and a test that
   could disprove it. Missing any of these, call it a hypothesis.

For non-trivial investigations, answer with: **Verdict**, **Evidence**, **Root causes**, **Fixes**,
**Validation** (expected and falsifying signal), **Docs to update**. Short questions and small edits
don't need this.

**Replies are short.** Lead with the answer, then the evidence that carries it. Offer more detail
instead of writing it.

---

## 3. Code conventions

- **Comments are pointers, not essays.** At most two or three lines. Say what the code does and, if
  the reason isn't obvious, link the rule: `// docs/invariants.md#floor-to-floor`. Trim longer
  comments when you touch a file.
- **No capitals for emphasis** in comments or docs. Write "not", never "NOT".
- **Braces.** `if (x) return;` on one line is fine for one simple statement within 120 columns, and
  not in an `if`/`else` chain with a braced branch (IDE0011). Nested control statements get braces.
- **All reflection into game types goes through `GameInternals.cs`.** A missing member logs once,
  naming the feature it degrades. Unity 2022 / netstandard2.1 has no `[UnsafeAccessor]`; use
  `FieldInfo`.
- **Physics buffers** are pre-allocated and shared; use `NonAlloc` overloads.
- **Nullable reference types** are on and warnings are errors. Each `!` carries a one-line comment
  naming the guard the compiler can't see; never silence a warning with `?.` or `??`.
- **Public means promised.** Other mods compile against every `public` member. Keep new members
  `internal` unless a mod needs them; never remove or change a public signature without bumping the
  minor version, and update [docs/api.md](docs/api.md) in the same change.
- **Know no mod.** NPC.Core never references a mod's types and never assumes an NPC is a copy of the
  player: it asks `INpc` and `NpcRegistry`. State every NPC must agree on lives here, never in a mod
  ([docs/invariants.md](docs/invariants.md#several-npc-mods)).
- **Mod code never throws into the game.** A failure is logged and the game's own code runs on
  ([docs/invariants.md](docs/invariants.md#mod-code-never-throws-into-the-game)).
- **One-off tools stay out of the repo.** A script used once to make an asset or fix data is not
  committed; commit only tools someone will run again.
- **Editing from a shell:** files are UTF-8 without BOM, LF in the repo; never
  `Set-Content`, which re-encodes the file. Long Python goes into a script file, not a bash heredoc, which often fails to parse.

**Before you call a change safe**, run all of these; CI runs the first three:

1. `dotnet build NPC.Core/NPC.Core.csproj -c Release`, then `-c Debug`. Warnings are errors.
2. `dotnet test tools/ReachChecks/ReachChecks.csproj -c Release`. A change to reach planning needs a
   check for it. Stryker takes minutes: run it only when the work is about the checks themselves
   (adding checks, judging their strength), not after every change.
3. `python tools/doccheck.py` (doc links, anchors, `reference.md` constants) and
   `python tools/bundle_nodegraph.py --check` (the bundled graph).
4. A changed public member: rebuild YourBuddy against it.

After a game update, run [tools/GameCompat](tools/GameCompat/README.md) first. To test in game, copy
`NPC.Core/bin/Release/netstandard2.1/NPC.Core.dll` to `BepInEx/plugins/` together with a mod that uses it
(YourBuddy, built from a checkout beside this one); the copy fails while the game is running.

---

## 4. Documentation rules

Docs are read by players and contributors, not only by agents. Keep them short and current.

**Docs describe the code as it is now.** Not how it got there.

Never write into committed docs or code comments:

- **Verification status** - "awaiting validation", "unconfirmed", "not yet run in the game",
  "confirmed in play". Report status in your reply and in `LAST_SESSION.md`. Committed code is
  assumed to work; if it doesn't, that is an entry in `docs/known-issues.md`.
- **Dates and session markers** - "[decided 2026-…]", "[fixed …]", "round 3", "this session",
  "first playtest", "since 09-16".
- **Play diaries** - "reported from play", "the author said", quotes from chat, screenshots.
- **Capture references** - `debug_*.txt` names or log line numbers. Captures are not in the repo.
- **Test plans** - "Validation" tables of what to try next. Put them in your reply.

Write like this:

- Short sentences. One idea each. Tables and lists over paragraphs.
- Explain *why* and *what breaks*, not what the code plainly says.
- Keep history only when it stops someone repeating a mistake, and then as one "Why" sentence in
  `docs/invariants.md` or one row in `docs/known-issues.md#dead-ends` - never a story.
- When you add text, look for stale text to remove in the same file.

When you change behaviour:

1. Update the topic doc that owns the subject.
2. If a rule changed, edit its entry in `docs/invariants.md`; don't add a second one.
3. An open bug you fixed: delete its entry from `docs/known-issues.md`.
4. Keep anchors stable; code links to them. If you must rename a heading, update every link
   (`grep -r "old-anchor"`).
5. Update `LAST_SESSION.md` (local): what changed, what is unverified, what to look for next.

**`LAST_SESSION.md`** opens with a self-contained prompt for the next session: what to read, what
is done (commit ids), what is next, the rules that still apply. Newest section first. Shrink a
section to one line once it is committed and tested in game, and drop it when nothing in it is open.
