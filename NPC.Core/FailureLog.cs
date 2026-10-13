using System;
using System.Collections.Generic;
using UnityEngine;

namespace NPC.Core
{
    /// <summary>
    /// Exceptions caught where the game calls mod code: the first in full, a repeat once a minute with
    /// its count. docs/invariants.md#mod-code-never-throws-into-the-game
    /// </summary>
    internal static class FailureLog
    {
        private const float RepeatLogSeconds = 60f;

        private sealed class Failure
        {
            public int Count;
            public float LoggedAt;
        }

        private static readonly Dictionary<object, Failure> Failures = [];

        /// <summary>
        /// `what` names the failing code and what the game does instead, e.g. "patch X - the game's method ran unpatched".
        /// </summary>
        internal static void Report(object key, string what, Exception ex)
        {
            if (!Failures.TryGetValue(key, out Failure? failure)) Failures[key] = failure = new Failure();

            failure.Count++;
            if (failure.Count == 1)
            {
                NpcLog.Core.LogError($"[mod] {what} failed: {ex}");
                failure.LoggedAt = Time.time;
                return;
            }
            if (Time.time - failure.LoggedAt < RepeatLogSeconds) return;

            failure.LoggedAt = Time.time;
            NpcLog.Core.LogError($"[mod] {what} still failing ({failure.Count} times): {ex.Message}");
        }
    }
}
