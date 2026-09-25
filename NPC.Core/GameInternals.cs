using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using FMODUnity;
using Space;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NPC.Core
{
    /// <summary>
    /// Every reflection accessor NPC.Core needs into the game's private members, resolved once, with
    /// a one-time warning naming what a missing member degrades. Misses return null/default. Each mod
    /// keeps its own for what it alone reads. docs/invariants.md#reflection-lives-in-gameinternals
    /// </summary>
    internal static class GameInternals
    {
        private static readonly HashSet<string> WarnedMembers = [];
        private static int _resolvedCount;

        /// <summary>
        /// Runs every accessor group's initializer now, so a game update reports all its
        /// missing members at plugin load instead of whenever each feature is first used.
        /// </summary>
        internal static void ResolveAll()
        {
            foreach (Type group in typeof(GameInternals).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                try
                {
                    RuntimeHelpers.RunClassConstructor(group.TypeHandle);
                }
                catch (TypeInitializationException ex)
                {
                    NpcLog.Log.LogError($"[internals] {group.Name} failed to resolve: {ex.InnerException}");
                }
            }

            if (WarnedMembers.Count == 0)
            {
                NpcLog.Log.LogInfo($"[internals] All {_resolvedCount} game members resolved.");
            }
            else
            {
                NpcLog.Log.LogWarning(
                    $"[internals] {WarnedMembers.Count} of {_resolvedCount} game members missing - see warnings above.");
            }
        }

        private const BindingFlags MemberFlags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;

        /// <summary>
        /// A field whose type no longer holds a T counts as missing, so no accessor ever
        /// casts a changed type or silently reads null. `critical` marks a member whose loss a
        /// player would notice at once; its miss is logged as an error so it stands out.
        /// </summary>
        private static FieldInfo? Field<T>(Type type, string name, string feature, bool critical = false)
        {
            _resolvedCount++;
            FieldInfo? field = type.GetField(name, MemberFlags);
            if (field == null)
            {
                WarnOnce(type, name, "not found", feature, critical);
                return null;
            }
            if (!typeof(T).IsAssignableFrom(field.FieldType))
            {
                WarnOnce(type, name, "changed type from " + typeof(T).Name + " to " + field.FieldType.Name, feature, critical);
                return null;
            }
            return field;
        }

        private static MethodInfo? Method(Type type, string name, Type[] parameters, string feature)
        {
            _resolvedCount++;
            MethodInfo? method = type.GetMethod(name, MemberFlags, null, parameters, null);
            if (method == null) WarnOnce(type, name, "not found with its expected parameters", feature);

            return method;
        }

        private static void WarnOnce(Type type, string name, string problem, string feature, bool critical = false)
        {
            if (!WarnedMembers.Add(type.Name + "." + name)) return;

            string message = "[internals] Game internals changed: member '" + name + "' on '" + type.Name + "' " +
                             problem + ". A game update probably changed it - " + feature + " may be broken.";
            if (critical) NpcLog.Log.LogError(message);
            else NpcLog.Log.LogWarning(message);
        }

        private static T? Get<T>(FieldInfo? field, object? instance) where T : class
        {
            return field == null || instance == null ? null : field.GetValue(instance) as T;
        }

        private static T? GetValue<T>(FieldInfo? field, object? instance) where T : struct
        {
            return field != null && instance != null && field.GetValue(instance) is T value ? value : null;
        }

        /// <summary>
        /// Gate: the sliding mechanism the doorway carve-out is measured from - the anchors
        /// move along the gate's local x, which is what makes local x the passage width.
        /// </summary>
        internal static class GateAccess
        {
            private static readonly FieldInfo? OpenedWidth = Field<float>(typeof(Gate), "openedWidth", "the doorway carve-out width, which falls back to a fixed 0.85m");
            private static readonly FieldInfo? GateAnchors = Field<Transform[]>(typeof(Gate), "gateAnchors", "the doorway carve-out shape, which falls back to a cylinder");
            private static readonly FieldInfo? AntiCrashers = Field<AntiCrasher[]>(typeof(Gate), "antiCrashers", "doorway occupancy test before closing a door");

            internal static Transform[]? GetGateAnchors(Gate? gate) => Get<Transform[]>(GateAnchors, gate);

            /// <summary>
            /// The sensors whose OnTriggerEnter calls Gate.FailClose.
            /// </summary>
            internal static AntiCrasher[]? GetAntiCrashers(Gate? gate) => Get<AntiCrasher[]>(AntiCrashers, gate);

            /// <summary>
            /// Whether the anchors can be read at all. A gate with no anchors is not a
            /// door - `Gate.ApplyData` warns and refuses to work. A gate whose anchors
            /// cannot be read is a game update, and every gate must keep its doorway.
            /// </summary>
            internal static bool AnchorsReadable => GateAnchors != null;

            /// <summary>
            /// How far each leaf slides. 0 when unreadable, which leaves the carve-out at
            /// the leaf's current inner edge - the conservative direction.
            /// </summary>
            internal static float GetOpenedWidth(Gate? gate)
            {
                return OpenedWidth == null || gate == null ? 0f : (float)OpenedWidth.GetValue(gate);
            }
        }

        /// <summary>
        /// ConsoleMenu: the private command dictionary and console print method.
        /// </summary>
        internal static class ConsoleMenuAccess
        {
            private static readonly MethodInfo? PrintMethod = Method(typeof(ConsoleMenu), "Print", [typeof(string)], "console command output");
            private static readonly FieldInfo? Commands = Field<Dictionary<string, Action<string[]>>>(typeof(ConsoleMenu), "commands", "console command registration");

            internal static void Print(ConsoleMenu? console, string text)
            {
                try
                {
                    if (PrintMethod != null && console != null) PrintMethod.Invoke(console, [text]);
                }
                catch (Exception ex)
                {
                    // Console might be closed mid-print; keep the output visible in
                    // the BepInEx log so command feedback is never lost entirely.
                    NpcLog.Log.LogDebug("[internals] Console print failed: " + ex.Message);
                }
            }

            internal static Dictionary<string, Action<string[]>>? GetCommands(ConsoleMenu? console)
            {
                return Get<Dictionary<string, Action<string[]>>>(Commands, console);
            }
        }

        /// <summary>
        /// SpaceObject: the container the game deactivates when a station optimizes.
        /// Anything left aboard must live under it, as Grabbables do via
        /// SpaceObject.SetItemOwnership. docs/invariants.md#a-station-owns-its-interior-by-reference
        /// </summary>
        internal static class SpaceObjectAccess
        {
            private static readonly FieldInfo? ContentParent =
                Field<GameObject>(typeof(SpaceObject), "contentParent", "telling which station a floor or room belongs to, and parking NPCs there");

            internal static Transform? GetContentParent(SpaceObject? spaceObject)
            {
                GameObject? content = Get<GameObject>(ContentParent, spaceObject);
                return content != null ? content.transform : null;
            }
        }

        /// <summary>
        /// SpaceStation: room list. Read from the backing array to avoid referencing
        /// Unity.InputSystem just to iterate the public ReadOnlyArray.
        /// </summary>
        internal static class SpaceStationAccess
        {
            private static readonly FieldInfo? Rooms = Field<Room[]>(typeof(SpaceStation), "rooms", "station owner detection for nav nodes");

            internal static Room[]? GetRooms(SpaceStation? station) => Get<Room[]>(Rooms, station);
        }

        /// <summary>
        /// EntryDetector: rooms on both sides of a doorway, its side test, its door, and the
        /// player it remembers. NPC room tracking replicates the game's side test with these.
        /// </summary>
        internal static class EntryDetectorAccess
        {
            private static readonly FieldInfo? InnerRoom = Field<Room>(typeof(EntryDetector), "innerRoom", "NPC room tracking");
            private static readonly FieldInfo? OuterRoom = Field<Room>(typeof(EntryDetector), "outerRoom", "NPC room tracking");
            private static readonly FieldInfo? RotationReference = Field<Transform>(typeof(EntryDetector), "rotationReference", "NPC room tracking");
            private static readonly FieldInfo? ReverseSide = Field<bool>(typeof(EntryDetector), "reverseSide", "NPC room tracking");
            private static readonly FieldInfo? Door = Field<Gate>(typeof(EntryDetector), "door", "room content loading around doors");
            private static readonly FieldInfo? Optimize = Field<bool>(typeof(EntryDetector), "optimize", "switching off rooms an NPC left");
            // The detector's remembered player, which the game sets but never clears.
            // docs/invariants.md#npc-closes-must-not-move-the-player
            private static readonly FieldInfo? CurrentPlayer = Field<Player>(typeof(EntryDetector), "player", "keeping NPCs' door closes from moving the player between rooms");

            internal static Room? GetInnerRoom(EntryDetector? detector) => Get<Room>(InnerRoom, detector);
            internal static Room? GetOuterRoom(EntryDetector? detector) => Get<Room>(OuterRoom, detector);
            internal static Transform? GetRotationReference(EntryDetector? detector) => Get<Transform>(RotationReference, detector);
            internal static bool GetReverseSide(EntryDetector? detector) => GetValue<bool>(ReverseSide, detector) == true;
            internal static Gate? GetDoor(EntryDetector? detector) => Get<Gate>(Door, detector);
            /// <summary>
            /// Whether the game switches the far room off once the door shuts; false when unreadable.
            /// </summary>
            internal static bool GetOptimize(EntryDetector? detector) => GetValue<bool>(Optimize, detector) == true;
            internal static Player? GetCurrentPlayer(EntryDetector? detector) => Get<Player>(CurrentPlayer, detector);

            internal static bool SetCurrentPlayer(EntryDetector? detector, Player? player)
            {
                if (CurrentPlayer == null || detector == null) return false;

                CurrentPlayer.SetValue(detector, player);
                return true;
            }
        }

        /// <summary>
        /// Airlock: outer/inner doors, hatch and connected room. Airlock gates are never opened
        /// by an NPC, and an airlock chamber is never a safe spot.
        /// </summary>
        internal static class AirlockAccess
        {
            private static readonly FieldInfo? OuterDoor = Field<Gate>(typeof(Airlock), "outerDoor", "airlock gate detection / space protection");
            private static readonly FieldInfo? InnerDoor = Field<Gate>(typeof(Airlock), "innerDoor", "airlock gate detection / space protection");
            private static readonly FieldInfo? Hatch = Field<Gate>(typeof(Airlock), "hatch", "airlock gate detection / space protection");
            private static readonly FieldInfo? ConnectedRoom = Field<Room>(typeof(Airlock), "connectedRoom", "airlock chamber detection");

            internal static Gate? GetOuterDoor(Airlock? airlock) => Get<Gate>(OuterDoor, airlock);
            internal static Gate? GetInnerDoor(Airlock? airlock) => Get<Gate>(InnerDoor, airlock);
            internal static Gate? GetHatch(Airlock? airlock) => Get<Gate>(Hatch, airlock);
            internal static Room? GetConnectedRoom(Airlock? airlock) => Get<Room>(ConnectedRoom, airlock);
        }

        /// <summary>
        /// PlayerDetector: whether a doorway only opens for a suited player.
        /// docs/invariants.md#an-npc-opens-only-what-the-player-could
        /// </summary>
        internal static class PlayerDetectorAccess
        {
            private static readonly FieldInfo? HelmetRequired = Field<bool>(typeof(PlayerDetector), "helmetRequired",
                "keeping NPCs from opening a door you need a suit for (the tutorial's first door)");

            internal static bool? GetHelmetRequired(PlayerDetector? detector) => GetValue<bool>(HelmetRequired, detector);
        }

        /// <summary>
        /// DoorPinCode: the gate a pin-code panel unlocks, and the code that opens it. An NPC only
        /// uses a code the player gave it: docs/invariants.md#an-npc-only-knows-codes-it-was-told
        /// </summary>
        internal static class DoorPinCodeAccess
        {
            private static readonly FieldInfo? Door = Field<Gate>(typeof(DoorPinCode), "door", "password-locked gate detection");
            private static readonly FieldInfo? InitialPinCode = Field<int>(typeof(PinCode), "initialPinCode", "giving NPCs a door password");

            internal static Gate? GetWiredGate(DoorPinCode? pin) => Get<Gate>(Door, pin);

            /// <summary>
            /// The panel's code, or null when reflection failed - in which case no code the player
            /// types can ever match, and the door simply stays impassable.
            /// </summary>
            internal static int? GetPinCode(DoorPinCode? pin) => GetValue<int>(InitialPinCode, pin);
        }

        /// <summary>
        /// LifecareDisplay: the scanning terminal UI NPCs appear on (map icon + lifeforms counter).
        /// docs/lifecare.md
        /// </summary>
        internal static class LifecareDisplayAccess
        {
            // Icon mapping fallback used when the game's scale field is missing.
            private const float DefaultScale = 0.05f;

            private static readonly FieldInfo? LifeformsLabel = Field<TMP_Text>(typeof(LifecareDisplay), "lifeformsLabel", "lifecare terminal integration");
            private static readonly FieldInfo? BreathlessIcon = Field<Image>(typeof(LifecareDisplay), "breathlessIcon", "lifecare terminal integration");
            private static readonly FieldInfo? PlayerIcon = Field<Image>(typeof(LifecareDisplay), "playerIcon", "lifecare terminal integration");
            private static readonly FieldInfo? TempObjects = Field<GameObject[]>(typeof(LifecareDisplay), "tempObjects", "lifecare terminal integration");
            private static readonly FieldInfo? Scale = Field<float>(typeof(LifecareDisplay), "scale", "lifecare terminal integration");
            private static readonly FieldInfo? BootLoading = Field<LoadingAnimator>(typeof(LifecareDisplay), "bootLoading", "lifecare terminal integration");
            private static readonly FieldInfo? ScanLoading = Field<LoadingAnimator>(typeof(LifecareDisplay), "scanLoading", "lifecare terminal integration");

            internal static TMP_Text? GetLifeformsLabel(LifecareDisplay? display) => Get<TMP_Text>(LifeformsLabel, display);
            internal static Image? GetBreathlessIcon(LifecareDisplay? display) => Get<Image>(BreathlessIcon, display);
            internal static Image? GetPlayerIcon(LifecareDisplay? display) => Get<Image>(PlayerIcon, display);
            internal static GameObject[]? GetTempObjects(LifecareDisplay? display) => Get<GameObject[]>(TempObjects, display);
            internal static LoadingAnimator? GetBootLoading(LifecareDisplay? display) => Get<LoadingAnimator>(BootLoading, display);
            internal static LoadingAnimator? GetScanLoading(LifecareDisplay? display) => Get<LoadingAnimator>(ScanLoading, display);
            internal static float GetScale(LifecareDisplay? display) => GetValue<float>(Scale, display) ?? DefaultScale;
        }

        /// <summary>
        /// SellStation: the item zone a sell pen encloses.
        /// docs/invariants.md#a-fenced-sell-station-is-not-somewhere-to-stand
        /// </summary>
        internal static class SellStationAccess
        {
            private static readonly FieldInfo? ItemDetector = Field<ItemDetector>(typeof(SellStation), "itemDetector", "keeping NPCs out of sell station pens");

            internal static ItemDetector? GetItemDetector(SellStation? station) => Get<ItemDetector>(ItemDetector, station);
        }

        /// <summary>
        /// PlayerController: what the player is aiming at, so the talk window never steals the
        /// Interact key from a terminal or a held item; the head/foot triggers every NPC body must not touch.
        /// </summary>
        internal static class PlayerControllerAccess
        {
            private static readonly FieldInfo? FocusedInteractable = Field<Interactable>(typeof(PlayerController), "focusedInteractable",
                "keeping the talk window out of the way of terminals and held items", critical: true);
            private static readonly FieldInfo? HeadTriggerField = Field<HeadTrigger>(typeof(PlayerController), "headTrigger",
                "the player standing up next to an NPC");
            private static readonly FieldInfo? FootTriggerField = Field<HeadTrigger>(typeof(PlayerController), "footTrigger",
                "the player pushing off an NPC in zero gravity");

            internal static HeadTrigger? GetHeadTrigger(PlayerController? controller) => Get<HeadTrigger>(HeadTriggerField, controller);
            internal static HeadTrigger? GetFootTrigger(PlayerController? controller) => Get<HeadTrigger>(FootTriggerField, controller);

            /// <summary>
            /// False after a game update broke the focus field; the window then falls back to its own
            /// aim test rather than reading null as "not busy".
            /// </summary>
            internal static bool CanReadFocus => FocusedInteractable != null;

            internal static Interactable? GetFocusedInteractable(PlayerController? controller) =>
                Get<Interactable>(FocusedInteractable, controller);
        }

        /// <summary>
        /// CameraAnimator: the player's footstep FMOD events, for a mod whose NPC sounds like the player.
        /// </summary>
        internal static class CameraAnimatorAccess
        {
            private static readonly FieldInfo? FootstepEvents = Field<EventReference[]>(typeof(CameraAnimator), "footstepEvents", "NPC footstep sounds");

            internal static EventReference[]? GetFootstepEvents(CameraAnimator? animator) => Get<EventReference[]>(FootstepEvents, animator);
        }

        /// <summary>
        /// FootstepDetector: which side of a station door means which floor sound.
        /// docs/game-model.md#footstep-sounds
        /// </summary>
        internal static class FootstepDetectorAccess
        {
            private const string Feature = "NPC floor sound at station doors";
            private static readonly FieldInfo? RotationReference = Field<Transform>(typeof(FootstepDetector), "rotationReference", Feature);
            private static readonly FieldInfo? ReverseSide = Field<bool>(typeof(FootstepDetector), "reverseSide", Feature);
            private static readonly FieldInfo? EnterFootstepsId = Field<int>(typeof(FootstepDetector), "enterFootstepsID", Feature);
            private static readonly FieldInfo? ExitFootstepsId = Field<int>(typeof(FootstepDetector), "exitFootstepsID", Feature);

            internal static Transform? GetRotationReference(FootstepDetector detector) => Get<Transform>(RotationReference, detector);

            internal static bool? GetReverseSide(FootstepDetector detector) => GetValue<bool>(ReverseSide, detector);

            internal static int? GetEnterFootstepsId(FootstepDetector detector) => GetValue<int>(EnterFootstepsId, detector);

            internal static int? GetExitFootstepsId(FootstepDetector detector) => GetValue<int>(ExitFootstepsId, detector);
        }

        /// <summary>
        /// HealthSystem: the player's death counter, traced beside an NPC's. docs/game-model.md#atmosphere-kills-by-the-players-rule
        /// </summary>
        internal static class HealthSystemAccess
        {
            private static readonly FieldInfo? DeathCounter = Field<byte>(typeof(HealthSystem), "deathCounter",
                "the player's death counter in the level-2 atmosphere trace");

            internal static byte? GetDeathCounter(HealthSystem? health) => GetValue<byte>(DeathCounter, health);
        }
    }
}
