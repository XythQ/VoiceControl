# Models — download manifest

This repo is the **complete runnable mod** except for **5 model files** that exceed GitHub's 100 MB
limit. Download each from this repo's **[Releases](../../releases)** page and drop it at the target path
below (paths are relative to the mod root). Everything else — the sidecar servers, DLLs, config — is
already here.

> Verify each file's SHA256 before use. All five are hosted as **GitHub Release assets** on this repo.

| Component | Target path | Size | Release asset | SHA256 |
|---|---|---|---|---|
| **LLM** (chat) | `Resources/Models/Llama/Llama-3.2-3B-Instruct-IQ4_NL.gguf` | 1.8 G | `<TODO: release URL>` | `<TODO>` |
| **STT** (whisper) | `bin/WhisperServer/ggml-small.bin` | 465 M | `<TODO>` | `<TODO>` |
| **TTS** (Kokoro — wake-word) | `Resources/KokoroSherpa/model.onnx` | 311 M | `<TODO>` | `<TODO>` |
| **TTS** (Supertonic — default) | `Resources/Supertonic/onnx/vector_estimator.onnx` | 245 M | `<TODO>` | `<TODO>` |
| **Embeddings** (RAG) | `Resources/Models/Embed/nomic-embed-text-v1.5.Q8_0.gguf` | 139 M | `<TODO>` | `<TODO>` |

## Notes

- **All five are required** for full functionality (LLM chat, speech-to-text, both TTS engines, and RAG
  memory). Kokoro is needed for the current wake-word system; Supertonic is the default speaking voice.
- **LLM is folder-autodetected** — any single `.gguf` in `Resources/Models/Llama/` is used; the name above
  is what ships by default. Low-VRAM (6 GB) cards: keep the `IQ4_NL` quant + `ContextSize=4096` in
  `Config/modconfig.xml`.
- Once all five are in place, copy the mod folder to `…/7 Days To Die/Mods/1-XNPCVoiceControl/` and launch —
  the sidecar servers auto-start on first use.

## For the maintainer (uploading a new release)

Attach the five files to the GitHub Release, then fill the `<TODO>` URLs + `sha256sum` values above. These
assets don't count against repo size or LFS bandwidth.
