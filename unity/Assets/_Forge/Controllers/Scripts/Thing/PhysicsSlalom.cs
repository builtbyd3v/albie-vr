using UnityEngine;
using UnityEngine.SceneManagement;

namespace Forge.Controllers
{
    /// <summary>
    /// The mock slalom as Unity colliders, with ML A's example physics truck on it: the cones as capsules, the corridor's
    /// walls as boxes, and a <see cref="ControllerThing"/> on an <see cref="ExampleTruckBody"/> that the caller ticks
    /// (<c>tickInFixedUpdate</c> off). Sim x is world x and sim y (left) is world z, so a pose, a sensor reading and a
    /// course.bearing match <see cref="MockTruckSim"/>'s (ThingRuntimeCheck, "Sensors"). It has no renderers.
    /// Used by ThingRuntimeCheck (Editor) and the Quest timing kit's physics stage (on the headset).
    /// </summary>
    public static class PhysicsSlalom
    {
        public const float WallHeight = 0.5f, WallThickness = 0.2f;

        /// <summary>Builds the course and the truck in <paramref name="scene"/> (truck_manual attached on Init).</summary>
        public static ControllerThing Build(Scene scene)
        {
            foreach (float x in MockTruckSim.ConeX)
            {
                var capsule = NewObject("cone", scene, new Vector3(x, 0f, 0f)).AddComponent<CapsuleCollider>();
                capsule.radius = MockTruckSim.ConeRadius;
                capsule.height = 1f; // tall, so the rays (0.1 m up) hit its straight side
                capsule.center = Vector3.zero;
            }
            float cx = (MockTruckSim.WallXMin + MockTruckSim.WallXMax) / 2f, lx = MockTruckSim.WallXMax - MockTruckSim.WallXMin;
            float cz = (MockTruckSim.WallYMin + MockTruckSim.WallYMax) / 2f, lz = MockTruckSim.WallYMax - MockTruckSim.WallYMin;
            Wall(scene, new Vector3(cx, 0f, MockTruckSim.WallYMin - WallThickness / 2f), new Vector3(lx + 2 * WallThickness, WallHeight, WallThickness));
            Wall(scene, new Vector3(cx, 0f, MockTruckSim.WallYMax + WallThickness / 2f), new Vector3(lx + 2 * WallThickness, WallHeight, WallThickness));
            Wall(scene, new Vector3(MockTruckSim.WallXMin - WallThickness / 2f, 0f, cz), new Vector3(WallThickness, WallHeight, lz));
            Wall(scene, new Vector3(MockTruckSim.WallXMax + WallThickness / 2f, 0f, cz), new Vector3(WallThickness, WallHeight, lz));

            var truck = NewObject("truck", scene, Vector3.zero);
            truck.SetActive(false);  // in Play, so its Awake (Init) and OnEnable (ThingHosts) see the fields set below
            truck.AddComponent<ExampleTruckBody>();
            var thing = truck.AddComponent<ControllerThing>();
            thing.thingName = "truck";
            thing.thingVersion = 2;
            thing.tickInFixedUpdate = false;
            thing.controller = Resources.Load<TextAsset>("Mocks/controllers/truck_manual");
            truck.SetActive(true);
            Physics.SyncTransforms();
            return thing;
        }

        /// <summary>The truck's world rotation for a sim heading (radians, 0 along +x, positive to the left).</summary>
        public static Quaternion Heading(float radians) =>
            Quaternion.LookRotation(new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians)), Vector3.up);

        /// <summary>
        /// What a run engine does for course.bearing (<see cref="CourseBeacon"/>): counts the corridor's next gate once
        /// the truck at <paramref name="position"/> is in it (<see cref="MockTruckSim.CorridorGateY"/>), then points the
        /// beacon at the next gate in order, or the finish line's centre. Call it before each tick.
        /// </summary>
        public static void Beacon(Vector3 position, ref int gates)
        {
            int n = MockTruckSim.ConeX.Length;
            if (gates < n && Mathf.Abs(position.x - MockTruckSim.ConeX[gates]) <= MockTruckSim.GateHalfAlong &&
                Mathf.Abs(position.z - MockTruckSim.CorridorGateY[gates]) <= MockTruckSim.GateHalfAcross) gates++;
            CourseBeacon.Set(gates < n ? new Vector3(MockTruckSim.ConeX[gates], 0f, MockTruckSim.CorridorGateY[gates]) : new Vector3(MockTruckSim.FinishX, 0f, 0f));
        }

        public static bool Finished(Vector3 position) => position.x > MockTruckSim.FinishX;

        static void Wall(Scene scene, Vector3 centre, Vector3 size) =>
            NewObject("wall", scene, centre).AddComponent<BoxCollider>().size = size;

        static GameObject NewObject(string name, Scene scene, Vector3 position)
        {
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, scene);
            go.transform.position = position;
            return go;
        }
    }
}
