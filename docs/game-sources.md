# Reading the game

The game's code, scene and assets are not in the repository - they are the developer's
copyright. Generate them locally; all of it is gitignored.

| Source | Where | Generate with |
| --- | --- | --- |
| Code | `decompiled/` | [decompiled/README.md](../decompiled/README.md) (ilspycmd) |
| Scene hierarchy | the live game files | nothing - [`tools/unityscene.py`](../tools/unityscene.py) reads them |
| Serialized field values, prefabs, settings | `assetripper/unity/` | [assetripper/README.md](../assetripper/README.md) |
| Meshes (`.glb`) and textures (`.png`) | `assetripper/primary/` | [assetripper/README.md](../assetripper/README.md) |

After any export, `python tools/cleanexports.py --apply` moves folders into this layout and
deletes duplicates. Without `--apply` it only prints what it would do.

## Which to use

| Question | Tool |
| --- | --- |
| What does the code do | `decompiled/` |
| Where does an object sit, what is it parented under, which objects carry a script | `unityscene.py` (`tree`, `find`, `script`) |
| Which field references what, what value a field is set to | `assetripper/unity/` YAML (grep the field name) |
| Prefabs, ScriptableObject assets, the main menu, project settings | `assetripper/unity/` |
| What a model looks like, its UVs and textures | `assetripper/primary/` in Blockbench ([viewing the models](../assetripper/README.md#viewing-the-models)) |
| Anything a probe or script needs to load | `unityscene.py` (no export step, reads the live game files) |

## The scene: unityscene.py

```bash
python tools/unityscene.py find Airlock                   # objects whose name contains it
python tools/unityscene.py script SpaceObject             # objects carrying that MonoBehaviour
python tools/unityscene.py tree --root StaticObjects --depth 1 --scripts
python tools/unityscene.py refs World/Objects/DestroyedSpaceShip
```

It has no dependencies and finds the game folder by walking up from the repository (or pass
`--data "<game>/Isolated Inhale_Data"`). Inactive objects print as `name[off]`; paths are
accepted as printed. The Linux folder carries the version (`Isolated Inhale v0.8.9_Data`); on macOS
it is `<game>.app/Contents/Resources/Data`.

It shows, for example, that every station interior lives under the root
`StaticObjects/<Station>Parts`, not under the station object
([game-model.md](game-model.md#a-stations-interior-is-not-under-the-station)).

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

Why not UnityPy: it needs pip and native wheels, and it only decodes script fields by name
after generating type trees from the game's DLLs. For named field values, grep the
AssetRipper YAML instead.

## What is deliberately not kept

| Duplicate | Why it goes |
| --- | --- |
| AssetRipper's decompiled `.cs` (both exports) | Same assembly as `decompiled/`, from the decompiler version AssetRipper bundles. The Unity export keeps its `.cs.meta` for script GUIDs. |
| `AuxiliaryFiles/GameAssemblies/`, `primary/Assemblies/` | Copies of the game's `Managed/` DLLs. |
| `primary/Scripts/` | Decompiles of every assembly, `mscorlib` included. |
| `primary/Assets/Shader/` | Shader metadata dumps; the Unity export has the shaders. |
