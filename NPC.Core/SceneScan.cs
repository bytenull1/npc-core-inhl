using NPC.Core.Navigation;
using UnityEngine;

namespace NPC.Core
{
    /// <summary>
    /// Whole-scene FindObjectsOfType sweeps cost milliseconds each, so they are budgeted here.
    /// docs/invariants.md#scene-sweeps-are-budgeted
    /// </summary>
    public static class SceneScan
    {
        private const float SummaryInterval = 60f;

        private static int _rescanFrame = -1;
        private static int _arrayFrame = -1;
        private static int _rescans;
        private static int _deferred;
        private static int _inSearch;
        private static float _windowStart = -1f;

        /// <summary>
        /// May a TTL cache whose time is up sweep the scene now? One rescan per frame; the rest keep
        /// their list and ask again on their next access. `mustScan`: nothing to serve, or forced.
        /// </summary>
        public static bool MayRescan(bool mustScan)
        {
            int frame = Time.frameCount;
            if (!mustScan && _rescanFrame == frame)
            {
                _deferred++;
                return false;
            }
            _rescanFrame = frame;
            _rescans++;
            if (NavGraph.InSearch) _inSearch++;
            LogSummary();
            return true;
        }

        /// <summary>
        /// Level 2: how often the budget held a rescan back, so a stale cache can be ruled in or out.
        /// </summary>
        private static void LogSummary()
        {
            if (_windowStart < 0f) _windowStart = Time.time;
            if (Time.time - _windowStart < SummaryInterval) return;

            if (NpcLog.Level >= 2)
            {
                NpcLog.Log.LogInfo($"[probe] Scene rescans: {_rescans} in the last {Time.time - _windowStart:0} s, " +
                                   $"{_deferred} put off to a later frame, {_inSearch} inside a path search");
            }
            _windowStart = Time.time;
            _rescans = 0;
            _deferred = 0;
            _inSearch = 0;
        }

        /// <summary>
        /// A sweep already landed on this frame, budgeted or per-frame. A prefetch waits for a frame without one.
        /// </summary>
        internal static bool SweptThisFrame => _rescanFrame == Time.frameCount || _arrayFrame == Time.frameCount;

        /// <summary>
        /// FindObjectsOfType, swept once per frame: a decision's Count and the winner's TryStart share it.
        /// Read only. docs/invariants.md#scene-sweeps-are-budgeted
        /// </summary>
        public static T[] ThisFrame<T>() where T : Object => FrameArray<T>.Get();

        private static class FrameArray<T> where T : Object
        {
            private static T[] _items = [];
            private static int _frame = -1;

            internal static T[] Get()
            {
                if (_frame == Time.frameCount) return _items;

                _items = Object.FindObjectsOfType<T>();
                _frame = Time.frameCount;
                _arrayFrame = _frame;
                return _items;
            }
        }
    }
}
