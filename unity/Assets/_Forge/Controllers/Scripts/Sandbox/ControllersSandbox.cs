using System.IO;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Forge.Controllers
{
    /// <summary>
    /// Desktop sandbox for the controller runner and behaviour cloning (hub off, mock first). Drives
    /// <see cref="MockTruckSim"/> through a <see cref="ControllerHost"/> with the mock controller files in
    /// Resources/Mocks/controllers.
    /// Keys: 1 manual, 2 cruise (graph) · W/S or ↑/↓ throttle, A/D or ←/→ steer (manual) · [ / ] max_speed −/+ 0.2 m/s
    /// (a live tunable change) · R reset · T record a lap you drive (T again to stop; it also stops at the finish or a
    /// crash) · L record a lap the scripted driver drives · B train 3 candidate nets on the recorded laps (4 ms a
    /// frame) and save the laps and the candidates' controller files. One training run can fail to drive the course
    /// (PROJECT_CONTEXT 16.8), so the autopilot (next step) tries the candidates in order. The sandbox also scores each
    /// candidate on the mock slalom. P: the Autopilot drives with the trained candidates (or the mock autopilot file
    /// if nothing is trained yet); when a run crashes, the next candidate takes over, then the pretrained fallback.
    /// 3 attaches the mock autopilot file. E exports the running controller to a file and watches it: save an edit
    /// (say, max_speed or a PID gain) and it hot-reloads onto the running truck.
    /// M runs the ML template (<see cref="TemplateRun"/>, the same run Albie's template_train starts): it makes the
    /// "obstacle ahead" dataset, trains the net a few ms a frame, saves obstacle_ahead.json, logs the run in the notebook,
    /// and attaches truck_cruise with the learned brake in place of its hand-written one. 4 attaches it again after that;
    /// 2 is the hand-written brake, to compare. The mock truck is registered as the "truck" Thing (<see cref="ThingHosts"/>),
    /// so Albie's learned_brake tool drives it too.
    /// The readout is a sandbox debug overlay, not product UI.
    /// </summary>
    public sealed class ControllersSandbox : MonoBehaviour
    {
        public Transform truck;
        public LineRenderer[] rays;
        // Code-only, not a serialized field: a scene saved with an older, shorter list would override it (that broke key 3).
        static readonly string[] ControllerFiles = { "Mocks/controllers/truck_manual", "Mocks/controllers/truck_cruise", "Mocks/controllers/truck_autopilot" };
        /// <summary>Training time per frame. 72 fps leaves about 14 ms a frame.</summary>
        public float trainBudgetMs = 4f;
        /// <summary>Nets trained per B press, with seeds 1..n. The best of 3 drove the mock slalom in every sweep.</summary>
        public int candidates = 3;

        readonly MockTruckSim sim = new MockTruckSim();
        ControllerHost host;
        LapRecorder recorder;
        MockDriver autoDriver;
        BehaviourCloner[] cloners;
        int training;
        string[] candidateJson;
        bool templateMine;
        Autopilot autopilot;
        bool runReported;
        ControllerFileWatcher watcher;
        int stickY, handRoll, lapSeed = 1;
        float lastKeyThrottle, lastKeySteer, trainStarted;
        string readout = "", status = "";
        float nextReadout;
        readonly System.Text.StringBuilder sb = new System.Text.StringBuilder(512);

        public ControllerHost Host => host;
        public MockTruckSim Sim => sim;
        public LapRecorder Recorder => recorder;
        /// <summary>The candidate being trained, or the last one when all are done (null before B).</summary>
        public BehaviourCloner Cloner => cloners == null ? null : cloners[System.Math.Min(training, cloners.Length - 1)];
        public BehaviourCloner[] Candidates => cloners;
        public bool Training => cloners != null && training < cloners.Length;
        public string Status => status;
        public Autopilot Autopilot => autopilot;
        public ControllerFileWatcher Watcher => watcher;
        /// <summary>The ML template run (null before M or template_train).</summary>
        public ObstacleTemplate Template => TemplateRun.Current;
        public bool TemplateTraining => TemplateRun.Training;
        /// <summary>truck_cruise with the learned brake, once a template run has finished (null before).</summary>
        public string LearnedBrakeJson => TemplateRun.LearnedBrake?.ToString();
        /// <summary>Where B saves the laps and the learned controller.</summary>
        public static string SaveFolder => Path.Combine(Application.persistentDataPath, "controllers");

        void Awake()
        {
            host = new ControllerHost(new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames));
            host.Faulted += reason => status = "FAULT " + reason;
            host.Reloaded += (file, kept) => status = $"hot reload: {file.Name} v{file.Version} ({(kept ? "state kept" : "state reset")}), limit {ControllerFile.Fmt(file.SpeedLimit)} m/s";
            host.ReloadFailed += reason => status = "hot reload refused, still driving the old one: " + reason;
            recorder = new LapRecorder(host.IO, "truck", 2, "truck_manual", MockTruckSim.MirrorSwap, MockTruckSim.MirrorNegate);
            stickY = ManualInputs.IndexOf("stick.y");
            handRoll = ManualInputs.IndexOf("hand.roll");
            Use(0);
        }

        void OnEnable()
        {
            ThingHosts.Register("truck", host);
            TemplateRun.Finished += OnTemplateFinished;
        }

        void OnDisable()
        {
            ThingHosts.Unregister("truck", host);
            TemplateRun.Finished -= OnTemplateFinished;
        }

        /// <summary>Attaches controller file <paramref name="index"/> from <see cref="ControllerFiles"/>.</summary>
        public string Use(int index)
        {
            var asset = Resources.Load<TextAsset>(ControllerFiles[index]);
            if (asset == null) return status = $"missing Resources/{ControllerFiles[index]}.json (run `python -m ml.make_controller_mocks` in hub/)";
            var file = ControllerFile.Parse(asset.text, out string error);
            error = error ?? host.Attach(file);
            if (error == null) autopilot = null;
            status = error ?? $"attached {file.Name} ({file.Kind}, v{file.Version})";
            return error;
        }

        /// <summary>
        /// Starts the Autopilot from the start line with the trained candidates, in order, or with the mock autopilot
        /// file when nothing has been trained in this session.
        /// </summary>
        public string StartAutopilot()
        {
            if (Training) return status = "training: wait for it to finish";
            if (recorder.Recording) StopLap("aborted");
            var files = new System.Collections.Generic.List<ControllerFile>();
            string source;
            if (candidateJson != null)
            {
                foreach (var json in candidateJson)
                {
                    var f = ControllerFile.Parse(json, out string e);
                    if (f != null) files.Add(f);
                }
                source = $"{files.Count} trained candidates";
            }
            else
            {
                var asset = Resources.Load<TextAsset>("Mocks/controllers/truck_autopilot");
                var f = asset == null ? null : ControllerFile.Parse(asset.text, out _);
                if (f == null) return status = "nothing trained and no mock autopilot file";
                files.Add(f);
                source = "the mock autopilot file";
            }
            ManualInputs.Clear();
            var fallbackAsset = Resources.Load<TextAsset>("Controllers/truck_autopilot_fallback");
            var fallback = fallbackAsset == null ? null : ControllerFile.Parse(fallbackAsset.text, out _);
            autopilot = new Autopilot(files, fallback);
            string error = autopilot.Engage(host);
            sim.Reset(); host.ResetState(); runReported = false;
            status = error ?? $"Autopilot on with {source}{(fallback != null ? " (+ pretrained fallback)" : "")}: {autopilot.Describe()} driving";
            return error;
        }

        /// <summary>
        /// Writes the running controller to <c>SaveFolder/live/&lt;name&gt;.json</c> and watches it: a saved edit
        /// hot-reloads onto the running truck. Returns the path, or null with no controller attached.
        /// </summary>
        public string WatchCurrentFile()
        {
            if (host.File == null) { status = "no controller file attached"; return null; }
            string dir = Path.Combine(SaveFolder, "live");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, host.File.Name + ".json");
            ControllerFileWatcher.WriteAtomic(path, host.File.Raw.ToString());
            watcher = new ControllerFileWatcher(host, path);
            status = "edit and save " + path + ": it reloads live";
            return path;
        }

        /// <summary>The ML template: makes the dataset and starts training (a few ms a frame). M attaches the brake when it's done.</summary>
        public string StartTemplate()
        {
            if (TemplateTraining) return status = "the ML template is already training";
            var started = System.Diagnostics.Stopwatch.StartNew();
            string error = TemplateRun.Start(TemplateRun.LoadDefaultBase());
            if (error != null) return status = error;
            templateMine = true;
            var t = TemplateRun.Current;
            status = $"ML template: {t.Data.Count} samples made in {started.ElapsedMilliseconds} ms ({t.Data.Positives} obstacle ahead); training";
            return null;
        }

        /// <summary>Attaches truck_cruise with the learned brake (after M). Returns the error, or null.</summary>
        public string UseLearnedBrake()
        {
            if (TemplateRun.LearnedBrake == null) return status = "no learned brake yet: press M to train it";
            string error = TemplateRun.AttachBrake(host);
            if (error == null) autopilot = null;
            status = error ?? $"attached {host.File.Name}: the trained net brakes when it sees an obstacle ahead";
            return error;
        }

        // Any template run (M here, or Albie's template_train): show how it did. Only M attaches the brake by itself.
        void OnTemplateFinished()
        {
            bool mine = templateMine;
            templateMine = false;
            if (TemplateRun.State != TemplateRun.Phase.Done) { status = TemplateRun.Error; return; }
            var c = TemplateRun.Current.Classifier;
            c.Evaluate(out var model, out var baseline, out float threshold);
            string saved = TemplateRun.SaveError == null ? "saved to " + TemplateRun.ModelPath : "saving failed: " + TemplateRun.SaveError;
            status = $"ML template trained in {c.Steps} frames ({TemplateRun.WallSeconds:0.0} s, {c.ComputeSeconds * 1000:0} ms compute): " +
                     $"{model.Accuracy:P1} right on held-out samples, vs {baseline.Accuracy:P1} for \"{ObstacleClassifier.FrontRay} < {threshold:0.00}\". {saved}";
            if (mine) { string result = status; ResetTruck(); string error = UseLearnedBrake(); status = result + (error == null ? "  Attached the learned brake." : "  " + error); }
        }

        public void ResetTruck() { sim.Reset(); host.ResetState(); runReported = false; status = "reset"; }

        /// <summary>Starts recording a lap from the start line. With <paramref name="scripted"/>, the scripted driver drives it.</summary>
        public void StartLap(bool scripted)
        {
            if (Training) { status = "training: wait for it to finish"; return; }
            if (host.Current == null || host.Current.Kind != "manual") Use(0);
            ResetTruck();
            autoDriver = scripted ? new MockDriver(lapSeed++) : null;
            autopilot = null;
            recorder.BeginLap();
            status = scripted ? "recording a scripted lap" : "recording: drive the slalom (T to stop)";
        }

        /// <summary>Ends the lap being recorded, keeping it with this outcome.</summary>
        public void StopLap(string outcome)
        {
            if (!recorder.Recording) return;
            var lap = recorder.EndLap(outcome);
            if (autoDriver != null) ManualInputs.Clear();
            autoDriver = null;
            status = lap == null ? "nothing recorded" : $"lap {lap.Id} {lap.Outcome}: {lap.X.Length} frames, {lap.Seconds} s ({recorder.Set.CompletedLaps} completed)";
        }

        /// <summary>Starts training the candidates on the completed laps. Training runs in Update, a few ms a frame.</summary>
        public string Train()
        {
            if (Training) return status = "already training";
            if (recorder.Recording) StopLap("aborted");
            if (recorder.Set.CompletedLaps == 0) return status = "no completed laps yet: record one with T (or L)";
            cloners = new BehaviourCloner[System.Math.Max(1, candidates)];
            for (int i = 0; i < cloners.Length; i++)
                cloners[i] = new BehaviourCloner(recorder.Set, new BehaviourCloner.Options { Seed = i + 1 });
            training = 0;
            trainStarted = Time.unscaledTime;
            status = $"training {cloners.Length} candidates on {recorder.Set.CompletedLaps} laps ({cloners[0].TrainCount} rows)";
            return null;
        }

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null)
            {
                // Write only when the keys change, so other input sources (the hands transmitter, voice, a test)
                // aren't overwritten by an idle keyboard every frame.
                float throttle = (k.wKey.isPressed || k.upArrowKey.isPressed ? 1f : 0f) - (k.sKey.isPressed || k.downArrowKey.isPressed ? 1f : 0f);
                float steer = (k.aKey.isPressed || k.leftArrowKey.isPressed ? 1f : 0f) - (k.dKey.isPressed || k.rightArrowKey.isPressed ? 1f : 0f);
                if (throttle != lastKeyThrottle) { ManualInputs.Values[stickY] = throttle; lastKeyThrottle = throttle; }
                if (steer != lastKeySteer) { ManualInputs.Values[handRoll] = steer; lastKeySteer = steer; }
                if (k.digit1Key.wasPressedThisFrame) Use(0);
                if (k.digit2Key.wasPressedThisFrame) Use(1);
                if (k.digit3Key.wasPressedThisFrame) Use(2);
                if (k.pKey.wasPressedThisFrame) StartAutopilot();
                if (k.eKey.wasPressedThisFrame) WatchCurrentFile();
                if (k.rKey.wasPressedThisFrame) ResetTruck();
                if (k.leftBracketKey.wasPressedThisFrame) Tune(-0.2f);
                if (k.rightBracketKey.wasPressedThisFrame) Tune(0.2f);
                if (k.tKey.wasPressedThisFrame) { if (recorder.Recording) StopLap("aborted"); else StartLap(false); }
                if (k.lKey.wasPressedThisFrame && !recorder.Recording) StartLap(true);
                if (k.bKey.wasPressedThisFrame) Train();
                if (k.mKey.wasPressedThisFrame) StartTemplate();
                if (k.digit4Key.wasPressedThisFrame) UseLearnedBrake();
            }
#endif
            if (Training && cloners[training].Step(trainBudgetMs) && ++training == cloners.Length) Finished();
            TemplateRun.BudgetMs = trainBudgetMs;
            watcher?.Poll(Time.unscaledTimeAsDouble);
            DrawTruck();
            if (Time.unscaledTime >= nextReadout) { nextReadout = Time.unscaledTime + 0.2f; BuildReadout(); }
        }

        // Saves the laps and each candidate's controller file: truck_autopilot.json is candidate 1 (tried first),
        // candidates/truck_autopilot_2.json and _3 are the next ones. Step 6's autopilot attaches and drives them.
        void Finished()
        {
            float wall = Time.unscaledTime - trainStarted;
            candidateJson = new string[cloners.Length];
            for (int i = 0; i < cloners.Length; i++) candidateJson[i] = cloners[i].ToControllerJson("truck_autopilot", i + 1, 1.5f);
            int frames = 0; double compute = 0;
            var scores = new System.Text.StringBuilder();
            // One notebook entry per training run (C-17 section 3): candidate 1's block plus every candidate's.
            var block = cloners[0].TrainingBlock();
            var list = new Newtonsoft.Json.Linq.JArray();
            foreach (var c in cloners) list.Add(c.TrainingBlock());
            block["candidates"] = list;
            TrainingLog.Report("truck_autopilot", 1, block);
            foreach (var c in cloners)
            {
                frames += c.Steps; compute += c.ComputeSeconds;
                if (scores.Length > 0) scores.Append(", ");
                scores.Append($"seed {c.Settings.Seed}: val {c.ValidationLoss:0.0000}, mock slalom {MockTrial.Finishes(c)}/{MockTrial.Starts}");
            }
            try
            {
                Directory.CreateDirectory(Path.Combine(SaveFolder, "laps"));
                Directory.CreateDirectory(Path.Combine(SaveFolder, "candidates"));
                File.WriteAllText(Path.Combine(SaveFolder, "laps", "sandbox.json"), recorder.Set.ToJson());
                for (int i = 0; i < cloners.Length; i++)
                {
                    string path = i == 0 ? Path.Combine(SaveFolder, "truck_autopilot.json")
                                         : Path.Combine(SaveFolder, "candidates", $"truck_autopilot_{i + 1}.json");
                    File.WriteAllText(path, candidateJson[i]);
                }
                status = $"trained {cloners.Length} candidates in {frames} frames ({wall:0.0} s, {compute * 1000:0} ms compute). " +
                         $"{scores}. Saved to {SaveFolder}";
            }
            catch (IOException e) { status = "trained, but saving failed: " + e.Message; }
        }

        void Tune(float delta)
        {
            if (host.File == null || !host.File.Tunables.TryGetValue("max_speed", out var t)) { status = "no max_speed tunable"; return; }
            string error = host.SetTunable("max_speed", t.Value + delta);
            status = error ?? $"max_speed {ControllerFile.Fmt(host.File.Tunables["max_speed"].Value)} m/s";
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            sim.Sense(host.IO.Sensors);
            if (autoDriver != null) autoDriver.Drive(sim);
            host.Step(dt, sim.Speed);
            recorder.Record(host.IO.Sensors, host.Commanded, dt);
            sim.Step(host.IO.Actuators, dt);
            if (recorder.Recording && (sim.Finished || sim.Crashed)) StopLap(sim.Finished ? "completed" : "crashed");
            // The Autopilot's run ended: keep the candidate, or reset and let the next one try.
            if (autopilot != null && !runReported && (sim.Finished || sim.Crashed))
            {
                runReported = true;
                status = autopilot.RunEnded(host, sim.Finished);
                if (!sim.Finished && !autopilot.Exhausted) { sim.Reset(); host.ResetState(); runReported = false; }
            }
        }

        // Sim x → world x, sim y (left) → world z, CCW heading → negative yaw.
        void DrawTruck()
        {
            if (truck == null) return;
            var position = new Vector3(sim.X, 0f, sim.Y);
            truck.SetPositionAndRotation(position + Vector3.up * 0.06f, Quaternion.Euler(0f, -sim.Heading * Mathf.Rad2Deg, 0f));
            if (rays == null) return;
            for (int i = 0; i < rays.Length && i < MockTruckSim.Rays; i++)
            {
                float a = sim.Heading + MockTruckSim.RayAngle(i);
                var origin = position + Vector3.up * 0.05f;
                rays[i].SetPosition(0, origin);
                rays[i].SetPosition(1, origin + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * sim.RayDistance[i]);
            }
        }

        void BuildReadout()
        {
            sb.Clear();
            var c = host.Current;
            sb.Append(c == null ? "no controller" : $"{c.Name} ({c.Kind})");
            if (!float.IsPositiveInfinity(host.SpeedLimit)) sb.Append($"   limit {host.SpeedLimit:0.0} m/s");
            sb.Append($"\nspeed {sim.Speed:0.00} m/s   t {sim.Time:0.0} s   ");
            sb.Append(sim.Crashed ? "CRASHED (R to reset)" : sim.Finished ? "FINISHED" : "driving");
            sb.Append("\nsteer ").Append(host.IO.Actuators[0].ToString("+0.00;-0.00"))
              .Append("   throttle ").Append(host.IO.Actuators[1].ToString("+0.00;-0.00"));
            sb.Append("\nrays ");
            for (int i = 0; i < MockTruckSim.Rays; i++) sb.Append(host.IO.Sensors[i].ToString("0.00")).Append(' ');
            sb.Append($"\nlaps {recorder.Set.Laps.Count} ({recorder.Set.CompletedLaps} completed)");
            if (recorder.Recording) sb.Append($"   RECORDING {recorder.Frames} frames");
            var current = Cloner;
            if (current != null)
                sb.Append($"   candidate {System.Math.Min(training + 1, cloners.Length)}/{cloners.Length} epoch {current.Epoch}/{current.Settings.Epochs} val loss {current.ValidationLoss:0.0000}");
            if (host.Current is GraphController graph && !float.IsNaN(graph.ModelProbability))
                sb.Append("   P(obstacle ahead) ").Append(graph.ModelProbability.ToString("0.00"));
            if (TemplateTraining)
            {
                var tc = TemplateRun.Current.Classifier;
                sb.Append($"   ML template epoch {tc.Epoch}/{tc.Settings.Epochs} val loss {tc.ValidationLoss:0.0000}");
            }
            sb.Append("\n").Append(status);
            if (autopilot != null) sb.Append(autopilot.Exhausted ? "   AUTOPILOT: nothing left" : $"   AUTOPILOT {autopilot.Describe()}{(autopilot.Kept ? " (kept)" : "")}");
            if (watcher != null) sb.Append($"   LIVE FILE ({watcher.Reloads} reloads)");
            sb.Append("\n1 manual  2 cruise  3 mock autopilot  WASD drive  [ ] max_speed  R reset  T record lap  L scripted lap  B train  P autopilot  E edit file live  M ML template  4 learned brake");
            readout = sb.ToString();
        }

        void OnGUI()
        {
            GUI.Label(new Rect(12, 12, Screen.width - 24, 200), readout);
        }
    }
}
