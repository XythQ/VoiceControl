# Model Resources
#
These model files are not included in the build output.
Copy them from your local model cache into this directory:
#
## Resources/KokoroSherpa/ (TTS)
#   model.onnx       - Kokoro-82M-v1.0 multi-language TTS (~325MB)
#   voices.bin       - Voice speaker embeddings (~26MB)
#   tokens.txt       - Token vocabulary
#   lexicon-us-en.txt, lexicon-gb-en.txt, lexicon-zh.txt
#   dict/            - Japanese phonemizer data
#   espeak-ng-data/  - eSpeak NG data
#
## Resources/WakeWord/models/ (wake-word detection)
#   embedding_model.onnx    - Embedding extractor
#   hey_marvin_v0.1.onnx    - "Hey Marvin" wake word model
#   melspectrogram.onnx     - Mel spectrogram preprocessor
#
## Resources/Models/Llama/ (LLM)
#   Llama-3.2-3B-Instruct-IQ4_NL.gguf  - GGUF chat model (~1.8GB)
#
## Resources/Models/Embed/ (RAG embeddings)
#   nomic-embed-text-v1.5.Q8_0.gguf  - GGUF embedding model (~139MB)
