# Talking to NPCs

`Interaction/NpcInteraction.cs` (which NPC answers, the input handover), `NpcTalkWindow.cs` (the panel)
and `TalkSkin.cs` (drawing). A mod makes an NPC talkable with `NpcInteraction.Register(conversation)`,
an `INpcConversation` that answers what the player types ([api.md §6](api.md#6-talking---npccoreinteraction)).
There is no hotkey; the game's Interact binding is the only way in.

---

## 1. Opening it

Look at an NPC and press **Interact**. NPC.Core listens to `GameManager.Instance.InputHandler.OnInteract`
once for every mod ([one-interact-owner](invariants.md#one-interact-owner)) and checks each registered
conversation:

| Gate | Why |
|---|---|
| the NPC lives, is loaded, and `CanTalk` | the mod's own conditions: asleep, hidden, its setting |
| within `TalkRange` (2.4 m) | shorter than the player's reach, so using a keypad is not talking to an NPC |
| inside a `LookAngle` cone (20°) | measured to the nearest point of the NPC's controller, not its chest |
| the player is not busy | not aiming at or holding an `Interactable` - **the game's interaction wins**. Read from `PlayerController.focusedInteractable`; if a game update breaks that field, a ray along the view within reach decides instead, so a lost field never hands terminals' keypresses to the window |
| sight is not blocked | one ray on `ProbeLayers` to its chest, `ChestAboveFeet` above the feet its controller gives, whatever its origin; without it the window opens through walls |

Of the NPCs that pass every gate, the one at the smallest view angle answers (ties: the one registered
first). One window is open at a time. Its `Title` is the NPC's; the first time it opens on an NPC it
says the `Greeting`. `SetOpen(true)` tells the mod, which should hold the NPC still facing the player.

NPC.Core frees the cursor and calls `InputHandler.SwitchToUIInput()`, like `AssistanceBot.Talk`, and
reverses both on close (**Escape**, the close box, or the NPC dying or unregistering).

Deliberately **not** used:

- **The game's `DialogMenu`** - it is typed to `AssistanceBot` and reads the bot's data and animator.
- **A game `Interactable` on the NPC** - it needs a serialized `outlines` object a mod cannot fill,
  and `ThrowInteractionRaycast` never picks a `CharacterController`. So selection is an angle test;
  the ray only checks for walls.

`InputHandler` clears its listeners on teardown and a scene load brings a new one, so the
subscription is re-checked every frame. The shared tick checks that the window's own object still
exists, is active and updates, and makes it again if not (`[talk] Talk window object was … - creating
it again`): the listener lives on it, so without it Interact does nothing and nothing says why.

At debug level 2, a press that opens nothing logs `[talk] Interact: no NPC answers`, with the failed
gate for each NPC within 6 m. No such line after a press means the listener never ran.

---

## 2. The panel

Styled after the game's terminal dialogs: a framed near-black panel on the right, a title bar with a
close box, a message log, and a bottom row of *commands toggle · text field · send*. `TalkSkin` builds
it from 1×1 fills and the game's own font and sprites, so NPC.Core stays a single DLL.

Each conversation keeps its own log of the last 80 lines, which lives as long as its NPC is
registered. The commands page draws the mod's `Commands` in two columns fitted to the panel; each is
sent as if typed.

**Font:** the game's `Pixellari` (the AssistantBot dialog font), found among loaded fonts by name and
retried every 5 s; Consolas until then. Logged once when found. Title `TitleSize` 32, text `TextSize`
24 - change `TextSize` to rescale. No synthesized bold/italic: it smears a pixel font.

**Sprites** (loaded from `Resources`, point-filtered):

| Sprite | Used for |
|---|---|
| `textures/ui/interfaces/ColorButton` | buttons and the text field frame, nine-sliced |
| `textures/ui/interfaces/ColorButtonPressed` | a held button |
| `textures/ui/icons/Arrow2` | send (tinted `AcceptTint`, green) |
| `textures/ui/icons/Cross` | close |
| `textures/ui/icons/Chat` | commands toggle |

Sizes are in UI pixels: `TalkSkin.UiScale` = screen height / 540, rounded. A sprite that fails to
load falls back to a flat frame and a text label. Every rect is rounded to whole pixels to avoid blur.

IMGUI notes: build styles inside `OnGUI` (they read `GUI.skin`), and give generated textures and fonts
`HideFlags.HideAndDontSave` or they are lost on scene load. This panel has controls, so it runs on
every event and must **not** be gated on `Repaint`
([read-only-panels-build-on-repaint](invariants.md#read-only-panels-build-on-repaint)).
