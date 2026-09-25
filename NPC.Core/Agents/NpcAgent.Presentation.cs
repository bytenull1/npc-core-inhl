using System;
using System.Collections.Generic;
using FMOD.Studio;
using FMODUnity;
using NPC.Core.Navigation;
using NPC.Core.World;
using Space;
using UnityEngine;

namespace NPC.Core.Agents
{
    /// <summary>
    /// Audiovisual output: footstep sounds from the body, animation parameters and pooled debug visuals.
    /// </summary>
    public sealed partial class NpcAgent
    {
        // Parallel to anims. Animator.parameters allocates on every read, so which driven
        // parameters each controller declares is read once, not every frame.
        private AnimatorParams[]? animParams;
        private readonly struct AnimatorParams(RuntimeAnimatorController controller, bool resolved,
            bool hasSpeed, bool hasVelocity, bool hasIsWalking, bool hasIsGrounded)
        {
            public readonly RuntimeAnimatorController Controller = controller;
            /// <summary>
            /// False until the animator reported any parameter; an uninitialised one is asked again.
            /// </summary>
            public readonly bool Resolved = resolved;
            public readonly bool HasSpeed = hasSpeed;
            public readonly bool HasVelocity = hasVelocity;
            public readonly bool HasIsWalking = hasIsWalking;
            public readonly bool HasIsGrounded = hasIsGrounded;
        }
        private float currentAnimSpeed = 0f;

        // Indices into NpcBody.FootstepEvents: docs/game-model.md#footstep-sounds
        private const int ShipFootsteps = 0;
        private const int StationFootsteps = 1;
        private const float FootstepVolume = 0.5f;
        private const float FootstepFullVolumeDist = 2f;
        private const float FootstepSilentDist = 12f;
        // A FootstepDetector trigger is 2 m wide and 2 m tall at the station door.
        private const float FootstepDoorHalfWidth = 1.5f;
        private const float FootstepDoorHeight = 2.5f;
        private const float FootstepDetectorsTtl = 5f;

        private EventReference[]? footstepEvents;
        // Parallel to footstepEvents.
        private EventInstance[]? footstepInstances;
        // The vessel footstepOwnerDoor belongs to; looked up again when CurrentOwner changes.
        private string? footstepOwner;
        private FootstepDetector? footstepOwnerDoor;
        private int footstepIndex = ShipFootsteps;
        // Set by the first door crossing; from then on only crossings change footstepIndex.
        private bool footstepDoorCrossed;
        private FootstepDetector[] footstepDetectors = [];
        private float footstepDetectorsAt = float.NegativeInfinity;
        // Which side of each detector the feet were on last frame, as its local z.
        private readonly Dictionary<FootstepDetector, float> footstepDoorSide = [];
        private bool footstepListenerWarned;
        private float moveDelta = 0f;
        private bool stepPlayed = false;

        // Debug visualization
        private Transform? debugRoot;
        // Created together with debugRoot, and only read once it exists.
        private LineRenderer pathLine = null!;
        private LineRenderer probeLine = null!;
        private LineRenderer markerLine = null!;
        private LineRenderer jumpLine = null!;
        // Every line's points, refilled per line: the visuals run every frame.
        private static readonly List<Vector3> DebugPoints = [];

        /// <summary>
        /// One instance per floor sound, positioned at the feet on each step.
        /// </summary>
        private void InitializeFootsteps()
        {
            if (footstepEvents is not { Length: > 0 }) return;

            try
            {
                footstepInstances = new EventInstance[footstepEvents.Length];
                string kinds = "";
                for (int i = 0; i < footstepEvents.Length; i++)
                {
                    footstepInstances[i] = RuntimeManager.CreateInstance(footstepEvents[i]);
                    bool is3D = false;
                    if (footstepInstances[i].getDescription(out EventDescription description) == FMOD.RESULT.OK)
                    {
                        description.is3D(out is3D);
                    }
                    kinds += (i > 0 ? ", " : "") + "#" + i + (is3D ? " 3D" : " 2D");
                }
                NpcLog.Log.LogInfo("[ai] Footstep events: " + kinds);
            }
            catch (Exception ex)
            {
                NpcLog.Log.LogWarning($"[ai] Could not initialize footstep sounds: {ex.Message}");
            }
        }

        private void StopFootsteps()
        {
            if (footstepInstances == null) return;

            foreach (EventInstance step in footstepInstances)
            {
                if (step.isValid()) step.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
            }
        }

        private void ReleaseFootsteps()
        {
            StopFootsteps();
            if (footstepInstances == null) return;

            foreach (EventInstance step in footstepInstances)
            {
                if (step.isValid()) step.release();
            }
            footstepInstances = null;
        }

        /// <summary>
        /// Plays a footstep sound at the peak of the walk cycle (matches CameraAnimator logic).
        /// </summary>
        private void PlayFootstep()
        {
            if (!cc.isGrounded || currentAnimSpeed < 0.1f)
                return;

            float walkShakeSpeed = MoveSpeed * 5f;
            moveDelta += Time.deltaTime * walkShakeSpeed;

            if (moveDelta > 6.2831855f)
            {
                moveDelta = 0f;
                stepPlayed = false;
            }

            float walkOffsetX = Mathf.Sin(moveDelta);
            if (Mathf.Abs(walkOffsetX) < 0.95f || stepPlayed) return;

            stepPlayed = true;
            if (footstepInstances == null) return;

            EventInstance step = footstepInstances[Mathf.Clamp(FootstepIndex(), 0, footstepInstances.Length - 1)];
            if (!step.isValid()) return;

            Vector3 feet = GroundPos(0f);
            float volume = FootstepVolumeAt(feet);
            if (volume <= 0f) return;

            try
            {
                step.setVolume(volume);
                // A 3D event left unplaced sounds from the world origin, where the ship sits.
                step.set3DAttributes(feet.To3DAttributes());
                step.start();
            }
            catch (Exception ex)
            {
                NpcLog.Log.LogWarning($"[ai] Error playing footstep: {ex.Message}");
            }
        }

        /// <summary>
        /// The floor sound, as the player's: set by the last station door crossed. Before the
        /// first crossing, guessed from the side of the ridden station's door.
        /// docs/game-model.md#footstep-sounds
        /// </summary>
        private int FootstepIndex()
        {
            if (footstepDoorCrossed) return footstepIndex;

            if (CurrentOwner != footstepOwner)
            {
                footstepOwner = CurrentOwner;
                Transform? interior = CurrentOwner is null or NavGraph.ShipOwner or NavGraph.WorldOwner
                    ? null
                    : NpcVessels.AnchorForOwner(CurrentOwner);
                footstepOwnerDoor = interior != null ? interior.GetComponentInChildren<FootstepDetector>(true) : null;
            }

            int guess = footstepOwnerDoor != null ? DoorSideFootsteps(footstepOwnerDoor, GroundPos(0f)) : ShipFootsteps;
            if (guess == footstepIndex) return footstepIndex;

            footstepIndex = guess;
            NpcLog.Log.LogInfo("[ai] Footsteps: " + FootstepName(guess) + " floor on '" +
                               (CurrentOwner ?? "nothing") + "', no door crossed yet");
            return footstepIndex;
        }

        /// <summary>
        /// Watches the feet pass through a FootstepDetector doorway, the NPC's stand-in for
        /// the trigger that only sees the player.
        /// </summary>
        private void TrackFootstepDoors()
        {
            if (Time.time >= footstepDetectorsAt + FootstepDetectorsTtl &&
                SceneScan.MayRescan(float.IsNegativeInfinity(footstepDetectorsAt)))
            {
                footstepDetectors = FindObjectsOfType<FootstepDetector>();
                footstepDetectorsAt = Time.time;
            }

            Vector3 feet = GroundPos(0f);
            foreach (FootstepDetector detector in footstepDetectors)
            {
                if (detector == null) continue;

                Vector3 local = detector.transform.InverseTransformPoint(feet);
                bool seen = footstepDoorSide.TryGetValue(detector, out float lastZ);
                footstepDoorSide[detector] = local.z;
                if (!seen || (local.z > 0f) == (lastZ > 0f)) continue;

                if (Mathf.Abs(local.x) > FootstepDoorHalfWidth || local.y < -0.5f || local.y > FootstepDoorHeight) continue;

                CrossFootstepDoor(detector, feet);
            }
        }

        private void CrossFootstepDoor(FootstepDetector detector, Vector3 feet)
        {
            int index = DoorSideFootsteps(detector, feet);
            footstepDoorCrossed = true;
            if (index == footstepIndex) return;

            footstepIndex = index;
            NpcLog.Log.LogInfo("[ai] Footsteps: " + FootstepName(index) + " floor, crossed the door of '" +
                               (NpcVessels.OwnerOfTransform(detector.transform) ?? detector.name) + "'");
        }

        /// <summary>
        /// FootstepDetector.RequestCheckForEnter, for the NPC's feet.
        /// </summary>
        private static int DoorSideFootsteps(FootstepDetector detector, Vector3 feet)
        {
            Transform? reference = GameInternals.FootstepDetectorAccess.GetRotationReference(detector);
            if (reference == null) reference = detector.transform;

            float z = reference.InverseTransformPoint(feet).z;
            bool enter = GameInternals.FootstepDetectorAccess.GetReverseSide(detector) == true ? z < 0f : z > 0f;
            int? id = enter
                ? GameInternals.FootstepDetectorAccess.GetEnterFootstepsId(detector)
                : GameInternals.FootstepDetectorAccess.GetExitFootstepsId(detector);
            return id.GetValueOrDefault(enter ? StationFootsteps : ShipFootsteps);
        }

        private static string FootstepName(int index) => index == StationFootsteps ? "station" : "ship";

        /// <summary>
        /// Full volume near the listener, fading to silence at FootstepSilentDist.
        /// </summary>
        private float FootstepVolumeAt(Vector3 feet)
        {
            Vector3 listener;
            if (RuntimeManager.StudioSystem.getListenerAttributes(0, out FMOD.ATTRIBUTES_3D attributes) == FMOD.RESULT.OK)
            {
                listener = new Vector3(attributes.position.x, attributes.position.y, attributes.position.z);
            }
            else if (Camera.main != null)
            {
                listener = Camera.main.transform.position;
            }
            else
            {
                if (!footstepListenerWarned) NpcLog.Log.LogWarning("[ai] Footsteps: no listener, playing at full volume");
                footstepListenerWarned = true;
                return FootstepVolume;
            }

            float t = Mathf.InverseLerp(FootstepFullVolumeDist, FootstepSilentDist, Vector3.Distance(listener, feet));
            return FootstepVolume * (1f - t) * (1f - t);
        }


        private static readonly int IsWalking = Animator.StringToHash("isWalking");
        private static readonly int IsGrounded = Animator.StringToHash("isGrounded");
        private static readonly int Speed = Animator.StringToHash("Speed");
        private static readonly int Velocity = Animator.StringToHash("Velocity");

        /// <summary>
        /// Which of the driven parameters an animator declares, with their expected types.
        /// </summary>
        private static AnimatorParams ReadAnimatorParams(Animator a)
        {
            bool hasSpeed = false, hasVelocity = false, hasIsWalking = false, hasIsGrounded = false;
            AnimatorControllerParameter[] declared = a.parameters;
            foreach (AnimatorControllerParameter p in declared)
            {
                switch (p.name)
                {
                    case "Speed": hasSpeed = p.type == AnimatorControllerParameterType.Float; break;
                    case "Velocity": hasVelocity = p.type == AnimatorControllerParameterType.Float; break;
                    case "isWalking": hasIsWalking = p.type == AnimatorControllerParameterType.Bool; break;
                    case "isGrounded": hasIsGrounded = p.type == AnimatorControllerParameterType.Bool; break;
                }
            }
            return new AnimatorParams(a.runtimeAnimatorController, declared.Length > 0,
                hasSpeed, hasVelocity, hasIsWalking, hasIsGrounded);
        }


        /// <summary>
        /// Turns to face the player. Cosmetic only - movement never reads rotation.
        /// </summary>
        public void FacePlayer(Player player)
        {
            if (player == null || player.Controller == null) return;

            FacePoint(player.Controller.CachedTransform.position);
        }

        public void FacePoint(Vector3 point)
        {
            Vector3 toPoint = point - transform.position;
            toPoint.y = 0f;
            if (toPoint.sqrMagnitude <= 0.01f) return;

            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.LookRotation(toPoint.normalized), Time.deltaTime * 5f);
        }

        private void UpdateAnimation(Vector3 desired, bool wantMove, Player player)
        {
            bool isMoving = wantMove && desired.sqrMagnitude > 0.001f;

            if (isMoving)
            {
                Vector3 lookDir = new(desired.x, 0f, desired.z);
                if (lookDir.sqrMagnitude > 0.001f)
                {
                    Quaternion targetRotation = Quaternion.LookRotation(lookDir.normalized);
                    transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * 5f);
                }
            }
            else
            {
                brain.TryIdleFacing(player);
            }

            float targetAnimSpeed = isMoving ? 1f : 0f;
            currentAnimSpeed = Mathf.Lerp(currentAnimSpeed, targetAnimSpeed, Time.deltaTime * 5f);

            if (anims is { Length: > 0 })
            {
                if (animParams == null || animParams.Length != anims.Length)
                {
                    animParams = new AnimatorParams[anims.Length];
                }

                for (int i = 0; i < anims.Length; i++)
                {
                    Animator a = anims[i];
                    if (!a.gameObject.activeInHierarchy) continue;

                    AnimatorParams p = animParams[i];
                    if (!p.Resolved || p.Controller != a.runtimeAnimatorController)
                    {
                        p = ReadAnimatorParams(a);
                        animParams[i] = p;
                    }

                    if (p.HasSpeed) a.SetFloat(Speed, currentAnimSpeed);

                    if (p.HasVelocity) a.SetFloat(Velocity, currentAnimSpeed);

                    if (p.HasIsWalking) a.SetBool(IsWalking, currentAnimSpeed > 0.1f);

                    if (p.HasIsGrounded) a.SetBool(IsGrounded, cc.isGrounded);
                }
            }

            TrackFootstepDoors();
            if (isMoving && cc.isGrounded) PlayFootstep();

            wasMoving = isMoving;
        }


        // ------------------------------------------------------------------
        // Debug visualization
        // ------------------------------------------------------------------

        public void EnsureDebugVisuals(bool enableDebugVisuals)
        {
            if (enableDebugVisuals)
            {
                if (debugRoot == null)
                {
                    debugRoot = new GameObject("NpcAgentDebug").transform;
                    debugRoot.SetParent(transform, false);
                    pathLine = CreateDebugLine("Path", new Color(1f, 0.85f, 0.2f));
                    probeLine = CreateDebugLine("Probe", new Color(0.3f, 1f, 0.4f));
                    markerLine = CreateDebugLine("Marker", new Color(0.3f, 0.8f, 1f));
                    jumpLine = CreateDebugLine("JumpProbe", new Color(1f, 0.5f, 0f));
                }
                debugRoot.gameObject.SetActive(true);
            }
            else
            {
                HideDebugVisuals();
            }
        }

        private LineRenderer CreateDebugLine(string lineName, Color color)
        {
            GameObject lineGo = new(lineName);
            lineGo.transform.SetParent(debugRoot, false);
            LineRenderer line = lineGo.AddComponent<LineRenderer>();
            Shader? shader = Shader.Find("Sprites/Default");
            if (shader != null) line.material = new Material(shader);
            line.startWidth = 0.03f;
            line.endWidth = 0.03f;
            line.startColor = color;
            line.endColor = color;
            line.positionCount = 0;
            line.useWorldSpace = true;
            return line;
        }

        private void HideDebugVisuals()
        {
            if (debugRoot != null) debugRoot.gameObject.SetActive(false);
        }

        private void UpdateDebugVisuals()
        {
            if (debugRoot == null || !debugRoot.gameObject.activeInHierarchy) return;

            // Path: NPC -> current target -> upcoming NodeGraph path corners.
            List<Vector3> points = DebugPoints;
            points.Clear();
            points.Add(GroundPos(0.15f));
            if (hasMoveTarget) points.Add(currentMoveTarget + Vector3.up * 0.15f);

            if (navPlan != null) for (int i = navPathIndex; i < navPlan.Value.Count && i < navPathIndex + 10; i++) points.Add(navPlan.Value[i] + Vector3.up * 0.15f);
            SetLinePositions(pathLine, points);

            // Obstacle probe.
            Vector3 probeOrigin = GroundPos(0.5f);
            Vector3 probeDir = new(lastAvoidDirection.x, 0f, lastAvoidDirection.z);
            if (probeDir.sqrMagnitude < 0.001f) probeDir = transform.forward;

            bool blocked = BodyBlocked(probeDir.normalized, 0.9f);
            points.Clear();
            points.Add(probeOrigin);
            points.Add(probeOrigin + probeDir.normalized * 0.9f);
            SetLinePositions(probeLine, points);
            Color probeColor = blocked ? new Color(1f, 0.25f, 0.25f) : new Color(0.3f, 1f, 0.4f);
            probeLine.startColor = probeColor;
            probeLine.endColor = probeColor;

            // Target marker (diamond around the move target).
            points.Clear();
            if (hasMoveTarget)
            {
                Vector3 t = currentMoveTarget + Vector3.up * 0.1f;
                points.Add(t + Vector3.right * 0.2f);
                points.Add(t + Vector3.forward * 0.2f);
                points.Add(t + Vector3.left * 0.2f);
                points.Add(t + Vector3.back * 0.2f);
                points.Add(t + Vector3.right * 0.2f);
            }
            SetLinePositions(markerLine, points);


            // Shin-level jump probe.
            points.Clear();
            Vector3 jumpDir = currentMoveTarget - transform.position;
            jumpDir.y = 0f;
            if (hasMoveTarget && cc != null && cc.isGrounded && jumpDir.sqrMagnitude > 0.001f)
            {
                jumpDir.Normalize();
                Vector3 shinOrigin = GroundPos(0.25f);
                bool jumpBlocked = Physics.Raycast(shinOrigin, jumpDir, 0.7f, ProbeLayers, QueryTriggerInteraction.Ignore);
                points.Add(shinOrigin);
                points.Add(shinOrigin + jumpDir * 0.7f);
                Color jumpColor = jumpBlocked ? new Color(1f, 0.45f, 0f) : new Color(1f, 0.85f, 0.6f);
                jumpLine.startColor = jumpColor;
                jumpLine.endColor = jumpColor;
            }
            SetLinePositions(jumpLine, points);
        }

        private static void SetLinePositions(LineRenderer line, List<Vector3> positions)
        {
            if (line == null) return;

            line.positionCount = positions.Count;
            for (int i = 0; i < positions.Count; i++)
            {
                line.SetPosition(i, positions[i]);
            }
        }
    }
}
