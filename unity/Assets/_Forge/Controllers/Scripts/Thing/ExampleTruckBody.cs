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
        /// <summary>The last ray distances in metres, for drawing.</summary>
        public readonly float[] RayMetres = new float[MockTruckSim.Rays];

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
            if (Crashed) { Speed = 0f; return; }
            float s = Scale;
            float steer = actuators[0], throttle = actuators[1];
            Speed = Mathf.Max(-s, Speed + (throttle * MockTruckSim.MaxAccel * s - MockTruckSim.Drag * Speed) * dt);
            // Positive steer turns left (C-19 section 2.2), which is a negative turn about up in Unity.
            float yawRate = Speed / (MockTruckSim.Wheelbase * s) * Mathf.Tan(steer * MockTruckSim.MaxSteerDeg * Mathf.Deg2Rad);
            var t = transform;
            var rotation = Quaternion.AngleAxis(-yawRate * dt * Mathf.Rad2Deg, t.up) * t.rotation;
            var position = t.position + rotation * Vector3.forward * (Speed * dt);
            t.SetPositionAndRotation(position, rotation);
            if (Physics.OverlapSphere(position + t.up * SenseHeight, CrashRadius, touching, obstacles, QueryTriggerInteraction.Ignore) > 0)
                Crash();
        }

        /// <summary>Stops the truck as crashed (a touch, or a caller's rule such as driving off the table).</summary>
        public void Crash()
        {
            Crashed = true;
            Speed = 0f;
        }

        /// <summary>Puts the truck down at a pose, stopped and not crashed.</summary>
        public void Place(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);
            Speed = 0f;
            Crashed = false;
        }
    }
}
