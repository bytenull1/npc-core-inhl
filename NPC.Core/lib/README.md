# Reference assemblies

Put the DLLs the project compiles against here. They are **not** redistributed with the
library (`*.dll` in this folder is gitignored) - copy them out of your own game install.

The project references are compile-only and are never copied to the output. Use the assemblies
from the Isolated Inhale 0.9.0 Mono build. To build directly against the installed game, set
the `GameManagedDir` environment variable to the absolute path of `Isolated Inhale_Data/Managed`
before running `dotnet build`. The BepInEx references still come from `lib/`.

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
- `Unity.InputSystem.dll`
- `Unity.InputSystem.dll`
- `UnityEngine.InputLegacyModule.dll`
- `UnityEngine.TextRenderingModule.dll`
- `UnityEngine.UI.dll`
- `Unity.TextMeshPro.dll`
