using System.Collections.Generic;
using UnityEngine;

namespace NPC.Core.Interaction
{
    /// <summary>
    /// The talk window, drawn to match the game's own DialogMenu, which is hard-typed to AssistanceBot and
    /// dereferences its animator. One for every NPC mod, on NPC.Core's own GameObject. docs/interaction.md#2-the-panel
    /// </summary>
    internal sealed class NpcTalkWindow : MonoBehaviour
    {
        private static bool _showCommands;
        private static string _input = "";
        // The title, cached: OnGUI runs on every event.
        private static string _title = "";
        private static Vector2 _scroll;
        /// <summary>
        /// Reused to measure log lines; OnGUI runs several times a frame.
        /// </summary>
        private static readonly GUIContent LineContent = new();

        private InputHandler? subscribedTo;
        private static NpcTalkWindow? _instance;
        private static bool _created;
        private static int _lastUpdateFrame;
        /// <summary>
        /// Frames without an Update after which the object counts as dead: a tick is never that far apart.
        /// </summary>
        private const int StalledFrames = 30;

        /// <summary>
        /// At plugin load and from the shared tick: the window's object is made again if it is gone,
        /// switched off or not updating, and says which. docs/invariants.md#plugin-objects-outlive-the-first-scene
        /// </summary>
        internal static void Ensure()
        {
            string? why = _instance == null ? "destroyed"
                : !_instance.isActiveAndEnabled ? "switched off"
                : Time.frameCount - _lastUpdateFrame > StalledFrames ? "not updating"
                : null;
            if (why == null) return;

            if (_created)
            {
                NpcLog.Core.LogWarning("[talk] Talk window object was " + why + " - creating it again");
                if (_instance != null) Destroy(_instance.gameObject);
            }
            _created = true;
            _lastUpdateFrame = Time.frameCount;
            _instance = NpcCorePlugin.PersistentObject("NPC.Core TalkWindow").AddComponent<NpcTalkWindow>();
        }

        internal static void Opened(string title)
        {
            _title = title.ToUpperInvariant();
            _showCommands = false;
            _scroll = Vector2.zero;
        }

        internal static void ScrollToEnd() => _scroll.y = float.MaxValue;

        private void Update()
        {
            _lastUpdateFrame = Time.frameCount;
            // InputHandler drops all listeners on teardown and a scene load brings a new one, so the
            // subscription is re-checked rather than made once.
            InputHandler? handler = GameManager.Instance != null ? GameManager.Instance.InputHandler : null;
            if (handler != subscribedTo)
            {
                if (subscribedTo != null) subscribedTo.OnInteract.RemoveListener(NpcInteraction.OnInteractPressed);

                subscribedTo = handler;
                if (subscribedTo != null)
                {
                    subscribedTo.OnInteract.AddListener(NpcInteraction.OnInteractPressed);
                    NpcLog.Core.LogInfo($"[talk] Listening for Interact on InputHandler #{subscribedTo.GetInstanceID()}");
                }
            }

            NpcInteraction.Update();
        }

        private void OnDestroy()
        {
            if (subscribedTo != null) subscribedTo.OnInteract.RemoveListener(NpcInteraction.OnInteractPressed);
        }

        // ------------------------------------------------------------------
        // Panel
        // ------------------------------------------------------------------

        private void OnGUI()
        {
            NpcInteraction.Talk? talk = NpcInteraction.Open;
            if (talk == null) return;

            TalkSkin.Ensure();

            // Space.Event also exists; this is the IMGUI one.
            Event e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape) { NpcInteraction.Close(); return; }
                if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
                {
                    Submit(talk);
                    e.Use();
                }
            }

            // Whole pixels throughout: the pixel font drawn at a fractional origin lands between texels
            // and blurs.
            float w = Mathf.Round(Mathf.Clamp(Screen.width * 0.26f, 360f, 540f));
            float h = Mathf.Round(Mathf.Clamp(Screen.height * 0.48f, 280f, 560f));
            // Sits in the right-hand third rather than hard against the edge: the small panel looked
            // thrown into the corner at a 28px margin.
            float x = Mathf.Round(Screen.width * 0.72f - w * 0.5f);
            float y = Mathf.Round((Screen.height - h) * 0.5f);

            TalkSkin.Panel(new Rect(x, y, w, h));

            const float pad = 10f;
            // The game's buttons are sized in UI pixels, so the rows that hold them are too.
            float titleH = 26f * TalkSkin.UiScale;
            float rowH = 24f * TalkSkin.UiScale;

            Rect title = new(x + pad, y + pad, w - pad * 2f, titleH);
            TalkSkin.Panel(title);
            GUI.Label(title, _showCommands ? "COMMANDS" : _title, TalkSkin.Title);

            float closeSide = titleH - 8f;
            Rect close = new(title.xMax - closeSide - 4f, title.y + 4f, closeSide, closeSide);
            if (TalkSkin.CloseButton(close)) { NpcInteraction.Close(); return; }

            float bodyTop = title.yMax + pad;
            float bodyBottom = y + h - pad - (_showCommands ? 0f : rowH + pad);
            Rect body = new(x + pad, bodyTop, w - pad * 2f, bodyBottom - bodyTop);
            TalkSkin.Panel(body);

            if (_showCommands) DrawCommands(body, talk);
            else DrawLog(body, talk.Lines);

            if (_showCommands) return;

            float rowY = body.yMax + pad;
            Rect toggle = new(x + pad, rowY, rowH, rowH);
            if (TalkSkin.CommandsButton(toggle)) _showCommands = true;

            Rect send = new(x + w - pad - rowH, rowY, rowH, rowH);
            if (TalkSkin.SendButton(send)) Submit(talk);

            Rect field = new(toggle.xMax + 8f, rowY, send.x - toggle.xMax - 16f, rowH);
            TalkSkin.FieldFrame(field);
            // The text sits on the frame's face, above its lip.
            field = new Rect(field.x + TalkSkin.UiScale * 2f, field.y + TalkSkin.UiScale * 2f,
                field.width - TalkSkin.UiScale * 4f, field.height - TalkSkin.UiScale * 6f);
            GUI.SetNextControlName("npcTalkInput");
            _input = GUI.TextField(field, _input, 64, TalkSkin.Field);
            if (_input.Length == 0) GUI.Label(field, "Enter message...", TalkSkin.Placeholder);
            // Keep the caret in the field: the panel exists to be typed into.
            if (GUI.GetNameOfFocusedControl() != "npcTalkInput") GUI.FocusControl("npcTalkInput");
        }

        private static void Submit(NpcInteraction.Talk talk)
        {
            string text = _input;
            _input = "";
            NpcInteraction.Submit(talk, text);
        }

        private static void DrawLog(Rect body, List<string> lines)
        {
            Rect inner = new(body.x + 10f, body.y + 8f, body.width - 20f, body.height - 16f);
            float width = inner.width - 18f;
            float total = 4f;
            foreach (string line in lines)
            {
                LineContent.text = line;
                total += TalkSkin.Body.CalcHeight(LineContent, width) + 4f;
            }

            _scroll = GUI.BeginScrollView(inner, _scroll, new Rect(0f, 0f, width, total));
            float cursor = 0f;
            foreach (string line in lines)
            {
                LineContent.text = line;
                float lh = TalkSkin.Body.CalcHeight(LineContent, width);
                GUI.Label(new Rect(0f, cursor, width, lh), line,
                    line.StartsWith('$') ? TalkSkin.Echo : TalkSkin.Body);
                cursor += lh + 4f;
            }
            GUI.EndScrollView();
        }

        /// <summary>
        /// Two columns, and the row height fitted to the body: the word list outgrew one column, and the
        /// panel is only 240-400px tall. docs/interaction.md#2-the-panel
        /// </summary>
        private static void DrawCommands(Rect body, NpcInteraction.Talk talk)
        {
            IReadOnlyList<string> names = talk.Conversation.Commands;
            const float gap = 8f;
            int rows = (names.Count + 1) / 2;
            float rowH = Mathf.Floor(Mathf.Clamp((body.height - 14f - 52f) / Mathf.Max(rows, 1), 24f, 34f));
            float colW = Mathf.Floor((body.width - 36f - gap) * 0.5f);
            for (int i = 0; i < names.Count; i++)
            {
                int col = i / rows;
                int rowIdx = i % rows;
                Rect row = new(body.x + 18f + col * (colW + gap), body.y + 14f + rowIdx * rowH, colW, rowH);
                if (!GUI.Button(row, names[i], TalkSkin.Command)) continue;

                _showCommands = false;
                NpcInteraction.Submit(talk, names[i]);
            }

            Rect back = new(body.x + 18f, body.yMax - 52f, 130f, 40f);
            if (TalkSkin.Button(back, "Back", false)) _showCommands = false;
        }
    }
}
