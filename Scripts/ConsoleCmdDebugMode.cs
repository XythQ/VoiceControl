using System;
using System.Collections.Generic;
using UnityEngine;
using XNPCVoiceControl;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Console command "DM" — toggle debug mode (SCore-style).
    /// Type "dm" in the game console to enable/disable verbose logging.
    /// </summary>
    public class ConsoleCmdDebugMode : ConsoleCmdAbstract
    {
        public override string[] getCommands()
        {
            return new string[] { "vcdebug" };
        }

        public override string getDescription()
        {
            return "Toggle debug mode for 1-XNPCVoiceControl (verbose logging)";
        }

        public override string getHelp()
        {
            return @"Debug Mode Console Command:

vcdebug         - Toggle verbose debug logging on/off
vcdebug on      - Enable verbose debug logging
vcdebug off     - Disable verbose debug logging

When enabled, additional log messages are shown for:
  - Phrase trigger checks &amp; matches
  - NPC action execution details
  - LLM request/response info
  - Server retry progress
  - Key press/release events
  - Entity scanning details
  - Configuration reloads

Status is persisted per-player and survives restarts.";
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;

            if (_params.Count == 0)
            {
                // Toggle mode
                bool currentMode = Log.DebugMode;
                Log.SetDebugMode(!currentMode);
                output.Output($"[1-XNPCVoiceControl] Debug mode: {(Log.DebugMode ? "ON" : "OFF")}");
                return;
            }

            string arg = _params[0].ToLower();

            switch (arg)
            {
                case "on":
                    Log.SetDebugMode(true);
                    output.Output("[1-XNPCVoiceControl] Debug mode: ON");
                    break;
                case "off":
                    Log.SetDebugMode(false);
                    output.Output("[1-XNPCVoiceControl] Debug mode: OFF");
                    break;
                default:
                    output.Output($"Unknown argument: {arg}");
                    output.Output(getHelp());
                    break;
            }
        }
    }
}
