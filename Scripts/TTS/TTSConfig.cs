namespace XNPCVoiceControl.TTS
{
    /// <summary>
    /// Configuration for the HTTP-based TTS system.
    /// Sends text to an external TTS server and receives WAV audio back.
    /// </summary>
    public class TTSConfig
    {
        public bool Enabled { get; set; } = true;

        // TTS engine selection: "sherpa" (Kokoro, port 5053) or "supertonic" (Supertonic 3, port 5054).
        // Only the selected engine is started by ServerManager.
        public string TtsEngine { get; set; } = "sherpa";

        // TTS server endpoint. If empty or matching the engine default, derived from TtsEngine:
        //   sherpa     → http://127.0.0.1:5053
        //   supertonic → http://127.0.0.1:5054
        public string ServerUrl { get; set; } = "http://127.0.0.1:5053";

        // Audio settings
        public float Volume { get; set; } = 0.8f;
        public float MaxDistance { get; set; } = 20f;
        public float MinDistance { get; set; } = 2f;
        public float SpeechRate { get; set; } = 1.0f;

        // Voice settings (voice names supported by the TTS server)
        public string DefaultVoice { get; set; } = "af_aoede";
        public string TraderVoice { get; set; } = "am_adam";
        public string CompanionVoice { get; set; } = "af_sarah";
        public string BanditVoice { get; set; } = "am_eric";

        // HTTP timeout in seconds
        public int TimeoutSeconds { get; set; } = 30;

        // Sherpa-ONNX uses CPU by default. GPU support available via CoreML (macOS) or future DirectML.
        public bool UseGpu { get; set; } = false;

        // Face lip-sync — drive blendshapes from TTS audio amplitude + random blink.
        public bool FaceLipSyncEnabled { get; set; } = true;
        public float FaceLipSyncGain { get; set; } = 100f;
        public float FaceLipSyncAttack { get; set; } = 35f;
        public float FaceLipSyncRelease { get; set; } = 12f;
        public float FaceLipSyncNoiseGate { get; set; } = 0.004f;
        public float FaceLipSyncMaxWeight { get; set; } = 100f;
        public bool FaceLipSyncBlinkEnabled { get; set; } = true;
        public float FaceLipSyncBlinkIntervalMin { get; set; } = 2.0f;
        public float FaceLipSyncBlinkIntervalMax { get; set; } = 6.0f;
        public int FaceLipSyncBlinkDurationMs { get; set; } = 120;

        // Mode: auto | off | blendshape | animator | procedural
        public string FaceLipSyncMode { get; set; } = "auto";
        // Animator Float parameter name (default "IsTalking")
        public string FaceLipSyncAnimParam { get; set; } = "IsTalking";

        // Procedural jaw (tier 3) — runtime blendshape from head-weighted chin verts.
        // Position knobs are fractions of the head-local bbox range (0-1), mapped at bake time:
        //   abs = bboxMin[axis] + frac * (bboxMax[axis] - bboxMin[axis])
        public float FaceLipSyncProcOpenAngle { get; set; } = 6f;
        public float FaceLipSyncProcLowerMaxFrac { get; set; } = 0.4f;
        public float FaceLipSyncProcForwardMinFrac { get; set; } = 0.61f;
        public float FaceLipSyncProcHingeYFrac { get; set; } = 0.21f;
        public float FaceLipSyncProcHingeZFrac { get; set; } = 0.36f;
        public bool FaceLipSyncProcTestHold { get; set; } = false;

        // Procedural blink/wink (tier 3 fallback) — runtime blendshape from head-weighted eye verts.
        // Baked in the same pass as ProcJawOpen (unified Instantiate + multiple AddBlendShapeFrame).
        public float FaceLipSyncProcBlinkEyeYFrac { get; set; } = 0.31f;
        public float FaceLipSyncProcBlinkBandHeightFrac { get; set; } = 0.05f;
        public float FaceLipSyncProcBlinkBandWidthFrac { get; set; } = 0.1f;
        public float FaceLipSyncProcBlinkCloseAmount { get; set; } = 0.95f;
        public float FaceLipSyncProcBlinkForwardMinFrac { get; set; } = 0.7f;
        public string FaceLipSyncProcBlinkWinkMode { get; set; } = "off";
        public float FaceLipSyncProcBlinkWinkChance { get; set; } = 0.2f;
    }
}
