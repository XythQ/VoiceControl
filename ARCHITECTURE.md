# 1-XNPCVoiceControl — Architecture & Lessons Learned

## Table of Contents

- [Pipeline Architecture](#pipeline-architecture)
  - [Before: Callback Chain (Main-Thread Bound)](#before-callback-chain-main-thread-bound)
  - [After: Async Data Sandwich Pattern](#after-async-data-sandwich-pattern)
- [MainThreadDispatcher](#mainthreaddispatcher)
- [Component Overview](#component-overview)
- [RAG Memory System](#rag-memory-system)
- [Subtitle Window System](#subtitle-window-system)
- [Phrase Trigger Improvements](#phrase-trigger-improvements)
- [Lessons Learned](#lessons-learned)
- [Performance Notes](#performance-notes)

---

## Pipeline Architecture

### Before: Callback Chain (Main-Thread Bound)

The original voice pipeline executed as a series of callbacks that bounced between background threads and Unity's main thread at every step:

```
VAD stops → STTService.Transcribe() [Task.Run]
  ↓ returns transcript on bg thread
  ↓ marshals to main thread via callback
OnVoiceTranscribed() [main thread]
  ↓ calls LLMService.SendChatRequest() [Unity Coroutine — main thread!]
  ↓ coroutine yields frames while waiting for HTTP response (blocks main thread)
HandleLLMResponse() [main thread]
  ↓ calls TTSService.Synthesize() [Task.Run]
  ↓ returns WAV bytes on bg thread
  ↓ marshals to main thread via callback
PlayClip() [main thread — AudioClip.Create + AudioSource.Play]
```

**Problems:**
- **3 context switches** between background and main threads (STT→LLM, LLM→TTS, TTS→playback)
- **Unity coroutines run on the main thread**, so `LLMService.SendChatRequest()` blocked Unity's game loop while waiting for HTTP responses — visible as CPU stutters/hitches during LLM inference
- Each step had its own error handling, busy flags, and state management scattered across 4+ files

### After: Async Data Sandwich Pattern

The refactored pipeline chains all three HTTP calls into a single background task, touching the main thread only at the beginning (data extraction) and end (audio playback):

```
VAD stops → ProcessVoiceInputAsync(byte[] wavData) [main thread]
  ↓ Phase 1: extract plain data snapshot into VoicePipelineContext struct
Task.Run {
    HttpTranscribe()     // STT — HttpWebRequest on bg thread
      ↓ transcript string
    HttpChatCompletion() // LLM — HttpWebRequest on bg thread (no coroutine!)
      ↓ response string
    HttpSynthesizeTTS()  // TTS — HttpWebRequest on bg thread
      ↓ WAV bytes
}
  ↓ Phase 3: MainThreadDispatcher.Enqueue(() => HandleVoicePipelineSuccess(...))
HandleVoicePipelineSuccess() [main thread]
  → conversation history update, action parsing, subtitle display, AudioClip + Play
```

**Benefits:**
- **1 context switch** (final result only) vs. 3 previously
- **Zero main-thread blocking** — all HTTP I/O runs on a single background thread sequentially
- **Unified error handling** — one try/catch around the entire pipeline, three clean handler methods for success/empty/error
- **Per-step timing diagnostics** logged automatically (`[VOICE] Pipeline complete: STT=847ms LLM=2301ms TTS=156ms TOTAL=3304ms`)

---

## MainThreadDispatcher

A singleton `MonoBehaviour` that provides thread-safe marshaling of actions from background threads to Unity's main game loop.

### Design

```csharp
// Singleton with double-checked locking (thread-safe)
public static MainThreadDispatcher Instance { get; }

// Thread-safe queue — lock-free for producers, single-lock drain on consumer side
private readonly ConcurrentQueue<Action> _actionQueue = new();

// Enqueue from any thread. If already on main thread, executes immediately.
public void Enqueue(Action action)

// Called once per frame from Update(). Drains the entire queue atomically.
private void Update() { while (_actionQueue.TryDequeue(out var a)) a?.Invoke(); }
```

### Main-Thread Detection

Uses `Time.time` in a try/catch — Unity throws if accessed off-thread:

```csharp
public static bool IsMainThread => TryGetMainTime(out _);

private static bool TryGetMainTime(out float time)
{
    try { time = Time.time; return true; }
    catch { time = 0f; return false; }
}
```

This avoids reflection and works reliably across Unity versions.

### Error Isolation

Each enqueued action is wrapped in its own try/catch so one failure doesn't poison the queue:

```csharp
private void Update()
{
    while (_actionQueue.TryDequeue(out var action))
    {
        try { action?.Invoke(); }
        catch (Exception ex) { Log.Error($"MainThreadDispatcher: {ex.Message}"); }
    }
}
```

### Usage Pattern

```csharp
// From any background thread:
MainThreadDispatcher.Enqueue(() =>
{
    // Safe to call Unity APIs here
    AudioClip clip = AudioClip.Create("...", samples, 1, 24000, false);
    audioSource.clip = clip;
    audioSource.Play();
});
```

---

## Component Overview

| File | Responsibility | Thread Affinity |
|------|---------------|-----------------|
| `NPCVoiceControlMod.cs` | Mod entry point (`IModApi.InitMod()`), config loading, server startup, Harmony patches | Main thread |
| `ServerManager.cs` | Auto-starts whisper-server, llama-server, sherpa-server; GPU warmup coroutine | Main + bg threads |
| `VoiceInputManager.cs` | Microphone capture, VAD (voice activity detection), wake word routing | Main thread |
| `STTService.cs` | Whisper-server HTTP client, transcription config, `\n` artifact stripping | Background (`Task.Run`) |
| `LLMService.cs` | Legacy coroutine-based LLM client + RAG extraction requests | Main thread (coroutines) |
| `TTSService.cs` | Sherpa-ONNX TTS HTTP client, emote stripping, voice selection | Background (`Task.Run`) |
| `NPCChatComponent.cs` | **Core logic**: conversation history, phrase triggers, LLM routing, async pipeline entry point, RAG retrieval injection | Both (see below) |
| `NPCMemoryManager.cs` | RAG memory system: extraction, embedding, storage, retrieval, XML serialization | Background threads |
| `NPCAudioPlayer.cs` | AudioClip creation from WAV bytes, AudioSource management, playback lifecycle | Main thread |
| `SubtitleManager.cs` | Custom subtitle window (DontDestroyOnLoad singleton), bypasses vanilla tooltip queue | Main thread |
| `MainThreadDispatcher.cs` | Thread-safe action queue for background→main marshaling | Main thread (`Update()`) |
| `ActionParser.cs` / `ActionExecutor.cs` | JSON action parsing from LLM responses, NPC command execution | Main thread |

### What Runs Where

**Main thread (Unity game loop):**
- NPC targeting and proximity checks
- Phrase trigger matching (instant, no network)
- Conversation history management
- UI updates (subtitle window, typing indicators)
- AudioClip creation and AudioSource playback
- Harmony patches (game hook interception)

**Background threads:**
- All HTTP I/O (STT → LLM → TTS in single `Task.Run`)
- RAG memory extraction and embedding
- GPU warmup WAV POST to whisper-server
- Server process management (start/stop/restart)

---

## RAG Memory System

### Overview

NPCs remember facts about the player across conversations. The system extracts important details from conversation buffers, embeds them with a local model, and retrieves relevant memories during new conversations.

### Flow

```
Conversation → Shadow Buffer (15s idle or 20 messages)
  ↓ FlushShadowBuffer()
  ↓ Split into chunks of 6 messages
  ↓ NPCMemoryManager.ConsolidateBufferAsync()
    ↓ LLMService.GetMemoryLedgerAsync(chunk)
      → OpenAI chat format: clean system prompt + framed user message
      → Few-shot examples (4 total) with "The Player" prefix
      → Sent to LLM via HttpWebRequest
    ↓ Parse response, strip reasoning tags (`<think>`, `<thinking>`)
    ↓ Check StartsWith("NONE") — abort if no facts
    ↓ Sanitize summary: strip bullets, newlines → spaces, collapse whitespace
    ↓ Embed with local model (2048-dim vector)
    ↓ Store in NPCMemoryProfile
  ↓ Save to NPC_Memories.xml (XmlSerializer with [XmlArray] attributes)

Retrieval (during conversation):
  ↓ NPCMemoryManager.GetRelevantContextAsync(query)
    ↓ Embed query → cosine similarity score against all memories
    ↓ Return top 3 most relevant
  ↓ Inject as [SURVIVOR MEMORIES] block into system prompt
  ↓ Replace "The Player" with actual player name (noun alignment)
```

### Key Design Decisions

- **Buffer chunking**: Large buffers (12+ messages) overwhelm small LLMs. Split into chunks of 6 (3 exchanges) before extraction.
- **Few-shot examples beat rules**: Small models learn better from concrete examples than numbered constraints. 4 examples cover direct statements, questions, conversational synthesis, and negative case (NONE).
- **System/user message boundary**: Small LLMs fail when system prompt ends mid-sentence with transcript in a separate user message. Split into clean system prompt + framed user message via `<ExtractionUserPrefix>` config.
- **Player name substitution**: If system prompt says "Brian" but memories say "The Player", small LLMs think they're different people. Dynamically replace "The Player" with actual player name in recalled context.
- **XmlSerializer over JsonUtility**: JsonUtility fails silently on background threads and during Unity shutdown. XmlSerializer survives Unity teardown completely.
- **Shutdown save blocks**: `OnApplicationQuit()` handler calls `SaveAndWait()` which blocks until the background save thread completes, preventing data loss.

### Storage Format

`NPC_Memories.xml` with `[XmlArray("memories"), XmlArrayItem("MemoryEntry")]` attributes for proper array serialization:

```xml
<MemoryStore>
  <Profiles>
    <NPCMemoryProfile>
      <playerName>local_Player_171</playerName>
      <npcName>Ratchet the Hitter</npcName>
      <memories>
        <MemoryEntry>
          <summary>The Player has a red convertible...</summary>
          <vector>
            <float>0.123</float>
            ...
          </vector>
        </MemoryEntry>
      </memories>
    </NPCMemoryProfile>
  </Profiles>
</MemoryStore>
```

---

## Subtitle Window System

### Problem

NPC dialogue was routed through `GameManager.ShowTooltip()` which queues behind vanilla game popups (starter quest tutorial, "Inventory Full", etc.). Dialogue gets delayed or lost.

### Solution

Dedicated XUi window (`windowNPCSubtitles`) that bypasses the tooltip queue entirely.

**Architecture:**
- `SubtitleManager` — DontDestroyOnLoad singleton MonoBehaviour. Manages window lifecycle, auto-close coroutines, and `hasActionSetFor = None` to prevent input locking.
- `XUiC_NPCSubtitles` — XUi controller for the subtitle window. Uses typed `GetChildrenByViewType<XUiV_Label>()` for label access.
- Window position: `pos="-300,230"` in `Config/XUi/windows.xml`.

**Key rules:**
- `hasActionSetFor = XUiWindowGroup.EHasActionSetFor.None` must be set in `ShowSubtitle()` (not `Init()`) because `XUiWindowGroup` isn't registered during async XUi parsing.
- Auto-close coroutine runs on SubtitleManager (DontDestroyOnLoad) so it survives scene transitions.

---

## Phrase Trigger Improvements

### Word Weight Filter

Prevents conversational phrases from triggering actions. If the matched trigger phrase makes up less than 25% of the spoken sentence, it's treated as conversational context and sent to the LLM instead.

```csharp
private static bool IsCommandIntent(string transcribedText, string triggerPhrase)
{
    int sentenceWords = CountWords(transcribedText);
    int triggerWords = CountWords(triggerPhrase);
    float weight = (float)triggerWords / sentenceWords;
    return weight >= 0.25f; // Trigger must be at least 25% of the sentence
}
```

**Examples:**
| Input | Weight | Result |
|-------|--------|--------|
| "pick up" | 100% | ✅ Executes pickup |
| "can you pick up?" | 50% | ✅ Executes pickup |
| "pick up my cat" | 25% | ✅ Barely passes (edge case) |
| "we have to pick up my cat" | 25% | ❌ Sent to LLM |

### Pickup State Guard + Debounce

Prevents character duping from SCore race conditions:

1. **Hired check**: NPC must have a player leader before pickup executes
2. **HashSet debounce**: `_pendingPickups` tracks entity IDs currently being collected. Duplicate calls are blocked until the first completes (or fails). No cleanup needed on success — the entity ID dies with the world entity.

```csharp
// State guard: NPC must be hired
if (!ChatComponentManager.HasPlayerLeader(npc)) return "Hire me first!";

// Race condition guard
if (_pendingPickups.Contains(npc.entityId)) return "Already getting on!";
_pendingPickups.Add(npc.entityId);

try {
    EntitySyncUtils.Collect(npc.entityId, player.entityId);
    // No Remove() needed — entityId dies with the entity
} catch {
    _pendingPickups.Remove(npc.entityId); // Unlock on failure
}
```

### Language-Aware Responses

Phrase trigger responses filter by language:
- English responses: Latin-only characters required
- Japanese/Chinese responses: CJK characters required
- Falls back to any response if no language match exists

---

## Lessons Learned

### 1. Unity Coroutines Are Main-Thread Only — Don't Use Them for Network I/O

The original `LLMService.SendChatRequest()` used `UnityWebRequest` inside a coroutine, which meant the main thread yielded frames while waiting for LLM inference to complete. On slower hardware or with larger models, this caused visible 1–3 second hitches where the game appeared frozen.

**Fix:** Replace coroutines with synchronous `HttpWebRequest` calls inside `Task.Run`. The background thread blocks on I/O instead of the main thread yielding frames. Result: zero visible stutter during LLM inference.

### 2. Extract Plain Data Before Going Off-Thread (Data Sandwich)

Holding Unity object references (`MonoBehaviour`, `GameObject`, `AudioSource`) across thread boundaries is unsafe and leads to subtle bugs. The `VoicePipelineContext` struct captures everything the background pipeline needs as plain C# types:

```csharp
// Main thread — safe, all direct property access
VoicePipelineContext ctx = new VoicePipelineContext
{
    NpcName = _npcName,
    SystemPrompt = BuildActionSystemPrompt(),
    HistoryRoles = new List<string>(_conversationHistory.Count),
    // ... all config values as primitives/strings
};

// Background thread — no Unity references at all
Task.Run(() => { /* pure HTTP calls using ctx */ });
```

### 3. One `#endregion` Missing Breaks the Entire Build

C# region directives must be perfectly balanced. A missing `#endregion` caused a cascade of compiler errors (`CS1038: #endregion directive expected`) that were hard to trace back to the root cause. Always verify region balance after large edits.

### 4. Verbatim Interpolated Strings (`$@"..."`) Are an Escape-Hell Trap

The `ExtractJsonString` method originally used `$@\"\\\"{key}\\\"...\"` for regex patterns, which produced unresolvable escape sequences through multiple edit iterations. **Lesson:** avoid verbatim interpolated strings for complex escaping — use simple string concatenation or manual character-by-character parsing instead.

### 5. `HttpWebRequest` vs `UnityWebRequest` — Know the Difference

| | `HttpWebRequest` | `UnityWebRequest` |
|---|---|---|
| Thread safety | ✅ Works on any thread | ❌ Main thread only (coroutines) |
| Blocking | Synchronous by default | Async via coroutines or `.SendWebRequest()` |
| Dependencies | `System.Net` (built into .NET) | Unity engine |
| Best for | Background tasks, service calls | Asset loading, main-thread async ops |

For background HTTP I/O in Unity mods, always prefer standard .NET HTTP clients.

### 6. GPU Warmup Requires an Actual Transcription Request

The whisper-server's built-in warmup (~0.2s) only loads the model into memory. To compile OpenCL/GPU inference kernels for actual audio processing, you must send a real transcription request with audio data. The mod sends a 0.1s silent WAV buffer immediately after server startup to pre-compile these kernels without blocking Unity's main thread.

### 7. Stale DLLs Are the #1 "It Doesn't Work" Cause

After deploying code changes, always verify the game is loading the new DLL. The mod's build script copies to `Mods/1-XNPCVoiceControl/`, but if the game was already running or cached an old assembly, logs will show stale behavior. **Always restart the game after deployment.**

### 8. Keep Game Logic on Main Thread, Offload Only I/O

The refactoring preserved NPC targeting, phrase triggers, and conversation history management on the main thread because they depend on Unity APIs and game state. Only network I/O (STT→LLM→TTS) was moved to background threads. This separation keeps the architecture clean and avoids threading bugs in game-specific logic.

### 9. Small LLMs Ignore Appended Instructions

Appending `[Recalled Context]` + instructions after the base prompt is ineffective for small models. They treat tacked-on text as out-of-character metadata. **Fix:** integrate memories as a named block (`[SURVIVOR MEMORIES]`) inside the persona definition with explicit usage instructions.

### 10. Few-Shot Examples Must Cover Conversational Patterns

If few-shot examples only show pristine single-sentence declarations, small LLMs overfit to that pattern and reject conversational phrasing ("yeah", "no it's...") or multi-turn facts as NONE. **Fix:** add a 4th example showing multi-turn synthesis with casual transitions. Use different topics than the test case to force generalization.

### 11. System/User Message Boundary Breaks Small LLMs

Ending a system prompt with `Transcript to extract:` and passing the transcript as a separate user message causes small models to fail completely. They see the system prompt end abruptly and treat the user message as disconnected text. **Fix:** cap the system prompt cleanly after the last example, then frame the user message explicitly.

### 12. Word Weight Prevents False Positive Actions

Substring matching alone causes "pick up my cat" to trigger the pickup action. Comparing trigger phrase word count against total sentence word count (≥ 25% threshold) distinguishes commands from conversational context without regex complexity.

---

## Performance Notes

### Measured Pipeline Latency (Typical Hardware: Ryzen 5, RTX 3060)

| Step | Time | Thread |
|------|------|--------|
| STT (whisper-server, base model) | 400–900ms | Background |
| LLM (llama.cpp, 1.9B Q4_K_M) | 800–2500ms | Background |
| TTS (sherpa-server) | 100–300ms | Background |
| **Total pipeline** | **1.3–3.7s** | **Background** |
| Main-thread marshaling overhead | <1ms | Main |

### Before vs After Comparison

| Metric | Old (Callback Chain) | New (Data Sandwich) |
|--------|---------------------|-------------------|
| Context switches | 3 (STT→main, LLM→main, TTS→main) | 1 (final result only) |
| Main-thread blocking | Yes (LLM coroutine yields frames) | No (all I/O on bg thread) |
| Visible stutter during LLM | 1–3s hitch | None |
| Error handling locations | 4+ files, scattered | 3 handler methods in one place |
| Per-step timing logs | Manual per-service | Automatic in pipeline method |

### Trade-offs

- **Sequential vs parallel:** The pipeline runs STT→LLM→TTS sequentially on one thread. Parallel execution (e.g., start TTS while LLM is still thinking) would require speculative synthesis and is not implemented. For typical use, sequential is simpler and avoids wasted TTS calls when the user cancels mid-conversation.
- **No streaming:** The entire WAV buffer is captured before transcription begins. Streaming VAD → partial transcription → progressive response is a future optimization but adds significant complexity.

---

## Future Improvements (Not Implemented)

1. **Streaming pipeline** — Send audio chunks to whisper-server incrementally for faster first-word latency
2. **Speculative TTS** — Start synthesizing common responses while LLM is still thinking, cancel if wrong path taken
3. **Pipeline cancellation** — `CancellationToken` support so the user can interrupt mid-pipeline (e.g., walk away from NPC)
4. **Connection pooling** — Reuse `HttpClient` instances instead of creating new `HttpWebRequest` per call for reduced overhead
