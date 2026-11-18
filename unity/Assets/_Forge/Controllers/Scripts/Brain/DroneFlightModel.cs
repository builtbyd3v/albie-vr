using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Forge.Controllers
{
    /// <summary>
    /// How the quadcopter design flies: its CAD parameters (arms, arm length, prop and motor size, plate, battery, and an
    /// optional payload) turned into the drone's flight numbers, with no Unity types (a standalone check runs it).
    ///
    /// The model, simple on purpose and the same sums as the CAD hub's quadcopter analysis (hub/cad/parts/quadcopter.py):
    /// - Mass (g): battery 150 g per 1300 mAh; each motor 33 g at 28 mm, growing with its volume (diameter cubed); each
    ///   prop 0.156 g per square inch; each carbon arm by its length times its width (0.55 of the motor, at least 10 mm);
    ///   the plates by their area; 10 g of electronics; plus the payload. Scaled so the reference design weighs what the
    ///   hub says it does (389 g).
    /// - Thrust per motor (g): what the prop can push (55 g per square inch) or the motor can drive (2.2 g per mm²),
    ///   whichever is less. Thrust to weight (TWR) is all the motors over the mass.
    /// - Flight, against the reference design (<see cref="Reference"/>, a 5-inch 110 mm quad: every factor is 1 there,
    ///   which is the drone's hand-tuned arcade feel):
    ///   * acceleration and climb follow the spare thrust (TWR - 1), square-rooted: a heavy drone on small props
    ///     (TWR under 2) barely climbs; under 1.1 it cannot lift off at all (the body refuses);
    ///   * braking: the same spare thrust tilted back, plus a share that is just drag;
    ///   * top speed: spare thrust against the drag of its size (a bigger frame pushes more air);
    ///   * turning: the props' reaction torque (thrust times prop size) over the turning inertia (the mass out on the
    ///     arms times arm length squared): short arms turn fast, long arms turn slowly and steadily;
    ///   * agility (how quickly it leans into a move, the "twitchy" feel): thrust times arm length over the pitch inertia.
    /// Each factor is clamped to a wide range (<see cref="Lo"/>..<see cref="Hi"/> per factor) so a big change feels big but
    /// the drone stays flyable by a beginner.
    /// </summary>
    public static class DroneFlightModel
    {
        /// <summary>The design's parameters (CAD names: arm_count, arm_length, prop_diameter, motor_size, plate_size, battery_mah, payload_g).</summary>
        public sealed class Spec
        {
            public float Arms = 4, ArmMm = 110f, PropIn = 5f, MotorMm = 28f, PlateMm = 70f, BatteryMah = 1300f, PayloadG = 0f;

            public Spec Clone() => (Spec)MemberwiseClone();

            public bool SameAs(Spec o) =>
                o != null && Arms == o.Arms && ArmMm == o.ArmMm && PropIn == o.PropIn && MotorMm == o.MotorMm
                && PlateMm == o.PlateMm && BatteryMah == o.BatteryMah && PayloadG == o.PayloadG;

            /// <summary>Reads the CAD values it knows from <paramref name="values"/> (numbers or numeric strings); the rest stay.</summary>
            public Spec With(IDictionary<string, object> values)
            {
                var s = Clone();
                if (values == null) return s;
                s.Arms = Read(values, "arm_count", s.Arms);
                s.ArmMm = Read(values, "arm_length", s.ArmMm);
                s.PropIn = Read(values, "prop_diameter", s.PropIn);
                s.MotorMm = Read(values, "motor_size", s.MotorMm);
                s.PlateMm = Read(values, "plate_size", s.PlateMm);
                s.BatteryMah = Read(values, "battery_mah", s.BatteryMah);
                s.PayloadG = Read(values, "payload_g", s.PayloadG);
                return s;
            }

            static float Read(IDictionary<string, object> values, string name, float fallback)
            {
                if (!values.TryGetValue(name, out object v) || v == null) return fallback;
                try
                {
                    float f = Convert.ToSingle(v, CultureInfo.InvariantCulture);
                    return float.IsNaN(f) || float.IsInfinity(f) ? fallback : f;
                }
                catch (Exception) { return fallback; }
            }
        }

        /// <summary>The design's flight numbers. The K factors multiply the drone's base tuning (1 = the reference design).</summary>
        public struct Flight
        {
            public float MassG, ThrustG, Twr;
            public float SpeedK, AccelK, BrakeK, ClimbK, YawK, AgilityK;
            /// <summary>Half the span, motor centre to prop tip, metres.</summary>
            public float RadiusM;
        }

        /// <summary>The design the drone's tuning was made for: the CAD library's quadcopter v5 (5-inch props, 110 mm arms).</summary>
        public static readonly Spec Reference = new Spec();
        public const float ReferenceMassG = 389.2f;
        public const float Lo = 0.2f, Hi = 2f;

        static readonly float RawRef = RawMass(Reference);
        static readonly Flight Ref = Raw(Reference);

        static float RawMass(Spec s)
        {
            float n = Math.Max(3f, s.Arms);
            float battery = 150f * Math.Max(0f, s.BatteryMah) / 1300f;
            float motor = 33f * Cube(Math.Max(5f, s.MotorMm) / 28f);
            float prop = 0.156f * s.PropIn * s.PropIn;
            float arm = 0.00958f * Math.Max(10f, s.ArmMm) * Math.Max(10f, 0.55f * s.MotorMm);
            float plates = 37f * Sq(Math.Max(20f, s.PlateMm) / 70f);
            return battery + n * (motor + prop + arm) + plates + 10f;
        }

        static float Cube(float v) => v * v * v;
        static float Sq(float v) => v * v;

        /// <summary>Mass in grams (calibrated: the reference design is <see cref="ReferenceMassG"/>).</summary>
        public static float MassG(Spec s) => RawMass(s) * ReferenceMassG / RawRef + Math.Max(0f, s.PayloadG);

        public static float ThrustPerMotorG(Spec s) => Math.Min(55f * s.PropIn * s.PropIn, 2.2f * s.MotorMm * s.MotorMm);

        // The unclamped physical quantities the factors are ratios of.
        static Flight Raw(Spec s)
        {
            float n = Math.Max(3f, s.Arms);
            float mass = MassG(s), thrust = n * ThrustPerMotorG(s);
            float twr = thrust / Math.Max(1f, mass);
            float spare = Math.Max(0f, twr - 1f);
            float armM = Math.Max(0.02f, s.ArmMm / 1000f), propM = s.PropIn * 0.0254f;
            // The mass out on the arms (motors, props, half the arms) is what resists turning and leaning.
            float outG = n * (33f * Cube(Math.Max(5f, s.MotorMm) / 28f) + 0.156f * s.PropIn * s.PropIn
                              + 0.5f * 0.00958f * Math.Max(10f, s.ArmMm) * Math.Max(10f, 0.55f * s.MotorMm));
            float inertia = outG * armM * armM + 0.02f * mass * 0.04f * 0.04f;
            float span = 2f * armM + propM;
            return new Flight
            {
                MassG = mass, ThrustG = thrust, Twr = twr,
                AccelK = spare,                               // ratio taken below
                SpeedK = spare / Math.Max(0.05f, span),       // spare thrust over the drag of its size
                YawK = thrust * propM / Math.Max(1e-6f, inertia),
                AgilityK = thrust * armM / Math.Max(1e-6f, inertia),
                RadiusM = 0.5f * span,
            };
        }

        static float Clamp(float v, float lo, float hi) => float.IsNaN(v) ? 1f : v < lo ? lo : v > hi ? hi : v;

        /// <summary>The flight numbers for a design.</summary>
        public static Flight Of(Spec s)
        {
            var r = Raw(s);
            float thrustK = (float)Math.Sqrt(r.AccelK / Math.Max(1e-4f, Ref.AccelK));
            return new Flight
            {
                MassG = r.MassG, ThrustG = r.ThrustG, Twr = r.Twr, RadiusM = r.RadiusM,
                AccelK = Clamp(thrustK, Lo, 1.8f),
                ClimbK = Clamp(thrustK, 0.15f, 1.8f),
                BrakeK = Clamp(0.35f + 0.65f * thrustK, 0.3f, 1.6f),
                SpeedK = Clamp((float)Math.Pow(r.SpeedK / Math.Max(1e-4f, Ref.SpeedK), 0.3), 0.4f, 1.5f),
                YawK = Clamp((float)Math.Sqrt(r.YawK / Ref.YawK), 0.35f, Hi),
                AgilityK = Clamp((float)Math.Sqrt(r.AgilityK / Ref.AgilityK), 0.5f, Hi),
            };
        }

        /// <summary>The base tuning the factors multiply (DroneBody's values at full size).</summary>
        public struct Base { public float Speed, Climb, Yaw, Accel; }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// One line with the numbers before and after: "389 → 601 g · thrust 14.1 → 15.0 : 1 · top speed 1.20 → 1.14 m/s ·
        /// climb 0.60 → 0.58 m/s · turns 90 → 52 °/s". Only the numbers that moved (all of them with no change).
        /// </summary>
        public static string Compare(Flight a, Flight b, Base k)
        {
            var sb = new StringBuilder(160);
            Num(sb, "", a.MassG, b.MassG, "0", " g", 0.01f);
            Num(sb, "thrust ", a.Twr, b.Twr, "0.0", " : 1", 0.01f);
            Num(sb, "top speed ", a.SpeedK * k.Speed, b.SpeedK * k.Speed, "0.00", " m/s", 0.01f);
            Num(sb, "climb ", a.ClimbK * k.Climb, b.ClimbK * k.Climb, "0.00", " m/s", 0.01f);
            Num(sb, "pick-up ", a.AccelK * k.Accel, b.AccelK * k.Accel, "0.0", " m/s²", 0.01f);
            Num(sb, "turns ", a.YawK * k.Yaw, b.YawK * k.Yaw, "0", " °/s", 0.01f);
            if (sb.Length == 0) sb.Append("no change in flight");
            return sb.ToString();
        }

        static void Num(StringBuilder sb, string label, float a, float b, string f, string unit, float rel)
        {
            if (Math.Abs(b - a) <= rel * Math.Max(1e-3f, Math.Abs(a))) return;
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append(label).Append(a.ToString(f, Inv)).Append(" → ").Append(b.ToString(f, Inv)).Append(unit);
        }

        /// <summary>The numbers now, for a status line: "601 g · thrust 15.0 : 1 · top speed 1.14 m/s · climb 0.58 m/s · turns 52 °/s".</summary>
        public static string Describe(Flight f, Base k) =>
            f.MassG.ToString("0", Inv) + " g · thrust " + f.Twr.ToString("0.0", Inv) + " : 1 · top speed "
            + (f.SpeedK * k.Speed).ToString("0.00", Inv) + " m/s · climb " + (f.ClimbK * k.Climb).ToString("0.00", Inv)
            + " m/s · turns " + (f.YawK * k.Yaw).ToString("0", Inv) + " °/s";

        /// <summary>
        /// What Albie says about a change, in a sentence: what was changed, then the two or three biggest effects in flight
        /// ("Longer arms and a bigger battery: steadier, turns 42% slower, climbs 9% slower."). Null when nothing changed.
        /// </summary>
        public static string Say(Spec was, Spec now, Flight a, Flight b)
        {
            var what = new List<string>(6);
            Word(what, was.ArmMm, now.ArmMm, "longer arms", "shorter arms");
            Word(what, was.PropIn, now.PropIn, "bigger props", "smaller props");
            Word(what, was.MotorMm, now.MotorMm, "bigger motors", "smaller motors");
            Word(what, was.BatteryMah, now.BatteryMah, "a bigger battery", "a smaller battery");
            Word(what, was.PlateMm, now.PlateMm, "a bigger frame plate", "a smaller frame plate");
            Word(what, was.Arms, now.Arms, now.Arms + " arms", now.Arms + " arms");
            Word(what, was.PayloadG, now.PayloadG, "more payload", "less payload");

            var fx = new List<KeyValuePair<float, string>>(6);
            Effect(fx, a.YawK, b.YawK, "turns {0}% faster", "turns {0}% slower");
            Effect(fx, a.ClimbK, b.ClimbK, "climbs {0}% quicker", "climbs {0}% slower");
            Effect(fx, a.SpeedK, b.SpeedK, "{0}% more top speed", "{0}% less top speed");
            Effect(fx, a.AccelK, b.AccelK, "picks up speed {0}% quicker", "picks up speed {0}% slower");
            if (fx.Count == 0 && what.Count == 0) return null;
            fx.Sort((p, q) => q.Key.CompareTo(p.Key));

            var sb = new StringBuilder(160);
            if (what.Count == 0) sb.Append("The new design");
            else
            {
                for (int i = 0; i < what.Count; i++)
                {
                    if (i > 0) sb.Append(i == what.Count - 1 ? " and " : ", ");
                    sb.Append(what[i]);
                }
                sb[0] = char.ToUpperInvariant(sb[0]);
            }
            sb.Append(": ");
            string feel = b.AgilityK < a.AgilityK * 0.93f ? "steadier" : b.AgilityK > a.AgilityK * 1.07f ? "twitchier" : null;
            int said = 0;
            if (feel != null) { sb.Append(feel); said++; }
            for (int i = 0; i < fx.Count && i < 3; i++) { sb.Append(said > 0 ? ", " : "").Append(fx[i].Value); said++; }
            if (said == 0) sb.Append("it flies about the same");
            sb.Append('.');
            if (b.Twr < 1.1f) sb.Append(" Its thrust is only ").Append(b.Twr.ToString("0.0", Inv)).Append(" times its weight: it can't take off. Bigger props or motors, or a smaller battery.");
            else if (b.Twr < 2.5f) sb.Append(" With only ").Append(b.Twr.ToString("0.0", Inv)).Append(" times its weight in thrust it struggles to climb.");
            return sb.ToString();
        }

        static void Word(List<string> into, float a, float b, string more, string less)
        {
            if (Math.Abs(b - a) <= 1e-3f * Math.Max(1f, Math.Abs(a))) return;
            into.Add(b > a ? more : less);
        }

        static void Effect(List<KeyValuePair<float, string>> into, float a, float b, string more, string less)
        {
            float r = b / Math.Max(1e-4f, a) - 1f;
            int p = (int)Math.Round(Math.Abs(r) * 100f);
            if (p < 5) return;
            into.Add(new KeyValuePair<float, string>(Math.Abs(r), string.Format(Inv, r > 0f ? more : less, p)));
        }
    }
}
