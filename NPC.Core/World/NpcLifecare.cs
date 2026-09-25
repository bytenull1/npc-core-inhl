using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NPC.Core.World
{
    /// <summary>
    /// An NPC the ship's lifecare terminal shows as a lifeform. Optional beside <see cref="INpc"/>.
    /// docs/lifecare.md
    /// </summary>
    public interface INpcLifeform
    {
        /// <summary>
        /// Aboard the player's ship now. Answered by the floor, never a tracked room:
        /// docs/invariants.md#aboard-is-answered-by-the-floor
        /// </summary>
        bool IsAboardPlayerShip();

        /// <summary>
        /// The room it was last tracked into, for the scan's log line; "none" when it has none.
        /// </summary>
        string TrackedRoomName { get; }

        /// <summary>
        /// Drawn at all. Read at every scan: switched off, its icon goes at the next scan's end.
        /// </summary>
        bool ShowsOnLifecare { get; }
    }

    /// <summary>
    /// The lifecare scan sees every registered <see cref="INpcLifeform"/>: one map icon each, cloned from
    /// the game's own, and the lifeforms count. docs/lifecare.md
    /// </summary>
    internal static class NpcLifecare
    {
        private sealed class LifeIcon
        {
            public Image? Icon;
            /// <summary>
            /// Ship-local position at the last scan's end, or null when not drawn.
            /// </summary>
            public Vector3? LocalPos;
            /// <summary>
            /// Cloned on the current display, so a missing clone was lost rather than never made.
            /// </summary>
            public bool Created;
        }

        private const float PollSeconds = 0.1f;

        private static readonly Dictionary<INpc, LifeIcon> Icons = [];
        private static readonly List<INpc> Gone = [];
        private static LifecareDisplay? _display;
        private static float _pollAt;
        private static bool _scanWasActive;
        private static bool _terminalWasEnabled;

        internal static void Init()
        {
            NpcRegistry.Unregistered += Remove;
        }

        /// <summary>
        /// From the shared tick: a 10 Hz poll while any lifeform is loaded.
        /// </summary>
        internal static void Tick()
        {
            if (!AnyActive() || Time.time < _pollAt) return;

            _pollAt = Time.time + PollSeconds;
            TryHook();
            UpdateIcons();
        }

        private static bool AnyActive()
        {
            foreach (INpc npc in NpcRegistry.All)
            {
                if (Shows(npc, out _) && !NpcRegistry.IsGone(npc) && npc.IsActive) return true;
            }
            // A drawn icon whose NPC stopped showing is cleared at the next scan's end.
            foreach (LifeIcon entry in Icons.Values)
            {
                if (entry.LocalPos != null) return true;
            }
            return false;
        }

        private static bool Shows(INpc npc, [NotNullWhen(true)] out INpcLifeform? lifeform)
        {
            lifeform = npc as INpcLifeform;
            return lifeform != null && lifeform.ShowsOnLifecare;
        }

        private static Transform? ShipTransform
        {
            get
            {
                GameManager gm = GameManager.Instance;
                return gm != null && gm.PlayerShip != null ? gm.PlayerShip.transform : null;
            }
        }

        // "Aboard" is answered by the floor, never by the game's current room, which is null until a
        // doorway is first crossed. docs/invariants.md#aboard-is-answered-by-the-floor
        private static void TryHook()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.PlayerShip == null || gm.PlayerShip.LifecareController == null) return;

            LifecareDisplay display = gm.PlayerShip.LifecareController.Display;
            if (display == null) return;

            Image? breathless = GameInternals.LifecareDisplayAccess.GetBreathlessIcon(display);
            if (breathless == null) return;

            // A new terminal: the old clones and the last scan's snapshot belong to the old one.
            if (_display != display)
            {
                foreach (LifeIcon old in Icons.Values)
                {
                    if (old.Icon != null) Object.Destroy(old.Icon.gameObject);
                    old.Icon = null;
                    old.LocalPos = null;
                    old.Created = false;
                }
                _display = display;
            }

            // A pruned NPC never unregistered: its icon goes too.
            Gone.Clear();
            foreach (INpc npc in Icons.Keys)
            {
                if (NpcRegistry.IsGone(npc)) Gone.Add(npc);
            }
            foreach (INpc npc in Gone) Remove(npc);

            // Re-hook when our clone went missing: the game may rebuild the terminal UI while the display
            // object itself survives. docs/lifecare.md
            foreach (INpc npc in NpcRegistry.All)
            {
                if (!Shows(npc, out _) || NpcRegistry.IsGone(npc)) continue;

                if (!Icons.TryGetValue(npc, out LifeIcon? entry))
                {
                    entry = new LifeIcon();
                    Icons[npc] = entry;
                }
                if (entry.Icon != null && entry.Icon.transform.parent != null) continue;

                if (entry.Created)
                {
                    using NpcRegistry.ActingScope _ = NpcRegistry.Acting(npc);
                    NpcLog.Log.LogInfo("[lifecare] Lifecare icon was lost - re-creating it");
                }

                // Clone the breathless icon as the NPC's own map icon. The last scan's snapshot is kept: it
                // is still valid, and dropping it would blank the icon until the next scan.
                GameObject clone = Object.Instantiate(breathless.gameObject, breathless.transform.parent);
                clone.name = "NpcLifeIcon " + npc.Name;
                entry.Icon = clone.GetComponent<Image>();
                if (entry.Icon == null)
                {
                    Object.Destroy(clone);
                    continue;
                }
                entry.Icon.enabled = false;
                entry.Created = true;
            }
        }

        /// <summary>
        /// An NPC that is gone takes its icon and its count with it.
        /// </summary>
        private static void Remove(INpc npc)
        {
            if (!Icons.TryGetValue(npc, out LifeIcon? entry)) return;

            Icons.Remove(npc);
            if (entry.Icon != null) Object.Destroy(entry.Icon.gameObject);
            RecountLifeforms(_display);
        }

        private static void UpdateIcons()
        {
            if (_display == null) return;

            // Check if terminal is enabled (not in boot/loading state)
            LoadingAnimator? bootLoading = GameInternals.LifecareDisplayAccess.GetBootLoading(_display);
            bool terminalEnabled = bootLoading == null || !bootLoading.gameObject.activeSelf;

            // If terminal just turned off, hide the icons and reset
            if (!terminalEnabled && _terminalWasEnabled)
            {
                _terminalWasEnabled = false;
                _scanWasActive = false;
                foreach (LifeIcon entry in Icons.Values)
                {
                    if (entry.Icon != null && entry.Icon.enabled) entry.Icon.enabled = false;
                }
                RecountLifeforms(_display);
                return;
            }

            // Turned on: the cached positions from a previous scan are drawn below.
            if (terminalEnabled) _terminalWasEnabled = true;

            // The game uses scanLoading.gameObject.activeSelf to indicate an active scan.
            LoadingAnimator? scanLoading = GameInternals.LifecareDisplayAccess.GetScanLoading(_display);
            bool scanIsActive = scanLoading != null && scanLoading.gameObject.activeSelf;

            if (scanIsActive && !_scanWasActive)
            {
                // Scan just started - mark as active but don't update position yet
                _scanWasActive = true;
            }
            else if (!scanIsActive && _scanWasActive)
            {
                // Scan just ended - capture every NPC's position now
                _scanWasActive = false;
                foreach (KeyValuePair<INpc, LifeIcon> pair in Icons)
                {
                    using NpcRegistry.ActingScope _ = NpcRegistry.Acting(pair.Key);
                    CaptureScan(pair.Key, pair.Value);
                }
                UpdateIconVisibility();
                LogIconState();
            }

            // Re-asserted every poll, not only on scan edges: whatever the game does to this UI in
            // between, the icons and count are restored within 0.1 s instead of staying wrong until the
            // next scan.
            if (terminalEnabled) UpdateIconVisibility();
        }

        /// <summary>
        /// A snapshot taken when the scan finishes, like the game's own player and Breathless icons: an
        /// NPC not aboard at that instant is not drawn until the next scan. docs/lifecare.md
        /// </summary>
        private static void CaptureScan(INpc npc, LifeIcon entry)
        {
            entry.LocalPos = null;
            Transform? ship = ShipTransform;
            if (!NpcRegistry.IsGone(npc) && !npc.IsDead && ship != null && Shows(npc, out INpcLifeform? lifeform))
            {
                bool aboard = lifeform.IsAboardPlayerShip();
                if (aboard) entry.LocalPos = ship.InverseTransformPoint(npc.Transform.position);

                if (NpcLog.Level >= 1)
                {
                    NpcLog.Log.LogInfo(
                        "[lifecare] Lifecare scan finished: aboard=" + aboard +
                        ", pos=" + npc.Transform.position.ToString("0.0") +
                        ", trackedRoom=" + lifeform.TrackedRoomName +
                        " -> icon " + (entry.LocalPos.HasValue ? "shown" : "hidden"));
                }
            }
            else if (NpcLog.Level >= 1)
            {
                NpcLog.Log.LogInfo(
                    "[lifecare] Lifecare scan finished with nothing to draw (npc=" +
                    (NpcRegistry.IsGone(npc) ? "gone" : npc.IsDead ? "dead" : !Shows(npc, out _) ? "not shown" : "ok") + ", ship=" + (ship == null ? "null" : "ok") + ")");
            }
        }

        /// <summary>
        /// The rendered state, not our bookkeeping: is each clone still in a live UI hierarchy, and did our
        /// count survive to the label? Our own flags have reported "shown" while the player saw nothing.
        /// </summary>
        private static void LogIconState()
        {
            if (NpcLog.Level < 1 || _display == null) return;

            TMP_Text? label = GameInternals.LifecareDisplayAccess.GetLifeformsLabel(_display);
            Image? playerIcon = GameInternals.LifecareDisplayAccess.GetPlayerIcon(_display);
            foreach (KeyValuePair<INpc, LifeIcon> pair in Icons)
            {
                Image? icon = pair.Value.Icon;
                if (icon == null) continue;

                using NpcRegistry.ActingScope _ = NpcRegistry.Acting(pair.Key);
                NpcLog.Log.LogInfo(
                    "[lifecare] Lifecare icon state: enabled=" + icon.enabled +
                    ", activeInHierarchy=" + icon.gameObject.activeInHierarchy +
                    ", parent=" + (icon.transform.parent != null ? icon.transform.parent.name : "none") +
                    ", localPos=" + icon.transform.localPosition.ToString("0.0") +
                    ", playerIcon=" + (playerIcon != null && playerIcon.enabled) +
                    ", label=" + (label != null ? label.text : "<no label>"));
            }
        }

        private static void UpdateIconVisibility()
        {
            if (_display == null) return;

            float scale = GameInternals.LifecareDisplayAccess.GetScale(_display);
            foreach (LifeIcon entry in Icons.Values)
            {
                if (entry.Icon == null) continue;

                if (entry.LocalPos == null)
                {
                    if (entry.Icon.enabled) entry.Icon.enabled = false;
                    continue;
                }

                Vector3 wanted = -new Vector3(entry.LocalPos.Value.x * scale, entry.LocalPos.Value.z * scale, 0f);
                if (!entry.Icon.gameObject.activeSelf) entry.Icon.gameObject.SetActive(true);

                entry.Icon.enabled = true;
                entry.Icon.transform.localPosition = wanted;
            }
            RecountLifeforms(_display);
        }

        /// <summary>
        /// Recomputes the lifeforms label: the game's own count plus each NPC icon shown on that display.
        /// </summary>
        internal static void RecountLifeforms(LifecareDisplay? display)
        {
            if (display == null) return;

            TMP_Text? label = GameInternals.LifecareDisplayAccess.GetLifeformsLabel(display);
            if (label == null) return;

            int count = 0;

            // The game clamps breathless + temp blips to one between them:
            // docs/invariants.md#mirror-the-vanilla-lifeform-clamp
            bool otherLifeform = false;
            Image? breathless = GameInternals.LifecareDisplayAccess.GetBreathlessIcon(display);
            if (breathless != null && breathless.enabled) otherLifeform = true;

            GameObject[]? temps = GameInternals.LifecareDisplayAccess.GetTempObjects(display);
            if (!otherLifeform && temps != null)
            {
                foreach (GameObject temp in temps)
                {
                    if (temp != null && temp.activeSelf)
                    {
                        otherLifeform = true;
                        break;
                    }
                }
            }
            if (otherLifeform) count++;

            Image? playerIcon = GameInternals.LifecareDisplayAccess.GetPlayerIcon(display);
            if (playerIcon != null && playerIcon.enabled) count++;
            // Only the icons belonging to this display: the postfix fires for whichever LifecareDisplay
            // the game touched.
            if (display == _display)
            {
                foreach (LifeIcon entry in Icons.Values)
                {
                    if (entry.Icon != null && entry.Icon.enabled) count++;
                }
            }

            // Assigning TMP text forces a mesh rebuild, and this runs on every poll.
            string text = count.ToString();
            if (label.text != text) label.text = text;
        }

        /// <summary>
        /// Works around a game bug that hides the player's own icon (docs/lifecare.md#3-the-missing-player-icon-game-bug-worked-around).
        /// May only ever turn the icon on, and only from a floor probe:
        /// docs/invariants.md#player-icon-fix-is-one-directional
        /// </summary>
        internal static void FixPlayerIcon(ref bool enabled)
        {
            if (enabled) return; // the game already counts them; never take an icon away

            Space.Player? pilot = NpcPlayer.Pilot;
            if (pilot == null || pilot.Controller == null) return;

            if (NpcVessels.FloorOwner(pilot.Controller.CachedTransform.position) == FloorOwnership.PlayerShip) enabled = true;
        }
    }
}
