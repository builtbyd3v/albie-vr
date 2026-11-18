using System;
using System.Globalization;
using System.Text;

namespace Forge.Controllers
{
    /// <summary>
    /// The RC truck's design: nine numbers a person can change (<see cref="Params"/>), and the driving physics they give
    /// (<see cref="Derive"/>, a <see cref="TruckPhysics"/>) that <see cref="ExampleTruckBody.ApplyDesign"/> drives with and
    /// <see cref="TruckShell.SetDesign"/> draws. Plain C# (no Unity), so the model can be checked headless.
    ///
    /// The model, full size, SI units (r wheel radius, G the fixed 14:1 gearing, V = 3.7 V x cells, m = chassis + 45 g a cell):
    /// - gearing speed  v_gear = eta x kV x V / G / 60 x 2 pi r   (the motor's no-load wheel speed, less losses)
    /// - launch push    F = I_launch x (60 / (2 pi kV)) x G x eta / r   (the ESC's launch current through the motor's torque constant)
    /// - acceleration   A = min(F / m, mu g)                       (all four wheels driven: traction-limited by grip x weight)
    /// - along the road dv/dt = throttle x A - (A / v_gear) v - (c / m) v|v|,  c = 1/2 rho Cd x frontal area
    ///                  (the motor's back-EMF slope, which is also the coast-down drag brake, plus air); braking is the same
    ///                  A reversed, so it is traction-limited too
    /// - top speed      the v where A = (A / v_gear) v + (c / m) v^2
    /// - turn radius    R = wheelbase / tan(steering lock)
    /// - slide limit    a_grip = mu g;  roll-over limit  a_tip = g x (track / 2) / h_cg,  h_cg = ride height + 34 mm
    ///                  (the static stability factor); a corner asking more lateral g than mu g understeers, and if the truck
    ///                  can tip before it slides (track / 2h &lt; mu) it rolls over instead
    /// - corner speed   sqrt(min(a_grip, a_tip) x R) at full lock
    /// The two drivetrain constants (eta and I_launch) are fitted once so the stock design drives as the original tuning:
    /// 3 m/s top speed and 3 m/s^2 off the line, so existing laps and trained brains still make sense.
    /// </summary>
    public sealed class TruckDesign
    {
        public struct Param
        {
            public string Name, Label, Unit, Help;
            public float Min, Max, Default, Step;
        }

        public const int Wheelbase = 0, Track = 1, WheelDiameter = 2, RideHeight = 3, SteerLock = 4, Grip = 5, MotorKv = 6, Cells = 7, Mass = 8;

        /// <summary>The nine parameters, in panel order. The names are the CAD part's (hub/cad/parts/truck.py) and the tool's.</summary>
        public static readonly Param[] Params =
        {
            P("wheelbase_mm", "Wheelbase", "mm", 120f, 280f, 170f, 5f, "Front axle to rear axle. Longer turns wider and feels steadier."),
            P("track_mm", "Track width", "mm", 110f, 220f, 144f, 2f, "Left wheel centre to right. Wider is harder to roll over."),
            P("wheel_diameter_mm", "Wheel size", "mm", 50f, 110f, 68f, 2f, "Tyre diameter. Bigger is faster but pushes less and sits taller."),
            P("ride_height_mm", "Ride height", "mm", 10f, 60f, 21f, 1f, "Ground to the chassis. Higher clears bumps but tips sooner."),
            P("steer_deg", "Steering lock", "deg", 20f, 45f, 35f, 1f, "Front wheel angle at full lock. More turns tighter."),
            P("tyre_grip", "Tyre grip", "mu", 0.5f, 1.6f, 1.1f, 0.05f, "Tyre compound: 0.6 hard plastic, 1.1 rubber, 1.5 soft foam."),
            P("motor_kv", "Motor", "kV", 1000f, 4500f, 2000f, 100f, "rpm per volt. More is faster flat out but pushes less off the line."),
            P("battery_cells", "Battery", "cells", 1f, 4f, 2f, 1f, "LiPo cells, 3.7 V and 45 g each. More is faster and heavier."),
            P("mass_g", "Chassis + body", "g", 250f, 1500f, 460f, 10f, "Without the battery. Heavier is slower to speed up, same top speed."),
        };

        // Fixed parts of the truck (in the CAD part too).
        public const float GearRatio = 14f, CellVolts = 3.7f, CellMassG = 45f, Gravity = 9.81f;
        public const float AirDensity = 1.2f, DragCoefficient = 0.4f;
        /// <summary>Centre of mass above the chassis bottom, and the roof above it, at the stock layout (m).</summary>
        public const float ComAboveChassis = 0.034f, RoofAboveChassis = 0.109f, TyreWidth = 0.032f;
        /// <summary>The chassis can't sit lower than this under the wheel's centre (m): big wheels lift it.</summary>
        public const float ChassisUnderAxle = 0.013f;

        /// <summary>The original tuning the stock design reproduces (MockTruckSim: 3 m/s, 3 m/s^2).</summary>
        public const float ReferenceTopSpeed = 3f, ReferenceAccel = 3f;

        /// <summary>Drivetrain efficiency and the ESC's launch current (A), fitted so the stock design drives as the original tuning.</summary>
        public static readonly float Efficiency, LaunchCurrent;

        static TruckDesign()
        {
            var d = new TruckDesign();
            float r = d.Values[WheelDiameter] * 0.0005f, v = d.Values[Cells] * CellVolts, m = d.MassKg;
            float cq = AeroC(d) / m;
            // Top speed 3 with A = 3: v_gear = 3A / (A - 9 cq); then the launch current for A = F / m.
            float vt = ReferenceTopSpeed, a = ReferenceAccel;
            float vGear = vt * a / (a - vt * vt * cq);
            float noLoad = d.Values[MotorKv] * v / GearRatio / 60f * 2f * MathF.PI * r;
            Efficiency = vGear / noLoad;
            float kt = 60f / (2f * MathF.PI * d.Values[MotorKv]);
            LaunchCurrent = a * m * r / (kt * GearRatio * Efficiency);
        }

        static Param P(string name, string label, string unit, float min, float max, float def, float step, string help) =>
            new Param { Name = name, Label = label, Unit = unit, Min = min, Max = max, Default = def, Step = step, Help = help };

        public readonly float[] Values = new float[Params.Length];

        public TruckDesign() { for (int i = 0; i < Params.Length; i++) Values[i] = Params[i].Default; }

        public TruckDesign Clone()
        {
            var c = new TruckDesign();
            Array.Copy(Values, c.Values, Values.Length);
            return c;
        }

        public void CopyFrom(TruckDesign other) { if (other != null) Array.Copy(other.Values, Values, Values.Length); }

        public static int IndexOf(string name)
        {
            for (int i = 0; i < Params.Length; i++) if (Params[i].Name == name) return i;
            return -1;
        }

        /// <summary>Sets a value, clamped to its range and snapped to its step. Returns the value kept.</summary>
        public float Set(int index, float value)
        {
            var p = Params[index];
            if (float.IsNaN(value) || float.IsInfinity(value)) value = p.Default;
            value = Math.Clamp(value, p.Min, p.Max);
            if (p.Step > 0f) value = Math.Clamp(p.Min + MathF.Round((value - p.Min) / p.Step) * p.Step, p.Min, p.Max);
            value = MathF.Round(value * 1000f) / 1000f;
            Values[index] = value;
            return value;
        }

        public bool IsDefault
        {
            get
            {
                for (int i = 0; i < Params.Length; i++) if (MathF.Abs(Values[i] - Params[i].Default) > 1e-4f) return false;
                return true;
            }
        }

        public bool Same(TruckDesign other)
        {
            if (other == null) return false;
            for (int i = 0; i < Params.Length; i++) if (MathF.Abs(Values[i] - other.Values[i]) > 1e-4f) return false;
            return true;
        }

        /// <summary>A short key of the values ("170,144,68,..."): equal keys drive the same.</summary>
        public string Fingerprint()
        {
            var sb = new StringBuilder(64);
            for (int i = 0; i < Params.Length; i++) sb.Append(i == 0 ? "" : ",").Append(Values[i].ToString("0.###", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // ---- Geometry ----------------------------------------------------------------------------------------------

        public float WheelbaseM => Values[Wheelbase] * 0.001f;
        public float TrackM => Values[Track] * 0.001f;
        public float WheelRadiusM => Values[WheelDiameter] * 0.0005f;
        /// <summary>The chassis's real height off the ground (m): the ride height, or higher when big wheels lift it.</summary>
        public float ChassisHeightM => MathF.Max(Values[RideHeight] * 0.001f, WheelRadiusM - ChassisUnderAxle);
        public float ComHeightM => ChassisHeightM + ComAboveChassis;
        public float MassKg => (Values[Mass] + Values[Cells] * CellMassG) * 0.001f;

        static float AeroC(TruckDesign d) =>
            0.5f * AirDensity * DragCoefficient * (d.TrackM + TyreWidth) * (d.ChassisHeightM + RoofAboveChassis);

        // ---- Physics -----------------------------------------------------------------------------------------------

        public TruckPhysics Derive()
        {
            var p = new TruckPhysics();
            float r = WheelRadiusM, kv = Values[MotorKv], volts = Values[Cells] * CellVolts, m = MassKg, mu = Values[Grip];
            p.MassKg = m;
            p.Volts = volts;
            p.Wheelbase = WheelbaseM;
            p.Track = TrackM;
            p.MaxSteerDeg = Values[SteerLock];
            p.ComHeight = ComHeightM;
            p.GearSpeed = Efficiency * kv * volts / GearRatio / 60f * 2f * MathF.PI * r;
            p.LaunchForce = LaunchCurrent * (60f / (2f * MathF.PI * kv)) * GearRatio * Efficiency / r;
            p.GripAccel = mu * Gravity;
            float motorAccel = p.LaunchForce / m;
            p.TractionLimited = motorAccel > p.GripAccel;
            p.Accel = MathF.Min(motorAccel, p.GripAccel);
            p.LinearDrag = p.Accel / p.GearSpeed;
            p.QuadDrag = AeroC(this) / m;
            float lin = p.LinearDrag, q = p.QuadDrag, a = p.Accel;
            p.TopSpeed = q > 1e-6f ? (-lin + MathF.Sqrt(lin * lin + 4f * q * a)) / (2f * q) : a / lin;
            p.TurnRadius = p.Wheelbase / MathF.Tan(p.MaxSteerDeg * MathF.PI / 180f);
            p.TipAccel = Gravity * (p.Track * 0.5f) / p.ComHeight;
            p.TipsFirst = p.TipAccel < p.GripAccel;
            p.LateralLimit = MathF.Min(p.TipAccel, p.GripAccel);
            p.CornerSpeed = MathF.Sqrt(p.LateralLimit * p.TurnRadius);
            // 0 to 90% of top speed and the stop from top speed, by stepping the same equation the body steps.
            const float dt = 0.002f;
            float v = 0f, t = 0f;
            while (v < 0.9f * p.TopSpeed && t < 30f) { v += (a - lin * v - q * v * v) * dt; t += dt; }
            p.TimeTo90 = t;
            v = p.TopSpeed; float x = 0f; t = 0f;
            while (v > 0f && t < 30f) { v -= (a + lin * v + q * v * v) * dt; x += MathF.Max(v, 0f) * dt; t += dt; }
            p.StopDistance = x;
            return p;
        }

        // ---- Words -------------------------------------------------------------------------------------------------

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Format(int index, float value)
        {
            var p = Params[index];
            string number = p.Step >= 1f ? value.ToString("0", Inv) : value.ToString("0.00", Inv);
            switch (p.Unit)
            {
                case "deg": return number + "°";
                case "mu": return number;
                case "kV": return number + " kV";
                case "cells": return number + "S (" + (value * CellVolts).ToString("0.#", Inv) + " V)";
                default: return number + " " + p.Unit;
            }
        }

        /// <summary>The live readout: "top speed 3.0 m/s · turns in 0.24 m · slides above 1.6 m/s at full lock".</summary>
        public static string Readout(TruckPhysics p)
        {
            string corner = p.TipsFirst
                ? "tips over above " + p.CornerSpeed.ToString("0.0", Inv) + " m/s in a tight turn"
                : "slides above " + p.CornerSpeed.ToString("0.0", Inv) + " m/s at full lock (it slides before it can tip)";
            return "top speed " + p.TopSpeed.ToString("0.0", Inv) + " m/s · 0 to 90% in " + p.TimeTo90.ToString("0.0", Inv) + " s"
                 + (p.TractionLimited ? " (wheelspin: grip-limited)" : "")
                 + " · stops in " + p.StopDistance.ToString("0.0", Inv) + " m · turns in " + p.TurnRadius.ToString("0.00", Inv) + " m · " + corner;
        }

        /// <summary>
        /// What changed between two designs, in words: the parameters ("wider track (144 to 180 mm)") and then what that does
        /// to the driving ("corners 18% faster before it slides; top speed 3.0 to 3.4 m/s"). Empty when nothing changed.
        /// </summary>
        public static string Compare(TruckDesign before, TruckDesign after)
        {
            if (before == null || after == null) return "";
            var parts = new StringBuilder();
            for (int i = 0; i < Params.Length; i++)
            {
                float a = before.Values[i], b = after.Values[i];
                if (MathF.Abs(a - b) < 1e-4f) continue;
                parts.Append(parts.Length == 0 ? "" : ", ").Append(Word(i, b > a)).Append(" (").Append(Format(i, a)).Append( " to ").Append(Format(i, b)).Append(")");
            }
            if (parts.Length == 0) return "";
            TruckPhysics p0 = before.Derive(), p1 = after.Derive();
            var effects = new StringBuilder();
            void Add(string s) { if (!string.IsNullOrEmpty(s)) effects.Append(effects.Length == 0 ? "" : "; ").Append(s); }
            Add(Change("top speed", p0.TopSpeed, p1.TopSpeed, "m/s", "0.0"));
            Add(PercentPhrase(p0.TimeTo90 / MathF.Max(p1.TimeTo90, 1e-3f), "gets up to speed", "quicker", "slower"));
            Add(Change("turn radius", p0.TurnRadius, p1.TurnRadius, "m", "0.00"));
            string limit = p1.TipsFirst ? "before it tips" : "before it slides";
            Add(PercentPhrase(p1.CornerSpeed / MathF.Max(p0.CornerSpeed, 1e-3f), "corners", "faster " + limit, "slower " + limit));
            if (p1.TipsFirst && !p0.TipsFirst) Add("now it rolls over before its tyres slide: keep tight turns under " + p1.CornerSpeed.ToString("0.0", Inv) + " m/s");
            else if (!p1.TipsFirst && p0.TipsFirst) Add("it slides now instead of rolling over");
            if (p1.TractionLimited && !p0.TractionLimited) Add("the wheels spin off the line: grip, not the motor, limits the launch");
            return Capital(parts.ToString()) + (effects.Length > 0 ? ". " + Capital(effects.ToString()) + "." : ".");
        }

        static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        static string Word(int i, bool up)
        {
            switch (i)
            {
                case Wheelbase: return up ? "longer wheelbase" : "shorter wheelbase";
                case Track: return up ? "wider track" : "narrower track";
                case WheelDiameter: return up ? "bigger wheels" : "smaller wheels";
                case RideHeight: return up ? "higher ride" : "lower ride";
                case SteerLock: return up ? "more steering lock" : "less steering lock";
                case Grip: return up ? "grippier tyres" : "harder tyres";
                case MotorKv: return up ? "faster-wound motor" : "torquier motor";
                case Cells: return up ? "more battery cells" : "fewer battery cells";
                default: return up ? "heavier" : "lighter";
            }
        }

        static string Change(string what, float a, float b, string unit, string fmt)
        {
            string from = a.ToString(fmt, Inv), to = b.ToString(fmt, Inv);
            return from == to ? null : what + " " + from + " to " + to + " " + unit;
        }

        static string PercentPhrase(float ratio, string what, string more, string less)
        {
            int pct = (int)MathF.Round((ratio - 1f) * 100f);
            if (pct == 0) return null;
            return what + " " + (pct > 0 ? pct + "% " + more : -pct + "% " + less);
        }
    }

    /// <summary>The driving numbers a <see cref="TruckDesign"/> gives, full size, SI units.</summary>
    public struct TruckPhysics
    {
        /// <summary>Launch acceleration at full throttle (m/s^2) and the two drag terms: dv/dt = throttle A - LinearDrag v - QuadDrag v|v|.</summary>
        public float Accel, LinearDrag, QuadDrag;
        public float TopSpeed, GearSpeed, LaunchForce, MassKg, Volts;
        public float Wheelbase, Track, MaxSteerDeg, ComHeight, TurnRadius;
        /// <summary>Lateral acceleration (m/s^2) where the tyres let go, where the truck rolls over, and the lower of the two.</summary>
        public float GripAccel, TipAccel, LateralLimit;
        /// <summary>The fastest it holds full lock (m/s).</summary>
        public float CornerSpeed;
        public float TimeTo90, StopDistance;
        public bool TipsFirst, TractionLimited;
    }
}
