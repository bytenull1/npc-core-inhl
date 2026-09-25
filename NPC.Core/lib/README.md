# Reference assemblies

Put the DLLs the project compiles against here. They are **not** redistributed with the
library (`*.dll` in this folder is gitignored) - copy them out of your own game install.

The project references them by `HintPath` into this folder, marked `Private=false`, so
they are only used to compile and never copied into the build output. That matches what
BepInEx already provides at runtime.

From `BepInEx/core/`:

- `BepInEx.dll`
- `0Harmony.dll`

From `Isolated Inhale_Data/Managed/`:

- `Assembly-CSharp.dll`
- `Newtonsoft.Json.dll`
- `FMODUnity.dll`
- `UnityEngine.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.AnimationModule.dll`
- `UnityEngine.PhysicsModule.dll`
- `UnityEngine.IMGUIModule.dll`
- `UnityEngine.InputLegacyModule.dll`
- `UnityEngine.TextRenderingModule.dll`
- `UnityEngine.UI.dll`
- `Unity.TextMeshPro.dll`
