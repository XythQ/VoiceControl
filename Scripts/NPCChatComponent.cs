using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using XNPCVoiceControl.Actions;
using XNPCVoiceControl.Core;
using XNPCVoiceControl.STT;
using XNPCVoiceControl.TTS;
using XNPCVoiceControl.UI;
using XNPCVoiceControl.Net;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Attach this component to NPCCore NPCs to enable LLM-powered conversations.
    /// Manages conversation state, history, and personality for each NPC.
    /// </summary>
    public partial class NPCChatComponent : MonoBehaviour
    {
        // Reference to the NPC entity
        private EntityAlive _npcEntity;
        private int _entityId;

        // Conversation state
        private List<ChatMessage> _conversationHistory = new List<ChatMessage>();
        private int _maxHistoryLength = 5;
        private bool _isWaitingForResponse = false;
        private string _currentResponse = "";
        private bool _isTyping = false;
        private int _pipelineId = 0; // incremented each new voice pipeline; stale handlers check this

        // MP Phase 1b — chat-state sync keepalive
        private float _lastChatStateKeepaliveTime;  // Time.realtimeSinceStartup of last keepalive send
        private bool _lastNotifiedActive;           // tracks Notify state to detect transitions

        // NPC Personality
        private string _npcName = "Survivor";
        private string _systemPrompt;
        private PersonalityDefinition _personality;
        private string _clipFolderKey;  // resolved once at init: VoiceClipFolder ?? entityClassName
        private bool   _usesClips;      // true if clip folder exists and personality allows clips
        private bool   _llmDisabled;    // true for Clips-mode NPCs; no LLM for open chat

        // Configuration
        private LLMConfig _config;

        // TTS Audio Player
        private NPCAudioPlayer _audioPlayer;
        private bool _ttsEnabled = true;

        // Face Lip-Sync (TIER 0 blendshape driver)
        private NPCFaceLipSync _faceLipSync;
        private NPCHeadGesture _headGesture;

        // Events for UI integration
        public event Action<string> OnResponseStarted;
        public event Action<string> OnResponseComplete;
        public event Action<string> OnTypingUpdate;
        public event Action<string> OnError;
        public event Action<string> OnSpeechStarted;
        public event Action OnSpeechComplete;

        // Action system integration
        private bool _actionsEnabled = true;
        private EntityPlayer _lastInteractingPlayer;

        // Leader tracking - only the leader (hiring player) can issue orders
        private int _leaderEntityId = -1;
        private bool _hasLoggedLeaderRead; // Only log leader read on first init
        private bool _hireDayStamped;       // true once hireDay is recorded; prevents re-lock
        private float _lastCombatTargetTime = -999f; // Time.realtimeSinceStartup when NPC last had a live combat target

        // Follow-assist watchdog (stuck-follow catch-up teleport)
        private float _followAssistBestDist = float.MaxValue;
        private float _followAssistWindowStart = -1f;   // Time.realtimeSinceStartup, <0 = not armed
        private float _lastFollowAssistTime = -999f;    // Time.realtimeSinceStartup of last assist

        // Patrol recording state — mod-owned, not reliant on SCore's CurrentOrder cvar.
        private bool _recordingPatrol = false;
        private bool _activelyPatrolling = false;
        private Vector3i _lastRecordedBlock = new Vector3i(int.MinValue, int.MinValue, int.MinValue); // sentinel for first block

        // Player context for RAG memory (composite key: persistentPlayerId + npcName)
        private string _lastPlayerPersistentId = "";
        private string _lastPlayerDisplayName = "";

        // Shared across ALL NPCChatComponent instances — once ANY NPC resolves the player's
        // PersistentId, all NPCs can use it. Fixes "player ID not yet resolved" flush block
        // for NPCs the player never spoke to directly.
        private static string s_lastResolvedPlayerPersistentId = "";
        private static bool s_playerIdLogged = false;

        // Deterministic player-name capture — strong patterns only, no "I'm X" (false positives).
        // Matches: "my name is Dave", "my name's Dave", "call me Dave", "name's Dave"
        private static readonly Regex s_playerNameRegex = new Regex(
            @"\b(?<!\b(?:your|his|her|their|the)\s)(?:my\s+)?name(?:'s|\s+is)\s+([A-Za-z][A-Za-z'\-]{1,19})|\bcall\s+me\s+([A-Za-z][A-Za-z'\-]{1,19})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Words that look like names but aren't — filtered after regex match.
        private static readonly HashSet<string> s_nameStoplist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sorry", "not", "going", "here", "tired", "fine", "ready", "lost", "hungry",
            "thirsty", "afraid", "scared", "alone", "home", "back", "there", "where"
        };

        // Routine phrase-trigger commands excluded from RAG memory buffer.
        // These are deterministic, generic, content-free at scale — 30+ repetitions of follow/stay/guard
        // overwhelm the 3B extraction model's negative instructions, producing cycling fake facts.
        // Match against TriggerActionType.ToString().ToLowerInvariant() values.
        private static readonly HashSet<string> s_routineTriggersExcludedFromMemory =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "followplayer", "stopfollow", "guardhere", "guardreturn", "wander", "dismiss", "jumpup", "jumpdown" };

        // Tactical mode clarifications — spoken when a command miss occurs in tactical mode.
        private static readonly string[] s_tacticalClarifications =
            { "Say again?", "Didn't catch that.", "Repeat the order.", "Come again?" };

        // RAG memory: shadow buffer for consolidation
        private List<ChatMessage> _unsummarizedBuffer = new List<ChatMessage>();
        private float _timeSinceLastMessage = 0f;
        private bool _flushInProgress = false;
        private CancellationTokenSource _extractionCts;
        private bool _extractionInProgress; // true while ConsolidateBufferAsync tasks are running
        private string _lastPlayerMessageForMemory; // Captured for shadow buffer pairing

        // Pipeline timing for STT→TTS latency measurement
        private long _pipelineStartMs = 0;

        // Proactive greeting cooldown - UAI fires every cycle so guard here
        private float _lastProactiveGreetTime = -999f;
        private const float ProactiveGreetCooldown = 60f;

        // Global gate: prevent multiple NPCs from playing proactive greetings (audio + subtitle)
        // simultaneously. True from when a greeting starts until ITS OWN playback finishes - tied to
        // actual TTS/clip duration via the existing completion callbacks, not a fixed-time guess.
        private static bool s_proactiveGreetingBusy = false;

        // Loaded Chamber - buffered greeting from warmup (drained by UAI or player interaction)
        private string _bufferedGreetingText;

        // Proximity-based proactive greeting (replaces UAI task)
        private float _proactiveCheckTimer = 0f;
        private const float ProactiveCheckInterval = 2f;
        private const float ProactiveGreetRange = 10f;
        private const float GreetResetDistance = 20f;
        private int _lastGreetedPlayerId = -1;
        private bool _playerHasLeftRange = true;

        // Rotation hold: keep NPC facing player during greeting attention window
        private EntityPlayer _rotationTarget = null;
        private float _rotationHoldTimer = 0f;
        // NPC-to-NPC facing — separate from _rotationTarget (EntityPlayer-only) to avoid type mismatch
        // and stomp risk if both player-facing and NPC-facing holds overlap.
        private Entity _npcFaceTarget = null;
        private float _npcFaceHoldTimer = 0f;
        private const float RotationHoldDuration = 4f;

        // Chat inactivity timeout
        private float _chatIdleTimer = 0f;
        // UAI idle timeout: when player is in range but no conversation starts, NPC idles briefly
        // then lets Wander win so it doesn't get stuck forever.
        private float _chatIdleTimeoutStart = 0f;
        private const float ChatIdleTimeoutSeconds = 5f;
        // After idle timeout expires, keep returning false for this long so Wander actually gets
        // time to move the NPC out of range before offering another pause window.
        private float _chatWanderCooldownEnd = 0f;
        private const float ChatWanderCooldownSeconds = 5f;

        // Interruption Matrix - active polling (no Harmony patches)
        private CancellationTokenSource _interruptionTokenSource;
        private Coroutine _interruptionMonitorCoroutine;

        /// <summary>
        /// Play a proactively-generated greeting (from Loaded Chamber warmup or UAI initiation).
        /// Adds to history, shows subtitle (with typewriter if configured), and plays TTS audio.
        /// </summary>
        public void PlayProactiveGreeting(string greeting, bool showSubtitle = true)
        {
            if (_isWaitingForResponse) return;

            // Clips greeting: Greetings pool, then Default pool (clips-only). No TTS server needed.
            if (_usesClips && _audioPlayer != null)
            {
                if (VoiceClipLibrary.Instance.TryGetGreeting(_clipFolderKey, out var gclip) ||
                    (_llmDisabled && VoiceClipLibrary.Instance.TryGetDefault(_clipFolderKey, out gclip)))
                {
                    if (showSubtitle && s_proactiveGreetingBusy)
                    {
                        Log.Debug(() => $"[SUBTITLE] Skipped {_npcName} (clips) — another proactive greeting still playing");
                        return;
                    }

                    string gsub = gclip.ResolveSubtitle();
                    if (showSubtitle)
                    {
                        s_proactiveGreetingBusy = true;
                        Log.Debug(() => $"[SUBTITLE] Showing proactive greeting for {_npcName} (clips)");
                        if (!string.IsNullOrEmpty(gsub))
                            SubtitleManager.Instance.ShowSubtitle(_npcName, gsub, 25f);
                        OnSpeechStarted?.Invoke(gsub ?? string.Empty);
                    }
                    _audioPlayer.PlayClipFile(gclip, () =>
                    {
                        if (showSubtitle)
                        {
                            s_proactiveGreetingBusy = false;
                            SubtitleManager.Instance.ClearSubtitle();
                            OnSpeechComplete?.Invoke();
                        }
                    });
                    return;
                }
                // Clips-only with no greeting/default clip: stay silent (never TTS).
                if (_llmDisabled) return;
                // Mixed: fall through to the TTS greeting below.
            }

            if (ServerManager.ReadyState != ServerManager.ServerReadyState.Ready) return;
            if (string.IsNullOrEmpty(greeting)) return;

            if (showSubtitle && s_proactiveGreetingBusy)
            {
                Log.Debug(() => $"[SUBTITLE] Skipped {_npcName} — another proactive greeting still playing");
                return;
            }

            Log.Debug(() => $"[PROACTIVE] Playing greeting for {_npcName}: {greeting}");

            if (showSubtitle)
            {
                Log.Debug(() => $"[SUBTITLE] Showing proactive greeting for {_npcName}: {greeting?.Substring(0, Math.Min(40, greeting.Length))}");
                if (_config != null && _config.ShowTypingIndicator && _config.TypingDelayMs > 0)
                {
                    SubtitleManager.Instance.ShowSubtitle(_npcName, string.Empty, 25f);
                    StartCoroutine(TypeResponseCoroutine(greeting, null));
                }
                else
                    SubtitleManager.Instance.ShowSubtitle(_npcName, greeting, 25f);
            }

            // Play TTS (async, non-blocking)
            if (_ttsEnabled && _audioPlayer != null && TTSService.Instance.IsInitialized)
            {
                if (showSubtitle)
                {
                    s_proactiveGreetingBusy = true;
                    OnSpeechStarted?.Invoke(greeting);
                }
                _audioPlayer.SpeakStreaming(greeting, () =>
                {
                    if (showSubtitle)
                    {
                        s_proactiveGreetingBusy = false;
                        SubtitleManager.Instance.ClearSubtitle();
                        OnSpeechComplete?.Invoke();
                    }
                });
            }
        }

        /// <summary>
        /// Interrupt the current conversation: stop waiting, flush shadow buffer for extraction.
        /// History is NOT cleared — it stays bounded by TrimHistory() and is cleared in OnDestroy.
        /// This closes the 30s amnesia window where ChatTimeout fired before the 100s idle extraction.
        /// Called on chat inactivity timeout.
        /// </summary>
        public void Interrupt()
        {
            _isWaitingForResponse = false;
            _chatIdleTimer = 0f;
            _interruptionTokenSource?.Cancel();

            // Conversation end is the natural extraction moment — flush immediately.
            // Bypasses MinFlushMessages floor (that only gates the Update() idle path).
            // _extractionInProgress guard inside FlushShadowBuffer prevents overlap.
            FlushShadowBuffer();
        }

        /// <summary>
        /// Store a pre-generated greeting in the buffer (from Loaded Chamber warmup).
        /// Overwrites any existing buffer - only one greeting buffered at a time.
        /// </summary>
        public void StoreBufferedGreeting(string greeting)
        {
            _bufferedGreetingText = greeting;
        }

        /// <summary>
        /// True if this NPC can currently participate in ambient NPC-to-NPC chatter: not hired,
        /// not a trader, not in combat, not mid-conversation. Checked by NPCToNPCChatManager both
        /// before requesting a generation AND after it completes (state can change during the
        /// 300-800ms LLM call), so a pair is never played one-sided.
        /// </summary>
        public bool IsEligibleForAmbientChat()
        {
            if (_isWaitingForResponse) return false;
            if (_npcEntity == null || !_npcEntity.IsAlive()) return false;
            if (Core.ChatComponentManager.HasPlayerLeader(_npcEntity)) return false;
            if (_npcEntity.GetType().Name.Contains("Trader")) return false;
            bool inCombat = _npcEntity.IsAlert || _npcEntity.GetAttackTarget() != null || _npcEntity.GetRevengeTarget() != null;
            if (inCombat) return false;
            // An active player-facing greeting hold takes priority - don't let ambient chat pull
            // this NPC's facing away from a player it's currently acknowledging.
            if (_rotationTarget != null) return false;
            return true;
        }

        /// <summary>
        /// Drain the buffered greeting. Returns null if empty.
        /// </summary>
        public string DrainBufferedGreeting()
        {
            string buffered = _bufferedGreetingText;
            _bufferedGreetingText = null;
            return buffered;
        }

        /// <summary>
        /// UAI-initiated greeting: NPC decides to speak to a nearby player unprompted.
        /// Fast path: drains the Loaded Chamber buffer (pre-generated at 15m) - no LLM call, instant play.
        /// Slow path: buffer empty, fires LLM in background (NPC just loaded, TTL expired, etc.).
        /// Does NOT set _isWaitingForResponse - player can still initiate conversation during generation.
        /// </summary>
        public void TriggerProactiveGreeting(EntityPlayer player)
        {
            if (_isWaitingForResponse) return;
            if (ServerManager.ReadyState != ServerManager.ServerReadyState.Ready) return;
            if (Time.realtimeSinceStartup - _lastProactiveGreetTime < ProactiveGreetCooldown) return;

            if (IsNpcInCombat()) return;

            float chance = _personality?.ProactiveGreetingChance ?? 0f;
            if (chance <= 0f) return;
            if (UnityEngine.Random.value > chance) return;

            // Clips-mode: greet with a clip, no LLM text generation.
            if (_usesClips)
            {
                _rotationTarget = player;
                _rotationHoldTimer = RotationHoldDuration;
                _lastGreetedPlayerId = player.entityId;
                _playerHasLeftRange = false;
                _lastProactiveGreetTime = Time.realtimeSinceStartup;
                PlayProactiveGreeting(null);   // clip branch above handles it; null is safe
                return;
            }

            // Fast path: Loaded Chamber already pre-generated a greeting at 15m - drain it instantly
            if (!string.IsNullOrEmpty(_bufferedGreetingText))
            {
                string buffered = _bufferedGreetingText;
                _bufferedGreetingText = null;
                _rotationTarget = player;
                _rotationHoldTimer = RotationHoldDuration;
                _lastGreetedPlayerId = player.entityId;
                _playerHasLeftRange = false;
                _lastProactiveGreetTime = Time.realtimeSinceStartup;
                PlayProactiveGreeting(buffered);
                return;
            }

            // Slow path: buffer not ready - fire LLM now
            if (LLMService.Instance.IsBusy) return;

            _lastGreetedPlayerId = player.entityId;
            _playerHasLeftRange = false;
            _lastProactiveGreetTime = Time.realtimeSinceStartup;

            string playerId = GetPlayerPersistentId(player);
            NPCSenseSnapshot snapshot = NPCSenseSnapshot.Capture(_npcEntity);

            System.Threading.Tasks.Task.Run(async () =>
            {
                string greeting = await LLMService.Instance.GenerateBufferedGreetingAsync(
                    _npcName, _npcEntity, playerId, _config, _personality, snapshot);

                if (!string.IsNullOrEmpty(greeting))
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        _rotationTarget = player;
                        _rotationHoldTimer = RotationHoldDuration;
                        PlayProactiveGreeting(greeting);
                    });
            });
        }

        public void Initialize(EntityAlive npcEntity, LLMConfig config)
        {
            _npcEntity = npcEntity;
            _entityId = npcEntity.entityId;

            // Vanilla's EntityMoveHelper.UpdateMoveHelper() already auto-opens doors correctly every frame
            // via CheckForDoorAndOpen() - it's just gated behind CanOpenDoors, which defaults false and
            // which SCore never sets for its NPC classes. Enable it once per qualifying NPC instead of
            // reimplementing door-detection ourselves (SCore's own CheckForClosedDoor attempted that and
            // is now stale against the door TileEntity rework - see Ideas/SCORE_WANDER_WALLSTUCK_FIX.md).
            if (npcEntity.moveHelper != null &&
                (EntityUtilities.IsHuman(npcEntity.entityId) || EntityUtilities.IsHired(npcEntity.entityId)))
            {
                npcEntity.moveHelper.CanOpenDoors = true;
                Log.Out($"[DOOR] CanOpenDoors enabled for {npcEntity.EntityName} (class: {npcEntity.EntityClass?.entityClassName})");
            }
            else
            {
                Log.Out($"[DOOR] CanOpenDoors NOT enabled for {npcEntity.EntityName} (class: {npcEntity.EntityClass?.entityClassName}) - IsHuman={EntityUtilities.IsHuman(npcEntity.entityId)}, IsHired={EntityUtilities.IsHired(npcEntity.entityId)}, moveHelper null={npcEntity.moveHelper == null}");
            }
            _config = config;
            _maxHistoryLength = config.ContextMemory;

            // Assign personality from PersonalityManager (reads entity class properties or falls back to type matching)
            _personality = PersonalityManager.Instance.AssignPersonality(npcEntity);

            // Resolve clip folder key: personality VoiceClipFolder takes priority, entity class name as fallback.
            // This lets any NPC auto-discover clips via NPC_Voices/<entityClassName>/ without needing a personality entry.
            string clipFolder = _personality?.VoiceClipFolder;
            if (string.IsNullOrEmpty(clipFolder))
                clipFolder = npcEntity?.EntityClass?.entityClassName;
            _clipFolderKey = clipFolder;
            bool hasClipFolder = !string.IsNullOrEmpty(_clipFolderKey) && VoiceClipLibrary.Instance.HasFolder(_clipFolderKey);
            bool ttsOnlyPersonality = _personality != null && _personality.VoiceMode == VoiceMode.Tts;
            _usesClips    = hasClipFolder && !ttsOnlyPersonality;
            _llmDisabled  = _usesClips && (_personality == null || _personality.LlmDisabled);

            // Extract NPC name: personality name takes priority, then entity name, then fallback
            _npcName = GetNPCName();

            // Seed conversation history with a one-time self-identity turn. This is a real
            // "assistant" role message in the chat-formatted API request, not prose in the system
            // prompt - chat models tend to maintain consistency with established conversation turns
            // more reliably than buried system-prompt lines. Note: this WILL eventually be evicted
            // by TrimHistory() in a long conversation - the late per-turn reinforcement (search
            // "Remember: your own name is") is what carries self-identity for long conversations;
            // this just helps early game.
            _conversationHistory.Add(new ChatMessage("NPC", $"Hi there, I'm {_npcName}."));

            // Build personality-specific system prompt
            _systemPrompt = BuildSystemPrompt();

            // Initialize audio player if TTS is enabled OR if personality uses voice clips.
            // Clips-mode does not require TTS to be initialized - just needs an AudioSource.
            // Traders use base game voice system - never use Kokoro TTS for them.
            bool isTrader = npcEntity.GetType().Name.Contains("Trader");
            var ttsConfig = XNPCVoiceControlMod.TTSConfig;
            bool ttsReady = ttsConfig != null && ttsConfig.Enabled && TTSService.Instance.IsInitialized;
            bool needsAudioPlayer = !isTrader && ttsConfig != null &&
                (ttsReady || _usesClips);
            if (needsAudioPlayer)
            {
                _audioPlayer = gameObject.AddComponent<NPCAudioPlayer>();
                string maleVoice = _personality?.MaleVoice;
                string femaleVoice = _personality?.FemaleVoice;
                string genderTag = GetGenderTag();
                string ttsLanguage = _personality?.TTSLanguage ?? "en";
                _audioPlayer.Initialize(npcEntity, ttsConfig, maleVoice, femaleVoice, genderTag, ttsLanguage);
                _ttsEnabled = ttsReady;
                Log.Debug(() => $"Audio player created for NPC: {_npcName} (tts={_ttsEnabled}, clips={_usesClips}, folder={_clipFolderKey ?? "none"})");

                // Face lip-sync — attach if enabled in config AND not disabled via entity property
                // Skip on dedi — no rendering, emodel/avatarController may be null
                if (!GameManager.IsDedicatedServer && ttsConfig.FaceLipSyncEnabled && IsLipSyncEnabled(npcEntity))
                {
                    _faceLipSync = gameObject.AddComponent<NPCFaceLipSync>();
                    // NPCAudioPlayer.Initialize() created the AudioSource via AddComponent<AudioSource>()
                    var audioSource = _audioPlayer.GetComponent<AudioSource>();
                    if (audioSource != null)
                    {
                        // Merge per-character FaceOverride over global TTSConfig defaults
                        var fo = _personality?.FaceOverride;
                        _faceLipSync.Initialize(
                            _npcEntity,
                            audioSource,
                            ttsConfig.FaceLipSyncGain,
                            ttsConfig.FaceLipSyncAttack,
                            ttsConfig.FaceLipSyncRelease,
                            ttsConfig.FaceLipSyncNoiseGate,
                            ttsConfig.FaceLipSyncMaxWeight,
                            _npcName,
                            ttsConfig.FaceLipSyncBlinkEnabled,
                            ttsConfig.FaceLipSyncBlinkIntervalMin,
                            ttsConfig.FaceLipSyncBlinkIntervalMax,
                            ttsConfig.FaceLipSyncBlinkDurationMs,
                            ttsConfig.FaceLipSyncMode,
                            ttsConfig.FaceLipSyncAnimParam,
                            // Jaw (override or global default)
                            fo?.OpenAngle ?? ttsConfig.FaceLipSyncProcOpenAngle,
                            fo?.LowerMaxFrac ?? ttsConfig.FaceLipSyncProcLowerMaxFrac,
                            fo?.ForwardMinFrac ?? ttsConfig.FaceLipSyncProcForwardMinFrac,
                            fo?.HingeYFrac ?? ttsConfig.FaceLipSyncProcHingeYFrac,
                            fo?.HingeZFrac ?? ttsConfig.FaceLipSyncProcHingeZFrac,
                            fo?.TestHold ?? ttsConfig.FaceLipSyncProcTestHold,
                            // Procedural blink/wink (override or global default)
                            fo?.BlinkEyeYFrac ?? ttsConfig.FaceLipSyncProcBlinkEyeYFrac,
                            fo?.BlinkBandHeightFrac ?? ttsConfig.FaceLipSyncProcBlinkBandHeightFrac,
                            fo?.BlinkBandWidthFrac ?? ttsConfig.FaceLipSyncProcBlinkBandWidthFrac,
                            fo?.BlinkCloseAmount ?? ttsConfig.FaceLipSyncProcBlinkCloseAmount,
                            fo?.BlinkForwardMinFrac ?? ttsConfig.FaceLipSyncProcBlinkForwardMinFrac,
                            !string.IsNullOrEmpty(fo?.BlinkWinkMode) ? fo.BlinkWinkMode : ttsConfig.FaceLipSyncProcBlinkWinkMode,
                            fo?.BlinkWinkChance ?? ttsConfig.FaceLipSyncProcBlinkWinkChance,
                            // Store raw override for ReloadConfig re-apply
                            fo);

                        if (fo?.HasAnyOverride == true)
                        {
                            Log.Debug(() => $"[FACE-OVERRIDE] Applied FaceOverride for {_npcName} (personality: {_personality.Id})");
                        }
                    }
                }

                // Head gestures (nod/shake on accept/reject) — skip on dedi (no rendering)
                if (!GameManager.IsDedicatedServer)
                {
                    var gestureCfg = XNPCVoiceControlMod.HeadGestureConfig;
                    if (gestureCfg != null && gestureCfg.Enabled)
                    {
                        _headGesture = gameObject.AddComponent<NPCHeadGesture>();
                        _headGesture.Init(_npcEntity, gestureCfg);
                    }
                }
            }
            else
            {
                _ttsEnabled = false;
            }

            // Try to read the leader from SCore's hiring data
            TryReadLeaderFromSCore();

            // Wire typewriter effect - streams characters into subtitle UI incrementally
            OnTypingUpdate -= HandleTypingUpdate;
            OnTypingUpdate += HandleTypingUpdate;

            Log.Debug(() => $"Initialized chat component for NPC: {_npcName} (ID: {_entityId}), leader: {_leaderEntityId}");
        }

        /// <summary>
        /// Handler for typewriter effect - updates subtitle UI with growing text.
        /// </summary>
        private void HandleTypingUpdate(string currentText)
        {
            SubtitleManager.Instance?.UpdateSubtitle(_npcName, currentText);
        }

        private string GetNPCName()
        {
            // Priority 1: Name from entity (set by game from entityclasses.xml)
            // This is the actual game-assigned name, e.g. "Bruce"
            if (_npcEntity != null)
            {
                string entityName = _npcEntity.EntityName;
                if (!string.IsNullOrEmpty(entityName) && entityName != "playerMale" && entityName != "playerFemale")
                {
                    return entityName;
                }
            }

            // Priority 2: Name from entity class "Names" property (entityclasses.xml)
            // This may be a comma-separated list, e.g. "Bob,Billy,Beau,Ben,Brody,Byron,Brian,Bruce"
            string entityClassName = GetEntityClassName();
            if (!string.IsNullOrEmpty(entityClassName))
            {
                return entityClassName;
            }

            // Priority 3: Name from assigned personality definition (generic type name)
            if (_personality != null && !string.IsNullOrEmpty(_personality.Name))
            {
                return _personality.Name;
            }

            // Priority 4: Generate a name based on entity ID for consistency
            string[] names = { "Alex", "Jordan", "Sam", "Riley", "Casey", "Morgan", "Quinn", "Avery", "Blake", "Drew" };
            return names[_entityId % names.Length];
        }

        /// <summary>
        /// Read the "Names" property from the NPC's entity class.
        /// Uses EntityClass.Properties.Values[key] - same as SCore.
        /// </summary>
        private string GetEntityClassName()
        {
            if (_npcEntity?.EntityClass == null) return null;

            try
            {
                if (_npcEntity.EntityClass.Properties.Values.ContainsKey("Names"))
                {
                    return _npcEntity.EntityClass.Properties.Values["Names"];
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Error reading entity class Names property: {ex.Message}");
            }

            return null;
        }



        /// <summary>
        /// Read the "Tags" property from the NPC's entity class and extract gender.
        /// Returns "male" or "female" if found, null otherwise.
        /// Uses EntityClass.Properties.Values[key] - same as SCore.
        /// </summary>
        private string GetGenderTag()
        {
            if (_npcEntity?.EntityClass == null) return null;

            try
            {
                // Try both "Tags" and "tags"
                string tagsValue = null;
                foreach (string key in new[] { "Tags", "tags" })
                {
                    if (_npcEntity.EntityClass.Properties.Values.ContainsKey(key))
                    {
                        tagsValue = _npcEntity.EntityClass.Properties.Values[key];
                        if (!string.IsNullOrEmpty(tagsValue)) break;
                    }
                }

                if (string.IsNullOrEmpty(tagsValue)) return null;

                // Check for "male" or "female" in the tags (comma or space separated)
                string[] tagParts = tagsValue.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string tag in tagParts)
                {
                    string trimmed = tag.Trim().ToLower();
                    if (trimmed == "male" || trimmed == "female")
                    {
                        return trimmed;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Error reading entity class tags property: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Read the optional "lipsync" entity property.
        /// Default = on (absent → on). off/false/0/no (case-insensitive) → disabled.
        /// Mirrors GetPersonalityFromEntityProperty pattern.
        /// </summary>
        private bool IsLipSyncEnabled(EntityAlive npc)
        {
            if (npc?.EntityClass == null) return true;

            try
            {
                if (npc.EntityClass.Properties.Values.ContainsKey("lipsync"))
                {
                    string value = npc.EntityClass.Properties.Values["lipsync"].Trim().ToLower();
                    bool disabled = value == "off" || value == "false" || value == "0" || value == "no";
                    if (disabled)
                        Log.Debug(() => $"[LIPSYNC] {npc.EntityName}: disabled via 'lipsync' entity property");
                    return !disabled;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Error reading lipsync entity property: {ex.Message}");
            }

            return true; // default on
        }

        private string BuildSystemPrompt()
        {
            // Load base prompt and TTS rule from external XML (promptconfig.xml).
            // Falls back to hardcoded defaults if the config is missing or nodes are empty.
            string basePrompt = _config.SystemPrompt;

            // TTS compatibility rule: loaded from XML or falls back to hardcoded Romaji rule.
            string ttsRule = GetTtsRule();

            // Add NPC identity
            string identityPrompt = $"Your name is {_npcName}. ";

            // Add location context if available
            string locationContext = "";
            if (_npcEntity != null)
            {
                Vector3 pos = _npcEntity.position;
                // Could be expanded to detect biome, nearby POIs, etc.
                locationContext = "You are currently surviving in the wasteland. ";
            }

            // Stranger identity: prevent NPC from using its own name to address the player.
            // The NPC must learn the player's name organically through RAG memory, never from the base prompt.
            string strangerIdentity = "You are speaking directly to a fellow survivor - they are talking TO YOU. When they say 'I', 'me', 'my', or 'mine', they mean themselves. If they tell you their name (e.g. 'my name is X'), acknowledge them by that name. You do not know their name unless they have stated it or it appears in your [SURVIVOR MEMORIES]. If you do not know their name, refer to them as 'stranger', 'traveler', 'friend', or simply 'you'. You must NEVER refer to the survivor by your own name. ";

            // Check the personality definition's traits
            if (_personality != null && !string.IsNullOrEmpty(_personality.Traits))
            {
                return $"{identityPrompt}{locationContext}{strangerIdentity}{_personality.Traits} {basePrompt}{ttsRule}";
            }

            return $"{identityPrompt}{locationContext}{strangerIdentity}{basePrompt}{ttsRule}";
        }

        /// <summary>
        /// Get the TTS phonetic rule from promptconfig.xml, falling back to a hardcoded default.
        /// Dubbed architecture: ALL spoken_text is English regardless of NPC personality.
        /// subtitle_localized carries the player's UI language translation.
        /// </summary>
        private string GetTtsRule()
        {
            // Dubbed mode: always enforce English for spoken audio.
            // The LLM outputs dual JSON: spoken_text (English) + subtitle_localized (player's UI language).
            // This prevents the LLM from drifting into Japanese/Chinese for the audio field.
            return "\nCRITICAL TTS INSTRUCTION: Your spoken_text must be exclusively in English. Do NOT use Chinese characters, Japanese characters, or any non-Latin script in your spoken_text output. The subtitle_localized field may use the player's UI language." +
                   "\nBREVITY: Keep spoken_text to 1-2 short sentences. Be conversational and warm.";
        }

        /// <summary>
        /// Set the full personality definition for this NPC
        /// </summary>
        public void SetPersonality(PersonalityDefinition personality)
        {
            _personality = personality;
            if (personality != null && !string.IsNullOrEmpty(personality.Name))
            {
                _npcName = personality.Name;
            }
            _systemPrompt = BuildSystemPrompt();
        }

        /// <summary>
        /// Get the assigned personality definition
        /// </summary>
        public PersonalityDefinition Personality => _personality;

        /// <summary>
        /// Get a random greeting from the assigned personality, filtered by player UI language.
        /// </summary>
        public string GetGreeting()
        {
            string playerLang = LocalizationHelper.ResolveEffectiveLanguage(_personality);
            return _personality?.GetRandomGreeting(playerLang);
        }

        

        #region Interruption Matrix (Active Polling)

        /// <summary>
        /// Abort the current conversation immediately.
        /// Cancels LLM/TTS, stops audio, clears UI, resets busy flag.
        /// Call from main thread only.
        /// </summary>
        public void AbortConversation(string reason)
        {
            Log.Out($"[INTERRUPT] {reason} - aborting conversation for {_npcName}");

            // Cancel background LLM task
            if (_interruptionTokenSource != null && !_interruptionTokenSource.IsCancellationRequested)
            {
                try { _interruptionTokenSource.Cancel(); } catch { /* ObjectDisposedException if already disposed */ }
            }

                                        if (_audioPlayer != null)
            {
                _audioPlayer.StopSpeaking($"AbortConversation: {reason}");
            }

            // Also stop direct AudioSource playback (voice pipeline)
            if (_cachedAudioSource != null && _cachedAudioSource.isPlaying)
            {
                _cachedAudioSource.Stop();
            }

            // Clear UI subtitle
            SubtitleManager.Instance?.ClearSubtitle();

            // Reset busy flags
            _isWaitingForResponse = false;
            _isTyping = false;

            // Stop monitor coroutine
            if (_interruptionMonitorCoroutine != null)
            {
                try { StopCoroutine(_interruptionMonitorCoroutine); } catch { /* already stopped or coroutine invalid */ }
                _interruptionMonitorCoroutine = null;
            }

            // Unlock microphone for next input
            VoiceInputManager.Instance?.MarkProcessingComplete();
        }

        /// <summary>
        /// Start the interruption monitor coroutine.
        /// Polls NPC health and combat state every 0.25s while conversation is active.
        /// Call from main thread only.
        /// </summary>
        private void StartInterruptionMonitor()
        {
            // NPC may have been picked up or dismissed - its GameObject is gone
            if (!gameObject.activeInHierarchy) return;

            if (_interruptionMonitorCoroutine != null)
            {
                try { StopCoroutine(_interruptionMonitorCoroutine); } catch { /* already stopped or coroutine invalid */ }
            }

            _interruptionMonitorCoroutine = StartCoroutine(InterruptionMonitorCoroutine());
        }

        /// <summary>
        /// Stop the interruption monitor (conversation finished naturally).
        /// Call from main thread only.
        /// </summary>
        private void StopInterruptionMonitor()
        {
            if (_interruptionMonitorCoroutine != null)
            {
                try { StopCoroutine(_interruptionMonitorCoroutine); } catch { /* already stopped or coroutine invalid */ }
                _interruptionMonitorCoroutine = null;
            }

            // Dispose the token source
            if (_interruptionTokenSource != null && !_interruptionTokenSource.IsCancellationRequested)
            {
                try { _interruptionTokenSource.Dispose(); } catch { /* ObjectDisposedException if already disposed */ }
                _interruptionTokenSource = null;
            }
        }

        /// <summary>
        /// Active polling coroutine: checks for damage and combat entry.
        /// Only runs while _isWaitingForResponse is true (conversation active).
        /// </summary>
        private IEnumerator InterruptionMonitorCoroutine()
        {
            // Baseline: record health at start of conversation
            float baselineHealth = 0f;
            try { baselineHealth = _npcEntity?.Health ?? 0f; } catch { /* entity destroyed mid-conversation */ }

            while (_isWaitingForResponse && _npcEntity != null)
            {
                yield return new WaitForSeconds(0.25f);

                // Guard: NPC may have died or been destroyed during conversation
                if (_npcEntity == null || _npcEntity.IsDead())
                {
                    AbortConversation("NPC died or destroyed");
                    break;
                }

                // Check 1: Damage detection (health dropped below baseline)
                try
                {
                    float currentHealth = _npcEntity.Health;
                    if (currentHealth < baselineHealth - 0.5f) // 0.5f tolerance for floating point
                    {
                        AbortConversation("Took damage");
                        break;
                    }
                }
                catch { /* entity destroyed mid-loop */ }

                // Check 2: Combat entry - multiple signals for robustness
                try
                {
                    var attackTarget = _npcEntity.GetAttackTarget();
                    var revengeTarget = _npcEntity.GetRevengeTarget();
                    bool hasTarget = attackTarget != null || revengeTarget != null;
                    bool isAlerted = _npcEntity.IsAlert;
                    // Hired NPCs ignore alert-only state - they follow orders even while aware of threats.
                    // Wild NPCs abort on any combat signal including alert.
                    bool alertAbort = isAlerted && !HasLeader();
                    if (hasTarget || alertAbort)
                    {
                        AbortConversation("Entered combat" + (hasTarget ? " (target set)" : " (alerted)"));
                        break;
                    }
                }
                catch { /* entity destroyed mid-loop */ }
            }
        }

        #endregion

        /// <summary>
        /// Get the AudioSource from this NPC's audio player for direct playback.
        /// </summary>
        private AudioSource _audioSource
        {
            get
            {
                if (_cachedAudioSource == null && _audioPlayer != null)
                {
                    // Grab the AudioSource that NPCAudioPlayer created
                    _cachedAudioSource = _audioPlayer.GetComponent<AudioSource>();
                }
                return _cachedAudioSource;
            }
        }
        private AudioSource _cachedAudioSource;

        /// <summary>
        /// Wait for playback to finish, then destroy the clip and fire callback.
        /// </summary>
        private IEnumerator WaitForPlaybackFinish(AudioClip clip, System.Action onComplete)
        {
            yield return new WaitForSeconds((float)clip.length);
            if (_cachedAudioSource != null && _cachedAudioSource.isPlaying)
                _cachedAudioSource.Stop();
            UnityEngine.Object.Destroy(clip);
            onComplete?.Invoke();
        }

        /// <summary>
        /// Process a message from the player with player reference and name bonus flag.
        /// </summary>
        /// <param name="usedName">True if player used NPC name to target, granting +1 intent bonus on phrase matching.</param>
        public void ProcessPlayerMessage(string playerMessage, EntityPlayer player, bool usedName, Action<string> onComplete = null)
        {
            // ================================================================
            // LAYER 1: GLOBAL STATE GUARDS
            // Resolve infrastructure state before any branch decision.
            // ================================================================

            // Clips-mode NPCs can handle phrase triggers without LLM/TTS servers.
            if (ServerManager.ReadyState != ServerManager.ServerReadyState.Ready && !_usesClips)
                return;
            _pipelineStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (_isWaitingForResponse)
            {
                // Hired NPCs: new command interrupts current response/speech immediately.
                // Free-roaming NPCs: silently drop the message (player hasn't hired them).
                if (_leaderEntityId != -1)
                {
                    Log.Debug(() => $"NPC {_npcName}: hired - interrupting current response for new command");
                    StopSpeaking("hired-command-interrupt");
                    Interrupt();
                    XNPCVoiceControl.UI.SubtitleManager.Instance.ClearSubtitle();
                }
                else
                {
                    Log.Debug(() => $"NPC {_npcName} is still thinking...");
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(playerMessage))
            {
                return;
            }

            // Mark this player as having interacted so the proximity loop doesn't fire a
            // proactive greeting immediately after this response completes.
            if (player != null)
            {
                _lastGreetedPlayerId = player.entityId;
                _playerHasLeftRange = false;
                _lastProactiveGreetTime = Time.realtimeSinceStartup;
            }

            // Refresh leader from SCore - player may have hired via UI since last check.
            // MUST run before combat gate so hired NPCs pass through correctly.
            TryReadLeaderFromSCore();

            // Billing approval gate (Task 3B v3): if NPC is awaiting payment approval,
            // catch yes/no/pay before phrase-trigger/LLM path misroutes them.
            if (HasLeader() && _npcEntity.Buffs.GetCustomVar("RetainerAwaitingApproval") > 0f)
            {
                string lower = playerMessage.ToLowerInvariant();
                if (lower.Contains("yes") || lower.Contains("pay"))
                {
                    HandleBillingResponse(true);
                    return;
                }
                if (lower.Contains("no"))
                {
                    HandleBillingResponse(false);
                    return;
                }
                // Anything else while awaiting: fall through to normal handling.
            }

            // Combat gate: wild NPCs (no leader) should not respond during active combat
            // or within a short cooldown after the last live target dies.
            // Uses _lastCombatTargetTime (tracked in Update) instead of IsAlert,
            // which lingers unpredictably long after kills.
            // EXCEPT for action commands (hire, follow, guard, dismiss, etc.).
            float combatCooldown = XNPCVoiceControlMod.Config?.WildCombatChatCooldownSeconds ?? 5f;
            bool recentlyInCombat = (Time.realtimeSinceStartup - _lastCombatTargetTime) < combatCooldown;
            if (!HasLeader() && recentlyInCombat)
            {
                // Peek: does this message match an action command?
                bool isActionCmd = PhraseTriggerHandler.Instance.IsActionCommand(playerMessage, out var actionType);
                if (isActionCmd && IsCombatPassableCommand(actionType))
                {
                    Log.Debug(() => $"[NPCVoiceControl] {_npcName} is wild and in combat but allowing command: \"{playerMessage}\" ({actionType})");
                }
                else
                {
                    Log.Out($"[NPCVoiceControl] {_npcName} is wild and in combat - ignoring message: \"{playerMessage}\"");
                    return;
                }
            }

            // ================================================================
            // LAYER 2: PHRASE TRIGGER FAST-PATH (Deterministic)
            // Exact phrase match → execute action + hardcoded voice instantly.
            // No AI, no typewriter. Exit early on success.
            // ================================================================
            if (PhraseTriggerHandler.Instance.Enabled && player != null)
            {
                string triggerAudio;
                string triggerSubtitle;
                bool canExecuteActions = CanExecuteActions(player);
                string playerUiLang = LocalizationHelper.ResolveEffectiveLanguage(_personality);
                if (PhraseTriggerHandler.Instance.TryHandlePhrase(playerMessage, _npcEntity, player, _npcName, out triggerAudio, out triggerSubtitle, canExecuteActions, usedName, _personality?.TTSLanguage ?? "en", playerUiLang))
                {
                    _lastInteractingPlayer = player;

                    if (!canExecuteActions)
                    {
                        Log.Debug(() => $"Phrase trigger handled - leader-gated actions restricted for player {player.entityId} (not leader of {_npcName}, leader: {_leaderEntityId})");
                        _headGesture?.Play(HeadGestureType.Shake);
                    }
                    else
                    {
                        string matchedAction = PhraseTriggerHandler.Instance.LastMatchedTriggerName;
                        if (!string.IsNullOrEmpty(matchedAction) &&
                            !string.Equals(matchedAction, "none", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(matchedAction, "customdialog", StringComparison.OrdinalIgnoreCase))
                        {
                            _headGesture?.Play(HeadGestureType.Nod);
                        }
                    }

                    // Add to conversation history for context (store audio text)
                    _conversationHistory.Add(new ChatMessage("Player", playerMessage));
                    _conversationHistory.Add(new ChatMessage("NPC", triggerAudio ?? ""));
                    TrimHistory();

                    // RAG: add phrase trigger exchange to shadow buffer, tagged with the matched action
                    // so the extractor reads the semantic meaning of generic pre-written responses.
                    // EXCEPTION: routine movement/combat commands (follow, stay, guard, dismiss) are
                    // never added — they're deterministic, generic, content-free at scale, and 30+
                    // repetitions overwhelm the extraction model's "don't extract routine movement"
                    // instruction, producing cycling fake facts. Triggers with real narrative
                    // significance (Hire, Trade, GiveItem, etc.) still reach the buffer as before.
                    string triggerAction = PhraseTriggerHandler.Instance.LastMatchedTriggerName;
                    bool isRoutineCommand = s_routineTriggersExcludedFromMemory.Contains(triggerAction ?? "");
                    if (!string.IsNullOrEmpty(triggerAction) && !isRoutineCommand)
                    {
                        _unsummarizedBuffer.Add(new ChatMessage("Player", $"[{triggerAction.ToUpper()}] {playerMessage}"));
                        _unsummarizedBuffer.Add(new ChatMessage("NPC", triggerAudio ?? ""));
                    }
                    else if (!isRoutineCommand)
                    {
                        _unsummarizedBuffer.Add(new ChatMessage("Player", playerMessage));
                        _unsummarizedBuffer.Add(new ChatMessage("NPC", triggerAudio ?? ""));
                    }
                    _timeSinceLastMessage = 0f;

                    // Show localized subtitle for phrase trigger response.
                    // Skip for story triggers on TTS NPCs — the story path shows its own subtitle.
                    string displayText = !string.IsNullOrWhiteSpace(triggerSubtitle) ? triggerSubtitle : triggerAudio;
                    bool isStoryTrigger = string.Equals(PhraseTriggerHandler.Instance.LastMatchedTriggerName,
                        "customdialog", StringComparison.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(displayText) && !(isStoryTrigger && !_llmDisabled && _ttsEnabled))
                    {
                        SubtitleManager.Instance.ShowSubtitle(_npcName, displayText, 25f);
                    }

                    // Lock the pipeline and start the active polling matrix!
                    // This ensures phrase trigger audio can be interrupted if NPC takes damage.
                    _isWaitingForResponse = true;
                    _interruptionTokenSource = new CancellationTokenSource();
                    StartInterruptionMonitor();

                    OnResponseStarted?.Invoke(triggerAudio);
                    onComplete?.Invoke(triggerAudio);

                    // Capture pipeline ID to guard callbacks against interruption (TC-7 fix).
                    int myPipelineId = _pipelineId;

                    // Resolve a voice clip for this trigger (Clips/Mixed). CustomDialog draws from
                    // the Backstory pool; every other trigger from Triggers/<action>. Both fall to
                    // the Default pool when their own pool is empty (decision: Default before TTS).
                    ClipEntry tclip = null;
                    if (_usesClips && _audioPlayer != null)
                    {
                        string matched = PhraseTriggerHandler.Instance.LastMatchedTriggerName;
                        bool isStory = string.Equals(matched, "customdialog", StringComparison.OrdinalIgnoreCase);
                        bool found = isStory
                            ? VoiceClipLibrary.Instance.TryGetBackstory(_clipFolderKey, out tclip)
                            : VoiceClipLibrary.Instance.TryGetTrigger(_clipFolderKey, matched, out tclip);
                        if (!found)
                            VoiceClipLibrary.Instance.TryGetDefault(_clipFolderKey, out tclip);
                    }

                    if (tclip != null)
                    {
                        // Clip's own subtitle overrides the phrase-trigger text so the
                        // caption matches the recorded line (loc key wins, sidecar fallback).
                        string tsub = tclip.ResolveSubtitle();
                        if (!string.IsNullOrEmpty(tsub))
                            SubtitleManager.Instance.ShowSubtitle(_npcName, tsub, 25f);

                        OnSpeechStarted?.Invoke(triggerAudio);
                        _audioPlayer.PlayClipFile(tclip, () =>
                        {
                            if (_pipelineId == myPipelineId)
                            {
                                SubtitleManager.Instance.ClearSubtitle();
                                OnSpeechComplete?.Invoke();
                                VoiceInputManager.Instance?.MarkProcessingComplete();
                            }
                            MainThreadDispatcher.Enqueue(() =>
                            {
                                if (_pipelineId == myPipelineId)
                                {
                                    _isWaitingForResponse = false;
                                    if (_interruptionTokenSource != null)
                                    {
                                        _interruptionTokenSource.Dispose();
                                        _interruptionTokenSource = null;
                                    }
                                }
                            });
                        });
                    }
                    // Clips-only NPCs never fall to TTS; only Mixed/Tts modes do.
                    else if (!_llmDisabled && _ttsEnabled && _audioPlayer != null && TTSService.Instance.IsInitialized)
                    {
                        long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _pipelineStartMs;
                        Log.Debug(() => $"[TIMING] Phrase trigger → TTS start: {elapsed}ms");

                        // Story trigger (CustomDialog) on TTS NPCs: pick randomly from personality's story pool.
                        bool isStory = string.Equals(PhraseTriggerHandler.Instance.LastMatchedTriggerName,
                            "customdialog", StringComparison.OrdinalIgnoreCase);

                        if (isStory)
                        {
                            string storyText = _personality?.GetRandomStory()
                                ?? "I've seen a lot of things out here... but I'd rather not talk about it.";

                            _conversationHistory.Add(new ChatMessage("NPC", storyText));
                            SubtitleManager.Instance.ShowSubtitle(_npcName, storyText, 25f);
                            OnSpeechStarted?.Invoke(storyText);
                            onComplete?.Invoke(storyText);
                            _audioPlayer.SpeakStreaming(storyText, () =>
                            {
                                if (_pipelineId == myPipelineId)
                                {
                                    XNPCVoiceControl.UI.SubtitleManager.Instance.ClearSubtitle();
                                    OnSpeechComplete?.Invoke();
                                    VoiceInputManager.Instance?.MarkProcessingComplete();
                                }
                                MainThreadDispatcher.Enqueue(() =>
                                {
                                    if (_pipelineId == myPipelineId)
                                    {
                                        _isWaitingForResponse = false;
                                        if (_interruptionTokenSource != null)
                                        {
                                            _interruptionTokenSource.Dispose();
                                            _interruptionTokenSource = null;
                                        }
                                    }
                                });
                            });
                        }
                        else
                        {
                            OnSpeechStarted?.Invoke(triggerAudio);
                            _audioPlayer.SpeakStreaming(triggerAudio, () =>
                            {
                                if (_pipelineId == myPipelineId)
                                {
                                    SubtitleManager.Instance.ClearSubtitle();
                                    OnSpeechComplete?.Invoke();
                                    VoiceInputManager.Instance?.MarkProcessingComplete();
                                }
                                MainThreadDispatcher.Enqueue(() =>
                                {
                                    if (_pipelineId == myPipelineId)
                                    {
                                        _isWaitingForResponse = false;
                                        if (_interruptionTokenSource != null)
                                        {
                                            _interruptionTokenSource.Dispose();
                                            _interruptionTokenSource = null;
                                        }
                                    }
                                });
                            });
                        }
                    }
                    else
                    {
                        // No TTS - release lock and clear pipeline immediately
                        if (_pipelineId == myPipelineId)
                            VoiceInputManager.Instance?.MarkProcessingComplete();
                        OnResponseComplete?.Invoke(triggerAudio);
                        MainThreadDispatcher.Enqueue(() =>
                        {
                            if (_pipelineId == myPipelineId)
                            {
                                _isWaitingForResponse = false;
                                if (_interruptionTokenSource != null)
                                {
                                    _interruptionTokenSource.Dispose();
                                    _interruptionTokenSource = null;
                                }
                            }
                        });
                    }

                    return; // Skip LLM processing
                }
            }

            // Clips-only NPCs have no LLM - play a Default clip if available, then stop (never TTS)
            if (_llmDisabled)
            {
                int myPipelineId = _pipelineId;  // TC-7 guard
                if (_audioPlayer != null && !string.IsNullOrEmpty(_clipFolderKey) &&
                    VoiceClipLibrary.Instance.TryGetDefault(_clipFolderKey, out var dclip))
                {
                    string dsub = dclip.ResolveSubtitle();
                    if (!string.IsNullOrEmpty(dsub))
                        SubtitleManager.Instance.ShowSubtitle(_npcName, dsub, 25f);
                    OnSpeechStarted?.Invoke(dsub ?? string.Empty);
                    _audioPlayer.PlayClipFile(dclip, () =>
                    {
                        if (_pipelineId == myPipelineId)
                        {
                            SubtitleManager.Instance.ClearSubtitle();
                            _isWaitingForResponse = false;
                            VoiceInputManager.Instance?.MarkProcessingComplete();
                            OnSpeechComplete?.Invoke();
                            onComplete?.Invoke(string.Empty);
                        }
                    });
                }
                else
                {
                    if (_pipelineId == myPipelineId)
                    {
                        VoiceInputManager.Instance?.MarkProcessingComplete();
                        _isWaitingForResponse = false;
                        onComplete?.Invoke(string.Empty);
                    }
                }
                return;
            }

            // TACTICAL MODE: if player is in tactical mode and no phrase trigger matched,
            // give a brief clarification instead of falling through to LLM chat.
            bool isTacticalMode = player != null && (int)player.Buffs.GetCustomVar("varTacticalMode") == 1;
            if (isTacticalMode)
            {
                string clarification = s_tacticalClarifications[UnityEngine.Random.Range(0, s_tacticalClarifications.Length)];
                SubtitleManager.Instance.ShowSubtitle(_npcName, clarification, 25f);
                SpeakText(clarification);
                return;
            }

            _lastInteractingPlayer = player;
            if (player != null)
            {
                string resolvedId = GetPlayerPersistentId(player);
                _lastPlayerPersistentId = resolvedId;
                if (resolvedId != "unknown")
                {
                    s_lastResolvedPlayerPersistentId = resolvedId;
                    if (!s_playerIdLogged)
                    {
                        Log.Out($"[RAG] Resolved stable player ID: {resolvedId}");
                        s_playerIdLogged = true;
                    }
                }
                _lastPlayerDisplayName = GetPlayerDisplayName(player);
            }
            _isWaitingForResponse = true;

            // Initialize interruption token and start polling monitor
            _interruptionTokenSource = new CancellationTokenSource();
            StartInterruptionMonitor();

            OnResponseStarted?.Invoke("...");

            // Add player message to history (raw text for context continuity)
            _conversationHistory.Add(new ChatMessage("Player", playerMessage));
            TrimHistory();

            // Capture player message for shadow buffer pairing in HandleLLMResponse
            _lastPlayerMessageForMemory = playerMessage;

            // Strip wake words before sending to LLM (phrase triggers already got raw text above)
            string llmInput = StripWakeWords(playerMessage);

            // Capture environmental sense on main thread before the coroutine goes off-thread (mirrors voice pipeline)
            string senseBlock = NPCSenseSnapshot.Capture(_npcEntity)?.ToPromptString() ?? "";

            // Start coroutine: fetch RAG memory context, then send to LLM
            StartCoroutine(ProcessPlayerMessageCoroutine(
                BuildStreamingSystemPrompt(),
                senseBlock, llmInput, _interruptionTokenSource.Token, onComplete));
        }

        /// <summary>
        /// Build system prompt that includes action instructions for the LLM.
        /// Dubbed architecture: spoken_text is ALWAYS English. subtitle_localized uses player's UI language.
        /// </summary>
        private string BuildActionSystemPrompt()
        {
            // Player's UI language controls subtitle translation
            string playerLang = LocalizationHelper.ResolveEffectiveLanguage(_personality);
            string playerLangName = GetLanguageDisplayName(playerLang);

            return _systemPrompt + $@"

IMPORTANT: You can perform actions based on player requests. When you agree to do something, include a JSON action block in your response.

You must respond using a dual-output JSON format:
{{
  ""spoken_text"": ""The text the NPC speaks (ALWAYS in English)"",
  ""subtitle_localized"": ""The subtitle shown to the player (in {playerLangName})"",
  ""action"": ""the action type or null""
}}

Rules:
- spoken_text: ALWAYS in English. This is what the TTS engine speaks. Never use non-Latin script here.
- subtitle_localized: Always in {playerLangName}. This is what the player reads on screen.
- If both languages are English, both fields should contain the same text.
- action: null for pure dialogue, or one of the available actions below.

Available actions and when to use them:
- follow: Player asks you to come with them, accompany them, follow them
- stop: Player asks you to stop following or stay where you are
- wait: Player asks you to wait or hold position
- guard: Player asks you to guard, protect, or watch an area
- trade: ONLY when the player explicitly asks to trade, buy, sell, or see your items. Do NOT use this just because the player mentions owning or having something.
- give: You decide to give the player an item
- heal: ONLY when the player explicitly asks to be healed, patched up, or treated. Do NOT use this just because medical supplies, bandages, or treatment are mentioned in passing.
- refuse: You decline a request (dangerous, unreasonable, out of character)

Examples:
Player: ""Come with me, I need backup""
Response: {{""spoken_text"": ""Alright, I've got your back. Let's move."", ""subtitle_localized"": ""Alright, I've got your back. Let's move."", ""action"": ""follow""}}

Player: ""What's it like out here?""
Response: {{""spoken_text"": ""It's rough. Every day is a fight for survival."", ""subtitle_localized"": ""It's rough. Every day is a fight for survival."", ""action"": null}}

Player: ""Can you give me some bandages?""
Response: {{""spoken_text"": ""Here, take these. Stay safe out there."", ""subtitle_localized"": ""Here, take these. Stay safe out there."", ""action"": ""give"", ""item"": ""bandage"", ""amount"": 2}}

--- Japanese input examples (dubbed: English audio + Japanese subtitles) ---
Player: ""ついてきて!""
Response: {{""spoken_text"": ""On it, I'll follow."", ""subtitle_localized"": ""了解、ついていくよ。"", ""action"": ""follow""}}

Player: ""ここで待ってて""
Response: {{""spoken_text"": ""Understood, I'll wait here."", ""subtitle_localized"": ""了解、ここで待ってる。"", ""action"": ""stop""}}

Player: ""お腹がすいた""
Response: {{""spoken_text"": ""Here, eat up. You look like you could use it."", ""subtitle_localized"": ""了解、食べて。"", ""action"": ""give"", ""item"": ""foodCornMeal"", ""amount"": 1}}

Stay in character. Only perform actions that make sense for your personality.";
        }

        /// <summary>
        /// Build system prompt for the streaming path.
        /// Plain-text output (no JSON) so the first token is actual dialogue.
        /// Actions are signaled with bracket tags: [follow], [guard], etc.
        /// </summary>
        private string BuildStreamingSystemPrompt()
        {
            if (!_actionsEnabled)
                return _systemPrompt;

            return _systemPrompt + @"

You may perform actions when the player requests them. If you do, place a single action tag at the very start of your response, then speak naturally:
- [follow] - you will accompany the player
- [stop] - you will stop following
- [wait] - you will hold position
- [guard] - you will guard this area
- [trade] - ONLY when the player explicitly asks to trade, buy, sell, or see your inventory/items. Do NOT use this just because the player mentions owning, having, or finding something.
- [refuse] - you are declining a request

Example: '[follow] Alright, I've got your back. Let's move out.'
If no action is needed, respond naturally with no tag.";
        }

        private void HandleLLMResponse(string response, Action<string> onComplete)
        {
            // Clean up interruption monitor (LLM response received naturally)
            StopInterruptionMonitor();
            _isWaitingForResponse = false;

            // Log raw LLM response for debugging
            Log.Debug(() => $"Raw LLM response for {_npcName}: \"{response.Substring(0, Math.Min(500, response.Length))}\"");

            // Parse response for actions (JSON only - never parse NPC dialogue for commands)
            NPCAction action = null;
            string spokenText = response;       // For TTS (NPC's native language)
            string subtitleText = null;         // For UI (player's UI language)

            if (_actionsEnabled)
            {
                action = ActionParser.Parse(response);
                if (action != null && !string.IsNullOrEmpty(action.DialogueBefore))
                    spokenText = action.DialogueBefore;

                // Dual-output: use subtitle_localized if present
                if (!string.IsNullOrEmpty(action?.SubtitleLocalized))
                    subtitleText = action.SubtitleLocalized;

                Log.Debug(() => $"Parsed action: {action?.Type ?? NPCActionType.None} (confidence: {action?.Confidence ?? 0f:F2})");
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
                // Try to end at a sentence
                int lastPeriod = spokenText.LastIndexOf('.');
                if (lastPeriod > _config.MaxResponseLength / 2)
                {
                    spokenText = spokenText.Substring(0, lastPeriod + 1);
                }
            }

            // Subtitle defaults to spoken text if no localized version was parsed
            if (string.IsNullOrEmpty(subtitleText))
                subtitleText = spokenText;

            // Add NPC response to history (store the spoken/native text)
            _conversationHistory.Add(new ChatMessage("NPC", spokenText));
            TrimHistory();

            // RAG: add complete exchange to shadow buffer for memory consolidation
            _unsummarizedBuffer.Add(new ChatMessage("Player", _lastPlayerMessageForMemory ?? ""));
            _unsummarizedBuffer.Add(new ChatMessage("NPC", spokenText));
            _timeSinceLastMessage = 0f;

            // Execute action if parsed - only trust JSON-parsed actions (confidence >= 0.85)
            // Natural language parsing of NPC dialogue can produce false positives
            // (e.g., "Keep your guard up" matching the Guard action pattern)
            // Only the leader (hiring player) can have actions executed; leaderless NPCs reject all orders.
            if (action != null && action.Type != NPCActionType.None && action.Confidence >= 0.85f && _npcEntity != null)
            {
                // Refresh leader from SCore - player may have hired via UI
                TryReadLeaderFromSCore();

                if (!CanExecuteActions(_lastInteractingPlayer))
                {
                    Log.Debug(() => $"Action {action.Type} blocked - player {_lastInteractingPlayer?.entityId ?? -1} is not the leader of {_npcName} (leader: {_leaderEntityId})");
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
                    catch (Exception ex)
                    {
                        Log.Error($"Action execution failed: {ex.Message}");
                    }
                }
            }

            // Show subtitle for LLM response (use localized text if available)
            if (!string.IsNullOrWhiteSpace(subtitleText))
            {
                if (_config.ShowTypingIndicator && _config.TypingDelayMs > 0)
                {
                    // Typewriter mode: open window with indicator, stream characters
                    SubtitleManager.Instance.ShowSubtitle(_npcName, "...", 25f);
                    StartCoroutine(TypeResponseCoroutine(subtitleText, onComplete));
                }
                else
                {
                    // Instant display mode
                    SubtitleManager.Instance.ShowSubtitle(_npcName, subtitleText, 25f);
                    _currentResponse = subtitleText;
                    OnResponseComplete?.Invoke(subtitleText);
                    onComplete?.Invoke(subtitleText);
                }
            }

            // Trigger TTS if enabled (use spoken text in NPC's native language)
            if (_ttsEnabled && _audioPlayer != null && TTSService.Instance.IsInitialized)
            {
                long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _pipelineStartMs;
                Log.Debug(() => $"[TIMING] LLM response → TTS start: {elapsed}ms");
                Log.Debug(() => $"[TTS-DIAG] SpeakStreaming entered for action-parsed response (action={action?.Type}, confidence={action?.Confidence})");
                OnSpeechStarted?.Invoke(spokenText);
                _audioPlayer.SpeakStreaming(spokenText, () =>
                {
                    SubtitleManager.Instance.ClearSubtitle();
                    OnSpeechComplete?.Invoke();
                });
            }
            else
            {
                Log.Debug(() => $"[TTS-DIAG] SpeakStreaming SKIPPED: ttsEnabled={_ttsEnabled}, audioPlayer={_audioPlayer != null}, ttsInit={TTSService.Instance.IsInitialized}");
            }
        }

        /// <summary>
        /// Same as HandleLLMResponse but skips the TTS call - used by the streaming path
        /// where TTS was already started sentence-by-sentence via EnqueueSpeech.
        /// Still handles: history, shadow buffer, action execution, subtitle update.
        /// </summary>
        private void HandleLLMResponseNoTTS(string response, Action<string> onComplete)
        {
            StopInterruptionMonitor();
            _isWaitingForResponse = false; // safety net: streaming path normally clears this on first chunk

            Log.Debug(() => $"[STREAMING] Final response for {_npcName}: \"{response.Substring(0, Math.Min(200, response.Length))}\"");

            NPCAction action = null;
            string spokenText = response;
            string subtitleText = null;

            if (_actionsEnabled)
            {
                action = ActionParser.Parse(response);
                if (action != null && !string.IsNullOrEmpty(action.DialogueBefore))
                    spokenText = action.DialogueBefore;
                if (!string.IsNullOrEmpty(action?.SubtitleLocalized))
                    subtitleText = action.SubtitleLocalized;
                Log.Debug(() => $"Parsed action: {action?.Type ?? NPCActionType.None}");
            }

            spokenText = CleanDialogueForTts(spokenText);
            spokenText = ValidateSpokenText(spokenText, _personality?.TTSLanguage ?? "en", action);

            if (string.IsNullOrWhiteSpace(spokenText))
                spokenText = GetActionFallback(action);

            if (string.IsNullOrEmpty(subtitleText))
                subtitleText = spokenText;

            _conversationHistory.Add(new ChatMessage("NPC", spokenText));
            TrimHistory();

            _unsummarizedBuffer.Add(new ChatMessage("Player", _lastPlayerMessageForMemory ?? ""));
            _unsummarizedBuffer.Add(new ChatMessage("NPC", spokenText));
            _timeSinceLastMessage = 0f;

            if (action != null && action.Type != NPCActionType.None && action.Confidence >= 0.85f && _npcEntity != null)
            {
                TryReadLeaderFromSCore();
                if (!CanExecuteActions(_lastInteractingPlayer))
                {
                    _headGesture?.Play(HeadGestureType.Shake);
                }
                else
                {
                    try
                    {
                        ActionExecutor.Instance.ExecuteAction(_npcEntity, _lastInteractingPlayer, action);
                        if (action.Type == NPCActionType.Refuse)
                            _headGesture?.Play(HeadGestureType.Shake);
                        else
                            _headGesture?.Play(HeadGestureType.Nod);
                    }
                    catch (Exception ex) { Log.Error($"Action execution failed: {ex.Message}"); }
                }
            }

            // Clean subtitle text of any leftover emotes/action tags before display.
            // If cleaning leaves nothing (emote-only response), skip update so the streaming
            // chunk's subtitle stays until its timer expires.
            if (!string.IsNullOrWhiteSpace(subtitleText))
            {
                string cleanedSubtitle = CleanDialogueForTts(subtitleText);
                if (!string.IsNullOrWhiteSpace(cleanedSubtitle))
                    SubtitleManager.Instance.UpdateSubtitle(_npcName, cleanedSubtitle);
            }

            OnResponseComplete?.Invoke(subtitleText);
            onComplete?.Invoke(subtitleText);
            // TTS intentionally omitted - already running via streaming chunks
        }

        private IEnumerator TypeResponseCoroutine(string fullResponse, Action<string> onComplete)
        {
            _isTyping = true;
            var sb = new StringBuilder();

            foreach (char c in fullResponse)
            {
                // Abort if conversation was interrupted (e.g., NPC entered combat mid-typing)
                if (!_isTyping) yield break;  // AbortConversation sets this to false

                sb.Append(c);
                OnTypingUpdate?.Invoke(sb.ToString());
                yield return new WaitForSeconds(_config.TypingDelayMs / 1000f);
            }

            _isTyping = false;
            OnResponseComplete?.Invoke(fullResponse);
            onComplete?.Invoke(fullResponse);
        }

        private void HandleLLMError(string error)
        {
            // Clean up interruption monitor (LLM request failed)
            StopInterruptionMonitor();
            _isWaitingForResponse = false;

            // Provide a fallback response
            string fallback = GetFallbackResponse();

            // Show fallback subtitle
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                SubtitleManager.Instance.ShowSubtitle(_npcName, fallback, 25f);
            }

            OnError?.Invoke(error);
            OnResponseComplete?.Invoke(fallback);

            Log.Warning($"Error for NPC {_npcName}: {error}. Using fallback.");
        }

        private string GetFallbackResponse()
        {
            // Immersion-preserving fallback responses
            string[] fallbacks = {
                "*looks distracted* Sorry, what was that?",
                "*pauses, scanning the horizon* Hold on... thought I heard something.",
                "Hmm? My mind wandered for a second there.",
                "*rubs temples* Long day. What were you saying?",
                "Give me a moment... *checks surroundings*"
            };
            return fallbacks[UnityEngine.Random.Range(0, fallbacks.Length)];
        }

        private void TrimHistory()
        {
            // Read max history from saved CVar (updated live from config window)
            int maxHistory = XNPCVoiceControlMod.GetMaxHistory();
            while (_conversationHistory.Count > maxHistory * 2) // *2 for player + NPC pairs
            {
                _conversationHistory.RemoveAt(0);
            }
        }

        /// <summary>
        /// Extract a display name for the current player (for LLM context injection).
        /// Returns the player's Steam/display name if available, otherwise "Survivor".
        /// </summary>
        private static string GetPlayerDisplayName(EntityPlayer player)
        {
            if (player == null) return "Survivor";
            // Use entityId as a stable session identifier for display.
            // The actual Steam name would require ZNetScene which isn't always available.
            // For context injection, we just need something the LLM can reference.
            return "Survivor";
        }

        /// <summary>
        /// Clear conversation history (e.g., when player leaves and returns)
        /// </summary>
        public void ClearHistory()
        {
            _conversationHistory.Clear();
        }

        /// <summary>
        /// Get the current conversation history
        /// </summary>
        public List<ChatMessage> GetHistory()
        {
            return new List<ChatMessage>(_conversationHistory);
        }

        public bool IsWaitingForResponse => _isWaitingForResponse;
        public bool HasRotationHold => _rotationTarget != null || _npcFaceTarget != null;

        public bool IsTyping => _isTyping;
        public string CurrentResponse => _currentResponse;
        public string NPCName => _npcName;
        public EntityAlive NPCEntity => _npcEntity;
        public bool ActionsEnabled
        {
            get => _actionsEnabled;
            set => _actionsEnabled = value;
        }

        // TTS properties and methods
        public bool TTSEnabled
        {
            get => _ttsEnabled;
            set => _ttsEnabled = value;
        }

        public bool IsSpeaking => _audioPlayer != null && _audioPlayer.IsSpeaking;

        /// <summary>
        /// Stop any current speech playback
        /// </summary>
        public void StopSpeaking(string reason = "external-call")
        {
            if (_audioPlayer != null)
            {
                _audioPlayer.StopSpeaking(reason);
            }
        }

        /// <summary>
        /// Speak text through TTS (for external callers like squad ack).
        /// Guarded for null audio player and TTS initialization.
        /// </summary>
        public void SpeakText(string text)
        {
            if (_audioPlayer != null && TTSService.Instance.IsInitialized && !string.IsNullOrWhiteSpace(text))
            {
                _audioPlayer.SpeakStreaming(text, () => { });
            }
        }

        /// <summary>
        /// Set a custom voice for this NPC
        /// </summary>
        public void SetVoice(string voiceId)
        {
            if (_audioPlayer != null)
            {
                _audioPlayer.SetVoice(voiceId);
            }
        }

        /// <summary>
        /// Get the current state of this NPC from the action system
        /// </summary>
        public NPCState GetCurrentState()
        {
            return ActionExecutor.Instance?.GetNPCState(_entityId);
        }

        /// <summary>
        /// Check if the NPC is currently engaged in combat.
        /// Uses the same signals as the Interruption Matrix: attack target, revenge target, and alert state.
        /// Returns false if entity is null or dead (defensive).
        /// </summary>
        private bool IsNpcInCombat()
        {
            if (_npcEntity == null || !_npcEntity.IsAlive()) return false;

            try
            {
                var attackTarget = _npcEntity.GetAttackTarget();
                var revengeTarget = _npcEntity.GetRevengeTarget();
                bool hasTarget = attackTarget != null || revengeTarget != null;
                bool isAlerted = _npcEntity.IsAlert;
                return hasTarget || isAlerted;
            }
            catch
            {
                return false; // Defensive - treat as not in combat if check fails
            }
        }

        /// <summary>
        /// Determine which phrase trigger action types are allowed through the combat gate.
        /// Action commands (hire, follow, guard, dismiss) pass - dialogue triggers (story, custom dialog) do not.
        /// </summary>
        private static bool IsCombatPassableCommand(TriggerActionType actionType)
        {
            return actionType == TriggerActionType.Hire ||
                   actionType == TriggerActionType.FollowPlayer ||
                   actionType == TriggerActionType.StopFollow ||
                   actionType == TriggerActionType.GuardHere ||
                   actionType == TriggerActionType.GuardReturn ||
                   actionType == TriggerActionType.Wander ||
                   actionType == TriggerActionType.Loot ||
                   actionType == TriggerActionType.Dismiss ||
                   actionType == TriggerActionType.Pickup ||
                   actionType == TriggerActionType.OpenInventory ||
                   actionType == TriggerActionType.SwapWeapon ||
                   actionType == TriggerActionType.SetCombatMode ||
                   actionType == TriggerActionType.SetEngage ||
                   actionType == TriggerActionType.OpenTraderInventory ||
                   actionType == TriggerActionType.JumpUp ||
                   actionType == TriggerActionType.JumpDown;
        }

        #region Dialogue Cleaning

        /// <summary>
        /// Strip JSON blocks from dialogue text before sending to TTS.
        /// LLM sometimes embeds JSON action blocks inside natural language responses.
        /// </summary>
        private static string CleanDialogueForTts(string dialogue)
        {
            if (string.IsNullOrEmpty(dialogue)) return dialogue;

            // Remove JSON blocks: {"action": "...", ...}
            int start = dialogue.IndexOf('{');
            while (start >= 0)
            {
                int end = dialogue.IndexOf('}', start);
                if (end > start)
                {
                    dialogue = dialogue.Substring(0, start) + dialogue.Substring(end + 1);
                }
                else
                {
                    break;
                }
                start = dialogue.IndexOf('{');
            }

            // Delegate all emote/tag/quote stripping to the single canonical cleaner
            // (TTSService.StripEmotesForTts) so subtitle and TTS paths can never diverge in
            // coverage again. That function also handles trailing whitespace cleanup.
            return TTSService.StripEmotesForTts(dialogue);
        }

        /// <summary>
        /// Detect and replace placeholder/garbage LLM output.
        /// Small models (Qwen2.5-3B) sometimes emit literal language names like "Japanese text"
        /// or bracketed placeholders like "[English response]" when confused by misheard input.
        /// Returns a language-appropriate fallback phrase instead.
        /// </summary>
        private static string ValidateSpokenText(string text, string ttsLanguage, NPCAction action)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            string trimmed = text.Trim();
            // Strip surrounding brackets/quotes for comparison
            string normalized = trimmed.Trim('[', ']', '"', '"', '"').ToLowerInvariant();

            // Patterns that indicate placeholder/garbage output
            string[] placeholders = new[]
            {
                "japanese text",
                "english text",
                "chinese text",
                "spanish text",
                "korean text",
                "french text",
                "german text",
                "russian text",
                "[response]",
                "[dialogue]",
                "[text]",
                "[spoken text]",
                "[subtitle]",
                "[translation]",
                "i don't understand",
                "i do not understand",
                "i cannot understand",
            };

            if (Array.IndexOf(placeholders, normalized) >= 0)
            {
                Log.Debug(() => $"Caught placeholder LLM output: \"{trimmed}\" - replacing with fallback");
                return GetPlaceholderFallback(ttsLanguage);
            }

            // Catch repeated single words (e.g. "text text text") - model hallucination
            string[] words = trimmed.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 3)
            {
                bool allSame = true;
                for (int i = 1; i < words.Length; i++)
                {
                    if (words[i].ToLowerInvariant() != words[0].ToLowerInvariant())
                    {
                        allSame = false;
                        break;
                    }
                }
                if (allSame)
                {
                    Log.Debug(() => $"Caught repeated-word hallucination: \"{trimmed}\" - replacing with fallback");
                    return GetPlaceholderFallback(ttsLanguage);
                }
            }

            return text;
        }

        /// <summary>
        /// Return a short, natural-sounding fallback phrase for the given TTS language.
        /// Used when the LLM produces garbage/placeholder output.
        /// </summary>
        private static string GetPlaceholderFallback(string ttsLanguage)
        {
            return ttsLanguage switch
            {
                "ja" => "はい、もう一度言ってください。",  // "Yes, please say it again."
                _    => "Sorry, could you say that again?",  // English default
            };
        }

        /// <summary>
        /// Map an ISO language code to a display name for LLM prompts.
        /// </summary>
        private static string GetLanguageDisplayName(string isoLang)
        {
            return isoLang switch
            {
                "de" => "German",
                "es" => "Spanish",
                "fr" => "French",
                "it" => "Italian",
                "ja" => "Japanese",
                "ko" => "Korean",
                "pl" => "Polish",
                "pt" => "Portuguese",
                "ru" => "Russian",
                "tr" => "Turkish",
                "zh" => "Chinese",
                _    => "English",  // en, en-GB, en-IE, hi, and everything else
            };
        }

        /// <summary>
        /// Generate a short fallback phrase when the LLM returns an action-only response
        /// (e.g., {"action": "open_backpack"}) with no dialogue field.
        /// </summary>
        private static string GetActionFallback(NPCAction action)
        {
            if (action == null || action.Type == NPCActionType.None)
                return "*nods*";

            // Context-aware fallback phrases based on the action type
            switch (action.Type)
            {
                case NPCActionType.Follow:
                    return "On it. I'll follow you.";
                case NPCActionType.StopFollow:
                    return "Understood. I'll stay here.";
                case NPCActionType.Guard:
                    return "I've got this spot covered.";
                case NPCActionType.Trade:
                    return "Take a look at what I've got.";
                case NPCActionType.GiveItem:
                    return "Here, take this.";
                case NPCActionType.Heal:
                    return "Let me patch you up.";
                case NPCActionType.Refuse:
                    return "I can't do that.";
                case NPCActionType.Emote:
                    return ""; // emotes are visual only — no spoken fallback
                default:
                    return "*nods*";
            }
        }

        #endregion

        #region RAG Memory Integration (Shadow Buffer + Context Injection)

        /// <summary>
        /// Per-frame tick: flush shadow buffer after idle timeout or 20+ messages.
        /// Early-return when buffer is empty = zero overhead.
        /// </summary>
        void Update()
        {
            // Track combat target presence for the wild-NPC chat cooldown gate.
            // Cheap check — only reads two references, no allocation.
            if (_npcEntity != null && _npcEntity.IsAlive())
            {
                if (_npcEntity.GetAttackTarget() != null || _npcEntity.GetRevengeTarget() != null)
                    _lastCombatTargetTime = Time.realtimeSinceStartup;
            }

            // Compute chat-pause state once per frame — MUST run before the buffer early-return
            // so UAI considerations always see fresh state even when shadow buffer is empty.
            UpdateChatPauseState();

            // Proximity greeting check (replaces UAI ProactiveGreet action)
            UpdateProximityGreeting();

            // MP Phase 1b — chat-state sync (client → server only, zero cost on SP/listen-host)
            bool currentlyActive = ChatPauseActive;
            if (currentlyActive != _lastNotifiedActive)
            {
                _lastNotifiedActive = currentlyActive;
                VCChatStateNotifier.Notify(_entityId, currentlyActive);
                if (currentlyActive)
                    _lastChatStateKeepaliveTime = Time.realtimeSinceStartup; // reset keepalive on start
            }
            else if (currentlyActive && (Time.realtimeSinceStartup - _lastChatStateKeepaliveTime > 20f))
            {
                // Keepalive every 20s while conversation stays active
                VCChatStateNotifier.Notify(_entityId, true);
                _lastChatStateKeepaliveTime = Time.realtimeSinceStartup;
            }

            if (_unsummarizedBuffer.Count == 0)
                return;

            _timeSinceLastMessage += Time.deltaTime;

            // Flush trigger: deep idle OR at hard cap (20).
            // Skip flush if below MinFlushMessages floor (prevents 2-message NONE flushes),
            // UNLESS we're at the 20 cap — that always fires.
            bool atCap = _unsummarizedBuffer.Count >= 20;
            bool idleTimeout = _timeSinceLastMessage >= XNPCVoiceControlMod.ExtractionIdleSeconds;
            if ((idleTimeout || atCap) && (atCap || _unsummarizedBuffer.Count >= XNPCVoiceControlMod.MinFlushMessages))
                FlushShadowBuffer();
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Clean up resources when this component is destroyed (NPC death, scene unload, etc.).
        /// Prevents memory leaks from conversation history, audio player, and event handlers.
        /// </summary>
        void OnDestroy()
        {
            // RAG: always save pending buffer synchronously on destruction
            // (async LLM calls fail during game quit, and we can't reliably detect quitting)
            if (_unsummarizedBuffer.Count > 0)
            {
                SavePendingBuffer();
            }

            // Stop any ongoing speech
            if (_audioPlayer != null)
            {
                _audioPlayer.StopSpeaking("component-destroyed");
            }

            // Clear conversation history to free memory
            _conversationHistory.Clear();

            // Unsubscribe from events to prevent dangling delegates
            OnResponseStarted = null;
            OnResponseComplete = null;
            OnTypingUpdate = null;
            OnError = null;
            OnSpeechStarted = null;
            OnSpeechComplete = null;

            // Null out references
            _npcEntity = null;
            _config = null;
            _personality = null;
            _lastInteractingPlayer = null;

            _extractionCts?.Dispose();
            _extractionCts = null;
            _extractionInProgress = false;

            Log.Debug(() => $"Cleaned up chat component for NPC: {_npcName} (ID: {_entityId})");
        }

        /// <summary>
        /// Synchronously save unsummarized buffer to disk for processing on next boot.
        /// Called only during Application.isQuitting when async LLM calls would fail.
        /// </summary>
        private void SavePendingBuffer()
        {
            try
            {
                // Resolve player ID: instance → shared static → GameManager
                string playerId = _lastPlayerPersistentId;
                if (string.IsNullOrEmpty(playerId) || playerId == "unknown")
                    playerId = s_lastResolvedPlayerPersistentId;
                if (string.IsNullOrEmpty(playerId) || playerId == "unknown")
                {
                    try
                    {
                        EntityPlayer nearest = FindNearestPlayer();
                        if (nearest != null)
                            playerId = GetPlayerPersistentId(nearest);
                    }
                    catch { /* GameManager access failed during OnDestroy flush */ }
                }
                if (string.IsNullOrEmpty(playerId) || playerId == "unknown")
                    playerId = "unknown";
                string path = NPCMemoryManager.GetPendingFilePath(playerId, _npcName);
                var pendingMessages = new NPCPendingMessage[_unsummarizedBuffer.Count];
                for (int i = 0; i < _unsummarizedBuffer.Count; i++)
                {
                    pendingMessages[i] = new NPCPendingMessage
                    {
                        role = _unsummarizedBuffer[i].Role,
                        content = _unsummarizedBuffer[i].Content
                    };
                }

                var store = new NPCPendingMemoryStore
                {
                    playerName = playerId,
                    npcName = _npcName,
                    messages = pendingMessages
                };

                // XmlSerializer is pure .NET - works on any thread, survives Unity shutdown
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(NPCPendingMemoryStore));
                using (var writer = new StreamWriter(path))
                {
                    serializer.Serialize(writer, store);
                }

                Log.Debug(() => $"[RAG] Saved {pendingMessages.Length} pending messages for NPC {_npcName} (player: {playerId})");
            }
            catch (Exception ex)
            {
                Log.Warning($"[RAG] Failed to save pending buffer: {ex.Message}");
            }
            finally
            {
                _unsummarizedBuffer.Clear();
                _timeSinceLastMessage = 0f;
            }
        }

        /// <summary>
        /// Public method called by NPCMemoryManager during game quit.
        /// Saves the unsummarized buffer synchronously before servers are killed.
        /// </summary>
        public void SavePendingBufferOnQuit()
        {
            if (_unsummarizedBuffer.Count > 0)
                SavePendingBuffer();
        }

        #endregion

        #region Wake Word Sanitization

        /// <summary>
        /// Strip wake word phrases from transcript before sending to LLM.
        /// Phrase triggers still receive the raw text for +1 intent bonus.
        /// This prevents the LLM from thinking the player's name is "Marvin".
        /// </summary>
        /// <summary>
        /// Try to capture the player's given name from their message using strong regex patterns.
        /// Returns true if a name was captured and stored. Case-insensitive, title-cased on store.
        /// Does NOT match "I'm X" / "I am X" (too many false positives).
        /// </summary>
        private static bool TryCapturePlayerName(string text, string playerId, string npcName)
        {
            if (string.IsNullOrEmpty(text) || NPCMemoryManager.Instance == null) return false;

            Match match = s_playerNameRegex.Match(text);
            if (!match.Success) return false;

            // Either group 1 (name is/name's) or group 2 (call me) will have a value
            string rawName = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

            // Filter stoplist words that might match the pattern
            if (s_nameStoplist.Contains(rawName)) return false;

            NPCMemoryManager.Instance.SetGivenName(playerId, npcName, rawName);
            return true;
        }

        private static string StripWakeWords(string rawTranscript)
        {
            if (string.IsNullOrEmpty(rawTranscript)) return rawTranscript;

            // Order matters: strip longer phrases first to avoid partial matches
            string[] wakeWordsToStrip = { "hey marvin", "marvin" };
            string llmInput = rawTranscript;

            foreach (string ww in wakeWordsToStrip)
            {
                llmInput = System.Text.RegularExpressions.Regex.Replace(
                    llmInput, ww, "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            // Clean up leftover punctuation and whitespace
            llmInput = llmInput.Trim(' ', ',', '.', '-', '!', '?');

            return llmInput;
        }

        #endregion
    }
}
