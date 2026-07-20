using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace XNPCVoiceControl.STT
{
    /// <summary>
    /// HTTP-based Speech-to-Text service.
    /// Sends WAV audio to whisper-server via HTTP POST, receives transcribed text back.
    /// Completely avoids Whisper.net/Mono compatibility issues in Unity.
    /// </summary>
    public class STTService
    {
        private static STTService _instance;
        public static STTService Instance => _instance ??= new STTService();

        private STTConfig _config;
        private bool _initialized = false;

        // Stats
        private int _requestCount = 0;
        private double _totalTranscriptionTimeMs = 0;
        private double _lastTranscriptionTimeMs = 0;

        public STTConfig Config => _config;
        public bool IsInitialized => _initialized;
        public int RequestCount => _requestCount;
        public double LastTranscriptionTimeMs => _lastTranscriptionTimeMs;
        public double AvgTranscriptionTimeMs => _requestCount > 0 ? _totalTranscriptionTimeMs / _requestCount : 0;

        private readonly object _lock = new object();

        // Guard against overlapping restart commands (prevents death loop)
        private static bool _isRestarting;
        private static readonly object _restartLock = new object();

        // Strip whisper.cpp non-speech tokens from raw transcription output
        // Strip all bracketed tokens ([BLANK_AUDIO], [speaker labels]) and parenthesized audio events ((music), (sigh), etc.)
        private static readonly Regex WhisperNonSpeech = new Regex(@"\[[^\]]+\]|\([^)]+\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Common Whisper hallucinated phrases from training data (YouTube captions, Amara.org subtitles)
        private static readonly string[] HallucinationBlacklist = {
            "youtube",
            "subscribe",
            "amara.org",
            "amara dot org",
            "cc licensed",
            "creative commons",
            "licensed under creative commons",
            "translation hosted by amara",
        };

        private static string StripNonSpeech(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = WhisperNonSpeech.Replace(text, "");
            return Regex.Replace(text, @"\s{2,}", " ").Trim();
        }

        /// <summary>
        /// Safely trim a steering prompt to avoid Whisper's 224-token limit.
        /// Cuts at the last natural word boundary (comma or space) within maxLength.
        /// </summary>
        public static string GetSafeWhisperPrompt(string rawPrompt, int maxLength = 800)
        {
            if (string.IsNullOrEmpty(rawPrompt) || rawPrompt.Length <= maxLength)
                return rawPrompt;

            int lastComma = rawPrompt.LastIndexOf(',', maxLength);
            int lastSpace = rawPrompt.LastIndexOf(' ', maxLength);

            int cutIndex = Math.Max(lastComma, lastSpace);

            if (cutIndex <= 0)
                cutIndex = maxLength;

            return rawPrompt.Substring(0, cutIndex).TrimEnd(',', ' ');
        }

        /// <summary>
        /// Sanitize a raw STT transcript: strip caption artifacts ([], (), <>),
        /// remove hallucinated phrases (YouTube/Amara training data), and collapse whitespace.
        /// Call this on every transcribed string before passing it to the LLM or phrase triggers.
        /// </summary>
        public static string SanitizeTranscript(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 1. Strip literal \n strings and actual newlines (whisper-server artifact)
            text = text.Replace("\\n", "").Replace("\r", "");

            // 2. Strip all bracket/parenthesis artifacts: [BLANK_AUDIO], (sigh), <speaker>, etc.
            text = Regex.Replace(text, @"\[[^\]]+\]|\([^)]+\)|<[^>]+>", "", RegexOptions.IgnoreCase);

            // 3. Remove hallucinated phrases line-by-line (they often appear as appended sentences)
            string[] lines = text.Split(new[] { '.', '!', '?', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var cleanLines = new System.Collections.Generic.List<string>();
            foreach (var line in lines)
            {
                string trimmedLower = line.Trim().ToLower();
                bool isHallucination = false;
                foreach (var phrase in HallucinationBlacklist)
                {
                    if (trimmedLower.Contains(phrase))
                    {
                        isHallucination = true;
                        break;
                    }
                }
                if (!isHallucination && trimmedLower.Length > 0)
                {
                    cleanLines.Add(line.Trim());
                }
            }

            text = string.Join(" ", cleanLines);

            // 3. Strip ALL punctuation (whisper inserts it; we never speak it).
            // Keeps letters (any script), digits, whitespace, apostrophe (contractions). Replace rest with space.
            text = Regex.Replace(text, @"[^\p{L}\p{N}\s']", " ");
            text = Regex.Replace(text, @"\s{2,}", " ").Trim();

            return text;
        }

        private STTService() { }

        /// <summary>
        /// Initialize the STT service with the given config.
        /// </summary>
        public void Initialize(STTConfig config)
        {
            if (_initialized)
            {
                Log.Debug(() => "STTService already initialized, reconfiguring...");
            }

            _config = config;
            _initialized = true;

            Log.Out($"STTService initialized (HTTP mode, server: {config.ServerUrl})");
        }

        /// <summary>
        /// Transcribe WAV audio data to text via HTTP POST to whisper-server.
        /// Returns transcribed text via callback on the Unity main thread.
        /// </summary>
        public void Transcribe(byte[] wavData, Action<string> onSuccess, Action<string> onError)
        {
            Transcribe(wavData, null, onSuccess, onError);
        }

        /// <summary>
        /// Transcribe with a custom steering prompt (e.g. injected NPC names).
        /// When promptOverride is null or empty, falls back to config default.
        /// </summary>
        public void Transcribe(byte[] wavData, string promptOverride, Action<string> onSuccess, Action<string> onError)
        {
            if (!_initialized)
            {
                onError?.Invoke("STT not initialized");
                return;
            }

            if (wavData == null || wavData.Length < 44)
            {
                onError?.Invoke("Invalid or empty audio data");
                return;
            }

            string effectivePrompt = !string.IsNullOrEmpty(promptOverride)
                ? GetSafeWhisperPrompt(promptOverride)
                : _config.Prompt;

            Interlocked.Increment(ref _requestCount);
            long startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            Log.Debug(() => $"STT: sending {wavData.Length} bytes ({wavData.Length / 32000f:F1}s of mono 16kHz audio) to {_config.ServerUrl}");

            // Send HTTP request on background thread
            Task.Run(() =>
            {
                try
                {
                    string text = SendHttpRequest(_config.ServerUrl, wavData, _config.TimeoutSeconds, effectivePrompt);

                    long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime;
                    lock (_lock)
                    {
                        _lastTranscriptionTimeMs = elapsed;
                        _totalTranscriptionTimeMs += elapsed;
                    }

                    if (!string.IsNullOrEmpty(text))
                    {
                        // Strip whisper non-speech tokens before logging and passing on
                        string clean = StripNonSpeech(text);
                        Log.Debug(() => $"[TIMING] STT transcribed in {elapsed}ms: \"{clean}\"");

                        // Return result on main thread (stripped)
                        ThreadManager.AddSingleTaskMainThread("STT_TranscribeComplete", _ =>
                        {
                            onSuccess?.Invoke(clean);
                        });
                    }
                    else
                    {
                        Log.Debug(() => $"STT completed in {elapsed}ms (no speech detected)");
                        ThreadManager.AddSingleTaskMainThread("STT_TranscribeComplete", _ =>
                        {
                            onError?.Invoke("No speech detected");
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"STT transcription error: {ex.Message}");

                    // whisper-server can crash after a request under Unity Mono.
                    // Try restarting it once with proper readiness polling, then retry.
                    if (ex.Message.Contains("ConnectFailure") || ex.Message.Contains("connection"))
                    {
                        bool alreadyRestarting;
                        lock (_restartLock)
                        {
                            alreadyRestarting = _isRestarting;
                            if (!alreadyRestarting)
                                _isRestarting = true;
                        }

                        if (alreadyRestarting)
                        {
                            Log.Debug(() => "STT: restart already in progress, skipping");
                        }
                        else
                        {
                            try
                            {
                                Log.Out("STT: server unreachable, restarting whisper-server...");
                                ServerManager.RestartWhisperServer();

                                // Poll for port 5052 readiness instead of blind sleep.
                                // Whisper can take 5-10+ seconds to reload the model into VRAM.
                                bool ready = WaitForPortReady("127.0.0.1", 5052, 15);

                                if (ready)
                                {
                                    Log.Out("STT: whisper-server back online, retrying transcription...");
                                    try
                                    {
                                        string text = SendHttpRequest(_config.ServerUrl, wavData, _config.TimeoutSeconds, effectivePrompt);

                                        long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime;
                                        lock (_lock)
                                        {
                                            _lastTranscriptionTimeMs = elapsed;
                                            _totalTranscriptionTimeMs += elapsed;
                                        }

                                        ThreadManager.AddSingleTaskMainThread("STT_TranscribeComplete", _ =>
                                        {
                                            if (!string.IsNullOrEmpty(text))
                                                onSuccess?.Invoke(StripNonSpeech(text));
                                            else
                                                onError?.Invoke("No speech detected");
                                        });
                                        return;
                                    }
                                    catch (Exception retryEx)
                                    {
                                        Log.Error($"STT: retry after restart also failed: {retryEx.Message}");
                                    }
                                }
                                else
                                {
                                    Log.Warning("STT: whisper-server did not come back online within 15s, giving up");
                                }
                            }
                            finally
                            {
                                lock (_restartLock)
                                {
                                    _isRestarting = false;
                                }
                            }
                        }
                    }

                    ThreadManager.AddSingleTaskMainThread("STT_TranscribeError", _ =>
                    {
                        onError?.Invoke(ex.Message);
                    });
                }
            });
        }

        /// <summary>
        /// Poll a TCP port until it accepts connections or timeout expires.
        /// Used after restarting whisper-server to wait for model load completion.
        /// </summary>
        private static bool WaitForPortReady(string host, int port, int maxSeconds)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < maxSeconds * 1000L)
            {
                try
                {
                    using (var client = new System.Net.Sockets.TcpClient())
                    {
                        var result = client.BeginConnect(host, port, null, null);
                        bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                        if (success && client.Connected)
                            return true;
                    }
                }
                catch
                {
                    // Port not ready yet, keep polling
                }
                System.Threading.Thread.Sleep(1000); // Check every second
            }
            return false;
        }

        /// <summary>
        /// Send HTTP POST request to whisper-server with WAV bytes as multipart/form-data.
        /// Steering words and language params are passed as URL query parameters.
        /// Returns transcribed text.
        /// </summary>
        private string SendHttpRequest(string serverUrl, byte[] wavData, int timeoutSeconds, string prompt)
        {
            // Build query parameters
            var queryParams = new System.Collections.Generic.List<string>();

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                queryParams.Add("initial_prompt=" + System.Uri.EscapeDataString(prompt.Trim()));
            }

            // Language: pass explicit ISO 639-1 code ("ja", "de") or omit for auto-detect.
            // Legacy LanguageLocked=true maps to "en" for backward compatibility.
            string lang = _config.Language?.ToLower() ?? "auto";
            if (lang == "auto" && _config.LanguageLocked)
                lang = "en"; // Legacy fallback
            if (lang != "auto")
            {
                queryParams.Add("language=" + System.Uri.EscapeDataString(lang));
            }

            // Translation: when true, Whisper translates foreign speech to English.
            // When false (default), transcribes in the source language.
            queryParams.Add("translate=" + (_config.Translate ? "true" : "false"));

            string url = $"{serverUrl.TrimEnd('/')}/inference";
            if (queryParams.Count > 0)
                url += "?" + string.Join("&", queryParams);

            // Build multipart/form-data body manually (Unity Mono lacks MultipartFormDataContent)
            string boundary = "----WebKitFormBoundary7MA4YWxkTrZu0gW";
            var bodyBuilder = new System.IO.MemoryStream();
            using (var writer = new System.IO.BinaryWriter(bodyBuilder, Encoding.UTF8))
            {
                // File field header
                string header = $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\n";
                writer.Write(Encoding.UTF8.GetBytes(header));

                // WAV file bytes
                writer.Write(wavData);

                // Closing boundary
                writer.Write(Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n"));
            }
            byte[] bodyBytes = bodyBuilder.ToArray();

            var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = $"multipart/form-data; boundary={boundary}";
            request.Timeout = timeoutSeconds * 1000;
            request.ReadWriteTimeout = timeoutSeconds * 1000;
            request.ContentLength = bodyBytes.Length;

            using (var requestStream = request.GetRequestStream())
            {
                requestStream.Write(bodyBytes, 0, bodyBytes.Length);
            }

            return ReadResponse(request);
        }

        /// <summary>
        /// Read and parse the HTTP response from whisper-server.
        /// </summary>
        private static string ReadResponse(System.Net.HttpWebRequest request)
        {
            using (var response = (System.Net.HttpWebResponse)request.GetResponse())
            using (var responseStream = response.GetResponseStream())
            using (var reader = new System.IO.StreamReader(responseStream))
            {
                string responseBody = reader.ReadToEnd();

                // Parse JSON: {"text": "..."} or {"error": "..."} or {"text": "", "empty": true}
                string text = ExtractJsonString(responseBody, "text");
                string error = ExtractJsonString(responseBody, "error");

                if (!string.IsNullOrEmpty(error))
                    throw new Exception($"STT server error: {error}");

                return text;
            }
        }

        /// <summary>
        /// Extract a string value from simple JSON (no dependencies).
        /// </summary>
        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;

            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                json, $@"""{key}""\s*:\s*""([^""]*)""");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Get a human-readable status string.
        /// </summary>
        public string GetStatusString()
        {
            if (!_initialized)
                return "STT: Not initialized";

            return $"STT: Ready (HTTP, {_requestCount} requests, avg {AvgTranscriptionTimeMs:F0}ms)";
        }
    }
}
