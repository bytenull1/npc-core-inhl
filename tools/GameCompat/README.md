# Game compatibility check

Checks built mod assemblies against a game install by reading both as metadata. Nothing runs, so it
works on a new game build before the mod is installed. It needs the game, so CI cannot run it.

```
dotnet run --project tools/GameCompat/GameCompat.csproj -c Release -- [--game <folder>] [mod.dll ...]
```

`--game` defaults to the first folder above the current one that holds `Isolated Inhale_Data`. With no
DLLs it checks everything in `BepInEx/plugins`. DLLs named on the command line win over installed copies.

| Check | A miss means |
|---|---|
| Every type, method and field the mod references in a game or plugin assembly | `MissingMethodException` or `TypeLoadException` the first time that code runs |
| Every `GameInternals` `Field<T>` and `Method` lookup | The feature it names degrades; the game log warns at load |
| Every `[HarmonyPatch(typeof(X), "Name")]`: one declared method, and patch parameters that exist on it | That patch group fails to apply |

The lookup count must equal the game log's `[internals] All N game members resolved.` A lower count
means a lookup is written in a form the IL scan does not recognise.
