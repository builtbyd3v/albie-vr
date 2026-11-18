using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// A worked example of <see cref="IThingBody"/> for Person 2: the mock truck's channels and driving model
    /// (<see cref="MockTruckSim"/>: bicycle model, 3 m/s top speed, 30° steering) on a Unity transform, with its ray
    /// fan sensing real colliders (<see cref="RayFan"/>) and a crash when the body touches one. It isn't the real truck:
    /// the Thing runtime drives wheels with forces. What carries over is the shape: sensors in, speed out, actuators
    /// to physics, once per physics step through <see cref="ControllerThing"/>.
    /// The truck drives along its transform's forward; its sensors cast from <see cref="sensorHeight"/> above it.
    ///
    /// A miniature (<see cref="SetScale"/>, a desk course at 1:4 to 1:10) is the same truck shrunk exactly: wheelbase,
    /// acceleration, ray range, beam, body sphere and sensor height all times <see cref="Scale"/>, drag unchanged. So it
    /// drives the scaled course along the scaled path in the same time, and every sensor it writes (distances over the
    /// scaled range, speed over the scaled top speed, a bearing) reads exactly what the full-size truck's would on the
    /// full-size course: laps recorded at either size train one brain that drives at either size.
    ///
    /// A design (<see cref="ApplyDesign"/>, from <see cref="TruckDesign"/>) sets the launch, drag, wheelbase and steering
    /// lock, and adds two limits the original tuning didn't have: past the tyres' grip it understeers and scrubs speed
    /// (<see cref="Sliding"/>), and a truck that can tip before it slides rolls over in a fast tight turn (a crash,
    /// <see cref="RolledOver"/>). The stock design reproduces the original numbers; its grip limit only binds above
    /// 1.6 m/s at full lock, faster than hand laps (0.8 m/s) or the Autopilot (1.5 m/s cap) drive.
    /// </summary>
    public sealed class ExampleTruckBody : MonoBehaviour, IThingBody
    {
        /// <summary>Full-size sensor height (m); a miniature senses from this times <see cref="Scale"/> (see <see cref="SenseHeight"/>).</summary>
        public float sensorHeight = 0.1f;
        public float beam = MockTruckSim.DefaultBeamRadius;
        /// <summary>What the rays see and the body crashes into (everything by default; the truck itself has no collider).</summary>
        public LayerMask obstacles = ~0;

        /// <summary>
        /// On a miniature, the gap kept between the bottom of the ray beam and the surface it drives on (m). The room's
        /// table has a collider (the floor has none), and a beam or body sphere touching it would read as an obstacle or a
        /// crash; the full-size proportions leave only a millimetre or two at 1:10.
        /// </summary>
        public const float SurfaceClearance = 0.008f;

        public string[] SensorNames => MockTruckSim.SensorNames;
        public string[] ActuatorNames => MockTruckSim.ActuatorNames;
        public string[][] MirrorSwap => MockTruckSim.MirrorSwap;
        public string[] MirrorNegate => MockTruckSim.MirrorNegate;

        /// <summary>Real speed, m/s (a miniature's is its scaled speed).</summary>
        public float Speed { get; private set; }
        /// <summary>
        /// For the controller's <c>limits.speed</c>: the speed at full size (<see cref="Speed"/> / <see cref="Scale"/>),
        /// so a controller's speed limit means the same on the floor and on a desk. Equal to <see cref="Speed"/> at 1.
        /// </summary>
        public float ForwardSpeed => Speed / Scale;
        public bool Crashed { get; private set; }
        /// <summary>1 on the floor; 0.1 to 0.25 on a desk course (<see cref="SetScale"/>).</summary>
        public float Scale { get; private set; } = 1f;
        /// <summary>The last steer.angle applied (-1..1, + is left), for the shell's front wheels (<see cref="TruckShell"/>).</summary>
        public float Steer { get; private set; }
        /// <summary>The last drive.throttle applied (-1..1), for the shell's brake lights and squat.</summary>
        public float Throttle { get; private set; }
        /// <summary>The last yaw rate about the truck's up (rad/s, Unity's sign: + turns right), for the shell's lean.</summary>
        public float YawRate { get; private set; }
        /// <summary>When <see cref="Apply"/> last ran (Time.time): the shell treats a truck not stepped lately as parked.</summary>
        public float AppliedAt { get; private set; } = -1f;
        /// <summary>The last ray distances in metres, for drawing.</summary>
        public readonly float[] RayMetres = new float[MockTruckSim.Rays];

        // ---- The driving model's numbers (full size; SetScale scales them on top) --------------------------------
        // Until a design is applied they are exactly the original tuning (MockTruckSim), with no grip or roll-over limit.

        /// <summary>Full-throttle launch acceleration (m/s^2).</summary>
        public float Accel { get; private set; } = MockTruckSim.MaxAccel;
        /// <summary>The speed-proportional drag (1/s): the motor's back-EMF, which is also the coast-down brake.</summary>
        public float LinearDrag { get; private set; } = MockTruckSim.Drag;
        /// <summary>Air drag over mass (1/m): dv/dt gets - QuadDrag v|v|.</summary>
        public float QuadDrag { get; private set; }
        public float Wheelbase { get; private set; } = MockTruckSim.Wheelbase;
        public float MaxSteerDeg { get; private set; } = MockTruckSim.MaxSteerDeg;
        /// <summary>Lateral acceleration where the tyres let go (m/s^2): asked for more, the truck understeers and scrubs speed.</summary>
        public float GripAccel { get; private set; } = float.PositiveInfinity;
        /// <summary>Lateral acceleration where it rolls over (m/s^2); only reachable when it is below <see cref="GripAccel"/>.</summary>
        public float TipAccel { get; private set; } = float.PositiveInfinity;
        /// <summary>The design's derived numbers, when one is applied (<see cref="ApplyDesign"/>).</summary>
        public bool HasDesign { get; private set; }
        public TruckPhysics Design { get; private set; }
        /// <summary>The tyres are past their grip this step (understeering).</summary>
        public bool Sliding { get; private set; }
        /// <summary>It crashed by rolling over in a corner (<see cref="RollSide"/>: +1 onto its right side, -1 its left).</summary>
        public bool RolledOver { get; private set; }
        public int RollSide { get; private set; }
        /// <summary>The lateral acceleration the tyres carried last step, full size (m/s^2).</summary>
        public float LateralAccel { get; private set; }
        /// <summary>How long a corner must stay over the roll-over limit before it goes over (s): a flick of the wheel doesn't.</summary>
        public const float TipHoldSeconds = 0.12f;
        float overTip;

        /// <summary>
        /// Drives with a design's physics (<see cref="TruckDesign.Derive"/>): launch, drag, wheelbase, steering lock, and the
        /// grip and roll-over limits. Live: a truck already on a course keeps its speed and drives on with the new numbers.
        /// <see cref="SetScale"/> still applies on top (a desk miniature is this design shrunk). The sensors are unchanged:
        /// speed.forward stays over the original 3 m/s, so a brain reads speed the same way on any design.
        /// </summary>
        public void ApplyDesign(TruckPhysics p)
        {
            if (!(p.Accel > 0f) || !(p.Wheelbase > 0f)) return;
            Design = p;
            HasDesign = true;
            Accel = p.Accel;
            LinearDrag = p.LinearDrag;
            QuadDrag = p.QuadDrag;
            Wheelbase = p.Wheelbase;
            MaxSteerDeg = p.MaxSteerDeg;
            GripAccel = p.GripAccel > 0f ? p.GripAccel : float.PositiveInfinity;
            TipAccel = p.TipAccel > 0f ? p.TipAccel : float.PositiveInfinity;
        }

        /// <summary>
        /// Where the rays and the body sphere are, above the transform (m): <see cref="sensorHeight"/> at full size; on a
        /// miniature that times <see cref="Scale"/>, lifted so the beam and the sphere clear the table top by
        /// <see cref="SurfaceClearance"/>.
        /// </summary>
        public float SenseHeight => Scale >= 1f ? sensorHeight
            : Mathf.Max(sensorHeight * Scale, Mathf.Max(beam * Scale, CrashRadius) + SurfaceClearance);
        /// <summary>The body sphere's radius for the crash test (m).</summary>
        public float CrashRadius => MockTruckSim.TruckRadius * Scale;

        static readonly Collider[] touching = new Collider[1];
        PhysicsScene Physics => gameObject.scene.GetPhysicsScene();

        /// <summary>
        /// Sizes the truck's driving model and sensors for a course at <paramref name="scale"/> (1 = full size). Its
        /// visual size is the caller's (the transform's scale). Call between runs; the speed is kept in proportion.
        /// </summary>
        public void SetScale(float scale)
        {
            float s = scale >= 0.999f || float.IsNaN(scale) ? 1f : Mathf.Max(0.02f, scale);
            Speed *= s / Scale;
            Scale = s;
        }

        public void Sense(float[] sensors)
        {
            var t = transform;
            float s = Scale;
            RayFan.Sense(Physics, t.position + t.up * SenseHeight, t.forward, t.up, MockTruckSim.Rays, MockTruckSim.FovDeg,
                         beam * s, MockTruckSim.RayRange * s, obstacles, sensors, 0, RayMetres);
            sensors[MockTruckSim.Rays] = Mathf.Clamp(Speed / (MockTruckSim.TopSpeed * s), -1f, 1f);
            sensors[MockTruckSim.BearingIndex] = CourseBeacon.Bearing(t.position, t.forward, t.up);
        }

        public void Apply(float[] actuators, float dt)
        {
            AppliedAt = Time.time;
            if (Crashed) { Speed = 0f; YawRate = 0f; Sliding = false; return; }
            float s = Scale;
            float steer = actuators[0], throttle = actuators[1];
            Steer = steer; Throttle = throttle;
            // dv/dt = throttle A - LinearDrag v - QuadDrag v|v| at full size; a miniature's speed is s times it.
            Speed = Mathf.Max(-s, Speed + (throttle * Accel * s - LinearDrag * Speed - QuadDrag * Speed * Mathf.Abs(Speed) / s) * dt);
            // Positive steer turns left (C-19 section 2.2), which is a negative turn about up in Unity.
            float yawRate = Speed / (Wheelbase * s) * Mathf.Tan(steer * MaxSteerDeg * Mathf.Deg2Rad);
            // The corner's lateral acceleration at full size. Past the tyres' grip it understeers: the turn is held to what
            // the grip carries and the sliding tyres scrub speed. Over the roll-over limit for TipHoldSeconds, it goes over.
            float lateral = Mathf.Abs(Speed * yawRate) / s;
            Sliding = lateral > GripAccel;
            if (Sliding)
            {
                yawRate *= GripAccel / lateral;
                float scrub = Mathf.Min(lateral - GripAccel, GripAccel) * 0.3f * s * dt;
                Speed = Speed > 0f ? Mathf.Max(0f, Speed - scrub) : Mathf.Min(0f, Speed + scrub);
                lateral = GripAccel;
            }
            LateralAccel = lateral;
            if (lateral > TipAccel)
            {
                overTip += dt;
                if (overTip >= TipHoldSeconds)
                {
                    RollOver(yawRate > 0f ? 1 : -1);
                    return;
                }
            }
            else overTip = 0f;
            YawRate = -yawRate;
            var t = transform;
            var rotation = Quaternion.AngleAxis(-yawRate * dt * Mathf.Rad2Deg, t.up) * t.rotation;
            var position = t.position + rotation * Vector3.forward * (Speed * dt);
            t.SetPositionAndRotation(position, rotation);
            if (Physics.OverlapSphere(position + t.up * SenseHeight, CrashRadius, touching, obstacles, QueryTriggerInteraction.Ignore) > 0)
                Crash();
        }

        // Turning left (+ yaw here), it goes over onto its right side, and the other way round.
        void RollOver(int side)
        {
            RolledOver = true;
            RollSide = side;
            Crash();
        }

        /// <summary>Stops the truck as crashed (a touch, or a caller's rule such as driving off the table).</summary>
        public void Crash()
        {
            Crashed = true;
            Speed = 0f;
            YawRate = 0f;
            Sliding = false;
        }

        /// <summary>Puts the truck down at a pose, stopped and not crashed.</summary>
        public void Place(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);
            Speed = 0f;
            Crashed = false;
            Steer = Throttle = YawRate = LateralAccel = overTip = 0f;
            Sliding = RolledOver = false;
            RollSide = 0;
        }
    }
}
