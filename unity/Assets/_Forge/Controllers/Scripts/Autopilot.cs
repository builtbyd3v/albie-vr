using System;
using System.Collections.Generic;

namespace Forge.Controllers
{
    /// <summary>
    /// The Autopilot: the trained candidate brains, tried in order (PROJECT_CONTEXT 16.8). One training run can fail to
    /// drive the course and validation loss can't tell which, so training makes a few candidates and the first arena run
    /// decides. <see cref="Engage"/> attaches the current candidate; when a run fails (a crash, a roll-over, the person
    /// says "that didn't work"), <see cref="RunEnded"/> moves to the next one. After the last trained candidate comes the
    /// optional pretrained fallback (16.3, Nov 12); <see cref="UsingFallback"/> says when it's driving, so the video and
    /// the Devpost text can disclose it. The arena (Person 5) and voice (Person 3) only call these two methods.
    /// </summary>
    public sealed class Autopilot
    {
        readonly List<ControllerFile> candidates = new List<ControllerFile>();
        readonly List<string> skipped = new List<string>();
        readonly int fallbackIndex = -1;

        /// <summary>Trained candidates, not counting the fallback.</summary>
        public int Count => fallbackIndex >= 0 ? candidates.Count - 1 : candidates.Count;
        /// <summary>Index of the candidate driving (or about to), or -1 when none is left.</summary>
        public int Index { get; private set; }
        public ControllerFile Current => Index >= 0 && Index < candidates.Count ? candidates[Index] : null;
        /// <summary>True when the pretrained fallback is the one driving.</summary>
        public bool UsingFallback => fallbackIndex >= 0 && Index == fallbackIndex;
        /// <summary>True after a candidate finished a run: keep it.</summary>
        public bool Kept { get; private set; }
        /// <summary>True when every candidate (and the fallback, if any) failed or was refused.</summary>
        public bool Exhausted => Index < 0 || Index >= candidates.Count;
        /// <summary>Candidates the host refused, with why (a bad file never stops the others).</summary>
        public IReadOnlyList<string> Skipped => skipped;

        /// <param name="files">The trained candidates, in the order to try them.</param>
        /// <param name="fallback">The pretrained controller to use when every candidate failed, or null.</param>
        public Autopilot(IEnumerable<ControllerFile> files, ControllerFile fallback = null) : this(files, fallback, int.MaxValue) { }

        /// <summary>
        /// As above, with the fallback tried at position <paramref name="fallbackAt"/> (0: first) instead of last: when the
        /// rehearsal says no trained candidate finishes the course but the fallback does (<see cref="AutopilotPlan"/>).
        /// </summary>
        public Autopilot(IEnumerable<ControllerFile> files, ControllerFile fallback, int fallbackAt)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            candidates.AddRange(files);
            if (fallback != null)
            {
                int at = fallbackAt < 0 ? 0 : fallbackAt > candidates.Count ? candidates.Count : fallbackAt;
                candidates.Insert(at, fallback);
                fallbackIndex = at;
            }
            Index = candidates.Count > 0 ? 0 : -1;
        }

        /// <summary>The driving candidate's number among the trained ones, in the order they are tried (1-based; 0 for the fallback or none).</summary>
        public int CandidateNumber => Exhausted || UsingFallback ? 0 : fallbackIndex >= 0 && Index > fallbackIndex ? Index : Index + 1;

        /// <summary>The one driving, for the UI: "candidate 2 of 3" or "the pretrained fallback".</summary>
        public string Describe() => Exhausted ? "none" : UsingFallback ? "the pretrained fallback" : $"candidate {CandidateNumber} of {Count}";

        /// <summary>Attaches the current candidate, skipping any the host refuses. Returns null, or why nothing is attached.</summary>
        public string Engage(ControllerHost host)
        {
            while (!Exhausted)
            {
                string error = host.Attach(candidates[Index]);
                if (error == null) return null;
                skipped.Add($"{Describe()}: {error}");
                Index++;
            }
            Index = -1;
            return "no candidate could be attached";
        }

        /// <summary>
        /// A run ended. On success the current candidate is kept. On failure the next one is attached and the Thing should
        /// be reset for another run. Returns a short line for the UI and the notebook.
        /// </summary>
        public string RunEnded(ControllerHost host, bool success)
        {
            if (Exhausted) return "no candidate left";
            if (success)
            {
                Kept = true;
                return $"{Describe()} drove the course: keeping it";
            }
            Kept = false;
            string failed = Describe();
            Index++;
            if (Exhausted)
            {
                Index = -1;
                return fallbackIndex >= 0 ? $"{failed} failed, and nothing is left" : $"{failed} failed, and none is left: use the pretrained fallback";
            }
            string error = Engage(host);
            return error ?? $"{failed} failed: trying {Describe()}";
        }
    }
}
