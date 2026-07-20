using System;
using System.Collections.Generic;
using UnityEngine;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Plain-data snapshot of NPC/environmental state captured on the main thread.
    /// Passed to background LLM pipelines — zero Unity API access after snapshot.
    /// </summary>
    public class NPCSenseSnapshot
    {
        // --- Environmental ---
        public int WorldHour;           // 0-23
        public bool IsDaytime;          // true if hour 6-18
        public string BiomeName;        // e.g. "Temperate Forest"

        // --- Physical ---
        public float HealthPercentage;  // 0.0-1.0

        // --- Status Effects (subset of meaningful buffs) ---
        public List<string> ActiveBuffs; // e.g. ["buffInjuryBleeding", "buffStatusHungry2"]

        // --- Behavioral (SCore) ---
        public int CurrentOrder;        // EntityUtilities.Orders enum value
        public bool IsHired;            // has a leader/owner

        // --- Combat State (interruption triggers for future use) ---
        public int AttackTargetEntityId;   // -1 if none
        public int RevengeTargetEntityId;  // -1 if none

        // --- Situational ---
        public string ThreatProximity;   // "none", "nearby (Xm)", or "engaged"
        public bool IsBloodMoon;         // true if current night is a blood moon night

        /// <summary>
        /// Capture all NPC/environmental state from the main thread.
        /// Only call this on the Unity main thread — accesses world, entity, and buffs.
        /// </summary>
        public static NPCSenseSnapshot Capture(EntityAlive npc)
        {
            if (npc == null) return null;

            var snapshot = new NPCSenseSnapshot();

            // --- Time of Day (worldTime is a plain ulong — safe to read anywhere) ---
            ulong worldTime = GameManager.Instance?.World?.worldTime ?? 0;
            int hourOfDay = (int)((worldTime % 24000) / 1000);
            snapshot.WorldHour = hourOfDay;
            snapshot.IsDaytime = hourOfDay >= 6 && hourOfDay < 18;

            // --- Biome (must be main thread — reads chunk data) ---
            try
            {
                var world = GameManager.Instance?.World;
                if (world != null)
                {
                    Vector3 pos = npc.position;
                    var biomeDef = world.GetBiome((int)pos.x, (int)pos.z);
                    snapshot.BiomeName = biomeDef?.LocalizedName ?? biomeDef?.m_sBiomeName ?? "Unknown";
                }
            }
            catch
            {
                snapshot.BiomeName = "Unknown";
            }

            // --- Health (Health is a plain float; GetMaxHealth may touch Unity) ---
            try
            {
                float maxHealth = npc.GetMaxHealth();
                snapshot.HealthPercentage = maxHealth > 0f ? npc.Health / maxHealth : 1f;
            }
            catch
            {
                snapshot.HealthPercentage = 1f;
            }

            // --- Active Buffs (iterate collection — main thread only) ---
            try
            {
                var activeBuffs = npc.Buffs?.ActiveBuffs;
                if (activeBuffs != null && activeBuffs.Count > 0)
                {
                    snapshot.ActiveBuffs = new List<string>(activeBuffs.Count);
                    foreach (var buff in activeBuffs)
                    {
                        string name = buff?.BuffName;
                        if (!string.IsNullOrEmpty(name))
                        {
                            snapshot.ActiveBuffs.Add(name);
                        }
                    }
                }
            }
            catch
            {
                // Buffs iteration failed — proceed without them
            }

            // --- SCore Behavioral State (uses GameManager.World.GetEntity) ---
            try
            {
                var leaderOrOwner = EntityUtilities.GetLeaderOrOwner(npc.entityId);
                snapshot.IsHired = leaderOrOwner != null;

                int order = 0;
                if (npc.Buffs.HasCustomVar("CurrentOrder"))
                {
                    order = (int)npc.Buffs.GetCustomVar("CurrentOrder");
                }
                snapshot.CurrentOrder = order;
            }
            catch
            {
                // SCore not loaded or entity gone — defaults are fine
            }

            // --- Combat State (future interruption matrix use) ---
            try
            {
                var attackTarget = npc.GetAttackTarget();
                snapshot.AttackTargetEntityId = attackTarget != null ? attackTarget.entityId : -1;

                var revengeTarget = npc.GetRevengeTarget();
                snapshot.RevengeTargetEntityId = revengeTarget != null ? revengeTarget.entityId : -1;
            }
            catch
            {
                snapshot.AttackTargetEntityId = -1;
                snapshot.RevengeTargetEntityId = -1;
            }

            // --- Threat Proximity (nearest zombie within 30m, or "engaged" if already targeting) ---
            try
            {
                if (snapshot.AttackTargetEntityId != -1)
                {
                    snapshot.ThreatProximity = "engaged";
                }
                else
                {
                    var threatWorld = GameManager.Instance?.World;
                    if (threatWorld != null)
                    {
                        const float scanRadiusSq = 900f; // 30m²
                        float minDistSq = float.MaxValue;
                        Vector3 npcPos = npc.position;
                        var entityList = threatWorld.Entities.list;
                        for (int i = 0; i < entityList.Count; i++)
                        {
                            Entity e = entityList[i];
                            if (e is EntityZombie && !e.IsDead())
                            {
                                float dSq = (e.position - npcPos).sqrMagnitude;
                                if (dSq < scanRadiusSq && dSq < minDistSq)
                                    minDistSq = dSq;
                            }
                        }
                        snapshot.ThreatProximity = minDistSq < float.MaxValue
                            ? $"nearby ({Mathf.Sqrt(minDistSq):F0}m)"
                            : "none";
                    }
                    else
                        snapshot.ThreatProximity = "none";
                }
            }
            catch { snapshot.ThreatProximity = "none"; }

            // --- Blood Moon Detection (night-only, day-number % frequency) ---
            try
            {
                if (!snapshot.IsDaytime)
                {
                    int freq = GamePrefs.GetInt(EnumGamePrefs.BloodMoonFrequency);
                    if (freq > 0)
                    {
                        ulong wt = GameManager.Instance?.World?.worldTime ?? 0;
                        int dayNumber = (int)(wt / 24000) + 1;
                        snapshot.IsBloodMoon = (dayNumber % freq == 0);
                    }
                }
            }
            catch { snapshot.IsBloodMoon = false; }

            return snapshot;
        }

        /// <summary>
        /// Convert the snapshot into a prompt injection block.
        /// Skips empty fields (e.g., no buffs). Returns null if nothing to report.
        /// </summary>
        public string ToPromptString()
        {
            if (this == null) return null;

            var parts = new List<string>();

            // Time of day
            string timeLabel = IsDaytime ? "day" : "night";
            parts.Add($"Time: {WorldHour:00}:00 ({timeLabel})");

            // Blood moon (contextual — night-only)
            if (IsBloodMoon)
                parts.Add("Blood Moon active");

            // Biome
            if (!string.IsNullOrEmpty(BiomeName) && BiomeName != "Unknown")
            {
                parts.Add($"Biome: {BiomeName}");
            }

            // Health (only notable if not perfect)
            int healthPct = Mathf.RoundToInt(HealthPercentage * 100f);
            parts.Add($"Health: {healthPct}%");

            // Status effects (filter to meaningful ones for prompt context)
            if (ActiveBuffs != null && ActiveBuffs.Count > 0)
            {
                var readableBuffs = new List<string>();
                foreach (var buff in ActiveBuffs)
                {
                    string label = GetReadableBuffName(buff);
                    if (!string.IsNullOrEmpty(label))
                    {
                        readableBuffs.Add(label);
                    }
                }
                if (readableBuffs.Count > 0)
                {
                    parts.Add($"Status: {string.Join(", ", readableBuffs)}");
                }
            }

            // Threat proximity (omit when clear)
            if (!string.IsNullOrEmpty(ThreatProximity) && ThreatProximity != "none")
                parts.Add($"Threat: {ThreatProximity}");

            // Behavioral mode
            string orderLabel = GetReadableOrderName(CurrentOrder);
            if (!string.IsNullOrEmpty(orderLabel))
            {
                parts.Add($"Mode: {orderLabel}");
            }
            else
            {
                parts.Add(IsHired ? "Mode: Hired" : "Mode: Unhired");
            }

            // Build final block
            return $"\n\n[ENVIRONMENTAL STATE: {string.Join(", ", parts)}. Do not read this block aloud, but let it influence your tone and dialogue.]";
        }

        /// <summary>
        /// Map buff names to human-readable labels for the LLM prompt.
        /// </summary>
        private static string GetReadableBuffName(string buffName)
        {
            if (string.IsNullOrEmpty(buffName)) return null;

            // Injury buffs
            if (buffName.Contains("Bleeding")) return "bleeding";
            if (buffName.Contains("LegSprained")) return "leg sprained";
            if (buffName.Contains("ArmBroken")) return "arm broken";

            // Status effects
            if (buffName.Contains("Wounded")) return "wounded";
            if (buffName.Contains("Hungry2")) return "very hungry";
            if (buffName.Contains("Hungry1")) return "hungry";
            if (buffName.Contains("Thirsty2")) return "very thirsty";
            if (buffName.Contains("Thirsty1")) return "thirsty";
            if (buffName.Contains("Stun")) return "stunned";

            // Combat buffs
            if (buffName.Contains("NotifyTeamAttack")) return "in combat";

            return null; // Skip internal/irrelevant buffs
        }

        /// <summary>
        /// Map EntityUtilities.Orders enum to readable label.
        /// </summary>
        private static string GetReadableOrderName(int orderValue)
        {
            switch (orderValue)
            {
                case 1: return "Following";
                case 2: return "Staying";
                case 3: return "Wandering";
                case 4: return "Patrol Point Set";
                case 5: return "Patrolling";
                case 6: return "Being Hired";
                case 7: return "Looting";
                case 8: return "On Task";
                case 9: return "Guarding";
                default: return null; // Order 0 (None) — return null to let IsHired handle it
            }
        }
    }
}
