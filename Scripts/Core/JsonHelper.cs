using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace XNPCVoiceControl.Core
{
    /// <summary>
    /// Centralized JSON parsing helpers. Replaces hand-rolled string scanners.
    /// All methods return null/empty on any parse failure — callers handle missing data.
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// Extract a string field from a JSON object by key.
        /// Handles nested objects — only matches top-level keys.
        /// Returns null if key not found or value is not a string.
        /// </summary>
        public static string GetString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var obj = JObject.Parse(json);
                var token = obj[key];
                return token?.Type == JTokenType.String ? token.Value<string>() : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Extract a boolean field from a JSON object by key.
        /// Returns null if key not found or value is not a boolean.
        /// </summary>
        public static bool? GetBool(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var obj = JObject.Parse(json);
                var token = obj[key];
                return token?.Type == JTokenType.Boolean ? token.Value<bool>() : (bool?)null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Extract a string array field from a JSON object by key.
        /// Returns empty list if key not found or array is empty.
        /// </summary>
        public static List<string> GetStringArray(string json, string key)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(json)) return result;
            try
            {
                var obj = JObject.Parse(json);
                var token = obj[key] as JArray;
                if (token == null) return result;
                foreach (var item in token)
                {
                    if (item.Type == JTokenType.String)
                        result.Add(item.Value<string>());
                }
            }
            catch { /* JSON malformed or key not a string array */ }
            return result;
        }

        /// <summary>
        /// Extract a float array from a JSON object by key (for embeddings).
        /// Returns empty array on failure.
        /// </summary>
        public static float[] GetFloatArray(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return Array.Empty<float>();
            try
            {
                var obj = JObject.Parse(json);
                var token = obj[key] as JArray;
                if (token == null) return Array.Empty<float>();
                var result = new float[token.Count];
                for (int i = 0; i < token.Count; i++)
                    result[i] = token[i].Value<float>();
                return result;
            }
            catch { return Array.Empty<float>(); }
        }

        /// <summary>
        /// Extract the float embedding array from an OpenAI-compatible /v1/embeddings response.
        /// Navigates: data[0].embedding
        /// Returns empty array on failure.
        /// </summary>
        public static float[] GetEmbeddingFromOpenAIResponse(string json)
        {
            if (string.IsNullOrEmpty(json)) return Array.Empty<float>();
            try
            {
                var obj = JObject.Parse(json);
                var embeddingToken = obj["data"]?[0]?["embedding"] as JArray;
                if (embeddingToken == null) return Array.Empty<float>();
                var result = new float[embeddingToken.Count];
                for (int i = 0; i < embeddingToken.Count; i++)
                    result[i] = embeddingToken[i].Value<float>();
                return result;
            }
            catch { return Array.Empty<float>(); }
        }

        /// <summary>
        /// Extract the LLM response content from an OpenAI-compatible chat completion response.
        /// Navigates: choices[0].message.content
        /// Returns null if structure doesn't match.
        /// </summary>
        public static string GetLLMContent(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var obj = JObject.Parse(json);
                return obj["choices"]?[0]?["message"]?["content"]?.Value<string>();
            }
            catch { return null; }
        }

        /// <summary>
        /// Extract reasoning_content from an OpenAI-compatible chat completion response.
        /// Used as fallback for reasoning models (DeepSeek-R1, Gemma MoE) that return empty content.
        /// Navigates: choices[0].message.reasoning_content
        /// Returns null if not present.
        /// </summary>
        public static string GetLLMReasoningContent(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var obj = JObject.Parse(json);
                return obj["choices"]?[0]?["message"]?["reasoning_content"]?.Value<string>();
            }
            catch { return null; }
        }

        /// <summary>
        /// Extract delta content token from an OpenAI-compatible SSE streaming chunk.
        /// Navigates: choices[0].delta.content
        /// Returns null if not present (e.g. role-only chunk or finish_reason chunk).
        /// </summary>
        public static string GetStreamingDelta(string sseJson)
        {
            if (string.IsNullOrEmpty(sseJson)) return null;
            try
            {
                var obj = JObject.Parse(sseJson);
                return obj["choices"]?[0]?["delta"]?["content"]?.Value<string>();
            }
            catch { return null; }
        }
    }
}
