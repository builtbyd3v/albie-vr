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
    /// </summary>
    public sealed class ExampleTruckBody : MonoBehaviour, IThingBody
    {
        public float sensorHeight = 0.1f;
        public float beam = MockTruckSim.DefaultBeamRadius;
        /// <summary>What the rays see and the body crashes into (everything by default; the truck itself has no collider).</summary>
        public LayerMask obstacles = ~0;

        public string[] SensorNames => MockTruckSim.SensorNames;
        public string[] ActuatorNames => MockTruckSim.ActuatorNames;
        public string[][] MirrorSwap => MockTruckSim.MirrorSwap;
        public string[] MirrorNegate => MockTruckSim.MirrorNegate;

        public float Speed { get; private set; }
        public float ForwardSpeed => Speed;
        public bool Crashed { get; private set; }
        /// <summary>The last ray distances in metres, for drawing.</summary>
        public readonly float[] RayMetres = new float[MockTruckSim.Rays];

        static readonly Collider[] touching = new Collider[1];
        PhysicsScene Physics => gameObject.scene.GetPhysicsScene();

        public void Sense(float[] sensors)
        {
            var t = transform;
            RayFan.Sense(Physics, t.position + t.up * sensorHeight, t.forward, t.up, MockTruckSim.Rays, MockTruckSim.FovDeg,
                         beam, MockTruckSim.RayRange, obstacles, sensors, 0, RayMetres);
            sensors[MockTruckSim.Rays] = Mathf.Clamp(Speed / MockTruckSim.TopSpeed, -1f, 1f);
            sensors[MockTruckSim.BearingIndex] = CourseBeacon.Bearing(t.position, t.forward, t.up);
        }

        public void Apply(float[] actuators, float dt)
        {
            if (Crashed) { Speed = 0f; return; }
            float steer = actuators[0], throttle = actuators[1];
            Speed = Mathf.Max(-1f, Speed + (throttle * MockTruckSim.MaxAccel - MockTruckSim.Drag * Speed) * dt);
            // Positive steer turns left (C-19 section 2.2), which is a negative turn about up in Unity.
            float yawRate = Speed / MockTruckSim.Wheelbase * Mathf.Tan(steer * MockTruckSim.MaxSteerDeg * Mathf.Deg2Rad);
            var t = transform;
            var rotation = Quaternion.AngleAxis(-yawRate * dt * Mathf.Rad2Deg, t.up) * t.rotation;
            var position = t.position + rotation * Vector3.forward * (Speed * dt);
            t.SetPositionAndRotation(position, rotation);
            if (Physics.OverlapSphere(position + t.up * sensorHeight, MockTruckSim.TruckRadius, touching, obstacles, QueryTriggerInteraction.Ignore) > 0)
            {
                Crashed = true;
                Speed = 0f;
            }
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
