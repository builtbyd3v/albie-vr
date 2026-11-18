using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Forge.Controllers
{
    /// <summary>
    /// The drone's course brain, the parts with no Unity types (so a standalone check can run them): the features the
    /// learned pilot sees, the frame turns, a kinematic copy of <c>DroneBody</c>'s flight model, the rehearsal that tries
    /// each trained candidate on the recorded course line before it flies for real, and the non-learned "follow the
    /// course line" pilot.
    ///
    /// The frame. The drone flies headless: a stick's forward is away from the pilot, whichever way the nose points, so
    /// a recorded command is in the pilot's frame and the pilot turns their head. Everything the net sees and says is in
    /// the course's own frame instead (x right, y up, z forward from the start), so it learns the same thing however the
    /// pilot stood: a recorded (right, forward) is turned from the pilot's heading into the course's, and the net's
    /// output is turned back into the pilot's heading of the moment when it flies.
    ///
    /// The features (all in [-1, 1]): the next target's direction over the floor (<c>target.right</c>,
    /// <c>target.forward</c>: a unit vector while it is over <see cref="Near"/> away, shrinking to 0 on top of it), its
    /// distance (<c>target.dist</c>, over <see cref="Far"/>), its height difference (<c>target.up</c>, over
    /// <see cref="UpScale"/>), and the drone's velocity in the course frame over its top speeds. The target is the run
    /// engine's (<c>CourseBeacon</c>): the next zone of an ordered set (hoop, gate, pad), then the finish, for any course.
    /// </summary>
    public static class DroneCourseMath
    {
        public static readonly string[] FeatureNames =
            { "target.right", "target.forward", "target.dist", "target.up", "speed.right", "speed.forward", "speed.climb" };
        /// <summary>The drone's actuators, in <c>DroneBody.Actuators</c> order.</summary>
        public static readonly string[] ActuatorNames = { "fly.forward", "fly.right", "fly.climb", "fly.yaw" };
        public const int Features = 7, Actuators = 4;
        public const float Near = 0.5f, Far = 3f, UpScale = 0.5f;

        /// <summary>Turns (x, z) by <paramref name="degrees"/> about up, as Unity's <c>Quaternion.Euler(0, degrees, 0) * v</c>.</summary>
        public static void Turn(float degrees, ref float x, ref float z)
        {
            double a = degrees * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);
            float nx = (float)(x * c + z * s), nz = (float)(-x * s + z * c);
            x = nx; z = nz;
        }

        static float Clamp(float v) => v < -1f ? -1f : v > 1f ? 1f : v;

        /// <summary>
        /// The features, from the target minus the drone (<paramref name="dx"/>, <paramref name="dy"/>, <paramref name="dz"/>)
        /// and its velocity, both already in the course frame. No target: the four target features are 0.
        /// </summary>
        public static void Fill(float[] f, bool hasTarget, float dx, float dy, float dz, float vx, float vy, float vz, float maxSpeed, float climbSpeed)
        {
            if (hasTarget)
            {
                float h = (float)Math.Sqrt(dx * dx + dz * dz), d = Math.Max(h, Near);
                f[0] = Clamp(dx / d); f[1] = Clamp(dz / d); f[2] = Math.Min(h, Far) / Far; f[3] = Clamp(dy / UpScale);
            }
            else f[0] = f[1] = f[2] = f[3] = 0f;
            f[4] = Clamp(vx / Math.Max(0.05f, maxSpeed));
            f[5] = Clamp(vz / Math.Max(0.05f, maxSpeed));
            f[6] = Clamp(vy / Math.Max(0.05f, climbSpeed));
        }

        /// <summary>
        /// "Follow the course line": not learned, not trained on anything. Flies straight at the next target at
        /// <paramref name="pace"/> of top speed, slowing on top of it, and matches its height. Commands in the course frame.
        /// </summary>
        public static void FollowLine(float[] f, float[] cmd, float pace = 0.55f)
        {
            cmd[0] = Clamp(f[1] * pace - 0.35f * (f[5] - f[1] * pace));
            cmd[1] = Clamp(f[0] * pace - 0.35f * (f[4] - f[0] * pace));
            cmd[2] = Clamp(f[3] * 1.2f);
            cmd[3] = 0f;
        }
    }

    /// <summary>What turns features into commands (course frame): a trained net, or the course-line follower.</summary>
    public interface IDronePolicy
    {
        string Name { get; }
        void Act(float[] features, float[] commands);
    }

    /// <summary>The course-line follower as a policy (explicitly not learned).</summary>
    public sealed class FollowLinePolicy : IDronePolicy
    {
        public string Name => "drone_follow_line";
        public void Act(float[] features, float[] commands) => DroneCourseMath.FollowLine(features, commands);
    }

    /// <summary>
    /// A trained <c>albie.controller/1</c> learned file run as a policy: the same <see cref="LearnedController"/> the
    /// truck's Autopilot runs, bound to the feature channels rather than the body's own sensors.
    /// </summary>
    public sealed class LearnedPolicy : IDronePolicy
    {
        public readonly ControllerFile File;
        readonly LearnedController net;
        readonly ControllerIO io = new ControllerIO(DroneCourseMath.FeatureNames, DroneCourseMath.ActuatorNames);

        LearnedPolicy(ControllerFile file) { File = file; net = new LearnedController(file); }

        public string Name => File.Name;

        /// <summary>The policy for a trained file, or null with <paramref name="error"/>.</summary>
        public static LearnedPolicy From(string json, out string error)
        {
            var file = ControllerFile.Parse(json, out error);
            if (file == null) return null;
            var policy = new LearnedPolicy(file);
            error = policy.net.Bind(policy.io);
            return error == null ? policy : null;
        }

        public void Act(float[] features, float[] commands)
        {
            Array.Copy(features, io.Sensors, DroneCourseMath.Features);
            Array.Clear(io.Actuators, 0, io.Actuators.Length);
            net.Tick(io, 0f);
            Array.Copy(io.Actuators, commands, DroneCourseMath.Actuators);
        }
    }

    /// <summary>
    /// A kinematic copy of <c>DroneBody</c>'s flight (Fly, with no colliders): speed eased towards what the sticks ask
    /// (accel, or brake when they are let go), the climb stick moving the height it holds, and height pulled back to it.
    /// Axes are the course frame; the drone is headless, so yaw does not change where it goes.
    /// </summary>
    public sealed class DroneFlightSim
    {
        public float MaxSpeed = 1.2f, Accel = 2.5f, Brake = 3.5f, ClimbSpeed = 0.6f, AltitudeGain = 3f, Low = 0.2f, High = 1.9f;
        public float X, Y, Z, Vx, Vy, Vz, HoldY;

        public void Reset(float x, float y, float z) { X = x; Y = y; Z = z; Vx = Vy = Vz = 0f; HoldY = y; }

        public void Step(float forward, float right, float climb, float dt)
        {
            float push = (float)Math.Sqrt(forward * forward + right * right);
            if (push > 1f) { forward /= push; right /= push; }
            float wx = right * MaxSpeed, wz = forward * MaxSpeed;
            float rate = (wx * wx + wz * wz < 1e-4f ? Brake : Accel) * dt;
            float ex = wx - Vx, ez = wz - Vz, e = (float)Math.Sqrt(ex * ex + ez * ez);
            if (e <= rate || e < 1e-6f) { Vx = wx; Vz = wz; }
            else { Vx += ex / e * rate; Vz += ez / e * rate; }
            HoldY = Math.Min(High, Math.Max(Low, HoldY + Math.Max(-1f, Math.Min(1f, climb)) * ClimbSpeed * dt));
            Vy = Math.Max(-ClimbSpeed, Math.Min(ClimbSpeed, (HoldY - Y) * AltitudeGain));
            X += Vx * dt; Y += Vy * dt; Z += Vz * dt;
        }
    }

    /// <summary>
    /// Before the drone's Autopilot flies, each trained candidate flies the recorded course line in
    /// <see cref="DroneFlightSim"/> from <see cref="Starts"/> starts (on the start, and 10 cm off it four ways). A start
    /// finishes when the candidate reaches every target in order (within <see cref="Reach"/>) before the time is up.
    /// Candidates then fly best-first, and one that finishes fewer than <see cref="MinFinishes"/> is not flown at all:
    /// the loop says so instead. Time-sliced (<see cref="Step"/>) so the headset holds its frame rate.
    /// It knows nothing of the room's walls or a hoop's frame, so it can only rule a candidate out, never vouch for it.
    /// </summary>
    public sealed class DroneRehearsal
    {
        public const float Dt = 0.02f, Reach = 0.3f;
        public const int MinFinishes = 3;
        static readonly float[] OffX = { 0f, 0.1f, -0.1f, 0f, 0f }, OffZ = { 0f, 0f, 0f, 0.1f, -0.1f };
        public static int Starts => OffX.Length;

        readonly IDronePolicy[] policies;
        readonly List<float[]> targets;   // course frame, relative to the start on the floor
        readonly DroneFlightSim sim;
        readonly float startHeight, timeoutS;
        readonly int[] finished;
        readonly float[] seconds;
        readonly float[] features = new float[DroneCourseMath.Features], commands = new float[DroneCourseMath.Actuators];
        readonly Stopwatch clock = new Stopwatch();
        int candidate, start, next;
        float t;
        bool flying;

        public int[] Finished => finished;
        /// <summary>Mean seconds of the finished starts per candidate (NaN when none finished).</summary>
        public float[] Seconds => seconds;
        public bool Done => candidate >= policies.Length;
        public double ComputeSeconds => clock.Elapsed.TotalSeconds;

        public DroneRehearsal(IDronePolicy[] policies, List<float[]> targets, DroneFlightSim sim, float startHeight, float timeoutS)
        {
            this.policies = policies ?? throw new ArgumentNullException(nameof(policies));
            this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
            this.sim = sim ?? new DroneFlightSim();
            this.startHeight = startHeight;
            this.timeoutS = timeoutS;
            finished = new int[policies.Length];
            seconds = new float[policies.Length];
            if (targets.Count == 0) candidate = policies.Length;
        }

        public bool Step(double budgetMs)
        {
            if (Done) return true;
            long until = clock.ElapsedTicks + (long)(budgetMs * Stopwatch.Frequency / 1000.0);
            clock.Start();
            while (!Done && clock.ElapsedTicks < until)
                for (int i = 0; i < 50 && !Done; i++) Tick();
            clock.Stop();
            return Done;
        }

        /// <summary>Runs to the end in one call (checks).</summary>
        public void RunToEnd() { while (!Step(double.MaxValue)) { } }

        void Tick()
        {
            if (!flying)
            {
                sim.Reset(OffX[start], startHeight, OffZ[start]);
                next = 0; t = 0f; flying = true;
            }
            var target = targets[next];
            float dx = target[0] - sim.X, dy = target[1] - sim.Y, dz = target[2] - sim.Z;
            if (dx * dx + dy * dy + dz * dz <= Reach * Reach)
            {
                next++;
                if (next >= targets.Count) { finished[candidate]++; seconds[candidate] += t; EndStart(); return; }
                target = targets[next];
                dx = target[0] - sim.X; dy = target[1] - sim.Y; dz = target[2] - sim.Z;
            }
            DroneCourseMath.Fill(features, true, dx, dy, dz, sim.Vx, sim.Vy, sim.Vz, sim.MaxSpeed, sim.ClimbSpeed);
            policies[candidate].Act(features, commands);
            sim.Step(commands[0], commands[1], commands[2], Dt);
            t += Dt;
            if (t >= timeoutS) EndStart();
        }

        void EndStart()
        {
            flying = false;
            start++;
            if (start < Starts) return;
            seconds[candidate] = finished[candidate] > 0 ? seconds[candidate] / finished[candidate] : float.NaN;
            start = 0;
            candidate++;
        }

        /// <summary>Candidate indexes best first: most finishes, then fastest.</summary>
        public int[] Order()
        {
            var order = new int[policies.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                if (finished[a] != finished[b]) return finished[b].CompareTo(finished[a]);
                float sa = float.IsNaN(seconds[a]) ? float.MaxValue : seconds[a], sb = float.IsNaN(seconds[b]) ? float.MaxValue : seconds[b];
                return sa.CompareTo(sb);
            });
            return order;
        }
    }
}
