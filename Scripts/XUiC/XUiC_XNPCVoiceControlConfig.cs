using System;
using System.Collections.Generic;
using XNPCVoiceControl;
using XNPCVoiceControl.Core;
using XNPCVoiceControl.STT;
using XNPCVoiceControl.TTS;
using XNPCVoiceControl.UI;
using UnityEngine;

// XUI controllers must be in the global namespace for 7DTD to find them
/// <summary>
/// Controller for the XNPCVoiceControl configuration window.
/// Manages UI controls and persists settings to player buffs.
/// </summary>
public class XUiC_XNPCVoiceControlConfig : XUiController
    {
        // UI Controls
        private XUiC_SimpleButton btnClose;
        private XUiC_SimpleButton btnSave;
        private XUiC_SimpleButton btnCancel;
        private XUiC_SimpleButton btnTestTTS;
        private XUiC_SimpleButton btnTestSTT;
        private XUiC_SimpleButton btnTestLLM;
        private XUiC_SimpleButton btnClearConversations;

        private XUiC_ToggleButton toggleTTS;
        private XUiC_ToggleButton toggleSTT;
        private XUiC_ToggleButton toggleWakeWord;
        private XUiC_ToggleButton toggleDebug;
        private XUiC_SimpleButton btnSpeechModel;
        private bool _speechModelAccurate = true;

        private XUiC_ComboBoxFloat sliderVolume;
        private XUiC_ComboBoxFloat sliderSpeechRate;
        private XUiC_ComboBoxFloat sliderMaxHistory;
        private XUiC_ComboBoxFloat sliderChatDistance;
        private XUiC_ComboBoxFloat sliderHiredChatDistance;
        private XUiC_ComboBoxFloat sliderChatTimeout;
        private XUiC_ComboBoxFloat sliderVoiceDistance;
        private XUiC_ComboBoxFloat sliderMaxTokens;
        private XUiC_ComboBoxFloat sliderBeamSize;
        // Default voice radio buttons
        private XUiC_ToggleButton radioDefaultHeart;
        private XUiC_ToggleButton radioDefaultSarah;
        private XUiC_ToggleButton radioDefaultAdam;

        // Companion voice radio buttons
        private XUiC_ToggleButton radioCompanionHeart;
        private XUiC_ToggleButton radioCompanionSarah;
        private XUiC_ToggleButton radioCompanionAdam;

        // Trader voice radio buttons
        private XUiC_ToggleButton radioTraderHeart;
        private XUiC_ToggleButton radioTraderSarah;
        private XUiC_ToggleButton radioTraderAdam;

        private XUiC_TextInput txtModel;

        private EntityPlayerLocal _entityPlayerLocal;

        // Available voices (Kokoro voice names from Resources/*.bin)
        private readonly string[] _availableVoices = new[]
        {
            // Female voices
            "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore",
            "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",
            // Male voices
            "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam",
            "am_michael", "am_onyx", "am_puck", "am_santa"
        };

        // CVar names for persistence
        private const string CVAR_TTS_ENABLED = "XNPCVoiceControl_TTSEnabled";
        private const string CVAR_STT_ENABLED = "XNPCVoiceControl_STTEnabled";
        private const string CVAR_VOLUME = "XNPCVoiceControl_Volume";
        private const string CVAR_SPEECH_RATE = "XNPCVoiceControl_SpeechRate";
        private const string CVAR_MAX_HISTORY = "XNPCVoiceControl_MaxHistory";
        private const string CVAR_CHAT_DISTANCE = "XNPCVoiceControl_ChatDistance";
        private const string CVAR_HIRED_CHAT_DISTANCE = "XNPCVoiceControl_HiredChatDistance";
        private const string CVAR_VOICE_DISTANCE = "XNPCVoiceControl_VoiceDistance";
        private const string CVAR_CHAT_TIMEOUT = "XNPCVoiceControl_ChatTimeout";
        private const string CVAR_DEFAULT_VOICE = "XNPCVoiceControl_DefaultVoice";
        private const string CVAR_COMPANION_VOICE = "XNPCVoiceControl_CompanionVoice";
        private const string CVAR_TRADER_VOICE = "XNPCVoiceControl_TraderVoice";
        private const string CVAR_DEBUG_MODE = "XNPCVoiceControl_DebugMode";
        private const string CVAR_MAX_TOKENS = "XNPCVoiceControl_MaxTokens";
        private const string CVAR_WAKE_WORD_ENABLED = "XNPCVoiceControl_WakeWordEnabled";
        private const string CVAR_BEAM_SIZE = "XNPCVoiceControl_BeamSize";
        private const string CVAR_SPEECH_MODEL = "XNPCVoiceControl_SpeechModel";


        public override void Init()
        {
            base.Init();

            // Get UI controls
            btnClose = GetChildById("btnClose") as XUiC_SimpleButton;
            btnSave = GetChildById("btnSave") as XUiC_SimpleButton;
            btnCancel = GetChildById("btnCancel") as XUiC_SimpleButton;
            btnTestTTS = GetChildById("btnTestTTS") as XUiC_SimpleButton;
            btnTestSTT = GetChildById("btnTestSTT") as XUiC_SimpleButton;
            btnTestLLM = GetChildById("btnTestLLM") as XUiC_SimpleButton;
            btnClearConversations = GetChildById("btnClearConversations") as XUiC_SimpleButton;

            toggleTTS = GetChildById("toggleTTS") as XUiC_ToggleButton;
            toggleSTT = GetChildById("toggleSTT") as XUiC_ToggleButton;
            toggleWakeWord = GetChildById("toggleWakeWord") as XUiC_ToggleButton;
            toggleDebug = GetChildById("toggleDebug") as XUiC_ToggleButton;
            btnSpeechModel = GetChildById("btnSpeechModel") as XUiC_SimpleButton;

            sliderVolume = GetChildById("sliderVolume") as XUiC_ComboBoxFloat;
            sliderSpeechRate = GetChildById("sliderSpeechRate") as XUiC_ComboBoxFloat;
            sliderMaxHistory = GetChildById("sliderMaxHistory") as XUiC_ComboBoxFloat;
            sliderChatDistance = GetChildById("sliderChatDistance") as XUiC_ComboBoxFloat;
            sliderHiredChatDistance = GetChildById("sliderHiredChatDistance") as XUiC_ComboBoxFloat;
            sliderChatTimeout = GetChildById("sliderChatTimeout") as XUiC_ComboBoxFloat;
            sliderVoiceDistance = GetChildById("sliderVoiceDistance") as XUiC_ComboBoxFloat;
            sliderMaxTokens = GetChildById("sliderMaxTokens") as XUiC_ComboBoxFloat;

            // Debug: Log which sliders were found
            Log.Debug(() => $"Init: sliderVolume = {(sliderVolume != null ? "found" : "NULL")}");
            Log.Debug(() => $"Init: sliderSpeechRate = {(sliderSpeechRate != null ? "found" : "NULL")}");
            Log.Debug(() => $"Init: sliderMaxHistory = {(sliderMaxHistory != null ? "found" : "NULL")}");
            Log.Debug(() => $"Init: sliderChatDistance = {(sliderChatDistance != null ? "found" : "NULL")}");
            Log.Debug(() => $"Init: sliderVoiceDistance = {(sliderVoiceDistance != null ? "found" : "NULL")}");


            // Get radio buttons for voice selection
            radioDefaultHeart = GetChildById("radioDefaultHeart") as XUiC_ToggleButton;
            radioDefaultSarah = GetChildById("radioDefaultSarah") as XUiC_ToggleButton;
            radioDefaultAdam = GetChildById("radioDefaultAdam") as XUiC_ToggleButton;

            radioCompanionHeart = GetChildById("radioCompanionHeart") as XUiC_ToggleButton;
            radioCompanionSarah = GetChildById("radioCompanionSarah") as XUiC_ToggleButton;
            radioCompanionAdam = GetChildById("radioCompanionAdam") as XUiC_ToggleButton;

            radioTraderHeart = GetChildById("radioTraderHeart") as XUiC_ToggleButton;
            radioTraderSarah = GetChildById("radioTraderSarah") as XUiC_ToggleButton;
            radioTraderAdam = GetChildById("radioTraderAdam") as XUiC_ToggleButton;

            txtModel = GetChildById("txtModel") as XUiC_TextInput;

            sliderBeamSize = GetChildById("sliderBeamSize") as XUiC_ComboBoxFloat;

            // Wire up button events
            if (btnClose != null) btnClose.OnPressed += BtnClose_OnPressed;
            if (btnSave != null) btnSave.OnPressed += BtnSave_OnPressed;
            if (btnCancel != null) btnCancel.OnPressed += BtnCancel_OnPressed;
            if (btnTestTTS != null) btnTestTTS.OnPressed += BtnTestTTS_OnPressed;
            if (btnTestSTT != null) btnTestSTT.OnPressed += BtnTestSTT_OnPressed;
            if (btnTestLLM != null) btnTestLLM.OnPressed += BtnTestLLM_OnPressed;
            if (btnClearConversations != null) btnClearConversations.OnPressed += BtnClearConversations_OnPressed;
            if (btnSpeechModel != null)
                btnSpeechModel.OnPressed += BtnSpeechModel_OnPressed;
        }

        public override void OnOpen()
        {
            base.OnOpen();

            _entityPlayerLocal = xui.playerUI.entityPlayer;

            Log.Debug(() => "OnOpen called, about to load settings");

            // Load current settings from player buffs (or defaults from config)
            LoadSettings();

            // Log the actual slider values after loading
            if (sliderVolume != null)
                Log.Debug(() => $"OnOpen: After LoadSettings, sliderVolume.Value = {sliderVolume.Value}");
            if (sliderSpeechRate != null)
                Log.Debug(() => $"OnOpen: After LoadSettings, sliderSpeechRate.Value = {sliderSpeechRate.Value}");
        }

        private void LoadSettings()
        {
            if (_entityPlayerLocal == null) return;

            // Load TTS settings
            if (toggleTTS != null)
            {
                toggleTTS.Value = GetBoolCVar(CVAR_TTS_ENABLED, TTSService.Instance?.Config?.Enabled ?? true);
            }

            if (sliderVolume != null)
            {
                // Load from saved CVar first, fall back to TTSService config default
                float volumeNormalized = GetFloatCVar(CVAR_VOLUME, TTSService.Instance?.Config?.Volume ?? 0.8f);
                sliderVolume.Value = volumeNormalized;
                Log.Debug(() => $"LoadSettings: Setting sliderVolume.Value to {volumeNormalized}");
            }

            if (sliderSpeechRate != null)
            {
                // Load from saved CVar first, fall back to TTSService config default
                sliderSpeechRate.Value = GetFloatCVar(CVAR_SPEECH_RATE, TTSService.Instance?.Config?.SpeechRate ?? 1.0f);
            }

            // Load default voice radio buttons
            var defaultVoice = GetStringCVar(CVAR_DEFAULT_VOICE, TTSService.Instance?.Config?.DefaultVoice ?? "af_heart");
            if (radioDefaultHeart != null) radioDefaultHeart.Value = (defaultVoice == "af_heart");
            if (radioDefaultSarah != null) radioDefaultSarah.Value = (defaultVoice == "af_sarah");
            if (radioDefaultAdam != null) radioDefaultAdam.Value = (defaultVoice == "am_adam");

            // Load companion voice radio buttons
            var companionVoice = GetStringCVar(CVAR_COMPANION_VOICE, TTSService.Instance?.Config?.CompanionVoice ?? "af_sarah");
            if (radioCompanionHeart != null) radioCompanionHeart.Value = (companionVoice == "af_heart");
            if (radioCompanionSarah != null) radioCompanionSarah.Value = (companionVoice == "af_sarah");
            if (radioCompanionAdam != null) radioCompanionAdam.Value = (companionVoice == "am_adam");

            // Load trader voice radio buttons
            var traderVoice = GetStringCVar(CVAR_TRADER_VOICE, TTSService.Instance?.Config?.TraderVoice ?? "am_adam");
            if (radioTraderHeart != null) radioTraderHeart.Value = (traderVoice == "af_heart");
            if (radioTraderSarah != null) radioTraderSarah.Value = (traderVoice == "af_sarah");
            if (radioTraderAdam != null) radioTraderAdam.Value = (traderVoice == "am_adam");

            // Load STT settings
            if (toggleSTT != null)
            {
                toggleSTT.Value = GetBoolCVar(CVAR_STT_ENABLED, STTService.Instance?.Config?.Enabled ?? true);
            }

            // Load wake word setting
            if (toggleWakeWord != null)
            {
                toggleWakeWord.Value = GetBoolCVar(CVAR_WAKE_WORD_ENABLED, STTService.Instance?.Config?.WakeWordEnabled ?? false);
            }

            // Load debug mode setting
            if (toggleDebug != null)
            {
                toggleDebug.Value = GetBoolCVar(CVAR_DEBUG_MODE, false);
            }

            // Load conversation settings - need to normalize to 0-1 range
            if (sliderMaxHistory != null)
            {
                sliderMaxHistory.Value = GetFloatCVar(CVAR_MAX_HISTORY, 10f);
            }

            if (sliderChatDistance != null)
            {
                sliderChatDistance.Value = GetFloatCVar(CVAR_CHAT_DISTANCE, 5f);
            }

            if (sliderHiredChatDistance != null)
            {
                sliderHiredChatDistance.Value = GetFloatCVar(CVAR_HIRED_CHAT_DISTANCE, 20f);
            }

            if (sliderChatTimeout != null)
            {
                sliderChatTimeout.Value = GetFloatCVar(CVAR_CHAT_TIMEOUT, XNPCVoiceControlMod.Config?.ChatTimeout ?? 30f);
            }

            if (sliderVoiceDistance != null)
            {
                sliderVoiceDistance.Value = GetFloatCVar(CVAR_VOICE_DISTANCE, 15f);
            }

            // Load model display (read-only, shows current LLMService model)
            if (txtModel != null)
            {
                txtModel.Text = LLMService.Instance?.CurrentModel ?? "unknown";
            }

            // Load max tokens slider
            if (sliderMaxTokens != null)
            {
                sliderMaxTokens.Value = GetFloatCVar(CVAR_MAX_TOKENS, LLMService.Instance?.GetCurrentMaxTokens() ?? 512f);
            }

            // Load beam size slider
            if (sliderBeamSize != null)
            {
                sliderBeamSize.Value = GetFloatCVar(CVAR_BEAM_SIZE, STTService.Instance?.Config?.BeamSize ?? 3f);
            }

            _speechModelAccurate = GetStringCVar(CVAR_SPEECH_MODEL, "") != "Fast";
            if (btnSpeechModel != null)
                btnSpeechModel.Text = _speechModelAccurate ? "Accurate" : "Fast";

        }

        private void SaveSettings()
        {
            if (_entityPlayerLocal == null) return;

            Log.Debug(() => "SaveSettings called");

            var buffs = _entityPlayerLocal.Buffs;

            // Save TTS settings
            if (toggleTTS != null)
            {
                SetBoolCVar(CVAR_TTS_ENABLED, toggleTTS.Value);
                if (TTSService.Instance != null)
                {
                    TTSService.Instance.Config.Enabled = toggleTTS.Value;
                }
            }

            if (sliderVolume != null)
            {
                // Slider value is already 0.0-1.0 (normalized), use directly
                float volumeNormalized = (float)sliderVolume.Value;
                Log.Debug(() => $"SaveSettings: sliderVolume.Value={volumeNormalized}");

                SetFloatCVar(CVAR_VOLUME, volumeNormalized);
                if (TTSService.Instance != null)
                {
                    TTSService.Instance.Config.Volume = volumeNormalized;
                    Log.Debug(() => $"SaveSettings: Set TTSService volume to {volumeNormalized}");
                }
            }

            if (sliderSpeechRate != null)
            {
                float speechRate = (float)sliderSpeechRate.Value;

                SetFloatCVar(CVAR_SPEECH_RATE, speechRate);
                if (TTSService.Instance != null)
                {
                    TTSService.Instance.Config.SpeechRate = speechRate;
                    Log.Debug(() => $"SaveSettings: Set TTSService speech rate to {speechRate}");
                }
            }

            // Save default voice based on radio button selection
            string defaultVoice = "af_heart";
            if (radioDefaultSarah != null && radioDefaultSarah.Value) defaultVoice = "af_sarah";
            else if (radioDefaultAdam != null && radioDefaultAdam.Value) defaultVoice = "am_adam";
            SetStringCVar(CVAR_DEFAULT_VOICE, defaultVoice);
            if (TTSService.Instance != null)
            {
                TTSService.Instance.Config.DefaultVoice = defaultVoice;
            }

            // Save companion voice based on radio button selection
            string companionVoice = "af_heart";
            if (radioCompanionSarah != null && radioCompanionSarah.Value) companionVoice = "af_sarah";
            else if (radioCompanionAdam != null && radioCompanionAdam.Value) companionVoice = "am_adam";
            SetStringCVar(CVAR_COMPANION_VOICE, companionVoice);
            if (TTSService.Instance != null)
            {
                TTSService.Instance.Config.CompanionVoice = companionVoice;
            }

            // Save trader voice based on radio button selection
            string traderVoice = "af_heart";
            if (radioTraderSarah != null && radioTraderSarah.Value) traderVoice = "af_sarah";
            else if (radioTraderAdam != null && radioTraderAdam.Value) traderVoice = "am_adam";
            SetStringCVar(CVAR_TRADER_VOICE, traderVoice);
            if (TTSService.Instance != null)
            {
                TTSService.Instance.Config.TraderVoice = traderVoice;
            }

            // Save STT settings
            if (toggleSTT != null)
            {
                SetBoolCVar(CVAR_STT_ENABLED, toggleSTT.Value);
                if (STTService.Instance != null)
                {
                    STTService.Instance.Config.Enabled = toggleSTT.Value;
                }
                if (MicrophoneCapture.Instance != null)
                {
                    MicrophoneCapture.Instance.IsEnabled = toggleSTT.Value;
                }
            }

            // Save wake word setting (takes effect on next game session)
            if (toggleWakeWord != null)
            {
                SetBoolCVar(CVAR_WAKE_WORD_ENABLED, toggleWakeWord.Value);
                if (STTService.Instance != null)
                {
                    STTService.Instance.Config.WakeWordEnabled = toggleWakeWord.Value;
                }
            }

            // Save debug mode setting
            if (toggleDebug != null)
            {
                SetBoolCVar(CVAR_DEBUG_MODE, toggleDebug.Value);
                Log.SetDebugMode(toggleDebug.Value);
            }

            // Save conversation settings
            if (sliderMaxHistory != null)
            {
                float maxHistory = (float)sliderMaxHistory.Value;
                SetFloatCVar(CVAR_MAX_HISTORY, maxHistory);
            }

            if (sliderChatDistance != null)
            {
                float chatDistance = (float)sliderChatDistance.Value;
                SetFloatCVar(CVAR_CHAT_DISTANCE, chatDistance);
            }

            if (sliderHiredChatDistance != null)
            {
                float hiredChatDistance = (float)sliderHiredChatDistance.Value;
                SetFloatCVar(CVAR_HIRED_CHAT_DISTANCE, hiredChatDistance);
            }

            if (sliderChatTimeout != null)
            {
                float chatTimeout = (float)sliderChatTimeout.Value;
                SetFloatCVar(CVAR_CHAT_TIMEOUT, chatTimeout);
                if (XNPCVoiceControlMod.Config != null)
                    XNPCVoiceControlMod.Config.ChatTimeout = chatTimeout;
            }

            if (sliderVoiceDistance != null)
            {
                float voiceDistance = (float)sliderVoiceDistance.Value;
                SetFloatCVar(CVAR_VOICE_DISTANCE, voiceDistance);

                // Push new max distance to all active NPC audio players immediately
                foreach (var chatComp in ChatComponentManager.GetAll())
                {
                    if (chatComp != null && chatComp.TTSEnabled)
                    {
                        var audioPlayer = chatComp.GetComponent<NPCAudioPlayer>();
                        if (audioPlayer != null)
                        {
                            audioPlayer.UpdateAudioRange(voiceDistance);
                        }
                    }
                }
            }

            // Model field is read-only — no save needed

            // Save max tokens
            if (sliderMaxTokens != null)
            {
                float maxTokens = (float)sliderMaxTokens.Value;
                int rounded = Mathf.RoundToInt(maxTokens);
                SetFloatCVar(CVAR_MAX_TOKENS, rounded);
                LLMService.Instance?.SetMaxTokens(rounded);
            }

            // Save beam size
            if (sliderBeamSize != null)
            {
                float beamSize = (float)sliderBeamSize.Value;
                int rounded = Mathf.RoundToInt(beamSize);
                SetFloatCVar(CVAR_BEAM_SIZE, rounded);
                if (STTService.Instance != null)
                {
                    STTService.Instance.Config.BeamSize = rounded;
                }
            }

            string modelChoice = _speechModelAccurate ? "Accurate" : "Fast";
            string previousChoice = GetStringCVar(CVAR_SPEECH_MODEL, "");
            SetStringCVar(CVAR_SPEECH_MODEL, modelChoice);
            string modelName = _speechModelAccurate ? "ggml-small.bin" : "ggml-base.en.bin";
            if (STTService.Instance != null)
            {
                STTService.Instance.Config.Model = modelName;
            }
            if (modelChoice != previousChoice)
            {
                SubtitleManager.Instance.ShowSubtitle("System", "Restart game to apply.", 3f);
            }

            // VoiceMap is now unified — no server restart needed for language switch
            // The per-request "lang" field handles G2P routing automatically

            SubtitleManager.Instance.ShowSubtitle("System", LocalizationHelper.Get("settings_saved"), 3f);
        }

        // CVar helper methods
        private bool GetBoolCVar(string cvar, bool defaultValue)
        {
            if (_entityPlayerLocal.Buffs.HasCustomVar(cvar))
            {
                return _entityPlayerLocal.Buffs.GetCustomVar(cvar) > 0f;
            }
            return defaultValue;
        }

        // Float settings persist in PlayerPrefs (global, cross-session) to match the
        // runtime getters in NPCVoiceControlMod.cs, which read these same keys via
        // PlayerPrefs.GetFloat. (Previously these wrote to player Buffs, which the
        // runtime never read — and which still held pre-range-fix normalized 0-1
        // values that clamped every slider to its minimum on load.)
        private float GetFloatCVar(string cvar, float defaultValue)
        {
            return PlayerPrefs.GetFloat(cvar, defaultValue);
        }

        private string GetStringCVar(string cvar, string defaultValue)
        {
            // String storage: use a special encoding or separate storage
            // For simplicity, we'll use PlayerPrefs which persists across sessions
            return PlayerPrefs.GetString(cvar, defaultValue);
        }

        private void SetBoolCVar(string cvar, bool value)
        {
            _entityPlayerLocal.Buffs.SetCustomVar(cvar, value ? 1f : 0f);
        }

        private void SetFloatCVar(string cvar, float value)
        {
            PlayerPrefs.SetFloat(cvar, value);
            PlayerPrefs.Save();
        }

        private void SetStringCVar(string cvar, string value)
        {
            PlayerPrefs.SetString(cvar, value);
            PlayerPrefs.Save();
        }

        // Button handlers
        private void BtnClose_OnPressed(XUiController _sender, int _mouseButton)
        {
            CloseWindow();
        }

        private void BtnSave_OnPressed(XUiController _sender, int _mouseButton)
        {
            SaveSettings();
            CloseWindow();
        }

        private void BtnCancel_OnPressed(XUiController _sender, int _mouseButton)
        {
            CloseWindow();
        }

        private void BtnTestTTS_OnPressed(XUiController _sender, int _mouseButton)
        {
            if (TTSService.Instance == null || !TTSService.Instance.IsInitialized)
            {
                SubtitleManager.Instance.ShowSubtitle("System", LocalizationHelper.Get("tts_not_available"), 3f);
                return;
            }

            string testText = "Hello! This is a test of the text to speech system.";
            // Get selected default voice from radio buttons
            string voice = "af_heart";
            if (radioDefaultSarah != null && radioDefaultSarah.Value) voice = "af_sarah";
            else if (radioDefaultAdam != null && radioDefaultAdam.Value) voice = "am_adam";

            SubtitleManager.Instance.ShowSubtitle("System", LocalizationHelper.Get("generating_test_audio"), 3f);

            // Use Synthesize to generate audio, then play it
            TTSService.Instance.Synthesize(
                testText,
                voice,
                "a",
                wavBytes => {
                    if (_entityPlayerLocal != null)
                    {
                        Log.Debug(() => $"Test WAV bytes received: {wavBytes.Length} bytes");
                        float volume = (float)(sliderVolume?.Value ?? 0.8);

                        // Phase 1: background-safe PCM conversion
                        var (audioData, sampleRate, channels) = AudioUtils.ProcessWavBytes(wavBytes);

                        // Phase 2: main-thread AudioClip creation + playback
                        ThreadManager.AddSingleTaskMainThread("PlayTestTTS", (_taskInfo) => {
                            AudioClip clip = AudioUtils.CreateClipFromData(audioData, sampleRate, channels);
                            if (clip == null)
                            {
                                SubtitleManager.Instance.ShowSubtitle("System", LocalizationHelper.Get("failed_parse_wav"), 3f);
                                return;
                            }
                            _entityPlayerLocal.StartCoroutine(PlayTestAudioClip(clip, volume));
                        });

                        SubtitleManager.Instance.ShowSubtitle("System", LocalizationHelper.Get("playing_test_audio"), 3f);
                    }
                },
                error => {
                    Log.Error($"TTS test error: {error}");
                    SubtitleManager.Instance.ShowSubtitle("System", LocalizationHelper.Get("tts_test_failed", error), 5f);
                }
            );
        }

        /// <summary>
        /// Play an AudioClip directly (no file I/O), then cleanup.
        /// </summary>
        private System.Collections.IEnumerator PlayTestAudioClip(AudioClip clip, float volume)
        {
            GameObject tempAudio = new GameObject("TempTestAudio");
            tempAudio.transform.position = _entityPlayerLocal.position;

            AudioSource source = tempAudio.AddComponent<AudioSource>();
            source.clip = clip;
            source.volume = volume;
            source.spatialBlend = 0f;
            source.playOnAwake = false;
            source.loop = false;
            source.bypassEffects = true;
            source.bypassListenerEffects = true;
            source.bypassReverbZones = true;
            source.priority = 0;

            source.Play();

            yield return new WaitForSeconds((float)clip.length);

            UnityEngine.Object.Destroy(tempAudio);
            UnityEngine.Object.Destroy(clip);
        }

        private void BtnTestSTT_OnPressed(XUiController _sender, int _mouseButton)
        {

            if (STTService.Instance == null || !STTService.Instance.IsInitialized)
            {
                SubtitleManager.Instance.ShowSubtitle("System", LocalizationHelper.Get("stt_not_available"), 3f);
                return;
            }

            if (MicrophoneCapture.Instance == null || !MicrophoneCapture.Instance.IsInitialized)
            {
                SubtitleManager.Instance.ShowSubtitle("System", LocalizationHelper.Get("microphone_not_available"), 3f);
                return;
            }

            SubtitleManager.Instance.ShowSubtitle("System", LocalizationHelper.Get("recording_3_seconds"), 3f);

            MicrophoneCapture.Instance.TestRecording(3f, wavData =>
            {
                if (wavData == null || wavData.Length == 0)
                {
                    SubtitleManager.Instance.ShowSubtitle("System", "No audio captured", 3f);
                    return;
                }

                STTService.Instance.Transcribe(
                    wavData,
                    text => {
                        SubtitleManager.Instance.ShowSubtitle("System", $"You said: \"{text}\"", 5f);
                    },
                    error => {
                        SubtitleManager.Instance.ShowSubtitle("System", $"STT test failed: {error}", 5f);
                    }
                );
            });
        }

        private void BtnTestLLM_OnPressed(XUiController _sender, int _mouseButton)
        {

            if (LLMService.Instance == null)
            {
                SubtitleManager.Instance.ShowSubtitle("System", "LLM service not available", 3f);
                return;
            }

            // LLMService exists and Instance is not null means it's initialized
            SubtitleManager.Instance.ShowSubtitle("System", $"AI service is ready! Model: {LLMService.Instance?.CurrentModel ?? "unknown"}", 5f);
        }

        private void BtnClearConversations_OnPressed(XUiController _sender, int _mouseButton)
        {

            // Clear all NPC conversation histories
            var npcs = GameManager.Instance.World.Entities.list;
            int clearedCount = 0;

            foreach (var entity in npcs)
            {
                if (entity is EntityAlive npc)
                {
                    var chatComponent = npc.gameObject?.GetComponent<NPCChatComponent>();
                    if (chatComponent != null)
                    {
                        chatComponent.ClearHistory();
                        clearedCount++;
                    }
                }
            }

            SubtitleManager.Instance.ShowSubtitle("System", $"Cleared {clearedCount} conversation(s)", 3f);
        }

        private void BtnSpeechModel_OnPressed(XUiController _sender, int _mouseButton)
        {
            _speechModelAccurate = !_speechModelAccurate;
            if (btnSpeechModel != null)
                btnSpeechModel.Text = _speechModelAccurate ? "Accurate" : "Fast";
            if (sliderBeamSize != null)
                sliderBeamSize.Value = _speechModelAccurate ? 3f : 1f;
        }

        private void CloseWindow()
        {
            xui.playerUI.windowManager.Close(this.windowGroup, false);
        }

        /// <summary>
        /// Apply wake word toggle at runtime: start or stop the WakeWordListener.
        /// </summary>
        private void ApplyWakeWordToggle(bool enabled)
        {
            if (enabled && WakeWordListener.Instance == null)
            {
                // Try to initialize on demand
                var config = STTService.Instance?.Config;
                if (config != null)
                {
                    string modelsDir = ResolveWakeWordModelsDir();
                    if (!string.IsNullOrEmpty(modelsDir))
                    {
                        var runtimeConfig = new WakeWordRuntimeConfig
                        {
                            WakeWords = new[]
                            {
                                new WakeWordConfig
                                {
                                    Model = config.WakeWordModel,
                                    Threshold = config.WakeWordThreshold,
                                    TriggerLevel = 4,
                                    Refractory = 20
                                }
                            },
                            StepFrames = 4,
                            DebugAction = (model, prob, detected) =>
                            {
                                if (detected)
                                    Log.Debug(() => $"WakeWord [{model}] probability: {prob:F4} DETECTED");
                            }
                        };

                        string micDevice = MicrophoneCapture.Instance?.SelectedDevice ?? "";
                        WakeWordListener.Instance.Initialize(modelsDir, micDevice, config.SampleRate, runtimeConfig);

                        // Wire up voice input manager if not already wired
                        if (VoiceInputManager.Instance != null)
                        {
                            VoiceInputManager.Instance.Initialize(config, WakeWordListener.Instance, MicrophoneCapture.Instance);
                        }
                    }
                    else
                    {
                        SubtitleManager.Instance.ShowSubtitle("System", "Wake word models not found — check Resources/WakeWord/models/", 5f);
                        if (toggleWakeWord != null) toggleWakeWord.Value = false;
                    }
                }
            }
            else if (!enabled && WakeWordListener.Instance != null)
            {
                WakeWordListener.Instance.Stop();
            }
        }

        /// <summary>
        /// Resolve the directory containing wake word ONNX models.
        /// </summary>
        private static string ResolveWakeWordModelsDir()
        {
            string modPath = XNPCVoiceControlMod.GetModPath();
            if (string.IsNullOrEmpty(modPath)) return null;

            string modModelsDir = System.IO.Path.Combine(modPath, "Resources", "WakeWord", "models");
            if (System.IO.Directory.Exists(modModelsDir) &&
                System.IO.File.Exists(System.IO.Path.Combine(modModelsDir, "melspectrogram.onnx")))
                return modModelsDir;

            // Fallback: assembly location
            string asmPath = typeof(XNPCVoiceControlMod).Assembly.Location;
            if (!string.IsNullOrEmpty(asmPath))
            {
                string asmModelsDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(asmPath), "Resources", "WakeWord", "models");
                if (System.IO.Directory.Exists(asmModelsDir) &&
                    System.IO.File.Exists(System.IO.Path.Combine(asmModelsDir, "melspectrogram.onnx")))
                    return asmModelsDir;
            }

            return null;
        }
    }
