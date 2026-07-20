using UnityEngine;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Shared formation slot direction computation. Single source of truth for the angle→direction
    /// convention so the servo (UAITaskDynamicFollow) and follow-assist (NPCChatComponent.Social)
    /// can never diverge on sign.
    /// </summary>
    public static class FormationUtils
    {
        /// <summary>
        /// World-anchored socket direction from a compass bearing in Unity yaw degrees:
        /// 0=North(+Z), 90=East(+X), 180=South, 270=West. No heading input — sockets are map-fixed.
        /// </summary>
        public static Vector3 GetSlotDirection(float bearingDeg)
        {
            return Quaternion.Euler(0f, bearingDeg, 0f) * Vector3.forward;
        }

        /// <summary>Snap a bearing to the 8 compass sockets (45° steps), normalized to [0,360).</summary>
        public static float Snap45(float bearingDeg)
        {
            return ((Mathf.Round(bearingDeg / 45f) * 45f) % 360f + 360f) % 360f;
        }

        /// <summary>Resolve a leader-relative angle (existing phrase convention: fwd 0, rear 180,
        /// left +90, right -90) into a world socket bearing using the player's yaw at command time.</summary>
        public static float ResolveRelativeToBearing(float playerYawDeg, float relAngleDeg)
        {
            return Snap45(playerYawDeg - relAngleDeg);
        }
    }
}
