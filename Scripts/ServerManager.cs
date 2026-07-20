using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using XNPCVoiceControl.Core;

namespace XNPCVoiceControl
{
    /// <summary>
    /// MonoBehaviour host for ServerManager coroutines.
    /// Server startup (Thread.Sleep, port-checking) must not block the main thread.
    /// </summary>
    public class ServerManagerHost : MonoBehaviour
    {
        private static ServerManagerHost _instance;

        public static ServerManagerHost Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("ServerManagerHost");
                    _instance = go.AddComponent<ServerManagerHost>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        public void StartServersCoroutine()
        {
            StartCoroutine(ServerManager.StartServersRoutine());
        }

        public void StopServersCoroutine()
        {
            ServerManager.StopServers();
        }

        /// <summary>
        /// Restart whisper-server with the current STT config.
        /// Fires a coroutine on this DontDestroyOnLoad host — survives UI close.
        /// </summary>
        public void RestartWhisperServer()
        {
            StartCoroutine(ServerManager.RestartWhisperServerRoutine());
        }

        void OnDestroy()
        {
            ServerManager.StopServers();
            // Singleton survives reloads — Unity null-checks destroyed GOs
        }
    }

    /// <summary>
    /// Manages automatic startup of llama-server.exe, sherpa-server.exe, and whisper-server.exe on Windows.
    /// Uses coroutines via ServerManagerHost to avoid blocking the main thread.
    /// </summary>
    public static class ServerManager
    {
        /// <summary>
        /// Overall server readiness state machine. Updated from background warmup task,
        /// marshaled to main thread via MainThreadDispatcher for safe reads in game loop.
        /// </summary>
        public enum ServerReadyState { Starting, Ready, Failed }

        private static Process _llamaServerProcess;
        private static Process _sherpaServerProcess;
        private static Process _supertonicServerProcess;
        private static Process _whisperServerProcess;
        private static Process _embedServerProcess;
        private static string _modPath = null;           // set at startup, reused by watchdog
        private static bool _serversStarted = false;
        private static bool _serverReady = false;      // result flag for StartLlamaServerOnce
        private static bool _serversStopped = false;    // guard against duplicate StopServers calls
        private static bool _whisperGpuFailed = false;  // remember if Vulkan GPU crashed — stick to CPU after that
        private static bool _ttServerReady = false;     // result flag for StartSherpaServerOnce
        private static bool _ttSupertonicReady = false; // result flag for StartSupertonicServerOnce
        private static bool _sttServerReady = false;    // result flag for StartWhisperServerOnce
        private static bool _embedServerReady = false;  // result flag for StartEmbedServerOnce
        private static bool _wasWorldLoaded = false;     // tracks world-loaded → menu transition for watchdog
        private static bool _worldWatchdogStarted = false; // ensure WorldUnloadWatchdog started only once
        private static readonly HashSet<int> _tacticalDefaultApplied = new HashSet<int>(); // DefaultTacticalMode: applied once per player per session

        /// <summary>
        /// Whether the llama-server is running and accepting connections.
        /// </summary>
        public static bool IsServerReady => _serverReady;

        /// <summary>
        /// Whether the sherpa-server (TTS) is running and accepting connections.
        /// </summary>
        public static bool IsTtSServerReady => _ttServerReady;

        /// <summary>
        /// Whether the supertonic-server (TTS) is running and accepting connections.
        /// </summary>
        public static bool IsTtSSupertonicReady => _ttSupertonicReady;

        /// <summary>
        /// Whether the whisper-server (STT) is running and accepting connections.
        /// </summary>
        public static bool IsSTTServerReady => _sttServerReady;

        /// <summary>
        /// Whether the dedicated embedding server (nomic, port 5056) is running.
        /// </summary>
        public static bool IsEmbedServerReady => _embedServerReady;

        /// <summary>
        /// Overall server readiness state. Updated from background warmup task via MainThreadDispatcher.
        /// Read safely on main thread (NPCChatComponent, UI, etc.).
        /// </summary>
        public static ServerReadyState ReadyState { get; private set; } = ServerReadyState.Starting;

        public static void StartServers()
        {
            if (GameManager.IsDedicatedServer)
            {
                Log.Out("[VoiceMod] Dedicated server — sidecar startup skipped (no GPU/audio subsystem)");
                return;
            }
            ServerManagerHost.Instance.StartServersCoroutine();
        }

        /// <summary>
        /// Coroutine version of server startup. Yields between steps to avoid freezing the game.
        /// </summary>
        public static IEnumerator StartServersRoutine()
        {
            if (_serversStarted) yield break;
            _serversStarted = true;
            _serversStopped = false;  // re-arm shutdown latch on each world load

            if (!PlatformHelper.IsWindows)
            {
                Log.Out("ServerManager: Not on Windows, skipping auto-start");
                yield break;
            }

            string modPath = GetModPath();
            if (string.IsNullOrEmpty(modPath))
            {
                Log.Warning("ServerManager: Could not determine mod path");
                yield break;
            }

            _modPath = modPath;

            Log.Out($"ServerManager: Mod path = {modPath}");

            // VULKAN BLACKWELL FIX (parent process): Prevent ggml-vulkan from crashing on RTX 50-series GPUs.
            // Set once so all children inherit it via environment — NoInheritProcess.Start does NOT copy startInfo.EnvironmentVariables.
            Environment.SetEnvironmentVariable("GGML_VK_DISABLE_COOPMAT2", "1");

            // Kill any stale processes from a previous game session or old mod DLL
            KillProcessOnPort(LLMService.DefaultChatPort, "llama-server");
            KillProcessOnPort(LLMService.DefaultEmbedPort, "embed-server");
            KillProcessOnPort(5053, "sherpa-server");
            KillProcessOnPort(5054, "supertonic-server");
            KillProcessOnPort(5052, "whisper-server");
            // Give OS time to release ports
            yield return new WaitForSeconds(1f);

            // Start llama-server.exe (chat model, GPU, port 5055)
            yield return StartLlamaServerRoutine(modPath);

            // Start TTS server — only the selected engine (sherpa or supertonic), not both.
            string ttsEngine = XNPCVoiceControlMod.TTSConfig?.TtsEngine ?? "sherpa";
            if (ttsEngine == "supertonic")
            {
                yield return StartSupertonicServerRoutine(modPath);
            }
            else
            {
                yield return StartSherpaServerRoutine(modPath);
            }

            // Start whisper-server.exe (STT)
            yield return StartWhisperServerRoutine(modPath);

            // Start embed-server (nomic-embed-text, CPU-only, port 5056)
            yield return StartEmbedServerRoutine(modPath);

            // Consolidated server health summary (replaces per-server "accepting connections" Log.Out lines)
            string healthyServers = string.Join(
                ", ",
                new (string Name, bool Ready)[] {
                    ("llama", _serverReady),
                    (ttsEngine == "supertonic" ? "supertonic" : "sherpa", ttsEngine == "supertonic" ? _ttSupertonicReady : _ttServerReady),
                    ("whisper", _sttServerReady),
                    ("embed", _embedServerReady)
                }.Where(s => s.Ready).Select(s => s.Name));
            Log.Out($"ServerManager: {healthyServers} {(string.IsNullOrEmpty(healthyServers) ? "no servers healthy" : "ready")}");

            // Start mid-session liveness watchdog for all sidecars
            ServerManagerHost.Instance.StartCoroutine(SidecarLivenessWatchdog());

            // Start world-unload watchdog (stops sidecars on exit-to-menu to release ports).
            // One long-lived coroutine on the persistent host — survives world transitions.
            if (!_worldWatchdogStarted)
            {
                _worldWatchdogStarted = true;
                ServerManagerHost.Instance.StartCoroutine(WorldUnloadWatchdog());
            }
        }

        private static IEnumerator StartLlamaServerRoutine(string modPath)
        {
            // Check if something is already listening on port 5055 (e.g. stale process survived the kill step,
            // or an old mod DLL also started one). If so, reuse it instead of starting a duplicate.
            yield return CheckTcpPortNonBlocking("127.0.0.1", LLMService.DefaultChatPort, 1f);
            if (_lastPortCheckResult)
            {
                Log.Debug(() => $"ServerManager: llama-server already listening on port {LLMService.DefaultChatPort}, reusing existing instance");
                _serverReady = true;
                // Don't set _llamaServerProcess — we don't own this process.
                // StopServers() will fall back to KillProcessOnPort({LLMService.DefaultChatPort}) if handle is null.
                yield break;
            }

            const int maxRetries = 2;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (attempt > 1)
                {
                    Log.Debug(() => $"ServerManager: llama-server retry {attempt}/{maxRetries}...");
                    KillProcessOnPort(LLMService.DefaultChatPort, "llama-server");
                    yield return new WaitForSeconds(2f);
                }

                _serverReady = false;
                yield return StartLlamaServerOnce(modPath, attempt);

                if (_serverReady)
                    yield break;

                // If we have a process but it didn't become ready, kill it before retry
                if (_llamaServerProcess != null && !_llamaServerProcess.HasExited)
                {
                    try { _llamaServerProcess.Kill(); } catch { /* process already exited or access denied */ }
                    _llamaServerProcess = null;
                }

                if (attempt < maxRetries)
                {
                    Log.Debug(() => $"ServerManager: llama-server attempt {attempt} failed, retrying...");
                    yield return new WaitForSeconds(3f);
                }
            }

            Log.Warning("ServerManager: llama-server failed to start after all retries");
            Log.Warning("ServerManager: Ensure bin/LlamaServer/ and Resources/ are present");
        }

        private static IEnumerator StartLlamaServerOnce(string modPath, int attempt)
        {
            // Find llama-server.exe
            string serverDir = Path.Combine(modPath, "bin", "LlamaServer");
            if (!Directory.Exists(serverDir))
            {
                Log.Warning($"ServerManager: bin/LlamaServer/ not found at {serverDir}");
                yield break;
            }

            string serverExe = Path.Combine(serverDir, "llama-server.exe");
            if (!File.Exists(serverExe))
            {
                Log.Debug(() => $"ServerManager: llama-server.exe not found at {serverExe}");
                yield break;
            }

            // Find GGUF model in Resources/
            string resourcesDir = Path.Combine(modPath, "Resources");
            string configuredModelFilename = XNPCVoiceControlMod.Config?.ModelFilename ?? "";
            string modelPath = FindGgufModel(resourcesDir, configuredModelFilename);
            if (modelPath == null)
            {
                Log.Debug(() => $"ServerManager: No .gguf model found in {resourcesDir}");
                yield break;
            }

            Log.Debug(() => $"ServerManager: Found model at {modelPath}");

            // Build arguments
            var args = new System.Text.StringBuilder();
            args.Append($"-m \"{modelPath}\"");
            args.Append($" --port {LLMService.DefaultChatPort}");
            args.Append(" --host 127.0.0.1");
            args.Append(" --no-ui");           // Disable web UI
            int ctxSize = XNPCVoiceControlMod.Config?.ContextSize ?? 8192;
            args.Append($" --ctx-size {ctxSize}");
            args.Append(" --ubatch-size 2048");
            args.Append(" --gpu-layers -1");
            args.Append(" --split-mode none");
            args.Append(" --device vulkan0");  // Always use primary GPU (best one is always first)
            args.Append(" -lv 4");             // Verbose logging for device selection diagnostics

            Log.Debug(() => $"ServerManager: Starting llama-server.exe (attempt {attempt})...");
            Log.Debug(() => $"ServerManager: Args: {args}");

            try
            {
                _llamaServerProcess = NoInheritProcess.Start(serverExe, args.ToString(), serverDir,
                    Path.Combine(serverDir, "llama-server-stdout.log"));
                if (_llamaServerProcess == null) throw new Exception("NoInheritProcess returned null");
                Log.Debug(() => $"ServerManager: llama-server started (PID: {_llamaServerProcess?.Id})");
            }
            catch (Exception ex)
            {
                Log.Warning($"ServerManager: Failed to start llama-server: {ex.Message}");
                yield break;
            }

            // Wait for server to be ready (it needs time to load the model)
            Log.Debug(() => "ServerManager: Waiting for llama-server to initialize (loading model)...");
            yield return new WaitForSeconds(5f);

            // Non-blocking TCP readiness check (replaces BeginConnect + WaitOne which blocked main thread)
            bool ready = false;
            for (int poll = 0; poll < 60 && !ready; poll++)
            {
                if (_llamaServerProcess != null && _llamaServerProcess.HasExited)
                {
                    Log.Warning($"ServerManager: llama-server process died (exit code: {_llamaServerProcess.ExitCode})");
                    string capturedLog = NoInheritProcess.ReadCapturedLog(Path.Combine(serverDir, "llama-server-stdout.log"));
                    if (!string.IsNullOrEmpty(capturedLog))
                        Log.Warning($"ServerManager: llama-server captured output:\n{capturedLog}");
                    yield break;
                }

                yield return CheckTcpPortNonBlocking("127.0.0.1", LLMService.DefaultChatPort, 1f);
                ready = _lastPortCheckResult;
                if (ready)
                {
                    Log.Debug(() => $"ServerManager: llama-server is accepting connections on port {LLMService.DefaultChatPort}!");
                    break;
                }
                if (poll < 59)
                {
                    yield return new WaitForSeconds(1f);
                }
            }

            if (!ready)
            {
                Log.Debug(() => $"ServerManager: llama-server did not become ready (attempt {attempt})");
            }

            _serverReady = ready;
        }

        private static IEnumerator StartSherpaServerRoutine(string modPath)
        {
            // Check if something is already listening on port 5053 (e.g. stale process survived the kill step,
            // or an old mod DLL also started one). If so, reuse it instead of starting a duplicate.
            yield return CheckTcpPortNonBlocking("127.0.0.1", 5053, 1f);
            if (_lastPortCheckResult)
            {
                Log.Debug(() => "ServerManager: sherpa-server already listening on port 5053, reusing existing instance");
                _ttServerReady = true;
                // Don't set _sherpaServerProcess — we don't own this process.
                // StopServers() will fall back to KillProcessOnPort(5053) if handle is null.
                yield break;
            }

            const int maxRetries = 2;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (attempt > 1)
                {
                    Log.Debug(() => $"ServerManager: sherpa-server retry {attempt}/{maxRetries}...");
                    KillProcessOnPort(5053, "sherpa-server");
                    yield return new WaitForSeconds(2f);
                }

                _ttServerReady = false;
                yield return StartSherpaServerOnce(modPath, attempt);

                if (_ttServerReady)
                    yield break;

                // If we have a process but it didn't become ready, kill it before retry
                if (_sherpaServerProcess != null && !_sherpaServerProcess.HasExited)
                {
                    try { _sherpaServerProcess.Kill(); } catch { /* process already exited or access denied */ }
                    _sherpaServerProcess = null;
                }

                if (attempt < maxRetries)
                {
                    Log.Debug(() => $"ServerManager: sherpa-server attempt {attempt} failed, retrying...");
                    yield return new WaitForSeconds(3f);
                }
            }

            Log.Warning("ServerManager: sherpa-server failed to start after all retries");
            Log.Warning("ServerManager: Ensure bin/SherpaServer/ and Config/ServerConfig.xml are present");
        }

        private static IEnumerator StartSherpaServerOnce(string modPath, int attempt)
        {
            // Find sherpa-server.exe
            string serverDir = Path.Combine(modPath, "bin", "SherpaServer");
            if (!Directory.Exists(serverDir))
            {
                Log.Debug(() => $"ServerManager: bin/SherpaServer/ not found at {serverDir}");
                yield break;
            }

            string serverExe = Path.Combine(serverDir, "sherpa-server.exe");
            if (!File.Exists(serverExe))
            {
                Log.Debug(() => $"ServerManager: sherpa-server.exe not found at {serverExe}");
                yield break;
            }

            // sherpa-server reads Config/ServerConfig.xml (not modconfig.xml).
            // No CLI args needed — model paths, engine type, and voice map are all in XML.
            string configPath = Path.Combine(modPath, "Config", "ServerConfig.xml");
            if (!File.Exists(configPath))
            {
                Log.Debug(() => $"ServerManager: Config/ServerConfig.xml not found at {configPath}");
                yield break;
            }

            Log.Debug(() => $"ServerManager: Found sherpa-server config at {configPath}");

            Log.Debug(() => $"[SherpaBootCommand] {serverExe} (cwd: {serverDir})");

            try
            {
                _sherpaServerProcess = NoInheritProcess.Start(serverExe, "", serverDir,
                    Path.Combine(serverDir, "sherpa-server-stdout.log"));
                if (_sherpaServerProcess == null) throw new Exception("NoInheritProcess returned null");
                Log.Debug(() => $"ServerManager: sherpa-server started (PID: {_sherpaServerProcess?.Id})");
            }
            catch (Exception ex)
            {
                Log.Warning($"ServerManager: Failed to start sherpa-server: {ex.Message}");
                yield break;
            }

            // Wait for server to be ready (port opens immediately, model loads in background)
            Log.Debug(() => "ServerManager: Waiting for sherpa-server to initialize...");
            yield return new WaitForSeconds(5f);

            // Non-blocking TCP readiness check.
            bool ready = false;
            for (int poll = 0; poll < 30 && !ready; poll++)
            {
                // Check if the process is still alive — if it crashed, stop polling immediately
                if (_sherpaServerProcess != null && _sherpaServerProcess.HasExited)
                {
                    int exitCode = _sherpaServerProcess.ExitCode;
                    Log.Warning($"ServerManager: sherpa-server process died (exit code: {exitCode})");
                    string capturedLog = NoInheritProcess.ReadCapturedLog(Path.Combine(serverDir, "sherpa-server-stdout.log"));
                    if (!string.IsNullOrEmpty(capturedLog))
                        Log.Warning($"ServerManager: sherpa-server captured output:\n{capturedLog}");
                    yield break;
                }

                yield return CheckTcpPortNonBlocking("127.0.0.1", 5053, 1f);
                ready = _lastPortCheckResult;
                if (ready)
                {
                    Log.Debug(() => "ServerManager: sherpa-server is accepting connections on port 5053!");
                    break;
                }
                if (poll < 29)
                {
                    yield return new WaitForSeconds(1f);
                }
            }

            if (!ready)
            {
                Log.Debug(() => $"ServerManager: sherpa-server did not become ready (attempt {attempt})");
            }

            _ttServerReady = ready;
        }

        private static IEnumerator StartSupertonicServerRoutine(string modPath)
        {
            // Check if something is already listening on port 5054.
            yield return CheckTcpPortNonBlocking("127.0.0.1", 5054, 1f);
            if (_lastPortCheckResult)
            {
                Log.Debug(() => "ServerManager: supertonic-server already listening on port 5054, reusing existing instance");
                _ttSupertonicReady = true;
                yield break;
            }

            const int maxRetries = 2;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (attempt > 1)
                {
                    Log.Debug(() => $"ServerManager: supertonic-server retry {attempt}/{maxRetries}...");
                    KillProcessOnPort(5054, "supertonic-server");
                    yield return new WaitForSeconds(2f);
                }

                _ttSupertonicReady = false;
                yield return StartSupertonicServerOnce(modPath, attempt);

                if (_ttSupertonicReady)
                    yield break;

                if (_supertonicServerProcess != null && !_supertonicServerProcess.HasExited)
                {
                    try { _supertonicServerProcess.Kill(); } catch { /* process already exited or access denied */ }
                    _supertonicServerProcess = null;
                }

                if (attempt < maxRetries)
                {
                    Log.Debug(() => $"ServerManager: supertonic-server attempt {attempt} failed, retrying...");
                    yield return new WaitForSeconds(3f);
                }
            }

            Log.Warning("ServerManager: supertonic-server failed to start after all retries");
            Log.Warning("ServerManager: Ensure bin/SupertonicServer/ and Config/SupertonicConfig.xml are present");
        }

        private static IEnumerator StartSupertonicServerOnce(string modPath, int attempt)
        {
            // Find supertonic-server.exe
            string serverDir = Path.Combine(modPath, "bin", "SupertonicServer");
            if (!Directory.Exists(serverDir))
            {
                Log.Debug(() => $"ServerManager: bin/SupertonicServer/ not found at {serverDir}");
                yield break;
            }

            string serverExe = Path.Combine(serverDir, "supertonic-server.exe");
            if (!File.Exists(serverExe))
            {
                Log.Debug(() => $"ServerManager: supertonic-server.exe not found at {serverExe}");
                yield break;
            }

            // supertonic-server reads Config/SupertonicConfig.xml (not modconfig.xml).
            string configPath = Path.Combine(modPath, "Config", "SupertonicConfig.xml");
            if (!File.Exists(configPath))
            {
                Log.Debug(() => $"ServerManager: Config/SupertonicConfig.xml not found at {configPath}");
                yield break;
            }

            Log.Debug(() => $"ServerManager: Found supertonic-server config at {configPath}");

            try
            {
                _supertonicServerProcess = NoInheritProcess.Start(serverExe, "", serverDir,
                    Path.Combine(serverDir, "supertonic-server-stdout.log"));
                if (_supertonicServerProcess == null) throw new Exception("NoInheritProcess returned null");
                Log.Debug(() => $"[SupertonicBootCommand] {serverExe} (cwd: {serverDir})");
            }
            catch (Exception ex)
            {
                Log.Warning($"ServerManager: Failed to start supertonic-server: {ex.Message}");
                yield break;
            }

            // Wait for server to be ready
            Log.Debug(() => "ServerManager: Waiting for supertonic-server to initialize...");
            yield return new WaitForSeconds(5f);

            bool ready = false;
            for (int poll = 0; poll < 30 && !ready; poll++)
            {
                if (_supertonicServerProcess != null && _supertonicServerProcess.HasExited)
                {
                    int exitCode = _supertonicServerProcess.ExitCode;
                    Log.Warning($"ServerManager: supertonic-server process died (exit code: {exitCode})");
                    string capturedLog = NoInheritProcess.ReadCapturedLog(Path.Combine(serverDir, "supertonic-server-stdout.log"));
                    if (!string.IsNullOrEmpty(capturedLog))
                        Log.Warning($"ServerManager: supertonic-server captured output:\n{capturedLog}");
                    yield break;
                }

                yield return CheckTcpPortNonBlocking("127.0.0.1", 5054, 1f);
                ready = _lastPortCheckResult;
                if (ready)
                {
                    Log.Debug(() => "ServerManager: supertonic-server is accepting connections on port 5054!");
                    break;
                }
                if (poll < 29)
                {
                    yield return new WaitForSeconds(1f);
                }
            }

            if (!ready)
            {
                Log.Debug(() => $"ServerManager: supertonic-server did not become ready (attempt {attempt})");
            }

            _ttSupertonicReady = ready;
        }



        /// <summary>
        /// Find the Whisper GGML model file in bin/WhisperServer/.
        /// If modelName is specified, look for that exact file first.
        /// Otherwise autodetect: prefer ggml-small.bin, then any ggml-*.bin.
        /// </summary>
        private static string FindWhisperModel(string whisperServerDir, string modelName)
        {
            if (!Directory.Exists(whisperServerDir))
                return null;

            // If a specific model is configured, try it first
            if (!string.IsNullOrEmpty(modelName))
            {
                string explicitPath = Path.Combine(whisperServerDir, modelName);
                if (File.Exists(explicitPath))
                    return explicitPath;

                Log.Warning($"ServerManager: Configured whisper model \"{modelName}\" not found at {explicitPath}, falling back to autodetect");
            }

            // Autodetect: prefer ggml-small.bin (accurate default), then any ggml-*.bin
            string preferred = Path.Combine(whisperServerDir, "ggml-small.bin");
            if (File.Exists(preferred))
                return preferred;

            var binFiles = Directory.GetFiles(whisperServerDir, "ggml-*.bin");
            if (binFiles.Length > 0)
                return binFiles[0];

            return null;
        }

        /// <summary>
        /// Find a .gguf model file in Resources/Models/Llama/.
        /// If modelName is specified, look for that exact file first.
        /// Otherwise autodetect: return the first .gguf file found.
        /// </summary>
        public static string FindGgufModel(string resourcesDir, string modelName = null)
        {
            // Prefer Models/Llama/ subdirectory
            string llamaDir = Path.Combine(resourcesDir, "Models", "Llama");

            if (Directory.Exists(llamaDir))
            {
                // Primary: try the exact configured filename first
                if (!string.IsNullOrEmpty(modelName))
                {
                    string explicitPath = Path.Combine(llamaDir, modelName);
                    if (File.Exists(explicitPath))
                        return explicitPath;

                    Log.Warning($"ServerManager: Configured LLM model \"{modelName}\" not found at {explicitPath}, falling back to autodetect");
                }

                // Fallback: return first .gguf found (legacy behavior)
                var ggufFiles = Directory.GetFiles(llamaDir, "*.gguf");
                if (ggufFiles.Length > 0)
                    return ggufFiles[0];
            }

            // Fallback: scan Resources/ directly
            if (Directory.Exists(resourcesDir))
            {
                var ggufFiles = Directory.GetFiles(resourcesDir, "*.gguf");
                if (ggufFiles.Length > 0)
                    return ggufFiles[0];
            }

            return null;
        }

        private static IEnumerator StartEmbedServerRoutine(string modPath)
        {
            yield return CheckTcpPortNonBlocking("127.0.0.1", LLMService.DefaultEmbedPort, 1f);
            if (_lastPortCheckResult)
            {
                Log.Debug(() => $"ServerManager: embed-server already listening on port {LLMService.DefaultEmbedPort}, reusing existing instance");
                _embedServerReady = true;
                yield break;
            }

            const int maxRetries = 2;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (attempt > 1)
                {
                    Log.Debug(() => $"ServerManager: embed-server retry {attempt}/{maxRetries}...");
                    KillProcessOnPort(LLMService.DefaultEmbedPort, "embed-server");
                    yield return new WaitForSeconds(2f);
                }

                _embedServerReady = false;
                yield return StartEmbedServerOnce(modPath, attempt);

                if (_embedServerReady)
                    yield break;

                if (_embedServerProcess != null && !_embedServerProcess.HasExited)
                {
                    try { _embedServerProcess.Kill(); } catch { /* process already exited or access denied */ }
                    _embedServerProcess = null;
                }

                if (attempt < maxRetries)
                {
                    Log.Debug(() => $"ServerManager: embed-server attempt {attempt} failed, retrying...");
                    yield return new WaitForSeconds(3f);
                }
            }

            Log.Warning("ServerManager: embed-server failed to start — embeddings will be unavailable");
            Log.Warning("ServerManager: Ensure Resources/Models/Embed/ contains nomic-embed-text-v1.5.Q8_0.gguf");
        }

        private static IEnumerator StartEmbedServerOnce(string modPath, int attempt)
        {
            // Reuse the same llama-server.exe from bin/LlamaServer/
            string serverDir = Path.Combine(modPath, "bin", "LlamaServer");
            if (!Directory.Exists(serverDir))
            {
                Log.Debug(() => $"ServerManager: bin/LlamaServer/ not found at {serverDir}");
                yield break;
            }

            string serverExe = Path.Combine(serverDir, "llama-server.exe");
            if (!File.Exists(serverExe))
            {
                Log.Debug(() => $"ServerManager: llama-server.exe not found at {serverExe}");
                yield break;
            }

            // Find embedding model in Resources/Models/Embed/
            string embedDir = Path.Combine(modPath, "Resources", "Models", "Embed");
            string modelPath = FindEmbedModel(embedDir);
            if (modelPath == null)
            {
                Log.Debug(() => $"ServerManager: No .gguf model found in {embedDir} — skipping embed server");
                yield break;
            }

            Log.Debug(() => $"ServerManager: Found embed model at {modelPath}");

            var args = new System.Text.StringBuilder();
            args.Append($"-m \"{modelPath}\"");
            args.Append($" --port {LLMService.DefaultEmbedPort}");
            args.Append(" --host 127.0.0.1");
            args.Append(" --no-ui");
            args.Append(" --ctx-size 512");   // nomic max input is 8192 tokens but 512 covers any single fact
            args.Append(" --gpu-layers -1"); // GPU offload all layers (nomic is tiny ~140MB)
            args.Append(" --embedding --pooling cls");
            args.Append(" -lv 4");

            Log.Debug(() => $"ServerManager: Starting embed-server (attempt {attempt})...");
            Log.Debug(() => $"ServerManager: Args: {args}");

            try
            {
                _embedServerProcess = NoInheritProcess.Start(serverExe, args.ToString(), serverDir,
                    Path.Combine(serverDir, "embed-server-stdout.log"));
                if (_embedServerProcess == null) throw new Exception("NoInheritProcess returned null");
                Log.Debug(() => $"ServerManager: embed-server started (PID: {_embedServerProcess?.Id})");
            }
            catch (Exception ex)
            {
                Log.Warning($"ServerManager: Failed to start embed-server: {ex.Message}");
                yield break;
            }

            // nomic is small (~137M params) — loads much faster than the chat model
            Log.Debug(() => "ServerManager: Waiting for embed-server to initialize...");
            yield return new WaitForSeconds(3f);

            bool ready = false;
            for (int poll = 0; poll < 30 && !ready; poll++)
            {
                if (_embedServerProcess != null && _embedServerProcess.HasExited)
                {
                    Log.Warning($"ServerManager: embed-server process died (exit code: {_embedServerProcess.ExitCode})");
                    string capturedLog = NoInheritProcess.ReadCapturedLog(Path.Combine(serverDir, "embed-server-stdout.log"));
                    if (!string.IsNullOrEmpty(capturedLog))
                        Log.Warning($"ServerManager: embed-server captured output:\n{capturedLog}");
                    yield break;
                }

                yield return CheckTcpPortNonBlocking("127.0.0.1", LLMService.DefaultEmbedPort, 1f);
                ready = _lastPortCheckResult;
                if (ready)
                {
                    Log.Debug(() => $"ServerManager: embed-server is accepting connections on port {LLMService.DefaultEmbedPort}!");
                    break;
                }
                if (poll < 29)
                    yield return new WaitForSeconds(1f);
            }

            if (!ready)
                Log.Debug(() => $"ServerManager: embed-server did not become ready (attempt {attempt})");

            _embedServerReady = ready;
        }

        /// <summary>
        /// Find a .gguf embedding model in Resources/Models/Embed/.
        /// Returns the first .gguf found — no autodetect ambiguity since this dir is embed-only.
        /// </summary>
        private static string FindEmbedModel(string embedDir)
        {
            if (!Directory.Exists(embedDir))
                return null;

            var files = Directory.GetFiles(embedDir, "*.gguf");
            return files.Length > 0 ? files[0] : null;
        }

        private static IEnumerator StartWhisperServerRoutine(string modPath)
        {
            // Check if something is already listening on port 5052 (e.g. stale process survived the kill step,
            // or an old mod DLL also started one). If so, reuse it instead of starting a duplicate.
            yield return CheckTcpPortNonBlocking("127.0.0.1", 5052, 1f);
            if (_lastPortCheckResult)
            {
                Log.Debug(() => "ServerManager: whisper-server already listening on port 5052, reusing existing instance");
                _sttServerReady = true;
                // Don't set _whisperServerProcess — we don't own this process.
                // StopServers() will fall back to KillProcessOnPort(5052) if handle is null.
                yield break;
            }

            const int maxRetries = 2;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (attempt > 1)
                {
                    Log.Debug(() => $"ServerManager: whisper-server retry {attempt}/{maxRetries}...");
                    KillProcessOnPort(5052, "whisper-server");
                    yield return new WaitForSeconds(2f);
                }

                _sttServerReady = false;
                yield return StartWhisperServerOnce(modPath, attempt);

                if (_sttServerReady)
                    yield break;

                // If we have a process but it didn't become ready, kill it before retry
                if (_whisperServerProcess != null && !_whisperServerProcess.HasExited)
                {
                    try { _whisperServerProcess.Kill(); } catch { /* process already exited or access denied */ }
                    _whisperServerProcess = null;
                }

                if (attempt < maxRetries)
                {
                    Log.Debug(() => $"ServerManager: whisper-server attempt {attempt} failed, retrying...");
                    yield return new WaitForSeconds(3f);
                }
            }

            Log.Warning("ServerManager: whisper-server failed to start after all retries");
            Log.Warning("ServerManager: Ensure bin/WhisperServer/ and Resources/*.bin are present");
        }

        private static IEnumerator StartWhisperServerOnce(string modPath, int attempt)
        {
            // Find whisper-server.exe
            string serverDir = Path.Combine(modPath, "bin", "WhisperServer");
            if (!Directory.Exists(serverDir))
            {
                Log.Debug(() => $"ServerManager: bin/WhisperServer/ not found at {serverDir}");
                yield break;
            }

            string serverExe = Path.Combine(serverDir, "whisper-server.exe");
            if (!File.Exists(serverExe))
            {
                Log.Debug(() => $"ServerManager: whisper-server.exe not found at {serverExe}");
                yield break;
            }

            // Find Whisper GGML model in bin/WhisperServer/ (lives alongside the server exe)
            string modelPath = FindWhisperModel(serverDir, XNPCVoiceControlMod.STTConfig?.Model ?? "");
            if (modelPath == null)
            {
                Log.Debug(() => $"ServerManager: No whisper .bin model found in {serverDir}");
                yield break;
            }

            Log.Debug(() => $"ServerManager: Found whisper model at {modelPath}");

            // Build arguments from STTConfig.
            // On retry (attempt > 1) or if GPU previously crashed, force CPU fallback.
            bool cpuFallback = attempt > 1 || _whisperGpuFailed;
            var args = BuildWhisperServerArgs(modelPath, cpuFallback);

            Log.Debug(() => $"ServerManager: Starting whisper-server.exe (attempt {attempt})...");
            Log.Debug(() => $"ServerManager: Args: {args}");

            try
            {
                _whisperServerProcess = NoInheritProcess.Start(serverExe, args.ToString(), serverDir,
                    Path.Combine(serverDir, "whisper-server-stdout.log"));
                if (_whisperServerProcess == null) throw new Exception("NoInheritProcess returned null");
                Log.Debug(() => $"ServerManager: whisper-server started (PID: {_whisperServerProcess?.Id})");
            }
            catch (Exception ex)
            {
                Log.Warning($"ServerManager: Failed to start whisper-server: {ex.Message}");
                yield break;
            }

            // Wait for server to be ready
            Log.Debug(() => "ServerManager: Waiting for whisper-server to initialize...");
            yield return new WaitForSeconds(5f);

            // Non-blocking TCP readiness check.
            bool ready = false;
            for (int poll = 0; poll < 30 && !ready; poll++)
            {
                // Check if the process is still alive — if it crashed, stop polling immediately
                if (_whisperServerProcess != null && _whisperServerProcess.HasExited)
                {
                    int exitCode = _whisperServerProcess.ExitCode;
                    Log.Warning($"ServerManager: whisper-server process died (exit code: {exitCode})");
                    string capturedLog = NoInheritProcess.ReadCapturedLog(Path.Combine(serverDir, "whisper-server-stdout.log"));
                    if (!string.IsNullOrEmpty(capturedLog))
                        Log.Warning($"ServerManager: whisper-server captured output:\n{capturedLog}");

                    // If this was a GPU attempt, mark Vulkan as failed so retry falls back to CPU
                    if (!cpuFallback)
                    {
                        _whisperGpuFailed = true;
                        Log.Out($"ServerManager: Vulkan GPU crash detected (exit code {exitCode}). All future attempts will use CPU.");
                    }
                    yield break;
                }

                yield return CheckTcpPortNonBlocking("127.0.0.1", 5052, 1f);
                ready = _lastPortCheckResult;
                if (ready)
                {
                    Log.Debug(() => "ServerManager: whisper-server is accepting connections on port 5052!");
                    break;
                }
                if (poll < 29)
                {
                    yield return new WaitForSeconds(1f);
                }
            }

            if (!ready)
            {
                Log.Debug(() => $"ServerManager: whisper-server did not become ready (attempt {attempt})");
            }

            _sttServerReady = ready;

            // GPU warmup: send a tiny silent WAV to pre-compile OpenCL kernels.
            // Without this, the first real transcription takes ~3.4s (cold start).
            // Fire-and-forget background task — no longer blocks the coroutine pipeline.
            if (ready)
                StartWarmupAsync(CancellationToken.None);
        }

        public static void StopServers()
        {
            // Guard against duplicate calls from triple shutdown hooks
            if (_serversStopped) return;
            _serversStopped = true;
            _serversStarted = false;   // allow StartServers to respawn sidecars on the next world load

            Log.Out("ServerManager: Stopping servers...");

            if (_llamaServerProcess != null)
            {
                try
                {
                    if (!_llamaServerProcess.HasExited)
                    {
                        _llamaServerProcess.Kill();
                        if (!_llamaServerProcess.WaitForExit(3000))
                        {
                            Log.Warning("ServerManager: llama-server didn't exit in 3s, forcing KillProcessOnPort");
                            KillProcessOnPort(LLMService.DefaultChatPort, "llama-server");
                        }
                        else
                        {
                            Log.Debug(() => "ServerManager: llama-server killed");
                        }
                    }
                    else
                    {
                        Log.Debug(() => "ServerManager: llama-server already exited");
                    }
                    _llamaServerProcess.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning($"ServerManager: error killing llama-server: {ex.Message}, falling back to KillProcessOnPort");
                    KillProcessOnPort(LLMService.DefaultChatPort, "llama-server");
                }
                finally { _llamaServerProcess = null; }
            }
            if (_sherpaServerProcess != null)
            {
                try
                {
                    if (!_sherpaServerProcess.HasExited)
                    {
                        _sherpaServerProcess.Kill();
                        if (!_sherpaServerProcess.WaitForExit(3000))
                        {
                            Log.Warning("ServerManager: sherpa-server didn't exit in 3s, forcing KillProcessOnPort");
                            KillProcessOnPort(5053, "sherpa-server");
                        }
                        else
                        {
                            Log.Debug(() => "ServerManager: sherpa-server killed");
                        }
                    }
                    else
                    {
                        Log.Debug(() => "ServerManager: sherpa-server already exited");
                    }
                    _sherpaServerProcess.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning($"ServerManager: error killing sherpa-server: {ex.Message}, falling back to KillProcessOnPort");
                    KillProcessOnPort(5053, "sherpa-server");
                }
                finally { _sherpaServerProcess = null; }
            }
            if (_supertonicServerProcess != null)
            {
                try
                {
                    if (!_supertonicServerProcess.HasExited)
                    {
                        _supertonicServerProcess.Kill();
                        if (!_supertonicServerProcess.WaitForExit(3000))
                        {
                            Log.Warning("ServerManager: supertonic-server didn't exit in 3s, forcing KillProcessOnPort");
                            KillProcessOnPort(5054, "supertonic-server");
                        }
                        else
                        {
                            Log.Debug(() => "ServerManager: supertonic-server killed");
                        }
                    }
                    else
                    {
                        Log.Debug(() => "ServerManager: supertonic-server already exited");
                    }
                    _supertonicServerProcess.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning($"ServerManager: error killing supertonic-server: {ex.Message}, falling back to KillProcessOnPort");
                    KillProcessOnPort(5054, "supertonic-server");
                }
                finally { _supertonicServerProcess = null; }
            }
            if (_whisperServerProcess != null)
            {
                try
                {
                    if (!_whisperServerProcess.HasExited)
                    {
                        _whisperServerProcess.Kill();
                        if (!_whisperServerProcess.WaitForExit(3000))
                        {
                            Log.Warning("ServerManager: whisper-server didn't exit in 3s, forcing KillProcessOnPort");
                            KillProcessOnPort(5052, "whisper-server");
                        }
                        else
                        {
                            Log.Debug(() => "ServerManager: whisper-server killed");
                        }
                    }
                    else
                    {
                        Log.Debug(() => "ServerManager: whisper-server already exited");
                    }
                    _whisperServerProcess.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning($"ServerManager: error killing whisper-server: {ex.Message}, falling back to KillProcessOnPort");
                    KillProcessOnPort(5052, "whisper-server");
                }
                finally { _whisperServerProcess = null; }
            }
            if (_embedServerProcess != null)
            {
                try
                {
                    if (!_embedServerProcess.HasExited)
                    {
                        _embedServerProcess.Kill();
                        if (!_embedServerProcess.WaitForExit(3000))
                        {
                            Log.Warning("ServerManager: embed-server didn't exit in 3s, forcing KillProcessOnPort");
                            KillProcessOnPort(LLMService.DefaultEmbedPort, "embed-server");
                        }
                        else
                        {
                            Log.Debug(() => "ServerManager: embed-server killed");
                        }
                    }
                    else
                    {
                        Log.Debug(() => "ServerManager: embed-server already exited");
                    }
                    _embedServerProcess.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning($"ServerManager: error killing embed-server: {ex.Message}, falling back to KillProcessOnPort");
                    KillProcessOnPort(LLMService.DefaultEmbedPort, "embed-server");
                }
                finally { _embedServerProcess = null; }
            }
            // Note: Windows Job Object (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) guarantees
            // all server processes are killed by the OS kernel when Unity exits, even on crash.
            _serversStarted = false;
            _serverReady = false;
            _ttServerReady = false;
            _ttSupertonicReady = false;
            _sttServerReady = false;
            _embedServerReady = false;
            _whisperGpuFailed = false;
            Log.Out("ServerManager: Servers stopped.");
        }

        /// <summary>
        /// Coroutine: kill existing whisper-server, wait for port free, start new one with current config.
        /// Runs on ServerManagerHost (DontDestroyOnLoad) — survives UI close.
        /// </summary>
        public static IEnumerator RestartWhisperServerRoutine()
        {
            Log.Out("ServerManager: Restarting whisper-server with updated config...");

            // 1. Kill existing process
            if (_whisperServerProcess != null)
            {
                try
                {
                    if (!_whisperServerProcess.HasExited)
                        _whisperServerProcess.Kill();
                    _whisperServerProcess.Dispose();
                    Log.Out("ServerManager: whisper-server killed (restart)");
                }
                catch { /* process already exited or access denied */ }
                finally { _whisperServerProcess = null; }
            }
            _sttServerReady = false;

            // 2. Wait for port 5052 to be freed
            yield return new WaitForSeconds(1f);
            bool portFree = false;
            for (int poll = 0; poll < 30 && !portFree; poll++)
            {
                yield return CheckTcpPortNonBlocking("127.0.0.1", 5052, 1f);
                // Port is free when the connection check FAILS (server no longer listening)
                if (!_lastPortCheckResult)
                {
                    portFree = true;
                    break;
                }
                yield return new WaitForSeconds(1f);
            }
            if (!portFree)
            {
                Log.Warning("ServerManager: Port 5052 still in use after 30s, forcing restart anyway");
            }

            // 3. Start new server with current config
            string modPath = GetModPath();
            if (!string.IsNullOrEmpty(modPath))
            {
                yield return StartWhisperServerOnce(modPath, 1);
            }

            Log.Out($"ServerManager: whisper-server restart complete (ready: {_sttServerReady})");
        }

        /// <summary>
        /// Restart whisper-server if it has crashed.
        /// Called by STTService when transcription fails with ConnectFailure.
        /// </summary>
        public static void RestartWhisperServer()
        {
            try
            {
                // Kill existing process if still around
                if (_whisperServerProcess != null)
                {
                    try { if (!_whisperServerProcess.HasExited) _whisperServerProcess.Kill(); } catch { /* process already exited or access denied */ }
                    try { _whisperServerProcess.Dispose(); } catch { /* ObjectDisposedException if already disposed */ }
                    _whisperServerProcess = null;
                }

                string modPath = GetModPath();
                if (string.IsNullOrEmpty(modPath)) return;

                string serverDir = Path.Combine(modPath, "bin", "WhisperServer");
                string serverExe = Path.Combine(serverDir, "whisper-server.exe");
                if (!File.Exists(serverExe)) return;

                string modelPath = FindWhisperModel(serverDir, XNPCVoiceControlMod.STTConfig?.Model ?? "");
                if (modelPath == null) return;

                // Runtime restart: if GPU previously crashed, force CPU. Otherwise try GPU as configured.
                var args = BuildWhisperServerArgs(modelPath, cpuFallback: _whisperGpuFailed);

                _whisperServerProcess = NoInheritProcess.Start(serverExe, args.ToString(), serverDir,
                    Path.Combine(serverDir, "whisper-server-stdout.log"));
                Log.Out($"ServerManager: whisper-server restarted (PID: {_whisperServerProcess?.Id})");
            }
            catch (Exception ex)
            {
                Log.Error($"ServerManager: Failed to restart whisper-server: {ex.Message}");
            }
        }

        private static void KillProcessTree(int pid)
        {
            try
            {
                var killProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c taskkill /F /T /PID {pid}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                killProcess.Start();
            }
            catch (Exception ex)
            {
                Log.Warning($"ServerManager: taskkill failed for PID {pid}: {ex.Message}");
            }
        }

        private static void KillProcessOnPort(int port, string serverName)
        {
            try
            {
                var netstatProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "netstat",
                        Arguments = "-ano",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                netstatProcess.Start();
                string output = netstatProcess.StandardOutput.ReadToEnd();
                netstatProcess.WaitForExit();

                string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    if (line.Contains($":{port}") && line.Contains("LISTENING"))
                    {
                        string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            string pidStr = parts[parts.Length - 1];
                            if (int.TryParse(pidStr, out int pid) && pid > 0)
                            {
                                try
                                {
                                    using (Process existingProcess = Process.GetProcessById(pid))
                                    {
                                        Log.Debug(() => $"ServerManager: Killing existing {serverName} server (PID: {pid})");
                                        existingProcess.Kill();
                                        existingProcess.WaitForExit(2000);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.Warning($"ServerManager: Could not kill process {pid}: {ex.Message}");
                                }
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"ServerManager: Error checking port {port}: {ex.Message}");
            }
        }

        /// <summary>
        /// Non-blocking TCP port readiness check.
        /// Replaces the old BeginConnect + WaitOne pattern that blocked the Unity main thread for up to 1s per poll.
        ///
        /// Strategy:
        /// 1. Try UnityWebRequest GET to the port (yields per-frame, never blocks main thread).
        ///    - Success or ProtocolError (404, etc.) → port is open, server is ready.
        ///    - NetworkError → ambiguous (could be "connection refused" or "server doesn't speak HTTP").
        /// 2. If ambiguous, fall back to a raw TCP socket check on a background thread.
        ///    The background thread blocks, but the main thread yields frames via the coroutine.
        /// </summary>
        private static IEnumerator CheckTcpPortNonBlocking(string host, int port, float timeoutSeconds)
        {
            string url = $"http://{host}:{port}/";

            using (UnityWebRequest request = new UnityWebRequest(url, "GET"))
            {
                request.timeout = Mathf.Max(1, (int)timeoutSeconds);
                request.downloadHandler = new DownloadHandlerBuffer();
                // GET request — no upload handler needed (UploadHandlerEmpty unavailable in Unity 2019)

                // SendWebRequest yields per-frame — never blocks the main thread
                yield return request.SendWebRequest();

                // Success or ProtocolError (404, 405, etc.) means the TCP port is open and responding
                if (request.result == UnityWebRequest.Result.Success ||
                    request.result == UnityWebRequest.Result.ProtocolError)
                {
                    _lastPortCheckResult = true;
                    yield break;
                }

                // NetworkError is ambiguous: could be "connection refused" (port closed)
                // or "connection reset" (server accepts TCP but doesn't speak HTTP).
                // Fall back to raw TCP socket check on a background thread.
                yield return CheckTcpSocketOnBackgroundThread(host, port, timeoutSeconds);
            }
        }

        private static bool _lastPortCheckResult = false;

        /// <summary>
        /// Raw TCP socket check executed on a background thread.
        /// The main thread yields frames while waiting — never blocked.
        /// </summary>
        private static IEnumerator CheckTcpSocketOnBackgroundThread(string host, int port, float timeoutSeconds)
        {
            bool done = false;
            bool connected = false;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (var socket = new System.Net.Sockets.Socket(
                        System.Net.Sockets.AddressFamily.InterNetwork,
                        System.Net.Sockets.SocketType.Stream,
                        System.Net.Sockets.ProtocolType.Tcp))
                    {
                        var ar = socket.BeginConnect(host, port, null, null);
                        // This WaitOne blocks the BACKGROUND thread, not the Unity main thread
                        if (ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(timeoutSeconds)))
                        {
                            connected = socket.Connected;
                        }
                        try { socket.Shutdown(System.Net.Sockets.SocketShutdown.Both); } catch { /* socket already closed or never connected */ }
                    }
                }
                catch { connected = false; }
                finally
                {
                    done = true;
                }
            });

            // Main thread yields frames while waiting for background thread — never blocked
            while (!done)
            {
                yield return null; // yield one frame
            }

            _lastPortCheckResult = connected;
        }

        /// <summary>
        /// Build whisper-server command-line arguments from STTConfig.
        /// </summary>
        private static System.Text.StringBuilder BuildWhisperServerArgs(string modelPath, bool cpuFallback = false)
        {
            var args = new System.Text.StringBuilder();
            args.Append($"--model \"{modelPath}\"");
            args.Append(" --port 5052");
            args.Append(" --host 127.0.0.1");

            var sttConfig = XNPCVoiceControlMod.STTConfig;
            args.Append($" --beam-size {sttConfig?.BeamSize ?? 2}");

            // Language: pass explicit ISO 639-1 code (--language en, ja, zh, etc.).
            // Default is "en" — omit --language only when user explicitly sets "auto".

            // Manual GPU device override (escape hatch for iGPU-first systems).
            // whisper-server flag is --device N. Empty/unset = don't pass anything (default device 0).
            string gpuDevice = sttConfig?.WhisperGpuDevice ?? "";
            if (!string.IsNullOrEmpty(gpuDevice))
            {
                if (int.TryParse(gpuDevice, out int devIdx) && devIdx >= 0)
                    args.Append($" --device {devIdx}");
                else
                    Log.Warning($"ServerManager: Invalid WhisperGpuDevice \"{gpuDevice}\", must be a non-negative integer");
            }

            string lang = sttConfig?.Language?.ToLower() ?? "en";
            if (lang == "auto" && sttConfig?.LanguageLocked == true)
                lang = "en"; // Legacy LanguageLocked=true fallback
            if (lang != "auto")
                args.Append($" --language {lang}");

            // GPU acceleration: let whisper auto-detect Vulkan when UseGpu is enabled.
            // Explicit --device/--split-mode flags caused crashes on RTX 50-series (Blackwell).
            // GGML_VK_DISABLE_COOPMAT2=1 env var is set at process launch to handle Blackwell safely.
            // cpuFallback=true means GPU crashed — omit all GPU args so whisper falls back to CPU.
            if (sttConfig?.UseGpu == true && cpuFallback)
            {
                Log.Out("ServerManager: Falling back to CPU for whisper-server (GPU/Vulkan not available on first attempt)");
            }

            // Read prompt from config, trim and gracefully truncate to 800 chars (whisper context limit)
            string prompt = sttConfig?.Prompt ?? "";
            if (!string.IsNullOrEmpty(prompt))
            {
                prompt = prompt.Trim();
                if (prompt.Length > 800)
                {
                    int lastSpace = prompt.LastIndexOf(' ', 800);
                    if (lastSpace > 0)
                        prompt = prompt.Substring(0, lastSpace).TrimEnd();
                    else
                        prompt = prompt.Substring(0, 800);
                }
                args.Append($" --prompt \"{prompt}\"");
            }
            args.Append(" --entropy-thold 2.4");
            args.Append(" --logprob-thold -1.0");

            Log.Out($"ServerManager: whisper-server args — {args}");
            return args;
        }

        /// <summary>
        /// Background warmup task: polls health endpoints of all servers, then sends a silent WAV
        /// to whisper-server to pre-compile GPU kernels. Runs entirely on a background thread.
        /// State changes are marshaled to main thread via MainThreadDispatcher.
        /// </summary>
        public static void StartWarmupAsync(CancellationToken token)
        {
            Task.Run(async () =>
            {
                try
                {
                    // Phase 1: Poll health endpoints until all three servers respond with 200 OK
                    // (They may still be loading models — give them up to 60 seconds total)
                    const int maxPollMs = 60000;
                    var pollStart = DateTimeOffset.UtcNow;

                    while (!token.IsCancellationRequested &&
                           (DateTimeOffset.UtcNow - pollStart).TotalMilliseconds < maxPollMs)
                    {
                        bool allReady = true;

                        // Check LLM server (port 5055)
                        if (!PingHealthEndpoint($"http://127.0.0.1:{LLMService.DefaultChatPort}/health", token))
                            allReady = false;

                        // Check TTS server — port depends on which engine is selected.
                        string ttsEngine = XNPCVoiceControlMod.TTSConfig?.TtsEngine ?? "sherpa";
                        int ttsPort = ttsEngine == "supertonic" ? 5054 : 5053;
                        if (!PingHealthEndpoint($"http://127.0.0.1:{ttsPort}/health", token))
                            allReady = false;

                        // Check STT server (port 5052)
                        if (!PingHealthEndpoint("http://127.0.0.1:5052/health", token))
                            allReady = false;

                        if (allReady)
                            break;

                        await Task.Delay(500, token).ConfigureAwait(false);
                    }

                    // Phase 2: GPU warmup — send a silent WAV to whisper-server
                    Log.Out("ServerManager: Warming up whisper-server (pre-compiling GPU kernels)...");

                    byte[] silentWav = CreateSilentWav(32000, 16000);
                    if (silentWav == null)
                    {
                        Log.Warning("ServerManager: Failed to create warmup WAV");
                        MainThreadDispatcher.Enqueue(() => ReadyState = ServerReadyState.Failed);
                        return;
                    }

                    string resultMsg = PerformGpuWarmup(silentWav);
                    Log.Out($"ServerManager: whisper-server {resultMsg}");

                    // All good — mark as ready on main thread
                    MainThreadDispatcher.Enqueue(() => ReadyState = ServerReadyState.Ready);
                }
                catch (OperationCanceledException)
                {
                    Log.Debug(() => "ServerManager: Warmup cancelled.");
                }
                catch (Exception ex)
                {
                    Log.Warning($"ServerManager: Warmup failed: {ex.Message}");
                    MainThreadDispatcher.Enqueue(() => ReadyState = ServerReadyState.Failed);
                }
            }, token);
        }

        /// <summary>
        /// Ping a single health endpoint. Returns true if 200 OK received.
        /// Runs on background thread.
        /// </summary>
        private static bool PingHealthEndpoint(string url, CancellationToken token)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 3000;
                request.ReadWriteTimeout = 3000;
                request.KeepAlive = true;
                request.Proxy = null;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false; // Server not ready yet — expected during startup
            }
        }

        /// <summary>
        /// Send a silent WAV to whisper-server to force GPU kernel compilation.
        /// Runs on background thread. Returns a human-readable result string.
        /// </summary>
        private static string PerformGpuWarmup(byte[] silentWav)
        {
            try
            {
                // Build multipart/form-data body (native C++ server requires this)
                string boundary = "----WebKitFormBoundary7MA4YWxkTrZu0gW";
                var bodyBuilder = new System.IO.MemoryStream();
                using (var writer = new System.IO.BinaryWriter(bodyBuilder, System.Text.Encoding.UTF8))
                {
                    string header = $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\n";
                    writer.Write(System.Text.Encoding.UTF8.GetBytes(header));
                    writer.Write(silentWav);
                    writer.Write(System.Text.Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n"));
                }
                byte[] bodyBytes = bodyBuilder.ToArray();

                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:5052/inference");
                request.KeepAlive = true;
                request.Proxy = null;
                request.Method = "POST";
                request.ContentType = $"multipart/form-data; boundary={boundary}";
                request.Timeout = 30000; // GPU warmup can take a few seconds
                request.ReadWriteTimeout = 30000;
                request.ContentLength = bodyBytes.Length;

                using (var stream = request.GetRequestStream())
                    stream.Write(bodyBytes, 0, bodyBytes.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return $"warmup OK ({response.StatusCode})";
                }
            }
            catch (Exception ex)
            {
                return $"warmup failed: {ex.Message}";
            }
        }


        /// <summary>
        /// Create a minimal valid WAV file filled with silence (zero samples).
        /// </summary>
        private static byte[] CreateSilentWav(int sampleCount, int sampleRate)
        {
            using (var stream = new MemoryStream())
            using (var writer = new System.IO.BinaryWriter(stream))
            {
                int channels = 1;
                int bitsPerSample = 16;
                int byteRate = sampleRate * channels * bitsPerSample / 8;
                int blockAlign = channels * bitsPerSample / 8;
                int dataSize = sampleCount * bitsPerSample / 8;

                // RIFF header
                writer.Write(new char[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataSize);
                writer.Write(new char[] { 'W', 'A', 'V', 'E' });

                // fmt chunk
                writer.Write(new char[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);  // PCM
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(byteRate);
                writer.Write((short)blockAlign);
                writer.Write((short)bitsPerSample);

                // data chunk (all zeros = silence)
                writer.Write(new char[] { 'd', 'a', 't', 'a' });
                writer.Write(dataSize);
                writer.Write(new byte[dataSize]);

                return stream.ToArray();
            }
        }

        private static string GetModPath()
        {
            try
            {
                string assemblyLocation = typeof(ServerManager).Assembly.Location;
                if (!string.IsNullOrEmpty(assemblyLocation))
                {
                    return Path.GetDirectoryName(assemblyLocation);
                }
            }
            catch { /* assembly location not available */ }

            return null;
        }

        /// <summary>
        /// Log server stderr line, promoting GPU/CPU backend detection and real errors to visible.
        /// Suppresses llama-server slot/sampler/timing info noise and other benign chatter.
        /// </summary>
        private static void LogServerError(string prefix, string line)
        {
            if (line == null) return;

            string trimmed = line.Trim();

            // Suppress harmless curl timeout noise from llama-server's internal libcurl calls.
            // These are expected during startup and don't indicate a problem.
            if (trimmed.StartsWith("Curl error"))
                return;

            // Suppress llama-server slot/sampler/timing info noise (stderr spam on every request).
            // These are internal operational details: slot selection, sampler params, prompt eval timing.
            // Pattern: "I slot ..." or "I srv  ..." with operational sub-commands.
            if (IsLlamaServerNoise(trimmed))
                return;

            string lower = line.ToLowerInvariant();
            bool isBackendLine = IsBackendKeywordMatch(lower);
            bool isErrorLine = lower.Contains("[error]") || lower.Contains("exception") || lower.Contains("fatal");

            if (isErrorLine)
                Log.Error($"[{prefix}] {trimmed}");
            else if (isBackendLine)
                Log.Out($"[{prefix}] {trimmed}");
            else
                Log.Debug(() => $"[{prefix}] {trimmed}");
        }

        /// <summary>
        /// Detect llama-server stderr info lines that are operational noise, not useful diagnostics.
        /// These flood the log on every LLM request (RAG flush, chat completion, embedding).
        /// </summary>
        private static bool IsLlamaServerNoise(string line)
        {
            // Lines starting with "I slot" or "I srv  " are llama-server internal operational info.
            // We keep backend/GPU lines (caught by IsBackendKeywordMatch) but suppress the rest.
            if (line.StartsWith("I slot ") || line.StartsWith("I srv  "))
                return true;
            // System info / timing lines
            if (line.StartsWith("system_info:") || line.StartsWith("whisper_backend_init"))
                return true;
            // Prompt cache / memory management noise
            if (line.Contains("prompt cache") || line.Contains("memory_seq_rm") ||
                line.Contains("graphs reused") || line.Contains("cache state:"))
                return true;
            return false;
        }

        private static bool IsBackendKeywordMatch(string lower)
        {
            return lower.Contains("cuda") || lower.Contains("vulkan") ||
                lower.Contains("opencl") || lower.Contains("gpu") ||
                lower.Contains("cpu") || lower.Contains("executionprovider") ||
                lower.Contains("accelerate") || lower.Contains("mps") ||
                (lower.Contains("device") && lower.Contains(":"));
        }

        /// <summary>
        /// Polls all 5 sidecar processes every 10s for the rest of the session. If a process that
        /// was successfully started later dies (HasExited becomes true after its ready flag was
        /// set), logs it clearly and resets the ready flag - otherwise a mid-session crash is
        /// completely silent (ready flag stays true forever, only symptom is bare ConnectFailure
        /// on the next request with zero server-side diagnostic trail). Detection + logging +
        /// flag correction only - does NOT attempt to restart the sidecar.
        /// </summary>
        private static IEnumerator WorldUnloadWatchdog()
        {
            while (true)
            {
                yield return new WaitForSeconds(0.5f);

                bool worldLoaded = GameManager.Instance?.World != null;

                // Detect in-world → menu transition.
                if (_wasWorldLoaded && !worldLoaded)
                {
                    // Debounce: confirm World is still null on the next tick (guards against transient null during load).
                    yield return new WaitForSeconds(0.5f);
                    if (GameManager.Instance?.World == null)
                    {
                        Log.Out("ServerManager: world unloaded (exit to menu) — stopping sidecars to release ports");
                        ServerManager.StopServers();
                    }
                }
                else if (!_wasWorldLoaded && worldLoaded)
                {
                    // menu → in-world (Continue/reload): respawn sidecars
                    Log.Out("ServerManager: world loaded — (re)starting sidecars");
                    ServerManager.StartServers();   // idempotent: StartServersRoutine's _serversStarted guard prevents double-spawn
                }

                _wasWorldLoaded = worldLoaded;
            }
        }

        private static IEnumerator SidecarLivenessWatchdog()
        {
            while (true)
            {
                yield return new WaitForSeconds(10f);

                CheckSidecarLiveness("llama-server", _llamaServerProcess, () => _serverReady, v => _serverReady = v, "LlamaServer");
                CheckSidecarLiveness("sherpa-server", _sherpaServerProcess, () => _ttServerReady, v => _ttServerReady = v, "SherpaServer");
                CheckSidecarLiveness("supertonic-server", _supertonicServerProcess, () => _ttSupertonicReady, v => _ttSupertonicReady = v, "SupertonicServer");
                CheckSidecarLiveness("whisper-server", _whisperServerProcess, () => _sttServerReady, v => _sttServerReady = v, "WhisperServer");
                CheckSidecarLiveness("embed-server", _embedServerProcess, () => _embedServerReady, v => _embedServerReady = v, "LlamaServer");
            }
        }

        private static void CheckSidecarLiveness(string serverName, Process process, Func<bool> getReady, Action<bool> setReady, string binFolderName)
        {
            if (process == null || !getReady()) return;  // never started, or already known-dead - nothing new to detect
            if (!process.HasExited) return;               // still alive, nothing to do

            int exitCode;
            try { exitCode = process.ExitCode; } catch { exitCode = -1; }

            Log.Warning($"ServerManager: {serverName} died MID-SESSION (exit code: {exitCode}) - was running fine, now unavailable. Future requests will fail with ConnectFailure until this is corrected.");

            string logPath = Path.Combine(_modPath, "bin", binFolderName, $"{serverName}-stdout.log");
            string capturedLog = NoInheritProcess.ReadCapturedLog(logPath);
            if (!string.IsNullOrEmpty(capturedLog))
                Log.Warning($"ServerManager: {serverName} captured output at time of death:\n{capturedLog}");

            setReady(false);
        }

        // ========================================================================
        // NPC respawn diagnostics — read-only roster scan + removal logging
        // ========================================================================

        /// <summary>
        /// Scan world NPCs at T+5s and T+15s after player spawn, logging persistence-relevant fields.
        /// Read-only diagnostic — no behavior changes. Fires once per world load.
        /// </summary>
        public static void StartNPCDiagScan()
        {
            if (ServerManagerHost.Instance == null) return;
            ServerManagerHost.Instance.StartCoroutine(NPCDiagRosterScan());
        }

        private static IEnumerator NPCDiagRosterScan()
        {
            // Wait ~5s for NPCs to settle after spawn.
            yield return new WaitForSeconds(5f);
            ScanAndLogNPCRoster("T+5s");

            // Second scan at ~15s — some NPCs spawn late (async).
            yield return new WaitForSeconds(10f);
            ScanAndLogNPCRoster("T+15s");

            // Continuous server-side attach pass: every 5s, ensure ChatComponent exists on all chat-target entities.
            // Gate: ConnectionManager.Instance.IsServer (true in SP/listen-host too — harmless, components just attach
            // slightly earlier than lazy paths; same components either way). On dedi client this is skipped entirely.
            if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            {
                Log.Debug(() => "[VC-NET] Server-side attach pass started");
                while (true)
                {
                    yield return new WaitForSeconds(5f);
                    AttachChatComponentsToAll();
                }
            }
        }

        /// <summary>Iterate all world entities and ensure ChatComponent is attached where IsChatTarget returns true.
        /// Also refreshes _leaderEntityId on every component (new + existing) from SCore's hiring data,
        /// so patrol recording, billing reactor, and follow-assist see the correct leader after UI hire/dismiss/save-resume.</summary>
        private static void AttachChatComponentsToAll()
        {
            var world = GameManager.Instance?.World;
            if (world == null) return;

            int attached = 0;
            foreach (var entity in world.Entities.list)
            {
                if (!(entity is EntityAlive alive)) continue;
                if (!Core.ChatComponentManager.IsChatTarget(alive)) continue;
                if (Core.ChatComponentManager.IsAnimal(alive)) continue; // animals: phrase triggers only, no full component

                bool isNew = !Core.ChatComponentManager.TryGet(alive.entityId, out _);
                if (isNew)
                {
                    Core.ChatComponentManager.GetOrCreate(alive);
                    attached++;
                }

                // Refresh leader from SCore for every component — new and existing.
                // GetLeaderOrOwner reads through SphereCache.LeaderCache; a just-hired NPC may read
                // stale null on the first tick, but the next 5s tick self-heals. Acceptable.
                var leader = EntityUtilities.GetLeaderOrOwner(alive.entityId) as EntityPlayer;
                if (Core.ChatComponentManager.TryGet(alive.entityId, out var comp))
                {
                    if (leader != null && leader.entityId > 0)
                    {
                        comp.SetLeader(leader);

                        // Apply the boot default ONCE per player; after that the player toggles freely.
                        if (XNPCVoiceControlMod.DefaultTacticalMode && _tacticalDefaultApplied.Add(leader.entityId))
                            leader.Buffs.SetCustomVar("varTacticalMode", 1);
                    }
                    else
                        comp.ClearLeader();
                }
            }
            if (attached > 0)
                Log.Debug(() => $"[VC-NET] Attach pass: {attached} new component(s)");
        }

        private static void ScanAndLogNPCRoster(string timeLabel)
        {
            var world = GameManager.Instance?.World;
            if (world == null) return;

            int day = 0;
            try { day = (int)SkyManager.dayCount; } catch { }

            EntityPlayer player = null;
            try { player = world.GetPrimaryPlayer(); } catch { }

            int count = 0;
            foreach (var entity in world.Entities.list)
            {
                if (!(entity is EntityAlive alive)) continue;
                if (!Core.ChatComponentManager.IsChatTarget(alive)) continue;

                Log.Out(BuildNPCDiagLine(alive, player));
                count++;
            }

            Log.Out($"[NPC-DIAG] Roster @{timeLabel} (day {day}): {count} NPCs in world.");
        }

        /// <summary>Build a single [NPC-DIAG] line for an NPC entity.</summary>
        internal static string BuildNPCDiagLine(EntityAlive npc, EntityPlayer player)
        {
            string name = npc.EntityName;
            int id = npc.entityId;
            string cls = npc.EntityClass != null ? npc.EntityClass.entityClassName : "(null)";

            int leaderId = -1;
            try { leaderId = EntityUtilities.GetLeaderOrOwner(id)?.entityId ?? -1; } catch { }

            bool hasChatComp = false;
            try { hasChatComp = Core.ChatComponentManager.TryGet(id, out _); } catch { }

            EnumSpawnerSource spawnerSrc = EnumSpawnerSource.Unknown;
            try { spawnerSrc = npc.GetSpawnerSource(); } catch { }

            bool canDespawn = false;
            try { canDespawn = npc.canDespawn(); } catch { }

            bool savedToFile = false;
            try { savedToFile = npc.IsSavedToFile(); } catch { }

            bool leaderCvar = false;
            bool persistCvar = false;
            try
            {
                leaderCvar = npc.Buffs.HasCustomVar("Leader");
                persistCvar = npc.Buffs.HasCustomVar("Persist");
            } catch { }

            bool isDespawned = false;
            bool despawnWhenFar = false;
            bool chunkObserver = false;
            try
            {
                isDespawned = npc.IsDespawned;
                despawnWhenFar = npc.isDespawnWhenPlayerFar;
                chunkObserver = npc.bIsChunkObserver;
            } catch { }

            float dist = 0f;
            if (player != null)
                dist = Vector3.Distance(npc.position, player.position);

            return $"[NPC-DIAG] {name} id={id} class={cls} leader={leaderId} hasChatComp={hasChatComp} spawnerSrc={spawnerSrc} canDespawn={canDespawn} savedToFile={savedToFile} leaderCvar={leaderCvar} persistCvar={persistCvar} isDespawned={isDespawned} despawnWhenFar={despawnWhenFar} chunkObserver={chunkObserver} pos={npc.position.x:F0},{npc.position.z:F0} dist={dist:F0}";
        }
    }
}
