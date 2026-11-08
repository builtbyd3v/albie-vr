using Forge.Controllers;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Forge.Artifacts
{
    /// <summary>
    /// SlalomSandbox only: the demo's brain beat end to end on a room slalom (PROJECT_CONTEXT 15.6 step 8, 16.8 "room
    /// slalom, in the app"). Every piece is the real one except the truck's body:
    /// - the truck is ML A's physics example (<see cref="ExampleTruckBody"/>: a ray fan on the cones' colliders, a crash
    ///   when it touches one) with a <see cref="ControllerThing"/>, standing in for Person 2's Thing runtime;
    /// - a lap is a scored run of the open slalom (<see cref="ArtifactHost.ArmRun"/>), driven through the manual
    ///   controller (WASD, a Touch thumbstick, or <see cref="RecordScriptedLap"/>) and recorded by the Thing's lap recorder;
    /// - the run engine sets <c>course.bearing</c>'s target (<see cref="CourseBeacon"/>) as the gates are passed;
    /// - <see cref="Train"/> trains <see cref="Candidates"/> candidates a few ms a frame from the completed laps;
    /// - <see cref="StartAutopilot"/> gives each candidate (then the pretrained fallback) a scored run until one
    ///   completes, as the Controllers sandbox's P does on the mock;
    /// - by voice (<see cref="IBrainLoop"/>, the <c>brain_*</c> and <c>autopilot_*</c> tools), <see cref="Record"/>
    ///   chains it all: "drive it twice and I'll learn it" records 2 completed laps, trains, then runs the Autopilot.
    /// A run that touches a cone or a wall is aborted (the artifact only counts hits; the demo shouldn't keep a crash).
    /// </summary>
    [DefaultExecutionOrder(-100)]  // the scripted driver's FixedUpdate writes the stick before ControllerThing reads it
    public sealed class SlalomBrain : MonoBehaviour, IBrainLoop
    {
        public const int Candidates = 3;
        public const double BudgetMs = 4.0;
        public const float RunTimeoutS = 30f;
        /// <summary>W/S hold this speed (m/s), about the scripted driver's: the stick's full 2 m/s is too fast to weave
        /// 1 m gates from a keyboard, and the trained net drives at the speed of its laps.</summary>
        public const float KeySpeed = 0.8f;
        public enum Mode { Idle, Lap, Training, Autopilot }

        public ExampleTruckBody Body { get; private set; }
        public ControllerThing Thing { get; private set; }
        public ArtifactThing Scored { get; private set; }
        public Mode State { get; private set; }
        public string Status { get; private set; } = "brain truck: open a slalom (2), then L to drive a lap by hand (WASD)";
        /// <summary>Who completed the last Autopilot run: "candidate 1", "the pretrained fallback" or "nothing".</summary>
        public string Drove { get; private set; }
        /// <summary>A hand lap is driven with WASD, a Touch controller's thumbstick (<see cref="QuestStick"/>), or any
        /// other source writing <see cref="ManualInputs"/> (the hands transmitter): none overwrites another every frame.
        /// The keys: forward (W +1, S -1) and left (A +1, D -1). Read from the keyboard while
        /// <see cref="KeysFromKeyboard"/>; a check sets them itself.</summary>
        public float KeyForward, KeyLeft;
        public bool KeysFromKeyboard = true;
        /// <summary>Checks only: <see cref="Record"/>'s laps are driven by the scripted driver, not by hand. No tool
        /// sets it, so "drive it yourself" by voice is always the person's own laps.</summary>
        public bool ScriptedLaps;
        public int CompletedLaps => Thing != null ? Thing.Recorder.Set.CompletedLaps : 0;
        public BehaviourCloner[] Cloners => cloners;
        public Autopilot Autopilot => autopilot;
        /// <summary>Every Autopilot run's result, in order ("candidate 1 crashed", "candidate 2 completed").</summary>
        public readonly System.Collections.Generic.List<string> Attempts = new System.Collections.Generic.List<string>();

        ArtifactHost host;
        ControllerFile manual;
        BehaviourCloner[] cloners;
        int training, trainFrames;
        // Record(laps, thenAutopilot): the completed-lap count that ends recording (0: one lap at a time, as L does),
        // and whether training and the Autopilot follow with no further call.
        int lapTarget;
        bool chainAutopilot;
        int chainSeed;
        Autopilot autopilot;
        float runStarted;
        // A scripted lap: the scripted driver reads the truck's pose in the course's frame (MockDriver.ForRoom).
        MockDriver script;
        MockTruckSim view;
        readonly int stickX = ManualInputs.IndexOf("stick.x"), stickY = ManualInputs.IndexOf("stick.y"), handRoll = ManualInputs.IndexOf("hand.roll");
        // What the keys and the thumbstick last wrote: each writes only when it changes (see Keys).
        float lastKeyForward, lastKeyLeft;
        Vector2 lastStick;

        /// <summary>Makes the brain truck and binds it to <paramref name="host"/>'s runs.</summary>
        public static SlalomBrain Spawn(ArtifactHost host)
        {
            var go = new GameObject("Brain truck");
            go.SetActive(false);  // so ControllerThing's Awake sees the fields set below
            var shell = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(shell.GetComponent<Collider>());  // the rays mustn't see the truck itself
            shell.transform.SetParent(go.transform, false);
            shell.transform.localPosition = new Vector3(0f, 0.04f, 0f);
            shell.transform.localScale = new Vector3(0.16f, 0.08f, 0.24f);
            var renderer = shell.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = ArtifactLook.Material("#FF9F1C");

            var brain = go.AddComponent<SlalomBrain>();
            brain.host = host;
            brain.Body = go.AddComponent<ExampleTruckBody>();
            brain.Scored = go.AddComponent<ArtifactThing>();
            // The run reads the shell's centre: at the floor the truck sat exactly on the gate zones' bottom face.
            brain.Scored.ProbeHeight = 0.04f;
            brain.Scored.Radius = MockTruckSim.TruckRadius;
            var body = brain.Body;
            brain.Scored.SpeedSource = () => Mathf.Abs(body.Speed);
            brain.Thing = go.AddComponent<ControllerThing>();
            brain.Thing.thingName = "truck";
            brain.Thing.thingVersion = 2;
            var manualAsset = Resources.Load<TextAsset>("Mocks/controllers/truck_manual");
            brain.manual = manualAsset != null ? ControllerFile.Parse(manualAsset.text, out _) : null;
            go.SetActive(true);
            if (brain.manual != null) brain.Thing.Host.Attach(brain.manual);
            host.Thing = brain.Scored;
            brain.PlaceAtStart();
            brain.Park();
            return brain;
        }

        void OnEnable() => BrainLoops.Register(this);
        void OnDisable() => BrainLoops.Unregister(this);

        void OnDestroy()
        {
            ManualInputs.Clear();
            if (host != null && ReferenceEquals(host.Thing, Scored)) host.Thing = null;
        }

        // ---- Laps -----------------------------------------------------------------------------------------------

        /// <summary>Arms a run and records a lap you drive with WASD or a thumbstick (through truck_manual).</summary>
        public string RecordLap() { StopChain(); return BeginLap(null); }

        /// <summary>A lap driven by the scripted driver (MockDriver.ForRoom), for checks and quick demos.</summary>
        public string RecordScriptedLap(int seed = 0) { StopChain(); return BeginLap(MockDriver.ForRoom(seed)); }

        /// <summary>Hand-driven laps until <paramref name="laps"/> more have completed (a crashed or failed lap is
        /// re-armed at the start), then, if <paramref name="thenAutopilot"/>, <see cref="Train"/> and
        /// <see cref="StartAutopilot"/> with no further call.</summary>
        public string Record(int laps, bool thenAutopilot)
        {
            if (State != Mode.Idle) return Status = "busy: " + State.ToString().ToLowerInvariant();
            lapTarget = CompletedLaps + Mathf.Clamp(laps, 1, 10);
            chainAutopilot = thenAutopilot;
            string error = BeginLap(ChainDriver());
            if (error != null) StopChain();
            else Status = LapPrompt();
            return error;
        }

        /// <summary>Aborts the lap being driven; laps already completed stay recorded.</summary>
        public string StopRecording()
        {
            if (State != Mode.Lap) return Status = "not recording a lap";
            StopChain();
            if (host != null && host.Run != null) host.Run.Abort();
            EndLap("aborted");
            return null;
        }

        void StopChain() { lapTarget = 0; chainAutopilot = false; }

        // The scripted driver's seeds 1 and 2 in turn, the two the brain check drives every room with.
        MockDriver ChainDriver() => ScriptedLaps ? MockDriver.ForRoom(1 + chainSeed++ % 2) : null;

        string LapPrompt()
        {
            int left = lapTarget - CompletedLaps;
            return $"lap: drive through the gates ({left} more completed lap{(left == 1 ? "" : "s")} to go)";
        }

        string BeginLap(MockDriver driver)
        {
            if (State == Mode.Training || State == Mode.Autopilot) return Status = "busy: " + State.ToString().ToLowerInvariant();
            if (manual != null && Thing.Host.File?.Name != manual.Name) Thing.Host.Attach(manual);
            string error = Arm();
            if (error != null) return Status = error;
            script = driver;
            view = driver != null ? new MockTruckSim(CourseFromSlots()) : null;
            Thing.BeginLap();
            // The lap's own inputs start at rest (EndLap clears them): a source writes once it moves, so an untouched
            // stick never zeroes the keys (or the transmitter) at the start.
            lastKeyForward = lastKeyLeft = 0f;
            lastStick = Vector2.zero;
            State = Mode.Lap;
            Status = driver != null ? "scripted lap: recording" : "lap: drive through the gates with WASD or the thumbstick (recording)";
            return null;
        }

        void EndLap(string outcome)
        {
            var lap = Thing.EndLap(outcome);
            State = Mode.Idle;
            script = null;
            ManualInputs.Clear();
            Park();
            Status = $"lap {outcome} in {lap.Seconds:0.0} s; {CompletedLaps} completed lap{(CompletedLaps == 1 ? "" : "s")}" +
                     (CompletedLaps >= 2 ? " (T trains)" : " (L for another)");
            if (lapTarget <= 0) return;
            string said = Status;
            if (CompletedLaps < lapTarget)
            {
                // Re-armed at the start: the next lap is driven from there, as L would.
                string error = BeginLap(ChainDriver());
                if (error != null) { StopChain(); Status = error; }
                else Status = $"{said}. {LapPrompt()}";
                return;
            }
            bool then = chainAutopilot;
            lapTarget = 0;
            if (!then) return;
            // TrainStep starts the Autopilot when training is done; a refused Train (fewer than 2 laps) ends the chain.
            chainAutopilot = Train() == null;
        }

        // ---- Training -------------------------------------------------------------------------------------------

        /// <summary>Trains <see cref="Candidates"/> candidates on the completed laps, <see cref="BudgetMs"/> ms a frame.</summary>
        public string Train()
        {
            if (State != Mode.Idle) return Status = "busy: " + State.ToString().ToLowerInvariant();
            if (CompletedLaps < 2) return Status = $"drive 2 completed laps first ({CompletedLaps} so far)";
            cloners = new BehaviourCloner[Candidates];
            for (int i = 0; i < Candidates; i++) cloners[i] = new BehaviourCloner(Thing.Recorder.Set, new BehaviourCloner.Options { Seed = i + 1 });
            training = 0; trainFrames = 0;
            State = Mode.Training;
            Status = $"training {Candidates} candidates on {CompletedLaps} laps ({cloners[0].TrainCount} rows)";
            return null;
        }

        void TrainStep()
        {
            trainFrames++;
            if (cloners[training].Step(BudgetMs)) training++;
            if (training < cloners.Length) return;
            State = Mode.Idle;
            double compute = 0;
            foreach (var c in cloners) compute += c.ComputeSeconds;
            // The notebook's training entry (C-17 section 3), as the Controllers sandbox's B reports it.
            var block = cloners[0].TrainingBlock();
            var list = new JArray();
            foreach (var c in cloners) list.Add(c.TrainingBlock());
            block["candidates"] = list;
            TrainingLog.Report("truck_autopilot", 1, block);
            Status = $"trained {Candidates} candidates in {trainFrames} frames ({compute * 1000:0} ms compute). P: Autopilot";
            if (!chainAutopilot) return;
            chainAutopilot = false;
            StartAutopilot();
        }

        // ---- Autopilot ------------------------------------------------------------------------------------------

        /// <summary>Gives each candidate, then the pretrained fallback, a scored run until one completes.</summary>
        public string StartAutopilot()
        {
            if (State != Mode.Idle) return Status = "busy: " + State.ToString().ToLowerInvariant();
            if (cloners == null) return Status = "train first (T)";
            var files = new System.Collections.Generic.List<ControllerFile>();
            for (int i = 0; i < cloners.Length; i++)
            {
                var file = ControllerFile.Parse(cloners[i].ToControllerJson("truck_autopilot", i + 1, 1.5f), out string error);
                if (file == null) return Status = error;
                files.Add(file);
            }
            var fallbackAsset = Resources.Load<TextAsset>("Controllers/truck_autopilot_fallback");
            var fallback = fallbackAsset != null ? ControllerFile.Parse(fallbackAsset.text, out _) : null;
            autopilot = new Autopilot(files, fallback);
            string engage = autopilot.Engage(Thing.Host);
            if (engage != null) return Status = engage;
            Attempts.Clear();
            Drove = null;
            State = Mode.Autopilot;
            return BeginAttempt();
        }

        /// <summary>Stops the Autopilot's run and gives the truck back to the manual controller.</summary>
        public string StopAutopilot()
        {
            if (State != Mode.Autopilot) return Status = "the Autopilot isn't driving";
            if (host != null && host.Run != null) host.Run.Abort();
            string who = autopilot.UsingFallback ? "the pretrained fallback" : $"candidate {autopilot.Index + 1}";
            Attempts.Add($"{who} stopped");
            Drove = null;
            State = Mode.Idle;
            Park();
            if (manual != null) Thing.Host.Attach(manual);
            Status = "Autopilot stopped; the truck is back on the manual controller";
            return null;
        }

        /// <summary>The loop's state, for <c>brain_status</c> and the other brain tools' results.</summary>
        public JObject ToolState()
        {
            var attempts = new JArray();
            foreach (var a in Attempts) attempts.Add(a);
            int trained = 0;
            if (cloners != null) foreach (var c in cloners) if (c.Done) trained++;
            return new JObject
            {
                ["mode"] = State.ToString().ToLowerInvariant(),
                ["status"] = Status,
                ["preview_only"] = false,
                ["thing"] = Thing != null ? Thing.thingName : null,
                ["controller"] = Thing != null && Thing.Host.File != null ? Thing.Host.File.Name : null,
                ["completed_laps"] = CompletedLaps,
                ["laps_to_go"] = lapTarget > 0 ? Mathf.Max(0, lapTarget - CompletedLaps) : 0,
                ["then_autopilot"] = chainAutopilot,
                ["slalom_open"] = host != null && host.Run != null,
                ["candidates"] = Candidates,
                ["candidates_trained"] = trained,
                ["autopilot"] = State == Mode.Autopilot ? autopilot.Describe() : null,
                ["drove"] = Drove,
                ["attempts"] = attempts,
            };
        }

        string BeginAttempt()
        {
            string error = Arm();
            if (error != null) { State = Mode.Idle; return Status = error; }
            Status = $"Autopilot: {autopilot.Describe()} driving";
            return null;
        }

        void AttemptEnded(bool completed, string how)
        {
            string who = autopilot.UsingFallback ? "the pretrained fallback" : $"candidate {autopilot.Index + 1}";
            Attempts.Add($"{who} {how}");
            string said = autopilot.RunEnded(Thing.Host, completed);
            if (completed) { Drove = who; State = Mode.Idle; Status = said; Park(); return; }
            if (autopilot.Exhausted) { Drove = "nothing"; State = Mode.Idle; Status = said; Park(); return; }
            Status = said;
            BeginAttempt();
        }

        // ---- The frame loop -------------------------------------------------------------------------------------

        string Arm()
        {
            if (host == null || host.Run == null) return "open the slalom first (2)";
            host.Thing = Scored;
            var file = Thing.Host.File;
            if (file != null) { Scored.ControllerName = file.Name; Scored.ControllerKind = file.Kind; Scored.ControllerVersion = file.Version; }
            string error = host.ArmRun();
            if (error != null) return error;
            PlaceAtStart();
            Thing.tickInFixedUpdate = true;
            runStarted = Time.time;
            return null;
        }

        /// <summary>Stops the truck where it is between runs: the kept controller would otherwise drive on past the finish.</summary>
        void Park() => Thing.tickInFixedUpdate = false;

        /// <summary>The lap's outcome (contract section 4) for how its run ended.</summary>
        static string LapOutcome(bool completed, string how) => completed ? "completed" : how == "crashed" ? "crashed" : "aborted";

        public void PlaceAtStart()
        {
            var at = host != null ? host.SceneOrigin : new Pose(transform.position, transform.rotation);
            Body.Place(at.position, at.rotation);
            Thing.Host.ResetState();
        }

        void Update()
        {
            switch (State)
            {
                case Mode.Lap:
                    if (script == null) Keys();
                    if (RunOver(out bool completed, out string how)) EndLap(LapOutcome(completed, how));
                    break;
                case Mode.Training:
                    TrainStep();
                    break;
                case Mode.Autopilot:
                    if (RunOver(out bool done, out string why)) AttemptEnded(done, why);
                    break;
            }
        }

        // The scripted driver steps once a physics step, as it does on the mock (its steering noise is per step).
        void FixedUpdate()
        {
            if (State == Mode.Lap && script != null) { Mirror(); script.Drive(view); }
        }

        // The artifact decides completed or failed; a touch, or 30 s without a finish, stops the run here.
        bool RunOver(out bool completed, out string how)
        {
            completed = false; how = null;
            var run = host != null ? host.Run : null;
            if (run == null) { how = "closed"; return true; }
            if (run.State == ArtifactRunState.Finished)
            {
                completed = run.Outcome == "completed";
                how = completed ? "completed" : $"{run.Outcome} ({run.Reason ?? "-"})";
                return true;
            }
            if (Body.Crashed) { run.Abort(); how = "crashed"; return true; }
            if (Time.time - runStarted > RunTimeoutS) { run.Abort(); how = "timed out"; return true; }
            return false;
        }

        void Keys()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (KeysFromKeyboard && k != null)
            {
                KeyForward = (k.wKey.isPressed ? 1f : 0f) - (k.sKey.isPressed ? 1f : 0f);
                KeyLeft = (k.aKey.isPressed ? 1f : 0f) - (k.dKey.isPressed ? 1f : 0f);
            }
#endif
            // The throttle that holds KeySpeed against drag (MockTruckSim's model, which ExampleTruckBody shares).
            // A full thumbstick holds the same speed, so hand laps on a headset train like keyboard laps.
            const float keyThrottle = KeySpeed * MockTruckSim.Drag / MockTruckSim.MaxAccel;
            // Each source writes only when its own value changes, so it doesn't overwrite another writer every frame
            // (Person 1's hands transmitter writes stick.y and hand.roll too). Letting go writes 0 once: the truck stops.
            if (KeyForward != lastKeyForward || KeyLeft != lastKeyLeft)
            {
                lastKeyForward = KeyForward; lastKeyLeft = KeyLeft;
                ManualInputs.Values[stickY] = KeyForward * keyThrottle;
                ManualInputs.Values[handRoll] = KeyLeft;
            }
            // A Touch controller's thumbstick (QuestStick): forward drives, sideways steers (truck_manual's stick.x).
            var stick = QuestStick.Read();
            if (stick.x != lastStick.x || stick.y != lastStick.y)
            {
                lastStick = stick;
                ManualInputs.Values[stickY] = Mathf.Clamp(stick.y, -1f, 1f) * keyThrottle;
                ManualInputs.Values[stickX] = Mathf.Clamp(stick.x, -1f, 1f);
            }
        }

        // The truck's pose in the course's frame (x along the course, y to the left), for the scripted driver.
        void Mirror()
        {
            var origin = host.SceneOrigin;
            var toCourse = Quaternion.Inverse(origin.rotation);
            var local = toCourse * (transform.position - origin.position);
            var forward = toCourse * transform.forward;
            view.X = local.z; view.Y = -local.x;
            view.Heading = Mathf.Atan2(-forward.x, forward.z);
            view.Speed = Body.Speed;
        }

        // The open slalom's slots as a MockCourse (cones, gates, finish; no walls: the driver only needs the path).
        MockCourse CourseFromSlots()
        {
            var course = new MockCourse { Name = "open slalom" };
            var slots = host.Build.Slots;
            if (slots.TryGetValue("course.cones", out var cones)) foreach (var c in cones) course.AddCone(c.position.z, -c.position.x);
            if (slots.TryGetValue("course.gates", out var gates)) foreach (var g in gates) course.AddGate(g.position.z, -g.position.x);
            if (slots.TryGetValue("course.finish", out var finish) && finish.Count > 0) course.SetFinish(finish[0].position.z, -finish[0].position.x);
            return course;
        }
    }
}
