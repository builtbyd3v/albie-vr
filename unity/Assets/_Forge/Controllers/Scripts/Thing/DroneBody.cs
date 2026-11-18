using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Forge.Controllers
{
    /// <summary>
    /// The drone's body: an <see cref="IThingBody"/> a person can fly in their room, the peer of the truck's
    /// <see cref="ExampleTruckBody"/>. It is an arcade model, not a rotor simulation, made for a beginner flying by hand:
    /// - it holds its height by itself (the climb stick moves the height it holds), levels itself, and brakes to a hover
    ///   when the sticks are let go;
    /// - speed, climb, turn rate and tilt are capped (<see cref="maxSpeed"/>, <see cref="climbSpeed"/>,
    ///   <see cref="yawRate"/>, <see cref="maxTiltDeg"/>);
    /// - it stays between a soft floor (<see cref="minHover"/> above whatever is under it) and a soft ceiling (the room's
    ///   ceiling less <see cref="ceilingMargin"/>, just under anything else above it, and never above <see cref="maxAltitude"/>);
    /// - <see cref="TakeOff"/> spins the props up and climbs to <see cref="takeoffHeight"/>; <see cref="Land"/> comes
    ///   straight down onto whatever is under it; <see cref="Hold"/> is the safe stop (brake and hover where it is);
    ///   <see cref="ComeHome"/> flies back over where it was placed and lands.
    /// It moves its transform itself, once per physics step, as the truck does, and has no collider of its own (so no
    /// sensor ray sees it). Solid colliders stop it: a sphere of <see cref="radius"/> is swept along each step. A slow
    /// touch slides along the surface (<see cref="Bumps"/> counts them); a touch faster than <see cref="crashSpeed"/> is
    /// a crash: the motors cut, it falls and ends on its back, so <see cref="Upright"/> goes negative and a run's
    /// <c>upright</c> rule can end the run. <see cref="Place"/> puts it back, landed and whole.
    /// Actuators (each -1 to 1): <c>fly.forward</c>, <c>fly.right</c>, <c>fly.climb</c>, <c>fly.yaw</c> (+ turns right).
    /// With <see cref="headless"/> on, forward is away from the pilot whichever way the nose points.
    /// It also reads the keyboard, a Touch controller's sticks and (when switched on) the pinch sticks into
    /// <see cref="ManualInputs"/>, each only when it changes, as the brain truck does. No allocations per step or frame.
    /// </summary>
    public sealed class DroneBody : MonoBehaviour, IThingBody
    {
        public enum Phase { Landed, TakingOff, Flying, Landing, Crashed }

        public static readonly string[] Sensors = { "alt.height", "speed.forward", "speed.right", "speed.climb", "course.bearing" };
        public static readonly string[] Actuators = { "fly.forward", "fly.right", "fly.climb", "fly.yaw" };
        static readonly string[][] NoSwap = new string[0][];
        static readonly string[] Negate = { "fly.right", "fly.yaw", "speed.right", "course.bearing" };

        /// <summary>The drone being flown now, or null (none spawned, or it is hidden). Albie's tools and the Drive window read it.</summary>
        public static DroneBody Active { get; private set; }

        // ---- Tuning: the numbers to change first when it is flown on a headset --------------------------------------
        [Header("Speed")]
        [Tooltip("Top horizontal speed, m/s.")] public float maxSpeed = 1.2f;
        [Tooltip("How fast it picks up speed towards what the stick asks, m/s2.")] public float accel = 2.5f;
        [Tooltip("How fast it stops when the stick is let go, m/s2.")] public float brake = 3.5f;
        [Tooltip("Top climb and descent speed, m/s.")] public float climbSpeed = 0.6f;
        [Tooltip("Turn rate at full stick, degrees a second.")] public float yawRate = 90f;
        [Tooltip("The most it leans, degrees.")] public float maxTiltDeg = 18f;
        [Tooltip("How quickly the lean follows, 1/s.")] public float tiltFollow = 8f;
        [Header("Height")]
        [Tooltip("Height it climbs to on take-off, metres above what it stood on.")] public float takeoffHeight = 0.8f;
        [Tooltip("Lowest hover, metres above whatever is under it.")] public float minHover = 0.2f;
        [Tooltip("Highest hover, metres above the floor.")] public float maxAltitude = 1.9f;
        [Tooltip("Gap kept under the room's ceiling, metres.")] public float ceilingMargin = 0.35f;
        [Tooltip("How hard it pulls back to the height it holds, 1/s.")] public float altitudeGain = 3f;
        [Tooltip("Descent speed when landing, m/s.")] public float landSpeed = 0.35f;
        [Tooltip("Holding the climb stick down at the lowest hover for this long lands it, seconds.")] public float pushDownToLand = 0.6f;
        [Header("Body")]
        [Tooltip("Half its span: the sphere swept against colliders, and the half-width a course scores hits with.")] public float radius = 0.14f;
        [Tooltip("Height of its centre above the surface it stands on.")] public float restHeight = 0.05f;
        [Tooltip("Hitting something faster than this (m/s into the surface) is a crash; slower just slides.")] public float crashSpeed = 0.9f;
        [Tooltip("Seconds for the props to spin up before it lifts.")] public float spinUpSeconds = 0.6f;
        [Tooltip("Prop speed at full spin, turns a second (for the look only).")] public float propTurnsPerSecond = 18f;
        [Header("Range and home")]
        [Tooltip("It will not fly further than this from where it was placed, metres.")] public float maxRange = 6f;
        [Tooltip("Speed when it flies home by itself, m/s.")] public float returnSpeed = 0.7f;
        [Header("Control")]
        [Tooltip("Forward on the stick is away from the pilot (the camera), whichever way the nose points.")] public bool headless = true;
        [Tooltip("Read the keyboard (arrows move, PageUp/PageDown climb, comma/period turn), Touch sticks and pinch sticks.")] public bool readsInput = true;
        /// <summary>What the sweep and the height rays see (everything by default; UI canvases are skipped, see <see cref="Solid"/>).</summary>
        public LayerMask obstacles = ~0;

        // ---- State ---------------------------------------------------------------------------------------------------
        public Phase State { get; private set; } = Phase.Landed;
        public bool Crashed => State == Phase.Crashed;
        /// <summary>True from take-off until it is back on a surface (or has crashed).</summary>
        public bool Airborne => State == Phase.TakingOff || State == Phase.Flying || State == Phase.Landing;
        /// <summary>True while it flies home by itself (<see cref="ComeHome"/>).</summary>
        public bool Returning { get; private set; }
        /// <summary>World velocity, m/s.</summary>
        public Vector3 Velocity => velocity;
        /// <summary>Speed along all three axes, m/s. Stepped in FixedUpdate, so a run reads it there.</summary>
        public float Speed => velocity.magnitude;
        public float ForwardSpeed => Vector3.Dot(velocity, transform.forward);
        /// <summary>Dot of the body's up and world up: 1 level, about 0.95 at full lean, negative on its back after a crash.</summary>
        public float Upright { get; private set; } = 1f;
        /// <summary>Height of its centre above the room's floor, metres.</summary>
        public float HeightAboveFloor => transform.position.y - floorY;
        /// <summary>Slow touches since it was placed (it slid along something).</summary>
        public int Bumps { get; private set; }
        /// <summary>0 props at rest, 1 flying speed.</summary>
        public float Spin => spin;
        /// <summary>Where <see cref="ComeHome"/> returns to: where it was last placed.</summary>
        public Vector3 Home => home;
        /// <summary>The part that leans and is drawn between physics steps; the shell goes under it.</summary>
        public Transform Visual { get; private set; }

        public string[] SensorNames => Sensors;
        public string[] ActuatorNames => Actuators;
        public string[][] MirrorSwap => NoSwap;
        public string[] MirrorNegate => Negate;

        Vector3 velocity, home, lastPosition;
        float yaw, holdY, spin, pitch, roll, floorY, ceilingY = float.PositiveInfinity;
        float groundY, roofY, pushedDown, pilotYaw;
        bool hasRoom;
        Transform[] props = new Transform[0];
        Quaternion crashFrom;
        float crashT;

        static readonly RaycastHit[] hits = new RaycastHit[8];
        Collider lastSkipped, lastSolid;
        const float Skin = 0.01f;

        readonly int stickX = ManualInputs.IndexOf("stick.x"), stickY = ManualInputs.IndexOf("stick.y"),
                     stick2X = ManualInputs.IndexOf("stick2.x"), stick2Y = ManualInputs.IndexOf("stick2.y");
        Vector4 lastKeys, lastHands;
        Vector2 lastLeft, lastRight;

        PhysicsScene Physics => gameObject.scene.GetPhysicsScene();

        // ---- Follow-me, the design and the course's scale (DroneFollow, DroneDesign, ArtifactDrone) -------------------

        /// <summary>True while it flies to a point it is given (<see cref="FollowTo"/>), facing another; DroneFollow keeps the point fresh.</summary>
        public bool Following { get; private set; }
        /// <summary>Why the last follow ended ("stick", "stop", "land", "home", "crash", "placed", "ended"), or null while following / never followed.</summary>
        public string FollowEnded { get; private set; }
        /// <summary>The design's thrust to weight (NaN when not known). Below <see cref="MinThrustToWeight"/> it will not take off.</summary>
        public float ThrustToWeight { get; set; } = float.NaN;
        public const float MinThrustToWeight = 1.1f;
        /// <summary>The course's scale it was last retuned for (1 on the floor, 1/4 to 1/10 on a desk course).</summary>
        public float SizeScale { get; private set; } = 1f;

        Vector3 followGoal, followFace;
        float followSpeed;
        float bMaxSpeed, bAccel, bBrake, bClimb, bYaw, bTakeoff, bMinHover, bRadius, bRest, bCrash, bRange, bReturn, bLand;
        bool baseCaptured;

        void CaptureBase()
        {
            if (baseCaptured) return;
            baseCaptured = true;
            bMaxSpeed = maxSpeed; bAccel = accel; bBrake = brake; bClimb = climbSpeed; bYaw = yawRate;
            bTakeoff = takeoffHeight; bMinHover = minHover; bRadius = radius; bRest = restHeight; bCrash = crashSpeed;
            bRange = maxRange; bReturn = returnSpeed; bLand = landSpeed;
        }

        /// <summary>The rest height it was built with (the design's feet are placed this far under its centre, before any course scale).</summary>
        public float BaseRestHeight { get { CaptureBase(); return bRest; } }

        /// <summary>
        /// Re-derives the tuning from the values it was made with (so calls never compound): lengths and speeds times
        /// <paramref name="sizeScale"/> (a desk course's scale; the look scales too), the swept radius from
        /// <paramref name="radiusFullSize"/> (the design's half span at full size; 0 keeps the built-in one), and the
        /// design's factors on acceleration, braking, climb and turn rate (1 = unchanged). The altitude limits stay the room's.
        /// </summary>
        public void Retune(float sizeScale, float radiusFullSize, float accelK, float brakeK, float climbK, float yawK)
        {
            CaptureBase();
            float s = Mathf.Clamp(sizeScale, 0.05f, 1f);
            SizeScale = s;
            maxSpeed = bMaxSpeed * s;
            accel = bAccel * s * Mathf.Clamp(accelK, 0.5f, 1.5f);
            brake = bBrake * s * Mathf.Clamp(brakeK, 0.5f, 1.5f);
            climbSpeed = bClimb * s * Mathf.Clamp(climbK, 0.5f, 1.5f);
            yawRate = bYaw * Mathf.Clamp(yawK, 0.5f, 1.5f);
            takeoffHeight = bTakeoff * s;
            minHover = bMinHover * s;
            radius = (radiusFullSize > 0.01f ? radiusFullSize : bRadius) * s;
            restHeight = bRest * s;
            crashSpeed = bCrash * s;
            maxRange = bRange * s;
            returnSpeed = bReturn * s;
            landSpeed = bLand * s;
            EnsureVisual();
            Visual.localScale = Vector3.one * s;
        }

        /// <summary>
        /// Flies towards <paramref name="goal"/> (world; its y is the height to hold) at no more than
        /// <paramref name="speed"/> m/s (and <see cref="maxSpeed"/>), turning to face <paramref name="faceAt"/>. Call it
        /// again to move the goal. A firm stick (the actuators or the manual inputs), <see cref="Hold"/>, <see cref="Land"/>,
        /// <see cref="ComeHome"/>, a crash or <see cref="Place"/> ends it. Returns null, or why not.
        /// </summary>
        public string FollowTo(Vector3 goal, Vector3 faceAt, float speed)
        {
            if (State == Phase.Crashed) return "crashed: reset the drone first";
            if (State != Phase.Flying && State != Phase.TakingOff) return "not flying: take off first";
            Returning = false;
            followGoal = goal;
            followFace = faceAt;
            followSpeed = Mathf.Max(0f, speed);
            if (!Following) FollowEnded = null;
            Following = true;
            return null;
        }

        /// <summary>Ends a follow (it then brakes and holds where it is). <paramref name="why"/> goes to <see cref="FollowEnded"/>.</summary>
        public void EndFollow(string why = "ended")
        {
            if (!Following) return;
            Following = false;
            FollowEnded = why;
            if (State == Phase.Flying) holdY = transform.position.y;
        }

        /// <summary>True when a manual stick channel (stick, stick2) is pushed past <paramref name="threshold"/>: "the pilot took over".</summary>
        public static bool SticksActive(float threshold = 0.25f)
        {
            var v = ManualInputs.Values;
            for (int i = 0; i < StickChannels.Length; i++)
            {
                int c = StickChannels[i];
                if (c >= 0 && c < v.Length && (v[c] > threshold || v[c] < -threshold)) return true;
            }
            return false;
        }

        static readonly int[] StickChannels =
        {
            ManualInputs.IndexOf("stick.x"), ManualInputs.IndexOf("stick.y"), ManualInputs.IndexOf("stick2.x"), ManualInputs.IndexOf("stick2.y"),
        };

        void Awake()
        {
            CaptureBase();
            EnsureVisual();
            yaw = transform.eulerAngles.y;
            home = lastPosition = transform.position;
            floorY = transform.position.y - restHeight;
            groundY = floorY;
            holdY = transform.position.y;
        }

        void EnsureVisual()
        {
            if (Visual != null) return;
            var go = new GameObject("Visual");
            go.transform.SetParent(transform, false);
            Visual = go.transform;
        }

        void OnEnable() => Active = this;

        void OnDisable()
        {
            if (Active == this) Active = null;
        }

        /// <summary>The prop pivots to spin (the shell's builder gives them). Odd ones turn the other way.</summary>
        public void SetProps(Transform[] pivots) => props = pivots ?? new Transform[0];

        /// <summary>
        /// The room's floor and ceiling in world y. <paramref name="ceiling"/> may be NaN or infinity (no ceiling known):
        /// <see cref="maxAltitude"/> still holds. With no call, the floor is where the drone was first placed.
        /// </summary>
        public void SetRoom(float floor, float ceiling)
        {
            floorY = floor;
            ceilingY = float.IsNaN(ceiling) ? float.PositiveInfinity : ceiling;
            hasRoom = true;
        }

        // ---- Commands ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Puts the drone down at <paramref name="position"/> (a point on the surface it should stand on), facing
        /// <paramref name="rotation"/>'s heading: landed, stopped, props at rest, not crashed. This is the reset between runs.
        /// </summary>
        public void Place(Vector3 position, Quaternion rotation)
        {
            EnsureVisual();
            yaw = rotation.eulerAngles.y;
            var at = position + Vector3.up * restHeight;
            transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
            if (!hasRoom) floorY = position.y;
            home = lastPosition = at;
            groundY = position.y;
            holdY = at.y;
            velocity = Vector3.zero;
            spin = pitch = roll = 0f;
            pushedDown = 0f;
            Upright = 1f;
            Bumps = 0;
            Returning = false;
            EndFollow("placed");
            State = Phase.Landed;
            Visual.SetPositionAndRotation(at, transform.rotation);
        }

        /// <summary>Spins up and climbs to <see cref="takeoffHeight"/>. Returns null, or why it will not (it has crashed).</summary>
        public string TakeOff()
        {
            if (State == Phase.Crashed) return "crashed: reset the drone first";
            if (State == Phase.Flying || State == Phase.TakingOff) return null;
            if (!float.IsNaN(ThrustToWeight) && ThrustToWeight < MinThrustToWeight)
                return "too_heavy: this design's thrust to weight is " + ThrustToWeight.ToString("0.0") + " : 1, so it cannot lift itself. Make it lighter or give it bigger props or motors in CAD.";
            Returning = false;
            pushedDown = 0f;
            if (State == Phase.Landing)
            {
                holdY = Mathf.Max(transform.position.y, groundY + minHover);
                State = Phase.Flying;
                return null;
            }
            holdY = transform.position.y - restHeight + takeoffHeight;
            State = Phase.TakingOff;
            return null;
        }

        /// <summary>Comes straight down onto whatever is under it. Returns null, or why not.</summary>
        public string Land()
        {
            if (State == Phase.Crashed) return "crashed: reset the drone first";
            if (State == Phase.Landed || State == Phase.Landing) return null;
            Returning = false;
            EndFollow("land");
            State = Phase.Landing;
            return null;
        }

        /// <summary>The safe stop: gives up flying home and following, and (with the sticks at rest) it brakes and hovers where it is. It never cuts the motors in the air.</summary>
        public void Hold()
        {
            Returning = false;
            EndFollow("stop");
            pushedDown = 0f;
            if (State == Phase.Flying) holdY = transform.position.y;
        }

        /// <summary>Flies back over where it was placed, at the height it has now, and lands there. Returns null, or why not.</summary>
        public string ComeHome()
        {
            if (State == Phase.Crashed) return "crashed: reset the drone first";
            if (State == Phase.Landed) return "it is on the ground already";
            if (State == Phase.Landing) State = Phase.Flying;
            EndFollow("home");
            holdY = Mathf.Max(transform.position.y, groundY + minHover);
            Returning = true;
            return null;
        }

        // ---- The physics step ----------------------------------------------------------------------------------------

        public void Sense(float[] sensors)
        {
            var t = transform;
            sensors[0] = Mathf.Clamp(HeightAboveFloor / Mathf.Max(0.1f, maxAltitude), -1f, 1f);
            sensors[1] = Mathf.Clamp(Vector3.Dot(velocity, t.forward) / maxSpeed, -1f, 1f);
            sensors[2] = Mathf.Clamp(Vector3.Dot(velocity, t.right) / maxSpeed, -1f, 1f);
            sensors[3] = Mathf.Clamp(velocity.y / climbSpeed, -1f, 1f);
            sensors[4] = CourseBeacon.Bearing(t.position, t.forward, Vector3.up);
        }

        public void Apply(float[] actuators, float dt)
        {
            if (dt <= 0f) return;
            float forward = actuators[0], right = actuators[1], climb = actuators[2], turn = actuators[3];
            var position = transform.position;
            lastPosition = position;
            ProbeHeights(position);

            switch (State)
            {
                case Phase.Landed:
                    velocity = Vector3.zero;
                    spin = Mathf.MoveTowards(spin, 0f, dt / Mathf.Max(0.05f, spinUpSeconds));
                    Lean(0f, 0f, dt);
                    break;

                case Phase.TakingOff:
                    velocity = Vector3.zero;
                    spin = Mathf.MoveTowards(spin, 1f, dt / Mathf.Max(0.05f, spinUpSeconds));
                    if (spin >= 1f) State = Phase.Flying;
                    break;

                case Phase.Flying:
                    spin = 1f;
                    position = Fly(position, forward, right, climb, turn, dt);
                    break;

                case Phase.Landing:
                {
                    spin = 1f;
                    var flat = Vector3.MoveTowards(new Vector3(velocity.x, 0f, velocity.z), Vector3.zero, brake * dt);
                    float before = position.y;
                    velocity = flat;
                    position = Move(position, flat * dt, false);
                    if (State != Phase.Landing) break;
                    float rest = groundY + restHeight;
                    position.y = Mathf.Max(rest, before - landSpeed * dt);
                    velocity.y = (position.y - before) / dt;
                    Lean(0f, 0f, dt);
                    if (position.y <= rest + 1e-4f)
                    {
                        velocity = Vector3.zero;
                        State = Phase.Landed;
                    }
                    break;
                }

                case Phase.Crashed:
                {
                    spin = Mathf.MoveTowards(spin, 0f, dt * 4f);
                    velocity.x *= Mathf.Clamp01(1f - 3f * dt);
                    velocity.z *= Mathf.Clamp01(1f - 3f * dt);
                    velocity.y -= 9.81f * dt;
                    float before = position.y;
                    position = Move(position, new Vector3(velocity.x, 0f, velocity.z) * dt, false);
                    float rest = groundY + restHeight;
                    position.y = Mathf.Max(rest, before + velocity.y * dt);
                    if (position.y <= rest + 1e-4f) velocity = Vector3.zero;
                    // Rolls onto its back over about a third of a second, and stays there.
                    crashT = Mathf.Min(1f, crashT + dt * 3f);
                    Upright = Mathf.Lerp(Upright, -0.94f, crashT);
                    break;
                }
            }
            transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
        }

        Vector3 Fly(Vector3 position, float forward, float right, float climb, float turn, float dt)
        {
            // What the pilot asks, as a velocity over the floor.
            Vector3 want;
            if (Returning)
            {
                // Any firm push on a stick takes the drone back.
                if (Mathf.Abs(forward) > 0.3f || Mathf.Abs(right) > 0.3f || Mathf.Abs(climb) > 0.3f || Mathf.Abs(turn) > 0.3f) Returning = false;
            }
            if (Following)
            {
                // Any stick input takes it back: the actuators (whatever controller is attached) or the raw manual sticks.
                if (Mathf.Abs(forward) > 0.25f || Mathf.Abs(right) > 0.25f || Mathf.Abs(climb) > 0.25f || Mathf.Abs(turn) > 0.25f
                    || SticksActive()) EndFollow("stick");
            }
            if (Following)
            {
                var to = followGoal - position;
                to.y = 0f;
                float far = to.magnitude;
                // Slows down over the last stretch so it settles on the point instead of overshooting it.
                float top = Mathf.Min(followSpeed, maxSpeed, far * 1.5f);
                want = far > 1e-3f ? to / far * top : Vector3.zero;
                holdY = Mathf.MoveTowards(holdY, followGoal.y, climbSpeed * dt);
                var face = followFace - position;
                face.y = 0f;
                if (face.sqrMagnitude > 0.01f)
                {
                    float target = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
                    yaw = Mathf.Repeat(Mathf.MoveTowardsAngle(yaw, target, yawRate * 0.8f * dt), 360f);
                }
                climb = 0f;
                turn = 0f;
                Fence(position, ref want);
            }
            else if (Returning)
            {
                var to = home - position;
                to.y = 0f;
                float far = to.magnitude;
                if (far < 0.06f && velocity.sqrMagnitude < 0.01f)
                {
                    Returning = false;
                    State = Phase.Landing;
                    return position;
                }
                want = far > 1e-4f ? to / far * Mathf.Min(returnSpeed, far * 2f) : Vector3.zero;
                climb = 0f;
                turn = 0f;
            }
            else
            {
                float push = Mathf.Sqrt(forward * forward + right * right);
                if (push > 1f) { forward /= push; right /= push; }
                float heading = headless ? pilotYaw : yaw;
                want = Quaternion.Euler(0f, heading, 0f) * new Vector3(right, 0f, forward) * maxSpeed;
                Fence(position, ref want);
            }
            yaw = Mathf.Repeat(yaw + Mathf.Clamp(turn, -1f, 1f) * yawRate * dt, 360f);

            // Height hold: the stick moves the height it holds, between the soft floor and the soft ceiling.
            float low = groundY + minHover;
            // Under the room's ceiling it keeps ceilingMargin; under a thing (a hoop's bar, a lamp) only a hand's width.
            float high = Mathf.Min(floorY + maxAltitude, Mathf.Min(ceilingY - ceilingMargin, roofY - radius - 0.05f));
            if (high < low) high = low;
            holdY = Mathf.Clamp(holdY + Mathf.Clamp(climb, -1f, 1f) * climbSpeed * dt, low, high);
            // Holding the stick down at the lowest hover is "land".
            if (!Returning && climb < -0.5f && holdY <= low + 1e-3f && position.y <= low + 0.05f)
            {
                pushedDown += dt;
                if (pushedDown >= pushDownToLand) { pushedDown = 0f; State = Phase.Landing; return position; }
            }
            else pushedDown = 0f;

            var flat = new Vector3(velocity.x, 0f, velocity.z);
            var next = Vector3.MoveTowards(flat, want, (want.sqrMagnitude < 1e-4f ? brake : accel) * dt);
            var gained = (next - flat) / dt;
            velocity.x = next.x;
            velocity.z = next.z;
            velocity.y = Mathf.Clamp((holdY - position.y) * altitudeGain, -climbSpeed, climbSpeed);

            // It leans into what it is doing: mostly the push, a little the speed. For the look and the upright probe only.
            var local = Quaternion.Euler(0f, -yaw, 0f) * (gained / Mathf.Max(0.1f, accel) * 0.75f + next / Mathf.Max(0.1f, maxSpeed) * 0.35f);
            Lean(Mathf.Clamp(local.z, -1f, 1f) * maxTiltDeg, Mathf.Clamp(local.x, -1f, 1f) * maxTiltDeg, dt);

            // Near the surface it took off from, the swept sphere would sit in the floor: move across only, the height rays do the rest.
            bool clear = position.y - groundY > radius + 0.03f;
            if (clear) return Move(position, velocity * dt, true);
            float y = position.y + velocity.y * dt;
            position = Move(position, new Vector3(velocity.x, 0f, velocity.z) * dt, true);
            if (State == Phase.Flying) position.y = y;
            return position;
        }

        // The range fence: nothing pushes it further out than maxRange from home.
        void Fence(Vector3 position, ref Vector3 want)
        {
            var out3 = position - home;
            out3.y = 0f;
            float away = out3.magnitude;
            if (away <= maxRange) return;
            out3 /= away;
            float outward = Vector3.Dot(want, out3);
            if (outward > 0f) want -= out3 * outward;
        }

        void Lean(float pitchDeg, float rollDeg, float dt)
        {
            float k = 1f - Mathf.Exp(-tiltFollow * dt);
            pitch += (pitchDeg - pitch) * k;
            roll += (rollDeg - roll) * k;
            Upright = Mathf.Cos(pitch * Mathf.Deg2Rad) * Mathf.Cos(roll * Mathf.Deg2Rad);
        }

        // Sweeps the body along delta. A slow touch slides along the surface; a fast one (when canCrash) is a crash.
        Vector3 Move(Vector3 position, Vector3 delta, bool canCrash)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                float distance = delta.magnitude;
                if (distance < 1e-6f) return position;
                var direction = delta / distance;
                if (!Sweep(position, direction, distance + Skin, out var hit)) return position + delta;
                float travel = Mathf.Max(0f, hit.distance - Skin);
                position += direction * travel;
                float closing = -Vector3.Dot(velocity, hit.normal);
                if (canCrash && closing > crashSpeed)
                {
                    Crash();
                    return position;
                }
                Bumps++;
                if (closing > 0f) velocity += hit.normal * closing;
                delta = Vector3.ProjectOnPlane(delta - direction * travel, hit.normal);
            }
            return position;
        }

        /// <summary>Cuts the motors: it falls and ends on its back. A fast hit does this; a check or a tool may too.</summary>
        public void Crash()
        {
            if (State == Phase.Crashed) return;
            State = Phase.Crashed;
            Returning = false;
            EndFollow("crash");
            crashT = 0f;
            crashFrom = Quaternion.Euler(pitch, 0f, -roll);
        }

        // The nearest solid thing the swept body would touch. Things it already overlaps, and things it is moving away from, don't count.
        bool Sweep(Vector3 origin, Vector3 direction, float distance, out RaycastHit nearest)
        {
            nearest = default;
            int n = Physics.SphereCast(origin, radius, direction, hits, distance, obstacles, QueryTriggerInteraction.Ignore);
            float best = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                if (h.distance <= 0f || h.distance >= best) continue;
                if (Vector3.Dot(direction, h.normal) >= 0f) continue;
                if (!Solid(h.collider)) continue;
                best = h.distance;
                nearest = h;
            }
            return best < float.PositiveInfinity;
        }

        // What is under it (it lands on that, and hovers no lower than minHover above it) and what is over it.
        void ProbeHeights(Vector3 position)
        {
            if (Ray(position, Vector3.down, 6f, out var down)) groundY = down.point.y;
            else
            {
                // Nothing solid under it. Over where it was placed that is still the surface it stood on (a desk or a
                // pad with no collider); anywhere else it is the room's floor.
                float dx = position.x - home.x, dz = position.z - home.z;
                groundY = dx * dx + dz * dz < 0.0625f ? Mathf.Max(floorY, home.y - restHeight) : floorY;
            }
            roofY = float.PositiveInfinity;
            if (Ray(position, Vector3.up, 4f, out var up)) roofY = up.point.y;
        }

        bool Ray(Vector3 origin, Vector3 direction, float distance, out RaycastHit nearest)
        {
            nearest = default;
            int n = Physics.Raycast(origin, direction, hits, distance, obstacles, QueryTriggerInteraction.Ignore);
            float best = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                if (h.distance >= best || !Solid(h.collider)) continue;
                best = h.distance;
                nearest = h;
            }
            return best < float.PositiveInfinity;
        }

        // UI is not an obstacle: a window's canvas has a collider for the hand rays, and the Drive window is right in front
        // of the pilot. The answer for the last collider asked about is kept, so flying beside one thing costs no lookup.
        bool Solid(Collider c)
        {
            if (c == null) return false;
            if (ReferenceEquals(c, lastSolid)) return true;
            if (ReferenceEquals(c, lastSkipped)) return false;
            bool ui = c.gameObject.layer == 5 || c.GetComponentInParent<Canvas>() != null;
            if (ui) lastSkipped = c; else lastSolid = c;
            return !ui;
        }

        // ---- The frame: the look and the input sources ---------------------------------------------------------------

        void Update()
        {
            var cam = Camera.main;
            if (cam != null) pilotYaw = cam.transform.eulerAngles.y;
            if (readsInput) ReadSources();

            // Drawn between physics steps, so it moves smoothly when the display is faster than the physics.
            float step = Time.fixedDeltaTime;
            float t = step > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / step) : 1f;
            var at = Vector3.Lerp(lastPosition, transform.position, t);
            Quaternion lean = State == Phase.Crashed
                ? Quaternion.Slerp(crashFrom, Quaternion.Euler(0f, 0f, 160f), crashT)
                : Quaternion.Euler(pitch, 0f, -roll);
            Visual.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f) * lean);

            if (spin <= 0f) return;
            float degrees = spin * propTurnsPerSecond * 360f * Time.deltaTime;
            for (int i = 0; i < props.Length; i++)
                if (props[i] != null) props[i].Rotate(0f, (i & 1) == 0 ? degrees : -degrees, 0f, Space.Self);
        }

        // Each source writes the channels only when its own value changes, so none overwrites another every frame (the
        // Drive window's pads write the same channels while they are held). Letting go writes zero once: the drone hovers.
        // stick is the right stick (forward, sideways); stick2 is the left (climb, turn).
        void ReadSources()
        {
            Vector4 keys = default;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null && State != Phase.Landed)
            {
                keys.x = (k.rightArrowKey.isPressed ? 1f : 0f) - (k.leftArrowKey.isPressed ? 1f : 0f);
                keys.y = (k.upArrowKey.isPressed ? 1f : 0f) - (k.downArrowKey.isPressed ? 1f : 0f);
                keys.z = (k.periodKey.isPressed ? 1f : 0f) - (k.commaKey.isPressed ? 1f : 0f);
                keys.w = (k.pageUpKey.isPressed ? 1f : 0f) - (k.pageDownKey.isPressed ? 1f : 0f);
            }
#endif
            if (keys != lastKeys) { lastKeys = keys; Write(keys.x, keys.y, keys.z, keys.w); }

            QuestStick.ReadBoth(out Vector2 left, out Vector2 right);
            if (left != lastLeft || right != lastRight)
            {
                lastLeft = left; lastRight = right;
                Write(right.x, right.y, left.x, left.y);
            }

            HandsTransmitter.ReadFlight(out float forward, out float sideways, out float climb, out float turn);
            var hands = new Vector4(sideways, forward, turn, climb);
            if (hands != lastHands) { lastHands = hands; Write(hands.x, hands.y, hands.z, hands.w); }
        }

        void Write(float x, float y, float x2, float y2)
        {
            var v = ManualInputs.Values;
            if (stickX >= 0) v[stickX] = Mathf.Clamp(x, -1f, 1f);
            if (stickY >= 0) v[stickY] = Mathf.Clamp(y, -1f, 1f);
            if (stick2X >= 0) v[stick2X] = Mathf.Clamp(x2, -1f, 1f);
            if (stick2Y >= 0) v[stick2Y] = Mathf.Clamp(y2, -1f, 1f);
        }
    }
}
