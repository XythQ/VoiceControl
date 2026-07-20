namespace XNPCVoiceControl.STT
{
    /// <summary>
    /// STT configuration loaded from sttconfig.xml.
    /// HTTP-based — sends audio to whisper-server for transcription.
    /// </summary>
    public class STTConfig
    {
        // General
        public bool Enabled = true;

        // Server
        public string ServerUrl = "http://127.0.0.1:5052";
        public int TimeoutSeconds = 60;
        public int BeamSize = 3;              // 1 (fastest) to 5 (best quality)
        public bool LanguageLocked = true;    // Legacy: true = English only, false = auto-detect. Superseded by Language.
        public string Language = "en";        // ISO 639-1 code ("en", "ja", "zh", "fr", "de"). Use "auto" for detection (less reliable on short clips).
        public string Model = "";             // Whisper model filename (e.g. "ggml-small.bin"). Empty = autodetect.
        public bool UseGpu = false;           // true = run on GPU (Vulkan/OpenCL), false = CPU only
        public bool Translate = false;        // true = translate to English, false = raw transcription in source language
        public string Prompt = "Open inventory, use knife, swap weapon, reload, status, drop item, north, south, east, west, northeast, northwest, southeast, southwest, socket, formation, flank, take point, hold position, squad, spread out, tighten up, come closer, back off";

        // Audio
        public int SampleRate = 16000;
        public int MaxRecordingSeconds = 15;

        // Input
        public string PushToTalkKey = "V";
        public string MicrophoneDevice = "";  // Empty = use devices[0]. Set to device name for explicit selection.

        // Wake Word
        public bool WakeWordEnabled = false;
        public string WakeWordModel = "hey_marvin_v0.1";
        public float WakeWordThreshold = 0.5f;
        public int VadSilenceMs = 800;
        public string WakeWordThreadPriority = "Normal"; // AboveNormal, Normal, BelowNormal, Lowest
        public int WakeWordMaxChunksPerSignal = 4;       // Max ONNX chunks per signal burst (1-8)
        public string WhisperGpuDevice = "";             // GPU device index for whisper-server. Empty = default (device 0).
        public float VadRmsThreshold = 0.008f; // RMS energy threshold for VAD (looping clip samples are quieter)

        /// <summary>
        /// Load configuration from XML file.
        /// </summary>
    }
}
