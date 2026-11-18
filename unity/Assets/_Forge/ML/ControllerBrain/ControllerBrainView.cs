using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Forge.Controllers;

namespace Forge.ML
{
    /// <summary>
    /// The ML desk's Controller brain panel. Two modes, never mixed:
    /// - Live: when a brain loop runs (<see cref="BrainLoops.Active"/> as an <see cref="ICourseBrain"/>: the truck's
    ///   <c>SlalomBrain</c> or the drone's <c>DroneBrain</c>) with its course open, or training, the buttons drive it:
    ///   Record lap / Stop, Train (the real per-epoch loss curve and progress), Autopilot on/off, the version picker, and
    ///   for the drone "Follow course line" (explicitly not learned). The bars are the Thing's live inputs and actuators.
    /// - Sample: with no loop, the panel says what to open, with buttons that open a course, and plays the C-19 lap
    ///   fixture as a clearly labelled preview (its loss curve is a canned shape and says so).
    /// Mouse (DesktopRig) and hand ray/poke both work: everything is a ForgePill button.
    /// </summary>
    [RequireComponent(typeof(ForgePanel))]
    public sealed class ControllerBrainView : MonoBehaviour
    {
        const string LapsResource = "Mocks/controllers/laps/truck_v2";
        const float SampleSeconds = 0.08f, LiveSeconds = 0.1f;
        const int InputRows = 9, OutputRows = 4;
        static readonly string[] SampleLabels = { "ray left", "ray left", "ray left", "ray ahead", "ray right", "ray right", "ray right", "speed" };
        static readonly string[] SampleChannels = { "d0", "d1", "d2", "d3", "d4", "d5", "d6", "forward" };
        // -1.00 .. +1.00 in 0.05 steps, so a live value shows with no allocation.
        static readonly string[] Numbers = BuildNumbers();

        RectTransform root;
        TMP_Text titleText, modeText, sampleText, lossText, lapText, trainingText, versionText, inputsCaption, outputsCaption;
        readonly TMP_Text[] inputLabels = new TMP_Text[InputRows], inputValues = new TMP_Text[InputRows];
        readonly Image[] inputBars = new Image[InputRows];
        readonly TMP_Text[] outputLabels = new TMP_Text[OutputRows], outputValues = new TMP_Text[OutputRows];
        readonly Image[] outputBars = new Image[OutputRows];
        TMP_Text controllerStatus, maxSpeedValue, recordLabel, autopilotLabel;
        float maxSpeed = 1.5f;
        Image trainProgress;
        MLCurve sparkline;
        GameObject[] liveOnly, sampleOnly, droneOnly, truckOnly;
        float inputWidth, outputWidth;
        readonly List<float> lossHistory = new List<float>(512);
        readonly List<float[]> samples = new List<float[]>();
        readonly List<float[]> outputs = new List<float[]>();
        Coroutine training;
        float sampleClock, liveClock;
        int sampleIndex;
        BrainMode mode;
        JArray laps;
        string[] sensors;
        int recordedSamples;
        bool initialized;
        // Live: what the texts were last built from, so they are rebuilt only on a change.
        ICourseBrain shown;
        bool wasLive;
        string lastStatus, lastMode, lastCourse;
        int lastLaps = -1, lastLoss = -1, lastVersions = -1, lastSelected = -1, lastProgress = -2;
        string[] lastInputNames, lastOutputNames;
        string actionNote;

        enum BrainMode { Ready, Recording, Training, Autopilot }

        public string Mode => Live != null ? Live.ModeName : mode.ToString().ToLowerInvariant();
        public int SampleCount => samples.Count;
        /// <summary>The loop the panel drives, or null when it shows the sample.</summary>
        public ICourseBrain Live
        {
            get
            {
                var loop = BrainLoops.Active as ICourseBrain;
                if (loop == null || (loop is UnityEngine.Object o && o == null)) return null;
                return loop.CourseId != null || loop.ModeName != "idle" ? loop : null;
            }
        }

        void Start()
        {
            if (initialized) return;
            initialized = true;
            root = GetComponent<ForgePanel>().Content;
            LoadFixture();
            BuildView();
            SetMode(BrainMode.Ready);
            RefreshSample();
            ApplyLive(Live);
        }

        void Update()
        {
            if (!initialized) return;
            var live = Live;
            if (live != null || wasLive || !ReferenceEquals(live, shown)) ApplyLive(live);
            if (live != null) return;

            if (mode == BrainMode.Training) return;
            sampleClock += Time.unscaledDeltaTime;
            if (sampleClock < SampleSeconds) return;
            sampleClock = 0;
            sampleIndex = (sampleIndex + 1) % Mathf.Max(1, samples.Count);
            if (mode == BrainMode.Recording && samples.Count > 0)
            {
                samples.Add((float[])samples[sampleIndex].Clone());
                outputs.Add((float[])outputs[sampleIndex].Clone());
                recordedSamples++;
                if (lapText != null) lapText.text = $"Sample preview  ·  recording  ·  {recordedSamples} samples";
            }
            RefreshSample();
        }

        void LoadFixture()
        {
            var text = Resources.Load<TextAsset>(LapsResource);
            if (text == null) { Debug.LogWarning("[ML] Missing controller lap fixture: " + LapsResource); return; }
            try
            {
                var json = JObject.Parse(text.text);
                sensors = json["sensors"]?.ToObject<string[]>() ?? new string[8];
                laps = json["laps"] as JArray;
                if (laps == null) return;
                foreach (var lap in laps)
                {
                    var x = lap["x"] as JArray; var y = lap["y"] as JArray;
                    if (x == null || y == null) continue;
                    for (int i = 0; i < Mathf.Min(x.Count, y.Count); i++)
                    {
                        samples.Add(x[i].ToObject<float[]>());
                        outputs.Add(y[i].ToObject<float[]>());
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[ML] Bad controller lap fixture: " + e.Message); }
        }

        // ---- The view ----------------------------------------------------------------------------------------------

        void BuildView()
        {
            float width = root.rect.width;
            titleText = MLUI.Caps(root, "Sample laps  ·  preview", 2, 0, width - 124);
            modeText = MLUI.Text(root, "READY", width - 120, 2, 118, 16, 10); modeText.alignment = TextAlignmentOptions.Right;
            modeText.color = MLUI.Faint;

            inputWidth = width * .48f - 6;
            MLUI.Card(root, 0, 28, inputWidth, 236, 12);
            inputsCaption = MLUI.Caps(root, "INPUTS  ·  7 RAY SENSORS + SPEED", 14, 42, inputWidth - 22);
            float rowY = 62;
            for (int i = 0; i < InputRows; i++)
            {
                inputLabels[i] = MLUI.Text(root, "", 14, rowY, 120, 20, 9.5f);
                inputLabels[i].color = MLUI.Sub;
                MLUI.Well(root, 138, rowY + 4, inputWidth - 184, 10, 5);
                inputBars[i] = MLUI.Fill(root, 138, rowY + 4, 1, 10, i == 7 ? MLUI.Accent : MLUI.ClassColor(i));
                inputValues[i] = MLUI.Text(root, "—", inputWidth - 41, rowY, 34, 18, 9.5f); inputValues[i].alignment = TextAlignmentOptions.Right;
                rowY += 21;
            }

            float outputX = width * .48f + 6;
            outputWidth = width * .52f - 6;
            MLUI.Card(root, outputX, 28, outputWidth, 236, 12);
            outputsCaption = MLUI.Caps(root, "OUTPUTS  ·  ACTUATORS", outputX + 14, 42, outputWidth - 28);
            Color[] colors = { MLUI.Train, MLUI.Good, MLUI.Val, MLUI.Accent };
            for (int i = 0; i < OutputRows; i++)
            {
                float y = 64 + i * 34;
                outputLabels[i] = MLUI.Text(root, "", outputX + 14, y, 150, 28, 10);
                MLUI.Well(root, outputX + 158, y + 6, outputWidth - 220, 12, 6);
                outputBars[i] = MLUI.Fill(root, outputX + 158, y + 6, 1, 12, colors[i]);
                outputValues[i] = MLUI.Text(root, "—", outputX + outputWidth - 52, y, 44, 22, 13);
            }

            var truck = new List<GameObject>();
            truck.Add(MLUI.Caps(root, "CONTROLLER FILE", outputX + 14, 206, 150).gameObject);
            truck.Add(MLUI.Text(root, "truck_autopilot  ·  max_speed", outputX + 14, 223, 170, 18, 9).gameObject);
            truck.Add(MLUI.Button(root, "−", outputX + 170, 216, 34, 26, () => ChangeMaxSpeed(-0.1f)).gameObject);
            maxSpeedValue = MLUI.Text(root, "1.5", outputX + 208, 218, 43, 22, 12);
            maxSpeedValue.alignment = TextAlignmentOptions.Center;
            truck.Add(maxSpeedValue.gameObject);
            truck.Add(MLUI.Button(root, "+", outputX + 254, 216, 34, 26, () => ChangeMaxSpeed(0.1f)).gameObject);
            truck.Add(MLUI.Button(root, "Apply", outputX + 292, 216, 58, 26, ApplyMaxSpeed).gameObject);
            controllerStatus = MLUI.Text(root, "Apply sets the running truck's max speed", outputX + 14, 246, outputWidth - 28, 14, 8.5f);
            controllerStatus.color = MLUI.Faint;
            truck.Add(controllerStatus.gameObject);
            truckOnly = truck.ToArray();

            MLUI.Caps(root, "Training loss", 2, 272, 300);
            MLUI.Well(root, 0, 292, width, 92, 10);
            var chart = MLUI.Rect("Loss sparkline", root, 12, 314, width - 24, 56).gameObject;
            chart.AddComponent<CanvasRenderer>(); sparkline = chart.AddComponent<MLCurve>(); sparkline.externalColor = MLUI.Train;
            sparkline.SetExternalValues(lossHistory);
            lossText = MLUI.Text(root, "loss  —", 14, 298, 260, 14, 9); lossText.color = MLUI.Sub;
            sampleText = MLUI.Text(root, "sample 0", width - 222, 298, 208, 14, 9); sampleText.alignment = TextAlignmentOptions.Right; sampleText.color = MLUI.Sub;

            float buttonY = 400;
            var record = MLUI.Button(root, "Record lap", 2, buttonY, 132, 30, OnRecord);
            recordLabel = record.GetComponentInChildren<TMP_Text>(true);
            MLUI.Button(root, "Train", 142, buttonY, 112, 30, OnTrain);
            var auto = MLUI.Button(root, "Autopilot", 262, buttonY, 132, 30, OnAutopilot);
            autopilotLabel = auto.GetComponentInChildren<TMP_Text>(true);

            // Live only: the version picker. Drone only: the course-line follower, named as not learned.
            var live = new List<GameObject>();
            live.Add(MLUI.Button(root, "‹", 402, buttonY, 30, 30, () => StepVersion(-1)).gameObject);
            versionText = MLUI.Text(root, "no versions yet", 436, buttonY + 7, width - 476, 18, 9.5f);
            live.Add(versionText.gameObject);
            live.Add(MLUI.Button(root, "›", width - 34, buttonY, 30, 30, () => StepVersion(1)).gameObject);
            liveOnly = live.ToArray();
            droneOnly = new[] { MLUI.Button(root, "Follow course line (not learned)", width - 252, 474, 250, 26, OnFollowLine).gameObject };

            // Sample only: what to open, and buttons that open it.
            var sample = new List<GameObject>();
            sample.Add(MLUI.Button(root, "Open slalom (truck)", 402, buttonY, 150, 30, () => OpenCourse("slalom")).gameObject);
            sample.Add(MLUI.Button(root, "Open hoop course (drone)", 560, buttonY, Mathf.Max(150, width - 562), 30, () => OpenCourse("hoop_course")).gameObject);
            sampleOnly = sample.ToArray();

            lapText = MLUI.Text(root, SampleLapsLine(), 2, 438, width - 4, 16, 10); lapText.color = MLUI.Faint;
            trainingText = MLUI.Text(root, NoLoopLine, 2, 452, width - 4, 16, 9); trainingText.color = MLUI.Sub;
            MLUI.Well(root, 2, 468, width - 4, 4, 2);
            trainProgress = MLUI.Fill(root, 2, 468, 0, 4, MLUI.Train); trainProgress.gameObject.SetActive(false);
            ShowSampleChannels();
        }

        const string NoLoopLine = "No truck or drone is learning: open a course (slalom for the truck, hoop course for the drone). Below is a sample preview.";

        static void Show(GameObject[] group, bool on)
        {
            if (group == null) return;
            foreach (var g in group) if (g != null && g.activeSelf != on) g.SetActive(on);
        }

        void ShowSampleChannels()
        {
            inputsCaption.text = "INPUTS  ·  7 RAY SENSORS + SPEED  ·  SAMPLE";
            for (int i = 0; i < InputRows; i++)
            {
                bool used = i < 8;
                string channel = i < 7 ? "ray_front." + (sensors != null && i < sensors.Length ? sensors[i] : SampleChannels[i]) : "speed.forward";
                inputLabels[i].text = used ? SampleLabels[i] + "  <size=75%>" + channel + "</size>" : "";
                inputValues[i].text = used ? "—" : "";
                inputBars[i].gameObject.SetActive(used);
            }
            outputsCaption.text = "OUTPUTS  ·  ACTUATORS  ·  SAMPLE";
            string[] outs = { "STEER  <size=75%>steer.angle</size>", "THROTTLE  <size=75%>drive.throttle</size>" };
            for (int i = 0; i < OutputRows; i++)
            {
                outputLabels[i].text = i < outs.Length ? outs[i] : "";
                outputValues[i].text = i < outs.Length ? "—" : "";
                outputBars[i].gameObject.SetActive(i < outs.Length);
            }
            lastInputNames = lastOutputNames = null;
        }

        // ---- Live --------------------------------------------------------------------------------------------------

        void ApplyLive(ICourseBrain live)
        {
            bool isLive = live != null;
            if (isLive != wasLive || !ReferenceEquals(live, shown))
            {
                wasLive = isLive;
                shown = live;
                lastStatus = lastMode = lastCourse = null;
                lastLaps = lastLoss = lastVersions = lastSelected = lastProgress = -2;
                actionNote = null;
                Show(liveOnly, isLive);
                Show(sampleOnly, !isLive);
                Show(droneOnly, isLive && live.ThingName == "drone");
                Show(truckOnly, !isLive || live.ThingName == "truck");
                if (!isLive)
                {
                    if (training != null) { StopCoroutine(training); training = null; trainProgress.gameObject.SetActive(false); }
                    ShowSampleChannels();
                    lossHistory.Clear(); sparkline.SetExternalValues(lossHistory);
                    lossText.text = "loss  —";
                    titleText.text = "Sample laps  ·  preview";
                    lapText.text = SampleLapsLine();
                    trainingText.text = NoLoopLine;
                    if (recordLabel != null) recordLabel.text = "Record lap";
                    if (autopilotLabel != null) autopilotLabel.text = "Autopilot";
                    SetMode(BrainMode.Ready);
                    return;
                }
            }
            if (!isLive) return;

            liveClock += Time.unscaledDeltaTime;
            if (liveClock < LiveSeconds) return;
            liveClock = 0f;
            LiveChannels(live);
            LiveTexts(live);
        }

        void LiveChannels(ICourseBrain live)
        {
            var inNames = live.InputNames; var inValues = live.InputValues;
            if (!ReferenceEquals(inNames, lastInputNames))
            {
                lastInputNames = inNames;
                int n = inNames != null ? inNames.Length : 0;
                inputsCaption.text = $"INPUTS  ·  {n} {live.ThingName.ToUpperInvariant()} CHANNELS  ·  LIVE";
                for (int i = 0; i < InputRows; i++)
                {
                    bool used = i < n;
                    inputLabels[i].text = used ? inNames[i] : "";
                    inputBars[i].gameObject.SetActive(used);
                    if (!used) inputValues[i].text = "";
                }
            }
            if (inNames != null && inValues != null)
                for (int i = 0; i < InputRows && i < inValues.Length && i < inNames.Length; i++) Bar(inputBars[i], inputValues[i], inValues[i], inputWidth - 184, 10);

            var outNames = live.OutputNames; var outValues = live.OutputValues;
            if (!ReferenceEquals(outNames, lastOutputNames))
            {
                lastOutputNames = outNames;
                outputsCaption.text = "OUTPUTS  ·  ACTUATORS  ·  LIVE";
                int n = outNames != null ? outNames.Length : 0;
                for (int i = 0; i < OutputRows; i++)
                {
                    bool used = i < n;
                    outputLabels[i].text = used ? outNames[i] : "";
                    outputBars[i].gameObject.SetActive(used);
                    if (!used) outputValues[i].text = "";
                }
            }
            if (outNames != null && outValues != null)
                for (int i = 0; i < OutputRows && i < outValues.Length && i < outNames.Length; i++) Bar(outputBars[i], outputValues[i], outValues[i], outputWidth - 220, 12);
        }

        static void Bar(Image bar, TMP_Text text, float value, float width, float height)
        {
            float v = float.IsNaN(value) ? 0f : Mathf.Clamp(value, -1f, 1f);
            bar.rectTransform.sizeDelta = new Vector2(Mathf.Max(1f, width * Mathf.Abs(v)), height);
            text.text = Numbers[Mathf.RoundToInt((v + 1f) * 20f)];
        }

        static string[] BuildNumbers()
        {
            var list = new string[41];
            for (int i = 0; i <= 40; i++) list[i] = (i / 20f - 1f).ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture);
            return list;
        }

        void LiveTexts(ICourseBrain live)
        {
            string modeName = live.ModeName, course = live.CourseId;
            if (modeName != lastMode || course != lastCourse)
            {
                lastMode = modeName; lastCourse = course;
                string thing = live.ThingName == "drone" ? "Drone" : "Truck";
                titleText.text = $"{thing} autopilot  ·  {course ?? "no course open"}  ·  live";
                modeText.text = modeName.ToUpperInvariant();
                modeText.color = modeName == "autopilot" ? MLUI.Good : modeName == "training" ? MLUI.Train : modeName == "lap" ? MLUI.Warn : MLUI.Faint;
                if (recordLabel != null) recordLabel.text = modeName == "lap" ? "Stop lap" : "Record lap";
                if (autopilotLabel != null) autopilotLabel.text = modeName == "autopilot" ? "Stop" : "Autopilot";
            }

            var history = live.LossHistory;
            int count = history != null ? history.Count : 0;
            if (count != lastLoss)
            {
                if (count < lossHistory.Count || lastLoss < 0) lossHistory.Clear();
                for (int i = lossHistory.Count; i < count; i++) lossHistory.Add(history[i]);
                lastLoss = count;
                sparkline.SetExternalValues(lossHistory);
                lossText.text = count > 0 ? $"train loss  {lossHistory[count - 1]:0.0000}  ·  {count} epochs" : "loss  —  (train to see the real curve)";
            }

            float progress = live.TrainProgress;
            int pct = progress < 0f ? -1 : Mathf.RoundToInt(progress * 100f);
            if (pct != lastProgress)
            {
                lastProgress = pct;
                trainProgress.gameObject.SetActive(pct >= 0);
                if (pct >= 0) trainProgress.rectTransform.sizeDelta = new Vector2((root.rect.width - 4) * pct / 100f, 4);
                sampleText.text = pct >= 0 ? $"training  {pct}%" : "";
            }

            int laps = live.LapsCompleted;
            string status = live.StatusLine;
            if (laps != lastLaps || !ReferenceEquals(status, lastStatus))
            {
                lastLaps = laps; lastStatus = status;
                lapText.text = $"{laps} completed lap{(laps == 1 ? "" : "s")} recorded{(laps < 2 ? "  ·  2 are needed to train" : "")}";
                trainingText.text = actionNote ?? status;
                actionNote = null;
            }

            int versions = live.VersionCount, selected = live.SelectedVersion;
            if (versions != lastVersions || selected != lastSelected)
            {
                lastVersions = versions; lastSelected = selected;
                versionText.text = versions == 0 ? "no versions yet" : live.DescribeVersion(selected) ?? $"{versions} versions";
            }
        }

        void Said(string error)
        {
            if (error == null) return;
            actionNote = error;
            if (trainingText != null) trainingText.text = error;
        }

        void StepVersion(int delta)
        {
            var live = Live;
            if (live == null || live.VersionCount == 0) return;
            int next = Mathf.Clamp(live.SelectedVersion + delta, 1, live.VersionCount);
            Said(live.SelectVersion(next));
            lastSelected = -2;
        }

        void OnFollowLine()
        {
            var live = Live;
            if (live == null || live.ThingName != "drone") return;
            var drone = live as Forge.Artifacts.DroneBrain;
            if (drone != null) Said(drone.State == Forge.Artifacts.DroneBrain.Mode.Autopilot ? drone.StopAutopilot() : drone.StartFollowLine());
        }

        static void OpenCourse(string id)
        {
            var host = Forge.Artifacts.ArtifactHost.Current != null ? Forge.Artifacts.ArtifactHost.Current : Forge.Artifacts.ArtifactBootstrap.Ensure();
            if (host == null) { Debug.Log("[ML] No artifact host in this scene: open the course from the Albie menu."); return; }
            host.Open(id);
        }

        // ---- Buttons -----------------------------------------------------------------------------------------------

        void OnRecord()
        {
            var live = Live;
            if (live != null) { Said(live.ModeName == "lap" ? live.StopRecording() : live.Record(1, false)); return; }
            if (training != null) return;
            SetRecording(mode != BrainMode.Recording);
        }

        void OnTrain()
        {
            var live = Live;
            if (live != null) { Said(live.Train()); return; }
            TrainPreview();
        }

        void OnAutopilot()
        {
            var live = Live;
            if (live != null) { Said(live.ModeName == "autopilot" ? live.StopAutopilot() : live.StartAutopilot()); return; }
            if (training != null) return;
            SetAutopilotPreview(mode != BrainMode.Autopilot);
        }

        // ---- Sample preview (no loop running) ------------------------------------------------------------------------

        string SampleLapsLine() => $"Sample laps (C-19 fixture)  ·  {(laps != null ? laps.Count : 0)} laps  ·  preview only";

        public bool SetRecording(bool on)
        {
            if (training != null) return false;
            SetMode(on ? BrainMode.Recording : BrainMode.Ready);
            if (on) recordedSamples = 0;
            if (lapText != null) lapText.text = mode == BrainMode.Recording ? "Sample preview  ·  recording  ·  0 samples" : $"Sample preview  ·  {recordedSamples} samples replayed  ·  nothing was learned";
            return true;
        }

        public bool TrainPreview()
        {
            if (training != null || samples.Count == 0) return false;
            training = StartCoroutine(TrainMock());
            return true;
        }

        IEnumerator TrainMock()
        {
            SetMode(BrainMode.Training); trainProgress.gameObject.SetActive(true); lossHistory.Clear();
            for (int i = 0; i <= 100; i++)
            {
                float progress = i / 100f;
                float loss = Mathf.Lerp(0.18f, 0.006f, progress * progress);
                lossHistory.Add(loss); sparkline.SetExternalValues(lossHistory); lossText.text = "sample curve (not a real training)";
                if (trainingText != null) trainingText.text = $"Sample preview  ·  {progress:0%}  ·  open a course to train for real";
                trainProgress.rectTransform.sizeDelta = new Vector2((root.rect.width - 4) * progress, 4);
                yield return new WaitForSecondsRealtime(0.1f);
            }
            trainProgress.gameObject.SetActive(false); training = null; SetMode(BrainMode.Ready);
            lapText.text = "Sample preview finished  ·  nothing was trained";
            if (trainingText != null) trainingText.text = NoLoopLine;
        }

        public bool SetAutopilotPreview(bool on)
        {
            if (training != null) return false;
            SetMode(on ? BrainMode.Autopilot : BrainMode.Ready);
            if (lapText != null) lapText.text = mode == BrainMode.Autopilot ? "Sample preview  ·  replaying the sample laps (no Autopilot is flying)" : SampleLapsLine();
            return true;
        }

        void ChangeMaxSpeed(float delta)
        {
            maxSpeed = Mathf.Clamp(Mathf.Round((maxSpeed + delta) * 10f) / 10f, 0.1f, 5f);
            if (maxSpeedValue != null) maxSpeedValue.text = maxSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        void ApplyMaxSpeed()
        {
            float value = maxSpeed;
            // The running truck, wherever it runs: the Thing runtime, the controllers sandbox or the slalom's brain truck
            // all register it with ThingHosts.
            var host = ThingHosts.Find(TemplateTools.DefaultThing);
            string error = host == null ? null : host.SetTunable("max_speed", value);
            if (error != null)
            {
                if (controllerStatus != null) controllerStatus.text = "Not applied  ·  " + error;
                return;
            }
            if (controllerStatus != null)
                controllerStatus.text = host == null ? $"No truck running  ·  max speed {value:0.0} m/s not applied" : $"Applied  ·  {host.File.Name} max speed {value:0.0} m/s";
        }

        public JObject ToolState()
        {
            return new JObject
            {
                ["mode"] = mode.ToString().ToLowerInvariant(),
                ["samples"] = SampleCount,
                ["sample"] = samples.Count == 0 ? 0 : sampleIndex + 1,
                ["preview_only"] = true,
                ["controller"] = null,
                ["max_speed_editable"] = true,
                ["note"] = "No truck or drone is learning: the panel replays the C-19 sample laps. Open a course (slalom for the truck, hoop course for the drone) to record, train and fly for real."
            };
        }

        void SetMode(BrainMode next)
        {
            mode = next;
            if (Live != null) return;
            modeText.text = next == BrainMode.Ready ? "SAMPLE" : next.ToString().ToUpperInvariant() + " (SAMPLE)";
            modeText.color = next == BrainMode.Autopilot ? MLUI.Good : next == BrainMode.Training ? MLUI.Train : next == BrainMode.Recording ? MLUI.Warn : MLUI.Faint;
        }

        void RefreshSample()
        {
            if (samples.Count == 0) return;
            var input = samples[sampleIndex]; var output = outputs[sampleIndex];
            for (int i = 0; i < 8 && i < input.Length; i++)
            {
                float value = input[i];
                inputBars[i].rectTransform.sizeDelta = new Vector2(Mathf.Max(1, (inputWidth - 184) * Mathf.Clamp01((value + (i == 7 ? 1 : 0)) / (i == 7 ? 2 : 1))), 10);
                inputValues[i].text = Numbers[Mathf.RoundToInt((Mathf.Clamp(value, -1f, 1f) + 1f) * 20f)];
            }
            for (int i = 0; i < 2 && i < output.Length; i++) Bar(outputBars[i], outputValues[i], output[i], outputWidth - 220, 12);
            sampleText.text = "sample " + (sampleIndex + 1) + " / " + samples.Count;
        }
    }
}
