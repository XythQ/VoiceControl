using System;
using UnityEngine;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Conditional logging wrapper for 1-XNPCVoiceControl.
    /// 
    /// - Log.Out()   — Always logs. Use for critical events: init, shutdown, errors,
    ///                  server status, personality assignment, phrase trigger results,
    ///                  STT recording start/stop, audio pipeline results.
    /// - Log.Debug()  — Only logs when debug mode is enabled (config toggle or "dm" console command).
    ///                  Use for routine state changes, action details, LLM request info,
    ///                  server retry progress, key press/release events.
    ///                  Accepts Func&lt;string&gt; to guarantee zero allocation when off.
    /// - Log.Warning() — Always logs warnings.
    /// - Log.Error()   — Always logs errors.
    /// </summary>
    public static class Log
    {
        private const string PREFIX = "[1-XNPCVoiceControl] ";
        private const string CVAR_DEBUG = "XNPCVoiceControl_DebugMode";

        // Cached debug mode — refreshed at most once per second to avoid the full
        // player+buff-cvar walk on every one of 352 Debug call sites.
        private static bool _cachedDebugMode;
        private static int _nextRefreshTick;   // Environment.TickCount-based, thread-safe

        /// <summary>
        /// Whether verbose debug logging is enabled.
        /// Stored as a player buff CVar for per-player persistence.
        /// Defaults to false (suppress debug spam).
        /// </summary>
        public static bool DebugMode
        {
            get
            {
                // Refresh at most once per second. Subtraction form handles int wraparound.
                if (Environment.TickCount - _nextRefreshTick >= 0)
                {
                    _nextRefreshTick = Environment.TickCount + 1000;
                    try
                    {
                        var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                        if (player != null && player.Buffs.HasCustomVar(CVAR_DEBUG))
                        {
                            _cachedDebugMode = player.Buffs.GetCustomVar(CVAR_DEBUG) > 0f;
                            return _cachedDebugMode;
                        }
                    }
                    catch
                    {
                        // Player not available yet (early init), default to false
                    }
                    _cachedDebugMode = false;
                }
                return _cachedDebugMode;
            }
        }

        /// <summary>
        /// Set debug mode on the current player's buffs.
        /// </summary>
        public static void SetDebugMode(bool enabled)
        {
            try
            {
                var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                if (player != null)
                {
                    player.Buffs.SetCustomVar(CVAR_DEBUG, enabled ? 1f : 0f);
                }
            }
            catch
            {
                // Player not available
            }
            // Update cache instantly so "dm" toggle takes effect immediately.
            _cachedDebugMode = enabled;
        }

        /// <summary>
        /// Always logs. Use for critical events that must never be suppressed:
        /// init/shutdown, server status, personality assignment, phrase trigger results,
        /// STT recording start/stop, audio pipeline results, network errors.
        /// </summary>
        public static void Out(string message)
        {
            UnityEngine.Debug.Log(PREFIX + message);
        }

        /// <summary>
        /// Only logs when DebugMode is true. Accepts a factory function for zero formatting
        /// cost when off; capturing lambdas still allocate a closure at the call site —
        /// guard per-frame/per-tick call sites with `if (Log.DebugMode)`.
        /// 
        /// Usage: Log.Debug(() => $"NPC {name} moved to {pos}")
        /// </summary>
        public static void Debug(Func<string> messageFactory)
        {
            if (DebugMode && messageFactory != null)
            {
                UnityEngine.Debug.Log(PREFIX + messageFactory());
            }
        }

        /// <summary>
        /// Only logs when DebugMode is true. Overload for pre-constructed strings
        /// (use sparingly — the string is always allocated even if debug is off).
        /// </summary>
        public static void Debug(string message)
        {
            if (DebugMode)
            {
                UnityEngine.Debug.Log(PREFIX + message);
            }
        }

        /// <summary>
        /// Always logs warnings. Never suppressed.
        /// </summary>
        public static void Warning(string message)
        {
            UnityEngine.Debug.LogWarning(PREFIX + message);
        }

        /// <summary>
        /// Always logs errors. Never suppressed.
        /// </summary>
        public static void Error(string message)
        {
            UnityEngine.Debug.LogError(PREFIX + message);
        }
    }
}
