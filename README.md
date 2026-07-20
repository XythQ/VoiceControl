# XNPC Voice Control

A voice-command and AI-conversation mod for **7 Days to Die** (built on **0-SCore** / NPCCore). Talk to
your hired NPCs by voice — squad and positioning commands, or a real conversation powered by a **local**
LLM, speech-to-text, and text-to-speech (no cloud, no API keys).

> **This repo is the complete, runnable mod** — sidecar servers, DLLs, and config included. You need to add an ai model in .gguf format into the mods resources/Llama/Models folder. A small model suitable for a 6 gig vram gpu is an additional download file in this repo.  Better AI is recommended for better chats. 

## Features (v3.0.x)

- **Voice commands:** follow / stay / guard / patrol; formation positioning (`cover/hold/watch
  north|south|east|west`) with an auto-spreading squad allocator; follow-distance; commanded crouch;
  engage/disengage.
- **Tactical mode:** command-only mode (voice toggle) — deterministic, no chat, clarify-on-miss.
- **Local AI conversation:** LLM chat with per-NPC personality + RAG memory; local STT (whisper) and TTS
  (Supertonic + Kokoro).
- **Single-player + dedicated-server** support.

## Build from source (optional)

The compiled mod DLL ships in this repo, so you only need this to modify it.

1. Set the `GamePath` MSBuild property (or env var) to your install, e.g.
   `C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die`.
2. `NPCVoiceControl.csproj` derives the game/Harmony reference paths from `GamePath`; `SCorePath` defaults
   to the standard Steam `Mods\0-SCore` — edit that one line if yours differs.
3. Build `NPCVoiceControl.sln` (Release, .NET Framework 4.8) → outputs `1-XNPCVoiceControl.dll`.

## Docs

`Config/` (all mod XML), `NPCVoiceControl/Docs/COMMAND-REFERENCE.md` (voice-command list),
`ARCHITECTURE.md`, `CHANGELOG.md`.

## Credits & License

Built on SphereII's **0-SCore** / NPCCore. See `LICENSE`.
