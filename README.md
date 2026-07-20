# XNPC Voice Control

A voice-command and AI-conversation mod for **7 Days to Die** (built on **0-SCore** / NPCCore). Talk to
your hired NPCs by voice — squad and positioning commands, or a real conversation powered by a **local**
LLM, speech-to-text, and text-to-speech (no cloud, no API keys).

> **This repo is the complete, runnable mod** — sidecar servers, DLLs, and config included. The only thing
> not in git is **5 large model files** (>100 MB) — grab them from **[Releases](../../releases)** per
> **[MODELS.md](MODELS.md)**.

## Features (v3.0.x)

- **Voice commands:** follow / stay / guard / patrol; formation positioning (`cover/hold/watch
  north|south|east|west`) with an auto-spreading squad allocator; follow-distance; commanded crouch;
  engage/disengage.
- **Tactical mode:** command-only mode (voice toggle) — deterministic, no chat, clarify-on-miss.
- **Local AI conversation:** LLM chat with per-NPC personality + RAG memory; local STT (whisper) and TTS
  (Supertonic + Kokoro).
- **Single-player + dedicated-server** support.

## Install (run it)

1. **Clone** this repo (or download the ZIP).
2. **Download the 5 models** from **[Releases](../../releases)** and place each at its path — see
   **[MODELS.md](MODELS.md)**.
3. **Copy the whole folder** into `…\7 Days To Die\Mods\1-XNPCVoiceControl\`.
4. Ensure **0-SCore** and **0_TFP_Harmony** are also installed as mods.
5. Launch — the mod auto-starts the local STT/TTS/LLM servers on first use.

**Requirements:** Windows, a mic, a GPU with **≥6 GB VRAM** (8 GB+ recommended).

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
