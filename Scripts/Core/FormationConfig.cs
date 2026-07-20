using System.Collections.Generic;

namespace XNPCVoiceControl.Core
{
    /// <summary>
    /// Formation slot distance configuration. Loaded from modconfig.xml &lt;FormationDistances&gt; section.
    /// Reads child &lt;Tier index="N" distance="M" /&gt; elements into a dictionary.
    /// Tolerant of extra tiers — add a &lt;Tier&gt; line, no code change needed.
    /// Defaults: 1=tight=2m, 2=wide=5m.
    /// </summary>
    public class FormationConfig
    {
        /// <summary>Slot distances keyed by vcFormationDistance index (1=tight, 2=wide, ...).</summary>
        public Dictionary<int, float> Distances = new Dictionary<int, float>
        {
            { 1, 2f },   // tight
            { 2, 5f },   // wide
        };

        /// <summary>
        /// Resolve a distance index to meters. Returns default (2.5m) for unknown index.
        /// </summary>
        public float GetDistance(int index)
        {
            if (Distances.TryGetValue(index, out float d))
                return d;
            return 2.5f; // fallback — matches DefaultMinDist in the task
        }
    }
}
