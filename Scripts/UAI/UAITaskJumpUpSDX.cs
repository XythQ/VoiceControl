using System.Collections.Generic;
using UnityEngine;

// using this namespace is necessary for Utilities AI Tasks
//       <task class="JumpUpSDX, 1-XNPCVoiceControl" />
namespace UAI
{
    public class UAITaskJumpUpSDX : UAITaskBase
    {
        // Task instances are singletons: one UAITaskJumpUpSDX object is created per <task> XML
        // element at utilityai.xml parse time and shared by every entity using this package -
        // per-entity state must be entityId-keyed, not a plain field (lesson learned building
        // UAITaskWanderSDX's wall-stuck fix this session).
        private readonly Dictionary<int, Vector3> _targets = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, float> _selfAbortUntil = new Dictionary<int, float>();
        private readonly Dictionary<int, int> _elevatorWaitTicks = new Dictionary<int, int>();
        private const int MaxElevatorWaitTicks = 60; // ~3s at Update()'s per-frame rate

        public override void Start(Context _context)
        {
            var self = _context.Self;
            int entityId = self.entityId;

            // Guard against unexplained re-selection within 2s of a prior exit.
            if (_selfAbortUntil.TryGetValue(entityId, out float abortUntil) && Time.time < abortUntil)
            {
                Stop(_context);
                return;
            }

            _elevatorWaitTicks[entityId] = 0; // fresh attempt, fresh timeout window

            var ladderPos = XNPCVoiceControl.JumpCommandUtils.FindNearestLadder(self);
            if (ladderPos == null)
            {
                XNPCVoiceControl.Log.Warning($"[JUMPUP-DIAG] {self.EntityName} ({entityId}): task's own ladder search found nothing (pre-check passed but this search failed)");
                XNPCVoiceControl.JumpCommandUtils.ClearCommand(self);
                _selfAbortUntil[entityId] = Time.time + 2f;
                Stop(_context);
                return;
            }

            var rawTarget = new Vector3(ladderPos.Value.x + 0.5f, ladderPos.Value.y, ladderPos.Value.z + 0.5f);
            var target = SCoreUtils.GetMoveToLocation(_context, rawTarget, XNPCVoiceControl.JumpCommandUtils.LadderSearchRadiusBlocks + 5f);
            _targets[entityId] = target;

            XNPCVoiceControl.Log.Debug(() => $"[JUMPUP-DIAG] {self.EntityName} ({entityId}): found ladder at {ladderPos.Value}, ladder-aware target resolved to {target} (self Y={self.position.y})");

            _context.ActionData.Started = true;
            _context.ActionData.Executing = true;

            SCoreUtils.FindPath(_context, target, false);
        }

        public override void Update(Context _context)
        {
            var self = _context.Self;
            int entityId = self.entityId;

            if (!_targets.TryGetValue(entityId, out var target))
            {
                Stop(_context);
                return;
            }

            bool blocked = SCoreUtils.IsBlocked(_context);
            bool noPath = self.getNavigator().noPathAndNotPlanningOne();
            if (blocked || noPath)
            {
                XNPCVoiceControl.Log.Warning($"[JUMPUP-DIAG] {self.EntityName} ({entityId}): aborting - IsBlocked={blocked} noPathAndNotPlanningOne={noPath}, distance-to-target={Vector3.Distance(self.position, target):F2}");
                XNPCVoiceControl.JumpCommandUtils.ClearCommand(self);
                _selfAbortUntil[entityId] = Time.time + 2f;
                Stop(_context);
                return;
            }

            self.SetLookPosition(target);

            var distance = Vector3.Distance(self.position, target);
            if (distance < 1.0f)
            {
                if (self.IsInElevator())
                {
                    XNPCVoiceControl.Log.Debug(() => $"[JUMPUP-DIAG] {self.EntityName} ({entityId}): entered elevator, handing off to vanilla climb");
                    XNPCVoiceControl.JumpCommandUtils.ClearCommand(self);
                    _selfAbortUntil[entityId] = Time.time + 2f;
                    Stop(_context);
                    return;
                }

                // Close by distance but not yet in the ladder's own column - give the NPC a short window
                // to finish walking the last bit onto it before giving up.
                _elevatorWaitTicks.TryGetValue(entityId, out int waitTicks);
                waitTicks++;
                _elevatorWaitTicks[entityId] = waitTicks;
                if (waitTicks > MaxElevatorWaitTicks)
                {
                    XNPCVoiceControl.Log.Warning($"[JUMPUP-DIAG] {self.EntityName} ({entityId}): reached ladder area but never entered elevator after {MaxElevatorWaitTicks} ticks, giving up");
                    XNPCVoiceControl.JumpCommandUtils.ClearCommand(self);
                    _selfAbortUntil[entityId] = Time.time + 2f;
                    Stop(_context);
                }
            }
        }

        public override void Stop(Context _context)
        {
            var self = _context.Self;
            if (EntityUtilities.GetCVarValue(self.entityId, "$XVC_PendingCommand") == (float)XNPCVoiceControl.PendingJumpCommand.JumpUp)
            {
                XNPCVoiceControl.Log.Warning($"[JUMPUP-DIAG] Stop() called externally while JumpCommand still pending for {self.EntityName} ({self.entityId}) - likely PREEMPTED by a higher-scoring action");
            }
            _context.Self.getNavigator().clearPath();
            base.Stop(_context);
        }
    }
}
