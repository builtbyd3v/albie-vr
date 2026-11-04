using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Forge.Controllers
{
    /// <summary>
    /// Desktop sandbox for the controller runner (hub off, mock first). Drives <see cref="MockTruckSim"/> through a
    /// <see cref="ControllerHost"/> with the mock controller files in Resources/Mocks/controllers.
    /// Keys: 1 manual, 2 cruise (graph) · W/S or ↑/↓ throttle, A/D or ←/→ steer (manual) · [ / ] max_speed −/+ 0.2 m/s
    /// (a live tunable change) · R reset. The readout is a sandbox debug overlay, not product UI.
    /// </summary>
    public sealed class ControllersSandbox : MonoBehaviour
    {
        public Transform truck;
        public LineRenderer[] rays;
        public string[] controllerFiles = { "Mocks/controllers/truck_manual", "Mocks/controllers/truck_cruise" };

        readonly MockTruckSim sim = new MockTruckSim();
        ControllerHost host;
        int stickY, handRoll;
        float lastKeyThrottle, lastKeySteer;
        string readout = "", status = "";
        float nextReadout;
        readonly System.Text.StringBuilder sb = new System.Text.StringBuilder(512);

        public ControllerHost Host => host;
        public MockTruckSim Sim => sim;

        void Awake()
        {
            host = new ControllerHost(new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames));
            host.Faulted += reason => status = "FAULT " + reason;
            stickY = ManualInputs.IndexOf("stick.y");
            handRoll = ManualInputs.IndexOf("hand.roll");
            Use(0);
        }

        /// <summary>Attaches controller file <paramref name="index"/> from <see cref="controllerFiles"/>.</summary>
        public string Use(int index)
        {
            var asset = Resources.Load<TextAsset>(controllerFiles[index]);
            if (asset == null) return status = $"missing Resources/{controllerFiles[index]}.json (run `python -m ml.make_controller_mocks` in hub/)";
            var file = ControllerFile.Parse(asset.text, out string error);
            error = error ?? host.Attach(file);
            status = error ?? $"attached {file.Name} ({file.Kind}, v{file.Version})";
            return error;
        }

        public void ResetTruck() { sim.Reset(); host.ResetState(); status = "reset"; }

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null)
            {
                // Write only when the keys change, so other input sources (the hands transmitter, voice, a test)
                // aren't overwritten by an idle keyboard every frame.
                float throttle = (k.wKey.isPressed || k.upArrowKey.isPressed ? 1f : 0f) - (k.sKey.isPressed || k.downArrowKey.isPressed ? 1f : 0f);
                float steer = (k.aKey.isPressed || k.leftArrowKey.isPressed ? 1f : 0f) - (k.dKey.isPressed || k.rightArrowKey.isPressed ? 1f : 0f);
                if (throttle != lastKeyThrottle) { ManualInputs.Values[stickY] = throttle; lastKeyThrottle = throttle; }
                if (steer != lastKeySteer) { ManualInputs.Values[handRoll] = steer; lastKeySteer = steer; }
                if (k.digit1Key.wasPressedThisFrame) Use(0);
                if (k.digit2Key.wasPressedThisFrame) Use(1);
                if (k.rKey.wasPressedThisFrame) ResetTruck();
                if (k.leftBracketKey.wasPressedThisFrame) Tune(-0.2f);
                if (k.rightBracketKey.wasPressedThisFrame) Tune(0.2f);
            }
#endif
            DrawTruck();
            if (Time.unscaledTime >= nextReadout) { nextReadout = Time.unscaledTime + 0.2f; BuildReadout(); }
        }

        void Tune(float delta)
        {
            if (host.File == null || !host.File.Tunables.TryGetValue("max_speed", out var t)) { status = "no max_speed tunable"; return; }
            string error = host.SetTunable("max_speed", t.Value + delta);
            status = error ?? $"max_speed {ControllerFile.Fmt(host.File.Tunables["max_speed"].Value)} m/s";
        }

        void FixedUpdate()
        {
            sim.Sense(host.IO.Sensors);
            host.Step(Time.fixedDeltaTime, sim.Speed);
            sim.Step(host.IO.Actuators, Time.fixedDeltaTime);
        }

        // Sim x → world x, sim y (left) → world z, CCW heading → negative yaw.
        void DrawTruck()
        {
            if (truck == null) return;
            var position = new Vector3(sim.X, 0f, sim.Y);
            truck.SetPositionAndRotation(position + Vector3.up * 0.06f, Quaternion.Euler(0f, -sim.Heading * Mathf.Rad2Deg, 0f));
            if (rays == null) return;
            for (int i = 0; i < rays.Length && i < MockTruckSim.Rays; i++)
            {
                float a = sim.Heading + MockTruckSim.RayAngle(i);
                var origin = position + Vector3.up * 0.05f;
                rays[i].SetPosition(0, origin);
                rays[i].SetPosition(1, origin + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * sim.RayDistance[i]);
            }
        }

        void BuildReadout()
        {
            sb.Clear();
            var c = host.Current;
            sb.Append(c == null ? "no controller" : $"{c.Name} ({c.Kind})");
            if (!float.IsPositiveInfinity(host.SpeedLimit)) sb.Append($"   limit {host.SpeedLimit:0.0} m/s");
            sb.Append($"\nspeed {sim.Speed:0.00} m/s   t {sim.Time:0.0} s   ");
            sb.Append(sim.Crashed ? "CRASHED (R to reset)" : sim.Finished ? "FINISHED" : "driving");
            sb.Append("\nsteer ").Append(host.IO.Actuators[0].ToString("+0.00;-0.00"))
              .Append("   throttle ").Append(host.IO.Actuators[1].ToString("+0.00;-0.00"));
            sb.Append("\nrays ");
            for (int i = 0; i < MockTruckSim.Rays; i++) sb.Append(host.IO.Sensors[i].ToString("0.00")).Append(' ');
            sb.Append("\n").Append(status);
            sb.Append("\n1 manual  2 cruise  WASD drive  [ ] max_speed  R reset");
            readout = sb.ToString();
        }

        void OnGUI()
        {
            GUI.Label(new Rect(12, 12, 640, 140), readout);
        }
    }
}
