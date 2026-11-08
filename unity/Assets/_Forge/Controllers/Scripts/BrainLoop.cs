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

    /// <summary>The running brain loop, or null. Set by whoever owns it (today SlalomBrain in SlalomSandbox).</summary>
    public static class BrainLoops
    {
        public static IBrainLoop Active { get; private set; }

        public static void Register(IBrainLoop loop) => Active = loop;

        public static void Unregister(IBrainLoop loop)
        {
            if (ReferenceEquals(Active, loop)) Active = null;
        }
    }
}
