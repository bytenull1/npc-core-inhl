# The lifecare scanner terminal

`World/NpcLifecare.cs`, and the `Lifecare` and `Doors` patch groups in `Patches.cs`. Every NPC that
implements `INpcLifeform` and answers `ShowsOnLifecare` shows on the ship's lifecare terminal like the
player does. An `NpcAgent` answers from `NpcAgentSettings.ShowOnLifecare`.

---

## 1. What the game does

`LifecareController.StartScan` shows a ~3 s progress bar, then `EndScan` takes a **snapshot**: it
sets two booleans straight into two existing `Image`s under `LifeformIcons`. No icons are created;
"showing" one is `Image.enabled = true`.

There is no registry, layer, tag or room query. A lifeform is exactly:

1. `playerIcon.enabled` - the player;
2. `breathlessIcon.enabled` - the monster, or a `Warn(target)` ping;
3. any active entry in `tempObjects` - scripted one-off blips.

The count is `(breathless || any temp ? 1 : 0) + (player ? 1 : 0)`, so vanilla never shows more than
2. Horror events use the same three channels.

Icon position is `-(x·scale, z·scale, 0)`. The player icon uses a ship-local point, the breathless
icon a raw world position (a game inconsistency).

---

## 2. What NPC.Core adds

- `TryHook` clones the breathless icon as an `NpcLifeIcon <name>` for each registered lifeform shown,
  and re-creates a destroyed one, keeping its last snapshot. An NPC that unregisters takes its clone
  with it.
- `UpdateIcons` polls at 10 Hz (`PollSeconds`) while any lifeform is loaded. When a scan ends it
  snapshots every NPC's ship-local position, like `EndScan`, then re-asserts each icon's state,
  position and the count on every poll.
- `RecountLifeforms` (postfix on `UpdateLifeformsCount`) shows vanilla's count plus each NPC icon
  shown ([mirror-the-vanilla-lifeform-clamp](invariants.md#mirror-the-vanilla-lifeform-clamp)), only
  on the display the clones live in.

"Is it aboard" is the NPC's own `INpcLifeform.IsAboardPlayerShip`, which must come from the floor,
never a room ([aboard-is-answered-by-the-floor](invariants.md#aboard-is-answered-by-the-floor)).
NPC.Core writes only its clones and the label; it never touches `playerIcon`, `breathlessIcon` or
`tempObjects`.

---

## 3. The missing player icon (game bug, worked around)

`LifecareController.EndScan` decides whether the player counts:

```csharp
CustomRoom[] rooms = base.Owner.Rooms;
for (int i = 0; i < rooms.Length; i++)
{
    _ = rooms[i];                                                  // iterator discarded
    if (base.Owner.Pilot.CurrentRoom == base.Owner.Rooms.First())  // always rooms[0]
        flag = true;
}
```

A typo for `rooms[i]`: **the player registers only while their tracked room is room 0**
(`Front_M00`). Two things move `CurrentRoom` off room 0:

1. **Docking** - the game skips the room transition
   ([game-model](game-model.md#docking-does-not-run-the-room-transition)).
2. **An NPC closing a door elsewhere** - `EntryDetector` re-files the player
   ([game-model](game-model.md#entrydetector-re-files-the-player-when-a-door-closes)).

Fixes:

- The `Doors` patch group hides the detector's remembered player for a close an NPC issued away from
  the player ([doors.md §5](doors.md#5-closes-away-from-the-player)).
- `NpcLifecare.FixPlayerIcon`, a prefix on `UpdatePlayerIcon`, restores the icon
  ([player-icon-fix-is-one-directional](invariants.md#player-icon-fix-is-one-directional)).

### Reading a capture

```
[lifecare] Lifecare scan finished: aboard=True, pos=(2.5, 0.6, -1.8),
      trackedRoom=Front_M00 -> icon shown
[lifecare] Lifecare icon state: enabled=True, activeInHierarchy=True, parent=LifeformIcons,
      localPos=(-3.7, 3.7, 0.0), playerIcon=False, label=1
```

Both lines carry the NPC's own source. The second is what is actually **rendered** - read it first.
`aboard=False` with the NPC plainly aboard is a floor-probe problem; `aboard=True` with no icon is a
clone problem. `playerIcon=False` while NPC.Core is correct points at the game bug above.

---

## 4. Known limitations

- The terminal shows the snapshot from the last scan, like the game's own icons. Re-scan to refresh.
- A terminal powered **off** (`Emission`, a blackout) is not detected; NPC.Core keeps writing into a
  hidden UI. Harmless.
