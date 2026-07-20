using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Platform;
using UnityEngine;

namespace XNPCVoiceControl
{
    /// <summary>
    /// RAG long-term memory system for NPCs.
    /// Stores vector-embedded conversation summaries per (player, NPC) relationship.
    /// Uses cosine similarity + temporal decay for relevance scoring.
    /// Zero-GC math, zero LINQ, XmlSerializer serialization, async disk I/O.
    /// 
    /// Key format: "{playerId}_{npcName}" — survives entityId volatility across restarts.
    /// </summary>
    public class NPCMemoryManager : MonoBehaviour
    {
        private static NPCMemoryManager _instance;

        public static NPCMemoryManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("NPCMemoryManager");
                    _instance = go.AddComponent<NPCMemoryManager>();
                    DontDestroyOnLoad(go);
                    _instance.Initialize();
                }
                return _instance;
            }
        }

        // In-memory store: "{playerName}_{npcName}" → memory profile
        private Dictionary<string, NPCMemoryProfile> _memoryStore = new Dictionary<string, NPCMemoryProfile>();

        // Initialization gate — blocks retrieval until disk load completes
#pragma warning disable CS0414 // field assigned but never used — retained for future diagnostics
        private volatile bool _isInitialized = false;
#pragma warning restore CS0414
        private volatile bool _storeLoaded = false;  // set by Load() alone — retrieval only needs this, not the full pending-backlog drain
        private int _pendingInitTasks = 0;
        private readonly object _initLock = new object();
        private readonly object _lock = new object();
        private readonly object _fileIoLock = new object();  // guards actual file I/O on NPC_Memories.xml (separate from _lock, which guards the in-memory dictionary)

        // Disk I/O guard — prevent concurrent saves
        private int _isSaving;

        // Guard against double-save on shutdown (Harmony patch + OnApplicationQuit both fire)
        private bool _shutdownSaveComplete = false;

        // Dedi-server gate — one-time log
        private static bool _dediSkipLogged = false;

        // Constants
        private const int MaxMemoriesPerRelationship = 100;
        private const string MemoryFileName = "NPC_Memories.xml";
        private const float TemporalDecayPerDay = 0.01f;
        private const int TopKResults = 5;
        private const int MaxContextChars = 1200;

        /// <summary>
        /// Build the composite key for a player-NPC relationship.
        /// </summary>
        public static string BuildKey(string playerName, string npcName)
        {
            if (string.IsNullOrEmpty(playerName))
                playerName = "Survivor";
            if (string.IsNullOrEmpty(npcName))
                npcName = "Unknown";
            return $"{playerName}_{npcName}";
        }

        /// <summary>
        /// Parse a composite key back into its components.
        /// </summary>
        public static bool ParseKey(string key, out string playerName, out string npcName)
        {
            playerName = "";
            npcName = "";

            if (string.IsNullOrEmpty(key)) return false;

            int lastUnderscoreIdx = key.LastIndexOf('_');
            if (lastUnderscoreIdx <= 0 || lastUnderscoreIdx >= key.Length - 1) return false;

            playerName = key.Substring(0, lastUnderscoreIdx);
            npcName = key.Substring(lastUnderscoreIdx + 1);
            return true;
        }

        /// <summary>
        /// Load memories from disk on a background thread.
        /// </summary>
        private void Initialize()
        {
            Log.Debug(() => "[RAG] NPCMemoryManager initializing...");
            _pendingInitTasks = 2; // Load + LoadPendingFiles
            Load();
            // Process any pending buffers from previous quit
            LoadPendingFiles();
        }

        /// <summary>
        /// Called when each init task completes. Sets _isInitialized when all are done.
        /// Uses a lock to prevent thread collision on the decrement.
        /// </summary>
        private void OnInitTaskComplete()
        {
            lock (_initLock)
            {
                _pendingInitTasks--;
                if (_pendingInitTasks <= 0)
                {
                    _isInitialized = true;
                    Log.Debug(() => "[RAG] Initialization complete — memory store ready");
                }
            }
        }

        /// <summary>
        /// Called by Unity on game quit. Triggers a final save and waits for it to complete.
        /// </summary>
        void OnApplicationQuit()
        {
            Log.Debug(() => "[RAG] OnApplicationQuit — saving memories...");
            SaveAndWait();
        }

        /// <summary>
        /// Synchronously save all pending buffers from active chat components.
        /// Called during game quit BEFORE killing servers, so async consolidations can still complete.
        /// </summary>
        public void SaveAllPendingBuffers()
        {
            try
            {
                var allComponents = FindObjectsOfType<NPCChatComponent>();
                for (int i = 0; i < allComponents.Length; i++)
                {
                    try
                    {
                        allComponents[i].SavePendingBufferOnQuit();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[RAG] Failed to save pending buffer for component: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[RAG] SaveAllPendingBuffers failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Synchronous save that runs entirely on the calling thread.
        /// Called from Harmony ApplicationQuitPatch which already runs on main thread.
        /// XmlSerializer is pure .NET — works on any thread, survives Unity shutdown.
        /// </summary>
        public void SaveAndWait()
        {
            // MP Phase 3: replace with custodian logic
            if (GameManager.IsDedicatedServer)
            {
                Log.Out("[RAG] SaveAndWait skipped on dedi — memory persistence disabled (becomes server-side custodian in MP Phase 3)");
                return;
            }

            // Guard: only save once on shutdown (Harmony patch + OnApplicationQuit both call this)
            if (_shutdownSaveComplete)
            {
                Log.Out($"[RAG] SaveAndWait skipped — already saved on shutdown");
                return;
            }
            _shutdownSaveComplete = true;

            try
            {
                // Snapshot the store
                NPCMemoryProfile[] profiles;
                lock (_lock)
                {
                    profiles = new NPCMemoryProfile[_memoryStore.Count];
                    int i = 0;
                    foreach (var kvp in _memoryStore)
                        profiles[i++] = kvp.Value;
                }

                // Diagnostic: log what's actually in memory
                Log.Out($"[RAG] Shutdown save: {_memoryStore.Count} keys, {profiles.Length} profiles");
                for (int i = 0; i < profiles.Length; i++)
                {
                    var p = profiles[i];
                    Log.Out($"[RAG]   Profile[{i}]: player={p.playerName}, npc={p.npcName}, memories={p.memories?.Length ?? 0}");
                    if (p.memories != null && p.memories.Length > 0)
                        Log.Out($"[RAG]     Memory[0]: summary=\"{p.memories[0].summary.Substring(0, Math.Min(60, p.memories[0].summary.Length))}\", dim={p.memories[0].vectorDim}");
                }

                // Serialize with XmlSerializer (pure .NET, any thread)
                var store = new NPCMemoryStore { profiles = profiles };
                var serializer = new XmlSerializer(typeof(NPCMemoryStore));
                string path = GetMemoryFilePath();

                lock (_fileIoLock)
                {
                    using (var writer = new StreamWriter(path))
                    {
                        serializer.Serialize(writer, store);
                    }
                }

                Log.Out($"[RAG] Saved memories to: {path} ({profiles.Length} profiles) — shutdown complete");
            }
            catch (Exception ex)
            {
                Log.Warning($"[RAG] Failed to save memory file on quit: {ex.Message}\n{ex.StackTrace}");
            }
        }

        #region Disk I/O (Background Thread, XmlSerializer)

        /// <summary>
        /// Load NPC_Memories.xml from mod folder on a background thread.
        /// </summary>
        private void Load()
        {
            // MP Phase 3: replace with custodian logic
            if (GameManager.IsDedicatedServer)
            {
                _storeLoaded = true;
                OnInitTaskComplete();
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string path = GetMemoryFilePath();

                    NPCMemoryStore store;
                    lock (_fileIoLock)
                    {
                        if (!File.Exists(path))
                        {
                            Log.Debug(() => "[RAG] No existing memory file found, starting fresh");
                            return;
                        }

                        var serializer = new XmlSerializer(typeof(NPCMemoryStore));
                        using (var reader = new StreamReader(path))
                        {
                            store = (NPCMemoryStore)serializer.Deserialize(reader);
                        }
                    }

                    if (store?.profiles == null) return;

                    // MIGRATION: rewrite volatile "Player_<entityId>" keys to stable player ID.
                    // Player.name embeds per-session entityId (e.g. "Player_171") — next session differs → orphaned memories.
                    string stablePlayerId = ResolveStablePlayerId();
                    for (int i = 0; i < store.profiles.Length; i++)
                    {
                        var p = store.profiles[i];
                        if (System.Text.RegularExpressions.Regex.IsMatch(p.playerName, @"^Player_\d+$"))
                        {
                            if (!string.IsNullOrEmpty(stablePlayerId))
                            {
                                string oldKey = BuildKey(p.playerName, p.npcName);
                                p.playerName = stablePlayerId;
                                Log.Out($"[RAG] Migrated profile {oldKey} → {BuildKey(p.playerName, p.npcName)}");
                            }
                            // else: stable ID unresolvable at load time — leave as-is, migrate lazily on first retrieval
                        }
                    }

                    // Populate in-memory store (separate lock — _lock guards the dictionary, _fileIoLock already released)
                    lock (_lock)
                        {
                            _memoryStore.Clear();
                            for (int i = 0; i < store.profiles.Length; i++)
                            {
                                var profile = store.profiles[i];
                                if (profile.memories == null || profile.memories.Length == 0)
                                    continue;

                                // Sanitize: drop placeholder junk memories (e.g. "[SURVIVOR]: no fact")
                                // that leaked through the LLM on older versions.
                                var cleanMemories = new List<MemoryEntry>();
                                int droppedJunk = 0;
                                for (int m = 0; m < profile.memories.Length; m++)
                                {
                                    string summary = profile.memories[m].summary ?? "";

                                    // Extract content after [SURVIVOR] or [NPC] tag and check each segment
                                    bool isJunk = true;
                                    string[] segments = summary.Split(',');
                                    for (int s = 0; s < segments.Length; s++)
                                    {
                                        string seg = segments[s].Trim();
                                        int bracketEnd = seg.IndexOf(']');
                                        if (bracketEnd >= 0)
                                        {
                                            string afterTag = seg.Substring(bracketEnd + 1).Trim();
                                            if (!LLMService.IsPlaceholderFact(afterTag))
                                            {
                                                isJunk = false;
                                                break;
                                            }
                                        }
                                        else if (!LLMService.IsPlaceholderFact(seg))
                                        {
                                            isJunk = false;
                                            break;
                                        }
                                    }

                                    if (isJunk)
                                    {
                                        droppedJunk++;
                                    }
                                    else
                                    {
                                        cleanMemories.Add(profile.memories[m]);
                                    }
                                }

                                if (cleanMemories.Count > 0)
                                {
                                    profile.memories = cleanMemories.ToArray();
                                    string key = BuildKey(profile.playerName, profile.npcName);
                                    // Migration merge: if a profile with this key already exists (old+new collided), merge
                                    if (_memoryStore.TryGetValue(key, out NPCMemoryProfile existing))
                                    {
                                        MergeProfiles(existing, profile);
                                        Log.Out($"[RAG] Merged duplicate profile for {key} ({existing.memories.Length} memories)");
                                    }
                                    else
                                    {
                                        _memoryStore[key] = profile;
                                    }
                                    if (droppedJunk > 0)
                                        Log.Debug(() => $"[RAG] Sanitized {profile.npcName}: dropped {droppedJunk} junk memories, kept {cleanMemories.Count}");
                                }
                                else if (droppedJunk > 0)
                                {
                                    Log.Debug(() => $"[RAG] Dropped entire profile for {profile.npcName}: all {droppedJunk} memories were placeholder junk");
                                }
                            }
                        }

                        int count;
                        lock (_lock) { count = _memoryStore.Count; }
                        Log.Out($"[RAG] Loaded memories for {count} relationships from disk");
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RAG] Failed to load memory file: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    _storeLoaded = true;
                    OnInitTaskComplete();
                }
            });
        }

        /// <summary>
        /// Save all memories to disk on a background thread.
        /// XmlSerializer is pure .NET — no main thread required.
        /// Guarded against concurrent saves.
        /// </summary>
        public void Save()
        {
            if (GameManager.IsDedicatedServer)
            {
                if (!_dediSkipLogged)
                {
                    _dediSkipLogged = true;
                    Log.Out("[VoiceMod] Dedicated server — NPC memory persistence disabled (becomes server-side custodian in MP Phase 3)");
                }
                return;
            }

            if (Interlocked.Exchange(ref _isSaving, 1) != 0)
                return; // Another save in progress

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    NPCMemoryProfile[] profiles;
                    lock (_lock)
                    {
                        profiles = new NPCMemoryProfile[_memoryStore.Count];
                        int i = 0;
                        foreach (var kvp in _memoryStore)
                            profiles[i++] = kvp.Value;
                    }

                    var store = new NPCMemoryStore { profiles = profiles };
                    var serializer = new XmlSerializer(typeof(NPCMemoryStore));
                    string path = GetMemoryFilePath();

                    lock (_fileIoLock)
                    {
                        using (var writer = new StreamWriter(path))
                        {
                            serializer.Serialize(writer, store);
                        }
                    }

                    Log.Out($"[RAG] Saved memories to: {path} ({profiles.Length} profiles)");
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RAG] Failed to save memory file: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    Interlocked.Exchange(ref _isSaving, 0);
                }
            });
        }

        /// <summary>
        /// Remove a specific relationship's memory profile.
        /// </summary>
        public void RemoveRelationship(string playerName, string npcName)
        {
            string key = BuildKey(playerName, npcName);
            lock (_lock)
            {
                if (_memoryStore.Remove(key))
                {
                    Log.Debug(() => $"[RAG] Removed memory profile for relationship: {key}");
                    Save();
                }
            }
        }

        private string GetMemoryFilePath()
        {
            try
            {
                string assemblyDir = Path.GetDirectoryName(typeof(NPCMemoryManager).Assembly.Location);
                if (!string.IsNullOrEmpty(assemblyDir))
                    return Path.Combine(assemblyDir, MemoryFileName);
            }
            catch { /* assembly location not available */ }
            return MemoryFileName; // fallback to working directory
        }

        /// <summary>
        /// Get the file path for a pending memory buffer for a specific relationship.
        /// </summary>
        public static string GetPendingFilePath(string playerName, string npcName)
        {
            string safePlayer = SanitizeFileName(playerName ?? "Survivor");
            string safeName = SanitizeFileName(npcName ?? "Unknown");

            try
            {
                string assemblyDir = Path.GetDirectoryName(typeof(NPCMemoryManager).Assembly.Location);
                if (!string.IsNullOrEmpty(assemblyDir))
                    return Path.Combine(assemblyDir, $"NPC_PendingMemories_{safePlayer}_{safeName}.xml");
            }
            catch { /* assembly location not available */ }
            return $"NPC_PendingMemories_{safePlayer}_{safeName}.xml"; // fallback to working directory
        }

        /// <summary>
        /// Sanitize a string for use in filenames.
        /// </summary>
        private static string SanitizeFileName(string input)
        {
            if (string.IsNullOrEmpty(input)) return "Unknown";
            char[] sanitized = new char[input.Length];
            int idx = 0;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                    sanitized[idx++] = c;
                else if (idx == 0)
                    sanitized[idx++] = '_';
                // else: skip non-alphanumeric chars in middle of string
            }
            return new string(sanitized, 0, idx);
        }

        /// <summary>
        /// Resolve the stable player ID for migration. Mirrors GetPlayerPersistentId's fallback chain
        /// but works without an EntityPlayer reference (called from background thread during Load).
        /// </summary>
        private static string ResolveStablePlayerId()
        {
            try
            {
                var localId = PlatformManager.InternalLocalUserIdentifier;
                if (localId != null)
                    return localId.CombinedString;
            }
            catch { /* PlatformManager not ready */ }

            try
            {
                string prefName = GamePrefs.GetString(EnumGamePrefs.PlayerName);
                if (!string.IsNullOrEmpty(prefName))
                    return prefName;
            }
            catch { /* prefs not loaded */ }

            return null; // unresolvable — leave old key, migrate lazily on first retrieval
        }

        /// <summary>
        /// Merge two profiles with the same key (migration collision).
        /// Concatenates memories respecting 100-cap LRU, keeps non-empty playerGivenName,
        /// keeps the older (smaller ≥0) hireDay.
        /// </summary>
        private static void MergeProfiles(NPCMemoryProfile existing, NPCMemoryProfile incoming)
        {
            // Merge memories: existing first, then incoming, cap at MaxMemoriesPerRelationship
            int total = existing.memories.Length + incoming.memories.Length;
            int cap = Math.Min(total, MaxMemoriesPerRelationship);
            var merged = new List<MemoryEntry>(cap);
            for (int i = 0; i < existing.memories.Length && merged.Count < cap; i++)
                merged.Add(existing.memories[i]);
            for (int i = 0; i < incoming.memories.Length && merged.Count < cap; i++)
                merged.Add(incoming.memories[i]);
            existing.memories = merged.ToArray();

            // Keep non-empty playerGivenName (prefer existing, fallback to incoming)
            if (string.IsNullOrEmpty(existing.playerGivenName) && !string.IsNullOrEmpty(incoming.playerGivenName))
                existing.playerGivenName = incoming.playerGivenName;

            // Keep older (smaller ≥0) hireDay
            if (incoming.hireDay >= 0)
            {
                if (existing.hireDay < 0 || incoming.hireDay < existing.hireDay)
                    existing.hireDay = incoming.hireDay;
            }
        }

        /// <summary>
        /// Write a conversation buffer to a pending file so next boot retries consolidation.
        /// Used when LLM/embed server is down — facts are not silently lost.
        /// </summary>
        private void WritePendingFile(string playerName, string npcName, List<ChatMessage> buffer)
        {
            try
            {
                string path = GetPendingFilePath(playerName, npcName);
                var pendingMessages = new NPCPendingMessage[buffer.Count];
                for (int i = 0; i < buffer.Count; i++)
                {
                    pendingMessages[i] = new NPCPendingMessage
                    {
                        role = buffer[i].Role,
                        content = buffer[i].Content
                    };
                }

                var store = new NPCPendingMemoryStore
                {
                    playerName = playerName ?? "Survivor",
                    npcName = npcName ?? "Unknown",
                    messages = pendingMessages
                };

                var serializer = new XmlSerializer(typeof(NPCPendingMemoryStore));
                using (var writer = new StreamWriter(path))
                {
                    serializer.Serialize(writer, store);
                }

                Log.Debug(() => $"[RAG] Re-queued {pendingMessages.Length} messages to pending file for {npcName}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[RAG] Failed to write pending file for {npcName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Process pending memory files from previous quit on next boot.
        /// Silently consolidates them into embeddings in the background.
        /// Waits for sidecar server readiness before first attempt, then schedules
        /// a same-session retry backstop if anything still fails after the wait.
        /// </summary>
        private void LoadPendingFiles()
        {
            // MP Phase 3: replace with custodian logic
            if (GameManager.IsDedicatedServer)
            {
                OnInitTaskComplete();
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string assemblyDir;
                    try { assemblyDir = Path.GetDirectoryName(typeof(NPCMemoryManager).Assembly.Location); }
                    catch { return; }

                    if (string.IsNullOrEmpty(assemblyDir)) return;

                    // Quick check: are there any pending files at all?
                    string[] pendingFiles = Directory.GetFiles(assemblyDir, "NPC_PendingMemories_*.xml");
                    if (pendingFiles.Length == 0)
                    {
                        Log.Debug(() => "[RAG] No pending memory files to process");
                        return;
                    }

                    Log.Debug(() => $"[RAG] Found {pendingFiles.Length} pending file(s), waiting for server readiness...");

                    // Wait for LLM + embed servers to be ready before consolidation.
                    // LoadPendingFiles fires at mod init, same moment StartServers() kicks off sidecars,
                    // so we systematically race their 15-30s startup without this wait.
                    int waitedMs = 0;
                    const int maxWaitMs = 60000; // 60s — generous vs documented worst-case startup
                    while (waitedMs < maxWaitMs && !(ServerManager.IsServerReady && ServerManager.IsEmbedServerReady))
                    {
                        Task.Delay(1000).Wait();
                        waitedMs += 1000;
                    }

                    if (!(ServerManager.IsServerReady && ServerManager.IsEmbedServerReady))
                    {
                        Log.Warning($"[RAG] Pending files found but servers not ready after {maxWaitMs/1000}s — attempting anyway, will retry later this session if it fails");
                    }
                    else
                    {
                        Log.Debug(() => $"[RAG] Servers ready after {waitedMs}ms wait, proceeding with pending files");
                    }

                    bool allOk = ProcessPendingFilesOnce(assemblyDir);

                    // Same-session retry backstop: if something still failed even after the readiness wait,
                    // schedule one delayed retry ~3 minutes later (well past any startup congestion).
                    if (!allOk)
                    {
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            Task.Delay(180000).Wait(); // 3 min backoff
                            Log.Debug(() => "[RAG] Retrying pending-file processing after startup-congestion backoff");
                            ProcessPendingFilesOnce(assemblyDir);
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RAG] Failed to scan for pending files: {ex.Message}");
                }
                finally
                {
                    OnInitTaskComplete();
                }
            });
        }

        /// <summary>
        /// Scan and process all pending memory files in the given directory.
        /// Returns true if ALL found pending files were fully processed and deleted,
        /// false if any remain (consolidation failed or timed out for at least one).
        /// </summary>
        private bool ProcessPendingFilesOnce(string assemblyDir)
        {
            string[] pendingFiles = Directory.GetFiles(assemblyDir, "NPC_PendingMemories_*.xml");
            if (pendingFiles.Length == 0) return true;

            bool allOk = true;

            for (int i = 0; i < pendingFiles.Length; i++)
            {
                try
                {
                    // Step 1: Deserialize — read inside using, close reader before any file ops.
                    NPCPendingMemoryStore store = null;
                    var serializer = new XmlSerializer(typeof(NPCPendingMemoryStore));
                    using (var reader = new StreamReader(pendingFiles[i]))
                    {
                        store = (NPCPendingMemoryStore)serializer.Deserialize(reader);
                    }
                    // StreamReader closed — file handle released.

                    if (store?.messages == null || store.messages.Length == 0)
                    {
                        // Empty file — delete it so we don't retry forever.
                        try { File.Delete(pendingFiles[i]); } catch { /* best effort */ }
                        continue;
                    }

                    Log.Debug(() => $"[RAG] Processing {store.messages.Length} pending messages for NPC {store.npcName} (player: {store.playerName})");

                    // Convert pending messages to ChatMessage list for consolidation
                    var chatMessages = new List<ChatMessage>();
                    for (int j = 0; j < store.messages.Length; j++)
                    {
                        chatMessages.Add(new ChatMessage(store.messages[j].role, store.messages[j].content));
                    }

                    // Step 2: Consolidate (separate try/catch from delete).
                    bool consolidationOk = false;
                    try
                    {
                        // TASK D: Don't run RAG consolidation during active voice interaction.
                        // Defer up to 30s — if player is mid-conversation, wait for silence.
                        int deferMs = 0;
                        while (TTS.NPCAudioPlayer.IsAnySpeaking && deferMs < 30000)
                        {
                            Task.Delay(500).Wait();
                            deferMs += 500;
                        }
                        if (TTS.NPCAudioPlayer.IsAnySpeaking)
                        {
                            Log.Debug(() => $"[RAG] Defer limit reached for {pendingFiles[i]}, processing anyway");
                        }
                        else if (deferMs > 0)
                        {
                            Log.Debug(() => $"[RAG] Deferred consolidation {deferMs}ms for active voice, now proceeding");
                        }

                        var task = ConsolidateBufferAsync(store.playerName, store.npcName, chatMessages);
                        consolidationOk = task.Wait(15000); // 15s timeout — LLM + embedding under normal load
                    }
                    catch (Exception consEx)
                    {
                        Log.Warning($"[RAG] Consolidation failed for {pendingFiles[i]}, file retained for next boot: {consEx.Message}");
                        consolidationOk = false;
                    }

                    if (!consolidationOk)
                    {
                        allOk = false; // at least one file remains — mark for retry
                        continue; // consolidation failed or timed out — keep file for retry
                    }

                    // Step 3: Delete the pending file (separate try/catch, with retry).
                    bool deleted = false;
                    for (int attempt = 0; attempt < 3 && !deleted; attempt++)
                    {
                        try
                        {
                            File.Delete(pendingFiles[i]);
                            deleted = true;
                            Log.Debug(() => $"[RAG] Deleted pending file {Path.GetFileName(pendingFiles[i])}");
                        }
                        catch (IOException)
                        {
                            if (attempt < 2)
                                Task.Delay(200).Wait(); // backoff before retry
                        }
                    }
                    if (!deleted)
                    {
                        Log.Warning($"[RAG] Delete failed for {pendingFiles[i]} after retries, file retained for next boot");
                        allOk = false;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RAG] Failed to process pending file {pendingFiles[i]}: {ex.Message}");
                    allOk = false;
                }
            }

            return allOk;
        }

        #endregion

        #region Cosine Similarity (Zero-GC, for-loop only)

        /// <summary>
        /// Calculate cosine similarity between two vectors, then apply temporal decay.
        /// Zero allocations — uses only local float variables and a for loop.
        /// </summary>
        public static float GetRelevanceScore(float[] playerVector, MemoryEntry memory)
        {
            if (playerVector == null || memory.vector == null)
                return float.NegativeInfinity;

            // Dimension mismatch — skip this entry
            if (playerVector.Length != memory.vector.Length)
                return float.NegativeInfinity;

            int len = playerVector.Length;
            float dot = 0f, magA = 0f, magB = 0f;

            for (int i = 0; i < len; i++)
            {
                float a = playerVector[i];
                float b = memory.vector[i];
                dot += a * b;
                magA += a * a;
                magB += b * b;
            }

            // Cosine similarity with epsilon to avoid div-by-zero
            float similarity = dot / (Mathf.Sqrt(magA) * Mathf.Sqrt(magB) + 1e-9f);

            // Temporal decay: subtract 0.01f per day since memory was created.
            // High-importance entries (milestones, alliances) are exempt from decay
            // so they stay retrievable over long journeys.
            if (!string.Equals(memory.importance, "high", System.StringComparison.OrdinalIgnoreCase))
            {
                double daysOld = (DateTime.Now.Ticks - memory.timestampTicks) / (10.0 * TimeSpan.TicksPerDay);
                similarity -= (float)(daysOld * TemporalDecayPerDay);
            }

            return similarity;
        }

        #endregion

        #region Consolidation (Buffer → Summary → Embedding → Store)

        /// <summary>
        /// Consolidate a conversation buffer into a single memory entry.
        /// 1. Ask LLM for a summary (GetMemoryLedgerAsync).
        /// 2. If "NONE", abort — nothing to remember.
        /// 3. Get embedding vector for the summary.
        /// 4. Add MemoryEntry to store, cap at MaxMemoriesPerRelationship.
        /// 5. Fire off async disk save.
        /// </summary>
        public async Task ConsolidateBufferAsync(string playerName, string npcName, List<ChatMessage> buffer, CancellationToken token = default)
        {
            if (buffer == null || buffer.Count == 0)
                return;

            string key = BuildKey(playerName, npcName);

            try
            {
                // Step 1: Get summary from LLM (pass NPC name for context)
                string summary = await LLMService.Instance.GetMemoryLedgerAsync(buffer, npcName, token);

                if (string.IsNullOrEmpty(summary))
                {
                    Log.Debug(() => $"[RAG] Consolidation aborted for {key}: empty summary");
                    return;
                }

                // Strip reasoning tags (e.g., <think>...</think>) from reasoning models
                summary = System.Text.RegularExpressions.Regex.Replace(summary, "<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
                summary = System.Text.RegularExpressions.Regex.Replace(summary, "<thinking>.*?</thinking>", "", System.Text.RegularExpressions.RegexOptions.Singleline);

                // Step 2: Check for "NONE" sentinel (only at start — LLM may echo instructions)
                if (summary.Trim().StartsWith("NONE", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug(() => $"[RAG] Consolidation skipped for {key}: no new facts");
                    return;
                }

                // === ATOMIC FACT STORAGE ===
                // Split extraction into individual facts BEFORE collapsing newlines.
                // Each fact gets its own embedding → sharp retrieval, not a muddy blur.
                string[] rawLines = summary.Split('\n', '\r');
                List<string> factLines = new List<string>();
                for (int li = 0; li < rawLines.Length; li++)
                {
                    string line = rawLines[li];
                    // Strip leading numbering/bullets
                    line = System.Text.RegularExpressions.Regex.Replace(line, "^\\d+[.)]\\s+", "");
                    line = System.Text.RegularExpressions.Regex.Replace(line, "^[-*•]\\s+", "");
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;
                    if (LLMService.IsPlaceholderFact(line)) continue;
                    factLines.Add(line);
                }

                if (factLines.Count == 0)
                {
                    Log.Debug(() => $"[RAG] Consolidation skipped for {key}: no facts after split");
                    return;
                }

                Log.Debug(() => $"[RAG] Extracted {factLines.Count} atomic fact(s) for {key}");

                // Process each fact: embed → dedup → store (reusing existing machinery per-fact)
                int storedCount = 0;
                int dupCount = 0;
                NPCMemoryProfile profile = null;

                lock (_lock)
                {
                    if (!_memoryStore.TryGetValue(key, out profile))
                    {
                        profile = new NPCMemoryProfile
                        {
                            playerName = playerName ?? "Survivor",
                            npcName = npcName ?? "",
                            memories = new MemoryEntry[0]
                        };
                        _memoryStore[key] = profile;
                    }
                }

                for (int fi = 0; fi < factLines.Count; fi++)
                {
                    string fact = factLines[fi];

                    // Get embedding vector for this individual fact
                    float[] vector = await LLMService.Instance.GetEmbeddingAsync(fact);
                    if (vector == null || vector.Length == 0)
                    {
                        Log.Debug(() => $"[RAG] Skipping fact with empty embedding: \"{fact.Substring(0, Math.Min(50, fact.Length))}\"");
                        continue;
                    }

                    var entry = new MemoryEntry
                    {
                        summary = fact.Trim(),
                        vector = vector,
                        vectorDim = vector.Length,
                        timestampTicks = DateTime.Now.Ticks
                    };

                    bool isDuplicate = false;
                    lock (_lock)
                    {
                        // Re-fetch profile (may have been updated by prior iteration)
                        if (!_memoryStore.TryGetValue(key, out profile))
                        {
                            profile = new NPCMemoryProfile
                            {
                                playerName = playerName ?? "Survivor",
                                npcName = npcName ?? "",
                                memories = new MemoryEntry[0]
                            };
                            _memoryStore[key] = profile;
                        }

                        List<MemoryEntry> list = new List<MemoryEntry>(profile.memories.Length + 1);
                        for (int i = 0; i < profile.memories.Length; i++)
                            list.Add(profile.memories[i]);

                        // Dedup: skip insert if a near-identical memory already exists (cosine >= 0.92)
                        const float DedupThreshold = 0.92f;
                        float sim = 0f;
                        for (int d = 0; d < list.Count && !isDuplicate; d++)
                        {
                            var ex = list[d];
                            if (ex.vector == null || ex.vector.Length != vector.Length) continue;
                            float dot = 0f, magA = 0f, magB = 0f;
                            for (int i = 0; i < vector.Length; i++)
                            {
                                dot  += vector[i] * ex.vector[i];
                                magA += vector[i] * vector[i];
                                magB += ex.vector[i] * ex.vector[i];
                            }
                            sim = dot / (Mathf.Sqrt(magA) * Mathf.Sqrt(magB) + 1e-9f);
                            if (sim >= DedupThreshold)
                            {
                                ex.timestampTicks = DateTime.Now.Ticks; // refresh
                                isDuplicate = true;
                                Log.Debug(() => $"[RAG] Dedup: refreshed existing memory (sim={sim:F3}) for {key}");
                            }
                        }

                        if (!isDuplicate)
                        {
                            list.Add(entry);

                            // LRU eviction
                            while (list.Count > MaxMemoriesPerRelationship)
                            {
                                int evictIdx = 0;
                                long oldestTicks = list[0].lastRetrievedTicks > 0 ? list[0].lastRetrievedTicks : list[0].timestampTicks;
                                for (int i = 1; i < list.Count; i++)
                                {
                                    long t = list[i].lastRetrievedTicks > 0 ? list[i].lastRetrievedTicks : list[i].timestampTicks;
                                    if (t < oldestTicks) { oldestTicks = t; evictIdx = i; }
                                }
                                Log.Debug(() => $"[RAG] LRU evict: removed entry at index {evictIdx} for {key}");
                                list.RemoveAt(evictIdx);
                            }
                        }

                        profile.memories = list.ToArray();
                    }

                    if (isDuplicate) dupCount++;
                    else storedCount++;
                }

                Log.Debug(() => $"[RAG] Consolidated {storedCount} new + {dupCount} dup(s) for {key}");

                // P4: If ALL embeddings failed (stored==0, dup==0 but we had facts), re-queue to pending file.
                // Do NOT re-queue on legitimate NONE/no-facts results — those are above as early returns.
                if (storedCount == 0 && dupCount == 0 && factLines.Count > 0)
                {
                    WritePendingFile(playerName, npcName, buffer);
                    Log.Out($"[RAG] Consolidation failed — chunk re-queued to pending file for {key} (all embeddings returned null/empty)");
                }
                else
                {
                    // Step 5: Fire off async disk save (don't await)
                    Save();
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[RAG] Consolidation failed for {key}: {ex.Message}");
                // P4: On exception, re-queue the raw buffer to pending file so next boot retries.
                WritePendingFile(playerName, npcName, buffer);
                Log.Out($"[RAG] Consolidation failed — chunk re-queued to pending file for {key} (exception)");
            }
        }

        /// <summary>
        /// Add a pre-written event memory directly (no LLM summary step).
        /// Used by NPCEventMemoryHooks for episodic events (blood moon survived, player died near NPC).
        /// Gets embedding → dedup guard → store/cap → async save.
        /// </summary>
        public async Task AddEventMemoryAsync(string playerId, string npcName, string text, string importance = "high")
        {
            if (string.IsNullOrEmpty(text)) return;

            string key = BuildKey(playerId, npcName);

            try
            {
                // Sanitize for XML storage
                text = text.Replace("\n", " ").Replace("\r", " ");
                text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();

                // Get embedding vector for the event text
                float[] vector = await LLMService.Instance.GetEmbeddingAsync(text);

                if (vector == null || vector.Length == 0)
                {
                    Log.Debug(() => $"[EVENT-MEM] Skipped for {key}: empty embedding (text: \"{text.Substring(0, Math.Min(60, text.Length))}\")");
                    return;
                }

                // Create and store the memory entry
                var entry = new MemoryEntry
                {
                    summary = text,
                    vector = vector,
                    vectorDim = vector.Length,
                    timestampTicks = DateTime.Now.Ticks,
                    importance = importance ?? "high"
                };

                bool isDuplicate = false;
                lock (_lock)
                {
                    if (!_memoryStore.TryGetValue(key, out var profile))
                    {
                        profile = new NPCMemoryProfile
                        {
                            playerName = playerId ?? "Survivor",
                            npcName = npcName ?? "",
                            memories = new MemoryEntry[0]
                        };
                        _memoryStore[key] = profile;
                    }

                    List<MemoryEntry> list = new List<MemoryEntry>(profile.memories.Length + 1);
                    for (int i = 0; i < profile.memories.Length; i++)
                        list.Add(profile.memories[i]);

                    // Dedup: skip insert if a near-identical memory already exists (cosine >= 0.92)
                    const float DedupThreshold = 0.92f;
                    for (int d = 0; d < list.Count && !isDuplicate; d++)
                    {
                        var ex = list[d];
                        if (ex.vector == null || ex.vector.Length != vector.Length) continue;
                        float dot = 0f, magA = 0f, magB = 0f;
                        for (int i = 0; i < vector.Length; i++)
                        {
                            dot  += vector[i] * ex.vector[i];
                            magA += vector[i] * vector[i];
                            magB += ex.vector[i] * ex.vector[i];
                        }
                        float sim = dot / (Mathf.Sqrt(magA) * Mathf.Sqrt(magB) + 1e-9f);
                        if (sim >= DedupThreshold)
                        {
                            ex.timestampTicks = DateTime.Now.Ticks;
                            isDuplicate = true;
                            Log.Debug(() => $"[EVENT-MEM] Dedup: refreshed existing memory (sim={sim:F3}) for {key}");
                        }
                    }

                    if (!isDuplicate)
                    {
                        list.Add(entry);

                        // LRU eviction
                        while (list.Count > MaxMemoriesPerRelationship)
                        {
                            int evictIdx = 0;
                            long oldestTicks = list[0].lastRetrievedTicks > 0 ? list[0].lastRetrievedTicks : list[0].timestampTicks;
                            for (int i = 1; i < list.Count; i++)
                            {
                                long t = list[i].lastRetrievedTicks > 0 ? list[i].lastRetrievedTicks : list[i].timestampTicks;
                                if (t < oldestTicks) { oldestTicks = t; evictIdx = i; }
                            }
                            Log.Debug(() => $"[EVENT-MEM] LRU evict for {key}");
                            list.RemoveAt(evictIdx);
                        }
                    }

                    profile.memories = list.ToArray();
                }

                if (!isDuplicate)
                    Log.Debug(() => $"[EVENT-MEM] Stored for {key}: \"{text.Substring(0, Math.Min(60, text.Length))}\"");

                Save();
            }
            catch (Exception ex)
            {
                Log.Warning($"[EVENT-MEM] Failed for {key}: {ex.Message}");
            }
        }

        #endregion

        #region Retrieval (Query → Score → Top-K → Context String)

        /// <summary>
        /// Get relevant memory context for the current conversation.
        /// 1. Embed the player's input text.
        /// 2. Score all memories against the query vector.
        /// 3. Return top 3 as a formatted string for system prompt injection.
        /// </summary>
        public async Task<string> GetRelevantContextAsync(string playerName, string npcName, string playerInput)
        {
            if (string.IsNullOrEmpty(playerInput))
                return "";

            // Async gate: wait for initialization instead of returning empty.
            // This ensures the first query gets full memory context, preventing hallucinations.
            while (!_storeLoaded)
            {
                await Task.Delay(50); // Yields back to Unity, checking every 50ms
            }

            string key = BuildKey(playerName, npcName);

            NPCMemoryProfile profile;
            int storeSize = 0;
            lock (_lock)
            {
                storeSize = _memoryStore.Count;
                if (!_memoryStore.TryGetValue(key, out profile) || profile.memories == null || profile.memories.Length == 0)
                {
                    Log.Debug(() => $"[RAG] Retrieval: no memories for key '{key}' (store has {storeSize} profiles, profile={profile != null}, memCount={profile?.memories?.Length ?? 0})");
                    return "";
                }
            }

            try
            {
                // Step 1: Embed the player's input
                float[] queryVector = await LLMService.Instance.GetEmbeddingAsync(playerInput);

                if (queryVector == null || queryVector.Length == 0)
                    return "";

                // Step 2: Score all memories (zero-GC for-loop)
                int memCount = profile.memories.Length;
                float[] scores = new float[memCount];

                for (int i = 0; i < memCount; i++)
                    scores[i] = GetRelevanceScore(queryVector, profile.memories[i]);

                // Step 3: Selection — high-importance entries are ADDITIVE (not competitive).
                // Phase A injects up to 3 recent high-importance facts; Phase B always gets full
                // TopKResults cosine-relevant slots. Net: up to 8 lines, inside 1200-char budget.
                int maxSlots = TopKResults + 3;
                string[] topSummaries = new string[maxSlots];
                bool[] used = new bool[memCount];
                int found = 0;

                // Phase A: high-importance entries, scan backwards (newest first) so recent events win.
                const int HighImportanceCap = 3;
                int highCount = 0;
                for (int i = memCount - 1; i >= 0 && found < maxSlots && highCount < HighImportanceCap; i--)
                {
                    if (!used[i] && string.Equals(profile.memories[i].importance, "high", StringComparison.OrdinalIgnoreCase))
                    {
                        used[i] = true;
                        topSummaries[found++] = profile.memories[i].summary;
                        highCount++;
                    }
                }

                // Phase B: cosine top-K — always gets full TopKResults slots regardless of high count.
                int cosineTarget = TopKResults + highCount;
                for (int k = found; k < cosineTarget && k < memCount; k++)
                {
                    int bestIdx = -1;
                    float bestScore = float.NegativeInfinity;

                    for (int i = 0; i < memCount; i++)
                    {
                        if (!used[i] && scores[i] > bestScore)
                        {
                            bestScore = scores[i];
                            bestIdx = i;
                        }
                    }

                    if (bestIdx >= 0 && bestScore > 0f) // Only include positive-relevance memories
                    {
                        used[bestIdx] = true;
                        topSummaries[found++] = profile.memories[bestIdx].summary;
                    }
                }

                // Stamp retrieved entries so LRU eviction knows they're still valuable
                if (found > 0)
                {
                    lock (_lock)
                    {
                        long now = DateTime.Now.Ticks;
                        for (int i = 0; i < memCount; i++)
                            if (used[i]) profile.memories[i].lastRetrievedTicks = now;
                    }
                }

                // Step 4: Format as context string with player identity
                if (found == 0)
                    return "";

                StringBuilder sb = new StringBuilder();
                sb.Append("[Recalled Context:\n");
                for (int i = 0; i < found; i++)
                    sb.Append($"- {topSummaries[i]}\n");
                sb.Append("]");

                string context = sb.ToString();

                // Truncate to MaxContextChars to avoid bloating the system prompt
                if (context.Length > MaxContextChars)
                    context = context.Substring(0, MaxContextChars) + "...";

                Log.Debug(() => $"[RAG] Retrieval: injected {found} memories for '{key}' (query: \"{playerInput.Substring(0, Math.Min(40, playerInput.Length))}\")");
                return context;
            }
            catch (Exception ex)
            {
                Log.Warning($"[RAG] Context retrieval failed for {key}: {ex.Message}");
                return ""; // Graceful degradation — no context injection
            }
        }

        #endregion

        #region Player Given Name (Deterministic, per-NPC)

        /// <summary>
        /// Get the player's given name for this NPC. Returns null if not yet told.
        /// </summary>
        public string GetGivenName(string playerName, string npcName)
        {
            string key = BuildKey(playerName, npcName);
            lock (_lock)
            {
                if (_memoryStore.TryGetValue(key, out var profile))
                    return profile.playerGivenName;
            }
            return null;
        }

        /// <summary>
        /// Set the player's given name for this NPC. Title-cases and sanitizes.
        /// Auto-saves to disk on change. Overwrites if different (conversational correction).
        /// </summary>
        public void SetGivenName(string playerName, string npcName, string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return;

            // Title-case: first char upper, rest lower
            string name = char.ToUpperInvariant(rawName[0]) + rawName.Substring(1).ToLowerInvariant();

            string key = BuildKey(playerName, npcName);
            lock (_lock)
            {
                if (!_memoryStore.TryGetValue(key, out var profile))
                {
                    profile = new NPCMemoryProfile
                    {
                        playerName = playerName ?? "Survivor",
                        npcName = npcName ?? "",
                        memories = new MemoryEntry[0]
                    };
                    _memoryStore[key] = profile;
                }

                if (profile.playerGivenName != name)
                {
                    profile.playerGivenName = name;
                    Log.Out($"[NAME] {npcName} now knows the player as \"{name}\"");
                    Save(); // Persist to disk
                }
            }
        }

        /// <summary>
        /// Record the hire day for a (player, NPC) relationship.
        /// Only stamps if hireDay is currently -1 (never overwrite a real hire day).
        /// Auto-saves to disk on change.
        /// </summary>
        public void RecordHireDay(string playerId, string npcName, int day)
        {
            if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(npcName)) return;
            if (day < 0) return;

            string key = BuildKey(playerId, npcName);
            lock (_lock)
            {
                if (!_memoryStore.TryGetValue(key, out var profile))
                {
                    profile = new NPCMemoryProfile
                    {
                        playerName = playerId ?? "Survivor",
                        npcName = npcName ?? "",
                        memories = new MemoryEntry[0]
                    };
                    _memoryStore[key] = profile;
                }

                if (profile.hireDay < 0)
                {
                    profile.hireDay = day;
                    Log.Debug(() => $"[MEMORY] {npcName} hire day recorded: day {day} for player {playerId}");
                    Save();
                }
            }
        }

        /// <summary>
        /// Get a short factual tenure string for context injection.
        /// Returns "" if no profile, hireDay unset, or tenure <= 0.
        /// </summary>
        public string GetTenureString(string playerId, string npcName)
        {
            string key = BuildKey(playerId, npcName);
            lock (_lock)
            {
                if (!_memoryStore.TryGetValue(key, out var profile))
                    return "";
                if (profile.hireDay < 0)
                    return "";

                int tenure = (int)SkyManager.dayCount - profile.hireDay;
                if (tenure <= 0)
                    return "";

                return $"You have traveled together for {tenure} days.";
            }
        }

        #endregion
    }

    #region Serializable Data Models (XmlSerializer-compatible)

    /// <summary>
    /// A single consolidated memory entry for an NPC.
    /// </summary>
    /// <summary>
    /// Single extracted fact with its embedding vector.
    /// </summary>
    [System.Serializable]
    public class MemoryEntry
    {
        public MemoryEntry() { }

        [XmlElement]
        public string summary;
        [XmlArray("vector"), XmlArrayItem("float")]
        public float[] vector;
        [XmlElement]
        public int vectorDim;           // Embedding dimension — used to detect model-change mismatches
        [XmlElement]
        public long timestampTicks;     // DateTime.Now.Ticks at creation
        [XmlElement]
        public long lastRetrievedTicks; // Updated each retrieval; 0 = never retrieved
        [XmlElement]
        public string importance;       // "normal" (default) or "high" — high-importance entries are exempt from temporal decay
    }

    /// <summary>
    /// All memories for a single (player, NPC) relationship.
    /// Keyed by playerName + npcName for cross-session stability.
    /// </summary>
    [System.Serializable]
    public class NPCMemoryProfile
    {
        public NPCMemoryProfile() { }

        [XmlElement]
        public string playerName;
        [XmlElement]
        public string npcName;
        [XmlArray("memories"), XmlArrayItem("MemoryEntry")]
        public MemoryEntry[] memories;
        /// <summary>
        /// Player's given name as told to THIS NPC. Set via deterministic regex capture
        /// ("my name is X", "call me X"). Per-NPC scope — each NPC tracks independently.
        /// </summary>
        [XmlElement]
        public string playerGivenName;
        /// <summary>
        /// Game day when this NPC was first hired by this player.
        /// (int)SkyManager.dayCount at hire; -1 = never hired (unset).
        /// DayCount starts at 1, so -1 is a safe sentinel.
        /// </summary>
        [XmlElement]
        public int hireDay = -1;
    }

    /// <summary>
    /// Root object for disk serialization.
    /// </summary>
    [System.Serializable]
    public class NPCMemoryStore
    {
        public NPCMemoryStore() { }

        [XmlArray("profiles"), XmlArrayItem("NPCMemoryProfile")]
        public NPCMemoryProfile[] profiles;
    }

    /// <summary>
    /// Pending memory buffer saved synchronously on quit.
    /// Processed into embeddings on next boot.
    /// </summary>
    [System.Serializable]
    public class NPCPendingMemoryStore
    {
        public NPCPendingMemoryStore() { }

        public string playerName;
        public string npcName;
        public NPCPendingMessage[] messages;
    }

    /// <summary>
    /// Simple serializable message for pending buffer storage.
    /// </summary>
    [System.Serializable]
    public class NPCPendingMessage
    {
        public NPCPendingMessage() { }

        public string role;   // "Player" or "NPC"
        public string content;
    }

    #endregion
}
