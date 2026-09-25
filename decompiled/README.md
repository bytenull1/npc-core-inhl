# Decompiled game source - generate this yourself

**This folder is intentionally empty in the repository.** It holds a decompile of
*Isolated Inhale*'s own assembly, which is the game developer's copyrighted code and is
not ours to redistribute. Everything here except this file is gitignored.

NPC.Core does not need it to build. It exists so that you - or an AI agent working on
NPC.Core - can read how the game actually behaves, which the whole `docs/` knowledge
base refers to constantly (`Gate.cs`, `EntryDetector.cs`, `LifecareController.cs`,
`Docker.cs`, `Airlock.cs`, `PlayerDetector.cs` are the ones cited most).

## Generating it

Point ILSpy at the game's `Assembly-CSharp.dll`:

```bash
dotnet tool install -g ilspycmd
ilspycmd -p -o decompiled "<game folder>/Isolated Inhale_Data/Managed/Assembly-CSharp.dll"
```

`-p` decompiles as a project, which keeps the namespace folders (`Space/Player.cs` and
so on). Any other decompiler works too - ILSpy's GUI, dnSpyEx, dotPeek - as long as the output
lands here as `.cs` files. The `docs/` line references were taken from an ILSpy dump of
**v0.8.9**; a different game version will shift them.

Keep the version you decompiled written down. Line numbers in `docs/` are only
meaningful against the same build.

The game ships as Mono, not IL2CPP, so the output is close to the original source.
`Assembly-CSharp.pdb` sits next to the DLL; keep it there and ILSpy picks up the real
local variable names.

## Looking at one type without redumping

The full dump is what `docs/` references, but a single question is often faster to
answer with a targeted decompile. Flags vary a little between ilspycmd versions -
check `ilspycmd -h`.

```bash
# one type, to stdout (fully qualified name: Airlock, but Space.Player)
ilspycmd -t Airlock "<game folder>/Isolated Inhale_Data/Managed/Assembly-CSharp.dll"

# list the types in an assembly (c = classes, i = interfaces, s = structs, d = delegates, e = enums)
ilspycmd -l c "<game folder>/Isolated Inhale_Data/Managed/Assembly-CSharp.dll"

# IL instead of C#, when the decompiled C# looks wrong (compiler-generated
# iterators and coroutines are the usual suspects). -il ignores -t and prints
# the whole assembly, so write it to a file outside this folder and search it.
ilspycmd -il "<game folder>/Isolated Inhale_Data/Managed/Assembly-CSharp.dll" > assembly.il
```

The same works on the other assemblies in `Managed/` when behaviour lives outside the
game's own code: `UnityEngine.*Module.dll` (engine - physics, animation),
`UnityEngine.UI.dll`, `FMODUnity.dll` (audio), `Newtonsoft.Json.dll` (the save format).
Don't dump those into this folder; `docs/` line references assume it holds
`Assembly-CSharp` only.

## What the decompile can't tell you: the scene

The decompile is code only. **Where** something sits - which GameObject carries a
script, what a room is parented under, what a serialized field points at - lives in
the scene file, and it can be read offline:

```bash
python tools/unityscene.py find Airlock                   # objects whose name contains it
python tools/unityscene.py script SpaceObject             # objects carrying that MonoBehaviour
python tools/unityscene.py tree --root StaticObjects --depth 1 --scripts
python tools/unityscene.py refs World/Objects/DestroyedSpaceShip
```

[`tools/unityscene.py`](../tools/unityscene.py) has no dependencies and finds the game
folder by walking up from the repository (or pass `--data "<game>/Isolated Inhale_Data"`).
Inactive objects print as `name[off]`; paths are accepted as printed.

What it relies on, if you need to extend it:

- `Isolated Inhale_Data/level1` is the game scene: Unity **2022.1.20f1**, SerializedFile
  **format 22**, **no type trees**, little-endian object data. Without type trees a
  script's fields can't be decoded by name, so the layouts used are hard-coded:
  GameObject = classID 1, Transform = 4 (RectTransform 224), MonoBehaviour = 114,
  BoxCollider = 65 (52 bytes; size + center are the last 24).
- A MonoBehaviour's script is a PPtr into an external file. Script class names
  (MonoScript, classID 115) live in `globalgamemanagers.assets`, external fileID 1.
- A PPtr is `int32 fileID + int64 pathID`. Field names aren't stored, so `refs` finds
  serialized references by scanning the MonoBehaviour body at 4-byte offsets for
  `(0, pathID)` pairs that hit a known GameObject or Transform. It prints body offsets;
  match them against the field order of the class in the decompile. A false hit is
  possible but rare.

Reach for it whenever a question is about the scene rather than the code - for example, it
shows that every station interior lives under the root `StaticObjects/<Station>Parts`, not
under the station object ([game-model.md](https://github.com/bytenull1/yourbuddy-inhl/blob/main/docs/game-model.md#a-stations-interior-is-not-under-the-station)).

## Serialized field values: the AssetRipper export

`unityscene.py` can say *that* a script references an object, but not *which field*
holds the reference, or what plain values (numbers, strings, colors, lists) the scene
sets. For that, export the game with [AssetRipper](https://github.com/AssetRipper/AssetRipper)
into `assetripper/` at the repository root (gitignored; ~300 MB, same copyright
reasoning as this folder). Open `<game folder>/Isolated Inhale_Data` and export everything
as a Unity project.

AssetRipper reads the game's assemblies to recover the field layouts the scene file
leaves out, so the export is plain Unity YAML with every field named:

```yaml
# assetripper/ExportedProject/Assets/Scenes/GameScene.unity - the OxygenStation SpaceObject
  m_GameObject: {fileID: 427}
  m_Script: {fileID: 11500000, guid: 9fac49c410e1d08befe20f3cb1f4715b, type: 3}
  richPresenceName: Oxygen station
  contentParent: {fileID: 725}
  rooms:
  - {fileID: 89168}
```

- Scenes: `ExportedProject/Assets/Scenes/GameScene.unity` (the `level1` scene, ~69 MB)
  and `MainMenu.unity`. Also exported: prefabs (`Assets/GameObject`), ScriptableObject
  data (`Assets/MonoBehaviour`), materials, `ProjectSettings/` (tags, layers, physics).
- `{fileID: N}` is another object in the same file: search for `--- !u!<classID> &N`.
  A `guid` points at an asset; `grep -rl <guid> --include=*.meta` names it. Script
  GUIDs resolve to `Assets/Scripts/Assembly-CSharp/<Class>.cs.meta`.
- Its `Scripts/*.cs` are a second decompile of the same assembly, matching this folder
  apart from lambda syntax (`() =>` vs `delegate`). They are redundant: delete the
  `.cs` files after exporting, but keep the `.cs.meta` files, which carry the script
  GUIDs the scene references.

Which to use:

| Question | Tool |
| --- | --- |
| Where does an object sit, what is it parented under, which objects carry a script | `unityscene.py` (`tree`, `find`, `script`) |
| Which field references what, what value a field is set to | AssetRipper YAML (grep the field name) |
| Prefabs, ScriptableObject assets, the main menu, project settings | AssetRipper |
| Anything a probe or script needs to load | `unityscene.py` (no export step, reads the live game files) |

The export is a snapshot: re-export after a game update, the same as the decompile.
