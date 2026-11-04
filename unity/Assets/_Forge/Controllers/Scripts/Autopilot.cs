using System;
using System.Collections.Generic;

namespace Forge.Controllers
{
    /// <summary>
    /// The Autopilot: the trained candidate brains, tried in order (PROJECT_CONTEXT 16.8). One training run can fail to
    /// drive the course and validation loss can't tell which, so training makes a few candidates and the first arena run
    /// decides. <see cref="Engage"/> attaches the current candidate; when a run fails (a crash, a roll-over, the person
    /// says "that didn't work"), <see cref="RunEnded"/> moves to the next one. When none is left, the caller uses the
    /// pretrained fallback. The arena (Person 5) and voice (Person 3) only call these two methods.
    /// </summary>
    public sealed class Autopilot
    {
        readonly List<ControllerFile> candidates = new List<ControllerFile>();
        readonly List<string> skipped = new List<string>();

        public int Count => candidates.Count;
        /// <summary>Index of the candidate driving (or about to), or -1 when none is left.</summary>
        public int Index { get; private set; }
        public ControllerFile Current => Index >= 0 && Index < candidates.Count ? candidates[Index] : null;
        /// <summary>True after a candidate finished a run: keep it.</summary>
        public bool Kept { get; private set; }
        /// <summary>True when every candidate failed or was refused: use the pretrained fallback.</summary>
        public bool Exhausted => Index < 0 || Index >= candidates.Count;
        /// <summary>Candidates the host refused, with why (a bad file never stops the others).</summary>
        public IReadOnlyList<string> Skipped => skipped;

        public Autopilot(IEnumerable<ControllerFile> files)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            candidates.AddRange(files);
            Index = candidates.Count > 0 ? 0 : -1;
        }

        /// <summary>Attaches the current candidate, skipping any the host refuses. Returns null, or why nothing is attached.</summary>
        public string Engage(ControllerHost host)
        {
            while (!Exhausted)
            {
                string error = host.Attach(candidates[Index]);
                if (error == null) return null;
                skipped.Add($"candidate {Index + 1}: {error}");
                Index++;
            }
            Index = -1;
            return "no candidate could be attached: use the pretrained fallback";
        }

        /// <summary>
        /// A run ended. On success the current candidate is kept. On failure the next one is attached and the Thing should
        /// be reset for another run. Returns a short line for the UI and the notebook.
        /// </summary>
        public string RunEnded(ControllerHost host, bool success)
        {
            if (Exhausted) return "no candidate left: use the pretrained fallback";
            if (success)
            {
                Kept = true;
                return $"candidate {Index + 1} of {Count} drove the course: keeping it";
            }
            Kept = false;
            int failed = Index + 1;
            Index++;
            if (Exhausted) { Index = -1; return $"candidate {failed} failed, and none is left: use the pretrained fallback"; }
            string error = Engage(host);
            return error ?? $"candidate {failed} failed: trying candidate {Index + 1} of {Count}";
        }
    }
}
