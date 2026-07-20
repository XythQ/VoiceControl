using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml;
using UnityEngine;
using XNPCVoiceControl.Net;
using XNPCVoiceControl.TTS;
using XNPCVoiceControl.STT;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Main mod entry point for NPC LLM Chat.
    /// Handles initialization, configuration loading, and lifecycle management.
    /// </summary>
    public class XNPCVoiceControlMod : IModApi
    {
        // P/Invoke: pin onnxruntime.dll by absolute path before any InferenceSession.
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private static string _modPath;
        private static XNPCVoiceControlMod _instance;
        private static LLMConfig _config;
        private static TTSConfig _ttsConfig;
        private static STTConfig _sttConfig;
        private static HeadGestureConfig _headGestureConfig;
        private static Core.BillingConfig _billingConfig;
        private static Core.FollowAssistConfig _followAssistConfig;
        private static Core.FormationConfig _formationConfig;

        public static Core.BillingConfig Billing => _billingConfig;
        public static Core.FollowAssistConfig FollowAssist => _followAssistConfig;
        public static Core.FormationConfig Formation => _formationConfig;

        // Personal-space floor for the follow servo (Phase 1 yield).
        private static float _followMinSeparation = 1.0f;
        public static float FollowMinSeparation => _followMinSeparation;

        // Tactical mode default — applied on hire/load to set varTacticalMode initially.
        private static bool _defaultTacticalMode = false;
        public static bool DefaultTacticalMode => _defaultTacticalMode;

        // RAG flush tuning (loaded from modconfig.xml <RAG> section)
        private static int _minFlushMessages = 4;       // skip flush if buffer < this (unless at 20 cap)
        private static float _extractionIdleSeconds = 100f; // idle before extraction fires

        public static int MinFlushMessages => _minFlushMessages;
        public static float ExtractionIdleSeconds => _extractionIdleSeconds;
        private static bool _initialized = false;

        /// <summary>
        /// Common English words that should never be treated as NPC names during voice transcription.
        /// Prevents false positives like "Open" → "Ben", "Where" → "Wren", etc.
        /// </summary>
        private static readonly HashSet<string> sStopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Articles, pronouns, prepositions
            "the", "a", "an", "is", "am", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could", "should",
            "shall", "may", "might", "must", "can", "need", "dare", "ought", "used",
            "i", "me", "my", "mine", "myself", "you", "your", "yours", "yourself",
            "he", "him", "his", "himself", "she", "her", "hers", "herself", "it", "its",
            "itself", "we", "us", "our", "ours", "ourselves", "they", "them", "their",
            "theirs", "themselves", "this", "that", "these", "those", "what", "which",
            "who", "whom", "whose", "where", "when", "why", "how",
            "in", "on", "at", "to", "for", "of", "with", "by", "from", "up", "about",
            "into", "through", "during", "before", "after", "above", "below", "between",
            "under", "again", "further", "then", "once", "here", "there", "and", "but",
            "or", "nor", "not", "so", "yet", "both", "either", "neither", "each",
            "every", "all", "any", "few", "more", "most", "other", "some", "such",
            "no", "only", "own", "same", "than", "too", "very", "just", "also",
            // Common verbs that start sentences
            "open", "close", "give", "take", "come", "go", "look", "find", "make",
            "keep", "let", "begin", "show", "call", "try", "ask", "use", "work",
            "seem", "feel", "leave", "put", "mean", "stay", "happen", "play", "run",
            "move", "live", "believe", "hold", "bring", "happen", "write", "provide",
            "sit", "stand", "lose", "pay", "meet", "include", "continue", "set",
            "learn", "change", "lead", "understand", "watch", "follow", "stop",
            "create", "speak", "read", "allow", "add", "spend", "grow", "open",
            // Common commands/phrases
            "please", "thank", "thanks", "sorry", "hello", "hi", "hey", "yes", "no",
            "wait", "stop", "help", "follow", "stay", "come", "go", "kill", "attack",
        };

        /// <summary>Reserved squad targeting keywords. Checked before sStopWords so they aren't swallowed.</summary>
        private static readonly HashSet<string> sSquadKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "squad", "everyone", "all", "team",
        };

        public static TTSConfig TTSConfig => _ttsConfig;
        public static STTConfig STTConfig => _sttConfig;
        public static LLMConfig Config => _config;
        public static HeadGestureConfig HeadGestureConfig => _headGestureConfig;

        public static string GetModPath() => _modPath;

        // --- Runtime CVar getters (read from PlayerPrefs, fall back to config defaults) ---

        private const string CVAR_CHAT_DISTANCE = "XNPCVoiceControl_ChatDistance";
        private const string CVAR_HIRED_CHAT_DISTANCE = "XNPCVoiceControl_HiredChatDistance";
        private const string CVAR_MAX_HISTORY = "XNPCVoiceControl_MaxHistory";
        private const string CVAR_VOICE_DISTANCE = "XNPCVoiceControl_VoiceDistance";

        /// <summary>
        /// Get the chat distance from saved CVar, falling back to 5m (XML default).
        /// </summary>
        public static float GetChatDistance()
        {
            return PlayerPrefs.GetFloat(CVAR_CHAT_DISTANCE, 5f);
        }

        /// <summary>
        /// Get the hired NPC chat distance from saved CVar, falling back to 20m.
        /// Hired NPCs get a larger range so you can give them commands from further away.
        /// </summary>
        public static float GetHiredChatDistance()
        {
            return PlayerPrefs.GetFloat(CVAR_HIRED_CHAT_DISTANCE, 20f);
        }

        /// <summary>
        /// Get the max history length from saved CVar, falling back to config.ContextMemory.
        /// </summary>
        public static int GetMaxHistory()
        {
            float saved = PlayerPrefs.GetFloat(CVAR_MAX_HISTORY, -1f);
            if (saved >= 0f)
                return Mathf.RoundToInt(saved);
            return _config?.ContextMemory ?? 10;
        }

        /// <summary>
        /// Get the voice distance from saved CVar, falling back to TTS config MaxDistance.
        /// </summary>
        public static float GetVoiceDistance()
        {
            return PlayerPrefs.GetFloat(CVAR_VOICE_DISTANCE, _ttsConfig?.MaxDistance ?? 20f);
        }

        public void InitMod(Mod _modInstance)
        {
            _instance = this;
            _modPath = _modInstance.Path;

            // Initialize MainThreadDispatcher on the main thread before any background task runs.
            // Captures _mainThreadId and creates the GameObject (Unity API) safely on-thread.
            MainThreadDispatcher.Touch();

            Log.Out("Initializing XNPCVoiceControl...");

            if (GameManager.IsDedicatedServer)
            {
                Log.Out("[VoiceMod] Dedicated server detected — voice/audio/UI subsystems disabled; NPC AI and voice NetPackage relay active.");
            }

            // Load configurations
            _config = LoadConfig();
            if (_config == null)
            {
                Log.Error("Failed to load configuration. Mod disabled.");
                return;
            }

            // Auto-detect player's vanilla game language (supersedes manual JP toggle)
            LocalizationHelper.InitLanguageDetection();

            // Discover the actual .gguf model on disk and override the XML <Model> value.
            // Only do this for the default local llama-server endpoint — if the user configured
            // an external API (OpenAI, Anthropic, etc.), respect their <Model> setting entirely.
            string defaultLocalEndpoint = LLMService.DefaultChatEndpoint;
            bool isLocalEndpoint = _config.Endpoint.Equals(defaultLocalEndpoint, StringComparison.OrdinalIgnoreCase);

            if (isLocalEndpoint)
            {
                string configuredFilename = !string.IsNullOrEmpty(_config.ModelFilename) ? _config.ModelFilename : null;
                string discoveredGguf = ServerManager.FindGgufModel(Path.Combine(_modPath, "Resources"), configuredFilename);
                if (string.IsNullOrEmpty(discoveredGguf))
                {
                    // Fallback: resolve via assembly location (matches ServerManager's runtime path)
                    string asmPath = typeof(XNPCVoiceControlMod).Assembly.Location;
                    if (!string.IsNullOrEmpty(asmPath))
                    {
                        string asmDir = Path.GetDirectoryName(asmPath);
                        discoveredGguf = ServerManager.FindGgufModel(Path.Combine(asmDir, "Resources"), configuredFilename);
                    }
                }
                if (!string.IsNullOrEmpty(discoveredGguf))
                {
                    _config.Model = Path.GetFileNameWithoutExtension(discoveredGguf);
                    Log.Out($"LLM model loaded: {_config.Model} ({discoveredGguf})");
                }
                else
                {
                    Log.Warning($"[1-XNPCVoiceControl] No .gguf found in Resources/ — using XML default: {_config.Model}");
                }
            }
            else
            {
                Log.Out($"[1-XNPCVoiceControl] External API endpoint detected — using XML model: {_config.Model}");
            }

            // Initialize LLM Service
            LLMService.Instance.Initialize(_config);

            // Load RAG extraction prompt from XML (no recompile needed for prompt tuning)
            string ragPrompt = LoadRagExtractionPrompt();
            if (!string.IsNullOrEmpty(ragPrompt))
            {
                LLMService.Instance.SetRagExtractionPrompt(ragPrompt);
            }

            // Load extraction user prefix (frames the transcript in the user message)
            string ragUserPrefix = LoadRagExtractionUserPrefix();
            if (!string.IsNullOrEmpty(ragUserPrefix))
            {
                LLMService.Instance.SetExtractionUserPrefix(ragUserPrefix);
            }

            // Load extraction tuning knobs (temperature, max facts)
            float ragTemp = LoadRagExtractionTemperature();
            if (ragTemp >= 0f)
            {
                LLMService.Instance.SetExtractionTemperature(ragTemp);
            }

            int ragMaxFacts = LoadRagMaxExtractedFacts();
            if (ragMaxFacts > 0)
            {
                LLMService.Instance.SetMaxExtractedFacts(ragMaxFacts);
            }

            string embeddingEndpoint = LoadEmbeddingEndpoint();
            if (!string.IsNullOrEmpty(embeddingEndpoint))
            {
                LLMService.Instance.SetEmbeddingEndpoint(embeddingEndpoint);
            }

            // Load RAG flush tuning knobs
            _minFlushMessages = LoadRagMinFlushMessages();
            _extractionIdleSeconds = LoadRagExtractionIdleSeconds();

            // Load ambient NPC-to-NPC chatter config
            LoadNPCToNPCConfig();

            // Load TTS and STT configs on all platforms (needed for server-side pipeline logic)
            _ttsConfig = LoadTTSConfigInternal();
            _sttConfig = LoadSTTConfig();

            // Load head gesture config (uses same modconfig.xml <TTS> section as lip-sync)
            _headGestureConfig = LoadHeadGestureConfig();

            // Load billing config (weekly hire billing)
            _billingConfig = LoadBillingConfig();

            // Load follow-assist watchdog config (stuck-follow catch-up teleport)
            _followAssistConfig = LoadFollowAssistConfig();
            _formationConfig = LoadFormationConfig();

            // Personal-space floor for the follow servo (Phase 1 yield).
            _followMinSeparation = LoadFollowMinSeparation();

            // Tactical mode default — applied on hire/load.
            _defaultTacticalMode = LoadDefaultTacticalMode();

            // Consolidated config summary (replaces 14 individual Log.Out lines above)
            Log.Out($"Config: TTS={(_ttsConfig.Enabled ? "on" : "off")} ({_ttsConfig.TtsEngine}, {_ttsConfig.ServerUrl}), STT={(_sttConfig.Enabled ? "on" : "off")} ({_sttConfig.ServerUrl}), WakeWord={_sttConfig.WakeWordEnabled}, NPCToNPC={NPCToNPCChatManager.Instance?._enabled}");

            // Guard: wake-word + Supertonic is incompatible (CPU contention kills both).
            if (_sttConfig.WakeWordEnabled && _ttsConfig.TtsEngine != "sherpa")
            {
                Log.Warning("[VoiceMod] WakeWordEnabled=true is not compatible with TtsEngine=" + _ttsConfig.TtsEngine + ". Forcing TtsEngine=sherpa. Disable wake-word or wait for GPU wake-word support to use Supertonic.");
                _ttsConfig.TtsEngine = "sherpa";
                _ttsConfig.ServerUrl = "http://127.0.0.1:5053";
                Log.Out("[VoiceMod] TTS engine forced to sherpa (port 5053) for wake-word compatibility.");
            }

            // Client-only: microphone capture, TTS playback, wake word listener.
            // Dedicated servers have no audio subsystem — Microphone.devices throws.
            if (!GameManager.IsDedicatedServer)
            {
                if (_ttsConfig != null && _ttsConfig.Enabled)
                {
                    TTSService.Instance.Initialize(_ttsConfig);
                }

                if (_sttConfig != null && _sttConfig.Enabled)
                {
                    STTService.Instance.Initialize(_sttConfig);
                    MicrophoneCapture.Instance.Initialize(_sttConfig);

                    // Wire up microphone capture to talk to nearest NPC
                    MicrophoneCapture.Instance.OnTranscriptionComplete += OnVoiceTranscribed;

                    // Wire up voice input manager (wake-word recording path)
                    VoiceInputManager.Instance.OnTranscriptionComplete += OnVoiceTranscribed;

                    // Initialize wake word listener (optional, hands-free activation)
                    if (_sttConfig.WakeWordEnabled)
                    {
                        Log.Out("Wake word: initializing...");

                        // Pin the correct onnxruntime.dll by absolute path BEFORE any InferenceSession.
                        // PATH-append below is kept for transitive native deps, but LoadLibrary wins over System32 shadowing.
                        string onnxNativePath = Path.Combine(_modPath, "Plugins", "OnnxRuntime");
                        if (Directory.Exists(onnxNativePath))
                        {
                            try
                            {
                                var ortDllPath = Path.Combine(onnxNativePath, "onnxruntime.dll");
                                IntPtr hModule = LoadLibrary(ortDllPath);
                                if (hModule == IntPtr.Zero)
                                {
                                    Log.Error($"Wake word: LoadLibrary returned null for {ortDllPath}");
                                }
                                else
                                {
                                    Log.Out($"Wake word: pinned onnxruntime.dll from {ortDllPath}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"Wake word: failed to pin onnxruntime.dll — {ex.Message}");
                            }

                            Environment.SetEnvironmentVariable("PATH", Environment.GetEnvironmentVariable("PATH") + ";" + onnxNativePath);
                            Log.Out($"Wake word: added {onnxNativePath} to PATH");
                        }
                        else
                        {
                            Log.Warning($"Wake word: native DLL folder not found at {onnxNativePath}");
                        }

                        string modelsDir = ResolveWakeWordModelsDir();
                        if (!string.IsNullOrEmpty(modelsDir))
                        {
                            Log.Out($"Wake word: models found at {modelsDir}");

                            try
                            {
                                var runtimeConfig = new WakeWordRuntimeConfig
                                {
                                    WakeWords = new[]
                                    {
                                        new WakeWordConfig
                                        {
                                            Model = _sttConfig.WakeWordModel,
                                            Threshold = _sttConfig.WakeWordThreshold,
                                            TriggerLevel = 2,
                                            Refractory = 20
                                        }
                                    },
                                    StepFrames = 4,
                                    DebugAction = (model, prob, detected) =>
                                    {
                                        if (detected)
                                            Log.Out($"WakeWord [{model}] probability: {prob:F4} DETECTED");
                                        else if (prob > 0.1f)
                                            Log.Debug(() => $"WakeWord [{model}] probability: {prob:F4}");
                                    }
                                };

                                string micDevice = MicrophoneCapture.Instance.SelectedDevice;
                                bool ok = WakeWordListener.Instance.Initialize(modelsDir, micDevice, _sttConfig.SampleRate, runtimeConfig, _sttConfig.WakeWordThreadPriority, _sttConfig.WakeWordMaxChunksPerSignal);
                                if (ok)
                                    Log.Out("Wake word: listener started successfully");
                                else
                                    Log.Warning("Wake word: listener failed to start");
                            }
                            catch (TypeInitializationException tex)
                            {
                                Log.Error($"Wake word ONNX init FAILED — {tex.Message}");
                                Log.Error("Likely cause: conflicting onnxruntime.dll loaded from System32 or another mod's bin/.");
                                Log.Error("Fix: check Plugins/OnnxRuntime/onnxruntime.dll exists; install VC++ 2015-2022 x64 redistributable.");
                                if (tex.InnerException != null)
                                    Log.Error($"  Root cause: {tex.InnerException.GetType().Name}: {tex.InnerException.Message}");
                            }
                            catch (DllNotFoundException dllex)
                            {
                                Log.Error($"Wake word native DLL missing — {dllex.Message}");
                                Log.Error("Fix: ensure Plugins/OnnxRuntime/ has onnxruntime.dll + vcruntime140.dll.");
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"Wake word init exception: {ex.GetType().Name}: {ex.Message}");
                                if (ex.InnerException != null)
                                    Log.Error($"  Inner: {ex.InnerException.Message}");
                            }
                        }
                        else
                        {
                            Log.Warning("WakeWord enabled but ONNX models not found — wake word disabled");
                        }
                    }

                    // Initialize voice input manager (coordinates push-to-talk + wake word)
                    VoiceInputManager.Instance.Initialize(_sttConfig, WakeWordListener.Instance, MicrophoneCapture.Instance);
                }
            }
            else
            {
                Log.Out("[1-XNPCVoiceControl] Dedicated server detected — skipping microphone/TTS/wake-word init");
            }

            // Load phrase triggers from XML config
            PhraseTriggerHandler.Instance.Initialize(_modPath);

            // Load NPC personality definitions
            PersonalityManager.Instance.LoadPersonalities(_modPath);

            // Index voice clips from all loaded mods' Resources/NPC_Voices folders
            Core.VoiceClipLibrary.Instance.Build();

            // Start llama-server and sherpa-server immediately (not waiting for game start)
            if (!GameManager.IsDedicatedServer)
                ServerManager.StartServers();

            // Register for game events
            ModEvents.GameStartDone.RegisterHandler(GameStartDoneHandler);
            ModEvents.GameShutdown.RegisterHandler(GameShutdownHandler);
            ModEvents.PlayerSpawnedInWorld.RegisterHandler(PlayerSpawnedInWorldHandler);

            // Additional shutdown hooks for reliable server cleanup.
            // ModEvents.GameShutdown may not fire on hard exit (Alt+F4 from main menu).
            AppDomain.CurrentDomain.ProcessExit += (s, e) => ServerManager.StopServers();

            Log.Out("Mod initialized successfully!");
        }

        private static void GameStartDoneHandler(ref ModEvents.SGameStartDoneData data)
        {
            if (_initialized) return;

            Log.Out("Game started - initializing Harmony patches...");

            NPCFaceLipSync.ClearProcCache();

            try
            {
                Core.ChatComponentManager.Initialize(_config);
                NPCEventMemoryHooks.Register();
                Harmony.ChatMessagePatch.Apply();

                // Loaded Chamber: start proximity-based greeting pre-generation (client only)
                if (!GameManager.IsDedicatedServer)
                {
                    _ = NPCWarmUpManager.Instance; // Lazy-init → Start() → coroutine begins
                    _ = NPCToNPCChatManager.Instance;
                }

                _initialized = true;
                Log.Out("Ready! Talk to NPCs using @message in chat");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to initialize: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void GameShutdownHandler(ref ModEvents.SGameShutdownData data)
        {
            Log.Out("Shutting down...");
            Harmony.ChatMessagePatch.Unapply();
            NPCEventMemoryHooks.Unregister();
            Core.ChatComponentManager.Shutdown();
            ServerManager.StopServers();

            // Clean up wake word listener (client-only — not initialized on dedi servers)
            if (!GameManager.IsDedicatedServer && WakeWordListener.Instance != null)
            {
                WakeWordListener.Instance.Cleanup();
            }

            // Shut down Loaded Chamber warmup manager
            NPCWarmUpManager.Instance?.Shutdown();

            // Shut down ambient NPC-to-NPC chatter
            NPCToNPCChatManager.Instance?.Shutdown();

            _initialized = false;
        }

        /// <summary>Guard against double-send of handshake if handler fires more than once per session.</summary>
        private static bool _handshakeSent;

        /// <summary>
        /// Start NPC diagnostic roster scan after player spawns in world.
        /// Scans at T+5s and T+15s — read-only logging for respawn persistence debugging.
        /// </summary>
        private static void PlayerSpawnedInWorldHandler(ref ModEvents.SPlayerSpawnedInWorldData data)
        {
            ServerManager.StartNPCDiagScan();

            // MP Phase 1c: send version handshake on dedi client spawn (once per session).
            if (!_handshakeSent && SingletonMonoBehaviour<ConnectionManager>.Instance.IsClient)
            {
                _handshakeSent = true;
                try
                {
                    SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
                        NetPackageManager.GetPackage<Net.NetPackageVCHandshake>()
                            .Setup(Net.VCBuildId.Current), false);
                    Log.Out($"[VC-NET] Handshake sent: {Net.VCBuildId.Current}");
                }
                catch (Exception ex)
                {
                    Log.Warning($"[VC-NET] Handshake send failed: {ex.Message}");
                }
            }
        }


        /// </summary>
        private static void CopyDirectory(string sourceDir, string destDir)
        {
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                if (!File.Exists(destFile))
                    File.Copy(file, destFile, false);
            }

            foreach (string subdir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destDir, Path.GetFileName(subdir));
                CopyDirectory(subdir, destSubDir);
            }
        }

        /// <summary>
        /// Called when voice transcription completes - sends message to nearest NPC
        /// Checks if first word is an NPC name for target override + intent bonus.
        /// </summary>
        private static void OnVoiceTranscribed(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Log.Warning("Voice transcription returned empty text");
                return;
            }

            // Sanitize transcript: strip caption artifacts and hallucinated phrases
            text = STTService.SanitizeTranscript(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                Log.Warning("Voice transcription returned empty after sanitization");
                return;
            }

            Log.Debug(() => $"[TIMING] Voice pipeline start: \"{text}\"");

            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null)
            {
                Log.Warning("No primary player found for voice input, aborting");
                return;
            }

            // Step 1: Check if first word is a squad keyword (target all hired NPCs)
            string cleanedText = text;
            bool usedName = false;
            EntityAlive namedTarget = null;
            bool squadTarget = false;

            string firstWord = ExtractFirstWord(text);
            if (!string.IsNullOrEmpty(firstWord) && sSquadKeywords.Contains(firstWord))
            {
                // Squad keyword — resolve roster of all hired NPCs (no distance filter)
                var squadRoster = ResolveSquadRoster(player);
                Log.Out($"[VC-SQUAD] Keyword \"{firstWord}\" detected → {squadRoster.Count} NPC(s)");

                if (squadRoster.Count == 0)
                {
                    Log.Debug(() => "[VC-SQUAD] No hired NPCs in roster, no-op");
                    XNPCVoiceControl.UI.SubtitleManager.Instance.ShowSubtitle("", LocalizationHelper.Get("no_npc_nearby"), 4f);
                    return;
                }

                squadTarget = true;
                // Strip the keyword + leading punctuation (same as name path)
                cleanedText = text.Substring(firstWord.Length).Trim();
                cleanedText = cleanedText.Trim(',', '.', '!', '?', ' ', '\t');

                // Nearest NPC is the ack responder; others comply silently
                namedTarget = GetNearestEntity(squadRoster, player.position);
                Log.Debug(() => $"[VC-SQUAD] Ack responder: {namedTarget.EntityName}, cleaned message: \"{cleanedText}\"");
            }
            else if (!string.IsNullOrEmpty(firstWord) && firstWord.Length >= 2 && !sStopWords.Contains(firstWord))
            {
                namedTarget = FindNPCByName(player, firstWord, 20f);
                if (namedTarget != null)
                {
                    usedName = true;
                    // Remove the name from the message
                    cleanedText = text.Substring(firstWord.Length).Trim();
                    // Strip leading punctuation (e.g., "Billy, stay" -> "stay")
                    cleanedText = cleanedText.Trim(',', '.', '!', '?', ' ', '\t');
                    Log.Debug(() => $"Name detected: \"{firstWord}\" → target {namedTarget.EntityName}, cleaned message: \"{cleanedText}\"");
                }
            }

            // Step 2: Find target NPC (named override or nearest)
            // Priority: named/squad target > nearest trader > nearest hired NPC
            // Traders are in-game traders (can't be hired, don't move, only trade/quests).
            // Hired NPCs (SDX) respond to orders. If both are nearby, only target hired NPCs by name.
            EntityAlive targetNPC;
            if (namedTarget != null)
            {
                targetNPC = namedTarget;
            }
            else
            {
                // First check for a nearby trader (close proximity, ~5m)
                EntityAlive nearestTrader = FindNearestTrader(player, 5f);
                if (nearestTrader != null)
                {
                    targetNPC = nearestTrader;
                }
                else
                {
                    // No trader nearby — fall back to nearest hired NPC
                    targetNPC = FindNearestNPC(player, 15f);
                }
            }

            if (targetNPC == null)
            {
                Log.Debug(() => "No NPC nearby to talk to via voice");
                Log.Debug(() => $"Checked {GameManager.Instance?.World?.Entities?.list?.Count ?? 0} entities");
                XNPCVoiceControl.UI.SubtitleManager.Instance.ShowSubtitle("", LocalizationHelper.Get("no_npc_nearby"), 4f);
                return;
            }

            // === TRADER HANDLING ===
            // Traders have their own base game voice system — leave it untouched.
            // We only allow the "let's trade" phrase trigger to open the trade dialog.
            // All other chat with traders is blocked (no LLM, no Kokoro TTS).
            if (IsTrader(targetNPC))
            {
                Log.Debug(() => $"Target is a trader ({targetNPC.EntityName}) — only trade commands allowed");
                string triggerResponse;
                if (PhraseTriggerHandler.Instance.Enabled &&
                    PhraseTriggerHandler.Instance.TryHandlePhrase(cleanedText, targetNPC, player, targetNPC.EntityName, out triggerResponse, true, usedName, "en", LocalizationHelper.GetPlayerUiLanguage()))
                {
                    // Phrase trigger matched — check if it's the OpenTraderInventory action
                    // (other actions like GiveItem won't match for traders since only OpenTraderInventory phrases exist for them)
                    Log.Debug(() => $"Trader phrase trigger matched for {targetNPC.EntityName}: {triggerResponse}");
                    if (player is EntityPlayerLocal localPlayer && !string.IsNullOrWhiteSpace(triggerResponse))
                    {
                        XNPCVoiceControl.UI.SubtitleManager.Instance.ShowSubtitle(targetNPC.EntityName, triggerResponse);
                    }
                    return; // Trade dialog opened, done — no LLM, no TTS
                }
                // No matching phrase — silently ignore (trader uses base game voice, not our chat system)
                Log.Debug(() => $"No trade phrase matched for trader {targetNPC.EntityName}, ignoring chat");
                return;
            }

            // === HIRED ANIMAL HANDLING (phrase triggers only, no LLM/TTS) ===
            // Hired animals (npcAnimalFox, etc.) accept movement/order commands via phrase triggers
            // but don't participate in LLM conversation.
            if (IsAnimalEntity(targetNPC))
            {
                Log.Debug(() => $"Target is a hired animal ({targetNPC.EntityName}) — only command phrases allowed");
                string triggerResponse;
                if (PhraseTriggerHandler.Instance.Enabled &&
                    PhraseTriggerHandler.Instance.TryHandlePhrase(cleanedText, targetNPC, player, targetNPC.EntityName, out triggerResponse, true, usedName, "en", LocalizationHelper.GetPlayerUiLanguage()))
                {
                    Log.Debug(() => $"Animal command matched for {targetNPC.EntityName}: {triggerResponse}");
                    if (player is EntityPlayerLocal localPlayer && !string.IsNullOrWhiteSpace(triggerResponse))
                    {
                        XNPCVoiceControl.UI.SubtitleManager.Instance.ShowSubtitle(targetNPC.EntityName, triggerResponse);
                    }
                    return;
                }
                Log.Debug(() => $"No phrase matched for animal {targetNPC.EntityName}, ignoring chat");
                return;
            }

            // === SQUAD COMMAND HANDLING ===
            // Squad keyword detected — route through VCCommandRouter.ExecuteForSquad.
            // Nearest NPC produces the voice/subtitle ack; others comply silently.
            if (squadTarget && namedTarget != null)
            {
                var squadRoster = ResolveSquadRoster(player);
                string triggerResponse = null;
                bool matched = PhraseTriggerHandler.Instance.Enabled &&
                    PhraseTriggerHandler.Instance.TryHandlePhrase(cleanedText, namedTarget, player, namedTarget.EntityName,
                        out triggerResponse, false, usedName, "en", LocalizationHelper.GetPlayerUiLanguage());

                if (matched)
                {
                    // Resolve VCCommand + arg from matched trigger.
                    if (PhraseTriggerHandler.Instance.TryResolveSquadCommand(out var cmd, out var arg, cleanedText))
                    {
                        VCCommandRouter.ExecuteForSquad(player.entityId, namedTarget.entityId, squadRoster, cmd, arg);
                    }
                    else
                    {
                        Log.Out($"[VC-SQUAD] No VCCommand mapped from '{PhraseTriggerHandler.Instance.LastMatchedTriggerName}'");
                    }

                    // Ack: nearest NPC shows subtitle + voices the response.
                    if (player is EntityPlayerLocal && !string.IsNullOrWhiteSpace(triggerResponse))
                    {
                        XNPCVoiceControl.UI.SubtitleManager.Instance.ShowSubtitle(namedTarget.EntityName, triggerResponse);
                        var ackComp = Core.ChatComponentManager.GetOrCreate(namedTarget);
                        if (ackComp != null)
                            ackComp.SpeakText(triggerResponse);
                    }
                    return;
                }
                // No phrase matched — fall through to normal LLM chat with nearest NPC.
                Log.Debug(() => $"[VC-SQUAD] No phrase matched for \"{cleanedText}\", falling through to LLM");
            }

            // === HIRED NPC HANDLING (normal chat flow) ===
            // Get or create chat component
            var chatComponent = Core.ChatComponentManager.GetOrCreate(targetNPC);
            if (chatComponent == null)
            {
                Log.Warning("Failed to get chat component for NPC");
                return;
            }

            // Clean up STT artifacts (backslashes, control chars) before sending to LLM
            cleanedText = System.Text.RegularExpressions.Regex.Replace(cleanedText, "[\\\\\x00-\x1F]", "").Trim();
            // Strip whisper.cpp non-speech tokens: [BLANK_AUDIO], speaker labels, parenthesized audio events ((music), etc.)
            cleanedText = System.Text.RegularExpressions.Regex.Replace(
                cleanedText,
                @"\[[^\]]+\]|\([^)]+\)",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

            Log.Debug(() => $"Voice message to {chatComponent.NPCName}: \"{cleanedText}\" (usedName: {usedName})");

            // Send message to NPC with name bonus flag
            chatComponent.ProcessPlayerMessage(cleanedText, player, usedName, response =>
            {
                Log.Debug(() => $"{chatComponent.NPCName} responded: {response}");
            });
        }

        /// <summary>
        /// Extract the first word from a string (no allocation).
        /// </summary>
        private static string ExtractFirstWord(string text)
        {
            int end = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    end = i;
                    break;
                }
                end = i + 1;
            }
            if (end == 0) return null;
            return text.Substring(0, end).Trim(',', '.', '!', '?');
        }

        /// <summary>
        /// Find an NPC by name within range of the player.
        /// Uses exact Contains match first, then falls back to fuzzy (Levenshtein) match for STT errors.
        /// </summary>
        private static EntityAlive FindNPCByName(EntityPlayer player, string name, float maxDistance)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var world = GameManager.Instance?.World;
            if (world == null) return null;

            string lowerName = name.ToLower();

            // Pass 1: Exact Contains match
            foreach (var entity in world.Entities.list)
            {
                if (entity is EntityAlive alive && alive.entityId != player.entityId)
                {
                    if (!IsNPC(alive)) continue;

                    float dist = UnityEngine.Vector3.Distance(player.position, alive.position);
                    if (dist > maxDistance) continue;

                    // Check NPC display name (from chat component or personality)
                    var chatComp = Core.ChatComponentManager.GetOrCreate(alive);
                    if (chatComp != null && chatComp.NPCName.ToLower().Contains(lowerName))
                    {
                        Log.Debug(() => $"Name match: \"{name}\" → {chatComp.NPCName} at {dist:F1}m");
                        return alive;
                    }

                    // Also check entity class name
                    if (alive.EntityName.ToLower().Contains(lowerName))
                    {
                        Log.Debug(() => $"Name match (entity): \"{name}\" → {alive.EntityName} at {dist:F1}m");
                        return alive;
                    }
                }
            }

            // Pass 2: Fuzzy match (Levenshtein distance ≤ 2) for STT transcription errors
            // e.g., "Nana" → "Nanna", "Bil" → "Billy"
            EntityAlive bestFuzzy = null;
            int bestFuzzyDist = 3; // max edit distance
            float bestFuzzyRange = maxDistance;

            foreach (var entity in world.Entities.list)
            {
                if (entity is EntityAlive alive && alive.entityId != player.entityId)
                {
                    if (!IsNPC(alive)) continue;

                    float dist = UnityEngine.Vector3.Distance(player.position, alive.position);
                    if (dist > maxDistance) continue;

                    var chatComp = Core.ChatComponentManager.GetOrCreate(alive);
                    string displayName = chatComp?.NPCName ?? alive.EntityName;

                    // Require shared prefix of at least 2 characters to prevent
                    // common words like "open" from matching unrelated names like "ben"
                    if (!HasSharedPrefix(lowerName, displayName.ToLower(), 2))
                        continue;

                    int editDist = LevenshteinDistance(lowerName, displayName.ToLower());
                    if (editDist < bestFuzzyDist || (editDist == bestFuzzyDist && dist < bestFuzzyRange))
                    {
                        bestFuzzy = alive;
                        bestFuzzyDist = editDist;
                        bestFuzzyRange = dist;
                    }
                }
            }

            if (bestFuzzy != null && bestFuzzyDist <= 2)
            {
                var chatComp = Core.ChatComponentManager.GetOrCreate(bestFuzzy);
                Log.Debug(() => $"Fuzzy name match: \"{name}\" → {chatComp?.NPCName ?? bestFuzzy.EntityName} (edit distance: {bestFuzzyDist}, range: {bestFuzzyRange:F1}m)");
                return bestFuzzy;
            }

            return null;
        }

        /// <summary>
        /// Calculate Levenshtein (edit) distance between two strings.
        /// </summary>
        private static int LevenshteinDistance(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];

            for (int j = 0; j <= b.Length; j++) previous[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                int[] temp = previous;
                previous = current;
                current = temp;
            }

            return previous[b.Length];
        }

        /// <summary>
        /// Check if two strings share a common prefix of at least the specified length.
        /// Used to prevent fuzzy name matching from pairing unrelated words
        /// (e.g., "open" should not match "ben" even if edit distance is low).
        /// </summary>
        private static bool HasSharedPrefix(string a, string b, int minLength)
        {
            int len = Math.Min(a.Length, b.Length);
            if (len < minLength) return false;
            for (int i = 0; i < minLength; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Resolve squad roster — all hired NPCs belonging to this player (no distance filter).
        /// Used for squad targeting commands like "squad, back off".
        /// </summary>
        private static List<EntityAlive> ResolveSquadRoster(EntityPlayer player)
        {
            var roster = new List<EntityAlive>();
            var world = GameManager.Instance?.World;
            if (world == null) return roster;

            foreach (var entity in world.Entities.list)
            {
                if (entity is EntityAlive alive && Core.ChatComponentManager.IsChatTarget(alive))
                {
                    var leader = EntityUtilities.GetLeaderOrOwner(alive.entityId);
                    if (leader != null && leader.entityId == player.entityId)
                    {
                        roster.Add(alive);
                    }
                }
            }
            return roster;
        }

        /// <summary>
        /// Get nearest entity from a list to a position.
        /// </summary>
        private static EntityAlive GetNearestEntity(List<EntityAlive> entities, Vector3 position)
        {
            EntityAlive closest = null;
            float closestDist = float.MaxValue;
            foreach (var e in entities)
            {
                float d = Vector3.Distance(e.position, position);
                if (d < closestDist)
                {
                    closest = e;
                    closestDist = d;
                }
            }
            return closest;
        }

        /// <summary>
        /// Find nearest hired NPC within range (excludes traders).
        /// Traders are handled separately — they only respond to trade commands, not chat.
        /// </summary>
        private static EntityAlive FindNearestNPC(EntityPlayer player, float maxDistance)
        {
            EntityAlive closest = null;
            float closestDist = maxDistance;
            int npcCount = 0;

            var world = GameManager.Instance?.World;
            if (world == null) return null;

            Log.Debug(() => "Scanning entities for NPCs...");
            foreach (var entity in world.Entities.list)
            {
                if (entity is EntityAlive alive && alive.entityId != player.entityId)
                {
                    // Skip traders — they have their own voice system and only respond to trade commands
                    if (IsTrader(alive)) continue;

                    float dist = UnityEngine.Vector3.Distance(player.position, alive.position);
                    bool isNPC = IsNPC(alive);
                    Log.Debug(() => $"Entity: {alive.EntityName} (type: {alive.GetType().Name}) at {dist:F1}m - IsNPC: {isNPC}");

                    if (isNPC)
                    {
                        npcCount++;
                        if (dist < closestDist)
                        {
                            closest = alive;
                            closestDist = dist;
                        }
                    }
                }
            }

            Log.Debug(() => $"Found {npcCount} NPCs total, closest: {closest?.EntityName ?? "none"} at {closestDist:F1}m");
            return closest;
        }

        /// <summary>
        /// Find nearest trader within range.
        /// Traders are in-game traders (EntityTrader types) — they can't be hired, don't move,
        /// and only buy/sell and offer quests. Accessed via E key to open trade dialog.
        /// </summary>
        private static EntityAlive FindNearestTrader(EntityPlayer player, float maxDistance)
        {
            EntityAlive closest = null;
            float closestDist = maxDistance;

            var world = GameManager.Instance?.World;
            if (world == null) return null;

            foreach (var entity in world.Entities.list)
            {
                if (entity is EntityAlive alive && alive.entityId != player.entityId)
                {
                    if (!IsTrader(alive)) continue;

                    float dist = UnityEngine.Vector3.Distance(player.position, alive.position);
                    if (dist < closestDist)
                    {
                        closest = alive;
                        closestDist = dist;
                    }
                }
            }

            if (closest != null)
            {
                Log.Debug(() => $"Found trader: {closest.EntityName} (type: {closest.GetType().Name}) at {closestDist:F1}m");
            }
            return closest;
        }

        /// <summary>
        /// Check if entity is a valid chat target (NPC, hired companion, trader).
        /// Delegates to ChatComponentManager for canonical classification.
        /// </summary>
        private static bool IsNPC(EntityAlive entity) => Core.ChatComponentManager.IsChatTarget(entity);

        /// <summary>
        /// Check if entity is a trader (in-game trader, not a hired NPC).
        /// Traders cannot be hired, don't move, and only buy/sell and offer quests.
        /// Accessed via E key to open trade dialog.
        /// </summary>
        private static bool IsTrader(EntityAlive entity)
        {
            if (entity == null) return false;
            string name = entity.GetType().Name;
            return name.Contains("Trader");
        }

        /// <summary>
        /// Check if entity is an animal type (hired pet or wild).
        /// Hired animals accept movement/order commands but don't participate in LLM chat.
        /// </summary>
        private static bool IsAnimalEntity(EntityAlive entity) => Core.ChatComponentManager.IsAnimal(entity);

        private LLMConfig LoadConfig()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");

            if (!File.Exists(configPath))
            {
                Log.Warning($"Config file not found at {configPath}, using defaults");
                return GetDefaultConfig();
            }

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);

                var config = new LLMConfig();

                // Server settings
                var serverNode = doc.SelectSingleNode("//LLM");
                if (serverNode != null)
                {
                    config.Endpoint = GetNodeValue(serverNode, "Endpoint", LLMService.DefaultChatEndpoint);
                    config.Model = GetNodeValue(serverNode, "Model", "Qwen2.5-3B-Instruct-Q4_K_L");
                    config.ModelFilename = GetNodeValue(serverNode, "ModelFilename", "").Trim();
                    config.TimeoutSeconds = ParseInt(serverNode, "TimeoutSeconds", 15);
                    config.MaxTokens = ParseInt(serverNode, "MaxTokens", 200);
                    config.ContextSize = ParseInt(serverNode, "ContextSize", 8192);
                    config.Temperature = ParseFloat(serverNode, "Temperature", 0.8f);
                }

                // Personality settings
                if (serverNode != null)
                {
                    config.SystemPrompt = GetNodeValue(serverNode, "SystemPrompt",
                        "You are a survivor in a post-apocalyptic zombie wasteland. Keep responses brief and in-character.");
                    config.ContextMemory = ParseInt(serverNode, "ContextMemory", 10);
                }

                // Response settings
                if (serverNode != null)
                {
                    config.ShowTypingIndicator = ParseBool(serverNode, "ShowTypingIndicator", false);
                    config.TypingDelayMs = ParseInt(serverNode, "TypingDelayMs", 0);
                    config.MaxResponseLength = ParseInt(serverNode, "MaxResponseLength", 300);
                    config.ChatTimeout = ParseFloat(serverNode, "ChatTimeout", 30f);
                    config.WildCombatChatCooldownSeconds = ParseFloat(serverNode, "WildCombatChatCooldownSeconds", 5f);
                }

                Log.Debug(() => $"Configuration loaded - Model: {config.Model}");
                return config;
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading config: {ex.Message}");
                return GetDefaultConfig();
            }
        }

        private string GetNodeValue(XmlNode parent, string childName, string defaultValue)
        {
            var child = parent.SelectSingleNode(childName);
            return child?.InnerText ?? defaultValue;
        }

        private int ParseInt(XmlNode parent, string childName, int defaultValue)
        {
            string raw = GetNodeValue(parent, childName, null);
            if (raw == null) return defaultValue;
            if (!int.TryParse(raw, out int result))
            {
                Log.Warning($"[Config] <{childName}> has invalid value '{raw}', using default {defaultValue}");
                return defaultValue;
            }
            return result;
        }

        private float ParseFloat(XmlNode parent, string childName, float defaultValue)
        {
            string raw = GetNodeValue(parent, childName, null);
            if (raw == null) return defaultValue;
            if (!float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float result))
            {
                Log.Warning($"[Config] <{childName}> has invalid value '{raw}', using default {defaultValue}");
                return defaultValue;
            }
            return result;
        }

        private bool ParseBool(XmlNode parent, string childName, bool defaultValue)
        {
            string raw = GetNodeValue(parent, childName, null);
            if (raw == null) return defaultValue;
            if (!bool.TryParse(raw, out bool result))
            {
                Log.Warning($"[Config] <{childName}> has invalid value '{raw}', using default {defaultValue}");
                return defaultValue;
            }
            return result;
        }

        private LLMConfig GetDefaultConfig()
        {
            return new LLMConfig
            {
                // Server
                Endpoint = LLMService.DefaultChatEndpoint,
                Model = "Qwen2.5-3B-Instruct-Q4_K_L",
                TimeoutSeconds = 15,
                MaxTokens = 512,
                Temperature = 0.8f,

                // Personality
                SystemPrompt = "You are a survivor in a post-apocalyptic zombie wasteland. You speak naturally, showing weariness but also hope. Keep responses brief (1-3 sentences) and in-character. Never break character or mention being an AI.",
                ContextMemory = 10,

                // Response
                ShowTypingIndicator = false,
                TypingDelayMs = 0,
                MaxResponseLength = 300
            };
        }

        /// <summary>
        /// Load the RAG extraction system prompt from modconfig.xml.
        /// Returns null if file or node is missing (LLMService uses hardcoded fallback).
        /// </summary>
        private string LoadRagExtractionPrompt()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");

            if (!File.Exists(configPath))
            {
                Log.Debug(() => $"Config not found at {configPath}, using hardcoded fallback prompt");
                return null;
            }

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);

                var extractionNode = doc.SelectSingleNode("//RAG/ExtractionPrompt");
                if (extractionNode != null && !string.IsNullOrWhiteSpace(extractionNode.InnerText))
                {
                    Log.Debug(() => $"RAG extraction prompt loaded from modconfig.xml ({extractionNode.InnerText.Length} chars)");
                    return extractionNode.InnerText;
                }

                Log.Warning($"ExtractionPrompt node missing or empty in modconfig.xml, using hardcoded fallback");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading RAG config: {ex.Message}. Using hardcoded fallback.");
                return null;
            }
        }

        /// <summary>
        /// Load the RAG extraction user message prefix from modconfig.xml.
        /// Returns null if file or node is missing (LLMService uses hardcoded fallback).
        /// </summary>
        private string LoadRagExtractionUserPrefix()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");

            if (!File.Exists(configPath))
                return null;

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);

                var prefixNode = doc.SelectSingleNode("//RAG/ExtractionUserPrefix");
                if (prefixNode != null && !string.IsNullOrWhiteSpace(prefixNode.InnerText))
                {
                    // Convert XML escape sequences to actual characters
                    string prefix = prefixNode.InnerText.Replace("\\n", "\n").Replace("\\t", "\t");
                    Log.Debug(() => $"RAG extraction user prefix loaded from modconfig.xml ({prefix.Length} chars)");
                    return prefix;
                }

                Log.Debug(() => "ExtractionUserPrefix node missing or empty in modconfig.xml, using hardcoded fallback");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading RAG user prefix: {ex.Message}. Using hardcoded fallback.");
                return null;
            }
        }

        /// <summary>
        /// Load the extraction temperature from modconfig.xml. Returns -1 if missing (LLMService uses default 0.0).
        /// </summary>
        private float LoadRagExtractionTemperature()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");
            if (!File.Exists(configPath))
                return -1f;

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);

                var node = doc.SelectSingleNode("//RAG/ExtractionTemperature");
                if (node != null && float.TryParse(node.InnerText, out float temp))
                {
                    temp = Mathf.Clamp(temp, 0f, 0.8f);
                    Log.Debug(() => $"RAG extraction temperature loaded from modconfig.xml: {temp}");
                    return temp;
                }

                Log.Debug(() => "ExtractionTemperature node missing or invalid in modconfig.xml, using default (0.0)");
                return -1f;
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading extraction temperature: {ex.Message}. Using default.");
                return -1f;
            }
        }

        /// <summary>
        /// Load the max extracted facts limit from modconfig.xml. Returns 0 if missing (LLMService uses default 10).
        /// </summary>
        private int LoadRagMaxExtractedFacts()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");
            if (!File.Exists(configPath))
                return 0;

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);

                var node = doc.SelectSingleNode("//RAG/MaxExtractedFacts");
                if (node != null && int.TryParse(node.InnerText, out int max))
                {
                    max = Mathf.Clamp(max, 1, 50);
                    Log.Debug(() => $"RAG max extracted facts loaded from modconfig.xml: {max}");
                    return max;
                }

                Log.Debug(() => "MaxExtractedFacts node missing or invalid in modconfig.xml, using default (10)");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading max extracted facts: {ex.Message}. Using default.");
                return 0;
            }
        }

        /// <summary>
        /// Load the embedding endpoint URL from modconfig.xml RAG section.
        /// Returns null if missing (LLMService keeps its built-in default).
        /// </summary>
        private string LoadEmbeddingEndpoint()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");
            if (!File.Exists(configPath))
                return null;

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);
                var node = doc.SelectSingleNode("//RAG/EmbeddingEndpoint");
                if (node != null && !string.IsNullOrEmpty(node.InnerText.Trim()))
                {
                    string endpoint = node.InnerText.Trim();
                    Log.Debug(() => $"Embedding endpoint loaded from modconfig.xml: {endpoint}");
                    return endpoint;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading embedding endpoint: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Load MinFlushMessages from modconfig.xml RAG section (default 4).
        /// </summary>
        private static int LoadRagMinFlushMessages()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");
            if (!File.Exists(configPath))
                return 4;

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);
                var node = doc.SelectSingleNode("//RAG/MinFlushMessages");
                if (node != null && !string.IsNullOrEmpty(node.InnerText.Trim()))
                {
                    if (int.TryParse(node.InnerText.Trim(), out int val))
                    {
                        Log.Debug(() => $"RAG MinFlushMessages loaded from modconfig.xml: {val}");
                        return val;
                    }
                    Log.Warning($"[Config] <MinFlushMessages> has invalid value, using default 4");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading MinFlushMessages: {ex.Message}. Using default (4).");
            }
            return 4;
        }

        /// <summary>
        /// Load ExtractionIdleSeconds from modconfig.xml RAG section (default 100).
        /// </summary>
        private static float LoadRagExtractionIdleSeconds()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");
            if (!File.Exists(configPath))
                return 100f;

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);
                var node = doc.SelectSingleNode("//RAG/ExtractionIdleSeconds");
                if (node != null && !string.IsNullOrEmpty(node.InnerText.Trim()))
                {
                    if (float.TryParse(node.InnerText.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float val))
                    {
                        Log.Debug(() => $"RAG ExtractionIdleSeconds loaded from modconfig.xml: {val}");
                        return val;
                    }
                    Log.Warning($"[Config] <ExtractionIdleSeconds> has invalid value, using default 100");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading ExtractionIdleSeconds: {ex.Message}. Using default (100).");
            }
            return 100f;
        }

        /// <summary>
        /// Load ambient NPC-to-NPC chatter config from modconfig.xml NPCToNPC section.
        /// </summary>
        private void LoadNPCToNPCConfig()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");
            if (!File.Exists(configPath))
            {
                Log.Warning($"[NPCToNPC] Config file not found at {configPath}, using defaults");
                return;
            }

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);
                var npcNode = doc.SelectSingleNode("//NPCToNPC");
                if (npcNode == null)
                {
                    Log.Debug(() => "[NPCToNPC] No <NPCToNPC> section found in modconfig.xml, using defaults");
                    return;
                }

                bool enabled = ParseBool(npcNode, "Enabled", true);
                float range = ParseFloat(npcNode, "Range", 5f);
                float scanInterval = ParseFloat(npcNode, "ScanIntervalSeconds", 30f);
                float chance = ParseFloat(npcNode, "Chance", 0.25f);
                int maxLines = ParseInt(npcNode, "MaxLinesPerNPC", 2);
                float globalCd = ParseFloat(npcNode, "GlobalCooldownMinutes", 5f);
                float pairCd = ParseFloat(npcNode, "PairCooldownMinutes", 15f);

                NPCToNPCChatManager.Instance?.SetConfig(enabled, range, scanInterval, chance, maxLines, globalCd, pairCd);
                Log.Debug(() => $"[NPCToNPC] Config loaded: enabled={enabled}, range={range}m, interval={scanInterval}s, chance={chance:F2}");
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading NPCToNPC config: {ex.Message}. Using defaults.");
            }
        }

        private TTSConfig LoadTTSConfigInternal()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");

            if (!File.Exists(configPath))
            {
                Log.Warning($"Config file not found at {configPath}, using defaults");
                return GetDefaultTTSConfig();
            }

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);

                var config = new TTSConfig();

                // TTS settings
                var ttsNode = doc.SelectSingleNode("//TTS");
                if (ttsNode != null)
                {
                    config.Enabled = ParseBool(ttsNode, "Enabled", true);
                    config.TtsEngine = GetNodeValue(ttsNode, "TtsEngine", "sherpa").ToLower();
                    string explicitServerUrl = GetNodeValue(ttsNode, "ServerUrl", "");

                    // Derive ServerUrl from TtsEngine.
                    // Defaults: sherpa → 5053, supertonic → 5054.
                    // Both known localhost defaults are treated as "not a real override" —
                    // only a genuinely custom (remote) URL is honored.
                    string engineDefault = config.TtsEngine == "supertonic"
                        ? "http://127.0.0.1:5054"
                        : "http://127.0.0.1:5053";
                    bool isKnownLocalDefault = explicitServerUrl == "http://127.0.0.1:5053" ||
                                               explicitServerUrl == "http://127.0.0.1:5054";
                    config.ServerUrl =
                        (string.IsNullOrEmpty(explicitServerUrl) || isKnownLocalDefault)
                            ? engineDefault
                            : explicitServerUrl;

                    config.TimeoutSeconds = ParseInt(ttsNode, "TimeoutSeconds", 30);
                    config.UseGpu = ParseBool(ttsNode, "UseGpu", false);
                    config.Volume = ParseFloat(ttsNode, "Volume", 0.8f);
                    config.MaxDistance = ParseFloat(ttsNode, "MaxDistance", 20f);
                    config.MinDistance = ParseFloat(ttsNode, "MinDistance", 2f);
                    config.SpeechRate = ParseFloat(ttsNode, "SpeechRate", 1.0f);
                    config.DefaultVoice = GetNodeValue(ttsNode, "DefaultVoice", "af_aoede");
                    config.TraderVoice = GetNodeValue(ttsNode, "TraderVoice", "am_adam");
                    config.CompanionVoice = GetNodeValue(ttsNode, "CompanionVoice", "af_sarah");
                    config.BanditVoice = GetNodeValue(ttsNode, "BanditVoice", "am_eric");
                    config.FaceLipSyncEnabled = ParseBool(ttsNode, "FaceLipSyncEnabled", true);
                    config.FaceLipSyncGain = ParseFloat(ttsNode, "FaceLipSyncGain", 100f);
                    config.FaceLipSyncAttack = ParseFloat(ttsNode, "FaceLipSyncAttack", 35f);
                    config.FaceLipSyncRelease = ParseFloat(ttsNode, "FaceLipSyncRelease", 12f);
                    config.FaceLipSyncNoiseGate = ParseFloat(ttsNode, "FaceLipSyncNoiseGate", 0.004f);
                    config.FaceLipSyncMaxWeight = ParseFloat(ttsNode, "FaceLipSyncMaxWeight", 100f);
                    config.FaceLipSyncBlinkEnabled = ParseBool(ttsNode, "FaceLipSyncBlinkEnabled", true);
                    config.FaceLipSyncBlinkIntervalMin = ParseFloat(ttsNode, "FaceLipSyncBlinkIntervalMin", 2.0f);
                    config.FaceLipSyncBlinkIntervalMax = ParseFloat(ttsNode, "FaceLipSyncBlinkIntervalMax", 6.0f);
                    config.FaceLipSyncBlinkDurationMs = ParseInt(ttsNode, "FaceLipSyncBlinkDurationMs", 120);
                    config.FaceLipSyncMode = GetNodeValue(ttsNode, "FaceLipSyncMode", "auto").ToLower();
                    config.FaceLipSyncAnimParam = GetNodeValue(ttsNode, "FaceLipSyncAnimParam", "IsTalking");

                    // Procedural jaw (tier 3) tuning
                    config.FaceLipSyncProcOpenAngle = ParseFloat(ttsNode, "FaceLipSyncProcOpenAngle", 6f);
                    config.FaceLipSyncProcLowerMaxFrac = ParseFloat(ttsNode, "FaceLipSyncProcLowerMaxFrac", 0.4f);
                    config.FaceLipSyncProcForwardMinFrac = ParseFloat(ttsNode, "FaceLipSyncProcForwardMinFrac", 0.61f);
                    config.FaceLipSyncProcHingeYFrac = ParseFloat(ttsNode, "FaceLipSyncProcHingeYFrac", 0.21f);
                    config.FaceLipSyncProcHingeZFrac = ParseFloat(ttsNode, "FaceLipSyncProcHingeZFrac", 0.36f);
                    config.FaceLipSyncProcTestHold = ParseBool(ttsNode, "FaceLipSyncProcTestHold", false);

                    // Procedural blink/wink (tier 3 fallback) tuning
                    config.FaceLipSyncProcBlinkEyeYFrac = ParseFloat(ttsNode, "FaceLipSyncProcBlinkEyeYFrac", 0.31f);
                    config.FaceLipSyncProcBlinkBandHeightFrac = ParseFloat(ttsNode, "FaceLipSyncProcBlinkBandHeightFrac", 0.05f);
                    config.FaceLipSyncProcBlinkBandWidthFrac = ParseFloat(ttsNode, "FaceLipSyncProcBlinkBandWidthFrac", 0.1f);
                    config.FaceLipSyncProcBlinkCloseAmount = ParseFloat(ttsNode, "FaceLipSyncProcBlinkCloseAmount", 0.95f);
                    config.FaceLipSyncProcBlinkForwardMinFrac = ParseFloat(ttsNode, "FaceLipSyncProcBlinkForwardMinFrac", 0.7f);
                    config.FaceLipSyncProcBlinkWinkMode = GetNodeValue(ttsNode, "FaceLipSyncProcBlinkWinkMode", "off");
                    config.FaceLipSyncProcBlinkWinkChance = ParseFloat(ttsNode, "FaceLipSyncProcBlinkWinkChance", 0.2f);
                }

                Log.Debug(() => $"TTS configuration loaded - Enabled: {config.Enabled}, Engine: {config.TtsEngine}, Server: {config.ServerUrl}");
                return config;
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading TTS config: {ex.Message}");
                return GetDefaultTTSConfig();
            }
        }

        /// <summary>
        /// Re-parse TTS config from modconfig.xml and update the live static. Used by vc reloadface.
        /// </summary>
        public static void ReloadTTSConfig()
        {
            if (_instance != null)
                _ttsConfig = _instance.LoadTTSConfigInternal();
        }

        /// <summary>
        /// Re-parse personalities.xml so FaceOverride data is fresh. Used by vc reloadface.
        /// </summary>
        public static void ReloadPersonalities()
        {
            if (_instance != null)
                PersonalityManager.Instance.LoadPersonalities(_modPath);
        }

        private TTSConfig GetDefaultTTSConfig()
        {
            return new TTSConfig
            {
                Enabled = true,
                ServerUrl = "http://127.0.0.1:5053",
                TimeoutSeconds = 30,
                UseGpu = false,
                Volume = 0.8f,
                MaxDistance = 20f,
                MinDistance = 2f,
                SpeechRate = 1.0f,
                DefaultVoice = "af_aoede",
                TraderVoice = "am_adam",
                CompanionVoice = "af_sarah",
                BanditVoice = "am_eric"
            };
        }

        private STTConfig LoadSTTConfig()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");

            if (!File.Exists(configPath))
            {
                Log.Warning($"Config file not found at {configPath}, using defaults");
                return GetDefaultSTTConfig();
            }

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);

                var config = new STTConfig();

                // STT settings
                var sttNode = doc.SelectSingleNode("//STT");
                if (sttNode != null)
                {
                    config.Enabled = ParseBool(sttNode, "Enabled", true);
                    config.ServerUrl = GetNodeValue(sttNode, "ServerUrl", "http://127.0.0.1:5052");
                    config.TimeoutSeconds = ParseInt(sttNode, "TimeoutSeconds", 60);
                    config.BeamSize = ParseInt(sttNode, "BeamSize", 3);
                    config.LanguageLocked = ParseBool(sttNode, "LanguageLocked", false);
                    config.Language = GetNodeValue(sttNode, "Language", "en").Trim().ToLower();
                    config.Model = GetNodeValue(sttNode, "Model", "").Trim();
                    config.UseGpu = ParseBool(sttNode, "UseGpu", false);
                    config.Translate = ParseBool(sttNode, "Translate", false);
                    config.Prompt = GetNodeValue(sttNode, "Prompt", "Open inventory, use knife, swap weapon, reload, status, drop item");
                    config.SampleRate = ParseInt(sttNode, "SampleRate", 16000);
                    config.MaxRecordingSeconds = ParseInt(sttNode, "MaxRecordingSeconds", 15);
                    config.PushToTalkKey = GetNodeValue(sttNode, "PushToTalkKey", "V");
                    config.MicrophoneDevice = GetNodeValue(sttNode, "MicrophoneDevice", "").Trim();
                    config.WakeWordEnabled = ParseBool(sttNode, "WakeWordEnabled", false);
                    config.WakeWordModel = GetNodeValue(sttNode, "WakeWordModel", "hey_marvin_v0.1");
                    config.WakeWordThreshold = ParseFloat(sttNode, "WakeWordThreshold", 0.5f);
                    config.VadSilenceMs = ParseInt(sttNode, "VadSilenceMs", 800);
                    config.WakeWordThreadPriority = GetNodeValue(sttNode, "WakeWordThreadPriority", "Normal");

                config.WakeWordMaxChunksPerSignal = ParseInt(sttNode, "WakeWordMaxChunksPerSignal", 4);
                    config.WhisperGpuDevice = GetNodeValue(sttNode, "WhisperGpuDevice", "").Trim();
                }

                // Apply in-game Speech Recognition toggle preference (Accurate=small, Fast=base).
                // If modconfig.xml has an explicit <Model>, that takes priority.
                string speechModelPref = UnityEngine.PlayerPrefs.GetString("XNPCVoiceControl_SpeechModel", "");
                if (!string.IsNullOrEmpty(speechModelPref) && string.IsNullOrEmpty(config.Model))
                {
                    config.Model = speechModelPref == "Fast" ? "ggml-base.en.bin" : "ggml-small.bin";
                }

                // Apply in-game beam size preference (persists across restarts via PlayerPrefs).
                // modconfig.xml <BeamSize> takes priority if explicitly set.
                float savedBeam = UnityEngine.PlayerPrefs.GetFloat("XNPCVoiceControl_BeamSize", 0f);
                if (savedBeam > 0f)
                {
                    config.BeamSize = Mathf.RoundToInt(savedBeam);
                }

                Log.Debug(() => $"STT configuration loaded - Server: {config.ServerUrl}, Enabled: {config.Enabled}, WakeWord: {config.WakeWordEnabled}, BeamSize: {config.BeamSize}, Language: {config.Language}, Gpu: {config.UseGpu}, Model: {(string.IsNullOrEmpty(config.Model) ? "autodetect" : config.Model)}, WWThreadPriority: {config.WakeWordThreadPriority}, MaxChunksPerSignal: {config.WakeWordMaxChunksPerSignal}");
                return config;
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading STT config: {ex.Message}");
                return GetDefaultSTTConfig();
            }
        }

        /// <summary>
        /// Resolve the directory containing wake word ONNX models.
        /// Checks mod path first, then assembly location as fallback.
        /// </summary>
        private static string ResolveWakeWordModelsDir()
        {
            // Try mod path
            string modModelsDir = Path.Combine(_modPath, "Resources", "WakeWord", "models");
            if (Directory.Exists(modModelsDir) && File.Exists(Path.Combine(modModelsDir, "melspectrogram.onnx")))
                return modModelsDir;

            // Fallback: assembly location
            string asmPath = typeof(XNPCVoiceControlMod).Assembly.Location;
            if (!string.IsNullOrEmpty(asmPath))
            {
                string asmModelsDir = Path.Combine(Path.GetDirectoryName(asmPath), "Resources", "WakeWord", "models");
                if (Directory.Exists(asmModelsDir) && File.Exists(Path.Combine(asmModelsDir, "melspectrogram.onnx")))
                    return asmModelsDir;
            }

            Log.Warning($"Wake word models not found in Resources/WakeWord/models/");
            return null;
        }

        private STTConfig GetDefaultSTTConfig()
        {
            return new STTConfig
            {
                Enabled = true,
                ServerUrl = "http://127.0.0.1:5052",
                TimeoutSeconds = 60,
                SampleRate = 16000,
                MaxRecordingSeconds = 15,
                PushToTalkKey = "V",
                WakeWordEnabled = false,
                BeamSize = 3,
                LanguageLocked = false,
                Language = "en",
                Prompt = "Open inventory, use knife, swap weapon, reload, status, drop item"
            };
        }

        /// <summary>
        /// Load head gesture configuration from modconfig.xml <TTS> section.
        /// Follows the same XmlDocument pattern as LoadTTSConfigInternal.
        /// </summary>
        private HeadGestureConfig LoadHeadGestureConfig()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");

            if (!File.Exists(configPath))
                return new HeadGestureConfig();

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);

                var config = new HeadGestureConfig();
                var ttsNode = doc.SelectSingleNode("//TTS");
                if (ttsNode != null)
                {
                    config.Enabled      = ParseBool(ttsNode,  "HeadGesturesEnabled",   true);
                    config.GestureParam = GetNodeValue(ttsNode, "HeadGestureParam",    "IdleVar").Trim();
                    config.NodValue     = ParseInt(ttsNode,   "HeadGestureNodValue",    1);
                    config.ShakeValue   = ParseInt(ttsNode,   "HeadGestureShakeValue",  2);
                    config.NeutralValue = ParseInt(ttsNode,   "HeadGestureNeutralValue",0);
                    float hold          = ParseFloat(ttsNode, "HeadGestureHoldSeconds", 1.5f);
                    config.HoldSeconds  = Mathf.Clamp(hold, 0.1f, 5.0f);
                }

                Log.Debug(() => $"Head gesture configuration loaded - Enabled: {config.Enabled}, Param: {config.GestureParam}, Hold: {config.HoldSeconds}s");
                return config;
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading head gesture config: {ex.Message}");
                return new HeadGestureConfig();
            }
        }

        private Core.BillingConfig LoadBillingConfig()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");

            if (!File.Exists(configPath))
                return new Core.BillingConfig();

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);

                var config = new Core.BillingConfig();
                var billingNode = doc.SelectSingleNode("//Billing");
                if (billingNode != null)
                {
                    config.GrowthRate = ParseFloat(billingNode, "GrowthRate", 1.2f);
                    config.MaxWeeks = ParseInt(billingNode, "MaxWeeks", 12);
                    config.GraceDays = ParseInt(billingNode, "GraceDays", 2);
                    config.ApprovalTimeoutDays = ParseInt(billingNode, "ApprovalTimeoutDays", 3);
                }

                Log.Debug(() => $"Billing configuration loaded - GrowthRate: {config.GrowthRate}, MaxWeeks: {config.MaxWeeks}, GraceDays: {config.GraceDays}");
                return config;
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading billing config: {ex.Message}");
                return new Core.BillingConfig();
            }
        }

        private Core.FollowAssistConfig LoadFollowAssistConfig()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");

            if (!File.Exists(configPath))
                return new Core.FollowAssistConfig();

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);

                var config = new Core.FollowAssistConfig();
                var node = doc.SelectSingleNode("//FollowAssist");
                if (node != null)
                {
                    config.Enabled = ParseBool(node, "Enabled", true);
                    config.NoProgressSeconds = ParseFloat(node, "NoProgressSeconds", 9f);
                    config.MinSeparation = ParseFloat(node, "MinSeparation", 3.0f);
                    config.ProgressEpsilon = ParseFloat(node, "ProgressEpsilon", 0.5f);
                    config.CooldownSeconds = ParseFloat(node, "CooldownSeconds", 15f);
                }

                Log.Debug(() => $"FollowAssist configuration loaded - Enabled: {config.Enabled}, NoProgressSeconds: {config.NoProgressSeconds}");
                return config;
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading follow-assist config: {ex.Message}");
                return new Core.FollowAssistConfig();
            }
        }

        private Core.FormationConfig LoadFormationConfig()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");

            if (!File.Exists(configPath))
                return new Core.FormationConfig();

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);

                var config = new Core.FormationConfig();
                var node = doc.SelectSingleNode("//FormationDistances");
                if (node != null)
                {
                    // Parse child <Tier index="N" distance="M" /> elements.
                    // Tolerant of extra tiers — just add a <Tier> line, no code change.
                    var tiers = node.SelectNodes("Tier");
                    if (tiers != null)
                    {
                        foreach (XmlNode tier in tiers)
                        {
                            int idx = 0;
                            float dist = 0f;

                            string idxStr = tier.Attributes?["index"]?.Value;
                            if (idxStr == null || !int.TryParse(idxStr, out idx) || idx <= 0)
                            {
                                Log.Warning($"[Config] <Tier> missing or invalid index, skipping: {tier.OuterXml}");
                                continue;
                            }

                            string distStr = tier.Attributes?["distance"]?.Value;
                            if (distStr == null || !float.TryParse(distStr,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out dist))
                            {
                                Log.Warning($"[Config] <Tier index=\"{idx}\"> missing or invalid distance, skipping");
                                continue;
                            }

                            config.Distances[idx] = dist;
                        }
                    }

                    Log.Debug(() => $"Formation distances loaded: {string.Join(", ", config.Distances)}");
                }

                return config;
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading formation config: {ex.Message}");
                return new Core.FormationConfig();
            }
        }

        /// <summary>Load FollowMinSeparation from modconfig.xml (default 1.0m).</summary>
        private static float LoadFollowMinSeparation()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");
            if (!File.Exists(configPath))
                return 1.0f;

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);
                var node = doc.SelectSingleNode("//FollowMinSeparation");
                if (node != null && !string.IsNullOrEmpty(node.InnerText.Trim()))
                {
                    if (float.TryParse(node.InnerText.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float val))
                    {
                        Log.Debug(() => $"FollowMinSeparation loaded from modconfig.xml: {val}");
                        return val;
                    }
                    Log.Warning($"[Config] <FollowMinSeparation> has invalid value, using default 1.0");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading FollowMinSeparation: {ex.Message}. Using default (1.0).");
            }
            return 1.0f;
        }

        /// <summary>Load DefaultTacticalMode from modconfig.xml (default false).</summary>
        private static bool LoadDefaultTacticalMode()
        {
            string configPath = Path.Combine(_modPath, "Config", "modconfig.xml");
            if (!File.Exists(configPath))
                return false;

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);
                var node = doc.SelectSingleNode("//DefaultTacticalMode");
                if (node != null && !string.IsNullOrEmpty(node.InnerText.Trim()))
                {
                    bool val = bool.Parse(node.InnerText.Trim());
                    Log.Debug(() => $"DefaultTacticalMode loaded from modconfig.xml: {val}");
                    return val;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading DefaultTacticalMode: {ex.Message}. Using default (false).");
            }
            return false;
        }
    }
}
