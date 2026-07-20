using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using XNPCVoiceControl.Actions;
using XNPCVoiceControl.Core;
using XNPCVoiceControl.STT;
using XNPCVoiceControl.TTS;
using XNPCVoiceControl.UI;

namespace XNPCVoiceControl
{
    public partial class NPCChatComponent : MonoBehaviour
    {
        #region Async Voice Pipeline (Data Sandwich Pattern)

        /// <summary>
        /// Plain-data snapshot extracted on the main thread before offloading to a background task.
        /// Contains everything the async pipeline needs without holding Unity references across threads.
        /// </summary>
        private struct VoicePipelineContext
        {
            public string NpcName;
            public int EntityId;
            public string SystemPrompt;
            public List<string> HistoryRoles;    // "Player" / "NPC"
            public List<string> HistoryContents;
            public bool ActionsEnabled;
            public int MaxResponseLength;
            public bool ShowTypingIndicator;
            public int TypingDelayMs;
            public bool TtsEnabled;
            public string SttServerUrl;
            public int SttTimeoutSeconds;
            public string LlmEndpoint;
            public string LlmModel;
            public int LlmMaxTokens;
            public float LlmTemperature;
            public int LlmTimeoutSeconds;
            public string TtsServerUrl;
            public string TtsVoiceName;
            public int TtsTimeoutSeconds;
            public float TtsSpeechRate;
            public string PlayerUiLanguage;      // ISO code ("en", "ja", etc.) - from escape menu toggle
            public string PlayerPersistentId;    // for RAG memory retrieval
            public string PlayerDisplayName;     // for RAG memory retrieval
            public string SenseBlock;            // environmental snapshot - appended last so static prefix is KV-cacheable
            public CancellationToken InterruptionToken;  // bound to HttpWebRequest for abort
        }

        /// <summary>
        /// Process raw voice input (WAV bytes) through the full STT → LLM → TTS pipeline.
        ///
        /// Data Sandwich pattern:
        /// 1. Setup (Main Thread): Extract NPC data, conversation history, system prompt into plain C# types
        /// 2. Background Task: Run STT → LLM → TTS sequentially using HttpWebRequest on a background thread
        /// 3. Handoff (Main Thread): Use MainThreadDispatcher.Enqueue to marshal AudioClip creation,
        ///    playback, and UI updates back to the main thread
        /// </summary>
        public void ProcessVoiceInputAsync(byte[] wavData, EntityPlayer player)
        {
            if (ServerManager.ReadyState != ServerManager.ServerReadyState.Ready) return; // servers still warming up

            // --- COMBAT GUARD CLAUSE (voice path) ---
            // We can't check for hiring keywords yet - STT hasn't run. Let it through.
            // The text path (ProcessPlayerMessage) has the full combat gate with hiring bypass.
            if (_npcEntity != null)
            {
                bool isInCombat = _npcEntity.IsAlert ||
                                  _npcEntity.GetAttackTarget() != null ||
                                  _npcEntity.GetRevengeTarget() != null;

                Entity leaderOrOwner = EntityUtilities.GetLeaderOrOwner(_npcEntity.entityId);
                bool isHired = leaderOrOwner != null;

                if (isInCombat && !isHired)
                {
                    // Wild NPC in combat - let voice through so STT can transcribe,
                    // then ProcessPlayerMessage will gate on the actual text.
                    Log.Debug(() => $"[NPCVoiceControl] {_npcName} is wild and in combat. Voice input proceeding to STT for hiring check.");
                }
            }

            // Interruption: if a prior pipeline is active, kill its audio and let this one through.
            // This prevents mixed audio from overlapping pipelines (TC-7 fix).
            if (_isWaitingForResponse)
            {
                Log.Debug(() => $"[VOICE] Interrupting prior pipeline for {_npcName}");

                                                    if (_audioPlayer != null)
                    _audioPlayer.StopSpeaking("voice-pipeline-interrupt");
                if (_cachedAudioSource != null && _cachedAudioSource.isPlaying)
                    _cachedAudioSource.Stop();

                // Cancel the prior pipeline's background task (LLM/STT)
                if (_interruptionTokenSource != null && !_interruptionTokenSource.IsCancellationRequested)
                {
                    try { _interruptionTokenSource.Cancel(); } catch { /* ObjectDisposedException if already disposed */ }
                }

                // Increment pipeline ID so stale callbacks from the old pipeline are ignored
                System.Threading.Interlocked.Increment(ref _pipelineId);

                // Clear state for the new pipeline
                _isWaitingForResponse = false;
                StopInterruptionMonitor();
            }

            if (wavData == null || wavData.Length < 44)
            {
                Log.Warning($"VoiceInput: invalid or empty audio data ({(wavData?.Length ?? 0)} bytes)");
                return;
            }

            _pipelineStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // === PHASE 1: SETUP (Main Thread) ===
            // Capture environmental sense snapshot on main thread (Unity API access)
            string senseBlock = NPCSenseSnapshot.Capture(_npcEntity)?.ToPromptString() ?? "";

            // Extract all Unity-dependent data into plain C# types before going off-thread.
            VoicePipelineContext ctx = new VoicePipelineContext
            {
                NpcName = _npcName,
                EntityId = _entityId,
                SystemPrompt = _actionsEnabled ? BuildActionSystemPrompt() : _systemPrompt,
                SenseBlock = senseBlock,
                HistoryRoles = new List<string>(_conversationHistory.Count),
                HistoryContents = new List<string>(_conversationHistory.Count),
                ActionsEnabled = _actionsEnabled,
                MaxResponseLength = _config.MaxResponseLength,
                ShowTypingIndicator = _config.ShowTypingIndicator,
                TypingDelayMs = _config.TypingDelayMs,
                TtsEnabled = _ttsEnabled && _audioPlayer != null && TTSService.Instance.IsInitialized,
                SttServerUrl = STTService.Instance.Config?.ServerUrl ?? "http://127.0.0.1:5052",
                SttTimeoutSeconds = STTService.Instance.Config?.TimeoutSeconds ?? 30,
                LlmEndpoint = _config.Endpoint,
                LlmModel = _config.Model,
                LlmMaxTokens = _config.MaxTokens,
                LlmTemperature = _config.Temperature,
                LlmTimeoutSeconds = _config.TimeoutSeconds,
                TtsServerUrl = TTSService.Instance.Config?.ServerUrl ?? "http://127.0.0.1:5053",
                TtsVoiceName = _audioPlayer != null ? SelectTTSVoice() : (TTSService.Instance.Config?.DefaultVoice ?? "af_aoede"),
                TtsTimeoutSeconds = TTSService.Instance.Config?.TimeoutSeconds ?? 30,
                TtsSpeechRate = TTSService.Instance.Config?.SpeechRate ?? 1f,
                PlayerUiLanguage = LocalizationHelper.ResolveEffectiveLanguage(_personality)
            };

            foreach (var msg in _conversationHistory)
            {
                ctx.HistoryRoles.Add(msg.Role);
                ctx.HistoryContents.Add(msg.Content);
            }

            // Capture player context for RAG memory
            string playerId = "unknown";
            string playerName = "Survivor";
            if (player != null)
            {
                playerId = GetPlayerPersistentId(player);
                _lastPlayerPersistentId = playerId;
                if (playerId != "unknown")
                    s_lastResolvedPlayerPersistentId = playerId;
                playerName = GetPlayerDisplayName(player);
                _lastPlayerDisplayName = playerName;
            }

            // Add player context to pipeline struct for background thread RAG retrieval
            ctx.PlayerPersistentId = playerId;
            ctx.PlayerDisplayName = playerName;

            // Cancel any still-running prior pipeline before starting a new one
            if (_interruptionTokenSource != null && !_interruptionTokenSource.IsCancellationRequested)
                try { _interruptionTokenSource.Cancel(); } catch { /* ObjectDisposedException if already disposed */ }

            // NOTE: RAG extraction is NO LONGER cancelled at pipeline start.
            // Extraction runs to completion after deep idle; if the player speaks mid-extraction,
            // that response serializes behind it on 5055 (~3-5s, rare). This is the deliberate
            // 2-server cost — chat (5055) and embed (5056) are separate, so only extraction blocks.

            // Initialize interruption infrastructure (token MUST be created before capture)
            _interruptionTokenSource = new CancellationTokenSource();
            ctx.InterruptionToken = _interruptionTokenSource.Token;
            int myPipelineId = System.Threading.Interlocked.Increment(ref _pipelineId);

            // Mark as busy on main thread so concurrent triggers are rejected
            _isWaitingForResponse = true;
            StartInterruptionMonitor();

            OnResponseStarted?.Invoke("...");

            Log.Debug(() => $"[VOICE] Starting async pipeline for {_npcName} ({wavData.Length} bytes, {ctx.HistoryRoles.Count} history messages)");

            // === PHASE 2: BACKGROUND TASK (Off-Thread) ===
            Task.Run(() =>
            {
                try
                {
                    // --- Step 1: STT ---
                    long sttStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    string transcript = HttpTranscribe(ctx.SttServerUrl, wavData, ctx.SttTimeoutSeconds, ctx.InterruptionToken);
                    long sttElapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sttStart;

                    // Sanitize: strip caption artifacts and hallucinated phrases
                    transcript = STTService.SanitizeTranscript(transcript);

                    if (string.IsNullOrWhiteSpace(transcript))
                    {
                        Log.Debug(() => $"[VOICE] STT returned no speech ({sttElapsed}ms)");
                        MainThreadDispatcher.Enqueue(() => { if (_pipelineId == myPipelineId) HandleVoicePipelineEmpty(sttElapsed); });
                        return;
                    }

                    Log.Debug(() => $"[VOICE] STT → \"{transcript}\" ({sttElapsed}ms)");

                    // --- Step 1.4: Deterministic player-name capture (before RAG + LLM) ---
                    TryCapturePlayerName(transcript, ctx.PlayerPersistentId, ctx.NpcName);

                    // --- Step 1.5: RAG Memory Retrieval (background thread) ---
                    string memoryContext = "";
                    if (NPCMemoryManager.Instance != null && !string.IsNullOrWhiteSpace(transcript))
                    {
                        try
                        {
                            long ragStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            // Await synchronously on background thread - includes async gate for init
                            memoryContext = NPCMemoryManager.Instance.GetRelevantContextAsync(
                                ctx.PlayerPersistentId, ctx.NpcName, transcript).Result;
                            long ragElapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ragStart;
                            if (!string.IsNullOrEmpty(memoryContext))
                            {
                                Log.Debug(() => $"[VOICE] RAG retrieved {memoryContext.Length} chars in {ragElapsed}ms");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"[VOICE] RAG retrieval failed: {ex.Message}");
                            memoryContext = "";
                        }
                    }

                    // Build final prompt with memory context injected
                    string finalSystemPrompt = ctx.SystemPrompt;
                    if (!string.IsNullOrEmpty(memoryContext))
                    {
                        // Split memories by [NPC] / [SURVIVOR] tags into separate context blocks
                        var npcMemories = new List<string>();
                        var survivorMemories = new List<string>();
                        string survivorNoun = !string.IsNullOrEmpty(ctx.PlayerDisplayName) && ctx.PlayerDisplayName != "Survivor"
                            ? ctx.PlayerDisplayName
                            : "the survivor";

                        // memoryContext format: "[Recalled Context:\n- [SURVIVOR] fact\n- [NPC] fact\n]"
                        string[] rawLines = memoryContext.Split('\n');
                        foreach (string raw in rawLines)
                        {
                            string trimmed = raw.TrimStart('-', ' ').Trim();
                            if (trimmed.StartsWith("[Recalled") || trimmed == "]" || trimmed.Length < 3)
                                continue;
                            if (trimmed.StartsWith("[NPC] "))
                            {
                                npcMemories.Add(trimmed.Substring(6).Replace("The Player", survivorNoun));
                            }
                            else if (trimmed.StartsWith("[SURVIVOR] "))
                            {
                                survivorMemories.Add(trimmed.Substring(11).Replace("The Player", survivorNoun));
                            }
                            else if (!trimmed.StartsWith("[") && trimmed.Length > 3)
                            {
                                survivorMemories.Add(trimmed.Replace("The Player", survivorNoun));
                            }
                        }

                        StringBuilder contextBlock = new StringBuilder();
                        if (npcMemories.Count > 0)
                        {
                            contextBlock.Append("[ABOUT YOU]\n");
                            foreach (string m in npcMemories)
                                contextBlock.Append($"- {m}\n");
                            contextBlock.Append($"These are facts about yourself. You know this is your status.\n\n");
                        }
                        if (survivorMemories.Count > 0)
                        {
                            contextBlock.Append("[ABOUT YOUR COMPANION ").Append(survivorNoun).Append("]\n");
                            foreach (string m in survivorMemories)
                                contextBlock.Append($"- {m}\n");
                            contextBlock.Append($"These are facts about {survivorNoun}. Reference them naturally when relevant.");
                        }

                        finalSystemPrompt = ctx.SystemPrompt + "\n\n" + contextBlock.ToString();
                    }
                    if (!string.IsNullOrEmpty(ctx.SenseBlock))
                        finalSystemPrompt += "\n\n" + ctx.SenseBlock;

                    // Inject player's given name (quiet factual context, no instruction to use it)
                    string givenName = NPCMemoryManager.Instance.GetGivenName(ctx.PlayerPersistentId, ctx.NpcName);
                    if (!string.IsNullOrEmpty(givenName))
                        finalSystemPrompt += $"\n\nThe survivor's name is {givenName}.";
                    // Inject tenure (quiet factual context, no instruction)
                    string tenureStr = NPCMemoryManager.Instance.GetTenureString(ctx.PlayerPersistentId, ctx.NpcName);
                    if (!string.IsNullOrEmpty(tenureStr))
                        finalSystemPrompt += $"\n\n{tenureStr}";
                    // Late reinforcement of the NPC's own name (recency bias - small models attend
                    // far more reliably to facts near the end of a long prompt than the front-loaded
                    // identity line in BuildSystemPrompt()).
                    finalSystemPrompt += $"\n\nRemember: your own name is {ctx.NpcName}.";

                    // --- Step 2: LLM (strip wake words so NPC doesn't think player is named Marvin) ---
                    long llmStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    string llmInput = StripWakeWords(transcript);
                    string llmResponse = HttpChatCompletion(
                        ctx.LlmEndpoint, ctx.LlmModel,
                        finalSystemPrompt, ctx.HistoryRoles, ctx.HistoryContents,
                        llmInput, ctx.LlmMaxTokens, ctx.LlmTemperature,
                        ctx.LlmTimeoutSeconds, ctx.InterruptionToken);
                    long llmElapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - llmStart;

                    if (string.IsNullOrWhiteSpace(llmResponse))
                    {
                        Log.Warning($"[VOICE] LLM returned empty response ({llmElapsed}ms)");
                        MainThreadDispatcher.Enqueue(() => { if (_pipelineId == myPipelineId) HandleVoicePipelineError($"LLM returned empty response", sttElapsed + llmElapsed, transcript); });
                        return;
                    }

                    Log.Debug(() => $"[VOICE] LLM → \"{llmResponse.Substring(0, Math.Min(80, llmResponse.Length))}\" ({llmElapsed}ms)");

                    // NOTE: _isWaitingForResponse is NOT released here.
                    // It stays true until HandleVoicePipelineSuccess completes playback,
                    // preventing a new pipeline from starting while audio plays (TC-7 fix).

                    // --- Step 3: TTS (only if enabled) ---
                    byte[] ttsBytes = null;
                    long ttsElapsed = 0;

                    if (ctx.TtsEnabled)
                    {
                        long ttsStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        ttsBytes = HttpSynthesizeTTS(
                            ctx.TtsServerUrl, llmResponse,
                            ctx.TtsVoiceName, ctx.TtsSpeechRate,
                            ctx.TtsTimeoutSeconds, ctx.InterruptionToken);
                        ttsElapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ttsStart;

                        if (ttsBytes != null && ttsBytes.Length > 0)
                        {
                            Log.Debug(() => $"[VOICE] TTS → {ttsBytes.Length} bytes ({ttsElapsed}ms)");
                        }
                        else
                        {
                            Log.Warning($"[VOICE] TTS returned empty audio ({ttsElapsed}ms)");
                            ttsBytes = null;
                        }
                    }

                    long totalElapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _pipelineStartMs;
                    Log.Debug(() => $"[VOICE] Pipeline complete: STT={sttElapsed}ms LLM={llmElapsed}ms TTS={ttsElapsed}ms TOTAL={totalElapsed}ms");

                    // === PHASE 3: HANDOFF (Main Thread) ===
                    MainThreadDispatcher.Enqueue(() => { if (_pipelineId == myPipelineId) HandleVoicePipelineSuccess(transcript, llmResponse, ttsBytes, sttElapsed, llmElapsed, ttsElapsed, totalElapsed); });
                }
                catch (Exception ex)
                {
                    long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _pipelineStartMs;
                    Log.Error($"[VOICE] Pipeline error ({elapsed}ms): {ex.Message}");

                    MainThreadDispatcher.Enqueue(() => { if (_pipelineId == myPipelineId) HandleVoicePipelineError(ex.Message, elapsed, null); });
                }
            });
        }

        /// <summary>
        /// Select the appropriate TTS voice for this NPC (mirrors NPCAudioPlayer.SelectVoice).
        /// </summary>
        private string SelectTTSVoice()
        {
            if (_audioPlayer == null)
                return TTSService.Instance.Config?.DefaultVoice ?? "af_aoede";

            // Use reflection-free approach: read the voice from NPCAudioPlayer's current selection logic
            // by checking gender tag and personality voices stored in the component.
            string maleVoice = _personality?.MaleVoice;
            string femaleVoice = _personality?.FemaleVoice;
            string genderTag = GetGenderTag();

            if (genderTag == "male" && !string.IsNullOrEmpty(maleVoice)) return maleVoice;
            if (genderTag == "female" && !string.IsNullOrEmpty(femaleVoice)) return femaleVoice;
            if (!string.IsNullOrEmpty(maleVoice)) return maleVoice;
            if (!string.IsNullOrEmpty(femaleVoice)) return femaleVoice;

            var ttsConfig = XNPCVoiceControlMod.TTSConfig;
            return ttsConfig?.DefaultVoice ?? "af_aoede";
        }

        /// <summary>
        /// HTTP POST to whisper-server /transcribe endpoint. Runs on background thread.
        /// </summary>
        private static string HttpTranscribe(string serverUrl, byte[] wavData, int timeoutSeconds, CancellationToken token = default)
        {
            string url = $"{serverUrl.TrimEnd('/')}/transcribe";

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.KeepAlive = true;
            request.Proxy = null;
            request.Method = "POST";
            request.ContentType = "audio/wav";
            request.Timeout = timeoutSeconds * 1000;
            request.ReadWriteTimeout = timeoutSeconds * 1000;
            request.ContentLength = wavData.Length;

            // Bind cancellation token to HTTP request
            using (token.Register(() => request.Abort(), useSynchronizationContext: false))
            {
                try
                {
                    using (var stream = request.GetRequestStream())
                        stream.Write(wavData, 0, wavData.Length);

                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        string body = reader.ReadToEnd();
                        return ExtractJsonString(body, "text");
                    }
                }
                catch (WebException) when (token.IsCancellationRequested)
                {
                    Log.Debug(() => "[VOICE] STT request aborted by Interruption Matrix.");
                    throw new OperationCanceledException(token);
                }
            }
        }

        /// <summary>
        /// HTTP POST to LLM /v1/chat/completions endpoint. Runs on background thread.
        /// </summary>
        private static string HttpChatCompletion(
            string endpoint, string model,
            string systemPrompt, List<string> historyRoles, List<string> historyContents,
            string playerMessage, int maxTokens, float temperature, int timeoutSeconds,
            CancellationToken token = default)
        {
            // Build messages array
            var sb = new StringBuilder();
            sb.Append($"{{\"role\": \"system\", \"content\": \"{EscapeJson(systemPrompt)}\"}}");

            for (int i = 0; i < historyRoles.Count; i++)
            {
                string role = historyRoles[i].ToLower() == "player" ? "user" : "assistant";
                sb.Append($", {{\"role\": \"{role}\", \"content\": \"{EscapeJson(historyContents[i])}\"}}");
            }
            sb.Append($", {{\"role\": \"user\", \"content\": \"{EscapeJson(playerMessage)}\"}}");

            string jsonBody = $@"{{
                ""model"": ""{model}"",
                ""messages"": [{sb}],
                ""temperature"": {temperature},
                ""max_tokens"": {maxTokens}
            }}";

            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.KeepAlive = true;
            request.Proxy = null;
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = timeoutSeconds * 1000;
            request.ReadWriteTimeout = timeoutSeconds * 1000;

            byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
            request.ContentLength = bodyBytes.Length;

            // Bind cancellation token to HTTP request
            using (token.Register(() => request.Abort(), useSynchronizationContext: false))
            {
                try
                {
                    using (var stream = request.GetRequestStream())
                        stream.Write(bodyBytes, 0, bodyBytes.Length);

                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        string responseBody = reader.ReadToEnd();
                        return ParseLLMContent(responseBody);
                    }
                }
                catch (WebException) when (token.IsCancellationRequested)
                {
                    Log.Debug(() => "[VOICE] LLM request aborted by Interruption Matrix.");
                    throw new OperationCanceledException(token);
                }
            }
        }

        /// <summary>
        /// HTTP POST to Kokoro /tts endpoint. Runs on background thread.
        /// </summary>
        private static byte[] HttpSynthesizeTTS(string serverUrl, string text, string voiceName, float speed, int timeoutSeconds, CancellationToken token = default)
        {
            // Strip emotes before sending
            string cleanText = TTSService.StripEmotesForTts(text);
            if (string.IsNullOrWhiteSpace(cleanText)) return null;

            string jsonBody = $"{{\"text\":\"{EscapeJson(cleanText)}\",\"voice\":\"{voiceName}\",\"speed\":{speed}}}";
            string url = $"{serverUrl.TrimEnd('/')}/tts";

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.KeepAlive = true;
            request.Proxy = null;
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = timeoutSeconds * 1000;
            request.ReadWriteTimeout = timeoutSeconds * 1000;

            byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
            request.ContentLength = bodyBytes.Length;

            // Bind cancellation token to HTTP request
            using (token.Register(() => request.Abort(), useSynchronizationContext: false))
            {
                try
                {
                    using (var stream = request.GetRequestStream())
                        stream.Write(bodyBytes, 0, bodyBytes.Length);

                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        using (var ms = new MemoryStream())
                        {
                            response.GetResponseStream().CopyTo(ms);
                            return ms.ToArray();
                        }
                    }
                }
                catch (WebException) when (token.IsCancellationRequested)
                {
                    Log.Debug(() => "[VOICE] TTS request aborted by Interruption Matrix.");
                    throw new OperationCanceledException(token);
                }
            }
        }

        /// <summary>
        /// Parse "content" from OpenAI-compatible LLM JSON response.
        /// </summary>
        private static string ParseLLMContent(string jsonResponse)
        {
            return JsonHelper.GetLLMContent(jsonResponse);
        }

        /// <summary>
        /// Extract a string value from simple JSON.
        /// </summary>
        private static string ExtractJsonString(string json, string key)
        {
            return JsonHelper.GetString(json, key);
        }

        /// <summary>
        /// Escape a string for JSON embedding.
        /// </summary>
        private static string EscapeJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string quoted = Newtonsoft.Json.JsonConvert.ToString(text);
            return quoted.Substring(1, quoted.Length - 2);
        }

    #endregion

    #region Async Pipeline Main-Thread Handlers

        /// <summary>
        /// Called on main thread after successful STT → LLM → TTS pipeline.
        /// Updates conversation history, executes actions, shows tooltips, plays audio.
        /// </summary>
        private void HandleVoicePipelineSuccess(
            string transcript, string llmResponse, byte[] ttsBytes,
            long sttElapsed, long llmElapsed, long ttsElapsed, long totalElapsed)
        {
            // Capture pipeline ID to guard callbacks against interruption (TC-7 fix).
            int myPipelineId = _pipelineId;

            // Clean up interruption monitor (conversation finished naturally)
            StopInterruptionMonitor();
            _isWaitingForResponse = false;

            // Log raw LLM response for debugging
            Log.Debug(() => $"[VOICE] Raw LLM: \"{llmResponse.Substring(0, Math.Min(120, llmResponse.Length))}\"");

            // Parse response for actions (JSON only)
            NPCAction action = null;
            string spokenText = llmResponse;       // For TTS (NPC's native language)
            string subtitleText = null;            // For UI (player's UI language)

            if (_actionsEnabled)
            {
                action = ActionParser.Parse(llmResponse);
                if (action != null && !string.IsNullOrEmpty(action.DialogueBefore))
                    spokenText = action.DialogueBefore;

                // Dual-output: use subtitle_localized if present, otherwise fall back to spoken text
                if (!string.IsNullOrEmpty(action?.SubtitleLocalized))
                    subtitleText = action.SubtitleLocalized;
            }

            // Strip any remaining JSON blocks from spoken text (LLM sometimes embeds JSON in text)
            spokenText = CleanDialogueForTts(spokenText);

            // Catch placeholder/garbage LLM output (e.g. "Japanese text", "[English response]")
            spokenText = ValidateSpokenText(spokenText, _personality?.TTSLanguage ?? "en", action);

            // If dialogue is empty after parsing (action-only response like {"action": "open_backpack"}),
            // use a fallback phrase so TTS doesn't speak nothing or raw JSON
            if (string.IsNullOrWhiteSpace(spokenText))
            {
                spokenText = GetActionFallback(action);
            }

            // Trim response if too long
            if (spokenText.Length > _config.MaxResponseLength)
            {
                spokenText = spokenText.Substring(0, _config.MaxResponseLength);
                int lastPeriod = spokenText.LastIndexOf('.');
                if (lastPeriod > _config.MaxResponseLength / 2)
                    spokenText = spokenText.Substring(0, lastPeriod + 1);
            }

            // Subtitle defaults to spoken text if no localized version was parsed
            if (string.IsNullOrEmpty(subtitleText))
                subtitleText = spokenText;

            // Update conversation history (store the spoken/native text)
            _conversationHistory.Add(new ChatMessage("Player", transcript));
            _conversationHistory.Add(new ChatMessage("NPC", spokenText));
            TrimHistory();

            // RAG: add voice exchange to shadow buffer for memory consolidation
            _unsummarizedBuffer.Add(new ChatMessage("Player", transcript));
            _unsummarizedBuffer.Add(new ChatMessage("NPC", spokenText));
            _timeSinceLastMessage = 0f;

            // Execute action if parsed - only trust JSON-parsed actions (confidence >= 0.85)
            if (action != null && action.Type != NPCActionType.None && action.Confidence >= 0.85f && _npcEntity != null)
            {
                TryReadLeaderFromSCore();

                if (!CanExecuteActions(_lastInteractingPlayer))
                {
                    Log.Debug(() => $"Action {action.Type} blocked - player is not the leader of {_npcName}");
                    _headGesture?.Play(HeadGestureType.Shake);
                }
                else
                {
                    try
                    {
                        ActionExecutor.Instance.ExecuteAction(_npcEntity, _lastInteractingPlayer, action);
                        // Nod on accept (all actions except Refuse), shake on explicit refusal
                        if (action.Type == NPCActionType.Refuse)
                            _headGesture?.Play(HeadGestureType.Shake);
                        else
                            _headGesture?.Play(HeadGestureType.Nod);
                    }
                    catch (Exception ex) { Log.Error($"Action execution failed: {ex.Message}"); }
                }
            }

            // Show subtitle for LLM response (use localized text if available)
            if (!string.IsNullOrWhiteSpace(subtitleText))
            {
                if (_config.ShowTypingIndicator && _config.TypingDelayMs > 0)
                {
                    // Typewriter mode: open window with indicator, stream characters
                    SubtitleManager.Instance.ShowSubtitle(_npcName, "...", 25f);
                    StartCoroutine(TypeResponseCoroutine(subtitleText, null));
                }
                else
                {
                    // Instant display mode
                    SubtitleManager.Instance.ShowSubtitle(_npcName, subtitleText, 25f);
                    _currentResponse = subtitleText;
                    OnResponseComplete?.Invoke(subtitleText);
                }
            }

            // Play TTS audio if available (use spoken text in NPC's native language)
            if (ttsBytes != null && ttsBytes.Length > 0 && _audioPlayer != null)
            {
                long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _pipelineStartMs;
                Log.Debug(() => $"[VOICE] Playing TTS audio: {elapsed}ms total pipeline");
                OnSpeechStarted?.Invoke(spokenText);

                // If connected to a dedi/listen server, relay the WAV to all clients in range
                if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsClient)
                {
                    try
                    {
                        var voicePkg = new NetPackageNPCVoice(_entityId, ttsBytes, XNPCVoiceControlMod.TTSConfig?.Volume ?? 0.8f);
                        SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(voicePkg);
                        Log.Debug(() => $"[VOICE] Sent relay packet ({ttsBytes.Length} bytes) for entity {_entityId}");
                    }
                    catch (System.Exception ex)
                    {
                        Log.Warning($"[VOICE] Failed to send relay packet: {ex.Message}");
                    }
                }

                var (audioData, sampleRate, channels) = AudioUtils.ProcessWavBytes(ttsBytes);
                AudioClip clip = AudioUtils.CreateClipFromData(audioData, sampleRate, channels);
                if (clip != null && _audioSource != null)
                {
                    _audioSource.clip = clip;
                    _audioSource.volume = XNPCVoiceControlMod.TTSConfig?.Volume ?? 0.8f;
                    _audioSource.Play();
                    StartCoroutine(WaitForPlaybackFinish(clip, () =>
                    {
                        if (_pipelineId == myPipelineId)
                        {
                            SubtitleManager.Instance.ClearSubtitle();
                            OnSpeechComplete?.Invoke();
                            VoiceInputManager.Instance?.MarkProcessingComplete();
                        }
                    }));
                }
                else
                {
                    // Clip creation failed — unlock mic immediately
                    if (_pipelineId == myPipelineId)
                        VoiceInputManager.Instance?.MarkProcessingComplete();
                }
            }
            else if (_ttsEnabled && _audioPlayer != null)
            {
                // Fallback: use existing TTS pipeline for playback
                long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _pipelineStartMs;
                Log.Debug(() => $"[VOICE] LLM response → TTS start (fallback): {elapsed}ms");
                OnSpeechStarted?.Invoke(spokenText);
                _audioPlayer.SpeakStreaming(spokenText, () =>
                {
                    if (_pipelineId == myPipelineId)
                    {
                        SubtitleManager.Instance.ClearSubtitle();
                        OnSpeechComplete?.Invoke();
                        VoiceInputManager.Instance?.MarkProcessingComplete();
                    }
                });
            }
            else
            {
                // No audio playback — unlock mic immediately
                if (_pipelineId == myPipelineId)
                    VoiceInputManager.Instance?.MarkProcessingComplete();
            }
        }

        /// <summary>
        /// Called on main thread when STT returns no speech.
        /// </summary>
        private void HandleVoicePipelineEmpty(long elapsed)
        {
            // Clean up interruption monitor (pipeline finished with no speech)
            StopInterruptionMonitor();
            _isWaitingForResponse = false;

            Log.Debug(() => $"[VOICE] No speech detected ({elapsed}ms), ignoring");

            // Unlock microphone for next voice input
            VoiceInputManager.Instance?.MarkProcessingComplete();
        }

        /// <summary>
        /// Called on main thread when the pipeline encounters an error.
        /// </summary>
        private void HandleVoicePipelineError(string errorMessage, long elapsed, string transcript)
        {
            // Clean up interruption monitor (pipeline finished with error)
            StopInterruptionMonitor();
            _isWaitingForResponse = false;

            // Provide a fallback response
            string fallback = GetFallbackResponse();

            if (!string.IsNullOrWhiteSpace(fallback))
            {
                SubtitleManager.Instance.ShowSubtitle(_npcName, fallback, 25f);
            }

            OnError?.Invoke(errorMessage);
            OnResponseComplete?.Invoke(fallback);

            Log.Warning($"[VOICE] Error ({elapsed}ms): {errorMessage}. Using fallback.");

            // Unlock microphone for next voice input (error path)
            VoiceInputManager.Instance?.MarkProcessingComplete();
        }

        /// <summary>
        /// Coroutine: fetch RAG memory context, then send chat request to LLM.
        /// Graceful degradation: if embedding fails, uses base prompt as-is.
        /// </summary>
        private IEnumerator ProcessPlayerMessageCoroutine(string basePrompt, string senseBlock, string llmInput, CancellationToken interruptToken, Action<string> onComplete)
        {
            string playerId = !string.IsNullOrEmpty(_lastPlayerPersistentId) ? _lastPlayerPersistentId : "unknown";

            // Deterministic player-name capture (before RAG + LLM)
            TryCapturePlayerName(llmInput, playerId, _npcName);

            // Fetch relevant memory context (async via TaskCompletionSource → coroutine bridge)
            string memoryContext = "";

            if (NPCMemoryManager.Instance != null && !string.IsNullOrEmpty(llmInput))
            {
                Task<string> memoryTask = null;
                try { memoryTask = NPCMemoryManager.Instance.GetRelevantContextAsync(playerId, _npcName, llmInput); }
                catch (Exception ex) { Log.Warning($"[RAG] Retrieval init failed: {ex.Message}"); }

                if (memoryTask != null)
                {
                    yield return AwaitTask(memoryTask, result => memoryContext = result);
                }
            }

            // Inject memory context + player identity into system prompt (if any)
            string finalPrompt = basePrompt;
            if (!string.IsNullOrEmpty(memoryContext))
            {
                // Determine the noun to use for the survivor in memories
                string survivorNoun = !string.IsNullOrEmpty(_lastPlayerDisplayName) && _lastPlayerDisplayName != "Survivor"
                    ? _lastPlayerDisplayName
                    : "the survivor";

                // Split memories by [NPC] / [SURVIVOR] tags into separate context blocks
                var npcMemories = new List<string>();
                var survivorMemories = new List<string>();

                // memoryContext format: "[Recalled Context:\n- [SURVIVOR] fact\n- [NPC] fact\n]"
                string[] rawLines = memoryContext.Split('\n');
                foreach (string raw in rawLines)
                {
                    string trimmed = raw.TrimStart('-', ' ').Trim();
                    if (trimmed.StartsWith("[Recalled") || trimmed == "]" || trimmed.Length < 3)
                        continue;
                    if (trimmed.StartsWith("[NPC] "))
                    {
                        npcMemories.Add(trimmed.Substring(6).Replace("The Player", survivorNoun));
                    }
                    else if (trimmed.StartsWith("[SURVIVOR] "))
                    {
                        survivorMemories.Add(trimmed.Substring(11).Replace("The Player", survivorNoun));
                    }
                    else if (!trimmed.StartsWith("[") && trimmed.Length > 3)
                    {
                        // Untagged legacy memory - default to survivor
                        survivorMemories.Add(trimmed.Replace("The Player", survivorNoun));
                    }
                }

                StringBuilder contextBlock = new StringBuilder();
                if (npcMemories.Count > 0)
                {
                    contextBlock.Append("[ABOUT YOU]\n");
                    foreach (string m in npcMemories)
                        contextBlock.Append($"- {m}\n");
                    contextBlock.Append($"These are facts about yourself. You know this is your status.\n\n");
                }
                if (survivorMemories.Count > 0)
                {
                    contextBlock.Append("[ABOUT YOUR COMPANION]");
                    contextBlock.Append(survivorNoun);
                    contextBlock.Append("]\n");
                    foreach (string m in survivorMemories)
                        contextBlock.Append($"- {m}\n");
                    contextBlock.Append($"These are facts about {survivorNoun}. Reference them naturally when relevant - if they mention their dog, acknowledge you remember it. If they bring up their past, show you recall the details.");
                }

                finalPrompt = basePrompt + "\n\n" + contextBlock.ToString();
            }
            if (!string.IsNullOrEmpty(senseBlock))
                finalPrompt += "\n\n" + senseBlock;

            // Inject player's given name (quiet factual context, no instruction to use it)
            string givenName = NPCMemoryManager.Instance.GetGivenName(playerId, _npcName);
            if (!string.IsNullOrEmpty(givenName))
                finalPrompt += $"\n\nThe survivor's name is {givenName}.";
            // Inject tenure (quiet factual context, no instruction)
            string tenureStr = NPCMemoryManager.Instance.GetTenureString(playerId, _npcName);
            if (!string.IsNullOrEmpty(tenureStr))
                finalPrompt += $"\n\n{tenureStr}";
            // Late reinforcement of the NPC's own name (recency bias - small models attend
            // far more reliably to facts near the end of a long prompt than the front-loaded
            // identity line in BuildSystemPrompt()).
            finalPrompt += $"\n\nRemember: your own name is {_npcName}.";

            // Track whether this is the first chunk (for subtitle + _isWaitingForResponse)
            bool firstChunk = true;

            // Streaming: start TTS on each sentence as it arrives, process action/history when done
            LLMService.Instance.SendStreamingChatRequest(
                _entityId,
                finalPrompt,
                _conversationHistory,
                llmInput,
                interruptToken,
                chunk =>
                {
                    // First chunk: unblock the player + show subtitle
                    if (firstChunk)
                    {
                        firstChunk = false;
                        _isWaitingForResponse = false;
                        long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _pipelineStartMs;
                        Log.Debug(() => $"[TIMING] First streaming chunk → audio: {elapsed}ms");
                        // Clean action tags ([follow], [trade], etc.) and emote tags before showing
                        SubtitleManager.Instance.ShowSubtitle(_npcName, CleanDialogueForTts(chunk), 25f);
                    }
                    if (_ttsEnabled && _audioPlayer != null && TTSService.Instance.IsInitialized)
                        _audioPlayer.EnqueueSpeech(chunk, () => XNPCVoiceControl.UI.SubtitleManager.Instance.ClearSubtitle());
                },
                fullResponse => HandleLLMResponseNoTTS(fullResponse, onComplete),
                error => HandleLLMError(error)
            );
        }

        /// <summary>
        /// Bridge: yield on an async Task from a Unity coroutine.
        /// </summary>
        private IEnumerator AwaitTask(Task task)
        {
            while (!task.IsCompleted)
                yield return null;
            if (task.IsFaulted)
                throw task.Exception.InnerException ?? task.Exception;
        }

        private IEnumerator AwaitTask<T>(Task<T> task, System.Action<T> callback)
        {
            while (!task.IsCompleted)
                yield return null;
            if (task.IsFaulted)
                throw task.Exception.InnerException ?? task.Exception;
            callback(task.Result);
        }

    #endregion
    }
}
