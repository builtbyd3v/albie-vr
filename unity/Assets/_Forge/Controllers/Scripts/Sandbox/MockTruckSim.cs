using System;

namespace Forge.Controllers
{
    /// <summary>
    /// The fake Thing (PROJECT_CONTEXT 16.5) until the Thing runtime (C-16) lands: a top-down RC truck on a 5-cone
    /// slalom, ported from hub/ml/make_controller_mocks.py with the same constants, so its sensors match the mock laps.
    /// Bicycle model at true scale (metres, m/s); x runs along the course, y is to the left, heading is CCW from +x.
    /// Channels (contract section 2): sensors ray_front.d0-d6 (d0 leftmost, 1 = nothing hit) and speed.forward;
    /// actuators steer.angle (+ is left) and drive.throttle. Plain C#, so headless checks can drive it too.
    /// </summary>
    public sealed class MockTruckSim
    {
        public const float Wheelbase = 0.28f, MaxSteerDeg = 30f, MaxAccel = 3f, Drag = 1f, TruckRadius = 0.12f;
        public const float TopSpeed = MaxAccel / Drag;   // 3 m/s: the scale for speed.forward
        public const int Rays = 7;
        public const float FovDeg = 120f, RayRange = 2f;
        public const float ConeSpacing = 1.6f, ConeRadius = 0.08f;
        public static readonly float[] ConeX = { 1.5f, 3.1f, 4.7f, 6.3f, 7.9f };
        public const float WallXMin = -0.5f, WallXMax = 9.8f, WallYMin = -1.5f, WallYMax = 1.5f;
        public const float FinishX = 8.9f;

        public static readonly string[] SensorNames =
            { "ray_front.d0", "ray_front.d1", "ray_front.d2", "ray_front.d3", "ray_front.d4", "ray_front.d5", "ray_front.d6", "speed.forward" };
        public static readonly string[] ActuatorNames = { "steer.angle", "drive.throttle" };
        /// <summary>The left-right mirror map the runtime publishes with a Thing (contract section 2.3).</summary>
        public static readonly string[][] MirrorSwap =
            { new[] { "ray_front.d0", "ray_front.d6" }, new[] { "ray_front.d1", "ray_front.d5" }, new[] { "ray_front.d2", "ray_front.d4" } };
        public static readonly string[] MirrorNegate = { "steer.angle" };

        public float X, Y, Heading, Speed;
        /// <summary>
        /// Each ray also hits things within this many metres of its line (a sphere cast), so a thin cone can't slip
        /// between two rays: 7 rays over 120° are about 0.5 m apart at 1.6 m, and a cone is 0.16 m wide. 0 is a plain
        /// ray. The default matches the Python mock and the proposed ray sensor (contract section 2.1).
        /// </summary>
        public float BeamRadius = DefaultBeamRadius;
        public const float DefaultBeamRadius = 0.1f;
        public bool Crashed, Finished;
        /// <summary>A lap ends at the finish line. Off for free driving (the obstacle-ahead data), where only a crash stops it.</summary>
        public bool StopAtFinish = true;
        public float Time;
        /// <summary>The last ray distances in metres, for drawing.</summary>
        public readonly float[] RayDistance = new float[Rays];

        public MockTruckSim() { Reset(); }

        /// <summary>Starts <paramref name="y"/> metres left of the centre line, at <paramref name="heading"/> radians.</summary>
        public MockTruckSim(float y, float heading) { Reset(y, heading); }

        public void Reset(float y = 0f, float heading = 0f)
        {
            X = 0f; Y = y; Heading = heading; Speed = 0f; Time = 0f;
            Crashed = Finished = false;
        }

        /// <summary>Angle of ray <paramref name="i"/> relative to the truck's heading, in radians (d0 is +60°, left).</summary>
        public static float RayAngle(int i) => Deg(FovDeg / 2f - i * FovDeg / (Rays - 1));

        /// <summary>Writes the sensors in <see cref="SensorNames"/> order.</summary>
        public void Sense(float[] sensors)
        {
            for (int i = 0; i < Rays; i++)
            {
                float a = Heading + RayAngle(i);
                RayDistance[i] = Cast(X, Y, (float)Math.Cos(a), (float)Math.Sin(a));
                sensors[i] = RayDistance[i] / RayRange;
            }
            sensors[Rays] = Math.Max(-1f, Math.Min(1f, Speed / TopSpeed));
        }

        /// <summary>One physics step from the actuators in <see cref="ActuatorNames"/> order. The truck stops for good
        /// after a crash or at the finish line (a lap ends there, as in the Python mock); Reset starts a new one.</summary>
        public void Step(float[] actuators, float dt)
        {
            if (Crashed || Finished) { Speed = 0f; return; }
            float steer = Clamp(actuators[0]), throttle = Clamp(actuators[1]);
            float accel = throttle * MaxAccel - Drag * Speed;
            Speed = Math.Max(-1f, Speed + accel * dt);
            Heading += Speed / Wheelbase * (float)Math.Tan(steer * Deg(MaxSteerDeg)) * dt;
            X += Speed * (float)Math.Cos(Heading) * dt;
            Y += Speed * (float)Math.Sin(Heading) * dt;
            Time += dt;
            if (HitsSomething()) { Crashed = true; Speed = 0f; }
            if (StopAtFinish && X > FinishX) Finished = true;
        }

        /// <summary>True when the truck's body overlaps a cone or a wall at its current pose.</summary>
        public bool HitsSomething()
        {
            for (int i = 0; i < ConeX.Length; i++)
            {
                float dx = X - ConeX[i], dy = Y;
                if (dx * dx + dy * dy < (ConeRadius + TruckRadius) * (ConeRadius + TruckRadius)) return true;
            }
            return !(WallXMin + TruckRadius < X && X < WallXMax - TruckRadius && WallYMin + TruckRadius < Y && Y < WallYMax - TruckRadius);
        }

        // Distance to the first cone or wall along a unit ray, capped at RayRange (same maths as the Python mock).
        float Cast(float px, float py, float dx, float dy)
        {
            float best = RayRange, r = ConeRadius + BeamRadius;
            for (int i = 0; i < ConeX.Length; i++)
            {
                float ox = ConeX[i] - px, oy = -py;
                float along = ox * dx + oy * dy;
                if (along <= 0) continue;
                float miss2 = ox * ox + oy * oy - along * along;
                if (miss2 <= r * r) best = Math.Min(best, Math.Max(0f, along - (float)Math.Sqrt(r * r - miss2)));
            }
            best = Wall(WallXMin, px, dx, best); best = Wall(WallXMax, px, dx, best);
            best = Wall(WallYMin, py, dy, best); best = Wall(WallYMax, py, dy, best);
            return best;
        }

        static float Wall(float wall, float p, float d, float best)
        {
            if (Math.Abs(d) < 1e-9f) return best;
            float t = (wall - p) / d;
            return t > 0 && t < best ? t : best;
        }

        static float Clamp(float v) => v < -1f ? -1f : v > 1f ? 1f : v;
        static float Deg(float degrees) => degrees * (float)Math.PI / 180f;
    }
}
