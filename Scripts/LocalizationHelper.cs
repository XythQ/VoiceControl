using System;
using System.Collections.Generic;
using UnityEngine;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Simple localization helper for static mod text (tooltips, config labels).
    /// Auto-detects the player's vanilla game language via Localization.language.
    /// Returns localized strings or falls back to English.
    /// </summary>
    public static class LocalizationHelper
    {
        private static bool _useJapanese = false;

        // Map 7DTD game language names (Localization.language) to ISO codes.
        // Confirmed values from Assembly-CSharp/Localization.cs::GetCurrentLocale().
        private static readonly Dictionary<string, string> GameLanguageToIso = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "english",   "en" },
            { "japanese",  "ja" },
            { "schinese",  "zh" },
            { "koreana",   "ko" },
            { "brazilian", "pt" },
            { "spanish",   "es" },
            { "french",    "fr" },
            { "german",    "de" },
            { "italian",   "it" },
            { "polish",    "pl" },
            { "russian",   "ru" },
            { "turkish",   "tr" }
        };

        // Static text keys and their translations
        private static readonly Dictionary<string, string> _english = new Dictionary<string, string>
        {
            { "no_npc_nearby", "No NPC nearby to talk to" },
            { "settings_saved", "Settings saved — takes effect on next launch" },
            { "tts_not_available", "TTS service not available" },
            { "generating_test_audio", "Generating test audio..." },
            { "failed_parse_wav", "Failed to parse WAV data" },
            { "playing_test_audio", "Playing test audio..." },
            { "tts_test_failed", "TTS test failed: {0}" },
            { "stt_not_available", "STT service not available" },
            { "microphone_not_available", "Microphone not available" },
            { "recording_3_seconds", "Recording for 3 seconds..." },
            { "voice_error", "Voice error: {0}" }
        };

        private static readonly Dictionary<string, string> _japanese = new Dictionary<string, string>
        {
            { "no_npc_nearby", "話しかけられるNPCがいません" },
            { "settings_saved", "設定を保存しました — 次回起動から有効になります" },
            { "tts_not_available", "TTSサービスが利用できません" },
            { "generating_test_audio", "テスト音声を作成中..." },
            { "failed_parse_wav", "WAVデータの解析に失敗しました" },
            { "playing_test_audio", "テスト音声を再生中..." },
            { "tts_test_failed", "TTSテストに失敗しました: {0}" },
            { "stt_not_available", "STTサービスが利用できません" },
            { "microphone_not_available", "マイクが利用できません" },
            { "recording_3_seconds", "3秒間録音中..." },
            { "voice_error", "音声エラー: {0}" }
        };

        /// <summary>Set to true for Japanese UI text, false for English.</summary>
        public static bool UseJapanese
        {
            get => _useJapanese;
            set => _useJapanese = value;
        }

        /// <summary>
        /// Resolve the player's vanilla game language to an ISO code.
        /// Reads GamePrefs.GetString(EnumGamePrefs.Language) and maps to ISO.
        /// Unknown/missing languages default to "en".
        /// Call InitLanguageDetection() once at mod start to populate _useJapanese.
        /// </summary>
        public static string GetPlayerUiLanguage()
        {
            try
            {
                string gameLang = GamePrefs.GetString(EnumGamePrefs.Language);
                Log.Debug(() => $"[I18N] GamePrefs.Language = '{gameLang}'");
                if (!string.IsNullOrEmpty(gameLang) && GameLanguageToIso.TryGetValue(gameLang, out string iso))
                {
                    Log.Debug(() => $"[I18N] Mapped to ISO '{iso}'");
                    return iso;
                }
                Log.Debug(() => $"[I18N] No mapping for '{gameLang}', falling back to 'en'");
            }
            catch (System.Exception ex)
            {
                // GamePrefs not yet available at very early boot
                Log.Debug(() => $"[I18N] GamePrefs unavailable: {ex.Message}");
            }
            return "en";
        }

        /// <summary>
        /// Call once at mod init to set _useJapanese for static UI text.
        /// Supersedes the manual JP toggle (PlayerPrefs XNPCVoiceControl_UiJapanese).
        /// </summary>
        public static void InitLanguageDetection()
        {
            string iso = GetPlayerUiLanguage();
            _useJapanese = (iso == "ja");
        }

        /// <summary>
        /// Resolve the effective subtitle language for an NPC.
        /// Priority: per-NPC ForceLanguage override → player UI language → English fallback.
        /// Behavior-identical to GetPlayerUiLanguage() when ForceLanguage is empty.
        /// </summary>
        public static string ResolveEffectiveLanguage(PersonalityDefinition personality)
        {
            if (personality?.ForceLanguage != null && personality.ForceLanguage.Length > 0)
                return personality.ForceLanguage;
            string playerLang = GetPlayerUiLanguage();
            if (!string.IsNullOrEmpty(playerLang))
                return playerLang;
            return "en";
        }

        /// <summary>Get localized string by key. Falls back to English if key not found.</summary>
        public static string Get(string key)
        {
            return Get(key, null);
        }

        /// <summary>Get localized string by key with optional format argument.</summary>
        public static string Get(string key, string formatArg)
        {
            if (_useJapanese && _japanese.TryGetValue(key, out string jp))
            {
                return formatArg != null ? string.Format(jp, formatArg) : jp;
            }

            // Fallback to English
            if (_english.TryGetValue(key, out string en))
            {
                return formatArg != null ? string.Format(en, formatArg) : en;
            }

            // Key not found — return the key itself as last resort
            return key;
        }
    }
}
