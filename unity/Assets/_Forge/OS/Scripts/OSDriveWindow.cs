using Forge.Artifacts;
using Forge.Controllers;
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
    /// Optional wrist-roll steering (<c>hand.roll</c>) is not built: the pad is the transmitter.
    /// </summary>
    public sealed class OSDriveWindow : MonoBehaviour
    {
        /// <summary>Window id, and the id of the tool's tile.</summary>
        public const string WindowId = "drive";

        static readonly Vector2 SizeMeters = new Vector2(0.44f, 0.50f);
        const float PadSize = 260f, KnobSize = 84f;            // OS units: 169 mm and 55 mm
        const float StopWidth = 300f, StopHeight = 76f;         // OS units: 195 x 49 mm
        const float PollSeconds = 0.2f;
        const float LineSize = OSUi.BodyText, HintSize = OSUi.SmallText;

        // What the tile's status line last said (rebuilt only when the truck's state changes).
        static int tileKey = -1;
        static string tileText = "";

        ForgePanel panel;
        OSDrivePad pad;
        Button stop;
        TMP_Text thingText, stateText;
        float nextPoll;
        int thingKey = int.MinValue, stateKey = int.MinValue;
        string thingLine = "", stateLine = "";

        /// <summary>The line that says which Thing the pad drives, or that there is none.</summary>
        public string ThingLine => thingLine;

        /// <summary>The state line (recording lap n of m, idle, training, autopilot).</summary>
        public string StateLine => stateLine;

        /// <summary>The pad.</summary>
        public OSDrivePad Pad => pad;

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
                open = () => Open(),
            });
        }

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

            RectTransform padRow = OSUi.Row(column, PadSize, 0f);
            padRow.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
            pad = OSDrivePad.Create(padRow, PadSize, KnobSize);

            TMP_Text hint = OSUi.Text(column, "Push up to drive, down to reverse, sideways to steer. Let go and it stops.", HintSize, true);
            hint.alignment = TextAlignmentOptions.Center;

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

            Poll(true);
        }

        void OnDisable()
        {
            if (pad != null) pad.Release();  // the pad also zeroes itself when it goes; this covers a hidden window
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
            int hosts = ThingHosts.Count;
            int tk = (brain != null ? 1 : 0) | (loop != null ? 2 : 0) | (Mathf.Min(hosts, 15) << 2);
            if (force || tk != thingKey)
            {
                thingKey = tk;
                if (brain != null) thingLine = "Drives: the truck (the brain truck on the slalom)";
                else if (hosts > 0) thingLine = "Drives: " + FirstThing();
                else thingLine = "No truck to drive yet. Open the RC truck project and tap Course.";
                thingText.text = thingLine;
                // The brain truck holds a hand's speed (see OSDrivePad.ThrottleScale); another truck gets the whole range.
                if (pad != null) pad.ThrottleScale = brain != null ? HandThrottle : 1f;
            }

            int sk = StateKey(loop, brain);
            if (force || sk != stateKey)
            {
                stateKey = sk;
                stateLine = StateText(loop, brain);
                stateText.text = stateLine;
            }
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
            if (brain == null) return "Brain loop running";
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
            int key = StateKey(loop, brain) + (ThingHosts.Count > 0 ? 1 << 20 : 0);
            if (key == tileKey) return tileText;
            tileKey = key;
            if (brain != null)
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
            ManualInputs.Clear();
            IBrainLoop loop = OSProjects.LiveLoop;
            string said = "Stopped. The pad is at rest.";
            if (loop != null)
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
