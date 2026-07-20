using System.IO;

namespace XNPCVoiceControl.Net
{
    /// <summary>
    /// Network packet for state-mutating voice/dialog commands.
    /// Carries player entity ID, NPC entity ID, command byte, and optional string arg.
    /// 
    /// 7DTD auto-discovers this class via reflection at startup (inherits NetPackage, has parameterless ctor).
    /// ProcessPackage: server executes authoritatively; client ignores (no re-broadcast).
    /// </summary>
    public class NetPackageVCCommand : NetPackage
    {
        private int _playerEntityId;
        private int _npcEntityId;
        private byte _cmd;
        private string _arg;

        /// <summary>Parameterless constructor — REQUIRED for 7DTD reflection-based packet instantiation.</summary>
        public NetPackageVCCommand() { }

        /// <summary>Fluent setup for sending to server.</summary>
        public NetPackageVCCommand Setup(int playerEntityId, int npcEntityId, byte cmd, string arg)
        {
            _playerEntityId = playerEntityId;
            _npcEntityId = npcEntityId;
            _cmd = cmd;
            _arg = arg ?? "";
            return this;
        }

        public override void read(PooledBinaryReader _br)
        {
            // Cast to base BinaryReader to avoid PooledBinaryReader's ReadOnlySpan overloads (Mono compat)
            var br = (BinaryReader)_br;
            _playerEntityId = br.ReadInt32();
            _npcEntityId = br.ReadInt32();
            _cmd = br.ReadByte();
            int len = br.ReadUInt16();
            _arg = len > 0 ? System.Text.Encoding.UTF8.GetString(br.ReadBytes(len)) : "";
        }

        public override void write(PooledBinaryWriter _bw)
        {
            base.write(_bw);
            // Cast to base BinaryWriter to avoid ALL PooledBinaryWriter ReadOnlySpan overloads (Mono compat)
            var bw = (BinaryWriter)_bw;
            bw.Write(_playerEntityId);
            bw.Write(_npcEntityId);
            bw.Write(_cmd);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(_arg ?? "");
            bw.Write((ushort)bytes.Length);
            if (bytes.Length > 0)
                bw.Write(bytes, 0, bytes.Length);
        }

        public override int GetLength()
        {
            // 2 (base package-ID header) + 4 (playerEntityId) + 4 (npcEntityId) + 1 (cmd) + 2 (arg len) + arg bytes
            return 13 + System.Text.Encoding.UTF8.GetByteCount(_arg ?? "");
        }

        public override void ProcessPackage(World _world, GameManager _callbacks)
        {
            if (_world == null) return;

            // Server only — execute authoritatively. Clients ignore (no re-broadcast).
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) return;

            VCCommandRouter.ExecuteAuthoritative(_playerEntityId, _npcEntityId, (VCCommand)_cmd, _arg);
        }
    }
}
