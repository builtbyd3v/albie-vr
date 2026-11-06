using System;

namespace Forge.Controllers
{
    /// <summary>
    /// A scripted stand-in for a person driving the mock slalom: pure pursuit along a weave (left of cone 0, right of
    /// cone 1, ...; on a room course, through each gate's side, <see cref="MockCourse.PathY"/>) with smoothed noise on the steering, like a hand on a transmitter. Ported from
    /// hub/ml/make_controller_mocks.py. It writes <see cref="ManualInputs"/> (stick.y, hand.roll), so laps it drives go
    /// through the real manual controller, host and recorder. For checks and the sandbox's auto-lap key.
    /// </summary>
    public sealed class MockDriver
    {
        /// <summary>How far to each side of a cone the driver passes, and how far ahead it aims (the corridor's 0.4 and 0.5).</summary>
        public float Weave = 0.4f, Lookahead = 0.5f;
        const float CorridorWeave = 0.4f;
        /// <summary>
        /// The hand's wobble, in steer.angle units. 0.13 gives the path the same wobble the 0.25 did on the old, bigger
        /// truck (0.28 m, 30°): a stick wobble turns the 0.17 m, 35° truck about twice as sharply.
        /// </summary>
        public const float Wobble = 0.13f;
        readonly Random rng;
        readonly int stickY = ManualInputs.IndexOf("stick.y"), handRoll = ManualInputs.IndexOf("hand.roll");
        float noise;
        public float Cruise;
        public float NoiseScale;

        /// <summary>How a room slalom is driven: through the middle of each gate (0.3 m out), aiming 0.35 m ahead, at 0.7 m/s.</summary>
        public const float RoomWeave = 0.3f, RoomLookahead = 0.35f, RoomCruise = 0.7f;

        /// <summary>A driver for a room slalom (<see cref="MockCourse"/>). All 60 laps of 10 drivers in the 6 mock rooms finish.</summary>
        public static MockDriver ForRoom(int seed, float noiseScale = 1f) =>
            new MockDriver(seed, RoomCruise, noiseScale) { Weave = RoomWeave, Lookahead = RoomLookahead };

        public MockDriver(int seed, float cruise = 1.2f, float noiseScale = 1f)
        {
            rng = new Random(seed);
            Cruise = cruise;
            NoiseScale = noiseScale;
        }

        /// <summary>Writes this step's stick inputs for the truck's current state.</summary>
        public void Drive(MockTruckSim sim)
        {
            float tx = sim.X + Lookahead, ty = sim.Course != null ? sim.Course.PathY(tx, Weave) : PathY(tx);  // the corridor's weave is fixed
            float alpha = (float)Math.Atan2(ty - sim.Y, tx - sim.X) - sim.Heading;
            float distance = (float)Math.Sqrt((tx - sim.X) * (tx - sim.X) + (ty - sim.Y) * (ty - sim.Y));
            noise = 0.9f * noise + 0.1f * Gaussian() * Wobble * NoiseScale;
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
            return CorridorWeave * (float)Math.Cos(Math.PI * (x - first) / MockTruckSim.ConeSpacing) * easeIn * easeOut;
        }

        float Gaussian()
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
