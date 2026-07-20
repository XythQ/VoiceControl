using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace XNPCVoiceControl.Core
{
    /// <summary>
    /// Holds metadata for a single voice clip file.
    /// </summary>
    public class ClipEntry
    {
        public string AudioPath;   // loose OGG full path
        public string Subtitle;    // from .txt sidecar; may be null
        public string LocKey;      // OGG basename = Localization.txt key

        /// <summary>
        /// Resolve the subtitle: game Localization (player's language) wins,
        /// then the .txt sidecar, then null. Localization.Get handles all
        /// languages and auto-falls-back to English.
        /// </summary>
        public string ResolveSubtitle()
        {
            if (!string.IsNullOrEmpty(LocKey) && Localization.Exists(LocKey))
                return Localization.Get(LocKey);
            return Subtitle;
        }
    }

    internal class VoiceClipFolder
    {
        public List<ClipEntry> Greetings  = new List<ClipEntry>();
        public List<ClipEntry> Defaults   = new List<ClipEntry>();
        public List<ClipEntry> Backstory  = new List<ClipEntry>();
        public Dictionary<string, List<ClipEntry>> Triggers = new Dictionary<string, List<ClipEntry>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Index of voice clips loaded from Resources/NPC_Voices in all loaded mods.
    /// Built once at mod init.  Loose-OGG only - no AssetBundle logic.
    /// </summary>
    public class VoiceClipLibrary
    {
        public static VoiceClipLibrary Instance { get; } = new VoiceClipLibrary();

        private Dictionary<string, VoiceClipFolder> _folders = new Dictionary<string, VoiceClipFolder>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Scan all loaded mods for Resources/NPC_Voices and build the index.
        /// Call once during mod initialization.
        /// </summary>
        public void Build()
        {
            int folderCount = 0;
            int clipCount = 0;

            foreach (Mod mod in ModManager.GetLoadedMods())
            {
                string voicesDir = Path.Combine(mod.Path, "Resources", "NPC_Voices");
                if (!Directory.Exists(voicesDir)) continue;

                foreach (string folder in Directory.GetDirectories(voicesDir))
                {
                    string folderName = Path.GetFileName(folder);
                    var holder = new VoiceClipFolder();

                    // Greetings: scan <folder>/Greetings/*.ogg
                    string greetingsPath = Path.Combine(folder, "Greetings");
                    if (Directory.Exists(greetingsPath))
                    {
                        foreach (string ogg in Directory.GetFiles(greetingsPath, "*.ogg", SearchOption.TopDirectoryOnly))
                        {
                            var entry = new ClipEntry { AudioPath = ogg };
                            entry.Subtitle = ReadSidecar(ogg);
                            entry.LocKey = Path.GetFileNameWithoutExtension(ogg);
                            holder.Greetings.Add(entry);
                            clipCount++;
                        }
                    }

                    // Backstory: scan <folder>/Backstory/*.ogg — played for CustomDialog triggers (Clips mode)
                    string backstoryPath = Path.Combine(folder, "Backstory");
                    if (Directory.Exists(backstoryPath))
                    {
                        foreach (string ogg in Directory.GetFiles(backstoryPath, "*.ogg", SearchOption.TopDirectoryOnly))
                        {
                            var entry = new ClipEntry { AudioPath = ogg };
                            entry.Subtitle = ReadSidecar(ogg);
                            entry.LocKey = Path.GetFileNameWithoutExtension(ogg);
                            holder.Backstory.Add(entry);
                            clipCount++;
                        }
                    }

                    // Default: scan <folder>/Default/*.ogg — played when no trigger matches (Clips mode)
                    string defaultPath = Path.Combine(folder, "Default");
                    if (Directory.Exists(defaultPath))
                    {
                        foreach (string ogg in Directory.GetFiles(defaultPath, "*.ogg", SearchOption.TopDirectoryOnly))
                        {
                            var entry = new ClipEntry { AudioPath = ogg };
                            entry.Subtitle = ReadSidecar(ogg);
                            entry.LocKey = Path.GetFileNameWithoutExtension(ogg);
                            holder.Defaults.Add(entry);
                            clipCount++;
                        }
                    }

                    // Triggers: for each subdir under <folder>/Triggers/, key = subdir name lowercased
                    string triggersPath = Path.Combine(folder, "Triggers");
                    if (Directory.Exists(triggersPath))
                    {
                        foreach (string triggerFolder in Directory.GetDirectories(triggersPath))
                        {
                            string key = Path.GetFileName(triggerFolder).ToLowerInvariant();
                            var entries = new List<ClipEntry>();
                            foreach (string ogg in Directory.GetFiles(triggerFolder, "*.ogg", SearchOption.TopDirectoryOnly))
                            {
                                var entry = new ClipEntry { AudioPath = ogg };
                                entry.Subtitle = ReadSidecar(ogg);
                                entry.LocKey = Path.GetFileNameWithoutExtension(ogg);
                                entries.Add(entry);
                                clipCount++;
                            }
                            if (entries.Count > 0)
                                holder.Triggers[key] = entries;
                        }
                    }

                    if (holder.Greetings.Count > 0 || holder.Backstory.Count > 0 || holder.Defaults.Count > 0 || holder.Triggers.Count > 0)
                    {
                        _folders[folderName] = holder;
                        folderCount++;
                    }
                }
            }

            Log.Out($"VoiceClipLibrary: {folderCount} folders, {clipCount} clips");
            if (clipCount > 0) PrewarmClips();
        }

        private void PrewarmClips()
        {
            Log.Out("VoiceClipLibrary: pre-warming clip cache...");
            foreach (var holder in _folders.Values)
            {
                foreach (var entry in holder.Greetings)
                    VoiceClipLoader.GetClip(entry, _ => { });
                foreach (var entry in holder.Backstory)
                    VoiceClipLoader.GetClip(entry, _ => { });
                foreach (var entry in holder.Defaults)
                    VoiceClipLoader.GetClip(entry, _ => { });
                foreach (var pool in holder.Triggers.Values)
                    foreach (var entry in pool)
                        VoiceClipLoader.GetClip(entry, _ => { });
            }
        }

        /// <summary>
        /// Pick a random greeting clip for the given voice clip folder.
        /// Returns false if folder not found or no greetings loaded.
        /// </summary>
        public bool TryGetGreeting(string folder, out ClipEntry entry)
        {
            if (!_folders.TryGetValue(folder, out var holder))
            {
                entry = null;
                return false;
            }
            if (holder.Greetings.Count == 0)
            {
                entry = null;
                return false;
            }
            entry = holder.Greetings[UnityEngine.Random.Range(0, holder.Greetings.Count)];
            return true;
        }

        /// <summary>
        /// Pick a random trigger clip for the given folder and action name.
        /// actionName is case-insensitive.  Returns false on any miss.
        /// </summary>
        public bool TryGetTrigger(string folder, string actionName, out ClipEntry entry)
        {
            if (string.IsNullOrEmpty(actionName)) { entry = null; return false; }
            if (!_folders.TryGetValue(folder, out var holder))
            {
                entry = null;
                return false;
            }
            string key = actionName.ToLowerInvariant();
            if (!holder.Triggers.TryGetValue(key, out var pool))
            {
                entry = null;
                return false;
            }
            if (pool.Count == 0)
            {
                entry = null;
                return false;
            }
            entry = pool[UnityEngine.Random.Range(0, pool.Count)];
            return true;
        }

        /// <summary>
        /// True if a clip folder with the given name was indexed (has any clips).
        /// </summary>
        public bool HasFolder(string folder)
        {
            return !string.IsNullOrEmpty(folder) && _folders.ContainsKey(folder);
        }

        /// <summary>
        /// Pick a random backstory clip for the given folder.
        /// Used by Clips-mode NPCs for CustomDialog (story) triggers.
        /// </summary>
        public bool TryGetBackstory(string folder, out ClipEntry entry)
        {
            if (!_folders.TryGetValue(folder, out var holder))
            { entry = null; return false; }
            if (holder.Backstory.Count == 0)
            { entry = null; return false; }
            entry = holder.Backstory[UnityEngine.Random.Range(0, holder.Backstory.Count)];
            return true;
        }

        /// <summary>
        /// Pick a random default-response clip for the given folder.
        /// Used by Clips-mode NPCs when no trigger matches player input.
        /// </summary>
        public bool TryGetDefault(string folder, out ClipEntry entry)
        {
            if (!_folders.TryGetValue(folder, out var holder))
            { entry = null; return false; }
            if (holder.Defaults.Count == 0)
            { entry = null; return false; }
            entry = holder.Defaults[UnityEngine.Random.Range(0, holder.Defaults.Count)];
            return true;
        }

        private static string ReadSidecar(string oggPath)
        {
            string txtPath = oggPath.Substring(0, oggPath.Length - 4) + ".txt";
            if (File.Exists(txtPath))
            {
                try
                {
                    return File.ReadAllText(txtPath).Trim();
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }
    }
}
