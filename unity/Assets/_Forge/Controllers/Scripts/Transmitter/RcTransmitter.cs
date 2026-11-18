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
    /// Docked (the default, <see cref="Docked"/>): it waits on a fixed spot instead of in the hands, on the table in front of
    /// you or floating at lap height about 40 cm ahead, and doesn't move with hand jitter. Rest a thumb or an index finger
    /// on each stick: the nearest tip within a generous zone takes it, centred where it touched, and its movement across the
    /// face from there is the stick (dead zone, smoothing, the cap tilts to show it); lift the finger and the stick centres.
    /// Pinch the body (away from the sticks and buttons) and move to put the dock somewhere else. Close a hand round it and
    /// lift to hold it (handheld); let go and it goes back to its dock. The Drive window's switch turns docking off.
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
        /// <summary>The truck at full throttle; off holds the training speed the brain trains at (<see cref="DriveAids.TrainingSpeed"/>).</summary>
        public static bool FullSpeed;
        /// <summary>Docked: it waits on a fixed spot and the fingertips work the sticks (see the class notes). On by default.</summary>
        public static bool Docked = true;

        /// <summary>The dock: this far ahead of the head (m, level) and this far below it, tilted this much towards you when floating.</summary>
        public const float DockAhead = 0.40f, DockBelow = 0.42f, DockTiltDegrees = 30f;
        /// <summary>A table top this far below the head (m) or less, and no nearer than <see cref="DockTableMinBelow"/>, takes the dock.</summary>
        public const float DockTableMaxBelow = 0.9f, DockTableMinBelow = 0.2f;

        static readonly int StickX = ManualInputs.IndexOf("stick.x"), StickY = ManualInputs.IndexOf("stick.y"),
                            Stick2X = ManualInputs.IndexOf("stick2.x"), Stick2Y = ManualInputs.IndexOf("stick2.y");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
            TiltFallback = true;
            EasySteering = true;
            FullSpeed = false;
            Docked = true;
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
            if (Docked)
            {
                DockPose(head, out at, out Quaternion dockRot, out where);
                if (!wasShown)
                {
                    transform.SetPositionAndRotation(at, dockRot);
                    measurePos = at;
                    measureRot = dockRot;
                    appear = 0f;
                    model.transform.localScale = Vector3.one * 0.01f;
                }
                else StartGlide(at, dockRot);
                Debug.Log("[Transmitter] docked " + where);
                return where;
            }
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
            StopFreeDrive();
            DriveAids.EndCruise();
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
                // A locked thumb lets go: it takes the stick again once it is back where it rested (re-centred there).
                if (locks[s].Locked) { locks[s].Locked = false; locks[s].LastX = locks[s].LastY = 0f; }
            }
            mouseStick = -1;
            DriveAids.EndCruise();
            ZeroWritten();
            PlaceSticks();
        }

        // ---- The dock -------------------------------------------------------------------------------------------------

        bool dockSet;
        Vector3 dockPos;
        Quaternion dockRot = Quaternion.identity;

        /// <summary>
        /// Docks it (on) or lets it be held and put down anywhere (off). Docking lets go of it, centres the sticks and
        /// sends it to its dock in front of you. Returns where it went, or null when it was only switched off.
        /// </summary>
        public string SetDocked(bool on)
        {
            Docked = on;
            liftHand = -1;
            if (dragHand >= 0) dragHand = -1;
            if (!on) { labelKey = int.MinValue; return null; }
            ReleaseAll();
            mouseHeld = false;
            CentreSticks();
            HandsTransmitter.TransmitterInUse = false;
            return shown ? Show() : null;
        }

        /// <summary>Forgets where the dock was moved to: the next show docks it in front of you again.</summary>
        public void ResetDock() => dockSet = false;

        // Where it docks: where it was last put (a pinch-drag) if that is still near you, else on the table in front of you
        // (a surface 20-90 cm under your head, about 40 cm ahead), else floating at lap height, tilted towards you.
        void DockPose(Transform head, out Vector3 at, out Quaternion rot, out string where)
        {
            if (dockSet && (dockPos - head.position).sqrMagnitude < 1.2f * 1.2f)
            {
                at = dockPos; rot = dockRot; where = "where you docked it";
                return;
            }
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.ProjectOnPlane(head.up, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            flat.Normalize();
            Vector3 ahead = head.position + flat * DockAhead;
            if (FindSurface(ahead + Vector3.up * 0.05f, DockTableMaxBelow + 0.05f, out Vector3 top) && head.position.y - top.y >= DockTableMinBelow)
            {
                // On a desk course the table is the course: the dock hovers a little above it, at its near edge.
                at = top + Vector3.up * (RestHeight + (OnDeskCourse ? 0.05f : 0f));
                rot = Quaternion.LookRotation(flat, Vector3.up) * Quaternion.Euler(OnDeskCourse ? -DockTiltDegrees * 0.5f : 0f, 0f, 0f);
                where = OnDeskCourse ? "docked just above the table in front of you" : "docked on the table in front of you";
                return;
            }
            at = ahead + Vector3.down * DockBelow;
            rot = Quaternion.LookRotation(flat, Vector3.up) * Quaternion.Euler(-DockTiltDegrees, 0f, 0f);
            where = "docked in front of you at lap height";
        }

        bool OnDeskCourse => target == Target.Truck && brain != null && brain.CourseScale < 0.99f;

        // A pinch-drag ended (or a docked one was let go): the dock is here; settled onto a surface just under it, and
        // turned to face you.
        void SetDockHere()
        {
            var cam = Camera.main;
            Vector3 at = transform.position;
            Vector3 flat = cam != null ? Vector3.ProjectOnPlane(at - cam.transform.position, Vector3.up) : Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            flat.Normalize();
            Quaternion rot;
            if (FindSurface(at + Vector3.up * 0.02f, RestHeight + 0.10f, out Vector3 top))
            {
                at = top + Vector3.up * RestHeight;
                rot = Quaternion.LookRotation(flat, Vector3.up);
            }
            else rot = Quaternion.LookRotation(flat, Vector3.up) * Quaternion.Euler(-DockTiltDegrees, 0f, 0f);
            dockPos = at; dockRot = rot; dockSet = true;
            StartGlide(at, rot);
            Debug.Log("[Transmitter] dock moved");
        }

        // The nearest surface to lie on straight under a point, within depth (a table, the desk, the floor): hands and moving
        // things have a rigidbody, so they are skipped, as are slopes.
        bool FindSurface(Vector3 from, float depth, out Vector3 top)
        {
            top = default;
            int n = Physics.RaycastNonAlloc(from, Vector3.down, restHits, depth, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                var h = restHits[i];
                if (h.collider == null || h.collider.attachedRigidbody != null || h.normal.y < 0.7f) continue;
                if (h.distance < best) { best = h.distance; top = h.point; }
            }
            return !float.IsPositiveInfinity(best);
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
            HoverOnLetGo();
            // Docked: back to its dock (it was lifted off it to be held).
            if (Docked)
            {
                var cam = Camera.main;
                if (cam != null) { DockPose(cam.transform, out Vector3 at, out Quaternion rot, out _); StartGlide(at, rot); }
                return;
            }
            if (FindRest(out Vector3 rest, out Quaternion flat)) StartGlide(rest, flat);
        }

        // Let go of while flying the drone by hand: the sticks centre (Update) and it brakes to a hover where it is.
        // Not while it flies itself (follow-me, flying home): that is the drone's, and the transmitter didn't move it.
        void HoverOnLetGo()
        {
            if (target != Target.Drone || !flownByHand) return;
            flownByHand = false;
            var drone = ArtifactDrone.Live;
            if (drone == null || drone.Body.State != DroneBody.Phase.Flying || drone.Body.Returning) return;
            if (drone.Follower != null && drone.Follower.Active) return;
            drone.Body.Hold();
        }

        // ---- Making it obviously do something ----------------------------------------------------------------------------

        bool flownByHand;      // the transmitter wrote a non-zero stick to the drone since it was picked up
        float stickUpFor;      // the left stick held up while the drone is landed (s): a take-off
        bool stickUpFired;
        float nextSay;
        string lastSaid;
        const float SayGapSeconds = 30f;

        // Picked up (from nobody holding it): when there is nothing it can drive yet, the screen already says what to
        // do (UpdateScreen); Albie says it too, once in a while.
        void PickedUp()
        {
            flownByHand = false;
            string line = null;
            if (target == Target.None) line = "Open a course to drive the truck.";
            else if (target == Target.Drone)
            {
                var drone = ArtifactDrone.Live;
                if (drone != null && drone.Body.State == DroneBody.Phase.Landed) line = "Tap Take off, or push the left stick up.";
                else if (drone != null && drone.Body.State == DroneBody.Phase.Crashed) line = "It crashed. Press Reset on the transmitter.";
            }
            else if (target == Target.Truck && brain != null)
            {
                if (brain.State == SlalomBrain.Mode.Autopilot) line = "The Autopilot is driving. Press Reset to take the truck back.";
                else if (brain.State == SlalomBrain.Mode.Training) line = "I'm training on your laps. The truck is yours again in a moment.";
                else if (brain.Body != null && brain.Body.Crashed) line = "The truck crashed. Press Reset on the transmitter.";
            }
            if (line == null) return;
            float now = Time.unscaledTime;
            if (line == lastSaid && now < nextSay) return;
            lastSaid = line;
            nextSay = now + SayGapSeconds;
            FirstRunGuide.Say(line);
        }

        // The drone, landed: pushing the climb stick (left) up for a moment takes off, as A does.
        void StickTakeOff(float dt)
        {
            bool inHand = HeldMask() != 0 || (Docked && owner[0] >= 0);
            var drone = target == Target.Drone && inHand ? ArtifactDrone.Live : null;
            if (drone == null || drone.Body.State != DroneBody.Phase.Landed || stick[0].y < 0.8f)
            {
                stickUpFor = 0f;
                if (stick[0].y < 0.3f) stickUpFired = false;
                return;
            }
            stickUpFor += dt;
            if (stickUpFor < 0.35f || stickUpFired) return;
            stickUpFired = true;
            Flash(PressA());
        }

        // ---- Free driving: the brain truck between laps ------------------------------------------------------------------

        // SlalomBrain parks the truck between runs (its ControllerThing stops ticking), so between laps the sticks moved
        // nothing. Held with the brain idle, the transmitter ticks it itself on the manual controller: it drives, it is not
        // recorded (the lap recorder records only between BeginLap and EndLap) and no run is armed, so nothing is scored.
        // A lap, training or the Autopilot take the truck over (State is not Idle) and this stands down without touching
        // it. Let go, it keeps ticking until the truck has coasted to a stop, then parks it again.
        bool freeDriving;
        ControllerFile manualFile;

        // SlalomBrain drives the truck freely between laps itself when its FreeDrive is on (its FreeStep stops the truck
        // where it touches a cone or the table edge); the transmitter then only writes the sticks and stays out of it.
        bool CanFreeDrive => target == Target.Truck && brain != null && brain.isActiveAndEnabled && !brain.Hidden && !brain.FreeDrive
                             && brain.State == SlalomBrain.Mode.Idle && brain.Thing != null && brain.Thing.Host != null;

        /// <summary>True while the transmitter drives the brain truck between laps (not recorded).</summary>
        public bool FreeDriving => freeDriving;

        void FreeDrive()
        {
            bool can = CanFreeDrive;
            if (!can) { if (freeDriving) freeDriving = false; return; }  // a lap or the Autopilot has it now: hands off
            bool moving = brain.Body != null && !brain.Body.Crashed && Mathf.Abs(brain.Body.Speed) > 0.02f;
            bool want = Held || stick[0] != Vector2.zero || stick[1] != Vector2.zero || (freeDriving && moving);
            if (want == freeDriving) return;
            if (want)
            {
                // After an Autopilot run the winner stays attached: the hands drive on the manual controller.
                var host = brain.Thing.Host;
                if (host.File == null || host.File.Kind != "manual")
                {
                    if (manualFile == null)
                    {
                        var asset = Resources.Load<TextAsset>("Mocks/controllers/truck_manual");
                        manualFile = asset != null ? ControllerFile.Parse(asset.text, out _) : null;
                    }
                    if (manualFile == null || host.Attach(manualFile) != null) return;
                }
                brain.Thing.tickInFixedUpdate = true;
                freeDriving = true;
            }
            else StopFreeDrive();
        }

        void StopFreeDrive()
        {
            if (!freeDriving) return;
            freeDriving = false;
            if (CanFreeDrive) brain.Thing.tickInFixedUpdate = false;  // parked again, as SlalomBrain leaves it
        }

        // ---- Feel: a click and a little seat when a thumb locks, a tick at the end of a stick's travel ------------------

        AudioSource clicker;
        AudioClip lockClip, endClip;
        readonly float[] seatUntil = new float[2];
        float ledFlashUntil;
        const float SeatSeconds = 0.12f;

        void Seat(int s)
        {
            float until = Time.unscaledTime + SeatSeconds;
            seatUntil[s] = until;
            ledFlashUntil = until;
        }

        void Click(bool end)
        {
            if (clicker == null) return;
            clicker.PlayOneShot(end ? endClip : lockClip, end ? 0.35f : 0.6f);
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
            StickTakeOff(dt);
            WriteChannels();
            FreeDrive();
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
                // Drawn steadier than it is measured: the sticks are read in each hand's own frame (LocalToHand).
                float kh = 1f - Mathf.Exp(-dt / HeldPoseSmoothing);
                transform.SetPositionAndRotation(Vector3.Lerp(transform.position, p, kh), Quaternion.Slerp(transform.rotation, r, kh));
                return;
            }
            if (dragHand >= 0)
            {
                // A pinch moving the dock: it follows the pinch, keeping its turn.
                gliding = false;
                transform.position = Vector3.Lerp(transform.position, dragTo, k);
                measurePos = transform.position;
                measureRot = transform.rotation;
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

        // The brain truck holds the training speed unless the switch says full: the throttle that holds it on the truck's
        // own design and scale (DriveAids.TrainingSpeed), the stock tuning's number while there is no body.
        float TruckThrottle => target == Target.Truck && !FullSpeed
            ? (brain != null && brain.Body != null ? brain.Body.ThrottleFor(DriveAids.TrainingSpeed) : DriveAids.TrainingSpeed * MockTruckSim.Drag / MockTruckSim.MaxAccel)
            : 1f;

        // The steady throttle ends when the truck's lap ends, the Autopilot or training takes over, or it is reset.
        SlalomBrain.Mode lastBrainMode;

        void CruiseGuard()
        {
            if (brain == null) { DriveAids.EndCruise(); return; }
            var mode = brain.State;
            if (mode != lastBrainMode || mode == SlalomBrain.Mode.Autopilot || mode == SlalomBrain.Mode.Training || brain.Hidden) DriveAids.EndCruise();
            lastBrainMode = mode;
        }

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
                    if (stick[0] != Vector2.zero || stick[1] != Vector2.zero) flownByHand = true;
                    break;
                case Target.Truck:
                    // The brain truck, with the driving aids: the training speed cap, the steady throttle, the soft
                    // steering (DriveAids). truck_manual: stick.y is the throttle, stick.x steers (+ right).
                    CruiseGuard();
                    Put(StickY, DriveAids.Throttle(stick[0].y, TruckThrottle, FullSpeed));
                    Put(StickX, FullSpeed ? stick[1].x : DriveAids.Steer(stick[1].x));
                    break;
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
            // Someone cleared the channels (a lap's end, the Drive window's Stop, ManualInputs.Clear): what this wrote is
            // gone, so a stick held still is written again rather than left at zero until it moves.
            if (written[channel] != 0f && ManualInputs.Values[channel] == 0f) written[channel] = 0f;
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
            Flash(control == ModeSwitch ? FlipMode() : control == ButtonA ? PressA() : PressB());
        }

        void Flash(string said)
        {
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
