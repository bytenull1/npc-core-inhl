# AGENTS.md - rules for AI agents in this repository

**NPC.Core** is a BepInEx 5 library plugin for *Isolated Inhale* v0.8.9 (Unity 2022.1.20f1) that NPC
mods build on: navigation, probes, and the state several NPC mods must share. The game has **no navmesh
and no AI navigation**; everything is custom. Its reference user is
[YourBuddy](https://github.com/bytenull1/yourbuddy-inhl).

Human contributors: see [CONTRIBUTING.md](CONTRIBUTING.md). The rules below apply to people too.

---

## 1. Where knowledge lives

- **[docs/README.md](docs/README.md)** - map of every doc. Load only the ones your task needs.
- **[docs/architecture.md](docs/architecture.md)** - which two or three files matter. Start there
  instead of reading the codebase.
- **[docs/api.md](docs/api.md)** - the public surface other mods compile against.
- **[docs/invariants.md](docs/invariants.md)** - rules that must not be broken, each with an anchor.
- **`decompiled/`** - local, read-only decompile of the game (not in the repo). Generate it with
  [decompiled/README.md](decompiled/README.md), which also covers reading the scene
  (`tools/unityscene.py`) and the `assetripper/` export.
- **`LAST_SESSION.md`** - local, uncommitted hand-off: what the last session changed and what is
  still unverified.

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

**Build:** `dotnet build NPC.Core/NPC.Core.csproj -c Release` (and Debug) - both must build;
`TreatWarningsAsErrors` is on. Then `python tools/doccheck.py` and `python tools/bundle_nodegraph.py --check`,
which CI also runs: they fail on a broken doc link or anchor, a `reference.md` constant that no longer
matches the code, or a bad bundled graph. To test in game, copy
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
