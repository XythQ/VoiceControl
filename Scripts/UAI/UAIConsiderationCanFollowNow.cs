using System.Collections.Generic;
using UnityEngine;
using XNPCVoiceControl;

namespace UAI
{
    /// <summary>
    /// Replaces the bare EnemyNotNear consideration on DynamicFollow.
    /// 
    /// Returns 1 (allow follow) when ANY of these is true:
    ///   - vcEngage != 1 (default hold OR disengaged — never break to fight)
    ///   - distance to leader > 15m (long-range catch-up, don't break for enemies)
    ///   - no enemy within 30m (nothing to fight anyway)
    /// 
    /// Returns 0 (break to fight) when: engaged AND close to leader AND enemy nearby.
    /// 
    /// This fixes the lagging-NPC-idles-near-enemies flip-flop and lets a non-engaged NPC
    /// follow peacefully past enemies.
    /// </summary>
    public class UAIConsiderationCanFollowNow : UAIConsiderationBase
    {
        private const float LongRangeThreshold = 15f; // beyond this, always follow (catch-up priority)
        private const float EnemySearchDistance = 30f; // match DynamicFollow's EnemyNotNear distance

        public override float GetScore(Context _context, object target)
        {
            var self = _context.Self;
            int id = self.entityId;

            // Gate 1: not engaged (default hold OR disengaged) → always follow, never break to fight.
            float engage = EntityUtilities.GetCVarValue(id, "vcEngage");
            if (engage != 1f)
                return 1f;

            // Gate 2: distance to leader > 15m → always follow (catch-up priority over fighting)
            Entity leaderEntity = EntityUtilities.GetLeaderOrOwner(id);
            if (leaderEntity != null)
            {
                float distToLeader = (self.position - leaderEntity.position).magnitude;
                if (distToLeader > LongRangeThreshold)
                    return 1f;
            }

            // Gate 3: no enemy within search distance → follow is fine
            if (!SCoreUtils.IsEnemyNearby(_context, EnemySearchDistance))
                return 1f;

            // All gates failed: engaged, close to leader, enemy nearby → break to fight
            return 0f;
        }
    }
}
