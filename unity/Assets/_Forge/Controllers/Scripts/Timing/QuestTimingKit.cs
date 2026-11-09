using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
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
    /// 3b. physics truck: the same candidates drive ML A's example truck on Unity physics (<see cref="PhysicsSlalom"/>:
    ///    the mock slalom as colliders, 7 sphere casts a step, course.bearing), one tick per FixedUpdate as the app's
    ///    Thing does, and each physics step's work is timed. This is what the in-app brain loop (the room slalom's
    ///    SlalomBrain) adds over the mock;
    /// 4. ML template: <see cref="TemplateRun"/> makes its dataset and trains a few ms a frame, then saves a file a
    ///    frame, as Albie's template_train does; every frame of its work is timed. Its files go to a scratch folder, not the real controllers folder, and it isn't logged in
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

        public enum Stage { Off, Idle, Training, Driving, Physics, Template, Done }
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
        // Physics truck (its own local physics scene, so nothing else in the scene is in its rays)
        Scene physicsScene;
        ControllerThing physicsThing;
        ExampleTruckBody physicsBody;
        Autopilot physicsAutopilot;
        int physicsGates, physicsTicks, physicsFailed, physicsMaxStepsInFrame, physicsStepsThisFrame;
        float physicsRunSeconds, physicsFrameMs;
        double physicsTotalUs, physicsMaxUs;
        string physicsFailures, physicsDrove;
        bool physicsDone;
        // Template
        double templatePumpBefore;
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
            if (Current == Stage.Physics) { ms = physicsFrameMs; physicsFrameMs = 0f; physicsStepsThisFrame = 0; }
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
                ClosePhysics();
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
                    host = new ControllerHost(new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames));
                    autopilot = new Autopilot(CandidateFiles(), Fallback());
                    string engage = autopilot.Engage(host);
                    if (engage != null) throw new InvalidOperationException(engage);
                    placement = new System.Random(DriverSeed * 7919);
                    PlaceTruck();
                    failedRuns = 0; ticks = 0; tickTotalUs = tickMaxUs = 0; failures = ""; drove = null;
                    return new JObject();
                }
                case Stage.Physics:
                {
                    long t0 = Stopwatch.GetTimestamp();
                    physicsScene = SceneManager.CreateScene("QuestTimingPhysics", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                    physicsThing = PhysicsSlalom.Build(physicsScene);
                    physicsBody = physicsThing.GetComponent<ExampleTruckBody>();
                    if (physicsThing.StartError != null) throw new InvalidOperationException(physicsThing.StartError);
                    double buildMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    physicsAutopilot = new Autopilot(CandidateFiles(), Fallback());
                    string engage = physicsAutopilot.Engage(physicsThing.Host);
                    if (engage != null) throw new InvalidOperationException(engage);
                    placement = new System.Random(DriverSeed * 7919);  // the same starts as the mock drive
                    PlacePhysicsTruck();
                    physicsTicks = physicsFailed = physicsMaxStepsInFrame = physicsStepsThisFrame = 0;
                    physicsTotalUs = physicsMaxUs = 0; physicsFrameMs = 0f;
                    physicsFailures = ""; physicsDrove = null; physicsDone = false;
                    return new JObject { ["fixed_step_ms"] = Round(Time.fixedDeltaTime * 1000.0), ["rays"] = MockTruckSim.Rays, ["built_ms"] = Round(buildMs) };
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
                    templatePumpBefore = 0;
                    return new JObject { ["samples"] = TemplateRun.Current.Samples, ["start_ms"] = Round(ms) };
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
                case Stage.Physics:
                    return physicsDone;
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

        List<ControllerFile> CandidateFiles()
        {
            var files = new List<ControllerFile>();
            foreach (var c in cloners)
            {
                var f = ControllerFile.Parse(c.ToControllerJson("truck_autopilot", 1, 1.5f), out string error);
                if (f == null) throw new InvalidOperationException(error);
                files.Add(f);
            }
            return files;
        }

        static ControllerFile Fallback()
        {
            var fallbackAsset = Resources.Load<TextAsset>("Controllers/truck_autopilot_fallback");
            return fallbackAsset == null ? null : ControllerFile.Parse(fallbackAsset.text, out _);
        }

        // The physics truck: one tick per physics step, as ControllerThing does in the app (its own FixedUpdate is off
        // here so this one can time it). A step is the course.bearing update, then the Thing's tick: 7 sphere casts,
        // the controller, the lap recorder, the move and its overlap test.
        void FixedUpdate()
        {
            if (Current != Stage.Physics || physicsDone || physicsThing == null) return;
            long allocBefore = AllocatedBytes();
            long t0 = Stopwatch.GetTimestamp();
            PhysicsSlalom.Beacon(physicsBody.transform.position, ref physicsGates);
            physicsThing.Tick(Time.fixedDeltaTime);
            double us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
            sampler.Bytes += AllocatedBytes() - allocBefore;
            physicsFrameMs += (float)(us / 1000.0);
            physicsTotalUs += us; if (us > physicsMaxUs) physicsMaxUs = us;
            physicsTicks++;
            if (++physicsStepsThisFrame > physicsMaxStepsInFrame) physicsMaxStepsInFrame = physicsStepsThisFrame;
            physicsRunSeconds += Time.fixedDeltaTime;
            bool finished = PhysicsSlalom.Finished(physicsBody.transform.position);
            if (!physicsBody.Crashed && !finished && physicsRunSeconds < RunTimeoutS) return;
            bool fallbackDriving = physicsAutopilot.UsingFallback;
            int index = physicsAutopilot.Index;
            physicsAutopilot.RunEnded(physicsThing.Host, finished);
            if (finished) { physicsDrove = fallbackDriving ? "the pretrained fallback" : $"candidate {index + 1}"; physicsDone = true; return; }
            physicsFailed++;
            physicsFailures += (physicsFailures.Length > 0 ? ", " : "") + (fallbackDriving ? "fallback" : $"candidate {index + 1}") + (physicsBody.Crashed ? " crashed" : " stalled");
            if (physicsAutopilot.Exhausted) { physicsDrove = "nothing"; physicsDone = true; return; }
            PlacePhysicsTruck();
        }

        void PlacePhysicsTruck()
        {
            float y0 = (float)(placement.NextDouble() * 2 - 1) * StartOffset, turn = (float)(placement.NextDouble() * 2 - 1) * StartTurn;
            physicsBody.Place(new Vector3(0f, 0f, y0), PhysicsSlalom.Heading(turn));
            physicsThing.Host.ResetState();
            physicsGates = 0;
            physicsRunSeconds = 0f;
            PhysicsSlalom.Beacon(physicsBody.transform.position, ref physicsGates);
        }

        void ClosePhysics()
        {
            if (physicsThing == null) return;
            physicsThing.Unregister();
            physicsThing = null;
            physicsBody = null;
            CourseBeacon.Clear();
            if (physicsScene.IsValid() && physicsScene.isLoaded) SceneManager.UnloadSceneAsync(physicsScene);
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

        // Left mid-run (scene change, Play stopped): don't leave the notebook muted or the physics course up.
        void OnDisable() { RestoreTemplateSettings(); ClosePhysics(); }

        // This frame's template work: its pump runs before this Update (DefaultExecutionOrder), so the change in
        // PumpSeconds is this frame's making data, training or saving.
        float TemplateWorkMs()
        {
            double now = TemplateRun.PumpSeconds;
            float ms = (float)((now - templatePumpBefore) * 1000.0);
            templatePumpBefore = now;
            return ms;
        }

        void EndStage()
        {
            // Re-assigning a JObject that's already in the tree would store a copy, so only add a new one.
            if (!(stages[Key(Current)] is JObject result)) stages[Key(Current)] = result = new JObject();
            // Only the training stage's work runs inside this kit's own timer; the template trains in TemplateRun's pump.
            Summarise(result, Current == Stage.Training || Current == Stage.Physics);
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
                    Begin(Stage.Physics);
                    return;
                case Stage.Physics:
                    result["drove"] = physicsDrove;
                    result["failed_runs"] = physicsFailed;
                    if (physicsFailures.Length > 0) result["before_that"] = physicsFailures;
                    result["steps"] = physicsTicks;
                    result["step_us_mean"] = Round(physicsTicks > 0 ? physicsTotalUs / physicsTicks : 0);
                    result["step_us_max"] = Round(physicsMaxUs);
                    result["max_steps_in_a_frame"] = physicsMaxStepsInFrame;
                    ClosePhysics();
                    Begin(Stage.Template);
                    return;
                case Stage.Template:
                    RestoreTemplateSettings();
                    result["state"] = TemplateRun.State.ToString().ToLowerInvariant();
                    if (TemplateRun.State == TemplateRun.Phase.Done)
                    {
                        var t = TemplateRun.Current;
                        result["data_frames"] = t.Maker?.Steps ?? 0;
                        result["dataset_ms"] = Round((t.Maker?.ComputeSeconds ?? 0) * 1000);
                        result["epochs"] = t.Settings.Epochs;
                        result["compute_ms"] = Round(TemplateRun.PumpSeconds * 1000);
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
            if (st["physics_truck"] is JObject p)
            {
                s.Append($"Physics truck: {p["drove"]} drove it{((int)p["failed_runs"] > 0 ? $" (before that {p["before_that"]})" : "")}; a physics step ({p["rays"]} sphere casts + the net) takes {p["step_us_mean"]} µs (max {p["step_us_max"]}), every {p["fixed_step_ms"]} ms\n");
                s.Append($"  frame {p["frame_ms_mean"]} ms (p95 {p["frame_ms_p95"]}, max {p["frame_ms_max"]}), {p["slow_frames"]} slow{Bytes(p)}\n");
            }
            if (st["ml_template"] is JObject m)
            {
                s.Append($"ML template: {m["samples"]} samples made in {m["data_frames"]} frames ({m["dataset_ms"]} ms); made, trained and saved in {m["frames"]} frames, {m["seconds"]} s");
                s.Append(m["val_accuracy"] != null ? $", {Math.Round((double)m["val_accuracy"] * 100, 1)}% held out\n" : $": {m["error"] ?? m["state"]}\n");
                s.Append($"  work {m["work_ms_mean"]} ms a frame (max {m["work_ms_max"]}); frame {m["frame_ms_mean"]} ms (p95 {m["frame_ms_p95"]}, max {m["frame_ms_max"]}), {m["slow_frames"]} slow{Bytes(m)}\n");
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
            sb.Append(Current == Stage.Physics ? "> " : "  ").Append("4. physics truck\n");
            sb.Append(Current == Stage.Template ? "> " : "  ").Append("5. ML template\n");
            return sb.ToString();
        }

        void Show(string message) { if (text != null) text.text = message; }

        static string Key(Stage stage) => stage switch
        {
            Stage.Idle => "idle",
            Stage.Training => "autopilot_training",
            Stage.Driving => "autopilot_driving",
            Stage.Physics => "physics_truck",
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

        // GC.GetAllocatedBytesForCurrentThread reads 0 under Unity's garbage collector. The profiler's "GC Allocated In
        // Frame" counter counts each allocation as it happens (the Editor and development builds; a release build may
        // not have it). If it doesn't count a probe, the report leaves the byte counts out rather than print a false 0.
        ProfilerRecorder gcAllocated;

        bool CheckAllocCounter()
        {
            try
            {
                gcAllocated = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
                if (!gcAllocated.Valid) return false;
                long before = gcAllocated.CurrentValue;
                var probe = new byte[4096];
                probe[0] = 1;
                return gcAllocated.CurrentValue - before >= 4096;
            }
            catch (Exception) { return false; }
        }

        // Read twice in one Update (the counter starts again each frame).
        long AllocatedBytes() => allocCounterWorks ? gcAllocated.CurrentValue : 0;

        void OnDestroy() => gcAllocated.Dispose();

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
