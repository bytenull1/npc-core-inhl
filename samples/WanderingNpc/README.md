# WanderingNpc - a sample NPC mod

The smallest mod built on NPC.Core: one of the game's plush toys, blown up, that wanders the nav graph. Start here when you
write your own; [docs/api.md](../../docs/api.md) is the reference.

| File | Shows |
|---|---|
| `WanderingNpc.csproj` | referencing NPC.Core without shipping it |
| `WanderingNpcPlugin.cs` | the `BepInDependency`, console commands, a body built from a game model, `NpcAgent.Attach`, registering |
| `WandererBrain.cs` | an `INpcBrain` (wander, or wait) that is also its `INpcConversation` |

It is also the check that two NPC mods coexist: run it next to YourBuddy.

---

## Building and installing

1. Build NPC.Core first ([README](../../README.md#building-from-source)); the sample uses its `lib/`.
2. `dotnet build samples/WanderingNpc/WanderingNpc.csproj -c Release`
3. Copy `samples/WanderingNpc/bin/Release/netstandard2.1/WanderingNpc.dll` to `BepInEx/plugins/`, next to
   `NPC.Core.dll`. Close the game first.

The configuration file is `BepInEx/config/com.bytenull1.wanderingnpc.cfg`: `ShowOnLifecare` (off)
draws wanderers on the lifecare terminal.

## The body

- The controller copies the player's (1 m x 0.22 m), so it fits every doorway and stair the graph was built for.
- The model is a plush from the game's `Resources` (Rain, Lord, Hyena, Dieter, Wypher, picked at
  random at spawn), scaled to 1.1 m high and at most 0.8 m wide. A missing one falls back to an orange capsule.
- The ragdoll is a second copy on the `Grabbable` layer, as the player's is. On `Default` it would
  fall through the floor.

## Console commands

| Command | Does |
|---|---|
| `wanderer_spawn [count]` | up to 8 wanderers in front of you |
| `wanderer_kill` | every wanderer dies; its plush falls and can be carried |
| `wanderer_clear` | removes them all |

Look at one and press Interact to talk: `wait` and `walk`. Wanderers are not saved.

## What to see with YourBuddy installed

- At load, NPC.Core refuses the sample's `debug_level` (`[mod] Console command 'debug_level' already
  belongs to NPC.Core - WanderingNpc's is not registered`), and the command still works.
- Buddies and wanderers walk through each other, and through you.
- A wanderer's lines log under `WanderingNpc:Wanderer 1`, a buddy's under `YourBuddy:Buddy`.
- Interact opens the window on whichever NPC you look at, buddy or wanderer.
- `ai_disable npc` freezes both.
