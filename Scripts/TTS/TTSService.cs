using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace XNPCVoiceControl.TTS
{
    /// <summary>
    /// HTTP-based TTS service.
    /// Sends text to an external TTS server via HTTP POST, receives WAV audio back.
    /// Completely avoids KokoroSharp/Mono compatibility issues.
    /// </summary>
    public class TTSService
    {
        private static TTSService _instance;
        public static TTSService Instance => _instance ??= new TTSService();

        private TTSConfig _config;
        private bool _initialized = false;

        // Cached compiled regexes for emote stripping (called every TTS synthesis)
        private static readonly Regex AsteriskEmote = new Regex(@"\*[^*]+\*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex UnderscoreEmote = new Regex(@"_[^_]+_", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TildeEmote = new Regex(@"~[^~]+~", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Whisper.cpp non-speech tokens: [BLANK_AUDIO], [LAUGH], speaker labels, parenthesized audio events
        // Strip all bracketed tokens ([BLANK_AUDIO], [speaker labels]) and parenthesized audio events ((music), (sigh), etc.)
        private static readonly Regex WhisperNonSpeech = new Regex(@"\[[^\]]+\]|\([^)]+\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex WhitespaceRegex = new Regex(@"\s{2,}", RegexOptions.Compiled);

        // Stats
        private int _requestCount = 0;
        private double _totalSynthesisTimeMs = 0;
        private double _lastSynthesisTimeMs = 0;

        public TTSConfig Config => _config;
        public bool IsInitialized => _initialized;
        public int RequestCount => _requestCount;
        public double LastSynthesisTimeMs => _lastSynthesisTimeMs;
        public double AvgSynthesisTimeMs => _requestCount > 0 ? _totalSynthesisTimeMs / _requestCount : 0;

        private readonly object _lock = new object();

        private TTSService() { }

        /// <summary>
        /// Initialize the TTS service with the given config.
        /// </summary>
        public void Initialize(TTSConfig config)
        {
            if (_initialized)
            {
                Log.Debug(() => "TTSService already initialized, reconfiguring...");
            }

            _config = config;
            _initialized = true;

            Log.Out($"TTSService initialized (HTTP mode, server: {config.ServerUrl})");
        }

        /// <summary>
        /// Strip roleplay emotes/actions from text before TTS synthesis.
        /// Removes patterns like *sighs*, _whispers_, ~smiles~
        /// Also strips whisper.cpp non-speech tokens ([BLANK_AUDIO], (music), etc.)
        /// </summary>
        public static string StripEmotesForTts(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Remove *asterisk* emotes
            text = AsteriskEmote.Replace(text, "");
            // Remove _underscore_ emotes
            text = UnderscoreEmote.Replace(text, "");
            // Remove ~tilde~ emotes
            text = TildeEmote.Replace(text, "");
            // Remove whisper.cpp non-speech tokens ([BLANK_AUDIO], [LAUGH], etc.)
            text = WhisperNonSpeech.Replace(text, "");

            // Strip stray double-quote characters (greeting/story text can carry literal quotes)
            text = text.Replace("\"", "");

            // Clean up extra whitespace left behind
            text = WhitespaceRegex.Replace(text, " ").Trim();

            return text;
        }

        /// <summary>
        /// Synthesize text to speech via HTTP POST to the TTS server.
        /// Returns raw WAV bytes via callback on the Unity main thread (no temp files).
        /// </summary>
        public void Synthesize(string text, string voiceName, Action<byte[]> onSuccess, Action<string> onError)
        {
            Synthesize(text, voiceName, "en", onSuccess, onError);
        }

        /// <summary>
        /// Synthesize text to speech via HTTP POST to the TTS server.
        /// Returns raw WAV bytes via callback on the Unity main thread (no temp files).
        /// </summary>
        public void Synthesize(string text, string voiceName, string language, Action<byte[]> onSuccess, Action<string> onError)
        {
            if (!_initialized)
            {
                onError?.Invoke("TTS not initialized");
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                onError?.Invoke("Empty text");
                return;
            }

            // Strip emotes before synthesis
            string cleanText = StripEmotesForTts(text);
            if (string.IsNullOrWhiteSpace(cleanText))
            {
                // Entire text was emotes — nothing to speak
                return;
            }

            Interlocked.Increment(ref _requestCount);
            long startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Normalize ISO language code for TTS server API.
            // Input is now ISO; normalize en-GB/en-IE to "en" for engines without those variants.
            string langCode = language switch
            {
                "ja"    => "ja",
                "zh"    => "zh",
                "es"    => "es",
                "hi"    => "hi",
                "pl"    => "pl",
                "en-GB" => "en",  // Kokoro/supertonic don't have GB-specific G2P
                "en-IE" => "en",  // same for Irish
                _       => "en",  // "en" and everything else defaults to American English
            };

            // Build JSON body with dynamic language flag
            string jsonBody = $"{{\"text\":\"{EscapeJson(cleanText)}\",\"voice\":\"{voiceName}\",\"speed\":{_config.SpeechRate},\"lang\":\"{langCode}\"}}";

            // Send HTTP request on background thread
            Task.Run(() =>
            {
                try
                {
                    byte[] wavBytes = SendHttpRequest(_config.ServerUrl, jsonBody, _config.TimeoutSeconds);

                    if (wavBytes == null || wavBytes.Length == 0)
                    {
                        throw new Exception("TTS server returned empty audio");
                    }

                    long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime;
                    lock (_lock)
                    {
                        _lastSynthesisTimeMs = elapsed;
                        _totalSynthesisTimeMs += elapsed;
                    }

                    Log.Debug(() => $"[TIMING] TTS synthesized in {elapsed}ms: '{cleanText.Substring(0, Math.Min(40, cleanText.Length))}...' ({wavBytes.Length} bytes)");

                    // Return raw WAV bytes to main thread (no temp file)
                    ThreadManager.AddSingleTaskMainThread("TTS_SynthesizeComplete", _ =>
                    {
                        onSuccess?.Invoke(wavBytes);
                    });
                }
                catch (Exception ex)
                {
                    Log.Error($"TTS synthesis error: {ex.Message}");

                    ThreadManager.AddSingleTaskMainThread("TTS_SynthesizeError", _ =>
                    {
                        onError?.Invoke(ex.Message);
                    });
                }
            });
        }

        /// <summary>
        /// Send HTTP POST request to the TTS server and return WAV bytes.
        /// Uses System.Net.HttpWebRequest for Mono compatibility.
        /// </summary>
        private byte[] SendHttpRequest(string serverUrl, string jsonBody, int timeoutSeconds)
        {
            string url = $"{serverUrl.TrimEnd('/')}/tts";

            var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.Timeout = timeoutSeconds * 1000;
            request.ReadWriteTimeout = timeoutSeconds * 1000;

            byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
            request.ContentLength = bodyBytes.Length;

            Log.Debug(() => $"TTS POST to {url}: {jsonBody.Substring(0, Math.Min(120, jsonBody.Length))}...");

            using (var requestStream = request.GetRequestStream())
            {
                requestStream.Write(bodyBytes, 0, bodyBytes.Length);
            }

            try
            {
                using (var response = (System.Net.HttpWebResponse)request.GetResponse())
                {
                    // Log response metadata for debugging
                    Log.Debug(() => $"TTS HTTP response: status={response.StatusCode}, contentType={response.ContentType}, contentLength={response.ContentLength}");

                    using (var responseStream = response.GetResponseStream())
                    {
                        if (response.ContentType?.Contains("audio") != true && response.ContentType?.Contains("wav") != true)
                        {
                            // Try to read error message
                            using (var reader = new System.IO.StreamReader(responseStream))
                            {
                                string errorBody = reader.ReadToEnd();
                                throw new Exception($"TTS server returned {response.StatusCode}: {errorBody}");
                            }
                        }

                        using (var memoryStream = new System.IO.MemoryStream())
                        {
                            responseStream.CopyTo(memoryStream);
                            byte[] data = memoryStream.ToArray();

                            // Log first 16 bytes as hex for debugging
                            string hexDump = "";
                            for (int i = 0; i < Math.Min(16, data.Length); i++)
                                hexDump += data[i].ToString("X2") + " ";
                            Log.Debug(() => $"TTS response first 16 bytes: {hexDump.Trim()}");

                            return data;
                        }
                    }
                }
            }
            catch (System.Net.WebException webEx)
            {
                // Server returned an error status (4xx/5xx) - try to read the response body
                if (webEx.Response is System.Net.HttpWebResponse errorResponse)
                {
                    using (var errorStream = errorResponse.GetResponseStream())
                    using (var reader = new System.IO.StreamReader(errorStream))
                    {
                        string errorBody = reader.ReadToEnd();
                        Log.Error($"TTS server error ({errorResponse.StatusCode}): {errorBody}");
                        throw new Exception($"TTS server returned {(int)errorResponse.StatusCode}: {errorBody}");
                    }
                }
                throw;
            }
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

        /// <summary>
        /// Get the appropriate voice for an NPC type.
        /// </summary>
        public string GetVoiceForNPCType(string npcType)
        {
            if (_config == null) return _config?.DefaultVoice ?? "af_aoede";

            switch (npcType.ToLower())
            {
                case "trader":
                    return _config.TraderVoice;
                case "companion":
                    return _config.CompanionVoice;
                case "bandit":
                    return _config.BanditVoice;
                default:
                    return _config.DefaultVoice;
            }
        }

        /// <summary>
        /// Get a human-readable status string.
        /// </summary>
        public string GetStatusString()
        {
            if (!_initialized)
                return "TTS: Not initialized";

            return $"TTS: Ready (HTTP, {_requestCount} requests, avg {AvgSynthesisTimeMs:F0}ms)";
        }
    }
}
