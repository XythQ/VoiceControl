using System.Collections.Generic;
using UnityEngine;

// UAI discovery: <task class="PatrolRecorded, 1-XNPCVoiceControl" />
// Path-Follower V3.2 — engine-native next-point targeting + off-path A* recovery.
// Normal mode: target = B (next waypoint), nextMoveToPos = waypoint after that.
//   Engine blends yaw toward nextMoveToPos as NPC approaches moveToPos.
// Recovery mode: when vertically off-path (>1.25m for 0.5s), hand movement to A* navigator
//   which knows ladders/stairs. Resume direct follow once back on the line.
// Advance at t >= 0.999f || dist-to-B < 0.35m (slow-zone gone with hasNextPos).
// Travel-direction convention: all segment vectors follow `dir`, so idx=count-1, dir=-1 is valid.

namespace UAI
{
    public class UAITaskPatrolRecorded : UAITaskBase
    {
        // V3.2 constants.
        private const float VertexAdvanceRadius = 0.35f;       // snap to next vertex when closer than this
        private const float OffPathVThreshold = 1.25f;         // vertical error to enter recovery
        private const float OnPathVThreshold = 0.75f;          // vertical error to exit recovery
        private const float OnPathHThreshold = 1.0f;           // horizontal distance to segment to exit recovery
        private const float OffPathDebounceTime = 0.5f;        // must be off-path this long before entering recovery
        private const float RepathInterval = 2f;               // re-path to B every N seconds during recovery

        // Progress-based stuck detection thresholds (two-tier).
        private const float ProgressEpsilon = 0.02f;           // progress scalar must increase by this much
        private const float SkipWaypointAfterSeconds = 4f;     // tier 1 — skip blocked segment
        private const float TeleportAfterSeconds = 10f;        // tier 2 — last resort

        // Task instances are singletons shared across all entities running this action,
        // so state must be dictionary-keyed, never a plain field.
        private static readonly Dictionary<int, int> _waypointIndex = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> _direction = new Dictionary<int, int>();
        private static readonly Dictionary<int, float> _segT = new Dictionary<int, float>();

        // Stuck detection.
        private static readonly Dictionary<int, float> _bestProgress = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _noProgressTime = new Dictionary<int, float>();
        private static readonly Dictionary<int, int> _stuckTier = new Dictionary<int, int>();

        // Recovery mode state.
        private static readonly Dictionary<int, bool> _recovering = new Dictionary<int, bool>();
        private static readonly Dictionary<int, float> _offPathTime = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _repathTimer = new Dictionary<int, float>();

        // Debug: periodic logging (every 60 ticks ≈ 1s).
        private static readonly Dictionary<int, int> _debugTick = new Dictionary<int, int>();

        // Airborne starvation detection — logs when Update is starved for >2s.
        private static readonly Dictionary<int, int> _airborneTick = new Dictionary<int, int>();

        public override void Start(Context _context)
        {
            base.Start(_context);
            var self = _context.Self;
            int entityId = self.entityId;

            if (!(self is IEntityOrderReceiverSDX r) || r.PatrolCoordinates.Count < 2)
            {
                Stop(_context);
                return;
            }

            bool resuming = _waypointIndex.ContainsKey(entityId);
            int count = r.PatrolCoordinates.Count;

            if (!resuming)
            {
                _waypointIndex[entityId] = 0;
                _direction[entityId] = 1;
                _segT[entityId] = 0f;
            }

            // Global-project: scan all segments, pick nearest to NPC (horizontal distance).
            int bestSeg = _waypointIndex[entityId];
            float bestT = _segT[entityId];
            float bestDist = float.MaxValue;

            for (int i = 0; i < count - 1; i++)
            {
                Vector3 segDir = r.PatrolCoordinates[i + 1] - r.PatrolCoordinates[i];
                segDir.y = 0f;
                float segLenSq = segDir.x * segDir.x + segDir.z * segDir.z;
                if (segLenSq < 0.001f) continue;

                Vector3 toP = self.position - r.PatrolCoordinates[i];
                toP.y = 0f;
                float t = Mathf.Clamp01((toP.x * segDir.x + toP.z * segDir.z) / segLenSq);

                Vector3 proj = r.PatrolCoordinates[i] + segDir * t;
                proj.y = r.PatrolCoordinates[i].y;
                float dist = (self.position - proj).sqrMagnitude;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestSeg = i;
                    bestT = t;
                }
            }

            _waypointIndex[entityId] = bestSeg;
            _segT[entityId] = bestT;
            if (!resuming)
                _direction[entityId] = 1; // fresh start always forward

            ResetProgress(entityId);

            // SCore persists the Patrol order in the save — after world reload this task
            // resumes with no mod code involved, leaving _activelyPatrolling false and
            // breaking the voice-cancel gate. The task is the one place that always knows
            // a patrol is running, so set the flag here. Set-only: do NOT clear in Stop(),
            // which fires on every UAI action switch (combat interrupts would wrongly
            // clear it mid-patrol). Cancel/dialog handlers own clearing.
            if (XNPCVoiceControl.Core.ChatComponentManager.TryGet(entityId, out var chatComp))
                chatComp.SetActivelyPatrolling(true);
        }

        public override void Update(Context _context)
        {
            var self = _context.Self;
            int entityId = self.entityId;

            // Ladder/climb volume counts as grounded for patrol logic.
            // IsInElevator() is the engine's ladder-volume check — same one UpdateMoveHelper uses.
            bool grounded = self.onGround || self.IsInElevator();
            if (!grounded)
            {
                _airborneTick[entityId] = (_airborneTick.TryGetValue(entityId, out var at) ? at : 0) + 1;
                if (_airborneTick[entityId] >= 120) // ~2s at 60 ticks/s
                {
                    _airborneTick[entityId] = 0; // rate-limit: log once per starvation episode
                    XNPCVoiceControl.Log.Out($"[PATROL] {self.EntityName} Update starved: onGround=false for >2s (falling or stuck in climb state)");
                }
                return;
            }
            _airborneTick.Remove(entityId);
            if (!(self is IEntityOrderReceiverSDX r) || r.PatrolCoordinates.Count < 2)
            {
                Stop(_context);
                return;
            }

            int count = r.PatrolCoordinates.Count;

            // Defensive re-entry guard: UAI can call Update after Stop removed state.
            if (!_waypointIndex.ContainsKey(entityId))
            {
                Start(_context);
                return;
            }

            int idx = _waypointIndex[entityId];
            int dir = _direction[entityId];

            base.Update(_context);

            // Guard: if idx+dir is out of bounds, flip direction immediately.
            // Can happen after skip/teleport lands us at an endpoint.
            if (idx + dir < 0 || idx + dir >= count)
            {
                _direction[entityId] = -dir;
                dir = -dir;
                ResetProgress(entityId);

                // After flip, advance idx so target != position (avoids instant StopMove).
                int newNext = idx + dir;
                if (newNext >= 0 && newNext < count)
                {
                    idx = newNext;
                    _waypointIndex[entityId] = idx;
                }
            }

            // --- Compute t on current segment (travel-direction aware) ---
            Vector3 segDir = r.PatrolCoordinates[idx + dir] - r.PatrolCoordinates[idx];
            segDir.y = 0f;
            float segLenSq = segDir.x * segDir.x + segDir.z * segDir.z;

            float t;
            if (segLenSq > 0.001f)
            {
                Vector3 toP = self.position - r.PatrolCoordinates[idx];
                toP.y = 0f;
                t = Mathf.Clamp01((toP.x * segDir.x + toP.z * segDir.z) / segLenSq);
            }
            else
            {
                t = 0f;
            }

            // --- Compute vertical error for off-path detection ---
            Vector3 A = r.PatrolCoordinates[idx];
            float expectedY = Mathf.Lerp(A.y, r.PatrolCoordinates[idx + dir].y, t);
            float vErr = Mathf.Abs(self.position.y - expectedY);

            // --- Off-path debounce accumulator (runs in both modes) ---
            if (vErr > OffPathVThreshold)
            {
                _offPathTime[entityId] = (_offPathTime.TryGetValue(entityId, out var ot) ? ot : 0f) + Time.deltaTime;
            }
            else
            {
                _offPathTime[entityId] = 0f;
            }

            // --- Enter RECOVER mode when vertically off-path for debounce time ---
            bool recovering = _recovering.TryGetValue(entityId, out var rec) ? rec : false;
            if (!recovering && _offPathTime[entityId] >= OffPathDebounceTime)
            {
                recovering = true;
                _recovering[entityId] = true;
                self.moveHelper.hasNextPos = false; // critical — stale flag must not leak into A* movement
                XNPCVoiceControl.Log.Out($"[PATROL] {self.EntityName} off path (vErr={vErr:F1}) — A* recovery to waypoint {idx + dir}");
            }

            if (recovering)
            {
                // --- RECOVERY MODE: use A* navigator instead of direct SetMoveTo ---
                Vector3 recoverTarget = r.PatrolCoordinates[idx + dir];

                // Re-path every RepathInterval seconds.
                _repathTimer[entityId] = (_repathTimer.TryGetValue(entityId, out var rt) ? rt : 0f) + Time.deltaTime;
                if (_repathTimer[entityId] >= RepathInterval)
                {
                    _repathTimer[entityId] = 0f;
                    SCoreUtils.FindPath(_context, recoverTarget); // A* + GetMoveToLocation handles ladders
                }

                // --- Stuck detection in RECOVER: use negative 3D distance to target ---
                float recoverP = -(recoverTarget - self.position).magnitude;
                float recoverBest = _bestProgress.TryGetValue(entityId, out var recoverBp) ? recoverBp : float.NegativeInfinity;

                if (recoverP > recoverBest + ProgressEpsilon)
                {
                    _bestProgress[entityId] = recoverP;
                    _noProgressTime[entityId] = 0f;
                    _stuckTier[entityId] = 0;
                }
                else
                {
                    float elapsed = (_noProgressTime.TryGetValue(entityId, out var nt) ? nt : 0f) + Time.deltaTime;
                    _noProgressTime[entityId] = elapsed;
                    int tier = _stuckTier.TryGetValue(entityId, out var ti) ? ti : 0;

                    if (tier < 1 && elapsed >= SkipWaypointAfterSeconds)
                    {
                        // Tier 1: skip — advance idx by dir and re-path.
                        _stuckTier[entityId] = 1;
                        int skipIdx = idx + dir;
                        if (skipIdx >= count)
                        {
                            skipIdx = count - 2;
                            _direction[entityId] = -1;
                        }
                        else if (skipIdx < 0)
                        {
                            skipIdx = 1;
                            _direction[entityId] = 1;
                        }
                        _waypointIndex[entityId] = skipIdx;
                        _segT[entityId] = 0f;
                        ResetProgress(entityId);
                        // Re-path to new target immediately.
                        int rp = skipIdx + _direction[entityId];
                        if (rp < 0 || rp >= count) rp = skipIdx;
                        SCoreUtils.FindPath(_context, r.PatrolCoordinates[rp]);
                        XNPCVoiceControl.Log.Out($"[PATROL] {self.EntityName} no progress {SkipWaypointAfterSeconds}s in recovery — skipping to waypoint {_waypointIndex[entityId]}");
                    }
                    else if (elapsed >= TeleportAfterSeconds)
                    {
                        // Tier 2: teleport to current segment start.
                        SCoreUtils.TeleportToPosition(_context, A);
                        ResetProgress(entityId);
                        XNPCVoiceControl.Log.Out($"[PATROL] {self.EntityName} stuck {TeleportAfterSeconds}s in recovery — teleported to waypoint");
                    }
                }

                // --- Exit RECOVER when back on path ---
                bool recovered = false;
                if (vErr < OnPathVThreshold)
                {
                    // Horizontal distance to segment projection point.
                    Vector3 projPoint = A + segDir * t;
                    projPoint.y = self.position.y; // ignore Y for horizontal distance
                    float hDist = (self.position - projPoint).magnitude;

                    if (hDist < OnPathHThreshold)
                        recovered = true;
                }
                // Belt-and-braces: also exit when arrived at the waypoint itself.
                if ((recoverTarget - self.position).magnitude < 1.0f)
                    recovered = true;

                if (recovered)
                {
                    recovering = false;
                    _recovering[entityId] = false;
                    self.navigator.clearPath(); // so A* doesn't fight SetMoveTo
                    _offPathTime[entityId] = 0f;
                    ResetProgress(entityId);
                    XNPCVoiceControl.Log.Out($"[PATROL] {self.EntityName} recovered — resuming direct follow");
                }

                if (recovering) return; // skip normal follow block
            }

            // --- Advance loop: walk idx forward while we've reached the next vertex ---
            // Reverted to t >= 0.999f || dist < VertexAdvanceRadius (slow-zone gone with hasNextPos).
            while (t >= 0.999f || IsCloseToNext(r, idx, dir, count, self.position))
            {
                int nextIdx = idx + dir;

                // Advance to next vertex.
                idx = nextIdx;
                _waypointIndex[entityId] = idx;

                // Unified flip: if idx+dir is now out of bounds, we've reached an endpoint — flip direction.
                // Forward arrival at count-1 → segment becomes count-1 → count-2 (dir=-1).
                // Backward arrival at 0 → segment 0 → 1 (dir=+1).
                if (idx + dir < 0 || idx + dir >= count)
                {
                    _direction[entityId] = -dir;
                    dir = -dir;
                    ResetProgress(entityId); // direction flip = fresh progress window
                }

                // Recompute segDir/t against the now-valid segment.
                segDir = r.PatrolCoordinates[idx + dir] - r.PatrolCoordinates[idx];
                segDir.y = 0f;
                segLenSq = segDir.x * segDir.x + segDir.z * segDir.z;

                if (segLenSq > 0.001f)
                {
                    Vector3 toP = self.position - r.PatrolCoordinates[idx];
                    toP.y = 0f;
                    t = Mathf.Clamp01((toP.x * segDir.x + toP.z * segDir.z) / segLenSq);
                }
                else
                {
                    t = 0f;
                }
                break; // always break after advancing (recompute loop condition next Update)
            }

            _segT[entityId] = t;

            // --- Final segment (travel-direction aware) ---
            idx = _waypointIndex[entityId];
            dir = _direction[entityId];

            A = r.PatrolCoordinates[idx];
            Vector3 B = r.PatrolCoordinates[idx + dir];

            // --- Target = next waypoint directly (no push, no clamp) ---
            Vector3 target = B;

            // 3-arg overload: explicit walk speed. The 2-arg overload hardcodes aggro/run speed.
            self.moveHelper.SetMoveTo(target, false, self.GetMoveSpeed());

            // --- Engine-native next-point blending ---
            // Set nextMoveToPos to the waypoint after B so the engine blends yaw smoothly
            // as NPC approaches B. This eliminates micro-pauses at waypoint boundaries
            // and produces natural corner turns without any custom smoothing math.
            int nextVert = idx + dir * 2;
            if (nextVert < 0) nextVert = 1;                    // past start — blend toward the return leg
            if (nextVert >= count) nextVert = count - 2;       // past end — same
            self.moveHelper.nextMoveToPos = r.PatrolCoordinates[nextVert];
            self.moveHelper.hasNextPos = true;

            // --- Progress scalar for stuck detection ---
            // Strictly increasing along travel in BOTH directions.
            // Forward: idx increases, t goes 0→1 → p = idx + t increases.
            // Backward: idx decreases, t goes 0→1 (along travel) → p = (count-1) - idx + t increases.
            // Continuity at vertex crossing verified: forward t=1→advance→idx+1,t=0 same p;
            // backward t=1→advance→idx-1,t=0 same p.
            float p;
            if (dir > 0)
                p = idx + t;
            else
                p = (count - 1) - idx + t;

            // Sentinel: NegativeInfinity → first tick always initializes naturally.
            float best = _bestProgress.TryGetValue(entityId, out var bp) ? bp : float.NegativeInfinity;

            // Gate: only accept new _bestProgress when on-path (vErr <= threshold).
            // Belt-and-braces against fake progress during the 0.5s debounce window.
            bool acceptProgress = vErr <= OffPathVThreshold;

            if (acceptProgress && p > best + ProgressEpsilon)
            {
                _bestProgress[entityId] = p;
                _noProgressTime[entityId] = 0f;
                _stuckTier[entityId] = 0;
            }
            else if (!acceptProgress || p <= best + ProgressEpsilon)
            {
                float elapsed = (_noProgressTime.TryGetValue(entityId, out var nt) ? nt : 0f) + Time.deltaTime;
                _noProgressTime[entityId] = elapsed;
                int tier = _stuckTier.TryGetValue(entityId, out var ti) ? ti : 0;

                if (tier < 1 && elapsed >= SkipWaypointAfterSeconds)
                {
                    // Tier 1: skip — advance idx by dir.
                    _stuckTier[entityId] = 1;
                    int skipIdx = idx + dir;
                    if (skipIdx >= count)
                    {
                        skipIdx = count - 2;
                        _direction[entityId] = -1;
                    }
                    else if (skipIdx < 0)
                    {
                        skipIdx = 1;
                        _direction[entityId] = 1;
                    }
                    _waypointIndex[entityId] = skipIdx;
                    _segT[entityId] = 0f;
                    ResetProgress(entityId);
                    XNPCVoiceControl.Log.Out($"[PATROL] {self.EntityName} no progress {SkipWaypointAfterSeconds}s — skipping to waypoint {_waypointIndex[entityId]}");
                }
                else if (elapsed >= TeleportAfterSeconds)
                {
                    // Tier 2: teleport to current segment start.
                    SCoreUtils.TeleportToPosition(_context, A);
                    ResetProgress(entityId);
                    XNPCVoiceControl.Log.Out($"[PATROL] {self.EntityName} stuck {TeleportAfterSeconds}s — teleported to waypoint");
                }
            }

            // --- Debug logging (every ~1s) ---
            _debugTick[entityId] = (_debugTick.TryGetValue(entityId, out var dt) ? dt : 0) + 1;
            if (_debugTick[entityId] >= 60)
            {
                _debugTick[entityId] = 0;
                XNPCVoiceControl.Log.Debug($"[PATROL-DBG] {self.EntityName} idx={idx}/{count-1} t={t:F3} dir={dir} p={p:F2} vErr={vErr:F2} recovering={(_recovering.TryGetValue(entityId, out var rc) && rc)} target=({target.x:F1},{target.z:F1}) pos=({self.position.x:F1},{self.position.z:F1})");
            }
        }

        public override void Stop(Context _context)
        {
            base.Stop(_context);
            _context.Self.moveHelper.hasNextPos = false; // clear our injected next-point; Vector3 SetMoveTo never resets it
            int entityId = _context.Self.entityId;
            _waypointIndex.Remove(entityId);
            _direction.Remove(entityId);
            _segT.Remove(entityId);
            _debugTick.Remove(entityId);
            _airborneTick.Remove(entityId);
            _recovering.Remove(entityId);
            ResetProgress(entityId);
        }

        private static void ResetProgress(int entityId)
        {
            _bestProgress.Remove(entityId);
            _noProgressTime.Remove(entityId);
            _stuckTier.Remove(entityId);
            _offPathTime.Remove(entityId);
            _repathTimer.Remove(entityId);
        }

        /// <summary>
        /// Horizontal distance from NPC position to the next vertex along travel direction.
        /// </summary>
        private static float DistToNextVertex(IEntityOrderReceiverSDX r, int idx, int dir, Vector3 pos)
        {
            int nextIdx = idx + dir;
            Vector3 d = r.PatrolCoordinates[nextIdx] - pos;
            d.y = 0f;
            return d.magnitude;
        }

        /// <summary>
        /// True if NPC is within VertexAdvanceRadius of the next vertex along travel direction.
        /// Returns false if nextIdx is out of bounds (end of route).
        /// </summary>
        private static bool IsCloseToNext(IEntityOrderReceiverSDX r, int idx, int dir, int count, Vector3 pos)
        {
            int nextIdx = idx + dir;
            if (nextIdx < 0 || nextIdx >= count) return false;
            return DistToNextVertex(r, idx, dir, pos) < VertexAdvanceRadius;
        }
    }
}
