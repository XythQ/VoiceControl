using System;
using UnityEngine;

namespace XNPCVoiceControl.Core
{
    /// <summary>
    /// Centralized WAV parsing and AudioClip creation.
    /// 
    /// Two-phase architecture to avoid main-thread stutter:
    ///   Phase 1 (background-safe): ProcessWavBytes — converts byte[] → float[], no Unity APIs
    ///   Phase 2 (main thread only): CreateClipFromData — instantiates AudioClip from float[]
    /// </summary>
    public static class AudioUtils
    {
        // ========================================================================
        // Phase 1: Background-safe PCM extraction (no Unity APIs)
        // ========================================================================

        /// <summary>
        /// Convert raw WAV bytes to a float32 audio array. Safe to call from background threads.
        /// Returns (audioData, sampleRate, channels).
        /// 
        /// Three-strategy fallback cascade:
        ///   1. Strict RIFF+WAVE header — reads sample rate and channels from header
        ///   2. Lenient "data" chunk search — assumes 24kHz mono
        ///   3. Raw PCM fallback — assumes 24kHz mono
        /// </summary>
        public static (float[] audioData, int sampleRate, int channels) ProcessWavBytes(byte[] rawBytes)
        {
            if (rawBytes == null || rawBytes.Length == 0)
                throw new ArgumentException("WAV bytes is null or empty", nameof(rawBytes));

            // Strategy 1: Strict RIFF+WAVE header parsing
            if (TryParseRiffWave(rawBytes, out int s1Channels, out int s1SampleRate, out int s1DataIndex))
            {
                return ExtractPcm(rawBytes, s1DataIndex, s1Channels, s1SampleRate);
            }

            // Strategy 2: Lenient — brute-force "data" chunk search
            int dataChunk = FindDataChunk(rawBytes, 0);
            if (dataChunk > 0)
            {
                Log.Warning($"[AudioUtils] WAV header malformed, using brute-force data chunk search (24kHz mono fallback)");
                return ExtractPcm(rawBytes, dataChunk, 1, 24000);
            }

            // Strategy 3: Raw PCM — no header at all
            Log.Warning($"[AudioUtils] No WAV header or data chunk found, treating entire buffer as raw PCM (24kHz mono)");
            return ExtractPcm(rawBytes, 0, 1, 24000);
        }

        private static bool TryParseRiffWave(byte[] data, out int channels, out int sampleRate, out int dataIndex)
        {
            channels = 0;
            sampleRate = 0;
            dataIndex = -1;

            if (data.Length < 44) return false;
            if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return false;
            if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E') return false;

            channels = data[22];
            sampleRate = BitConverter.ToInt32(data, 24);
            int bitsPerSample = BitConverter.ToInt16(data, 34);

            if (channels <= 0 || sampleRate <= 0 || bitsPerSample != 16) return false;

            dataIndex = FindDataChunk(data, 12);
            return dataIndex >= 0;
        }

        private static int FindDataChunk(byte[] data, int startOffset)
        {
            for (int i = startOffset; i < data.Length - 3; i++)
            {
                if (data[i] == 'd' && data[i + 1] == 'a' && data[i + 2] == 't' && data[i + 3] == 'a')
                    return i + 8; // skip "data" label + 4-byte chunk size
            }
            return -1;
        }

        private static (float[] audioData, int sampleRate, int channels) ExtractPcm(byte[] wavBytes, int dataIndex, int channels, int sampleRate)
        {
            int samples = (wavBytes.Length - dataIndex) / 2;
            if (samples <= 0)
                throw new ArgumentException($"No PCM samples extracted (dataIndex={dataIndex}, totalBytes={wavBytes.Length})");

            float[] audioData = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                short sample = BitConverter.ToInt16(wavBytes, dataIndex + (i * 2));
                audioData[i] = sample / 32768f;
            }

            return (audioData, sampleRate, channels);
        }

        // ========================================================================
        // Phase 2: Main-thread AudioClip creation (Unity API)
        // ========================================================================

        /// <summary>
        /// Create a Unity AudioClip from pre-converted float data.
        /// MUST be called on the main thread (AudioClip.Create is a Unity API).
        /// Call this from ThreadManager.AddSingleTaskMainThread or a coroutine.
        /// </summary>
        public static AudioClip CreateClipFromData(float[] audioData, int sampleRate, int channels, string clipName = "TTSAudio")
        {
            if (audioData == null || audioData.Length == 0) return null;

            AudioClip clip = AudioClip.Create(clipName, audioData.Length, channels, sampleRate, false);
            if (clip == null) return null;
            clip.SetData(audioData, 0);
            return clip;
        }
    }
}
