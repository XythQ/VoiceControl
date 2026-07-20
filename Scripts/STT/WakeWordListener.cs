using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace XNPCVoiceControl.STT
{
    /// <summary>
    /// Continuous wake word sentinel. Single-threaded dual-consumer architecture:
    /// one read cursor feeds both the ONNX engine and VoiceInputManager per frame.
    /// </summary>
    public class WakeWordListener : MonoBehaviour
    {
        private static WakeWordListener _instance;
        public static WakeWordListener Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("WakeWordListener");
                    _instance = go.AddComponent<WakeWordListener>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        public event Action<float[], int, int> OnWakeWordDetected;

        /// <summary>
        /// Continuously updated ambient noise floor (RMS) during idle wake-word listening.
        /// VoiceInputManager reads this to seed VAD baseline, skipping the 0.3s calibration phase.
        /// </summary>
        public float CurrentAmbientNoiseFloor { get; private set; }

        private WakeWordRuntime _runtime;
        private AudioClip _loopingClip;
        private string _selectedDevice;
        private int _sampleRate = 16000;
        private bool _isListening = false;
        private float _cooldownTimer = 0f;
        private float _diagTimer = 2f; // Start diagnostic after 2s
        private const float CooldownSeconds = 1f;

        private readonly int _frameSize = 512; // ~32ms at 16kHz

        // --- Pre-allocated buffers (zero GC in Update) ---
        private float[] _chunkBuffer;       // Main read buffer — used during recording
        private float[] _wrapPart1;         // First segment on wrap-around
        private float[] _wrapPart2;         // Second segment on wrap-around

        // --- Idle-mode buffers (larger — reads all accumulated samples per tick) ---
        // At 16kHz × 76ms = ~1216 samples/tick. Oversize to 4096 for frame-drop safety.
        private const int MaxIdleChunkSize = 4096;
        private float[] _idleBuffer;        // Full accumulated chunk (up to MaxIdleChunkSize)
        private float[] _idleWrapPart1;     // First segment on wrap-around
        private float[] _idleWrapPart2;     // Second segment on wrap-around
        private short[] _idlePcmChunk;      // Float->short conversion for ONNX (MaxIdleChunkSize)
        private int _lastReadPos;           // Track read cursor to avoid blind spots between ticks

        // --- Pre-buffer (0.5s of audio before wake word fires) ---
        // Catches first syllables of speech that would otherwise be lost between
        // ONNX detection and VIM subscribing to OnAudioFrameAvailable.
        // Kept short: 1s pre-buffer forced Whisper to process a full second of dead air
        // on every wake-word command, adding noticeable latency.
        private const int PreBufferSize = 8000; // 0.5s at 16kHz (~32KB)
        private float[] _preBuffer;
        private int _preBufferHead;
        private int _preBufferCount;

        // --- Pre-buffer extraction workspace (zero GC) ---
        private float[] _preBufferData; // Flat array handed to VIM on wake word

        // --- Ambient noise floor warm-up ---
        // First ~1.5s of boot: aggressively track true room tone without the 2.5x gate.
        // Prevents initialization trap where floor stays at safety clamp (0.0005) forever.
        private int _warmupTicks = 16; // ~2 seconds at 8 Hz

        // --- Idle RMS estimate (unclamped) ---
        // Running EMA of idle ring-buffer samples, used for pre-buffer trim threshold.
        // Separate from CurrentAmbientNoiseFloor so the safety clamp (0.0005) doesn't
        // anchor the trim threshold too low to distinguish silence from ambient.
        private float _idleRmsEstimate = 0.001f; // seed with reasonable default

        // --- Inference throttle ---
        private float _inferenceTimer = 0f;
        private const float InferenceInterval = 0.25f; // ~4 Hz — matches ONNX processing time, prevents ring buffer overflow

        // --- Background inference thread (offloads ONNX off main thread) ---
        private Thread _inferenceThread;
        private readonly object _inferenceLock = new object();
        private bool _inferenceHasWork = false;
        private readonly ManualResetEvent _inferenceSignal = new ManualResetEvent(false);

        // Detection result from background thread (read on main thread)
        private bool _pendingDetection;

        // --- Zero-GC ring buffer: feeds ONNX exactly 1280 samples per call ---
        // Variable chunk sizes cause dynamic shape re-allocations in ONNX Runtime,
        // spiking inference from ~5ms to 50ms+. Fixed stride = locked graph = stable times.
        private const int RingBufferSize = 32768;   // ~2s of audio at 16kHz
        private const int InferenceChunkSize = 1280; // 80ms — matches openWakeWord ChunkSamples
        private readonly short[] _ringBuffer = new short[RingBufferSize];
        private readonly short[] _fixedInferenceChunk = new short[InferenceChunkSize];
        private int _ringWriteHead;
        private int _ringReadHead;
        private readonly object _ringLock = new object();

        // --- Performance telemetry ---
        private static readonly Stopwatch _inferenceStopwatch = new Stopwatch();
        private long _lastPerfLogTime; // ms, for rate-limited perf warnings

        // --- Inference burst control ---
        private int _maxChunksPerSignal = 4; // cap chunks per signal to prevent CPU pegging



        public AudioClip LoopingClip => _loopingClip;
        public string DeviceName => _selectedDevice;
        public int SampleRate => _sampleRate;
        public int FrameSize => _frameSize;

        /// <summary>
        /// Callback invoked every frame during recording with new audio samples.
        /// VoiceInputManager subscribes to receive fresh float[] chunks.
        /// Signature: (buffer, count) — buffer is pre-allocated and reused each frame.
        /// Subscribers MUST copy samples immediately — do NOT store the reference
        /// or pass it to async/Task code. Contents change on the next frame.
        /// </summary>
        public event Action<float[], int> OnAudioFrameAvailable;

        public bool Initialize(string modelsDir, string deviceName, int sampleRate, WakeWordRuntimeConfig config, string threadPriority = "Normal", int maxChunksPerSignal = 4)
        {
            if (string.IsNullOrEmpty(deviceName))
            {
                Log.Warning("WakeWordListener: no microphone device available");
                return false;
            }

            try
            {
                _selectedDevice = deviceName;
                _sampleRate = sampleRate;

                // Pre-allocate all hot-path buffers once at init time.
                // Recording buffers: sized for variable-length delta reads (up to 4096).
                // Must match MaxIdleChunkSize because both paths use the same _lastReadPos cursor.
                int recordingBufferSize = MaxIdleChunkSize;
                _chunkBuffer = new float[recordingBufferSize];
                _wrapPart1 = new float[recordingBufferSize];
                _wrapPart2 = new float[recordingBufferSize];

                // Idle-mode buffers (larger — reads all accumulated samples per tick)
                _idleBuffer = new float[MaxIdleChunkSize];
                _idleWrapPart1 = new float[MaxIdleChunkSize];
                _idleWrapPart2 = new float[MaxIdleChunkSize];
                _idlePcmChunk = new short[MaxIdleChunkSize];

                // Pre-buffer (1s lookback to catch first syllables of speech)
                _preBuffer = new float[PreBufferSize];
                _preBufferData = new float[PreBufferSize];

                _loopingClip = Microphone.Start(_selectedDevice, true, 10, _sampleRate);
                if (_loopingClip == null)
                {
                    Log.Error("WakeWordListener: failed to start looping microphone");
                    return false;
                }

                _runtime = new WakeWordRuntime(config, modelsDir);
                _isListening = true;
                _maxChunksPerSignal = Math.Max(1, Math.Min(maxChunksPerSignal, 8));

                // Start background inference thread (ONNX never blocks main thread)
                var priority = ParseThreadPriority(threadPriority);
                _inferenceThread = new Thread(InferenceWorkerLoop) { Name = "WW-Inference", IsBackground = true, Priority = priority };
                _inferenceThread.Start();

                Log.Out($"WakeWordListener initialized — device: {_selectedDevice}, model: {config.WakeWords[0].Model}, maxChunksPerSignal: {_maxChunksPerSignal}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"WakeWordListener initialization failed: {ex.Message}");
                Cleanup();
                return false;
            }
        }

        public void Stop()
        {
            _isListening = false;

            // Signal background thread to stop
            if (_inferenceThread != null && _inferenceThread.IsAlive)
            {
                _inferenceSignal.Set(); // Wake it up so it can exit
                try { _inferenceThread.Join(2000); } catch { /* thread already terminated or join timeout */ }
            }

            if (_loopingClip != null && !string.IsNullOrEmpty(_selectedDevice))
            {
                try { Microphone.End(_selectedDevice); } catch { /* microphone already released or device in use */ }
                _loopingClip = null;
            }
            Log.Debug(() => "WakeWordListener: stopped");
        }

        void Update()
        {
            if (!_isListening || _runtime == null || _loopingClip == null)
                return;

            // Decay timers every frame (cheap — just float math, no native calls)
            float dt = Time.deltaTime;
            _inferenceTimer += dt;
            if (_cooldownTimer > 0f) { _cooldownTimer -= dt; if (_cooldownTimer < 0f) _cooldownTimer = 0f; }
            if (_diagTimer > 0f)   { _diagTimer -= dt;   if (_diagTimer < 0f) _diagTimer = 0f; }

            // Check if VIM is subscribed to audio frames (i.e., actively recording).
            bool hasListeners = OnAudioFrameAvailable != null;

            try
            {
                int clipSamples = _loopingClip.samples;
                int writePos = Microphone.GetPosition(_selectedDevice);
                if (writePos <= 0) return; // Mic hasn't started or error

                if (hasListeners)
                {
                    // === RECORDING PATH: every frame, read ALL new samples since last read ===
                    // Uses the same _lastReadPos cursor as idle path for zero-gap continuity.
                    int delta = writePos - _lastReadPos;

                    // Handle Unity's circular mic buffer wrap-around (writePos resets after clip length)
                    if (delta < 0) delta += clipSamples;

                    if (delta <= 0) return; // No new data this frame

                    // Cap to buffer size (safety net for extreme frame drops)
                    if (delta > MaxIdleChunkSize) delta = MaxIdleChunkSize;

                    int readStart = (((_lastReadPos % clipSamples) + clipSamples) % clipSamples);

                    // Read entire accumulated chunk, handling wrap-around
                    if (readStart + delta <= clipSamples)
                    {
                        _loopingClip.GetData(_chunkBuffer, readStart);
                    }
                    else
                    {
                        int firstPart = clipSamples - readStart;
                        int secondPart = delta - firstPart;
                        if (firstPart > MaxIdleChunkSize) firstPart = MaxIdleChunkSize;
                        _loopingClip.GetData(_wrapPart1, readStart);
                        Array.Copy(_wrapPart1, 0, _chunkBuffer, 0, firstPart);
                        if (secondPart > 0)
                        {
                            if (secondPart > MaxIdleChunkSize) secondPart = MaxIdleChunkSize;
                            _loopingClip.GetData(_wrapPart2, 0);
                            Array.Copy(_wrapPart2, 0, _chunkBuffer, firstPart, secondPart);
                        }
                    }

                    // Advance cursor by exactly what we read, not to writePos.
                    // If a lag spike caused delta > buffer size, the remaining samples
                    // will be picked up on the next frame. Zero lost audio.
                    _lastReadPos += delta;

                    // Broadcast to VIM with exact count (zero GC, pre-allocated buffer)
                    OnAudioFrameAvailable?.Invoke(_chunkBuffer, delta);
                }
                else if (_inferenceTimer >= InferenceInterval)
                {
                    // === IDLE PATH: ~13 Hz, read ALL accumulated samples (zero blind spots) ===
                    _inferenceTimer = 0f;

                    int delta = writePos - _lastReadPos;
                    if (delta <= 0) { _lastReadPos = writePos; return; } // First tick or no new data

                    // Cap to buffer size (safety net for extreme frame drops)
                    if (delta > MaxIdleChunkSize) delta = MaxIdleChunkSize;

                    int readStart = (((_lastReadPos % clipSamples) + clipSamples) % clipSamples);

                    // Read entire accumulated chunk, handling wrap-around
                    if (readStart + delta <= clipSamples)
                    {
                        _loopingClip.GetData(_idleBuffer, readStart);
                    }
                    else
                    {
                        int firstPart = clipSamples - readStart;
                        int secondPart = delta - firstPart;
                        // Read into temp buffers then copy (avoids partial writes to idleBuffer)
                        if (firstPart > MaxIdleChunkSize) firstPart = MaxIdleChunkSize;
                        _loopingClip.GetData(_idleWrapPart1, readStart);
                        Array.Copy(_idleWrapPart1, 0, _idleBuffer, 0, firstPart);
                        if (secondPart > 0)
                        {
                            if (secondPart > MaxIdleChunkSize) secondPart = MaxIdleChunkSize;
                            _loopingClip.GetData(_idleWrapPart2, 0);
                            Array.Copy(_idleWrapPart2, 0, _idleBuffer, firstPart, secondPart);
                        }
                    }

                    // Advance cursor by exactly what we read, not to writePos.
                    // If a lag spike caused delta > buffer size, remaining samples
                    // are picked up on the next idle tick. Zero lost audio.
                    _lastReadPos += delta;

                    // Track unclamped idle RMS for pre-buffer trim threshold (EMA α=0.1).
                    // Idle audio is ambient noise — this gives a real room-tone baseline
                    // that isn't anchored to the 0.0005 safety clamp.
                    float idleRms = CalculateRms(_idleBuffer, delta);
                    _idleRmsEstimate = _idleRmsEstimate * 0.9f + idleRms * 0.1f;

                    // Convert to PCM and write into ring buffer (zero main-thread blocking)
                    for (int i = 0; i < delta; i++)
                        _idlePcmChunk[i] = (short)(Mathf.Clamp(_idleBuffer[i], -1f, 1f) * 32767f);

                    lock (_ringLock)
                    {
                        int spaceToEnd = RingBufferSize - _ringWriteHead;
                        if (delta <= spaceToEnd)
                        {
                            Array.Copy(_idlePcmChunk, 0, _ringBuffer, _ringWriteHead, delta);
                        }
                        else
                        {
                            Array.Copy(_idlePcmChunk, 0, _ringBuffer, _ringWriteHead, spaceToEnd);
                            Array.Copy(_idlePcmChunk, spaceToEnd, _ringBuffer, 0, delta - spaceToEnd);
                        }
                        _ringWriteHead = (_ringWriteHead + delta) % RingBufferSize;
                    }

                    lock (_inferenceLock)
                    {
                        _inferenceHasWork = true;
                    }
                    _inferenceSignal.Set();

                    // Write idle samples into pre-buffer ring for wake-word lookback (main thread)
                    WritePreBuffer(_idleBuffer, delta);

                    // Check if background thread detected the wake word this frame
                    lock (_inferenceLock)
                    {
                        if (_pendingDetection && _cooldownTimer <= 0f)
                        {
                            _pendingDetection = false;
                            HandleWakeWordDetected();
                        }
                    }
                }

                // Periodic diagnostic removed — too noisy for debug mode
                if (_diagTimer <= 0f)
                {
                    _diagTimer = 2f;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"WakeWordListener: audio processing exception: {ex.GetType().Name}: {ex.Message}");
                _isListening = false;
            }
        }


        /// <summary>
        /// Background worker thread: drains ring buffer in fixed 1280-sample chunks,
        /// runs ONNX inference off the Unity main thread. Lock closes before ONNX call
        /// so main thread can keep writing new audio without blocking.
        /// </summary>
        private void InferenceWorkerLoop()
        {
            while (_isListening)
            {
                // Wait for work signal (blocks without CPU spin)
                _inferenceSignal.WaitOne();
                _inferenceSignal.Reset();

                lock (_inferenceLock)
                {
                    if (!_inferenceHasWork) continue; // Spurious wakeup or shutdown
                    _inferenceHasWork = false;
                }

                if (_runtime == null) continue;

                // Drain ring buffer in fixed 1280-sample chunks.
                // Lock is held only for the Array.Copy — ONNX runs outside the lock.
                // Cap burst to _maxChunksPerSignal so we don't CPU-peg under contention.
                int chunksThisSignal = 0;
                while (true)
                {
                    // Cap FIRST — don't consume a chunk we won't process.
                    if (chunksThisSignal >= _maxChunksPerSignal)
                        break; // Leave remaining chunks for the next signal.

                    bool hasChunk = false;

                    lock (_ringLock)
                    {
                        int available = _ringWriteHead - _ringReadHead;
                        if (available < 0) available += RingBufferSize;

                        // Drop stale backlog when far behind (>4 chunks = thrashing).
                        // Wake word is in recent audio, not half-second-old backlog.
                        if (available > InferenceChunkSize * 4)
                        {
                            int keep = InferenceChunkSize * 2; // keep only the most recent ~2 chunks
                            _ringReadHead = (_ringWriteHead - keep);
                            if (_ringReadHead < 0) _ringReadHead += RingBufferSize;
                            available = keep;
                        }

                        if (available >= InferenceChunkSize)
                        {
                            hasChunk = true;
                            int spaceToEnd = RingBufferSize - _ringReadHead;

                            if (InferenceChunkSize <= spaceToEnd)
                            {
                                // Clean read without wrapping
                                Array.Copy(_ringBuffer, _ringReadHead, _fixedInferenceChunk, 0, InferenceChunkSize);
                            }
                            else
                            {
                                // Wrap-around read
                                Array.Copy(_ringBuffer, _ringReadHead, _fixedInferenceChunk, 0, spaceToEnd);
                                Array.Copy(_ringBuffer, 0, _fixedInferenceChunk, spaceToEnd, InferenceChunkSize - spaceToEnd);
                            }
                            _ringReadHead = (_ringReadHead + InferenceChunkSize) % RingBufferSize;
                        }
                    } // Lock released — ONNX can run freely

                    if (!hasChunk)
                        break; // Not enough data for a full 1280 chunk, wait for next tick

                    chunksThisSignal++;

                    // --- ONNX inference (runs entirely on background thread, outside lock) ---
                    _inferenceStopwatch.Restart();
                    int result = _runtime.Process(_fixedInferenceChunk, InferenceChunkSize);
                    _inferenceStopwatch.Stop();

                    long elapsedMs = _inferenceStopwatch.ElapsedMilliseconds;
                    // Rate-limited perf warning: only log if >100ms and at most once per second.
                    // With fixed 1280-sample input, normal CPU inference should be ~5-15ms.
                    if (elapsedMs > 100)
                    {
                        long now = DateTime.UtcNow.Ticks / 10000; // ms
                        if (now - _lastPerfLogTime >= 1000)
                        {
                            _lastPerfLogTime = now;
                            UnityEngine.Debug.LogWarning($"[PERF] WakeWord ONNX inference took {elapsedMs}ms. Background thread may be starved.");
                        }
                    }

                    lock (_inferenceLock)
                    {
                        if (result >= 0)
                            _pendingDetection = true;
                    }
                }
            }
        }

        /// <summary>
        /// Handle wake word detection on the Unity main thread (called from Update).
        /// Extracts pre-buffer, trims silence, fires OnWakeWordDetected event.
        /// </summary>
        private void HandleWakeWordDetected()
        {
            Log.Debug(() => $"WakeWordListener: detected '{_runtime.GetType().Name}' wake word");
            _cooldownTimer = CooldownSeconds;
            float[] preData = ExtractPreBuffer();

            // Trim leading silence from pre-buffer so Whisper only sees actual speech.
            // Use unclamped idle RMS estimate (not CurrentAmbientNoiseFloor which is clamped to 0.0005).
            // The clamp floor makes the threshold too low to distinguish silence from ambient.
            // Keep 0.001f as absolute minimum to avoid divide-by-zero on dead mics.
            float trimBaseline = Mathf.Max(_idleRmsEstimate, 0.001f);
            int voiceStartOffset = FindLeadingSpeechFrame(preData, trimBaseline * 1.5f);
            int validCount = preData.Length - voiceStartOffset;

            Log.Debug(() => $"Pre-buffer: {preData.Length} total, trimmed leading silence at offset {voiceStartOffset}, sending {validCount} samples ({(validCount / 16000f):F2}s). Idle RMS: {_idleRmsEstimate:F6}, VAD floor: {CurrentAmbientNoiseFloor:F6}");

            try { OnWakeWordDetected?.Invoke(preData, voiceStartOffset, validCount); } catch { /* don't crash update */ }
        }

        /// <summary>Calculate RMS of a float[] audio chunk.</summary>
        private static float CalculateRms(float[] data)
        {
            double sum = 0;
            for (int i = 0; i < data.Length; i++)
                sum += data[i] * data[i];
            return Mathf.Sqrt((float)(sum / data.Length));
        }

        /// <summary>Calculate RMS of a portion of a float[] audio chunk.</summary>
        private static float CalculateRms(float[] data, int length)
        {
            double sum = 0;
            for (int i = 0; i < length; i++)
                sum += data[i] * data[i];
            return Mathf.Sqrt((float)(sum / length));
        }

        /// <summary>
        /// Parse a config string to ThreadPriority. Unknown/empty values default to Normal.
        /// </summary>
        private static System.Threading.ThreadPriority ParseThreadPriority(string value)
        {
            if (string.IsNullOrEmpty(value))
                return System.Threading.ThreadPriority.Normal;

            switch (value.Trim().ToLower())
            {
                case "abovenormal":  return System.Threading.ThreadPriority.AboveNormal;
                case "belownormal":  return System.Threading.ThreadPriority.BelowNormal;
                case "lowest":       return System.Threading.ThreadPriority.Lowest;
                default:             return System.Threading.ThreadPriority.Normal;
            }
        }

        #region Pre-Buffer Ring

        /// <summary>
        /// Write samples into the circular pre-buffer and update ambient noise floor.
        /// Called every idle tick (~13 Hz) with accumulated audio chunk.
        /// </summary>
        private void WritePreBuffer(float[] chunk, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _preBuffer[_preBufferHead] = chunk[i];
                _preBufferHead = (_preBufferHead + 1) % PreBufferSize;
                if (_preBufferCount < PreBufferSize)
                    _preBufferCount++;
            }

            // Update ambient noise floor with exponential moving average.
            // Warm-up phase: track true room tone for first ~2 seconds,
            // ignoring the 2.5x gate so hardware-agnostic baselines are found at boot.
            // Only count down ticks when we receive actual audio — Unity's mic buffer
            // outputs literal 0.0f silence during hardware init, which would anchor
            // the baseline to zero and trap it forever ("Hardware Zero" trap).
            float chunkRms = CalculateRms(chunk, count);

            if (_warmupTicks > 0)
            {
                // Only accept frames where the hardware has actually started transmitting
                if (chunkRms > 0.0001f)
                {
                    // Slower alpha (0.8/0.2) for smoother convergence to true room tone.
                    // Alpha=0.5 averaged too aggressively downward on quiet frames.
                    CurrentAmbientNoiseFloor = CurrentAmbientNoiseFloor * 0.8f + chunkRms * 0.2f;
                    _warmupTicks--; // Only count down valid audio frames
                }
            }
            else if (chunkRms < CurrentAmbientNoiseFloor * 2.5f)
            {
                // Normal gated EMA: only update on quiet frames, skip speech spikes
                CurrentAmbientNoiseFloor = CurrentAmbientNoiseFloor * 0.95f + chunkRms * 0.05f;
            }

            // Safety clamp to prevent divide-by-zero in downstream VAD math
            CurrentAmbientNoiseFloor = Mathf.Max(CurrentAmbientNoiseFloor, 0.0005f);
        }

        /// <summary>
        /// Linearize the circular pre-buffer into a flat array for VIM.
        /// Returns oldest-first (chronological order). Zero GC — reuses _preBufferData.
        /// </summary>
        private float[] ExtractPreBuffer()
        {
            int count = _preBufferCount;

            if (_preBufferCount >= PreBufferSize)
            {
                // Buffer is full — read from head (oldest) wrapping around
                int firstPart = PreBufferSize - _preBufferHead;
                int secondPart = PreBufferSize - firstPart;

                Array.Copy(_preBuffer, _preBufferHead, _preBufferData, 0, firstPart);
                if (secondPart > 0)
                    Array.Copy(_preBuffer, 0, _preBufferData, firstPart, secondPart);
            }
            else
            {
                // Buffer not yet full — data starts at index 0
                Array.Copy(_preBuffer, 0, _preBufferData, 0, count);
            }

            return _preBufferData;
        }

        /// <summary>
        /// Scan forward through pre-buffer in 512-sample frames. Return the index of the first
        /// frame whose RMS exceeds the threshold (speech-level energy). Returns 0 if no speech found.
        ///
        /// Uses same threshold as VAD (noiseFloor * 1.5) with lookback padding: once speech is
        /// detected, step back one frame (-512 samples / ~32ms) to capture quiet fricatives
        /// (H, S, F) that have low RMS but are part of the first syllable.
        /// </summary>
        private static int FindLeadingSpeechFrame(float[] data, float rmsThreshold)
        {
            const int frameSize = 512;
            for (int i = 0; i + frameSize <= data.Length; i += frameSize)
            {
                double rmsSum = 0;
                for (int j = i; j < i + frameSize; j++)
                    rmsSum += data[j] * data[j];

                float rms = Mathf.Sqrt((float)(rmsSum / frameSize));
                if (rms > rmsThreshold)
                {
                    // Step back two frames (-1024 samples / ~64ms) to capture quiet fricatives
                    // before full voice engagement. Wider padding compensates for the tighter
                    // 2.5x threshold that cuts dead air more aggressively.
                    return Mathf.Max(0, i - frameSize * 2);
                }
            }
            return 0; // No speech-level energy found, keep all data
        }

        #endregion

        public void Cleanup()
        {
            _isListening = false;

            // Signal and wait for background thread to exit
            if (_inferenceThread != null && _inferenceThread.IsAlive)
            {
                _inferenceSignal.Set();
                try { _inferenceThread.Join(2000); } catch { /* thread already terminated or join timeout */ }
            }

            if (_loopingClip != null && !string.IsNullOrEmpty(_selectedDevice))
            {
                try { Microphone.End(_selectedDevice); } catch { /* microphone already released or device in use */ }
                _loopingClip = null;
            }

            _runtime?.Dispose();
            _runtime = null;
            _chunkBuffer = null;
            _wrapPart1 = null;
            _wrapPart2 = null;
            _idleBuffer = null;
            _idleWrapPart1 = null;
            _idleWrapPart2 = null;
            _idlePcmChunk = null;
            _preBuffer = null;
            _preBufferData = null;
            _inferenceSignal?.Dispose();
            _lastReadPos = 0;
            _isListening = false;
        }

        void OnDestroy()
        {
            Cleanup();
            // Singleton survives reloads — Unity null-checks destroyed GOs
        }
    }
}
