using System.IO;
using XNPCVoiceControl.Core;
using UnityEngine;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Custom network packet for relaying NPC voice audio across clients.
    /// Carries the final WAV byte array from Kokoro TTS so all players in earshot hear the NPC speak.
    ///
    /// 7DTD auto-discovers this class via reflection at startup (inherits NetPackage, has parameterless ctor).
    /// No manual registration required.
    /// </summary>
    public class NetPackageNPCVoice : NetPackage
    {
        // --- Fields ---

        private int _npcEntityId;
        private byte[] _wavData;
        private float _volume;

        /// <summary>
        /// Parameterless constructor — REQUIRED for 7DTD reflection-based packet instantiation.
        /// </summary>
        public NetPackageNPCVoice() { }

        /// <summary>
        /// Create a new voice packet from WAV data (client-side send).
        /// </summary>
        public NetPackageNPCVoice(int npcEntityId, byte[] wavData, float volume)
        {
            _npcEntityId = npcEntityId;
            _wavData = wavData;
            _volume = volume;
        }

        // --- NetPackage Overrides ---

        public override void read(PooledBinaryReader _br)
        {
            _npcEntityId = _br.ReadInt32();
            int length = _br.ReadInt32();
            // Cast to base BinaryReader to avoid PooledBinaryReader's ReadOnlySpan overloads
            _wavData = length > 0 ? ((System.IO.BinaryReader)_br).ReadBytes(length) : new byte[0];
            _volume = _br.ReadSingle();
        }

        public override void write(PooledBinaryWriter _bw)
        {
            base.write(_bw);
            // Cast to base BinaryWriter to avoid ALL PooledBinaryWriter ReadOnlySpan overloads
            var bw = (System.IO.BinaryWriter)_bw;
            bw.Write(_npcEntityId);
            int len = _wavData != null ? _wavData.Length : 0;
            bw.Write(len);
            if (_wavData != null && len > 0)
                bw.Write(_wavData, 0, len);
            bw.Write(_volume);
        }

        public override int GetLength()
        {
            // 2 (base package-ID header) + 4 (npcEntityId) + 4 (array length) + wav bytes + 4 (volume)
            return 14 + (_wavData != null ? _wavData.Length : 0);
        }

        /// <summary>
        /// Fires on whoever receives the packet. Determines context and acts accordingly.
        /// </summary>
        public override void ProcessPackage(World _world, GameManager _callbacks)
        {
            // Engine auto-populates this.Sender before ProcessPackage runs
            int senderEntityId = Sender != null ? Sender.entityId : -1;

            bool isServer = SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;

            if (isServer)
            {
                // --- SERVER SIDE: relay to all other clients in range ---
                EntityAlive npc = _world?.GetEntity(_npcEntityId) as EntityAlive;
                if (npc == null) return;

                float maxDistance = XNPCVoiceControlMod.GetVoiceDistance();

                // Relay to all clients except the original sender
                SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                    this,                     // 1. package
                    false,                    // 2. bServer (relay to clients)
                    -1,                       // 3. attachedToEntityId
                    (int)maxDistance,         // 4. distance
                    senderEntityId,           // 5. ignoreEntityId (excludes origin client)
                    null,                     // 6. position (Vector3?)
                    -1                        // 7. playRadius
                );

                // Listen server: host is also playing locally — play audio here too
                if (!GameManager.IsDedicatedServer)
                {
                    PlayVoiceLocally(_npcEntityId, _wavData, _volume);
                }
            }
            else
            {
                // --- CLIENT SIDE: just play the audio ---
                PlayVoiceLocally(_npcEntityId, _wavData, _volume);
            }
        }

        // --- Audio Playback (main-thread marshaled) ---

        /// <summary>
        /// Play the received WAV audio at the NPC's position.
        /// Marshals AudioClip creation to Unity's main thread via MainThreadDispatcher.
        /// </summary>
        private static void PlayVoiceLocally(int npcEntityId, byte[] wavData, float volume)
        {
            EntityAlive npc = GameManager.Instance?.World?.GetEntity(npcEntityId) as EntityAlive;
            if (npc == null) return;

            // Phase 1: background-safe PCM conversion (before main thread marshal)
            var (audioData, sampleRate, channels) = AudioUtils.ProcessWavBytes(wavData);

            // Phase 2: main-thread AudioClip creation + playback
            MainThreadDispatcher.Enqueue(() =>
            {
                try
                {
                    AudioClip clip = AudioUtils.CreateClipFromData(audioData, sampleRate, channels, "NPCVoice");
                    if (clip == null) return;

                    AudioSource source = npc.GetComponent<AudioSource>();
                    if (source == null)
                        source = npc.gameObject.AddComponent<AudioSource>();

                    // Configure spatial audio to match TTS settings
                    source.spatialBlend = 1f;                       // Full 3D
                    source.rolloffMode = AudioRolloffMode.Logarithmic;
                    source.maxDistance = XNPCVoiceControlMod.GetVoiceDistance();
                    source.minDistance = XNPCVoiceControlMod.TTSConfig?.MinDistance ?? 2f;

                    source.clip = clip;
                    source.volume = volume;
                    source.Play();

                    // Clips are native-memory objects and are NOT garbage-collected while alive;
                    // schedule destruction after playback finishes (SP paths do the equivalent).
                    UnityEngine.Object.Destroy(clip, clip.length + 0.5f);
                }
                catch (System.Exception ex)
                {
                    Log.Error($"Failed to play relayed voice: {ex.Message}");
                }
            });
        }

    }
}
