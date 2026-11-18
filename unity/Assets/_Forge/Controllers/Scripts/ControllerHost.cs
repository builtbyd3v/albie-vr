using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>
    /// Runs one controller for one Thing (contract sections 1 and 3.1). The Thing runtime (Person 2) owns one host per
    /// Thing and calls <see cref="Step"/> from FixedUpdate after writing the sensors. Then it reads io.Actuators and
    /// maps them to torque, steer angle and thrust.
    ///
    /// Each step: zero the actuators → controller.Tick → NaN or infinity becomes 0, clamp to [-1, 1] → speed governor.
    /// A controller that throws is marked faulted: its actuators stay at zero and it isn't called again until a new one
    /// is attached. A swap that fails Bind keeps the old controller running.
    /// </summary>
    public sealed class ControllerHost
    {
        public readonly ControllerIO IO;
        public IThingController Current { get; private set; }
        /// <summary>The attached controller's file, when it came from one (null for a code-built controller).</summary>
        public ControllerFile File { get; private set; }
        /// <summary>m/s; above it, drive throttles can't push forward. +infinity means no limit.</summary>
        public float SpeedLimit { get; private set; } = float.PositiveInfinity;
        /// <summary>Why the current controller stopped (Tick threw), or null.</summary>
        public string Fault { get; private set; }
        /// <summary>
        /// The last step's actuators after the clamp and before the speed governor: what the controller (or the person
        /// driving) asked for. Behaviour cloning records these (contract section 4).
        /// </summary>
        public readonly float[] Commanded;

        /// <summary>Raised with the reason when the controller faults (contract event controller.fault).</summary>
        public event Action<string> Faulted;
        /// <summary>Raised after a successful swap (contract event controller.attached).</summary>
        public event Action<IThingController> Attached;
        /// <summary>
        /// Raised with the reason when <see cref="Reload"/> refuses a file. The running controller keeps driving
        /// (contract section 6); map it to controller.fault with that reason when events go through a contract PR.
        /// </summary>
        public event Action<string> ReloadFailed;
        /// <summary>Raised after a hot reload: the new file, and whether its state (PID integrators) was kept.</summary>
        public event Action<ControllerFile, bool> Reloaded;

        public ControllerHost(ControllerIO io)
        {
            IO = io ?? throw new ArgumentNullException(nameof(io));
            Commanded = new float[io.Actuators.Length];
        }

        /// <summary>Builds the controller for a file and attaches it. Returns null, or why the old one keeps running.</summary>
        public string Attach(ControllerFile file)
        {
            var controller = Create(file, out string error);
            if (controller == null) return error;
            error = Attach(controller, file.SpeedLimit);
            if (error == null) File = file;
            return error;
        }

        /// <summary>Attaches a controller between two steps. Returns null, or the Bind reason (the old one keeps running).</summary>
        public string Attach(IThingController controller, float speedLimit = float.PositiveInfinity)
        {
            if (controller == null) return "bad_value: no controller";
            string error;
            try { error = controller.Bind(IO); }
            catch (Exception e) { error = "bad_value: " + e.Message; }
            if (error != null) return error;
            controller.ResetState();
            Current = controller;
            File = null;
            SpeedLimit = speedLimit;
            Fault = null;
            Attached?.Invoke(controller);
            return null;
        }

        /// <summary>
        /// Attaches a code-built controller that a file describes (a learned net run on features its Thing's body doesn't
        /// sense itself, as the drone's course autopilot): <see cref="File"/> is that file, so a run records its name and
        /// version. Returns null, or the Bind reason (the old one keeps running).
        /// </summary>
        public string Attach(IThingController controller, ControllerFile describedBy, float speedLimit)
        {
            string error = Attach(controller, speedLimit);
            if (error == null) File = describedBy;
            return error;
        }

        /// <summary>Changes a tunable on the attached file and re-binds so it takes effect. PID state is kept.</summary>
        public string SetTunable(string key, float value)
        {
            if (File == null || Current == null) return "bad_value: the attached controller has no file";
            float old = File.Tunables.TryGetValue(key, out var t) ? t.Value : float.NaN;
            string error = File.SetTunable(key, value);
            if (error != null) return error;
            error = Current.Bind(IO);
            if (error != null) { File.SetTunable(key, old); Current.Bind(IO); return error; }
            SpeedLimit = File.SpeedLimit;
            return null;
        }

        public void Detach() { Current = null; File = null; Fault = null; SpeedLimit = float.PositiveInfinity; }

        /// <summary>
        /// Hot reload (contract section 6): a changed file for the running Thing, from the code panel, controller_tune
        /// or a git checkout. Call it from Update (never FixedUpdate); the swap takes effect at the next physics step.
        /// When only the tunables, version, notes or training block changed, the controller's state is kept, so the
        /// Thing doesn't jerk. Any other change (nodes, net, bindings, kind) resets it. A file that doesn't parse or
        /// bind is refused and the running controller keeps driving. Returns null, or why it was refused.
        /// </summary>
        public string Reload(string json)
        {
            var file = ControllerFile.Parse(json, out string error);
            return file == null ? Refuse(error) : Reload(file);
        }

        public string Reload(ControllerFile file)
        {
            var next = Create(file, out string error);
            if (next == null) return Refuse(error);
            try { error = next.Bind(IO); }
            catch (Exception e) { error = "bad_value: " + e.Message; }
            if (error != null) return Refuse(error);

            bool keep = Current != null && File != null && Fault == null && File.Kind == file.Kind
                        && StructureKey(File) == StructureKey(file);
            if (keep && next is GraphController graph && Current is GraphController old) graph.CopyStateFrom(old);
            else next.ResetState();
            Current = next;
            File = file;
            SpeedLimit = file.SpeedLimit;
            Fault = null;
            Reloaded?.Invoke(file, keep);
            return null;
        }

        string Refuse(string error)
        {
            ReloadFailed?.Invoke(error);
            return error;
        }

        /// <summary>The file without the parts a tunable-only edit may change.</summary>
        static string StructureKey(ControllerFile file)
        {
            var copy = (JObject)file.Raw.DeepClone();
            foreach (var key in new[] { "version", "tunables", "notes", "training" }) copy.Remove(key);
            return copy.ToString(Formatting.None);
        }

        public void ResetState() { Current?.ResetState(); }

        /// <summary>One physics step. <paramref name="forwardSpeed"/> is the Thing's forward speed in m/s.</summary>
        public void Step(float dt, float forwardSpeed)
        {
            float[] a = IO.Actuators;
            Array.Clear(a, 0, a.Length);
            Array.Clear(Commanded, 0, Commanded.Length);
            if (Current == null || Fault != null) return;

            try { Current.Tick(IO, dt); }
            catch (Exception e)
            {
                Array.Clear(a, 0, a.Length);
                Fault = $"{Current.Name} stopped: {e.Message}";
                Faulted?.Invoke(Fault);
                return;
            }

            for (int i = 0; i < a.Length; i++)
            {
                float v = a[i];
                a[i] = float.IsNaN(v) || float.IsInfinity(v) ? 0f : v < -1f ? -1f : v > 1f ? 1f : v;
            }
            Array.Copy(a, Commanded, a.Length);

            if (forwardSpeed > SpeedLimit)
            {
                int[] throttles = IO.ThrottleActuators;
                for (int i = 0; i < throttles.Length; i++) if (a[throttles[i]] > 0) a[throttles[i]] = 0;
            }
        }

        /// <summary>The controller for a file's kind, unbound.</summary>
        public static IThingController Create(ControllerFile file, out string error)
        {
            error = null;
            switch (file.Kind)
            {
                case "manual": return new ManualController(file);
                case "graph": return new GraphController(file);
                case "learned": return new LearnedController(file);
                default:
                    error = $"bad_value: kind \"{file.Kind}\" must be manual, graph or learned";
                    return null;
            }
        }
    }
}
