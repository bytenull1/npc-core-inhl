using System.Collections.Generic;
using UnityEngine;
// ReSharper disable UseObjectOrCollectionInitializer

namespace NPC.Core.Interaction
{
    /// <summary>
    /// IMGUI styling for the talk window, matched to the game's own DialogMenu: near-black fills,
    /// the game's ColorButton frames and icons, its Pixellari face, tan command words and a green
    /// send button. See docs/interaction.md#2-the-panel
    /// </summary>
    internal static class TalkSkin
    {
        private static readonly Color Fill = new(0.07f, 0.07f, 0.07f, 0.96f);
        private static readonly Color Border = new(0.78f, 0.78f, 0.78f, 1f);
        private static readonly Color Accept = new(0.49f, 0.80f, 0.42f, 1f);
        private static readonly Color Tan = new(0.91f, 0.65f, 0.30f, 1f);
        private static readonly Color Text = new(0.90f, 0.90f, 0.90f, 1f);
        private static readonly Color Dim = new(0.55f, 0.55f, 0.55f, 1f);

        private const float BorderWidth = 2f;
        /// <summary>
        /// The source .ttf of "Pixellari SDF", which the AssistantBot's DialogMenu draws with. A
        /// 16px-grid face whose capitals are only 11px tall there: the title is drawn at twice the
        /// grid, and the rest at TextSize - one and a half, where its smoothing keeps it clean.
        /// docs/interaction.md#2-the-panel
        /// </summary>
        private const string PixelFontName = "Pixellari";
        private static bool _fontMissLogged;
        private const int TitleSize = 32;
        private const int TextSize = 24;
        private const float FontRetrySeconds = 5f;

        // The game's own UI sprites. docs/interaction.md#2-the-panel
        private const string SendArrowPath = "textures/ui/icons/Arrow2";
        private const string ChatIconPath = "textures/ui/icons/Chat";
        private const string CrossIconPath = "textures/ui/icons/Cross";
        private const string ButtonPath = "textures/ui/interfaces/ColorButton";
        private const string ButtonPressedPath = "textures/ui/interfaces/ColorButtonPressed";
        /// <summary>
        /// The send button's tint over the white ColorButton: the green the game uses for confirm,
        /// pale enough that its frame still reads as a frame.
        /// </summary>
        private static readonly Color AcceptTint = new(0.72f, 1f, 0.68f, 1f);

        // Built by Ensure(), which NpcTalkWindow.OnGUI calls before it draws anything. Not `= null!`:
        // this class has a static constructor, and the initializers would be emitted into it.
        #pragma warning disable CS8618
        private static Texture2D _fill;
        private static Texture2D _border;
        private static Texture2D _accept;
        private static bool _built;
        private static Font? _pixelFont;
        private static float _fontRetryAt;
        private static Sprite? _sendArrow;
        private static Sprite? _chatIcon;
        private static Sprite? _crossIcon;
        private static Sprite? _button;
        private static Sprite? _buttonPressed;
        private static bool _spritesLoaded;

        internal static GUIStyle Title { get; private set; }
        internal static GUIStyle Body { get; private set; }
        internal static GUIStyle Echo { get; private set; }
        internal static GUIStyle Command { get; private set; }
        internal static GUIStyle Field { get; private set; }
        internal static GUIStyle Placeholder { get; private set; }
        private static GUIStyle _buttonLabel;
        #pragma warning restore CS8618

        internal static void Ensure()
        {
            if (_built)
            {
                // Not loaded when the styles were built: it may be now. Cheap, and rare.
                if (_pixelFont == null && Time.unscaledTime >= _fontRetryAt) TryPixelFont();

                return;
            }
            _built = true;

            _fill = Solid(Fill);
            _border = Solid(Border);
            _accept = Solid(Accept);

            // Until the game's own face turns up: the terminals are monospaced, Consolas is the
            // safe Windows pick, and a null font simply falls back to the IMGUI default.
            Font mono = Font.CreateDynamicFontFromOSFont(
                ["Consolas", "Lucida Console", "Courier New"], 17);
            // Styles are built once and outlive the scene; without this the font is
            // collected on a scene load and every label silently loses its face.
            if (mono != null) mono.hideFlags = HideFlags.HideAndDontSave;

            Title = new GUIStyle(GUI.skin.label)
            {
                font = mono,
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                richText = false
            };
            Title.normal.textColor = Text;

            Body = new GUIStyle(GUI.skin.label)
            {
                font = mono,
                fontSize = 17,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft
            };
            Body.normal.textColor = Text;

            Echo = new GUIStyle(Body);
            Echo.normal.textColor = Dim;

            Command = new GUIStyle(GUI.skin.label)
            {
                font = mono,
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            Command.normal.textColor = Tan;
            Command.hover.textColor = Color.white;
            Command.active.textColor = Color.white;

            Field = new GUIStyle(GUI.skin.textField)
            {
                font = mono,
                fontSize = 17,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 10, 0, 0)
            };
            // The ColorButton frame under it is the background.
            Field.normal.background = null;
            Field.focused.background = null;
            Field.hover.background = null;
            Field.padding = new RectOffset(8, 8, 0, 0);
            Field.normal.textColor = Text;
            Field.focused.textColor = Text;

            Placeholder = new GUIStyle(Field);
            Placeholder.normal.background = null;
            Placeholder.normal.textColor = Dim;

            _buttonLabel = new GUIStyle(GUI.skin.label)
            {
                font = mono,
                fontSize = 19,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            _buttonLabel.normal.textColor = Text;

            TryPixelFont();
        }

        /// <summary>
        /// Finds the game's Pixellari among the loaded fonts and moves every style onto it, without
        /// the synthesized bold and italic that smear a pixel face. Looked for, not loaded: the .ttf
        /// is not under Resources itself.
        /// </summary>
        private static void TryPixelFont()
        {
            _fontRetryAt = Time.unscaledTime + FontRetrySeconds;
            Font[] loaded = Resources.FindObjectsOfTypeAll<Font>();
            Font? found = null;
            foreach (Font f in loaded)
            {
                if (f != null && f.name == PixelFontName)
                {
                    found = f;
                    break;
                }
            }
            if (found == null)
            {
                // Once: what is there instead, so a renamed asset is a log read, not a guess.
                if (!_fontMissLogged)
                {
                    _fontMissLogged = true;
                    List<string> names = [];
                    foreach (Font f in loaded)
                    {
                        if (f != null) names.Add(f.name);
                    }
                    NpcLog.Core.LogInfo(
                        $"[talk] {PixelFontName} is not loaded yet - using Consolas. Loaded fonts: " + string.Join(", ", names));
                }
                return;
            }

            _pixelFont = found;

            PixelStyle(Title, TitleSize);
            PixelStyle(Body, TextSize);
            PixelStyle(Echo, TextSize);
            PixelStyle(Command, TextSize);
            PixelStyle(Field, TextSize);
            PixelStyle(Placeholder, TextSize);
            PixelStyle(_buttonLabel, TextSize);
            NpcLog.Core.LogInfo($"[talk] Using the game's {found.name} font");
        }

        private static void PixelStyle(GUIStyle style, int size)
        {
            style.font = _pixelFont;
            style.fontSize = size;
            style.fontStyle = FontStyle.Normal;
        }

        private static Texture2D Solid(Color c)
        {
            Texture2D t = new(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        /// <summary>
        /// A filled rectangle with a one-tone outline - the shape every part of the
        /// game's terminal UI is built from.
        /// </summary>
        internal static void Panel(Rect r) => Framed(r, _border);

        private static void Framed(Rect r, Texture2D edge)
        {
            GUI.DrawTexture(r, _fill);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, BorderWidth), edge);
            GUI.DrawTexture(new Rect(r.x, r.yMax - BorderWidth, r.width, BorderWidth), edge);
            GUI.DrawTexture(new Rect(r.x, r.y, BorderWidth, r.height), edge);
            GUI.DrawTexture(new Rect(r.xMax - BorderWidth, r.y, BorderWidth, r.height), edge);
        }

        /// <summary>
        /// Screen pixels per UI pixel: 2 at 1080p, 3 at 1440p, 4 at 4K. The game's buttons draw their
        /// frame at twice that, their icons at once.
        /// </summary>
        internal static int UiScale => Mathf.Max(1, Mathf.RoundToInt(Screen.height / 540f));

        private static void LoadSprites()
        {
            if (_spritesLoaded) return;

            _spritesLoaded = true;
            _sendArrow = LoadSprite(SendArrowPath);
            _chatIcon = LoadSprite(ChatIconPath);
            _crossIcon = LoadSprite(CrossIconPath);
            _button = LoadSprite(ButtonPath);
            _buttonPressed = LoadSprite(ButtonPressedPath);
        }

        private static Sprite? LoadSprite(string path)
        {
            Sprite? sprite = Resources.Load<Sprite>(path);
            if (sprite == null || sprite.texture == null)
            {
                NpcLog.Core.LogWarning($"[talk] No sprite at Resources/{path} - drawing a plain stand-in");
                return null;
            }
            sprite.texture.filterMode = FilterMode.Point;
            return sprite;
        }

        /// <summary>
        /// A ColorButton frame - white edge, dark ring, grey lip underneath - nine-sliced at a whole
        /// pixel scale, or ColorButtonPressed, dropped a pixel, while it is held down.
        /// </summary>
        private static void ButtonFrame(Rect r, bool pressed, Color tint)
        {
            LoadSprites();
            Sprite? sprite = pressed && _buttonPressed != null ? _buttonPressed : _button;
            if (sprite == null)
            {
                Framed(r, tint == Color.white ? _border : _accept);
                return;
            }
            Color old = GUI.color;
            GUI.color = tint;
            NineSlice(r, sprite, UiScale * 2);
            GUI.color = old;
        }

        /// <summary>
        /// Draws a sprite's nine slices from its own border (x left, y bottom, z right, w top, in
        /// texels), corners at `scale` screen pixels per texel and the middle stretched.
        /// </summary>
        private static void NineSlice(Rect r, Sprite sprite, int scale)
        {
            Texture2D tex = sprite.texture;
            Rect px = sprite.textureRect;
            Vector4 b = sprite.border;
            float[] srcX = [px.xMin, px.xMin + b.x, px.xMax - b.z, px.xMax];
            // Texture space runs bottom-up, the screen top-down.
            float[] srcY = [px.yMax, px.yMax - b.w, px.yMin + b.y, px.yMin];
            float[] dstX = [r.xMin, r.xMin + b.x * scale, r.xMax - b.z * scale, r.xMax];
            float[] dstY = [r.yMin, r.yMin + b.w * scale, r.yMax - b.y * scale, r.yMax];
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    Rect dst = Rect.MinMaxRect(dstX[col], dstY[row], dstX[col + 1], dstY[row + 1]);
                    if (dst.width <= 0f || dst.height <= 0f) continue;

                    Rect uv = Rect.MinMaxRect(srcX[col] / tex.width, srcY[row + 1] / tex.height,
                        srcX[col + 1] / tex.width, srcY[row] / tex.height);
                    GUI.DrawTextureWithTexCoords(dst, tex, uv);
                }
            }
        }

        /// <summary>
        /// A sprite centred in `r` at `scale` screen pixels per texel, nudged down a UI pixel with a
        /// pressed button so the icon moves with its face.
        /// </summary>
        private static void Icon(Rect r, Sprite sprite, int scale, bool pressed)
        {
            Texture2D tex = sprite.texture;
            Rect px = sprite.textureRect;
            float w = px.width * scale;
            float h = px.height * scale;
            float drop = pressed ? UiScale * 2f : 0f;
            // The lip sits under the face: centre on the face, not on the whole button.
            float faceH = r.height - UiScale * 2f;
            Rect dst = new(Mathf.Round(r.center.x - w * 0.5f), Mathf.Round(r.y + (faceH - h) * 0.5f + drop), w, h);
            Rect uv = new(px.x / tex.width, px.y / tex.height, px.width / tex.width, px.height / tex.height);
            GUI.DrawTextureWithTexCoords(dst, tex, uv);
        }

        private static bool Held(Rect r)
        {
            Event e = Event.current;
            return GUIUtility.hotControl != 0 && Input.GetMouseButton(0) && r.Contains(e.mousePosition);
        }

        /// <summary>
        /// A ColorButton with a text label.
        /// </summary>
        internal static bool Button(Rect r, string label, bool accept)
        {
            bool held = Held(r);
            ButtonFrame(r, held, accept ? AcceptTint : Color.white);
            if (label.Length > 0)
            {
                Rect face = new(r.x, r.y + (held ? UiScale * 2f : 0f), r.width, r.height - UiScale * 2f);
                GUI.Label(face, label, _buttonLabel);
            }
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        /// <summary>
        /// A ColorButton with one of the game's icons on it, or `fallback` as text without the icon.
        /// </summary>
        private static bool IconButton(Rect r, Sprite? icon, string fallback, bool accept)
        {
            LoadSprites();
            if (icon == null) return Button(r, fallback, accept);

            bool held = Held(r);
            ButtonFrame(r, held, accept ? AcceptTint : Color.white);
            Icon(r, icon, UiScale, held);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        internal static bool SendButton(Rect r)
        {
            LoadSprites();
            return IconButton(r, _sendArrow, ">", true);
        }

        internal static bool CloseButton(Rect r)
        {
            LoadSprites();
            return IconButton(r, _crossIcon, "X", false);
        }

        internal static bool CommandsButton(Rect r)
        {
            LoadSprites();
            return IconButton(r, _chatIcon, "?", false);
        }

        /// <summary>
        /// The text field's frame: the same ColorButton, never pressed.
        /// </summary>
        internal static void FieldFrame(Rect r) => ButtonFrame(r, false, Color.white);
    }
}
