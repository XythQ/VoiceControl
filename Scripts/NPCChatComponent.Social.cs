using System;
using System.Collections.Generic;
using UnityEngine;
using XNPCVoiceControl.UI;

namespace XNPCVoiceControl
{
    public partial class NPCChatComponent : MonoBehaviour
    {
        #region Social (Proximity Greeting, Chat Pause, Leader Tracking)

        /// <summary>Set once per frame by UpdateChatPauseState(); read by UAI considerations.</summary>
        public bool ChatPauseActive { get; private set; }

        // MP Phase 1b — remote chat state (set by NetPackageVCChatState on server)
        private float _remoteChatUntil; // Time.realtimeSinceStartup when remote chat expires

        /// <summary>
        /// Used by UAI HasActiveChat consideration. Returns the value computed once per frame
        /// by UpdateChatPauseState(), OR true if a remote client has an active conversation
        /// that hasn't expired yet (45s timeout with 20s keepalives). When all are false,
        /// Wander wins and NPC moves on.
        /// </summary>
        public bool IsInChatPauseWindow => ChatPauseActive || Time.realtimeSinceStartup < _remoteChatUntil;

        /// <summary>
        /// Compute chat-pause state once per frame. Called from Update() before the buffer
        /// early-return so it runs even when the shadow buffer is empty.
        /// </summary>
        private void UpdateChatPauseState()
        {
            bool wasPauseActive = ChatPauseActive;
            if (_isWaitingForResponse || HasRotationHold)
            {
                _chatIdleTimeoutStart = 0f; // reset - conversation active
                _chatWanderCooldownEnd = 0f;
                ChatPauseActive = true;
                return;
            }

            // Hired NPCs: only hold for genuine conversation (waiting for response or rotation lock).
            // DynamicFollow + DynamicFollowBackup now cover the near-band positioning that the old
            // leader-in-range hack was plugging. The player-in-range idle/wander cycle below is
            // unhired-only.
            if (HasLeader())
            {
                ChatPauseActive = false;
                return;
            }

            // Check if any player is within interaction range (4m = UAI Chat action's TargetDistance max)
            var world = GameManager.Instance?.World;
            if (world == null)
            {
                ChatPauseActive = false;
                return;
            }
            var players = world.Players?.list;
            if (players == null || players.Count == 0)
            {
                ChatPauseActive = false;
                return;
            }
            float rangeSq = 16f; // 4m²
            bool playerInRange = false;
            foreach (var p in players)
            {
                if (p != null && !p.IsDead() && (_npcEntity.position - p.position).sqrMagnitude < rangeSq)
                {
                    playerInRange = true;
                    break;
                }
            }
            if (!playerInRange)
            {
                _chatIdleTimeoutStart = 0f; // reset when player leaves
                _chatWanderCooldownEnd = 0f;
                ChatPauseActive = false;
                return;
            }

            // Player is in range - start or check the idle timeout.
            // If no conversation started within 5 seconds, let Wander win for 5 seconds so NPC moves on.
            if (_chatIdleTimeoutStart <= 0f)
                _chatIdleTimeoutStart = Time.time;
            else if (Time.time - _chatIdleTimeoutStart > ChatIdleTimeoutSeconds)
            {
                _chatWanderCooldownEnd = Time.time + ChatWanderCooldownSeconds;
                _chatIdleTimeoutStart = 0f; // reset so a fresh window opens after cooldown
                if (Log.DebugMode && wasPauseActive)
                    Log.Debug(() => $"[UAI-DIAG] IsInChatPauseWindow: {_npcName} -> false (idle timeout after {ChatIdleTimeoutSeconds}s, wander cooldown {ChatWanderCooldownSeconds}s)");
                ChatPauseActive = false;
                return;
            }

            // Check if we're in the wander cooldown period — keep returning false so NPC keeps moving.
            if (Time.time < _chatWanderCooldownEnd)
            {
                if (Log.DebugMode && wasPauseActive)
                    Log.Debug(() => $"[UAI-DIAG] IsInChatPauseWindow: {_npcName} -> false (wander cooldown {(Time.time - (_chatWanderCooldownEnd - ChatWanderCooldownSeconds)):F1}s/{ChatWanderCooldownSeconds}s)");
                ChatPauseActive = false;
                return;
            }

            if (Log.DebugMode && !wasPauseActive)
                Log.Debug(() => $"[UAI-DIAG] IsInChatPauseWindow: {_npcName} -> true (player in range, idle {(Time.time - _chatIdleTimeoutStart):F1}s/{ChatIdleTimeoutSeconds}s)");
            ChatPauseActive = true;
        }

        /// <summary>
        /// Face another entity (used for NPC-to-NPC ambient chat). Separate from the existing
        /// player-facing _rotationTarget/_rotationHoldTimer pair to avoid type mismatch (that
        /// field is EntityPlayer-only) and to avoid the two facing-hold purposes stomping each
        /// other if both were active at overlapping times.
        /// </summary>
        public void FaceEntity(Entity target, float holdSeconds)
        {
            _npcFaceTarget = target;
            _npcFaceHoldTimer = holdSeconds;
        }

        // Proximity greeting check (replaces UAI ProactiveGreet action)
        private void UpdateProximityGreeting()
        {
            // Per-frame rotation hold during greeting: body turn + head tracking.
            // RotateTo here creates a natural brief pause (competes with wander) then UAI wins after hold expires.
            if (_rotationTarget != null)
            {
                _rotationHoldTimer -= Time.deltaTime;
                if (_rotationTarget.IsDead() || _rotationHoldTimer <= 0f ||
                    Vector3.Distance(_npcEntity.position, _rotationTarget.position) > ProactiveGreetRange * 1.5f)
                {
                    _rotationTarget = null;
                }
                else
                {
                    _npcEntity.RotateTo(_rotationTarget.position.x, _rotationTarget.position.y, _rotationTarget.position.z, 10f, 10f);
                    _npcEntity.SetLookPosition(_rotationTarget.getHeadPosition());
                }
            }

            // NPC-to-NPC facing hold (ambient chatter) — mirrors the player-facing block above.
            if (_npcFaceTarget != null)
            {
                _npcFaceHoldTimer -= Time.deltaTime;
                var targetAlive = _npcFaceTarget as EntityAlive;
                if ((targetAlive != null && targetAlive.IsDead()) || _npcFaceHoldTimer <= 0f)
                {
                    _npcFaceTarget = null;
                }
                else
                {
                    _npcEntity.RotateTo(_npcFaceTarget, 10f, 10f);
                    if (targetAlive != null)
                        _npcEntity.SetLookPosition(targetAlive.getHeadPosition());
                }
            }

            _proactiveCheckTimer -= Time.deltaTime;
            if (_proactiveCheckTimer <= 0f)
            {
                _proactiveCheckTimer = ProactiveCheckInterval;
                CheckProximityGreeting();

                // Billing reactor tick (Task 3B v2) — same cadence as proximity greeting, no new timer.
                CheckBillingReactor();

                // Follow-assist watchdog (stuck-follow catch-up teleport)
                CheckFollowAssist();
            }

            // Patrol recording — per-frame, fast bail when not recording.
            CheckPatrolRecording();

            // Chat inactivity timeout
            if (!_isWaitingForResponse && _conversationHistory.Count > 0)
            {
                float timeout = XNPCVoiceControlMod.Config?.ChatTimeout ?? 30f;
                _chatIdleTimer += Time.deltaTime;
                if (_chatIdleTimer >= timeout)
                {
                    _chatIdleTimer = 0f;
                    Interrupt();
                }
            }
            else
            {
                _chatIdleTimer = 0f;
            }
        }

        private void CheckProximityGreeting()
        {
            if (_npcEntity == null || _npcEntity.IsDead()) return;
            var world = GameManager.Instance?.World;
            if (world == null) return;

            Vector3 npcPos = _npcEntity.position;
            var players = world.Players.list;
            if (players.Count == 0) return;

            EntityPlayer closest = null;
            float closestDistSq = ProactiveGreetRange * ProactiveGreetRange;

            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || p.IsDead()) continue;
                float dSq = (p.position - npcPos).sqrMagnitude;
                if (dSq < closestDistSq) { closestDistSq = dSq; closest = p; }
            }

            // Track whether last-greeted player has left reset range
            if (_lastGreetedPlayerId != -1)
            {
                bool stillNear = false;
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p == null || p.entityId != _lastGreetedPlayerId) continue;
                    if ((p.position - npcPos).sqrMagnitude < GreetResetDistance * GreetResetDistance) { stillNear = true; break; }
                }
                if (!stillNear) _playerHasLeftRange = true;
            }

            if (closest == null) return;

            bool isNewContact = closest.entityId != _lastGreetedPlayerId || _playerHasLeftRange;
            if (!isNewContact) return;

            // Stamp last-greeted ID immediately (not inside TriggerProactiveGreeting which may
            // exit early on RNG/cooldown/LLM-busy). This prevents the 2s check cycle from
            // re-firing the one-shot turn and greeting attempt on every tick.
            _lastGreetedPlayerId = closest.entityId;
            _playerHasLeftRange = false;

            // One-shot body turn acknowledgment - the full rotation hold only engages if
            // TriggerProactiveGreeting actually plays a greeting (see call sites above).
            _npcEntity.RotateTo(closest.position.x, closest.position.y, closest.position.z, 10f, 10f);

            TriggerProactiveGreeting(closest);
        }

        #region Leader Management

        /// <summary>
        /// Get the entity ID of the NPC's leader (hiring player), or -1 if leaderless.
        /// </summary>
        public int LeaderEntityId => _leaderEntityId;

        /// <summary>
        /// Check if this NPC has a leader assigned.
        /// </summary>
        public bool HasLeader() => _leaderEntityId != -1;

        /// <summary>
        /// True when this NPC's leader is within the Core Chat action's hold range (~4m). Used to hold
        /// hired NPCs near their leader (via the Chat/IdleSDX action) instead of wandering through the
        /// 1-2.5m gap in the Hired UAI package. Leader-specific (not any player) so a hired NPC follows
        /// its own leader rather than idling next to a passer-by.
        /// </summary>
        private bool IsLeaderWithinChatRange()
        {
            if (_leaderEntityId < 0 || _npcEntity == null) return false;
            var leader = GameManager.Instance?.World?.GetEntity(_leaderEntityId) as EntityAlive;
            if (leader == null || leader.IsDead()) return false;
            const float rangeSq = 16f; // 4m² - matches the Chat action's TargetDistance max="4"
            return (_npcEntity.position - leader.position).sqrMagnitude < rangeSq;
        }

        /// <summary>
        /// Set the leader (hiring player) for this NPC.
        /// Only the leader can issue action orders to this NPC.
        /// </summary>
        public void SetLeader(EntityPlayer player)
        {
            if (player == null)
            {
                Log.Warning($"Cannot set null as leader for NPC {_npcName}");
                return;
            }
            _leaderEntityId = player.entityId;
            Log.Debug(() => $"Set leader for NPC {_npcName} (ID: {_entityId}) to player {player.entityId}");
        }

        /// <summary>
        /// Remove the leader from this NPC (makes it leaderless).
        /// Leaderless NPCs cannot receive orders from anyone.
        /// </summary>
        public void ClearLeader()
        {
            int oldLeader = _leaderEntityId;
            _leaderEntityId = -1;
            Log.Debug(() => $"Cleared leader for NPC {_npcName} (ID: {_entityId}), was player {oldLeader}");
        }

        /// <summary>
        /// Check if the given player is the leader of this NPC.
        /// Returns false if the NPC is leaderless or the player doesn't match.
        /// </summary>
        public bool IsPlayerLeader(EntityPlayer player)
        {
            if (player == null) return false;
            return _leaderEntityId == player.entityId;
        }

        /// <summary>
        /// Check if the given player can have actions executed on their behalf.
        /// Returns false if the NPC is leaderless or the player is not the leader.
        /// </summary>
        private bool CanExecuteActions(EntityPlayer player)
        {
            if (_leaderEntityId == -1)
            {
                // Leaderless NPC - no one can issue orders
                return false;
            }
            if (player == null)
            {
                return false;
            }
            return _leaderEntityId == player.entityId;
        }

        /// <summary>
        /// Try to read the leader from SCore's hiring/ownership tracking.
        /// Uses direct EntityUtilities calls, falls back to buffs CVar.
        /// </summary>
        private void TryReadLeaderFromSCore()
        {
            try
            {
                // SCore's GetLeaderOrOwner returns an Entity - cast to get entityId
                Entity leader = EntityUtilities.GetLeaderOrOwner(_entityId);
                if (leader != null && leader.entityId > 0)
                {
                    _leaderEntityId = leader.entityId;
                    if (!_hasLoggedLeaderRead)
                        Log.Debug(() => $"Read leader from SCore.GetLeaderOrOwner() for NPC {_npcName}: player {_leaderEntityId}");
                    MaybeStampHireDay(leader);
                    return;
                }

                // Fallback: try GetLeader and GetOwner individually
                leader = EntityUtilities.GetLeader(_entityId);
                if (leader != null && leader.entityId > 0)
                {
                    _leaderEntityId = leader.entityId;
                    if (!_hasLoggedLeaderRead)
                        Log.Debug(() => $"Read leader from SCore.GetLeader() for NPC {_npcName}: player {_leaderEntityId}");
                    MaybeStampHireDay(leader);
                    return;
                }

                leader = EntityUtilities.GetOwner(_entityId);
                if (leader != null && leader.entityId > 0)
                {
                    _leaderEntityId = leader.entityId;
                    if (!_hasLoggedLeaderRead)
                        Log.Debug(() => $"Read leader from SCore.GetOwner() for NPC {_npcName}: player {_leaderEntityId}");
                    MaybeStampHireDay(leader);
                    return;
                }

                // Last resort: read the "Leader" and "Owner" custom vars directly from the NPC's buffs
                foreach (string cvarName in new[] { "Leader", "Owner" })
                {
                    if (_npcEntity.Buffs.HasCustomVar(cvarName))
                    {
                        float cvarValue = _npcEntity.Buffs.GetCustomVar(cvarName);
                        int cvarId = (int)cvarValue;
                        if (cvarId > 0)
                        {
                            _leaderEntityId = cvarId;
                            if (!_hasLoggedLeaderRead)
                                Log.Debug(() => $"Read leader from NPC buffs CVar '{cvarName}' for NPC {_npcName}: player {cvarId}");
                            // For CVar path, resolve leader entity for hire-day stamp
                            Entity cvarLeader = GameManager.Instance?.World?.GetEntity(cvarId) as Entity;
                            MaybeStampHireDay(cvarLeader);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not read leader from SCore for NPC {_npcName}: {ex.Message}");
            }

            // Leader remains -1 (leaderless) if no leader found
            if (_leaderEntityId == -1 && !_hasLoggedLeaderRead)
            {
                Log.Out($"NPC {_npcName} has no leader (leaderless) - actions will be rejected until a leader is assigned");
            }
            _hasLoggedLeaderRead = true;
        }

        /// <summary>
        /// Stamp the hire day in the memory profile, firing once per NPC instance.
        /// Retries on subsequent polls until player ID resolves, then locks via _hireDayStamped.
        /// </summary>
        private void MaybeStampHireDay(Entity leader)
        {
            if (_hireDayStamped || _leaderEntityId <= 0)
                return; // already stamped, or not hired

            // Try instance cache first (updated on every chat interaction)
            string playerId = _lastPlayerPersistentId;
            if (string.IsNullOrEmpty(playerId) || playerId == "unknown")
                playerId = s_lastResolvedPlayerPersistentId;

            // Fallback: resolve directly from leader entity via ConnectionManager
            if ((string.IsNullOrEmpty(playerId) || playerId == "unknown") && leader is EntityPlayer leaderPlayer)
            {
                try
                {
                    var cInfo = SingletonMonoBehaviour<ConnectionManager>.Instance.Clients.ForEntityId(leaderPlayer.entityId);
                    if (cInfo != null)
                    {
                        PlatformUserIdentifierAbs pId = cInfo.CrossplatformId ?? cInfo.PlatformId;
                        if (pId != null)
                            playerId = pId.CombinedString;
                    }
                }
                catch { /* ConnectionManager access failed */ }
            }

            if (string.IsNullOrEmpty(playerId) || playerId == "unknown")
                return; // can't key the memory — will retry on next poll when ID is resolved

            NPCMemoryManager.Instance?.RecordHireDay(playerId, _npcName, (int)SkyManager.dayCount);

            // Seed billing cvars (Task 3B v3 — cvars only, no backing buff).
            try
            {
                _npcEntity.Buffs.SetCustomVar("RetainerWeek", 1f);
                _npcEntity.Buffs.SetCustomVar("RetainerNextDueDay", (float)SkyManager.dayCount + 7f);
                _npcEntity.Buffs.SetCustomVar("RetainerGraceDay", -1f);
                // v3 additions: per-contract state resets fresh each hire.
                _npcEntity.Buffs.SetCustomVar("RetainerAwaitingApproval", 0f);
                _npcEntity.Buffs.SetCustomVar("RetainerPromptDay", -1f);
                // Lifetime earnings persist across dismiss/rehire — only seed if absent.
                if (!_npcEntity.Buffs.HasCustomVar("RetainerEarnings"))
                    _npcEntity.Buffs.SetCustomVar("RetainerEarnings", 0f);
                Log.Debug(() => $"[BILLING] Contract seeded for {_npcName}: week=1, nextDue={SkyManager.dayCount + 7}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[BILLING] Failed to seed contract for {_npcName}: {ex.Message}");
            }

            _hireDayStamped = true;
        }

        /// <summary>
        /// Billing reactor — checks if weekly payment is due, prompts for approval, manages grace/dismiss.
        /// Runs at the same cadence as proximity greeting (~5-10s). Guarded on HasLeader() + cvar presence.
        /// </summary>
        private void CheckBillingReactor()
        {
            // Authority gate — only the server executes watchdogs; dedi clients are no-ops.
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) return;

            if (!HasLeader()) return;

            // Only NPCs with billing cvars are subject to billing.
            if (!_npcEntity.Buffs.HasCustomVar("RetainerNextDueDay")) return;

            int dayCount = (int)SkyManager.dayCount;
            float nextDueDay = _npcEntity.Buffs.GetCustomVar("RetainerNextDueDay");
            float graceDay = _npcEntity.Buffs.GetCustomVar("RetainerGraceDay");

            Core.BillingConfig billing = XNPCVoiceControlMod.Billing;
            if (billing == null) return;

            // Branch 1: Billing day arrived, not in grace → prompt for approval (v3).
            if (dayCount >= (int)nextDueDay && graceDay < 0f)
            {
                float awaiting = _npcEntity.Buffs.GetCustomVar("RetainerAwaitingApproval");
                if (awaiting <= 0f)
                {
                    // Not yet prompted this cycle — ask, don't deduct.
                    _npcEntity.Buffs.SetCustomVar("RetainerAwaitingApproval", 1f);
                    _npcEntity.Buffs.SetCustomVar("RetainerPromptDay", (float)dayCount);
                    int weekCost = ComputeWeekCost();
                    string prompt = $"I need {weekCost} {GetCurrencyName()} for this week — can you cover it?";
                    SubtitleManager.Instance.ShowSubtitle(_npcName, prompt, 25f);
                    _audioPlayer.SpeakStreaming(prompt, () => { });
                    Log.Out($"[BILLING] {_npcName} prompted for payment: {weekCost} {GetCurrencyName()}");
                }
                else
                {
                    // Already awaiting — check the no-response timeout fallback.
                    float promptDay = _npcEntity.Buffs.GetCustomVar("RetainerPromptDay");
                    if (dayCount - (int)promptDay >= billing.ApprovalTimeoutDays)
                    {
                        _npcEntity.Buffs.SetCustomVar("RetainerAwaitingApproval", 0f);
                        _npcEntity.Buffs.SetCustomVar("RetainerGraceDay", (float)dayCount);
                        string warn = $"Never heard back — I'll need that payment within {billing.GraceDays} days.";
                        SubtitleManager.Instance.ShowSubtitle(_npcName, warn, 25f);
                        _audioPlayer.SpeakStreaming(warn, () => { });
                        Log.Out($"[BILLING] {_npcName} approval timed out — grace period started ({billing.GraceDays} days)");
                    }
                    // else: still waiting, say nothing (don't re-prompt every tick)
                }
            }

            // Branch 2: In grace period, check if grace expired → dismiss.
            // (HasCustomVar("RetainerNextDueDay") guard at top prevents re-entry after cvar cleanup.)
            if (graceDay >= 0f)
            {
                if (dayCount - (int)graceDay >= billing.GraceDays)
                {
                    Log.Out($"[BILLING] {_npcName} grace expired — dismissing");
                    string dismissLine = "I'm done. Pay up or find someone else.";
                    SubtitleManager.Instance.ShowSubtitle(_npcName, dismissLine, 25f);
                    _audioPlayer.SpeakStreaming(dismissLine, () => { });

                    // Resolve leader entity and call Dismiss.
                    Entity leader = GameManager.Instance?.World?.GetEntity(_leaderEntityId);
                    if (leader is EntityPlayer leaderPlayer)
                    {
                        try
                        {
                            EntityUtilities.ExecuteCMD(_entityId, "Dismiss", leaderPlayer);
                            // Clear billing cvars (no backing buff — cvars are independent).
                            _npcEntity.Buffs.RemoveCustomVar("RetainerWeek");
                            _npcEntity.Buffs.RemoveCustomVar("RetainerNextDueDay");
                            _npcEntity.Buffs.RemoveCustomVar("RetainerGraceDay");
                            Log.Debug(() => $"[BILLING] Auto-dismissed {_npcName} for non-payment");
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"[BILLING] Auto-dismiss failed for {_npcName}: {ex.Message}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Compute the weekly cost for the current week. Shared by prompt text and deduction.
        /// Formula: round(baseCost * GrowthRate ^ min(week-1, MaxWeeks)).
        /// </summary>
        private int ComputeWeekCost()
        {
            Core.BillingConfig billing = XNPCVoiceControlMod.Billing;
            if (billing == null) return EntityUtilities.GetHireCost(_entityId);

            int baseCost = EntityUtilities.GetHireCost(_entityId);
            float week = _npcEntity.Buffs.GetCustomVar("RetainerWeek");
            int exponent = Mathf.Min((int)week - 1, billing.MaxWeeks);
            return (int)Mathf.Round(baseCost * Mathf.Pow(billing.GrowthRate, exponent));
        }

        /// <summary>
        /// Get the primary currency display name for prompt text.
        /// </summary>
        private string GetCurrencyName()
        {
            ItemValue currency = EntityUtilities.GetHireCurrency(_entityId);
            return currency?.ItemClass?.GetLocalizedItemName() ?? "coins";
        }

        /// <summary>
        /// Handle the player's yes/no response to a billing prompt.
        /// Clears RetainerAwaitingApproval and routes to deduction or grace.
        /// </summary>
        private void HandleBillingResponse(bool accepted)
        {
            _npcEntity.Buffs.SetCustomVar("RetainerAwaitingApproval", 0f);
            Core.BillingConfig billing = XNPCVoiceControlMod.Billing;

            if (accepted)
            {
                bool paid = TryDeductPayment();
                int dayCount = (int)SkyManager.dayCount;
                if (paid)
                {
                    float week = _npcEntity.Buffs.GetCustomVar("RetainerWeek") + 1f;
                    _npcEntity.Buffs.SetCustomVar("RetainerWeek", week);
                    _npcEntity.Buffs.SetCustomVar("RetainerNextDueDay", (float)(dayCount + 7));
                    string confirm = "Much obliged. See you next week.";
                    SubtitleManager.Instance.ShowSubtitle(_npcName, confirm, 25f);
                    _audioPlayer.SpeakStreaming(confirm, () => { });
                    Log.Debug(() => $"[BILLING] {_npcName} paid week {week}, next due day {dayCount + 7}");
                }
                else
                {
                    // Said yes but still can't actually afford it — same as a miss.
                    _npcEntity.Buffs.SetCustomVar("RetainerGraceDay", (float)dayCount);
                    string warn = $"That's not enough — I'll need it within {billing?.GraceDays ?? 2} days.";
                    SubtitleManager.Instance.ShowSubtitle(_npcName, warn, 25f);
                    _audioPlayer.SpeakStreaming(warn, () => { });
                    Log.Out($"[BILLING] {_npcName} approved but insufficient funds — grace period started");
                }
            }
            else
            {
                // Declined — starts grace immediately (symmetric with insufficient funds).
                _npcEntity.Buffs.SetCustomVar("RetainerGraceDay", (float)SkyManager.dayCount);
                string ack = $"Understood — but I'll need payment within {billing?.GraceDays ?? 2} days.";
                SubtitleManager.Instance.ShowSubtitle(_npcName, ack, 25f);
                _audioPlayer.SpeakStreaming(ack, () => { });
                Log.Out($"[BILLING] {_npcName} declined payment — grace period started");
            }
        }

        /// <summary>
        /// Attempt to deduct the weekly billing payment from the leader's inventory.
        /// Tries primary currency first, then alt currency fallback if configured.
        /// On success, accumulates RetainerEarnings. Returns true if fully deducted.
        /// </summary>
        private bool TryDeductPayment()
        {
            Entity leader = GameManager.Instance?.World?.GetEntity(_leaderEntityId);
            if (!(leader is EntityPlayer playerEntity))
            {
                Log.Warning($"[BILLING] {_npcName}: leader entity not found or not a player");
                return false;
            }

            int weekCost = ComputeWeekCost();
            ItemValue primaryCurrency = EntityUtilities.GetHireCurrency(_entityId);

            Log.Debug(() => $"[BILLING] {_npcName} week {(int)_npcEntity.Buffs.GetCustomVar("RetainerWeek")} cost: {weekCost}");

            // Try primary currency first.
            int remaining = weekCost;
            remaining = DeductFromInventory(playerEntity, primaryCurrency, remaining);

            if (remaining <= 0)
            {
                // Accumulate earnings.
                float earnings = _npcEntity.Buffs.GetCustomVar("RetainerEarnings");
                _npcEntity.Buffs.SetCustomVar("RetainerEarnings", earnings + weekCost);
                Log.Debug(() => $"[BILLING] {_npcName} paid {weekCost} in {primaryCurrency.ItemClass?.GetLocalizedItemName() ?? "coins"}, earnings={earnings + weekCost}");
                return true;
            }

            // Primary short — try alt currency fallback.
            PersonalityDefinition personality = _personality;
            if (personality != null && !string.IsNullOrEmpty(personality.AltPaymentItem) && personality.AltPaymentRatio > 0f)
            {
                ItemValue altCurrency = ItemClass.GetItem(personality.AltPaymentItem);
                if (altCurrency != null && !altCurrency.IsEmpty())
                {
                    // AltPaymentRatio = how many primary coins one alt item is worth.
                    // So altCountNeeded = ceil(remaining / ratio).
                    int altCountNeeded = (int)Mathf.Ceil((float)remaining / personality.AltPaymentRatio);
                    remaining = DeductFromInventory(playerEntity, altCurrency, altCountNeeded);

                    if (remaining <= 0)
                    {
                        // Accumulate earnings.
                        float earnings = _npcEntity.Buffs.GetCustomVar("RetainerEarnings");
                        _npcEntity.Buffs.SetCustomVar("RetainerEarnings", earnings + weekCost);
                        Log.Debug(() => $"[BILLING] {_npcName} paid {altCountNeeded} {altCurrency.ItemClass?.GetLocalizedItemName() ?? "items"} (ratio {personality.AltPaymentRatio}), earnings={earnings + weekCost}");
                        return true;
                    }
                }
            }

            Log.Debug(() => $"[BILLING] {_npcName} short — need {remaining} more (primary={primaryCurrency.ItemClass?.GetLocalizedItemName() ?? "coins"})");
            return false;
        }

        /// <summary>
        /// Deduct up to _count items from player's inventory (equipment) + bag (backpack/toolbelt).
        /// Returns the remaining amount still needed (0 = fully deducted, >0 = shortfall).
        /// </summary>
        private static int DeductFromInventory(EntityPlayer player, ItemValue itemValue, int count)
        {
            if (itemValue == null || itemValue.IsEmpty()) return count;

            // Check inventory (equipment slots).
            if (player.inventory != null)
            {
                int avail = player.inventory.GetItemCount(itemValue);
                if (avail > 0)
                {
                    int take = Mathf.Min(avail, count);
                    player.inventory.DecItem(itemValue, take);
                    count -= take;
                }
            }

            if (count <= 0) return 0;

            // Check bag (backpack/toolbelt).
            if (player.bag != null)
            {
                int avail = player.bag.GetItemCount(itemValue);
                if (avail > 0)
                {
                    int take = Mathf.Min(avail, count);
                    player.bag.DecItem(itemValue, take);
                    count -= take;
                }
            }

            return count; // remaining shortfall
        }

        #endregion Leader Management

        #region Follow-Assist Watchdog

        /// <summary>
        /// Detects stuck-following NPCs (spiral stairs, tight interiors) and fires a catch-up teleport.
        /// Called every 2s from the proactive check timer. No new timer needed.
        ///
        /// NOTE (2026-07-14): Follow order is now gated — UAITaskDynamicFollow owns the full
        /// 3-tier recovery system (direct → A* → teleport). This watchdog only serves non-Follow
        /// orders. If no non-Follow order ever reaches here, this is dead code — delete next pass.
        /// </summary>
        private void CheckFollowAssist()
        {
            // Authority gate — only the server executes watchdogs; dedi clients are no-ops.
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) return;

            // Follow order: UAITaskDynamicFollow owns stuck detection now (3-tier: direct → A* → teleport).
            // Skip to avoid racing with the task's own recovery system.
            if (EntityUtilities.GetCurrentOrder(_entityId) == EntityUtilities.Orders.Follow)
            {
                _followAssistWindowStart = -1f;
                return;
            }

            Core.FollowAssistConfig config = XNPCVoiceControlMod.FollowAssist;
            if (config == null || !config.Enabled)
            {
                _followAssistWindowStart = -1f;
                return;
            }

            // Guard: must have a leader.
            if (!HasLeader())
            {
                _followAssistWindowStart = -1f;
                return;
            }

            // Guard: only assist NPCs on Follow order (never Guard/Stay/Patrol).
            if (EntityUtilities.GetCurrentOrder(_entityId) != EntityUtilities.Orders.Follow)
            {
                _followAssistWindowStart = -1f;
                return;
            }

            // Guard: don't interfere while recording a patrol route — the NPC is supposed to
            // follow the leader, and teleporting creates gaps in the breadcrumb trail.
            if (_recordingPatrol)
            {
                _followAssistWindowStart = -1f;
                return;
            }

            // Guard: don't yank an NPC that is actively chatting.
            if (ChatPauseActive)
            {
                _followAssistWindowStart = -1f;
                return;
            }

            // Guard: don't yank an NPC out of combat.
            if (_npcEntity.GetAttackTarget() != null || _npcEntity.GetRevengeTarget() != null)
            {
                _followAssistWindowStart = -1f;
                return;
            }

            // Resolve leader entity.
            Entity leader = GameManager.Instance?.World?.GetEntity(_leaderEntityId);
            if (leader == null || !leader.IsAlive())
            {
                _followAssistWindowStart = -1f;
                return;
            }

            // Guard: don't teleport toward a falling/jumping player.
            if (!leader.onGround)
            {
                _followAssistWindowStart = -1f;
                return;
            }

            // Compute effective hold distance — accounts for formation slot distance.
            float effectiveHold;
            bool isFormation;
            float formDist = EntityUtilities.GetCVarValue(_entityId, "vcFormationDist");
            if (formDist > 0f)
            {
                // Formation mode — use the continuous formation distance.
                effectiveHold = formDist;
                isFormation = true;
            }
            else
            {
                // Plain follow — use vcFollowMin.
                effectiveHold = EntityUtilities.GetCVarValue(_entityId, "vcFollowMin");
                if (effectiveHold <= 0f) effectiveHold = 2.5f;
                isFormation = false;
            }

            // Compute slot target for formation mode; plain follow uses behind-leader.
            Vector3 slotTarget;
            if (isFormation)
            {
                float formAngle = EntityUtilities.GetCVarValue(_entityId, "vcFormationAngle");
                Vector3 slotDir = FormationUtils.GetSlotDirection(formAngle);
                slotTarget = leader.position + slotDir * effectiveHold;
                slotTarget.y = leader.position.y;
            }
            else
            {
                slotTarget = leader.position - leader.transform.forward * effectiveHold;
                slotTarget.y = leader.position.y;
            }

            // Distance to the target position (slot or behind-leader).
            float distToTarget = Vector3.Distance(_npcEntity.position, slotTarget);

            // Guard: must be separated enough from leader to care.
            float dist = Vector3.Distance(_npcEntity.position, leader.position);
            if (dist <= config.MinSeparation)
            {
                _followAssistWindowStart = -1f;
                return;
            }

            // Guard: don't arm when NPC is already near its target position.
            // Without this, the watchdog triggers because the NPC stops moving at hold distance
            // and CheckFollowAssist sees "no progress" → teleport.
            if (distToTarget <= effectiveHold + 1.5f)
            {
                _followAssistWindowStart = -1f;
                return;
            }

            // Guard: cooldown between assists.
            float now = Time.realtimeSinceStartup;
            if (now - _lastFollowAssistTime < config.CooldownSeconds)
            {
                _followAssistWindowStart = -1f;
                return;
            }

            // --- Progress tracking (distance to slot target, not leader) ---

            // Window not yet armed → arm it.
            if (_followAssistWindowStart < 0f)
            {
                _followAssistWindowStart = now;
                _followAssistBestDist = distToTarget;
                Log.Debug(() => $"[FOLLOW-ASSIST] {_npcName} armed: distToTarget={distToTarget:F1}");
                return;
            }

            // Progress made (distance to target shrank by epsilon) → re-arm with new best.
            if (distToTarget < _followAssistBestDist - config.ProgressEpsilon)
            {
                _followAssistBestDist = distToTarget;
                _followAssistWindowStart = now;
                Log.Debug(() => $"[FOLLOW-ASSIST] {_npcName} progress: distToTarget={distToTarget:F1}");
                return;
            }

            // --- Fire: no progress for the full window ---
            if (now - _followAssistWindowStart >= config.NoProgressSeconds)
            {
                _npcEntity.SetPosition(slotTarget);
                _lastFollowAssistTime = now;
                _followAssistWindowStart = -1f;
                _followAssistBestDist = float.MaxValue;

                Log.Out($"[FOLLOW-ASSIST] {_npcName} caught up: was {distToTarget:F1}m from target, no progress for {config.NoProgressSeconds}s");
            }
        }

        #endregion Follow-Assist Watchdog

        #region Patrol Recording

        /// <summary>True while the NPC is recording waypoints (SetPatrolPoint phase).</summary>
        public void SetPatrolRecording(bool active)
        {
            _recordingPatrol = active;
            if (active) _lastRecordedBlock = new Vector3i(int.MinValue, int.MinValue, int.MinValue); // reset sentinel
        }

        /// <summary>True while the NPC is actively looping a patrol route.</summary>
        public void SetActivelyPatrolling(bool active)
        {
            _activelyPatrolling = active;
        }

        /// <summary>True while the NPC is recording waypoints (SetPatrolPoint phase).</summary>
        public bool IsRecordingPatrol => _recordingPatrol;

        /// <summary>True while the NPC is actively looping a patrol route.</summary>
        public bool IsActivelyPatrolling => _activelyPatrolling;

        /// <summary>True when the NPC is recording or patrolling — used by CancelPatrolRecord's ExecuteMatch gate.</summary>
        public bool IsRecordingOrPatrolling => _recordingPatrol || _activelyPatrolling;

        /// <summary>
        /// Records leader position as a patrol waypoint while recording flag is set.
        /// Called every Update() frame — fast bail when not recording. Snaps to block centers
        /// so walking lays a contiguous breadcrumb trail (one point per block, ~1m apart).
        /// </summary>
        private void CheckPatrolRecording()
        {
            // Authority gate — only the server executes watchdogs; dedi clients are no-ops.
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) return;

            if (!_recordingPatrol) return;
            if (!HasLeader()) return;
            Entity leader = GameManager.Instance?.World?.GetEntity(_leaderEntityId);
            if (leader == null || !leader.IsAlive()) return;

            if (_npcEntity is IEntityOrderReceiverSDX r)
            {
                Vector3 support = GameManager.Instance.World.FindSupportingBlockPos(leader.position);
                Vector3i block = new Vector3i(Utils.Fastfloor(support.x), Utils.Fastfloor(support.y), Utils.Fastfloor(support.z));
                if (block == _lastRecordedBlock) return;
                _lastRecordedBlock = block;

                // X/Z = block center; Y = NPC's actual position (not floored).
                // Floored Y stores a waypoint exactly 1.0 below feet level,
                // causing constant vErr=1.00 on-path which triggers false off-path detection.
                Vector3 point = new Vector3(block.x + 0.5f, leader.position.y, block.z + 0.5f);
                r.UpdatePatrolPoints(point);

                // Recording visibility logging — first waypoint + every 10th (~10m walked).
                int count = r.PatrolCoordinates.Count;
                if (count == 1)
                    Log.Out($"[PATROL-REC] {_npcName}: recording started at {block}");
                else if (count % 10 == 0)
                    Log.Out($"[PATROL-REC] {_npcName}: {count} points");
            }
        }

        #endregion Patrol Recording

        #region Remote Chat State (MP Phase 1b)

        /// <summary>
        /// Called by NetPackageVCChatState.ProcessPackage on the server.
        /// On true: set _remoteChatUntil = now + 45s (each keepalive refreshes).
        /// On false: clear to 0.
        /// </summary>
        public void SetRemoteChatActive(bool active)
        {
            if (active)
                _remoteChatUntil = Time.realtimeSinceStartup + 45f;
            else
                _remoteChatUntil = 0f;
        }

        #endregion Remote Chat State

        #endregion Social
    }
}
