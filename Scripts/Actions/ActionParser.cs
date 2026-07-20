using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace XNPCVoiceControl.Actions
{
    /// <summary>
    /// Parses LLM responses to extract actions and dialogue.
    /// Supports both structured JSON responses and natural language parsing.
    /// </summary>
    public static class ActionParser
    {
        // Regex patterns for detecting actions in natural language
        private static readonly Dictionary<NPCActionType, string[]> ActionPatterns = new Dictionary<NPCActionType, string[]>
        {
            { NPCActionType.Follow, new[] {
                @"\b(follow|come with|i'?ll come|let'?s go|lead the way|i'?m with you|right behind you)\b",
                @"\b(sure,? i'?ll follow|okay,? let'?s move|alright,? lead on)\b"
            }},
            { NPCActionType.StopFollow, new[] {
                @"\b(stop following|stay here|wait here|i'?ll stay|not coming|staying put)\b",
                @"\b(this is where i stop|i'?m done following|go on without me)\b"
            }},
            { NPCActionType.Wait, new[] {
                @"\b(i'?ll wait|waiting here|stay put|hold position|standing by)\b",
                @"\b(okay,? i'?ll wait|sure,? i'?ll stay)\b"
            }},
            { NPCActionType.Guard, new[] {
                @"\b(guard|protect|watch over|keep watch|defend|patrol)\b",
                @"\b(i'?ll guard|i'?ll protect|i'?ll watch)\b"
            }},
            { NPCActionType.Trade, new[] {
                @"\b(trade|barter|buy|sell|deal|exchange|what do you have|show me your|let me see your)\b",
                @"\b(let'?s trade|want to trade|interested in trading)\b"
            }},
            { NPCActionType.GiveItem, new[] {
                @"\b(here,? take|giving you|have this|take this|for you|gift)\b",
                @"\b(i'?ll give you|let me give you)\b"
            }},
            { NPCActionType.Heal, new[] {
                @"\b(patch you up|fix you up|let me help you)\b",
                @"\b(i'?ll heal you|let me treat you)\b"
            }},
            { NPCActionType.Refuse, new[] {
                @"\b(can'?t|won'?t|refuse|not going to|forget it|no way|absolutely not)\b",
                @"\b(i refuse|not happening|out of the question)\b"
            }},
            { NPCActionType.Attack, new[] {
                @"\b(attack|kill|shoot|fight|engage|take down|hostile)\b"
            }},
            { NPCActionType.Flee, new[] {
                @"\b(run|flee|escape|get out|danger|we need to go|let'?s get out)\b"
            }},
            { NPCActionType.Emote, new[] {
                @"\*(wave|nod|shake|point|salute|shrug|laugh|sit|crouch|look around)\*",
                @"\b(waves|nods|shakes head|points|salutes|shrugs|laughs)\b"
            }},
            { NPCActionType.ShareInfo, new[] {
                @"\b(there'?s a|i know where|i'?ve seen|i can show you|let me mark|there'?s something)\b",
                @"\b(poi|location|place|spot|area) (north|south|east|west|nearby)\b"
            }},
            { NPCActionType.Barter, new[] {
                @"\b(how about|counter.?offer|instead|what if|deal|negotiate)\b",
                @"\b(\d+)\s*(duke|dukes|coin|coins)\b"
            }}
        };

        // Patterns for extracting structured JSON from LLM response
        private static readonly Regex JsonPattern = new Regex(
            @"\{[^{}]*""action""\s*:\s*""([^""]+)""[^{}]*\}",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled
        );

        private static readonly Regex JsonDialoguePattern = new Regex(
            @"""dialogue""\s*:\s*""([^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        private static readonly Regex JsonParamsPattern = new Regex(
            @"""(\w+)""\s*:\s*""?([^"",}]+)""?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        // Matches JSON where action is null: {"action": null, "dialogue": "..."}
        private static readonly Regex JsonPatternNullAction = new Regex(
            @"\{[^{}]*""action""\s*:\s*null[^{}]*\}",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled
        );

        // Dual-output JSON fields: spoken_text (for TTS) and subtitle_localized (for UI)
        private static readonly Regex JsonSpokenTextPattern = new Regex(
            @"""spoken_text""\s*:\s*""([^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        private static readonly Regex JsonSubtitleLocalizedPattern = new Regex(
            @"""subtitle_localized""\s*:\s*""([^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        // Bracket-tag pattern for plain-text streaming responses: [follow], [guard], etc.
        private static readonly Regex BracketTagPattern = new Regex(
            @"^\s*\[(\w+)\]\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        /// <summary>
        /// Parse an LLM response to extract action and dialogue.
        /// Attempts JSON parsing first, falls back to natural language.
        /// </summary>
        public static NPCAction Parse(string llmResponse)
        {
            if (string.IsNullOrWhiteSpace(llmResponse))
                return new NPCAction(NPCActionType.None, "...");

            // Try JSON parsing first (structured response with quoted action)
            var jsonAction = TryParseJson(llmResponse);
            if (jsonAction != null)
            {
                if (jsonAction.Type != NPCActionType.None || !string.IsNullOrEmpty(jsonAction.DialogueBefore))
                    return jsonAction;
            }

            // Try JSON with null/empty action: {"action": null, "dialogue": "..."}
            var jsonNullAction = TryParseJsonNullAction(llmResponse);
            if (jsonNullAction != null && !string.IsNullOrEmpty(jsonNullAction.DialogueBefore))
                return jsonNullAction;

            // Try bracket-tag: [follow], [guard], etc. at start of plain-text response
            var bracketAction = TryParseBracketTag(llmResponse);
            if (bracketAction != null)
                return bracketAction;

            // Fall back to natural language parsing
            return ParseNaturalLanguage(llmResponse);
        }

        /// <summary>
        /// Try to parse structured JSON response
        /// Expected format: {"action": "follow", "dialogue": "Sure, I'll come with you!", "params": {...}}
        /// </summary>
        private static NPCAction TryParseJson(string response)
        {
            try
            {
                var jsonMatch = JsonPattern.Match(response);
                if (!jsonMatch.Success)
                    return null;

                string jsonBlock = jsonMatch.Value;
                var action = new NPCAction();

                // Extract action type
                string actionStr = jsonMatch.Groups[1].Value.ToLower();
                action.Type = ParseActionType(actionStr);

                // Extract dialogue - handle escaped quotes
                var dialogueMatch = JsonDialoguePattern.Match(response);
                if (dialogueMatch.Success)
                {
                    action.DialogueBefore = UnescapeJson(dialogueMatch.Groups[1].Value);
                }
                else
                {
                    // Try alternative extraction for escaped strings
                    int dialogueStart = response.IndexOf("\"dialogue\"");
                    if (dialogueStart >= 0)
                    {
                        int colonPos = response.IndexOf(':', dialogueStart);
                        if (colonPos >= 0)
                        {
                            int quoteStart = response.IndexOf('"', colonPos);
                            if (quoteStart >= 0)
                            {
                                // Find matching end quote (accounting for escaped quotes)
                                int quoteEnd = quoteStart + 1;
                                while (quoteEnd < response.Length)
                                {
                                    if (response[quoteEnd] == '"' && response[quoteEnd - 1] != '\\')
                                        break;
                                    quoteEnd++;
                                }
                                if (quoteEnd < response.Length)
                                {
                                    action.DialogueBefore = UnescapeJson(response.Substring(quoteStart + 1, quoteEnd - quoteStart - 1));
                                }
                            }
                        }
                    }
                }

                // Extract parameters
                var paramMatches = JsonParamsPattern.Matches(jsonBlock);
                foreach (Match param in paramMatches)
                {
                    string key = param.Groups[1].Value.ToLower();
                    string value = param.Groups[2].Value;
                    if (key != "action" && key != "dialogue")
                    {
                        action.Parameters[key] = value;
                    }
                }

                // Dual-output: extract spoken_text (for TTS) and subtitle_localized (for UI)
                var spokenMatch = JsonSpokenTextPattern.Match(response);
                if (spokenMatch.Success)
                {
                    action.DialogueBefore = UnescapeJson(spokenMatch.Groups[1].Value);
                }

                var subtitleMatch = JsonSubtitleLocalizedPattern.Match(response);
                if (subtitleMatch.Success)
                {
                    action.SubtitleLocalized = UnescapeJson(subtitleMatch.Groups[1].Value);
                }
                if (string.IsNullOrEmpty(action.DialogueBefore))
                {
                    string nonJson = JsonPattern.Replace(response, "").Trim();
                    if (!string.IsNullOrEmpty(nonJson))
                    {
                        action.DialogueBefore = nonJson;
                    }
                }

                action.Confidence = 0.9f;
                return action;
            }
            catch (Exception ex)
            {
                Log.Debug(() => $"JSON parse failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Try to parse JSON where action is null/empty but dialogue is present.
        /// Handles: {"action": null, "dialogue": "..."} or {"action": "", "dialogue": "..."}
        /// </summary>
        private static NPCAction TryParseJsonNullAction(string response)
        {
            try
            {
                var jsonMatch = JsonPatternNullAction.Match(response);
                if (!jsonMatch.Success)
                    return null;

                string jsonBlock = jsonMatch.Value;
                var action = new NPCAction { Type = NPCActionType.None, Confidence = 0.9f };

                // Extract dialogue
                var dialogueMatch = JsonDialoguePattern.Match(response);
                if (dialogueMatch.Success)
                {
                    action.DialogueBefore = UnescapeJson(dialogueMatch.Groups[1].Value);
                }

                // Dual-output: extract spoken_text and subtitle_localized
                var spokenMatch = JsonSpokenTextPattern.Match(response);
                if (spokenMatch.Success)
                {
                    action.DialogueBefore = UnescapeJson(spokenMatch.Groups[1].Value);
                }

                var subtitleMatch = JsonSubtitleLocalizedPattern.Match(response);
                if (subtitleMatch.Success)
                {
                    action.SubtitleLocalized = UnescapeJson(subtitleMatch.Groups[1].Value);
                }

                var paramMatches = JsonParamsPattern.Matches(jsonBlock);
                foreach (Match param in paramMatches)
                {
                    string key = param.Groups[1].Value.ToLower();
                    string value = param.Groups[2].Value;
                    if (key != "action" && key != "dialogue")
                    {
                        action.Parameters[key] = value;
                    }
                }

                return action;
            }
            catch (Exception ex)
            {
                Log.Debug(() => $"JSON null-action parse failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Try to parse a bracket-tag action at the start of a plain-text response.
        /// Format: [follow] Alright, I've got your back.
        /// </summary>
        private static NPCAction TryParseBracketTag(string response)
        {
            var match = BracketTagPattern.Match(response);
            if (!match.Success) return null;

            string tag = match.Groups[1].Value;
            NPCActionType actionType = ParseActionType(tag);
            if (actionType == NPCActionType.None) return null;

            string dialogue = response.Substring(match.Length).Trim();
            return new NPCAction
            {
                Type = actionType,
                DialogueBefore = string.IsNullOrWhiteSpace(dialogue) ? null : dialogue,
                Confidence = 0.92f
            };
        }

        /// <summary>
        /// Parse natural language response for implied actions
        /// </summary>
        private static NPCAction ParseNaturalLanguage(string response)
        {
            var action = new NPCAction
            {
                DialogueBefore = CleanDialogue(response),
                Type = NPCActionType.None,
                Confidence = 0.5f
            };

            string lowerResponse = response.ToLower();

            // Check each action pattern
            float highestConfidence = 0f;
            NPCActionType detectedAction = NPCActionType.None;

            foreach (var kvp in ActionPatterns)
            {
                foreach (string pattern in kvp.Value)
                {
                    var match = Regex.Match(lowerResponse, pattern, RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        // Calculate confidence based on match position and specificity
                        float confidence = CalculateConfidence(match, response.Length, kvp.Key);
                        if (confidence > highestConfidence)
                        {
                            highestConfidence = confidence;
                            detectedAction = kvp.Key;

                            // Extract any relevant parameters from the match
                            ExtractParameters(action, match, kvp.Key, response);
                        }
                    }
                }
            }

            action.Type = detectedAction;
            action.Confidence = highestConfidence;

            // Extract emote type if applicable
            if (action.Type == NPCActionType.Emote)
            {
                var emoteMatch = Regex.Match(lowerResponse, @"\*(\w+)\*");
                if (emoteMatch.Success)
                {
                    action.Parameters["emote"] = emoteMatch.Groups[1].Value;
                }
            }

            return action;
        }

        private static NPCActionType ParseActionType(string actionStr)
        {
            // Map various string representations to action types
            var mappings = new Dictionary<string, NPCActionType>
            {
                { "follow", NPCActionType.Follow },
                { "come", NPCActionType.Follow },
                { "accompany", NPCActionType.Follow },
                { "stop", NPCActionType.StopFollow },
                { "stopfollow", NPCActionType.StopFollow },
                { "stay", NPCActionType.Wait },
                { "wait", NPCActionType.Wait },
                { "guard", NPCActionType.Guard },
                { "protect", NPCActionType.Guard },
                { "patrol", NPCActionType.Guard },
                { "trade", NPCActionType.Trade },
                { "barter", NPCActionType.Barter },
                { "sell", NPCActionType.Trade },
                { "buy", NPCActionType.Trade },
                { "give", NPCActionType.GiveItem },
                { "take", NPCActionType.TakeItem },
                { "heal", NPCActionType.Heal },
                { "attack", NPCActionType.Attack },
                { "fight", NPCActionType.Attack },
                { "flee", NPCActionType.Flee },
                { "run", NPCActionType.Flee },
                { "emote", NPCActionType.Emote },
                { "wave", NPCActionType.Emote },
                { "nod", NPCActionType.Emote },
                { "refuse", NPCActionType.Refuse },
                { "no", NPCActionType.Refuse },
                { "info", NPCActionType.ShareInfo },
                { "location", NPCActionType.ShareInfo },
                { "none", NPCActionType.None },
                { "talk", NPCActionType.None },
                { "chat", NPCActionType.None },
                { "dialogue", NPCActionType.None },  // LLM sometimes uses this for pure dialogue
                { "dialog", NPCActionType.None }     // Alternative spelling
            };

            actionStr = actionStr.ToLower().Trim();
            return mappings.TryGetValue(actionStr, out var type) ? type : NPCActionType.None;
        }

        private static float CalculateConfidence(Match match, int responseLength, NPCActionType actionType)
        {
            float confidence = 0.5f;

            // Higher confidence if match is at the start
            if (match.Index < responseLength * 0.3)
                confidence += 0.2f;

            // Higher confidence for longer matches (more specific)
            if (match.Length > 10)
                confidence += 0.1f;

            // Some actions need stronger signals
            if (actionType == NPCActionType.Attack || actionType == NPCActionType.Refuse)
            {
                confidence -= 0.1f; // Be more conservative with these
            }

            return Math.Min(confidence, 0.95f);
        }

        private static void ExtractParameters(NPCAction action, Match match, NPCActionType actionType, string fullResponse)
        {
            string response = fullResponse.ToLower();

            switch (actionType)
            {
                case NPCActionType.GiveItem:
                case NPCActionType.TakeItem:
                    // Try to extract item name
                    var itemMatch = Regex.Match(response, @"(give|take|have|here'?s?)\s+(?:you\s+)?(?:a\s+|an\s+|some\s+)?(\w+)");
                    if (itemMatch.Success)
                    {
                        action.Parameters["item"] = itemMatch.Groups[2].Value;
                    }
                    break;

                case NPCActionType.Barter:
                    // Extract price if mentioned
                    var priceMatch = Regex.Match(response, @"(\d+)\s*(duke|dukes|coin|coins)?");
                    if (priceMatch.Success)
                    {
                        action.Parameters["price"] = priceMatch.Groups[1].Value;
                    }
                    break;

                case NPCActionType.Guard:
                    // Extract duration if mentioned
                    var durationMatch = Regex.Match(response, @"for\s+(\d+)\s*(hour|minute|day)");
                    if (durationMatch.Success)
                    {
                        action.Parameters["duration"] = durationMatch.Groups[1].Value;
                        action.Parameters["unit"] = durationMatch.Groups[2].Value;
                    }
                    break;

                case NPCActionType.ShareInfo:
                    // Extract direction/location hints
                    var dirMatch = Regex.Match(response, @"(north|south|east|west|nearby|close)");
                    if (dirMatch.Success)
                    {
                        action.Parameters["direction"] = dirMatch.Groups[1].Value;
                    }
                    break;
            }
        }

        private static string CleanDialogue(string response)
        {
            // Remove any JSON blocks
            string cleaned = JsonPattern.Replace(response, "").Trim();

            // Remove action indicators in brackets
            cleaned = Regex.Replace(cleaned, @"\[ACTION:?\s*\w+\]", "", RegexOptions.IgnoreCase);

            // Clean up extra whitespace
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            return string.IsNullOrEmpty(cleaned) ? response : cleaned;
        }

        private static string UnescapeJson(string str)
        {
            return str.Replace("\\n", "\n")
                      .Replace("\\r", "\r")
                      .Replace("\\t", "\t")
                      .Replace("\\\"", "\"")
                      .Replace("\\\\", "\\");
        }
    }
}
