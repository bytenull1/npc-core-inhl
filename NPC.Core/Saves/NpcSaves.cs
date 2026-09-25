using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace NPC.Core.Saves
{
    /// <summary>
    /// Sidecar files beside the game's saves, one per mod: "&lt;save name&gt;.&lt;extension&gt;". NPC.Core
    /// writes, deletes and prunes them with their save; each mod owns its own format. The game never reads
    /// them, so uninstalling a mod leaves every save intact. docs/invariants.md#mod-state-never-enters-the-vanilla-save
    /// </summary>
    public static class NpcSaves
    {
        private sealed class Sidecar(string owner, string extension, Func<string, string?> write)
        {
            public readonly string Owner = owner;
            public readonly string Extension = extension;
            public readonly Func<string, string?> Write = write;
        }

        private static readonly List<Sidecar> Sidecars = [];
        private static readonly Regex ValidExtension = new("^[a-z0-9]+$");

        /// <summary>
        /// Adds a sidecar for `owner` (a mod name, for the log). `write` gets the save's name and returns
        /// the file's contents, or null for none, which deletes an old one: a stale sidecar next to a save it
        /// no longer describes brings back state that save does not have. False when the extension is taken,
        /// or is the game's own "json".
        /// </summary>
        public static bool RegisterSidecar(string owner, string extension, Func<string, string?> write)
        {
            string ext = extension.TrimStart('.').ToLowerInvariant();
            if (!ValidExtension.IsMatch(ext) || ext == "json")
            {
                NpcLog.Core.LogWarning($"[save] {owner}'s sidecar extension '{extension}' is not allowed - not registered");
                return false;
            }
            foreach (Sidecar existing in Sidecars)
            {
                if (existing.Extension != ext) continue;

                NpcLog.Core.LogWarning($"[save] Sidecar '.{ext}' already belongs to {existing.Owner} - {owner}'s is not registered");
                return false;
            }
            Sidecars.Add(new Sidecar(owner, ext, write));
            return true;
        }

        /// <summary>
        /// Where the sidecar with this extension of this save lives.
        /// </summary>
        public static string PathOf(string saveName, string extension) =>
            Path.Combine(SaveParser.SaveFilesPath, saveName + "." + extension.TrimStart('.'));

        /// <summary>
        /// A sidecar's contents; null when there is none or it cannot be read (that is logged).
        /// </summary>
        public static string? Read(string saveName, string extension)
        {
            string path = PathOf(saveName, extension);
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex)
            {
                NpcLog.Core.LogWarning($"[save] Failed to read sidecar '{path}': {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------
        // The lifecycle, from the SaveParser patches
        // ------------------------------------------------------------------

        /// <summary>
        /// Every sidecar, every time the game writes a save.
        /// </summary>
        internal static void WriteAll(string saveName)
        {
            if (string.IsNullOrEmpty(saveName)) return;

            foreach (Sidecar sidecar in Sidecars)
            {
                string path = PathOf(saveName, sidecar.Extension);
                try
                {
                    string? contents = sidecar.Write(saveName);
                    if (contents == null)
                    {
                        TryDelete(path);
                        continue;
                    }
                    WriteAtomically(path, contents);
                    NpcLog.Core.LogInfo($"[save] {sidecar.Owner} sidecar written: {path}");
                }
                catch (Exception ex)
                {
                    NpcLog.Core.LogError($"[save] Failed to write {sidecar.Owner}'s sidecar '{path}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Writes beside the target and swaps it in, so a crash mid-write leaves the old sidecar rather
        /// than a truncated one.
        /// </summary>
        private static void WriteAtomically(string path, string contents)
        {
            string temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, contents);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        /// <summary>
        /// A sidecar belongs to its save file and goes with it. Unconditional on purpose: a stale sidecar
        /// outlives the save that made it, and a new game with the recycled name then brings it back.
        /// </summary>
        internal static void DeleteAll(string saveName)
        {
            if (string.IsNullOrEmpty(saveName)) return;

            foreach (Sidecar sidecar in Sidecars) TryDelete(PathOf(saveName, sidecar.Extension));
        }

        /// <summary>
        /// The game wiped every save.
        /// </summary>
        internal static void ClearAll()
        {
            try
            {
                if (!Directory.Exists(SaveParser.SaveFilesPath)) return;

                foreach (Sidecar sidecar in Sidecars)
                {
                    foreach (string path in Directory.GetFiles(SaveParser.SaveFilesPath, "*." + sidecar.Extension)) TryDelete(path);
                }
            }
            catch (Exception ex)
            {
                NpcLog.Core.LogWarning($"[save] Failed to clear sidecars: {ex.Message}");
            }
        }

        /// <summary>
        /// Sidecars whose save is gone - deleted outside the game, or by a path not patched. Cheap enough
        /// to run wherever the game re-reads its save list.
        /// </summary>
        internal static void PruneOrphans()
        {
            try
            {
                if (!Directory.Exists(SaveParser.SaveFilesPath)) return;

                foreach (Sidecar sidecar in Sidecars)
                {
                    foreach (string path in Directory.GetFiles(SaveParser.SaveFilesPath, "*." + sidecar.Extension))
                    {
                        // The game's own existence test, so a changed save format never orphans every sidecar.
                        if (SaveParser.SaveFileExists(Path.GetFileNameWithoutExtension(path))) continue;

                        TryDelete(path);
                    }
                }
            }
            catch (Exception ex)
            {
                NpcLog.Core.LogWarning($"[save] Failed to prune sidecars: {ex.Message}");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return;

                File.Delete(path);
                NpcLog.Core.LogInfo($"[save] Sidecar deleted: {path}");
            }
            catch (Exception ex)
            {
                NpcLog.Core.LogWarning($"[save] Failed to delete sidecar '{path}': {ex.Message}");
            }
        }
    }
}
