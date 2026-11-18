using System;
using System.Collections.Generic;
using Forge.Artifacts;
using Forge.Controllers;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// The open project, as one thing that outlives any window: which project, which stage the person is at, how every
    /// stage stands (<see cref="ProjectStages"/>, judged from real state gathered here), the last result line, and the stage
    /// actions (each opens the tool it needs and answers with one plain sentence). The project bar
    /// (<see cref="OSProjectRail"/>) and the project window (<see cref="OSProjectWindow"/>) are views of it.
    ///
    /// <see cref="Go"/> is moving to a stage: a locked stage only says why and which stage unlocks it; otherwise the previous
    /// stage's tool is put away (its window closed, its Workspace mode closed) unless the new stage uses it too, the project
    /// window is tucked (hidden, one tap away on the bar), the stage's action runs and the bar docks under the tool it opened.
    /// The room's course is never closed by a stage change. <see cref="Do"/> runs one action and nothing else (the Drive
    /// window and older callers use it).
    ///
    /// What is gathered: the design version (CAD's version as the project pins or CAD showed it in this project, or the
    /// truck's saved version), the courses linked to the project, the laps on the live brain, training entries and the
    /// versions cut after them, Autopilot runs (seen as the brain leaving Autopilot), simulations, launch-pad runs, and the
    /// ML desk steps taken. What can only be seen once (a simulation, a lap count, an Autopilot run, the data opened) is kept
    /// in the project's record (<see cref="ProjectMeta"/>, field <c>stages</c>) with the stage the person was at, so a project
    /// opens where it was left. Writes happen on a change only, never per frame; the poll compares a few numbers and
    /// allocates nothing.
    /// </summary>
    public sealed class OSProjectSession : MonoBehaviour
    {
        const float PollSeconds = 0.25f, NoticeSeconds = 8f;
        /// <summary>The record field everything here is kept under.</summary>
        public const string RecordField = "stages";

        static OSProjectSession instance;

        /// <summary>The session, or null before anything opened a project.</summary>
        public static OSProjectSession Current => instance;

        /// <summary>Raised after the stages were judged again or the status line changed (views refresh).</summary>
        public event Action Changed;

        string projectId, design, mlName, current, statusLine = "", notice, noticeUnlock;
        OSProjectKind kind;
        OSProjectWindow.Loop loop;
        StageLoop stageLoop;
        bool dirty, linkDirty = true, begun;
        float nextPoll, noticeUntil;
        int pollKey = int.MinValue, lastMode = -1;
        ArtifactHost watchedHost;
        string watchedOpenId;
        CadSession cad;
        readonly StageFacts facts = new StageFacts();
        readonly List<StageState> stages = new List<StageState>(6);
        readonly List<StageStatus> before = new List<StageStatus>(6);
        int next = -1;
        readonly List<OSProjects.LinkedArtifact> linked = new List<OSProjects.LinkedArtifact>();
        string courseName = "slalom";

        // ---------------------------------------------------------------- lifetime

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => instance = null;

        /// <summary>The session, made the first time (one hidden object that lives as long as the app).</summary>
        public static OSProjectSession Ensure()
        {
            if (instance != null) return instance;
            var go = new GameObject(nameof(OSProjectSession)) { hideFlags = HideFlags.HideInHierarchy };
            if (Application.isPlaying) DontDestroyOnLoad(go);
            instance = go.AddComponent<OSProjectSession>();
            return instance;
        }

        void OnEnable()
        {
            NotebookStore.EntryCreated += OnNotebook;
            NotebookStore.EntryUpdated += OnNotebook;
            NotebookStore.VersionCut += OnNotebook;
            NotebookStore.PlanUpdated += OnNotebook;
            OSRegistry.Changed += MarkDirty;
            TruckDesignStore.Changed += MarkDirty;
            TrainingLog.Finished += OnTraining;
        }

        void OnDisable()
        {
            NotebookStore.EntryCreated -= OnNotebook;
            NotebookStore.EntryUpdated -= OnNotebook;
            NotebookStore.VersionCut -= OnNotebook;
            NotebookStore.PlanUpdated -= OnNotebook;
            OSRegistry.Changed -= MarkDirty;
            TruckDesignStore.Changed -= MarkDirty;
            TrainingLog.Finished -= OnTraining;
            WatchCad(null);
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        void OnNotebook(JObject _)
        {
            dirty = true;
            linkDirty = true;  // a run attached to the notebook is a new run record of a linked artifact
        }

        void OnTraining(string controller, int version, JObject training) => dirty = true;

        void MarkDirty() => dirty = true;

        // ---------------------------------------------------------------- what views read

        /// <summary>True while a project is open here (the bar is up).</summary>
        public bool Active => projectId != null;
        public string ProjectId => projectId;
        public OSProjectWindow.Loop Loop => loop;
        public StageLoop StageLoop => stageLoop;
        public string Design => design;
        public string MlName => mlName;
        /// <summary>The stages as last judged, in order.</summary>
        public IReadOnlyList<StageState> Stages { get { Judge(); return stages; } }
        /// <summary>The index of Next in <see cref="Stages"/>, or -1 when nothing is left to offer.</summary>
        public int NextIndex { get { Judge(); return next; } }
        /// <summary>The stage the person is at.</summary>
        public string CurrentStage { get { Judge(); return current; } }
        /// <summary>The facts the stages were judged on (read only).</summary>
        public StageFacts Facts { get { Judge(); return facts; } }
        public string StatusLine => statusLine;
        /// <summary>Why the last stage tapped is locked, and the stage that unlocks it (shown on the bar for a few seconds).</summary>
        public string Notice => notice != null && Time.unscaledTime < noticeUntil ? notice : null;
        public string NoticeUnlock => Notice != null ? noticeUnlock : null;

        /// <summary>The stage with this id as last judged; false when the project has none.</summary>
        public bool TryStage(string id, out StageState state)
        {
            Judge();
            int i = ProjectStages.IndexOf(stages, id);
            state = i >= 0 ? stages[i] : default;
            return i >= 0;
        }

        /// <summary>Next's id, or null.</summary>
        public string NextId
        {
            get
            {
                Judge();
                return next >= 0 ? stages[next].Id : null;
            }
        }

        // ---------------------------------------------------------------- open, close

        /// <summary>
        /// Opens <paramref name="id"/> here: it becomes the session's project and lands on the stage it was left at (or the
        /// first one that needs doing), and the bar comes up. Opening the same project again keeps where it is. The notebook
        /// must already have it open (<see cref="OSProjects.EnsureOpen"/>).
        /// </summary>
        public void Begin(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (id != projectId)
            {
                projectId = id;
                kind = OSProjects.KindOf(id);
                design = OSProjects.DesignOf(id);
                mlName = OSProjects.MlProjectOf(id);
                loop = OSProjectWindow.LoopOf(id);
                stageLoop = (StageLoop)(int)loop;
                statusLine = "";
                notice = noticeUnlock = null;
                current = null;
                lastMode = -1;
                begun = false;
                linkDirty = true;
                before.Clear();
                pollKey = int.MinValue;
                dirty = true;
                Judge();
                current = ProjectStages.Resume(stages, Record("current"), next);
                Remember("current", current);
                dirty = true;
                Judge();
                begun = true;
            }
            OSProjectRail.Show(this);
            Raise();
        }

        /// <summary>Closes the project here: the bar and the project window go; the tools stay as they are. The notebook keeps it open.</summary>
        public void End()
        {
            if (projectId == null) return;
            projectId = null;
            stages.Clear();
            before.Clear();
            next = -1;
            OSProjectRail.Hide();
            OSShell shell = OSShell.Current;
            if (shell != null) shell.Windows.Close(OSProjectWindow.WindowId);
            Raise();
        }

        // ---------------------------------------------------------------- moving between stages

        /// <summary>
        /// Goes to a stage: a locked one says why (and which stage unlocks it) and changes nothing; otherwise the previous
        /// stage's tool is put away, the project window is tucked, the stage's action runs and the bar docks under its tool.
        /// Going to the stage the person is at runs it again (reopens its tool). Returns the line it showed.
        /// </summary>
        public string Go(string stageId)
        {
            if (!Active) return "No project is open.";
            Judge();
            int i = ProjectStages.IndexOf(stages, stageId);
            if (i < 0) return Say("This project has no " + (stageId ?? "such") + " stage.");
            StageState s = stages[i];
            if (s.Status == StageStatus.Locked)
            {
                notice = s.Reason;
                noticeUnlock = s.Unlock;
                noticeUntil = Time.unscaledTime + NoticeSeconds;
                Raise();
                return s.Unlock != null ? s.Reason + " Go to " + NameOf(s.Unlock) + " first." : s.Reason;
            }
            notice = noticeUnlock = null;
            string previous = current;
            SetCurrent(stageId);
            PutAway(previous, stageId);
            TuckHome();
            string line = Do(stageId);
            OSProjectRail.Current?.Redock();
            return line;
        }

        /// <summary>Goes to Next (or, with nothing left, to the stage the person is at).</summary>
        public string GoNext()
        {
            Judge();
            if (Notice != null && NoticeUnlock != null) return Go(NoticeUnlock);
            return Go(next >= 0 ? stages[next].Id : current);
        }

        /// <summary>Opens (or brings back) the project window, the project's overview.</summary>
        public void ShowHome()
        {
            if (!Active) return;
            OSProjectWindow.Show(projectId);
        }

        /// <summary>What the Next button says: "Next: Train", "Fly v8" (the laps were on the old design), "Retrain".</summary>
        public string NextLabel()
        {
            Judge();
            if (Notice != null) return NoticeUnlock != null ? "Go to " + NameOf(NoticeUnlock) : null;
            if (next < 0) return null;
            StageState s = stages[next];
            if (s.Status == StageStatus.Stale)
            {
                switch (s.Id)
                {
                    case "drive":
                    case "fly":
                    case "simulate": return s.Def.Name + " v" + facts.DesignVersion;
                    case "train": return "Retrain";
                    case "autopilot": return "Run the Autopilot";
                    default: return "Redo " + s.Def.Name;
                }
            }
            return "Next: " + s.Def.Name;
        }

        /// <summary>The line the bar shows under the project's name: why a tapped stage is locked, else the last result, else what to do now.</summary>
        public string BarLine()
        {
            Judge();
            if (Notice != null) return Notice;
            if (!string.IsNullOrEmpty(statusLine)) return statusLine;
            int at = ProjectStages.IndexOf(stages, current);
            if (at < 0) return "";
            StageState s = stages[at];
            if (s.Status == StageStatus.Done || s.Status == StageStatus.Stale) return s.Def.Name + ": " + s.Result + ".";
            return ProjectStages.DoingLine(stageLoop, current, facts) ?? "";
        }

        string NameOf(string stageId)
        {
            StageDef d = ProjectStages.Find(stageLoop, stageId, facts.PlanCourse);
            return d != null ? d.Name : stageId;
        }

        void SetCurrent(string stageId)
        {
            if (ProjectStages.Find(stageLoop, stageId, facts.PlanCourse) == null || stageId == current) return;
            current = stageId;
            Remember("current", stageId);
            dirty = true;
        }

        // The windows and the Workspace mode each stage opens, so a stage change puts away what the next stage doesn't use.
        static readonly string[] NoWindows = Array.Empty<string>(), TruckDesignWindows = { "truckdesign" }, DriveWindows = { "drive" },
            BrainWindows = { OSToolsCatalog.BrainId, "train_help", "drive" }, AutopilotWindows = { "drive" };

        /// <summary>The windows a stage of this kind opens (the bar docks under the first one that is up).</summary>
        public static string[] WindowsOf(OSProjectWindow.Loop loop, string stageId)
        {
            switch (stageId)
            {
                case "design": return loop == OSProjectWindow.Loop.Truck ? TruckDesignWindows : NoWindows;
                case "drive":
                case "fly": return DriveWindows;
                // The course stages share the Drive window (the pad, Land): it stays up between Drive or Fly, Train and Autopilot.
                case "autopilot": return loop == OSProjectWindow.Loop.Truck || loop == OSProjectWindow.Loop.Quadcopter ? AutopilotWindows : NoWindows;
                case "train": return loop == OSProjectWindow.Loop.Ml ? NoWindows : BrainWindows;
                default: return NoWindows;
            }
        }

        /// <summary>The Workspace mode a stage of this kind opens, or null.</summary>
        public static string ModeOf(OSProjectWindow.Loop loop, string stageId)
        {
            switch (stageId)
            {
                case "design": return loop == OSProjectWindow.Loop.Truck ? null : "cad";
                case "cad":
                case "simulate": return "cad";
                case "code": return "code";
                case "data":
                case "results": return "ml";
                case "train": return loop == OSProjectWindow.Loop.Ml || loop == OSProjectWindow.Loop.Truck || loop == OSProjectWindow.Loop.Generic ? "ml" : null;
                default: return null;
            }
        }

        void PutAway(string from, string to)
        {
            if (string.IsNullOrEmpty(from) || from == to) return;
            OSShell shell = OSShell.Current;
            if (shell == null) return;
            string[] old = WindowsOf(loop, from), now = WindowsOf(loop, to);
            for (int i = 0; i < old.Length; i++)
                if (Array.IndexOf(now, old[i]) < 0 && shell.Windows.IsOpen(old[i])) shell.Windows.Close(old[i]);
            string oldMode = ModeOf(loop, from), newMode = ModeOf(loop, to);
            if (oldMode != null && oldMode != newMode && OSModes.IsOpen(oldMode))
            {
                try { OSModes.Close(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        // The overview steps aside while a stage is worked on; the bar's home button brings it back.
        static void TuckHome()
        {
            OSShell shell = OSShell.Current;
            OSWindow home = shell != null ? shell.Windows.Find(OSProjectWindow.WindowId) : null;
            if (home != null && home.gameObject.activeSelf) home.gameObject.SetActive(false);
        }

        /// <summary>True while the project window is put away by a stage (it is still open, one tap away on the bar).</summary>
        public static bool HomeTucked
        {
            get
            {
                OSShell shell = OSShell.Current;
                OSWindow home = shell != null ? shell.Windows.Find(OSProjectWindow.WindowId) : null;
                return home != null && !home.gameObject.activeSelf;
            }
        }

        /// <summary>
        /// Where the bar docks: under the window the stage at hand opened (or the Workspace mode's panels), as its bottom
        /// and top centre, its up direction and its distance from the eyes. False when the stage has no tool up (the bar then floats).
        /// </summary>
        public bool AnchorFor(Vector3 eye, List<Workspace.ItemSlot> scratch, out Vector3 bottom, out Vector3 top, out Vector3 up, out int anchorId)
        {
            bottom = top = Vector3.zero;
            up = Vector3.up;
            anchorId = 0;
            if (!Active || current == null) return false;
            OSShell shell = OSShell.Current;
            string[] ids = WindowsOf(loop, current);
            for (int i = 0; shell != null && i < ids.Length; i++)
            {
                OSWindow w = shell.Windows.Find(ids[i]);
                if (w == null || w.Panel == null || !w.gameObject.activeInHierarchy) continue;
                var rect = (RectTransform)w.Panel.transform;
                Vector3 center = rect.TransformPoint(rect.rect.center);
                up = rect.up;
                float half = w.Panel.SizeMeters.y * 0.5f;
                bottom = center - up * half;
                top = center + up * half;
                anchorId = w.GetHashCode();
                return true;
            }
            string mode = ModeOf(loop, current);
            Workspace ws = Workspace.Current;
            if (mode == null || ws == null || ws.ActiveMode != mode) return false;
            ws.GetSlots(scratch);
            if (scratch.Count == 0) return false;
            float low = float.MaxValue, high = float.MinValue;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < scratch.Count; i++)
            {
                Workspace.ItemSlot slot = scratch[i];
                sum += slot.center;
                low = Mathf.Min(low, slot.center.y - slot.size.y * 0.5f);
                high = Mathf.Max(high, slot.center.y + slot.size.y * 0.5f);
            }
            Vector3 mid = sum / scratch.Count;
            bottom = new Vector3(mid.x, low, mid.z);
            top = new Vector3(mid.x, high, mid.z);
            anchorId = mode.GetHashCode();
            return true;
        }

        // ---------------------------------------------------------------- polling and judging

        void Update()
        {
            if (!Active) return;
            if (Time.unscaledTime >= nextPoll)
            {
                nextPoll = Time.unscaledTime + PollSeconds;
                Poll();
            }
            if (dirty) Judge();
        }

        // A few numbers that change what the stages say: compared, never allocated.
        void Poll()
        {
            string open = NotebookStore.ProjectId;
            if (open != null && open != projectId)  // something else opened another project: the bar follows it
            {
                Begin(open);
                return;
            }
            if (CadSession.Instance != cad) WatchCad(CadSession.Instance);
            ArtifactHost host = ArtifactHost.Current;
            string openId = host != null ? host.OpenId : null;
            if (host != watchedHost || !ReferenceEquals(openId, watchedOpenId))
            {
                watchedHost = host;
                watchedOpenId = openId;
                linkDirty = dirty = true;
            }

            int laps = LiveLaps(out int mode);
            if (laps >= 0 && laps != facts.Laps) dirty = true;
            if (lastMode == 3 && mode != 3) AutopilotFinished();  // 3: Autopilot in both brains' Mode enums
            lastMode = mode;

            int key = (ForgeCloud.Serves("/ml/") ? 1 : 0) | (OSRegistry.Find(OSTab.Tools, "drive") != null ? 2 : 0)
                      | (ArtifactDrone.Live != null ? 4 : 0) | (DroneLoop() != null ? 8 : 0) | (OSProjects.LiveLoop != null ? 16 : 0)
                      | ((mode + 1) << 5);
            if (key != pollKey)
            {
                pollKey = key;
                dirty = true;
            }
            if (notice != null && Time.unscaledTime >= noticeUntil)
            {
                notice = noticeUnlock = null;
                Raise();
            }
        }

        void Judge()
        {
            if (!dirty || projectId == null) return;
            dirty = false;
            Gather();
            before.Clear();
            for (int i = 0; i < stages.Count; i++) before.Add(stages[i].Status);
            next = ProjectStages.Evaluate(stageLoop, facts, current, stages);
            if (begun && before.Count == stages.Count) Completed();
            Raise();
        }

        // A stage that just became done says so, and offers the next one.
        void Completed()
        {
            for (int i = 0; i < stages.Count; i++)
            {
                if (stages[i].Status != StageStatus.Done || before[i] == StageStatus.Done) continue;
                string line = ProjectStages.DoneLine(stages[i]);
                if (next >= 0 && next != i) line += " Next: " + stages[next].Def.Name + ".";
                SetStatus(line);
                OSSounds.Open();
                return;
            }
        }

        void Raise()
        {
            try { Changed?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // Everything the stages are judged on, from the real state (see the summary). Runs only when something changed.
        void Gather()
        {
            bool open = OSProjects.IsOpen(projectId);
            JObject record = RecordObject();
            StageFacts f = facts;

            // The design.
            if (loop == OSProjectWindow.Loop.Truck)
            {
                bool mine = open && TruckDesignStore.ProjectId == projectId;
                f.DesignVersion = mine ? TruckDesignStore.Version : Int(record, "design_version", 0);
                f.DesignLine = mine && TruckDesignStore.Edited ? "edited, not saved" : null;
            }
            else if (design != null)
            {
                int pinned = open ? ProjectSpine.PinnedCad(design) : 0;
                f.DesignVersion = Mathf.Max(pinned, Int(record, "design_version", 0));
                f.DesignLine = Str(record, "design_line");
            }
            else
            {
                f.DesignVersion = 0;
                f.DesignLine = null;
            }

            // The course, the launch pad.
            if (linkDirty)
            {
                linkDirty = false;
                OSProjects.LinkedArtifacts(projectId, linked);
            }
            string planCourse = loop == OSProjectWindow.Loop.Generic || loop == OSProjectWindow.Loop.Truck || loop == OSProjectWindow.Loop.Quadcopter ? PlanCourse() : null;
            f.PlanCourse = loop == OSProjectWindow.Loop.Generic && planCourse != null;
            string thing = OSProjects.ThingOfProject(projectId);
            f.CourseMade = false;
            f.Launched = false;
            f.LaunchLine = null;
            for (int i = 0; i < linked.Count; i++)
            {
                OSProjects.LinkedArtifact a = linked[i];
                if (a.template == OSProjectWindow.RocketLaunchTemplate || a.id == OSProjectWindow.RocketLaunchTemplate)
                {
                    if (a.runId != null)
                    {
                        f.Launched = true;
                        f.LaunchLine = RunLine(a);
                    }
                    continue;
                }
                bool isCourse = (planCourse != null && (a.template == planCourse || a.id == planCourse))
                                || (thing != null && thing != "rocket" && a.thing == thing);
                if (!isCourse) continue;
                f.CourseMade = true;
                f.CourseName = CourseName(a.template ?? a.id);
            }
            ArtifactHost host = ArtifactHost.Current;
            f.CourseOpen = host != null && host.OpenId != null && OSProjects.EffectiveProject(host.OpenManifest) == projectId
                           && OSProjects.ThingOfArtifact(host.OpenManifest) != "rocket";
            if (f.CourseOpen) f.CourseMade = true;

            // Laps: the live brain's while this project's course is open (kept), else what was kept.
            int live = LiveLaps(out _);
            int kept = Int(record, "laps", 0);
            if (live >= 0 && live != kept)
            {
                if (live > kept) Remember("laps_design", f.DesignVersion);
                Remember("laps", live);
                record = RecordObject();
            }
            f.Laps = live >= 0 ? live : kept;
            f.LapsOnDesign = Int(record, "laps_design", -1);
            f.LapsNeeded = loop == OSProjectWindow.Loop.Quadcopter ? DroneBrain.MinLaps : 2;

            // Training, from the open notebook.
            f.Trainings = 0;
            f.TrainLine = null;
            f.DesignChangedSinceTraining = false;
            if (open)
            {
                var all = NotebookStore.Entries;
                for (int i = 0; i < all.Count; i++)
                    if ((string)all[i]["kind"] == "training") f.Trainings++;
                f.TrainLine = LastTrainingLine();
                f.DesignChangedSinceTraining = DesignCutAfterTraining();
                if (f.Trainings != Int(record, "trainings", -1)) Remember("trainings", f.Trainings);
            }
            else f.Trainings = Mathf.Max(0, Int(record, "trainings", 0));
            f.Trained = f.Trainings > 0;

            f.AutopilotOnTrainings = Int(record, "autopilot_trainings", -1);
            f.AutopilotLine = Str(record, "autopilot_line");
            f.SimulatedDesign = Int(record, "sim_version", -1);
            f.SimLine = Str(record, "sim_line");
            f.DataOpened = Bool(record, "data_opened");
            f.ResultsSeen = Bool(record, "results_seen");
            f.PublishAsked = Bool(record, "publish_asked");
            f.CodeOpened = Bool(record, "code_opened");
            f.CadOpened = Bool(record, "cad_opened");

            // What this build has, right now.
            f.DriveOff = OSProjects.FindTool("drive") == null ? OSProjectWindow.DriveMissing : null;
            f.BrainOff = loop == OSProjectWindow.Loop.Quadcopter && ArtifactDrone.Live != null && DroneLoop() == null ? OSProjectWindow.NoDroneBrain : null;
            f.LaunchOff = loop == OSProjectWindow.Loop.Rocket && !HasLaunch() ? "the launch pad isn't in this build yet." : null;
            string cloudOnly = mlName != null && !ForgeCloud.Serves("/ml/") ? OSProjects.NeedsCloud : null;
            f.DataOff = cloudOnly;
            f.TrainOff = cloudOnly;
            f.PublishOff = !ForgeCloud.Serves("/ml/") ? OSProjectWindow.PublishNeedsCloud : CanPublish() ? null : "only for a model Albie built for you.";
        }

        // True when a design version (CAD's, or the truck's saved one) was cut after the newest training version.
        static bool DesignCutAfterTraining()
        {
            var versions = NotebookStore.Versions;
            bool designAfter = false;
            for (int i = versions.Count - 1; i >= 0; i--)
            {
                string by = (string)versions[i]["cut_by"];
                if (by == "training") return designAfter;
                if (by == "cad" || by == "thing") designAfter = true;
            }
            return false;
        }

        // The laps on the live brain of this project's open course, and the brain's mode (0 idle, 1 lap, 2 training,
        // 3 autopilot), or -1 for both when there is none.
        int LiveLaps(out int mode)
        {
            mode = -1;
            if (loop == OSProjectWindow.Loop.Truck)
            {
                var brain = OSProjects.LiveLoop as SlalomBrain;
                if (brain == null || !CourseIsMine()) return -1;
                mode = (int)brain.State;
                return brain.CompletedLaps;
            }
            if (loop == OSProjectWindow.Loop.Quadcopter)
            {
                var drone = DroneLoop() as DroneBrain;
                if (drone == null || !CourseIsMine()) return -1;
                mode = (int)drone.State;
                return drone.CompletedLaps;
            }
            return -1;
        }

        bool CourseIsMine()
        {
            ArtifactHost host = ArtifactHost.Current;
            return host != null && host.OpenId != null && OSProjects.EffectiveProject(host.OpenManifest) == projectId;
        }

        // The brain left Autopilot: an Autopilot run happened on the brain the project has now.
        void AutopilotFinished()
        {
            string line = null;
            if (loop == OSProjectWindow.Loop.Truck && OSProjects.LiveLoop is SlalomBrain brain) line = brain.Status;
            else if (DroneLoop() is DroneBrain drone) line = drone.Status;
            Remember("autopilot_trainings", facts.Trainings);
            if (!string.IsNullOrWhiteSpace(line)) Remember("autopilot_line", ProjectStages.Clip(StripPrefix(line), 60));
            dirty = true;
            linkDirty = true;
        }

        // "brain truck: finished in 12 s" reads better without the brain's own prefix.
        static string StripPrefix(string line)
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            return colon > 0 && colon < 16 ? line.Substring(colon + 2) : line;
        }

        // ---------------------------------------------------------------- CAD: the design's version, as this project sees it

        void WatchCad(CadSession session)
        {
            if (cad != null) cad.ModelChanged -= OnCadModel;
            cad = session;
            if (cad != null) cad.ModelChanged += OnCadModel;
        }

        void OnCadModel(CadUpdate update)
        {
            CadModelUpdated info = update?.info;
            if (!Active || info == null || design == null || info.part != design || !OSProjects.IsOpen(projectId)) return;
            int had = Int(RecordObject(), "design_version", 0);
            if (info.version == had) return;
            Remember("design_version", info.version);
            Remember("design_line", DesignWords(info));
            if (had > 0 && info.version > had && current == "design") SetStatus("The " + design + " is now v" + info.version + ".");
            dirty = true;
        }

        // "160 mm arms", "360 mm body", or CAD's own label for the version.
        static string DesignWords(CadModelUpdated info)
        {
            var p = info.parameters;
            if (p != null)
            {
                if (p.TryGetValue("arm_length", out object arm) && arm != null) return Number(arm) + " mm arms";
                if (p.TryGetValue("body_length", out object body) && body != null) return Number(body) + " mm body";
            }
            return string.IsNullOrWhiteSpace(info.label) ? null : ProjectStages.Clip(info.label, 32);
        }

        static string Number(object value)
        {
            try { return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture).ToString("0", System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { return value.ToString(); }
        }

        // ---------------------------------------------------------------- the project's record

        JObject RecordObject() => ProjectMeta.Read(projectId)?[RecordField] as JObject;

        string Record(string key) => Str(RecordObject(), key);

        static string Str(JObject o, string key)
        {
            JToken t = o?[key];
            return t != null && t.Type == JTokenType.String ? (string)t : null;
        }

        static int Int(JObject o, string key, int fallback)
        {
            JToken t = o?[key];
            return t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? (int)t : fallback;
        }

        static bool Bool(JObject o, string key)
        {
            JToken t = o?[key];
            return t != null && t.Type == JTokenType.Boolean && (bool)t;
        }

        // Keeps one value in the project's record; nothing is written when it is already that.
        void Remember(string key, JToken value)
        {
            if (projectId == null || key == null) return;
            JObject record = RecordObject();
            JToken had = record?[key];
            if (had != null && value != null && JToken.DeepEquals(had, value)) return;
            if (had == null && (value == null || value.Type == JTokenType.Null)) return;
            try
            {
                ProjectMeta.Update(projectId, meta =>
                {
                    var stagesRecord = meta[RecordField] as JObject ?? new JObject();
                    if (value == null) stagesRecord.Remove(key);
                    else stagesRecord[key] = value;
                    meta[RecordField] = stagesRecord;
                });
            }
            catch (Exception e)
            {
                Debug.Log("[OS] Couldn't keep the project's " + key + ": " + e.Message);
            }
        }

        // ---------------------------------------------------------------- what the voice tool and the window read

        /// <summary>The loop as steps (each stage, and why one is off), then Notebook and Send.</summary>
        public void FillSteps(List<OSProjectWindow.Step> into)
        {
            into.Clear();
            Judge();
            for (int i = 0; i < stages.Count; i++)
            {
                StageState s = stages[i];
                into.Add(new OSProjectWindow.Step { label = s.Def.Name, action = s.Id, unavailable = s.Status == StageStatus.Locked ? s.Reason : null });
            }
            string noNotebook = OSProjects.FindTool("notebook") == null ? "The notebook window couldn't be found." : null;
            into.Add(new OSProjectWindow.Step { label = "Notebook", action = "notebook", unavailable = noNotebook });
            into.Add(new OSProjectWindow.Step { label = "Send", action = "send", unavailable = null });
        }

        /// <summary>The next step as a sentence: "Next step: tap Course to set up a drone course."</summary>
        public string NextStepLine()
        {
            Judge();
            if (next < 0)
            {
                int done = ProjectStages.DoneCount(stages, out int total);
                return done >= total ? "Every stage is done. Tap any stage to go back to it." : "Tap a stage to continue.";
            }
            StageState s = stages[next];
            if (s.Status == StageStatus.Stale)
                return "Next step: " + NextLabel() + ". " + s.Def.Name + " is out of date: " + s.Result + ".";
            switch (s.Id)
            {
                case "course":
                    return loop == OSProjectWindow.Loop.Quadcopter ? "Next step: tap Course to set up a drone course."
                        : loop == OSProjectWindow.Loop.Truck ? "Next step: tap Course to make the course for the truck." : "Next step: tap Course to set up the test.";
                case "drive": return "Next step: tap Drive and drive two laps by hand.";
                case "train": return loop == OSProjectWindow.Loop.Ml ? "Next step: tap Train to train the model." : "Next step: tap Train so the brain learns from your laps.";
                case "autopilot": return "Next step: tap Autopilot and watch it drive itself.";
                case "design": return "Next step: tap Design to open the " + design + " in CAD.";
                case "fly": return "Next step: tap Fly and take off from the Drive window.";
                case "simulate": return "Next step: tap Simulate to see how high it flies.";
                case "launch": return "Next step: tap Launch to fly it in the room.";
                case "data": return "Next step: tap Data to look at the examples.";
                case "results": return "Next step: tap Results to see what it got right.";
                case "cad": return "Next step: tap CAD to open the design.";
                default: return "Tap a stage to continue.";
            }
        }

        /// <summary>Puts a line in the status area without running anything; it stays until another action sets the line.</summary>
        public void Suggest(string line) => SetStatus(line);

        void SetStatus(string text)
        {
            text = text ?? "";
            if (text == statusLine) return;
            statusLine = text;
            Raise();
        }

        string Say(string text)
        {
            SetStatus(text);
            return statusLine;
        }

        // ---------------------------------------------------------------- the actions

        /// <summary>
        /// Runs one action by id (course, drive, fly, train, autopilot, code, send, design, simulate, launch, data, dataset,
        /// results, publish, cad, notebook, make_own, start_own), shows its result in the status line and returns that line.
        /// A stage's action also makes it the stage the person is at; nothing is put away (that is <see cref="Go"/>).
        /// </summary>
        public string Do(string action)
        {
            if (!Active) return "No project is open.";
            if (ProjectStages.Find(stageLoop, action, facts.PlanCourse) != null) SetCurrent(action);
            notice = noticeUnlock = null;
            string text;
            try
            {
                text = Run(action);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                text = OSProjects.Plain(e.Message);
            }
            if (text != null) SetStatus(text);
            dirty = true;
            linkDirty = true;
            return statusLine;
        }

        // Returns the sentence to show, or null when the action already set the status itself (it answers later).
        string Run(string action)
        {
            switch (action)
            {
                case "course": return DoCourse();
                case "drive": return DoDrive();
                case "fly": return DoFly();
                case "simulate": return DoSimulate();
                case "launch": return DoLaunch();
                case "data": return DoData();
                case "results": return DoResults();
                case "publish": return DoPublish();
                case "train": return DoTrain();
                case "autopilot": return DoAutopilot();
                case "code":
                    Remember("code_opened", true);
                    return OpenMode("code", "Code");
                case "cad":
                    Remember("cad_opened", true);
                    return OpenModeOrTool("cad", "cad", "CAD");
                case "send":
                    MessagesShare.DraftForProject(projectId, out string sendStatus);
                    return string.IsNullOrEmpty(sendStatus) ? "Nothing to share yet." : sendStatus;
                case "notebook": return OpenToolLine(OSProjects.FindTool("notebook"), "Notebook");
                case "design" when loop == OSProjectWindow.Loop.Truck:
                    // The truck's design is on the device (TruckDesign): sliders change it on the course at once.
                    TruckDesignPanel.Open();
                    return "Truck design is open. Move a slider and the truck on the course changes; Save as version keeps it.";
                case "design":
                    if (string.IsNullOrEmpty(design)) return "This project has no design to open.";
                    SetStatus("Opening CAD...");
                    OSProjects.OpenDesign(design, line =>
                    {
                        SetStatus(line);
                        ShowParameters();
                        if (CadSession.Instance != null && CadSession.Instance.Latest?.info != null) OnCadModel(CadSession.Instance.Latest);
                    });
                    return null;
                case "dataset": return ForgeCloud.Serves("/ml/") ? DoData() : OSProjects.NeedsCloud;
                case "make_own":
                case "copy": return DoMakeOwn();  // "copy" is the old name: a sample is never copied, Make my own plans a new one
                case "start_own":
                    OSProjects.StartOwn();
                    return "New project is open. Answer a few questions and Albie plans your project.";
                default: return "I don't know that step.";
            }
        }

        // Make my own: Build something with the sample's kind picked; Start there makes the person's own project and the
        // session follows it. The sample is left as it is.
        string DoMakeOwn()
        {
            if (!OSProjects.IsSample(projectId)) return "This is already your own project.";
            if (!OSProjects.MakeOwn(projectId)) return "New project couldn't open right now.";
            return "New project is open with " + OSProjects.TitleFor(projectId) + " picked. Answer the questions, then tap Start to make " + OSProjects.OwnTitle(projectId) + ".";
        }

        // The course is opened when a stage needs it on (Drive, Fly, Train, Autopilot): the one this project made, or a new one.
        string DoCourse()
        {
            // The project must be open first: the course links to this project only while its notebook is the open one.
            if (!OSProjects.EnsureOpen(projectId, out string error)) return error;

            // The course the plan's test is run on (Build something saves it); the truck's is the slalom when the plan names none.
            string course = PlanCourse() ?? (loop == OSProjectWindow.Loop.Quadcopter ? "hoop_course" : "slalom");
            courseName = CourseName(course);
            ArtifactHost current = ArtifactHost.Current;
            if (current != null && current.OpenId == course && OSProjects.LiveLoop is SlalomBrain && LinksTruckForThisProject(current))
                return "The " + courseName + " is already open, with the brain truck at the start.";

            OSProjects.LinkedArtifacts(projectId, linked);
            OSProjects.LinkedArtifact made = null;
            for (int i = 0; i < linked.Count; i++)
                if (linked[i].template == course || linked[i].id == course) { made = linked[i]; break; }

            // What this step makes belongs to this project (ArtifactLibrary.ForProject), whatever else is open.
            JObject result = made != null
                ? AlbieTools.Call("artifact_open", new JObject { ["id"] = made.id })
                : ArtifactLibrary.ForProject(projectId, () => AlbieTools.Call("artifact_create", new JObject { ["template"] = course }));
            linkDirty = true;
            return DescribeCourse(result);
        }

        // The saved plan's course for this project ("dash", "hover"...), or null: no plan, another project open, or a test with no course.
        string PlanCourse()
        {
            if (!NotebookStore.IsOpen || NotebookStore.ProjectId != projectId) return null;
            JObject plan = NotebookStore.CurrentPlan;
            string course = plan != null && plan["course"] != null && plan["course"].Type == JTokenType.String ? (string)plan["course"] : null;
            if (string.IsNullOrEmpty(course)) return null;
            return ArtifactCourses.IsTemplate(course) || Array.IndexOf(ArtifactLibrary.Templates, course) >= 0 ? course : null;
        }

        /// <summary>"slalom", "hoop course", "dash course": what the status line calls a course.</summary>
        public static string CourseName(string course)
        {
            if (string.IsNullOrEmpty(course) || course == "slalom") return "slalom";
            if (course == "hoop_course") return "hoop course";
            return ArtifactCourses.IsTemplate(course) ? ArtifactCourses.Title(course).ToLowerInvariant() + " course" : course.Replace('_', ' ');
        }

        bool LinksTruckForThisProject(ArtifactHost host)
        {
            JObject manifest = host.OpenManifest;
            return manifest != null && (string)manifest["links"]?["thing"] == "truck" && (string)manifest["links"]?["project"] == projectId;
        }

        string DescribeCourse(JObject result)
        {
            string error = (string)result["error"];
            if (error != null) return OSProjects.Plain(error);

            JObject opened = result["opened"] as JObject ?? result;  // artifact_create nests the open result
            string status = (string)opened["status"];
            if (status == "awaiting_approval")
            {
                var asks = new List<string>();
                if (opened["asks"] is JArray lines)
                    foreach (JToken line in lines) asks.Add(OSProjects.Plain((string)line));
                string what = asks.Count > 0 ? " " + string.Join(" ", asks) : "";
                return "The " + courseName + " asks first." + what + " Tap Allow on the card in front of you.";
            }
            if (status != "open") return "The " + courseName + " didn't open.";

            ArtifactHost host = ArtifactHost.Current;
            string thing = host != null && host.OpenManifest != null ? (string)host.OpenManifest["links"]?["thing"] : null;
            string text;
            if (thing == "drone") text = "The " + courseName + " is open. The drone waits at the start: tap Take off in the Drive window.";
            else if (thing != "truck") text = "The " + courseName + " is open, but it isn't linked to the truck, so no brain truck came with it.";
            else if (OSProjects.LiveLoop is SlalomBrain) text = "The " + courseName + " is open. The brain truck is at the start.";
            else text = "The " + courseName + " is open, but the brain truck didn't appear.";
            string say = (string)opened["say"];
            return string.IsNullOrWhiteSpace(say) ? text : text + " " + say.Trim();
        }

        // A stage that runs on the course (Drive, Fly, Train, Autopilot) opens this project's course first when it isn't up,
        // instead of saying "open the course first". Null when it is up (or came up now); else the line to show.
        string EnsureCourse()
        {
            if (CourseIsMine() && (loop != OSProjectWindow.Loop.Truck || OSProjects.LiveLoop is SlalomBrain)
                && (loop != OSProjectWindow.Loop.Quadcopter || ArtifactDrone.Live != null)) return null;
            string line = DoCourse();
            bool up = CourseIsMine() && (loop != OSProjectWindow.Loop.Truck || OSProjects.LiveLoop is SlalomBrain)
                      && (loop != OSProjectWindow.Loop.Quadcopter || ArtifactDrone.Live != null);
            return up ? null : line;
        }

        string DoDrive()
        {
            OSEntry drive = OSProjects.FindTool("drive");
            if (drive == null) return OSProjectWindow.DriveMissing;
            string courseLine = kind == OSProjectKind.Truck ? EnsureCourse() : null;
            if (!OSProjects.OpenTool(drive, out string error)) return error;

            var brain = OSProjects.LiveLoop as SlalomBrain;
            if (brain == null) return courseLine ?? "The Drive pad is open. Open the course first so the truck can run.";
            if (brain.State != SlalomBrain.Mode.Idle) return "The Drive pad is open. The truck is " + StateWord(brain.State) + ".";

            // Laps are only scored while the brain records them, so the Drive beat starts a two-lap recording.
            JObject result = AlbieTools.Call("brain_record", new JObject { ["start"] = true, ["laps"] = 2, ["then_autopilot"] = false });
            string recordError = (string)result["error"];
            if (recordError != null) return "The Drive pad is open. " + OSProjects.Plain(recordError);
            return "Recording 2 laps. Drive the truck around the cones with the pad, then tap Train.";
        }

        string DoTrain()
        {
            if (loop == OSProjectWindow.Loop.Ml) return DoMlTrain();
            if (loop == OSProjectWindow.Loop.Quadcopter)
            {
                string courseLine = EnsureCourse();
                if (courseLine != null) return courseLine;
                string problem = DroneBrainProblem();
                if (problem != null) return "Train: " + problem;
                OpenBrainWindow();
                return BrainTrain("Training the drone's brain on ");
            }
            if (kind == OSProjectKind.Truck)
            {
                string courseLine = EnsureCourse();
                string toolError = OpenTrainTool();
                if (OSProjects.LiveLoop == null) return courseLine ?? toolError ?? "Open the course first. The brain learns from laps on the slalom.";
                return BrainTrain("Training the brain on ");
            }
            string openError = OpenTrainTool();
            return openError ?? "Train is open.";
        }

        static void OpenBrainWindow()
        {
            try { OSToolsCatalog.OpenBrain(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // "Training the brain on 2 laps. It takes a few seconds." (or without the laps when the tool doesn't say).
        static string BrainTrain(string lead)
        {
            JObject result = AlbieTools.Call("brain_train", new JObject());
            string error = (string)result["error"];
            if (error != null) return OSProjects.Plain(error);
            int laps = (int?)result["completed_laps"] ?? 0;
            return laps > 0 ? lead + laps + " laps. It takes a few seconds." : lead.Substring(0, lead.Length - 4) + ". It takes a few seconds.";
        }

        string DoAutopilot()
        {
            if (loop == OSProjectWindow.Loop.Quadcopter)
            {
                string courseLine = EnsureCourse();
                if (courseLine != null) return courseLine;
                string problem = DroneBrainProblem();
                if (problem != null) return "Autopilot: " + problem;
                OSEntry drive = OSProjects.FindTool("drive");
                if (drive != null) OSProjects.OpenTool(drive, out _);  // Land is on the Drive window
                JObject flown = AlbieTools.Call("autopilot_on", new JObject());
                string flyError = (string)flown["error"];
                return flyError != null ? OSProjects.Plain(flyError) : "The Autopilot is flying the drone course.";
            }
            string truckCourse = EnsureCourse();
            var brain = OSProjects.LiveLoop as SlalomBrain;
            if (brain == null) return truckCourse ?? "Open the course first. The Autopilot drives the slalom.";
            bool stop = brain.State == SlalomBrain.Mode.Autopilot;
            // Something solid on the start spot would crash every brain at once, so say so instead of burning through them.
            if (!stop && brain.State == SlalomBrain.Mode.Idle && OSProjects.StartBlocked(brain, out string blocker))
            {
                Debug.Log("[OS] The Autopilot did not start: " + blocker + " is on the truck's start spot.");
                return "Something is in the truck's way at the start. Move it, then tap Autopilot again.";
            }
            JObject result = AlbieTools.Call(stop ? "autopilot_off" : "autopilot_on", new JObject());
            string error = (string)result["error"];
            if (error != null) return OSProjects.Plain(error);
            return stop ? "The Autopilot stopped. The truck is yours again." : "The Autopilot is driving the slalom.";
        }

        // ---- the quadcopter and the rocket

        string DoFly()
        {
            OSEntry drive = OSProjects.FindTool("drive");
            if (drive == null) return OSProjectWindow.DriveMissing;
            string courseLine = EnsureCourse();
            if (!OSProjects.OpenTool(drive, out string error)) return error;
            if (ArtifactDrone.Live == null) return courseLine ?? "The Drive window is open. Tap Course first: the drone stands at the start of a drone course.";
            // A hand flight through the course is recorded as a lap by the drone's brain by itself (DroneBrain's passive laps).
            return "The Drive window flies the drone. Tap Take off, fly " + DroneBrain.MinLaps + " laps through the hoops with the two pads, then Land.";
        }

        // The rocket module's own simulate tool when it is there; otherwise CAD's flight of the design on screen (the hub's
        // sim, or the offline sample's stored flight).
        string DoSimulate()
        {
            if (AlbieTools.Has(OSProjectWindow.RocketSimulateTool))
            {
                SetStatus("Simulating the flight...");
                int version = facts.DesignVersion;
                AlbieTools.CallAsync(OSProjectWindow.RocketSimulateTool, new JObject(), result =>
                {
                    string error = result != null ? (string)result["error"] : "no answer";
                    string say = result != null ? (string)result["say"] : null;
                    if (error == null) Simulated(version, null);
                    SetStatus(error != null ? OSProjects.Plain(error) : !string.IsNullOrWhiteSpace(say) ? say.Trim() : "The flight is simulated.");
                });
                return null;
            }
            CadSession session = CadSession.Instance;
            CadUpdate shown = session != null && session.Ready ? session.Latest : null;
            if (shown?.info == null || shown.info.part != "rocket")
            {
                // The flight is of the design on screen: open it first, then simulate once CAD shows it.
                if (design == "rocket") return OpenDesignThenSimulate();
                return "Open the rocket first: tap Design, then Simulate.";
            }
            int shownVersion = shown.info.version;
            SetStatus("Simulating the flight of v" + shownVersion + "...");
            session.GetFlight(shownVersion, null, flight =>
            {
                if (flight == null)
                {
                    SetStatus(session.IsMock ? "The offline sample has no flight for v" + shownVersion + "." : "The flight sim didn't answer.");
                    return;
                }
                string line = "v" + shownVersion + " flies to " + flight.apogeeM.ToString("0") + " m and lands " + flight.landingTimeS.ToString("0")
                              + " s after launch, " + flight.driftM.ToString("0") + " m from the pad.";
                if (!string.IsNullOrWhiteSpace(flight.warning)) line += " " + OSProjects.Plain(flight.warning);
                Simulated(shownVersion, "apogee " + flight.apogeeM.ToString("0") + " m · lands " + flight.driftM.ToString("0") + " m out");
                SetStatus(line);
            });
            return null;
        }

        string OpenDesignThenSimulate()
        {
            SetStatus("Opening the rocket to simulate it...");
            OSProjects.OpenDesign(design, line =>
            {
                CadSession session = CadSession.Instance;
                if (session != null && session.Ready && session.Latest?.info != null && session.Latest.info.part == "rocket")
                {
                    OnCadModel(session.Latest);
                    Do("simulate");
                }
                else SetStatus(line + " Tap Simulate once the rocket is on screen.");
            });
            return null;
        }

        void Simulated(int version, string line)
        {
            if (version > 0) Remember("sim_version", version);
            if (line != null) Remember("sim_line", line);
            dirty = true;
        }

        string DoLaunch()
        {
            if (!HasLaunch()) return "Launch: the launch pad isn't in this build yet.";
            if (!OSProjects.EnsureOpen(projectId, out string error)) return error;
            OSProjects.LinkedArtifacts(projectId, linked);
            OSProjects.LinkedArtifact made = null;
            for (int i = 0; i < linked.Count; i++)
                if (linked[i].template == OSProjectWindow.RocketLaunchTemplate || linked[i].id == OSProjectWindow.RocketLaunchTemplate) { made = linked[i]; break; }
            JObject result = made != null
                ? AlbieTools.Call("artifact_open", new JObject { ["id"] = made.id })
                : ArtifactLibrary.ForProject(projectId, () => AlbieTools.Call("artifact_create", new JObject { ["template"] = OSProjectWindow.RocketLaunchTemplate }));
            linkDirty = true;
            string problem = (string)result["error"];
            if (problem != null) return OSProjects.Plain(problem);
            JObject opened = result["opened"] as JObject ?? result;  // artifact_create nests the open result
            string status = (string)opened["status"];
            if (status == "awaiting_approval") return "The launch pad asks first. Tap Allow on the card in front of you.";
            if (status != "open") return "The launch pad didn't open.";
            string say = (string)opened["say"];
            return "The launch pad is open. Tap Launch on it." + (string.IsNullOrWhiteSpace(say) ? "" : " " + say.Trim());
        }

        // ---- ML projects (the classifier, cloud projects)

        // The hub or the cloud opens this project on the ML desk; offline the desk opens on the bundled images.
        string DoData()
        {
            if (ForgeCloud.Serves("/ml/"))
            {
                SetStatus("Asking Albie's cloud to open the data...");
                var args = new JObject();
                if (mlName != null) args["project"] = mlName;
                AlbieTools.CallAsync("ml_open", args, result =>
                {
                    string error = result != null ? (string)result["error"] : "no answer";
                    if (error == null) Remember("data_opened", true);
                    SetStatus(error != null ? OSProjects.Plain(error) : "The ML desk is open on " + OSProjects.TitleFor(projectId) + "'s data.");
                    dirty = true;
                });
                return null;
            }
            if (mlName != null) return "Data: " + OSProjects.NeedsCloud;
            if (!OSModes.Open("ml")) return "Train couldn't open right now.";
            Remember("data_opened", true);
            return "Train is open on the bundled images on this device. Tap Train on the Training panel to learn from them.";
        }

        string DoMlTrain()
        {
            if (ForgeCloud.Serves("/ml/"))
            {
                SetStatus("Starting a training run...");
                AlbieTools.CallAsync("ml_train", new JObject(), result =>
                {
                    string error = result != null ? (string)result["error"] : "no answer";
                    SetStatus(error != null ? OSProjects.Plain(error) : "Training. The curves stream on the ML desk, and the score is spoken when it's done.");
                });
                return null;
            }
            if (mlName != null) return "Train: " + OSProjects.NeedsCloud;
            if (!OSModes.Open("ml")) return "Train couldn't open right now.";
            return "Train is open. Tap Train on the Training panel to learn from bundled images on this device.";
        }

        string DoResults()
        {
            string last = LastTrainingLine();
            if (last == null) return "Results: no training run yet. Tap Train first.";
            bool opened = OSModes.Open("ml");
            Remember("results_seen", true);
            return (opened ? "The results are on the ML desk. " : "") + "Last run: " + last;
        }

        // Asks first, always: ml_publish without confirm only reads back what would be pushed. The person says yes to Albie.
        string DoPublish()
        {
            if (!ForgeCloud.Serves("/ml/")) return "Publish " + OSProjectWindow.PublishNeedsCloud;
            if (!CanPublish()) return "Publish is only for a model Albie built for you.";
            SetStatus("Asking what would be published...");
            AlbieTools.CallAsync("ml_publish", new JObject(), result =>
            {
                string error = result != null ? (string)result["error"] : "no answer";
                if (error == null) Remember("publish_asked", true);
                SetStatus(error != null ? OSProjects.Plain(error)
                    : "Nothing is pushed yet. Albie read back what would go on GitHub: say yes to him to open the pull request.");
                dirty = true;
            });
            return null;
        }

        string OpenTrainTool()
        {
            OSEntry tool = OSProjects.FindTool("train", "ml");
            if (tool != null) return OSProjects.OpenTool(tool, out string error) ? null : error;
            return OpenMode("ml", "Train").EndsWith(" is open.", StringComparison.Ordinal) ? null : "Train couldn't open right now.";
        }

        static string OpenMode(string modeId, string name)
        {
            bool opened;
            try { opened = OSModes.Open(modeId); }
            catch (Exception e)
            {
                Debug.LogException(e);
                opened = false;
            }
            return opened ? name + " is open." : name + " couldn't open right now.";
        }

        static string OpenModeOrTool(string toolId, string modeId, string name)
        {
            OSEntry tool = OSProjects.FindTool(toolId);
            return tool != null ? OpenToolLine(tool, name) : OpenMode(modeId, name);
        }

        static string OpenToolLine(OSEntry tool, string name) =>
            OSProjects.OpenTool(tool, out string error) ? name + " is open." : error;

        // The design's Parameters tab, so the design can be changed at once (CAD's own cad.action "tab", which waits until the
        // design is on screen).
        void ShowParameters()
        {
            if (string.IsNullOrEmpty(design)) return;
            try
            {
                ForgeEvents.Raise(ForgeEvents.CadActionType, new JObject { ["action"] = "tab", ["tab"] = "parameters", ["part"] = design });
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        // ---------------------------------------------------------------- small reads

        /// <summary>The newest training entry of the open project: what it did, and its numbers when it has them.</summary>
        public static string LastTrainingLine()
        {
            var all = NotebookStore.Entries;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                if ((string)all[i]["kind"] != "training") continue;
                string change = (string)all[i]["change"] ?? "a run";
                string note = (string)all[i]["observed"]?["note"];
                return string.IsNullOrEmpty(note) ? change : change.TrimEnd('.') + " · " + note;
            }
            return null;
        }

        /// <summary>"last run passed · 12.4 s · crashed": a linked artifact's newest run.</summary>
        public static string RunLine(OSProjects.LinkedArtifact a)
        {
            if (a.runId == null) return "no runs yet";
            string line = "last run " + OSProjects.RunWord(a.runOutcome);
            if (a.runSeconds.HasValue) line += " · " + a.runSeconds.Value.ToString("0.0") + " s";
            if (!string.IsNullOrEmpty(a.runReason) && a.runOutcome != "completed") line += " · " + a.runReason.Replace('_', ' ');
            return line;
        }

        // A learning brain on the drone course: the live brain loop when it isn't the truck's.
        static IBrainLoop DroneLoop()
        {
            if (ArtifactDrone.Live == null) return null;
            IBrainLoop live = OSProjects.LiveLoop;
            return live != null && !(live is SlalomBrain) ? live : null;
        }

        static string DroneBrainProblem()
        {
            if (ArtifactDrone.Live == null) return "open a drone course first.";
            return DroneLoop() == null ? OSProjectWindow.NoDroneBrain : null;
        }

        /// <summary>True when this build has the rocket launch pad.</summary>
        public static bool HasLaunch()
        {
            if (Array.IndexOf(ArtifactLibrary.Templates, OSProjectWindow.RocketLaunchTemplate) >= 0 || ArtifactCourses.IsTemplate(OSProjectWindow.RocketLaunchTemplate)) return true;
            try { return ArtifactLibrary.TryTemplate(OSProjectWindow.RocketLaunchTemplate, out _, out _); }
            catch (Exception) { return false; }
        }

        // Publish is for a model Albie built (ml_new): a cloud project made that way, or a project one was made from.
        bool CanPublish()
        {
            JObject meta = ProjectMeta.Read(projectId);
            if (meta == null) return false;
            if ((string)meta["ml_kind"] == "new") return true;
            return meta["ml_links"] is JArray links && links.Count > 0;
        }

        /// <summary>The truck brain's mode as a word ("recording a lap", "on Autopilot").</summary>
        public static string StateWord(SlalomBrain.Mode mode)
        {
            switch (mode)
            {
                case SlalomBrain.Mode.Lap: return "recording a lap";
                case SlalomBrain.Mode.Training: return "training";
                case SlalomBrain.Mode.Autopilot: return "on Autopilot";
                default: return "ready";
            }
        }
    }
}
