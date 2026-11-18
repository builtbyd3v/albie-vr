using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>
    /// The demo's brain beat as one object a voice tool can drive (PROJECT_CONTEXT 15.6 step 8): record laps driven
    /// by hand, train on them, then let the Autopilot drive. Each call returns null when it started, or what stopped it.
    /// The tools in <c>ML/Resources/AlbieTools/controller_brain.json</c> drive the running loop, and fall back to the
    /// brain panel's preview when none is running.
    /// </summary>
    public interface IBrainLoop
    {
        /// <summary>Records hand-driven laps until <paramref name="laps"/> have completed (a crashed lap re-arms),
        /// then, if <paramref name="thenAutopilot"/>, trains and runs the Autopilot with no further call.</summary>
        string Record(int laps, bool thenAutopilot);
        /// <summary>Aborts the lap being driven and keeps the laps already completed.</summary>
        string StopRecording();
        string Train();
        string StartAutopilot();
        /// <summary>Stops the Autopilot's run and hands the truck back to the manual controller.</summary>
        string StopAutopilot();
        /// <summary>The loop's state for <c>brain_status</c>: mode, laps, training progress, who drove.</summary>
        JObject ToolState();
    }

    /// <summary>
    /// The richer view of a brain loop that the ML desk's Controller brain panel and the brain tools read: which Thing,
    /// which course, the live loss curve, the trained versions. Implemented by the truck (<c>SlalomBrain</c>) and the
    /// drone (<c>DroneBrain</c>). Every number here comes from the real loop; none is made up.
    /// </summary>
    public interface ICourseBrain : IBrainLoop
    {
        /// <summary>"truck" or "drone".</summary>
        string ThingName { get; }
        /// <summary>The open course's id, or null when the loop's Thing has no course open.</summary>
        string CourseId { get; }
        /// <summary>idle, lap, training or autopilot.</summary>
        string ModeName { get; }
        string StatusLine { get; }
        int LapsCompleted { get; }
        /// <summary>0..1 while training, else -1.</summary>
        float TrainProgress { get; }
        /// <summary>Training loss per finished epoch of the run being (or last) trained, all candidates in turn.</summary>
        IReadOnlyList<float> LossHistory { get; }
        /// <summary>Trained versions so far (1-based numbering); 0 before the first training.</summary>
        int VersionCount { get; }
        /// <summary>The version the Autopilot uses (1-based), 0 when none.</summary>
        int SelectedVersion { get; }
        /// <summary>Picks the version the next Autopilot run uses. Returns null, or why not.</summary>
        string SelectVersion(int version);
        /// <summary>One version in a few words, from its own records ("v2 · 3 laps · loss 0.012 · rehearsal 9/9").</summary>
        string DescribeVersion(int version);
        /// <summary>The live channels the panel shows (names fixed per Thing; values are the live buffers, never copied).</summary>
        string[] InputNames { get; }
        float[] InputValues { get; }
        string[] OutputNames { get; }
        float[] OutputValues { get; }
    }

    /// <summary>
    /// The running brain loop, or null. Set by whoever owns it (SlalomBrain for the truck, DroneBrain for the drone).
    /// Loops stack: the last one registered (or brought forward with <see cref="Register"/> again, when its course opens)
    /// is <see cref="Active"/>; unregistering it gives the slot back to the one before, so a drone loop stepping in never
    /// loses the truck.
    /// </summary>
    public static class BrainLoops
    {
        static readonly List<IBrainLoop> stack = new List<IBrainLoop>();

        public static IBrainLoop Active { get; private set; }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { stack.Clear(); Active = null; }

        public static void Register(IBrainLoop loop)
        {
            if (loop == null) return;
            stack.Remove(loop);
            stack.Add(loop);
            Active = loop;
        }

        public static void Unregister(IBrainLoop loop)
        {
            stack.Remove(loop);
            if (!ReferenceEquals(Active, loop)) return;
            // Unity-destroyed loops compare equal to null through UnityEngine.Object; skip them.
            Active = null;
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                if (stack[i] is UnityEngine.Object o && o == null) { stack.RemoveAt(i); continue; }
                Active = stack[i];
                break;
            }
        }
    }
}
