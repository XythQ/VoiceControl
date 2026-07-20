namespace XNPCVoiceControl.Core
{
    /// <summary>
    /// Weekly hire billing configuration. Loaded from modconfig.xml &lt;Billing&gt; section.
    /// </summary>
    public class BillingConfig
    {
        /// <summary>Growth rate applied per week (default 1.2 = 20% increase).</summary>
        public float GrowthRate = 1.2f;

        /// <summary>Max weeks before cost plateaus (default 12).</summary>
        public int MaxWeeks = 12;

        /// <summary>Days of grace after missed payment before dismiss (default 2).</summary>
        public int GraceDays = 2;

        /// <summary>Days with no yes/no response before auto-falling into grace (default 3).</summary>
        public int ApprovalTimeoutDays = 3;
    }
}
