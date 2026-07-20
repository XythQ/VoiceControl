using UnityEngine;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Shared block-scanning helpers for the JumpUp/JumpDown voice commands - used both by
    /// PhraseTriggerHandler (immediate pre-check, so a missing ladder/edge fails fast with a
    /// dialog line) and by the UAI tasks that actually execute the movement.
    /// </summary>
    public static class JumpCommandUtils
    {
        /// <summary>
        /// Clear a pending jump command by resetting the cvar switch.
        /// Cooldown is handled declaratively via XVC_JumpCooldown buff (OnStopAddBuffs).
        /// </summary>
        public static void ClearCommand(EntityAlive self)
        {
            self.SetCVar("$XVC_PendingCommand", 0f);
        }

        public const float LadderSearchRadiusBlocks = 20f;
        private const int LadderSearchYRange = 6;

        public static Vector3i? FindNearestLadder(EntityAlive self)
        {
            var world = self.world;
            var center = World.worldToBlockPos(self.position);
            int minPreferredY = center.y - 1; // small tolerance below current feet level

            Vector3i? bestPreferred = null;
            float bestPreferredDistSq = LadderSearchRadiusBlocks * LadderSearchRadiusBlocks;
            Vector3i? bestAny = null;
            float bestAnyDistSq = LadderSearchRadiusBlocks * LadderSearchRadiusBlocks;

            int xzRadius = (int)LadderSearchRadiusBlocks;
            for (int x = -xzRadius; x <= xzRadius; x++)
            {
                for (int z = -xzRadius; z <= xzRadius; z++)
                {
                    for (int y = -LadderSearchYRange; y <= LadderSearchYRange; y++)
                    {
                        var pos = new Vector3i(center.x + x, center.y + y, center.z + z);
                        var block = world.GetBlock(pos);
                        if (!(block.Block is BlockLadder)) continue;

                        float dx = pos.x - center.x;
                        float dy = pos.y - center.y;
                        float dz = pos.z - center.z;
                        float distSq = dx * dx + dy * dy + dz * dz;

                        if (distSq < bestAnyDistSq)
                        {
                            bestAnyDistSq = distSq;
                            bestAny = pos;
                        }

                        // "Jump Up" should prefer a ladder that actually leads upward from roughly the
                        // NPC's current level, not just the nearest one in raw distance - a ladder one
                        // level down is geometrically closer but goes the wrong way.
                        if (pos.y >= minPreferredY && distSq < bestPreferredDistSq)
                        {
                            bestPreferredDistSq = distSq;
                            bestPreferred = pos;
                        }
                    }
                }
            }

            // Prefer a ladder at/above current level; fall back to the nearest ladder overall if none
            // qualify (e.g. only a lower ladder exists within range).
            return bestPreferred ?? bestAny;
        }

        private const int EdgeMaxStepsPerDirection = 10;
        public const float EdgePastDistance = 1.5f;

        private static readonly Vector3[] EdgeSearchDirections =
        {
            Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
            (Vector3.forward + Vector3.left).normalized, (Vector3.forward + Vector3.right).normalized,
            (Vector3.back + Vector3.left).normalized, (Vector3.back + Vector3.right).normalized,
        };

        public static Vector3? FindNearestEdge(EntityAlive self)
        {
            Vector3? best = null;
            float bestStep = EdgeMaxStepsPerDirection + 1;

            foreach (var dir in EdgeSearchDirections)
            {
                for (int step = 1; step <= EdgeMaxStepsPerDirection; step++)
                {
                    var checkPos = self.position + dir * step;
                    if (!IsEdgeAt(self.world, checkPos)) continue;

                    if (step < bestStep)
                    {
                        bestStep = step;
                        best = checkPos + dir * EdgePastDistance;
                    }
                    break; // found the nearest edge in this direction, stop scanning further out
                }
            }

            return best;
        }

        private static bool IsEdgeAt(World world, Vector3 pos)
        {
            var blockPos = new Vector3i(Utils.Fastfloor(pos.x), Utils.Fastfloor(pos.y), Utils.Fastfloor(pos.z));
            var block = world.GetBlock(blockPos);
            if (!block.isair)
            {
                Log.Debug(() => $"[JUMPDOWN-DIAG] {pos} -> blockPos {blockPos}: NOT air, rejecting");
                return false;
            }

            // A 1-block drop is already handled by vanilla's automatic step-down - only a genuine
            // edge (2+ block drop) warrants the JumpDown command. If the block immediately below
            // the candidate standing spot is solid, it's just a normal step, not a jump-worthy edge.
            var belowPos = blockPos;
            belowPos.y -= 1;
            var belowBlock = world.GetBlock(belowPos);
            if (belowBlock.Block.IsMovementBlocked(world, belowPos, belowBlock, BlockFace.None))
            {
                Log.Debug(() => $"[JUMPDOWN-DIAG] {pos} -> blockPos {blockPos}: solid ground 1 block below (normal auto-step, not jump-worthy), rejecting");
                return false;
            }

            Log.Debug(() => $"[JUMPDOWN-DIAG] {pos} -> blockPos {blockPos}: EDGE FOUND");
            return true;
        }
    }
}
