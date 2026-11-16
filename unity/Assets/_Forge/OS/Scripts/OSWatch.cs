using Oculus.Interaction;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>Why the left hand is, or is not, in the watch pose this frame. <see cref="Ok"/> is the pose.</summary>
    public enum WatchPose
    {
        /// <summary>Back of the hand toward the eyes, wrist in view, at arm's length: the watch pose.</summary>
        Ok,
        /// <summary>The left hand is not tracked (or the engine is not running).</summary>
        NoHand,
        /// <summary>The palm is up: that is the Call Albie gesture, not a watch.</summary>
        PalmUp,
        /// <summary>Thumb and index finger are together: a pinch, which belongs to grabbing.</summary>
        Pinch,
        /// <summary>The wrist is closer to the head than minDistance.</summary>
        TooClose,
        /// <summary>The wrist is farther from the head than maxDistance.</summary>
        TooFar,
        /// <summary>The wrist is lower than maxBelowEyeMeters under the eyes (a hand resting on the desk or in the lap).</summary>
        TooLow,
        /// <summary>The back of the hand does not face the eyes enough.</summary>
        BackAway,
        /// <summary>The wrist is outside the cone around the gaze line.</summary>
        OutOfView,
    }

    /// <summary>
    /// The wrist watch turn (spec 5.11): turn the left wrist as if checking a watch and a small round button appears just
    /// above the wrist; poke it with the right index finger (or ray or mouse: it is an ordinary UI Set button) and the Albie
    /// menu opens, growing from the wrist. It works at a desk and away from one, and is switched by
    /// <see cref="OSSettings.watchTurn"/>. The shell owns it: it is a component of the OSShell object (added by
    /// OSPrefabBuilder.BuildShell, or on demand by <see cref="Ensure"/>).
    /// <para>
    /// It reads the left hand from <c>GestureEngine.Current.Hands[0]</c> (index 0 is the left hand) in LateUpdate, after the
    /// engine has updated it, and never reads a hand source itself. The pose: the hand is tracked; the back of the hand faces
    /// the eyes (the back normal is minus <see cref="HandFeatures.PalmNormal"/>, which is the cross product of wrist to index
    /// knuckle and wrist to pinky knuckle, and is the direction out of the palm for either hand; its dot with the unit
    /// direction from the middle of the hand to the head must be above <see cref="backFacingDot"/>); the wrist is within
    /// <see cref="viewDegrees"/> of the gaze line (the head's forward: there is no eye tracking) and
    /// <see cref="minDistance"/> to <see cref="maxDistance"/> from the head; it is not lower than
    /// <see cref="maxBelowEyeMeters"/> under the eyes; and it is not a palm-up or a pinch. Held for
    /// <see cref="holdSeconds"/> it shows the button. It does not start while a panel is held
    /// (<c>PanelManipulator.Grabbing</c>), while the menu is open, or for <see cref="quietSeconds"/> after the engine fired a
    /// gesture with the left hand.
    /// </para>
    /// <para>
    /// The button hides <see cref="hideSeconds"/> after the pose is lost. After a press it stays away until the wrist has
    /// turned away once (the pose lost for <see cref="hideSeconds"/>). Every threshold is a public field: they need tuning
    /// with real hands on a headset. No allocations per frame.
    /// </para>
    /// </summary>
    public sealed class OSWatch : MonoBehaviour
    {
        [Header("Pose (needs headset tuning)")]
        [Tooltip("The back of the hand faces the eyes: dot of the back-of-hand normal with the direction from the hand to the head must be above this. 1 is square on, 0.6 is about 53 degrees off. Lower it if the watch is hard to trigger, raise it if a hand resting on the desk or in front of the chest triggers it.")]
        [Range(0.2f, 0.95f)] public float backFacingDot = 0.6f;

        [Tooltip("The wrist must be within this many degrees of the gaze line (the way the head faces).")]
        [Range(10f, 80f)] public float viewDegrees = 35f;

        [Tooltip("Closest the wrist may be to the head, in metres.")]
        public float minDistance = 0.2f;

        [Tooltip("Farthest the wrist may be from the head, in metres.")]
        public float maxDistance = 0.7f;

        [Tooltip("Not lower than this many metres under the eyes, so a hand resting on a desk or a lap that is looked at is not a watch. 0 turns the check off. (An addition to the spec, there to stop that false trigger.)")]
        public float maxBelowEyeMeters = 0.42f;

        [Tooltip("How long the pose must be held before the button shows, in seconds.")]
        public float holdSeconds = 0.3f;

        [Tooltip("How long the pose may be lost before the button hides, in seconds. Also how long the wrist must be turned away to re-arm after a press.")]
        public float hideSeconds = 0.25f;

        [Tooltip("Pinch: thumb tip to index tip, in hand lengths (the engine's pinch feature). Below this the hand is pinching and the watch stays off. The engine's own pinch pose is 0.22; a relaxed hand is around 0.4.")]
        public float pinchBelow = 0.26f;

        [Tooltip("Palm up: the engine's palm-up feature (the palm normal's height, 1 = palm straight up). Above this the watch stays off. The engine's Call Albie pose needs 0.6.")]
        [Range(0f, 1f)] public float palmUpAbove = 0.45f;

        [Tooltip("After the engine fires any gesture with the left hand, the watch stays off this many seconds.")]
        public float quietSeconds = 0.8f;

        [Header("Staying on (so the button does not flicker once it is shown)")]
        [Tooltip("While the button is shown, the back-facing dot may drop this much below backFacingDot before the pose counts as lost.")]
        public float keepBackFacingSlack = 0.15f;

        [Tooltip("While the button is shown, the view cone is this many degrees wider.")]
        public float keepViewSlackDegrees = 10f;

        [Header("Button")]
        [Tooltip("Diameter of the round button in millimetres. Never below 22 (the finger-sized minimum).")]
        [Range(22f, 60f)] public float buttonMillimetres = 28f;

        [Tooltip("How far the button floats above the wrist, along the back-of-hand normal, in millimetres.")]
        public float liftMillimetres = 45f;

        [Tooltip("How quickly the button follows the wrist, per second (higher is tighter, 0 is rigid). A little smoothing steadies it for the finger.")]
        public float followSharpness = 25f;

        /// <summary>The smallest button, in millimetres: 22 mm is the finger-sized minimum (OSUi.MinTarget).</summary>
        public const float MinButtonMillimetres = 22f;

        // The backplate around the round button, which is the surface a ray or poke hits: a little larger than the button.
        const float PlatePadMillimetres = 10f;

        OSShell shell;
        GestureEngine hooked;
        GameObject root;
        RectTransform canvasRoot, plate, buttonRect;
        Oculus.Interaction.Samples.RoundedBoxUIProperties plateRound, buttonRound;
        Button button;
        bool built, buildFailed, reported;

        bool shown;
        bool armed = true;
        float heldFor, lostFor, hideFor, quietUntil;
        Vector3 lastWrist, lastBack = Vector3.up;
        bool haveWrist;

        /// <summary>True while the wrist button is up.</summary>
        public bool Shown => shown;
        /// <summary>The wrist button, or null before it is first shown (for checks).</summary>
        public Button Button => button;
        /// <summary>Where the button is in the world (its centre), or zero when it was never built.</summary>
        public Vector3 ButtonPosition => root != null ? root.transform.position : Vector3.zero;
        /// <summary>The button's face direction: its forward points away from the viewer, as every canvas does.</summary>
        public Vector3 ButtonForward => root != null ? root.transform.forward : Vector3.forward;
        /// <summary>The button's diameter in metres (at least 22 mm).</summary>
        public float ButtonMeters => Mathf.Max(MinButtonMillimetres, buttonMillimetres) * 0.001f;
        /// <summary>False after a press until the wrist has turned away once.</summary>
        public bool Armed => armed;
        /// <summary>How long the pose has been held so far (seconds), 0 when it is not.</summary>
        public float HeldSeconds => heldFor;
        /// <summary>How many times the button was pressed since the shell started.</summary>
        public int Presses { get; private set; }
        /// <summary>What the last frame said about the left hand (see <see cref="Evaluate"/>).</summary>
        public WatchPose LastPose { get; private set; } = WatchPose.NoHand;
        /// <summary>The last back-facing dot that <see cref="Evaluate"/> measured. For tuning on the headset.</summary>
        public float LastBackDot { get; private set; }
        /// <summary>The last angle between the gaze line and the wrist, in degrees.</summary>
        public float LastViewDegrees { get; private set; }
        /// <summary>The last distance from the head to the wrist, in metres.</summary>
        public float LastDistance { get; private set; }

        /// <summary>Adds the component to the shell if it has none (when the prefab was not rebuilt yet). Returns it.</summary>
        public static OSWatch Ensure(OSShell shell)
        {
            if (shell == null) return null;
            var watch = shell.GetComponent<OSWatch>();
            return watch != null ? watch : shell.gameObject.AddComponent<OSWatch>();
        }

        void Awake() => shell = GetComponent<OSShell>();

        void OnEnable()
        {
            if (shell == null) shell = GetComponent<OSShell>();
            buildFailed = false;
            reported = false;
            ResetState();
        }

        void OnDisable()
        {
            Unhook();
            Hide();
            ResetState();
        }

        void OnDestroy()
        {
            Unhook();
            if (root != null) Destroy(root);
        }

        void ResetState()
        {
            shown = false;
            armed = true;
            heldFor = lostFor = hideFor = 0f;
            haveWrist = false;
            LastPose = WatchPose.NoHand;
        }

        // ---------------------------------------------------------------- the pose

        /// <summary>
        /// Is this left hand in the watch pose? Reads only the hand's joints and features and the head's pose, so a test can
        /// hand it a made-up hand. <paramref name="keeping"/> is true while the button is already shown (the slack fields
        /// widen the cone). <paramref name="wrist"/> and <paramref name="backNormal"/> are filled whenever the hand is usable
        /// (whatever the result); with <see cref="WatchPose.NoHand"/> they are zero and up. Updates LastBackDot,
        /// LastViewDegrees and LastDistance. No allocations.
        /// </summary>
        public WatchPose Evaluate(GestureHand hand, Vector3 headPosition, Vector3 headForward, bool keeping, out Vector3 wrist, out Vector3 backNormal)
        {
            wrist = Vector3.zero;
            backNormal = Vector3.up;
            if (hand == null || !hand.Usable || !hand.sample.isLeft) return WatchPose.NoHand;

            // HandFeatures.PalmNormal is the direction out of the palm for either hand (it negates the cross product on a
            // right hand), so the back of the hand is its opposite. A zero cross product (a collapsed hand) normalizes to 0.
            Vector3 palmNormal = HandFeatures.PalmNormal(hand.sample);
            if (palmNormal.sqrMagnitude < 0.25f) return WatchPose.NoHand;
            backNormal = -palmNormal;
            wrist = hand.sample.joints[HandJoint.Wrist];

            Vector3 toWrist = wrist - headPosition;
            float distance = toWrist.magnitude;
            LastDistance = distance;

            Vector3 toHead = headPosition - HandFeatures.PalmCenter(hand.sample);
            float toHeadLength = toHead.magnitude;
            LastBackDot = toHeadLength > 1e-4f ? Vector3.Dot(backNormal, toHead / toHeadLength) : 0f;
            LastViewDegrees = distance > 1e-4f ? Vector3.Angle(headForward, toWrist) : 0f;

            if (GestureBlocks(hand.values, out bool palmUp)) return palmUp ? WatchPose.PalmUp : WatchPose.Pinch;
            if (distance < minDistance) return WatchPose.TooClose;
            if (distance > maxDistance) return WatchPose.TooFar;
            if (maxBelowEyeMeters > 0f && headPosition.y - wrist.y > maxBelowEyeMeters) return WatchPose.TooLow;
            if (LastBackDot < (keeping ? backFacingDot - keepBackFacingSlack : backFacingDot)) return WatchPose.BackAway;
            if (LastViewDegrees > (keeping ? viewDegrees + keepViewSlackDegrees : viewDegrees)) return WatchPose.OutOfView;
            return WatchPose.Ok;
        }

        /// <summary>
        /// True when the hand's features say palm up (<see cref="palmUpAbove"/>, wider than the engine's palm_up pose) or a
        /// pinch (<see cref="pinchBelow"/>, wider than the engine's pinch pose). <paramref name="palmUp"/> says which.
        /// </summary>
        public bool GestureBlocks(float[] values, out bool palmUp)
        {
            palmUp = values[(int)HandFeature.PalmUp] > palmUpAbove;
            if (palmUp) return true;
            return values[(int)HandFeature.Pinch] < pinchBelow;
        }

        /// <summary>The same, without saying which (for tests).</summary>
        public bool GestureBlocks(float[] values) => GestureBlocks(values, out _);

        // ---------------------------------------------------------------- every frame

        void LateUpdate()
        {
            if (shell == null) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            OSSettings settings = OSSettings.Current;

            GestureEngine engine = GestureEngine.Current;
            Hook(engine);

            if (!settings.watchTurn)
            {
                // Switched off: nothing shows, and nothing counts toward the next time.
                if (shown) Hide();
                heldFor = lostFor = hideFor = 0f;
                LastPose = WatchPose.NoHand;
                return;
            }

            Transform head = shell.Head;
            Vector3 wrist = Vector3.zero, back = Vector3.up;
            WatchPose pose = WatchPose.NoHand;
            if (head != null && engine != null)
                pose = Evaluate(engine.Hands[0], head.position, head.forward, shown, out wrist, out back);
            LastPose = pose;
            bool ok = pose == WatchPose.Ok;
            if (pose != WatchPose.NoHand)
            {
                lastWrist = wrist;
                lastBack = back;
                haveWrist = true;
            }

            // The wrist has to turn away once after a press: the pose lost for hideSeconds (the menu being open is not that).
            if (ok)
            {
                lostFor = 0f;
            }
            else
            {
                lostFor += dt;
                if (lostFor >= hideSeconds) armed = true;
            }

            bool blocked = shell.MenuOpen || PanelManipulator.Grabbing > 0 || Time.unscaledTime < quietUntil;

            if (shown)
            {
                if (blocked)
                {
                    // The menu opened some other way, a panel is being dragged, or a gesture just fired.
                    Hide();
                    heldFor = 0f;
                    return;
                }
                if (ok) hideFor = 0f;
                else
                {
                    hideFor += dt;
                    if (hideFor >= hideSeconds)
                    {
                        Hide();
                        return;
                    }
                }
                if (head != null && haveWrist) Follow(lastWrist, lastBack, head, dt, false);
                return;
            }

            if (ok && !blocked && armed)
            {
                heldFor += dt;
                if (heldFor >= holdSeconds && head != null) Show(wrist, back, head);
            }
            else
            {
                heldFor = 0f;
            }
        }

        void Hook(GestureEngine engine)
        {
            if (hooked == engine) return;
            Unhook();
            hooked = engine;
            if (hooked != null) hooked.Fired += OnGesture;
        }

        void Unhook()
        {
            if (hooked != null) hooked.Fired -= OnGesture;
            hooked = null;
        }

        // A gesture fired with the left hand (palm up, a fist ...): the watch keeps out of its way for a moment.
        void OnGesture(GestureDef def, GestureHand hand)
        {
            if (hand != null && hand.sample.isLeft) quietUntil = Time.unscaledTime + quietSeconds;
        }

        // ---------------------------------------------------------------- the button

        void Show(Vector3 wrist, Vector3 back, Transform head)
        {
            if (!Build())
            {
                if (!reported) Debug.Log("[OS] The wrist button couldn't be built (is Resources/OSKit.asset there?).");
                reported = true;
                heldFor = 0f;
                return;
            }
            ApplySize();
            shown = true;
            hideFor = 0f;
            root.SetActive(true);
            Follow(wrist, back, head, 0f, true);
            Debug.Log("[OS] Watch turn: wrist button shown (back dot " + LastBackDot.ToString("F2") + ", " + LastViewDegrees.ToString("F0") + " degrees from the gaze).");
        }

        void Hide()
        {
            shown = false;
            hideFor = 0f;
            if (root != null && root.activeSelf) root.SetActive(false);
        }

        // Lifts the button off the wrist along the back-of-hand normal and turns it to face the eyes. A canvas faces the
        // viewer when its forward points away from them, which is how the menu and the table button are turned.
        void Follow(Vector3 wrist, Vector3 back, Transform head, float dt, bool snap)
        {
            if (root == null) return;
            Vector3 target = wrist + back * (liftMillimetres * 0.001f);
            Transform t = root.transform;
            Vector3 position = snap || followSharpness <= 0f ? target : Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-followSharpness * dt));
            Vector3 away = position - head.position;
            Quaternion rotation = away.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(away, head.up) : t.rotation;
            t.SetPositionAndRotation(position, rotation);
        }

        void Pressed()
        {
            if (shell == null || !shown) return;
            Vector3 from = root.transform.position;
            armed = false;       // stays away until the wrist has turned away once
            heldFor = 0f;
            lostFor = 0f;
            Presses++;
            shell.OpenMenu(OSSummon.Watch, from);
            Hide();
        }

        // A failed build is remembered (until the component is enabled again) so the missing kit is not looked up every frame.
        bool Build()
        {
            if (built) return root != null;
            if (buildFailed) return false;
            if (!TryBuild())
            {
                buildFailed = true;
                if (root != null) Destroy(root);
                root = null;
                button = null;
                return false;
            }
            built = true;
            return true;
        }

        // The same recipe as the table button: a UI Set backplate (mouse, ray and poke all reach it) carrying one icon-only
        // UI Set button, both made round. Sizes are set in ApplySize each time it shows.
        bool TryBuild()
        {
            OSKit kit = OSKit.Current;
            if (kit == null || kit.backplate == null || kit.primaryButton == null) return false;

            root = Instantiate(kit.backplate);
            root.name = "OSWatchButton";
            canvasRoot = root.transform.Find("CanvasRoot") as RectTransform;
            plate = canvasRoot != null ? canvasRoot.Find("UIBackplate") as RectTransform : null;
            if (canvasRoot == null || plate == null) return false;
            foreach (var layout in canvasRoot.GetComponents<LayoutGroup>()) layout.enabled = false;
            foreach (var layout in plate.GetComponents<LayoutGroup>()) layout.enabled = false;
            canvasRoot.localScale = Vector3.one * kit.unitMeters;
            plate.anchorMin = plate.anchorMax = plate.pivot = new Vector2(0.5f, 0.5f);
            plate.anchoredPosition = Vector2.zero;
            plateRound = plate.GetComponent<Oculus.Interaction.Samples.RoundedBoxUIProperties>();
            OSBody.EnsureCanvas(canvasRoot.GetComponent<Canvas>());
            var wrapper = root.GetComponent<PointableCanvasUnityEventWrapper>();
            if (wrapper != null) wrapper.enabled = false;  // the button plays its own press sound
            var manager = root.GetComponent<UIThemeManager>();
            if (manager != null && kit.theme != null) OSBody.SetThemes(manager, kit.theme);

            button = OSUi.Button(plate, null, Pressed, OSButtonStyle.Primary, "apps");
            if (button == null) return false;
            buttonRect = (RectTransform)button.transform;
            buttonRect.anchorMin = buttonRect.anchorMax = buttonRect.pivot = new Vector2(0.5f, 0.5f);
            buttonRect.anchoredPosition = Vector2.zero;
            var layoutElement = button.GetComponent<LayoutElement>();
            if (layoutElement != null) layoutElement.ignoreLayout = true;
            Transform background = button.transform.Find("Content/Background");
            buttonRound = background != null ? background.GetComponent<Oculus.Interaction.Samples.RoundedBoxUIProperties>() : null;

            OSBody.RaiseAboveScene(root);  // drawn over transparent scene content, like the menu
            root.SetActive(false);
            ApplySize();
            if (manager != null) manager.ApplyCurrentTheme();
            return true;
        }

        // Round button of buttonMillimetres (never under 22) on a plate a little larger; sizes in OS units.
        void ApplySize()
        {
            OSKit kit = OSKit.Current;
            float unit = kit != null ? kit.unitMeters : 0.00065f;
            float buttonUnits = ButtonMeters / unit;
            float plateUnits = buttonUnits + PlatePadMillimetres * 0.001f / unit;
            if (canvasRoot != null) canvasRoot.sizeDelta = new Vector2(plateUnits, plateUnits);
            if (plate != null) plate.sizeDelta = new Vector2(plateUnits, plateUnits);
            if (plateRound != null) plateRound.borderRadius = new Vector4(plateUnits, plateUnits, plateUnits, plateUnits);
            if (buttonRect != null) OSUi.Size(buttonRect, buttonUnits, buttonUnits);
            if (buttonRound != null) buttonRound.borderRadius = new Vector4(buttonUnits, buttonUnits, buttonUnits, buttonUnits);
        }
    }
}
