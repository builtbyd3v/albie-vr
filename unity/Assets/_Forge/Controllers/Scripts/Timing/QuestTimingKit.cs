using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.XR;
using Debug = UnityEngine.Debug;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Forge.Controllers
{
    /// <summary>
    /// The Quest timing kit (PROJECT_CONTEXT 16.8: "a timed run on a Quest 3", "72 fps while training"). It runs ML A's
    /// on-device work on the frame loop, the way the demo does, and times every frame:
    /// 1. idle: <see cref="idleFrames"/> frames with nothing running, the headset's baseline;
    /// 2. Autopilot training: two demo laps (<see cref="MockLaps"/>), then 3 candidates trained <see cref="budgetMs"/>
    ///    ms a frame, as the Controllers sandbox's B key does;
    /// 3. Autopilot driving: the Autopilot drives the mock slalom with those candidates, then the pretrained fallback,
    ///    and each controller tick is timed (on a Thing it's one tick per physics step);
    /// 4. ML template: <see cref="TemplateRun"/> makes its dataset in one frame and trains a few ms a frame, as Albie's
    ///    template_train does. Its files go to a scratch folder, not the real controllers folder, and it isn't logged in
    ///    the lab notebook (<see cref="TrainingLog.Muted"/>).
    /// The panel shows the results; they're also logged (<c>adb logcat -s Unity</c>, lines start "[QuestTiming]") and
    /// saved as JSON in <c>persistentDataPath/quest_timing/</c>. It starts by itself; A, X, a trigger, a pinch or R
    /// (Editor) runs it again. A frame is "slow" when it took more than 1.5 frames of the display's refresh.
    /// Allocation-free while a stage is sampling; the setup between stages allocates (and is reported on its own).
    /// </summary>
    public sealed class QuestTimingKit : MonoBehaviour
    {
        public TMP_Text text;
        /// <summary>Training time per frame, as the sandbox and TemplateRun use (72 fps leaves about 14 ms a frame).</summary>
        public float budgetMs = 4f;
        public int idleFrames = 300;
        public bool runOnStart = true;
        public const int MaxFrames = 8192, Candidates = 3, TicksPerFrame = 50, DriverSeed = 501;
        const float Dt = 0.02f, RunTimeoutS = 30f;
        // Put down by hand, as FiveInARowCheck does (same seeded placement), so its first attempt is the one to compare
        // with: in the Editor, candidate 1 drives it.
        const float StartOffset = 0.15f, StartTurn = 0.08f;

        public enum Stage { Off, Idle, Training, Driving, Template, Done }
        public Stage Current { get; private set; }
        public bool Running => Current != Stage.Off && Current != Stage.Done;
        /// <summary>The last finished report (null before the first one).</summary>
        public JObject Report { get; private set; }
        public string ReportText { get; private set; } = "";
        public string SavedPath { get; private set; }

        /// <summary>One stage's frames: the work done in each frame and the time until the next frame started.</summary>
        sealed class Sampler
        {
            public readonly float[] Work = new float[MaxFrames], Interval = new float[MaxFrames];
            public int Count;
            public bool Awaiting;
            public long Bytes;
            public double StartedAt;
            public void Clear() { Count = 0; Awaiting = false; Bytes = 0; }
        }

        readonly Sampler sampler = new Sampler();
        readonly float[] scratch = new float[MaxFrames];
        readonly StringBuilder sb = new StringBuilder(2048);
        JObject stages;
        bool closing, allocCounterWorks;
        float refreshHz;
        bool xr;

        // Training and driving
        BehaviourCloner[] cloners;
        int training;
        ControllerHost host;
        Autopilot autopilot;
        readonly MockTruckSim sim = new MockTruckSim();
        int failedRuns, ticks;
        System.Random placement;
        double tickTotalUs, tickMaxUs;
        string failures, drove;
        // Template
        double templateComputeBefore;
        string savedFolderOverride;
        bool savedMuted, templateSettings;

        void Start()
        {
            allocCounterWorks = CheckAllocCounter();
            if (runOnStart) Run();
            else Show("Quest timing kit: press A, X, a trigger or pinch to run it.");
        }

        /// <summary>Starts a full run (ignored while one is running).</summary>
        public void Run()
        {
            if (Running) return;
            refreshHz = RefreshRate(out xr);
            stages = new JObject();
            Report = null;
            Begin(Stage.Idle);
        }

        void Update()
        {
            if (!Running)
            {
                if (RunPressed()) Run();
                return;
            }
            // The frame that just ended: its interval belongs to the last sample.
            if (sampler.Awaiting)
            {
                sampler.Interval[sampler.Count - 1] = Time.unscaledDeltaTime * 1000f;
                sampler.Awaiting = false;
                if (closing) { closing = false; EndStage(); return; }
            }
            long allocBefore = AllocatedBytes();
            long t0 = Stopwatch.GetTimestamp();
            bool done = Work();
            float ms = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            if (Current == Stage.Template) ms = TemplateWorkMs();
            sampler.Bytes += AllocatedBytes() - allocBefore;
            if (sampler.Count < MaxFrames) { sampler.Work[sampler.Count++] = ms; sampler.Awaiting = true; }
            if (done || sampler.Count >= MaxFrames) closing = true;
        }

        // ---- Stages ----------------------------------------------------------------------------------------------

        void Begin(Stage stage)
        {
            Current = stage;
            sampler.Clear();
            closing = false;
            Show(Progress());
            long t0 = Stopwatch.GetTimestamp();
            JObject setup;
            try { setup = Setup(stage); }
            catch (Exception e)
            {
                Debug.LogException(e);
                RestoreTemplateSettings();
                Current = Stage.Done;
                Show($"Quest timing kit stopped at {Key(stage)}: {e.Message}");
                return;
            }
            double setupMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (setup != null) { setup["setup_ms"] = Round(setupMs); stages[Key(stage)] = setup; }
            sampler.StartedAt = Time.realtimeSinceStartupAsDouble;
        }

        // Runs once, in its own frame, before the stage samples (that frame isn't counted).
        JObject Setup(Stage stage)
        {
            switch (stage)
            {
                case Stage.Training:
                {
                    long t0 = Stopwatch.GetTimestamp();
                    var laps = MockLaps.Record(DriverSeed, 1f, MockLaps.Style.Demo, out float lapSeconds, out int redos);
                    double lapMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    cloners = new BehaviourCloner[Candidates];
                    for (int i = 0; i < Candidates; i++) cloners[i] = new BehaviourCloner(laps, new BehaviourCloner.Options { Seed = i + 1 });
                    training = 0;
                    return new JObject { ["laps"] = laps.CompletedLaps, ["lap_seconds"] = Round(lapSeconds), ["laps_redone"] = redos, ["laps_recorded_ms"] = Round(lapMs), ["rows"] = cloners[0].TrainCount, ["candidates"] = Candidates };
                }
                case Stage.Driving:
                {
                    var files = new List<ControllerFile>();
                    foreach (var c in cloners)
                    {
                        var f = ControllerFile.Parse(c.ToControllerJson("truck_autopilot", 1, 1.5f), out string error);
                        if (f == null) throw new InvalidOperationException(error);
                        files.Add(f);
                    }
                    var fallbackAsset = Resources.Load<TextAsset>("Controllers/truck_autopilot_fallback");
                    var fallback = fallbackAsset == null ? null : ControllerFile.Parse(fallbackAsset.text, out _);
                    host = new ControllerHost(new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames));
                    autopilot = new Autopilot(files, fallback);
                    string engage = autopilot.Engage(host);
                    if (engage != null) throw new InvalidOperationException(engage);
                    placement = new System.Random(DriverSeed * 7919);
                    PlaceTruck();
                    failedRuns = 0; ticks = 0; tickTotalUs = tickMaxUs = 0; failures = ""; drove = null;
                    return new JObject();
                }
                case Stage.Template:
                {
                    savedFolderOverride = TemplateRun.FolderOverride;
                    savedMuted = TrainingLog.Muted;
                    TrainingLog.Muted = true; // a timing run isn't an experiment: no notebook entry
                    templateSettings = true;
                    TemplateRun.FolderOverride = Path.Combine(Application.persistentDataPath, "quest_timing", "template");
                    TemplateRun.BudgetMs = budgetMs;
                    long t0 = Stopwatch.GetTimestamp();
                    string error = TemplateRun.Start();
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    if (error != null) throw new InvalidOperationException(error);
                    templateComputeBefore = 0;
                    return new JObject { ["samples"] = TemplateRun.Current.Data.Count, ["dataset_ms"] = Round(ms) };
                }
                default:
                    return new JObject();
            }
        }

        // One frame of the stage's work. Returns true when the stage is done.
        bool Work()
        {
            switch (Current)
            {
                case Stage.Idle:
                    return sampler.Count + 1 >= idleFrames;
                case Stage.Training:
                    if (cloners[training].Step(budgetMs)) training++;
                    return training >= cloners.Length;
                case Stage.Driving:
                    return Drive();
                case Stage.Template:
                    return !TemplateRun.Training;
                default:
                    return true;
            }
        }

        // The Autopilot on the mock slalom, TicksPerFrame ticks a frame; each controller tick is timed.
        bool Drive()
        {
            for (int i = 0; i < TicksPerFrame; i++)
            {
                sim.Sense(host.IO.Sensors);
                long t0 = Stopwatch.GetTimestamp();
                host.Step(Dt, sim.Speed);
                double us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
                tickTotalUs += us; if (us > tickMaxUs) tickMaxUs = us; ticks++;
                sim.Step(host.IO.Actuators, Dt);
                if (!sim.Crashed && !sim.Finished && sim.Time < RunTimeoutS) continue;
                bool fallbackDriving = autopilot.UsingFallback;
                int index = autopilot.Index;
                autopilot.RunEnded(host, sim.Finished);
                if (sim.Finished) { drove = fallbackDriving ? "the pretrained fallback" : $"candidate {index + 1}"; return true; }
                failedRuns++;
                failures += (failures.Length > 0 ? ", " : "") + (fallbackDriving ? "fallback" : $"candidate {index + 1}") + (sim.Crashed ? " crashed" : " stalled");
                if (autopilot.Exhausted) { drove = "nothing"; return true; }
                PlaceTruck();
            }
            return false;
        }

        void PlaceTruck()
        {
            float y0 = (float)(placement.NextDouble() * 2 - 1) * StartOffset, turn = (float)(placement.NextDouble() * 2 - 1) * StartTurn;
            sim.Reset(y0, turn);
            host.ResetState();
        }

        void RestoreTemplateSettings()
        {
            if (!templateSettings) return;
            templateSettings = false;
            TemplateRun.FolderOverride = savedFolderOverride;
            TrainingLog.Muted = savedMuted;
        }

        // Left mid-run (scene change, Play stopped): don't leave the notebook muted.
        void OnDisable() => RestoreTemplateSettings();

        float TemplateWorkMs()
        {
            var c = TemplateRun.Current?.Classifier;
            if (c == null) return 0f;
            double now = c.ComputeSeconds;
            float ms = (float)((now - templateComputeBefore) * 1000.0);
            templateComputeBefore = now;
            return ms;
        }

        void EndStage()
        {
            // Re-assigning a JObject that's already in the tree would store a copy, so only add a new one.
            if (!(stages[Key(Current)] is JObject result)) stages[Key(Current)] = result = new JObject();
            // Only the training stage's work runs inside this kit's own timer; the template trains in TemplateRun's pump.
            Summarise(result, Current == Stage.Training);
            switch (Current)
            {
                case Stage.Training:
                    double compute = 0;
                    foreach (var c in cloners) compute += c.ComputeSeconds;
                    result["compute_ms"] = Round(compute * 1000);
                    result["epochs"] = cloners[0].Settings.Epochs;
                    Begin(Stage.Driving);
                    return;
                case Stage.Driving:
                    result["drove"] = drove;
                    result["failed_runs"] = failedRuns;
                    if (failures.Length > 0) result["before_that"] = failures;
                    result["ticks"] = ticks;
                    result["tick_us_mean"] = Round(ticks > 0 ? tickTotalUs / ticks : 0);
                    result["tick_us_max"] = Round(tickMaxUs);
                    Begin(Stage.Template);
                    return;
                case Stage.Template:
                    RestoreTemplateSettings();
                    result["state"] = TemplateRun.State.ToString().ToLowerInvariant();
                    if (TemplateRun.State == TemplateRun.Phase.Done)
                    {
                        var c = TemplateRun.Current.Classifier;
                        result["epochs"] = c.Settings.Epochs;
                        result["compute_ms"] = Round(c.ComputeSeconds * 1000);
                        result["wall_seconds"] = Round(TemplateRun.WallSeconds);
                        result["val_accuracy"] = TemplateRun.TrainingBlock?["val_accuracy"];
                    }
                    else result["error"] = TemplateRun.Error;
                    Finish();
                    return;
                default:
                    Begin(Stage.Training);
                    return;
            }
        }

        // Frame statistics for the stage just sampled.
        void Summarise(JObject result, bool countBytes)
        {
            int n = sampler.Count;
            float target = 1000f / refreshHz, slowAt = 1.5f * target;
            double workSum = 0, workMax = 0, intervalSum = 0, intervalMax = 0;
            int slow = 0;
            for (int i = 0; i < n; i++)
            {
                workSum += sampler.Work[i]; workMax = Math.Max(workMax, sampler.Work[i]);
                float iv = sampler.Interval[i];
                intervalSum += iv; intervalMax = Math.Max(intervalMax, iv);
                if (iv > slowAt) slow++;
                scratch[i] = iv;
            }
            Array.Sort(scratch, 0, n);
            double seconds = Time.realtimeSinceStartupAsDouble - sampler.StartedAt;
            result["frames"] = n;
            result["seconds"] = Round(seconds);
            result["fps"] = Round(seconds > 0 ? n / seconds : 0);
            result["work_ms_mean"] = Round(n > 0 ? workSum / n : 0);
            result["work_ms_max"] = Round(workMax);
            result["frame_ms_mean"] = Round(n > 0 ? intervalSum / n : 0);
            result["frame_ms_p95"] = Round(n > 0 ? scratch[Math.Min(n - 1, (int)(n * 0.95))] : 0);
            result["frame_ms_max"] = Round(intervalMax);
            result["slow_frames"] = slow;
            if (countBytes && allocCounterWorks) result["allocated_bytes"] = sampler.Bytes;
        }

        void Finish()
        {
            Current = Stage.Done;
            var training = (JObject)stages["autopilot_training"];
            var template = (JObject)stages["ml_template"];
            var idle = (JObject)stages["idle"];
            bool holds = Holds(training, idle) && Holds(template, idle);
            Report = new JObject
            {
                ["schema"] = "albie.quest_timing/1",
                ["when"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["device"] = SystemInfo.deviceModel,
                ["cpu"] = $"{SystemInfo.processorType}, {SystemInfo.processorCount} cores",
                ["graphics"] = SystemInfo.graphicsDeviceType.ToString(),
                ["xr"] = xr,
                ["editor"] = Application.isEditor,
                ["unity"] = Application.unityVersion,
                ["refresh_hz"] = Round(refreshHz),
                ["budget_ms"] = budgetMs,
                ["holds_frame_rate"] = holds,
                ["stages"] = stages,
            };
            ReportText = Describe(Report);
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "quest_timing");
                Directory.CreateDirectory(dir);
                SavedPath = Path.Combine(dir, "timing_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".json");
                File.WriteAllText(SavedPath, Report.ToString(Formatting.Indented));
                Report["saved"] = SavedPath;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { SavedPath = null; Report["save_error"] = e.Message; }
            foreach (string line in ReportText.Split('\n')) if (line.Length > 0) Debug.Log("[QuestTiming] " + line);
            Debug.Log("[QuestTiming] json " + Report.ToString(Formatting.None));
            Show(ReportText + (SavedPath != null ? "\nSaved " + SavedPath : "") + "\nA, X, a trigger or a pinch runs it again.");
        }

        // A training stage holds the frame rate when it has no more slow frames than idle had (as a share), plus 1% of
        // its frames and at least 1: training mustn't add dropped frames to what the headset (or Editor) drops anyway.
        static bool Holds(JObject stage, JObject idle)
        {
            if (stage?["slow_frames"] == null) return false;
            int frames = (int)stage["frames"], slow = (int)stage["slow_frames"];
            double idleShare = idle?["frames"] != null && (int)idle["frames"] > 0 ? (int)idle["slow_frames"] / (double)(int)idle["frames"] : 0;
            return slow <= Math.Max(1, frames * (idleShare + 0.01));
        }

        /// <summary>The panel text for a report.</summary>
        public static string Describe(JObject r)
        {
            var s = new StringBuilder();
            bool editor = (bool)r["editor"];
            double hz = (double)r["refresh_hz"];
            s.Append($"{(editor ? "EDITOR (not a headset)" : r["device"])}  {hz} Hz ({Math.Round(1000 / hz, 1)} ms a frame)  training budget {r["budget_ms"]} ms a frame\n");
            s.Append((bool)r["holds_frame_rate"] ? "HOLDS THE FRAME RATE while training (no more slow frames than idle)\n" : "DROPS FRAMES while training: more slow frames than idle, see below\n");
            if (editor) s.Append("Editor frame times include the Editor itself; the headset's are the ones that count.\n");
            var st = (JObject)r["stages"];
            if (st["idle"] is JObject idle) s.Append($"Idle: {idle["fps"]} fps, frame {idle["frame_ms_mean"]} ms (p95 {idle["frame_ms_p95"]}, max {idle["frame_ms_max"]}), {idle["slow_frames"]} slow\n");
            if (st["autopilot_training"] is JObject t)
            {
                s.Append($"Autopilot training: {t["candidates"]} nets in {t["frames"]} frames, {t["seconds"]} s ({t["compute_ms"]} ms compute)\n");
                s.Append($"  training {t["work_ms_mean"]} ms a frame (max {t["work_ms_max"]}); frame {t["frame_ms_mean"]} ms (p95 {t["frame_ms_p95"]}, max {t["frame_ms_max"]}), {t["slow_frames"]} slow{Bytes(t)}\n");
                s.Append($"  laps recorded in {t["laps_recorded_ms"]} ms before it\n");
            }
            if (st["autopilot_driving"] is JObject d)
                s.Append($"Autopilot driving: {d["drove"]} drove it{((int)d["failed_runs"] > 0 ? $" (before that {d["before_that"]})" : "")}; a tick takes {d["tick_us_mean"]} µs (max {d["tick_us_max"]})\n");
            if (st["ml_template"] is JObject m)
            {
                s.Append($"ML template: {m["samples"]} samples made in {m["dataset_ms"]} ms (one frame); trained in {m["frames"]} frames, {m["seconds"]} s");
                s.Append(m["val_accuracy"] != null ? $", {Math.Round((double)m["val_accuracy"] * 100, 1)}% held out\n" : $": {m["error"] ?? m["state"]}\n");
                s.Append($"  training {m["work_ms_mean"]} ms a frame (max {m["work_ms_max"]}); frame {m["frame_ms_mean"]} ms (p95 {m["frame_ms_p95"]}, max {m["frame_ms_max"]}), {m["slow_frames"]} slow{Bytes(m)}\n");
            }
            return s.ToString();
        }

        static string Bytes(JObject stage) => stage["allocated_bytes"] != null ? $", {stage["allocated_bytes"]} bytes allocated" : "";

        string Progress()
        {
            sb.Clear();
            sb.Append("Quest timing kit: running, keep still.\n");
            sb.Append(Current == Stage.Idle ? "> " : "  ").Append("1. idle baseline\n");
            sb.Append(Current == Stage.Training ? "> " : "  ").Append("2. Autopilot training (3 nets)\n");
            sb.Append(Current == Stage.Driving ? "> " : "  ").Append("3. Autopilot driving\n");
            sb.Append(Current == Stage.Template ? "> " : "  ").Append("4. ML template\n");
            return sb.ToString();
        }

        void Show(string message) { if (text != null) text.text = message; }

        static string Key(Stage stage) => stage switch
        {
            Stage.Idle => "idle",
            Stage.Training => "autopilot_training",
            Stage.Driving => "autopilot_driving",
            Stage.Template => "ml_template",
            _ => "done",
        };

        static double Round(double v) => Math.Round(v, 2);

        // ---- Device -----------------------------------------------------------------------------------------------

        static readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();

        /// <summary>The headset's refresh rate, or the screen's when no XR display is running.</summary>
        static float RefreshRate(out bool xr)
        {
            displays.Clear();
            SubsystemManager.GetSubsystems(displays);
            foreach (var d in displays)
                if (d.running && d.TryGetDisplayRefreshRate(out float hz) && hz > 1f) { xr = true; return hz; }
            xr = false;
            double screen = Screen.currentResolution.refreshRateRatio.value;
            return screen > 1 ? (float)screen : 60f;
        }

        // IL2CPP may not count allocations; then the report leaves the byte counts out rather than print a false 0.
        static bool CheckAllocCounter()
        {
            try
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var probe = new byte[4096];
                probe[0] = 1;
                return GC.GetAllocatedBytesForCurrentThread() - before >= 4096;
            }
            catch (Exception) { return false; }
        }

        long AllocatedBytes() => allocCounterWorks ? GC.GetAllocatedBytesForCurrentThread() : 0;

        static bool RunPressed()
        {
            if (OVRInput.GetDown(OVRInput.RawButton.A | OVRInput.RawButton.X | OVRInput.RawButton.LIndexTrigger | OVRInput.RawButton.RIndexTrigger)) return true;
            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.Hands)) return true;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null && k.rKey.wasPressedThisFrame) return true;
#endif
            return false;
        }
    }
}
