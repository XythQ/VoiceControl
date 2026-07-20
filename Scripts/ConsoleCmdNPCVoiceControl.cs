using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using XNPCVoiceControl.Actions;
using XNPCVoiceControl.Core;
using XNPCVoiceControl.TTS;
using XNPCVoiceControl.STT;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Console commands for testing and managing NPC LLM chat.
    /// </summary>
    public class ConsoleCmdLLMChat : ConsoleCmdAbstract
    {
        public override string[] getCommands()
        {
            return new string[] { "voicecontrol", "vc" };
        }

        public override string getDescription()
        {
            return "NPC VoiceControl commands - voicecontrol <test|status|talk|tts|stt|action>";
        }

        public override string getHelp()
        {
            return @"NPC VoiceControl Console Commands:

voicecontrol test            - Test connection to LLM server
voicecontrol status          - Show status and performance
voicecontrol talk <message>  - Talk to the nearest NPC
voicecontrol action <action> - Execute action (follow, stop, guard, wait)
voicecontrol clear           - Clear conversation history
voicecontrol list            - List active NPC sessions

TTS Commands:
voicecontrol tts             - Show TTS status
voicecontrol tts test        - Test TTS with sample speech
voicecontrol tts on          - Enable TTS globally
voicecontrol tts off         - Disable TTS globally
voicecontrol tts voices      - List available voices

STT Commands (Voice Input):
voicecontrol stt             - Show STT status
voicecontrol stt test        - Test recording and transcription
voicecontrol stt on          - Enable voice input
voicecontrol stt off         - Disable voice input
voicecontrol stt devices     - List available microphones

Face Lip-Sync (Dev):
voicecontrol reloadface      - Hot-reload lip-sync config from modconfig.xml (no restart)
voicecontrol facescan        - Scan nearby NPC face rigs (diagnostic)

Diagnostics:
voicecontrol selftest        - Run self-test (config, personalities, servers, STT/TTS/LLM)
voicecontrol npcdiag         - Dump current world NPC roster with persistence fields
voicecontrol patrol          - Show patrol state of nearest hired NPC (order, points)
voicecontrol patrol clear    - Clear recorded patrol points and stop patrol on nearest hired NPC

Formation:
voicecontrol formation <bearing|n|ne|e|se|s|sw|w|nw> <metres> - Set world-anchored socket on nearest hired NPC
  bearing: compass degrees (0=N, 90=E, 180=S, 270=W) or cardinal name (n/ne/e/se/s/sw/w/nw)
  metres: distance in metres (0 = cancel formation)
  Examples: vc formation e 5 (east/5m), vc formation ne 4, vc formation 0 0 (cancel)

Examples:
  voicecontrol test
  voicecontrol talk Hello, how are you?
  voicecontrol tts test
  voicecontrol stt test
  voicecontrol action follow";
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            if (_params.Count == 0)
            {
                SingletonMonoBehaviour<SdtdConsole>.Instance.Output(getHelp());
                return;
            }

            string subCommand = _params[0].ToLower();

            switch (subCommand)
            {
                case "test":
                    TestLLMConnection();
                    break;
                case "status":
                    ShowStatus();
                    break;
                case "talk":
                    if (_params.Count < 2)
                    {
                        SingletonMonoBehaviour<SdtdConsole>.Instance.Output("Usage: voicecontrol talk <message>");
                        return;
                    }
                    string message = string.Join(" ", _params.GetRange(1, _params.Count - 1));
                    TalkToNearestNPC(message);
                    break;
                case "action":
                    if (_params.Count < 2)
                    {
                        SingletonMonoBehaviour<SdtdConsole>.Instance.Output("Usage: voicecontrol action <follow|stop|guard|wait>");
                        return;
                    }
                    TestAction(_params[1]);
                    break;
                case "clear":
                    ClearAllHistory();
                    break;
                case "list":
                    ListActiveSessions();
                    break;
                case "tts":
                    HandleTTSCommand(_params);
                    break;
                case "stt":
                    HandleSTTCommand(_params);
                    break;
                case "facescan":
                    ScanFaceRigs();
                    break;
                case "reloadface":
                case "facereload":
                    ReloadFaceConfig();
                    break;
                case "selftest":
                    RunSelfTest();
                    break;
                case "npcdiag":
                    RunNPCDiag();
                    break;
                case "patrol":
                    RunPatrolDiag(_params);
                    break;
                case "formation":
                    RunFormationCommand(_params);
                    break;
                default:
                    SingletonMonoBehaviour<SdtdConsole>.Instance.Output($"Unknown: {subCommand}");
                    SingletonMonoBehaviour<SdtdConsole>.Instance.Output(getHelp());
                    break;
            }
        }

        private void TestLLMConnection()
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            output.Output("Testing LLM connection...");

            LLMService.Instance.SendChatRequest(
                -1,
                "You are a test. Respond with 'Connection successful!' only.",
                new List<ChatMessage>(),
                "Test",
                default, // no interruption for console test
                response => output.Output($"[SUCCESS] Response: {response}"),
                error => output.Output($"[ERROR] {error}\nMake sure llama-server is running (auto-started by the mod)")
            );
        }

        private void ShowStatus()
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            output.Output("=== NPC LLM Chat Status ===");
            output.Output($"LLM Service: {(LLMService.Instance != null ? "Active" : "Inactive")}");

            var llm = LLMService.Instance;
            if (llm != null && llm.RequestCount > 0)
            {
                output.Output($"Requests: {llm.RequestCount}");
                output.Output($"Last Response: {llm.LastResponseTimeMs:F0}ms");
                output.Output($"Avg Response: {llm.AvgResponseTimeMs:F0}ms");
            }

            int activeNPCs = 0;
            var world = GameManager.Instance?.World;
            if (world != null)
            {
                foreach (var entity in world.Entities.list)
                {
                    if (entity is EntityAlive alive && alive.GetComponent<NPCChatComponent>() != null)
                        activeNPCs++;
                }
            }
            output.Output($"Active NPC Sessions: {activeNPCs}");
            output.Output("");
            output.Output("To talk: @Hello NPC!");
        }

        private void TalkToNearestNPC(string message)
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();

            if (player == null)
            {
                output.Output("No player found");
                return;
            }

            EntityAlive nearestNPC = FindNearestNPC(player, 15f);
            if (nearestNPC == null)
            {
                output.Output("No NPC found nearby (15m range)");
                return;
            }

            var chatComponent = Core.ChatComponentManager.GetOrCreate(nearestNPC);
            if (chatComponent == null)
            {
                output.Output("Failed to init chat with NPC");
                return;
            }

            output.Output($"Talking to {chatComponent.NPCName}...");
            chatComponent.ProcessPlayerMessage(message, player, false, response =>
            {
                output.Output($"[{chatComponent.NPCName}]: {response}");
            });
        }

        private void TestAction(string actionName)
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();

            if (player == null)
            {
                output.Output("No player found");
                return;
            }

            EntityAlive nearestNPC = FindNearestNPC(player, 15f);
            if (nearestNPC == null)
            {
                output.Output("No NPC found nearby");
                return;
            }

            NPCActionType actionType;
            switch (actionName.ToLower())
            {
                case "follow": actionType = NPCActionType.Follow; break;
                case "stop": actionType = NPCActionType.StopFollow; break;
                case "wait": actionType = NPCActionType.Wait; break;
                case "guard": actionType = NPCActionType.Guard; break;
                default:
                    output.Output($"Unknown action: {actionName}");
                    output.Output("Available: follow, stop, wait, guard");
                    return;
            }

            var action = new NPCAction(actionType);
            ActionExecutor.Instance.ExecuteAction(nearestNPC, player, action);
            output.Output($"Executed {actionType} on NPC");
        }

        private EntityAlive FindNearestNPC(EntityPlayer player, float maxDistance)
        {
            EntityAlive closest = null;
            float closestDist = maxDistance;

            var world = GameManager.Instance?.World;
            if (world == null) return null;

            foreach (var entity in world.Entities.list)
            {
                if (entity is EntityAlive alive && IsNPC(alive) && alive.entityId != player.entityId)
                {
                    float dist = Vector3.Distance(player.position, alive.position);
                    if (dist < closestDist)
                    {
                        closest = alive;
                        closestDist = dist;
                    }
                }
            }
            return closest;
        }

        private bool IsNPC(EntityAlive entity)
        {
            // Use the same check as voice commands — ChatComponentManager.IsChatTarget
            // handles type names, entity class names (survivor/trader), Bandits, and hired animals.
            return ChatComponentManager.IsChatTarget(entity);
        }

        private void ClearAllHistory()
        {
            int cleared = 0;
            var world = GameManager.Instance?.World;
            if (world != null)
            {
                foreach (var entity in world.Entities.list)
                {
                    if (entity is EntityAlive alive)
                    {
                        var chat = alive.GetComponent<NPCChatComponent>();
                        if (chat != null)
                        {
                            chat.ClearHistory();
                            cleared++;
                        }
                    }
                }
            }
            SingletonMonoBehaviour<SdtdConsole>.Instance.Output($"Cleared {cleared} NPC conversations");
        }

        private void ListActiveSessions()
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            output.Output("=== Active NPC Sessions ===");

            int count = 0;
            var world = GameManager.Instance?.World;
            if (world != null)
            {
                foreach (var entity in world.Entities.list)
                {
                    if (entity is EntityAlive alive)
                    {
                        var chat = alive.GetComponent<NPCChatComponent>();
                        if (chat != null)
                        {
                            var history = chat.GetHistory();
                            var state = chat.GetCurrentState();
                            string status = state?.IsFollowing == true ? " [Following]" :
                                           state?.IsGuarding == true ? " [Guarding]" : "";
                            output.Output($"  [{alive.entityId}] {chat.NPCName} - {history.Count} msgs{status}");
                            count++;
                        }
                    }
                }
            }

            if (count == 0) output.Output("  No active sessions");
            output.Output($"Total: {count}");
        }

        private void HandleTTSCommand(List<string> _params)
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            var tts = TTSService.Instance;

            // Default: show status
            if (_params.Count < 2)
            {
                ShowTTSStatus();
                return;
            }

            string subCommand = _params[1].ToLower();

            switch (subCommand)
            {
                case "test":
                    TestTTS();
                    break;
                case "on":
                    EnableTTS(true);
                    break;
                case "off":
                    EnableTTS(false);
                    break;
                case "voices":
                    ListVoices();
                    break;
                case "status":
                    ShowTTSStatus();
                    break;
                default:
                    output.Output($"Unknown TTS command: {subCommand}");
                    output.Output("Use: tts, tts test, tts on, tts off, tts voices");
                    break;
            }
        }

        private void ShowTTSStatus()
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            var tts = TTSService.Instance;
            var config = XNPCVoiceControlMod.TTSConfig;

            output.Output("=== TTS Status ===");
            output.Output($"TTS Enabled: {(config?.Enabled ?? false)}");
            output.Output($"TTS Initialized: {(tts?.IsInitialized ?? false)}");

            if (tts != null && tts.RequestCount > 0)
            {
                output.Output($"Requests: {tts.RequestCount}");
                output.Output($"Last Synthesis: {tts.LastSynthesisTimeMs:F0}ms");
                output.Output($"Avg Synthesis: {tts.AvgSynthesisTimeMs:F0}ms");
            }

            if (config != null)
            {
                output.Output($"Default Voice: {config.DefaultVoice}");
                output.Output($"Volume: {config.Volume:P0}");
                output.Output($"Server: {config.ServerUrl}");
            }

            if (!(tts?.IsInitialized ?? false))
            {
            output.Output("TTS not initialized. Check that sherpa-server is running.");
            }
        }

        private void TestTTS()
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            var tts = TTSService.Instance;

            if (tts == null || !tts.IsInitialized)
            {
                output.Output("TTS not initialized");
                return;
            }

            output.Output("Testing TTS synthesis...");
            Log.Debug(() => "TestTTS() called");

            // Create a test audio source at player position
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null)
            {
                output.Output("No player found for audio test");
                Log.Warning("TestTTS: No player found");
                return;
            }

            string testText = "Hey survivor, the wasteland is rough but we will make it through together.";
            Log.Debug(() => $"TestTTS: Calling Synthesize with text: {testText}");

            tts.Synthesize(
                testText,
                null,
                "a",
                wavBytes =>
                {
                    Log.Debug(() => $"TestTTS: WAV bytes received ({wavBytes.Length} bytes)");
                    output.Output($"[SUCCESS] Generated audio");

                    var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                    if (player == null)
                    {
                        output.Output("No player found for audio test");
                        return;
                    }

                    var (audioData, sampleRate, channels) = AudioUtils.ProcessWavBytes(wavBytes);
                    AudioClip clip = AudioUtils.CreateClipFromData(audioData, sampleRate, channels);
                    if (clip == null)
                    {
                        output.Output("Failed to parse WAV data");
                        return;
                    }

                    var go = new GameObject("TTSTest");
                    go.transform.position = player.position;
                    var audioSource = go.AddComponent<AudioSource>();
                    audioSource.clip = clip;
                    audioSource.volume = XNPCVoiceControlMod.TTSConfig?.Volume ?? 0.8f;
                    audioSource.spatialBlend = 0f;
                    audioSource.bypassEffects = true;
                    audioSource.bypassListenerEffects = true;
                    audioSource.bypassReverbZones = true;
                    audioSource.priority = 0;
                    audioSource.Play();

                    output.Output($"Playing audio ({clip.length:F2}s)...");

                    // Cleanup after playback
                    player.StartCoroutine(WaitForClipFinish(clip, go));
                },
                error =>
                {
                    Log.Warning($"TestTTS: ERROR - {error}");
                    output.Output($"[ERROR] TTS failed: {error}");
                }
            );
        }

        /// <summary>
        /// Parse WAV bytes into a Unity AudioClip (same logic as NPCAudioPlayer).
        /// </summary>


        /// <summary>
        /// Wait for clip playback to finish, then destroy the GameObject.
        /// </summary>
        private static System.Collections.IEnumerator WaitForClipFinish(AudioClip clip, GameObject go)
        {
            yield return new WaitForSeconds((float)clip.length);
            UnityEngine.Object.Destroy(clip);
            UnityEngine.Object.Destroy(go);
        }

        private void EnableTTS(bool enabled)
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            var config = XNPCVoiceControlMod.TTSConfig;

            if (config == null)
            {
                output.Output("TTS config not loaded");
                return;
            }

            // Note: This only affects runtime state, not the config file
            // We need to update all active NPC chat components

            var world = GameManager.Instance?.World;
            if (world != null)
            {
                int updated = 0;
                foreach (var entity in world.Entities.list)
                {
                    if (entity is EntityAlive alive)
                    {
                        var chat = alive.GetComponent<NPCChatComponent>();
                        if (chat != null)
                        {
                            chat.TTSEnabled = enabled;
                            updated++;
                        }
                    }
                }
                output.Output($"TTS {(enabled ? "enabled" : "disabled")} for {updated} NPCs");
            }

            if (enabled && !TTSService.Instance.IsInitialized)
            {
                output.Output("Warning: TTS not initialized");
            }
        }

        private void ListVoices()
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            var config = XNPCVoiceControlMod.TTSConfig;

            output.Output("=== Available Voices ===");
            output.Output($"Default: {config?.DefaultVoice ?? "af_aoede"}");
            output.Output($"Trader: {config?.TraderVoice ?? "am_adam"}");
            output.Output($"Companion: {config?.CompanionVoice ?? "af_sarah"}");
            output.Output($"Bandit: {config?.BanditVoice ?? "am_eric"}");
            output.Output("");
            output.Output("See Resources/KokoroSherpa/voices.bin for available voices.");
            output.Output($"Server: {config?.ServerUrl ?? "http://127.0.0.1:5053"}");
        }

        // ========== STT Commands ==========

        private void HandleSTTCommand(List<string> _params)
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;

            // Default: show status
            if (_params.Count < 2)
            {
                ShowSTTStatus();
                return;
            }

            string subCommand = _params[1].ToLower();

            switch (subCommand)
            {
                case "test":
                    TestSTT();
                    break;
                case "on":
                    EnableSTT(true);
                    break;
                case "off":
                    EnableSTT(false);
                    break;
                case "devices":
                    ListMicrophones();
                    break;
                case "status":
                    ShowSTTStatus();
                    break;
                case "refresh":
                    RefreshSTT();
                    break;
                default:
                    output.Output($"Unknown STT command: {subCommand}");
                    output.Output("Use: stt, stt test, stt on, stt off, stt devices, stt refresh");
                    break;
            }
        }

        private void ShowSTTStatus()
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;

            if (GameManager.IsDedicatedServer)
            {
                output.Output("=== STT Status ===");
                output.Output("Not available on dedicated server (no audio subsystem)");
                return;
            }

            var stt = STTService.Instance;
            var mic = MicrophoneCapture.Instance;
            var config = XNPCVoiceControlMod.STTConfig;

            output.Output("=== STT Status ===");
            output.Output($"STT Enabled: {(config?.Enabled ?? false)}");
            output.Output($"STT Initialized: {(stt?.IsInitialized ?? false)}");
            output.Output($"Microphone Ready: {(mic?.IsInitialized ?? false)}");
            output.Output($"Voice Input Active: {(mic?.IsEnabled ?? false)}");

            if (stt != null && stt.RequestCount > 0)
            {
                output.Output($"Requests: {stt.RequestCount}");
                output.Output($"Last Transcription: {stt.LastTranscriptionTimeMs:F0}ms");
                output.Output($"Avg Transcription: {stt.AvgTranscriptionTimeMs:F0}ms");
            }

            if (config != null)
            {
                output.Output($"Server: {config.ServerUrl}");
                output.Output($"Push-to-talk Key: {config.PushToTalkKey}");
                output.Output($"Max Recording: {config.MaxRecordingSeconds}s");
            }

            if (mic != null && mic.IsInitialized)
            {
                output.Output($"Selected Microphone: {mic.SelectedDevice ?? "None"}");
            }

            if (!(stt?.IsInitialized ?? false))
            {
                output.Output("");
                output.Output("STT not initialized. Check that whisper-server is running.");
            }
        }

        private void TestSTT()
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;

            if (GameManager.IsDedicatedServer)
            {
                output.Output("Not available on dedicated server (no audio subsystem)");
                return;
            }

            var stt = STTService.Instance;
            var mic = MicrophoneCapture.Instance;

            if (stt == null || !stt.IsInitialized)
            {
                output.Output("STT not initialized");
                return;
            }

            if (mic == null || !mic.IsInitialized)
            {
                output.Output("Microphone not initialized");
                output.Output("Check that a microphone is connected");
                return;
            }

            output.Output("Recording for 3 seconds... Speak now!");

            // Use test recording
            mic.TestRecording(3f, wavData =>
            {
                if (wavData == null || wavData.Length < 100)
                {
                    output.Output("[ERROR] Failed to record audio");
                    return;
                }

                output.Output($"Recorded {wavData.Length} bytes, sending to whisper-server...");

                stt.Transcribe(
                    wavData,
                    text =>
                    {
                        output.Output($"[SUCCESS] Transcription: \"{text}\"");
                    },
                    error =>
                    {
                        output.Output($"[ERROR] Transcription failed: {error}");
                        output.Output("Make sure whisper-server is running on port 5052");
                    }
                );
            });
        }

        private void EnableSTT(bool enabled)
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            var mic = MicrophoneCapture.Instance;
            var config = XNPCVoiceControlMod.STTConfig;

            if (config == null)
            {
                output.Output("STT config not loaded");
                return;
            }

            if (mic == null || !mic.IsInitialized)
            {
                output.Output("Microphone not initialized");
                return;
            }

            mic.IsEnabled = enabled;
            output.Output($"Voice input {(enabled ? "enabled" : "disabled")}");

            if (enabled)
            {
                output.Output($"Hold '{config.PushToTalkKey}' to talk to NPCs");

                if (!STTService.Instance.IsInitialized)
                {
                    output.Output("Warning: STT not initialized");
                }
            }
        }

        private void ListMicrophones()
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;

            if (GameManager.IsDedicatedServer)
            {
                output.Output("Not available on dedicated server (no audio subsystem)");
                return;
            }

            var mic = MicrophoneCapture.Instance;

            output.Output("=== Available Microphones ===");

            string[] devices = mic?.GetDevices() ?? Microphone.devices;

            if (devices == null || devices.Length == 0)
            {
                output.Output("  No microphones found!");
                output.Output("  Check your audio settings and permissions");
                return;
            }

            string selected = mic?.SelectedDevice ?? "";

            for (int i = 0; i < devices.Length; i++)
            {
                string marker = devices[i] == selected ? " [SELECTED]" : "";
                output.Output($"  [{i}] {devices[i]}{marker}");
            }

            output.Output("");
            output.Output($"Total: {devices.Length} device(s)");
        }

        private void RefreshSTT()
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            var config = XNPCVoiceControlMod.STTConfig;

            if (config == null)
            {
                output.Output("STT not initialized");
                return;
            }

            output.Output($"Checking whisper-server at {config.ServerUrl}...");

            // Simple HTTP health check
            try
            {
                var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(config.ServerUrl.TrimEnd('/') + "/health");
                request.Method = "GET";
                request.Timeout = 5000;

                using (var response = (System.Net.HttpWebResponse)request.GetResponse())
                using (var reader = new System.IO.StreamReader(response.GetResponseStream()))
                {
                    string body = reader.ReadToEnd();
                    output.Output($"Health check response: {body}");
                }
            }
            catch (Exception ex)
            {
                output.Output($"Health check failed: {ex.Message}");
                output.Output("Make sure whisper-server is running.");
            }
        }
        // ========== Face Scan (Diagnostic, throwaway) ==========

        private void ReloadFaceConfig()
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;

            try
            {
                if (GameManager.IsDedicatedServer)
                {
                    Log.Out("[RELOADFACE] Dedicated server, aborting");
                    output.Output("Cannot run on dedicated server");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[RELOADFACE] GameManager check failed: {ex.Message}");
            }

            // Re-parse TTS config from modconfig.xml and personalities from personalities.xml
            XNPCVoiceControlMod.ReloadTTSConfig();
            XNPCVoiceControlMod.ReloadPersonalities();
            var newConfig = XNPCVoiceControlMod.TTSConfig;

            // Marshal to main thread — touches Unity APIs and regenerates meshes
            ThreadManager.AddSingleTaskMainThread("VCReloadFace", (_taskInfo) =>
            {
                int reloaded = NPCFaceLipSync.ReloadAll(newConfig);
                output.Output($"[RELOADFACE] Re-applied FaceLipSync config to {reloaded} live component(s)");

                if (reloaded == 0)
                {
                    output.Output("No active NPCFaceLipSync instances found — NPCs may not be spawned or lip-sync is disabled");
                }
            });
        }

        // ========== Face Scan (Diagnostic, throwaway) ==========

        private void ScanFaceRigs()
        {
            Log.Debug(() => "[FACESCAN] Command invoked");

            try
            {
                if (GameManager.IsDedicatedServer)
                {
                    Log.Debug(() => "[FACESCAN] Dedicated server, aborting");
                    SingletonMonoBehaviour<SdtdConsole>.Instance.Output("Cannot run on dedicated server");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[FACESCAN] GameManager check failed: {ex.Message}");
            }

            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            if (output == null)
            {
                Log.Debug(() => "[FACESCAN] SdtdConsole.Instance is null");
                return;
            }

            EntityPlayer player = null;

            try
            {
                player = GameManager.Instance?.World?.GetPrimaryPlayer();
                if (player == null)
                {
                    Log.Debug(() => "[FACESCAN] No player found");
                    output.Output("No player found");
                    return;
                }
                Log.Debug(() => $"[FACESCAN] Player at {player.position}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[FACESCAN] GetPrimaryPlayer failed: {ex.Message}");
                output.Output("Failed to get player: " + ex.Message);
                return;
            }

            output.Output("=== Face Rig Scan (15m) ===");
            Log.Debug(() => "[FACESCAN] Header printed, scanning entities...");

            var world = GameManager.Instance?.World;
            if (world == null)
            {
                Log.Debug(() => "[FACESCAN] World is null");
                output.Output("World not loaded");
                return;
            }

            int entityCount = 0;
            try { entityCount = world.Entities.list.Count; } catch (Exception ex) { Log.Warning($"[FACESCAN] Can't read entities: {ex.Message}"); }
            Log.Debug(() => $"[FACESCAN] Total entities in world: {entityCount}");
            output.Output($"Scanning {entityCount} entities... (running on main thread)");

            // Marshal to main thread — Unity APIs (GetComponentInChildren, mesh.isReadable) crash on background threads
            long handlerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            Log.Debug(() => $"[FACESCAN] handler thread={handlerThread}");

            ThreadManager.AddSingleTaskMainThread("VCFaceScan", (_taskInfo) =>
            {
                Log.Debug(() => $"[FACESCAN] scan thread={System.Threading.Thread.CurrentThread.ManagedThreadId}");

                int scanned = 0;
                for (int i = 0; i < entityCount; i++)
                {
                    Entity entity = null;
                    try
                    {
                        entity = world.Entities.list[i];
                        Log.Debug(() => $"[FACESCAN]   [{i}] {entity.GetType().Name}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[FACESCAN] Entity[{i}] access failed: {ex.Message}");
                        continue;
                    }

                    try
                    {
                        // Filter to EntityAlive (humanoid NPCs) — skip player, animals, flying zombies.
                        // Accepts both EntityAliveSDX (V2/V3) and EntityAliveSDXV4.
                        if (!(entity is EntityAlive aliveSDX) || entity is EntityPlayer) continue;

                        float dist = Vector3.Distance(player.position, aliveSDX.position);
                        if (dist > 15f) continue;

                        scanned++;
                        string line = ScanSingleFaceRig(aliveSDX, dist);
                        output.Output(line);
                        Log.Debug(() => line);
                    }
                    catch (Exception ex)
                    {
                        string warn = $"[FACESCAN] Error scanning {entity.GetType().Name}: {ex.Message}";
                        output.Output(warn);
                        Log.Warning(warn);
                    }
                }

                output.Output($"=== Scanned {scanned} NPCs ===");
            });
        }

        private string ScanSingleFaceRig(EntityAlive npc, float dist)
        {
            Log.Debug(() => $"[FACESCAN]   ScanSingleFaceRig entered: {npc.GetType().Name}");
            var parts = new System.Text.StringBuilder();
            parts.Append($"[FACESCAN] {npc.GetType().Name} (d={dist:F1})");

            // Animator
            Log.Debug(() => $"[FACESCAN]     calling GetComponentInChildren<Animator>...");
            var animator = npc.GetComponentInChildren<Animator>(true);
            Log.Debug(() => $"[FACESCAN]     got animator: {animator != null}");
            bool hasAnimator = animator != null;
            bool isHuman = false;
            if (hasAnimator)
            {
                try { isHuman = animator.isHuman; } catch { /* non-humanoid rig or animator not initialized */ }
            }
            parts.Append($" | Animator={hasAnimator} isHuman={isHuman}");

            // Tier 1 — Humanoid Jaw + Head (only for humanoid rigs)
            Transform jawBone = null;
            Transform headBone = null;
            if (hasAnimator && isHuman)
            {
                try { jawBone = animator.GetBoneTransform(HumanBodyBones.Jaw); } catch { /* non-humanoid rig or bone not mapped */ }
                parts.Append($" | Jaw={(jawBone != null ? jawBone.name : "null")}");

                try { headBone = animator.GetBoneTransform(HumanBodyBones.Head); } catch { /* non-humanoid rig or bone not mapped */ }
                parts.Append($" | Head={(headBone != null ? headBone.name : "null")}");

                // Tier 2 — unmapped jaw bone search
                if (headBone != null)
                {
                    var jawChildren = FindJawInChildren(headBone);
                    parts.Append($" | unmappedJaw={(jawChildren.Count > 0 ? string.Join(", ", jawChildren) : "none")}");
                }
                else
                {
                    parts.Append(" | unmappedJaw=skipped (no head)");
                }
            }

            // SkinnedMeshRenderers — scan for FACE meshes, blendshapes, open-mouth candidates
            var smrs = npc.GetComponentsInChildren<SkinnedMeshRenderer>(true); // include inactive
            parts.Append(" | SMRs:");
            int smrCount = 0;

            // Track best open-mouth candidate across ALL SMRs (blendshapes readable even on non-readable meshes)
            string bestOpenCandidate = null;       // "smrName/shapeName (idx N)"
            int bestOpenCandidateStrength = -1;    // 3=strong, 2=viseme, 1=weak
            bool anySmrHasShapes = false;
            List<string> allShapeNames = new List<string>();   // for "manual pick needed" listing
            bool anyFaceReadable = false;

            foreach (var smr in smrs)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                smrCount++;

                string smrInfo = $" {smr.name}(readable={smr.sharedMesh.isReadable} verts={smr.sharedMesh.vertexCount})";
                bool isFace = false;
                int headWeightedVerts = 0;

                // FACE detection (needs readable mesh)
                try
                {
                    if (headBone != null)
                    {
                        var result = IsFaceMeshWithCount(smr, headBone);
                        isFace = result.isFace;
                        headWeightedVerts = result.headWeightedVerts;
                    }
                }
                catch (Exception ex)
                {
                    smrInfo += $" [faceCheck error: {ex.Message}]";
                }

                // FACE info (head-weighted verts — needs readable)
                if (isFace)
                {
                    anyFaceReadable = anyFaceReadable || smr.sharedMesh.isReadable;
                    smrInfo += $" headWeightedVerts={headWeightedVerts}";
                }

                // Blendshapes — ALWAYS read, independent of FACE flag and isReadable
                // blendShapeCount / GetBlendShapeName work on non-readable meshes (metadata only)
                int shapeCount = 0;
                try { shapeCount = smr.sharedMesh.blendShapeCount; } catch { /* mesh corrupted or null sharedMesh */ }

                if (shapeCount > 0)
                {
                    anySmrHasShapes = true;

                    // Collect shape names for open-mouth matching
                    var shapeNames = new List<string>();
                    for (int s = 0; s < shapeCount; s++)
                    {
                        string shapeName = "";
                        try { shapeName = smr.sharedMesh.GetBlendShapeName(s); } catch { /* mesh corrupted or blendshape index invalid */ }
                        shapeNames.Add(shapeName);

                        // Track all names for manual-pick listing
                        if (!allShapeNames.Contains(shapeName))
                        {
                            allShapeNames.Add(shapeName);
                        }

                        // Open-mouth candidate matching
                        int strength = MatchOpenMouthStrength(shapeName);
                        if (strength > bestOpenCandidateStrength)
                        {
                            bestOpenCandidateStrength = strength;
                            bestOpenCandidate = $"{smr.name}/{shapeName} (idx {s})";
                        }
                    }

                    string shapesStr = "shapes[" + shapeCount + "]: ";
                    for (int s = 0; s < shapeCount; s++)
                    {
                        if (s > 0) shapesStr += " ";
                        shapesStr += $"{s}={shapeNames[s]}";
                    }
                    smrInfo += $" | {shapesStr}";
                }
                else
                {
                    smrInfo += " | shapes: none";
                }

                parts.Append(isFace ? smrInfo + " FACE" : smrInfo);
            }

            if (smrCount == 0) parts.Append(" none");

            // Open-mouth candidate line
            if (bestOpenCandidate != null)
            {
                string strengthLabel = bestOpenCandidateStrength switch { 3 => "(strong)", 2 => "(viseme)", 1 => "(weak)", _ => "" };
                parts.Append($" | openCandidate={bestOpenCandidate} {strengthLabel}");
            }
            else if (anySmrHasShapes)
            {
                // Shapes exist but no auto-matched open-mouth — manual pick needed
                string shapesList = string.Join(", ", allShapeNames);
                parts.Append($" | HAS-SHAPES-NO-OPEN-MATCH: {shapesList}");
            }
            else
            {
                parts.Append(" | openCandidate=NONE");
            }

            // Resolved-tier verdict
            string verdict = ResolveTierVerdict(
                bestOpenCandidate, bestOpenCandidateStrength,
                anySmrHasShapes, allShapeNames,
                jawBone != null,
                headBone != null && FindJawInChildren(headBone).Count > 0,
                anyFaceReadable);
            parts.Append($" | RESOLVED: {verdict}");

            return parts.ToString();
        }

        private int MatchOpenMouthStrength(string shapeName)
        {
            if (string.IsNullOrEmpty(shapeName)) return 0;
            string lower = shapeName.ToLowerInvariant();

            // Strong matches
            if (lower.Contains("jawopen") || lower.Contains("mouthopen") ||
                lower.Contains("jaw_open") || lower.Contains("mouth_open") ||
                lower.Contains("openmouth")) return 3;

            // Viseme matches
            if (lower.Contains("viseme_aa") || lower.Contains("v_aa") ||
                lower == "aa" || lower == "ah") return 2;

            // Weak match
            if (lower.Contains("open")) return 1;

            return 0;
        }

        private string ResolveTierVerdict(
            string openCandidate, int candidateStrength,
            bool anySmrHasShapes, List<string> allShapeNames,
            bool hasJawBone, bool hasUnmappedJaw, bool anyFaceReadable)
        {
            // TIER 0 — blendshape with auto-matched open-mouth
            if (openCandidate != null && candidateStrength > 0)
            {
                // Extract shape name from "smrName/shapeName (idx N)"
                string shapeName = openCandidate.Split('/')[1].Split(' ')[0];
                return $"TIER 0 (blendshape '{shapeName}')";
            }

            // TIER 0 — shapes present but no auto-matched open (manual pick needed)
            if (anySmrHasShapes)
            {
                string list = string.Join(", ", allShapeNames);
                return $"TIER 0 (shapes present — manual open-shape pick needed: {list})";
            }

            // TIER 1 — humanoid Jaw slot mapped
            if (hasJawBone)
                return "TIER 1 (humanoid Jaw bone)";

            // TIER 2 — unmapped jaw bone under Head
            if (hasUnmappedJaw)
                return "TIER 2 (unmapped jaw bone under Head)";

            // TIER 3 — readable face mesh for procedural verts
            if (anyFaceReadable)
                return "TIER 3 (procedural chin-vert, readable mesh)";

            return "NONE (needs R/W flip or authoring)";
        }

        private List<string> FindJawInChildren(Transform root)
        {
            var results = new List<string>();
            foreach (Transform child in root)
            {
                string nameLower = child.name.ToLowerInvariant();
                if (nameLower.Contains("jaw") || nameLower.Contains("mandible") || nameLower.Contains("chin"))
                {
                    results.Add(child.name);
                }
                // Recursive search
                var deeper = FindJawInChildren(child);
                results.AddRange(deeper);
            }
            return results;
        }

        private (bool isFace, int headWeightedVerts) IsFaceMeshWithCount(SkinnedMeshRenderer smr, Transform headBone)
        {
            var mesh = smr.sharedMesh;
            if (mesh == null) return (false, 0);

            BoneWeight[] boneWeights = mesh.boneWeights;
            Transform[] bones = smr.bones;
            if (boneWeights == null || bones == null || bones.Length == 0) return (false, 0);

            int headIndex = -1;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == headBone)
                {
                    headIndex = i;
                    break;
                }
            }

            if (headIndex < 0) return (false, 0);

            int count = 0;
            foreach (var bw in boneWeights)
            {
                if (bw.boneIndex0 == headIndex && bw.weight0 > 0.5f) { count++; continue; }
                if (bw.boneIndex1 == headIndex && bw.weight1 > 0.5f) { count++; continue; }
                if (bw.boneIndex2 == headIndex && bw.weight2 > 0.5f) { count++; continue; }
                if (bw.boneIndex3 == headIndex && bw.weight3 > 0.5f) { count++; continue; }
            }

            return (count > 0, count);
        }

        // ========== Self-Test (vc selftest) ==========

        private void RunSelfTest()
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;
            Log.Out("=== vc selftest — starting ===");
            output.Output("=== vc selftest — starting ===");

            Task.Run(() =>
            {
                try
                {
                    Action<string> log = s => MainThreadDispatcher.Enqueue(() => { output.Output(s); Log.Out(s); });
                    SelfTestConfig(log);
                    SelfTestPersonalities(log);
                    SelfTestSidecarHealth(log);
                    SelfTestSttRoundTrip(log);
                    SelfTestTtsRoundTrip(log);
                    SelfTestPhraseTrigger(log);
                    SelfTestLlmPing(log);

                    MainThreadDispatcher.Enqueue(() => { output.Output("=== vc selftest — complete ==="); Log.Out("=== vc selftest — complete ==="); });
                }
                catch (Exception ex)
                {
                    MainThreadDispatcher.Enqueue(() => { output.Output($"[SELFTEST] FATAL: {ex.Message}"); Log.Warning($"vc selftest fatal: {ex.Message}"); });
                    Log.Warning($"vc selftest fatal: {ex}");
                }
            });
        }

        private void SelfTestConfig(Action<string> log)
        {
            var config = XNPCVoiceControlMod.Config;
            var ttsConfig = XNPCVoiceControlMod.TTSConfig;
            var sttConfig = XNPCVoiceControlMod.STTConfig;

            string detail = $"LLM={config?.Endpoint ?? "null"} model={config?.Model ?? "null"}, " +
                $"TTS={ttsConfig?.TtsEngine ?? "null"}@{ttsConfig?.ServerUrl ?? "null"}, " +
                $"STT={sttConfig?.ServerUrl ?? "null"}";

            bool pass = config != null && ttsConfig != null && sttConfig != null &&
                !string.IsNullOrEmpty(config.Endpoint) && !string.IsNullOrEmpty(ttsConfig.ServerUrl);

            log($"[SELFTEST] {(pass ? "PASS" : "FAIL")} Config: {detail}");
        }

        private void SelfTestPersonalities(Action<string> log)
        {
            var pm = PersonalityManager.Instance;
            int count = pm.LoadedCount;
            int faceOverrideCount = pm.FaceOverrideCount;

            bool pass = count > 0;
            log($"[SELFTEST] {(pass ? "PASS" : "FAIL")} Personalities: {count} loaded, {faceOverrideCount} with FaceOverride");
        }

        private void SelfTestSidecarHealth(Action<string> log)
        {
            var ttsConfig = XNPCVoiceControlMod.TTSConfig;
            string ttsEngine = ttsConfig?.TtsEngine ?? "sherpa";
            int ttsPort = ttsEngine == "supertonic" ? 5054 : 5053;

            // LLM (5055)
            log($"[SELFTEST] {(PingHealth($"http://127.0.0.1:{LLMService.DefaultChatPort}/health") ? "PASS" : "FAIL")} Sidecar llama-server: {LLMService.DefaultChatPort}/health");

            // TTS (5053 or 5054)
            log($"[SELFTEST] {(PingHealth($"http://127.0.0.1:{ttsPort}/health") ? "PASS" : "FAIL")} Sidecar {ttsEngine}: {ttsPort}/health");

            // Whisper (5052)
            log($"[SELFTEST] {(PingHealth("http://127.0.0.1:5052/health") ? "PASS" : "FAIL")} Sidecar whisper-server: 5052/health");

            // Embed — only if configured
            string embedUrl = LLMService.Instance.EmbeddingEndpoint;

            if (!string.IsNullOrEmpty(embedUrl))
            {
                string embedHost = embedUrl.Replace("/v1/embeddings", "");
                bool embedOk = PingHealth(embedHost + "/health");
                log($"[SELFTEST] {(embedOk ? "PASS" : "FAIL")} Sidecar embed-server: {embedHost}/health");
            }
        }

        private void SelfTestSttRoundTrip(Action<string> log)
        {
            try
            {
                byte[] silentWav = CreateSilentWav(32000, 16000); // 1s of silence at 16kHz
                if (silentWav == null)
                {
                    log("[SELFTEST] FAIL STT round-trip: could not create test WAV");
                    return;
                }

                var sttConfig = XNPCVoiceControlMod.STTConfig;
                string url = sttConfig?.ServerUrl ?? "http://127.0.0.1:5052";
                bool ok = PostSilentWavToWhisper(url, silentWav);

                log($"[SELFTEST] {(ok ? "PASS" : "FAIL")} STT round-trip: {url}/inference (silent WAV, empty transcript expected)");
            }
            catch (Exception ex)
            {
                log($"[SELFTEST] FAIL STT round-trip: {ex.Message}");
            }
        }

        private void SelfTestTtsRoundTrip(Action<string> log)
        {
            try
            {
                var ttsConfig = XNPCVoiceControlMod.TTSConfig;
                string url = ttsConfig?.ServerUrl ?? "http://127.0.0.1:5053";
                string voice = ttsConfig?.DefaultVoice ?? "af_aoede";

                string jsonBody = $@"{{""text"":""Self test."",""voice"":""{voice}"",""speed"":1,""lang"":""en""}}";
                byte[] wavBytes = PostTtsRequest(url, jsonBody);

                bool ok = wavBytes != null && wavBytes.Length > 0;
                log($"[SELFTEST] {(ok ? "PASS" : "FAIL")} TTS round-trip: {url}/tts voice={voice} ({(ok ? $"{wavBytes.Length} WAV bytes" : "empty/error")})");
            }
            catch (Exception ex)
            {
                log($"[SELFTEST] FAIL TTS round-trip: {ex.Message}");
            }
        }

        private void SelfTestPhraseTrigger(Action<string> log)
        {
            try
            {
                var handler = PhraseTriggerHandler.Instance;
                if (!handler.Initialized)
                {
                    log("[SELFTEST] FAIL Phrase trigger: handler not initialized");
                    return;
                }

                // Run matcher on "follow me" — we just need to know if a trigger matches.
                // TryHandlePhrase needs npc/player params; pass nulls since we only care about the match.
                string dummyResponse;
                bool matched = handler.TryHandlePhrase("follow me", null, null, "TestNPC", out dummyResponse, executeAction: false);

                if (matched)
                {
                    log($"[SELFTEST] PASS Phrase trigger: \"follow me\" matched → {handler.LastMatchedTriggerName}");
                }
                else
                {
                    log("[SELFTEST] FAIL Phrase trigger: \"follow me\" did not match any trigger");
                }
            }
            catch (Exception ex)
            {
                log($"[SELFTEST] FAIL Phrase trigger: {ex.Message}");
            }
        }

        private void SelfTestLlmPing(Action<string> log)
        {
            try
            {
                var config = XNPCVoiceControlMod.Config;
                string endpoint = config?.Endpoint ?? LLMService.DefaultChatEndpoint;
                string model = config?.Model ?? "llama3";

                // Minimal 1-message completion, max_tokens=8.
                string body = $@"{{""model"":""{model}"",""messages"":[{{""role"":""user"",""content"":""ping""}}],""max_tokens"":8}}";

                var request = (HttpWebRequest)WebRequest.Create(endpoint);
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                request.Proxy = null;

                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bodyBytes.Length;
                using (var stream = request.GetRequestStream())
                    stream.Write(bodyBytes, 0, bodyBytes.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        string responseBody = reader.ReadToEnd();
                        bool ok = !string.IsNullOrEmpty(responseBody);
                        string preview = ok && responseBody.Length > 80 ? responseBody.Substring(0, 80) + "..." : responseBody;
                        log($"[SELFTEST] {(ok ? "PASS" : "FAIL")} LLM ping: {endpoint} ({preview})");
                    }
                }
            }
            catch (Exception ex)
            {
                log($"[SELFTEST] FAIL LLM ping: {ex.Message}");
            }
        }

        // --- Patrol diagnostic (Phase 4) ---

        /// <summary>
        /// vc patrol — show patrol state of nearest hired NPC.
        /// vc patrol clear — clear recorded points and stop patrol.
        /// </summary>
        private void RunPatrolDiag(List<string> args)
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;

            // Marshal to main thread — Unity APIs crash on background threads
            ThreadManager.AddSingleTaskMainThread("VCPatrolDiag", (_taskInfo) =>
            {
                var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                if (player == null)
                {
                    output.Output("[PATROL-DIAG] No player found");
                    Log.Out("[PATROL-DIAG] No player found");
                    return;
                }

                var npc = FindNearestNPC(player, 15f);
                if (npc == null)
                {
                    output.Output("[PATROL-DIAG] No hired NPC found within 15m");
                    Log.Out("[PATROL-DIAG] No hired NPC found within 15m");
                    return;
                }

                if (!Core.ChatComponentManager.TryGet(npc.entityId, out var comp))
                {
                    output.Output($"[PATROL-DIAG] No chat component on {npc.GetType().Name}");
                    Log.Out($"[PATROL-DIAG] No chat component on {npc.GetType().Name}");
                    return;
                }

                // "vc patrol clear" — mirrors CancelPatrolRecord exactly
                if (args.Count > 0 && args[0].ToLower() == "clear")
                {
                    if (npc is IEntityOrderReceiverSDX r) r.PatrolCoordinates.Clear();
                    EntityUtilities.SetCurrentOrder(npc.entityId, EntityUtilities.Orders.Stay);
                    comp.SetPatrolRecording(false);
                    comp.SetActivelyPatrolling(false);
                    string msg = $"[PATROL-DIAG] Cleared patrol for {comp.NPCName} (order->Stay)";
                    output.Output(msg);
                    Log.Out(msg);
                    return;
                }

                // "vc patrol" — read-only diagnostic
                string state = comp.IsRecordingPatrol ? "recording" : comp.IsActivelyPatrolling ? "patrolling" : "idle";
                string header = $"[PATROL-DIAG] {comp.NPCName} (entityId={npc.entityId}) state={state}";
                output.Output(header);
                Log.Out(header);

                if (npc is IEntityOrderReceiverSDX receiver)
                {
                    int count = receiver.PatrolCoordinates.Count;
                    output.Output($"[PATROL-DIAG]   Points: {count}");
                    Log.Out($"[PATROL-DIAG]   Points: {count}");
                    for (int i = 0; i < count; i++)
                    {
                        Vector3 p = receiver.PatrolCoordinates[i];
                        string line = $"[PATROL-DIAG]     [{i}] ({p.x:F1}, {p.y:F1}, {p.z:F1})";
                        output.Output(line);
                        Log.Out(line);
                    }
                }
                else
                {
                    output.Output("[PATROL-DIAG]   Not an IEntityOrderReceiverSDX — no patrol coordinates");
                    Log.Out("[PATROL-DIAG]   Not an IEntityOrderReceiverSDX — no patrol coordinates");
                }
            });
        }

        // --- Formation slot command ---

        /// <summary>
        /// Set world-anchored formation socket on nearest hired NPC.
        /// Accepts compass bearing (degrees or cardinal name) + metres. Console bypasses yaw resolution.
        /// </summary>
        private void RunFormationCommand(List<string> args)
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;

            // Marshal to main thread — Unity APIs crash on background threads
            ThreadManager.AddSingleTaskMainThread("VCFormation", (_taskInfo) =>
            {
                var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                if (player == null)
                {
                    output.Output("[FORMATION] No player found");
                    Log.Out("[FORMATION] No player found");
                    return;
                }

                var npc = FindNearestNPC(player, 15f);
                if (npc == null)
                {
                    output.Output("[FORMATION] No hired NPC found within 15m");
                    Log.Out("[FORMATION] No hired NPC found within 15m");
                    return;
                }

                // Parse args: vc formation <bearing|n|ne|e|se|s|sw|w|nw> <metres>
                // Console bypasses tier lookup + yaw resolution — sets cvars directly.
                float bearing = 0f;
                float metres = 0f;

                if (args.Count > 1)
                {
                    string bearingArg = args[1].ToLower();
                    // Map cardinal names to bearings.
                    switch (bearingArg)
                    {
                        case "n": bearing = 0f; break;
                        case "ne": bearing = 45f; break;
                        case "e": bearing = 90f; break;
                        case "se": bearing = 135f; break;
                        case "s": bearing = 180f; break;
                        case "sw": bearing = 225f; break;
                        case "w": bearing = 270f; break;
                        case "nw": bearing = 315f; break;
                        default:
                            try { bearing = float.Parse(bearingArg, System.Globalization.CultureInfo.InvariantCulture); } catch { }
                            break;
                    }
                    bearing = FormationUtils.Snap45(bearing);
                }

                if (args.Count > 2)
                {
                    try { metres = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture); } catch { }
                }

                string stateName = metres <= 0f ? "cancel" : $"{bearing:F0}\u00b0/{metres}m";

                // Set cvars on the NPC (Buffs.SetCustomVar — self-networks in MP).
                npc.Buffs.SetCustomVar("vcFormationAngle", bearing);
                npc.Buffs.SetCustomVar("vcFormationDist", metres);

                // Verify round-trip.
                float rAngle = EntityUtilities.GetCVarValue(npc.entityId, "vcFormationAngle");
                float rDist = EntityUtilities.GetCVarValue(npc.entityId, "vcFormationDist");

                string msg = $"[FORMATION] {npc.EntityName} set={stateName} readback=angle:{rAngle} dist:{rDist}";
                output.Output(msg);
                Log.Out(msg);
            });
        }

        // --- Leader collision pass-through switch ---

        // --- NPC diagnostic roster dump ---

        /// <summary>
        /// On-demand dump of current world NPC roster with persistence-relevant fields.
        /// Same format as the auto-scan at T+5s/T+15s. Read-only — no behavior changes.
        /// </summary>
        private void RunNPCDiag()
        {
            var output = SingletonMonoBehaviour<SdtdConsole>.Instance;

            // Marshal to main thread — Unity APIs crash on background threads
            ThreadManager.AddSingleTaskMainThread("VCNPCDiag", (_taskInfo) =>
            {
                var world = GameManager.Instance?.World;
                if (world == null)
                {
                    output.Output("[NPC-DIAG] World not loaded");
                    Log.Out("[NPC-DIAG] World not loaded");
                    return;
                }

                int day = 0;
                try { day = (int)SkyManager.dayCount; } catch { }

                EntityPlayer player = null;
                try { player = world.GetPrimaryPlayer(); } catch { }

                int count = 0;
                foreach (var entity in world.Entities.list)
                {
                    if (!(entity is EntityAlive alive)) continue;
                    if (!ChatComponentManager.IsChatTarget(alive)) continue;

                    string line = ServerManager.BuildNPCDiagLine(alive, player);
                    output.Output(line);
                    Log.Out(line);
                    count++;
                }

                string header = $"[NPC-DIAG] Roster on-demand (day {day}): {count} NPCs in world.";
                output.Output(header);
                Log.Out(header);
            });
        }

        // --- Self-test helpers (background thread, no Unity APIs) ---

        private static bool PingHealth(string url)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                request.KeepAlive = true;
                request.Proxy = null;

                using (var response = (HttpWebResponse)request.GetResponse())
                    return response.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }

        private static bool PostSilentWavToWhisper(string serverUrl, byte[] silentWav)
        {
            try
            {
                string boundary = "----WebKitFormBoundary7MA4YWxkTrZu0gW";
                var bodyBuilder = new MemoryStream();
                using (var writer = new BinaryWriter(bodyBuilder, Encoding.UTF8))
                {
                    string header = $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\n";
                    writer.Write(Encoding.UTF8.GetBytes(header));
                    writer.Write(silentWav);
                    writer.Write(Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n"));
                }
                byte[] bodyBytes = bodyBuilder.ToArray();

                var request = (HttpWebRequest)WebRequest.Create(serverUrl.TrimEnd('/') + "/inference");
                request.Method = "POST";
                request.ContentType = $"multipart/form-data; boundary={boundary}";
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                request.Proxy = null;
                request.ContentLength = bodyBytes.Length;

                using (var stream = request.GetRequestStream())
                    stream.Write(bodyBytes, 0, bodyBytes.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                    return response.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }

        private static byte[] PostTtsRequest(string serverUrl, string jsonBody)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(serverUrl.TrimEnd('/') + "/tts");
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                request.Proxy = null;

                byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
                request.ContentLength = bodyBytes.Length;
                using (var stream = request.GetRequestStream())
                    stream.Write(bodyBytes, 0, bodyBytes.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    using (var ms = new MemoryStream())
                    {
                        response.GetResponseStream().CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch { return null; }
        }

        private static byte[] CreateSilentWav(int sampleCount, int sampleRate)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                int channels = 1;
                int bitsPerSample = 16;
                int byteRate = sampleRate * channels * bitsPerSample / 8;
                int blockAlign = channels * bitsPerSample / 8;
                int dataSize = sampleCount * bitsPerSample / 8;

                writer.Write(new char[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataSize);
                writer.Write(new char[] { 'W', 'A', 'V', 'E' });
                writer.Write(new char[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);  // PCM
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(byteRate);
                writer.Write((short)blockAlign);
                writer.Write((short)bitsPerSample);
                writer.Write(new char[] { 'd', 'a', 't', 'a' });
                writer.Write(dataSize);
                writer.Write(new byte[dataSize]);

                return stream.ToArray();
            }
        }
    }
}
