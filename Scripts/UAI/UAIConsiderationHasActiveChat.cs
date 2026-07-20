// UAI discovery: <consideration class="HasActiveChat, 1-XNPCVoiceControl" />
// Appended to NPCCore's Chat actions via utilityai.xml XPath patch.
// Returns 1f when NPC has an active conversation or greeting hold, 0f otherwise.
// When this consideration returns 0, the Chat action is skipped and Wander wins.

using System;
using UnityEngine;

namespace UAI
{
    public class UAIConsiderationHasActiveChat : UAIConsiderationBase
    {
        public override float GetScore(Context _context, object target)
        {
            if (_context.Self == null) return 0f;

            try
            {
                if (!XNPCVoiceControl.Core.ChatComponentManager.TryGet(_context.Self.entityId, out var chatComponent))
                {
                    if (XNPCVoiceControl.Log.DebugMode)
                        XNPCVoiceControl.Log.Debug(() => $"[UAI-DIAG] HasActiveChat: no chat component on {_context.Self.EntityName}");
                    return 0f;
                }

                // Active conversation, active rotation hold (greeting in progress), or within the
                // brief chat pause window (gives player time to initiate). When all three are false,
                // Wander wins and NPC moves on.
                float score = chatComponent.IsInChatPauseWindow ? 1f : 0f;
                return score;
            }
            catch (Exception ex)
            {
                // Chat component threw (entity destroyed mid-frame, etc.) — fail-safe: don't idle.
                if (XNPCVoiceControl.Log.DebugMode)
                    XNPCVoiceControl.Log.Debug(() => $"[UAI-DIAG] HasActiveChat exception: {ex.Message}");
                return 0f;
            }
        }
    }
}
