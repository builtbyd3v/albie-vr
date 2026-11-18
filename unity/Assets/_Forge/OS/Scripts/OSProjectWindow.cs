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
    /// The Project window (window id "project"): the project's home, a view of <see cref="OSProjectSession"/>. Top to bottom:
    /// one sentence (the sample's line or the plan's goal), how far it is ("3 of 5 stages done · at Fly") with a progress
    /// rail, ONE big button (Next: Train, Fly v8, Go to Course), the last result, and the stages as rows (each with its
    /// status mark and one-line result: "v7 · 160 mm arms", "2 laps flown", "apogee 197 m"; a locked one says why and "Go to
    /// Course", and a tap goes there). Along the bottom: Make my own on a sample, and More (Details, Notebook, Send). Details
    /// is a second page: the test, plan, versions, design, last training, last run, artifacts, what it follows, the live truck
    /// line and what is off. A stage tap is <see cref="OSProjectSession.Go"/>: the stage's tool opens and this window tucks
    /// away (hidden, not closed: the project bar's home button brings it back). The stage loops:
    /// - RC truck: Design, Course, Drive, Train, Autopilot, Code;
    /// - Quadcopter: Design (CAD), Course (a drone course), Fly (the Drive window as the drone's transmitter), Train, Autopilot;
    /// - Rocket: Design (CAD), Simulate (the design's flight), Launch (the <c>rocket_launch</c> artifact);
    /// - Image classifier and every ML project: Data, Train, Results, Publish;
    /// - anything else: Course (when its plan names one), CAD, Train.
    /// </summary>
    public sealed class OSProjectWindow : MonoBehaviour
    {
        public const string WindowId = "project";

        static readonly Vector2 SizeMeters = new Vector2(0.70f, 0.70f);
        const float PollSeconds = 0.5f;
        const int MaxArtifacts = 2;

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

        ForgePanel panel;
        string projectId;
        OSProjectSession session;
        bool dirty, details, more;
        TMP_Text statusText, brainText;
        float nextPoll;
        int brainKey = int.MinValue;
        readonly List<OSProjects.LinkedArtifact> linked = new List<OSProjects.LinkedArtifact>();
        readonly List<Step> steps = new List<Step>();

        /// <summary>One step of the loop: its label, its action id, and why it is off (null when it is on).</summary>
        public struct Step
        {
            public string label, action, unavailable;
        }

        /// <summary>The loop's steps (each stage, then Notebook and Send), as the voice tool reads them.</summary>
        public IReadOnlyList<Step> Steps
        {
            get
            {
                if (Session != null) Session.FillSteps(steps);
                return steps;
            }
        }

        /// <summary>The project this window shows.</summary>
        public string ProjectId => projectId;

        /// <summary>The last line the status area showed (the tool's result, or its error as a sentence).</summary>
        public string StatusLine => Session != null ? Session.StatusLine : "";

        /// <summary>Puts a line in the status area without running anything (New project offers the first step this way).</summary>
        public void Suggest(string line) => Session?.Suggest(line);

        OSProjectSession Session => session != null ? session : session = OSProjectSession.Ensure();

        /// <summary>
        /// The Project window on <paramref name="projectId"/>: opened, or the open one pointed at it. A window tucked away by a
        /// stage stays tucked (the Drive window reaches the project's actions through it); <see cref="Show"/> brings it forward.
        /// Null when the scene has no shell (a sandbox). The caller makes the project the open notebook first.
        /// </summary>
        public static OSWindow Open(string projectId)
        {
            OSShell shell = OSShell.Current;
            if (shell == null || string.IsNullOrEmpty(projectId)) return null;
            OSWindow existing = shell.Windows.Find(WindowId);
            if (existing != null && !existing.gameObject.activeSelf)
            {
                var view = existing.GetComponent<OSProjectWindow>();
                if (view != null && view.projectId != projectId) view.Bind(projectId);
                return existing;
            }
            return Show(projectId);
        }

        /// <summary>The Project window on <paramref name="projectId"/>, brought forward (out of a tuck or its minimised dot).</summary>
        public static OSWindow Show(string projectId)
        {
            OSShell shell = OSShell.Current;
            if (shell == null || string.IsNullOrEmpty(projectId)) return null;
            OSWindow window = shell.Windows.Open(WindowId, OSProjects.TitleFor(projectId), SizeMeters, p =>
            {
                var view = p.gameObject.AddComponent<OSProjectWindow>();
                view.Init(p, projectId);
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

        /// <summary>Points the window at a project (it becomes the session's project) and builds it.</summary>
        public void Bind(string id)
        {
            if (panel == null || string.IsNullOrEmpty(id)) return;
            if (id != projectId) details = more = false;
            projectId = id;
            if (Session.ProjectId != id) Session.Begin(id);
            panel.Title = OSProjects.TitleFor(id);
            panel.SetBadge(OSProjects.IsSample(id) ? "SAMPLE" : "MY PROJECT", ForgeTheme.Tool);  // a sample says so in its header
            dirty = true;
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

        /// <summary>Goes to the stage the project is at (opens its tool). Opening a project no longer does this by itself.</summary>
        public void RunOpenAction() => Session.Go(Session.CurrentStage);

        void OnEnable()
        {
            Session.Changed += MarkDirty;
            dirty = true;
        }

        void OnDisable()
        {
            if (session != null) session.Changed -= MarkDirty;
        }

        void MarkDirty() => dirty = true;

        void Update()
        {
            if (panel == null) return;
            OSProjectSession s = Session;
            if (s.Active && s.ProjectId != projectId)  // the session went to another project: the window follows it
            {
                Bind(s.ProjectId);
                return;
            }
            if (dirty) Rebuild();
            float now = Time.unscaledTime;
            if (now < nextPoll) return;
            nextPoll = now + PollSeconds;
            PollBrain();
        }

        // ---------------------------------------------------------------- building the window

        void Rebuild()
        {
            dirty = false;
            statusText = null;
            brainText = null;
            brainKey = int.MinValue;
            if (panel == null || string.IsNullOrEmpty(projectId)) return;

            RectTransform body = OSUi.Body(panel);
            if (body == null) return;
            OSUi.Clear(body);
            RectTransform column = OSUi.Column(body, OSProjectLayout.GroupGap);

            OSProjects.LinkedArtifacts(projectId, linked);
            bool sample = OSProjects.IsSample(projectId);
            string status = OSProjects.ComputeStatus(projectId, linked);
            panel.Subtitle = sample ? OSProjects.SampleTag + " · " + status : status;
            bool isOpen = OSProjects.IsOpen(projectId);
            if (details) BuildDetails(column, isOpen);
            else BuildOverview(column, sample, isOpen);
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

        /// <summary>The line the overview shows when every stage is done.</summary>
        public const string AllDoneLine = "Every stage is done. Tap any stage to go back to it.";

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

        /// <summary>True when a stage is out of date because the design got a new version (the big button then offers to redo it).</summary>
        public bool DesignChanged
        {
            get
            {
                OSProjectSession s = Session;
                return s.Active && s.Facts.DesignChangedSinceTraining;
            }
        }

        // ---- the overview

        void BuildOverview(RectTransform column, bool sample, bool isOpen)
        {
            OSProjectSession s = Session;
            IReadOnlyList<StageState> stages = s.Stages;

            // 1 What it is, in one sentence, and how far it is.
            RectTransform head = OSUi.Column(column, ForgeTheme.Space2);
            OSProjectLayout.FitHeight(head);
            JObject plan = isOpen ? NotebookStore.CurrentPlan : null;
            string goal = plan != null ? (string)plan["goal"] : null;
            bool noPlan = !sample && string.IsNullOrWhiteSpace(goal);
            string what = sample ? OSProjects.SampleShows(projectId) + "."
                : !noPlan ? goal
                : "No plan yet. Ask Albie to plan it with you.";
            TMP_Text lead = OSUi.Text(head, ProjectStages.Clip(what, 110), OSUi.TitleText, noPlan);
            lead.fontStyle = FontStyles.Bold;
            int done = ProjectStages.DoneCount(StagesList(stages), out int total);
            string at = s.TryStage(s.CurrentStage, out StageState here) ? " · at " + here.Def.Name : "";
            OSUi.Text(head, done + " of " + total + " stages done" + at, OSUi.SmallText, true);
            OSUi.ProgressRail(head, total > 0 ? done / (float)total : 0f);

            // 2 The one big button.
            string label = s.NextLabel();
            if (label != null)
            {
                string icon = s.NextId != null && s.TryStage(s.NextId, out StageState n) ? n.Def.Icon : "chevron_right";
                OSProjectLayout.Hero(column, label, () => s.GoNext(), icon);
            }
            else if (done >= total) OSUi.Text(column, AllDoneLine, OSUi.BodyText, true);

            // 3 The last result.
            if (!string.IsNullOrEmpty(s.StatusLine)) statusText = OSUi.Text(column, ProjectStages.Clip(s.StatusLine, 180), OSUi.BodyText);

            // 4 The stages, one row each: a tap goes there (a locked one goes to the stage that unlocks it).
            RectTransform list = OSUi.Column(column, OSProjectLayout.StageRowGap);
            OSProjectLayout.FitHeight(list);
            for (int i = 0; i < stages.Count; i++)
            {
                StageState st = stages[i];
                string id = st.Id;
                string goTo = st.Status == StageStatus.Locked && st.Unlock != null && s.TryStage(st.Unlock, out StageState u) ? u.Def.Name : null;
                string target = st.Status == StageStatus.Locked && st.Unlock != null ? st.Unlock : id;
                OSProjectLayout.StageRow(list, st, goTo, () => s.Go(target));
            }

            // 5 Along the bottom.
            OSProjectLayout.FlexibleHeight(column);
            if (more)
            {
                RectTransform extraRow = OSUi.Row(column, OSUi.ChipHeight, OSUi.MinGap);
                OSUi.Rigid(extraRow);
                OSProjectLayout.Fit(OSUi.Button(extraRow, DetailsLabel, () => DetailsOpen = true, OSButtonStyle.Secondary), OSUi.ChipHeight);
                ExtraButtons(extraRow);
            }
            RectTransform row = OSUi.Row(column, OSUi.ChipHeight, OSUi.MinGap);
            OSUi.Rigid(row);
            if (sample) OSProjectLayout.Fit(OSUi.Button(row, OSProjects.MakeOwnLabel(projectId), () => Do("make_own"), OSButtonStyle.Secondary, "plus"), OSUi.ChipHeight);
            OSProjectLayout.FlexibleWidth(row);
            OSProjectLayout.Fit(OSUi.Button(row, more ? LessLabel : MoreLabel, () => MoreOpen = !more, OSButtonStyle.Borderless), OSUi.ChipHeight);
        }

        readonly List<StageState> scratchStages = new List<StageState>(6);

        List<StageState> StagesList(IReadOnlyList<StageState> stages)
        {
            scratchStages.Clear();
            for (int i = 0; i < stages.Count; i++) scratchStages.Add(stages[i]);
            return scratchStages;
        }

        // Notebook and Send. Send drafts the project's latest result to its channel (rc-truck, quadcopter, rocket,
        // image-classifier); the message panel's Send / Cancel is the confirm, so nothing leaves the headset from it.
        void ExtraButtons(RectTransform row)
        {
            bool noNotebook = OSProjects.FindTool("notebook") == null;
            Button notebook = OSUi.Button(row, "Notebook", () => { if (OSProjects.FindTool("notebook") != null) Do("notebook"); }, OSButtonStyle.Secondary, "notebook");
            OSProjectLayout.Fit(notebook, OSUi.ChipHeight);
            if (notebook != null && noNotebook) notebook.interactable = false;
            OSProjectLayout.Fit(OSUi.Button(row, "Send", () => Do("send"), OSButtonStyle.Secondary, "send"), OSUi.ChipHeight);
        }

        // ---- the Details page

        void BuildDetails(RectTransform column, bool isOpen)
        {
            RectTransform row = OSUi.Row(column, OSUi.ChipHeight, OSUi.MinGap);
            OSUi.Rigid(row);
            OSProjectLayout.Fit(OSUi.Button(row, BackLabel, () => DetailsOpen = false, OSButtonStyle.Secondary, "chevron_left"), OSUi.ChipHeight);
            OSProjectLayout.FlexibleWidth(row);
            ExtraButtons(row);
            BuildSummary(OSUi.Card(column, ForgeTheme.Space5, ForgeTheme.Space2), isOpen);
        }

        // The Details page's card: where it stands, as a two-column summary.
        void BuildSummary(RectTransform card, bool isOpen)
        {
            Loop loop = LoopOf(projectId);
            string design = OSProjects.DesignOf(projectId);
            bool sample = OSProjects.IsSample(projectId);
            string follows = OSProjects.CopyOf(projectId);
            OSUi.Tag(card, sample ? SampleTagLine : follows != null ? OwnTagLine + " · like the " + OSProjects.TitleFor(follows) + " sample" : OwnTagLine);
            JObject plan = isOpen ? NotebookStore.CurrentPlan : null;
            string test = plan != null && plan["test"] != null && plan["test"].Type == JTokenType.String ? (string)plan["test"] : null;
            if (!string.IsNullOrWhiteSpace(test)) OSUi.Pair(card, "Test", ProjectStages.Clip(test, 110));
            else if (sample) OSUi.Pair(card, "Sample", OSProjects.SampleLine);
            OSUi.Pair(card, "Plan", PlanLine(plan));

            var versions = isOpen ? NotebookStore.Versions : null;
            if (versions != null && versions.Count > 0)
            {
                JObject v = versions[versions.Count - 1];
                int number = (int?)v["version"] ?? 0;
                string summary = (string)v["summary"];
                OSUi.Pair(card, "Versions", "v" + number + " · " + ProjectStages.Clip(summary ?? "", 60) + " · " + OutcomeOfVersion(number)
                                            + (versions.Count > 1 ? " (" + versions.Count + " in all)" : ""));
            }
            else OSUi.Pair(card, "Versions", "none yet. " + VersionRule(loop));

            if (design != null) OSUi.Pair(card, "Design", CadLine(design));
            string run = isOpen ? OSProjectSession.LastTrainingLine() : null;
            if (run != null || loop == Loop.Truck || loop == Loop.Quadcopter || loop == Loop.Ml)
                OSUi.Pair(card, "Last training", run != null ? ProjectStages.Clip(run, 80) : "none yet");

            OSProjects.LinkedArtifact newest = null;
            for (int i = 0; i < linked.Count; i++)
                if (linked[i].runId != null && (newest == null || string.CompareOrdinal(linked[i].runId, newest.runId) > 0)) newest = linked[i];
            if (linked.Count > 0) OSUi.Pair(card, "Last run", newest != null ? ProjectStages.Clip(newest.title, 30) + " · " + OSProjectSession.RunLine(newest) : "no runs yet");

            OSUi.Pair(card, "Artifacts", ArtifactsLine(loop));
            string from = MlLinksLine(out string fromLabel);
            if (from != null) OSUi.Pair(card, fromLabel, from);
            if (OSProjects.KindOf(projectId) == OSProjectKind.Truck) brainText = OSUi.Pair(card, "Truck", "");
            string off = OffLine();
            if (off != null) OSUi.Pair(card, "Off", ProjectStages.Clip(off, 150));
            if (StatusLine.Length > 0) statusText = OSUi.Pair(card, "Last result", StatusLine);
        }

        // The stages this build can't run, with why: "Train, Autopilot: the drone has no brain...".
        string OffLine()
        {
            var lines = new List<string>();
            IReadOnlyList<StageState> stages = Session.Stages;
            for (int i = 0; i < stages.Count; i++)
            {
                StageState s = stages[i];
                if (s.Status != StageStatus.Locked || s.Unlock != null || string.IsNullOrEmpty(s.Reason)) continue;
                string tail = ": " + s.Reason;
                int at = lines.FindIndex(l => l.EndsWith(tail, StringComparison.Ordinal));
                if (at >= 0) lines[at] = lines[at].Substring(0, lines[at].Length - tail.Length) + ", " + s.Def.Name + tail;
                else lines.Add(s.Def.Name + tail);
            }
            return lines.Count == 0 ? null : string.Join(" ", lines);
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
            return upNext != null ? line + " · next: " + ProjectStages.Clip(upNext, 48) : line;
        }

        // "Slalom v1, Hoop course v2" (the newest few), or what makes the first one.
        string ArtifactsLine(Loop loop)
        {
            if (linked.Count == 0)
                return loop == Loop.Truck ? "No artifacts yet. Course makes the slalom for this project."
                    : "No artifacts yet. What this project's stages make shows here.";
            var sb = new System.Text.StringBuilder();
            int shown = Mathf.Min(linked.Count, MaxArtifacts);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ProjectStages.Clip(linked[i].title, 28)).Append(" v").Append(linked[i].version);
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

        // "Made from: RC truck", "Follows: the RC truck sample", or "ML: Digit CNN, made from it".
        string MlLinksLine(out string label)
        {
            label = "Made from";
            string parent = OSProjects.ParentOf(projectId);
            if (parent != null) return OSProjects.TitleFor(parent);
            label = "Follows";
            string copied = OSProjects.CopyOf(projectId);
            if (copied != null) return "the " + OSProjects.TitleFor(copied) + " sample's stages (its history stays in the sample)";
            label = "ML";
            if (!(ProjectMeta.Read(projectId)?["ml_links"] is JArray links) || links.Count == 0) return null;
            var names = new List<string>(links.Count);
            foreach (JToken t in links)
            {
                string title = (string)t?["title"] ?? (string)t?["name"];
                if (!string.IsNullOrEmpty(title)) names.Add(title);
            }
            return names.Count == 0 ? null : ProjectStages.Clip(string.Join(", ", names), 70) + ", made from it";
        }

        /// <summary>What the Drive step says in the one case left: the Drive tool is not in the menu's registry (it is registered at start).</summary>
        public const string DriveMissing = "The Drive pad couldn't be found.";

        // ---------------------------------------------------------------- the next step, the actions (the session's)

        /// <summary>The stage to do next, from what is really there (<see cref="ProjectStages"/>). Null when there is none to suggest.</summary>
        public string NextAction() => Session.Active ? Session.NextId : null;

        /// <summary>The next step as a sentence: "Next step: tap Course to set up a drone course."</summary>
        public string NextStepLine() => Session.NextStepLine();

        /// <summary>
        /// Runs one action by id (course, drive, fly, train, autopilot, code, send, design, simulate, launch, data, dataset,
        /// results, publish, cad, notebook, make_own), shows its result in the status line and returns that line. Nothing is put
        /// away; a tap on a stage is <see cref="Go"/>.
        /// </summary>
        public string Do(string action)
        {
            if (Session.ProjectId != projectId && projectId != null) Session.Begin(projectId);
            string line = Session.Do(action);
            dirty = true;
            return line;
        }

        /// <summary>Goes to a stage, as a tap on it does (<see cref="OSProjectSession.Go"/>).</summary>
        public string Go(string stage)
        {
            if (Session.ProjectId != projectId && projectId != null) Session.Begin(projectId);
            return Session.Go(stage);
        }

        // ---------------------------------------------------------------- the live brain line (truck, Details page)

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
            brainText.text = course + " · brain truck " + OSProjectSession.StateWord(brain.State) + " · " + laps + (laps == 1 ? " lap" : " laps") + " recorded";
        }
    }
}
