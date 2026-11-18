using Forge.Artifacts;
using Forge.Controllers;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The Drive tool (window id "drive"): the hand transmitter. It says which Thing it drives (the brain truck on the open
    /// slalom, another truck that is running, or an honest line when there is none), shows the two-axis <see cref="OSDrivePad"/>,
    /// a big Stop button, and a small state line (recording lap n of m, idle, training, autopilot). Stop zeroes the inputs,
    /// stops a recording or the Autopilot if one is running, and so stops the truck. Opening the tool never starts anything by
    /// itself: the truck project's Drive action does that (it starts a two-lap recording), through the same tools Albie uses.
    /// The window registers its tile on the Tools tab (<see cref="RegisterTool"/>, called by <see cref="OSToolsCatalog"/> so
    /// it comes after Gestures). It re-reads the truck a few times a second and rebuilds a line only when it changes.
    ///
    /// Wrist-roll steering (<c>hand.roll</c>) is the optional air pinch (a switch here); the handheld transmitter is the default.
    ///
    /// With the drone on the open course (<see cref="ArtifactDrone.Live"/>) the same window is its transmitter: a second pad
    /// appears on the left (up and down climbs, sideways turns; it writes <c>stick2</c>), the first pad flies it forward and
    /// sideways, and the truck's lap buttons give way to Take off, Land, Home and Reset. Two switches: easy steering (forward
    /// is away from you, whichever way the nose points) and the pinch sticks (<see cref="HandsTransmitter.ReadFlight"/>).
    /// Stop then means hover: the pads let go and the drone brakes where it is; it never cuts the motors in the air.
    ///
    /// The handheld <see cref="RcTransmitter"/> is the main way to drive: "Hand me the transmitter" (and the Drive tile)
    /// bring it to your hands, "Put it away" hides it. The pads stay as the mouse and accessibility fallback, and the air
    /// pinch (<see cref="HandsTransmitter.PinchDriveEnabled"/>) is an option for the truck, off by default.
    /// </summary>
    public sealed class OSDriveWindow : MonoBehaviour
    {
        /// <summary>Window id, and the id of the tool's tile.</summary>
        public const string WindowId = "drive";

        static readonly Vector2 SizeMeters = new Vector2(0.50f, 0.86f);
        const float PadSize = 260f, KnobSize = 84f;            // OS units: 169 mm and 55 mm
        const float StopWidth = 300f, StopHeight = 76f;         // OS units: 195 x 49 mm
        const float PadGap = 28f;                               // between the drone's two pads
        const float PollSeconds = 0.2f;
        const string TruckHint = "Grip the transmitter: left thumb is the throttle, right thumb steers. Let go and it stops. "
                               + "No hands? Drag this pad: up drives, sideways steers.";
        const string DroneHint = "Transmitter, mode 2: left stick climbs and turns, right stick flies. Let go and it hovers. "
                               + "The pads here do the same.";
        const string TransmitterLine = "Pick it up with one or both hands; put it down and the sticks centre.";
        const int FlyTakeOff = 0, FlyLand = 1, FlyHome = 2, FlyReset = 3, FlyFollow = 4, FlyCam = 5;
        const float LineSize = OSUi.BodyText, HintSize = OSUi.SmallText;

        // What the tile's status line last said (rebuilt only when the truck's state changes).
        static int tileKey = -1;
        static string tileText = "";

        // Kept for the session: a new Drive window, and a drone spawned later, start with what the pilot last chose. Shared
        // with the transmitter's mode switch.
        static bool easySteering
        {
            get => RcTransmitter.EasySteering;
            set => RcTransmitter.EasySteering = value;
        }

        ForgePanel panel;
        Toggle easyToggle, pinchDriveToggle;
        GameObject truckOptions;
        Button handMe, putAway;
        OSDrivePad pad, leftPad;
        Button stop;
        Button setup, setupDrone, record, train, autopilot, followLine;
        TMP_Text recordLabel, autopilotLabel;
        int brainLabelKey = int.MinValue;
        Button takeOff, land, comeHome, reset, follow, camButton;
        TMP_Text followLabel, camLabel, bodyText;
        GameObject setupBar, actionsBar, flightBar, flightBar2, options;
        int bodyKey = int.MinValue;
        bool wasFollowing;
        TMP_Text actionText, hintText;
        TMP_Text thingText, stateText;
        float nextPoll;
        int thingKey = int.MinValue, stateKey = int.MinValue;
        string thingLine = "", stateLine = "";

        /// <summary>The line that says which Thing the pad drives, or that there is none.</summary>
        public string ThingLine => thingLine;

        /// <summary>The state line (recording lap n of m, idle, training, autopilot).</summary>
        public string StateLine => stateLine;

        /// <summary>The pad (the truck's only one; the drone's right one: forward and sideways).</summary>
        public OSDrivePad Pad => pad;

        /// <summary>The drone's left pad (climb and turn). Switched off while there is no drone.</summary>
        public OSDrivePad LeftPad => leftPad;

        /// <summary>The Stop button.</summary>
        public Button StopButton => stop;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            tileKey = -1;
            tileText = "";
        }

        /// <summary>Adds the Drive tile to the Tools tab. <see cref="OSToolsCatalog.Register"/> calls this after the others; the same id replaces.</summary>
        public static void RegisterTool()
        {
            OSRegistry.RegisterTool(new OSEntry
            {
                id = WindowId,
                title = "Drive",
                icon = "drive",
                status = TileStatus,
                isOpen = () => OSShell.Current != null && OSShell.Current.Windows.IsOpen(WindowId),
                // Opening Drive with something to drive also hands you the transmitter.
                open = () => { Open(); if (ArtifactDrone.Live != null || ThingHosts.Count > 0) HandTransmitter(); },
            });
        }

        /// <summary>Brings the handheld transmitter to the user's hands (or in front of them). Returns where it went.</summary>
        public static string HandTransmitter() => RcTransmitter.Ensure().Show();

        /// <summary>Opens the Drive window, or brings the open one forward. Null when this scene has no shell or no panel prefab.</summary>
        public static OSWindow Open()
        {
            OSShell shell = OSShell.Current;
            if (shell == null)
            {
                Debug.Log("[OS] Drive needs the Albie menu, and this scene has none.");
                return null;
            }
            return shell.Windows.Open(WindowId, "Drive", SizeMeters, Build, "DRIVE");
        }

        static void Build(ForgePanel forgePanel)
        {
            var view = forgePanel.gameObject.AddComponent<OSDriveWindow>();
            view.Init(forgePanel);
        }

        // ---------------------------------------------------------------- building

        void Init(ForgePanel owner)
        {
            panel = owner;
            RectTransform body = OSUi.Body(panel);
            if (body == null) return;
            RectTransform column = OSUi.Column(body, 14f);

            thingText = OSUi.Text(column, "", LineSize);

            // The handheld transmitter: the main way to drive.
            RectTransform txRow = OSUi.Row(column, OSUi.ButtonHeight, 12f);
            handMe = OSUi.Button(txRow, "Hand me the transmitter", () =>
            {
                string where = HandTransmitter();
                if (actionText != null) actionText.text = "The transmitter is " + where + ". " + TransmitterLine;
            }, OSButtonStyle.Primary);
            putAway = OSUi.Button(txRow, "Put it away", () =>
            {
                if (RcTransmitter.Instance != null) RcTransmitter.Instance.Hide();
                if (actionText != null) actionText.text = "Transmitter put away. Hand me the transmitter brings it back.";
            });

            RectTransform setupRow = OSUi.Row(column, OSUi.ButtonHeight, 12f);
            setupBar = setupRow.gameObject;
            setup = OSUi.Button(setupRow, "Set up truck", () => RunProjectAction("course"), OSButtonStyle.Primary);
            setupDrone = OSUi.Button(setupRow, "Set up drone", SetUpDrone);

            // The drone's two switches. Only shown with a drone.
            RectTransform optionsColumn = OSUi.Column(column, 4f);
            optionsColumn.anchorMin = optionsColumn.anchorMax = optionsColumn.pivot = new Vector2(0f, 1f);
            optionsColumn.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            options = optionsColumn.gameObject;
            easyToggle = OSUi.Toggle(optionsColumn, "Easy steering (away from you is forward)", easySteering, on =>
            {
                easySteering = on;
                if (DroneBody.Active != null) DroneBody.Active.headless = on;
            });
            OSUi.Toggle(optionsColumn, "Pinch sticks (fly with pinched hands, no transmitter)", HandsTransmitter.FlightEnabled, on => HandsTransmitter.FlightEnabled = on);
            options.SetActive(false);

            // The truck's option: the air pinch, for driving with no transmitter in hand.
            RectTransform truckColumn = OSUi.Column(column, 4f);
            truckColumn.anchorMin = truckColumn.anchorMax = truckColumn.pivot = new Vector2(0f, 1f);
            truckColumn.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            truckOptions = truckColumn.gameObject;
            pinchDriveToggle = OSUi.Toggle(truckColumn, "Air pinch driving (pinch right hand, tilt to steer)", HandsTransmitter.PinchDriveEnabled,
                                           on => HandsTransmitter.PinchDriveEnabled = on);

            // The drone's left pad (climb, turn) stands before the pad; with no drone it is off and the pad sits in the middle.
            RectTransform padRow = OSUi.Row(column, PadSize, PadGap);
            padRow.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
            leftPad = OSDrivePad.Create(padRow, PadSize, KnobSize);
            leftPad.name = "Climb pad";
            leftPad.SetChannels("stick2.x", "stick2.y");
            leftPad.gameObject.SetActive(false);
            pad = OSDrivePad.Create(padRow, PadSize, KnobSize);

            hintText = OSUi.Text(column, TruckHint, HintSize, true);
            hintText.alignment = TextAlignmentOptions.Center;

            RectTransform actions = OSUi.Row(column, OSUi.ButtonHeight, 12f);
            actionsBar = actions.gameObject;
            // The truck: the truck project's own actions, as before. The drone: its learning loop (BrainLoops' front loop).
            record = OSUi.Button(actions, "Record laps", () => BrainAction("drive"));
            train = OSUi.Button(actions, "Train", () => BrainAction("train"));
            autopilot = OSUi.Button(actions, "Autopilot", () => BrainAction("autopilot"));
            followLine = OSUi.Button(actions, "Follow line", () => BrainAction("follow_line"));
            recordLabel = record != null ? record.GetComponentInChildren<TMP_Text>(true) : null;
            autopilotLabel = autopilot != null ? autopilot.GetComponentInChildren<TMP_Text>(true) : null;
            if (followLine != null) followLine.gameObject.SetActive(false);

            RectTransform flight = OSUi.Row(column, OSUi.ButtonHeight, 12f);
            flightBar = flight.gameObject;
            takeOff = OSUi.Button(flight, "Take off", () => Fly(FlyTakeOff), OSButtonStyle.Primary);
            land = OSUi.Button(flight, "Land", () => Fly(FlyLand));
            comeHome = OSUi.Button(flight, "Home", () => Fly(FlyHome));
            reset = OSUi.Button(flight, "Reset", () => Fly(FlyReset));
            flightBar.SetActive(false);

            RectTransform flight2 = OSUi.Row(column, OSUi.ButtonHeight, 12f);
            flightBar2 = flight2.gameObject;
            follow = OSUi.Button(flight2, "Follow me", () => Fly(FlyFollow));
            camButton = OSUi.Button(flight2, "Drone cam", () => Fly(FlyCam));
            followLabel = follow != null ? follow.GetComponentInChildren<TMP_Text>(true) : null;
            camLabel = camButton != null ? camButton.GetComponentInChildren<TMP_Text>(true) : null;
            flightBar2.SetActive(false);

            // What the drone is made of, and what that does to how it flies (the design's numbers, honestly modest).
            bodyText = OSUi.Text(column, "", HintSize, true);
            bodyText.alignment = TextAlignmentOptions.Center;
            bodyText.gameObject.SetActive(false);

            RectTransform stopRow = OSUi.Row(column, StopHeight, 0f);
            stopRow.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
            // The UI Set button draws its plate at 48 units whatever its box, and its animator owns its own scale. So Stop is the
            // stock button laid out at the stock size inside a holder that scales it up to StopHeight (49 mm): the plate, the
            // label and the touch target all grow.
            float grow = StopHeight / OSUi.ButtonHeight;
            var holder = new GameObject("Stop holder", typeof(RectTransform));
            holder.transform.SetParent(stopRow, false);
            OSUi.Size(holder.transform, StopWidth, StopHeight);
            holder.GetComponent<LayoutElement>().flexibleHeight = 0f;
            var scaler = new GameObject("Stop scaler", typeof(RectTransform));
            scaler.transform.SetParent(holder.transform, false);
            var scalerRect = (RectTransform)scaler.transform;
            scalerRect.anchorMin = scalerRect.anchorMax = scalerRect.pivot = new Vector2(0.5f, 0.5f);
            scalerRect.anchoredPosition = Vector2.zero;
            scalerRect.sizeDelta = new Vector2(StopWidth / grow, OSUi.ButtonHeight);
            scalerRect.localScale = new Vector3(grow, grow, 1f);
            stop = OSUi.Button(scaler.transform, "Stop", () => Stop(), OSButtonStyle.Destructive);
            if (stop != null)
            {
                var stopRect = (RectTransform)stop.transform;
                stopRect.anchorMin = stopRect.anchorMax = stopRect.pivot = new Vector2(0.5f, 0.5f);
                stopRect.anchoredPosition = Vector2.zero;
                stopRect.sizeDelta = new Vector2(StopWidth / grow, OSUi.ButtonHeight);
            }
            stopRow.GetComponent<LayoutElement>().flexibleHeight = 0f;
            padRow.GetComponent<LayoutElement>().flexibleHeight = 0f;

            stateText = OSUi.Text(column, "", LineSize, true);
            stateText.alignment = TextAlignmentOptions.Center;
            thingText.alignment = TextAlignmentOptions.Center;
            actionText = OSUi.Text(column, "Tap Set up truck to create a course, then Allow on its card.", HintSize, true);
            actionText.alignment = TextAlignmentOptions.Center;

            Poll(true);
        }

        void OnDisable()
        {
            if (pad != null) pad.Release();  // the pad also zeroes itself when it goes; this covers a hidden window
            if (leftPad != null) leftPad.Release();
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (now < nextPoll) return;
            nextPoll = now + PollSeconds;
            Poll(false);
        }

        // Reads the truck and rewrites a line only when it changes (no string is built otherwise).
        void Poll(bool force)
        {
            if (thingText == null) return;
            IBrainLoop loop = OSProjects.LiveLoop;
            var brain = loop as SlalomBrain;
            ArtifactDrone drone = ArtifactDrone.Live;
            bool flying = drone != null;
            // The switches follow the transmitter's (its mode switch and these share the same settings).
            if (easyToggle != null && easyToggle.isOn != easySteering) easyToggle.isOn = easySteering;
            if (pinchDriveToggle != null && pinchDriveToggle.isOn != HandsTransmitter.PinchDriveEnabled) pinchDriveToggle.isOn = HandsTransmitter.PinchDriveEnabled;
            if (putAway != null) putAway.interactable = RcTransmitter.Instance != null && RcTransmitter.Instance.Shown;
            if (setupBar != null) setupBar.SetActive(!flying && loop == null && ThingHosts.Count == 0);
            if (flying)
            {
                DroneBody.Phase phase = drone.Body.State;
                if (takeOff != null) takeOff.interactable = phase == DroneBody.Phase.Landed || phase == DroneBody.Phase.Landing;
                if (land != null) land.interactable = phase == DroneBody.Phase.TakingOff || phase == DroneBody.Phase.Flying;
                if (comeHome != null) comeHome.interactable = phase == DroneBody.Phase.Flying && !drone.Body.Returning;
                if (follow != null) follow.interactable = phase != DroneBody.Phase.Crashed;
                bool following = drone.Follower != null && drone.Follower.Active;
                if (wasFollowing && !following && actionText != null && drone.Follower.EndedWhy != null)
                    actionText.text = "Follow-me ended (" + drone.Follower.EndedWhy + "). It hovers where it is unless it landed.";
                wasFollowing = following;
                bool camOn = drone.Cam != null && drone.Cam.On;
                int bk = (drone.Design != null ? drone.Design.Version * 4 + (drone.Design.Loaded ? 1 : 0) : 0) * 4 + (following ? 1 : 0) + (camOn ? 2 : 0);
                if (force || bk != bodyKey)
                {
                    bodyKey = bk;
                    if (followLabel != null) followLabel.text = following ? "Stop follow" : "Follow me";
                    if (camLabel != null) camLabel.text = camOn ? "Hide cam" : "Drone cam";
                    if (bodyText != null) bodyText.text = BodyLine(drone);
                }
            }
            ICourseBrain droneBrain = flying ? DroneBrainOf(loop) : null;
            if (droneBrain != null)
            {
                string m = droneBrain.ModeName;
                bool idle = m == "idle";
                if (record != null) record.interactable = idle || m == "lap";
                if (train != null) train.interactable = idle && droneBrain.LapsCompleted > 0;
                if (autopilot != null) autopilot.interactable = (idle && droneBrain.VersionCount > 0) || m == "autopilot";
                if (followLine != null) followLine.interactable = idle;
                int lk = (m == "lap" ? 1 : 0) + (m == "autopilot" ? 2 : 0) + 4;
                if (force || lk != brainLabelKey)
                {
                    brainLabelKey = lk;
                    if (recordLabel != null) recordLabel.text = m == "lap" ? "Stop lap" : "Record lap";
                    if (autopilotLabel != null) autopilotLabel.text = m == "autopilot" ? "Stop autopilot" : "Autopilot";
                }
            }
            else
            {
                if (record != null) record.interactable = brain != null && brain.State == SlalomBrain.Mode.Idle;
                if (train != null) train.interactable = brain != null && brain.State == SlalomBrain.Mode.Idle && brain.CompletedLaps > 0;
                if (autopilot != null) autopilot.interactable = brain != null && (brain.State == SlalomBrain.Mode.Idle || brain.State == SlalomBrain.Mode.Autopilot);
                if (brainLabelKey != 0)
                {
                    brainLabelKey = 0;
                    if (recordLabel != null) recordLabel.text = "Record laps";
                    if (autopilotLabel != null) autopilotLabel.text = "Autopilot";
                }
            }
            int hosts = ThingHosts.Count;
            // A desk course's scale is in the key too (as its 1:N), so "Desk course · 1:N scale" follows a rebuilt course.
            int deskRatio = brain != null && brain.CourseScale < 0.99f ? Mathf.Min(Mathf.RoundToInt(1f / brain.CourseScale), 63) : 0;
            int tk = (brain != null ? 1 : 0) | (loop != null ? 2 : 0) | (Mathf.Min(hosts, 15) << 2) | (flying ? 64 : 0) | (droneBrain != null ? 128 : 0) | (deskRatio << 8);
            if (force || tk != thingKey)
            {
                thingKey = tk;
                ShowDroneControls(flying, drone);
                // The learning buttons stay for the truck; with the drone they show only when its loop is there.
                if (actionsBar != null) actionsBar.SetActive(!flying || droneBrain != null);
                if (followLine != null) followLine.gameObject.SetActive(droneBrain != null);
                if (flying) thingLine = "Drives: the drone (on the open course)";
                else if (brain != null) thingLine = deskRatio > 0
                    ? "Drives: the truck (the brain truck on the course) · Desk course · 1:" + deskRatio + " scale"
                    : "Drives: the truck (the brain truck on the slalom)";
                else if (hosts > 0) thingLine = "Drives: " + FirstThing();
                else thingLine = "No truck to drive yet. Tap Set up truck to create a course.";
                thingText.text = thingLine;
                // The brain truck holds a hand's speed (see OSDrivePad.ThrottleScale); another truck gets the whole range.
                if (pad != null) pad.ThrottleScale = brain != null && !flying ? HandThrottle : 1f;
            }

            int sk = flying ? unchecked(DroneKey(drone) * 1000003 + BrainKey(droneBrain)) : StateKey(loop, brain);
            if (force || sk != stateKey)
            {
                stateKey = sk;
                stateLine = flying ? DroneText(drone) + BrainText(droneBrain) : StateText(loop, brain);
                stateText.text = stateLine;
                if (!flying && brain != null && actionText != null)
                    actionText.text = brain.State == SlalomBrain.Mode.Idle
                        ? "Drive it around any time. To teach it, record two laps, then Train and Autopilot."
                        : "Let go of the sticks to brake. Stop ends the recording or Autopilot.";
            }
        }

        // ---------------------------------------------------------------- the learning loop (truck or drone)

        // The front loop when it is the drone's (DroneBrain), else null.
        static ICourseBrain DroneBrainOf(IBrainLoop loop) =>
            loop is ICourseBrain cb && cb.ThingName == ArtifactDrone.ThingName ? cb : null;

        // The truck goes through its project's actions exactly as before; the drone through its loop's IBrainLoop calls.
        void BrainAction(string action)
        {
            ICourseBrain cb = ArtifactDrone.Live != null ? DroneBrainOf(OSProjects.LiveLoop) : null;
            if (cb == null)
            {
                if (action != "follow_line") RunProjectAction(action);
                return;
            }
            string m = cb.ModeName, error, said;
            switch (action)
            {
                case "drive":
                    if (m == "lap") { error = cb.StopRecording(); said = "Lap dropped. Laps already recorded are kept."; }
                    else { error = cb.Record(1, false); said = "Recording a lap: fly the course by hand through every target in order."; }
                    break;
                case "train":
                    error = cb.Train();
                    said = "Training on your laps. The state line shows the progress.";
                    break;
                case "autopilot":
                    if (m == "autopilot") { error = cb.StopAutopilot(); said = "Autopilot off. The drone hovers; it is yours again."; }
                    else { error = cb.StartAutopilot(); said = "Autopilot: the trained brain flies the course. Any stick takes it back."; }
                    break;
                default:
                    error = cb is DroneBrain db ? db.StartFollowLine() : "this loop has no course-line follower";
                    said = "Follow the course line: a plain line follower (not learned) flies the course. Any stick takes it back.";
                    break;
            }
            // The loop's calls return null when they started, or what stopped them (its Status says the same).
            if (actionText != null) actionText.text = error != null ? OSProjects.Plain(error) : said;
            nextPoll = 0f;
        }

        static int BrainKey(ICourseBrain cb)
        {
            if (cb == null) return 0;
            float p = cb.TrainProgress;
            int m = cb.ModeName == "lap" ? 1 : cb.ModeName == "training" ? 2 : cb.ModeName == "autopilot" ? 3 : 4;
            return (m + 5 * Mathf.Min(cb.LapsCompleted, 99) + 500 * Mathf.Min(cb.VersionCount, 20) + 10000 * (p >= 0f ? 1 + Mathf.RoundToInt(p * 20f) : 0));
        }

        static string BrainText(ICourseBrain cb)
        {
            if (cb == null) return "";
            switch (cb.ModeName)
            {
                case "lap": return " · recording a lap (" + cb.LapsCompleted + " done)";
                case "training":
                    float p = cb.TrainProgress;
                    return " · training" + (p >= 0f ? " " + Mathf.RoundToInt(p * 100f) + "%" : "");
                case "autopilot": return " · " + cb.StatusLine;
                default:
                    int laps = cb.LapsCompleted, v = cb.VersionCount;
                    if (laps == 0 && v == 0) return "";
                    return " · " + laps + (laps == 1 ? " lap" : " laps") + (v > 0 ? ", brain v" + cb.SelectedVersion + " of " + v : "");
            }
        }

        // ---------------------------------------------------------------- the drone

        // The window as the drone's transmitter, or back as the truck's. Called only when what is driven changes.
        void ShowDroneControls(bool on, ArtifactDrone drone)
        {
            if (leftPad != null) leftPad.gameObject.SetActive(on);
            if (options != null) options.SetActive(on);
            if (truckOptions != null) truckOptions.SetActive(!on);
            if (flightBar != null) flightBar.SetActive(on);
            if (flightBar2 != null) flightBar2.SetActive(on);
            if (bodyText != null) bodyText.gameObject.SetActive(on);
            bodyKey = int.MinValue;
            if (hintText != null) hintText.text = on ? DroneHint : TruckHint;
            if (!on) return;
            if (drone != null) drone.Body.headless = easySteering;
            if (actionText != null) actionText.text = "Pick up the transmitter: Take off, fly with the two sticks, then Land. Stop makes it hover.";
        }

        void Fly(int what)
        {
            ArtifactDrone drone = ArtifactDrone.Live;
            if (drone == null)
            {
                if (actionText != null) actionText.text = "There is no drone on a course right now.";
                return;
            }
            string error = null, said;
            switch (what)
            {
                case FlyTakeOff: error = drone.TakeOff(); said = "Taking off. It climbs to a hover and holds its height."; break;
                case FlyLand: error = drone.Land(); said = "Landing where it is."; break;
                case FlyHome: error = drone.Land(true); said = "Flying back to the start to land. Push a stick to take it back."; break;
                case FlyFollow:
                    if (drone.Follower != null && drone.Follower.Active)
                    {
                        drone.StopFollow();
                        said = "Stopped following. It hovers where it is.";
                    }
                    else
                    {
                        error = drone.Follow(2f, 0f);
                        said = "Follow-me: it keeps 2 m from your headset, just under head height, facing you, and steers round the scanned furniture. "
                             + "It holds if you come within 0.8 m. Push a stick, Stop or Land to take over.";
                    }
                    break;
                case FlyCam:
                    if (drone.Cam != null && drone.Cam.On)
                    {
                        OSDroneCamWindow.Close();
                        drone.SetCam(false);
                        said = "The drone cam is off.";
                    }
                    else
                    {
                        OSDroneCamWindow.Open();
                        said = "Drone cam: the drone's virtual view (the scene and the room scan as lines; passthrough can't reach it). The orange box is your headset's position, not a detection.";
                    }
                    break;
                default: drone.ResetToStart(); said = "Back at the start, landed. Take off starts a new run."; break;
            }
            if (actionText != null) actionText.text = error != null ? OSProjects.Plain(error) : said;
            nextPoll = 0f;
        }

        // Opens the hoop course (the built-in course whose Thing is the drone) through the same tools Albie uses; the
        // drone comes with it (ArtifactBootstrap.EnsureDrone). The first open asks permission on its own card.
        void SetUpDrone()
        {
            const string course = "hoop_course";
            JObject result = AlbieTools.Call("artifact_open", new JObject { ["id"] = course });
            if (result["error"] != null) result = AlbieTools.Call("artifact_create", new JObject { ["template"] = course });
            string error = (string)result["error"];
            JObject opened = result["opened"] as JObject ?? result;  // artifact_create nests the open result
            string status = (string)opened["status"];
            string text;
            if (error != null) text = OSProjects.Plain(error);
            else if (status == "awaiting_approval") text = "The hoop course asks first. Tap Allow on the card in front of you.";
            else if (status != "open") text = "The hoop course didn't open.";
            else if (ArtifactDrone.Live != null) text = "The hoop course is open. The drone is at the start.";
            else text = "The hoop course is open, but the drone didn't appear.";
            if (actionText != null) actionText.text = text;
            nextPoll = 0f;
        }

        static int DroneKey(ArtifactDrone drone) =>
            100000 + (int)drone.Body.State * 512 + (drone.Follower != null ? (int)drone.Follower.State : 0) * 64 + drone.RunKey * 8 + (drone.Body.Returning ? 1 : 0);

        static string BodyLine(ArtifactDrone drone)
        {
            var d = drone.Design;
            string line;
            if (d == null || !d.Loaded) line = "Body: the built-in shape (no quadcopter design found yet).";
            else
            {
                line = "Body: your Quadcopter v" + d.Version + " from " + d.Source
                     + (d.ShownScale < 0.99f ? ", shown at " + Mathf.RoundToInt(d.ShownScale * 100f) + "% size" : ", real size") + ". " + d.Note;
            }
            if (drone.CourseScale < 0.99f) line += " On this desk course it is a 1:" + Mathf.RoundToInt(1f / drone.CourseScale) + " miniature, speeds scaled to match.";
            return line;
        }

        static string DroneText(ArtifactDrone drone)
        {
            string text;
            switch (drone.Body.State)
            {
                case DroneBody.Phase.TakingOff: text = "Taking off"; break;
                case DroneBody.Phase.Flying:
                    switch (drone.FollowState)
                    {
                        case "following": text = "Following you"; break;
                        case "starting": text = "Follow-me: taking off"; break;
                        case "too_close": text = "Follow-me: holding (you are close)"; break;
                        case "blocked": text = "Follow-me: holding (no free spot near you)"; break;
                        default: text = drone.Body.Returning ? "Flying home" : "Flying by hand"; break;
                    }
                    break;
                case DroneBody.Phase.Landing: text = "Landing"; break;
                case DroneBody.Phase.Crashed: text = "Crashed: tap Reset"; break;
                default: text = "Landed"; break;
            }
            string run = drone.RunState;
            if (run == "armed" || run == "running") text += " · run " + run;
            else if (run == "finished") text += " · run " + (drone.RunOutcome ?? "finished");
            return text;
        }

        // Reuse the project's setup, permission card, recording and training actions so Drive can be used on its own.
        void RunProjectAction(string action)
        {
            string id = NotebookStore.ProjectId;
            if (string.IsNullOrEmpty(id) || OSProjects.KindOf(id) != OSProjectKind.Truck)
                id = NotebookStore.DefaultProject;
            if (!OSProjects.EnsureOpen(id, out string error))
            {
                actionText.text = error;
                return;
            }
            var window = OSProjectWindow.Open(id);
            var project = window != null ? window.GetComponent<OSProjectWindow>() : null;
            actionText.text = project != null ? project.Do(action) : "The truck project couldn't open. Try Set up truck again.";
            nextPoll = 0f;
        }

        /// <summary>The throttle at which a hand holds <see cref="SlalomBrain.KeySpeed"/> on the brain truck: what its W key and a Touch stick write.</summary>
        public static float HandThrottle => SlalomBrain.KeySpeed * MockTruckSim.Drag / MockTruckSim.MaxAccel;

        static string FirstThing()
        {
            foreach (string name in ThingHosts.Names) return name;
            return "a truck";
        }

        // ---------------------------------------------------------------- the state

        static int StateKey(IBrainLoop loop, SlalomBrain brain)
        {
            if (loop == null) return 0;
            if (brain == null) return 1;
            return 2 + (int)brain.State + 4 * Mathf.Min(brain.CompletedLaps, 99) + 512 * Mathf.Min(brain.LapTarget, 99);
        }

        static string StateText(IBrainLoop loop, SlalomBrain brain)
        {
            if (loop == null) return "Idle";
            if (brain == null) return "Brain running";
            switch (brain.State)
            {
                case SlalomBrain.Mode.Lap:
                    return brain.LapTarget > 0
                        ? "Recording lap " + Mathf.Min(brain.CompletedLaps + 1, brain.LapTarget) + " of " + brain.LapTarget
                        : "Recording a lap";
                case SlalomBrain.Mode.Training: return "Training the brain";
                case SlalomBrain.Mode.Autopilot: return "Autopilot driving";
                default:
                    int laps = brain.CompletedLaps;
                    return laps > 0 ? "Idle · " + laps + (laps == 1 ? " lap recorded" : " laps recorded") : "Idle";
            }
        }

        static string TileStatus()
        {
            IBrainLoop loop = OSProjects.LiveLoop;
            var brain = loop as SlalomBrain;
            ArtifactDrone drone = ArtifactDrone.Live;
            int key = drone != null ? (1 << 21) + (int)drone.Body.State : StateKey(loop, brain) + (ThingHosts.Count > 0 ? 1 << 20 : 0);
            if (key == tileKey) return tileText;
            tileKey = key;
            if (drone != null)
            {
                switch (drone.Body.State)
                {
                    case DroneBody.Phase.Landed: tileText = "drone ready"; break;
                    case DroneBody.Phase.Crashed: tileText = "drone crashed"; break;
                    default: tileText = "drone flying"; break;
                }
            }
            else if (brain != null)
            {
                switch (brain.State)
                {
                    case SlalomBrain.Mode.Lap: tileText = "recording a lap"; break;
                    case SlalomBrain.Mode.Training: tileText = "brain training"; break;
                    case SlalomBrain.Mode.Autopilot: tileText = "autopilot driving"; break;
                    default: tileText = "truck ready"; break;
                }
            }
            else tileText = loop != null || ThingHosts.Count > 0 ? "truck ready" : "no truck yet";
            return tileText;
        }

        // ---------------------------------------------------------------- Stop

        /// <summary>
        /// Stops everything: the pad lets go, every manual channel is zeroed, a lap being recorded is aborted (laps already
        /// recorded stay) and an Autopilot run is stopped, which parks the truck. Returns one plain sentence saying what
        /// it stopped. The Stop button calls this.
        /// </summary>
        public string Stop()
        {
            if (pad != null) pad.Release();
            if (leftPad != null) leftPad.Release();
            if (RcTransmitter.Instance != null) RcTransmitter.Instance.CentreSticks();
            ManualInputs.Clear();
            IBrainLoop loop = OSProjects.LiveLoop;
            string said = "Stopped. The sticks are at rest.";
            ArtifactDrone drone = ArtifactDrone.Live;
            if (drone != null)
            {
                // The drone's loop first (a lap or an Autopilot run ends, hovering), then the hover.
                ICourseBrain cb = DroneBrainOf(loop);
                if (cb != null && cb.ModeName == "lap") cb.StopRecording();
                else if (cb != null && cb.ModeName == "autopilot") cb.StopAutopilot();
                // A drone's stop is a hover: cutting the motors in the air would drop it.
                drone.Stop();
                said = drone.Body.Airborne ? "Stopped. The drone hovers where it is; Land brings it down." : "Stopped. The pads are at rest.";
            }
            else if (loop != null)
            {
                JObject state = loop.ToolState();
                string mode = state != null ? (string)state["mode"] : null;
                if (mode == "lap")
                {
                    loop.StopRecording();
                    said = "Stopped. The lap in progress was dropped; laps already recorded are kept.";
                }
                else if (mode == "autopilot")
                {
                    loop.StopAutopilot();
                    said = "Stopped. The Autopilot is off and the truck is yours again.";
                }
                else if (mode == "training")
                {
                    said = "Stopped the pad. The brain is still training; it will finish by itself.";
                }
            }
            ManualInputs.Clear();  // a recording's end clears too; this covers any frame in between
            nextPoll = 0f;         // the state line is re-read at once
            Debug.Log("[OS] Drive: " + said);
            return said;
        }
    }
}
