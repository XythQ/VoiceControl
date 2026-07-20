# NPCVoiceControl — Change Log

---

## 3.0.01 (Tester Build)

- **Supertonic TTS engine** now ships as the default engine (faster synthesis, better voice quality)
- **Push-to-Talk is default** — wake-word disabled out-of-box (opt-in via config or in-game menu)
- **Wake-word + Supertonic guard** — enabling wake-word auto-forces Kokoro engine (CPU contention incompatibility)
- **Pipeline TTS prefetch** — synthesizes next sentence while current one plays, closing inter-sentence gaps on both engines
- **Harley voice clips restored** — Harley Quinn personality reverted to VoiceMode=clips after Supertonic voice test; cloned voice alias and JSON preserved for future assignment

---

## 3.0 (Major Release)

### TTS Engine

- **Dual TTS engine architecture** — Choose between Kokoro (sherpa-server, 54 voices, multi-language audio) or Supertonic (supertonic-server, 10 presets + custom cloned voices). Configured via `<TtsEngine>` in `modconfig.xml`. Only the selected engine starts at launch.
- **Supertonic integration** — Full supertonic-server with 4 ONNX models, ORT acceleration, voice style JSON system, and HTTP API mirroring sherpa-server contract (POST /tts, GET /health). All Kokoro voice aliases map to Supertonic presets automatically.
- **Kokoro v1.0 model** — Unified Kokoro multi-language model supporting 54 voices across 9 language families (replaced Piper VITS)
- **Pipeline synthesis prefetch** — DrainSpeechQueue synthesizes sentence N+1 while N plays, closing inter-sentence gaps. Engine-agnostic; verified smooth on both Kokoro and Supertonic.
- **Streaming TTS for long text** — Pre-written/story/greeting text splits into sentences (min 30 chars) and enqueues each through the speech queue. First audio drops from ~7s to ~1.5s.

### Voice Clips

- **VoiceMode=clips** — NPCs can play pre-recorded OGG clips instead of TTS. Clip libraries in `Resources/NPC_Voices/<Folder>/{Greetings,Triggers/<action>,Default}/*.ogg` with optional `.txt` sidecars for subtitles.
- **Clip library auto-discovery** — Scans all loaded mods' `Resources/NPC_Voices/` folders at startup.

### Subtitles and Localization

- **Localized subtitles (11 languages)** — NPC dialogue displays in the player's game language: German, Spanish, French, Italian, Portuguese, Japanese, Korean, Chinese (Simplified), Russian, Turkish, Polish. Audio stays English (dubbed architecture). Auto-detected from game settings — no manual toggle needed.
- **Language auto-detection** — Replaced manual Japanese toggle with automatic detection via `GamePrefs.GetString(EnumGamePrefs.Language)` -> ISO code mapping (12 vanilla languages).
- **ForceLanguage per personality** — Optional `<ForceLanguage>` element forces an NPC's subtitle language regardless of player setting.

### Configuration

- **Unified modconfig.xml** — All engine and runtime settings consolidated into a single file (replaced llmconfig.xml, ragconfig.xml, promptconfig.xml, sttconfig.xml, ttsconfig.xml).
- **In-game config panel** — Escape menu button opens settings for TTS, STT, LLM, volume, speech rate, distances, wake-word toggle, and more. Test buttons for voice, microphone, and AI connection.
- **Config parse robustness** — All numeric/bool config values use `ParseInt`/`ParseFloat`/`ParseBool` helpers with per-field warning logs. A single typo no longer silently reverts the entire section.

### Speech Recognition

- **Push-to-Talk default (V key)** — Hold V, speak, release. Configurable via `<PushToTalkKey>`.
- **Wake-word opt-in** — "Hey Marvin" hands-free activation disabled by default. Toggle in-game or edit `<WakeWordEnabled>`. Thread priority configurable (`<WakeWordThreadPriority>`). Inference chunk burst capped (`<WakeWordMaxChunksPerSignal>`) to prevent CPU starvation.
- **Automatic language detection** — Whisper auto-detects spoken language by default (was locked to English).
- **GPU acceleration with safe fallback** — whisper-server uses Vulkan GPU when available, falls back to CPU on failure.

### LLM and Conversation

- **Qwen2.5-3B-Instruct-Q4_K_L** is now the default model (replaced ggml-model-Q4_K_M). Ships in the deploy folder alongside llama-server.
- **Drag-and-drop model swapping** — Place any `.gguf` in `Resources/Models/Llama/` and the mod auto-discovers it at startup.
- **Reasoning model support** — Increased MaxTokens range (64-2048), context size configurable (2048-32768).
- **Typewriter subtitle display** — Configurable typing indicator with per-character delay (`<ShowTypingIndicator>`, `<TypingDelayMs>`).
- **JSON parser refactor** — All LLM response parsing uses Newtonsoft.Json (replaced hand-rolled IndexOf scanners).

### NPC Memory (RAG)

- **Persistent player memory** — NPCs remember facts about you across game sessions using a stable player ID.
- **Relational memory system** — Memories keyed by `{player}_{npc_name}` so each NPC has its own knowledge of you.
- **Smarter fact extraction** — The LLM extracts clean facts from conversations, including information hidden in questions.
- **Safe shutdown saves** — All pending memories flush to disk before the game closes.
- **Consolidation gate** — RAG consolidation defers up to 30s while TTS is active, preventing latency spikes during speech.
- **Embedding server GPU offload** — Embedding model uses `--gpu-layers -1` for full Vulkan GPU acceleration (13/13 layers).

### NPC Personalities

- **Expanded personality library** — 27+ personalities with per-character `ProactiveGreetingChance` (opt-in, 0.0-0.8).
- **Gender-aware voice assignment** — Each personality has separate male and female voice selections.
- **Persistent personality binding** — NPCs keep their assigned personality across respawns (cached by name, not entity ID).
- **Random assignment filtering** — Non-American personalities excluded from random assignment to keep the Arizona setting consistent.

### Phrase Triggers (Voice Commands)

- **Multi-language commands** — Phrase triggers work in English, Japanese, and Chinese with automatic detection.
- **Combat mode switching** — Voice commands to set NPC behavior: Hunting (free attack), Full Control. Threat Control reserved for future update.
- **Complete weapon swap system** — Voice commands for every NPC weapon type (melee, ranged, special).
- **Leader-aware actions** — Commands like Follow, Guard, and Dismiss only work on hired NPCs; Trade and Hire work on anyone.
- **Story pool** — Ask an NPC to "tell me a story" for randomized lore dialogue.

### In-Game UI

- **Custom subtitle window** — NPC dialogue displays in a dedicated bottom-center overlay instead of the native tooltip queue.
- **Configuration panel** — Pause menu button opens settings for TTS, STT, LLM, volume, speech rate, distances, and more.
- **Test buttons** — Built-in buttons to test voice output, microphone input, and AI connection without leaving the menu.

### Reliability and Stability

- **Automatic server management** — llama-server (LLM), sherpa-server/supertonic-server (TTS), whisper-server (STT), and embed-server start automatically on game launch with auto-retry.
- **Background thread safety** — All memory operations run off the main thread with proper marshaling for UI updates. MainThreadDispatcher uses captured ManagedThreadId (fixed Unity 2022.3 Time.time detection bug).
- **Off-thread Unity API audit** — All Task.Run bodies use `Stopwatch`/`DateTimeOffset.UtcNow` instead of Unity APIs. Zero off-thread Unity calls.
- **Process handle disposal** — Server processes properly disposed on shutdown with event handler detachment.
- **Robust error handling** — Server errors logged visibly; harmless noise suppressed.

### Build and Deploy

- **dist/ source-of-truth packaging** — Build assembles `dist/` via MSBuild target (allowlist, wiped+rebuilt each build); deploy overlays it to the live folder. Eliminates stale file drift.
- **XML em-dash build guard** — `ValidateModXml` MSBuild target rejects XML comments with em-dashes, en-dashes, or `--` (Unity Mono load crash). Config files cleaned at build time.

---

## Pre-3.0 (Migration Era)

### Multi-Language Text-to-Speech

- **Unified Kokoro engine** replaced separate TTS backends with a single Kokoro multi-language model supporting 54 voices across 9 language families
- **Full language support**: American English, British English, Japanese (Kanji/Hiragana/Katakana), Simplified Chinese, Spanish, Hindi, Irish, Finnish, and Polish
- **CPU-only operation** — removed DirectML dependency for broader hardware compatibility
- **Language-aware greetings** — NPCs greet in their native language with fallback to English
- **Simple voice aliases** — use friendly names like "Adam" or "Sarah" instead of numeric IDs

### Speech Recognition (Whisper)

- **Automatic language detection** — Whisper now auto-detects spoken language by default (was locked to English)
- **Wake word support** — hands-free activation using a wake-word model for natural interaction
- **Push-to-talk** — configurable key binding for voice input
- **Improved accuracy** — domain-specific keyword hints tuned for 7 Days to Die context

### NPC Memory (RAG)

- **Persistent player memory** — NPCs remember facts about you across game sessions using a stable player ID
- **Relational memory system** — memories are keyed by `{player}_{npc_name}` so each NPC has its own knowledge of you
- **Smarter fact extraction** — the LLM now extracts clean facts from conversations, including information hidden in questions
- **Safe shutdown saves** — all pending memories flush to disk before the game closes, preventing data loss
- **Concurrent processing** — embedding and chat requests run in parallel for faster response times

### Phrase Triggers (Voice Commands)

- **Multi-language commands** — phrase triggers work in English, Japanese, and Chinese with automatic detection
- **Combat mode switching** — voice commands to set NPC behavior: Hunting (free attack), Threat Control, or Full Control
- **Complete weapon swap system** — voice commands for every NPC weapon type (melee, ranged, special)
- **Leader-aware actions** — commands like Follow, Guard, and Dismiss only work on hired NPCs; Trade and Hire work on anyone
- **Story pool** — ask an NPC to "tell me a story" for randomized lore dialogue

### In-Game UI

- **Custom subtitle window** — NPC dialogue displays in a dedicated bottom-center overlay instead of the native tooltip queue
- **Configuration panel** — pause menu button opens settings for TTS, STT, LLM, volume, speech rate, distances, and more
- **Test buttons** — built-in buttons to test voice output, microphone input, and AI connection without leaving the menu

### NPC Personalities

- **Expanded personality library** — 25+ personalities across American, British, Japanese, Chinese, Spanish, Hindi, Irish, Finnish, and Polish variants
- **Gender-aware voice assignment** — each personality has separate male and female voice selections
- **Persistent personality binding** — NPCs keep their assigned personality across respawns (cached by name, not entity ID)
- **Random assignment filtering** — non-American personalities are excluded from random assignment to keep the Arizona setting consistent

### Configuration

- **Consolidated settings** — all engine and runtime options unified into a single `modconfig.xml` file
- **Comprehensive documentation** — every config file now has inline comments explaining each field, valid ranges, and practical effects
- **Template blocks** — copy-paste templates in `personalities.xml` and `phrasetriggers.xml` make adding new content straightforward
- **XML-driven voice mapping** — all 54 Kokoro voices mapped in `ServerConfig.xml` with clear ID ranges by language/accent

### Default LLM Model

- **Qwen2.5-3B-Instruct-Q4_K_L** is now the default model (replaced `ggml-model-Q4_K_M`). Ships in the deploy folder alongside llama-server.

### Reliability & Stability

- **Automatic server management** — llama-server (LLM), sherpa-server (TTS), and whisper-server (STT) start automatically on game launch
- **Robust error handling** — server errors are logged visibly; harmless noise is suppressed
- **Safe process cleanup** — pending buffers save before servers shut down on game exit
- **Background thread safety** — all memory operations run off the main thread with proper marshaling for UI updates
