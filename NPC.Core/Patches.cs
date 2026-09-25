using System;
using System.Collections.Generic;
using System.ComponentModel;
using HarmonyLib;
using NPC.Core.Agents;
using NPC.Core.Interaction;
using NPC.Core.Navigation;
using NPC.Core.Saves;
using NPC.Core.World;
using Space;
using Space.Data;
using UnityEngine;
// ReSharper disable InconsistentNaming - this breaks Harmony patches.

namespace NPC.Core
{
    /// <summary>
    /// Every Harmony patch NPC.Core applies, one nested class per feature, so a game update that breaks
    /// one target disables only that feature (NpcCorePlugin.ApplyPatches). Each game method is patched
    /// here once for every NPC mod: docs/invariants.md#one-patch-per-game-hook
    /// </summary>
    internal static class Patches
    {
        [HarmonyPatch, Description("the shared tick: nav-graph autosave, registry upkeep, body collision pairs, NpcEvents.Tick")]
        internal static class Tick
        {
            /// <summary>
            /// Runs exactly while a game scene is loaded.
            /// </summary>
            [HarmonyPatch(typeof(GameManager), "FixedUpdate")]
            [HarmonyPostfix]
            public static void GameManager_FixedUpdate_Postfix()
            {
                NpcRegistry.PruneDestroyed();
                NavGraph.TickSave();
                CoreSidecar.Tick();
                // The game re-enables the monster on its own schedule, so an override is re-asserted.
                if (NpcMonster.Any) NpcMonster.Apply();
                NpcLifecare.Tick();
                NpcRegistry.KeepBodiesApart();
                NpcTalkWindow.Ensure();
                NpcCorePlugin.EnsureNodeEditor();
                NpcCorePlugin.CheckForMergedCopies();
                NpcEvents.RaiseTick();
            }
        }

        [HarmonyPatch, Description("NpcEvents.GameStarting and the scene reset on a new game")]
        internal static class GameStart
        {
            /// <summary>
            /// Before GameManager.Start puts the player in a cryo pod and advances worldTime, which is
            /// the game's own new-game test.
            /// </summary>
            [HarmonyPatch(typeof(GameManager), "Start")]
            [HarmonyPrefix]
            public static void GameManager_Start_Prefix()
            {
                SaveData? save = SceneLoader.Instance != null ? SceneLoader.Instance.SaveData : null;
                ResetScene();
                NpcEvents.RaiseGameStarting(save is { worldTime: 0 });
            }
        }

        /// <summary>
        /// A new scene: every shared scene cache refills on first use.
        /// </summary>
        private static void ResetScene()
        {
            NpcDoors.ResetScene();
            NpcMonster.ResetScene();
            NpcRegistry.ResetScene();
            NpcEvents.RaiseWorldReset();
        }

        [HarmonyPatch, Description("scene caches on a save load, NpcEvents.SaveLoaded")]
        internal static class Load
        {
            /// <summary>
            /// New scene: the 5 s gate caches may reference destroyed gates, and the debug overrides
            /// belong to the last scene's monster.
            /// </summary>
            [HarmonyPatch(typeof(SceneLoader), "LoadGame")]
            [HarmonyPostfix]
            public static void SceneLoader_LoadGame_Postfix(string saveName)
            {
                NavProbe.InvalidateGates();
                NpcMonster.ResetOverrides();
                ResetScene();
                NpcLog.Core.LogInfo($"[save] LoadGame('{saveName}') detected");
                if (SceneLoader.Instance == null) return;

                SaveData? data = SceneLoader.Instance.SaveData;
                if (data == null)
                {
                    NpcLog.Core.LogWarning("[save] SaveData is null after load - save failed to load");
                    return;
                }
                if (data.fileName != saveName)
                {
                    NpcLog.Core.LogWarning($"[save] Save name mismatch: requested '{saveName}', data holds '{data.fileName}'");
                    return;
                }
                NpcEvents.RaiseSaveLoaded(saveName);
            }
        }

        [HarmonyPatch, Description("console commands of every NPC mod")]
        internal static class ConsoleCommands
        {
            [HarmonyPatch(typeof(ConsoleMenu), "Init")]
            [HarmonyPostfix]
            public static void ConsoleMenu_Init_Postfix(ConsoleMenu __instance)
            {
                if (__instance != null) NpcConsole.Attach(__instance);
            }
        }

        [HarmonyPatch, Description("door closes by NPCs: the player's room and blocked-close retries")]
        internal static class Doors
        {
            /// <summary>
            /// EntryDetector re-files the player whenever a door it knows closes, with no proximity
            /// check. For a close an NPC issued elsewhere, hide the remembered player.
            /// docs/invariants.md#npc-closes-must-not-move-the-player
            /// </summary>
            [HarmonyPatch(typeof(EntryDetector), "DoorCheckForEnter")]
            [HarmonyPrefix]
            public static void EntryDetector_DoorCheckForEnter_Prefix(EntryDetector __instance, out Player? __state)
            {
                __state = null;

                Gate? gate = GameInternals.EntryDetectorAccess.GetDoor(__instance);
                if (gate == null || !NpcDoors.WasClosedByNpc(gate, out _)) return;

                Player? player = GameInternals.EntryDetectorAccess.GetCurrentPlayer(__instance);
                if (player == null || player.Controller == null) return;

                // The player really is at this doorway: let the game do its normal thing.
                if (NpcDoors.PlayerAtDoorway(player, gate)) return;

                if (GameInternals.EntryDetectorAccess.SetCurrentPlayer(__instance, null)) __state = player;
            }

            [HarmonyPatch(typeof(EntryDetector), "DoorCheckForEnter")]
            [HarmonyPostfix]
            public static void EntryDetector_DoorCheckForEnter_Postfix(EntryDetector __instance, Player? __state)
            {
                if (__state != null) GameInternals.EntryDetectorAccess.SetCurrentPlayer(__instance, __state);

                Gate? gate = GameInternals.EntryDetectorAccess.GetDoor(__instance);
                if (gate == null || !NpcDoors.WasClosedByNpc(gate, out INpc? closer)) return;

                // At the doorway the game has just switched rooms for the player. Anywhere else it switched
                // nothing, so the closer puts back what it loaded: docs/invariants.md#npc-closes-must-not-move-the-player
                Player? player = GameInternals.EntryDetectorAccess.GetCurrentPlayer(__instance);
                if (player != null && player.Controller != null && NpcDoors.PlayerAtDoorway(player, gate)) return;

                // The closer's own side test decides; every other NPC keeps its room through TracksRoom.
                if (closer is not INpcDoorUser user) return;

                using NpcRegistry.ActingScope _ = NpcRegistry.Acting(closer);
                user.OnDoorClosedAway(__instance);
            }

            /// <summary>
            /// A gate's AntiCrasher just undid a close, and nothing in the game will ever retry it. The NPC
            /// standing in it owns the fix: docs/doors.md#4-closes-an-npc-blocked
            /// </summary>
            [HarmonyPatch(typeof(Gate), nameof(Gate.FailClose))]
            [HarmonyPostfix]
            public static void Gate_FailClose_Postfix(Gate __instance)
            {
                // Each ignores a gate it is not standing in, so only the one in the way takes the close over.
                foreach (INpc npc in NpcRegistry.Snapshot())
                {
                    if (npc is not INpcDoorUser user || NpcRegistry.IsGone(npc)) continue;

                    using NpcRegistry.ActingScope _ = NpcRegistry.Acting(npc);
                    user.OnCloseFailed(__instance);
                }
            }
        }

        [HarmonyPatch, Description("rooms the mods keep loaded")]
        internal static class KeptRooms
        {
            /// <summary>
            /// No one switches off a room a keeper holds, the game's own doorway, dock and airlock unloads
            /// included. docs/invariants.md#a-kept-room-stays-loaded
            /// </summary>
            [HarmonyPatch(typeof(Room), nameof(Room.SetContentEnabled))]
            [HarmonyPrefix]
            public static bool Room_SetContentEnabled_Prefix(Room __instance, bool state) =>
                NpcRooms.AllowSwitch(__instance, state);
        }

        [HarmonyPatch, Description("every NPC mod's save sidecars")]
        internal static class Saves
        {
            [HarmonyPatch(typeof(SaveParser), "WriteSaveFile")]
            [HarmonyPostfix]
            public static void SaveParser_WriteSaveFile_Postfix(SaveData? save)
            {
                if (save != null) NpcSaves.WriteAll(save.fileName);
            }

            /// <summary>
            /// The only single-save delete path, and it runs after the player confirms - the call site is
            /// inside ConfirmationPopUp's callback, so patching SaveMenu instead would delete on cancel too.
            /// </summary>
            [HarmonyPatch(typeof(SaveParser), nameof(SaveParser.DeleteSaveFile))]
            [HarmonyPostfix]
            public static void SaveParser_DeleteSaveFile_Postfix(string fileName) => NpcSaves.DeleteAll(fileName);

            /// <summary>
            /// The bulk wipe. Scene-wired with no C# caller, like Gate.InvokeOpen.
            /// </summary>
            [HarmonyPatch(typeof(SaveParser), nameof(SaveParser.ClearSaveFiles))]
            [HarmonyPostfix]
            public static void SaveParser_ClearSaveFiles_Postfix() => NpcSaves.ClearAll();

            /// <summary>
            /// Self-healing sweep for sidecars whose save vanished by some other route. Runs on menu open
            /// and after every write, and touches nothing else.
            /// </summary>
            [HarmonyPatch(typeof(SaveParser), nameof(SaveParser.SyncSavePreviews))]
            [HarmonyPostfix]
            public static void SaveParser_SyncSavePreviews_Postfix() => NpcSaves.PruneOrphans();
        }

        [HarmonyPatch, Description("the ai_notarget and ai_disable debug commands")]
        internal static class MonsterDebug
        {
            /// <summary>
            /// ai_notarget: the monster's idle AI starts its chase routine from this callback, so
            /// suppressing it is half of "it does not see me".
            /// </summary>
            [HarmonyPatch(typeof(BreathlessController), "PlayerEntered")]
            [HarmonyPrefix]
            public static bool BreathlessController_PlayerEntered_Prefix() => !NpcMonster.PlayerIgnored;

            /// <summary>
            /// The other half, and the one that actually kills. Both AI components grab and kill from an
            /// ObjectDetector UnityEvent, which fires whether or not the component listening to it is
            /// enabled. Only a player detection is suppressed: the item branch still works, and an NPC never
            /// reaches here (it has no Player component), which is what keeps it huntable under ai_notarget.
            /// </summary>
            [HarmonyPatch(typeof(BreathlessController), "DetectItem")]
            [HarmonyPrefix]
            public static bool BreathlessController_DetectItem_Prefix(GameObject detectedObject) => !IgnoredTarget(detectedObject);

            [HarmonyPatch(typeof(BreathlessAggressor), "DetectItem")]
            [HarmonyPrefix]
            public static bool BreathlessAggressor_DetectItem_Prefix(GameObject detectedObject) => !IgnoredTarget(detectedObject);

            private static bool IgnoredTarget(GameObject detectedObject) =>
                NpcMonster.PlayerIgnored && detectedObject != null && detectedObject.GetComponent<Player>() != null;
        }

        [HarmonyPatch, Description("the lifecare terminal counting NPCs")]
        internal static class Lifecare
        {
            [HarmonyPatch(typeof(LifecareDisplay), nameof(LifecareDisplay.UpdatePlayerIcon))]
            [HarmonyPrefix]
            public static void LifecareDisplay_UpdatePlayerIcon_Prefix(ref bool enabled) => NpcLifecare.FixPlayerIcon(ref enabled);

            [HarmonyPatch(typeof(LifecareDisplay), "UpdateLifeformsCount")]
            [HarmonyPostfix]
            public static void LifecareDisplay_UpdateLifeformsCount_Postfix(LifecareDisplay __instance) =>
                NpcLifecare.RecountLifeforms(__instance);
        }

        [HarmonyPatch, Description("a hiding spot an NPC is inside")]
        internal static class Hiding
        {
            /// <summary>
            /// An NPC is not a Player, so the game would happily mount you into the closet it is already in.
            /// </summary>
            [HarmonyPatch(typeof(HidingSpot), nameof(HidingSpot.Interact))]
            [HarmonyPrefix]
            public static bool HidingSpot_Interact_Prefix(HidingSpot __instance)
            {
                if (!NpcHiding.OccupiedByNpc(__instance)) return true;

                if (MessagePopUp.Instance != null) MessagePopUp.Instance.Show("Occupied");

                return false;
            }
        }

        [HarmonyPatch, Description("NPCs undocking with the ship")]
        internal static class Docking
        {
            /// <summary>
            /// The agents in the undocking collar, judged before the station's content (and every
            /// collider in it) is switched off. Null when nothing was docked: Undock is then a no-op, and
            /// moving an NPC for it would be a teleport out of nowhere.
            /// </summary>
            [HarmonyPatch(typeof(Docker), nameof(Docker.Undock))]
            [HarmonyPrefix]
            public static void Docker_Undock_Prefix(Docker __instance, out List<NpcAgent>? __state)
            {
                __state = null;
                if (__instance == null || !__instance.IsDocked) return;

                __state = [];
                foreach (INpc npc in NpcRegistry.All)
                {
                    if (NpcRegistry.IsGone(npc) || npc is not NpcAgent agent) continue;

                    if (agent.gameObject.activeInHierarchy &&
                        NavProbe.TryFloorCollider(agent.transform.position, out Collider? floor) && floor != null &&
                        floor.GetComponentInParent<Docker>() == __instance)
                    {
                        __state.Add(agent);
                    }
                }
            }

            /// <summary>
            /// An agent with no floor of its own, or in the collar, goes aboard as the game's own NPC
            /// does (Docker.Undock); one elsewhere on the station is parked with it.
            /// docs/invariants.md#an-npc-rides-its-own-floor
            /// </summary>
            [HarmonyPatch(typeof(Docker), nameof(Docker.Undock))]
            [HarmonyPostfix]
            public static void Docker_Undock_Postfix(SpaceShip ship, List<NpcAgent>? __state)
            {
                if (__state == null || ship == null || ship.Airlock == null) return;

                foreach (INpc npc in NpcRegistry.Snapshot())
                {
                    if (NpcRegistry.IsGone(npc) || npc is not NpcAgent agent || agent.IsDead) continue;

                    if (!__state.Contains(agent))
                    {
                        if (!agent.gameObject.activeInHierarchy) continue;

                        NpcVessels.FloorOwner(agent.transform.position, out string? owner, out _);
                        if (owner != null) continue;
                    }
                    using NpcRegistry.ActingScope scope = NpcRegistry.Acting(agent);
                    agent.PullAboard(ship.Airlock.transform.position);
                }
            }
        }

        [HarmonyPatch, Description("NPCs riding a ship rebuild")]
        internal static class ShipRebuild
        {
            /// <summary>
            /// Where an agent aboard stood before the player ship is rebuilt, judged while every room
            /// is still where it was. Room is null on a floor of no room, or with no floor at all.
            /// </summary>
            public struct ShipRebuildState
            {
                public NpcAgent Agent;
                public int LayoutBefore;
                public bool HadFloor;
                public CustomRoom? Room;
                public Vector3 RoomAt;
            }

            /// <summary>
            /// SetShipLevel switches every room off and moves some; an NPC aboard rides the scene root,
            /// so its room is found here, before ClearLevel. docs/invariants.md#an-npc-rides-its-own-floor
            /// </summary>
            [HarmonyPatch(typeof(StorymodeShipBuilder), nameof(StorymodeShipBuilder.SetShipLevel))]
            [HarmonyPrefix]
            public static void StorymodeShipBuilder_SetShipLevel_Prefix(StorymodeShipBuilder __instance,
                out List<ShipRebuildState>? __state)
            {
                __state = null;
                SpaceShip? ship = GameManager.Instance != null ? GameManager.Instance.PlayerShip : null;
                if (ship == null || __instance == null || __instance != ship.StorymodeShipBuilder) return;

                int layoutBefore = NavGraph.ShipLayoutSignature();
                foreach (INpc npc in NpcRegistry.All)
                {
                    if (NpcRegistry.IsGone(npc) || npc is not NpcAgent agent || agent.IsDead ||
                        !agent.gameObject.activeInHierarchy || !agent.IsAboardPlayerShip())
                    {
                        continue;
                    }

                    ShipRebuildState state = new() { Agent = agent, LayoutBefore = layoutBefore };
                    if (NavProbe.TryFloorCollider(agent.transform.position, out Collider? floor) && floor != null)
                    {
                        state.HadFloor = true;
                        CustomRoom room = floor.GetComponentInParent<CustomRoom>();
                        if (room != null && Array.IndexOf(ship.Rooms, room) >= 0)
                        {
                            state.Room = room;
                            state.RoomAt = ship.transform.InverseTransformPoint(room.transform.position);
                        }
                    }
                    __state ??= [];
                    __state.Add(state);
                }
            }

            /// <summary>
            /// An agent in a room still built moves with it; one whose room is gone is left to the space
            /// protection. Either way its plan and safe spot belong to the old layout.
            /// </summary>
            [HarmonyPatch(typeof(StorymodeShipBuilder), nameof(StorymodeShipBuilder.SetShipLevel))]
            [HarmonyPostfix]
            public static void StorymodeShipBuilder_SetShipLevel_Postfix(byte level, List<ShipRebuildState>? __state)
            {
                if (__state == null) return;

                SpaceShip? ship = GameManager.Instance != null ? GameManager.Instance.PlayerShip : null;
                if (ship == null) return;

                if (NavGraph.ShipLayoutSignature() == __state[0].LayoutBefore)
                {
                    if (NpcLog.Level >= 1)
                    {
                        NpcLog.Log.LogInfo($"[ai] Ship rebuilt (stage {level}), layout unchanged - NPCs left as they were");
                    }
                    return;
                }

                foreach (ShipRebuildState state in __state)
                {
                    if (state.Agent == null || state.Agent.IsDead) continue;

                    using NpcRegistry.ActingScope _ = NpcRegistry.Acting(state.Agent);
                    RideRebuild(state, ship, level);
                }
            }

            private static void RideRebuild(ShipRebuildState state, SpaceShip ship, byte level)
            {
                NpcAgent agent = state.Agent;
                CustomRoom? room = state.Room;
                if (room == null)
                {
                    agent.RideShipRebuild(level, Vector3.zero, state.HadFloor,
                        state.HadFloor ? "on a floor of no room, which does not move" : "no floor under it to judge by");
                }
                else if (!room.EnabledStructure)
                {
                    agent.RideShipRebuild(level, Vector3.zero, false,
                        $"'{room.gameObject.name}' is not built any more - left to the space protection");
                }
                else
                {
                    Vector3 shift = ship.transform.InverseTransformPoint(room.transform.position) - state.RoomAt;
                    string what = shift.sqrMagnitude < 0.0001f
                        ? $"'{room.gameObject.name}' did not move"
                        : $"moved with '{room.gameObject.name}' by {shift:0.00}";
                    agent.RideShipRebuild(level, ship.transform.TransformVector(shift), true, what);
                }
            }
        }

        [HarmonyPatch, Description("parking NPCs with an unloaded ship")]
        internal static class ShipContent
        {
            /// <summary>
            /// Airlock.Exit unloads the player's ship and Airlock.Enter reloads it; an agent
            /// aboard is parked in between. Prefix: before the rooms fire OnContentStateChanged.
            /// docs/invariants.md#an-unloaded-ship-parks-the-npc
            /// </summary>
            [HarmonyPatch(typeof(SpaceShip), nameof(SpaceShip.SetContentEnabled))]
            [HarmonyPrefix]
            public static void SpaceShip_SetContentEnabled_Prefix(SpaceShip __instance, bool value)
            {
                GameManager gm = GameManager.Instance;
                if (gm == null || __instance != gm.PlayerShip) return;

                foreach (INpc npc in NpcRegistry.Snapshot())
                {
                    if (NpcRegistry.IsGone(npc) || npc is not NpcAgent agent) continue;

                    using NpcRegistry.ActingScope _ = NpcRegistry.Acting(agent);
                    if (value) agent.UnparkFromShip();
                    else agent.ParkWithShip();
                }
            }
        }
    }
}
