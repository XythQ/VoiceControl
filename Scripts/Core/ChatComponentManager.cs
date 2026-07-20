using System;
using System.Collections.Generic;
using UnityEngine;

namespace XNPCVoiceControl.Core
{
    /// <summary>
    /// Manages the lifecycle of NPCChatComponent instances.
    /// Static class — zero GC, no Update() loop, immune to Unity scene-load destruction.
    /// </summary>
    public static class ChatComponentManager
    {
        private static Dictionary<int, NPCChatComponent> _chatComponents = new Dictionary<int, NPCChatComponent>();
        private static LLMConfig _config;
        private static readonly List<Entity> _nearbyBuffer = new List<Entity>(64);

        /// <summary>Initialize the manager with the mod's config.</summary>
        public static void Initialize(LLMConfig config)
        {
            _config = config;
        }

        /// <summary>Shut down — clear all tracked components and personality assignments.</summary>
        public static void Shutdown()
        {
            try
            {
                foreach (var component in _chatComponents.Values)
                {
                    // During GameShutdown Unity may have already destroyed GameObjects.
                    // Unity's == null check handles destroyed objects, but accessing any
                    // property on a destroyed component can still throw from the native side,
                    // so we wrap each iteration defensively.
                    try
                    {
                        if (component != null)
                        {
                            component.StopSpeaking("manager-shutdown");
                            UnityEngine.Object.DestroyImmediate(component, false);
                        }
                    }
                    catch
                    {
                        // Component already destroyed by Unity — ignore during shutdown
                    }
                }
            }
            catch
            {
                // Dictionary or iteration corrupted during shutdown — ignore
            }
            finally
            {
                _chatComponents.Clear();
                try { PersonalityManager.Instance?.ClearAssignments(); } catch { /* ignore */ }
            }
        }

        /// <summary>Get or create an NPCChatComponent for the given NPC.</summary>
        public static NPCChatComponent GetOrCreate(EntityAlive npc)
        {
            if (npc == null)
            {
                Log.Warning("GetOrCreate: npc is null");
                return null;
            }

            if (npc.gameObject == null)
            {
                Log.Warning($"GetOrCreate: GameObject is null for {npc.EntityName}");
                return null;
            }

            int entityId = npc.entityId;

            // Return the cached component only if it's still alive. Unity's overloaded == makes a
            // destroyed MonoBehaviour compare == null, so a stale dict entry (after a world reload or
            // chunk streaming) must fall through and be recreated - not handed back as a dead reference.
            if (_chatComponents.TryGetValue(entityId, out NPCChatComponent chatComponent) && chatComponent != null)
                return chatComponent;

            // Missing or destroyed -> reuse a live component on the GameObject, else add a fresh one.
            chatComponent = npc.gameObject.GetComponent<NPCChatComponent>();
            if (chatComponent == null)
            {
                try
                {
                    Log.Debug(() => $"Adding NPCChatComponent to {npc.EntityName} (type: {npc.GetType().Name})");
                    chatComponent = npc.gameObject.AddComponent<NPCChatComponent>();
                    chatComponent.Initialize(npc, _config);
                    Log.Debug(() => $"Successfully initialized chat component for {npc.EntityName}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to add NPCChatComponent: {ex.Message}");
                    return null;
                }
            }
            _chatComponents[entityId] = chatComponent;   // overwrites any stale entry
            return chatComponent;
        }

        /// <summary>Fast lookup for UAI considerations. Never creates; returns false for
        /// missing or destroyed (Unity-null) components.</summary>
        public static bool TryGet(int entityId, out NPCChatComponent comp)
        {
            if (_chatComponents.TryGetValue(entityId, out comp) && comp != null)
                return true;
            comp = null;
            return false;
        }

        /// <summary>Remove a tracked component by entity ID (called on entity removal).</summary>
        public static void Remove(int entityId)
        {
            if (_chatComponents.TryGetValue(entityId, out NPCChatComponent component))
            {
                                component.StopSpeaking("entity-removed");
                if (component.gameObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(component, false);
                }
            }
            _chatComponents.Remove(entityId);
            PersonalityManager.Instance.RemoveAssignment(entityId);
        }

        /// <summary>Get all currently tracked chat components.</summary>
        public static IEnumerable<NPCChatComponent> GetAll()
        {
            return _chatComponents.Values;
        }

        // ========================================================================
        // Entity classification helpers (used by Harmony patches and voice input)
        // ========================================================================

        /// <summary>
        /// Check if entity is a hired NPC/animal by verifying it has an assigned player leader.
        /// Uses SCore's leader tracking — works for SDX companions, hired survivors, pets, etc.
        /// </summary>
        public static bool HasPlayerLeader(EntityAlive entity)
        {
            if (entity == null) return false;
            Entity leader = EntityUtilities.GetLeaderOrOwner(entity.entityId);
            return leader is EntityPlayer;
        }

        /// <summary>
        /// Check if an entity is a valid chat target (NPC, hired companion, trader, etc.).
        /// Excludes zombies and wild animals. Hired animals pass through but are handled separately.
        /// </summary>
        public static bool IsChatTarget(EntityAlive entity)
        {
            if (entity == null) return false;

            string typeName = entity.GetType().Name;

            // Exclude zombies entirely and wild animals (no player leader)
            // Hired animals (pets) are allowed through but handled with phrase triggers only
            if (typeName.Contains("Zombie"))
                return false;
            if (typeName.Contains("Animal") && !HasPlayerLeader(entity))
                return false;

            // NPCCore/XNPCCore NPC types
            if (typeName.Contains("NPC") ||
                typeName.Contains("Hired") ||
                typeName.Contains("Trader") ||
                typeName.Contains("Bandit"))
            {
                return true;
            }

            // Check entity class name
            if (entity.EntityClass != null)
            {
                string className = entity.EntityClass.entityClassName;
                if (className != null && (
                    className.ToLower().Contains("npc") ||
                    className.ToLower().Contains("trader") ||
                    className.ToLower().Contains("survivor")))
                {
                    return true;
                }
            }

            // Player-like NPCs (no client info = NPC)
            if (entity is EntityPlayer && !(entity is EntityPlayerLocal))
            {
                var clientInfo = ConnectionManager.Instance?.Clients?.ForEntityId(entity.entityId);
                if (clientInfo == null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Check if entity is an animal type (hired pet or wild).
        /// </summary>
        public static bool IsAnimal(EntityAlive entity)
        {
            if (entity == null) return false;
            return entity.GetType().Name.Contains("Animal");
        }

        // ========================================================================
        // @ prefix chat processing (called from Harmony ChatMessageServerPatch)
        // ========================================================================

        /// <summary>
        /// Process an @-prefixed chat command: find nearby NPC and route the message.
        /// </summary>
        public static void ProcessAtCommand(EntityPlayer player, string message)
        {
            EntityAlive nearbyNPC = FindNearbyNPC(player);
            if (nearbyNPC != null)
            {
                ProcessNPCChat(player, nearbyNPC, message.Trim());
            }
        }

        /// <summary>
        /// Called by RemoveEntityPatch when an entity is removed from the world.
        /// </summary>
        public static void OnEntityRemoved(int entityId)
        {
            // NPC-DIAG: log hired NPC removal mid-session (read-only diagnostic)
            LogHiredNPCRemoval(entityId);

            Remove(entityId);
            // Clear action state so recycled entity IDs don't inherit stale follow/guard/wait state.
            Actions.ActionExecutor.Instance?.ClearNPCState(entityId);
            // Clear Loaded Chamber warmup state for this entity
            NPCWarmUpManager.Instance?.OnEntityRemoved(entityId);
            // NOTE: We do NOT delete RAG memories here. NPCs respawn with new entityIds,
            // and memories are now keyed by "{playerId}_{npcName}" which survives restarts.
            // Deleting on entity removal would destroy cross-session memory persistence.
        }

        /// <summary>
        /// If the removed entity is a hired NPC (leader == local player), log persistence fields.
        /// Read-only diagnostic — does not affect removal behavior.
        /// </summary>
        private static void LogHiredNPCRemoval(int entityId)
        {
            var world = GameManager.Instance?.World;
            if (world == null) return;

            EntityAlive npc = null;
            try
            {
                foreach (var entity in world.Entities.list)
                {
                    if (entity is EntityAlive alive && alive.entityId == entityId)
                    {
                        npc = alive;
                        break;
                    }
                }
            } catch { return; }

            if (npc == null) return;

            // Check if hired by local player
            Entity leader = null;
            try { leader = EntityUtilities.GetLeaderOrOwner(entityId); } catch { return; }
            if (!(leader is EntityPlayerLocal)) return;

            int day = 0;
            try { day = (int)SkyManager.dayCount; } catch { }

            // Build diagnostic fields inline — same fields as roster scan
            string name = npc.EntityName;
            string cls = npc.EntityClass != null ? npc.EntityClass.entityClassName : "(null)";

            EnumSpawnerSource spawnerSrc = EnumSpawnerSource.Unknown;
            try { spawnerSrc = npc.GetSpawnerSource(); } catch { }

            bool canDespawn = false;
            try { canDespawn = npc.canDespawn(); } catch { }

            bool savedToFile = false;
            try { savedToFile = npc.IsSavedToFile(); } catch { }

            bool leaderCvar = false;
            try { leaderCvar = npc.Buffs.HasCustomVar("Leader"); } catch { }

            bool isDespawned = false;
            try { isDespawned = npc.IsDespawned; } catch { }

            Log.Out($"[NPC-DIAG] hired NPC {name} id={entityId} class={cls} REMOVED mid-session (day {day}, pos {npc.position.x:F0},{npc.position.z:F0}) spawnerSrc={spawnerSrc} canDespawn={canDespawn} savedToFile={savedToFile} leaderCvar={leaderCvar} isDespawned={isDespawned}");
        }

        private static EntityAlive FindNearbyNPC(EntityPlayer player)
        {
            float normalDistance = XNPCVoiceControlMod.GetChatDistance();
            float hiredDistance = XNPCVoiceControlMod.GetHiredChatDistance();
            float maxDistance = Mathf.Max(normalDistance, hiredDistance);

            var world = GameManager.Instance.World;
            if (world == null) return null;

            // Spatial query — only returns EntityAlive within bounds, not all world entities.
            // Hired NPCs get extended range; we query the larger radius and filter per-entity below.
            _nearbyBuffer.Clear();
            // Bounds(size) is total diameter, so multiply by 2 to cover full radius from center
            world.GetEntitiesInBounds(typeof(EntityAlive), new Bounds(player.position, Vector3.one * maxDistance * 2f), _nearbyBuffer);

            EntityAlive closestNPC = null;
            float closestDistance = maxDistance;

            for (int i = 0; i < _nearbyBuffer.Count; i++)
            {
                var alive = _nearbyBuffer[i] as EntityAlive;
                if (alive == null || !IsChatTarget(alive) || alive.entityId == player.entityId)
                    continue;

                float distance = Vector3.Distance(player.position, alive.position);
                // Hired NPCs get the extended range; others use normal range
                float allowedDistance = HasPlayerLeader(alive) ? hiredDistance : normalDistance;
                if (distance < closestDistance && distance <= allowedDistance)
                {
                    closestNPC = alive;
                    closestDistance = distance;
                }
            }

            return closestNPC;
        }

        private static void ProcessNPCChat(EntityPlayer player, EntityAlive npc, string message)
        {
            // Hired animals: phrase triggers only (movement/order commands), no LLM chat
            if (IsAnimal(npc))
            {
                Log.Debug(() => $"Target is a hired animal ({npc.EntityName}) — only command phrases allowed");
                string triggerResponse;
                if (PhraseTriggerHandler.Instance.Enabled &&
                    PhraseTriggerHandler.Instance.TryHandlePhrase(message, npc, player, npc.EntityName, out triggerResponse, false, false))
                {
                    Log.Debug(() => $"Animal command matched for {npc.EntityName}: {triggerResponse}");
                    if (player is EntityPlayerLocal localPlayer && !string.IsNullOrWhiteSpace(triggerResponse))
                    {
                        XNPCVoiceControl.UI.SubtitleManager.Instance.ShowSubtitle(npc.EntityName, triggerResponse);
                    }
                }
                return;
            }

            var chatComponent = GetOrCreate(npc);
            if (chatComponent == null)
            {
                Log.Warning("Could not create chat component for NPC");
                return;
            }

            // Process the message with player reference for actions
            // Tooltip is shown by NPCChatComponent internally
            chatComponent.ProcessPlayerMessage(message, player, false, response =>
            {
                Log.Debug(() => $"{chatComponent.NPCName}: {response}");
            });
        }
    }
}
