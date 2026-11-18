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
    /// The Project window (window id "project"), built in code in four parts (see <see cref="Rebuild"/>): a header card (a
    /// SAMPLE or YOUR PROJECT tag, what it is, its test, where it stands); one row with the single primary button (Next: the
    /// next step, or on a sample "Make my own RC truck", which opens Build something with the sample's kind picked) and the
    /// quiet Notebook and Send; a steps card with the loop as a stepper of chips (done, next, upcoming, off):
    /// - RC truck: Course, Drive, Train, Autopilot, Code;
    /// - Quadcopter: Design (CAD), Course (a drone course), Fly (the Drive window as the drone's transmitter), Train, Autopilot;
    /// - Rocket: Design (CAD), Simulate (the design's flight), Launch (the <c>rocket_launch</c> artifact);
    /// - Image classifier and every ML project: Data, Train, Results, Publish;
    /// - anything else: Course (when its plan names one), CAD, Train;
    /// and a two-column summary (plan, versions, last training, last run, artifacts, design, what it follows). A step whose
    /// feature isn't there (no drone brain yet, no cloud) is switched off with a short reason under the stepper, never a
    /// button that does nothing. Every step shows the tool's real result, or its error as a plain sentence, in the status
    /// line. What a step makes (the course, the launch pad) is linked to this project (<see cref="ArtifactLibrary.ForProject{T}"/>).
    /// A sample's steps run as sample runs (its laps and training go in the sample's notebook, as its history did); nothing
    /// else is attached to it. Versions are cut by <see cref="ProjectSpine"/>. The window follows the open notebook project.
    /// </summary>
    public sealed class OSProjectWindow : MonoBehaviour
    {
        public const string WindowId = "project";

        static readonly Vector2 SizeMeters = new Vector2(0.78f, 0.76f);
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
        }

        void OnDisable()
        {
            NotebookStore.EntryCreated -= OnNotebook;
            NotebookStore.EntryUpdated -= OnNotebook;
            NotebookStore.VersionCut -= OnNotebook;
            NotebookStore.PlanUpdated -= OnNotebook;
            OSRegistry.Changed -= MarkDirty;
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

        // The layout, top to bottom, in OS units (the body is about 1145 x 1086 at this window's size):
        // 1 a header card: a tag (SAMPLE · LOOK AROUND, or YOUR PROJECT), what the project is (the sample's line, or the
        //   plan's goal), its test, and where it stands (v2 · slalom passed);
        // 2 one row of actions: the one primary button (Next: Course for a project of your own; Make my own RC truck on a
        //   sample, with the next step beside it), then Notebook and Send, quiet;
        // 3 a steps card: the loop as a stepper, one chip per step (done, next, upcoming, off), why a step is off, the live
        //   brain line (truck) and the last result;
        // 4 a summary card, two columns: Plan, Versions, Last training, Last run, Artifacts, Design, Follows / Made from.
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
            RectTransform column = OSUi.Column(body, 18f);

            OSProjects.LinkedArtifacts(projectId, linked);
            bool sample = OSProjects.IsSample(projectId);
            string status = OSProjects.ComputeStatus(projectId, linked);
            panel.Subtitle = sample ? OSProjects.SampleTag + " · " + status : status;
            bool isOpen = OSProjects.IsOpen(projectId);

            availabilityKey = AvailabilityKey();
            string next = NextAction();
            BuildHeader(OSUi.Card(column, 24f, 8f), sample, isOpen, status);
            BuildActionRow(column, sample, next);
            BuildSteps(OSUi.Card(column, 24f, 10f), next);
            BuildSummary(OSUi.Card(column, 24f, 8f), isOpen);
            steps.AddRange(extras);  // the loop's steps first, then Notebook and Send (the voice tool reads them in this order)
            extras.Clear();
            PollBrain();
        }

        /// <summary>The sample window's one call to action ("Make my own RC truck"): Build something with the sample's kind picked.</summary>
        public static string MakeOwnLabel(string sampleId) => OSProjects.MakeOwnLabel(sampleId);

        /// <summary>Old name of the sample's call to action; it is now <see cref="MakeOwnLabel"/> (a sample is never copied).</summary>
        public const string CopyLabel = "Make my own";

        /// <summary>The tag line of a sample's header, and of a project of the person's own.</summary>
        public const string SampleTagLine = "Sample · look around", OwnTagLine = "Your project";

        // 1 What it is, its test, where it stands.
        void BuildHeader(RectTransform card, bool sample, bool isOpen, string status)
        {
            JObject plan = isOpen ? NotebookStore.CurrentPlan : null;
            string goal = plan != null ? (string)plan["goal"] : null;
            string test = plan != null && plan["test"] != null && plan["test"].Type == JTokenType.String ? (string)plan["test"] : null;
            string follows = OSProjects.CopyOf(projectId);
            string tag = sample ? SampleTagLine
                : follows != null ? OwnTagLine + " · like the " + OSProjects.TitleFor(follows) + " sample"
                : OwnTagLine;
            OSUi.Tag(card, tag);
            string what = sample ? OSProjects.SampleShows(projectId) + "."
                : !string.IsNullOrWhiteSpace(goal) ? goal
                : "No plan written yet. Ask Albie to plan it with you.";
            TMP_Text lead = OSUi.Text(card, Clip(what, 120), OSUi.TitleText, !sample && string.IsNullOrWhiteSpace(goal));
            lead.fontStyle = FontStyles.Bold;
            if (!string.IsNullOrWhiteSpace(test)) OSUi.Text(card, "Test: " + Clip(test, 110), LineSize);
            OSUi.Text(card, sample ? OSProjects.SampleLine : "Now: " + status, NoteSize, true);
        }

        // 2 The one primary button, then the quiet ones.
        void BuildActionRow(RectTransform column, bool sample, string next)
        {
            RectTransform row = OSUi.Row(column, OSUi.ChipHeight, OSUi.MinGap);
            OSUi.Rigid(row);
            string nextLabel = next != null ? LabelOf(next) : null;
            if (sample)
            {
                string own = OSProjects.MakeOwnLabel(projectId);
                Chip(OSUi.Button(row, own, () => Do("make_own"), OSButtonStyle.Primary, "plus"));
                if (nextLabel != null && UnavailableOf(next) == null)
                    Chip(OSUi.Button(row, "Next: " + nextLabel, () => Do(next), OSButtonStyle.Secondary));
            }
            else if (nextLabel != null && UnavailableOf(next) == null)
            {
                Chip(OSUi.Button(row, "Next: " + nextLabel, () => Do(next), OSButtonStyle.Primary, IconOf(next)));
            }
            var spacer = new GameObject("Spacer", typeof(RectTransform));
            spacer.transform.SetParent(row, false);
            spacer.AddComponent<LayoutElement>().flexibleWidth = 1f;
            AddAction(row, "Notebook", "notebook", OSButtonStyle.Borderless, OSProjects.FindTool("notebook") == null ? "The notebook window couldn't be found." : null, "notebook");
            // Send drafts the project's latest result to its channel (rc-truck, quadcopter, rocket, image-classifier); the
            // message panel's Send / Cancel is the confirm, so nothing leaves the headset from this button.
            AddAction(row, "Send", "send", OSButtonStyle.Borderless, null, "send");
        }

        static void Chip(Button button)
        {
            if (button == null) return;
            var layout = button.GetComponent<LayoutElement>();
            OSUi.Size(button.transform, layout != null ? layout.preferredWidth : OSUi.ButtonMinWidth, OSUi.ChipHeight);
        }

        // 3 The loop as a stepper: what is done, what is next, what is off and why, and the last result.
        void BuildSteps(RectTransform card, string next)
        {
            OSUi.Tag(card, "Steps");
            RectTransform stepper = OSUi.Stepper(card);
            LoopSteps(scratchSteps);
            int current = -1;
            for (int i = 0; i < scratchSteps.Count; i++)
                if (scratchSteps[i] == next) { current = i; break; }
            for (int i = 0; i < scratchSteps.Count; i++)
            {
                string action = scratchSteps[i];
                string off = UnavailableOf(action);
                OSUi.StepState state = off != null ? OSUi.StepState.Locked
                    : i == current ? OSUi.StepState.Current
                    : current >= 0 && i < current ? OSUi.StepState.Done
                    : OSUi.StepState.Upcoming;
                string captured = action;
                OSUi.StepChip(stepper, i + 1, LabelOf(action), IconOf(action), state, () => DoIfOn(captured));
                steps.Add(new Step { label = LabelOf(action), action = action, unavailable = off });
                if (off != null) Hint(LabelOf(action), off);
            }
            for (int i = 0; i < hints.Count; i++) OSUi.Text(card, hints[i], NoteSize, true);
            if (kind == OSProjectKind.Truck) brainText = OSUi.Text(card, "", NoteSize, true);
            statusText = OSUi.Text(card, statusLine.Length > 0 ? statusLine : NextStepLine(), LeadSize);
        }

        readonly List<string> scratchSteps = new List<string>(6);

        // A tap on a chip: a step that is off does nothing (its reason is already under the stepper).
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
                case Loop.Truck: into.Add("course"); into.Add("drive"); into.Add("train"); into.Add("autopilot"); into.Add("code"); break;
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

        // 4 Where it stands, as a two-column summary.
        void BuildSummary(RectTransform card, bool isOpen)
        {
            OSUi.Tag(card, "Where it stands");
            JObject plan = isOpen ? NotebookStore.CurrentPlan : null;
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

        void AddAction(RectTransform row, string label, string action, OSButtonStyle style, string disabledReason, string icon)
        {
            Button button = OSUi.Button(row, label, () => { if (disabledReason == null) Do(action); }, style, icon);
            Chip(button);
            extras.Add(new Step { label = label, action = action, unavailable = disabledReason });
            if (disabledReason == null) return;
            if (button != null) button.interactable = false;
            Hint(label, disabledReason);
        }

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
                    if (DroneBrainProblem() == null) return HasTraining() ? "autopilot" : "train";
                    return "fly";
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
                case "design":
                    SetStatus("Opening CAD...");
                    OSProjects.OpenDesign(design, SetStatus);
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
