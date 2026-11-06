using System;

namespace Forge.Controllers
{
    /// <summary>
    /// The ML template's dataset (PROJECT_CONTEXT 15.5, 16.8 Nov 14): "is there an obstacle ahead?" from the truck's own
    /// sensors, made on the device with no network and no download.
    ///
    /// Each sample is the truck at a random pose and speed in the mock slalom arena (<see cref="MockTruckSim"/>, the fake
    /// Thing until the Thing runtime lands). Its features are the Thing's sensors exactly as a controller sees them
    /// (<c>ray_front.d0</c>-<c>d6</c>, <c>speed.forward</c>). Its label comes from physics, not from the rays: the sim is
    /// run forward for <see cref="Horizon"/> seconds driving straight at the same speed (at least <see cref="PlanSpeed"/>),
    /// and the label is 1 if the truck touches a cone or a wall. So the right answer depends on speed as well as distance (faster needs more room), which a
    /// fixed "front ray is short" rule can't capture and a small net can learn.
    ///
    /// Classes are balanced (half obstacle, half clear) by sampling until each has its share. Deterministic for a seed.
    /// <see cref="Maker"/> makes it a few ms a frame (the headset), <see cref="Generate"/> all at once (checks, tools).
    /// </summary>
    public sealed class ObstacleData
    {
        public const float Horizon = 0.6f, Dt = 0.02f;
        /// <summary>The label assumes at least this speed (m/s), so a truck creeping up to a cone still sees it ahead.</summary>
        public const float DefaultPlanSpeed = 1.0f;
        public const float MinSpeed = 0.2f, MaxSpeed = 2.8f;
        public static readonly string[] Features = MockTruckSim.SensorNames;
        public static readonly string[] Classes = { "clear", "obstacle" };

        public readonly float[][] X;   // raw sensors, in Features order
        public readonly bool[] Y;      // true = obstacle within Horizon
        public readonly int Seed;
        public readonly float PlanSpeed;
        /// <summary>How many random poses were tried to fill both classes (the rest were the over-full class, or inside something).</summary>
        public readonly int Tried;
        public int Count => X.Length;

        ObstacleData(float[][] x, bool[] y, int seed, int tried, float planSpeed) { X = x; Y = y; Seed = seed; Tried = tried; PlanSpeed = planSpeed; }

        /// <summary>Makes the whole dataset now (about 8 ms in the Editor). On the headset, use <see cref="Maker"/> to spread it over frames.</summary>
        public static ObstacleData Generate(int samples = 3000, int seed = 7, float planSpeed = DefaultPlanSpeed)
        {
            var maker = new Maker(samples, seed, planSpeed);
            while (!maker.Step(double.MaxValue)) { }
            return maker.Result;
        }

        /// <summary>
        /// Makes the dataset a slice at a time, so it doesn't take a whole frame on the headset: <see cref="Step"/> tries
        /// random poses until its time budget is used, and <see cref="Result"/> is set once every sample is made. The
        /// same seed gives exactly the samples <see cref="Generate"/> does. The rows are allocated up front, so a step
        /// allocates nothing (only the last one, which wraps the result).
        /// </summary>
        public sealed class Maker
        {
            public readonly int Samples, Seed;
            public readonly float PlanSpeed;
            public ObstacleData Result { get; private set; }
            public bool Done => Result != null;
            public int Made => n;
            public int Steps { get; private set; }
            public double ComputeSeconds => compute.Elapsed.TotalSeconds;

            readonly Random rng;
            readonly MockTruckSim sim = new MockTruckSim { StopAtFinish = false }, probe = new MockTruckSim { StopAtFinish = false };
            readonly float[][] x;
            readonly bool[] y;
            readonly float[] hold = new float[2];
            readonly System.Diagnostics.Stopwatch compute = new System.Diagnostics.Stopwatch(), slice = new System.Diagnostics.Stopwatch();
            readonly int half;
            int n, positives, negatives, tried;

            public Maker(int samples = 3000, int seed = 7, float planSpeed = DefaultPlanSpeed)
            {
                if (samples < 2) throw new ArgumentOutOfRangeException(nameof(samples), "at least 2 samples");
                Samples = samples; Seed = seed; PlanSpeed = planSpeed;
                rng = new Random(seed);
                half = samples / 2;
                x = new float[samples][]; y = new bool[samples];
                for (int i = 0; i < samples; i++) x[i] = new float[Features.Length];
            }

            /// <summary>Makes samples until <paramref name="budgetMs"/> ms are used or all are made. Returns <see cref="Done"/>.</summary>
            public bool Step(double budgetMs)
            {
                if (Done) return true;
                Steps++;
                compute.Start();
                slice.Restart();
                try
                {
                    while (n < Samples)
                    {
                        Try();
                        if (slice.Elapsed.TotalMilliseconds >= budgetMs) break;
                    }
                }
                finally { compute.Stop(); }
                if (n >= Samples) Result = new ObstacleData(x, y, Seed, tried, PlanSpeed);
                return Done;
            }

            // One random pose: kept if it isn't inside something and its class isn't full yet.
            void Try()
            {
                tried++;
                if (tried > Samples * 200) throw new InvalidOperationException("couldn't balance the classes");
                sim.Reset();
                sim.X = Uniform(rng, MockTruckSim.WallXMin + 0.2f, MockTruckSim.WallXMax - 0.2f);
                sim.Y = Uniform(rng, MockTruckSim.WallYMin + 0.2f, MockTruckSim.WallYMax - 0.2f);
                // Mostly along the course, sometimes across or back, so walls ahead are in the data too.
                sim.Heading = rng.NextDouble() < 0.7 ? Uniform(rng, -1.2f, 1.2f) : Uniform(rng, -(float)Math.PI, (float)Math.PI);
                sim.Speed = Uniform(rng, MinSpeed, MaxSpeed);
                if (sim.HitsSomething()) return;

                bool obstacle = Ahead(sim, probe, PlanSpeed, hold);
                if (obstacle ? positives >= Samples - half : negatives >= half) return;
                sim.Sense(x[n]);
                y[n] = obstacle;
                if (obstacle) positives++; else negatives++;
                n++;
            }
        }

        /// <summary>
        /// The label: driving straight for <see cref="Horizon"/> s at the current speed, or at <paramref name="planSpeed"/>
        /// if that's faster, hits something. The floor matters in closed loop: with the current speed alone, a truck that
        /// braked and is crawling next to a cone reads "clear", the cruise control speeds it up, and it hits the cone.
        /// </summary>
        public static bool Ahead(MockTruckSim from, MockTruckSim probe, float planSpeed = DefaultPlanSpeed) => Ahead(from, probe, planSpeed, new float[2]);

        static bool Ahead(MockTruckSim from, MockTruckSim probe, float planSpeed, float[] hold)
        {
            float speed = Math.Max(from.Speed, planSpeed);
            probe.Reset();
            probe.StopAtFinish = false;
            probe.X = from.X; probe.Y = from.Y; probe.Heading = from.Heading; probe.Speed = speed;
            // Hold the speed: the throttle that balances drag.
            hold[0] = 0f; hold[1] = Math.Min(1f, MockTruckSim.Drag * speed / MockTruckSim.MaxAccel);
            for (float t = 0; t < Horizon && !probe.Crashed; t += Dt) probe.Step(hold, Dt);
            return probe.Crashed;
        }

        public int Positives { get { int p = 0; foreach (bool b in Y) if (b) p++; return p; } }

        static float Uniform(Random rng, float a, float b) => a + (float)rng.NextDouble() * (b - a);
    }
}
