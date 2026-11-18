using Forge.Controllers;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Forge.Artifacts
{
    /// <summary>
    /// SlalomSandbox (key B) and the app (<see cref="ArtifactBootstrap.EnsureBrain"/>): the demo's brain beat end to end on a room slalom (PROJECT_CONTEXT 15.6 step 8, 16.8 "room
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
    /// Between laps (<see cref="FreeDrive"/>) the truck drives around the course by hand, the way the drone flies free:
    /// nothing is recorded or scored, and a touch stops it where it touched so it can back away.
    /// </summary>
    [DefaultExecutionOrder(-100)]  // the scripted driver's FixedUpdate writes the stick before ControllerThing reads it
    public sealed class SlalomBrain : MonoBehaviour, ICourseBrain
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
        /// <summary>Out of the way while the host shows something else, or nothing: unseen and parked, but still the
        /// brain loop, so the tools say "open the slalom first" rather than lose the truck. Back when a truck course is built.</summary>
        public bool Hidden { get; private set; }
        Renderer[] shell;
        public string Status { get; private set; } = "brain truck: open a slalom (2), then L to drive a lap by hand (WASD)";
        /// <summary>Who completed the last Autopilot run: "candidate 1", "the pretrained fallback" or "nothing".</summary>
        public string Drove { get; private set; }
        /// <summary>A hand lap is driven with WASD, a Touch controller's thumbstick (<see cref="QuestStick"/>), the hands
        /// (<see cref="HandsTransmitter"/>: pinch to drive, tilt to steer), or any other source writing
        /// <see cref="ManualInputs"/>: none overwrites another every frame.
        /// The keys: forward (W +1, S -1) and left (A +1, D -1). Read from the keyboard while
        /// <see cref="KeysFromKeyboard"/>; a check sets them itself.</summary>
        public float KeyForward, KeyLeft;
        public bool KeysFromKeyboard = true;
        /// <summary>
        /// Free driving between laps (<see cref="Mode.Idle"/>, shown): the transmitter, the Drive pad, a Touch thumbstick
        /// or the hands drive the truck through <c>truck_manual</c> with no lap recorded and no run armed, as the drone
        /// flies whenever it is up. The keyboard is left to the desktop rig (WASD walks it) and drives laps only. A cone,
        /// a wall or a desk course's table edge stops the truck where it touched; it can back away (or drive on, after
        /// touching while reversing). Nothing ticks while it is at rest with no throttle. On by default.
        /// </summary>
        public bool FreeDrive = true;
        /// <summary>Checks only: <see cref="Record"/>'s laps are driven by the scripted driver, not by hand. No tool
        /// sets it, so "drive it yourself" by voice is always the person's own laps.</summary>
        public bool ScriptedLaps;
        /// <summary>The sandbox's key hints ("(2)", "L", "T", "P", "WASD") in <see cref="Status"/>. Off in the app, where
        /// there are no such keys and Albie reads the status aloud (<c>brain_status</c>).</summary>
        public bool KeyHints = true;
        string Hint(string text) => KeyHints ? text : "";
        public int CompletedLaps => Thing != null ? Thing.Recorder.Set.CompletedLaps : 0;
        /// <summary>The truck's size against full size: 1 on a floor course, the course's 1:N on a desk course
        /// (<see cref="ArtifactHost.CourseScale"/>). The Drive window says "Desk course · 1:N scale".</summary>
        public float CourseScale => Body != null ? Body.Scale : 1f;
        /// <summary>Why a desk run stops when the truck's centre leaves the table top (an outcome of <c>crashed</c>).</summary>
        public const string OffTable = "drove off the table";
        /// <summary>The completed-lap count that ends the recording started by <see cref="Record"/> (0 when one lap at a time, as <see cref="RecordLap"/>). The Drive window reads it for "recording lap 1 of 2".</summary>
        public int LapTarget => lapTarget;
        public BehaviourCloner[] Cloners => cloners;
        public Autopilot Autopilot => autopilot;
        /// <summary>Every Autopilot run's result, in order ("candidate 1 crashed", "candidate 2 completed").</summary>
        public readonly System.Collections.Generic.List<string> Attempts = new System.Collections.Generic.List<string>();

        ArtifactHost host;
        ControllerFile manual;
        BehaviourCloner[] cloners;
        int training, trainFrames;
        // After training, each candidate rehearses the open course in the mock sim; the Autopilot tries them best-first.
        Rehearsal rehearsal;
        ControllerFile[] trainedFiles;
        int[] order;
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
        // What the keys, the thumbstick and the hands last wrote: each writes only when it changes (see Keys).
        float lastKeyForward, lastKeyLeft, lastHandThrottle, lastHandRoll;
        Vector2 lastStick;
        // Free driving: the way the truck was going when it last touched something (+1 forward, -1 reversing, 0 clear).
        int bumpSign;

        /// <summary>Makes the brain truck and binds it to <paramref name="host"/>'s runs.</summary>
        public static SlalomBrain Spawn(ArtifactHost host, bool keyHints = true)
        {
            var go = new GameObject("Brain truck");
            go.SetActive(false);  // so ControllerThing's Awake sees the fields set below
            // The look: a short-course RC truck built in code (TruckShell), no colliders, so the rays and the crash
            // sphere don't see the truck itself. It scales with the transform, which Fit sets to the course's 1:N.
            TruckShell.Build(go.transform, ArtifactLook.Material("#FF6B1A"), ArtifactLook.Material("#FFE14D"),
                             ArtifactLook.Material("#23262D"), ArtifactLook.Material("#151515"),
                             ArtifactLook.Material("#FFF4D6", glow: true), ArtifactLook.Material("#6E0A0A", glow: true),
                             ArtifactLook.Material("#FF2A2A", glow: true), ArtifactLook.Trail());

            var brain = go.AddComponent<SlalomBrain>();
            brain.host = host;
            brain.Body = go.AddComponent<ExampleTruckBody>();
            // The truck drives and looks like the open project's truck design (TruckDesignStore), live.
            go.AddComponent<TruckDesignLink>();
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
            brain.shell = go.GetComponentsInChildren<Renderer>();
            brain.Fit(host);
            brain.KeyHints = keyHints;
            if (!keyHints) brain.Status = "brain truck at the start: drive it around, or record a lap to teach it";
            brain.PlaceAtStart();
            brain.Park();
            host.Shown += brain.OnHostShown;
            host.Closed += brain.OnHostClosed;
            host.Fired += brain.OnHostButton;
            return brain;
        }

        // The slalom panel's own buttons, so the course works with no voice and no keys.
        // "drive_lap": one lap by hand. "albie_drives": train on the laps there are and hand over to the Autopilot; with
        // fewer than 2 completed laps it first asks for the ones still missing, then goes on by itself.
        void OnHostButton(string input)
        {
            if (Hidden) return;
            if (input == "drive_lap") RecordLap();
            else if (input == "albie_drives")
            {
                if (State != Mode.Idle) { Status = "busy: " + State.ToString().ToLowerInvariant(); return; }
                if (CompletedLaps < 2) Record(2 - CompletedLaps, true);
                else chainAutopilot = Train() == null;
            }
        }

        void OnEnable() => BrainLoops.Register(this);
        void OnDisable() => BrainLoops.Unregister(this);

        void OnDestroy()
        {
            ManualInputs.Clear();
            if (host == null) return;
            host.Shown -= OnHostShown;
            host.Closed -= OnHostClosed;
            host.Fired -= OnHostButton;
            if (ReferenceEquals(host.Thing, Scored)) host.Thing = null;
        }

        // The host built a scene: a truck course gets the truck at its start, as the host's Thing. Anything else keeps
        // the truck hidden, so it isn't scored as that scene's Thing and isn't left standing beside it.
        void OnHostShown(ArtifactHost h)
        {
            bool truckCourse = h.Run != null && (string)h.OpenManifest?["links"]?["thing"] == "truck";
            if (!truckCourse) { OnHostClosed(h); return; }
            h.Thing = Scored;
            Fit(h);
            Hide(false);
            BrainLoops.Register(this);  // back in front of a drone loop that was the active one
            if (State == Mode.Idle) PlaceAtStart();
        }

        void OnHostClosed(ArtifactHost h)
        {
            if (ReferenceEquals(h.Thing, Scored)) h.Thing = null;
            Hide(true);
        }

        // ---- Desk courses ---------------------------------------------------------------------------------------

        // The table top a desk course stands on, for the off-the-table rule (none on the floor, or with no table found).
        bool onTable;
        Matrix4x4 worldToTable;
        float tableHalfX, tableHalfZ;

        /// <summary>
        /// Sizes the truck for <paramref name="h"/>'s course: the course's scale on a desk (1:4 to 1:10), 1 on the floor.
        /// Its shell, driving model and sensors shrink together (<see cref="ExampleTruckBody.SetScale"/>), and so do the
        /// run's probe (<see cref="ArtifactThing.ProbeHeight"/>, <see cref="ArtifactThing.Radius"/>). The truck is
        /// kinematic, so it can't fall through the table; leaving the top ends the run as a crash instead (<see cref="OffTable"/>).
        /// </summary>
        void Fit(ArtifactHost h)
        {
            float scale = h != null ? h.CourseScale : 1f;
            if (Body != null) Body.SetScale(scale);
            float s = CourseScale;
            transform.localScale = new Vector3(s, s, s);
            if (Scored != null)
            {
                Scored.ProbeHeight = 0.04f * s;  // the shell's centre, as at full size
                Scored.Radius = MockTruckSim.TruckRadius * s;
            }
            onTable = false;
            if (s < 1f && h != null && DeskPlan.TryMake(h.CourseRoom, s, out var desk))
            {
                worldToTable = desk.RoomToWorld.inverse;  // +z along the table's long side
                tableHalfZ = desk.TopSize.x * 0.5f;
                tableHalfX = desk.TopSize.y * 0.5f;
                onTable = true;
            }
        }

        bool LeftTable()
        {
            if (!onTable) return false;
            var local = worldToTable.MultiplyPoint3x4(transform.position);
            return local.x < -tableHalfX || local.x > tableHalfX || local.z < -tableHalfZ || local.z > tableHalfZ;
        }

        void Hide(bool hidden)
        {
            Hidden = hidden;
            if (shell != null) foreach (var r in shell) r.enabled = !hidden;
            if (hidden) Park();
        }

        // ---- Rounds and the panel (ICourseBrain) ------------------------------------------------------------------

        /// <summary>One training: its candidates and how they rehearsed. Kept so the panel can pick an earlier one.</summary>
        sealed class Round
        {
            public ControllerFile[] Files;
            public int[] Order, Finished;
            public int Laps;
            public float Loss;
            public string Course;
            public bool Slalom;
            /// <summary>The truck design it was trained on (<see cref="TruckDesign.Fingerprint"/>) and its saved version.</summary>
            public string Design;
            public int DesignVersion;
        }
        readonly System.Collections.Generic.List<Round> rounds = new System.Collections.Generic.List<Round>();
        readonly System.Collections.Generic.List<float> loss = new System.Collections.Generic.List<float>(512);
        int selected, lastEpoch;

        /// <summary>The open course is a slalom: the one the mock rehearsal and the pretrained fallback are made for.</summary>
        bool IsSlalom => host != null && host.OpenManifest != null
                         && ((string)host.OpenManifest["template"] == "slalom" || (string)host.OpenManifest["id"] == "slalom");

        public string ThingName => "truck";
        public string CourseId => !Hidden && host != null && host.Run != null ? host.OpenId : null;
        public string ModeName => State.ToString().ToLowerInvariant();
        public string StatusLine => Status;
        public int LapsCompleted => CompletedLaps;
        public float TrainProgress
        {
            get
            {
                if (State != Mode.Training || cloners == null) return -1f;
                float epochs = 0f, total = 0f;
                foreach (var c in cloners) { epochs += c.Epoch; total += c.Settings.Epochs; }
                return total > 0f ? (rehearsal != null ? 0.9f : 1f) * epochs / total : 0f;
            }
        }
        public System.Collections.Generic.IReadOnlyList<float> LossHistory => loss;
        public int VersionCount => rounds.Count;
        public int SelectedVersion => selected;

        public string SelectVersion(int version)
        {
            if (version < 1 || version > rounds.Count) return Status = $"no training {version} yet ({rounds.Count} so far)";
            if (State == Mode.Autopilot) return Status = "busy: autopilot";
            selected = version;
            Status = $"training {version} selected for the Autopilot";
            return null;
        }

        public string DescribeVersion(int version)
        {
            if (version < 1 || version > rounds.Count) return null;
            var r = rounds[version - 1];
            string rehearsed = r.Finished != null && r.Order != null ? $"rehearsal {r.Finished[r.Order[0]]}/{Rehearsal.Starts}" : "no rehearsal";
            return $"v{version} · {r.Laps} laps · loss {r.Loss:0.0000} · {rehearsed}";
        }

        /// <summary>
        /// Said when the Autopilot's chosen training learned on another truck design than the one on the course now (the
        /// design was edited since): its laps were driven with other physics, so it may crash. Null when it matches, or
        /// nothing is trained.
        /// </summary>
        public string DesignNote
        {
            get
            {
                var round = selected > 0 && selected <= rounds.Count ? rounds[selected - 1] : null;
                if (round == null || round.Design == null || round.Design == TruckDesignStore.Fingerprint) return null;
                string old = round.DesignVersion > 0 ? "truck v" + round.DesignVersion : "the stock truck";
                return "Your autopilot learned on the old truck (" + old + "); this design drives differently, so it may crash. Drive two laps to retrain.";
            }
        }

        // " (it rolled over: too fast for this design in a tight turn)" after a roll-over, else "".
        string RolledNote() => Body != null && Body.RolledOver ? " (it rolled over: too fast in a tight turn for this design)" : "";

        public string[] InputNames => Thing != null && Thing.Host != null ? Thing.Host.IO.SensorNames : null;
        public float[] InputValues => Thing != null && Thing.Host != null ? Thing.Host.IO.Sensors : null;
        public string[] OutputNames => Thing != null && Thing.Host != null ? Thing.Host.IO.ActuatorNames : null;
        public float[] OutputValues => Thing != null && Thing.Host != null ? Thing.Host.IO.Actuators : null;

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
            ForgetSources();
            State = Mode.Lap;
            Status = driver != null ? "scripted lap: recording"
                : KeyHints ? "lap: drive through the gates with WASD or the thumbstick (recording)"
                : "lap: pinch to drive, tilt your hand to steer (recording)";
            return null;
        }

        // Every input source starts at rest: each writes once it moves (see Keys).
        void ForgetSources()
        {
            lastKeyForward = lastKeyLeft = lastHandThrottle = lastHandRoll = 0f;
            lastStick = Vector2.zero;
            bumpSign = 0;
        }

        void EndLap(string outcome)
        {
            var lap = Thing.EndLap(outcome);
            State = Mode.Idle;
            script = null;
            ManualInputs.Clear();
            // Free driving reads the sources next: what they last wrote is gone with the clear, so a stale "let go" from
            // one source (a pinch the lap ended on) can't zero another's first write.
            ForgetSources();
            Park();
            Status = $"lap {outcome}{RolledNote()} in {(lap != null ? lap.Seconds : 0f):0.0} s; {CompletedLaps} completed lap{(CompletedLaps == 1 ? "" : "s")}" +
                     Hint(CompletedLaps >= 2 ? " (T trains)" : " (L for another)");
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
            training = 0; trainFrames = 0; lastEpoch = 0;
            rehearsal = null; trainedFiles = null; order = null;
            loss.Clear();
            State = Mode.Training;
            Status = $"training {Candidates} candidates on {CompletedLaps} laps ({cloners[0].TrainCount} rows)";
            return null;
        }

        void TrainStep()
        {
            trainFrames++;
            if (training < cloners.Length)
            {
                var c = cloners[training];
                bool done = c.Step(BudgetMs);
                if (c.Epoch != lastEpoch) { lastEpoch = c.Epoch; if (!float.IsNaN(c.TrainLoss)) loss.Add(c.TrainLoss); }
                if (done) { training++; lastEpoch = 0; }
                if (training < cloners.Length) return;
                string error = BeginRehearsal();
                if (error != null) { State = Mode.Idle; Status = error; chainAutopilot = false; return; }
                if (rehearsal != null) return;
            }
            else if (rehearsal != null && !rehearsal.Step(BudgetMs)) return;
            FinishTraining();
        }

        // The trained candidates as controller files, then a rehearsal on the open course (none if it was closed).
        string BeginRehearsal()
        {
            trainedFiles = new ControllerFile[cloners.Length];
            for (int i = 0; i < cloners.Length; i++)
            {
                trainedFiles[i] = ControllerFile.Parse(cloners[i].ToControllerJson("truck_autopilot", i + 1, 1.5f), out string error);
                if (trainedFiles[i] == null) return error;
            }
            // The rehearsal's mock course and the pretrained fallback are the slalom's: other truck courses (dash, park, a
            // room course) fly their candidates in training order with no fallback.
            rehearsal = host != null && host.Build != null && IsSlalom ? new Rehearsal(trainedFiles, CourseFromSlots()) : null;
            if (rehearsal != null) Status = $"rehearsing {Candidates} candidates on the course";
            return null;
        }

        /// <summary>Rehearsal finishes per candidate (out of <see cref="Rehearsal.Starts"/>), in training order, or null.</summary>
        public int[] RehearsalFinished => rehearsal != null && rehearsal.Done ? rehearsal.Finished : null;

        void FinishTraining()
        {
            State = Mode.Idle;
            order = rehearsal != null ? rehearsal.Order() : null;
            double compute = 0;
            foreach (var c in cloners) compute += c.ComputeSeconds;
            // The notebook's training entry (C-17 section 3), as the Controllers sandbox's B reports it.
            var block = cloners[0].TrainingBlock();
            var list = new JArray();
            foreach (var c in cloners) list.Add(c.TrainingBlock());
            block["candidates"] = list;
            block["thing"] = "truck";
            block["course"] = host != null ? host.OpenId : null;
            block["round"] = rounds.Count + 1;
            rounds.Add(new Round { Files = trainedFiles, Order = order, Finished = rehearsal != null ? (int[])rehearsal.Finished.Clone() : null,
                                   Laps = CompletedLaps, Loss = cloners[0].TrainLoss, Course = host != null ? host.OpenId : null, Slalom = IsSlalom,
                                   Design = TruckDesignStore.Fingerprint, DesignVersion = TruckDesignStore.Version });
            selected = rounds.Count;
            TrainingLog.Report("truck_autopilot", 1, block);
            Status = $"trained {Candidates} candidates in {trainFrames} frames ({compute * 1000:0} ms compute)" + RehearsalLine() + Hint(". P: Autopilot");
            if (!chainAutopilot) return;
            chainAutopilot = false;
            StartAutopilot();
        }

        // "; in rehearsal the best finished 9 of 9 starts (the others 7 and 4), so it drives first"
        string RehearsalLine()
        {
            if (order == null) return "";
            var f = rehearsal.Finished;
            var others = new System.Text.StringBuilder();
            for (int i = 1; i < order.Length; i++) others.Append(i == 1 ? "" : i == order.Length - 1 ? " and " : ", ").Append(f[order[i]]);
            return $"; in rehearsal the best finished {f[order[0]]} of {Rehearsal.Starts} starts" +
                   (order.Length > 1 ? $" (the other{(order.Length > 2 ? "s" : "")} {others})" : "") + ", so it drives first";
        }

        JArray RehearsalInOrder()
        {
            var list = new JArray();
            foreach (int i in order) list.Add(rehearsal.Finished[i]);
            return list;
        }

        // ---- Autopilot ------------------------------------------------------------------------------------------

        /// <summary>Gives each candidate, then the pretrained fallback, a scored run until one completes.</summary>
        public string StartAutopilot()
        {
            if (State != Mode.Idle) return Status = "busy: " + State.ToString().ToLowerInvariant();
            if (cloners == null) return Status = "train first" + Hint(" (T)");
            if (trainedFiles == null) return Status = "train first" + Hint(" (T)");
            // Best rehearsal first (training order if there was no rehearsal).
            // The chosen round (the newest unless the panel or a tool picked another), best rehearsal first.
            var round = selected > 0 && selected <= rounds.Count ? rounds[selected - 1] : null;
            var useFiles = round != null ? round.Files : trainedFiles;
            var useOrder = round != null ? round.Order : order;
            var files = new System.Collections.Generic.List<ControllerFile>();
            for (int i = 0; i < useFiles.Length; i++) files.Add(useFiles[useOrder != null ? useOrder[i] : i]);
            ControllerFile fallback = null;
            if (IsSlalom)
            {
                var fallbackAsset = Resources.Load<TextAsset>("Controllers/truck_autopilot_fallback");
                fallback = fallbackAsset != null ? ControllerFile.Parse(fallbackAsset.text, out _) : null;
            }
            autopilot = new Autopilot(files, fallback);
            string engage = autopilot.Engage(Thing.Host);
            if (engage != null) return Status = engage;
            Attempts.Clear();
            Drove = null;
            State = Mode.Autopilot;
            string begin = BeginAttempt();
            string note = DesignNote;
            if (begin == null && note != null) Status = note + " " + Status;
            return begin;
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
                // Rehearsal finishes per candidate in the order the Autopilot tries them (best first), out of rehearsal_starts.
                ["rehearsal"] = order != null ? RehearsalInOrder() : null,
                ["rehearsal_starts"] = order != null ? Rehearsal.Starts : 0,
                ["autopilot"] = State == Mode.Autopilot ? autopilot.Describe() : null,
                ["drove"] = Drove,
                ["attempts"] = attempts,
                ["course"] = CourseId,
                ["selected_version"] = selected,
                ["design_note"] = DesignNote,
                ["truck_design_version"] = TruckDesignStore.Version,
                ["versions"] = Versions(),
            };
        }

        JArray Versions()
        {
            var list = new JArray();
            for (int i = 1; i <= rounds.Count; i++) list.Add(DescribeVersion(i));
            return list;
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
            Attempts.Add($"{who} {how}{RolledNote()}");
            string said = autopilot.RunEnded(Thing.Host, completed);
            if (!completed && autopilot.Exhausted && DesignNote != null) said += " " + DesignNote;
            if (completed) { Drove = who; State = Mode.Idle; Status = said; Park(); return; }
            if (autopilot.Exhausted) { Drove = "nothing"; State = Mode.Idle; Status = said; Park(); return; }
            Status = said;
            BeginAttempt();
        }

        // ---- The frame loop -------------------------------------------------------------------------------------

        string Arm()
        {
            if (host == null || host.Run == null) return "open the slalom (or another truck course) first" + Hint(" (2)");
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
        static string LapOutcome(bool completed, string how) => completed ? "completed" : how == "crashed" || how == OffTable ? "crashed" : "aborted";

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
                case Mode.Idle:
                    if (FreeDrive && !Hidden) Keys(false);
                    break;
                case Mode.Lap:
                    if (script == null) Keys(true);
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
            else if (State == Mode.Idle && FreeDrive && !Hidden && !Thing.tickInFixedUpdate) FreeStep(Time.fixedDeltaTime);
        }

        // One free-driving physics step (FreeDrive): truck_manual drives the same body a lap does. A touch, or a desk
        // course's table edge, puts the truck back where it was this step, stopped; touching going one way, it can still
        // go the other way, out of what it touched. No allocations.
        void FreeStep(float dt)
        {
            if (manual == null || Thing.Host == null) return;
            float throttle = stickY >= 0 ? ManualInputs.Values[stickY] : 0f;
            if (throttle == 0f && Mathf.Abs(Body.Speed) < 1e-3f) return;  // at rest: no sensing, no physics
            if (Thing.Host.File?.Name != manual.Name)
            {
                // The Autopilot's controller stays attached after its run: the truck stays parked until a hand drives.
                if (throttle == 0f) return;
                Thing.Host.Attach(manual);
            }
            var t = transform;
            Vector3 position = t.position;
            Quaternion rotation = t.rotation;
            float before = Body.Speed;
            if (Body.Crashed) Body.Place(position, rotation);  // a lap that ended in a touch: free to drive off it
            Thing.Tick(dt);
            if (!Body.Crashed && !LeftTable()) { bumpSign = 0; return; }
            int going = Mathf.Abs(before) > 1e-3f ? (before > 0f ? 1 : -1) : throttle > 0f ? 1 : throttle < 0f ? -1 : 0;
            Body.Place(position, rotation);
            if (bumpSign != 0 && going == -bumpSign)
            {
                // Backing out of (or driving forward off) what it touched: at the hand's speed, straight, until clear.
                Body.Place(position + rotation * Vector3.forward * (going * KeySpeed * Body.Scale * dt), rotation);
                return;
            }
            if (going != 0) bumpSign = going;
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
            if (LeftTable()) { Body.Crash(); run.Abort(); how = OffTable; return true; }
            if (Body.Crashed) { run.Abort(); how = "crashed"; return true; }
            if (Time.time - runStarted > RunTimeoutS) { run.Abort(); how = "timed out"; return true; }
            return false;
        }

        // keyboard: WASD too (laps). Free driving leaves the keyboard to the desktop rig, which walks with WASD.
        void Keys(bool keyboard)
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (keyboard && KeysFromKeyboard && k != null)
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
            // Hands, with no controller paired (HandsTransmitter): a pinch drives at the keys' speed, the hand's tilt
            // steers through truck_manual's hand.roll. Written on change only, like the rest.
            HandsTransmitter.Read(out float handThrottle, out float roll);
            if (handThrottle != lastHandThrottle || roll != lastHandRoll)
            {
                lastHandThrottle = handThrottle; lastHandRoll = roll;
                ManualInputs.Values[stickY] = handThrottle * keyThrottle;
                ManualInputs.Values[handRoll] = -roll;  // hand.roll's +1 is left (as the A key), the transmitter's +1 is right
            }
        }

        // The truck's pose in the course's frame (x along the course, y to the left), for the scripted driver. A desk
        // course's slots are full size (the scene root is scaled), so a miniature's pose is read back at full size too.
        void Mirror()
        {
            var origin = host.SceneOrigin;
            var toCourse = Quaternion.Inverse(origin.rotation);
            float toFull = 1f / CourseScale;
            var local = toCourse * (transform.position - origin.position) * toFull;
            var forward = toCourse * transform.forward;
            view.X = local.z; view.Y = -local.x;
            view.Heading = Mathf.Atan2(-forward.x, forward.z);
            view.Speed = Body.Speed * toFull;
        }

        // The open slalom's slots as a MockCourse (cones, gates, finish; no walls: the driver only needs the path). The
        // slots are in the scene root's frame, full size even on a desk (the root is scaled), so the rehearsal and the
        // scripted driver work at full size, where a miniature's sensors read the same (ExampleTruckBody.SetScale).
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
