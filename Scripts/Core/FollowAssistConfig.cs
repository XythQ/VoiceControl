namespace XNPCVoiceControl.Core
{
    /// <summary>
    /// Follow-assist watchdog configuration. Loaded from modconfig.xml &lt;FollowAssist&gt; section.
    /// Detects stuck-following NPCs (spiral stairs, tight interiors) and fires a catch-up teleport.
    /// </summary>
    public class FollowAssistConfig
    {
        /// <summary>Enable/disable the watchdog entirely.</summary>
        public bool Enabled = true;

        /// <summary>Seconds with no net approach before assist fires (default 9).</summary>
        public float NoProgressSeconds = 9f;

        /// <summary>3D distance to leader that arms the watchdog (default 3.0).</summary>
        public float MinSeparation = 3.0f;

        /// <summary>Distance must shrink by at least this much per window to count as progress (default 0.5).</summary>
        public float ProgressEpsilon = 0.5f;

        /// <summary>Minimum seconds between assists per NPC (default 15).</summary>
        public float CooldownSeconds = 15f;
    }
}
