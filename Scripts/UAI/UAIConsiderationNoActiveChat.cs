// UAI discovery: <consideration class="NoActiveChat, 1-XNPCVoiceControl" />
// Inverse of HasActiveChat - returns 1f when the NPC does NOT have an active chat/greeting-hold
// /pause-window state, 0f when it does. Used to suppress default-activity actions (Wander) while
// a conversation is in progress, rather than only ever boosting Chat's own score - that approach
// doesn't help when Chat's OTHER considerations (e.g. TargetDistance requiring the player within
// 1-4m) happen to drop to 0 for an unrelated reason, letting Wander win by default since nothing
// was actually stopping it.
namespace UAI
{
    using System.Collections.Generic;
    using XNPCVoiceControl;
    using UnityEngine;
    public class UAIConsiderationNoActiveChat : UAIConsiderationBase
    {
        public override float GetScore(Context _context, object target)
        {
            if (_context.Self == null) return 1f; // fail open - don't block Wander if we can't check
            try
            {
                if (!XNPCVoiceControl.Core.ChatComponentManager.TryGet(_context.Self.entityId, out var chatComponent))
                    return 1f;
                bool paused = chatComponent.IsInChatPauseWindow;
                // Diagnostic: log when chat pause blocks (throttled per entity)
                if (paused)
                {
                    int id = _context.Self.entityId;
                    if (!_lastBlockTime.TryGetValue(id, out float lastT) || Time.time - lastT >= 3f)
                    {
                        _lastBlockTime[id] = Time.time;
                        Log.Out($"[NO-CHAT] BLOCKED {id} (chat pause active)");
                    }
                }
                return paused ? 0f : 1f;
            }
            catch
            {
                return 1f; // fail open
            }
        }

        private static readonly Dictionary<int, float> _lastBlockTime = new Dictionary<int, float>();
    }
}
