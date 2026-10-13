using UnityEngine;

namespace NPC.Core.Agents
{
    internal sealed class WanderTargetBackoff
    {
        private readonly Entry[] entries = new Entry[32];
        private int next;
        private readonly record struct Entry(Vector3 Goal, Vector3 From, float Until);

        internal void Remember(Vector3 goal, Vector3 from, float now)
        {
            entries[next] = new Entry(goal, from, now + 30f);
            next = (next + 1) % entries.Length;
        }

        internal bool Contains(Vector3 goal, Vector3 from, float now)
        {
            foreach (Entry entry in entries)
                if (now < entry.Until && (entry.Goal - goal).sqrMagnitude < .01f &&
                    (entry.From - from).sqrMagnitude < 4f) return true;
            return false;
        }
    }
}
