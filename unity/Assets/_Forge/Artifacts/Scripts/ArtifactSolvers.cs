using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// A fixed piece of C# that moves things over time, driven by a <c>sim</c> node. This is how an artifact gets live
    /// behaviour without a script engine: the agent picks a solver and writes its numbers, and no generated code runs
    /// (D-18). Step and Position run every frame, so they must not allocate.
    /// </summary>
    public interface IArtifactSolver
    {
        int Bodies { get; }

        /// <summary>Simulated time per <see cref="Step"/>.</summary>
        double StepSize { get; }

        /// <summary>Nothing more to show, such as a body leaving the bounds. The sim stops and stays visible.</summary>
        bool Finished { get; }

        /// <summary>Reads the node's numbers. True if any changed, so what's on screen no longer matches them.</summary>
        bool Configure(JObject node, ArtifactInputs inputs);

        /// <summary>Back to the starting state, using the numbers from <see cref="Configure"/>.</summary>
        void Restart();

        void Step();

        Vector3 Position(int body);

        /// <summary>An index for <see cref="Value"/>, or -1. Look it up once, not per frame.</summary>
        int ValueIndex(string name);

        double Value(int index);
    }

    /// <summary>
    /// Which solvers this host has. A registry for the same reason as <see cref="ArtifactNodes"/>: a new kind of simulation
    /// is one <see cref="Register"/> call. The values a solver publishes are listed in <see cref="ArtifactSchema.Solvers"/>.
    /// </summary>
    public static class ArtifactSolvers
    {
        static readonly Dictionary<string, Func<IArtifactSolver>> solvers = new Dictionary<string, Func<IArtifactSolver>>
        {
            { "n_body", () => new NBodySolver() },
            { "pendulum", () => new PendulumSolver() },
        };

        public static void Register(string name, Func<IArtifactSolver> create) => solvers[name] = create;

        public static IArtifactSolver Create(string name) => solvers.TryGetValue(name, out var create) ? create() : null;
    }

    /// <summary>
    /// Bodies pulling on each other by gravity. Leapfrog (kick, drift, kick) keeps energy from creeping away over long runs,
    /// which a simple Euler step wouldn't. Doubles throughout: three-body motion is chaotic, so float rounding would soon
    /// become a different answer. The centre of mass is held at the origin, so unequal masses don't drift out of view.
    /// </summary>
    public sealed class NBodySolver : IArtifactSolver
    {
        const int Time = 0, EnergyDrift = 1, Closest = 2, Escaped = 3;
        static readonly string[] Names = { "time", "energy_drift_pct", "closest_approach", "escaped" };

        int count;
        double g = 1.0, step = 0.001, softening2, velocityScale = 1.0, bound = double.PositiveInfinity;
        double[] mass, startPos, startVel, pos, vel, acc;
        double time, startEnergy, closest;
        bool escaped;
        double[] configured;  // every number that decides the motion, to notice a real change

        public int Bodies => count;
        public double StepSize => step;
        public bool Finished => escaped;

        public bool Configure(JObject node, ArtifactInputs inputs)
        {
            var list = (JArray)node["bodies"];
            var p = node["params"] as JObject ?? new JObject();
            if (mass == null || count != list.Count) Allocate(list.Count);
            g = inputs.Number(p["g"], 1f);
            step = inputs.Number(p["step"], 0.001f);
            float soft = inputs.Number(p["softening"], 0f);
            softening2 = (double)soft * soft;
            velocityScale = inputs.Number(p["velocity_scale"], 1f);
            bound = p["bound_radius"] != null ? inputs.Number(p["bound_radius"], 0f) : double.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                var body = list[i];
                mass[i] = inputs.Number(body["mass"], 1f);
                Read(body["pos"], startPos, i, inputs);
                Read(body["vel"], startVel, i, inputs);
            }

            bool changed = Remember();
            return changed;
        }

        void Allocate(int n)
        {
            count = n;
            mass = new double[n];
            startPos = new double[n * 3];
            startVel = new double[n * 3];
            pos = new double[n * 3];
            vel = new double[n * 3];
            acc = new double[n * 3];
            configured = null;
        }

        // Straight from the JSON when it's a plain number: going through float would throw away the digits that keep a
        // delicate orbit like the figure eight on its path.
        static void Read(JToken vector, double[] into, int body, ArtifactInputs inputs)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                var value = (vector as JArray)?[axis];
                into[body * 3 + axis] = value == null ? 0.0
                    : value.Type == JTokenType.String ? inputs.Number(value, 0f) : value.Value<double>();
            }
        }

        bool Remember()
        {
            int size = count * 7 + 5;
            var now = new double[size];
            int at = 0;
            now[at++] = g;
            now[at++] = step;
            now[at++] = softening2;
            now[at++] = velocityScale;
            now[at++] = bound;
            Array.Copy(mass, 0, now, at, count);
            at += count;
            Array.Copy(startPos, 0, now, at, count * 3);
            at += count * 3;
            Array.Copy(startVel, 0, now, at, count * 3);
            bool changed = configured == null;
            if (!changed)
                for (int i = 0; i < size; i++)
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
            Array.Copy(startPos, pos, pos.Length);
            for (int i = 0; i < vel.Length; i++) vel[i] = startVel[i] * velocityScale;
            CentreMass();
            Accelerate();
            time = 0.0;
            escaped = false;
            startEnergy = Energy();
            closest = double.PositiveInfinity;
            Observe();
        }

        void CentreMass()
        {
            double total = 0.0;
            for (int i = 0; i < count; i++) total += mass[i];
            if (total <= 0.0) return;
            for (int axis = 0; axis < 3; axis++)
            {
                double p = 0.0, v = 0.0;
                for (int i = 0; i < count; i++)
                {
                    p += mass[i] * pos[i * 3 + axis];
                    v += mass[i] * vel[i * 3 + axis];
                }
                p /= total;
                v /= total;
                for (int i = 0; i < count; i++)
                {
                    pos[i * 3 + axis] -= p;
                    vel[i * 3 + axis] -= v;
                }
            }
        }

        public void Step()
        {
            double half = step * 0.5;
            for (int i = 0; i < vel.Length; i++) vel[i] += acc[i] * half;
            for (int i = 0; i < pos.Length; i++) pos[i] += vel[i] * step;
            Accelerate();
            for (int i = 0; i < vel.Length; i++) vel[i] += acc[i] * half;
            time += step;
            Observe();
        }

        void Accelerate()
        {
            Array.Clear(acc, 0, acc.Length);
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                {
                    int a = i * 3, b = j * 3;
                    double dx = pos[b] - pos[a], dy = pos[b + 1] - pos[a + 1], dz = pos[b + 2] - pos[a + 2];
                    double r2 = dx * dx + dy * dy + dz * dz + softening2;
                    if (r2 < 1e-12) r2 = 1e-12;  // two bodies on one point: a huge pull, never a divide by zero
                    double inv = g / (r2 * Math.Sqrt(r2));
                    double fi = mass[j] * inv, fj = mass[i] * inv;
                    acc[a] += dx * fi;
                    acc[a + 1] += dy * fi;
                    acc[a + 2] += dz * fi;
                    acc[b] -= dx * fj;
                    acc[b + 1] -= dy * fj;
                    acc[b + 2] -= dz * fj;
                }
        }

        void Observe()
        {
            for (int i = 0; i < count; i++)
            {
                int a = i * 3;
                double r2 = pos[a] * pos[a] + pos[a + 1] * pos[a + 1] + pos[a + 2] * pos[a + 2];
                if (r2 > bound * bound) escaped = true;
                for (int j = i + 1; j < count; j++)
                {
                    int b = j * 3;
                    double dx = pos[b] - pos[a], dy = pos[b + 1] - pos[a + 1], dz = pos[b + 2] - pos[a + 2];
                    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d < closest) closest = d;
                }
            }
        }

        double Energy()
        {
            double e = 0.0;
            for (int i = 0; i < count; i++)
            {
                int a = i * 3;
                e += 0.5 * mass[i] * (vel[a] * vel[a] + vel[a + 1] * vel[a + 1] + vel[a + 2] * vel[a + 2]);
                for (int j = i + 1; j < count; j++)
                {
                    int b = j * 3;
                    double dx = pos[b] - pos[a], dy = pos[b + 1] - pos[a + 1], dz = pos[b + 2] - pos[a + 2];
                    e -= g * mass[i] * mass[j] / Math.Sqrt(Math.Max(dx * dx + dy * dy + dz * dz + softening2, 1e-12));
                }
            }
            return e;
        }

        public Vector3 Position(int body)
        {
            int a = body * 3;
            return new Vector3((float)pos[a], (float)pos[a + 1], (float)pos[a + 2]);
        }

        public int ValueIndex(string name) => Array.IndexOf(Names, name);

        public double Value(int index)
        {
            switch (index)
            {
                case Time: return time;
                case EnergyDrift: return startEnergy == 0.0 ? 0.0 : Math.Abs((Energy() - startEnergy) / startEnergy) * 100.0;
                case Closest: return closest;
                case Escaped: return escaped ? 1.0 : 0.0;
                default: return 0.0;
            }
        }
    }
}
