using System.IO;

namespace XNPCVoiceControl.Net
{
    /// <summary>
    /// Version handshake packet. Client sends build identity on spawn; server compares.
    /// 7DTD auto-discovers this class via reflection at startup (inherits NetPackage, has parameterless ctor).
    /// ProcessPackage: server-only (ignore if received on a client).
    /// </summary>
    public class NetPackageVCHandshake : NetPackage
    {
        private string _buildId;

        /// <summary>Parameterless constructor — REQUIRED for 7DTD reflection-based packet instantiation.</summary>
        public NetPackageVCHandshake() { }

        /// <summary>Fluent setup for sending to server.</summary>
        public NetPackageVCHandshake Setup(string buildId)
        {
            _buildId = buildId ?? "";
            return this;
        }

        public override void read(PooledBinaryReader _br)
        {
            var br = (BinaryReader)_br;
            int len = br.ReadInt32();
            _buildId = len > 0 ? System.Text.Encoding.UTF8.GetString(br.ReadBytes(len)) : "";
        }

        public override void write(PooledBinaryWriter _bw)
        {
            base.write(_bw); // header lesson — package-ID header FIRST
            var bw = (BinaryWriter)_bw;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(_buildId ?? "");
            bw.Write(bytes.Length);
            if (bytes.Length > 0)
                bw.Write(bytes, 0, bytes.Length);
        }

        public override int GetLength()
        {
            // 2 (base package-ID header) + 4 (len) + buildId bytes
            return 6 + System.Text.Encoding.UTF8.GetByteCount(_buildId ?? "");
        }

        public override void ProcessPackage(World _world, GameManager _callbacks)
        {
            if (_world == null) return;
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) return;

            string serverId = VCBuildId.Current;
            string clientId = _buildId ?? "";

            if (clientId == serverId)
            {
                Log.Debug(() => $"[VC-NET] Handshake match: player {Sender?.entityId} '{clientId}'");
                return;
            }

            // Mismatch — warn in log. No clean server→single-player chat path exists
            // (SdtdConsole.Output broadcasts to all clients; no per-entity chat API found).
            Log.Out($"[VC-NET] Build mismatch: player {Sender?.entityId} has '{clientId}', server has '{serverId}' — MP features may misbehave");
        }
    }
}
