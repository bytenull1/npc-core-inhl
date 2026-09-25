using NPC.Core.Navigation;
using NPC.Core.World;
using UnityEngine;

namespace NPC.Core.Agents
{
    /// <summary>
    /// Door interaction: open gates in the path, wait for them, close behind; room loading around doors. What
    /// an NPC may open and what routing avoids is the shared door knowledge of NpcDoors: docs/doors.md
    /// </summary>
    public sealed partial class NpcAgent
    {
        private float waitingGateSince = 0f;
        private float lastDoorRayTime = 0f;
        // What the last door ray hit, for the stall line.
        private string lastDoorRay = "not cast yet";
        private float closedToPlayerLogAt = 0f;
        /// <summary>
        /// Fallback radius around a gate's transform searched for bodies before issuing
        /// a close, used only when the gate's own AntiCrasher volumes are unreachable.
        /// </summary>
        private const float DoorwayClearRadius = 1.2f;
        /// <summary>
        /// How near the gate the NPC has to be to count as blocking it itself.
        /// </summary>
        private const float DoorwaySelfRadius = 1.4f;
        /// <summary>
        /// After this long unable to close, close anyway and let the AntiCrasher
        /// arbitrate. A wait with no end is indistinguishable from a forgotten door.
        /// </summary>
        private const float DoorDeferGiveUp = 20f;
        private float doorwayStepOutCooldown = 0f;
        private float doorwayBlockLogAt = 0f;

        /// <summary>
        /// How near the NPC must get to a pin panel before it may enter the code.
        /// </summary>
        private const float PinPanelReachDist = 1.6f;
        private float pinPanelTryAt = 0f;

        /// <summary>
        /// Simple door handling: open closed room gates in front of the NPC, wait for
        /// our door to finish opening, walk through everything already open.
        /// </summary>
        private Vector3 HandleDoors(Vector3 desired, ref bool wantMove)
        {
            if (waitingGate != null)
            {
                if (waitingGate.FullyOpened)
                {
                    waitingGate = null;
                }
                else if (Time.time - waitingGateSince > 5f)
                {
                    NpcLog.Log.LogWarning("[ai] Timed out waiting for a door, moving on");
                    waitingGate = null;
                }
                else
                {
                    wantMove = false;
                    return Vector3.zero;
                }
            }

            if (settings.CanOpenDoors && wantMove && Time.time >= lastDoorRayTime + 0.2f)
            {
                lastDoorRayTime = Time.time;

                Vector3 dir = new(desired.x, 0f, desired.z);
                if (dir.sqrMagnitude > 0.0001f)
                {
                    dir.Normalize();
                    Vector3 origin = GroundPos(0.9f);
                    lastDoorRay = "nothing within 1.2m";
                    if (Physics.Raycast(origin, dir, out RaycastHit hit, 1.2f, ProbeLayers, QueryTriggerInteraction.Ignore))
                    {
                        Gate gate = hit.collider.GetComponentInParent<Gate>();
                        lastDoorRay = $"'{hit.collider.name}' at {hit.distance:0.00}m, " + (gate == null ? "not a gate"
                            : $"gate '{gate.name}' {(gate.Opened ? "open" : "shut")}{(gate.Locked ? ", locked" : "")}");
                        // ReSharper disable once MergeIntoPattern
                        if (gate != null && gate.gameObject.activeInHierarchy && !gate.Locked && !gate.Opened)
                        {
                            // A gate the NPC may not open itself is left alone; the
                            // path search already routes around it.
                            if (!NpcDoors.MayOpen(gate))
                            {
                                LogClosedToPlayerRefusal(gate);
                                return desired;
                            }

                            // A password door the NPC does know the code for is opened
                            // through its own panel, not by Gate.Open.
                            if (TryUnlockPasswordGate(gate)) return desired;

                            LoadRoomsAroundGate(gate);
                            gate.Open();
                            waitingGate = gate;
                            waitingGateSince = Time.time;
                            ArmDoorClose(gate);
                            NpcLog.Log.LogInfo("[ai] Opening door '" + gate.gameObject.name + "'");
                        }
                    }
                }
            }

            return desired;
        }


        private void LogClosedToPlayerRefusal(Gate gate)
        {
            if (Time.time < closedToPlayerLogAt) return;

            string? why = NpcDoors.WhyClosedToPlayer(gate);
            if (why == null) return;

            closedToPlayerLogAt = Time.time + 5f;
            NpcLog.Log.LogInfo("[ai] Not opening '" + gate.gameObject.name + "' - " + why);
        }

        /// <summary>
        /// Enters a code the player gave the NPCs on the gate's own panel. Returns
        /// true when this gate is a password door, so the caller stops handling it -
        /// including while the NPC is still walking to the panel.
        /// </summary>
        private bool TryUnlockPasswordGate(Gate gate)
        {
            DoorPinCode? panel = NpcDoors.PinPanelFor(gate);
            if (panel == null) return false;

            if (Time.time < pinPanelTryAt) return true;

            // Out of arm's reach: keep walking, the route already leads past the panel.
            if ((transform.position - panel.transform.position).sqrMagnitude > PinPanelReachDist * PinPanelReachDist)
            {
                return true;
            }

            pinPanelTryAt = Time.time + 2f;
            LoadRoomsAroundGate(gate);
            // The panel's own OnValidated wiring opens the gate, so the door goes
            // through the game's path and the close-behind bookkeeping still applies.
            // Never PinCode.Interact(null) - that is the monster's random-guess branch.
            panel.ForceValidate();
            ArmDoorClose(gate);
            NpcLog.Log.LogInfo("[ai] Entered the password for '" + gate.gameObject.name + "'");
            return true;
        }

        /// <summary>
        /// Registers a door the NPC just opened; re-arming only refreshes the timer.
        /// `alreadyCrossed` is for a door it blocked rather than opened. docs/doors.md
        /// </summary>
        private void ArmDoorClose(Gate gate, bool alreadyCrossed = false)
        {
            foreach (PendingDoorClose existing in pendingDoorCloses)
            {
                if (existing.Gate == gate)
                {
                    existing.CloseAt = Time.time + 3f;
                    existing.ArmedAt = Time.time;
                    existing.Attempts = 0;
                    return;
                }
            }
            pendingDoorCloses.Add(new PendingDoorClose
            {
                Gate = gate,
                CloseAt = Time.time + 3f,
                ArmedAt = Time.time,
                Attempts = 0,
                DeferredSince = 0f,
                OpenedFrom = transform.position,
                Crossed = alreadyCrossed
            });
        }

        /// <summary>
        /// A close undone by the gate's own AntiCrasher, from the Gate.FailClose patch.
        /// The game never retries, so a door the NPC blocked is the NPC's to
        /// finish. docs/doors.md#4-closes-an-npc-blocked
        /// </summary>
        private void NoteCloseFailed(Gate gate)
        {
            if (IsDead || gate == null || !settings.CanOpenDoors) return;
            // Somebody else was in the way; not ours to finish.
            if ((transform.position - gate.transform.position).sqrMagnitude >= DoorwaySelfRadius * DoorwaySelfRadius)
            {
                return;
            }
            // Airlocks cycle themselves and password doors are not ours to touch.
            if (NpcDoors.IsAirlockGate(gate) || NpcDoors.IsPasswordGate(gate)) return;

            foreach (PendingDoorClose existing in pendingDoorCloses)
            {
                if (existing.Gate != gate) continue;
                // Already owed, and deliberately not re-armed:
                // docs/invariants.md#fail-close-must-not-rearm
                existing.CloseAt = Mathf.Max(existing.CloseAt, Time.time + 1.5f);
                StepOutOfDoorway(gate);
                return;
            }

            ArmDoorClose(gate, alreadyCrossed: true);
            NpcLog.Log.LogInfo("[ai] Blocked '" + gate.gameObject.name +
                               "' from closing - taking the close over");
            StepOutOfDoorway(gate);
        }

        /// <summary>
        /// "Behind itself" means it actually went through. A door opened ahead and shut
        /// again three seconds later is a door slammed in its own face.
        /// docs/invariants.md#close-only-what-you-walked-through
        /// </summary>
        private bool HasCrossed(PendingDoorClose pending, Gate gate)
        {
            if (pending.Crossed) return true;

            Vector3 gatePos = gate.transform.position;
            Vector3 now = transform.position - gatePos;
            Vector3 then = pending.OpenedFrom - gatePos;
            now.y = 0f;
            then.y = 0f;
            if (now.sqrMagnitude > 0.25f && Vector3.Dot(now, then) < 0f) pending.Crossed = true;

            return pending.Crossed;
        }

        /// <summary>
        /// Closes the doors the NPC opened, once it is clear of each doorway.
        /// Retries matter because a close issued into a blocker is undone by the door
        /// itself. Doors the NPC did not open are not tracked - see docs/doors.md.
        /// </summary>
        private void UpdateDoorCloseBehind()
        {
            if (IsDead || pendingDoorCloses.Count == 0) return;

            for (int i = pendingDoorCloses.Count - 1; i >= 0; i--)
            {
                PendingDoorClose pending = pendingDoorCloses[i];
                Gate gate = pending.Gate;

                // Destroyed with its room, already shut, or no longer ours to close.
                if (gate == null || !gate.Opened || !settings.CanOpenDoors ||
                    gate.Locked || Time.time - pending.ArmedAt > 90f)
                {
                    pendingDoorCloses.RemoveAt(i);
                    continue;
                }

                if (Time.time < pending.CloseAt) continue;

                if (!HasCrossed(pending, gate))
                {
                    pending.CloseAt = Time.time + 1f;
                    continue;
                }

                // Closing into a blocker just feeds the AntiCrasher, which re-opens the
                // door and burns an attempt. docs/doors.md
                bool selfBlocking = (transform.position - gate.transform.position).sqrMagnitude <
                                    DoorwaySelfRadius * DoorwaySelfRadius;
                Collider? blocker = selfBlocking ? null : DoorwayBlocker(gate);

                if (selfBlocking || blocker != null)
                {
                    if (pending.DeferredSince <= 0f) pending.DeferredSince = Time.time;

                    if (selfBlocking)
                    {
                        // Waiting alone is not enough: while the player stays close the
                        // NPC has no reason to move, so it can loiter in the doorway
                        // indefinitely. Push it clear instead.
                        StepOutOfDoorway(gate);
                    }
                    else
                    {
                        // Not self-blocking, so the check above found a blocker.
                        LogDoorwayBlocked(gate, blocker!);
                    }

                    // docs/invariants.md#no-permanent-deferral, except when the NPC is
                    // itself the blocker:
                    // docs/invariants.md#never-force-a-close-into-the-npc
                    if (selfBlocking || Time.time - pending.DeferredSince < DoorDeferGiveUp)
                    {
                        pending.CloseAt = Time.time + (selfBlocking ? 1f : 1.5f);
                        continue;
                    }
                }

                pending.DeferredSince = 0f;
                gate.Close();
                // Tell the EntryDetector patch this close was ours, so it does not let the game re-file
                // the player into whatever room this doorway leads to.
                NpcDoors.NoteClosedBy(gate, this);
                pending.Attempts++;
                NpcLog.Log.LogInfo("[ai] Closing door '" + gate.gameObject.name +
                                   "' behind itself (attempt " + pending.Attempts + ")");

                if (pending.Attempts >= 10)
                {
                    NpcLog.Log.LogWarning("[ai] Giving up on closing '" +
                                          gate.gameObject.name + "' after " + pending.Attempts + " attempts");
                    pendingDoorCloses.RemoveAt(i);
                }
                else
                {
                    pending.CloseAt = Time.time + 3f;
                }
            }
        }

        /// <summary>
        /// Walks the NPC out of a doorway it is holding open. Only when it is not
        /// already walking somewhere - a walk under way clears the doorway on its own.
        /// </summary>
        private void StepOutOfDoorway(Gate gate)
        {
            if (Time.time < followStepOffUntil || Time.time < doorwayStepOutCooldown) return;

            if (hasMoveTarget || !brain.Activity.LeavesDoorways) return;

            Vector3 away = transform.position - gate.transform.position;
            away.y = 0f;
            // Standing exactly in the threshold: any horizontal direction will do, so
            // use the way the NPC is facing.
            if (away.sqrMagnitude < 0.04f) away = transform.forward;

            away.y = 0f;
            if (away.sqrMagnitude < 0.001f) away = Vector3.forward;

            away.Normalize();

            followStepOffTarget = transform.position + away * 1.8f;
            followStepOffUntil = Time.time + 1.5f;
            doorwayStepOutCooldown = Time.time + 4f;
            NpcLog.Log.LogInfo("[ai] Standing in '" + gate.gameObject.name +
                               "' - stepping clear so it can close");
        }

        /// <summary>
        /// The collider standing in the doorway that would make a close fail, or null.
        /// Asks the gate's own AntiCrasher sensors rather than guessing at a shape:
        /// docs/invariants.md#occupancy-asks-the-anticrasher
        /// </summary>
        private Collider? DoorwayBlocker(Gate gate)
        {
            AntiCrasher[]? sensors = GameInternals.GateAccess.GetAntiCrashers(gate);
            bool probedASensor = false;
            if (sensors != null)
            {
                foreach (AntiCrasher sensor in sensors)
                {
                    if (sensor == null || !sensor.gameObject.activeInHierarchy) continue;
                    // Normally on the sensor itself; tolerate a prefab that parks it on
                    // a child rather than falling back to the crude radius.
                    if (!sensor.TryGetComponent(out Collider volume))
                    {
                        volume = sensor.GetComponentInChildren<Collider>();
                    }
                    if (volume == null) continue;

                    probedASensor = true;

                    Bounds bounds = volume.bounds;
                    // The sensor's own layer row, not the NPC's: an AntiCrasher stops
                    // for a dropped item, which a body walks straight through.
                    int hits = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, OverlapBuffer,
                        Quaternion.identity, NavProbe.CollisionMaskFor(volume.gameObject.layer),
                        QueryTriggerInteraction.Ignore);
                    Collider? blocker = FirstDoorwayBlocker(gate, hits);
                    if (blocker != null) return blocker;
                }
            }
            if (probedASensor) return null;

            // No sensors reachable (reflection failed, or this prefab has none): fall
            // back to the old radius so the check degrades rather than disappearing.
            int count = Physics.OverlapSphereNonAlloc(gate.transform.position, DoorwayClearRadius,
                OverlapBuffer, ProbeLayers, QueryTriggerInteraction.Ignore);
            return FirstDoorwayBlocker(gate, count);
        }

        /// <summary>
        /// First entry in OverlapBuffer that would trip the gate's AntiCrasher.
        /// Must be called before the buffer is reused.
        /// </summary>
        private Collider? FirstDoorwayBlocker(Gate gate, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Collider blocker = OverlapBuffer[i];
                if (blocker == null) continue;

                if (blocker.gameObject.layer == 0)
                {
                    continue;               // static geometry
                }

                if (blocker.transform.IsChildOf(gate.transform))
                {
                    continue; // the door leaves
                }

                if (blocker.transform.IsChildOf(transform))
                {
                    continue;      // us; the distance check owns that
                }

                return blocker;
            }
            return null;
        }

        /// <summary>
        /// Names whatever is holding a door open, throttled. Without it a silently
        /// deferred door cannot be diagnosed from a capture. See docs/logging.md.
        /// </summary>
        private void LogDoorwayBlocked(Gate gate, Collider blocker)
        {
            if (NpcLog.Level < 1) return;

            if (Time.time < doorwayBlockLogAt) return;

            doorwayBlockLogAt = Time.time + 5f;
            NpcLog.Log.LogInfo("[ai] Waiting to close '" + gate.gameObject.name +
                               "': '" + blocker.gameObject.name + "' (layer '" +
                               LayerMask.LayerToName(blocker.gameObject.layer) +
                               "') is in the doorway");
        }

        /// <summary>
        /// When the NPC opens a gate, immediately load the rooms on both sides.
        /// </summary>
        private void LoadRoomsAroundGate(Gate gate)
        {
            foreach (EntryDetector detector in NpcDoors.Detectors)
            {
                if (detector == null || NpcDoors.DoorOf(detector) != gate) continue;

                NpcDoors.RoomsOf(detector, out Room? innerRoom, out Room? outerRoom);
                LoadRoom(innerRoom, "opening the door to", outerRoom);
                LoadRoom(outerRoom, "opening the door to", innerRoom);
            }
        }
    }
}
