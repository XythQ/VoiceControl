using System.Collections.Generic;
using UnityEngine;
using XNPCVoiceControl;

// UAI discovery: <task class="DynamicFollow, 1-XNPCVoiceControl" />
// Unified distance-controlled follow task. Replaces the FollowSDX + BackupFromTargetSDX servo
// that structurally oscillated (static stop_distance can't track dynamic minDist).
//
// Servos the NPC to exactly minDist from leader with dead-band hysteresis — no boundary for
// two actions to fight over. The UAI action always wins on Follow order; this task internally
// decides move-vs-hold.
//
// Formation mode: when vcFormationDist > 0, targets a world-anchored socket at
// (bearing, metres). Bearing is compass degrees in Unity yaw terms (0=N, 90=E, 180=S, 270=W).
// Sockets are map-fixed — the leader turning does not move them.
//
// Per-entity state is dictionary-keyed (singleton lesson).
// Fully self-contained 3-tier recovery: direct servo → A* navigation → teleport.
namespace UAI
{
    public class UAITaskDynamicFollow : UAITaskBase
    {
        private const float DefaultMinDist = 2.5f;   // default vcFollowMin
        private const float EnterMoveThreshold = 0.5f; // start moving when error exceeds this
        private const float ExitMoveThreshold = 0.2f;  // stop moving when error below this
        private const float LeaderStationarySpeed = 0.5f; // below this, treat leader as stopped

        // Velocity servo — commands the speed needed to hold the slot (match leader + close gap).
        // Replaces the binary speed curve that used the NPC's authored speed.
        private const float CatchupGain = 1f;          // speed boost per metre of error
        private const float MaxCatchup = 3f;           // max catch-up speed bonus
        private const float SkatingCeiling = 1.25f;    // don't exceed ~1.25x run speed (animation skates)
        private const float WalkFloorFactor = 0.3f;    // floor: 30% of walk speed (no crawl-in-place)



        // Speed-scaled formation spacing — smooth effective slot distance (lerps toward base + running bonus).
        private const float FormDistLerpFactor = 0.25f;  // ~4 ticks to settle
        private const float FormDistRunningBonus = 2f;   // extra metres when leader is running
        private const float FormationMinDist = 2f;       // min socket radius — ~1 block clearance
        private static readonly Dictionary<int, float> _effectiveFormDist = new Dictionary<int, float>();

        // Per-entity state — task instances are singletons shared across all entities.
        private static readonly Dictionary<int, bool> _moving = new Dictionary<int, bool>();

        // Leader position tracking (velocity servo — shared by formation + plain follow).
        private static readonly Dictionary<int, Vector3> _leaderLastPos = new Dictionary<int, Vector3>();

        // Leader speed tracking (velocity servo — shared by formation + plain follow).
        private const float LeaderSpeedLerpFactor = 0.25f;  // smooth per-tick noise
        private static readonly Dictionary<int, float> _leaderSpeed = new Dictionary<int, float>();

        // Tier-1: A* recovery layer (ported from UAITaskPatrolRecorded).
        // Tier-2: teleport when truly wedged (A* failed after ~7s).
        // Self-movement stuck detection — measures NPC's own displacement, immune to leader movement.
        // "stuck" = NPC hasn't moved 0.3m in 3s (blocked by obstacle), not "error-to-target not improving"
        // (which false-triggers when leader walks away at constant speed).
        private const float RecoveryEnterTime = 3f;            // no self-movement for this long → enter A*
        private const float RecoveryRepathInterval = 2f;       // re-path every N seconds during recovery
        private const float RecoveryMinMove = 0.3f;            // NPC must move this far to reset checkpoint
        private const float RecoveryExitProgress = 0.5f;       // error must decrease by this much to exit A*
        private const float Tier2TeleportTime = 10f;           // no self-movement for this long → teleport

        // Per-entity recovery state.
        private static readonly Dictionary<int, bool> _recovering = new Dictionary<int, bool>();
        private static readonly Dictionary<int, float> _noProgressTime = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _repathTimer = new Dictionary<int, float>();
        private static readonly Dictionary<int, Vector3> _lastSelfPos = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, float> _recoveryEntryError = new Dictionary<int, float>();

        // Airborne starvation detection — logs when Update is starved for >2s (ladder lesson).
        private static readonly Dictionary<int, int> _airborneTick = new Dictionary<int, int>();

        // Rate-limited "no leader" log.
        private static readonly Dictionary<int, float> _noLeaderLog = new Dictionary<int, float>();

        // Freeze bisect — rate-limited debug log (every 2s per NPC).
        private static readonly Dictionary<int, float> _freezeDbgLog = new Dictionary<int, float>();
        private const float FreezeDbgInterval = 2f;

        public override void Start(Context _context)
        {
            base.Start(_context);
            int id = _context.Self.entityId;
            _moving[id] = false;
            _airborneTick.Remove(id);
            _noLeaderLog.Remove(id);
            _freezeDbgLog.Remove(id);
            _leaderLastPos.Remove(id);
            _effectiveFormDist.Remove(id);
            _leaderSpeed.Remove(id);
            // Recovery state.
            _recovering.Remove(id);
            _noProgressTime.Remove(id);
            _repathTimer.Remove(id);
            _lastSelfPos.Remove(id);
            _recoveryEntryError.Remove(id);
        }

        public override void Update(Context _context)
        {
            var self = _context.Self;
            int id = self.entityId;

            // Grounded gate — ladder/climb volume counts as grounded.
            bool grounded = self.onGround || self.IsInElevator();
            if (!grounded)
            {
                _airborneTick[id] = (_airborneTick.TryGetValue(id, out var at) ? at : 0) + 1;
                if (_airborneTick[id] >= 120) // ~2s at 60 ticks/s
                {
                    _airborneTick[id] = 0;
                    XNPCVoiceControl.Log.Out($"[FOLLOW] {self.EntityName} Update starved: onGround=false for >2s");
                }
                return;
            }
            _airborneTick.Remove(id);

            // Resolve leader.
            Entity leaderEntity = EntityUtilities.GetLeaderOrOwner(id);
            if (leaderEntity == null || !leaderEntity.IsAlive())
            {
                if (!_noLeaderLog.TryGetValue(id, out float lastT) || Time.time - lastT >= 5f)
                {
                    _noLeaderLog[id] = Time.time;
                    XNPCVoiceControl.Log.Debug(() => $"[FOLLOW] {self.EntityName} no leader — idle");
                }
                // Freeze bisect — log bail reason.
                if (!_freezeDbgLog.TryGetValue(id, out float lastDbgT) || Time.time - lastDbgT >= FreezeDbgInterval)
                {
                    _freezeDbgLog[id] = Time.time;
                    int currentOrder = (int)self.Buffs.GetCustomVar("CurrentOrder");
                    XNPCVoiceControl.Log.Debug(() => $"[FOLLOW-DBG] {self.EntityName} BAIL no-leader order={currentOrder}");
                }
                self.moveHelper.StopMove();
                return;
            }
            EntityAlive leader = leaderEntity as EntityAlive;
            if (leader == null)
            {
                self.moveHelper.StopMove();
                return;
            }

            // Read formation cvars (continuous angle + metres).
            float formAngle = EntityUtilities.GetCVarValue(id, "vcFormationAngle");
            float formDist = EntityUtilities.GetCVarValue(id, "vcFormationDist");

            bool isFormation = formDist > 0f;

            // Leader speed tracking (velocity servo — shared by formation + plain follow).
            // Measure delta every tick regardless of mode.
            if (!_leaderLastPos.ContainsKey(id))
                _leaderLastPos[id] = leader.position;
            Vector3 leaderDelta = leader.position - _leaderLastPos[id];
            float rawLeaderSpeed = leaderDelta.magnitude / Time.deltaTime;
            float currentSpeed = _leaderSpeed.TryGetValue(id, out var ls) ? ls : 0f;
            float smoothedLeaderSpeed = Mathf.Lerp(currentSpeed, rawLeaderSpeed, LeaderSpeedLerpFactor);
            _leaderSpeed[id] = smoothedLeaderSpeed;
            _leaderLastPos[id] = leader.position;



            // Horizontal vector and distance.
            Vector3 vec = self.position - leader.position;
            vec.y = 0f;
            float dist = vec.magnitude;

            // Leader running detection (shared by speed curve + formation spacing).
            bool leaderRunning = false;
            try
            {
                var tag = FastTags<TagGroup.Global>.Parse("running");
                leaderRunning = leader.CurrentMovementTag.Test_AllSet(tag);
            }
            catch { /* V4 entity — CurrentMovementTag may not exist */ }

            // Speed-scaled formation spacing: base tier distance + running bonus, smoothed.
            float effectiveFormDist = formDist; // default: raw cvar value
            if (isFormation)
            {
                formDist = Mathf.Max(formDist, FormationMinDist); // never place a socket inside 1-block room
                float targetDist = formDist + (leaderRunning ? FormDistRunningBonus : 0f);
                float current = _effectiveFormDist.TryGetValue(id, out var ef) ? ef : formDist;
                effectiveFormDist = Mathf.Lerp(current, targetDist, FormDistLerpFactor);
                _effectiveFormDist[id] = effectiveFormDist;
            }

            // Compute target position.
            Vector3 target;
            Vector3 slotDir = Vector3.zero; // for look-direction below

            if (isFormation)
            {
                // Formation slot: world-anchored compass bearing + metres from cvars (speed-scaled).
                slotDir = FormationUtils.GetSlotDirection(formAngle);
                target = leader.position + slotDir * effectiveFormDist;
            }
            else
            {
                // Plain follow: target at minDist from leader.
                float minDist = DefaultMinDist;
                float cvarVal = EntityUtilities.GetCVarValue(id, "vcFollowMin");
                if (cvarVal > 0f)
                    minDist = cvarVal;

                if (dist < 0.1f)
                {
                    // Degenerate — on top of leader, drop behind.
                    target = leader.position - leader.transform.forward * minDist;
                }
                else
                {
                    target = leader.position + vec.normalized * minDist;
                }
            }

            target.y = leader.position.y;

            // --- Phase 1 personal-space yield (active bubble) ---
            float minSep = XNPCVoiceControlMod.FollowMinSeparation;

            // (a) Clamp the target out of the bubble — never *aim* inside personal space.
            Vector3 fromLeader = target - leader.position; fromLeader.y = 0f;
            if (fromLeader.magnitude < minSep)
            {
                Vector3 dir = fromLeader.sqrMagnitude > 0.0001f ? fromLeader.normalized
                             : -leader.transform.forward; // degenerate: push behind
                target = leader.position + dir * minSep;
                target.y = leader.position.y;
            }

            // (b) Active yield — override the hold when the leader closes in.
            if (dist < minSep)
            {
                Vector3 away = dist > 0.01f ? vec.normalized : -leader.transform.forward;
                target = leader.position + away * (minSep + 0.3f);
                target.y = leader.position.y;
                _moving[id] = true;
                self.Buffs.RemoveBuff("RandomIdle");
            }

            // Dead-band hysteresis — move vs hold.
            // Use horizontal distance from NPC to computed target (correct in both modes).
            Vector3 toTarget = self.position - target;
            toTarget.y = 0f;
            float error = toTarget.magnitude;

            // Defensive: Stop() removes the key; UAI can call Update after Stop (patrol lesson).
            bool moving = _moving.TryGetValue(id, out var mv) && mv;
            if (moving)
            {
                // Currently moving — check if we've arrived AND leader is stopped.
                if (error <= ExitMoveThreshold && smoothedLeaderSpeed < LeaderStationarySpeed)
                {
                    _moving[id] = false;
                    self.moveHelper.StopMove();
                    if (self.navigator.getPath() != null)
                        self.navigator.clearPath();
                    // Arrived — add idle buff so gestures play while settled.
                    self.Buffs.AddBuff("RandomIdle");

                    // Settle-gate: apply crouch only when settled (not while moving to slot).
                    SCoreUtils.SetCrouching(_context, leader.IsCrouching);
                    return;
                }
            }
            else
            {
                // Currently holding — check if we need to move.
                if (error <= EnterMoveThreshold)
                {
                    // Settle-gate: apply crouch only when settled (not while moving to slot).
                    SCoreUtils.SetCrouching(_context, leader.IsCrouching);
                    return; // within band, stay idle
                }

                _moving[id] = true;
                // Starting to move — remove idle buff so movement animations play.
                self.Buffs.RemoveBuff("RandomIdle");
            }

            // --- 3-tier recovery system ---
            // Tier-0: direct SetMoveTo (precise distance setpoint)
            // Tier-1: A* navigator (ladders/stairs/doors) — enters after 3s no self-movement
            // Tier-2: teleport to target — fires after 10s no self-movement (A* had ~7s and failed)
            //
            // Self-movement checkpoint runs in BOTH modes. During A*, the navigator moves the NPC
            // → checkpoint resets → tier-2 never fires. Only a truly wedged NPC accumulates.
            bool recovering = _recovering.TryGetValue(id, out var rec) && rec;

            if (recovering)
            {
                // RECOVERY MODE (tier-1): use A* navigator instead of direct SetMoveTo.
                // Do NOT call SetMoveTo — it fights the navigator (patrol lesson).

                // Re-path every RepathInterval seconds (leader moves, target shifts).
                _repathTimer[id] = (_repathTimer.TryGetValue(id, out var rt) ? rt : 0f) + Time.deltaTime;
                if (_repathTimer[id] >= RecoveryRepathInterval)
                {
                    _repathTimer[id] = 0f;
                    SCoreUtils.FindPath(_context, target); // A* — fire-and-forget, entity pumps navigator itself
                }

                // Exit recovery: error decreased meaningfully from entry value, or arrived.
                float entryError = _recoveryEntryError.TryGetValue(id, out var ee) ? ee : float.MaxValue;
                if (error < entryError - RecoveryExitProgress)
                {
                    // A* rounded the obstacle — exit recovery, resume direct servo.
                    recovering = false;
                    _recovering[id] = false;
                    self.navigator.clearPath();
                    _noProgressTime[id] = 0f;
                    if (_lastSelfPos.ContainsKey(id))
                        _lastSelfPos[id] = self.position;
                    XNPCVoiceControl.Log.Out($"[FOLLOW] {self.EntityName} A* recovered — resuming direct servo");
                }
                else if (error <= ExitMoveThreshold)
                {
                    // Arrived at target.
                    recovering = false;
                    _recovering[id] = false;
                    self.navigator.clearPath();
                    _noProgressTime[id] = 0f;
                }

                // Self-movement checkpoint (unified — runs in both modes).
                if (!_lastSelfPos.ContainsKey(id))
                    _lastSelfPos[id] = self.position;

                float movedFromCheckpoint = (self.position - _lastSelfPos[id]).magnitude;
                if (movedFromCheckpoint > RecoveryMinMove)
                {
                    _lastSelfPos[id] = self.position;
                    _noProgressTime[id] = 0f;
                }
                else
                {
                    float elapsed = (_noProgressTime.TryGetValue(id, out var nt) ? nt : 0f) + Time.deltaTime;
                    _noProgressTime[id] = elapsed;

                    if (elapsed >= Tier2TeleportTime)
                    {
                        // TIER-2: A* failed after ~7s of trying — teleport to our computed target.
                        SCoreUtils.TeleportToPosition(_context, target);
                        recovering = false;
                        _recovering[id] = false;
                        self.navigator.clearPath();
                        _noProgressTime[id] = 0f;
                        _lastSelfPos[id] = self.position;
                        XNPCVoiceControl.Log.Out($"[FOLLOW] {self.EntityName} wedged {Tier2TeleportTime}s (A* failed) — teleported to target");
                        return; // skip direct servo after teleport
                    }
                }

                // If still recovering, skip the direct servo block below.
                if (recovering) return;
            }
            else if (moving)
            {
                // DIRECT SERVO MODE (tier-0): self-movement stuck detection.
                // Only check when actively moving — a holding/arrived NPC has ~0 motion legitimately.

                // Seed checkpoint on first move-tick.
                if (!_lastSelfPos.ContainsKey(id))
                    _lastSelfPos[id] = self.position;

                float movedFromCheckpoint = (self.position - _lastSelfPos[id]).magnitude; // 3D — vertical counts (ladders)
                if (movedFromCheckpoint > RecoveryMinMove)
                {
                    // Moved enough — reset checkpoint + timer.
                    _lastSelfPos[id] = self.position;
                    _noProgressTime[id] = 0f;
                }
                else
                {
                    // Not moved much — accumulate time.
                    float elapsed = (_noProgressTime.TryGetValue(id, out var nt) ? nt : 0f) + Time.deltaTime;
                    _noProgressTime[id] = elapsed;

                    if (elapsed >= RecoveryEnterTime)
                    {
                        // Enter A* recovery (tier-1).
                        recovering = true;
                        _recovering[id] = true;
                        self.moveHelper.hasNextPos = false; // critical — stale flag must not leak into A* movement (leak lesson)
                        _recoveryEntryError[id] = error;
                        XNPCVoiceControl.Log.Out($"[FOLLOW] {self.EntityName} stuck ({RecoveryMinMove}m/{elapsed:F0}s) — A* recovery");

                        // Initial path to target.
                        SCoreUtils.FindPath(_context, target);

                        // Skip direct servo this tick.
                        return;
                    }
                }
            }

            // --- Move toward target (direct servo) ---

            // Velocity servo: command the speed needed to hold the slot.
            // Match leader's actual speed + close the gap, clamped to animation-safe ceiling.
            float runSpeed = self.GetMoveSpeedAggro();
            float commandSpeed = smoothedLeaderSpeed + Mathf.Clamp(error * CatchupGain, 0f, MaxCatchup);
            commandSpeed = Mathf.Clamp(commandSpeed, self.GetMoveSpeed() * WalkFloorFactor, runSpeed * SkatingCeiling);

            // 3-arg SetMoveTo (2-arg hardcodes aggro speed — lesson).
            self.moveHelper.SetMoveTo(target, false, commandSpeed);

            // Engine blending + disables the 0.6m arrival slow-zone.
            // In formation mode, look along the slot direction (point→forward, rear→backward, flanks→outward).
            // Plain follow looks at leader (correct — you want to see your leader behind you).
            Vector3 lookTarget = leader.position;
            if (isFormation && slotDir.sqrMagnitude >= 0.001f)
                lookTarget = target + slotDir.normalized * 5f; // face along the slot's sector
            self.moveHelper.nextMoveToPos = lookTarget;
            self.moveHelper.hasNextPos = true;

            // Freeze bisect — rate-limited debug log.
            if (!_freezeDbgLog.TryGetValue(id, out float lastDbg) || Time.time - lastDbg >= FreezeDbgInterval)
            {
                _freezeDbgLog[id] = Time.time;
                int currentOrder = (int)self.Buffs.GetCustomVar("CurrentOrder");
                string recoveryStr = recovering ? "A*" : "direct";
                XNPCVoiceControl.Log.Debug(() => $"[FOLLOW-DBG] {self.EntityName} order={currentOrder} form={isFormation} err={error:F1} moving={moving} mode={recoveryStr} slotDist={(target-self.position).magnitude:F1}");
            }
        }

        public override void Stop(Context _context)
        {
            base.Stop(_context);
            int id = _context.Self.entityId;

            // Clean exit — don't leak idle buff into the next order.
            _context.Self.Buffs.RemoveBuff("RandomIdle");

            // Clear hasNextPos — Vector3 SetMoveTo never resets it (leak lesson).
            _context.Self.moveHelper.hasNextPos = false;

            // Clean per-entity state.
            _moving.Remove(id);
            _airborneTick.Remove(id);
            _noLeaderLog.Remove(id);
            _freezeDbgLog.Remove(id);
            _leaderLastPos.Remove(id);
            _recovering.Remove(id);
            _noProgressTime.Remove(id);
            _repathTimer.Remove(id);
            _lastSelfPos.Remove(id);
            _recoveryEntryError.Remove(id);
            _effectiveFormDist.Remove(id);
            _leaderSpeed.Remove(id);
        }

    }
}
