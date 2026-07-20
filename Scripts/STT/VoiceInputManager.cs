using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace XNPCVoiceControl.STT
{
    /// <summary>
    /// Manages voice recording with push-to-talk and wake-word triggering.
    /// In wake-word mode, receives audio frames via WakeWordListener's OnAudioFrameAvailable event.
    /// Runs VAD (voice activity detection) to auto-stop on silence.
    /// </summary>
    public class VoiceInputManager : MonoBehaviour
    {
        private static VoiceInputManager _instance;
        public static VoiceInputManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("VoiceInputManager");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<VoiceInputManager>();
                }
                return _instance;
            }
        }

        private int _isRecordingFlag = 0;
        public bool IsRecording => Interlocked.CompareExchange(ref _isRecordingFlag, 0, 0) == 1;

        /// <summary>
        /// Lock to prevent concurrent STT requests. Set when VAD stops recording,
        /// cleared by the async pipeline in all end states (success/empty/error).
        /// Volatile: read from main thread, written from both main and background STT callback threads.
        /// </summary>
        private volatile bool _isProcessingVoice;

        // Recording state
        private readonly List<float> _recordedSamples = new();
        private float _recordingStartTime;
        private bool _wakeWordTriggered;

        // Cooldown after "no speech" result to prevent rapid re-triggering loop.
        // When wake word fires → VAD cuts on silence → Whisper returns empty → immediate unlock →
        // wake word fires again. This 1-second cooldown breaks the cycle.
        private float _noSpeechCooldownTimer = 0f;
        private const float NoSpeechCooldownSec = 1f;

        // VAD (Voice Activity Detection) — adaptive noise floor + silence timeout
        private float _vadNoiseFloorRms = 0f;
        private bool _vadBaselineCaptured = false;
        private float _vadLastVoiceTime = 0f;
        private const float VadBaselineWindowSec = 0.3f;     // Noise floor measurement window
        private const float VadSilenceTimeoutDefault = 0.8f;   // Trailing silence before cutoff (sweet spot: natural pauses without dead-air latency)
        private const float VadMinRecordingTime = 0.5f;       // Minimum recording duration (short commands like "Drop it" need less)
        private const float VadMaxInitialSilenceSec = 2.0f;   // Abort if no voice within this time after baseline

        // Config references
        private STTConfig _sttConfig;
        private KeyCode _pushToTalkKey = KeyCode.V;
        private WakeWordListener _wakeWordListener;

        /// <summary>
        /// Adaptive threshold: voice must exceed this RMS to count as active speech.
        /// </summary>
        private float VadAdaptiveThreshold => _vadNoiseFloorRms * 2.5f;

        /// <summary>
        /// Silence timeout in seconds — read from config, defaults to 0.3s (aggressive dead-air cutoff).
        /// </summary>
        private float VadSilenceTimeoutSec => _sttConfig != null ? _sttConfig.VadSilenceMs / 1000f : VadSilenceTimeoutDefault;

        public event Action<string> OnTranscriptionComplete;
        public event Action<string> OnTranscriptionError;
        public event Action OnRecordingStarted;
        public event Action OnRecordingStopped;

        public void Initialize(STTConfig config, WakeWordListener wakeWordListener, MicrophoneCapture micCapture)
        {
            _sttConfig = config;
            _wakeWordListener = wakeWordListener;

            if (!string.IsNullOrEmpty(config.PushToTalkKey))
            {
                if (Enum.TryParse<KeyCode>(config.PushToTalkKey, true, out KeyCode key))
                    _pushToTalkKey = key;
            }

            if (_wakeWordListener != null)
            {
                _wakeWordListener.OnWakeWordDetected += OnWakeWordTriggered;
            }

            Log.Out($"VoiceInputManager initialized — push-to-talk: {_pushToTalkKey}, wake word: {(_wakeWordListener != null ? "enabled" : "disabled")}");
        }

        void Update()
        {
            // Decay no-speech cooldown
            if (_noSpeechCooldownTimer > 0f)
                _noSpeechCooldownTimer -= Time.deltaTime;

            ProcessKeyInput();
            UpdateVad();
        }

        public bool ShouldProcessInput()
        {
            var localPlayer = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (localPlayer == null) return false;
            if (GameManager.Instance.IsPaused()) return false;

            var windowManager = LocalPlayerUI.GetUIForPlayer(localPlayer)?.windowManager;
            if (windowManager != null && windowManager.IsModalWindowOpen()) return false;

            return true;
        }

        public void ProcessKeyInput()
        {
            if (_sttConfig == null || !_sttConfig.Enabled) return;

            try
            {
                if (IsRecording)
                {
                    if (Input.GetKeyUp(_pushToTalkKey))
                        StopRecordingAndTranscribe();
                }
                else
                {
                    if (Input.GetKeyDown(_pushToTalkKey) && ShouldProcessInput())
                        StartRecording(wakeWordTriggered: false);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"VoiceInputManager: ProcessKeyInput exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Called every frame to check VAD conditions during wake-word recording.
        /// Auto-stops on silence after minimum recording time.
        /// </summary>
        public void UpdateVad()
        {
            try
            {
                if (!IsRecording || !_wakeWordTriggered) return;

                float elapsed = Time.time - _recordingStartTime;

                // Capture noise floor in first VadBaselineWindowSec seconds
                if (!_vadBaselineCaptured && elapsed >= VadBaselineWindowSec)
                {
                    // Use RMS of earliest samples as baseline
                    double sum = 0;
                    int count = Mathf.Min(_recordedSamples.Count, (int)(VadBaselineWindowSec * _sttConfig.SampleRate));
                    for (int i = 0; i < count; i++)
                        sum += _recordedSamples[i] * _recordedSamples[i];

                    if (count > 0)
                    {
                        _vadNoiseFloorRms = Mathf.Sqrt((float)(sum / count));
                    }

                    _vadBaselineCaptured = true;
                }

                // Skip VAD until baseline is captured and minimum time elapsed
                if (!_vadBaselineCaptured || elapsed < VadMinRecordingTime) return;

                // Calculate RMS of latest 512 samples
                int frameSize = 512;
                if (_recordedSamples.Count >= frameSize)
                {
                    double rmsSum = 0;
                    for (int i = _recordedSamples.Count - frameSize; i < _recordedSamples.Count; i++)
                        rmsSum += _recordedSamples[i] * _recordedSamples[i];

                    float rms = Mathf.Sqrt((float)(rmsSum / frameSize));

                    // Update last voice time when RMS exceeds adaptive threshold
                    if (rms > VadAdaptiveThreshold)
                        _vadLastVoiceTime = Time.time;

                    float ratio = _vadNoiseFloorRms > 0.0001f ? rms / _vadNoiseFloorRms : 0f;

                    // Log diagnostics every ~0.5s or on high RMS spikes
                    if (Mathf.Abs(elapsed % 0.5f) < Time.deltaTime + 0.02f || ratio >= 1.8f)
                    {
                        // VAD per-frame diagnostics removed — too noisy for debug mode
                    }

                    // Stop on silence after minimum recording time
                    if (_vadLastVoiceTime > 0f && (Time.time - _vadLastVoiceTime) >= VadSilenceTimeoutSec)
                    {
                        StopRecordingAndTranscribe();
                        return;
                    }

                    // Abort on excessive leading silence: if no voice detected after a few seconds
                    // of recording (past baseline + minimum time), stop early to avoid sending
                    // dead air to STT. Only applies BEFORE voice is first detected.
                    float initialSilenceDeadline = VadBaselineWindowSec + VadMaxInitialSilenceSec;
                    if (_vadLastVoiceTime == 0f && elapsed >= initialSilenceDeadline)
                    {
                        StopRecordingAndTranscribe();
                        return;
                    }

                    // Hard timeout cap (safety net to prevent runaway recordings)
                    float maxSec = Mathf.Min(_sttConfig.MaxRecordingSeconds, 10f);
                    if (elapsed >= maxSec)
                    {
                        StopRecordingAndTranscribe();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"VoiceInputManager: UpdateVad exception: {ex.Message}");
            }
        }

        #region Wake Word Trigger

        /// <summary>
        /// Called by WakeWordListener when wake word is detected.
        /// Receives pre-buffered audio as (buffer, offset, count) to catch first syllables of speech.
        /// </summary>
        private void OnWakeWordTriggered(float[] preData, int offset, int count)
        {
            if (IsRecording)
            {
                Log.Debug(() => "VoiceInputManager: wake word ignored (already recording)");
                return;
            }

            if (_isProcessingVoice)
            {
                Log.Debug(() => "VoiceInputManager: wake word ignored (still processing previous voice input)");
                return;
            }

            if (_noSpeechCooldownTimer > 0f)
            {
                Log.Debug(() => $"VoiceInputManager: wake word ignored (no-speech cooldown active, {_noSpeechCooldownTimer:F1}s remaining)");
                return;
            }

            // Clear any stale data from previous recording (safety net if StopRecordingAndTranscribe was bypassed).
            _recordedSamples.Clear();

            // Seed recording with pre-buffered audio BEFORE subscribing to live frames.
            // Uses buffer/offset/count pattern — only valid samples, no leading silence.
            if (preData != null && count > 0)
            {
                for (int i = offset; i < offset + count; i++)
                    _recordedSamples.Add(preData[i]);
                Log.Debug(() => $"VoiceInputManager: seeded recording with {count} pre-buffer samples (offset={offset})");
            }

            StartRecording(wakeWordTriggered: true);
        }

        #endregion

        #region Recording Lifecycle

        private void StartRecording(bool wakeWordTriggered)
        {
            if (Interlocked.CompareExchange(ref _isRecordingFlag, 1, 0) != 0)
            {
                Log.Debug(() => "VoiceInputManager: recording already in progress, ignoring trigger");
                return;
            }

            _wakeWordTriggered = wakeWordTriggered;
            _recordingStartTime = Time.time;
            _vadLastVoiceTime = 0f;
            _vadNoiseFloorRms = 0f;
            _vadBaselineCaptured = false;

            // Wake-word recordings with pre-buffer: skip VAD calibration phase.
            // Use ambient noise floor that WakeWordListener calculated during idle listening.
            if (_wakeWordTriggered && _wakeWordListener != null)
            {
                float ambientFloor = _wakeWordListener.CurrentAmbientNoiseFloor;
                if (ambientFloor > 0.0001f)
                {
                    _vadNoiseFloorRms = ambientFloor;
                    _vadBaselineCaptured = true;
                    Log.Debug(() => $"VoiceInputManager: VAD baseline seeded from idle noise floor RMS={ambientFloor:F6}");
                }
            }

            if (_wakeWordListener != null && _wakeWordListener.LoopingClip != null)
            {
                // This works for BOTH wake-word and PTT modes, avoiding device conflicts
                // (Unity Mono only allows one mic session per device).
                _wakeWordListener.OnAudioFrameAvailable += OnAudioFrameReceived;
                Log.Debug(() => $"VoiceInputManager: recording started ({(_wakeWordTriggered ? "wake-word" : "push-to-talk")} mode, event-driven)");
            }
            else
            {
                // WakeWordListener not active — use MicrophoneCapture for direct PTT.
                try { MicrophoneCapture.Instance.StartRecording(); } catch { /* don't crash */ }
                Log.Debug(() => "VoiceInputManager: recording started (push-to-talk mode, direct capture)");
            }

            OnRecordingStarted?.Invoke();
        }

        public void StopRecordingAndTranscribe()
        {
            if (Interlocked.CompareExchange(ref _isRecordingFlag, 0, 1) != 1)
                return;

            // Lock: prevent new wake words / VAD triggers while we process this audio
            _isProcessingVoice = true;

            float elapsed = Time.time - _recordingStartTime;
            if (_wakeWordListener != null)
                Log.Debug(() => $"VoiceInputManager: stopping recording after {elapsed:F1}s ({_recordedSamples.Count} samples, noiseFloor={_vadNoiseFloorRms:F6}, adaptiveThreshold={VadAdaptiveThreshold:F6})");
            else
                Log.Debug(() => $"VoiceInputManager: stopping recording after {elapsed:F1}s (push-to-talk)");

            if (_wakeWordListener != null && _wakeWordListener.LoopingClip != null)
            {
                // Event-driven mode (wake-word or PTT via WakeWordListener looping clip).
                _wakeWordListener.OnAudioFrameAvailable -= OnAudioFrameReceived;

                if (_recordedSamples.Count >= 512)
                    TranscribeAccumulatedSamples();
                else
                {
                    Log.Warning($"VoiceInputManager: too few samples ({_recordedSamples.Count}) to transcribe, aborting");
                    _isProcessingVoice = false;
                    Log.Debug(() => "Voice pipeline complete (too few samples). Microphone unlocked.");
                }
            }
            else
            {
                // Direct capture mode (PTT via MicrophoneCapture).
                try { MicrophoneCapture.Instance.StopRecordingAndTranscribe(); } catch { /* don't crash */ }
            }

            // Reset VAD state for next recording
            _vadNoiseFloorRms = 0f;
            _vadBaselineCaptured = false;
            _vadLastVoiceTime = 0f;

            _recordedSamples.Clear();
            OnRecordingStopped?.Invoke();
        }

        /// <summary>
        /// Called by WakeWordListener every Update() frame during recording.
        /// Appends new audio samples to our recording buffer for VAD analysis.
        /// Receives (buffer, count) — only reads the exact valid sample range.
        /// </summary>
        private void OnAudioFrameReceived(float[] chunk, int count)
        {
            if (chunk == null || count <= 0) return;

            // Append exactly 'count' samples from this frame (zero GC, no array allocation)
            for (int i = 0; i < count; i++)
                _recordedSamples.Add(chunk[i]);
        }

        #endregion

        #region Transcription

        private void TranscribeAccumulatedSamples()
        {
            if (_recordedSamples.Count < 512)
            {
                Log.Warning("VoiceInputManager: too few samples to transcribe, aborting");
                _isProcessingVoice = false;
                Log.Debug(() => "Voice pipeline complete (too few samples). Microphone unlocked.");
                return;
            }

            // Trim trailing silence for faster STT.
            // Scan backwards in 512-sample frames, calculate RMS per frame.
            // A single transient sample (keyboard click, room tone) can exceed 0.01,
            // so we require an entire frame's RMS to exceed the VAD adaptive threshold
            // before considering it "voice" and stopping the trim.
            int lastVoiceIdx = _recordedSamples.Count - 1;
            const int trimFrameSize = 512;
            float trimThreshold = VadAdaptiveThreshold; // Reuse VAD's noise-floor-based threshold

            for (int frameEnd = _recordedSamples.Count - trimFrameSize;
                 frameEnd >= 512 * 2; // Don't trim below ~1s minimum
                 frameEnd -= trimFrameSize)
            {
                double rmsSum = 0;
                int frameStart = frameEnd + 1;
                for (int j = frameStart; j < frameStart + trimFrameSize && j < _recordedSamples.Count; j++)
                    rmsSum += _recordedSamples[j] * _recordedSamples[j];

                float rms = Mathf.Sqrt((float)(rmsSum / trimFrameSize));
                if (rms > trimThreshold)
                {
                    // This frame has speech-level energy — keep everything from here.
                    // Add +1024 samples (~64ms) to preserve trailing breath of last word,
                    // compensating for the tighter 2.5x threshold that cuts dead air more aggressively.
                    lastVoiceIdx = frameEnd + trimFrameSize + 1024;
                    break;
                }
            }

            int trimCount = _recordedSamples.Count - lastVoiceIdx;
            if (trimCount > 0)
            {
                _recordedSamples.RemoveRange(lastVoiceIdx, trimCount);
                Log.Debug(() => $"VoiceInputManager: trimmed {trimCount} trailing silence samples");
            }

            int sampleRate = WakeWordListener.Instance?.SampleRate ?? _sttConfig.SampleRate;
            float[] samples = _recordedSamples.ToArray();
            byte[] wavData = MicrophoneCapture.ConvertFloatSamplesToWav(samples, sampleRate);

            if (wavData == null || wavData.Length < 100)
            {
                Log.Warning($"VoiceInputManager: failed to convert audio to WAV ({_recordedSamples.Count} samples)");
                _isProcessingVoice = false;
                Log.Debug(() => "Voice pipeline complete (WAV conversion failed). Microphone unlocked.");
                return;
            }

            float durationSec = samples.Length / (float)sampleRate;

            // Tripwire: diagnose false triggers vs VAD guillotine.
            // Large sample count + garbage text = ONNX false positive (room noise misidentified as wake word).
            // Tiny sample count + short text = VAD cutting mid-sentence (noise floor too high).
            Log.Debug(() => $"Sending {_recordedSamples.Count} samples ({durationSec:F2}s, {wavData.Length} bytes) to Whisper. Baseline RMS: {_vadNoiseFloorRms:F6}");

            // Build effective prompt: static config prompt + currently-hired NPC personality names.
            // Biasing whisper toward the actual names prevents "Nightingale" → "night and gale" etc.
            string effectivePrompt = BuildEffectiveSTTPrompt();

            STTService.Instance.Transcribe(
                wavData,
                effectivePrompt,
                text =>
                {
                    if (!string.IsNullOrEmpty(text))
                    {
                        // Sanitize transcript: strip caption artifacts and hallucinated phrases
                        string clean = STTService.SanitizeTranscript(text);
                        Log.Debug(() => $"[TIMING] STT transcribed wake-word audio in {STTService.Instance.LastTranscriptionTimeMs}ms: \"{clean}\"");
                        OnTranscriptionComplete?.Invoke(clean);
                    }
                    else
                    {
                        // "No speech detected" is normal (false trigger + silence), not an error.
                        Log.Debug(() => $"VoiceInputManager: STT returned no text for {durationSec:F1}s of audio");
                        OnTranscriptionError?.Invoke("No speech detected");
                    }
                    // Unlock microphone — pipeline has received the transcription result
                    _isProcessingVoice = false;
                    Log.Debug(() => "Voice pipeline complete. Microphone unlocked.");
                },
                error =>
                {
                    // Distinguish real errors (connection failures, timeouts) from expected empty results.
                    if (error == "No speech detected")
                    {
                        Log.Debug(() => $"VoiceInputManager: STT returned no text for {durationSec:F1}s of audio");
                        _noSpeechCooldownTimer = NoSpeechCooldownSec; // Prevent immediate re-trigger
                    }
                    else
                    {
                        Log.Error($"VoiceInputManager: STT error: {error}");
                    }
                    OnTranscriptionError?.Invoke(error);
                    // Unlock microphone on error too
                    _isProcessingVoice = false;
                    Log.Debug(() => "Voice pipeline complete (STT error). Microphone unlocked.");
                }
            );
        }

        /// <summary>
        /// Build the effective whisper steering prompt: static config vocabulary + currently-hired NPC personality names.
        /// Keeps STTService game-state-free — VoiceInputManager is the bridge that knows about hired NPCs.
        /// </summary>
        private string BuildEffectiveSTTPrompt()
        {
            string basePrompt = _sttConfig?.Prompt ?? "";

            // Gather personality names of NPCs hired by the local player.
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null)
                return basePrompt; // No player — static prompt only

            var world = GameManager.Instance?.World;
            if (world == null)
                return basePrompt;

            var names = new List<string>();
            try
            {
                foreach (var entity in world.Entities.list)
                {
                    if (!(entity is EntityAlive alive))
                        continue;
                    if (!XNPCVoiceControl.Core.ChatComponentManager.IsChatTarget(alive))
                        continue;

                    Entity leader = null;
                    try { leader = EntityUtilities.GetLeaderOrOwner(alive.entityId); } catch { continue; }
                    if (leader == null || leader.entityId != player.entityId)
                        continue;

                    // Get personality name from chat component (the name the player speaks).
                    var comp = XNPCVoiceControl.Core.ChatComponentManager.TryGet(alive.entityId, out var chatComp) ? chatComp : null;
                    if (comp != null && comp.NPCName != null && comp.NPCName != "Survivor")
                    {
                        names.Add(comp.NPCName);
                    }
                }
            }
            catch
            {
                // World Entities.list may be null during load/unload — fall back to static prompt.
            }

            if (names.Count == 0)
                return basePrompt;

            return basePrompt + ", " + string.Join(", ", names);
        }

        #endregion

        public void Cleanup()
        {
            if (_wakeWordListener != null)
            {
                _wakeWordListener.OnWakeWordDetected -= OnWakeWordTriggered;
                _wakeWordListener.OnAudioFrameAvailable -= OnAudioFrameReceived;
            }
        }

        /// <summary>
        /// Call this from the async pipeline when voice processing is complete (success, empty, or error).
        /// Unlocks the microphone so new wake words / VAD triggers are accepted again.
        /// </summary>
        public void MarkProcessingComplete()
        {
            bool wasProcessing = _isProcessingVoice;
            _isProcessingVoice = false;
            if (wasProcessing)
                Log.Debug(() => "Voice pipeline complete. Microphone unlocked.");
        }

        void OnDestroy() => Cleanup();
    }
}
