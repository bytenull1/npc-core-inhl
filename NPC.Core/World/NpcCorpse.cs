using Space;
using UnityEngine;

namespace NPC.Core.World
{
    /// <summary>
    /// Makes a dead NPC's ragdoll carryable, the way the helper robot's is. Add it to the ragdoll's root
    /// once it is loose, then call <see cref="Init"/>. Deliberately not the game's Grabbable - that is a
    /// SaveObject and would write into the vanilla save. docs/invariants.md#mod-state-never-enters-the-vanilla-save
    /// </summary>
    public sealed class NpcCorpse : MonoBehaviour
    {
        /// <summary>
        /// Measured to the nearest point of the nearest limb, not to the center of mass: a sprawled
        /// ragdoll's center can be a metre from anything you can see.
        /// </summary>
        private const float LookAngle = 45f;
        private const float ReachFallback = 3f;
        /// <summary>
        /// Mirrors the game's own Grabbable (decompiled/Grabbable.cs:446): gravity off while held, heavy
        /// damping, velocity tracking the hold point. Why not a heavy spring: docs/reference.md#carrying-a-corpse---npccorpsecs
        /// </summary>
        private const float HoldDistance = 1.6f;
        /// <summary>
        /// Slightly below the eyeline, so it reads as carried rather than presented.
        /// </summary>
        private const float HoldDrop = 0.3f;
        private const float FollowSpeed = 50f;
        private const float CarryDrag = 10f;
        /// <summary>
        /// Let go rather than drag it through geometry or across the room.
        /// </summary>
        private const float SnapDistance = 1.8f;

        private INpc? owner;
        private Rigidbody? body;
        private Collider[]? parts;
        private InputHandler? subscribedTo;
        private bool carried;
        private bool restoreGravity;
        private float restoreDrag;
        private float restoreAngularDrag;

        /// <summary>
        /// `npc` is whose body this is, for the log; `ragdollBody` the rigidbody to carry by, else the
        /// first one found.
        /// </summary>
        public void Init(INpc npc, Rigidbody? ragdollBody)
        {
            owner = npc;
            body = ragdollBody != null ? ragdollBody : GetComponentInChildren<Rigidbody>(true);
            parts = GetComponentsInChildren<Collider>(true);
            if (body != null) return;

            using NpcRegistry.ActingScope _ = NpcRegistry.Acting(npc);
            NpcLog.Log.LogWarning("[mod] The corpse has no rigidbody - it cannot be carried.");
        }

        private void Update()
        {
            // InputHandler drops all listeners on teardown; a scene load brings a new one.
            InputHandler? handler = GameManager.Instance != null ? GameManager.Instance.InputHandler : null;
            if (handler == subscribedTo) return;

            if (subscribedTo != null) subscribedTo.OnGrab.RemoveListener(OnGrab);

            subscribedTo = handler;
            if (subscribedTo != null) subscribedTo.OnGrab.AddListener(OnGrab);
        }

        private void OnDestroy()
        {
            if (subscribedTo != null) subscribedTo.OnGrab.RemoveListener(OnGrab);
        }

        private void OnGrab(bool pressed)
        {
            // Null before Init: the NPC.Core source takes the line.
            using NpcRegistry.ActingScope _ = NpcRegistry.Acting(owner);
            if (!pressed)
            {
                if (carried) Release();
                return;
            }
            if (carried) return;
            if (body == null)
            {
                NpcLog.Log.LogWarning("[mod] Grab pressed but the corpse has no rigidbody.");
                return;
            }
            // Unity's == null, not a pattern: a destroyed camera transform must read as missing.
            Transform? cam = PlayerCamera();
            if (cam != null && LookingAtCorpse(cam))
            {
                Grab();
            }
        }

        private static Transform? PlayerCamera()
        {
            Player? player = NpcPlayer.Pilot;
            if (player == null || player.Controller == null) return null;

            return player.Controller.CameraAnimator != null
                ? player.Controller.CameraAnimator.CachedTransform
                : player.Controller.CachedTransform;
        }

        /// <summary>
        /// Only gates picking it up. Once carried the body hangs low and lags behind, so re-testing the
        /// cone would make it drop itself.
        /// </summary>
        private bool LookingAtCorpse(Transform camera)
        {
            float reach = Mathf.Max(
                SceneLoader.Instance != null ? SceneLoader.Instance.GlobalData.Settings.reachRange : ReachFallback,
                ReachFallback);

            float bestDist = float.MaxValue;
            float bestAngle = 180f;
            foreach (Collider part in parts ?? [])
            {
                if (part == null || !part.enabled) continue;

                Vector3 near = part.ClosestPoint(camera.position);
                Vector3 to = near - camera.position;
                float d = to.magnitude;
                if (d >= bestDist) continue;

                bestDist = d;
                bestAngle = d < 0.01f ? 0f : Vector3.Angle(camera.forward, to);
            }

            bool ok = bestDist <= reach && bestAngle <= LookAngle;
            if (!ok && NpcLog.Level >= 1)
            {
                NpcLog.Log.LogInfo(
                    $"[mod] Not picking up the body: nearest part {bestDist:0.00}m away at " +
                    $"{bestAngle:0}deg (need {reach:0.00}m / {LookAngle:0}deg)");
            }
            return ok;
        }

        private void Grab()
        {
            carried = true;
            // OnGrab returns before Grab when there is no body.
            restoreGravity = body!.useGravity;
            restoreDrag = body.drag;
            restoreAngularDrag = body.angularDrag;
            body.useGravity = false;
            body.drag = CarryDrag;
            body.angularDrag = CarryDrag;
            NpcLog.Log.LogInfo("[mod] Picked up the body");
        }

        private void Release()
        {
            carried = false;
            if (body == null) return;

            body.useGravity = restoreGravity;
            body.drag = restoreDrag;
            body.angularDrag = restoreAngularDrag;
            NpcLog.Log.LogInfo("[mod] Dropped the body");
        }

        private void FixedUpdate()
        {
            if (!carried) return;

            Transform? camera = PlayerCamera();
            if (body == null || camera == null)
            {
                using NpcRegistry.ActingScope _ = NpcRegistry.Acting(owner);
                Release();
                return;
            }

            Vector3 hold = camera.position + camera.forward * HoldDistance + Vector3.down * HoldDrop;
            Vector3 delta = hold - body.worldCenterOfMass;
            // Snagged on geometry, or you walked off without it: let go.
            if (delta.sqrMagnitude > SnapDistance * SnapDistance)
            {
                using NpcRegistry.ActingScope _ = NpcRegistry.Acting(owner);
                Release();
                return;
            }

            body.velocity = delta * FollowSpeed;
        }
    }
}
