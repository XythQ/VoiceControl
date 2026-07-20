using System;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using XNPCVoiceControl.Core;
using UnityEngine;

namespace XNPCVoiceControl.TTS
{
    /// <summary>
    /// Audio player component for NPC speech.
    /// Receives WAV file paths from TTSService, loads them via UnityWebRequestMultimedia,
    /// and plays through a 3D AudioSource with spatial audio.
    /// </summary>
    public class NPCAudioPlayer : MonoBehaviour
    {
        private AudioSource _audioSource;
        private EntityAlive _npcEntity;
        private TTSConfig _config;
        private string _maleVoice;
        private string _femaleVoice;
        private string _genderTag;
        private string _ttsLanguage = "en";  // ISO language code (en=American, ja=Japanese, etc.)
        private bool _isPlaying = false;

        // Streaming TTS: queue sentences for back-to-back playback
        private Queue<string> _speechQueue = new Queue<string>();
        private bool _draining = false;
        private Coroutine _drainCoroutine;

        // Global active-voice-interaction counter (thread-safe, used by RAG gate)
        private static int _activeVoiceInteractions = 0;
        public static bool IsAnySpeaking => Interlocked.CompareExchange(ref _activeVoiceInteractions, 0, 0) > 0;

        public void Initialize(EntityAlive npcEntity, TTSConfig config, string maleVoice, string femaleVoice, string genderTag)
        {
            Initialize(npcEntity, config, maleVoice, femaleVoice, genderTag, "en");
        }

        public void Initialize(EntityAlive npcEntity, TTSConfig config, string maleVoice, string femaleVoice, string genderTag, string ttsLanguage)
        {
            _npcEntity = npcEntity;
            _config = config;
            _maleVoice = maleVoice;
            _femaleVoice = femaleVoice;
            _genderTag = genderTag;
            _ttsLanguage = !string.IsNullOrEmpty(ttsLanguage) ? ttsLanguage : "en";

            // Create AudioSource for 3D spatial audio
            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.spatialBlend = 1f;  // Full 3D
            _audioSource.minDistance = config.MinDistance;
            _audioSource.maxDistance = config.MaxDistance;
            _audioSource.volume = config.Volume;
            _audioSource.playOnAwake = false;
            _audioSource.loop = false;

            // Bypass game audio effects for clear speech
            _audioSource.bypassEffects = true;
            _audioSource.bypassListenerEffects = true;
            _audioSource.bypassReverbZones = true;
            _audioSource.priority = 128;  // Normal priority
        }

        /// <summary>
        /// Speak text using KokoroSharp TTS.
        /// TTSService synthesizes on a background thread, returns raw WAV bytes,
        /// which are converted in-memory to an AudioClip and played via AudioSource.
        /// </summary>
        public void Speak(string text)
        {
            Speak(text, null);
        }

        /// <summary>
        /// Speak text with an optional completion callback.
        /// </summary>
        public void Speak(string text, System.Action onComplete)
        {
            if (_isPlaying)
            {
                // Stop current playback and start new speech
                Stop();
            }

            if (string.IsNullOrWhiteSpace(text))
                return;

            // Select voice based on gender
            string voiceName = SelectVoice();

            Log.Debug(() => $"NPCAudioPlayer.Speak: '{text.Substring(0, Math.Min(40, text.Length))}...' voice={voiceName} lang={_ttsLanguage}");
            Log.Out($"[VOICE] {_npcEntity?.EntityName ?? "Unknown"} ({_genderTag}) → {voiceName} [lang:{_ttsLanguage}]");

            TTSService.Instance.Synthesize(
                text,
                voiceName,
                _ttsLanguage,
                wavBytes =>
                {
                    OnWavBytesReady(wavBytes, onComplete);
                },
                error =>
                {
                    Log.Warning($"TTS synthesis failed: {error}");
                    _isPlaying = false;
                    onComplete?.Invoke();
                }
            );
        }

        /// <summary>
        /// Called when TTSService finishes synthesis and provides raw WAV bytes.
        /// Converts WAV to AudioClip in-memory (bypasses FMOD file loading entirely).
        /// </summary>
        private void OnWavBytesReady(byte[] wavBytes, System.Action onComplete)
        {
            if (_npcEntity == null || !_npcEntity.IsAlive())
            {
                _isPlaying = false;
                onComplete?.Invoke();
                return;
            }

            var (audioData, sampleRate, channels) = AudioUtils.ProcessWavBytes(wavBytes);
            AudioClip clip = AudioUtils.CreateClipFromData(audioData, sampleRate, channels);
            if (clip == null)
            {
                Log.Warning("Failed to convert WAV bytes to AudioClip");
                _isPlaying = false;
                onComplete?.Invoke();
                return;
            }

            Log.Debug(() => $"Loaded AudioClip: {clip.name}, {clip.length:F2}s, {clip.samples} samples");

            // Play through AudioSource
            PlayClip(clip, onComplete);
        }



        /// <summary>
        /// Set the double-speak guard CVar on the NPC.
        /// </summary>
        private void BeginAudio()
        {
            Interlocked.Increment(ref _activeVoiceInteractions);
            if (_npcEntity != null) _npcEntity.Buffs.SetCustomVar("npcVoiceActive", 1f);
        }

        /// <summary>
        /// Clear the double-speak guard CVar on the NPC.
        /// </summary>
        private void EndAudio()
        {
            Interlocked.Decrement(ref _activeVoiceInteractions);
            if (_npcEntity != null) _npcEntity.Buffs.SetCustomVar("npcVoiceActive", 0f);
        }

        /// <summary>
        /// Play an AudioClip through the AudioSource, then cleanup on completion.
        /// </summary>
        private void PlayClip(AudioClip clip, System.Action onComplete)
        {
            if (_audioSource == null)
            {
                Log.Warning("AudioSource is null");
                return;
            }

            BeginAudio();
            _audioSource.clip = clip;
            _audioSource.volume = _config?.Volume ?? 0.8f;
            _audioSource.Play();
            _isPlaying = true;

            Log.Debug(() => $"[TTS-TIMING] Playback START t={Time.realtimeSinceStartup:F2} len={clip.length:F2}s");

            // Schedule cleanup after playback finishes
            StartCoroutine(WaitForPlaybackFinish(clip, onComplete));
        }

        /// <summary>
        /// Play a cached voice clip from VoiceClipLoader. Does NOT destroy the clip on finish - the cache owns it.
        /// </summary>
        public void PlayClipFile(XNPCVoiceControl.Core.ClipEntry entry, System.Action onComplete)
        {
            if (_isPlaying) Stop();
            XNPCVoiceControl.Core.VoiceClipLoader.GetClip(entry, clip =>
            {
                if (clip == null || _npcEntity == null || !_npcEntity.IsAlive())
                { _isPlaying = false; onComplete?.Invoke(); return; }

                BeginAudio();
                _audioSource.clip = clip;
                _audioSource.volume = _config?.Volume ?? 0.8f;
                _audioSource.Play();
                _isPlaying = true;
                StartCoroutine(WaitForClipFinishNoDestroy(clip, onComplete));
            });
        }

        private System.Collections.IEnumerator WaitForClipFinishNoDestroy(AudioClip clip, System.Action onComplete)
        {
            yield return new WaitForSeconds((float)clip.length);
            if (_audioSource != null && _audioSource.isPlaying) _audioSource.Stop();
            _isPlaying = false;
            EndAudio();
            // NOTE: do NOT Destroy(clip) - the VoiceClipLoader cache owns it
            onComplete?.Invoke();
        }

        /// <summary>
        /// Wait for the current clip to finish playing, then destroy it and fire callback.
        /// </summary>
        private System.Collections.IEnumerator WaitForPlaybackFinish(AudioClip clip, System.Action onComplete)
        {
            yield return new WaitForSeconds((float)clip.length);
            Log.Debug(() => $"[TTS-TIMING] Playback FINISHED naturally t={Time.realtimeSinceStartup:F2} len={clip.length:F2}s");

            // Cleanup
            if (_audioSource != null && _audioSource.isPlaying)
                _audioSource.Stop();

            EndAudio();
            UnityEngine.Object.Destroy(clip);
            _isPlaying = false;
            onComplete?.Invoke();
        }

        /// <summary>
        /// Stop current playback.
        /// </summary>
        public void Stop()
        {
            if (_audioSource != null)
            {
                _audioSource.Stop();
            }
            EndAudio();
            _isPlaying = false;
        }

        /// <summary>
        /// Alias for Stop() - used by NPCChatComponent.
        /// </summary>
        public void StopSpeaking(string reason = "unspecified")
        {
            if (_isPlaying && _audioSource != null && _audioSource.clip != null)
            {
                float pos = _audioSource.time;
                float len = _audioSource.clip.length;
                Log.Debug(() => $"[TTS-TIMING] StopSpeaking(\"{reason}\") t={Time.realtimeSinceStartup:F2} — CUT OFF mid-clip at {pos:F2}/{len:F2}s ({(len > 0 ? pos / len * 100f : 0):F0}%), queueDepth={_speechQueue.Count}");
            }
            else
            {
                Log.Debug(() => $"[TTS-TIMING] StopSpeaking(\"{reason}\") t={Time.realtimeSinceStartup:F2} — not mid-playback, queueDepth={_speechQueue.Count}");
            }
            _speechQueue.Clear();
            if (_drainCoroutine != null)
            {
                StopCoroutine(_drainCoroutine);
                _drainCoroutine = null;
            }
            _draining = false;
            Stop();
        }

        /// <summary>
        /// Queue a text chunk for TTS playback. If not already draining the queue, starts immediately.
        /// Chunks play back-to-back with minimal gap (one coroutine frame between clips).
        /// </summary>
        public void EnqueueSpeech(string text, Action onAllDone = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _speechQueue.Enqueue(text);
            if (!_draining)
                _drainCoroutine = StartCoroutine(DrainSpeechQueue(onAllDone));
        }

        private IEnumerator DrainSpeechQueue(Action onAllDone)
        {
            _draining = true;

            // Pipeline: synthesize sentence N+1 WHILE sentence N is still playing, shrinking the
            // inter-sentence gap from the full synth latency to max(0, synth(N+1) - playback(N)).
            // Only WAV bytes are buffered (one slot ahead); AudioClip creation + playback stay
            // strictly sequential on the main thread (one clip alive at a time). This only ever
            // moves synthesis EARLIER and never delays playback, so a fast engine (Supertonic,
            // already smooth) is unaffected while a slower engine (Kokoro) loses its gaps.
            SynthSlot current = StartSynth(_speechQueue.Count > 0 ? _speechQueue.Dequeue() : null);

            while (current != null)
            {
                // Wait for the current sentence's synthesis to finish (success or failure).
                // Watchdog: if the TTS server dies or the callback is lost, don't spin forever
                // and leave _draining permanently stuck true (kills all future TTS silently).
                float synthDeadline = Time.realtimeSinceStartup + 15f;
                while (!current.Ready)
                {
                    if (!_draining) yield break;   // interrupted
                    if (Time.realtimeSinceStartup > synthDeadline)
                    {
                        Log.Warning("[TTS] Synthesis callback never fired - watchdog timeout (15s), marking as failed");
                        current.Failed = true;
                        current.Ready = true;
                        break;
                    }
                    yield return null;
                }
                if (!_draining) yield break;

                // Kick off the NEXT sentence's synthesis now so it overlaps current playback.
                SynthSlot next = StartSynth(_speechQueue.Count > 0 ? _speechQueue.Dequeue() : null);

                // Play the current sentence. A failed synth is skipped without stalling the queue.
                if (!current.Failed && current.Bytes != null)
                {
                    bool played = false;
                    try
                    {
                        OnWavBytesReady(current.Bytes, () => played = true);
                    }
                    catch (System.Exception ex)
                    {
                        Log.Warning($"[TTS] OnWavBytesReady threw: {ex.Message} - skipping sentence");
                        played = true;  // don't hang on broken audio
                    }

                    // Watchdog: WaitForPlaybackFinish is a separate untracked coroutine.
                    // If it fails to fire, cap the wait at estimated clip duration + 3s buffer.
                    float clipSeconds = current.Bytes.Length / 32000f;
                    float playDeadline = Time.realtimeSinceStartup + clipSeconds + 3f;
                    while (!played)
                    {
                        if (!_draining) yield break;   // interrupted mid-playback
                        if (Time.realtimeSinceStartup > playDeadline)
                        {
                            Log.Warning($"[TTS] Playback-finished callback never fired - watchdog timeout ({clipSeconds:F1}s clip), forcing queue to continue");
                            break;
                        }
                        yield return null;
                    }
                }

                current = next;
            }

            _draining = false;
            _drainCoroutine = null;
            onAllDone?.Invoke();
        }

        private SynthSlot StartSynth(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            // Pre-strip emotes to detect emote-only text before starting synthesis.
            // If TTSService.Synthesize returns early (empty after strip), the callback
            // never fires and the drain loop hangs on the 15s watchdog timeout.
            string cleanText = TTSService.StripEmotesForTts(text);
            if (string.IsNullOrWhiteSpace(cleanText))
            {
                // Emote-only text — nothing to synthesize, complete immediately
                var emptySlot = new SynthSlot { Ready = true, Failed = true };
                return emptySlot;
            }
            var slot = new SynthSlot();
            string voiceName = SelectVoice();
            TTSService.Instance.Synthesize(
                text,
                voiceName,
                _ttsLanguage,
                wavBytes => { slot.Bytes = wavBytes; slot.Ready = true; },
                error => { Log.Warning($"TTS synthesis failed: {error}"); slot.Failed = true; slot.Ready = true; }
            );
            return slot;
        }

        private sealed class SynthSlot
        {
            public byte[] Bytes;
            public bool Ready;
            public bool Failed;
        }

        /// <summary>
        /// Check if currently speaking.
        /// </summary>
        public bool IsSpeaking => _isPlaying;

        /// <summary>
        /// Set voice override (used by NPCChatComponent for personality voices).
        /// </summary>
        public void SetVoice(string voice)
        {
            _maleVoice = voice;
            _femaleVoice = voice;
        }

        /// <summary>
        /// Set separate male/female voice overrides.
        /// </summary>
        public void SetVoice(string maleVoice, string femaleVoice)
        {
            _maleVoice = maleVoice;
            _femaleVoice = femaleVoice;
        }

        /// <summary>
        /// Update the audio source's max distance at runtime (called from config save).
        /// </summary>
        public void UpdateAudioRange(float maxDistance)
        {
            if (_audioSource != null)
            {
                _audioSource.maxDistance = maxDistance;
            }
            if (_config != null)
            {
                _config.MaxDistance = maxDistance;
            }
        }

        /// <summary>
        /// Select the appropriate voice based on NPC gender.
        /// </summary>
        private string SelectVoice()
        {
            if (_genderTag == "male" && !string.IsNullOrEmpty(_maleVoice))
                return _maleVoice;
            if (_genderTag == "female" && !string.IsNullOrEmpty(_femaleVoice))
                return _femaleVoice;

            // Fallback: use whichever voice is defined
            if (!string.IsNullOrEmpty(_maleVoice))
                return _maleVoice;
            if (!string.IsNullOrEmpty(_femaleVoice))
                return _femaleVoice;

            // Final fallback: config default
            return _config?.DefaultVoice ?? "af_aoede";
        }

        private const int _minSentenceLength = 30;

        /// <summary>
        /// Split text into sentences on .!? boundaries, mirroring LLMService.cs boundary logic.
        /// Fragments shorter than _minSentenceLength are merged with the previous sentence.
        /// Trailing text with no final punctuation becomes the last sentence.
        /// </summary>
        private static List<string> SplitIntoSentences(string text)
        {
            var sentences = new List<string>();
            int start = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '.' || text[i] == '!' || text[i] == '?')
                {
                    // Include trailing whitespace in the boundary
                    int end = i + 1;
                    while (end < text.Length && char.IsWhiteSpace(text[end])) end++;

                    string candidate = text.Substring(start, end - start).Trim();
                    if (!string.IsNullOrEmpty(candidate))
                    {
                        // Merge tiny fragments into the previous sentence
                        if (candidate.Length < _minSentenceLength && sentences.Count > 0)
                        {
                            sentences[sentences.Count - 1] = sentences[sentences.Count - 1].TrimEnd() + " " + candidate;
                        }
                        else
                        {
                            sentences.Add(candidate);
                        }
                    }
                    start = end;
                }
            }

            // Remaining text after last punctuation
            if (start < text.Length)
            {
                string trailing = text.Substring(start).Trim();
                if (!string.IsNullOrEmpty(trailing))
                {
                    if (trailing.Length < _minSentenceLength && sentences.Count > 0)
                    {
                        sentences[sentences.Count - 1] = sentences[sentences.Count - 1].TrimEnd() + " " + trailing;
                    }
                    else
                    {
                        sentences.Add(trailing);
                    }
                }
            }

            return sentences;
        }

        /// <summary>
        /// Split text into sentences and enqueue each for streaming playback.
        /// Single-sentence input just enqueues once (no behavior change).
        /// onComplete fires when the entire queue drains.
        /// </summary>
        public void SpeakStreaming(string text, Action onComplete = null)
        {
            if (string.IsNullOrWhiteSpace(text)) { onComplete?.Invoke(); return; }

            // Proper reset when interrupting an in-progress stream (clears queue + _draining)
            if (_isPlaying || _draining) StopSpeaking();

            var sentences = SplitIntoSentences(text);
            if (sentences.Count == 0) { onComplete?.Invoke(); return; }

            // Enqueue ALL sentences first, then start ONE drain carrying the completion callback.
            // (EnqueueSpeech only honors its callback on the call that starts draining, so the
            //  previous loop dropped onComplete for any 2+ sentence reply.)
            foreach (var s in sentences)
                if (!string.IsNullOrWhiteSpace(s)) _speechQueue.Enqueue(s);

            if (!_draining)
                _drainCoroutine = StartCoroutine(DrainSpeechQueue(onComplete));
        }

        /// <summary>
        /// Cleanup on destroy.
        /// </summary>
        private void OnDestroy()
        {
            Stop();
        }
    }
}
