using System;

namespace Forge.Controllers
{
    /// <summary>
    /// The fake Thing (PROJECT_CONTEXT 16.5) until the Thing runtime (C-16) lands: a top-down RC truck on a 5-cone
    /// slalom, ported from hub/ml/make_controller_mocks.py with the same constants, so its sensors match the mock laps.
    /// Bicycle model at true scale (metres, m/s); x runs along the course, y is to the left, heading is CCW from +x.
    /// A small truck: 0.17 m wheelbase and 35° steering turn it in 0.24 m, so it can weave the room slalom (cones 1.0-1.2 m
    /// apart). Until 2026-11-06 it was 0.28 m and 30° (0.48 m), which couldn't pass cones 0.8 m apart at all.
    /// Channels (contract section 2): sensors ray_front.d0-d6 (d0 leftmost, 1 = nothing hit), speed.forward and
    /// course.bearing (the angle to the next gate, then the finish, over 90°: the corridor's own gates, or the course's;
    /// see <see cref="CourseBeacon"/>);
    /// actuators steer.angle (+ is left) and drive.throttle. Plain C#, so headless checks can drive it too.
    /// With <see cref="Course"/> set it drives that course instead (a room slalom), scored as the artifact scores it:
    /// gates in order, then the finish box. With it null, it's the built-in corridor, exactly as before.
    /// </summary>
    public sealed class MockTruckSim
    {
        public const float Wheelbase = 0.17f, MaxSteerDeg = 35f, MaxAccel = 3f, Drag = 1f, TruckRadius = 0.09f;
        public const float TopSpeed = MaxAccel / Drag;   // 3 m/s: the scale for speed.forward
        public const int Rays = 7;
        public const float FovDeg = 120f, RayRange = 2f;
        public const float ConeSpacing = 1.6f, ConeRadius = 0.08f;
        public static readonly float[] ConeX = { 1.5f, 3.1f, 4.7f, 6.3f, 7.9f };
        public const float WallXMin = -0.5f, WallXMax = 9.8f, WallYMin = -1.5f, WallYMax = 1.5f;
        public const float FinishX = 8.9f;
        /// <summary>
        /// The corridor is a course too, so course.bearing means the same everywhere: a gate on the side the scripted
        /// driver passes each cone (left of the first), <see cref="CorridorWeave"/> out, counted in order. A lap still
        /// ends at <see cref="FinishX"/>; the gates only move course.bearing's target on.
        /// </summary>
        public const float CorridorWeave = 0.4f, GateHalfAlong = 0.1f, GateHalfAcross = 0.3f;
        public static readonly float[] CorridorGateY = { CorridorWeave, -CorridorWeave, CorridorWeave, -CorridorWeave, CorridorWeave };

        public static readonly string[] SensorNames =
            { "ray_front.d0", "ray_front.d1", "ray_front.d2", "ray_front.d3", "ray_front.d4", "ray_front.d5", "ray_front.d6", "speed.forward", "course.bearing" };
        /// <summary>Where course.bearing is in <see cref="SensorNames"/>.</summary>
        public const int BearingIndex = Rays + 1;
        public static readonly string[] ActuatorNames = { "steer.angle", "drive.throttle" };
        /// <summary>The left-right mirror map the runtime publishes with a Thing (contract section 2.3).</summary>
        public static readonly string[][] MirrorSwap =
            { new[] { "ray_front.d0", "ray_front.d6" }, new[] { "ray_front.d1", "ray_front.d5" }, new[] { "ray_front.d2", "ray_front.d4" } };
        public static readonly string[] MirrorNegate = { "steer.angle", "course.bearing" };

        public float X, Y, Heading, Speed;
        /// <summary>
        /// Each ray also hits things within this many metres of its line (a sphere cast), so a thin cone can't slip
        /// between two rays: 7 rays over 120° are about 0.5 m apart at 1.6 m, and a cone is 0.16 m wide. 0 is a plain
        /// ray. The default matches the Python mock and the proposed ray sensor (contract section 2.1).
        /// </summary>
        public float BeamRadius = DefaultBeamRadius;
        public const float DefaultBeamRadius = 0.1f;
        public bool Crashed, Finished;
        /// <summary>A room course to drive instead of the built-in corridor (null: the corridor).</summary>
        public MockCourse Course;
        /// <summary>Gates passed in order so far (the course's, or the corridor's).</summary>
        public int GatesPassed;
        /// <summary>Why a run on a <see cref="Course"/> failed without touching anything: <c>missed_gate</c> or <c>missed_finish</c> (null otherwise). <see cref="Crashed"/> is set too, so a run stops.</summary>
        public string Missed;
        /// <summary>A lap ends at the finish line. Off for free driving (the obstacle-ahead data), where only a crash stops it.</summary>
        public bool StopAtFinish = true;
        public float Time;
        /// <summary>The last ray distances in metres, for drawing.</summary>
        public readonly float[] RayDistance = new float[Rays];

        // ---- The driving model: the original tuning unless a design's physics is applied (UsePhysics) ----------------
        // With the defaults every step is exactly the original arithmetic, so the existing checks and laps are unchanged.

        /// <summary>Full-throttle acceleration (m/s^2), linear drag (1/s) and quadratic drag (1/m): dv/dt = throttle A - L v - Q v|v|.</summary>
        public float Accel = MaxAccel, LinearDrag = Drag, QuadDrag;
        /// <summary>Wheelbase (m) and steering lock (degrees) the turn uses.</summary>
        public float WheelbaseM = Wheelbase, SteerLockDeg = MaxSteerDeg;
        /// <summary>Lateral acceleration where the tyres let go, and where it rolls over (m/s^2); infinite: no limit.</summary>
        public float GripAccel = float.PositiveInfinity, TipAccel = float.PositiveInfinity;
        /// <summary>It crashed by rolling over in a corner (only with a design whose roll-over limit is reachable).</summary>
        public bool RolledOver;
        float overTip;

        /// <summary>
        /// Drives with a truck design's physics (<see cref="TruckDesign.Derive"/>), as <see cref="ExampleTruckBody.ApplyDesign"/>
        /// does on the real truck: launch, drag, wheelbase, steering lock, grip (understeer and scrub) and roll-over. So a
        /// rehearsal on this sim is honest about the design the person is driving. The sensors are unchanged (speed over 3 m/s).
        /// </summary>
        public void UsePhysics(TruckPhysics p)
        {
            if (!(p.Accel > 0f) || !(p.Wheelbase > 0f)) return;
            Accel = p.Accel; LinearDrag = p.LinearDrag; QuadDrag = p.QuadDrag;
            WheelbaseM = p.Wheelbase; SteerLockDeg = p.MaxSteerDeg;
            GripAccel = p.GripAccel > 0f ? p.GripAccel : float.PositiveInfinity;
            TipAccel = p.TipAccel > 0f ? p.TipAccel : float.PositiveInfinity;
        }

        /// <summary>The throttle that holds <paramref name="speed"/> (m/s) against this model's drag, clamped to [0, 1].</summary>
        public float ThrottleFor(float speed)
        {
            float v = Math.Max(0f, speed);
            return Math.Max(0f, Math.Min(1f, (LinearDrag * v + QuadDrag * v * v) / Math.Max(1e-4f, Accel)));
        }

        public MockTruckSim() { Reset(); }

        /// <summary>Starts <paramref name="y"/> metres left of the centre line, at <paramref name="heading"/> radians.</summary>
        public MockTruckSim(float y, float heading) { Reset(y, heading); }

        /// <summary>On <paramref name="course"/>, <paramref name="y"/> metres left of the start, at <paramref name="heading"/> radians.</summary>
        public MockTruckSim(MockCourse course, float y = 0f, float heading = 0f) { Course = course; Reset(y, heading); }

        public void Reset(float y = 0f, float heading = 0f)
        {
            X = 0f; Y = y; Heading = heading; Speed = 0f; Time = 0f;
            Crashed = Finished = false;
            GatesPassed = 0; Missed = null;
            RolledOver = false; overTip = 0f;
        }

        /// <summary>Angle of ray <paramref name="i"/> relative to the truck's heading, in radians (d0 is +60°, left).</summary>
        public static float RayAngle(int i) => Deg(FovDeg / 2f - i * FovDeg / (Rays - 1));

        /// <summary>Writes the sensors in <see cref="SensorNames"/> order.</summary>
        public void Sense(float[] sensors)
        {
            // As RayFan does on real colliders: with something already inside the beam, every ray is a plain ray. The
            // small truck can be 0.17-0.18 m from a cone's centre, inside the beam but not touching it.
            float beam = InsideBeam() ? 0f : BeamRadius;
            for (int i = 0; i < Rays; i++)
            {
                float a = Heading + RayAngle(i);
                RayDistance[i] = Cast(X, Y, (float)Math.Cos(a), (float)Math.Sin(a), beam);
                sensors[i] = RayDistance[i] / RayRange;
            }
            sensors[Rays] = Math.Max(-1f, Math.Min(1f, Speed / TopSpeed));
            sensors[BearingIndex] = Bearing();
        }

        /// <summary>course.bearing: toward the next gate in order, then the finish (the corridor's or the course's).</summary>
        public float Bearing()
        {
            var c = Course;
            if (c == null)
            {
                bool gate = GatesPassed < ConeX.Length;
                return BearingValue((float)Math.Atan2((gate ? CorridorGateY[GatesPassed] : 0f) - Y, (gate ? ConeX[GatesPassed] : FinishX) - X) - Heading);
            }
            float tx = GatesPassed < c.Gates ? c.GateX[GatesPassed] : c.FinishX, ty = GatesPassed < c.Gates ? c.GateY[GatesPassed] : c.FinishY;
            return BearingValue((float)Math.Atan2(ty - Y, tx - X) - Heading);
        }

        /// <summary>An angle in radians (positive left, any range) as course.bearing: wrapped to ±π, over 90°, clamped to [-1, 1].</summary>
        public static float BearingValue(float radiansLeft)
        {
            double a = Math.IEEERemainder(radiansLeft, 2 * Math.PI);
            return (float)Math.Max(-1.0, Math.Min(1.0, a / (Math.PI / 2)));
        }

        /// <summary>One physics step from the actuators in <see cref="ActuatorNames"/> order. The truck stops for good
        /// after a crash or at the finish line (a lap ends there, as in the Python mock); Reset starts a new one.</summary>
        public void Step(float[] actuators, float dt)
        {
            if (Crashed || Finished) { Speed = 0f; return; }
            float steer = Clamp(actuators[0]), throttle = Clamp(actuators[1]);
            float accel = throttle * Accel - LinearDrag * Speed - QuadDrag * Speed * Math.Abs(Speed);
            Speed = Math.Max(-1f, Speed + accel * dt);
            float yawRate = Speed / WheelbaseM * (float)Math.Tan(steer * Deg(SteerLockDeg));
            // Past the tyres' grip it understeers and scrubs speed; over the roll-over limit for a moment, it goes over
            // (ExampleTruckBody's rules). Neither binds with the original tuning.
            float lateral = Math.Abs(Speed * yawRate);
            if (lateral > GripAccel)
            {
                yawRate *= GripAccel / lateral;
                float scrub = Math.Min(lateral - GripAccel, GripAccel) * 0.3f * dt;
                Speed = Speed > 0f ? Math.Max(0f, Speed - scrub) : Math.Min(0f, Speed + scrub);
                lateral = GripAccel;
            }
            if (lateral > TipAccel)
            {
                overTip += dt;
                if (overTip >= ExampleTruckBodyTipHoldSeconds) { RolledOver = true; Crashed = true; Speed = 0f; return; }
            }
            else overTip = 0f;
            Heading += yawRate * dt;
            X += Speed * (float)Math.Cos(Heading) * dt;
            Y += Speed * (float)Math.Sin(Heading) * dt;
            Time += dt;
            if (HitsSomething()) { Crashed = true; Speed = 0f; }
            if (Course != null) { Score(); return; }
            if (GatesPassed < ConeX.Length && Math.Abs(X - ConeX[GatesPassed]) <= GateHalfAlong && Math.Abs(Y - CorridorGateY[GatesPassed]) <= GateHalfAcross) GatesPassed++;
            if (StopAtFinish && X > FinishX) Finished = true;
        }

        // The artifact's rules: gates count in order while the centre is in one; the finish box needs every gate.
        void Score()
        {
            if (Crashed) return;
            var c = Course;
            if (GatesPassed < c.Gates && c.InGate(GatesPassed, X, Y)) GatesPassed++;
            if (!StopAtFinish) return;
            if (c.InFinish(X, Y))
            {
                if (GatesPassed >= c.Gates) Finished = true;
                else Fail("missed_gate");
            }
            else if (c.PastFinish(X)) Fail("missed_finish");
        }

        void Fail(string why) { Missed = why; Crashed = true; Speed = 0f; }

        /// <summary>True when the truck's body overlaps a cone or a wall at its current pose.</summary>
        public bool HitsSomething()
        {
            if (Course != null) return Course.Touches(X, Y, TruckRadius);
            for (int i = 0; i < ConeX.Length; i++)
            {
                float dx = X - ConeX[i], dy = Y;
                if (dx * dx + dy * dy < (ConeRadius + TruckRadius) * (ConeRadius + TruckRadius)) return true;
            }
            return !(WallXMin + TruckRadius < X && X < WallXMax - TruckRadius && WallYMin + TruckRadius < Y && Y < WallYMax - TruckRadius);
        }

        // Distance to the first cone or wall along a unit ray, capped at RayRange (same maths as the Python mock). It's a
        // sphere cast for walls too (contract 2.1): the distance the beam's centre travels until the beam touches the
        // wall. Until 2026-11-06 walls got a plain ray, so a sphere-cast fan on real colliders read them up to 0.21 m
        // nearer than the laps the mock brains learned from (ThingRuntimeCheck).
        // RayFan's OverlapSphere at the sensor: a cone or a wall within the beam.
        bool InsideBeam()
        {
            if (BeamRadius <= 0f) return false;
            if (Course != null) return Course.Touches(X, Y, BeamRadius);
            for (int i = 0; i < ConeX.Length; i++)
            {
                float dx = X - ConeX[i], r = ConeRadius + BeamRadius;
                if (dx * dx + Y * Y < r * r) return true;
            }
            return Math.Min(Math.Min(X - WallXMin, WallXMax - X), Math.Min(Y - WallYMin, WallYMax - Y)) < BeamRadius;
        }

        float Cast(float px, float py, float dx, float dy, float beam)
        {
            if (Course != null) return Course.Cast(px, py, dx, dy, beam, RayRange);
            float best = RayRange, r = ConeRadius + beam;
            for (int i = 0; i < ConeX.Length; i++)
            {
                float ox = ConeX[i] - px, oy = -py;
                float along = ox * dx + oy * dy;
                if (along <= 0) continue;
                float miss2 = ox * ox + oy * oy - along * along;
                if (miss2 <= r * r) best = Math.Min(best, Math.Max(0f, along - (float)Math.Sqrt(r * r - miss2)));
            }
            best = Wall(WallXMin, px, dx, best, beam); best = Wall(WallXMax, px, dx, best, beam);
            best = Wall(WallYMin, py, dy, best, beam); best = Wall(WallYMax, py, dy, best, beam);
            return best;
        }

        // The wall is met when the beam's edge reaches it, so its centre stops beam metres short. Already inside the
        // beam (the truck has crashed by then): a plain ray, as RayFan falls back to.
        static float Wall(float wall, float p, float d, float best, float beam)
        {
            if (Math.Abs(d) < 1e-9f) return best;
            float t = (wall - p) / d;
            if (t <= 0) return best;
            float sphere = (wall - Math.Sign(d) * beam - p) / d;
            if (sphere > 0) t = sphere;
            return t < best ? t : best;
        }

        /// <summary><see cref="ExampleTruckBody.TipHoldSeconds"/>, here so the sim stays plain C#.</summary>
        const float ExampleTruckBodyTipHoldSeconds = 0.12f;

        static float Clamp(float v) => v < -1f ? -1f : v > 1f ? 1f : v;
        static float Deg(float degrees) => degrees * (float)Math.PI / 180f;
    }
}
