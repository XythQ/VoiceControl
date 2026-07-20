using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Platform;
using UnityEngine;
using XNPCVoiceControl.TTS;
using XNPCVoiceControl.UI;

namespace XNPCVoiceControl
{
    public partial class NPCChatComponent : MonoBehaviour
    {
        #region RAG Memory Integration (Shadow Buffer + Context Injection)

        /// <summary>
        /// Extract a persistent player identifier for RAG memory keying.
        /// Uses 7D2D's cross-platform persistent ID (Steam/EOS) which survives restarts.
        /// For remote players: ConnectionManager → CrossplatformId/PlatformId.
        /// For local host: PlatformManager.InternalLocalUserIdentifier → GamePrefs.PlayerName → "Survivor".
        /// NEVER uses player.name (GameObject name embeds per-session entityId like "Player_171").
        /// </summary>
        internal static string GetPlayerPersistentId(EntityPlayer player)
        {
            if (player == null) return "unknown";

            string persistentPlayerId = null;

            // 1. Try to get ClientInfo (Works for all remote players connected to a server/host)
            try
            {
                var cInfo = SingletonMonoBehaviour<ConnectionManager>.Instance.Clients.ForEntityId(player.entityId);
                if (cInfo != null)
                {
                    // Prefer CrossplatformId (EOS), fallback to PlatformId (Steam/XBL/PSN)
                    PlatformUserIdentifierAbs platformId = cInfo.CrossplatformId ?? cInfo.PlatformId;
                    if (platformId != null)
                    {
                        // CombinedString returns the exact format we want: "EOS_0002..." or "Steam_7656..."
                        persistentPlayerId = platformId.CombinedString;
                    }
                }
            }
            catch { /* ConnectionManager access failed or player disconnected */ }

            // 2. Local host fallback — stable across sessions (NEVER use player.name)
            if (string.IsNullOrEmpty(persistentPlayerId))
            {
                try
                {
                    // PlatformManager.InternalLocalUserIdentifier: static, stable "Steam_…"/"EOS_…"
                    var localId = PlatformManager.InternalLocalUserIdentifier;
                    if (localId != null)
                    {
                        persistentPlayerId = localId.CombinedString;
                    }
                }
                catch { /* PlatformManager not ready */ }
            }

            // 3. GamePrefs.PlayerName fallback (stable in SP, set by user at character creation)
            if (string.IsNullOrEmpty(persistentPlayerId))
            {
                try
                {
                    string prefName = GamePrefs.GetString(EnumGamePrefs.PlayerName);
                    if (!string.IsNullOrEmpty(prefName))
                    {
                        persistentPlayerId = prefName;
                    }
                }
                catch { /* prefs not loaded */ }
            }

            // 4. Absolute fallback — SP has one player; a constant is correct
            if (string.IsNullOrEmpty(persistentPlayerId))
            {
                persistentPlayerId = "Survivor";
            }

            return persistentPlayerId;
        }

        /// <summary>
        /// Nearest living player to this NPC, or null. Used as the last-resort owner for memory
        /// flushes: in MP, players[0] is an arbitrary player and mis-attributes memories; a lost
        /// flush (null) is better than a corrupted ledger.
        /// </summary>
        private EntityPlayer FindNearestPlayer()
        {
            var players = GameManager.Instance?.World?.Players?.list;
            if (players == null || players.Count == 0) return null;

            EntityPlayer closest = null;
            float closestDistSq = float.MaxValue;

            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || p.IsDead()) continue;
                float dSq = (_npcEntity.position - p.position).sqrMagnitude;
                if (dSq < closestDistSq) { closestDistSq = dSq; closest = p; }
            }
            return closest;
        }

        /// <summary>
        /// Flush the shadow buffer: animal gate → copy → clear → consolidate.
        /// Animals (phrase-trigger-only) skip consolidation entirely.
        /// </summary>
        private static bool _dediFlushLogged = false;

        private void FlushShadowBuffer()
        {
            if (GameManager.IsDedicatedServer)
            {
                if (!_dediFlushLogged)
                {
                    _dediFlushLogged = true;
                    Log.Out("[VoiceMod] Dedicated server — RAG extraction disabled (no embed sidecar, becomes MP Phase 3 custodian)");
                }
                // Clear buffer so it doesn't grow in memory forever
                if (_unsummarizedBuffer.Count > 0)
                    _unsummarizedBuffer.Clear();
                return;
            }

            if (_flushInProgress || _unsummarizedBuffer.Count == 0)
                return;

            // Guard: resolve player ID — instance → shared static → direct GameManager lookup.
            // The static is updated whenever ANY NPC resolves the ID, so NPCs the player
            // never spoke to directly can still flush their buffers.
            string flushPlayerId = _lastPlayerPersistentId;
            if (string.IsNullOrEmpty(flushPlayerId) || flushPlayerId == "unknown")
            {
                flushPlayerId = s_lastResolvedPlayerPersistentId;
            }
            if (string.IsNullOrEmpty(flushPlayerId) || flushPlayerId == "unknown")
            {
                // Last resort: resolve directly from nearest living player
                try
                {
                    EntityPlayer nearest = FindNearestPlayer();
                    if (nearest != null)
                    {
                        flushPlayerId = GetPlayerPersistentId(nearest);
                        if (!string.IsNullOrEmpty(flushPlayerId) && flushPlayerId != "unknown")
                        {
                            _lastPlayerPersistentId = flushPlayerId;
                            s_lastResolvedPlayerPersistentId = flushPlayerId;
                        }
                    }
                }
                catch { /* GameManager access failed during flush */ }
            }
            if (string.IsNullOrEmpty(flushPlayerId) || flushPlayerId == "unknown")
            {
                Log.Debug(() => $"[RAG] Skipping flush for {_npcName} - player ID not yet resolved");
                _timeSinceLastMessage = 0f;   // retry in 60s, not every frame (was ~38 calls/sec)
                return;
            }

            // Tactical mode: suppress RAG flush (no memory accumulation while command-only).
            EntityPlayer flushPlayer = FindNearestPlayer();
            if (flushPlayer != null && (int)flushPlayer.Buffs.GetCustomVar("varTacticalMode") == 1)
            {
                _timeSinceLastMessage = 0f;
                return;
            }

            // Guard: skip if extraction already in-flight for this NPC.
            // Extraction runs to completion; new messages accumulate in the buffer and
            // get flushed on the next idle cycle after this one finishes.
            if (_extractionInProgress)
            {
                Log.Debug(() => $"[RAG] Skipping flush for {_npcName} - extraction already in-flight");
                return;
            }

            _flushInProgress = true;
            try
            {
                Log.Debug(() => $"[RAG] FlushShadowBuffer triggered for {_npcName}: {_unsummarizedBuffer.Count} messages, idle={_timeSinceLastMessage:F1}s");
                Log.Debug(() => $"[RAG] Flush key: {flushPlayerId}_{_npcName}");

                // Non-Talker Gate: animals get phrase triggers only, no LLM memory
                if (_npcEntity != null && _npcEntity.GetType().Name.Contains("Animal"))
                {
                    Log.Debug(() => $"[RAG] Flush skipped for {_npcName}: Animal gate");
                    _unsummarizedBuffer.Clear();
                    _timeSinceLastMessage = 0f;
                    return;
                }

                // Snapshot buffer and clear live reference immediately (free main thread)
                var snapshot = new List<ChatMessage>(_unsummarizedBuffer);
                _unsummarizedBuffer.Clear();
                _timeSinceLastMessage = 0f;

                // Fire-and-forget consolidation with player context (runs on background threads via TCS)
                if (NPCMemoryManager.Instance != null && snapshot.Count > 0)
                {
                    string playerId = flushPlayerId;

                    _extractionCts?.Dispose();
                    _extractionCts = new CancellationTokenSource();
                    CancellationToken extractionToken = _extractionCts.Token;
                    _extractionInProgress = true;

                    // Split large buffers into chunks of 6 messages (3 exchanges) - small LLMs choke on huge buffers
                    int chunkSize = 6;
                    var tasks = new System.Collections.Generic.List<System.Threading.Tasks.Task>();
                    for (int i = 0; i < snapshot.Count; i += chunkSize)
                    {
                        var chunk = snapshot.GetRange(i, Math.Min(chunkSize, snapshot.Count - i));
                        try
                        {
                            var t = NPCMemoryManager.Instance.ConsolidateBufferAsync(playerId, _npcName, chunk, extractionToken);
                            tasks.Add(t);
                        }
                        catch (Exception ex) { Log.Warning($"[RAG] Consolidation fire-and-forget failed for {_npcName}: {ex.Message}"); }
                    }

                    // Clear in-flight flag once all tasks complete (success, NONE, or cancellation).
                    // _extractionCts is disposed so the next flush gets a fresh token.
                    if (tasks.Count > 0)
                    {
                        System.Threading.Tasks.Task.WhenAll(tasks).ContinueWith(_ =>
                        {
                            MainThreadDispatcher.Enqueue(() =>
                            {
                                _extractionInProgress = false;
                                if (_extractionCts != null)
                                {
                                    _extractionCts.Dispose();
                                    _extractionCts = null;
                                }
                            });
                        }, System.Threading.Tasks.TaskScheduler.Default);
                    }
                    else
                    {
                        // No tasks were created (all chunks threw synchronously) — don't leave the flag stuck.
                        _extractionInProgress = false;
                        if (_extractionCts != null)
                        {
                            _extractionCts.Dispose();
                            _extractionCts = null;
                        }
                    }
                }
            }
            finally
            {
                _flushInProgress = false;
            }
        }

        #endregion
    }
}
