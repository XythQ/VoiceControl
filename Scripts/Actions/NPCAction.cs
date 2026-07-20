using System;
using System.Collections.Generic;
using UnityEngine;

namespace XNPCVoiceControl.Actions
{
    /// <summary>
    /// Defines available NPC actions that can be triggered by LLM responses.
    /// Each action maps to NPCCore/SCore AI tasks or custom behaviors.
    /// </summary>
    public enum NPCActionType
    {
        None,           // No action, just dialogue
        Follow,         // Follow the player
        StopFollow,     // Stop following
        Wait,           // Stay in current position
        Guard,          // Guard current location (attack hostiles)
        Trade,          // Open trade window
        GiveItem,       // Give item to player
        TakeItem,       // Take item from player
        Heal,           // Heal the player (if medic NPC)
        Craft,          // Craft an item
        MoveTo,         // Move to a specific location
        Attack,         // Attack a target
        Flee,           // Run away from danger
        Sleep,          // Rest/sleep action
        Emote,          // Play an animation/emote
        ShareInfo,      // Share map marker or quest info
        Refuse,         // Refuse the request
        Barter,         // Negotiate price/terms
    }

    /// <summary>
    /// Represents a parsed action from LLM response
    /// </summary>
    public class NPCAction
    {
        public NPCActionType Type { get; set; }
        public string DialogueBefore { get; set; }  // What NPC says before action (spoken_text for TTS)
        public string DialogueAfter { get; set; }   // What NPC says after action
        public string SubtitleLocalized { get; set; } // Localized subtitle text (subtitle_localized for UI)
        public Dictionary<string, string> Parameters { get; set; }
        public float Confidence { get; set; }       // How confident the parse was

        public NPCAction()
        {
            Type = NPCActionType.None;
            Parameters = new Dictionary<string, string>();
            Confidence = 1.0f;
        }

        public NPCAction(NPCActionType type, string dialogue = null)
        {
            Type = type;
            DialogueBefore = dialogue;
            Parameters = new Dictionary<string, string>();
            Confidence = 1.0f;
        }

        /// <summary>
        /// Get a parameter value with optional default
        /// </summary>
        public string GetParam(string key, string defaultValue = null)
        {
            return Parameters.TryGetValue(key, out string value) ? value : defaultValue;
        }

        /// <summary>
        /// Get parameter as integer
        /// </summary>
        public int GetParamInt(string key, int defaultValue = 0)
        {
            if (Parameters.TryGetValue(key, out string value) && int.TryParse(value, out int result))
                return result;
            return defaultValue;
        }

        /// <summary>
        /// Get parameter as float
        /// </summary>
        public float GetParamFloat(string key, float defaultValue = 0f)
        {
            if (Parameters.TryGetValue(key, out string value) && float.TryParse(value, out float result))
                return result;
            return defaultValue;
        }

        public override string ToString()
        {
            return $"NPCAction[{Type}] Dialogue: {DialogueBefore?.Substring(0, Math.Min(30, DialogueBefore?.Length ?? 0))}...";
        }
    }

    /// <summary>
    /// Available emotes/animations NPCs can perform
    /// </summary>
    public enum NPCEmote
    {
        Wave,
        Nod,
        Shake,      // Shake head (no)
        Point,
        Salute,
        Shrug,
        Laugh,
        Sad,
        Angry,
        Scared,
        Thumbsup,
        Clap,
        Sit,
        Crouch,
        LookAround
    }
}
