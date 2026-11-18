using System;
using System.Collections.Generic;
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
    /// The Project window (window id "project"), built in code with progressive disclosure (see <see cref="Rebuild"/>). The
    /// overview shows only what is needed now: one sentence (the sample's line, or the plan's goal), ONE big button (the next
    /// step, or "Fly the new version" right after the design changed), the last result, and the loop as a row of step chips
    /// that can all be tapped at any time (a done step opens its tool again: Design opens the CAD design on its Parameters tab):
    /// - RC truck: Course, Drive, Train, Autopilot, Code;
    /// - Quadcopter: Design (CAD), Course (a drone course), Fly (the Drive window as the drone's transmitter), Train, Autopilot;
    /// - Rocket: Design (CAD), Simulate (the design's flight), Launch (the <c>rocket_launch</c> artifact);
    /// - Image classifier and every ML project: Data, Train, Results, Publish;
    /// - anything else: Course (when its plan names one), CAD, Train.
    /// Along the bottom: Make my own on a sample, and More, which opens a small row with Details, Notebook and Send. Details is
    /// a second page: the test, plan, versions, design, last training, last run, artifacts, what it follows, the live truck line
    /// and why a step is off. A step whose feature isn't there (no drone brain yet, no cloud) is switched off with a short
    /// reason, never a button that does nothing. Every step shows the tool's real result, or its error as a plain sentence, in
    /// the status line. What a step makes (the course, the launch pad) is linked to this project
    /// (<see cref="ArtifactLibrary.ForProject{T}"/>). A sample's steps run as sample runs; nothing else is attached to it.
    /// Versions are cut by <see cref="ProjectSpine"/>. The window follows the open notebook project.
    /// </summary>
    public sealed class OSProjectWindow : MonoBehaviour
    {
        public const string WindowId = "project";

        static readonly Vector2 SizeMeters = new Vector2(0.70f, 0.62f);  // seen at 45-60 cm: smaller than before, the overview is short
        const float PollSeconds = 0.5f;
        const int MaxMilestones = 4, MaxVersions = 3, MaxArtifacts = 2;

        /// <summary>The rocket launch artifact's template (the rocket agent's); Launch is off until the build has it.</summary>
        public const string RocketLaunchTemplate = "rocket_launch";
        /// <summary>A simulate tool the rocket module may register; without it Simulate asks CAD for the design's flight.</summary>
        public const string RocketSimulateTool = "rocket_simulate";
        /// <summary>The Publish step's reason while neither the hub nor Albie's cloud serves ML.</summary>
        public const string PublishNeedsCloud = "needs Albie's cloud to open a pull request.";
        /// <summary>The Train and Autopilot reason on a drone course that has no learning brain in this build.</summary>
        public const string NoDroneBrain = "the drone has no brain to train in this build yet.";

        /// <summary>The loop a project's window shows.</summary>
        public enum Loop { Truck, Quadcopter, Rocket, Ml, Generic }
        const float LineSize = OSUi.BodyText, NoteSize = OSUi.SmallText, LeadSize = 24f;  // OS units: plan and result lines, hints, the lines people read first

        ForgePanel panel;
        string projectId;
        OSProjectKind kind;
        Loop loop;
        string design, mlName;
        bool dirty, simulated, dataOpened;
        // What the window shows: the overview, or the Details page; and whether More's small row is open.
        bool details, more;
        // After Design: the design's version when CAD showed it (-2 while waiting for CAD to show it, -1 not watching), the step
        // that was next before, and whether the design has changed since (the big button then offers the obvious next move).
        int designAtOpen = -1;
        string returnTo;
        bool designChanged;
        int availabilityKey = int.MinValue;
        string statusLine = "";
        TMP_Text statusText, brainText;
        float nextPoll;
        int brainKey = int.MinValue;
        readonly List<OSProjects.LinkedArtifact> linked = new List<OSProjects.LinkedArtifact>();
        readonly List<string> hints = new List<string>();
        readonly List<Step> steps = new List<Step>();
        readonly List<Step> extras = new List<Step>(2);

        /// <summary>One button of the loop as last built: its label, its action id, and why it is off (null when it is on).</summary>
        public struct Step
        {
            public string label, action, unavailable;
        }

        /// <summary>The loop's steps as the window last built them (the voice tool reads them).</summary>
        public IReadOnlyList<Step> Steps => steps;

        /// <summary>The project this window shows.</summary>
        public string ProjectId => projectId;

        /// <summary>The last line the status area showed (the tool's result, or its error as a sentence).</summary>
        public string StatusLine => statusLine;

        /// <summary>
        /// Puts a line in the status area without running anything (Build something uses it to offer the first step, for
        /// example "Next step: tap Course to make the course for the truck."). It stays until another action sets the line.
        /// </summary>
        public void Suggest(string line) => SetStatus(line);

        /// <summary>
        /// Opens the Project window on <paramref name="projectId"/>, or brings the open one forward and points it at this
        /// project. Null when the scene has no shell (a sandbox). The caller makes the project the open notebook first
        /// (<see cref="OSProjects.OpenProject"/> does).
        /// </summary>
        public static OSWindow Open(string projectId)
        {
            OSShell shell = OSShell.Current;
            if (shell == null || string.IsNullOrEmpty(projectId)) return null;
            OSWindow window = shell.Windows.Open(WindowId, OSProjects.TitleFor(projectId), SizeMeters, panel =>
            {
                var view = panel.gameObject.AddComponent<OSProjectWindow>();
                view.Init(panel, projectId);
            }, "PROJECT");
            if (window == null) return null;
            var existing = window.GetComponent<OSProjectWindow>();
            if (existing != null && existing.projectId != projectId) existing.Bind(projectId);
            return window;
        }

        void Init(ForgePanel owner, string id)
        {
            panel = owner;
            Bind(id);
        }

        /// <summary>Points the window at a project and builds it.</summary>
        public void Bind(string id)
        {
            if (panel == null || string.IsNullOrEmpty(id)) return;
            if (id != projectId)
            {
                statusLine = "";
                simulated = dataOpened = false;
                details = more = designChanged = false;
                designAtOpen = -1;
                returnTo = null;
            }
            projectId = id;
            kind = OSProjects.KindOf(id);
            design = OSProjects.DesignOf(id);
            mlName = OSProjects.MlProjectOf(id);
            loop = LoopOf(id);
            panel.Title = OSProjects.TitleFor(id);
            panel.SetBadge(OSProjects.IsSample(id) ? "SAMPLE" : "MY PROJECT", ForgeTheme.Tool);  // a sample says so in its header
            Rebuild();
        }

        /// <summary>The loop the window shows for a project.</summary>
        public static Loop LoopOf(string id)
        {
            switch (OSProjects.KindOf(id))
            {
                case OSProjectKind.Truck: return Loop.Truck;
                case OSProjectKind.Ml: return Loop.Ml;
                case OSProjectKind.Cad: return OSProjects.DesignOf(id) == "rocket" ? Loop.Rocket : Loop.Quadcopter;
                default: return Loop.Generic;
            }
        }

        /// <summary>What opening a project does by itself: a CAD starter opens its design, an ML project opens its data.</summary>
        public void RunOpenAction()
        {
            if (kind == OSProjectKind.Cad) Do("design");
            else if (kind == OSProjectKind.Ml) Do("data");
        }

        void OnEnable()
        {
            NotebookStore.EntryCreated += OnNotebook;
            NotebookStore.EntryUpdated += OnNotebook;
            NotebookStore.VersionCut += OnNotebook;
            NotebookStore.PlanUpdated += OnNotebook;
            OSRegistry.Changed += MarkDirty;
            CadSession.ParamsRequested += OnDesignParams;
        }

        void OnDisable()
        {
            NotebookStore.EntryCreated -= OnNotebook;
            NotebookStore.EntryUpdated -= OnNotebook;
            NotebookStore.VersionCut -= OnNotebook;
            NotebookStore.PlanUpdated -= OnNotebook;
            OSRegistry.Changed -= MarkDirty;
            CadSession.ParamsRequested -= OnDesignParams;
        }

        void OnNotebook(JObject _) => dirty = true;
        void MarkDirty() => dirty = true;

        void Update()
        {
            if (panel == null) return;
            if (dirty) Rebuild();
            float now = Time.unscaledTime;
            if (now < nextPoll) return;
            nextPoll = now + PollSeconds;

            string open = NotebookStore.ProjectId;
            if (open != null && open != projectId)  // another part opened a different project: the window follows it
            {
                Bind(open);
                return;
            }
            PollBrain();
            PollDesign();
            int key = AvailabilityKey();
            if (key != availabilityKey) dirty = true;  // a step became available or went away: rebuild the row next frame
        }

        // What switches steps on and off while the window is up: a drone or a brain appearing, the cloud coming and going.
        int AvailabilityKey()
        {
            int key = (ForgeCloud.Serves("/ml/") ? 1 : 0) | (OSProjects.FindTool("drive") != null ? 2 : 0);
            if (loop == Loop.Quadcopter) key |= (ArtifactDrone.Live != null ? 4 : 0) | (DroneLoop() != null ? 8 : 0);
            return key;
        }

        // ---------------------------------------------------------------- building the window

        // The overview, top to bottom, in OS units (the body is about 1020 x 780 at this window's size):
        // 1 one sentence: what the project is (the sample's line, or the plan's goal) - the title, the SAMPLE / MY PROJECT
        //   badge and the status ("v2 · slalom passed") are already in the window's header;
        // 2 the one big button (Next: Course; Fly the new version right after a design change);
        // 3 the last result (what the last step did), only when there is one;
        // 4 the loop as one row of step chips (done, next, upcoming, off), every one tappable, and why a step is off;
        // 5 at the bottom: Make my own on a sample, and More (Details, Notebook, Send in a small row).
        // Details is a second page: Back, then one two-column card (test, plan, versions, design, last training, last run,
        // artifacts, what it follows, the truck's live line, the steps that are off and why).
        void Rebuild()
        {
            dirty = false;
            statusText = null;
            brainText = null;
            brainKey = int.MinValue;
            hints.Clear();
            steps.Clear();
            extras.Clear();

            RectTransform body = OSUi.Body(panel);
            if (body == null) return;
            OSUi.Clear(body);
            RectTransform column = OSUi.Column(body, OSProjectLayout.GroupGap);

            OSProjects.LinkedArtifacts(projectId, linked);
            bool sample = OSProjects.IsSample(projectId);
            string status = OSProjects.ComputeStatus(projectId, linked);
            panel.Subtitle = sample ? OSProjects.SampleTag + " · " + status : status;
            bool isOpen = OSProjects.IsOpen(projectId);

            availabilityKey = AvailabilityKey();
            string next = NextAction();
            CollectSteps();
            CollectExtras();
            if (details) BuildDetails(column, isOpen);
            else BuildOverview(column, sample, isOpen, next);
            steps.AddRange(extras);  // the loop's steps first, then Notebook and Send (the voice tool reads them in this order)
            extras.Clear();
            PollBrain();
        }

        /// <summary>The sample window's call to action ("Make my own RC truck"): Build something with the sample's kind picked.</summary>
        public static string MakeOwnLabel(string sampleId) => OSProjects.MakeOwnLabel(sampleId);

        /// <summary>Old name of the sample's call to action; it is now <see cref="MakeOwnLabel"/> (a sample is never copied).</summary>
        public const string CopyLabel = "Make my own";

        /// <summary>The tag line of a sample on the Details page, and of a project of the person's own.</summary>
        public const string SampleTagLine = "Sample · look around", OwnTagLine = "Your project";

        /// <summary>The labels of the overflow button, its row's page button, and the Details page's way back.</summary>
        public const string MoreLabel = "More", LessLabel = "Less", DetailsLabel = "Details", BackLabel = "Back";

        /// <summary>True while the window shows the Details page (plan, versions, runs...) instead of the overview.</summary>
        public bool DetailsOpen
        {
            get => details;
            set { if (details != value) { details = value; more = false; dirty = true; } }
        }

        /// <summary>True while More's small row (Details, Notebook, Send) is open on the overview.</summary>
        public bool MoreOpen
        {
            get => more;
            set { if (more != value) { more = value; dirty = true; } }
        }

        /// <summary>True right after the project's design got a new version: the big button offers the obvious next move.</summary>
        public bool DesignChanged => designChanged;

        // The loop's steps, with why each is off (the voice tool and both pages read them).
        void CollectSteps()
        {
            LoopSteps(scratchSteps);
            for (int i = 0; i < scratchSteps.Count; i++)
            {
                string action = scratchSteps[i];
                string off = UnavailableOf(action);
                steps.Add(new Step { label = LabelOf(action), action = action, unavailable = off });
                if (off != null) Hint(LabelOf(action), off);
            }
        }

        // Notebook and Send (in More's row). Send drafts the project's latest result to its channel (rc-truck, quadcopter,
        // rocket, image-classifier); the message panel's Send / Cancel is the confirm, so nothing leaves the headset from it.
        void CollectExtras()
        {
            string noNotebook = OSProjects.FindTool("notebook") == null ? "The notebook window couldn't be found." : null;
            extras.Add(new Step { label = "Notebook", action = "notebook", unavailable = noNotebook });
            extras.Add(new Step { label = "Send", action = "send", unavailable = null });
            if (noNotebook != null) Hint("Notebook", noNotebook);
        }

        // ---- the overview

        void BuildOverview(RectTransform column, bool sample, bool isOpen, string next)
        {
            // 1 What it is, in one sentence.
            JObject plan = isOpen ? NotebookStore.CurrentPlan : null;
            string goal = plan != null ? (string)plan["goal"] : null;
            bool noPlan = !sample && string.IsNullOrWhiteSpace(goal);
            string what = sample ? OSProjects.SampleShows(projectId) + "."
                : !noPlan ? goal
                : "No plan yet. Ask Albie to plan it with you.";
            TMP_Text lead = OSUi.Text(column, Clip(what, 110), OSUi.TitleText, noPlan);
            lead.fontStyle = FontStyles.Bold;

            // 2 The one big button.
            string hero = HeroAction(next, out string heroLabel);
            if (hero != null)
            {
                string captured = hero;
                OSProjectLayout.Hero(column, heroLabel, () => Do(captured), hero == "notebook" ? "notebook" : IconOf(hero));
            }

            // 3 The last result. A suggestion that only repeats the big button ("Next step: tap Course...") isn't shown.
            string shown = hero != null && statusLine.StartsWith("Next step: tap ", StringComparison.Ordinal) ? "" : statusLine;
            statusText = OSUi.Text(column, shown, LeadSize);

            // 4 The loop: every chip can be tapped at any time (a done step opens its tool again); an off one says why below.
            RectTransform stepper = OSUi.Stepper(column);
            int current = -1;
            string ringed = hero != null && IsLoopStep(hero) ? hero : next;
            for (int i = 0; i < scratchSteps.Count; i++)
                if (scratchSteps[i] == ringed) { current = i; break; }
            int doneUpTo = -1;
            for (int i = 0; i < scratchSteps.Count; i++)
                if (scratchSteps[i] == next) { doneUpTo = i; break; }
            for (int i = 0; i < scratchSteps.Count; i++)
            {
                string action = scratchSteps[i];
                OSUi.StepState state = UnavailableOf(action) != null ? OSUi.StepState.Locked
                    : i == current ? OSUi.StepState.Current
                    : doneUpTo >= 0 && i < doneUpTo ? OSUi.StepState.Done
                    : OSUi.StepState.Upcoming;
                string captured = action;
                OSUi.StepChip(stepper, i + 1, LabelOf(action), IconOf(action), state, () => DoIfOn(captured));
            }
            if (hints.Count > 0) OSUi.Text(column, Clip(string.Join(" ", hints), 150), NoteSize, true);

            // 5 Along the bottom.
            OSProjectLayout.FlexibleHeight(column);
            if (more)
            {
                RectTransform extraRow = OSUi.Row(column, OSUi.ChipHeight, OSUi.MinGap);
                OSUi.Rigid(extraRow);
                OSProjectLayout.Fit(OSUi.Button(extraRow, DetailsLabel, () => DetailsOpen = true, OSButtonStyle.Secondary), OSUi.ChipHeight);
                for (int i = 0; i < extras.Count; i++)
                {
                    Step e = extras[i];
                    string action = e.action;
                    string off = e.unavailable;
                    Button b = OSUi.Button(extraRow, e.label, () => { if (off == null) Do(action); }, OSButtonStyle.Secondary, action);
                    OSProjectLayout.Fit(b, OSUi.ChipHeight);
                    if (b != null && off != null) b.interactable = false;
                }
            }
            RectTransform row = OSUi.Row(column, OSUi.ChipHeight, OSUi.MinGap);
            OSUi.Rigid(row);
            if (sample) OSProjectLayout.Fit(OSUi.Button(row, OSProjects.MakeOwnLabel(projectId), () => Do("make_own"), OSButtonStyle.Secondary, "plus"), OSUi.ChipHeight);
            OSProjectLayout.FlexibleWidth(row);
            OSProjectLayout.Fit(OSUi.Button(row, more ? LessLabel : MoreLabel, () => MoreOpen = !more, OSButtonStyle.Borderless), OSUi.ChipHeight);
        }

        bool IsLoopStep(string action)
        {
            for (int i = 0; i < scratchSteps.Count; i++)
                if (scratchSteps[i] == action) return true;
            return false;
        }

        // What the big button does, and its label: right after a design change the obvious next move ("Fly the new version",
        // "Simulate the new version", or back to the step that was next); otherwise the next step when it can run; a project
        // with no next step to suggest gets its notebook (Build something's first step for "something else").
        string HeroAction(string next, out string label)
        {
            label = null;
            if (designChanged)
            {
                string after = AfterDesign(next);
                if (after != null && UnavailableOf(after) == null)
                {
                    label = after == "fly" ? "Fly the new version"
                        : after == "simulate" ? "Simulate the new version"
                        : after == returnTo ? "Back to " + LabelOf(after)
                        : "Next: " + LabelOf(after);
                    return after;
                }
            }
            if (next != null)
            {
                if (UnavailableOf(next) != null) return null;
                label = "Next: " + LabelOf(next);
                return next;
            }
            if (OSProjects.FindTool("notebook") == null) return null;
            label = "Next: Notebook";
            return "notebook";
        }

        // The obvious move after the design changed: fly it (a drone on its course), simulate it (the rocket), or go back.
        string AfterDesign(string next)
        {
            if (loop == Loop.Quadcopter && ArtifactDrone.Live != null) return "fly";
            if (loop == Loop.Rocket) return "simulate";
            if (returnTo != null && returnTo != "design" && returnTo != "cad") return returnTo;
            return next != "design" && next != "cad" ? next : null;
        }

        // ---- the Details page

        void BuildDetails(RectTransform column, bool isOpen)
        {
            RectTransform row = OSUi.Row(column, OSUi.ChipHeight, OSUi.MinGap);
            OSUi.Rigid(row);
            OSProjectLayout.Fit(OSUi.Button(row, BackLabel, () => DetailsOpen = false, OSButtonStyle.Secondary, "chevron_left"), OSUi.ChipHeight);
            BuildSummary(OSUi.Card(column, 24f, 8f), isOpen);
        }

        readonly List<string> scratchSteps = new List<string>(6);

        // A tap on a chip: any step that is on runs, done or not (a done step opens its tool again). An off chip is switched
        // off (its reason is under the row), so this only guards a press that lands anyway.
        void DoIfOn(string action)
        {
            if (UnavailableOf(action) == null) Do(action);
        }

        // The loop's step ids, in order (Send and Notebook are in the action row).
        void LoopSteps(List<string> into)
        {
            into.Clear();
            switch (loop)
            {
                case Loop.Truck: into.Add("design"); into.Add("course"); into.Add("drive"); into.Add("train"); into.Add("autopilot"); into.Add("code"); break;
                case Loop.Quadcopter: into.Add("design"); into.Add("course"); into.Add("fly"); into.Add("train"); into.Add("autopilot"); break;
                case Loop.Rocket: into.Add("design"); into.Add("simulate"); into.Add("launch"); break;
                case Loop.Ml: into.Add("data"); into.Add("train"); into.Add("results"); into.Add("publish"); break;
                default:
                    // A plan whose test runs on a course (the drone's hover, hoops or landing) gets the step that makes it.
                    if (PlanCourse() != null) into.Add("course");
                    into.Add("cad");
                    into.Add("train");
                    break;
            }
        }

        static string LabelOf(string action)
        {
            switch (action)
            {
                case "cad": return "CAD";
                case "make_own": return "Make my own";
                default: return action.Length == 0 ? action : char.ToUpperInvariant(action[0]) + action.Substring(1);
            }
        }

        static string IconOf(string action)
        {
            switch (action)
            {
                case "course": return "slalom";
                case "drive": return "drive";
                case "fly": return "quadcopter";
                case "train": return "train";
                case "autopilot": return "truck";
                case "code": return "code";
                case "design":
                case "cad": return "cube";
                case "simulate": return "physics";
                case "launch": return "rocket";
                case "data": return "table";
                case "results": return "scoreboard";
                case "publish": return "send";
                default: return null;
            }
        }

        // Why a step is off right now, or null when it can run.
        string UnavailableOf(string action)
        {
            switch (action)
            {
                case "drive":
                case "fly": return OSProjects.FindTool("drive") == null ? DriveMissing : null;
                case "train":
                case "autopilot":
                    if (loop == Loop.Quadcopter) return DroneBrainProblem();
                    if (loop == Loop.Ml) return action == "train" ? MlCloudOnly() : null;
                    return null;
                case "launch": return HasLaunch() ? null : "the launch pad isn't in this build yet.";
                case "data": return MlCloudOnly();
                case "results": return HasTraining() ? null : "no training run yet.";
                case "publish": return !ForgeCloud.Serves("/ml/") ? PublishNeedsCloud : CanPublish() ? null : "only for a model Albie built for you.";
                default: return null;
            }
        }

        // A cloud project's data lives there.
        string MlCloudOnly() => mlName != null && !ForgeCloud.Serves("/ml/") ? OSProjects.NeedsCloud : null;

        // Steps off for the same reason share one line: "Train, Autopilot: the drone has no brain...".
        void Hint(string label, string reason)
        {
            string tail = ": " + reason;
            for (int i = 0; i < hints.Count; i++)
                if (hints[i].EndsWith(tail, StringComparison.Ordinal))
                {
                    hints[i] = hints[i].Substring(0, hints[i].Length - tail.Length) + ", " + label + tail;
                    return;
                }
            hints.Add(label + tail);
        }

        // The Details page's card: where it stands, as a two-column summary.
        void BuildSummary(RectTransform card, bool isOpen)
        {
            bool sample = OSProjects.IsSample(projectId);
            string follows = OSProjects.CopyOf(projectId);
            OSUi.Tag(card, sample ? SampleTagLine : follows != null ? OwnTagLine + " · like the " + OSProjects.TitleFor(follows) + " sample" : OwnTagLine);
            JObject plan = isOpen ? NotebookStore.CurrentPlan : null;
            string test = plan != null && plan["test"] != null && plan["test"].Type == JTokenType.String ? (string)plan["test"] : null;
            if (!string.IsNullOrWhiteSpace(test)) OSUi.Pair(card, "Test", Clip(test, 110));
            else if (sample) OSUi.Pair(card, "Sample", OSProjects.SampleLine);
            OSUi.Pair(card, "Plan", PlanLine(plan));

            var versions = isOpen ? NotebookStore.Versions : null;
            if (versions != null && versions.Count > 0)
            {
                JObject v = versions[versions.Count - 1];
                int number = (int?)v["version"] ?? 0;
                string summary = (string)v["summary"];
                OSUi.Pair(card, "Versions", "v" + number + " · " + Clip(summary ?? "", 60) + " · " + OutcomeOfVersion(number)
                                            + (versions.Count > 1 ? " (" + versions.Count + " in all)" : ""));
            }
            else OSUi.Pair(card, "Versions", "none yet. " + VersionRule(loop));

            if (design != null) OSUi.Pair(card, "Design", CadLine(design));
            string run = isOpen ? LastTrainingLine() : null;
            if (run != null || loop == Loop.Truck || loop == Loop.Quadcopter || loop == Loop.Ml)
                OSUi.Pair(card, "Last training", run != null ? Clip(run, 80) : "none yet");

            OSProjects.LinkedArtifact newest = null;
            for (int i = 0; i < linked.Count; i++)
                if (linked[i].runId != null && (newest == null || string.CompareOrdinal(linked[i].runId, newest.runId) > 0)) newest = linked[i];
            if (linked.Count > 0) OSUi.Pair(card, "Last run", newest != null ? Clip(newest.title, 30) + " · " + RunLine(newest) : "no runs yet");

            OSUi.Pair(card, "Artifacts", ArtifactsLine());
            string from = MlLinksLine(out string fromLabel);
            if (from != null) OSUi.Pair(card, fromLabel, from);
            if (kind == OSProjectKind.Truck) brainText = OSUi.Pair(card, "Truck", "");
            if (hints.Count > 0) OSUi.Pair(card, "Off", Clip(string.Join(" ", hints), 150));
            if (statusLine.Length > 0) statusText = OSUi.Pair(card, "Last result", statusLine);
        }

        // "v2 · 3 of 7 milestones done · next: A clean lap at 2 m/s or more", or what to do when there is none.
        static string PlanLine(JObject plan)
        {
            if (plan == null) return "No plan written yet. Ask Albie to plan it with you.";
            int version = (int?)plan["version"] ?? 0;
            string line = "Plan v" + version;
            if (!(plan["milestones"] is JArray milestones) || milestones.Count == 0) return line;
            int done = 0;
            string upNext = null;
            foreach (JToken t in milestones)
            {
                if (!(t is JObject item)) continue;
                if ((bool?)item["done"] == true) done++;
                else if (upNext == null) upNext = (string)item["text"];
            }
            line += " · " + done + " of " + milestones.Count + " milestones done";
            return upNext != null ? line + " · next: " + Clip(upNext, 48) : line;
        }

        // "Slalom v1, Hoop course v2" (the newest few), or what makes the first one.
        string ArtifactsLine()
        {
            if (linked.Count == 0)
                return kind == OSProjectKind.Truck ? "No artifacts yet. Course makes the slalom for this project."
                    : "No artifacts yet. What this project's steps make shows here.";
            var sb = new System.Text.StringBuilder();
            int shown = Mathf.Min(linked.Count, MaxArtifacts);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Clip(linked[i].title, 28)).Append(" v").Append(linked[i].version);
            }
            if (linked.Count > shown) sb.Append(" and ").Append(linked.Count - shown).Append(" more");
            return sb.ToString();
        }

        /// <summary>What cuts a version for this loop, in words. Only what <see cref="ProjectSpine"/> really does.</summary>
        public static string VersionRule(Loop loop)
        {
            switch (loop)
            {
                case Loop.Quadcopter: return "One is cut when the design gets a new version or a training run finishes.";
                case Loop.Rocket: return "One is cut when the design gets a new version.";
                default: return "One is cut when a training run finishes.";
            }
        }

        static string OutcomeOfVersion(int version)
        {
            var all = NotebookStore.Entries;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                string entryKind = (string)all[i]["kind"];
                if ((int?)all[i]["project_version"] != version || (entryKind != "attempt" && entryKind != "training")) continue;
                string outcome = (string)all[i]["outcome"];
                return string.IsNullOrEmpty(outcome) || outcome == "open" ? "no result yet" : outcome;
            }
            return "no attempt yet";
        }

        // "quadcopter v7 · v8 on screen": the version the project pins, and what CAD shows now if newer.
        static string CadLine(string part)
        {
            int pinned = ProjectSpine.PinnedCad(part);
            int shown = ProjectSpine.LastCadPart == part ? ProjectSpine.LastCadVersion : 0;
            if (pinned == 0 && shown == 0) return "the " + part + " design isn't open yet.";
            string line = part + " v" + (pinned > 0 ? pinned : shown);
            if (pinned == 0) line += " (as opened)";
            else if (shown > 0 && shown != pinned) line += " · v" + shown + " on screen";
            return line;
        }

        // The newest training entry of the open project: what it did, and its numbers when it has them.
        static string LastTrainingLine()
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

        // "Made from: RC truck", "Follows: the RC truck sample", or "ML: Digit CNN, made from it".
        string MlLinksLine(out string label)
        {
            label = "Made from";
            string parent = OSProjects.ParentOf(projectId);
            if (parent != null) return OSProjects.TitleFor(parent);
            label = "Follows";
            string copied = OSProjects.CopyOf(projectId);
            if (copied != null) return "the " + OSProjects.TitleFor(copied) + " sample's steps (its history stays in the sample)";
            label = "ML";
            if (!(ProjectMeta.Read(projectId)?["ml_links"] is JArray links) || links.Count == 0) return null;
            var names = new List<string>(links.Count);
            foreach (JToken t in links)
            {
                string title = (string)t?["title"] ?? (string)t?["name"];
                if (!string.IsNullOrEmpty(title)) names.Add(title);
            }
            return names.Count == 0 ? null : Clip(string.Join(", ", names), 70) + ", made from it";
        }

        static string RunLine(OSProjects.LinkedArtifact a)
        {
            if (a.runId == null) return "no runs yet";
            string line = "last run " + OSProjects.RunWord(a.runOutcome);
            if (a.runSeconds.HasValue) line += " · " + a.runSeconds.Value.ToString("0.0") + " s";
            if (!string.IsNullOrEmpty(a.runReason) && a.runOutcome != "completed") line += " · " + a.runReason.Replace('_', ' ');
            return line;
        }

        /// <summary>What the Drive step says in the one case left: the Drive tool is not in the menu's registry (it is registered at start).</summary>
        public const string DriveMissing = "The Drive pad couldn't be found.";

        // ---------------------------------------------------------------- the next step

        /// <summary>
        /// The step to do next, from what is really there: no course yet means Course, a course with no laps means Drive,
        /// and so on. Null when the loop has none to suggest.
        /// </summary>
        public string NextAction()
        {
            switch (loop)
            {
                case Loop.Truck:
                {
                    var brain = OSProjects.LiveLoop as SlalomBrain;
                    if (brain == null) return "course";
                    if (brain.CompletedLaps < 2 && !HasTraining()) return "drive";
                    return HasTraining() ? "autopilot" : "train";
                }
                case Loop.Quadcopter:
                    if (ProjectSpine.PinnedCad("quadcopter") == 0 && ProjectSpine.LastCadPart != "quadcopter") return "design";
                    if (ArtifactDrone.Live == null) return "course";
                    if (DroneBrainProblem() != null) return "fly";
                    if (!HasTraining() && ((DroneLoop() as Forge.Controllers.ICourseBrain)?.LapsCompleted ?? 0) < 2) return "fly";  // two flown laps before Train
                    return HasTraining() ? "autopilot" : "train";
                case Loop.Rocket:
                    if (ProjectSpine.PinnedCad("rocket") == 0 && ProjectSpine.LastCadPart != "rocket") return "design";
                    if (!simulated) return "simulate";
                    return HasLaunch() ? "launch" : "simulate";
                case Loop.Ml:
                    if (!HasTraining()) return dataOpened ? "train" : "data";
                    return ForgeCloud.Serves("/ml/") && CanPublish() ? "publish" : "results";
                default:
                    return PlanCourse() != null ? "course" : null;
            }
        }

        /// <summary>The next step as a sentence: "Next step: tap Course to set up a drone course."</summary>
        public string NextStepLine()
        {
            switch (NextAction())
            {
                case "course":
                    return loop == Loop.Quadcopter ? "Next step: tap Course to set up a drone course."
                        : loop == Loop.Truck ? "Next step: tap Course to make the course for the truck." : "Next step: tap Course to set up the test.";
                case "drive": return "Next step: tap Drive and drive two laps by hand.";
                case "train": return loop == Loop.Ml ? "Next step: tap Train to train the model." : "Next step: tap Train so the brain learns from your laps.";
                case "autopilot": return "Next step: tap Autopilot and watch it drive itself.";
                case "design": return "Next step: tap Design to open the " + design + " in CAD.";
                case "fly": return "Next step: tap Fly and take off from the Drive window.";
                case "simulate": return "Next step: tap Simulate to see how high it flies.";
                case "launch": return "Next step: tap Launch to fly it in the room.";
                case "data": return "Next step: tap Data to look at the examples.";
                case "results": return "Next step: tap Results to see what it got right.";
                case "publish": return "Next step: tap Publish to put it on GitHub (you confirm first).";
                default: return "Tap a step to continue.";
            }
        }

        static bool HasTraining()
        {
            var all = NotebookStore.Entries;
            for (int i = all.Count - 1; i >= 0; i--)
                if ((string)all[i]["kind"] == "training") return true;
            return false;
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
            return DroneLoop() == null ? NoDroneBrain : null;
        }

        static bool HasLaunch()
        {
            if (Array.IndexOf(ArtifactLibrary.Templates, RocketLaunchTemplate) >= 0 || ArtifactCourses.IsTemplate(RocketLaunchTemplate)) return true;
            try { return ArtifactLibrary.TryTemplate(RocketLaunchTemplate, out _, out _); }
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

        // ---------------------------------------------------------------- actions

        /// <summary>
        /// Runs one step by id (course, drive, fly, train, autopilot, code, send, design, simulate, launch, data, dataset,
        /// results, publish, cad, notebook), shows
        /// its result in the status line and returns that line. The buttons call this.
        /// </summary>
        public string Do(string action)
        {
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
            // Any other step of the loop answers "what now" after a design change; Notebook and Send don't.
            if (designChanged && action != "design" && action != "notebook" && action != "send")
            {
                designChanged = false;
                designAtOpen = -1;
                returnTo = null;
            }
            dirty = true;  // the lists (versions, runs, buttons) may have changed; the next Update rebuilds
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
                case "code": return OpenMode("code", "Code");
                case "cad": return OpenModeOrTool("cad", "cad", "CAD");
                case "send":
                    MessagesShare.DraftForProject(projectId, out string sendStatus);
                    return string.IsNullOrEmpty(sendStatus) ? "Nothing to share yet." : sendStatus;
                case "notebook": return OpenToolLine(OSProjects.FindTool("notebook"), "Notebook");
                case "design" when loop == Loop.Truck:
                    // The truck's design is on the device (TruckDesign): sliders change it on the course at once.
                    Forge.Controllers.TruckDesignPanel.Open();
                    return "Truck design is open. Move a slider and the truck on the course changes; Save as version keeps it.";
                case "design":
                    WatchDesign();
                    SetStatus("Opening CAD...");
                    OSProjects.OpenDesign(design, line =>
                    {
                        SetStatus(line);
                        ShowParameters();
                    });
                    return null;
                case "dataset": return DoDataset();
                case "make_own":
                case "copy": return DoMakeOwn();  // "copy" is the old name: a sample is never copied, Make my own plans a new one
                case "start_own":
                    OSProjects.StartOwn();
                    return "New project is open. Answer a few questions and Albie plans your project.";
                default: return "I don't know that step.";
            }
        }

        // Make my own: Build something with the sample's kind picked; Start there makes the person's own project and the
        // window follows it. The sample is left as it is.
        string DoMakeOwn()
        {
            if (!OSProjects.IsSample(projectId)) return "This is already your own project.";
            if (!OSProjects.MakeOwn(projectId)) return "New project couldn't open right now.";
            return "New project is open with " + OSProjects.TitleFor(projectId) + " picked. Answer the questions, then tap Start to make " + OSProjects.OwnTitle(projectId) + ".";
        }

        string DoCourse()
        {
            // The project must be open first: the slalom links to this project only while its notebook is the open one.
            if (!OSProjects.EnsureOpen(projectId, out string error)) return error;

            // The course the plan's test is run on (Build something saves it); the truck's is the slalom when the plan names none.
            string course = PlanCourse() ?? (loop == Loop.Quadcopter ? "hoop_course" : "slalom");
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
            return DescribeCourse(result);
        }

        string courseName = "slalom";

        // The saved plan's course for this project ("dash", "hover"...), or null: no plan, another project open, or a test with no course.
        string PlanCourse()
        {
            if (!NotebookStore.IsOpen || NotebookStore.ProjectId != projectId) return null;
            JObject plan = NotebookStore.CurrentPlan;
            string course = plan != null && plan["course"] != null && plan["course"].Type == JTokenType.String ? (string)plan["course"] : null;
            if (string.IsNullOrEmpty(course)) return null;
            return ArtifactCourses.IsTemplate(course) || System.Array.IndexOf(ArtifactLibrary.Templates, course) >= 0 ? course : null;
        }

        // "slalom", "hoop course", "dash course": what the status line calls it.
        static string CourseName(string course)
        {
            if (course == "slalom") return "slalom";
            if (course == "hoop_course") return "hoop course";
            return ArtifactCourses.Title(course).ToLowerInvariant() + " course";
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

        string DoDrive()
        {
            OSEntry drive = OSProjects.FindTool("drive");
            if (drive == null) return DriveMissing;
            if (!OSProjects.OpenTool(drive, out string error)) return error;

            var brain = OSProjects.LiveLoop as SlalomBrain;
            if (brain == null) return "The Drive pad is open. Open the course first so the truck can run.";
            if (brain.State != SlalomBrain.Mode.Idle) return "The Drive pad is open. The truck is " + StateWord(brain.State) + ".";

            // Laps are only scored while the brain records them, so the Drive beat starts a two-lap recording.
            JObject result = AlbieTools.Call("brain_record", new JObject { ["start"] = true, ["laps"] = 2, ["then_autopilot"] = false });
            string recordError = (string)result["error"];
            if (recordError != null) return "The Drive pad is open. " + OSProjects.Plain(recordError);
            return "Recording 2 laps. Drive the truck around the cones with the pad, then tap Train.";
        }

        string DoTrain()
        {
            if (loop == Loop.Ml) return DoMlTrain();
            if (loop == Loop.Quadcopter)
            {
                string problem = DroneBrainProblem();
                if (problem != null) return "Train: " + problem;
                return BrainTrain("Training the drone's brain on ");
            }
            string toolError = OpenTrainTool();
            if (kind != OSProjectKind.Truck)
            {
                if (toolError != null) return toolError;
                return "Train is open.";
            }

            if (OSProjects.LiveLoop == null) return "Open the course first. The brain learns from laps on the slalom.";
            return BrainTrain("Training the brain on ");
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
            if (loop == Loop.Quadcopter)
            {
                string problem = DroneBrainProblem();
                if (problem != null) return "Autopilot: " + problem;
                JObject flown = AlbieTools.Call("autopilot_on", new JObject());
                string flyError = (string)flown["error"];
                return flyError != null ? OSProjects.Plain(flyError) : "The Autopilot is flying the drone course.";
            }
            var brain = OSProjects.LiveLoop as SlalomBrain;
            if (brain == null) return "Open the course first. The Autopilot drives the slalom.";
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

        // The old "dataset" step (voice and checks may still name it): the Data step with the cloud.
        string DoDataset() => ForgeCloud.Serves("/ml/") ? DoData() : OSProjects.NeedsCloud;

        // ---------------------------------------------------------------- the quadcopter and the rocket

        string DoFly()
        {
            OSEntry drive = OSProjects.FindTool("drive");
            if (drive == null) return DriveMissing;
            if (!OSProjects.OpenTool(drive, out string error)) return error;
            if (ArtifactDrone.Live == null) return "The Drive window is open. Tap Course first: the drone stands at the start of a drone course.";
            return "The Drive window flies the drone. Tap Take off, fly with the two pads, then Land.";
        }

        // The rocket module's own simulate tool when it is there; otherwise CAD's flight of the design on screen (the hub's
        // sim, or the offline sample's stored flight).
        string DoSimulate()
        {
            if (AlbieTools.Has(RocketSimulateTool))
            {
                SetStatus("Simulating the flight...");
                AlbieTools.CallAsync(RocketSimulateTool, new JObject(), result =>
                {
                    string error = result != null ? (string)result["error"] : "no answer";
                    if (error == null) simulated = true;
                    string say = result != null ? (string)result["say"] : null;
                    SetStatus(error != null ? OSProjects.Plain(error) : !string.IsNullOrWhiteSpace(say) ? say.Trim() : "The flight is simulated.");
                    dirty = true;
                });
                return null;
            }
            CadSession cad = CadSession.Instance;
            CadUpdate shown = cad != null && cad.Ready ? cad.Latest : null;
            if (shown?.info == null || shown.info.part != "rocket") return "Open the rocket first: tap Design, then Simulate.";
            int version = shown.info.version;
            SetStatus("Simulating the flight of v" + version + "...");
            cad.GetFlight(version, null, flight =>
            {
                if (flight == null)
                {
                    SetStatus(cad.IsMock ? "The offline sample has no flight for v" + version + "." : "The flight sim didn't answer.");
                    return;
                }
                simulated = true;
                string line = "v" + version + " flies to " + flight.apogeeM.ToString("0") + " m and lands " + flight.landingTimeS.ToString("0")
                              + " s after launch, " + flight.driftM.ToString("0") + " m from the pad.";
                if (!string.IsNullOrWhiteSpace(flight.warning)) line += " " + OSProjects.Plain(flight.warning);
                SetStatus(line);
                dirty = true;
            });
            return null;
        }

        string DoLaunch()
        {
            if (!HasLaunch()) return "Launch: the launch pad isn't in this build yet.";
            if (!OSProjects.EnsureOpen(projectId, out string error)) return error;
            OSProjects.LinkedArtifacts(projectId, linked);
            OSProjects.LinkedArtifact made = null;
            for (int i = 0; i < linked.Count; i++)
                if (linked[i].template == RocketLaunchTemplate || linked[i].id == RocketLaunchTemplate) { made = linked[i]; break; }
            JObject result = made != null
                ? AlbieTools.Call("artifact_open", new JObject { ["id"] = made.id })
                : ArtifactLibrary.ForProject(projectId, () => AlbieTools.Call("artifact_create", new JObject { ["template"] = RocketLaunchTemplate }));
            string problem = (string)result["error"];
            if (problem != null) return OSProjects.Plain(problem);
            JObject opened = result["opened"] as JObject ?? result;  // artifact_create nests the open result
            string status = (string)opened["status"];
            if (status == "awaiting_approval") return "The launch pad asks first. Tap Allow on the card in front of you.";
            if (status != "open") return "The launch pad didn't open.";
            string say = (string)opened["say"];
            return "The launch pad is open." + (string.IsNullOrWhiteSpace(say) ? "" : " " + say.Trim());
        }

        // ---------------------------------------------------------------- ML projects (the classifier, cloud projects)

        // The hub or the cloud opens this project on the ML desk; offline the desk opens on the bundled images.
        string DoData()
        {
            dataOpened = true;
            if (ForgeCloud.Serves("/ml/"))
            {
                SetStatus("Asking Albie's cloud to open the data...");
                var args = new JObject();
                if (mlName != null) args["project"] = mlName;
                AlbieTools.CallAsync("ml_open", args, result =>
                {
                    string error = result != null ? (string)result["error"] : "no answer";
                    SetStatus(error != null ? OSProjects.Plain(error) : "The ML desk is open on " + OSProjects.TitleFor(projectId) + "'s data.");
                    dirty = true;
                });
                return null;
            }
            if (mlName != null) return "Data: " + OSProjects.NeedsCloud;
            if (!OSModes.Open("ml")) return "Train couldn't open right now.";
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
            return (opened ? "The results are on the ML desk. " : "") + "Last run: " + last;
        }

        // Asks first, always: ml_publish without confirm only reads back what would be pushed. The person says yes to Albie.
        string DoPublish()
        {
            if (!ForgeCloud.Serves("/ml/")) return "Publish " + PublishNeedsCloud;
            if (!CanPublish()) return "Publish is only for a model Albie built for you.";
            SetStatus("Asking what would be published...");
            AlbieTools.CallAsync("ml_publish", new JObject(), result =>
            {
                string error = result != null ? (string)result["error"] : "no answer";
                SetStatus(error != null ? OSProjects.Plain(error)
                    : "Nothing is pushed yet. Albie read back what would go on GitHub: say yes to him to open the pull request.");
            });
            return null;
        }

        string OpenTrainTool()
        {
            OSEntry tool = OSProjects.FindTool("train", "ml");
            if (tool != null) return OSProjects.OpenTool(tool, out string error) ? null : error;
            return OpenMode("ml", "Train") == null ? null : "Train couldn't open right now.";
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

        void SetStatus(string text)
        {
            statusLine = text ?? "";
            if (statusText != null) statusText.text = statusLine;
        }

        // ---------------------------------------------------------------- after a design change

        // Design was tapped: remember what was next, and watch the design's version from the one CAD shows now (or shows next).
        void WatchDesign()
        {
            string before = NextAction();
            if (before != "design" && before != "cad") returnTo = before;
            else if (!designChanged) returnTo = null;
            designChanged = false;
            designAtOpen = design != null && ProjectSpine.LastCadPart == design ? ProjectSpine.LastCadVersion : -2;
        }

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

        // A slider or preset of this design was changed after Design was tapped. Offline CAD shows the nearest stored version,
        // which may be the same number, so the change itself counts, not only a new version.
        void OnDesignParams(string part, Dictionary<string, object> changes)
        {
            if (designAtOpen == -1 || design == null || part != design || designChanged) return;
            designChanged = true;
            simulated = false;
            SetStatus("The " + design + " design changed.");
            dirty = true;
        }

        // Every poll (no allocation unless the version really changed): a newer version of the design than the one opened.
        void PollDesign()
        {
            if (designAtOpen == -1 || design == null || ProjectSpine.LastCadPart != design) return;
            int shown = ProjectSpine.LastCadVersion;
            if (designAtOpen == -2)
            {
                designAtOpen = shown;  // CAD now shows the design: changes count from here
                return;
            }
            if (shown <= designAtOpen) return;
            designChanged = true;
            simulated = false;  // a new rocket hasn't flown in the sim yet
            designAtOpen = shown;
            SetStatus("The " + design + " is now v" + shown + ".");
            dirty = true;
        }

        // ---------------------------------------------------------------- the live brain line (truck)

        void PollBrain()
        {
            if (brainText == null) return;
            ArtifactHost host = ArtifactHost.Current;
            bool courseOpen = host != null && host.OpenId != null;
            var brain = OSProjects.LiveLoop as SlalomBrain;
            int key = (courseOpen ? 1 : 0) + (brain != null ? 2 : 0);
            if (brain != null) key += (int)brain.State * 4 + Mathf.Min(brain.CompletedLaps, 99) * 32;
            if (key == brainKey) return;  // nothing changed: no string is built
            brainKey = key;

            string course = courseOpen ? "Course: open" : "Course: not open";
            if (brain == null)
            {
                brainText.text = course + " · no brain truck";
                return;
            }
            int laps = brain.CompletedLaps;
            brainText.text = course + " · brain truck " + StateWord(brain.State) + " · " + laps + (laps == 1 ? " lap" : " laps") + " recorded";
        }

        static string StateWord(SlalomBrain.Mode mode)
        {
            switch (mode)
            {
                case SlalomBrain.Mode.Lap: return "recording a lap";
                case SlalomBrain.Mode.Training: return "training";
                case SlalomBrain.Mode.Autopilot: return "on Autopilot";
                default: return "ready";
            }
        }

        static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace('\n', ' ').Trim();
            return text.Length <= max ? text : text.Substring(0, max - 3).TrimEnd() + "...";
        }
    }
}
