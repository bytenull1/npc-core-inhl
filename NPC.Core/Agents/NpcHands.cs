using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NPC.Core.Agents
{
    /// <summary>
    /// An NPC's hands: one item at most, frozen like the game's Crate.ParentItems, but moved to the hold point
    /// every frame instead of parented, so it never leaves its room's content. docs/agent.md#7-hands
    /// </summary>
    public sealed class NpcHands(MonoBehaviour body, Collider? blocker, Func<float, Vector3> groundPos, Func<Room?> room)
    {
        /// <summary>
        /// Waist height above the feet, in front: where a held item's collider centre sits.
        /// </summary>
        private const float HoldHeight = 0.95f;
        private const float HoldForward = 0.45f;
        /// <summary>
        /// A big item's centre is at least its flat half-size plus this ahead of the NPC's centre.
        /// </summary>
        private const float HoldBodyClearance = 0.3f;
        private const float HoldDropHeight = 0.5f;
        /// <summary>
        /// On top of the walking speed; further than HoldSnapDist it jumps (a teleport, a rebuild).
        /// </summary>
        private const float HoldMoveSpeed = 1.5f;
        private const float HoldSnapDist = 2f;
        /// <summary>
        /// A put-down item and the NPC's ItemBlocker ignore each other this long, so it does not bounce off its carrier.
        /// </summary>
        private const float PutDownIgnoreSeconds = 1f;

        private readonly List<Collider> colliders = [];
        private Quaternion rotation = Quaternion.identity;
        /// <summary>
        /// The item's collider centre, in its own space: that is what sits on the hold point.
        /// </summary>
        private Vector3 centre = Vector3.zero;
        /// <summary>
        /// Where the item is moving to instead of the hands: a trash can's slot. Null in the hands.
        /// </summary>
        private Vector3? reachTo;
        private float reachSpeed;

        /// <summary>
        /// The item in the hands, frozen and without colliders; null when empty-handed.
        /// </summary>
        public Grabbable? Item { get; private set; }
        /// <summary>
        /// The item's collider half-size, world axes, as measured when it was picked up.
        /// </summary>
        public Vector3 Extents { get; private set; }
        /// <summary>
        /// How far ahead of the body the item is carried: further for a big one.
        /// </summary>
        public float Forward { get; private set; } = HoldForward;

        private Transform Body => body.transform;

        /// <summary>
        /// Measured from the feet: the transform origin sits at the capsule's centre.
        /// </summary>
        public Vector3 Point => groundPos(HoldHeight) + Body.forward * Forward;

        /// <summary>
        /// The bounds of every enabled solid collider under `root`; false when it has none.
        /// </summary>
        public static bool ColliderBounds(GameObject root, out Bounds bounds)
        {
            bool found = false;
            bounds = default;
            foreach (Collider collider in root.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || collider.isTrigger) continue;

                if (found) bounds.Encapsulate(collider.bounds);
                else bounds = collider.bounds;
                found = true;
            }
            return found;
        }

        /// <summary>
        /// False, and nothing changes, when the item is in someone's hands or already held.
        /// </summary>
        public bool PickUp(Grabbable item)
        {
            if (Item != null || item == null || item.IsGrabbed || item.restrictGrab || !item.gameObject.activeInHierarchy) return false;

            bool measured = ColliderBounds(item.gameObject, out Bounds bounds);
            centre = measured ? item.transform.InverseTransformPoint(bounds.center) : Vector3.zero;
            Extents = measured ? bounds.extents : Vector3.zero;
            // A trash box is held clear of the body, not through it.
            Forward = Mathf.Max(HoldForward, Mathf.Max(Extents.x, Extents.z) + HoldBodyClearance);
            // restrictGrab first: it also turns the sleep coroutine's AwakePhysics into a no-op.
            item.restrictGrab = true;
            Rigidbody rb = item.CachedRigidbody;
            // A resting item is already kinematic (SettleAndSleep), and Unity warns on setting its velocity.
            if (!rb.isKinematic)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            rb.interpolation = RigidbodyInterpolation.None;
            item.FreezePhysics();
            // After the freeze, which may switch the static collider on.
            colliders.Clear();
            foreach (Collider collider in item.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled) continue;

                collider.enabled = false;
                colliders.Add(collider);
            }
            rotation = Quaternion.Inverse(Body.rotation) * item.transform.rotation;
            reachTo = null;
            reachSpeed = 0f;
            Item = item;
            // Resting items are kinematic: whatever lay on this one would hover. The game wakes them when
            // the player takes an item (EquipmentSystem), and the colliders are already off.
            item.AwakeNearItems();
            if (measured) WakeStackedOn(item, bounds);
            return true;
        }

        /// <summary>
        /// AwakeNearItems only reaches what the item's own detector zone covers, so a box on top may stay
        /// frozen in mid-air. Everything resting on `taken`'s old place - and on that in turn - is woken,
        /// and its body with it: a sleeping body is not woken by the collider under it going away.
        /// </summary>
        private static readonly Collider[] StackOverlapBuffer = new Collider[32];

        private void WakeStackedOn(Grabbable taken, Bounds bounds)
        {
            const int maxItems = 16;
            const float side = 0.04f;
            const float above = 0.15f;
            List<Grabbable> woken = [taken];
            Queue<Bounds> below = new();
            below.Enqueue(bounds);
            while (below.Count > 0 && woken.Count < maxItems)
            {
                Bounds under = below.Dequeue();
                Vector3 halfExtents = new(under.extents.x + side, above * 0.5f, under.extents.z + side);
                Vector3 boxCentre = new(under.center.x, under.max.y + above * 0.5f - 0.02f, under.center.z);
                int hitCount = Physics.OverlapBoxNonAlloc(boxCentre, halfExtents, StackOverlapBuffer, Quaternion.identity,
                    ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hitCount; i++)
                {
                    Grabbable? other = StackOverlapBuffer[i].GetComponentInParent<Grabbable>();
                    if (other == null || woken.Contains(other) || other.restrictGrab || other.IsGrabbed) continue;

                    woken.Add(other);
                    // Bounds before it starts to fall: whatever lies on it is next.
                    if (ColliderBounds(other.gameObject, out Bounds next)) below.Enqueue(next);

                    other.AwakePhysics();
                    Rigidbody rb = other.CachedRigidbody;
                    if (!rb.isKinematic) rb.WakeUp();
                }
            }
        }

        /// <summary>
        /// Moves the item's centre to `point` (null: back to the hands), still frozen, with its colliders
        /// on while it is away: that is how it touches a trigger.
        /// `speed` above zero moves it that slowly instead of at hand pace: setting it down.
        /// </summary>
        public void ReachTo(Vector3? point, float speed = 0f)
        {
            reachTo = point;
            reachSpeed = point.HasValue ? speed : 0f;
            foreach (Collider collider in colliders)
            {
                if (collider != null) collider.enabled = point.HasValue;
            }
        }

        /// <summary>
        /// Turns the item to face `worldRotation`, and keeps it that way relative to the body.
        /// </summary>
        public void Turn(Quaternion worldRotation) => rotation = Quaternion.Inverse(Body.rotation) * worldRotation;

        public float DistanceTo(Vector3 point) =>
            Item == null ? float.PositiveInfinity : Vector3.Distance(Item.transform.TransformPoint(centre), point);

        /// <summary>
        /// After the NPC has moved and turned this frame; `walkSpeed` is how fast the hands keep up.
        /// </summary>
        internal void Follow(float walkSpeed)
        {
            if (Item == null) return;

            ReownToCarrierRoom(Item);
            if (!Item.gameObject.activeInHierarchy)
            {
                NpcLog.Log.LogInfo($"[ai] '{Item.gameObject.name}' is gone from my hands");
                Release();
                return;
            }
            Transform item = Item.transform;
            item.rotation = Body.rotation * rotation;
            Vector3 current = item.TransformPoint(centre);
            Vector3 target = reachTo ?? Point;
            // Carried along at walking pace, lifted and reached out at hand pace.
            float step = (reachSpeed > 0f ? reachSpeed : walkSpeed + HoldMoveSpeed) * Time.deltaTime;
            Vector3 next = (target - current).sqrMagnitude > HoldSnapDist * HoldSnapDist ? target : Vector3.MoveTowards(current, target, step);
            item.position += next - current;
        }

        /// <summary>
        /// Lets go at `at` (the item's centre) with `velocity`. Undoes PickUp: Crate.UnparentItems.
        /// </summary>
        public void PutDown(Vector3 at, Vector3 velocity)
        {
            if (Item == null) return;

            Grabbable item = Item;
            item.transform.position = at - (item.transform.TransformPoint(centre) - item.transform.position);
            Release();
            if (blocker != null && body.gameObject.activeInHierarchy) body.StartCoroutine(IgnoreBlockerFor(item));
            if (!item.CachedRigidbody.isKinematic) item.CachedRigidbody.velocity = velocity;
        }

        /// <summary>
        /// Whatever the NPC was doing, the item goes down in front of it. Safe to call empty-handed.
        /// </summary>
        public void Drop(string why)
        {
            if (Item == null) return;

            string name = Item.gameObject.name;
            PutDown(groundPos(Mathf.Max(HoldDropHeight, Extents.y + 0.05f)) + Body.forward * Forward, Vector3.zero);
            NpcLog.Log.LogInfo($"[ai] Put down '{name}' - {why}");
        }

        /// <summary>
        /// Colliders, grab and physics back as they were; the item stays where it is.
        /// </summary>
        public void Release()
        {
            Grabbable? item = Item;
            Item = null;
            reachTo = null;
            reachSpeed = 0f;
            if (item == null) return;

            // Before SavePosition: that records a local position, so the wrong parent saves the
            // wrong place as surely as it hides the item.
            ReownToCarrierRoom(item);

            foreach (Collider collider in colliders)
            {
                if (collider != null) collider.enabled = true;
            }
            colliders.Clear();
            item.restrictGrab = false;
            item.CachedRigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            item.AwakePhysics();
            item.SavePosition();
        }

        /// <summary>
        /// EntryDetector.CheckItemParents reparents an item that crosses a doorway into the room it
        /// ended up in - but skips anything with restrictGrab set, which a held item always has. Left
        /// alone, a carried item keeps the room it was picked up in and goes inactive with that room's
        /// content the moment the player walks out of it.
        /// docs/invariants.md#a-carried-item-belongs-to-the-room-its-carrier-is-in
        /// </summary>
        private void ReownToCarrierRoom(Grabbable item)
        {
            Room? current = room();
            // activeSelf, not activeInHierarchy: a room switched off leaves the item's own flag alone,
            // a trash can or a sale clears it (Grabbable.Destroy), and that one must stay gone.
            if (item == null || !item.gameObject.activeSelf || current == null || !current.gameObject.activeInHierarchy) return;

            Transform content = current.ContentParent;
            if (content == null || item.transform.parent == content) return;

            item.SetParent(content);
            NpcLog.Log.LogInfo($"[ai] Carried '{item.gameObject.name}' into {current.gameObject.name}");
        }

        private IEnumerator IgnoreBlockerFor(Grabbable item)
        {
            Collider[] itemColliders = item.GetComponentsInChildren<Collider>();
            foreach (Collider collider in itemColliders) Physics.IgnoreCollision(collider, blocker, true);

            yield return new WaitForSeconds(PutDownIgnoreSeconds);

            foreach (Collider collider in itemColliders)
            {
                if (collider != null && blocker != null) Physics.IgnoreCollision(collider, blocker, false);
            }
        }
    }
}
