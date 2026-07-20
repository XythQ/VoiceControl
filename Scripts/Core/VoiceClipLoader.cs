using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using XNPCVoiceControl;

namespace XNPCVoiceControl.Core
{
    /// <summary>
    /// Loads loose OGG files into AudioClip on demand, caching results.
    /// Cached clips are never Destroyed - the cache owns them.
    /// OGG only - no WAV, no MP3, no bundle branch.
    /// </summary>
    public static class VoiceClipLoader
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, AudioClip> _cache = new Dictionary<string, AudioClip>();
        private static readonly Dictionary<string, List<Action<AudioClip>>> _inFlight = new Dictionary<string, List<Action<AudioClip>>>();

        /// <summary>
        /// Load the clip for the given entry and call onReady with the result.
        /// If already cached, onReady is called synchronously.
        /// On load failure, onReady(null) is called.
        /// </summary>
        public static void GetClip(ClipEntry entry, Action<AudioClip> onReady)
        {
            if (entry == null)
            {
                onReady?.Invoke(null);
                return;
            }

            List<Action<AudioClip>> callbacks = null;
            lock (_lock)
            {
                if (_cache.TryGetValue(entry.AudioPath, out var cached))
                {
                    onReady?.Invoke(cached);
                    return;
                }

                if (_inFlight.TryGetValue(entry.AudioPath, out var waiting))
                {
                    waiting.Add(onReady);
                    return;
                }

                callbacks = new List<Action<AudioClip>> { onReady };
                _inFlight[entry.AudioPath] = callbacks;
            }

            // Not cached - load via UnityWebRequest on the main thread host
            ServerManagerHost.Instance.StartCoroutine(LoadOggRoutine(entry, callbacks));
        }

        private static IEnumerator LoadOggRoutine(ClipEntry entry, List<Action<AudioClip>> callbacks)
        {
            string url = new System.Uri(entry.AudioPath).AbsoluteUri;

            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.OGGVORBIS))
            {
                yield return req.SendWebRequest();

                AudioClip clip = null;
                bool success = false;

                if (req.result == UnityWebRequest.Result.Success)
                {
                    clip = DownloadHandlerAudioClip.GetContent(req);
                    if (clip != null)
                    {
                        success = true;
                    }
                    else
                    {
                        Log.Warning($"VoiceClipLoader: returned null AudioClip for '{entry.AudioPath}'");
                    }
                }
                else
                {
                    Log.Warning($"VoiceClipLoader: failed to load '{entry.AudioPath}' - {req.error}");
                }

                List<Action<AudioClip>> allCallbacks;
                lock (_lock)
                {
                    if (success)
                    {
                        _cache[entry.AudioPath] = clip;
                    }
                    _inFlight.TryGetValue(entry.AudioPath, out allCallbacks);
                    _inFlight.Remove(entry.AudioPath);
                }

                // Invoke callbacks outside the lock
                if (allCallbacks != null)
                {
                    foreach (var cb in allCallbacks)
                        cb?.Invoke(success ? clip : (AudioClip)null);
                }
            }
        }
    }
}
