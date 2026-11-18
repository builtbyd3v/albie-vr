using System.Collections.Generic;
using Forge.Controllers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The drone's learning loop on any drone course (hoop course, hover, landing, a room course for the drone): the
    /// peer of the truck's <see cref="SlalomBrain"/>, behind the same <see cref="IBrainLoop"/> (the <c>brain_*</c> and
    /// <c>autopilot_*</c> tools, the scoreboard's <c>drive_lap</c>/<c>albie_drives</c> buttons, the Controller brain panel).
    /// - A lap is a scored run of the open course flown by hand (drone_manual: sticks, pads, keys, hands). Each physics
    ///   step while it flies, the course features (<see cref="DroneCourseMath"/>: the run engine's next target, the
    ///   drone's velocity, in the course's frame) are recorded with what the pilot commanded, turned into that frame.
    ///   The order of the targets is recorded too: the course line the rehearsal flies.
    /// - <see cref="Train"/> clones the laps into <see cref="Candidates"/> small nets (the truck's
    ///   <see cref="BehaviourCloner"/>, 7 inputs, 8x8 tanh, 4 outputs), <see cref="BudgetMs"/> of compute a frame, then
    ///   rehearses each on the recorded line (<see cref="DroneRehearsal"/>). Every training is a new version
    ///   (drone_autopilot v1, v2 ...), reported to <see cref="TrainingLog"/> as the truck's is.
    /// - <see cref="StartAutopilot"/> flies the chosen version's candidates best-first, each a scored run, until one
    ///   completes. A version no candidate of which passed rehearsal is refused, with why: it never flies badly on purpose.
    ///   <see cref="StartFollowLine"/> is the non-learned course-line follower, only ever on explicit request and named so.
    /// - Hand-back: any stick past <see cref="HandBack"/>, Stop, or <c>autopilot_off</c> gives the drone back to the
    ///   manual controller, hovering where it is. A crash, or <see cref="RunTimeoutS"/>, ends the run.
    /// Drives the drone only through <see cref="ArtifactDrone"/>'s public commands and its controller host.
    /// </summary>
    [DefaultExecutionOrder(-50)]  // before ControllerThing's FixedUpdate: records the step's features before it moves
    public sealed class DroneBrain : MonoBehaviour, ICourseBrain
    {
        public const int Candidates = 3;
        public const double BudgetMs = 4.0;
        public const float RunTimeoutS = 90f;
        /// <summary>A stick moved this far during the Autopilot takes the drone back.</summary>
        public const float HandBack = 0.3f;
        /// <summary>Fewer recorded rows than this is too little flying to learn from.</summary>
        public const int MinRows = 150;
        const int Hz = 50, MaxSeconds = 120, Capacity = Hz * MaxSeconds;
        public enum Mode { Idle, Lap, Training, Autopilot }

        public ArtifactDrone Drone { get; private set; }
        public Mode State { get; private set; }
        public string Status { get; private set; } = "drone brain: open a drone course, then fly a lap by hand";
        public string Drove { get; private set; }
        public readonly List<string> Attempts = new List<string>();

        /// <summary>One training: its candidates, rehearsal and provenance.</summary>
        public sealed class Version
        {
            public int Number, Laps, Rows;
            public string Course;
            public float Loss;
            public string[] Files;
            public IDronePolicy[] Policies;
            public ControllerFile[] Parsed;
            public int[] Finished, Order;
            public bool Ready;
            public JObject Training;
        }
        public readonly List<Version> Versions = new List<Version>();

        ArtifactHost host;
        LapSet set;
        string setCourse;
        IThingController manualController;
        ControllerFile manualFile;
        readonly float[] xs = new float[Capacity * DroneCourseMath.Features], ys = new float[Capacity * DroneCourseMath.Actuators];
        int rows, nextLapId = 1;
        float idle, lapSeconds;
        readonly float[] feat = new float[DroneCourseMath.Features], pending = new float[DroneCourseMath.Features];
        bool havePending;
        float pendingHeading;
        // The course line: targets in the course frame (from the start, on its floor) in the order the run gave them.
        readonly List<float[]> lapTargets = new List<float[]>();
        List<float[]> courseLine;
        readonly List<float> lapTimes = new List<float>();
        int lapTarget;
        bool chainAutopilot;

        BehaviourCloner[] cloners;
        int training, lastEpoch;
        DroneRehearsal rehearsal;
        Version building;
        readonly List<float> loss = new List<float>(512);

        int selected;  // 1-based, 0 none
        int[] flyOrder;
        int attempt;
        IDronePolicy flying;
        bool followLine;
        float runStarted;
        CourseNetController netController;

        Camera cam;
        float pilotYaw;
        readonly int stickX = ManualInputs.IndexOf("stick.x"), stickY = ManualInputs.IndexOf("stick.y"),
                     stick2X = ManualInputs.IndexOf("stick2.x"), stick2Y = ManualInputs.IndexOf("stick2.y");

        // ---- Spawning ------------------------------------------------------------------------------------------------

        /// <summary>The app: every build of a drone course makes sure the drone carries its brain (after <see cref="ArtifactBootstrap.EnsureDrone"/>).</summary>
        public static DroneBrain Ensure(ArtifactHost host)
        {
            if (host == null || !ArtifactDrone.IsDroneCourse(host)) return null;
            var drone = Object.FindFirstObjectByType<ArtifactDrone>();
            if (drone == null) return null;
            var brain = drone.GetComponent<DroneBrain>();
            if (brain != null) return brain;
            brain = drone.gameObject.AddComponent<DroneBrain>();
            brain.Bind(drone, host);
            return brain;
        }

        void Bind(ArtifactDrone drone, ArtifactHost h)
        {
            Drone = drone;
            host = h;
            manualController = drone.Thing.Host.Current;
            manualFile = drone.Thing.Host.File;
            host.Shown += OnHostShown;
            host.Closed += OnHostClosed;
            host.Fired += OnHostButton;
            OnHostShown(host);
        }

        void OnDestroy()
        {
            BrainLoops.Unregister(this);
            if (host == null) return;
            host.Shown -= OnHostShown;
            host.Closed -= OnHostClosed;
            host.Fired -= OnHostButton;
        }

        bool OnCourse => Drone != null && !Drone.Hidden && host != null && host.Run != null && ArtifactDrone.IsDroneCourse(host);

        void OnHostShown(ArtifactHost h)
        {
            if (!ArtifactDrone.IsDroneCourse(h)) { OnHostClosed(h); return; }
            string id = h.OpenId;
            if (set == null || setCourse != id)
            {
                // Laps and the course line belong to one course; trained versions are kept (their features are course-relative).
                set = new LapSet { Thing = ArtifactDrone.ThingName, ThingVersion = ArtifactDrone.ThingVersion, Controller = "drone_manual", Hz = Hz,
                                   Sensors = (string[])DroneCourseMath.FeatureNames.Clone(), Actuators = (string[])DroneCourseMath.ActuatorNames.Clone() };
                setCourse = id;
                courseLine = null;
                lapTimes.Clear();
            }
            if (State == Mode.Lap || State == Mode.Autopilot) Halt("the course was rebuilt");
            BrainLoops.Register(this);
            if (State == Mode.Idle) Status = $"drone at the start of {id}: {CompletedLaps} completed lap{(CompletedLaps == 1 ? "" : "s")}";
        }

        void OnHostClosed(ArtifactHost h)
        {
            if (State == Mode.Lap || State == Mode.Autopilot) Halt("the course closed");
            BrainLoops.Unregister(this);
        }

        void OnHostButton(string input)
        {
            if (!OnCourse) return;
            if (input == "drive_lap" || input == "fly_lap") Record(1, false);
            else if (input == "albie_drives" || input == "albie_flies")
            {
                if (State != Mode.Idle) { Status = "busy: " + ModeName; return; }
                if (CompletedLaps < 2) Record(2 - CompletedLaps, true);
                else chainAutopilot = Train() == null;
            }
        }

        public int CompletedLaps => set != null ? set.CompletedLaps : 0;
        int RowCount { get { int n = 0; if (set != null) foreach (var l in set.Laps) if (l.Outcome == "completed") n += l.X.Length; return n; } }

        // ---- Laps ----------------------------------------------------------------------------------------------------

        public string Record(int laps, bool thenAutopilot)
        {
            if (State != Mode.Idle) return Status = "busy: " + ModeName;
            if (!OnCourse) return Status = NoCourse;
            lapTarget = CompletedLaps + Mathf.Clamp(laps, 1, 10);
            chainAutopilot = thenAutopilot;
            string error = BeginLap();
            if (error != null) { lapTarget = 0; chainAutopilot = false; }
            return error;
        }

        const string NoCourse = "no_course: open a drone course first (the hoop course, hover or landing); the drone stands at its start";

        string BeginLap()
        {
            AttachManual();
            if (Drone.Body.Following) Drone.Body.EndFollow("lap");
            Drone.ResetToStart();
            string error = Drone.TakeOff();
            if (error != null) return Status = error;
            var run = host.Run;
            if (run == null || (run.State != ArtifactRunState.Armed && run.State != ArtifactRunState.Running))
                return Status = "the course did not arm a run; press Fly again on its panel";
            rows = 0; idle = 0f; lapSeconds = 0f; havePending = false;
            lapTargets.Clear();
            runStarted = Time.time;
            State = Mode.Lap;
            int left = lapTarget - CompletedLaps;
            Status = $"lap: fly the course by hand, through every target in order ({left} more completed lap{(left == 1 ? "" : "s")} to go; recording)";
            return null;
        }

        public string StopRecording()
        {
            if (State != Mode.Lap) return Status = "not recording a lap";
            lapTarget = 0; chainAutopilot = false;
            if (host != null && host.Run != null) host.Run.Abort();
            EndLap("aborted");
            Drone.Stop();
            return null;
        }

        void EndLap(string outcome)
        {
            State = Mode.Idle;
            if (rows > 0)
            {
                var lap = new Lap { Id = nextLapId++, Seconds = Mathf.Round(lapSeconds * 100f) / 100f, Outcome = outcome,
                                    X = new float[rows][], Y = new float[rows][] };
                for (int t = 0; t < rows; t++)
                {
                    lap.X[t] = new float[DroneCourseMath.Features];
                    lap.Y[t] = new float[DroneCourseMath.Actuators];
                    System.Array.Copy(xs, t * DroneCourseMath.Features, lap.X[t], 0, DroneCourseMath.Features);
                    System.Array.Copy(ys, t * DroneCourseMath.Actuators, lap.Y[t], 0, DroneCourseMath.Actuators);
                }
                set.Laps.Add(lap);
                if (outcome == "completed")
                {
                    courseLine = new List<float[]>(lapTargets);
                    lapTimes.Add(lapSeconds);
                }
            }
            if (outcome == "completed") Drone.Land();
            Status = $"lap {outcome} in {lapSeconds:0.0} s; {CompletedLaps} completed lap{(CompletedLaps == 1 ? "" : "s")}";
            if (lapTarget <= 0) return;
            if (CompletedLaps < lapTarget)
            {
                string said = Status, error = BeginLap();
                if (error != null) { lapTarget = 0; chainAutopilot = false; Status = said + ". " + error; }
                else Status = said + ". " + Status;
                return;
            }
            lapTarget = 0;
            if (!chainAutopilot) return;
            chainAutopilot = Train() == null;
        }

        // ---- Training ------------------------------------------------------------------------------------------------

        public string Train()
        {
            if (State != Mode.Idle) return Status = "busy: " + ModeName;
            if (set == null || CompletedLaps < 2) return Status = $"fly 2 completed laps of this course first ({CompletedLaps} so far): fewer is too little to learn from";
            int n = RowCount;
            if (n < MinRows) return Status = $"only {n} steps of flying recorded ({MinRows} needed): fly another lap or two first";
            cloners = new BehaviourCloner[Candidates];
            for (int i = 0; i < Candidates; i++) cloners[i] = new BehaviourCloner(set, new BehaviourCloner.Options { Seed = i + 1 });
            training = 0; lastEpoch = 0; rehearsal = null;
            loss.Clear();
            building = new Version { Number = Versions.Count + 1, Laps = CompletedLaps, Rows = n, Course = setCourse };
            State = Mode.Training;
            Status = $"training v{building.Number}: {Candidates} candidates on {CompletedLaps} laps ({cloners[0].TrainCount} rows)";
            return null;
        }

        void TrainStep()
        {
            if (training < cloners.Length)
            {
                var c = cloners[training];
                bool done = c.Step(BudgetMs);
                if (c.Epoch != lastEpoch) { lastEpoch = c.Epoch; if (!float.IsNaN(c.TrainLoss)) loss.Add(c.TrainLoss); }
                if (!done) return;
                training++; lastEpoch = 0;
                if (training < cloners.Length) return;
                string error = BeginRehearsal();
                if (error != null) { State = Mode.Idle; Status = error; chainAutopilot = false; return; }
                return;
            }
            if (rehearsal != null && !rehearsal.Step(BudgetMs)) return;
            FinishTraining();
        }

        string BeginRehearsal()
        {
            var v = building;
            v.Files = new string[cloners.Length];
            v.Policies = new IDronePolicy[cloners.Length];
            v.Parsed = new ControllerFile[cloners.Length];
            for (int i = 0; i < cloners.Length; i++)
            {
                v.Files[i] = cloners[i].ToControllerJson("drone_autopilot", v.Number, Drone.Body.maxSpeed);
                var policy = LearnedPolicy.From(v.Files[i], out string error);
                if (policy == null) return "training made a file that does not run: " + error;
                v.Policies[i] = policy;
                v.Parsed[i] = policy.File;
            }
            if (courseLine == null || courseLine.Count == 0) { rehearsal = null; return null; }
            var b = Drone.Body;
            var sim = new DroneFlightSim { MaxSpeed = b.maxSpeed, Accel = b.accel, Brake = b.brake, ClimbSpeed = b.climbSpeed,
                                           AltitudeGain = b.altitudeGain, Low = b.minHover, High = b.maxAltitude };
            float mean = 0f;
            foreach (var t in lapTimes) mean += t;
            mean = lapTimes.Count > 0 ? mean / lapTimes.Count : 30f;
            rehearsal = new DroneRehearsal(v.Policies, courseLine, sim, b.takeoffHeight, Mathf.Max(20f, 2.5f * mean));
            Status = $"rehearsing v{v.Number}'s {Candidates} candidates on the course line";
            return null;
        }

        void FinishTraining()
        {
            State = Mode.Idle;
            var v = building;
            building = null;
            v.Loss = cloners[0].TrainLoss;
            if (rehearsal != null)
            {
                v.Finished = (int[])rehearsal.Finished.Clone();
                v.Order = rehearsal.Order();
                v.Ready = v.Finished[v.Order[0]] >= DroneRehearsal.MinFinishes;
            }
            else
            {
                // No course line recorded (should not happen after a completed lap): nothing vouches for it.
                v.Order = new int[cloners.Length];
                for (int i = 0; i < v.Order.Length; i++) v.Order[i] = i;
                v.Ready = false;
            }
            // The notebook's training entry, as the truck's (SlalomBrain.FinishTraining): TrainingLog.Report.
            var block = cloners[0].TrainingBlock();
            var list = new JArray();
            foreach (var c in cloners) list.Add(c.TrainingBlock());
            block["candidates"] = list;
            block["thing"] = ArtifactDrone.ThingName;
            block["course"] = v.Course;
            block["round"] = v.Number;
            block["ready"] = v.Ready;
            if (v.Finished != null)
            {
                var r = new JArray();
                foreach (int i in v.Order) r.Add(v.Finished[i]);
                block["rehearsal"] = r;
                block["rehearsal_starts"] = DroneRehearsal.Starts;
            }
            v.Training = block;
            Versions.Add(v);
            selected = v.Number;
            TrainingLog.Report("drone_autopilot", v.Number, block);
            Status = v.Ready
                ? $"trained v{v.Number}: in rehearsal the best candidate finished {v.Finished[v.Order[0]]} of {DroneRehearsal.Starts} practice flights, so it can fly"
                : NotReady(v);
            if (!chainAutopilot) return;
            chainAutopilot = false;
            if (v.Ready) StartAutopilot();
        }

        static string NotReady(Version v)
        {
            string how = v.Finished != null
                ? $"in rehearsal its best candidate finished only {v.Finished[v.Order[0]]} of {DroneRehearsal.Starts} practice flights"
                : "there was no course line to rehearse on";
            return $"v{v.Number} isn't ready to fly: {how}. Fly a couple more clean laps and train again, or ask for \"follow the course line\" (not learned)";
        }

        // ---- Autopilot -----------------------------------------------------------------------------------------------

        public string StartAutopilot()
        {
            if (State != Mode.Idle) return Status = "busy: " + ModeName;
            if (!OnCourse) return Status = NoCourse;
            if (selected <= 0 || selected > Versions.Count) return Status = "train first: fly 2 laps, then train";
            var v = Versions[selected - 1];
            if (!v.Ready) return Status = NotReady(v);
            int n = 0;
            foreach (int i in v.Order) if (v.Finished == null || v.Finished[i] >= DroneRehearsal.MinFinishes) n++;
            flyOrder = new int[n];
            n = 0;
            foreach (int i in v.Order) if (v.Finished == null || v.Finished[i] >= DroneRehearsal.MinFinishes) flyOrder[n++] = i;
            followLine = false;
            attempt = 0;
            Attempts.Clear();
            Drove = null;
            return BeginAttempt();
        }

        /// <summary>The non-learned course-line follower, only on explicit request. Never presented as a trained model.</summary>
        public string StartFollowLine()
        {
            if (State != Mode.Idle) return Status = "busy: " + ModeName;
            if (!OnCourse) return Status = NoCourse;
            followLine = true;
            flyOrder = new[] { 0 };
            attempt = 0;
            Attempts.Clear();
            Drove = null;
            return BeginAttempt();
        }

        string Who => followLine ? "follow the course line (not learned)" : $"v{selected} candidate {flyOrder[attempt] + 1}";

        string BeginAttempt()
        {
            var v = followLine ? null : Versions[selected - 1];
            flying = followLine ? new FollowLinePolicy() : v.Policies[flyOrder[attempt]];
            if (Drone.Body.Following) Drone.Body.EndFollow("autopilot");
            Drone.ResetToStart();
            ZeroSticks();
            netController = new CourseNetController(this, flying, followLine ? "graph" : "learned");
            string error = followLine
                ? Drone.Thing.Host.Attach(netController)
                : Drone.Thing.Host.Attach(netController, v.Parsed[flyOrder[attempt]], float.PositiveInfinity);
            if (error == null) error = Drone.TakeOff();
            var run = host.Run;
            if (error == null && (run == null || (run.State != ArtifactRunState.Armed && run.State != ArtifactRunState.Running)))
                error = "the course did not arm a run";
            if (error != null) { AttachManual(); State = Mode.Idle; return Status = error; }
            runStarted = Time.time;
            State = Mode.Autopilot;
            Status = $"Autopilot: {Who} flying (any stick takes it back)";
            return null;
        }

        public string StopAutopilot()
        {
            if (State != Mode.Autopilot) return Status = "the Autopilot isn't flying";
            Halt("stopped");
            Status = "Autopilot stopped; the drone hovers where it is, back on the manual controller";
            return null;
        }

        // Ends a lap or an Autopilot run where it is: the run is dropped, the sticks are the pilot's, it hovers.
        void Halt(string why)
        {
            if (State == Mode.Lap) { lapTarget = 0; chainAutopilot = false; if (host != null && host.Run != null) host.Run.Abort(); EndLap("aborted"); }
            else if (State == Mode.Autopilot)
            {
                Attempts.Add($"{Who} {why}");
                if (host != null && host.Run != null) host.Run.Abort();
                AttachManual();
                State = Mode.Idle;
                Drove = null;
            }
            if (Drone != null && !Drone.Hidden) Drone.Stop();
        }

        void AttemptEnded(bool completed, string how)
        {
            string who = Who;
            Attempts.Add($"{who} {how}");
            if (completed)
            {
                Drove = who;
                State = Mode.Idle;
                AttachManual();
                Drone.Land();
                Status = $"{who} flew the course: keeping it";
                return;
            }
            attempt++;
            if (attempt >= flyOrder.Length)
            {
                Drove = "nothing";
                State = Mode.Idle;
                AttachManual();
                if (!Drone.Body.Crashed) Drone.Stop();
                Status = $"{who} {how}, and no candidate is left. Fly more laps and train again";
                return;
            }
            string said = $"{who} {how}: trying {Who}";
            string error = BeginAttempt();
            Status = error ?? said;
        }

        void AttachManual()
        {
            var h = Drone.Thing.Host;
            if (manualFile != null) { if (h.File != manualFile) h.Attach(manualFile); }
            else if (manualController != null && h.Current != manualController) h.Attach(manualController);
            netController = null;
        }

        void ZeroSticks()
        {
            var v = ManualInputs.Values;
            if (stickX >= 0) v[stickX] = 0f;
            if (stickY >= 0) v[stickY] = 0f;
            if (stick2X >= 0) v[stick2X] = 0f;
            if (stick2Y >= 0) v[stick2Y] = 0f;
        }

        // The body's own test for "the pilot took over" (every manual stick channel), at this loop's threshold.
        static bool SticksTouched() => DroneBody.SticksActive(HandBack);

        // ---- The frame -----------------------------------------------------------------------------------------------

        void Update()
        {
            if (cam == null || !cam.isActiveAndEnabled) cam = Camera.main;
            if (cam != null) pilotYaw = cam.transform.eulerAngles.y;
            switch (State)
            {
                case Mode.Lap:
                    if (RunOver(out bool completed, out string how)) EndLap(completed ? "completed" : how == "crashed" ? "crashed" : "aborted");
                    break;
                case Mode.Training:
                    TrainStep();
                    break;
                case Mode.Autopilot:
                    if (SticksTouched()) { Halt("handed back"); Status = "you took the sticks: the drone is yours, hovering where it was"; break; }
                    if (Drone.Body.Following) { Halt("handed to follow-me"); Status = "follow-me took over: the Autopilot stopped"; break; }
                    if (RunOver(out bool done, out string why)) AttemptEnded(done, why);
                    break;
            }
        }

        bool RunOver(out bool completed, out string how)
        {
            completed = false; how = null;
            var run = host != null ? host.Run : null;
            if (run == null || Drone.Hidden) { how = "closed"; return true; }
            if (run.State == ArtifactRunState.Finished)
            {
                completed = run.Outcome == "completed";
                how = completed ? "completed" : $"{run.Outcome} ({run.Reason ?? "-"})";
                return true;
            }
            if (Drone.Body.Crashed) { run.Abort(); how = "crashed"; return true; }
            if (Time.time - runStarted > RunTimeoutS) { run.Abort(); how = "timed out"; return true; }
            return false;
        }

        /// <summary>The heading the sticks' forward points along (the pilot's, flying headless), degrees.</summary>
        float CommandHeading => Drone.Body.headless ? pilotYaw : Drone.transform.eulerAngles.y;
        float CourseYaw => Drone.Start.rotation.eulerAngles.y;

        void FixedUpdate()
        {
            if (Drone == null || Drone.Hidden) return;
            FillFeatures(feat);
            if (State != Mode.Lap) return;
            float dt = Time.fixedDeltaTime;
            lapSeconds += dt;
            TrackTarget();
            bool airborne = Drone.Body.State == DroneBody.Phase.Flying;
            // The command in Commanded now is the last step's, given the features kept then.
            if (havePending && airborne) Keep(Drone.Thing.Host.Commanded, pendingHeading, dt);
            havePending = airborne;
            if (airborne) { System.Array.Copy(feat, pending, feat.Length); pendingHeading = CommandHeading; }
        }

        void Keep(float[] commanded, float heading, float dt)
        {
            bool still = true;
            for (int i = 0; i < commanded.Length; i++) if (Mathf.Abs(commanded[i]) > LapRecorder.IdleThreshold) { still = false; break; }
            idle = still ? idle + dt : 0f;
            if (idle > LapRecorder.IdleSeconds || rows >= Capacity) return;
            // Pilot frame to course frame: forward is z, right is x.
            float x = commanded[1], z = commanded[0];
            DroneCourseMath.Turn(heading - CourseYaw, ref x, ref z);
            int o = rows * DroneCourseMath.Actuators;
            ys[o] = Mathf.Clamp(z, -1f, 1f); ys[o + 1] = Mathf.Clamp(x, -1f, 1f); ys[o + 2] = commanded[2]; ys[o + 3] = commanded[3];
            System.Array.Copy(pending, 0, xs, rows * DroneCourseMath.Features, DroneCourseMath.Features);
            rows++;
        }

        // Each new target the run engine points at, in the course frame, in order: the course line.
        void TrackTarget()
        {
            if (!CourseBeacon.HasTarget) return;
            Vector3 local = ToCourse(CourseBeacon.Target - Drone.Start.position);
            if (lapTargets.Count > 0)
            {
                var last = lapTargets[lapTargets.Count - 1];
                float dx = last[0] - local.x, dy = last[1] - local.y, dz = last[2] - local.z;
                if (dx * dx + dy * dy + dz * dz < 0.0025f) return;
            }
            lapTargets.Add(new[] { local.x, local.y, local.z });  // once per target, not per step
        }

        Vector3 ToCourse(Vector3 world)
        {
            float x = world.x, z = world.z;
            DroneCourseMath.Turn(-CourseYaw, ref x, ref z);
            return new Vector3(x, world.y, z);
        }

        /// <summary>The features for the drone now (the autopilot's controller calls it each physics step).</summary>
        public void FillFeatures(float[] f)
        {
            var b = Drone.Body;
            Vector3 d = CourseBeacon.HasTarget ? ToCourse(CourseBeacon.Target - Drone.transform.position) : Vector3.zero;
            Vector3 v = ToCourse(b.Velocity);
            DroneCourseMath.Fill(f, CourseBeacon.HasTarget, d.x, d.y, d.z, v.x, v.y, v.z, b.maxSpeed, b.climbSpeed);
        }

        /// <summary>Turns the policy's course-frame (forward, right) into the sticks' frame of the moment.</summary>
        public void ToSticks(ref float forward, ref float right)
        {
            float x = right, z = forward;
            DroneCourseMath.Turn(CourseYaw - CommandHeading, ref x, ref z);
            forward = z; right = x;
        }

        // ---- State for the tools and the panel -----------------------------------------------------------------------

        public JObject ToolState()
        {
            var attempts = new JArray();
            foreach (var a in Attempts) attempts.Add(a);
            var versions = new JArray();
            foreach (var v in Versions) versions.Add(DescribeVersion(v.Number));
            int trained = 0;
            if (cloners != null) foreach (var c in cloners) if (c.Done) trained++;
            return new JObject
            {
                ["mode"] = ModeName,
                ["status"] = Status,
                ["preview_only"] = false,
                ["thing"] = ArtifactDrone.ThingName,
                ["controller"] = Drone != null && Drone.Thing.Host.Current != null ? Drone.Thing.Host.Current.Name : null,
                ["course"] = CourseId,
                ["course_open"] = OnCourse,
                ["completed_laps"] = CompletedLaps,
                ["recorded_steps"] = RowCount,
                ["laps_to_go"] = lapTarget > 0 ? Mathf.Max(0, lapTarget - CompletedLaps) : 0,
                ["then_autopilot"] = chainAutopilot,
                ["candidates"] = Candidates,
                ["candidates_trained"] = trained,
                ["versions"] = versions,
                ["selected_version"] = selected,
                ["ready"] = selected > 0 && Versions[selected - 1].Ready,
                ["autopilot"] = State == Mode.Autopilot ? Who : null,
                ["drove"] = Drove,
                ["attempts"] = attempts,
            };
        }

        public string ThingName => ArtifactDrone.ThingName;
        public string CourseId => OnCourse ? host.OpenId : null;
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
                // Training is 90 % of the work shown, the rehearsal the rest.
                float p = total > 0f ? 0.9f * epochs / total : 0f;
                return rehearsal != null && rehearsal.Done ? 1f : p;
            }
        }
        public IReadOnlyList<float> LossHistory => loss;
        public int VersionCount => Versions.Count;
        public int SelectedVersion => selected;

        public string SelectVersion(int version)
        {
            if (version < 1 || version > Versions.Count) return Status = $"no version {version} (there {(Versions.Count == 1 ? "is" : "are")} {Versions.Count})";
            if (State == Mode.Autopilot) return Status = "busy: autopilot";
            selected = version;
            Status = $"v{version} selected for the Autopilot" + (Versions[version - 1].Ready ? "" : " (it isn't ready to fly)");
            return null;
        }

        public string DescribeVersion(int version)
        {
            if (version < 1 || version > Versions.Count) return null;
            var v = Versions[version - 1];
            string rehearsed = v.Finished != null ? $"rehearsal {v.Finished[v.Order[0]]}/{DroneRehearsal.Starts}" : "no rehearsal";
            return $"v{v.Number} · {v.Laps} laps · loss {v.Loss:0.0000} · {rehearsed}{(v.Ready ? "" : " · not ready")}";
        }

        public string[] InputNames => DroneCourseMath.FeatureNames;
        public float[] InputValues => feat;
        public string[] OutputNames => DroneCourseMath.ActuatorNames;
        public float[] OutputValues => Drone != null && Drone.Thing.Host != null ? Drone.Thing.Host.IO.Actuators : null;
    }

    /// <summary>
    /// The drone's Autopilot as a controller on its own host: each tick the brain fills the course features, the policy
    /// (a trained net, or the course-line follower) answers in the course frame, and the answer is turned into the
    /// sticks' frame. No allocations per tick.
    /// </summary>
    public sealed class CourseNetController : IThingController
    {
        readonly DroneBrain brain;
        readonly IDronePolicy policy;
        readonly string kind;
        readonly float[] features = new float[DroneCourseMath.Features], commands = new float[DroneCourseMath.Actuators];
        int forward = -1, right = -1, climb = -1, yaw = -1;

        public CourseNetController(DroneBrain brain, IDronePolicy policy, string kind)
        {
            this.brain = brain; this.policy = policy; this.kind = kind;
        }

        public string Kind => kind;
        public string Name => policy.Name;

        public string Bind(ControllerIO io)
        {
            forward = io.ActuatorIndex("fly.forward"); right = io.ActuatorIndex("fly.right");
            climb = io.ActuatorIndex("fly.climb"); yaw = io.ActuatorIndex("fly.yaw");
            if (forward < 0 || right < 0 || climb < 0 || yaw < 0) return "unknown_channel: this Thing has no fly.* actuators (it has " + io.DescribeActuators() + ")";
            return null;
        }

        public void Tick(ControllerIO io, float dt)
        {
            brain.FillFeatures(features);
            policy.Act(features, commands);
            float f = commands[0], r = commands[1];
            brain.ToSticks(ref f, ref r);
            var a = io.Actuators;
            a[forward] = f; a[right] = r; a[climb] = commands[2]; a[yaw] = commands[3];
        }

        public void ResetState() { }
    }
}
