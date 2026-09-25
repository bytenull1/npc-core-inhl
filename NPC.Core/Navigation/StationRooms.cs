using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NPC.Core.Navigation
{
    /// <summary>
    /// The rooms of the docked station, by the names the game gives them, and which nav nodes stand
    /// in each: how "go to the library" becomes a node. docs/navigation.md#rooms-by-name
    /// </summary>
    public static class StationRooms
    {
        /// <summary>
        /// A room that has nodes. Nodes are the room's own, nearest the middle first; Keys are the
        /// squashed names it answers to.
        /// </summary>
        public sealed record Entry(string Name, string[] Keys, int[] Nodes);

        private const float CacheSeconds = 3f;
        private const int MinShortKey = 3;
        private static readonly HashSet<string> FillerWords =
            ["goto", "go", "to", "walk", "move", "the", "room", "please", "now", "into", "in"];

        private static Entry[] _entries = [];
        private static string _station = "";
        private static float _builtAt = float.NegativeInfinity;
        private static string _lastLogged = "";

        /// <summary>
        /// The docked station's rooms that hold a node, by name; empty when none is docked.
        /// </summary>
        public static Entry[] Current(out string station)
        {
            if (Time.unscaledTime >= _builtAt + CacheSeconds)
            {
                _builtAt = Time.unscaledTime;
                Rebuild();
            }
            station = _station;
            return _entries;
        }

        private static void Rebuild()
        {
            _entries = [];
            _station = "";
            if (!NavGraph.TryDockedStation(out string owner, out Room[]? rooms)) return;

            List<(int Index, Vector3 World)> nodes = NavGraph.WorldNodesOf(owner);
            List<Room> listed = [];
            List<string> names = [];
            foreach (Room room in rooms)
            {
                if (room == null) continue;

                listed.Add(room);
                names.Add(room.gameObject.name);
            }
            _station = owner;
            if (nodes.Count == 0 || listed.Count == 0) return;

            // A room has no volume: its furniture stands for it. docs/navigation.md#rooms-by-name
            List<Vector3[]> clouds = [];
            foreach (Room room in listed)
            {
                Transform[] parts = room.GetComponentsInChildren<Transform>(true);
                Vector3[] points = new Vector3[parts.Length];
                for (int i = 0; i < parts.Length; i++) points[i] = parts[i].position;
                clouds.Add(points);
            }

            List<(int Index, Vector3 World)>[] owned = new List<(int, Vector3)>[listed.Count];
            for (int r = 0; r < owned.Length; r++) owned[r] = [];
            foreach ((int Index, Vector3 World) node in nodes)
            {
                int nearest = 0;
                float nearestSqr = float.MaxValue;
                for (int r = 0; r < clouds.Count; r++)
                {
                    foreach (Vector3 point in clouds[r])
                    {
                        float sqr = (point - node.World).sqrMagnitude;
                        if (sqr >= nearestSqr) continue;

                        nearestSqr = sqr;
                        nearest = r;
                    }
                }
                owned[nearest].Add(node);
            }

            string prefix = CommonPrefix(names);
            List<Entry> entries = [];
            for (int r = 0; r < listed.Count; r++)
            {
                if (owned[r].Count == 0) continue;

                Vector3 middle = Vector3.zero;
                foreach (Vector3 point in clouds[r]) middle += point;
                middle /= clouds[r].Length;
                owned[r].Sort((a, b) =>
                    (a.World - middle).sqrMagnitude.CompareTo((b.World - middle).sqrMagnitude));

                List<string> keys = [Squash(names[r])];
                string shortKey = Squash(names[r][prefix.Length..]);
                if (prefix.Length > 0 && shortKey.Length >= MinShortKey) keys.Add(shortKey);

                int[] indices = new int[owned[r].Count];
                for (int i = 0; i < indices.Length; i++) indices[i] = owned[r][i].Index;
                entries.Add(new Entry(names[r], [.. keys], indices));
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            _entries = [.. entries];

            StringBuilder line = new();
            foreach (Entry e in _entries) line.Append(' ').Append(e.Name).Append(" #").Append(e.Nodes[0]);
            // Only when the answer changes: the list is rebuilt every few seconds while it is looked at.
            string summary = "[nav] Rooms at " + owner + " (" + nodes.Count + " nodes), goto node:" + line;
            if (summary == _lastLogged) return;

            _lastLogged = summary;
            NpcLog.Log.LogInfo(summary);
        }

        /// <summary>
        /// The leading part every name shares ("Yard", "Oxygen"), cut at a word start.
        /// </summary>
        private static string CommonPrefix(List<string> names)
        {
            if (names.Count < 2) return "";

            string prefix = names[0];
            foreach (string name in names)
            {
                int n = 0;
                while (n < prefix.Length && n < name.Length && prefix[n] == name[n]) n++;
                prefix = prefix[..n];
            }
            // Back to where every name goes on with a capital, or the cut lands mid-word.
            while (prefix.Length > 0 && !names.TrueForAll(name => prefix.Length < name.Length && char.IsUpper(name[prefix.Length])))
            {
                prefix = prefix[..^1];
            }
            return prefix.Length >= MinShortKey ? prefix : "";
        }

        /// <summary>
        /// The room the text names. True when it names one at all; then `room` is set if it is
        /// unambiguous, else `several` lists what it could mean. `moreFiller`: words of the caller's
        /// own that name no room, such as what the player calls its NPC.
        /// </summary>
        public static bool TryResolve(string lower, out Entry? room, out List<Entry>? several,
            IReadOnlyCollection<string>? moreFiller = null)
        {
            room = null;
            several = null;
            Entry[] entries = Current(out _);
            if (entries.Length == 0) return false;

            // A whole room name in the text, longest first: "conservatory lb" beats "conservatory".
            string all = Squash(lower);
            int best = 0;
            List<Entry> hits = [];
            foreach (Entry e in entries)
            {
                int longest = 0;
                foreach (string key in e.Keys)
                {
                    if (all.Contains(key, StringComparison.Ordinal)) longest = Math.Max(longest, key.Length);
                }
                if (longest == 0 || longest < best) continue;

                if (longest > best) hits.Clear();

                best = longest;
                hits.Add(e);
            }

            // Otherwise what was said after "goto" is the start or middle of a name.
            if (hits.Count == 0)
            {
                string word = Squash(WithoutFiller(lower, moreFiller));
                if (word.Length < MinShortKey) return false;

                foreach (Entry e in entries)
                {
                    foreach (string key in e.Keys)
                    {
                        if (!key.Contains(word, StringComparison.Ordinal)) continue;

                        hits.Add(e);
                        break;
                    }
                }
                if (hits.Count == 0) return false;
            }

            if (hits.Count == 1) room = hits[0];
            else several = hits;
            return true;
        }

        /// <summary>
        /// "Cryo, Hallway, Library": room names for a reply.
        /// </summary>
        public static string Join(IEnumerable<Entry> entries)
        {
            List<string> names = [];
            foreach (Entry e in entries) names.Add(e.Name);
            return string.Join(", ", names);
        }

        /// <summary>
        /// Lower case letters and digits only, so "Conservatory LB", "conservatory_lb" and the
        /// game's own name are one string.
        /// </summary>
        private static string Squash(string text)
        {
            StringBuilder sb = new(text.Length);
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static string WithoutFiller(string lower, IReadOnlyCollection<string>? moreFiller)
        {
            StringBuilder kept = new();
            foreach (string word in lower.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries))
            {
                if (FillerWords.Contains(word) || (moreFiller != null && IsIn(moreFiller, word))) continue;

                kept.Append(word).Append(' ');
            }
            return kept.ToString();
        }

        private static readonly char[] WordBreaks = [' ', ',', '.', '!', '?', '\'', '_', '-'];

        private static bool IsIn(IReadOnlyCollection<string> words, string word)
        {
            foreach (string w in words)
            {
                if (w == word) return true;
            }
            return false;
        }
    }
}
