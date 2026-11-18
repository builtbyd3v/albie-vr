using System.Collections.Generic;
using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// The brain truck's look, built in code as <see cref="DroneShell"/> is: a 1:10-style short-course RC truck about
    /// 0.25 m long, 0.18 m wide and 0.13 m to the roof (an antenna to 0.19 m), with no colliders, so no ray and no crash
    /// sphere of <see cref="ExampleTruckBody"/> sees the truck itself. Local frame: +Z is the nose, +Y is up, the origin
    /// is the ground under the middle of the wheelbase (<see cref="MockTruckSim.Wheelbase"/>), as the body's own pose.
    ///
    /// Parts: an unsprung chassis (plate, bumpers, axles, shock towers) and four lugged tyres with spoked rims on spin
    /// pivots, the front pair on steer pivots; a sprung body (tub, fenders, hood, cab, bed, windows, light bar, antenna,
    /// headlights) that leans out of turns and squats or dives with the throttle on an underdamped spring; tail lights
    /// that brighten when braking, reversing or crashed; and a soft contact shadow so it reads on passthrough.
    /// Static parts are merged into shared meshes (one renderer per group, one submesh per material): about 15 draw
    /// calls and 8 shared materials. The whole truck scales with its transform, so a desk course's 1:N
    /// (<see cref="ExampleTruckBody.SetScale"/>, applied as the transform's scale by the caller) shrinks the look with
    /// the driving model. <see cref="LateUpdate"/> only writes transforms and, on change, one material: no allocations.
    ///
    /// The look follows the design (<see cref="SetDesign"/>, a <see cref="TruckDesign"/>): the wheels sit at its wheelbase
    /// and track and are its size, the chassis is rebuilt at its ride height (lifted when big wheels need it), and the body
    /// stretches over the wheels and rides on the chassis. The stock design is exactly the shape above. A rolled-over truck
    /// (<see cref="ExampleTruckBody.RolledOver"/>) falls onto its side about its outer wheels.
    /// </summary>
    public sealed class TruckShell : MonoBehaviour
    {
        public const float WheelRadius = 0.034f, WheelWidth = 0.032f, Track = 0.144f;
        const float SprungY = 0.045f;  // the body's pivot: about the axles' height, so it leans around the chassis

        ExampleTruckBody body;
        Transform root, sprung, steerLeft, steerRight, shadow;
        readonly Transform[] spinners = new Transform[4];
        readonly Transform[] mounts = new Transform[4];
        readonly Transform[] wheels = new Transform[4];
        MeshFilter chassisFilter, sprungFilter;
        Mesh ownChassis;
        // The design's geometry (m): stock until SetDesign.
        float wheelbase = MockTruckSim.Wheelbase, track = Track, wheelRadius = WheelRadius, chassisLift, bodyLift;
        float rollOver;
        Renderer tail;
        Material tailDim, tailBright;
        bool tailOn, hasLast, wasCrashed;
        Vector3 lastPos, lastForward;
        float spin, steer, roll, rollVel, pitch, pitchVel, speedSmooth;

        static Mesh chassisMesh, sprungMesh, wheelMesh, tailMesh, shadowMesh;

        /// <summary>
        /// Builds the shell under <paramref name="parent"/> (the truck's transform, which carries the
        /// <see cref="ExampleTruckBody"/>) and returns its animator. A null material leaves the default one.
        /// <paramref name="paint"/> is the body colour, <paramref name="accent"/> the rims, hood stripe and antenna tip,
        /// <paramref name="dark"/> the chassis and glass, <paramref name="tyre"/> the rubber; <paramref name="head"/>,
        /// <paramref name="tailDim"/> and <paramref name="tailBright"/> are self-lit; <paramref name="shadowMaterial"/> is
        /// transparent and vertex-coloured (the shadow's mesh carries its fade).
        /// </summary>
        public static TruckShell Build(Transform parent, Material paint, Material accent, Material dark, Material tyre,
                                       Material head, Material tailDim, Material tailBright, Material shadowMaterial)
        {
            EnsureMeshes();
            var go = new GameObject("Shell");
            go.transform.SetParent(parent, false);
            var shell = go.AddComponent<TruckShell>();
            shell.root = parent;
            shell.tailDim = tailDim;
            shell.tailBright = tailBright;

            shell.shadow = Renderer(go.transform, "Contact shadow", shadowMesh, shadowMaterial).transform;
            shell.shadow.localPosition = new Vector3(0f, 0.002f, 0f);
            shell.chassisFilter = Renderer(go.transform, "Chassis", chassisMesh, dark).GetComponent<MeshFilter>();

            for (int i = 0; i < 4; i++)
            {
                // 0 front left, 1 front right, 2 rear left, 3 rear right.
                bool front = i < 2, left = i % 2 == 0;
                var mount = new GameObject(front ? (left ? "Steer FL" : "Steer FR") : (left ? "Axle RL" : "Axle RR")).transform;
                mount.SetParent(go.transform, false);
                mount.localPosition = new Vector3(left ? -Track * 0.5f : Track * 0.5f, WheelRadius, front ? MockTruckSim.Wheelbase * 0.5f : -MockTruckSim.Wheelbase * 0.5f);
                var spinner = new GameObject("Spin").transform;
                spinner.SetParent(mount, false);
                // The wheel mesh's rim faces +x: a left wheel is turned round so its rim faces out too.
                var wheel = Renderer(spinner, "Wheel", wheelMesh, tyre, accent);
                if (left) wheel.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                shell.spinners[i] = spinner;
                shell.mounts[i] = mount;
                shell.wheels[i] = wheel.transform;
                if (front && left) shell.steerLeft = mount;
                else if (front) shell.steerRight = mount;
            }

            shell.sprung = new GameObject("Sprung").transform;
            shell.sprung.SetParent(go.transform, false);
            shell.sprung.localPosition = new Vector3(0f, SprungY, 0f);
            shell.sprungFilter = Renderer(shell.sprung, "Body", sprungMesh, paint, dark, accent, head).GetComponent<MeshFilter>();
            shell.tail = Renderer(shell.sprung, "Tail lights", tailMesh, tailDim);
            return shell;
        }

        static MeshRenderer Renderer(Transform parent, string name, Mesh mesh, params Material[] materials)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>();
            bool any = false;
            foreach (var m in materials) any |= m != null;
            if (any)
            {
                var use = new Material[materials.Length];
                var fallback = r.sharedMaterial;
                for (int i = 0; i < materials.Length; i++) use[i] = materials[i] != null ? materials[i] : fallback;
                r.sharedMaterials = use;
            }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return r;
        }

        /// <summary>
        /// Reshapes the truck to <paramref name="design"/>: wheel positions and size, the chassis at its ride height, the body
        /// stretched over the wheels. Only when the geometry changed (a chassis mesh is rebuilt then; never per frame).
        /// </summary>
        public void SetDesign(TruckDesign design)
        {
            if (design == null) return;
            float wb = design.WheelbaseM, tr = design.TrackM, wr = design.WheelRadiusM;
            float lift = design.ChassisHeightM - StockChassisHeight;
            if (Mathf.Approximately(wb, wheelbase) && Mathf.Approximately(tr, track) && Mathf.Approximately(wr, wheelRadius)
                && Mathf.Approximately(lift, chassisLift) && (chassisFilter == null || chassisFilter.sharedMesh != null)) return;
            wheelbase = wb; track = tr; wheelRadius = wr; chassisLift = lift;
            // The body rides on the chassis, and clears the tyres' tops (the fenders sit 11 mm over the stock tyre).
            bodyLift = Mathf.Max(lift, (wr - WheelRadius) * 1.6f);
            for (int i = 0; i < 4; i++)
            {
                bool front = i < 2, left = i % 2 == 0;
                if (mounts[i] != null) mounts[i].localPosition = new Vector3(left ? -tr * 0.5f : tr * 0.5f, wr, front ? wb * 0.5f : -wb * 0.5f);
                if (wheels[i] != null) wheels[i].localScale = Vector3.one * (wr / WheelRadius);
            }
            float kx = tr / Track, kz = wb / MockTruckSim.Wheelbase;
            if (sprung != null) sprung.localScale = new Vector3(kx, 1f, kz);
            if (shadow != null) shadow.localScale = new Vector3(kx, 1f, kz);
            if (chassisFilter != null)
            {
                bool stock = Mathf.Approximately(wb, MockTruckSim.Wheelbase) && Mathf.Approximately(tr, Track)
                             && Mathf.Approximately(wr, WheelRadius) && Mathf.Approximately(lift, 0f);
                if (ownChassis != null) { Destroy(ownChassis); ownChassis = null; }
                if (stock) chassisFilter.sharedMesh = chassisMesh;
                else chassisFilter.sharedMesh = ownChassis = BuildChassis(wb, tr, wr, lift);
            }
        }

        void OnDestroy()
        {
            if (ownChassis != null) Destroy(ownChassis);
        }

        void Start()
        {
            if (root == null) root = transform.parent != null ? transform.parent : transform;
            body = root.GetComponent<ExampleTruckBody>();
            if (body == null) body = GetComponentInParent<ExampleTruckBody>();
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f || root == null) return;
            float s = Mathf.Max(1e-4f, root.lossyScale.y);
            Vector3 p = root.position, f = root.forward;
            if (!hasLast) { lastPos = p; lastForward = f; hasLast = true; }

            // Wheels roll by the distance actually covered along the nose (robust to physics steps vs frames; a
            // teleport, such as being put back at the start, doesn't spin them).
            Vector3 moved = p - lastPos;
            bool teleport = moved.sqrMagnitude > 0.25f * s * s;
            float along = teleport ? 0f : Vector3.Dot(moved, f);
            spin = Mathf.Repeat(spin + along / (wheelRadius * s) * Mathf.Rad2Deg, 360f);
            var spinRotation = Quaternion.Euler(spin, 0f, 0f);
            for (int i = 0; i < 4; i++) if (spinners[i] != null) spinners[i].localRotation = spinRotation;

            // The body's own state when it's being stepped; otherwise parked (its Speed is kept between runs).
            bool live = body != null && Time.time - body.AppliedAt < 0.15f;
            float speed = live ? body.Speed / s : 0f;  // full-size m/s
            float yawRate = live ? body.YawRate : (teleport ? 0f : Vector3.SignedAngle(lastForward, f, root.up) * Mathf.Deg2Rad / dt);
            if (body == null) speed = teleport ? 0f : along / s / dt;
            speedSmooth = Mathf.Lerp(speedSmooth, speed, 1f - Mathf.Exp(-dt * 10f));

            // Front wheels follow steer.angle (+ is left, a negative yaw in Unity), eased like a servo.
            float steerTarget = body != null ? body.Steer : 0f;
            steer = Mathf.MoveTowards(steer, steerTarget, dt * 8f);
            var steerRotation = Quaternion.Euler(0f, -steer * (body != null ? body.MaxSteerDeg : MockTruckSim.MaxSteerDeg), 0f);
            if (steerLeft != null) steerLeft.localRotation = steerRotation;
            if (steerRight != null) steerRight.localRotation = steerRotation;

            // Suspension: lean out of the turn (lateral accel v * yaw rate), squat on throttle, dive on braking; an
            // underdamped spring gives a little bob. Full-size accelerations, so a miniature leans the same.
            float lateral = speedSmooth * yawRate;
            float longitudinal = live && !body.Crashed ? body.Throttle * body.Accel - body.LinearDrag * speed : 0f;
            float rollTarget = Mathf.Clamp(lateral * 2.5f, -7f, 7f);
            float pitchTarget = Mathf.Clamp(-longitudinal * 1.2f, -4f, 4f);
            bool crashed = body != null && body.Crashed;
            if (crashed && !wasCrashed) pitchVel += 90f;  // the nose dips at the sudden stop
            wasCrashed = crashed;
            float h = Mathf.Min(dt, 0.05f);
            const float k = 180f, c = 14f;
            rollVel += (k * (rollTarget - roll) - c * rollVel) * h;
            roll += rollVel * h;
            pitchVel += (k * (pitchTarget - pitch) - c * pitchVel) * h;
            pitch += pitchVel * h;
            if (sprung != null)
            {
                sprung.localRotation = Quaternion.Euler(pitch, 0f, roll);
                sprung.localPosition = new Vector3(0f, SprungY + bodyLift - Mathf.Abs(pitch) * 0.0003f, 0f);
            }

            // Rolled over: the whole truck falls onto its side about the outer tyres' edge, and back up when it's placed.
            float rollTarget2 = body != null && body.RolledOver ? 90f * body.RollSide : 0f;
            float rolled = Mathf.MoveTowards(rollOver, rollTarget2, dt * (rollTarget2 != 0f ? 360f : 100000f));
            if (rolled != rollOver)
            {
                rollOver = rolled;
                float side = rollOver >= 0f ? 1f : -1f;
                var pivot = new Vector3(side * (track * 0.5f + WheelWidth * 0.5f * wheelRadius / WheelRadius), 0f, 0f);
                var rot = Quaternion.Euler(0f, 0f, -rollOver);
                transform.localRotation = rot;
                transform.localPosition = pivot - rot * pivot;
            }

            // Tail lights: bright when braking (throttle against the motion), reversing, or crashed.
            float raw = body != null ? body.Speed / s : speed;
            bool braking = live && (raw * body.Throttle < -0.01f && Mathf.Abs(raw) > 0.05f || raw < -0.02f);
            bool on = braking || crashed;
            if (on != tailOn && tail != null && tailDim != null && tailBright != null)
            {
                tailOn = on;
                tail.sharedMaterial = on ? tailBright : tailDim;
            }

            // The contact shadow sits 2 mm above the surface at any scale (clear of a table's depth).
            if (shadow != null) shadow.localPosition = new Vector3(0f, 0.002f / s, 0f);

            lastPos = p;
            lastForward = f;
        }

        // ---- The meshes, built once and shared by every truck -----------------------------------------------------

        /// <summary>The stock chassis's height off the ground (m): the ride height of the stock design.</summary>
        const float StockChassisHeight = 0.021f;

        static void EnsureMeshes()
        {
            if (chassisMesh != null && sprungMesh != null && wheelMesh != null && tailMesh != null && shadowMesh != null) return;
            chassisMesh = BuildChassis(MockTruckSim.Wheelbase, Track, WheelRadius, 0f);
            sprungMesh = BuildSprung();
            wheelMesh = BuildWheel();
            tailMesh = BuildTail();
            shadowMesh = BuildShadow();
        }

        // The chassis for a geometry: plate, bumpers and skid at the ride height (lift over the stock 21 mm), stretched to
        // the wheelbase and track; axles at the wheels' centres; shocks from each hub up to the plate's towers.
        static Mesh BuildChassis(float wheelbase, float track, float wheelRadius, float lift)
        {
            var b = new MeshBuilder(1);
            float wb = wheelbase * 0.5f, kx = track / Track, kz = wheelbase / MockTruckSim.Wheelbase;
            b.Box(0, new Vector3(0f, 0.026f + lift, 0f), new Vector3(0.085f * kx, 0.01f, 0.22f * kz));                    // plate
            b.Box(0, new Vector3(0f, 0.034f + lift, 0.124f * kz), new Vector3(0.13f * kx, 0.022f, 0.018f));               // front bumper
            b.Box(0, new Vector3(0f, 0.022f + lift, 0.112f * kz), new Vector3(0.05f * kx, 0.008f, 0.03f));                // skid plate
            b.Box(0, new Vector3(0f, 0.036f + lift, -0.122f * kz), new Vector3(0.11f * kx, 0.02f, 0.016f));               // rear bumper
            float towerY = 0.068f + lift, towerX = 0.032f * kx;
            for (int z = -1; z <= 1; z += 2)
            {
                b.Box(0, new Vector3(0f, wheelRadius, z * wb), new Vector3(track - WheelWidth, 0.008f, 0.008f));  // axle
                for (int x = -1; x <= 1; x += 2)  // shocks, leaning in from the hub to the tower
                {
                    var hub = new Vector3(x * (track * 0.5f - WheelWidth * 0.75f), wheelRadius, z * wb);
                    var top = new Vector3(x * towerX, towerY, z * wb);
                    Vector3 d = top - hub;
                    float len = Mathf.Max(0.01f, d.magnitude);
                    b.Box(0, (hub + top) * 0.5f, new Vector3(0.007f, len, 0.007f), Quaternion.FromToRotation(Vector3.up, d / len));
                }
            }
            return b.ToMesh("Truck chassis");
        }

        // Submeshes: 0 paint, 1 dark (glass, grille, light bar, antenna), 2 accent, 3 headlights. Built in ground
        // coordinates, then lowered by SprungY so the body's pivot is at the axles.
        static Mesh BuildSprung()
        {
            var b = new MeshBuilder(4) { Offset = new Vector3(0f, -SprungY, 0f) };
            float wb = MockTruckSim.Wheelbase * 0.5f;
            // Paint.
            b.Box(0, new Vector3(0f, 0.066f, 0f), new Vector3(0.11f, 0.03f, 0.236f));                // tub
            for (int z = -1; z <= 1; z += 2)
                for (int x = -1; x <= 1; x += 2)
                {
                    b.Box(0, new Vector3(x * 0.072f, 0.079f, z * wb), new Vector3(0.046f, 0.007f, 0.078f));       // fender top
                    b.Box(0, new Vector3(x * 0.072f, 0.072f, z * wb + 0.042f), new Vector3(0.046f, 0.02f, 0.007f), Quaternion.Euler(-30f, 0f, 0f));  // fender front lip
                    b.Box(0, new Vector3(x * 0.072f, 0.072f, z * wb - 0.042f), new Vector3(0.046f, 0.02f, 0.007f), Quaternion.Euler(30f, 0f, 0f));   // fender rear lip
                }
            b.Box(0, new Vector3(0f, 0.086f, 0.074f), new Vector3(0.104f, 0.012f, 0.084f), Quaternion.Euler(4f, 0f, 0f));  // hood, sloping to the nose
            b.Box(0, new Vector3(0f, 0.1f, -0.004f), new Vector3(0.1f, 0.04f, 0.07f));               // cab
            b.Box(0, new Vector3(0f, 0.124f, -0.008f), new Vector3(0.096f, 0.008f, 0.064f));         // roof
            b.Box(0, new Vector3(-0.049f, 0.091f, -0.078f), new Vector3(0.008f, 0.02f, 0.08f));      // bed side
            b.Box(0, new Vector3(0.049f, 0.091f, -0.078f), new Vector3(0.008f, 0.02f, 0.08f));       // bed side
            b.Box(0, new Vector3(0f, 0.091f, -0.115f), new Vector3(0.106f, 0.02f, 0.008f));          // tailgate
            b.Box(0, new Vector3(0f, 0.112f, -0.112f), new Vector3(0.11f, 0.004f, 0.022f), Quaternion.Euler(-10f, 0f, 0f));  // wing
            // Dark.
            b.Box(1, new Vector3(0f, 0.104f, 0.033f), new Vector3(0.088f, 0.036f, 0.004f), Quaternion.Euler(-28f, 0f, 0f));  // windscreen
            b.Box(1, new Vector3(-0.0505f, 0.104f, -0.006f), new Vector3(0.003f, 0.022f, 0.05f));    // side windows
            b.Box(1, new Vector3(0.0505f, 0.104f, -0.006f), new Vector3(0.003f, 0.022f, 0.05f));
            b.Box(1, new Vector3(0f, 0.105f, -0.0395f), new Vector3(0.08f, 0.02f, 0.003f));          // rear window
            b.Box(1, new Vector3(0f, 0.068f, 0.1195f), new Vector3(0.04f, 0.016f, 0.004f));          // grille
            b.Box(1, new Vector3(0f, 0.132f, 0.006f), new Vector3(0.084f, 0.008f, 0.01f));           // roof light bar
            b.Box(1, new Vector3(-0.036f, 0.128f, 0.006f), new Vector3(0.004f, 0.008f, 0.004f));     // its feet
            b.Box(1, new Vector3(0.036f, 0.128f, 0.006f), new Vector3(0.004f, 0.008f, 0.004f));
            b.Box(1, new Vector3(0.04f, 0.141f, -0.1f), new Vector3(0.0025f, 0.1f, 0.0025f));        // antenna
            b.Box(1, new Vector3(-0.044f, 0.104f, -0.112f), new Vector3(0.003f, 0.02f, 0.003f));     // wing posts
            b.Box(1, new Vector3(0.044f, 0.104f, -0.112f), new Vector3(0.003f, 0.02f, 0.003f));
            // Accent.
            b.Box(2, new Vector3(0f, 0.0925f, 0.074f), new Vector3(0.026f, 0.0015f, 0.084f), Quaternion.Euler(4f, 0f, 0f));  // hood stripe
            b.Box(2, new Vector3(0f, 0.1285f, -0.008f), new Vector3(0.026f, 0.0015f, 0.064f));       // roof stripe
            b.Box(2, new Vector3(0.04f, 0.193f, -0.1f), new Vector3(0.008f, 0.008f, 0.008f));        // antenna tip
            b.Box(2, new Vector3(-0.0555f, 0.066f, 0f), new Vector3(0.0015f, 0.008f, 0.2f));         // side stripes
            b.Box(2, new Vector3(0.0555f, 0.066f, 0f), new Vector3(0.0015f, 0.008f, 0.2f));
            // Headlights, and the light bar's lamps.
            b.Box(3, new Vector3(-0.036f, 0.073f, 0.1195f), new Vector3(0.022f, 0.012f, 0.004f));
            b.Box(3, new Vector3(0.036f, 0.073f, 0.1195f), new Vector3(0.022f, 0.012f, 0.004f));
            b.Box(3, new Vector3(0f, 0.132f, 0.0115f), new Vector3(0.07f, 0.005f, 0.002f));
            return b.ToMesh("Truck body");
        }

        static Mesh BuildTail()
        {
            var b = new MeshBuilder(1) { Offset = new Vector3(0f, -SprungY, 0f) };
            b.Box(0, new Vector3(-0.038f, 0.091f, -0.1195f), new Vector3(0.018f, 0.012f, 0.003f));
            b.Box(0, new Vector3(0.038f, 0.091f, -0.1195f), new Vector3(0.018f, 0.012f, 0.003f));
            return b.ToMesh("Truck tail lights");
        }

        // A lugged tyre (submesh 0) with a recessed face and a five-spoke rim (submesh 1) on the +x side, about the x axis.
        static Mesh BuildWheel()
        {
            var b = new MeshBuilder(2);
            const int Lugs = 14, Segments = Lugs * 2;
            float r = WheelRadius, groove = r * 0.9f, rimR = r * 0.64f, hw = WheelWidth * 0.5f;
            float xo = hw, xi = -hw, recess = hw - 0.006f;
            for (int i = 0; i < Segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / Segments, a1 = (i + 1) * Mathf.PI * 2f / Segments;
                float ro = i % 2 == 0 ? r : groove;
                Vector3 d0 = Dir(a0), d1 = Dir(a1);
                // Tread.
                b.Quad(0, new Vector3(xi, 0f, 0f) + d0 * ro, new Vector3(xo, 0f, 0f) + d0 * ro, new Vector3(xo, 0f, 0f) + d1 * ro, new Vector3(xi, 0f, 0f) + d1 * ro, Dir((a0 + a1) * 0.5f));
                // Lug walls where the radius steps.
                float next = (i + 1) % 2 == 0 ? r : groove;
                Vector3 tangent = Dir(a1 + Mathf.PI * 0.5f);
                if (ro > next) b.Quad(0, new Vector3(xi, 0f, 0f) + d1 * next, new Vector3(xo, 0f, 0f) + d1 * next, new Vector3(xo, 0f, 0f) + d1 * ro, new Vector3(xi, 0f, 0f) + d1 * ro, tangent);
                else b.Quad(0, new Vector3(xi, 0f, 0f) + d1 * ro, new Vector3(xo, 0f, 0f) + d1 * ro, new Vector3(xo, 0f, 0f) + d1 * next, new Vector3(xi, 0f, 0f) + d1 * next, -tangent);
                // Sidewalls: the outer one a ring round the rim, the inner one closed.
                b.Quad(0, new Vector3(xo, 0f, 0f) + d0 * rimR, new Vector3(xo, 0f, 0f) + d0 * ro, new Vector3(xo, 0f, 0f) + d1 * ro, new Vector3(xo, 0f, 0f) + d1 * rimR, Vector3.right);
                b.Tri(0, new Vector3(xi, 0f, 0f), new Vector3(xi, 0f, 0f) + d0 * ro, new Vector3(xi, 0f, 0f) + d1 * ro, Vector3.left);
                // The recess behind the spokes (dark), and its lip.
                b.Tri(0, new Vector3(recess, 0f, 0f), new Vector3(recess, 0f, 0f) + d0 * rimR, new Vector3(recess, 0f, 0f) + d1 * rimR, Vector3.right);
                b.Quad(1, new Vector3(recess, 0f, 0f) + d0 * rimR, new Vector3(xo - 0.001f, 0f, 0f) + d0 * rimR, new Vector3(xo - 0.001f, 0f, 0f) + d1 * rimR, new Vector3(recess, 0f, 0f) + d1 * rimR, -Dir((a0 + a1) * 0.5f));
            }
            // Rim: a lip ring, five spokes and a hub, standing just proud of the recess.
            float spokeX = recess + 0.003f;
            for (int i = 0; i < 5; i++)
            {
                float a = i * Mathf.PI * 2f / 5f;
                var rot = Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.right);  // local y onto Dir(a)
                b.Box(1, new Vector3(spokeX, 0f, 0f) + Dir(a) * (rimR * 0.5f), new Vector3(0.004f, rimR, 0.005f), rot);
            }
            b.Box(1, new Vector3(spokeX + 0.0005f, 0f, 0f), new Vector3(0.006f, 0.011f, 0.011f));
            for (int i = 0; i < 10; i++)
            {
                float a0 = i * Mathf.PI * 2f / 10f, a1 = (i + 1) * Mathf.PI * 2f / 10f;
                b.Quad(1, new Vector3(xo - 0.0008f, 0f, 0f) + Dir(a0) * (rimR * 0.82f), new Vector3(xo - 0.0008f, 0f, 0f) + Dir(a0) * rimR,
                          new Vector3(xo - 0.0008f, 0f, 0f) + Dir(a1) * rimR, new Vector3(xo - 0.0008f, 0f, 0f) + Dir(a1) * (rimR * 0.82f), Vector3.right);
            }
            return b.ToMesh("Truck wheel");
        }

        // The direction at angle a in the wheel's plane (y up, z forward): a = 0 is up.
        static Vector3 Dir(float a) => new Vector3(0f, Mathf.Cos(a), Mathf.Sin(a));

        // A soft dark ellipse under the truck: opaque-ish in the middle, fading to nothing at the edge.
        static Mesh BuildShadow()
        {
            const int Sides = 24;
            float rx = 0.11f, rz = 0.15f;
            var vertices = new List<Vector3> { Vector3.zero };
            var colors = new List<Color> { new Color(0f, 0f, 0f, 0.55f) };
            var triangles = new List<int>();
            for (int ring = 1; ring <= 2; ring++)
            {
                float f = ring == 1 ? 0.6f : 1f;
                float alpha = ring == 1 ? 0.4f : 0f;
                for (int i = 0; i < Sides; i++)
                {
                    float a = i * Mathf.PI * 2f / Sides;
                    vertices.Add(new Vector3(Mathf.Sin(a) * rx * f, 0f, Mathf.Cos(a) * rz * f));
                    colors.Add(new Color(0f, 0f, 0f, alpha));
                }
            }
            for (int i = 0; i < Sides; i++)
            {
                int a = 1 + i, bb = 1 + (i + 1) % Sides;
                // Seen from above (+y): clockwise there is the front face in Unity.
                triangles.Add(0); triangles.Add(a); triangles.Add(bb);
                int oa = a + Sides, ob = bb + Sides;
                triangles.Add(a); triangles.Add(oa); triangles.Add(ob);
                triangles.Add(a); triangles.Add(ob); triangles.Add(bb);
            }
            var normals = new List<Vector3>(vertices.Count);
            for (int i = 0; i < vertices.Count; i++) normals.Add(Vector3.up);
            var mesh = new Mesh { name = "Truck shadow" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Flat-shaded boxes, quads and triangles into one mesh with a submesh per material.</summary>
        sealed class MeshBuilder
        {
            readonly List<Vector3> vertices = new List<Vector3>();
            readonly List<Vector3> normals = new List<Vector3>();
            readonly List<int>[] triangles;
            public Vector3 Offset;

            public MeshBuilder(int submeshes)
            {
                triangles = new List<int>[submeshes];
                for (int i = 0; i < submeshes; i++) triangles[i] = new List<int>();
            }

            public void Box(int sub, Vector3 centre, Vector3 size) => Box(sub, centre, size, Quaternion.identity);

            public void Box(int sub, Vector3 centre, Vector3 size, Quaternion rotation)
            {
                Vector3 h = size * 0.5f;
                Vector3 X = rotation * new Vector3(h.x, 0f, 0f), Y = rotation * new Vector3(0f, h.y, 0f), Z = rotation * new Vector3(0f, 0f, h.z);
                Face(sub, centre, X, Y, Z);
                Face(sub, centre, -X, Y, -Z);
                Face(sub, centre, Y, Z, X);
                Face(sub, centre, -Y, Z, -X);
                Face(sub, centre, Z, X, Y);
                Face(sub, centre, -Z, X, -Y);
            }

            // The face whose centre is c + n, spanned by u and v.
            void Face(int sub, Vector3 c, Vector3 n, Vector3 u, Vector3 v)
            {
                Vector3 m = c + n;
                Quad(sub, m - u - v, m + u - v, m + u + v, m - u + v, n.normalized);
            }

            public void Quad(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
            {
                Tri(sub, a, b, c, normal);
                Tri(sub, a, c, d, normal);
            }

            // Wound so the face's front is on the normal's side (Unity's front face: Cross(b - a, c - a) towards the viewer).
            public void Tri(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
            {
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f) { var t = b; b = c; c = t; }
                int i = vertices.Count;
                vertices.Add(a + Offset); vertices.Add(b + Offset); vertices.Add(c + Offset);
                var n = normal.normalized;
                normals.Add(n); normals.Add(n); normals.Add(n);
                triangles[sub].Add(i); triangles[sub].Add(i + 1); triangles[sub].Add(i + 2);
            }

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh { name = name };
                if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(vertices);
                mesh.SetNormals(normals);
                mesh.subMeshCount = triangles.Length;
                for (int i = 0; i < triangles.Length; i++) mesh.SetTriangles(triangles[i], i);
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
