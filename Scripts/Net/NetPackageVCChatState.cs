using System.Collections.Generic;
using System.IO;

namespace XNPCVoiceControl.Net
{
    /// <summary>
    /// Network packet for chat-state sync (Phase 1b).
    /// Client sends active=true when a conversation starts, re-sends every 20s as keepalive,
    /// active=false on conversation end. Server sets _remoteChatUntil timeout (45s).
    ///
    /// 7DTD auto-discovers via reflection (inherits NetPackage, parameterless ctor).
    /// ProcessPackage: server-only; ignore on clients. No re-broadcast.
    /// </summary>
    public class NetPackageVCChatState : NetPackage
    {
        private int _npcEntityId;
        private bool _active;

        // Track which NPCs have had their first packet logged (one-time Log.Out per NPC).
        private static HashSet<int> _firstReceiptLogged = new HashSet<int>();

        /// <summary>Parameterless constructor — REQUIRED for 7DTD reflection-based packet instantiation.</summary>
        public NetPackageVCChatState() { }

        /// <summary>Fluent setup for sending to server.</summary>
        public NetPackageVCChatState Setup(int npcEntityId, bool active)
        {
            _npcEntityId = npcEntityId;
            _active = active;
            return this;
        }

        public override void read(PooledBinaryReader _br)
        {
            var br = (BinaryReader)_br;
            _npcEntityId = br.ReadInt32();
            _active = br.ReadBoolean();
        }

        public override void write(PooledBinaryWriter _bw)
        {
            base.write(_bw);
            var bw = (BinaryWriter)_bw;
            bw.Write(_npcEntityId);
            bw.Write(_active);
        }

        public override int GetLength()
        {
            return 2 + 4 + 1; // 2 (base package-ID header) + int + bool
        }

        public override void ProcessPackage(World _world, GameManager _callbacks)
        {
            if (_world == null) return;

            // Server only — set remote chat state. Clients ignore (no re-broadcast).
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) return;

            // Resolve entity from world — don't rely on TryGet (component may not exist yet on dedi).
            // Third silent-TryGet instance: roster scan and VCCommandRouter had the same pattern.
            EntityAlive target = null;
            foreach (var entity in _world.Entities.list)
            {
                if (entity is EntityAlive alive && alive.entityId == _npcEntityId)
                {
                    target = alive;
                    break;
                }
            }

            if (target == null)
            {
                Log.Debug(() => $"[VC-NET] ChatState: entity {_npcEntityId} not found in world, dropping");
                return;
            }

            var comp = Core.ChatComponentManager.GetOrCreate(target);
            if (comp == null)
            {
                Log.Debug(() => $"[VC-NET] ChatState: GetOrCreate failed for {target.EntityName} (id={_npcEntityId}), dropping");
                return;
            }

            comp.SetRemoteChatActive(_active);
            Log.Debug(() => $"[VC-NET] ChatState received: {target.EntityName} id={_npcEntityId} active={_active}");

            // One-time Log.Out per NPC on first receipt — confirms the pipe is live.
            if (_firstReceiptLogged.Add(_npcEntityId))
                Log.Out($"[VC-NET] ChatState: first packet for {target.EntityName} (id={_npcEntityId}) — pipe confirmed");
        }
    }
}
