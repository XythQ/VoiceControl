using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using XNPCVoiceControl.Core;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Ambient NPC-to-NPC idle chatter. Periodically scans nearby NPCs (via ChatComponentManager's
    /// already-tracked pool, which Wave 18 bounds to ~15m of a player) for eligible pairs within a
    /// short trigger range, occasionally generates a brief backstory-grounded exchange between
    /// them, then both resume normal activity. Pure atmosphere — no persistent state, no player
    /// interaction beyond what they overhear.
    /// </summary>
    public class NPCToNPCChatManager : MonoBehaviour
    {
        private static NPCToNPCChatManager _instance;
        public static NPCToNPCChatManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("NPCToNPCChatManager");
                    _instance = go.AddComponent<NPCToNPCChatManager>();
                    DontDestroyOnLoad(go);
                    _instance.Start();
                }
                return _instance;
            }
        }

        // Config (set from NPCVoiceControlMod.cs during load, mirrors static field pattern used elsewhere)
        internal bool _enabled = true;
        private float _range = 5f;
        private float _scanIntervalSeconds = 30f;
        private float _chance = 0.25f;
        private int _maxLinesPerNpc = 2;
        private float _globalCooldownMinutes = 5f;
        private float _pairCooldownMinutes = 15f;

        // Cooldown tracking (in-memory only, entityId-keyed — acceptable for pacing/flavor, not
        // persistent state; resets naturally on respawn/session change, low stakes if it does)
        private readonly Dictionary<(int, int), float> _pairCooldowns = new Dictionary<(int, int), float>();
        private readonly Dictionary<int, float> _playerGlobalCooldowns = new Dictionary<int, float>(); // keyed by player entityId

        private bool _initialized = false;
        private bool _scanInFlight = false; // prevents overlapping scans if a generation is still running when the next tick fires

        void Start()
        {
            if (GameManager.IsDedicatedServer) return;
            if (_initialized) return;
            _initialized = true;
            Log.Out("[NPCToNPC] Manager started");
            StartCoroutine(ScanCoroutine());
        }

        /// <summary>Set config values from NPCVoiceControlMod.cs during mod load or reload.</summary>
        public void SetConfig(bool enabled, float range, float scanIntervalSeconds, float chance,
            int maxLinesPerNpc, float globalCooldownMinutes, float pairCooldownMinutes)
        {
            _enabled = enabled;
            _range = range;
            _scanIntervalSeconds = scanIntervalSeconds;
            _chance = chance;
            _maxLinesPerNpc = maxLinesPerNpc;
            _globalCooldownMinutes = globalCooldownMinutes;
            _pairCooldownMinutes = pairCooldownMinutes;
        }

        public void Shutdown()
        {
            _initialized = false;
            _pairCooldowns.Clear();
            _playerGlobalCooldowns.Clear();
            StopAllCoroutines();
        }

        private IEnumerator ScanCoroutine()
        {
            while (_initialized)
            {
                yield return new WaitForSeconds(_scanIntervalSeconds);
                if (!_enabled) continue;
                if (_scanInFlight) continue;
                if (LLMService.Instance.IsBusy) continue; // player chat active somewhere - don't compete

                Log.Debug(() => "[NPCToNPC] Scan tick — starting");
                yield return RunScan();
            }
        }

        private IEnumerator RunScan()
        {
            _scanInFlight = true;
            try
            {
                var candidates = new List<NPCChatComponent>(ChatComponentManager.GetAll());
                Log.Debug(() => $"[NPCToNPC] Scan: {candidates.Count} tracked NPCs");

                int pairsInRange = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    for (int j = i + 1; j < candidates.Count; j++)
                    {
                        var a = candidates[i];
                        var b = candidates[j];
                        if (a == null || b == null) continue;
                        if (a.NPCEntity == null || b.NPCEntity == null) continue;

                        float dist = Vector3.Distance(a.NPCEntity.position, b.NPCEntity.position);
                        if (dist > _range) continue;

                        pairsInRange++;

                        if (!a.IsEligibleForAmbientChat() || !b.IsEligibleForAmbientChat())
                        {
                            Log.Debug(() => $"[NPCToNPC] {a.NPCName}+{b.NPCName} ({dist:F1}m) — ineligible (hired/combat/chatting)");
                            continue;
                        }

                        var pairKey = CanonicalPairKey(a.NPCEntity.entityId, b.NPCEntity.entityId);
                        if (_pairCooldowns.TryGetValue(pairKey, out float pairCd) && Time.time < pairCd)
                        {
                            Log.Debug(() => $"[NPCToNPC] {a.NPCName}+{b.NPCName} — pair cooldown active");
                            continue;
                        }

                        int nearestPlayerId = FindNearestPlayerId(a.NPCEntity.position, b.NPCEntity.position);
                        if (nearestPlayerId >= 0 && _playerGlobalCooldowns.TryGetValue(nearestPlayerId, out float zoneCd) && Time.time < zoneCd)
                        {
                            Log.Debug(() => $"[NPCToNPC] {a.NPCName}+{b.NPCName} — global zone cooldown active");
                            continue;
                        }

                        if (UnityEngine.Random.value > _chance)
                        {
                            Log.Debug(() => $"[NPCToNPC] {a.NPCName}+{b.NPCName} ({dist:F1}m) — RNG miss");
                            continue;
                        }

                        // Found an eligible, cooldown-clear, chance-passed pair. Generate.
                        Log.Debug(() => $"[NPCToNPC] Starting exchange: {a.NPCName} + {b.NPCName}");
                        yield return TryStartExchange(a, b, pairKey, nearestPlayerId);

                        // Only ever start ONE exchange per scan tick, even if multiple pairs qualify -
                        // keeps this genuinely ambient/rare, not a chatter explosion in a crowded town.
                        yield break;
                    }
                }

                if (pairsInRange == 0 && candidates.Count >= 2)
                    Log.Debug(() => $"[NPCToNPC] Scan: {candidates.Count} NPCs, 0 pairs within {_range}m");
            }
            finally
            {
                _scanInFlight = false;
            }
        }

        private IEnumerator TryStartExchange(NPCChatComponent a, NPCChatComponent b, (int, int) pairKey, int nearestPlayerId)
        {
            string nameA = a.NPCName;
            string nameB = b.NPCName;

            var task = LLMService.Instance.GenerateNPCChatAsync(
                nameA, a.Personality?.Backstory ?? "", nameB, b.Personality?.Backstory ?? "", _maxLinesPerNpc);

            while (!task.IsCompleted) yield return null;

            var result = task.Result;
            if (result == null)
            {
                Log.Debug(() => $"[NPCToNPC] LLM returned null for {nameA}+{nameB}, skipping");
                yield break;
            } // generation/parse failed - no cooldown consumed, will just roll again naturally next eligible scan

            // CRITICAL: re-validate both NPCs after the async generation gap - state may have
            // changed (player could have started talking to one of them, etc.). All-or-nothing:
            // never play just one side.
            if (a == null || b == null || !a.IsEligibleForAmbientChat() || !b.IsEligibleForAmbientChat())
            {
                Log.Debug(() => $"[NPCToNPC] Post-gen re-validation failed for {nameA}+{nameB}, aborting");
                yield break;
            }

            var (lineA, lineB) = result.Value;

            Log.Debug(() => $"[NPCToNPC] Playing: {nameA}: \"{lineA.Substring(0, Math.Min(60, lineA.Length))}\"");
            Log.Debug(() => $"[NPCToNPC] Playing: {nameB}: \"{lineB.Substring(0, Math.Min(60, lineB.Length))}\"");

            a.FaceEntity(b.NPCEntity, 4f);
            b.FaceEntity(a.NPCEntity, 4f);
            a.PlayProactiveGreeting(lineA, showSubtitle: false);
            b.PlayProactiveGreeting(lineB, showSubtitle: false);

            _pairCooldowns[pairKey] = Time.time + _pairCooldownMinutes * 60f;
            if (nearestPlayerId >= 0)
                _playerGlobalCooldowns[nearestPlayerId] = Time.time + _globalCooldownMinutes * 60f;
        }

        private static (int, int) CanonicalPairKey(int idA, int idB) =>
            idA < idB ? (idA, idB) : (idB, idA);

        /// <summary>Nearest connected player's entityId to the pair's midpoint, or -1 if none found.</summary>
        private static int FindNearestPlayerId(Vector3 posA, Vector3 posB)
        {
            var world = GameManager.Instance?.World;
            if (world == null) return -1;

            var players = world.Players.list;
            if (players == null || players.Count == 0) return -1;

            Vector3 midpoint = (posA + posB) * 0.5f;
            EntityPlayer closest = null;
            float closestDistSq = float.MaxValue;

            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || p.IsDead()) continue;
                float dSq = (p.position - midpoint).sqrMagnitude;
                if (dSq < closestDistSq) { closestDistSq = dSq; closest = p; }
            }

            return closest != null ? closest.entityId : -1;
        }
    }
}
