using System;
using System.Collections.Generic;

namespace Forge.Controllers
{
    /// <summary>
    /// The order the Autopilot tries its drivers in, from the rehearsal (<see cref="Rehearsal"/>): the trained candidates
    /// that finished the course reliably first (best first), then the pretrained fallback when it finished its own
    /// rehearsal, then candidates that only finished now and then, and last the ones that never finished. A candidate
    /// that never finished in rehearsal used to drive first anyway when all three failed, and each failure cost a run;
    /// now the one that can drive goes first, and <see cref="Order"/> says why in a plain sentence. Plain C#.
    /// </summary>
    public static class AutopilotPlan
    {
        /// <summary>A candidate that finished at least this many rehearsal starts drives before the fallback.</summary>
        public const int Reliable = 3;

        /// <summary>
        /// Candidate indexes in the order to try, with -1 for the fallback (left out when <paramref name="fallbackFinished"/>
        /// is below 0: there is none). <paramref name="finished"/> is each candidate's rehearsal finishes. <paramref name="why"/> is null when a trained candidate leads, else a sentence.
        /// </summary>
        public static int[] Order(int[] finished, int fallbackFinished, out string why, int starts = 0)
        {
            why = null;
            var list = new List<int>();
            bool hasFallback = fallbackFinished >= 0;
            if (finished == null) throw new ArgumentNullException(nameof(finished));
            int n = finished.Length;
            var byBest = new int[n];
            for (int i = 0; i < n; i++) byBest[i] = i;
            for (int i = 1; i < n; i++)
                for (int j = i; j > 0 && finished[byBest[j]] > finished[byBest[j - 1]]; j--)
                    (byBest[j], byBest[j - 1]) = (byBest[j - 1], byBest[j]);
            foreach (int i in byBest) if (finished[i] >= Reliable) list.Add(i);
            bool fallbackWorks = hasFallback && fallbackFinished > 0;
            if (fallbackWorks) list.Add(-1);
            foreach (int i in byBest) if (finished[i] > 0 && finished[i] < Reliable) list.Add(i);
            foreach (int i in byBest) if (finished[i] <= 0) list.Add(i);
            if (hasFallback && !fallbackWorks) list.Add(-1);
            if (list.Count > 0 && list[0] == -1)
            {
                int best = n > 0 ? finished[byBest[0]] : 0;
                string of = starts > 0 ? " of " + starts : "";
                why = best == 0
                    ? "None of the brains trained on your laps finished the course in rehearsal, so the pretrained driver goes first."
                    : $"The best brain trained on your laps finished only {best}{of} rehearsal starts, so the pretrained driver goes first.";
            }
            return list.ToArray();
        }
    }
}
