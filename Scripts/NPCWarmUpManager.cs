using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using XNPCVoiceControl.Core;

namespace XNPCVoiceControl
{
    /// <summary>
    /// "Loaded Chamber" — Proximity-based pre-generation of NPC greetings.
    ///
    /// When the player approaches an NPC, this manager generates a real,
    /// personality-driven greeting in the background and buffers it on the
    /// NPC's chat component. When the player interacts, the greeting plays
    /// instantly (0ms TTFA) before processing their actual query.
    ///
    /// One warmup per cycle (3s). Prioritizes closest NPC. Blocks during active conversations.
    /// </summary>
    public class NPCWarmUpManager : MonoBehaviour
    {
        private static NPCWarmUpManager _instance;
        public static NPCWarmUpManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("NPCWarmUpManager");
                    _instance = go.AddComponent<NPCWarmUpManager>();
                    DontDestroyOnLoad(go);
                    _instance.Start();
                }
                return _instance;
            }
        }

        // Tuning parameters (hardcoded defaults — externalize to modconfig.xml if tuning needed)
        private const float DetectionRadius = 15f;       // meters to consider "nearby"
        private const float CheckInterval = 3f;          // seconds between proximity scans
        private const float ClearDistance = 50f;         // player moves this far → clear all warmups + buffers
        private const float WarmUpTtlSeconds = 60f;      // re-warm after this many seconds

        // Zero-GC spatial query buffer (reused every cycle)
        private readonly List<Entity> _spatialBuffer = new List<Entity>(32);

        // Track warmed NPCs: entityId → (lastWarmupTime, npcName)
        private Dictionary<int, WarmUpEntry> _warmedUpNpcs = new Dictionary<int, WarmUpEntry>();
        private Vector3 _lastPlayerPos = Vector3.zero;
        private bool _initialized = false;

        private struct WarmUpEntry
        {
            public float lastWarmupTime;
            public string npcName;
        }

        void Start()
        {
            if (GameManager.IsDedicatedServer) return; // No NPCs on dedi
            _initialized = true;
            StartCoroutine(ProximityCheckCoroutine());
        }

        IEnumerator ProximityCheckCoroutine()
        {
            while (_initialized)
            {
                yield return new WaitForSeconds(CheckInterval);

                // Guard: skip if any LLM request is in-flight (active conversation blocks warmup)
                if (LLMService.Instance.IsBusy)
                    continue;

                EntityPlayer player = GameManager.Instance?.World?.GetPrimaryPlayer();
                if (player == null) continue;

                // Tactical mode: suppress proactive greetings entirely.
                if ((int)player.Buffs.GetCustomVar("varTacticalMode") == 1) continue;

                Vector3 playerPos = player.position;
                string playerName = NPCChatComponent.GetPlayerPersistentId(player);

                // If player moved far, clear all warmups (new area = new NPCs)
                if (Vector3.Distance(playerPos, _lastPlayerPos) > ClearDistance)
                {
                    _warmedUpNpcs.Clear();
                    _lastPlayerPos = playerPos;
                }
                else
                {
                    _lastPlayerPos = playerPos;
                }

                // Spatial query — only returns EntityAlive within bounds
                var world = GameManager.Instance?.World;
                if (world == null) continue;

                _spatialBuffer.Clear();
                world.GetEntitiesInBounds(
                    typeof(EntityAlive),
                    new Bounds(playerPos, Vector3.one * DetectionRadius * 2f),
                    _spatialBuffer);

                // Ensure ALL nearby NPCs have chat components initialized (face lip-sync, blink).
                // GetOrCreate is idempotent — returns existing component if already created.
                // This runs before the greeting-generation loop so blink starts immediately
                // when the player approaches, not just after first conversation.
                for (int i = 0; i < _spatialBuffer.Count; i++)
                {
                    EntityAlive alive = _spatialBuffer[i] as EntityAlive;
                    if (alive == null || !ChatComponentManager.IsChatTarget(alive))
                        continue;
                    if (ChatComponentManager.IsAnimal(alive))
                        continue;
                    ChatComponentManager.GetOrCreate(alive);
                }

                // Sort by distance (closest first) — prioritizes most likely conversation target
                _spatialBuffer.Sort((a, b) =>
                    Vector3.Distance(a.position, playerPos).CompareTo(
                        Vector3.Distance(b.position, playerPos)));

                float now = Time.realtimeSinceStartup;
                bool warmedThisCycle = false;

                for (int i = 0; i < _spatialBuffer.Count; i++)
                {
                    EntityAlive alive = _spatialBuffer[i] as EntityAlive;
                    if (alive == null || !ChatComponentManager.IsChatTarget(alive))
                        continue;
                    if (ChatComponentManager.IsAnimal(alive))
                        continue; // Pets use phrase triggers only — no LLM greeting to buffer

                    int entityId = alive.entityId;

                    // Skip if already warmed within TTL
                    if (_warmedUpNpcs.TryGetValue(entityId, out var entry))
                    {
                        if (now - entry.lastWarmupTime < WarmUpTtlSeconds)
                            continue;
                    }

                    // Resolve NPC name: prefer entity name (component may not exist yet)
                    string npcName = alive.EntityName ?? "Survivor";

                    // MAIN THREAD: resolve personality for chance check + pass to LLMService
                    PersonalityDefinition personality = null;
                    if (PersonalityManager.Instance != null)
                    {
                        personality = PersonalityManager.Instance.AssignPersonality(alive);
                    }

                    // Record warmup entry FIRST — NPC is locked out for TTL regardless of dice roll.
                    // This prevents the "50% chance becomes 100% after 6s" problem where skipped NPCs
                    // keep rolling every cycle until they eventually succeed.
                    _warmedUpNpcs[entityId] = new WarmUpEntry
                    {
                        lastWarmupTime = now,
                        npcName = npcName
                    };

                    // XML-driven greeting chance (defaults to 0.5 if personality is null)
                    float greetingChance = personality?.ProactiveGreetingChance ?? 0.5f;
                    float roll = UnityEngine.Random.value;
                    if (roll > greetingChance)
                    {
                        Log.Debug(() => $"[PROACTIVE] Skipped {npcName} — RNG roll {roll:F2} > chance {greetingChance:F2}");
                        warmedThisCycle = true; // Count as attempted so we don't retry this cycle
                        break;
                    }

                    // Capture environmental sense snapshot on main thread (Unity API access)
                    NPCSenseSnapshot senseSnapshot = NPCSenseSnapshot.Capture(alive);

                    // Generate greeting in background (Task.Run → HttpWebRequest)
                    Task<string> genTask = LLMService.Instance.GenerateBufferedGreetingAsync(
                        npcName, alive, playerName, XNPCVoiceControlMod.Config, personality, senseSnapshot);

                    // Yield back to Unity while the background thread computes the LLM response
                    yield return new WaitUntil(() => genTask.IsCompleted);

                    if (genTask.Exception != null)
                    {
                        Log.Debug(() => $"[PROACTIVE] Greeting task faulted for entity {entityId}: {genTask.Exception.Message}");
                        warmedThisCycle = true;
                        break;
                    }

                    string greeting = genTask.Result;
                    if (!string.IsNullOrEmpty(greeting))
                    {
                        // Use GetOrCreate to guarantee TTS and Personality are initialized
                        var chatComponent = ChatComponentManager.GetOrCreate(alive);

                        if (chatComponent != null)
                        {
                            // Store in buffer — UAI will drain it when NPC initiates (2-10m)
                            // or player interaction drains it for instant TTFA
                            chatComponent.StoreBufferedGreeting(greeting);
                            Log.Debug(() => $"[PROACTIVE] Buffered greeting for {npcName} (entity {entityId}): \"{greeting.Substring(0, Math.Min(60, greeting.Length))}\"");
                        }
                    }

                    // Only ONE warmup per cycle — prevents queue flooding
                    warmedThisCycle = true;
                    break;
                }

                // Cleanup stale entries (NPCs no longer tracked by ChatComponentManager)
                if (!warmedThisCycle)
                {
                    var keysToRemove = new List<int>();
                    foreach (var kvp in _warmedUpNpcs)
                    {
                        bool stillTracked = false;
                        foreach (var component in ChatComponentManager.GetAll())
                        {
                            if (component != null && component.NPCEntity != null && component.NPCEntity.entityId == kvp.Key)
                            {
                                stillTracked = true;
                                break;
                            }
                        }
                        // Remove if not tracked AND past TTL
                        if (!stillTracked && now - kvp.Value.lastWarmupTime > WarmUpTtlSeconds)
                        {
                            keysToRemove.Add(kvp.Key);
                        }
                    }
                    foreach (int key in keysToRemove)
                        _warmedUpNpcs.Remove(key);
                }
            }
        }

        /// <summary>Called from ChatComponentManager.OnEntityRemoved to clean up warmup state.</summary>
        public void OnEntityRemoved(int entityId)
        {
            _warmedUpNpcs.Remove(entityId);
        }

        /// <summary>Shut down the manager (called on game quit).</summary>
        public void Shutdown()
        {
            _initialized = false;
            _warmedUpNpcs.Clear();
        }
    }
}
