using System.Collections.Generic;
using System.Text;
using NPC.Core.Navigation;
using NPC.Core.World;
using Space;
using UnityEngine;

namespace NPC.Core.Interaction
{
    /// <summary>
    /// Talking to NPCs: look at one and press Interact. NPC.Core owns the game's Interact key for every
    /// NPC mod and opens one window at a time, on the NPC looked at most directly.
    /// docs/invariants.md#one-interact-owner
    /// </summary>
    public static class NpcInteraction
    {
        /// <summary>
        /// Measured to the nearest point of the NPC's controller, never to its chest: at arm's length the
        /// angle to a chest point blows up, which opened the window from across the room but not from right
        /// in front of it. The nearest point already widens the cone around the body's middle, so it stays narrow.
        /// </summary>
        private const float LookAngle = 20f;
        /// <summary>
        /// Shorter than the player's own reach, so standing at a keypad or a terminal does not mean talking
        /// to an NPC instead of using it.
        /// </summary>
        private const float TalkRange = 2.4f;
        /// <summary>
        /// NPCs further than this stay out of the Interact trace: that press was not meant for them.
        /// </summary>
        private const float NoteRange = 6f;
        /// <summary>
        /// Where the sight test aims: the player's chest, measured from the feet. The player's controller is a
        /// 1 m capsule on its origin, so this is 0.6 m above a player-like origin and 1.1 m above a body rooted
        /// at its feet.
        /// </summary>
        private const float ChestAboveFeet = 1.1f;
        private const int MaxLines = 80;

        internal sealed class Talk(INpcConversation conversation)
        {
            public readonly INpcConversation Conversation = conversation;
            public readonly List<string> Lines = [];
        }

        private static readonly List<Talk> Talks = [];
        private static readonly RaycastHit[] SightHits = new RaycastHit[16];
        private static bool _initialized;

        /// <summary>
        /// The conversation the window is open on, or null.
        /// </summary>
        internal static Talk? Open { get; private set; }

        /// <summary>
        /// Whether the talk window is open: hotkeys and overlays stay quiet while it is.
        /// </summary>
        public static bool IsOpen => Open != null;

        /// <summary>
        /// Makes an NPC talkable. It is dropped when its NPC unregisters. False when already registered.
        /// </summary>
        public static bool Register(INpcConversation conversation)
        {
            if (conversation == null || Find(conversation) != null) return false;

            if (!_initialized)
            {
                _initialized = true;
                NpcRegistry.Unregistered += Drop;
            }
            Talks.Add(new Talk(conversation));
            return true;
        }

        public static void Unregister(INpcConversation conversation)
        {
            Talk? talk = Find(conversation);
            if (talk == null) return;

            if (Open == talk) Close();
            Talks.Remove(talk);
        }

        private static Talk? Find(INpcConversation conversation)
        {
            foreach (Talk talk in Talks)
            {
                if (talk.Conversation == conversation) return talk;
            }
            return null;
        }

        private static void Drop(INpc npc)
        {
            for (int i = Talks.Count - 1; i >= 0; i--)
            {
                if (Talks[i].Conversation.Npc == npc) Unregister(Talks[i].Conversation);
            }
        }

        // ------------------------------------------------------------------
        // Opening: docs/interaction.md#1-opening-it
        // ------------------------------------------------------------------

        /// <summary>
        /// From the one OnInteract listener: of the NPCs the player could talk to, the one at the smallest
        /// view angle answers; a tie goes to the one registered first.
        /// </summary>
        internal static void OnInteractPressed()
        {
            if (Open != null) return;

            Talk? best = null;
            float bestAngle = float.MaxValue;
            int bestIndex = int.MaxValue;
            StringBuilder? why = NpcLog.Level >= 2 ? new StringBuilder() : null;
            foreach (Talk talk in Talks)
            {
                if (FacingAngle(talk.Conversation, why) is not { } angle) continue;

                int index = NpcRegistry.IndexOf(talk.Conversation.Npc);
                if (angle > bestAngle || (angle == bestAngle && index >= bestIndex)) continue;

                best = talk;
                bestAngle = angle;
                bestIndex = index;
            }
            if (best != null)
            {
                OpenOn(best, bestAngle);
                return;
            }

            if (why == null) return;

            NpcLog.Core.LogInfo($"[talk] Interact: no NPC answers ({Talks.Count} talkable)" +
                                (why.Length > 0 ? ":" + why : $", none within {NoteRange:0}m"));
        }

        /// <summary>
        /// The view angle to this NPC while the player could talk to it, else null. A rejection within
        /// NoteRange goes into `why`, for the Interact line.
        /// </summary>
        private static float? FacingAngle(INpcConversation conversation, StringBuilder? why)
        {
            INpc npc = conversation.Npc;
            if (NpcRegistry.IsGone(npc)) return null;

            Player? player = NpcPlayer.Pilot;
            if (player == null || player.Controller == null) return Reject(why, npc, "no player");

            Transform cam = player.Controller.CameraAnimator != null
                ? player.Controller.CameraAnimator.CachedTransform
                : player.Controller.CachedTransform;
            if (cam == null) return Reject(why, npc, "no camera");

            Vector3 aim = NearestPointTo(npc, cam.position) - cam.position;
            float reach = aim.magnitude;
            if (reach > NoteRange) return null;

            if (npc.IsDead) return Reject(why, npc, "dead");
            if (!npc.IsActive) return Reject(why, npc, "not active");
            if (!conversation.CanTalk) return Reject(why, npc, "CanTalk is false");
            if (reach > TalkRange) return Reject(why, npc, $"{reach:0.0}m away, range {TalkRange:0.0}m");

            float angle = reach > 0.01f ? Vector3.Angle(cam.forward, aim) : 0f;
            if (angle > LookAngle) return Reject(why, npc, $"{angle:0}deg off the view, cone {LookAngle:0}deg");

            if (PlayerIsBusy(player.Controller, cam, npc)) return Reject(why, npc, "the player aims at or holds an Interactable");

            Vector3 toNpc = ChestOf(npc) - cam.position;
            if (SightBlocked(cam.position, toNpc, toNpc.magnitude, npc, player.transform) is { } wall)
            {
                return Reject(why, npc, $"sight blocked by '{wall.name}'");
            }
            return angle;
        }

        private static float? Reject(StringBuilder? why, INpc npc, string reason)
        {
            why?.Append(' ').Append(npc.Name).Append(": ").Append(reason).Append(';');
            return null;
        }

        /// <summary>
        /// The closest point on the NPC's controller to `from`, falling back to its chest when it has none.
        /// </summary>
        private static Vector3 NearestPointTo(INpc npc, Vector3 from)
        {
            CharacterController? controller = npc.Controller;
            if (controller != null && controller.enabled) return controller.ClosestPoint(from);

            return ChestOf(npc);
        }

        private static Vector3 ChestOf(INpc npc)
        {
            CharacterController? controller = npc.Controller;
            float originToFeet = controller != null ? controller.center.y - controller.height * 0.5f : -0.5f;
            return npc.Transform.position + Vector3.up * (originToFeet + ChestAboveFeet);
        }

        /// <summary>
        /// The player is aiming at something interactive, or already carrying it. The game's own
        /// interaction wins - an NPC must not steal the keypress.
        /// </summary>
        private static bool PlayerIsBusy(PlayerController controller, Transform cam, INpc npc)
        {
            // A game update that renames the focus field must not hand every terminal's keypress to the
            // window: judge the aim ourselves instead.
            if (!GameInternals.PlayerControllerAccess.CanReadFocus) return AimingAtInteractable(controller, cam, npc);

            Interactable? focused = GameInternals.PlayerControllerAccess.GetFocusedInteractable(controller);
            if (focused == null) return false;

            try
            {
                if (focused.Grabbable != null && focused.Grabbable.IsGrabbed) return true;

                return focused.Highlighted;
            }
            catch (System.Exception)
            {
                // Highlighted dereferences a serialized field NPC.Core does not own.
                return false;
            }
        }

        /// <summary>
        /// The fallback for PlayerIsBusy: the nearest solid thing along the view within the player's reach,
        /// and whether it is something the game would interact with. Coarser than the game's own focus (no
        /// held-item test), but it fails toward the game's interaction, never toward the window.
        /// </summary>
        private static bool AimingAtInteractable(PlayerController controller, Transform cam, INpc npc)
        {
            float reach = SceneLoader.Instance != null ? SceneLoader.Instance.GlobalData.Settings.reachRange : TalkRange;
            int count = Physics.RaycastNonAlloc(cam.position, cam.forward, SightHits, reach,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

            Collider? nearest = null;
            float nearestDist = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                Collider hit = SightHits[i].collider;
                if (hit == null || hit.transform.IsChildOf(controller.CachedTransform)) continue;

                if (SightHits[i].distance >= nearestDist) continue;

                nearestDist = SightHits[i].distance;
                nearest = hit;
            }

            return nearest != null && !npc.IsOwnBody(nearest.transform) &&
                   nearest.GetComponent<Interactable>() != null;
        }

        /// <summary>
        /// The solid geometry between the player and the NPC, or null. Without it the cone test alone lets
        /// the window be opened through walls and shut doors.
        /// </summary>
        private static Collider? SightBlocked(Vector3 from, Vector3 toNpc, float distance, INpc npc, Transform playerBody)
        {
            int count = Physics.RaycastNonAlloc(from, toNpc / distance, SightHits, distance - 0.25f,
                NavProbe.ProbeLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider hit = SightHits[i].collider;
                if (hit == null || npc.IsOwnBody(hit.transform) || hit.transform.IsChildOf(playerBody)) continue;

                return hit;
            }
            return null;
        }

        private static void OpenOn(Talk talk, float angle)
        {
            Open = talk;
            INpcConversation conversation = talk.Conversation;
            using NpcRegistry.ActingScope _ = NpcRegistry.Acting(conversation.Npc);
            NpcLog.Log.LogInfo($"[talk] Talk window opened ({angle:0}deg off the view)");
            NpcTalkWindow.Opened(conversation.Title);
            if (talk.Lines.Count == 0) Say(talk, conversation.Greeting);

            conversation.SetOpen(true);
            SetPlayerInUi(true);
        }

        internal static void Close()
        {
            Talk? talk = Open;
            if (talk == null) return;

            Open = null;
            using NpcRegistry.ActingScope _ = NpcRegistry.Acting(talk.Conversation.Npc);
            talk.Conversation.SetOpen(false);
            SetPlayerInUi(false);
        }

        /// <summary>
        /// Same handover AssistanceBot.Talk performs: free the cursor and move the player onto the UI action
        /// map, then put both back.
        /// </summary>
        private static void SetPlayerInUi(bool inUi)
        {
            GameManager gm = GameManager.Instance;
            if (gm == null) return;

            Player? player = NpcPlayer.Pilot;
            if (player != null && player.Controller != null) player.Controller.LockedCursor = !inUi;

            if (gm.InputHandler == null) return;

            if (inUi) gm.InputHandler.SwitchToUIInput();
            else gm.InputHandler.SwitchToGameInput();
        }

        // ------------------------------------------------------------------
        // Conversation
        // ------------------------------------------------------------------

        /// <summary>
        /// Once per frame: the window closes on an NPC that died or is gone.
        /// </summary>
        internal static void Update()
        {
            Talk? talk = Open;
            if (talk != null && (NpcRegistry.IsGone(talk.Conversation.Npc) || talk.Conversation.Npc.IsDead)) Close();
        }

        internal static void Say(Talk talk, string text) => Append(talk, "> " + text);

        internal static void Echo(Talk talk, string text) => Append(talk, "$ " + text);

        private static void Append(Talk talk, string text)
        {
            talk.Lines.Add(text);
            if (talk.Lines.Count > MaxLines) talk.Lines.RemoveAt(0);

            NpcTalkWindow.ScrollToEnd();
        }

        /// <summary>
        /// The player's line and the NPC's answer.
        /// </summary>
        internal static void Submit(Talk talk, string text)
        {
            text = text.Trim();
            if (text.Length == 0) return;

            Echo(talk, text);
            using NpcRegistry.ActingScope _ = NpcRegistry.Acting(talk.Conversation.Npc);
            Say(talk, talk.Conversation.Answer(text));
        }
    }
}
