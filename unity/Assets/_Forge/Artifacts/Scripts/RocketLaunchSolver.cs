using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The <c>rocket</c> solver (host 8): a model rocket on a C6 motor, launched from a pad and flown along its simulated
    /// flight, shown at a fixed scale. It sits on the pad until <see cref="Launch"/> (the launch template's Launch button,
    /// through <see cref="RocketLaunchDirector"/>), counts down, then plays the flight: boost and coast in real time
    /// (times the sim's <c>rate</c>), the parachute descent <c>descent_speedup</c> times faster, and stops on landing.
    ///
    /// The flight is RocketPy's from the hub or cloud when the director hands one over for the design on the sliders
    /// (<see cref="SetReference"/>, the CAD design itself with no wind), else <see cref="RocketFlightModel"/>'s simplified
    /// one, computed when the numbers change (never per frame). <c>model</c> says which: 1 RocketPy, 0 simplified.
    ///
    /// Bodies, in the sim node's space (y up, downrange along +z): 0 the rocket, its tail at the trajectory point; then,
    /// optional, 1 the apogee marker (at the flight's top, so it moves as the sliders change the design), 2 the CG and
    /// 3 the CP marker (on the rocket's axis, drawn at <c>draw_scale</c>, riding with it).
    /// Params (numbers or <c>$input</c>): body_length, body_diameter, nose_length, fin_count, fin_root, fin_tip, fin_span,
    /// fin_sweep (mm, as rocket.py), nose_cone (0 ogive, 1 cone), wind_mps, scale (display units per real metre),
    /// draw_scale (the rocket's own drawing scale), descent_speedup, countdown_s, step.
    /// </summary>
    public sealed class RocketLaunchSolver : IArtifactSolver
    {
        public const string Name = "rocket";
        public static readonly string[] Values = { "time", "altitude_m", "speed_mps", "apogee_m", "max_speed_mps", "time_to_apogee_s", "flight_time_s", "drift_m", "stability_cal", "phase", "countdown", "model", "peak_m" };
        public static readonly string[] BodyKeys = { "node" };
        public static readonly string[] ParamKeys = { "body_length", "body_diameter", "nose_length", "nose_cone", "fin_count", "fin_root", "fin_tip", "fin_span", "fin_sweep", "wind_mps", "scale", "draw_scale", "descent_speedup", "countdown_s", "step" };

        public enum Stage { Pad = 0, Countdown = 1, Boost = 2, Coast = 3, Parachute = 4, Landed = 5 }

        const int VTime = 0, VAlt = 1, VSpeed = 2, VApogee = 3, VMaxSpeed = 4, VToApogee = 5, VFlight = 6, VDrift = 7, VMargin = 8, VPhase = 9, VCountdown = 10, VModel = 11, VPeak = 12;

        readonly RocketTrajectory simple = new RocketTrajectory();
        RocketTrajectory reference;
        bool useReference;
        RocketDesign design = RocketDesign.Default;
        float wind, scale = 0.005f, drawScale = 0.25f, speedup = 8f, countdownS = 3f, step = 0.02f;
        int bodies = 1;
        float flightT, countdownLeft, peak;
        int cursor;
        Stage stage;
        readonly float[] configured = new float[16];
        bool haveConfigured;

        public int Bodies => bodies;
        public double StepSize => step;
        public bool Finished => stage == Stage.Landed;

        public Stage Phase => stage;
        public RocketDesign Design => design;
        public float WindMps => wind;
        public float Scale => scale;
        public float DrawScale => drawScale;
        public float DescentSpeedup => speedup;
        public float FlightTime => flightT;
        public float CountdownLeft => countdownLeft;
        /// <summary>The flight being shown: RocketPy's or the simplified model's.</summary>
        public RocketTrajectory Active => useReference && reference != null ? reference : simple;
        public bool UsingRocketPy => useReference && reference != null;
        /// <summary>Raised when the shown flight changes (new numbers, a reference switched in or out). Not per frame.</summary>
        public event Action FlightChanged;

        public bool Configure(JObject node, ArtifactInputs inputs)
        {
            var p = node["params"] as JObject ?? new JObject();
            bodies = Mathf.Clamp((node["bodies"] as JArray)?.Count ?? 1, 1, 4);
            var d = RocketDesign.Default;
            d.bodyLength = Clamp(inputs.Number(p["body_length"], d.bodyLength), 50f, 2000f);
            d.bodyDiameter = Clamp(inputs.Number(p["body_diameter"], d.bodyDiameter), 10f, 200f);
            d.noseLength = Clamp(inputs.Number(p["nose_length"], d.noseLength), 10f, 600f);
            d.cone = inputs.Number(p["nose_cone"], 0f) >= 0.5f;
            d.finCount = Mathf.Clamp(Mathf.RoundToInt(inputs.Number(p["fin_count"], d.finCount)), 3, 8);
            d.finRoot = Clamp(inputs.Number(p["fin_root"], d.finRoot), 5f, 400f);
            d.finTip = Clamp(inputs.Number(p["fin_tip"], d.finTip), 1f, 400f);
            d.finSpan = Clamp(inputs.Number(p["fin_span"], d.finSpan), 5f, 300f);
            d.finSweep = Clamp(inputs.Number(p["fin_sweep"], d.finSweep), 0f, 400f);
            float w = Clamp(inputs.Number(p["wind_mps"], 0f), 0f, 20f);
            scale = Clamp(inputs.Number(p["scale"], 0.005f), 1e-5f, 1f);
            drawScale = Clamp(inputs.Number(p["draw_scale"], 0.25f), 0.01f, 1f);
            speedup = Clamp(inputs.Number(p["descent_speedup"], 8f), 1f, 50f);
            countdownS = Clamp(inputs.Number(p["countdown_s"], 3f), 0f, 10f);
            step = Clamp(inputs.Number(p["step"], 0.02f), 0.005f, 0.1f);

            int at = 0;
            bool changed = !haveConfigured;
            Note(d.bodyLength, ref at, ref changed); Note(d.bodyDiameter, ref at, ref changed); Note(d.noseLength, ref at, ref changed);
            Note(d.cone ? 1f : 0f, ref at, ref changed); Note(d.finCount, ref at, ref changed); Note(d.finRoot, ref at, ref changed);
            Note(d.finTip, ref at, ref changed); Note(d.finSpan, ref at, ref changed); Note(d.finSweep, ref at, ref changed);
            Note(w, ref at, ref changed); Note(scale, ref at, ref changed); Note(speedup, ref at, ref changed);
            Note(countdownS, ref at, ref changed); Note(step, ref at, ref changed); Note(drawScale, ref at, ref changed);
            haveConfigured = true;
            if (!changed) return false;
            design = d;
            wind = w;
            RocketFlightModel.Simulate(design, wind, simple);
            FlightChanged?.Invoke();
            return true;
        }

        void Note(float v, ref int at, ref bool changed)
        {
            if (configured[at] != v) changed = true;
            configured[at++] = v;
        }

        static float Clamp(float v, float lo, float hi) => float.IsNaN(v) ? lo : Mathf.Clamp(v, lo, hi);

        /// <summary>
        /// RocketPy's flight for this design (filled by the director from <c>GET /cad/flight</c>), and whether to show it:
        /// only while the sliders are the CAD design itself and there's no wind, since that's what it simulated. Null stops
        /// using one. Not per frame.
        /// </summary>
        public void SetReference(RocketTrajectory flight, bool use)
        {
            bool before = UsingRocketPy;
            var was = reference;
            reference = flight;
            useReference = use;
            if (before != UsingRocketPy || (UsingRocketPy && was != flight)) FlightChanged?.Invoke();
        }

        /// <summary>Back on the pad, waiting for <see cref="Launch"/>.</summary>
        public void Restart()
        {
            stage = Stage.Pad;
            flightT = 0f;
            countdownLeft = countdownS;
            peak = 0f;
            cursor = 0;
        }

        /// <summary>Start the countdown. From the pad only: a flight in the air finishes first.</summary>
        public bool Launch()
        {
            if (stage != Stage.Pad) return false;
            countdownLeft = countdownS;
            stage = countdownS > 0f ? Stage.Countdown : Stage.Boost;
            return true;
        }

        public void Step()
        {
            switch (stage)
            {
                case Stage.Pad:
                case Stage.Landed:
                    return;
                case Stage.Countdown:
                    countdownLeft -= step;
                    if (countdownLeft <= 0f)
                    {
                        countdownLeft = 0f;
                        stage = Stage.Boost;
                    }
                    return;
            }
            var f = Active;
            bool chute = f.Deploys && flightT >= f.deployS;
            flightT += chute ? step * speedup : step;
            if (flightT >= f.landingS)
            {
                flightT = f.landingS;
                stage = Stage.Landed;
            }
            else if (chute) stage = Stage.Parachute;
            else stage = flightT < f.burnoutS ? Stage.Boost : Stage.Coast;
            f.At(flightT, ref cursor, out _, out float alt, out _);
            if (alt > peak) peak = alt;
        }

        public Vector3 Position(int body)
        {
            if (body == 1) return ApogeePoint();
            Active.At(flightT, ref cursor, out float x, out float z, out _);
            var tail = new Vector3(0f, z * scale, x * scale);
            if (body < 2) return tail;
            // CG and CP on the axis, from the tail: the drawn rocket's length minus their distance from the nose tip.
            var f = Active;
            float fromTip = body == 2 ? f.cgMm : f.cpMm;
            return tail + Heading() * ((design.LengthMm - fromTip) * drawScale / 1000f);
        }

        /// <summary>Which way the rocket points now, in the sim node's space: along its path (along the rod on the pad;
        /// straight up under the parachute, where it hangs from it). No allocation.</summary>
        public Vector3 Heading()
        {
            if (stage == Stage.Pad || stage == Stage.Countdown || flightT <= 0f)
                return new Vector3(0f, Mathf.Sin(RocketFlightModel.InclinationDeg * Mathf.Deg2Rad), Mathf.Cos(RocketFlightModel.InclinationDeg * Mathf.Deg2Rad));
            if (stage == Stage.Parachute || stage == Stage.Landed) return Vector3.up;
            var f = Active;
            int c = cursor;
            f.At(flightT, ref c, out float x0, out float z0, out _);
            f.At(flightT + 0.1f, ref c, out float x1, out float z1, out _);
            var d = new Vector3(0f, z1 - z0, x1 - x0);
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.up;
        }

        /// <summary>Where the flight's top is, in the sim node's space.</summary>
        public Vector3 ApogeePoint()
        {
            var f = Active;
            return new Vector3(0f, f.apogeeM * scale, f.apogeeX * scale);
        }

        public Vector3 LandingPoint()
        {
            var f = Active;
            return f.count > 0 ? new Vector3(0f, 0f, f.x[f.count - 1] * scale) : Vector3.zero;
        }

        public int ValueIndex(string name) => Array.IndexOf(Values, name);

        public double Value(int index)
        {
            var f = Active;
            switch (index)
            {
                case VTime: return flightT;
                case VAlt:
                case VSpeed:
                    int c = cursor;
                    f.At(flightT, ref c, out _, out float alt, out float v);
                    return index == VAlt ? alt : (stage == Stage.Landed ? 0f : v);
                case VApogee: return f.apogeeM;
                case VMaxSpeed: return f.maxSpeedMps;
                case VToApogee: return f.timeToApogeeS;
                case VFlight: return f.landingS;
                case VDrift: return f.driftM;
                case VMargin: return f.marginCal;
                case VPhase: return (int)stage;
                case VCountdown: return stage == Stage.Countdown ? Mathf.Ceil(countdownLeft) : 0f;
                case VModel: return UsingRocketPy ? 1.0 : 0.0;
                case VPeak: return peak;
                default: return 0.0;
            }
        }
    }
}
