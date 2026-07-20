using System.Collections.Generic;
using Platform;
using UnityEngine;
using XNPCVoiceControl.Core;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Global event subscribers for episodic memory generation.
    /// ONE static instance — registered once at mod init, fires AddEventMemoryAsync
    /// for hired companions when notable events occur.
    /// </summary>
    public static class NPCEventMemoryHooks
    {
        // Blood moon tracking: snapshot of (entityId → leaderEntityId) at blood moon start.
        private static readonly Dictionary<int, int> _bloodMoonSnapshot = new Dictionary<int, int>();

        /// <summary>Register all global event subscribers.</summary>
        public static void Register()
        {
            EventOnBloodMoonStart.BloodMoonStart += OnBloodMoonStart;
            EventOnBloodMoonEnd.BloodMoonEnd += OnBloodMoonEnd;
            ModEvents.EntityKilled.RegisterHandler(OnEntityKilled);
            Log.Debug("[EVENT-HOOKS] Global event subscribers registered");
        }

        /// <summary>Unregister all global event subscribers.</summary>
        public static void Unregister()
        {
            EventOnBloodMoonStart.BloodMoonStart -= OnBloodMoonStart;
            EventOnBloodMoonEnd.BloodMoonEnd -= OnBloodMoonEnd;
            ModEvents.EntityKilled.UnregisterHandler(OnEntityKilled);
            Log.Debug("[EVENT-HOOKS] Global event subscribers unregistered");
        }

        // ========================================================================
        // Blood moon survived together
        // ========================================================================

        private static void OnBloodMoonStart()
        {
            _bloodMoonSnapshot.Clear();

            foreach (var comp in ChatComponentManager.GetAll())
            {
                if (comp == null) continue;
                // Unity destroyed-object check: comp == null catches destroyed MonoBehaviours
                try
                {
                    if (!comp.HasLeader()) continue;
                    var npc = comp.NPCEntity;
                    if (npc == null || npc.IsDead()) continue;
                    _bloodMoonSnapshot[npc.entityId] = comp.LeaderEntityId;
                }
                catch
                {
                    // Component or entity destroyed during iteration — skip
                }
            }

            Log.Debug(() => $"[EVENT-HOOKS] Blood moon start: {GetHiredCompanionCount()} hired companions alive");
        }

        private static void OnBloodMoonEnd()
        {
            int day = (int)SkyManager.dayCount;
            int survived = 0;

            foreach (var comp in ChatComponentManager.GetAll())
            {
                if (comp == null) continue;
                try
                {
                    var npc = comp.NPCEntity;
                    if (npc == null || npc.IsDead()) continue;

                    // Was this NPC in our start snapshot?
                    if (!_bloodMoonSnapshot.TryGetValue(npc.entityId, out int startLeaderId))
                        continue;

                    // Still has the same leader at blood moon end?
                    if (!comp.HasLeader() || comp.LeaderEntityId != startLeaderId)
                        continue;

                    // Resolve player persistent ID for memory keying
                    string playerId = ResolvePlayerPersistentId(comp.LeaderEntityId);
                    if (string.IsNullOrEmpty(playerId) || playerId == "unknown")
                        continue;

                    var memMgr = NPCMemoryManager.Instance;
                    if (memMgr != null)
                    {
                        _ = memMgr.AddEventMemoryAsync(
                            playerId, comp.NPCName,
                            $"Survived the blood moon on day {day} together.",
                            "high");
                        survived++;
                    }
                }
                catch
                {
                    // Component or entity destroyed during iteration — skip
                }
            }

            _bloodMoonSnapshot.Clear();
            Log.Debug(() => $"[EVENT-HOOKS] Blood moon end: {survived} event memories stored");
        }

        // ========================================================================
        // Player died near NPC
        // ========================================================================

        private static void OnEntityKilled(ref ModEvents.SEntityKilledData data)
        {
            // Filter: killed entity must be a player
            // Note: 7DTD's struct has a typo — "KilledEntitiy" not "KilledEntity"
            if (data.KilledEntitiy == null) return;
            if (!(data.KilledEntitiy is EntityPlayer)) return;

            int day = (int)SkyManager.dayCount;
            Vector3 deathPos = data.KilledEntitiy.position;
            const float CheckRadius = 50f;
            const float CheckRadiusSq = CheckRadius * CheckRadius;

            foreach (var comp in ChatComponentManager.GetAll())
            {
                if (comp == null) continue;
                try
                {
                    if (!comp.HasLeader()) continue;
                    var npc = comp.NPCEntity;
                    if (npc == null || npc.IsDead()) continue;

                    // Is the hired companion within range of the death?
                    if ((npc.position - deathPos).sqrMagnitude > CheckRadiusSq)
                        continue;

                    string playerId = ResolvePlayerPersistentId(comp.LeaderEntityId);
                    if (string.IsNullOrEmpty(playerId) || playerId == "unknown")
                        continue;

                    var memMgr = NPCMemoryManager.Instance;
                    if (memMgr != null)
                    {
                        _ = memMgr.AddEventMemoryAsync(
                            playerId, comp.NPCName,
                            $"You died near me on day {day}.",
                            "high");
                    }
                }
                catch
                {
                    // Component or entity destroyed during iteration — skip
                }
            }
        }

        // ========================================================================
        // Helpers
        // ========================================================================

        /// <summary>
        /// Resolve a player's persistent ID from their entityId.
        /// Mirrors GetPlayerPersistentId fallback chain: ConnectionManager → PlatformManager → GamePrefs.
        /// NEVER uses player.name (GameObject name embeds per-session entityId like "Player_171").
        /// </summary>
        private static string ResolvePlayerPersistentId(int leaderEntityId)
        {
            if (leaderEntityId <= 0) return "unknown";

            Entity leader = GameManager.Instance?.World?.GetEntity(leaderEntityId);
            if (leader is EntityPlayer player)
            {
                // 1. ConnectionManager — works for remote players, also local host when connected
                try
                {
                    var cInfo = SingletonMonoBehaviour<ConnectionManager>.Instance.Clients.ForEntityId(player.entityId);
                    if (cInfo != null)
                    {
                        PlatformUserIdentifierAbs pId = cInfo.CrossplatformId ?? cInfo.PlatformId;
                        if (pId != null)
                            return pId.CombinedString;
                    }
                }
                catch { /* ConnectionManager access failed */ }

                // 2. Local host fallback — stable across sessions (NEVER use player.name)
                try
                {
                    var localId = PlatformManager.InternalLocalUserIdentifier;
                    if (localId != null)
                        return localId.CombinedString;
                }
                catch { /* PlatformManager not ready */ }

                // 3. GamePrefs.PlayerName fallback (stable in SP, set by user at character creation)
                try
                {
                    string prefName = GamePrefs.GetString(EnumGamePrefs.PlayerName);
                    if (!string.IsNullOrEmpty(prefName))
                        return prefName;
                }
                catch { /* prefs not loaded */ }
            }

            return "unknown";
        }

        private static int GetHiredCompanionCount()
        {
            int count = 0;
            foreach (var comp in ChatComponentManager.GetAll())
            {
                if (comp != null && comp.HasLeader()) count++;
            }
            return count;
        }
    }
}
