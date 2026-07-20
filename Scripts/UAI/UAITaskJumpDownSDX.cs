using System.Collections.Generic;
using UnityEngine;

// using this namespace is necessary for Utilities AI Tasks
//       <task class="JumpDownSDX, 1-XNPCVoiceControl" />
namespace UAI
{
    public class UAITaskJumpDownSDX : UAITaskBase
    {
        // Entity-keyed for the same singleton-task reason as UAITaskJumpUpSDX.
        private readonly Dictionary<int, Vector3> _targets = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, float> _selfAbortUntil = new Dictionary<int, float>();
        private readonly Dictionary<int, int> _ticksRunning = new Dictionary<int, int>();
        private const int MaxTicks = 150; // hard safety net - task must never run forever

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

            var edgeTarget = XNPCVoiceControl.JumpCommandUtils.FindNearestEdge(self);
            if (edgeTarget == null)
            {
                XNPCVoiceControl.JumpCommandUtils.ClearCommand(self);
                _selfAbortUntil[entityId] = Time.time + 2f;
                Stop(_context);
                return;
            }

            _targets[entityId] = edgeTarget.Value;
            _ticksRunning[entityId] = 0; // fresh attempt, fresh timeout window
            _context.ActionData.Started = true;
            _context.ActionData.Executing = true;

            XNPCVoiceControl.Log.Debug(() => $"[JUMPDOWN-DIAG] {self.EntityName} ({entityId}): found edge at {edgeTarget.Value}, walking to it");

            // Deliberately bypass FindPath/the navigator - the A* pathfinder requires a
            // reachable, walkable target and would reject a point beyond a ledge (the same
            // validation that keeps ordinary Wander from picking unreachable targets - see
            // UAITaskWanderSDX's fixes this session). SetMoveTo is the lower-level "just move
            // toward this raw position" primitive that doesn't require that validation.
            self.moveHelper.SetMoveTo(edgeTarget.Value, false);
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

            // Deliberately do NOT call SCoreUtils.IsBlocked()/CheckJump() here - that's the
            // edge-safety machinery that would stop the NPC before it walks off, which is
            // exactly what this command is supposed to do.
            self.SetLookPosition(target);
            self.moveHelper.SetMoveTo(target, false);

            float dx = self.position.x - target.x;
            float dz = self.position.z - target.z;
            float horizontalDist = Mathf.Sqrt(dx * dx + dz * dz);

            if (horizontalDist < 1.0f || (horizontalDist <= XNPCVoiceControl.JumpCommandUtils.EdgePastDistance && !self.onGround))
            {
                XNPCVoiceControl.Log.Debug(() => $"[JUMPDOWN-DIAG] {self.EntityName} ({entityId}): reached edge (dist={horizontalDist:F2}, onGround={self.onGround})");
                XNPCVoiceControl.JumpCommandUtils.ClearCommand(self);
                _selfAbortUntil[entityId] = Time.time + 2f;
                Stop(_context);
                return;
            }

            // Hard timeout safety net - if the exit condition never fires (e.g. coyote-time mismatch,
            // forward momentum carrying NPC past target while falling), the task must eventually give up.
            _ticksRunning.TryGetValue(entityId, out int ticks);
            ticks++;
            _ticksRunning[entityId] = ticks;
            if (ticks > MaxTicks)
            {
                XNPCVoiceControl.Log.Warning($"[JUMPDOWN-DIAG] {self.EntityName} ({entityId}): timed out after {MaxTicks} ticks without reaching edge (last horizontalDist={horizontalDist:F2}, onGround={self.onGround}), giving up");
                XNPCVoiceControl.JumpCommandUtils.ClearCommand(self);
                _selfAbortUntil[entityId] = Time.time + 2f;
                Stop(_context);
            }
        }

        public override void Stop(Context _context)
        {
            var self = _context.Self;
            if (EntityUtilities.GetCVarValue(self.entityId, "$XVC_PendingCommand") == (float)XNPCVoiceControl.PendingJumpCommand.JumpDown)
            {
                XNPCVoiceControl.Log.Warning($"[JUMPDOWN-DIAG] Stop() called externally while JumpCommand still pending for {self.EntityName} ({self.entityId}) - likely PREEMPTED by a higher-scoring action");
            }
            _context.Self.moveHelper.StopMove(); // kill the SetMoveTo - Start() bypasses the navigator,
                                                  // so nothing else cancels a still-active raw move target
            base.Stop(_context);
        }

    }
}
