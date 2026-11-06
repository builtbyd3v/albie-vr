using System;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>
    /// The ML template's closed-loop test (sandbox and checks only; on a headset it's the real truck): a cruise controller
    /// drives the mock truck straight at a cone or a wall from seeded random starts and speeds, and a brake node decides
    /// when to stop it. It runs the real graph controller through a <see cref="ControllerHost"/>, so a learned
    /// <c>model</c> brake is tested exactly as it would run on a Thing. Allocates per call, so don't call it every frame.
    /// </summary>
    public static class ObstacleTrial
    {
        public sealed class Result
        {
            public int Starts, Crashes;
            /// <summary>Mean distance driven before the end of the run: a brake that never lets it move scores 0 crashes and 0 m.</summary>
            public float MeanDistance;
            public override string ToString() => $"{Crashes}/{Starts} crashed, {MeanDistance:0.00} m driven on average";
        }

        /// <summary>
        /// A cruise graph: hold <c>$cruise</c> m/s straight ahead, then <paramref name="brake"/> (a rule or model node,
        /// or null for none) last, so it overrides the throttle.
        /// </summary>
        public static JObject CruiseGraph(JObject brake, float cruise)
        {
            var nodes = new JArray
            {
                new JObject
                {
                    ["id"] = "cruise", ["type"] = "pid", ["input"] = "speed.forward", ["setpoint"] = "$cruise",
                    ["kp"] = 4.0, ["ki"] = 2.0, ["kd"] = 0.0, ["i_limit"] = 0.5, ["output"] = "drive.throttle",
                },
                new JObject { ["id"] = "straight", ["type"] = "const", ["value"] = 0, ["output"] = "steer.angle" },
            };
            if (brake != null) nodes.Add(brake);
            return new JObject
            {
                ["schema"] = "albie.controller/1", ["name"] = "truck_cruise_brake", ["kind"] = "graph", ["version"] = 1, ["thing"] = "truck",
                // The PID's setpoint is normalised speed (speed.forward is speed / top speed).
                ["tunables"] = new JObject { ["cruise"] = new JObject { ["value"] = Math.Round(cruise / MockTruckSim.TopSpeed, 4), ["min"] = 0, ["max"] = 1 } },
                ["nodes"] = nodes,
            };
        }

        /// <summary>Runs the brake from <paramref name="starts"/> seeded starts, each at its own cruise speed in [minSpeed, maxSpeed].</summary>
        public static Result Run(JObject brake, int starts = 60, int seed = 11, float minSpeed = 0.8f, float maxSpeed = 2.6f, float seconds = 5f)
        {
            var rng = new Random(seed);
            var result = new Result { Starts = starts };
            var io = new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames);
            var host = new ControllerHost(io);
            var sim = new MockTruckSim { StopAtFinish = false };
            var probe = new MockTruckSim { StopAtFinish = false };
            const float dt = 0.02f;
            double distance = 0;
            for (int s = 0; s < starts; s++)
            {
                float cruise = minSpeed + (float)rng.NextDouble() * (maxSpeed - minSpeed);
                string error = host.Attach(ControllerFile.Parse(CruiseGraph(brake, cruise).ToString(), out string parseError));
                if (error != null || parseError != null) throw new InvalidOperationException(error ?? parseError);
                Start(sim, rng, cruise, probe);
                float x0 = sim.X, y0 = sim.Y;
                for (float t = 0; t < seconds && !sim.Crashed; t += dt)
                {
                    sim.Sense(io.Sensors);
                    host.Step(dt, sim.Speed);
                    sim.Step(io.Actuators, dt);
                }
                if (sim.Crashed) result.Crashes++;
                distance += Math.Sqrt((sim.X - x0) * (sim.X - x0) + (sim.Y - y0) * (sim.Y - y0));
            }
            result.MeanDistance = (float)(distance / starts);
            return result;
        }

        // Aimed at a cone ahead (mostly) or across the arena at a wall, already moving at the cruise speed. Every start
        // can be saved: braking hard at once stops it short, so a crash is always the brake's fault.
        static void Start(MockTruckSim sim, Random rng, float speed, MockTruckSim probe)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                Place(sim, rng, speed);
                if (!sim.HitsSomething() && Saveable(sim, probe)) return;
            }
            throw new InvalidOperationException("couldn't find a start the truck can stop from");
        }

        static readonly float[] FullBrake = { 0f, -1f };

        static bool Saveable(MockTruckSim from, MockTruckSim probe)
        {
            probe.Reset();
            probe.StopAtFinish = false;
            probe.X = from.X; probe.Y = from.Y; probe.Heading = from.Heading; probe.Speed = from.Speed;
            for (float t = 0; t < 1.5f && !probe.Crashed && probe.Speed > 0f; t += 0.02f) probe.Step(FullBrake, 0.02f);
            return !probe.Crashed;
        }

        static void Place(MockTruckSim sim, Random rng, float speed)
        {
            sim.Reset();
            sim.StopAtFinish = false;
            sim.Speed = speed;
            if (rng.NextDouble() < 0.75)
            {
                int cone = rng.Next(1, MockTruckSim.ConeX.Length);
                float back = 1.4f + (float)rng.NextDouble() * 1.4f;
                sim.X = MockTruckSim.ConeX[cone] - back;
                sim.Y = (float)(rng.NextDouble() * 1.4 - 0.7);
                float aimY = (float)(rng.NextDouble() * 0.16 - 0.08);
                sim.Heading = (float)Math.Atan2(aimY - sim.Y, MockTruckSim.ConeX[cone] - sim.X);
            }
            else
            {
                sim.X = 1f + (float)rng.NextDouble() * 7f;
                sim.Y = (float)(rng.NextDouble() * 0.6 - 0.3);
                sim.Heading = (rng.NextDouble() < 0.5 ? 1f : -1f) * (float)(Math.PI / 2 + (rng.NextDouble() - 0.5) * 0.6);
            }
        }
    }
}
