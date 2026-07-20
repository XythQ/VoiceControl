using UnityEngine;
using XNPCVoiceControl.Core;

namespace XNPCVoiceControl.Harmony
{
    /// <summary>
    /// Asymmetric push — hired NPC yields to their leader, leader is never displaced.
    /// Zombies and unhired NPCs use the vanilla mutual push path.
    /// </summary>
    public class LeaderPushPatch
    {
        // One-shot log per NPC entityId on first asymmetric push.
        private static readonly System.Collections.Generic.HashSet<int> _logged = new System.Collections.Generic.HashSet<int>();

        public static bool Prefix(Entity __instance, Entity _entity)
        {
            EntityPlayer player;
            EntityAlive npc;

            // Identify which is the player and which is the NPC.
            if (__instance is EntityPlayer p && _entity is EntityAlive a && !(_entity is EntityPlayer))
            { player = p; npc = a; }
            else if (_entity is EntityPlayer p2 && __instance is EntityAlive a2 && !(__instance is EntityPlayer))
            { player = p2; npc = a2; }
            else return true; // not our pair -> vanilla

            var leader = EntityUtilities.GetLeaderOrOwner(npc.entityId);
            if (leader == null || leader.entityId != player.entityId) return true; // not hired by this player -> vanilla

            // Asymmetric: NPC yields, leader is never displaced.
            Vector3 away = npc.position - player.position; away.y = 0f;
            if (away.sqrMagnitude < 0.0001f) away = -player.transform.forward;
            npc.AddVelocity(away.normalized * 0.04f);

            // One-shot log per NPC.
            if (_logged.Add(npc.entityId))
                XNPCVoiceControl.Log.Out($"[PUSH] {npc.EntityName} yields to leader (asymmetric)");

            return false; // skip vanilla mutual push entirely
        }
    }
}
