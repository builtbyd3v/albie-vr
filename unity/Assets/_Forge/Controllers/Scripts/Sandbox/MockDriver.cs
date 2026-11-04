using System;

namespace Forge.Controllers
{
    /// <summary>
    /// A scripted stand-in for a person driving the mock slalom: pure pursuit along a weave (left of cone 0, right of
    /// cone 1, ...) with smoothed noise on the steering, like a hand on a transmitter. Ported from
    /// hub/ml/make_controller_mocks.py. It writes <see cref="ManualInputs"/> (stick.y, hand.roll), so laps it drives go
    /// through the real manual controller, host and recorder. For checks and the sandbox's auto-lap key.
    /// </summary>
    public sealed class MockDriver
    {
        const float Weave = 0.4f, Lookahead = 0.5f;
        readonly Random rng;
        readonly int stickY = ManualInputs.IndexOf("stick.y"), handRoll = ManualInputs.IndexOf("hand.roll");
        float noise;
        public float Cruise;
        public float NoiseScale;

        public MockDriver(int seed, float cruise = 1.2f, float noiseScale = 1f)
        {
            rng = new Random(seed);
            Cruise = cruise;
            NoiseScale = noiseScale;
        }

        /// <summary>Writes this step's stick inputs for the truck's current state.</summary>
        public void Drive(MockTruckSim sim)
        {
            float tx = sim.X + Lookahead, ty = PathY(tx);
            float alpha = (float)Math.Atan2(ty - sim.Y, tx - sim.X) - sim.Heading;
            float distance = (float)Math.Sqrt((tx - sim.X) * (tx - sim.X) + (ty - sim.Y) * (ty - sim.Y));
            noise = 0.9f * noise + 0.1f * Gaussian() * 0.25f * NoiseScale;
            float maxSteer = MockTruckSim.MaxSteerDeg * (float)Math.PI / 180f;
            float steer = (float)Math.Atan(2f * MockTruckSim.Wheelbase * Math.Sin(alpha) / Math.Max(distance, 0.3f)) / maxSteer + noise;
            float throttle = MockTruckSim.Drag * Cruise / MockTruckSim.MaxAccel + 1.5f * (Cruise - sim.Speed);
            ManualInputs.Values[handRoll] = Math.Max(-1f, Math.Min(1f, steer));
            ManualInputs.Values[stickY] = Math.Max(-1f, Math.Min(1f, throttle));
        }

        /// <summary>The weave the driver follows, eased in before the first cone and out after the last.</summary>
        public static float PathY(float x)
        {
            float first = MockTruckSim.ConeX[0], last = MockTruckSim.ConeX[MockTruckSim.ConeX.Length - 1];
            float easeIn = Clamp01((x - 0.3f) / (first - 0.3f));
            float easeOut = Clamp01((MockTruckSim.FinishX + 0.3f - x) / (MockTruckSim.FinishX + 0.3f - last));
            return Weave * (float)Math.Cos(Math.PI * (x - first) / MockTruckSim.ConeSpacing) * easeIn * easeOut;
        }

        float Gaussian()
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
