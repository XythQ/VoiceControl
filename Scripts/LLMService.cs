using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using XNPCVoiceControl.Core;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Handles communication with local LLM servers via the OpenAI-compatible
    /// /v1/chat/completions endpoint (llama.cpp, LM Studio, etc.).
    /// This is a singleton service that manages all LLM requests for NPCs.
    /// </summary>
    public class LLMService : MonoBehaviour
    {
        // PERFORMANCE TUNING: Optimize global ServicePointManager for high-frequency localhost API calls.
        // This is intentional — we control all HTTP traffic in this mod and want connection pooling + zero-latency POSTs.
        // Safe to mutate once at type load; no other code touches these globals.
        static LLMService()
        {
            ServicePointManager.DefaultConnectionLimit = 20;   // Pool multiple connections per endpoint
            ServicePointManager.Expect100Continue = false;     // Remove ~350ms artificial delay on POST payloads
            ServicePointManager.UseNagleAlgorithm = false;     // Prevent packet buffering/batching latency on localhost
        }

        private static LLMService _instance;
        public static LLMService Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("LLMService");
                    _instance = go.AddComponent<LLMService>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        // === Shared endpoint constants — single source of truth for port numbers ===
        public const int DefaultChatPort = 5055;       // llama-server (chat)
        public const int DefaultEmbedPort = 5056;      // nomic-embed-text (embedding)
        public const string DefaultChatEndpoint = "http://127.0.0.1:5055/v1/chat/completions";
        public const string DefaultEmbedEndpoint = "http://127.0.0.1:5056/v1/embeddings";

        // Configuration loaded from XML
        private string _endpoint = DefaultChatEndpoint;
        private string _model = "llama3";
        private int _timeoutSeconds = 30;
        private int _maxTokens = 512;
        private float _temperature = 0.7f;

        // RAG embedding endpoint (dedicated nomic server on port 5056; falls back to chat server on 5055)
        private string _embeddingEndpoint = DefaultChatEndpoint;

        // RAG extraction prompt (loaded from modconfig.xml, falls back to hardcoded default)
        private string _ragExtractionPrompt;

        // Framing text prepended to the user message for extraction requests
        private string _extractionUserPrefix;

        // Extraction tuning knobs (loaded from modconfig.xml)
        private float _extractionTemperature = 0f;
        private int _maxExtractedFacts = 10;

        // Track ongoing requests to prevent spam
        private HashSet<int> _pendingRequests = new HashSet<int>();

        /// <summary>
        /// True if any LLM request is currently in-flight (blocks warm-up).
        /// </summary>
        public bool IsBusy => _pendingRequests.Count > 0;

        // Performance tracking
        private float _lastResponseTimeMs = 0;
        private float _avgResponseTimeMs = 0;
        private int _requestCount = 0;

        public void Initialize(LLMConfig config)
        {
            _endpoint = config.Endpoint;
            _model = config.Model;
            _timeoutSeconds = config.TimeoutSeconds;
            _maxTokens = config.MaxTokens;
            _temperature = config.Temperature;

            Log.Out($"LLMService initialized - Endpoint: {_endpoint}, Model: {_model}");
        }

        /// <summary>
        /// Set the RAG extraction system prompt (loaded from modconfig.xml).
        /// Call during mod init. Falls back to hardcoded default if never set.
        /// </summary>
        public void SetRagExtractionPrompt(string prompt)
        {
            _ragExtractionPrompt = prompt;
        }

        /// <summary>
        /// Set the user message prefix for extraction requests (loaded from modconfig.xml).
        /// </summary>
        public void SetExtractionUserPrefix(string prefix)
        {
            _extractionUserPrefix = prefix;
        }

        /// <summary>
        /// Set the temperature for extraction requests (loaded from modconfig.xml, default 0.0).
        /// Clamped to [0.0, 0.8] — higher values risk hallucination on a 3B model.
        /// </summary>
        public void SetExtractionTemperature(float temp)
        {
            _extractionTemperature = Mathf.Clamp(temp, 0f, 0.8f);
        }

        /// <summary>
        /// Set the embedding endpoint URL (loaded from modconfig.xml — points at dedicated nomic server on 5056).
        /// </summary>
        public void SetEmbeddingEndpoint(string url)
        {
            if (!string.IsNullOrEmpty(url))
                _embeddingEndpoint = url;
        }

        /// <summary>
        /// Set the maximum number of facts to extract per request (loaded from modconfig.xml, default 10).
        /// Clamped to [1, 50].
        /// </summary>
        public void SetMaxExtractedFacts(int max)
        {
            _maxExtractedFacts = Mathf.Clamp(max, 1, 50);
        }

        // Hardcoded fallback if ragconfig.xml is missing or the node is empty
        private const string DefaultRagExtractionPrompt = "You are a fact extraction engine. Analyze the conversation and extract any permanent, long-term facts about the SURVIVOR (player). If there are facts, set has_new_facts to true and list them. If there are no facts (e.g., questions, greetings, immediate actions), set has_new_facts to false and leave the array empty.";

        // Framing text prepended to the user message for extraction requests
        private const string DefaultExtractionUserPrefix = "Here is the transcript to extract:\n\n";

        public float LastResponseTimeMs => _lastResponseTimeMs;
        public float AvgResponseTimeMs => _avgResponseTimeMs;
        public int RequestCount => _requestCount;

        /// <summary>
        /// Get the currently active model name.
        /// </summary>
        public string CurrentModel => _model;

        /// <summary>
        /// Get the current embedding endpoint URL (for diagnostics).
        /// </summary>
        public string EmbeddingEndpoint => _embeddingEndpoint;

        /// <summary>
        /// Get the current max tokens setting.
        /// </summary>
        public int GetCurrentMaxTokens() => _maxTokens;

        /// <summary>
        /// Update max tokens at runtime (e.g., from in-game settings UI).
        /// </summary>
        public void SetMaxTokens(int value)
        {
            if (value > 0)
            {
                _maxTokens = value;
                Log.Debug(() => $"LLM max_tokens changed to: {_maxTokens}");
            }
        }

        /// <summary>
        /// Check if an NPC already has a pending LLM request.
        /// </summary>
        internal bool IsRequestPending(int npcId)
        {
            return _pendingRequests.Contains(npcId);
        }
        /// </summary>
        /// <param name="npcId">Unique NPC entity ID for tracking</param>
        /// <param name="systemPrompt">The NPC's personality/context</param>
        /// <param name="conversationHistory">Previous exchanges for context</param>
        /// <param name="playerMessage">The player's input message</param>
        /// <param name="interruptToken">Cancellation token from Interruption Matrix</param>
        /// <param name="onResponse">Callback with the LLM's response (marshaled to main thread)</param>
        /// <param name="onError">Callback if request fails (marshaled to main thread)</param>
        public void SendChatRequest(
            int npcId,
            string systemPrompt,
            List<ChatMessage> conversationHistory,
            string playerMessage,
            CancellationToken interruptToken,
            Action<string> onResponse,
            Action<string> onError)
        {
            // Prevent duplicate requests for same NPC
            if (_pendingRequests.Contains(npcId))
            {
                onError?.Invoke("Request already in progress for this NPC");
                return;
            }

            _pendingRequests.Add(npcId);
            StartCoroutine(SendRequestCoroutine(npcId, systemPrompt, conversationHistory, playerMessage, interruptToken, onResponse, onError));
        }

        private IEnumerator SendRequestCoroutine(
            int npcId,
            string systemPrompt,
            List<ChatMessage> conversationHistory,
            string playerMessage,
            CancellationToken interruptToken,
            Action<string> onResponse,
            Action<string> onError)
        {
            string requestBody;

            // OpenAI-compatible /v1/chat/completions format (llama.cpp, LM Studio, etc.)
            requestBody = BuildOpenAIRequest(systemPrompt, conversationHistory, playerMessage);

            float startTime = Time.realtimeSinceStartup;

            using (UnityWebRequest request = new UnityWebRequest(_endpoint, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(requestBody);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = _timeoutSeconds;

                Log.Debug(() => $"Sending request to LLM for NPC {npcId}");
                Log.Debug(() => $"Endpoint: {_endpoint}");
                Log.Debug(() => $"Model: {_model}");
                Log.Debug(() => $"Request body (first 200 chars): {requestBody.Substring(0, Math.Min(200, requestBody.Length))}");

                // Send the request (starts async operation)
                AsyncOperation asyncOp = request.SendWebRequest();

                // Poll frame-by-frame for cancellation while waiting for response
                while (!asyncOp.isDone)
                {
                    if (interruptToken.IsCancellationRequested)
                    {
                        Log.Out($"[INTERRUPT] LLM request for NPC {npcId} aborted by Interruption Matrix.");
                        request.Abort();
                        _pendingRequests.Remove(npcId);
                        onError?.Invoke("Request interrupted — NPC entered combat or took damage.");
                        yield break;
                    }
                    yield return null;
                }

                _pendingRequests.Remove(npcId);

                // Track performance
                _lastResponseTimeMs = (Time.realtimeSinceStartup - startTime) * 1000f;
                _requestCount++;
                _avgResponseTimeMs = ((_avgResponseTimeMs * (_requestCount - 1)) + _lastResponseTimeMs) / _requestCount;

                if (request.result == UnityWebRequest.Result.Success)
                {
                    string response = ParseResponse(request.downloadHandler.text);
                    if (!string.IsNullOrEmpty(response))
                    {
                        Log.Debug(() => $"Got response for NPC {npcId} in {_lastResponseTimeMs:F0}ms: {response.Substring(0, Math.Min(50, response.Length))}...");
                        onResponse?.Invoke(response);
                    }
                    else
                    {
                        onError?.Invoke("Empty response from LLM");
                    }
                }
                else
                {
                    string error = $"LLM request failed: {request.error}";
                    Log.Warning($"{error}");
                    Log.Warning($"Response code: {request.responseCode}");
                    string responseText = request.downloadHandler?.text ?? "";
                    Log.Warning($"Response text: {(responseText.Length > 80 ? responseText.Substring(0, 80) + "..." : responseText)}");
                    onError?.Invoke(error);
                }
            }
        }

        private string BuildOpenAIRequest(string systemPrompt, List<ChatMessage> history, string playerMessage)
        {
            return BuildOpenAIRequest(systemPrompt, history, playerMessage, _temperature, null);
        }

        /// <summary>
        /// Build an OpenAI-compatible request with optional response_format for structured outputs.
        /// </summary>
        private string BuildOpenAIRequest(string systemPrompt, List<ChatMessage> history, string playerMessage, float temperature, string responseFormatJson, int maxTokens = 0, bool stream = false)
        {
            // Build messages array
            StringBuilder messages = new StringBuilder();
            messages.Append($@"{{""role"": ""system"", ""content"": ""{EscapeJson(systemPrompt)}""}}");

            foreach (var msg in history)
            {
                string role = msg.Role.ToLower() == "player" ? "user" : "assistant";
                messages.Append($@", {{""role"": ""{role}"", ""content"": ""{EscapeJson(msg.Content)}""}}");
            }
            messages.Append($@", {{""role"": ""user"", ""content"": ""{EscapeJson(playerMessage)}""}}");

            // OpenAI-compatible format with optional response_format
            string responseFormat = responseFormatJson != null
                ? $@",
                ""response_format"": {responseFormatJson}
                "
                : "";

            int effectiveMaxTokens = maxTokens > 0 ? maxTokens : _maxTokens;
            string streamField = stream ? @",
                ""stream"": true" : "";

            return $@"{{
                ""model"": ""{_model}"",
                ""messages"": [{messages}],
                ""temperature"": {temperature},
                ""max_tokens"": {effectiveMaxTokens}{responseFormat}{streamField}
            }}";
        }

        /// <summary>
        /// Escape a string for JSON embedding (used when building request bodies).
        /// </summary>
        private static string EscapeJson(string str)
        {
            if (string.IsNullOrEmpty(str)) return str;
            string quoted = Newtonsoft.Json.JsonConvert.ToString(str);
            return quoted.Substring(1, quoted.Length - 2);
        }

        /// <summary>
        /// Extract the spoken_text value from a partial (possibly incomplete) JSON string.
        /// Handles escape sequences. Sets isDone=true if the closing quote was found.
        /// Returns null if "spoken_text" key hasn't appeared yet.
        /// </summary>
        private static string ExtractPartialSpokenText(string json, out bool isDone)
        {
            isDone = false;

            int keyIdx = json.IndexOf("\"spoken_text\"", StringComparison.Ordinal);
            if (keyIdx < 0) return null;

            int colonIdx = json.IndexOf(':', keyIdx + 13);
            if (colonIdx < 0) return null;

            int openQuote = json.IndexOf('"', colonIdx + 1);
            if (openQuote < 0) return null;

            int pos = openQuote + 1;
            var sb = new StringBuilder();
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c == '\\' && pos + 1 < json.Length)
                {
                    char next = json[pos + 1];
                    switch (next)
                    {
                        case '"':  sb.Append('"');  pos += 2; break;
                        case '\\': sb.Append('\\'); pos += 2; break;
                        case 'n':  sb.Append('\n'); pos += 2; break;
                        case 'r':  sb.Append('\r'); pos += 2; break;
                        case 't':  sb.Append('\t'); pos += 2; break;
                        default:   sb.Append(next); pos += 2; break;
                    }
                }
                else if (c == '"')
                {
                    isDone = true;
                    break;
                }
                else
                {
                    sb.Append(c);
                    pos++;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Stream a chat request to the LLM. Calls onChunk for each detected sentence
        /// as tokens arrive, then onComplete with the full accumulated response when done.
        /// Both callbacks are marshaled to Unity's main thread.
        /// </summary>
        public void SendStreamingChatRequest(
            int npcId,
            string systemPrompt,
            List<ChatMessage> conversationHistory,
            string playerMessage,
            CancellationToken interruptToken,
            Action<string> onChunk,
            Action<string> onComplete,
            Action<string> onError)
        {
            if (_pendingRequests.Contains(npcId))
            {
                onError?.Invoke("Request already in progress for this NPC");
                return;
            }
            _pendingRequests.Add(npcId);

            string body = BuildOpenAIRequest(systemPrompt, conversationHistory, playerMessage, _temperature, null, 0, stream: true);
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

            Task.Run(() =>
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(_endpoint);
                    request.KeepAlive = true;
                    request.Proxy = null;
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Timeout = _timeoutSeconds * 1000;
                    request.ReadWriteTimeout = _timeoutSeconds * 1000;

                    using (interruptToken.Register(() => request.Abort(), useSynchronizationContext: false))
                    {
                        try
                        {
                            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                            request.ContentLength = bodyBytes.Length;
                            using (var reqStream = request.GetRequestStream())
                                reqStream.Write(bodyBytes, 0, bodyBytes.Length);

                            long sentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            Log.Debug(() => $"[TIMING] Chat request SENT t={sentMs}ms");

                            using (var response = (HttpWebResponse)request.GetResponse())
                            using (var reader = new StreamReader(response.GetResponseStream()))
                            {
                                var fullText = new StringBuilder();
                                bool jsonMode = false;
                                bool jsonChecked = false;
                                bool spokenTextDone = false;
                                int sentencePos = 0;

                                string line;
                                while ((line = reader.ReadLine()) != null)
                                {
                                    if (interruptToken.IsCancellationRequested) break;
                                    if (string.IsNullOrEmpty(line)) continue;
                                    if (!line.StartsWith("data: ")) continue;

                                    string data = line.Substring(6).Trim();
                                    if (data == "[DONE]") break;

                                    string delta = JsonHelper.GetStreamingDelta(data);
                                    if (delta == null) continue;

                                    fullText.Append(delta);
                                    string full = fullText.ToString();

                                    // Detect JSON vs plain text on first meaningful content
                                    if (!jsonChecked)
                                    {
                                        string trimmed = full.TrimStart();
                                        if (trimmed.Length > 0)
                                        {
                                            jsonMode = trimmed[0] == '{';
                                            jsonChecked = true;
                                        }
                                    }
                                    if (!jsonChecked) continue;

                                    // Get the current spoken text (partial, may be growing)
                                    string spokenSoFar;
                                    if (jsonMode)
                                    {
                                        if (spokenTextDone) continue;
                                        spokenSoFar = ExtractPartialSpokenText(full, out spokenTextDone);
                                        if (spokenSoFar == null) continue;
                                    }
                                    else
                                    {
                                        spokenSoFar = full;
                                    }

                                    // Scan for sentence boundaries from sentencePos forward
                                    for (int i = sentencePos; i < spokenSoFar.Length - 1; i++)
                                    {
                                        char c = spokenSoFar[i];
                                        char next = spokenSoFar[i + 1];
                                        if ((c == '.' || c == '!' || c == '?') &&
                                            (next == ' ' || next == '\n' || next == '"') &&
                                            (i - sentencePos >= 30))
                                        {
                                            string sentence = spokenSoFar.Substring(sentencePos, i - sentencePos + 1).Trim();
                                            sentencePos = i + 1;
                                            if (!string.IsNullOrWhiteSpace(sentence))
                                            {
                                                string captured = sentence;
                                                MainThreadDispatcher.Enqueue(() => onChunk?.Invoke(captured));
                                            }
                                        }
                                    }
                                }

                                // Flush any remaining text after [DONE] (last sentence may lack trailing punctuation)
                                string finalSpoken;
                                if (jsonMode)
                                    finalSpoken = ExtractPartialSpokenText(fullText.ToString(), out _);
                                else
                                    finalSpoken = fullText.ToString();

                                if (finalSpoken != null && sentencePos < finalSpoken.Length)
                                {
                                    string tail = finalSpoken.Substring(sentencePos).Trim();
                                    if (!string.IsNullOrWhiteSpace(tail))
                                    {
                                        string captured = tail;
                                        MainThreadDispatcher.Enqueue(() => onChunk?.Invoke(captured));
                                    }
                                }

                                _pendingRequests.Remove(npcId);
                                long elapsed = sw.ElapsedMilliseconds;
                                Log.Debug(() => $"[TIMING] Streaming complete for NPC {npcId} in {elapsed}ms");

                                string fullResponse = fullText.ToString();
                                MainThreadDispatcher.Enqueue(() => onComplete?.Invoke(fullResponse));
                            }
                        }
                        catch (WebException) when (interruptToken.IsCancellationRequested)
                        {
                            _pendingRequests.Remove(npcId);
                            MainThreadDispatcher.Enqueue(() => onError?.Invoke("Request interrupted"));
                        }
                        catch (WebException ex)
                        {
                            _pendingRequests.Remove(npcId);
                            string err = $"LLM streaming request failed: {ex.Message}";
                            Log.Warning(err);
                            MainThreadDispatcher.Enqueue(() => onError?.Invoke(err));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _pendingRequests.Remove(npcId);
                    Log.Warning($"[STREAMING] Outer error for NPC {npcId}: {ex.Message}");
                    MainThreadDispatcher.Enqueue(() => onError?.Invoke($"Streaming error: {ex.Message}"));
                }
            }, interruptToken);
        }

        /// <summary>
        /// Extract a string value for a given JSON key from raw JSON text.
        /// Zero-GC fallback when JsonUtility fails on nested structures.
        /// Handles escaped quotes within values.
        /// </summary>
        private static string ExtractJsonStringField(string json, string key)
        {
            return JsonHelper.GetString(json, key);
        }

        /// <summary>
        /// Extract embedding float array from JSON using Newtonsoft.Json.
        /// Replaces hand-rolled scanner with proper JSON parsing.
        /// </summary>
        private static float[] ExtractEmbeddingArray(string json)
        {
            return JsonHelper.GetEmbeddingFromOpenAIResponse(json);
        }

        /// <summary>
        /// Parse "content" from OpenAI-compatible LLM JSON response using JsonUtility.
        /// Falls back to reasoning_content if content is empty (reasoning models).
        /// </summary>
        private string ParseResponse(string jsonResponse)
        {
            // Strip markdown backticks — some LLMs wrap JSON in ```json ... ```
            string cleaned = jsonResponse.Trim();
            if (cleaned.StartsWith("```"))
            {
                int endIdx = cleaned.IndexOf("```", 3);
                cleaned = endIdx > 0
                    ? cleaned.Substring(3, endIdx - 3).Trim()
                    : cleaned.Substring(3).Trim();
            }

            string content = JsonHelper.GetLLMContent(cleaned);
            if (!string.IsNullOrWhiteSpace(content))
                return CleanResponse(content);

            // Reasoning model fallback: content empty but reasoning_content present
            string reasoning = JsonHelper.GetLLMReasoningContent(cleaned);
            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                Log.Warning("Model returned empty content but had reasoning_content — increase max_tokens. Using reasoning as fallback.");
                return CleanResponse(reasoning);
            }

            Log.Warning($"Could not parse LLM response: {cleaned.Substring(0, Math.Min(300, cleaned.Length))}");
            return null;
        }

        /// <summary>
        /// Remove common LLM formatting artifacts from a response string.
        /// If the text is JSON, extracts dialogue/text/content/message field via JsonUtility.
        /// Falls back to manual parsing for malformed JSON.
        /// </summary>
        private string CleanResponse(string response)
        {
            if (string.IsNullOrEmpty(response)) return response;

            string cleaned = response.Trim();

            // If the response looks like JSON, try to extract a text field from it
            if (cleaned.StartsWith("{") && cleaned.Contains("\""))
            {
                // Try JsonUtility first for clean extraction
                try
                {
                    var obj = JsonUtility.FromJson<LlmResponseObject>(cleaned);
                    string extracted = !string.IsNullOrEmpty(obj.dialogue) ? obj.dialogue
                        : !string.IsNullOrEmpty(obj.text) ? obj.text
                        : !string.IsNullOrEmpty(obj.content) ? obj.content
                        : !string.IsNullOrEmpty(obj.message) ? obj.message
                        : null;
                    if (!string.IsNullOrEmpty(extracted))
                    {
                        cleaned = extracted;
                        goto after_json_extraction;
                    }
                }
                catch { /* Invalid JSON — fall through to manual parsing */ }

                // Manual fallback for malformed JSON (best-effort)
                foreach (string field in new[] { "\"dialogue\"", "\"text\"", "\"content\"", "\"message\"" })
                {
                    int fieldIndex = cleaned.IndexOf(field, StringComparison.OrdinalIgnoreCase);
                    if (fieldIndex >= 0)
                    {
                        int colonIndex = cleaned.IndexOf(':', fieldIndex);
                        if (colonIndex > 0)
                        {
                            int quoteStart = cleaned.IndexOf('"', colonIndex);
                            if (quoteStart > 0)
                            {
                                int quoteEnd = quoteStart + 1;
                                while (quoteEnd < cleaned.Length)
                                {
                                    if (cleaned[quoteEnd] == '"' && cleaned[quoteEnd - 1] != '\\')
                                        break;
                                    quoteEnd++;
                                }
                                if (quoteEnd > quoteStart + 1)
                                {
                                    cleaned = cleaned.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                                    goto after_json_extraction;
                                }
                            }
                        }
                    }
                }
            }

after_json_extraction:
            // Remove "Response:" prefix (case insensitive)
            if (cleaned.StartsWith("Response:", System.StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(9).TrimStart();
            }

            // Remove "NPC:" prefix if present
            if (cleaned.StartsWith("NPC:", System.StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(4).TrimStart();
            }

            return cleaned;
        }

        #region RAG Embedding + Memory Ledger (HttpWebRequest on background threads)

        /// <summary>
        /// Get a vector embedding for the given text from llama-server /v1/embeddings.
        /// Uses HttpWebRequest on a background thread — survives Unity shutdown.
        /// </summary>
        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new float[0];

            return await Task.Run(() =>
            {
                string url = _embeddingEndpoint;
                string body = $@"{{""input"": ""{EscapeJson(text)}""}}";

                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.KeepAlive = true;
                    request.Proxy = null;
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Timeout = 120000; // 120s — llama-server processes sequentially

                    using (var writer = new StreamWriter(request.GetRequestStream()))
                        writer.Write(body);

                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        string rawResponse = reader.ReadToEnd();
                        Log.Debug(() => $"[RAG] Raw embedding response ({rawResponse.Length} bytes)");

                        float[] embedding = ExtractEmbeddingArray(rawResponse);
                        if (embedding != null && embedding.Length > 0)
                        {
                            Log.Debug(() => $"[RAG] Embedding extracted: dim={embedding.Length}");
                            return embedding;
                        }

                        Log.Warning($"[RAG] Embedding failed: no array found in {rawResponse.Length}-byte response (first 80 chars: {rawResponse.Substring(0, Math.Min(80, rawResponse.Length))})");
                        return new float[0];
                    }
                }
                catch (WebException ex)
                {
                    string errorBody = "";
                    if (ex.Response is HttpWebResponse errResp)
                    {
                        using (var reader = new StreamReader(errResp.GetResponseStream()))
                            errorBody = reader.ReadToEnd();
                    }
                    Log.Warning($"[RAG] Embedding request failed: {ex.Message} (body: {errorBody.Substring(0, Math.Min(80, errorBody.Length))})");
                    return new float[0];
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RAG] Embedding request failed: {ex.Message}");
                    return new float[0];
                }
            });
        }

        /// <summary>
        /// Ask the LLM to extract permanent facts from a conversation buffer.
        /// Uses HttpWebRequest on a background thread — survives Unity shutdown.
        /// Returns a concise third-person summary, or "NONE" if nothing noteworthy.
        /// </summary>
        public async Task<string> GetMemoryLedgerAsync(List<ChatMessage> buffer, string npcName, CancellationToken token = default)
        {
            return await Task.Run(() =>
            {
                // Use config-loaded prompt (falls back to default if not set)
                string systemPrompt = !string.IsNullOrEmpty(_ragExtractionPrompt)
                    ? _ragExtractionPrompt
                    : DefaultRagExtractionPrompt;

                // Build conversation text from buffer with explicit speaker labels
                StringBuilder convText = new StringBuilder();
                for (int i = 0; i < buffer.Count; i++)
                {
                    string label = buffer[i].Role;
                    if (label.Equals("Player", StringComparison.OrdinalIgnoreCase))
                        label = "SURVIVOR";
                    else if (label.Equals("NPC", StringComparison.OrdinalIgnoreCase))
                        label = npcName ?? "NPC";
                    convText.Append(label).Append(": ").Append(buffer[i].Content).Append("\n");
                }

                // Prepend the config-loaded prefix with NPC name context
                string prefix = !string.IsNullOrEmpty(_extractionUserPrefix)
                    ? _extractionUserPrefix
                    : DefaultExtractionUserPrefix;
                string userMessageText = $"{prefix} Transcript of interaction between SURVIVOR and {npcName ?? "NPC"}:\n\n{convText}";

                // Plain text output — no JSON schema. 256 max tokens for speed.
                string body = BuildOpenAIRequest(systemPrompt, new List<ChatMessage>(), userMessageText, _extractionTemperature, null, 1024);

                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(_endpoint);
                    request.KeepAlive = true;
                    request.Proxy = null;
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Timeout = _timeoutSeconds * 1000;

                    using (token.Register(() => request.Abort(), useSynchronizationContext: false))
                    {
                        try
                        {
                            using (var writer = new StreamWriter(request.GetRequestStream()))
                                writer.Write(body);

                            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                            using (var reader = new StreamReader(response.GetResponseStream()))
                            {
                                string rawResponse = reader.ReadToEnd();

                                string summary = ParseExtractionResponse(rawResponse, npcName);

                                // Enforce max facts cap
                                if (summary != null && _maxExtractedFacts > 0)
                                {
                                    int factCount = summary.Split('\n').Length;
                                    if (factCount > _maxExtractedFacts)
                                    {
                                        string[] parts = summary.Split('\n');
                                        summary = string.Join("\n", parts.Take(_maxExtractedFacts));
                                        Log.Debug(() => $"[RAG] Truncated extraction from {factCount} to {_maxExtractedFacts} facts");
                                    }
                                }

                                Log.Debug(() => $"[RAG] Ledger Summary Generated: {summary ?? "null"}");
                                return summary ?? "NONE";
                            }
                        }
                        catch (WebException) when (token.IsCancellationRequested)
                        {
                            Log.Debug(() => $"[RAG] Extraction for {npcName} cancelled — llama-server freed for conversation");
                            return "NONE";
                        }
                        catch (WebException ex)
                        {
                            string errorBody = "";
                            if (ex.Response is HttpWebResponse errResp)
                            {
                                using (var reader = new StreamReader(errResp.GetResponseStream()))
                                    errorBody = reader.ReadToEnd();
                            }
                            Log.Warning($"[RAG] Ledger request failed: {ex.Message} (body: {errorBody.Substring(0, Math.Min(80, errorBody.Length))})");
                            return "NONE";
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"[RAG] Ledger request failed: {ex.Message}");
                            return "NONE";
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RAG] Ledger request outer failed: {ex.Message}");
                    return "NONE";
                }
            }, token);
        }

        /// <summary>
        /// Parse plain-text extraction response (one fact per line) and return comma-separated summary.
        /// Returns null if no facts were extracted.
        /// </summary>
        private static string ParseExtractionResponse(string rawText, string npcName)
        {
            if (string.IsNullOrEmpty(rawText)) return null;

            // Extract content from chat completion wrapper (choices[0].message.content)
            string content = JsonHelper.GetLLMContent(rawText.Trim());
            if (string.IsNullOrEmpty(content))
            {
                // Reasoning model fallback: content empty but reasoning_content present
                string reasoning = JsonHelper.GetLLMReasoningContent(rawText.Trim());
                if (!string.IsNullOrEmpty(reasoning))
                {
                    Log.Debug(() => "[RAG] Extraction: content empty, using reasoning_content as fallback");
                    content = reasoning;
                }
                else
                {
                    Log.Debug(() => "[RAG] Extraction: API content field empty (model used all tokens on thinking) — skipping");
                    return null;
                }
            }

            // Strip markdown fences
            if (content.StartsWith("```"))
            {
                int endIdx = content.IndexOf("```", 3);
                content = endIdx > 0 ? content.Substring(3, endIdx - 3).Trim() : content.Substring(3).Trim();
                // Strip language tag on first line if present (e.g. "json\n" or "text\n")
                int firstNewline = content.IndexOf('\n');
                if (firstNewline > 0 && firstNewline < 10)
                    content = content.Substring(firstNewline + 1).Trim();
            }

            Log.Debug(() => $"[RAG] Raw extraction content: {content}");
            // NONE check
            if (content.TrimStart().StartsWith("NONE", StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug(() => "[RAG] Extraction returned NONE — no facts found");
                return null;
            }

            // Parse one fact per line
            string[] lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var facts = new List<string>();
            foreach (string line in lines)
            {
                string trimmed = line.Trim().TrimStart('-', '*', '\u2022').Trim();
                trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, @"^\d+[.)\]]\s*", "").Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (trimmed.StartsWith("NONE", StringComparison.OrdinalIgnoreCase)) continue;
                // Lenient tagging: 3B model won't reliably emit [SURVIVOR]/[NPC] brackets
                string taggedLine = null;

                // Case 1: already has [SURVIVOR] or [NPC] tag — keep as-is
                if (trimmed.StartsWith("[SURVIVOR]", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("[NPC]", StringComparison.OrdinalIgnoreCase))
                {
                    taggedLine = trimmed;
                }
                else
                {
                    string lower = trimmed.ToLowerInvariant();

                    // Case 2: starts with (optional "the ") survivor or player → [SURVIVOR]
                    if ((lower.StartsWith("survivor") || lower.StartsWith("the survivor")) ||
                        (lower.StartsWith("player") || lower.StartsWith("the player")))
                    {
                        taggedLine = "[SURVIVOR] " + trimmed;
                    }
                    // Case 3: starts with (optional "the ") npcName or "npc" → [NPC]
                    else if (!string.IsNullOrEmpty(npcName) &&
                             (lower.StartsWith(npcName.ToLowerInvariant()) ||
                              lower.StartsWith("the " + npcName.ToLowerInvariant())))
                    {
                        taggedLine = "[NPC] " + trimmed;
                    }
                    else if (lower.StartsWith("npc") || lower.StartsWith("the npc"))
                    {
                        taggedLine = "[NPC] " + trimmed;
                    }
                    // Case 4: looks like a fact sentence — keep untagged rather than lose it
                    else if (!trimmed.EndsWith(":") && !IsMetaPreamble(trimmed))
                    {
                        taggedLine = trimmed;
                    }
                }

                if (taggedLine == null) continue;

                // Extract content after any tag for placeholder check
                int bracketEnd = taggedLine.IndexOf(']');
                string factContent = bracketEnd >= 0 ? taggedLine.Substring(bracketEnd + 1).Trim() : taggedLine.Trim();
                if (IsPlaceholderFact(factContent)) continue;

                facts.Add(taggedLine);
            }

            return facts.Count > 0 ? string.Join("\n", facts) : null;
        }

        /// <summary>
        /// Check if a line is meta-preamble text ("here are the facts", "based on", etc.)
        /// rather than an actual extracted fact.
        /// </summary>
        private static bool IsMetaPreamble(string text)
        {
            string lower = text.ToLowerInvariant();
            if (lower.StartsWith("here ") || lower.StartsWith("the following") ||
                lower.StartsWith("based on") || lower.StartsWith("from the") ||
                lower.StartsWith("in this transcript"))
                return true;
            return false;
        }

        /// <summary>
        /// Check if a fact's content (after the [SURVIVOR]/[NPC] tag) is a placeholder
        /// rather than a real extracted fact. Catches model hallucinations like
        /// "no fact", "N/A", "nothing to extract", etc.
        /// </summary>
        public static bool IsPlaceholderFact(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;

            string normalized = text.Trim().TrimEnd('.', '!', ',', ' ').ToLowerInvariant();
            if (normalized.Length == 0) return true;

            // Exact matches
            if (normalized == "none" || normalized == "n/a" || normalized == "na") return true;
            if (normalized == "unknown" || normalized == "nothing") return true;

            // Contains matches (catches "no fact", "no new facts", etc.)
            if (normalized.Contains("no fact")) return true;
            if (normalized.Contains("nothing to extract")) return true;
            if (normalized.Contains("no applicable")) return true;
            if (normalized.Contains("no new fact")) return true;
            if (normalized.Contains("no relevant")) return true;
            if (normalized.Contains("no information")) return true;
            if (normalized.Contains("not applicable")) return true;
            if (normalized.Contains("not mentioned")) return true;
            // Known few-shot example echo — the model regurgitating the ExtractionPrompt's
            // format examples verbatim instead of extracting real facts. Covers both the OLD
            // examples already in saved files and the new placeholder examples.
            if (normalized == "billy is a former soldier") return true;
            if (normalized == "the player owns a red convertible") return true;
            if (normalized == "the player owns a green dirt bike") return true;
            if (normalized.Contains("zeke mentioned he used to repair radios")) return true;

            return false;
        }

        #endregion

        #region Loaded Chamber — Buffered Greeting Generation

        /// <summary>
        /// Generate a real, personality-driven greeting in the background.
        /// Uses the NPC's assigned personality, RAG memories, and a system instruction
        /// to produce a 1-sentence greeting. When the player interacts, this is played
        /// instantly (0ms TTFA) before processing their actual query.
        ///
        /// Runs entirely on a background thread using HttpWebRequest.
        /// </summary>
        public async Task<string> GenerateBufferedGreetingAsync(
            string npcName,
            EntityAlive npcEntity,
            string playerName,
            LLMConfig config,
            PersonalityDefinition personality = null,
            NPCSenseSnapshot senseSnapshot = null,
            CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(npcName) || config == null)
                return null;

            // Personality is pre-resolved by caller on main thread (Unity API safety).
            // If not provided, fall back to resolving here (also main thread).
            if (personality == null && npcEntity != null && PersonalityManager.Instance != null)
            {
                personality = PersonalityManager.Instance.AssignPersonality(npcEntity);
            }

            return await Task.Run(() =>
            {
                try
                {
                    // --- Step 2: Build system prompt (mirrors NPCChatComponent.BuildSystemPrompt) ---
                    string basePrompt = config.SystemPrompt;

                    // TTS rule: enforce English for spoken_text
                    string ttsRule = "\nCRITICAL TTS INSTRUCTION: Your spoken_text must be exclusively in English. Do NOT use Chinese characters, Japanese characters, or any non-Latin script in your spoken_text output. The subtitle_localized field may use the player's UI language.";

                    // Identity + location + stranger rule
                    string identityPrompt = $"Your name is {npcName}. ";
                    string locationContext = npcEntity != null ? "You are currently surviving in the wasteland. " : "";
                    string strangerIdentity = "You are speaking to a fellow survivor. You do not know their name unless it is explicitly provided to you in your [SURVIVOR MEMORIES]. If you do not know their name, refer to them using natural, generic terms (e.g., 'stranger', 'traveler', 'friend', or simply 'you'). You must NEVER refer to the survivor by your own name. ";

                    string systemPrompt;
                    if (personality != null && !string.IsNullOrEmpty(personality.Traits))
                    {
                        systemPrompt = $"{identityPrompt}{locationContext}{strangerIdentity}{personality.Traits} {basePrompt}{ttsRule}";
                    }
                    else
                    {
                        systemPrompt = $"{identityPrompt}{locationContext}{strangerIdentity}{basePrompt}{ttsRule}";
                    }

                    // --- Step 3: Retrieve RAG memories (player approach context) ---
                    string memoryContext = "";
                    if (NPCMemoryManager.Instance != null && !string.IsNullOrEmpty(playerName))
                    {
                        try
                        {
                            memoryContext = NPCMemoryManager.Instance.GetRelevantContextAsync(
                                playerName, npcName, "The player approached.").Result;
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"[RAG] Memory retrieval failed during greeting warmup: {ex.GetType().Name}: {ex.Message}");
                            memoryContext = "";
                        }
                    }

                    // --- Step 4: Inject memory context into system prompt ---
                    string finalSystemPrompt = systemPrompt;
                    if (!string.IsNullOrEmpty(memoryContext))
                    {
                        string survivorNoun = !string.IsNullOrEmpty(playerName) && playerName != "Survivor"
                            ? playerName
                            : "the survivor";

                        var npcMemories = new List<string>();
                        var survivorMemories = new List<string>();

                        string[] rawFacts = memoryContext.Split(new[] { ", " }, StringSplitOptions.None);
                        foreach (string raw in rawFacts)
                        {
                            string trimmed = raw.Trim().Trim('-', ' ');
                            if (trimmed.StartsWith("[NPC] "))
                            {
                                npcMemories.Add(trimmed.Substring(6).Replace("The Player", survivorNoun));
                            }
                            else if (trimmed.StartsWith("[SURVIVOR] "))
                            {
                                survivorMemories.Add(trimmed.Substring(11).Replace("The Player", survivorNoun));
                            }
                            else if (!trimmed.StartsWith("[") && trimmed.Length > 3)
                            {
                                survivorMemories.Add(trimmed.Replace("The Player", survivorNoun));
                            }
                        }

                        StringBuilder contextBlock = new StringBuilder();
                        if (npcMemories.Count > 0)
                        {
                            contextBlock.Append("[ABOUT YOU]\n");
                            foreach (string m in npcMemories)
                                contextBlock.Append($"- {m}\n");
                            contextBlock.Append($"These are facts about yourself. You know this is your status.\n\n");
                        }
                        if (survivorMemories.Count > 0)
                        {
                            contextBlock.Append("[ABOUT YOUR COMPANION ").Append(survivorNoun).Append("]\n");
                            foreach (string m in survivorMemories)
                                contextBlock.Append($"- {m}\n");
                            contextBlock.Append($"These are facts about {survivorNoun}. Reference them naturally when relevant.");
                        }

                        finalSystemPrompt = systemPrompt + "\n\n" + contextBlock.ToString();
                    }

                    // --- Step 4.5: Inject environmental sense snapshot ---
                    string senseBlock = senseSnapshot?.ToPromptString() ?? "";
                    if (!string.IsNullOrEmpty(senseBlock))
                    {
                        finalSystemPrompt += senseBlock;
                    }

                    // --- Step 5: Append greeting instruction ---
                    finalSystemPrompt += "\n\n[SYSTEM: The player has just approached you. Generate a short, 1-sentence greeting based on your personality and environment. Do not ask questions.]";

                    // --- Step 6: HTTP POST to llama-server ---
                    string body = BuildOpenAIRequest(finalSystemPrompt, new List<ChatMessage>(), "The player has approached you.", config.Temperature, null);

                    var request = (HttpWebRequest)WebRequest.Create(_endpoint);
                    request.KeepAlive = true;
                    request.Proxy = null;
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Timeout = config.TimeoutSeconds * 1000;
                    request.ReadWriteTimeout = config.TimeoutSeconds * 1000;

                    byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                    request.ContentLength = bodyBytes.Length;

                    // Bind cancellation token to HTTP request
                    using (token.Register(() => request.Abort(), useSynchronizationContext: false))
                    {
                        try
                        {
                            using (var stream = request.GetRequestStream())
                                stream.Write(bodyBytes, 0, bodyBytes.Length);

                            using (var response = (HttpWebResponse)request.GetResponse())
                            using (var reader = new StreamReader(response.GetResponseStream()))
                            {
                                string responseBody = reader.ReadToEnd();

                                // Step 1: Validate response is JSON, extract content from OpenAI envelope
                                string contentType = response.ContentType ?? "unknown";
                                if (!TryExtractLLMText(responseBody, contentType, out string content))
                                    return null;  // non-JSON response — silent skip (greeting)

                                // Step 2: content may itself be a JSON object — try to extract spoken_text from it
                                string greeting = JsonHelper.GetString(content, "spoken_text");

                                // Step 3: Model may output plain "spoken_text: ..." text without JSON wrapping
                                if (string.IsNullOrEmpty(greeting) && content.StartsWith("spoken_text:", StringComparison.OrdinalIgnoreCase))
                                    greeting = content.Substring("spoken_text:".Length).Trim(' ', '"');

                                // Step 4: Fallback — use content as-is
                                if (string.IsNullOrEmpty(greeting))
                                    greeting = content;

                                if (!string.IsNullOrEmpty(greeting))
                                {
                                    // Unescape and trim
                                    greeting = greeting.Replace("\\n", " ").Replace("\\r", " ")
                                                      .Replace("\\\"", "\"").Replace("\\\\", "\\")
                                                      .Trim();
                                }

                                return greeting;
                            }
                        }
                        catch (WebException) when (token.IsCancellationRequested)
                        {
                            Log.Debug(() => $"[PROACTIVE] Greeting request for {npcName} aborted by Interruption Matrix.");
                            throw new System.OperationCanceledException(token);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(() => $"[LOADED] Greeting generation failed for {npcName}: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>
        /// Generate a short, in-character personal story via the LLM.
        /// Grounded in the NPC's Backstory field + RAG memories.
        /// Simplified prompt — no action JSON, no spoken_text wrapping.
        /// Runs entirely on a background thread using HttpWebRequest.
        /// </summary>
        public async Task<string> GenerateStoryAsync(
            string npcName,
            EntityAlive npcEntity,
            string playerName,
            LLMConfig config,
            PersonalityDefinition personality = null,
            CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(npcName) || config == null)
                return null;

            // Personality is pre-resolved by caller on main thread (Unity API safety).
            if (personality == null && npcEntity != null && PersonalityManager.Instance != null)
            {
                personality = PersonalityManager.Instance.AssignPersonality(npcEntity);
            }

            return await Task.Run(() =>
            {
                try
                {
                    // --- Build system prompt (mirrors greeting path) ---
                    string basePrompt = config.SystemPrompt;
                    string ttsRule = "\nCRITICAL TTS INSTRUCTION: Your spoken_text must be exclusively in English. Do NOT use Chinese characters, Japanese characters, or any non-Latin script in your spoken_text output. The subtitle_localized field may use the player's UI language.";

                    // Identity + location + stranger rule
                    string identityPrompt = $"Your name is {npcName}. ";
                    string locationContext = npcEntity != null ? "You are currently surviving in the wasteland. " : "";
                    string strangerIdentity = "You are speaking to a fellow survivor. You do not know their name unless it is explicitly provided to you in your [SURVIVOR MEMORIES]. If you do not know their name, refer to them using natural, generic terms (e.g., 'stranger', 'traveler', 'friend', or simply 'you'). You must NEVER refer to the survivor by your own name. ";

                    string systemPrompt;
                    if (personality != null && !string.IsNullOrEmpty(personality.Traits))
                    {
                        systemPrompt = $"{identityPrompt}{locationContext}{strangerIdentity}{personality.Traits} {basePrompt}{ttsRule}";
                    }
                    else
                    {
                        systemPrompt = $"{identityPrompt}{locationContext}{strangerIdentity}{basePrompt}{ttsRule}";
                    }

                    // --- Inject Backstory ---
                    string backstoryText = personality?.Backstory ?? "";
                    if (!string.IsNullOrEmpty(backstoryText))
                    {
                        systemPrompt += $"\n\nThis is your personal history: {backstoryText}";
                    }

                    // --- Retrieve RAG memories ---
                    string memoryContext = "";
                    if (NPCMemoryManager.Instance != null && !string.IsNullOrEmpty(playerName))
                    {
                        try
                        {
                            memoryContext = NPCMemoryManager.Instance.GetRelevantContextAsync(
                                playerName, npcName, "The survivor asked you to tell a story.").Result;
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"[RAG] Memory retrieval failed during story generation: {ex.GetType().Name}: {ex.Message}");
                            memoryContext = "";
                        }
                    }

                    // --- Inject memory context ---
                    string finalSystemPrompt = systemPrompt;
                    if (!string.IsNullOrEmpty(memoryContext))
                    {
                        string survivorNoun = !string.IsNullOrEmpty(playerName) && playerName != "Survivor"
                            ? playerName
                            : "the survivor";

                        var npcMemories = new List<string>();
                        var survivorMemories = new List<string>();

                        string[] rawFacts = memoryContext.Split(new[] { ", " }, StringSplitOptions.None);
                        foreach (string raw in rawFacts)
                        {
                            string trimmed = raw.Trim().Trim('-', ' ');
                            if (trimmed.StartsWith("[NPC] "))
                            {
                                npcMemories.Add(trimmed.Substring(6).Replace("The Player", survivorNoun));
                            }
                            else if (trimmed.StartsWith("[SURVIVOR] "))
                            {
                                survivorMemories.Add(trimmed.Substring(11).Replace("The Player", survivorNoun));
                            }
                            else if (!trimmed.StartsWith("[") && trimmed.Length > 3)
                            {
                                survivorMemories.Add(trimmed.Replace("The Player", survivorNoun));
                            }
                        }

                        StringBuilder contextBlock = new StringBuilder();
                        if (npcMemories.Count > 0)
                        {
                            contextBlock.Append("[ABOUT YOU]\n");
                            foreach (string m in npcMemories)
                                contextBlock.Append($"- {m}\n");
                            contextBlock.Append($"These are facts about yourself. You know this is your status.\n\n");
                        }
                        if (survivorMemories.Count > 0)
                        {
                            contextBlock.Append("[ABOUT YOUR COMPANION ").Append(survivorNoun).Append("]\n");
                            foreach (string m in survivorMemories)
                                contextBlock.Append($"- {m}\n");
                            contextBlock.Append($"These are facts about {survivorNoun}. Reference them naturally when relevant.");
                        }

                        finalSystemPrompt = systemPrompt + "\n\n" + contextBlock.ToString();
                    }

                    // --- Append story instruction (overrides brevity) ---
                    finalSystemPrompt += "\n\n[SYSTEM: The survivor asked you to tell a story. Tell a short personal story from your past, 3-4 sentences, in character, drawing on your backstory and what you remember about this survivor. Try not to repeat a story you've already told them. Do not ask questions.]";

                    // --- HTTP POST to llama-server ---
                    string body = BuildOpenAIRequest(finalSystemPrompt, new List<ChatMessage>(), "Tell me a story.", config.Temperature, null);

                    var request = (HttpWebRequest)WebRequest.Create(_endpoint);
                    request.KeepAlive = true;
                    request.Proxy = null;
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Timeout = config.TimeoutSeconds * 1000;
                    request.ReadWriteTimeout = config.TimeoutSeconds * 1000;

                    byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                    request.ContentLength = bodyBytes.Length;

                    // Bind cancellation token to HTTP request
                    using (token.Register(() => request.Abort(), useSynchronizationContext: false))
                    {
                        try
                        {
                            using (var stream = request.GetRequestStream())
                                stream.Write(bodyBytes, 0, bodyBytes.Length);

                            using (var response = (HttpWebResponse)request.GetResponse())
                            using (var reader = new StreamReader(response.GetResponseStream()))
                            {
                                string responseBody = reader.ReadToEnd();

                                // Validate response is JSON before using as story text
                                string contentType = response.ContentType ?? "unknown";
                                if (!TryExtractLLMText(responseBody, contentType, out string content))
                                    return null;  // non-JSON — graceful skip (story)

                                // Story is plain text — no action JSON wrapping
                                string story = content.Trim();
                                if (!string.IsNullOrEmpty(story))
                                {
                                    story = story.Replace("\\n", " ").Replace("\\r", " ")
                                                 .Replace("\\\"", "\"").Replace("\\\\", "\\")
                                                 .Trim();
                                }
                                return story;
                            }
                        }
                        catch (WebException) when (token.IsCancellationRequested)
                        {
                            Log.Debug(() => $"[STORY] Story request for {npcName} aborted.");
                            throw new System.OperationCanceledException(token);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(() => $"[STORY] Story generation failed for {npcName}: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>
        /// Extract "content" from an OpenAI-compatible JSON response (background thread version).
        /// Mirrors NPCChatComponent.ParseLLMContent but static for use in background tasks.
        /// </summary>
        private static string ParseLLMContentFromRaw(string json)
        {
            return JsonHelper.GetLLMContent(json);
        }

        // One-time-per-session diagnostic latch — bad URL fails every call, no spam
        private static bool _sawNonJsonResponse = false;

        /// <summary>
        /// Validate and extract LLM text from a raw HTTP response body.
        /// Rejects HTML/non-JSON responses before they become "content" (AMP bug fix).
        /// Gate on JSON-parse success, NOT Content-Type equality (llama-server variants return text/plain etc.).
        /// Returns true if text was extracted, false if response is garbage (caller should treat as null/fail).
        /// </summary>
        private static bool TryExtractLLMText(string rawBody, string contentType, out string text)
        {
            text = null;
            if (string.IsNullOrEmpty(rawBody))
                return false;

            string trimmed = rawBody.TrimStart();
            if (trimmed.Length == 0)
                return false;

            char firstChar = trimmed[0];

            // HTML/XML rejection — the endpoint returned a web page, not JSON
            if (firstChar == '<')
            {
                if (!_sawNonJsonResponse)
                {
                    _sawNonJsonResponse = true;
                    string preview = trimmed.Length > 120 ? trimmed.Substring(0, 120) + "..." : trimmed;
                    Log.Warning($"[LLM] Endpoint returned non-JSON (contentType={contentType}, starts with '{firstChar}') — check LlmEndpoint points at llama-server, not a web panel. First 120 chars: {preview}");
                }
                return false;
            }

            // JSON parse attempt
            string content = JsonHelper.GetLLMContent(rawBody);
            if (content != null)
            {
                text = content;
                return true;
            }

            // Parse failed — not a valid OpenAI response envelope
            if (!_sawNonJsonResponse)
            {
                _sawNonJsonResponse = true;
                string preview = trimmed.Length > 120 ? trimmed.Substring(0, 120) + "..." : trimmed;
                Log.Warning($"[LLM] Endpoint returned non-JSON (contentType={contentType}, starts with '{firstChar}') — check LlmEndpoint points at llama-server, not a web panel. First 120 chars: {preview}");
            }
            return false;
        }

        // ========================================================================
        // Ambient NPC-to-NPC chatter generation (no RAG, backstory-only grounding)
        // ========================================================================

        private const string NPCToNPCSystemPromptTemplate =
            "You are writing a brief NPC-to-NPC encounter in a post-apocalyptic survival game.\n\n" +
            "NPC A: {0} — {1}\n" +
            "NPC B: {2} — {3}\n\n" +
            "They have just come within earshot of each other. Write a natural, brief exchange. " +
            "Each NPC speaks exactly {4} line(s). Keep it conversational and in-character. " +
            "Do NOT include stage directions, action tags, or bracketed text.\n\n" +
            "Format output as:\n" +
            "A: <line 1>\n" +
            "B: <line 1>\n" +
            "[A: <line 2>]\n" +
            "[B: <line 2>]";

        /// <summary>
        /// Generate a brief ambient NPC-to-NPC exchange. No RAG, no player context — backstory-only
        /// grounding, kept deliberately lightweight/fast. Returns null on any failure or parse error
        /// (caller should treat null as "skip this attempt", not retry immediately).
        /// </summary>
        public async Task<(string lineA, string lineB)?> GenerateNPCChatAsync(
            string nameA, string backstoryA, string nameB, string backstoryB, int maxLines)
        {
            string systemPrompt = string.Format(NPCToNPCSystemPromptTemplate, nameA, backstoryA, nameB, backstoryB, maxLines);

            return await Task.Run(() =>
            {
                string body = BuildOpenAIRequest(systemPrompt, new List<ChatMessage>(), "Begin.", _temperature, null, 256);
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(_endpoint);
                    request.KeepAlive = true;
                    request.Proxy = null;
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Timeout = _timeoutSeconds * 1000;

                    using (var writer = new StreamWriter(request.GetRequestStream()))
                        writer.Write(body);

                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        string rawResponse = reader.ReadToEnd();
                        string content = JsonHelper.GetLLMContent(rawResponse.Trim());
                        return ParseNPCChatResponse(content, nameA, nameB);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[NPC2NPC-DIAG] HTTP failed: {ex.GetType().Name}: {ex.Message}");
                    return ((string, string)?)null;
                }
            });
        }

        /// <summary>
        /// Parse lines attributed to NPC A or NPC B from the model's response. The model may use
        /// "A:" / "B:" as instructed, or its own names (e.g. "Brian:") — both are accepted.
        /// Joins multiple lines per side with a space. Returns null if either side never appears.
        /// </summary>
        private static (string, string)? ParseNPCChatResponse(string content, string nameA, string nameB)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var linesA = new List<string>();
            var linesB = new List<string>();

            // Build case-insensitive prefixes to match: "A:", "B:", and the actual NPC names.
            string prefixA_A = "a:";
            string prefixA_Name = (nameA + ":").ToLowerInvariant();
            string prefixB_B = "b:";
            string prefixB_Name = (nameB + ":").ToLowerInvariant();

            foreach (var rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                string lower = line.ToLowerInvariant();

                bool isA = lower.StartsWith(prefixA_A) || lower.StartsWith(prefixA_Name);
                bool isB = lower.StartsWith(prefixB_B) || lower.StartsWith(prefixB_Name);

                if (isA)
                {
                    // Strip the prefix (whichever matched)
                    string stripped = line;
                    if (lower.StartsWith(prefixA_A)) stripped = line.Substring(2).Trim();
                    else if (lower.StartsWith(prefixA_Name)) stripped = line.Substring(nameA.Length + 1).Trim();
                    linesA.Add(stripped);
                }
                else if (isB)
                {
                    string stripped = line;
                    if (lower.StartsWith(prefixB_B)) stripped = line.Substring(2).Trim();
                    else if (lower.StartsWith(prefixB_Name)) stripped = line.Substring(nameB.Length + 1).Trim();
                    linesB.Add(stripped);
                }
            }
            if (linesA.Count == 0 || linesB.Count == 0) return null;
            return (string.Join(" ", linesA), string.Join(" ", linesB));
        }

        #endregion
    }

    [System.Serializable]
    public class LlmResponseObject
    {
        public string dialogue;
        public string text;
        public string content;
        public string message;
    }

    /// <summary>
    /// DTOs for llama-server /v1/embeddings endpoint (RAG memory vectors).
    /// </summary>
    [System.Serializable]
    public class LlamaEmbeddingRequest
    {
        public string input;
    }

    [System.Serializable]
    public class LlamaEmbeddingResponse
    {
        public LlamaEmbeddingData[] data;
    }

    [System.Serializable]
    public class LlamaEmbeddingData
    {
        public float[] embedding;
    }

    /// <summary>
    /// Represents a single message in the conversation history
    /// </summary>
    public class ChatMessage
    {
        public string Role { get; set; }  // "Player" or "NPC"
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Configuration loaded from llmconfig.xml
    /// </summary>
    public class LLMConfig
    {
        // Server settings
        public string Endpoint { get; set; }
        public string Model { get; set; }
        public string ModelFilename { get; set; }  // Explicit GGUF filename (e.g. "Qwen3.5-9B-Instruct.gguf"). Empty = autodetect.
        public int TimeoutSeconds { get; set; }
        public int MaxTokens { get; set; }
        public float Temperature { get; set; }

        // Personality settings
        public string SystemPrompt { get; set; }
        public int ContextMemory { get; set; }

        // Response settings
        public bool ShowTypingIndicator { get; set; }
        public int TypingDelayMs { get; set; }
        public int MaxResponseLength { get; set; }
        public float ChatTimeout { get; set; } = 30f;
        public int ContextSize { get; set; } = 8192;

        // Wild NPC combat chat gate
        public float WildCombatChatCooldownSeconds { get; set; } = 5f;
    }
}
