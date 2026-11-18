using Forge.Artifacts;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Forge.Controllers
{
    /// <summary>
    /// The physical RC transmitter: a handheld two-stick radio you pick up and drive the truck or fly the drone with. Built in
    /// code and installed by itself (like <c>ArtifactBootstrap</c>): one per app, shown when a Thing spawns on a course, when
    /// the Drive window or its tile asks, or by voice (<c>transmitter_show</c> / <c>transmitter_hide</c>, <see cref="TransmitterTools"/>).
    ///
    /// Hands (<c>RcTransmitter.Hands.cs</c>): close a hand round it (or pinch it) to pick it up, with either or both hands. Held,
    /// each thumb drives the stick under it: the thumb tip's position relative to where it touched down, across the face,
    /// is the deflection (<see cref="TransmitterMath.StickFromOffset"/>), with a dead zone and smoothing; lift the thumb and
    /// the stick springs back to the middle. Held in one hand, that thumb works its stick and tilting the transmitter works
    /// the other. A free hand's index finger can push a stick or press a button. Open the hand and it stays where you
    /// left it: settled onto a table or desk just under it, or floating; the sticks centre, so the truck coasts to a stop and
    /// the drone hovers. The Interaction SDK's hand grab and ray are used where the rig has them (<see cref="TransmitterIsdk"/>).
    ///
    /// Mouse (<see cref="TransmitterRaycaster"/>): click the body to take it in front of the camera (click again to put it
    /// down), drag a stick cap, click a button or the mode switch.
    ///
    /// It writes the same manual inputs as the Drive pads and a Touch controller (<see cref="ManualInputs"/>), only when a
    /// value changes, so nothing else changes: the truck's <c>truck_manual</c> (left stick <c>stick.y</c> throttle, right
    /// stick <c>stick.x</c> steering) and the drone's <c>drone_manual</c> in mode 2 (left <c>stick2</c>: climb and yaw;
    /// right <c>stick</c>: pitch and roll). <see cref="DroneBody.SticksActive"/> and the autopilots' hand-back see it like any
    /// other stick. While it is in use the air-pinch inputs stand down (<see cref="HandsTransmitter.TransmitterInUse"/>).
    /// No allocations per frame: strings are rebuilt only when what they say changes, and the rig is rescanned (which
    /// allocates) only while a hand or a Thing is missing, every 2 s.
    /// </summary>
    public sealed partial class RcTransmitter : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        public enum Target { None, Truck, Drone, OtherTruck }

        /// <summary>The transmitter, once installed (null in checks and batch mode).</summary>
        public static RcTransmitter Instance { get; private set; }

        // ---- Tuning: the first constants to try on a headset -----------------------------------------------------------

        /// <summary>Thumb travel across the face, from where it touched down, for a full stick (m).</summary>
        public const float StickTravel = 0.018f;
        /// <summary>Radial dead zone, as a fraction of <see cref="StickTravel"/>: a resting thumb's jitter stays inside it.</summary>
        public const float StickDeadzone = 0.16f;
        /// <summary>A thumb that lands off-centre counts as centred up to this far from the stick's middle (m).</summary>
        public const float AnchorMaxOff = 0.008f;
        /// <summary>A fingertip takes a stick within this of its cap across the face (m), and lets go past <see cref="ReleaseRadius"/>.</summary>
        public const float CaptureRadius = 0.022f, ReleaseRadius = 0.04f;
        /// <summary>A fingertip takes a stick no higher than this above the cap's top (m), and lets go above <see cref="ReleaseHeight"/>.</summary>
        public const float EngageHeight = 0.016f, ReleaseHeight = 0.032f;
        /// <summary>Time constant of the stick's low-pass (s).</summary>
        public const float StickSmoothing = 0.05f;
        /// <summary>A released stick takes this long to come home from full (s).</summary>
        public const float SpringSeconds = 0.10f;
        /// <summary>The cap's tilt at full stick, degrees (what you see).</summary>
        public const float StickTiltDegrees = 24f;
        /// <summary>Tilt control (one hand): dead zone and full tilt from where it settled in the hand, degrees.</summary>
        public const float TiltDead = 12f, TiltFull = 35f, TiltSettleSeconds = 0.35f;
        /// <summary>Mouse: pixels of drag for a full stick.</summary>
        public const float MousePixels = 140f;
        /// <summary>A written value that moves less than this is not written again.</summary>
        const float WriteEpsilon = 0.004f;

        /// <summary>One-hand tilt fallback (the stick the holding thumb can't reach follows the tilt). On by default.</summary>
        public static bool TiltFallback = true;
        /// <summary>The drone's easy steering (headless: forward is away from you). The Drive window's switch and the transmitter's share it.</summary>
        public static bool EasySteering = true;
        /// <summary>The truck at full throttle; off holds the hand speed the brain trains at (as the W key and the Drive pad do).</summary>
        public static bool FullSpeed;

        static readonly int StickX = ManualInputs.IndexOf("stick.x"), StickY = ManualInputs.IndexOf("stick.y"),
                            Stick2X = ManualInputs.IndexOf("stick2.x"), Stick2Y = ManualInputs.IndexOf("stick2.y");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
            TiltFallback = true;
            EasySteering = true;
            FullSpeed = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Application.isBatchMode) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Ensure();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Ensure();

        /// <summary>The transmitter, made if there is none yet (it lives across scene loads).</summary>
        public static RcTransmitter Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("RC Transmitter");
            DontDestroyOnLoad(go);
            return go.AddComponent<RcTransmitter>();
        }

        // ---- State -------------------------------------------------------------------------------------------------------

        Target target = Target.None;
        SlalomBrain brain;
        float nextPoll, nextBrainScan;
        bool shown;

        // The two sticks: 0 left, 1 right. Values are x right, y forward, -1..1.
        readonly Vector2[] stick = new Vector2[2];
        readonly Vector2[] raw = new Vector2[2];
        readonly bool[] driven = new bool[2];
        readonly float[] written = new float[ManualInputs.Names.Length];

        // Mouse.
        bool mouseHeld;
        int mouseStick = -1;
        Vector2 mouseAccum;
        readonly bool[] mousePressed = new bool[3];

        // Put down: a short glide onto a surface, or nothing (it floats where it was let go).
        bool gliding;
        float glideT;
        Vector3 glideFrom, glideTo;
        Quaternion glideFromRot, glideToRot;
        const float GlideSeconds = 0.25f;
        float appear = 1f;  // the model's scale while it pops in

        // Flash: a line on the screen for a moment after a button.
        string flash;
        float flashUntil;
        int flashId;

        /// <summary>True while it is shown in the room.</summary>
        public bool Shown => shown;
        /// <summary>What it drives right now.</summary>
        public Target Driving => target;
        /// <summary>True while a hand or the mouse holds it.</summary>
        public bool Held => HeldMask() != 0 || mouseHeld;
        /// <summary>Who holds it, for a status line: "both hands", "left hand", "right hand", "mouse", or null.</summary>
        public string HeldBy
        {
            get
            {
                int m = HeldMask();
                if (m == 3) return "both hands";
                if (m == 1) return "left hand";
                if (m == 2) return "right hand";
                return mouseHeld ? "mouse" : null;
            }
        }
        /// <summary>A stick's value (0 left, 1 right): x right, y forward.</summary>
        public Vector2 Stick(int index) => stick[index];

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            for (int h = 0; h < 2; h++) hands[h].rot = hands[h].offRot = Quaternion.identity;
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            ZeroWritten();
            HandsTransmitter.TransmitterInUse = false;
            Instance = null;
        }

        // ---- Show, hide, summon ------------------------------------------------------------------------------------------

        /// <summary>
        /// Shows it within reach: between your hands when both are tracked, else in front of you just below eye level, face
        /// tilted towards you. Already shown and not held: it comes to you again. Returns where it went, in plain words.
        /// </summary>
        public string Show()
        {
            BuildIfNeeded();
            Target now = FindTarget();
            if (now != target) { ZeroWritten(); target = now; labelKey = int.MinValue; }
            bool wasShown = shown;
            shown = true;
            if (model != null && !model.activeSelf) model.SetActive(true);
            if (Held) return "it is already held (" + HeldBy + ")";
            var cam = Camera.main;
            if (cam == null) return "shown";
            Transform head = cam.transform;
            string where;
            Vector3 at;
            if (PalmsBetween(out Vector3 between))
            {
                at = between + Vector3.up * 0.03f;
                where = "between your hands";
            }
            else
            {
                Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                if (flat.sqrMagnitude < 1e-4f) flat = head.up;
                flat.Normalize();
                at = head.position + flat * 0.36f + Vector3.down * 0.22f;
                where = "in front of you";
            }
            Quaternion facing = FacePose(at, head.position, head.right);
            if (!wasShown)
            {
                transform.SetPositionAndRotation(at, facing);
                measurePos = at;
                measureRot = facing;
                appear = 0f;
                model.transform.localScale = Vector3.one * 0.01f;  // it pops in (Update)
            }
            else StartGlide(at, facing);
            Debug.Log("[Transmitter] shown " + where);
            return where;
        }

        /// <summary>Puts it away: let go of, sticks centred (the Thing coasts or hovers), hidden.</summary>
        public void Hide()
        {
            ReleaseAll();
            mouseHeld = false;
            CentreSticks();
            shown = false;
            gliding = false;
            if (model != null) model.SetActive(false);
            HandsTransmitter.TransmitterInUse = false;
            Debug.Log("[Transmitter] hidden");
        }

        /// <summary>Lets go of both sticks at once and writes zero (the Drive window's Stop). A thumb still on a stick takes it again, centred.</summary>
        public void CentreSticks()
        {
            for (int s = 0; s < 2; s++)
            {
                owner[s] = -1;
                stick[s] = raw[s] = Vector2.zero;
                driven[s] = false;
            }
            mouseStick = -1;
            ZeroWritten();
            PlaceSticks();
        }

        // Rotation with the face (local +y) towards the eye, a little flatter than straight at it, antenna away.
        static Quaternion FacePose(Vector3 at, Vector3 eye, Vector3 eyeRight)
        {
            Vector3 up = eye - at;
            if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
            up = (up.normalized + Vector3.up * 0.6f).normalized;
            Vector3 right = eyeRight - Vector3.Dot(eyeRight, up) * up;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.Cross(up, Vector3.forward);
            right.Normalize();
            return Quaternion.LookRotation(Vector3.Cross(right, up), up);
        }

        void StartGlide(Vector3 to, Quaternion toRot)
        {
            gliding = true;
            glideT = 0f;
            glideFrom = transform.position;
            glideFromRot = transform.rotation;
            glideTo = to;
            glideToRot = toRot;
        }

        // Let go: settle onto a surface just below (a table, the desk, the floor close under it), else float.
        void PutDown()
        {
            HandsTransmitter.TransmitterInUse = false;
            if (FindRest(out Vector3 rest, out Quaternion flat)) StartGlide(rest, flat);
        }

        static readonly RaycastHit[] restHits = new RaycastHit[8];
        const float RestHeight = 0.08f;      // the grips' lowest point under the body's centre
        const float RestSearch = 0.30f;      // how far below it looks for something to lie on

        bool FindRest(out Vector3 rest, out Quaternion flat)
        {
            rest = default; flat = default;
            Vector3 from = transform.position + Vector3.up * 0.02f;
            int n = Physics.RaycastNonAlloc(from, Vector3.down, restHits, RestSearch + RestHeight, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                var h = restHits[i];
                // Hands and moving things have a rigidbody; tables, the desk and the room's mesh don't.
                if (h.collider == null || h.collider.attachedRigidbody != null || h.normal.y < 0.7f) continue;
                if (h.distance < best) { best = h.distance; rest = h.point + Vector3.up * RestHeight; }
            }
            if (float.IsPositiveInfinity(best)) return false;
            Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(-transform.up, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            flat = Quaternion.LookRotation(forward.normalized, Vector3.up);
            return true;
        }

        // ---- The frame ------------------------------------------------------------------------------------------------------

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            float now = Time.unscaledTime;
            if (now >= nextPoll) { nextPoll = now + 0.25f; Poll(); }
            if (!shown || model == null) return;

            ReadHands(dt);
            FollowPose(dt);
            Fingers(now);
            MouseStick();
            StepSticks(dt);
            WriteChannels();
            HandsTransmitter.TransmitterInUse = Held || owner[0] >= 0 || owner[1] >= 0 || mouseStick >= 0;
            PlaceSticks();
            PlaceButtons();
            Led(now);
            UpdateScreen(now);
            if (appear < 1f)
            {
                appear = Mathf.Min(1f, appear + dt / 0.2f);
                float e = 1f - (1f - appear) * (1f - appear);
                model.transform.localScale = Vector3.one * Mathf.Max(0.01f, e);
            }
        }

        // Where fingertips are measured from: the hands' own idea of the pose while held (unsmoothed, so moving the hands
        // quickly never reads as a thumb pushing a stick), else the pose it is drawn at.
        Vector3 measurePos;
        Quaternion measureRot = Quaternion.identity;

        Vector3 Local(Vector3 world) => Quaternion.Inverse(measureRot) * (world - measurePos);

        void FollowPose(float dt)
        {
            float k = 1f - Mathf.Exp(-dt / PoseSmoothing);
            if (HeldMask() != 0 && HandTarget(out Vector3 p, out Quaternion r))
            {
                gliding = false;
                measurePos = p;
                measureRot = r;
                transform.SetPositionAndRotation(Vector3.Lerp(transform.position, p, k), Quaternion.Slerp(transform.rotation, r, k));
                return;
            }
            FollowFree(dt, k);
            measurePos = transform.position;
            measureRot = transform.rotation;
        }

        void FollowFree(float dt, float k)
        {
            if (mouseHeld)
            {
                var cam = Camera.main;
                if (cam == null) return;
                Transform head = cam.transform;
                Vector3 at = head.position + head.forward * 0.34f - head.up * 0.15f;
                gliding = false;
                transform.SetPositionAndRotation(Vector3.Lerp(transform.position, at, k),
                                                 Quaternion.Slerp(transform.rotation, FacePose(at, head.position, head.right), k));
                return;
            }
            if (!gliding) return;
            glideT = Mathf.Min(1f, glideT + dt / GlideSeconds);
            float e = 1f - (1f - glideT) * (1f - glideT) * (1f - glideT);
            transform.SetPositionAndRotation(Vector3.Lerp(glideFrom, glideTo, e), Quaternion.Slerp(glideFromRot, glideToRot, e));
            if (glideT >= 1f) gliding = false;
        }

        // ---- What it drives ----------------------------------------------------------------------------------------------

        void Poll()
        {
            Target now = FindTarget();
            if (now == target) return;
            Target was = target;
            ZeroWritten();
            target = now;
            labelKey = int.MinValue;
            if (now != Target.None && !Held && Application.isPlaying)
            {
                // A Thing just spawned on a course (or another took its place): the transmitter comes to you.
                Show();
            }
            else if (now == Target.None && was != Target.None && shown && !Held) Hide();
        }

        Target FindTarget()
        {
            if (ArtifactDrone.Live != null) return Target.Drone;
            if (brain == null && Time.unscaledTime >= nextBrainScan)
            {
                nextBrainScan = Time.unscaledTime + 2f;  // allocates a little; only while there is no truck
                brain = FindFirstObjectByType<SlalomBrain>();
            }
            // The brain truck stays registered as "truck" while it is hidden (another course is up), so it decides alone.
            if (brain != null) return brain.isActiveAndEnabled && !brain.Hidden ? Target.Truck : Target.None;
            // Another truck running a controller (the Controllers sandbox, a Thing runtime truck).
            return ThingHosts.Find("truck") != null ? Target.OtherTruck : Target.None;
        }

        // The brain truck holds a hand's speed unless the switch says full (OSDriveWindow.HandThrottle, the same number).
        float TruckThrottle => target == Target.Truck && !FullSpeed ? SlalomBrain.KeySpeed * MockTruckSim.Drag / MockTruckSim.MaxAccel : 1f;

        void WriteChannels()
        {
            switch (target)
            {
                case Target.Drone:
                    // Mode 2, as drone_manual: the right stick is stick (forward, sideways), the left is stick2 (climb, turn).
                    Put(StickX, stick[1].x);
                    Put(StickY, stick[1].y);
                    Put(Stick2X, stick[0].x);
                    Put(Stick2Y, stick[0].y);
                    break;
                case Target.Truck:
                case Target.OtherTruck:
                    // truck_manual: stick.y is the throttle (left stick), stick.x steers (right stick, + right).
                    Put(StickY, stick[0].y * TruckThrottle);
                    Put(StickX, stick[1].x);
                    break;
            }
        }

        void Put(int channel, float value)
        {
            if (channel < 0) return;
            value = Mathf.Clamp(value, -1f, 1f);
            if (!TransmitterMath.Changed(written[channel], value, WriteEpsilon)) return;
            ManualInputs.Values[channel] = value;
            written[channel] = value;
        }

        // Every channel this wrote that is not zero gets zero, once (a target change, a hide, Stop).
        void ZeroWritten()
        {
            for (int i = 0; i < written.Length; i++)
            {
                if (written[i] == 0f) continue;
                ManualInputs.Values[i] = 0f;
                written[i] = 0f;
            }
        }

        void StepSticks(float dt)
        {
            for (int s = 0; s < 2; s++)
            {
                Vector2 v = stick[s];
                if (driven[s])
                {
                    v.x = TransmitterMath.Snap(TransmitterMath.Smooth(v.x, raw[s].x, dt, StickSmoothing), 0.01f);
                    v.y = TransmitterMath.Snap(TransmitterMath.Smooth(v.y, raw[s].y, dt, StickSmoothing), 0.01f);
                    if (raw[s].x == 0f && Mathf.Abs(v.x) < 0.03f) v.x = 0f;
                    if (raw[s].y == 0f && Mathf.Abs(v.y) < 0.03f) v.y = 0f;
                }
                else
                {
                    v.x = TransmitterMath.Spring(v.x, dt, SpringSeconds);
                    v.y = TransmitterMath.Spring(v.y, dt, SpringSeconds);
                }
                stick[s] = v;
            }
        }

        // ---- The buttons and the switch ----------------------------------------------------------------------------------

        const int ButtonA = 0, ButtonB = 1, ModeSwitch = 2;

        void Press(int control)
        {
            string said = control == ModeSwitch ? FlipMode() : control == ButtonA ? PressA() : PressB();
            if (said == null) return;
            flash = said;
            flashUntil = Time.unscaledTime + 2.2f;
            flashId++;
            Debug.Log("[Transmitter] " + said);
        }

        string FlipMode()
        {
            if (target == Target.Drone)
            {
                EasySteering = !EasySteering;
                if (DroneBody.Active != null) DroneBody.Active.headless = EasySteering;
                return EasySteering ? "EASY: AWAY IS FORWARD" : "PRO: NOSE IS FORWARD";
            }
            FullSpeed = !FullSpeed;
            return FullSpeed ? "FULL SPEED" : "TRAINING SPEED";
        }

        string PressA()
        {
            switch (target)
            {
                case Target.Drone:
                    var drone = ArtifactDrone.Live;
                    if (drone == null) return null;
                    var phase = drone.Body.State;
                    if (phase == DroneBody.Phase.Crashed) return "CRASHED: PRESS RESET";
                    if (phase == DroneBody.Phase.Landed || phase == DroneBody.Phase.Landing)
                    {
                        drone.Body.headless = EasySteering;
                        string error = drone.TakeOff();
                        return error != null ? Short(error) : "TAKING OFF";
                    }
                    drone.Land();
                    return "LANDING";
                case Target.Truck:
                case Target.OtherTruck:
                    Honk();
                    return "BEEP BEEP";
                default:
                    return "NO THING TO DRIVE";
            }
        }

        string PressB()
        {
            switch (target)
            {
                case Target.Drone:
                    var drone = ArtifactDrone.Live;
                    if (drone == null) return null;
                    var phase = drone.Body.State;
                    if (phase == DroneBody.Phase.Flying && !drone.Body.Returning)
                    {
                        string error = drone.Land(true);
                        return error != null ? Short(error) : "FLYING HOME";
                    }
                    if (phase == DroneBody.Phase.Crashed || phase == DroneBody.Phase.Landed)
                    {
                        CentreSticks();
                        drone.ResetToStart();
                        return "BACK AT THE START";
                    }
                    drone.Stop();
                    return "HOVERING";
                case Target.Truck:
                    if (brain == null) return null;
                    CentreSticks();
                    if (brain.State == SlalomBrain.Mode.Lap) brain.StopRecording();
                    else if (brain.State == SlalomBrain.Mode.Autopilot) brain.StopAutopilot();
                    brain.PlaceAtStart();
                    return "BACK AT THE START";
                case Target.OtherTruck:
                    CentreSticks();
                    return "STICKS CENTRED";
                default:
                    return "NO THING TO DRIVE";
            }
        }

        // A tool error ("not flying: take off first") as a screen line.
        static string Short(string error)
        {
            int colon = error.IndexOf(':');
            string head = colon > 0 ? error.Substring(0, colon) : error;
            return head.Replace('_', ' ').ToUpperInvariant();
        }

        void Honk()
        {
            if (horn == null) return;
            Vector3 at = transform.position;
            if (brain != null && target == Target.Truck) at = brain.transform.position;
            horn.transform.position = at;
            horn.Play();
        }

        // ---- Mouse (DesktopRig, or a mouse in the Editor): TransmitterRaycaster puts the parts under the pointer ---------

        public void OnPointerDown(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left || model == null || !model.activeSelf) return;
            GameObject part = e.pointerPressRaycast.gameObject != null ? e.pointerPressRaycast.gameObject : e.pointerCurrentRaycast.gameObject;
            for (int s = 0; s < 2; s++)
            {
                if (part != capCollider[s]) continue;
                mouseStick = s;
                mouseAccum = Vector2.zero;
                return;
            }
            for (int c = 0; c < 3; c++)
            {
                if (part != controlCollider[c]) continue;
                mousePressed[c] = true;
                Press(c);
                return;
            }
            // The body: take it in front of the camera, or put it down again.
            if (HeldMask() != 0) return;
            if (mouseHeld) { mouseHeld = false; PutDown(); }
            else { mouseHeld = true; gliding = false; }
        }

        public void OnPointerUp(PointerEventData e)
        {
            mouseStick = -1;
            for (int c = 0; c < 3; c++) mousePressed[c] = false;
        }

        void MouseStick()
        {
            var mouse = Mouse.current;
            if (mouseStick < 0) return;
            if (mouse == null || !mouse.leftButton.isPressed) { mouseStick = -1; return; }  // an up that never came
            mouseAccum += mouse.delta.ReadValue();
            Vector2 v = mouseAccum / MousePixels;
            if (v.sqrMagnitude > 1f) v.Normalize();
            // A hand on that stick wins.
            if (owner[mouseStick] >= 0) return;
            raw[mouseStick] = v;
            driven[mouseStick] = true;
        }
    }
}
