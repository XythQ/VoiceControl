using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace XNPCVoiceControl.STT
{
    /// <summary>
    /// Wake word detection engine — adapted from NanoWakeWord (openWakeWord C# port).
    /// Loads ONNX models from disk instead of embedded resources.
    /// Optimized for zero GC allocations in the hot path.
    /// </summary>
    public class WakeWordRuntime : IDisposable
    {
        // Pipeline constants (specific to the openWakeWord ONNX models)
        private const int ChunkSamples = 1280;   // 80 ms at 16kHz
        private const int NumMels = 32;          // Vertical resolution of mel spectrogram
        private const int EmbWindowSize = 76;    // ~775 ms
        private const int EmbStepSize = 8;       // ~80 ms
        private const int EmbFeatures = 96;      // Embedding vector size
        private const int WWFeatures = 16;       // Consecutive embedding vectors for detection window

        private readonly WakeWordRuntimeConfig _settings;
        private readonly InferenceSession _melSession;
        private readonly InferenceSession _embSession;
        private readonly List<InferenceSession> _wwSessions;

        // --- Pre-allocated circular buffers (zero GC) ---
        private readonly float[] _samplesBuffer;
        private int _samplesHead = 0;   // Write position
        private int _samplesCount = 0;  // Number of valid samples

        private readonly float[] _melsBuffer;
        private int _melsCount = 0;     // Number of valid mel features

        // Per-model embedding feature buffers (pre-sized arrays)
        private readonly float[][] _features;
        private readonly int[] _featureCounts;

        // Per-model state tracking
        private readonly int[] _activations;
        private readonly int[] _refractoryCounts;

        // --- Pre-allocated ONNX tensors (reused every inference step) ---
        private readonly DenseTensor<float> _melInputTensor;
        private readonly float[] _melOutputBuffer;

        private readonly string _embInputName;
        private readonly DenseTensor<float> _embInputTensor;
        private readonly float[] _embOutputBuffer;

        private readonly string[] _wwInputNames;
        private readonly DenseTensor<float>[] _wwInputTensors;
        private readonly float[][] _wwOutputBuffers;
        private readonly float[] _wwWindowData; // Scratch buffer for window data

        private readonly int _frameSize;

        // --- Cached reflection (Shape property not visible in Unity Mono) ---
        private static PropertyInfo _denseTensorShapeProp;

        // --- Pre-allocated NamedOnnxValue arrays (zero GC per inference step) ---
        private readonly NamedOnnxValue[] _melInputs;   // length 1, reused every mel frame
        private readonly NamedOnnxValue[] _embInputs;   // length 1, reused every emb frame
        private readonly NamedOnnxValue[][] _wwInputs;  // one per WW model, reused each call

        public WakeWordRuntime(WakeWordRuntimeConfig settings, string modelsDir)
        {
            _settings = settings;
            _frameSize = _settings.StepFrames * ChunkSamples;

            // Load ONNX models from the provided directory
            string melPath = Path.Combine(modelsDir, "melspectrogram.onnx");
            string embPath = Path.Combine(modelsDir, "embedding_model.onnx");

            if (!File.Exists(melPath))
                throw new FileNotFoundException($"Mel spectrogram model not found: {melPath}");
            if (!File.Exists(embPath))
                throw new FileNotFoundException($"Embedding model not found: {embPath}");

            // Explicit CPU execution provider for optimal native performance
            var sessionOptions = new SessionOptions();
            sessionOptions.AppendExecutionProvider_CPU();

            try
            {
                _melSession = new InferenceSession(melPath, sessionOptions);
                _embSession = new InferenceSession(embPath, sessionOptions);

                // Capture input names once at init (avoids .First() LINQ in hot path)
                _embInputName = GetFirstInputName(_embSession);

                _wwSessions = new List<InferenceSession>();
                foreach (var ww in _settings.WakeWords)
                {
                    string modelPath = Path.Combine(modelsDir, $"{ww.Model}.onnx");
                    if (!File.Exists(modelPath))
                        throw new FileNotFoundException($"Wake word model not found: {modelPath}");

                    _wwSessions.Add(new InferenceSession(modelPath, sessionOptions));
                }
            }
            catch (TypeInitializationException)
            {
                throw new InvalidOperationException(
                    $"ONNX Runtime failed to initialize for wake word. " +
                    $"Likely cause: conflicting onnxruntime.dll loaded from System32 or another mod's bin/. " +
                    $"Fix: ensure Plugins/OnnxRuntime/onnxruntime.dll is the correct version; install VC++ 2015-2022 x64 redistributable.",
                    null);
            }
            catch (DllNotFoundException dllex)
            {
                throw new InvalidOperationException(
                    $"ONNX native DLL missing for wake word: {dllex.Message}. " +
                    $"Fix: ensure Plugins/OnnxRuntime/ has onnxruntime.dll, onnxruntime_providers_shared.dll, and vcruntime140.dll.",
                    dllex);
            }

            // --- Pre-allocate all buffers ---
            int maxSamples = _frameSize * 10; // Enough for ~5 seconds of audio at once
            _samplesBuffer = new float[maxSamples];

            // Mel buffer: enough to hold EmbWindowSize frames + some headroom
            _melsBuffer = new float[EmbWindowSize * NumMels * 4];

            // Per-model feature buffers
            int maxFeatures = WWFeatures * EmbFeatures * 4;
            _features = new float[_wwSessions.Count][];
            _featureCounts = new int[_wwSessions.Count];
            for (int i = 0; i < _wwSessions.Count; i++)
                _features[i] = new float[maxFeatures];

            _activations = new int[_wwSessions.Count];
            _refractoryCounts = new int[_wwSessions.Count];

            // --- Pre-allocate ONNX tensors ---
            _melInputTensor = new DenseTensor<float>(new[] { 1, _frameSize });
            _melOutputBuffer = new float[NumMels * EmbWindowSize]; // Max expected output

            _embInputTensor = new DenseTensor<float>(new[] { 1, EmbWindowSize, NumMels, 1 });
            _embOutputBuffer = new float[EmbFeatures];

            _wwInputNames = new string[_wwSessions.Count];
            _wwInputTensors = new DenseTensor<float>[_wwSessions.Count];
            _wwOutputBuffers = new float[_wwSessions.Count][];
            for (int i = 0; i < _wwSessions.Count; i++)
            {
                _wwInputNames[i] = GetFirstInputName(_wwSessions[i]);
                _wwInputTensors[i] = new DenseTensor<float>(new[] { 1, WWFeatures, EmbFeatures });
                _wwOutputBuffers[i] = new float[32]; // Max expected output per model
            }

            _wwWindowData = new float[WWFeatures * EmbFeatures];

            // --- Cache Shape PropertyInfo once (avoids reflection on every tensor read) ---
            _denseTensorShapeProp = typeof(DenseTensor<float>).GetProperty("Shape", BindingFlags.Public | BindingFlags.Instance);

            // --- Pre-allocate NamedOnnxValue arrays (reused every inference step, zero GC) ---
            _melInputs = new NamedOnnxValue[1];
            _melInputs[0] = NamedOnnxValue.CreateFromTensor("input", _melInputTensor);

            _embInputs = new NamedOnnxValue[1];
            _embInputs[0] = NamedOnnxValue.CreateFromTensor(_embInputName, _embInputTensor);

            _wwInputs = new NamedOnnxValue[_wwSessions.Count][];
            for (int i = 0; i < _wwSessions.Count; i++)
                _wwInputs[i] = new[] { NamedOnnxValue.CreateFromTensor(_wwInputNames[i], _wwInputTensors[i]) };
        }

        private static string GetFirstInputName(InferenceSession session)
        {
            foreach (var kvp in session.InputMetadata)
                return kvp.Key;
            throw new InvalidOperationException("No input metadata found");
        }

        /// <summary>
        /// Read a single float from any DenseTensor by flat index, converting to multi-dim coordinates.
        /// Zero allocation — uses cached Shape PropertyInfo and direct indexer access with known shapes.
        /// </summary>
        private static float GetTensorElement(DenseTensor<float> tensor, int flatIndex)
        {
            // Use cached reflection for Shape (avoids per-call PropertyInfo lookup)
            if (_denseTensorShapeProp != null)
            {
                long[] dims = (long[])_denseTensorShapeProp.GetValue(tensor);
                if (dims.Length == 1) return tensor[flatIndex];
                if (dims.Length == 2) return tensor[flatIndex / (int)dims[1], flatIndex % (int)dims[1]];
                if (dims.Length == 3)
                {
                    int d3 = (int)dims[2];
                    int d2 = (int)dims[1];
                    return tensor[flatIndex / (d2 * d3), (flatIndex / d3) % d2, flatIndex % d3];
                }
                if (dims.Length == 4)
                {
                    int d4 = (int)dims[3];
                    int d3 = (int)dims[2];
                    int d2 = (int)dims[1];
                    return tensor[flatIndex / (d2 * d3 * d4), (flatIndex / (d3 * d4)) % d2, (flatIndex / d4) % d3, flatIndex % d4];
                }
            }

            // Fallback: try common shapes directly with indexer
            // Try 1D first
            try { return tensor[flatIndex]; } catch { /* tensor shape mismatch - not 1D */ }
            // Try 2D (common for embedding output [1, N])
            int total = (int)tensor.Length;
            if (total > 0)
            {
                // For 2D tensors with first dim = 1: tensor[0, flatIndex]
                try { return tensor[0, flatIndex]; } catch { /* tensor shape mismatch - not 2D with first dim=1 */ }
            }
            // Last resort: iterate to find element at flatIndex
            int idx = 0;
            foreach (float val in tensor)
            {
                if (idx == flatIndex) return val;
                idx++;
            }
            return 0f;
        }

        /// <summary>
        /// Add a batch of 16-bit PCM audio samples for processing.
        /// buffer is a pre-allocated array; count specifies how many valid elements it contains.
        /// Returns the index of the wake word model that triggered, or -1 if none detected.
        /// </summary>
        public int Process(short[] buffer, int count)
        {
            // Append samples to circular buffer (zero alloc)
            for (int i = 0; i < count; i++)
            {
                if (_samplesCount >= _samplesBuffer.Length)
                {
                    // Compact: shift remaining data to front
                    int keep = _samplesBuffer.Length / 2;
                    Array.Copy(_samplesBuffer, _samplesHead, _samplesBuffer, 0, keep);
                    _samplesHead = 0;
                    _samplesCount = keep;
                }
                _samplesBuffer[(_samplesHead + _samplesCount) % _samplesBuffer.Length] = buffer[i];
                _samplesCount++;
            }

            AudioToMels();
            MelsToFeatures();
            FeaturesToOutput(out var detectedIndex);

            return detectedIndex;
        }

        private void AudioToMels()
        {
            while (_samplesCount >= _frameSize)
            {
                // Copy frame data into pre-allocated tensor (zero alloc)
                for (int i = 0; i < _frameSize; i++)
                    _melInputTensor[0, i] = _samplesBuffer[(_samplesHead + i) % _samplesBuffer.Length];

                // Shift head forward by _frameSize
                _samplesHead = (_samplesHead + _frameSize) % _samplesBuffer.Length;
                _samplesCount -= _frameSize;

                // Tensor data already updated in-place above — reuse cached wrapper (zero GC)
                using (var melResults = _melSession.Run(_melInputs))
                {
                    // Read output into pre-allocated buffer via direct indexer (zero alloc)
                    var outputTensor = melResults[0].Value as DenseTensor<float>;
                    int outLen = Math.Min((int)outputTensor.Length, _melOutputBuffer.Length);
                    for (int i = 0; i < outLen; i++)
                        _melOutputBuffer[i] = (GetTensorElement(outputTensor, i) / 10.0f) + 2.0f;

                    // Append to mel buffer with compaction if needed
                    int needed = _melsCount + outLen;
                    if (needed > _melsBuffer.Length)
                    {
                        // Shift: keep only what we need for next inference window
                        int keepFrom = Math.Max(0, _melsCount - (EmbWindowSize * NumMels));
                        Array.Copy(_melsBuffer, keepFrom, _melsBuffer, 0, _melsCount - keepFrom);
                        _melsCount -= keepFrom;
                    }

                    for (int i = 0; i < outLen; i++)
                        _melsBuffer[_melsCount + i] = _melOutputBuffer[i];
                    _melsCount += outLen;
                }
            }
        }

        private void MelsToFeatures()
        {
            int melFrames = _melsCount / NumMels;

            while (melFrames >= EmbWindowSize)
            {
                // Fill pre-allocated tensor directly from buffer (zero alloc)
                for (int f = 0; f < EmbWindowSize; f++)
                    for (int m = 0; m < NumMels; m++)
                        _embInputTensor[0, f, m, 0] = _melsBuffer[f * NumMels + m];

                // Tensor data already updated in-place above — reuse cached wrapper (zero GC)
                using (var embResults = _embSession.Run(_embInputs))
                {
                    var outputTensor = embResults[0].Value as DenseTensor<float>;
                    int outLen = Math.Min((int)outputTensor.Length, _embOutputBuffer.Length);
                    for (int i = 0; i < outLen; i++)
                        _embOutputBuffer[i] = GetTensorElement(outputTensor, i);

                    // Append to all model feature buffers
                    for (int m = 0; m < _features.Length; m++)
                    {
                        float[] featBuf = _features[m];
                        int count = _featureCounts[m];
                        int needed = count + outLen;
                        if (needed > featBuf.Length)
                        {
                            // Compact: keep only what's needed for next window
                            int keepFrom = Math.Max(0, count - (WWFeatures * EmbFeatures));
                            Array.Copy(featBuf, keepFrom, featBuf, 0, count - keepFrom);
                            count -= keepFrom;
                            _featureCounts[m] = count;
                        }

                        for (int i = 0; i < outLen; i++)
                            featBuf[count + i] = _embOutputBuffer[i];
                        _featureCounts[m] += outLen;
                    }

                    // Shift mel buffer forward by EmbStepSize frames
                    int shiftAmount = EmbStepSize * NumMels;
                    Array.Copy(_melsBuffer, shiftAmount, _melsBuffer, 0, _melsCount - shiftAmount);
                    _melsCount -= shiftAmount;
                }

                melFrames = _melsCount / NumMels;
            }
        }

        private void FeaturesToOutput(out int detectedIndex)
        {
            detectedIndex = -1;

            for (int i = 0; i < _wwSessions.Count; i++)
            {
                float[] featBuf = _features[i];
                int count = _featureCounts[i];
                int numBufferedFeatures = count / EmbFeatures;

                if (_refractoryCounts[i] > 0)
                    _refractoryCounts[i]--;

                while (numBufferedFeatures >= WWFeatures)
                {
                    // Copy window data into pre-allocated scratch buffer (zero alloc)
                    int totalNeeded = WWFeatures * EmbFeatures;
                    for (int j = 0; j < totalNeeded; j++)
                        _wwWindowData[j] = featBuf[j];

                    // Fill pre-allocated tensor from scratch buffer
                    for (int f = 0; f < WWFeatures; f++)
                        for (int e = 0; e < EmbFeatures; e++)
                            _wwInputTensors[i][0, f, e] = _wwWindowData[f * EmbFeatures + e];

                    // Tensor data already updated in-place above — reuse cached wrapper (zero GC)
                    using (var wwResults = _wwSessions[i].Run(_wwInputs[i]))
                    {
                        var outputTensor = wwResults[0].Value as DenseTensor<float>;
                        int outLen = Math.Min((int)outputTensor.Length, _wwOutputBuffers[i].Length);
                        for (int j = 0; j < outLen; j++)
                            _wwOutputBuffers[i][j] = GetTensorElement(outputTensor, j);

                        float threshold = _settings.WakeWords[i].Threshold;
                        string model = _settings.WakeWords[i].Model;

                        for (int j = 0; j < outLen; j++)
                        {
                            float probability = _wwOutputBuffers[i][j];
                            _settings.DebugAction?.Invoke(model, probability, false);

                            if (_refractoryCounts[i] > 0)
                                continue;

                            if (probability > threshold)
                            {
                                _activations[i]++;
                                if (_activations[i] >= _settings.WakeWords[i].TriggerLevel)
                                {
                                    _settings.DebugAction?.Invoke(model, probability, true);
                                    detectedIndex = i;
                                    _activations[i] = 0;
                                    _refractoryCounts[i] = _settings.WakeWords[i].Refractory;
                                }
                            }
                            else
                            {
                                if (_activations[i] > 0)
                                    _activations[i] = Math.Max(0, _activations[i] - 1);
                                else
                                    _activations[i] = Math.Min(0, _activations[i] + 1);
                            }
                        }
                    }

                    // Shift feature buffer forward by one embedding vector
                    int shiftAmount = EmbFeatures;
                    Array.Copy(featBuf, shiftAmount, featBuf, 0, count - shiftAmount);
                    count -= shiftAmount;
                    _featureCounts[i] = count;

                    numBufferedFeatures = count / EmbFeatures;
                }
            }
        }

        public void Dispose()
        {
            _melSession?.Dispose();
            _embSession?.Dispose();
            foreach (var session in _wwSessions)
                session.Dispose();
        }
    }

    /// <summary>
    /// Configuration for a single wake word model.
    /// </summary>
    public class WakeWordConfig
    {
        /// <summary>Model name without extension, e.g. "hey_marvin_v0.1"</summary>
        public string Model { get; set; }

        /// <summary>Minimum probability to count as a potential detection</summary>
        public float Threshold { get; set; } = 0.5f;

        /// <summary>Consecutive threshold crossings needed before official detection</summary>
        public int TriggerLevel { get; set; } = 4;

        /// <summary>Cool-down frames after a detection before allowing another</summary>
        public int Refractory { get; set; } = 20;
    }

    /// <summary>
    /// Configuration for the wake word runtime pipeline.
    /// </summary>
    public class WakeWordRuntimeConfig
    {
        public WakeWordConfig[] WakeWords { get; set; }

        /// <summary>How many ChunkSamples (1280) to process per inference step</summary>
        public int StepFrames { get; set; } = 4;

        /// <summary>Optional debug callback: (modelName, probability, detected)</summary>
        public Action<string, float, bool> DebugAction { get; set; }
    }
}
