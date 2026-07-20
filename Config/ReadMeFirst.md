# 1-XNPCVoiceControl — Read Me First

You've installed the mod. Here's how to actually use it, what to expect, and how to fix common problems.

---

## Quick Start

### Talking to NPCs

| Method | How |
|--------|-----|
| **Push-to-Talk** (default) | Walk up to an NPC, hold **V**, speak your message, release |
| **Text Chat** | Press **T**, type `@Your message here` in global chat (the `@` targets the nearest NPC) |

### What Happens

1. Your speech is transcribed to text (Whisper)
2. If it matches a known command ("follow me", "open inventory"), it executes **instantly**
3. If it doesn't match a command, the local AI thinks about it and responds with voice + subtitles

### First-Time Startup

When you first launch after installing, expect a **15-30 second delay** before the mod is ready. All four helper servers (LLM, TTS, STT, embeddings) start automatically. You'll see server status messages in the game console. The NPCs won't respond until all servers are online.

### Requirements

This mod requires **0-SCore v3.0.28.1524 or newer**. Older SCore versions have bugs with NPC inventory windows on dedicated servers and missing crouch override hooks. If you also use 0-XNPCCore, that is still required as well.

### Multiplayer Status

Multiplayer status: Singleplayer and Listen-host are fully supported. Dedicated servers are now supported — the mod runs NPC AI server-side, and voice commands (orders, follow distance, patrol, formations, squad commands) route correctly to the server. Install the mod on both the server and every client, and make sure they're the same build (a mismatch is logged as a warning at join).

Each player runs their own AI locally — the LLM, speech-to-text, and text-to-speech all run on your own machine, so every player needs the sidecar servers. The dedicated server itself needs no GPU and starts no AI servers; it only relays and arbitrates.

Not yet synchronised with 2+ players on one server: NPC memory is per-player (each client's NPC remembers its own conversations), there's no conversation lock (two players talking to the same NPC at once will conflict), and hire billing isn't arbitrated between clients. Single-player-per-server and singleplayer are unaffected.

---

## In-Game Settings Menu

Open the escape menu (**Esc**) and click **NPC VoiceControl Settings**. Here's what each control does:

### Text-to-Speech (TTS)

| Setting | What It Does |
|---------|-------------|
| **Text-to-Speech** toggle | Turn NPC voice on/off entirely |
| **Volume** | How loud NPCs speak (0.0–1.0) |
| **Speech Rate** | Speed of NPC speech (0.5 = slow, 2.0 = fast). Default 1.0 is natural |

### Speech-to-Text (STT)

| Setting | What It Does |
|---------|-------------|
| **Speech-to-Text** toggle | Turn voice recognition on/off |
| **Wake Word (Hands-Free)** | Say "Hey Marvin" instead of holding V. **Off by default** — it uses extra CPU. See FAQ below |
| **Beam Size (1–5)** | How carefully Whisper analyzes your speech. Higher = more accurate but slower. Default 3 is the sweet spot |
| **Speech Recognition** button | Click to toggle between **Accurate** (small model, best quality) and **Fast** (base model, faster on weak GPUs). **Requires full game restart to apply** |

### Conversation

| Setting | What It Does |
|---------|-------------|
| **Loaded AI Model** | Shows which LLM is loaded (read-only) |
| **Max Tokens (64–2048)** | Maximum response length. Higher = longer replies but slower. Default 512 is good for most |
| **Context Window (3–20)** | How many recent exchanges the AI remembers in conversation. Higher = more context-aware but uses more VRAM |

### Distance & Range

| Setting | What It Does |
|---------|-------------|
| **Chat Distance (3–15m)** | How close you need to be to talk to an NPC with voice |
| **Hired Chat Distance (5–40m)** | How far away your hired companions will still respond to you |
| **Voice Range (5–20m)** | How far NPCs' voices carry (audio volume drops off after this) |

### Facial Expressions

| Setting | What It Does |
|---------|-------------|
| **Procedural blink** (automatic) | NPCs blink naturally at random intervals (2–6 seconds). Works on most NPC models with eyelid geometry. Some models split eye geometry across multiple meshes and won't blink — this is a model limitation, not a bug |

### Long-Term Memory

| Setting | What It Does |
|---------|-------------|
| **Long-term memory** (automatic) | The mod remembers facts about you across conversations and game sessions. If you tell an NPC your name, what you're building, or where you live, they'll recall it later. Stored per-NPC — each NPC has its own memory of you. Saved to `NPC_Memories.xml` in the Mods folder. High-importance memories (like shared events) persist longer than casual facts |

### Other

| Setting | What It Does |
|---------|-------------|
| **Chat Timeout (15–120s)** | How long the NPC stays "in conversation mode" after you speak. After this idle period, the NPC forgets recent context and you'd start a fresh conversation. Default 30s is good for most — higher if you like long pauses between exchanges |
| **Mod Debug Logging** | Turn on only if troubleshooting — adds lots of console spam |

### Test Buttons

- **Test Voice** — plays a sample TTS line so you can hear the voice
- **Test Microphone** — records 3 seconds and shows what Whisper heard
- **Test AI Connection** — confirms the LLM server is responding
- **Clear All Conversations** — wipes chat history with all NPCs

---

## Common Config Changes (modconfig.xml)

The in-game menu covers most things. For advanced tweaks, edit `Config/modconfig.xml` in your Mods folder. **Changes here require a full game restart.**

### TTS Engine (`<TtsEngine>`)

| Value | When to Use |
|-------|------------|
| `supertonic` (default) | Best quality, fastest synthesis. Use this unless you have a reason not to |
| `sherpa` | You need wake-word mode (Supertonic is incompatible with wake-word) or want 54 different voice options |

### Speech Recognition Model (`<Model>`)

Leave empty for auto-detect. Set directly if the in-game toggle isn't enough:

- `ggml-small.bin` — Accurate (default, best transcription quality)
- `ggml-base.en.bin` — Fast (faster on weak GPUs, slightly less accurate)

### Push-to-Talk Key (`<PushToTalkKey>`)

Default is **V**. Change to any single key: `<PushToTalkKey>G</PushToTalkKey>`

### Microphone Device (`<MicrophoneDevice>`)

If voice isn't working at all, your mic might not be the default device. Set it explicitly:

```xml
<MicrophoneDevice>Microphone (Realtek Audio)</MicrophoneDevice>
```

Check the boot log for your exact device name — it prints `[0] Microphone (device name)` on startup.

### Max Tokens (`<MaxTokens>`)

Maximum response length in tokens. If NPC responses cut off mid-sentence, increase from 512 to 768 or 1024. If responses are too long and slow, decrease to 384.

### Max Response Length (`<MaxResponseLength>`)

Hard cap on response characters (default 300). Prevents the AI from generating very long speeches. Lower = snappier replies, higher = more detailed answers but slower TTS.

### Typing Delay (`<TypingDelayMs>`)

Per-character delay for the typewriter subtitle effect (default 75ms). Values: 50 = fast, 75 = natural, 150 = dramatic. Set to 0 to disable the typing animation entirely.

### Context Memory (`<ContextMemory>`)

How many recent exchanges the AI keeps in its short-term context window during conversation. The default is 5.
- **3–5** (default) — remembers last few things you said, lowest VRAM usage
- **8–10** — more context-aware, remembers longer conversations, uses more VRAM
- **15–20** — very context-aware but may repeat old topics and uses significant VRAM

When the conversation grows beyond this limit, older messages are trimmed from the LLM prompt (but facts you told NPCs are saved to long-term memory automatically).

### KV Cache Size (`<ContextSize>`)

Controls how much VRAM the LLM server reserves for its context window. Lower values help on GPUs with limited VRAM. The default 8192 works for most cards with 8 GB+ VRAM.

| ContextSize | VRAM Used | When to Use |
|-------------|-----------|-------------|
| **2048** | ~224 MB | Bare minimum — very limited conversation memory, last resort for 4 GB cards |
| **4096** | ~448 MB | 6 GB VRAM cards; use with IQ4_NL model |
| **8192** (default) | ~896 MB | Recommended for 8 GB+ VRAM (RTX 3060 / 4060 class) |
| **16384** | ~1.75 GB | 12 GB+ cards (RTX 3080 / 4070 Ti class) |
| **32768** | ~3.5 GB | 16 GB+ cards (RTX 3090 / 4090 class) |

Values above 32768 waste VRAM with no gameplay benefit. If your game stutters or crashes on launch, try lowering this to 4096.

### Supertonic TTS Speed (`SupertonicConfig.xml`)

If NPC voice synthesis feels slow on a weaker CPU, two settings in `Config/SupertonicConfig.xml` can help:

| Setting | Default | Faster Value | Trade-off |
|---------|---------|-------------|----------|
| `<TotalStep>` (denoising steps) | 6 | **4 or 5** | Slightly less polished audio, noticeably faster. At 4 most users won't notice quality loss |
| `<IntraOpNumThreads>` | 4 | **2** | Less CPU contention on quad-core CPUs; slower isolated synthesis but smoother overall game performance |

Neither is in the in-game menu — edit the XML directly and restart the game.

**Note:** The in-game **Speech Rate** slider controls how fast or slow the voice *sounds* (playback speed). These settings control how long it takes to *generate* the audio. They're separate.

---

## FAQ

### Why is speech recognition slow?

Whisper runs on your GPU via Vulkan. On older GPUs (GTX 10-series or older) or when the GPU is busy rendering, transcription can take several seconds.

**Fix:** In the in-game settings, click **Speech Recognition** to switch to **Fast**, then click **Save Settings**. The subtitle will say "Restart game to apply." Fully exit the game and restart — this uses a smaller, faster model.

### Why does it say "Restart game to apply"?

The speech recognition model loads once when the game starts. Changing it in the menu saves your preference but can't hot-swap the running server. A full game restart (not just quit-to-menu) is needed.

### What's the difference between Accurate and Fast?

| | Accurate (small) | Fast (base) |
|---|---|---|
| Model | `ggml-small.bin` | `ggml-base.en.bin` |
| Transcription quality | Best | Very good |
| GPU load | Higher | Lower |
| Speed | ~1-2s per phrase | ~0.5-1s per phrase |

For most users with decent GPUs (RTX 3060 or better), Accurate is fine. If you're on an older card or notice stuttering, switch to Fast.

### Can I use wake-word mode?

Yes, but it uses extra CPU and forces the Kokoro TTS engine (Supertonic is incompatible). To enable: toggle **Wake Word (Hands-Free)** in the in-game settings, or set `<WakeWordEnabled>true</WakeWordEnabled>` in `modconfig.xml`. Say "Hey Marvin" to start recording — it stops after silence.

### Why won't the NPC follow my orders?

The NPC must be **hired** (have you as their leader). Leaderless NPCs will chat with you but refuse commands like "follow me." Say "hire you" to open the hire dialog first.

### The NPC keeps saying "I don't take orders from just anyone"

You're talking to a wild NPC that isn't hired yet. Say "hire you" or use the vanilla hire system first. Traders are an exception — they'll always open their inventory regardless of leader status.

### My responses sound robotic or synthetic

This is Supertonic's default behavior on longer passages. It uses a diffusion model with 6 denoising steps — quality is good but can sound slightly mechanical on story-length text. This is expected and not a bug.

### Can I use my own LLM model?

Yes. The mod ships with a small 3B-parameter model optimized for NPC dialogue. You can swap it for a larger or different model:

**1. Download a `.gguf` file** — any llama.cpp-compatible GGUF model works (HuggingFace, TheBloke, etc.). Quantized models (Q4_K_M, Q4_0, IQ4_NL) are recommended.

**2. Place it in the folder:**
```
1-XNPCVoiceControl/Resources/Models/Llama/
```
Remove or rename the old `.gguf` file first — only keep one model in the folder.

**3. (Optional) Specify by name in modconfig.xml:**
```xml
<ModelFilename>your-model.gguf</ModelFilename>
```
If omitted, the mod auto-discovers the first `.gguf` it finds.

**4. Restart the game.** The server loads the model on startup and prints its name to the console.

### What model size should I use?

| Model Size | VRAM Needed | When to Use |
|------------|-------------|-------------|
| **3B** (included) | ~2 GB | Default — fast, good enough for NPC dialogue |
| **8B Q4_K_M** | ~5 GB | Better reasoning and personality. Needs 8 GB+ VRAM card |
| **8B IQ4_NL** | ~5 GB | Higher quality quantization of 8B models |
| **13B Q4_K_M** | ~8 GB | Excellent quality, needs 12 GB+ VRAM card |

**Important tuning when upgrading to a larger model:**

- **Increase Max Tokens** — the default 512 is fine for 3B models. An 8B or 13B model can handle longer responses, so bump to **768–1024** in the in-game settings.
- **Increase ContextSize** — the default 8192 KV cache may bottleneck a larger model. Try **16384** for 8B models, **32768** for 13B (see VRAM table above).
- **Increase ContextMemory** — larger models benefit from more conversation history. Try **8–10** instead of the default 5.

### The AI only speaks English

The included 3B model is multilingual but strongest in English. For other languages, a dedicated multilingual 4B model is available as an optional download from the mod page.

### What are "phrase triggers"?

Pre-written commands that bypass the AI entirely for instant execution: "follow me", "stay here", "open inventory", "use your knife", etc. These execute in under 1 second because no LLM thinking is involved.

When you give a command, your NPC nods to confirm acceptance or shakes their head to refuse — subtle head gestures that respond to whether they can comply (hired status, item availability, etc.).

### "Tell me a story"

This phrase trigger plays a pre-written story from the NPC's personality pool — instant response with no AI thinking involved. Each personality has multiple stories, picked at random.

### What are aliases?

Each phrase trigger can have alternative names called **aliases**. For example, the "open inventory" trigger also matches "open your backpack", "show me what you got", or "trade with you" — these are aliases defined in `phrasetriggers.xml`. You don't need to memorize exact phrases; if you say something close enough, it'll match. Modders can add their own aliases by editing the XML.

### Can I add my own voice commands?

Yes — edit `Config/phrasetriggers.xml`. Each trigger has a phrase pattern and a response. The XML is well-commented with examples. See the mod page for documentation.

---

## Troubleshooting

### Voice recognition isn't working at all

1. Check `bin/WhisperServer/` contains `whisper-server.exe`, `whisper.dll`, and `ggml*.dll` files
2. In-game settings → click **Test Microphone** — does it show what you said?
3. If mic test fails: check your `<MicrophoneDevice>` setting matches your actual device name (see boot log)
4. If whisper-server didn't start, look for error lines in the game console

### NPC voice isn't playing

1. Check TTS is enabled in the in-game settings
2. Click **Test Voice** — does it play?
3. Check which engine is active (`<TtsEngine>` in modconfig.xml) and verify that server started (console log shows `[supertonic-server]` or `[sherpa-server]`)

### NPC doesn't respond to anything

1. Are you within chat distance? Default is 5m — walk closer
2. Is the LLM server running? Check console for `llama-server is accepting connections on port 5055`
   The LLM uses port 5055 (moved off 8080 to avoid collisions with web admin panels like AMP).
   Upgrading and hand-edited modconfig.xml? Update Endpoint to 5055.
3. Is there a `.gguf` file in `Resources/Models/Llama/`?
4. Try text chat (`@hello`) — if that works, it's a voice issue, not an AI issue

### "Failed to bind one of the game ports (26900)" when hosting again

Fixed in 3.0.07. The mod's own helper servers were holding an inherited copy of the game's port after exiting to menu. Exiting to menu now automatically stops and restarts them — Continue should work normally without a full restart. If you still see this, please report it with your Player.log.

### Game hangs on exit

Known Steam/7DTD issue. Just wait or click Stop in Steam. Not caused by the mod.

### Speech recognition is very slow (several seconds)

See FAQ above — switch to Fast mode. If you're already on Fast and it's still slow, your GPU may be under heavy load. You can also lower **Beam Size** to 1 in the settings for maximum speed (at the cost of some accuracy).

### The LLM response cuts off mid-sentence

Increase **Max Tokens** in the in-game settings (try 768 or 1024). The default 512 is enough for most conversations but long stories need more.

### I hear two voices overlapping

This shouldn't happen in the current version. If it does, interrupting an NPC mid-speech should stop the old voice and start the new one immediately. Report this as a bug with your Player.log.

---

## Diagnostics

Press **F1** to open the console, then type:

- `vc status` — shows mod state, server connections, current settings
- `dm` — toggles debug logging on/off (useful for troubleshooting)

The game log location depends on how you launched the game:

| Launch Method | Log Location |
|---------------|-------------|
| **Steam / .exe** (default) | `%AppData%\..\LocalLow\The Fun Pimps\7 Days To Die\Player.log` |
| **Game Launcher** | `%AppData%\7DaysToDie\logs` |
| **Mod Launcher** | `<gamefolder>\7DaysToDie_Data\output.log` |

Search for `[1-XNPCVoiceControl]` to see mod-specific messages.

---

## What's New in 3.0.09

### Tactical Mode
- **Player-wide command-only mode** — toggle with `go tactical`/`lock in` (ON) or `at ease`/`stand down`/`normal mode` (OFF). In tactical mode, unrecognized phrases get a rotating clarification ("Say again?", "What do you need?") instead of routing to LLM chat. Suppresses proactive greetings and RAG flush.
- **`<DefaultTacticalMode>`** config option — set `true` in `modconfig.xml` to start all hires in tactical mode (applied once per player per session; "at ease" then sticks).

### Formation System
- **World-anchored cardinal sockets** — `cover north`, `hold south`, `watch east`, `cover west`. Sockets reference map compass directions (not leader heading) — NPCs stay on their side when you turn.
- **Adjacency allocator** — when a second NPC is ordered to an occupied cardinal, it auto-spreads to the nearest free diagonal. "Billy, cover north" then "Sam, cover north" → N then NE.
- **Squad fan-out** — `squad, cover north` spreads the squad across N/NE/NW automatically.
- **Unified mode-aware distance** — one vocabulary ("come closer", "back off", "normal distance") works in both plain follow and formation mode. The system knows your mode and adjusts the right value.

### Combat
- **Engagement model** — `weapons free` (free-fight), `cover me`/`defend only` (assist-only default), `disengage`/`break off` (passive, clears targets + cancels formation).
- **Live crouch** — `kneel`/`get down`/`stand up` — SCore ships the CrouchOverride hook at both follow and idle chokepoints.

### Memory
- **Improved long-term memory** — global player identity (same memories across NPCs sharing your player ID), minimum flush threshold so short conversations don't waste extraction cycles, junk-fact filter removes placeholder contamination, and high-importance memories persist longer than casual facts.

### Settings
- **Hired chat distance** increased from 30m to 40m maximum.

### Bug Fixes
- TTS permanent-silence bug (queue hang after one response)
- Mid-sentence audio cutoffs (watchdog timeouts added)
- GiveItem over-removal from NPC stock
- RAG memory contamination (few-shot examples leaking as real facts)
- Streaming subtitle showed literal action tags (`[follow]`, `[trade]`)
- Asterisk emotes (`*shakes head*`) showing in subtitles
- First-interaction RAG delay (unrelated NPC backlog blocking active chat)
- Various dialogue cleaning and emote stripping fixes
