using XNPCVoiceControl;

namespace UAI
{
    /// <summary>
    /// Combat gate for hired-NPC (Advanced) packages. Gate order:
    ///   0. No leader → free-fight (autonomous hunting; unhired hireable NPCs run these same Advanced packages)
    ///   1. vcEngage == 2 → hold (passive; never engage, not even self-defense)
    ///   2. vcEngage == 1 → free-fight (leader commanded)
    ///   3. self has revengeTarget → self-defense (I've been shot at)
    ///   4. enemy's attack-or-revenge target == leader → leader-assist (offensive+defensive)
    ///   else → hold fire (assist-only default: vcEngage absent or 0)
    /// Buff-free replacement for NPCCore's buffNPCModAttacking leader-assist.
    /// </summary>
    public class UAIConsiderationMayEngage : UAIConsiderationBase
    {
        public override float GetScore(Context _context, object target)
        {
            var self = _context.Self;
            int id = self.entityId;

            // Gate 0: unhired/masterless -> autonomous free-fight (hunting).
            // The engage model governs the leader's SQUAD only. AI packages are static per entity class
            // (NPCCore entityclasses.xml) and are NOT swapped on hire, so an unhired hireable NPC runs
            // these same Advanced packages — without this it would only fight when damaged and never hunt.
            var leader = EntityUtilities.GetLeaderOrOwner(id);
            if (leader == null)
                return 1f;                                              // autonomous hunting

            float engage = EntityUtilities.GetCVarValue(id, "vcEngage");
            if (engage == 2f) return 0f;                                // PASSIVE: never engage — not even self-defense (leader is moving through/fleeing)
            if (engage == 1f) return 1f;                                // free-fight

            if (self.GetRevengeTarget() != null)
                return 1f;                                              // self provoked (revengeTarget only — attackTarget is auto-acquired by EAI on sight via EAISetNearestEntityAsTarget, NOT a provocation signal; revengeTarget is set only in EntityAlive.DamageEntity)

            var enemy = UAIUtils.ConvertToEntityAlive(target);
            if (enemy != null)
            {
                var enemyTarget = EntityUtilities.GetAttackOrRevengeTarget(enemy.entityId);
                if (enemyTarget != null && enemyTarget.entityId == leader.entityId)
                    return 1f;                                          // leader-assist (off+def)
            }

            return 0f;                                                  // hold fire
        }
    }
}
