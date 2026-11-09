using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// A pendulum of one or two links hanging from the <c>sim</c> node's origin and swinging in its x-y plane: the
    /// <c>pendulum</c> solver. Each body is a bob at the end of a massless rod, with its <c>length</c> in metres, its
    /// starting <c>angle</c> from straight down and its starting <c>omega</c>, both in degrees. Classic fourth-order
    /// Runge-Kutta in doubles, because a double pendulum is chaotic: float rounding would soon become a different answer,
    /// and two copies started a hair apart (<c>params.angle_offset</c>) are the whole point.
    /// </summary>
    public sealed class PendulumSolver : IArtifactSolver
    {
        public const int MaxLinks = 2;
        const int Time = 0, EnergyDrift = 1, Flips = 2, Angle1 = 3, Angle2 = 4;
        static readonly string[] Names = { "time", "energy_drift_pct", "flips", "angle_1", "angle_2" };

        int links;
        double g = 9.81, step = 0.001, damping, offset;
        readonly double[] mass = new double[MaxLinks], length = new double[MaxLinks];
        readonly double[] startAngle = new double[MaxLinks], startOmega = new double[MaxLinks];

        // The state is (angle 1, angle 2, omega 1, omega 2), angles in radians and unwrapped, so a flip shows as a turn.
        readonly double[] s = new double[4], k1 = new double[4], k2 = new double[4], k3 = new double[4], k4 = new double[4], tmp = new double[4];
        readonly long[] turn = new long[MaxLinks];
        double time, startEnergy;
        long flips;
        double[] configured;

        public int Bodies => links;
        public double StepSize => step;
        public bool Finished => false;

        public bool Configure(JObject node, ArtifactInputs inputs)
        {
            var list = (JArray)node["bodies"];
            var p = node["params"] as JObject ?? new JObject();
            links = Mathf.Clamp(list.Count, 1, MaxLinks);
            g = inputs.Number(p["g"], 9.81f);
            step = inputs.Number(p["step"], 0.001f);
            damping = inputs.Number(p["damping"], 0f);
            offset = Deg(inputs.Number(p["angle_offset"], 0f), p["angle_offset"]);
            for (int i = 0; i < links; i++)
            {
                var body = list[i];
                mass[i] = inputs.Number(body["mass"], 1f);
                length[i] = inputs.Number(body["length"], 0.5f);
                startAngle[i] = Deg(inputs.Number(body["angle"], 0f), body["angle"]);
                startOmega[i] = Deg(inputs.Number(body["omega"], 0f), body["omega"]);
            }
            return Remember();
        }

        // A plain number straight from the JSON: a nudge of 0.001 degrees has to survive the trip, so not through float.
        static double Deg(float viaInputs, JToken raw)
        {
            double degrees = raw != null && (raw.Type == JTokenType.Float || raw.Type == JTokenType.Integer) ? raw.Value<double>() : viaInputs;
            return degrees * Math.PI / 180.0;
        }

        bool Remember()
        {
            var now = new double[5 + MaxLinks * 4];
            int at = 0;
            now[at++] = links;
            now[at++] = g;
            now[at++] = step;
            now[at++] = damping;
            now[at++] = offset;
            for (int i = 0; i < MaxLinks; i++)
            {
                now[at++] = mass[i];
                now[at++] = length[i];
                now[at++] = startAngle[i];
                now[at++] = startOmega[i];
            }
            bool changed = configured == null;
            if (!changed)
                for (int i = 0; i < now.Length; i++)
                    if (now[i] != configured[i])
                    {
                        changed = true;
                        break;
                    }
            configured = now;
            return changed;
        }

        public void Restart()
        {
            s[0] = startAngle[0] + offset;
            s[1] = links > 1 ? startAngle[1] + offset : 0.0;
            s[2] = startOmega[0];
            s[3] = links > 1 ? startOmega[1] : 0.0;
            time = 0.0;
            flips = 0;
            for (int i = 0; i < MaxLinks; i++) turn[i] = Turn(s[i]);
            startEnergy = Energy();
        }

        public void Step()
        {
            double h = step;
            Derive(s, k1);
            for (int i = 0; i < 4; i++) tmp[i] = s[i] + 0.5 * h * k1[i];
            Derive(tmp, k2);
            for (int i = 0; i < 4; i++) tmp[i] = s[i] + 0.5 * h * k2[i];
            Derive(tmp, k3);
            for (int i = 0; i < 4; i++) tmp[i] = s[i] + h * k3[i];
            Derive(tmp, k4);
            for (int i = 0; i < 4; i++) s[i] += h / 6.0 * (k1[i] + 2.0 * k2[i] + 2.0 * k3[i] + k4[i]);
            time += h;
            for (int i = 0; i < links; i++)
            {
                long now = Turn(s[i]);
                if (now != turn[i])
                {
                    flips += Math.Abs(now - turn[i]);
                    turn[i] = now;
                }
            }
        }

        // Which turn an angle is on, counted at the top: going over the pivot changes it by one.
        static long Turn(double angle) => (long)Math.Floor((angle + Math.PI) / (2.0 * Math.PI));

        // The equations of motion for point masses on massless rods (Lagrangian), plus linear damping at each joint.
        void Derive(double[] x, double[] d)
        {
            double a1 = x[0], a2 = x[1], w1 = x[2], w2 = x[3];
            d[0] = w1;
            d[1] = w2;
            if (links == 1)
            {
                d[2] = -g / length[0] * Math.Sin(a1) - damping * w1;
                d[3] = 0.0;
                return;
            }
            double m1 = mass[0], m2 = mass[1], l1 = length[0], l2 = length[1];
            double delta = a1 - a2, sinD = Math.Sin(delta), cosD = Math.Cos(delta);
            double den = 2.0 * m1 + m2 - m2 * Math.Cos(2.0 * delta);
            d[2] = (-g * (2.0 * m1 + m2) * Math.Sin(a1) - m2 * g * Math.Sin(a1 - 2.0 * a2)
                    - 2.0 * sinD * m2 * (w2 * w2 * l2 + w1 * w1 * l1 * cosD)) / (l1 * den) - damping * w1;
            d[3] = 2.0 * sinD * (w1 * w1 * l1 * (m1 + m2) + g * (m1 + m2) * Math.Cos(a1) + w2 * w2 * l2 * m2 * cosD) / (l2 * den)
                   - damping * w2;
        }

        double Energy()
        {
            double a1 = s[0], a2 = s[1], w1 = s[2], w2 = s[3];
            double x1v = length[0] * Math.Cos(a1) * w1, y1v = length[0] * Math.Sin(a1) * w1;
            double y1 = -length[0] * Math.Cos(a1);
            double e = 0.5 * mass[0] * (x1v * x1v + y1v * y1v) + mass[0] * g * y1;
            if (links > 1)
            {
                double x2v = x1v + length[1] * Math.Cos(a2) * w2, y2v = y1v + length[1] * Math.Sin(a2) * w2;
                double y2 = y1 - length[1] * Math.Cos(a2);
                e += 0.5 * mass[1] * (x2v * x2v + y2v * y2v) + mass[1] * g * y2;
            }
            return e;
        }

        public Vector3 Position(int body)
        {
            double x = length[0] * Math.Sin(s[0]), y = -length[0] * Math.Cos(s[0]);
            if (body > 0)
            {
                x += length[1] * Math.Sin(s[1]);
                y -= length[1] * Math.Cos(s[1]);
            }
            return new Vector3((float)x, (float)y, 0f);
        }

        public int ValueIndex(string name) => Array.IndexOf(Names, name);

        public double Value(int index)
        {
            switch (index)
            {
                case Time: return time;
                // Relative to the swing's own size (its height above the lowest point), since the energy itself can be 0.
                case EnergyDrift:
                {
                    double scale = Math.Abs(startEnergy - Lowest());
                    return scale < 1e-12 ? 0.0 : Math.Abs(Energy() - startEnergy) / scale * 100.0;
                }
                case Flips: return flips;
                case Angle1: return Wrapped(s[0]);
                case Angle2: return links > 1 ? Wrapped(s[1]) : 0.0;
                default: return 0.0;
            }
        }

        double Lowest()
        {
            double e = -mass[0] * g * length[0];
            if (links > 1) e -= mass[1] * g * (length[0] + length[1]);
            return e;
        }

        // Degrees from straight down, between -180 and 180.
        static double Wrapped(double angle)
        {
            double d = angle * 180.0 / Math.PI;
            d = (d + 180.0) % 360.0;
            if (d < 0.0) d += 360.0;
            return d - 180.0;
        }
    }
}
