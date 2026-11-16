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
    /// The Project window (window id "project"): the open project's plan card (goal, test, milestones), its versions, the
    /// artifacts linked to it with their latest run, and a row of Continue actions. For the truck the actions are the
    /// demo beats as buttons: Course, Drive, Train, Autopilot, Code and Send. Other projects get what makes sense for
    /// them (CAD, Train, Notebook). Every action shows the tool's real result, or its error as a plain sentence, in the
    /// status line at the bottom. The window follows the open notebook project and re-reads when the notebook changes.
    /// </summary>
    public sealed class OSProjectWindow : MonoBehaviour
    {
        public const string WindowId = "project";

        static readonly Vector2 SizeMeters = new Vector2(0.74f, 0.66f);
        const float PollSeconds = 0.5f;
        const int MaxMilestones = 4, MaxVersions = 3, MaxArtifacts = 3;
        const float LineSize = 20f, NoteSize = 18f, LeadSize = 22f;  // OS units: plan and result lines, hints, the lines people read first

        ForgePanel panel;
        string projectId;
        OSProjectKind kind;
        string design;
        bool dirty;
        string statusLine = "";
        TMP_Text statusText, brainText;
        float nextPoll;
        int brainKey = int.MinValue;
        readonly List<OSProjects.LinkedArtifact> linked = new List<OSProjects.LinkedArtifact>();
        readonly List<string> hints = new List<string>();

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
            if (id != projectId) statusLine = "";
            projectId = id;
            kind = OSProjects.KindOf(id);
            design = OSProjects.DesignOf(id);
            panel.Title = OSProjects.TitleFor(id);
            Rebuild();
        }

        /// <summary>What opening a project does by itself: a CAD starter opens its design, the ML project opens Train.</summary>
        public void RunOpenAction()
        {
            if (kind == OSProjectKind.Cad) Do("design");
            else if (kind == OSProjectKind.Ml) Do("train");
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
        }

        // ---------------------------------------------------------------- building the window

        void Rebuild()
        {
            dirty = false;
            statusText = null;
            brainText = null;
            brainKey = int.MinValue;
            hints.Clear();

            RectTransform body = OSUi.Body(panel);
            if (body == null) return;
            OSUi.Clear(body);
            RectTransform column = OSUi.Column(body, 14f);

            OSProjects.LinkedArtifacts(projectId, linked);
            panel.Subtitle = OSProjects.ComputeStatus(projectId, linked);
            bool isOpen = OSProjects.IsOpen(projectId);

            BuildPlan(OSUi.Section(column, PlanHeading(isOpen)), isOpen);
            BuildVersions(OSUi.Section(column, "Versions"), isOpen);
            BuildArtifacts(OSUi.Section(column, "Artifacts"));
            BuildActions(OSUi.Section(column, "Continue"));
            PollBrain();
        }

        static string PlanHeading(bool isOpen)
        {
            JObject plan = isOpen ? NotebookStore.CurrentPlan : null;
            int? version = plan != null ? (int?)plan["version"] : null;
            return version.HasValue ? "Plan v" + version.Value : "Plan";
        }

        void BuildPlan(RectTransform section, bool isOpen)
        {
            JObject plan = isOpen ? NotebookStore.CurrentPlan : null;
            if (plan == null)
            {
                OSUi.Text(section, "No plan written yet. Tap Build something to plan this project with Albie.", NoteSize, true);
                return;
            }
            string goal = (string)plan["goal"];
            string test = (string)plan["test"];
            OSUi.Text(section, "Goal: " + Clip(string.IsNullOrEmpty(goal) ? "not set" : goal, 140), LeadSize);
            if (!string.IsNullOrEmpty(test)) OSUi.Text(section, "Test: " + Clip(test, 140), LeadSize);

            var milestones = plan["milestones"] as JArray;
            if (milestones == null || milestones.Count == 0) return;
            int shown = Mathf.Min(milestones.Count, MaxMilestones);
            for (int i = 0; i < shown; i++)
            {
                var item = milestones[i] as JObject;
                if (item == null) continue;
                bool done = (bool?)item["done"] == true;
                OSUi.Text(section, (done ? "[x] " : "[ ] ") + Clip((string)item["text"] ?? "", 90), LineSize, done);
            }
            if (milestones.Count > shown) OSUi.Text(section, "and " + (milestones.Count - shown) + " more", NoteSize, true);
        }

        void BuildVersions(RectTransform section, bool isOpen)
        {
            var versions = isOpen ? NotebookStore.Versions : null;
            if (versions == null || versions.Count == 0)
            {
                OSUi.Text(section, "No versions yet. One is cut each time the design or the code changes.", NoteSize, true);
                return;
            }
            int shown = Mathf.Min(versions.Count, MaxVersions);
            for (int i = versions.Count - 1; i >= versions.Count - shown; i--)
            {
                JObject v = versions[i];
                int number = (int?)v["version"] ?? 0;
                string summary = (string)v["summary"];
                OSUi.Text(section, "v" + number + " · " + Clip(summary ?? "", 64) + " · " + OutcomeOfVersion(number), LineSize, i != versions.Count - 1);
            }
            if (versions.Count > shown) OSUi.Text(section, "and " + (versions.Count - shown) + " earlier", NoteSize, true);
        }

        static string OutcomeOfVersion(int version)
        {
            var all = NotebookStore.Entries;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                if ((int?)all[i]["project_version"] != version || (string)all[i]["kind"] != "attempt") continue;
                string outcome = (string)all[i]["outcome"];
                return string.IsNullOrEmpty(outcome) || outcome == "open" ? "no result yet" : outcome;
            }
            return "no attempt yet";
        }

        void BuildArtifacts(RectTransform section)
        {
            if (linked.Count == 0)
            {
                OSUi.Text(section, kind == OSProjectKind.Truck
                    ? "Nothing linked yet. Tap Course to make the slalom for this project."
                    : "Nothing linked yet. Artifacts made while this project is open link to it.", NoteSize, true);
                return;
            }
            int shown = Mathf.Min(linked.Count, MaxArtifacts);
            for (int i = 0; i < shown; i++)
            {
                var a = linked[i];
                OSUi.Text(section, Clip(a.title, 40) + " v" + a.version + " · " + RunLine(a), LeadSize);
            }
            if (linked.Count > shown) OSUi.Text(section, "and " + (linked.Count - shown) + " more", NoteSize, true);
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

        void BuildActions(RectTransform section)
        {
            RectTransform row = OSUi.Row(section, 48f, 19f);
            switch (kind)
            {
                case OSProjectKind.Truck:
                    AddAction(row, "Course", "course", OSButtonStyle.Primary);
                    AddAction(row, "Drive", "drive", OSButtonStyle.Secondary, OSProjects.FindTool("drive") == null ? DriveMissing : null);
                    AddAction(row, "Train", "train", OSButtonStyle.Secondary);
                    AddAction(row, "Autopilot", "autopilot", OSButtonStyle.Secondary);
                    AddAction(row, "Code", "code", OSButtonStyle.Secondary);
                    AddAction(row, "Send", "send", OSButtonStyle.Secondary, OSProjects.SendUnavailable);
                    break;
                case OSProjectKind.Cad:
                    AddAction(row, "Open design", "design", OSButtonStyle.Primary);
                    break;
                case OSProjectKind.Ml:
                    AddAction(row, "Train", "train", OSButtonStyle.Primary);
                    AddAction(row, "Load dataset", "dataset", OSButtonStyle.Secondary, ForgeCloud.Serves("/ml/") ? null : OSProjects.NeedsCloud);
                    break;
                default:
                    AddAction(row, "CAD", "cad", OSButtonStyle.Secondary);
                    AddAction(row, "Train", "train", OSButtonStyle.Secondary);
                    break;
            }
            AddAction(row, "Notebook", "notebook", OSButtonStyle.Secondary, OSProjects.FindTool("notebook") == null ? "The notebook window couldn't be found." : null);

            for (int i = 0; i < hints.Count; i++) OSUi.Text(section, hints[i], NoteSize, true);
            if (kind == OSProjectKind.Truck) brainText = OSUi.Text(section, "", NoteSize, true);
            statusText = OSUi.Text(section, statusLine.Length > 0 ? statusLine : "Tap a step to continue.", LeadSize);
        }

        void AddAction(RectTransform row, string label, string action, OSButtonStyle style, string disabledReason = null)
        {
            Button button = OSUi.Button(row, label, () => Do(action), style);
            if (disabledReason == null) return;
            if (button != null) button.interactable = false;
            hints.Add(label + ": " + disabledReason);
        }

        // ---------------------------------------------------------------- actions

        /// <summary>
        /// Runs one Continue action by id (course, drive, train, autopilot, code, send, design, dataset, cad, notebook), shows
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
                case "train": return DoTrain();
                case "autopilot": return DoAutopilot();
                case "code": return OpenMode("code", "Code");
                case "cad": return OpenModeOrTool("cad", "cad", "CAD");
                case "send": return OSProjects.SendUnavailable;
                case "notebook": return OpenToolLine(OSProjects.FindTool("notebook"), "Notebook");
                case "design":
                    SetStatus("Opening CAD...");
                    OSProjects.OpenDesign(design, SetStatus);
                    return null;
                case "dataset": return DoDataset();
                default: return "I don't know that step.";
            }
        }

        string DoCourse()
        {
            // The project must be open first: the slalom only links to the truck while a notebook is open.
            if (!OSProjects.EnsureOpen(projectId, out string error)) return error;

            ArtifactHost current = ArtifactHost.Current;
            if (current != null && current.OpenId == "slalom" && BrainLoops.Active is SlalomBrain && LinksTruckForThisProject(current))
                return "The slalom is already open, with the brain truck at the start.";

            OSProjects.LinkedArtifacts(projectId, linked);
            OSProjects.LinkedArtifact slalom = null;
            for (int i = 0; i < linked.Count; i++)
                if (linked[i].template == "slalom" || linked[i].id == "slalom") { slalom = linked[i]; break; }

            JObject result = slalom != null
                ? AlbieTools.Call("artifact_open", new JObject { ["id"] = slalom.id })
                : AlbieTools.Call("artifact_create", new JObject { ["template"] = "slalom" });
            return DescribeCourse(result);
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
                return "The slalom asks first." + what + " Tap Allow on the card in front of you.";
            }
            if (status != "open") return "The slalom didn't open.";

            ArtifactHost host = ArtifactHost.Current;
            string thing = host != null && host.OpenManifest != null ? (string)host.OpenManifest["links"]?["thing"] : null;
            string text;
            if (thing != "truck") text = "The slalom is open, but it isn't linked to the truck, so no brain truck came with it.";
            else if (BrainLoops.Active is SlalomBrain) text = "The slalom is open. The brain truck is at the start.";
            else text = "The slalom is open, but the brain truck didn't appear.";
            string say = (string)opened["say"];
            return string.IsNullOrWhiteSpace(say) ? text : text + " " + say.Trim();
        }

        string DoDrive()
        {
            OSEntry drive = OSProjects.FindTool("drive");
            if (drive == null) return DriveMissing;
            if (!OSProjects.OpenTool(drive, out string error)) return error;

            var brain = BrainLoops.Active as SlalomBrain;
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
            string toolError = OpenTrainTool();
            if (kind != OSProjectKind.Truck)
            {
                if (toolError != null) return toolError;
                if (kind == OSProjectKind.Ml)
                    return ForgeCloud.Serves("/ml/") ? "Train is open. Tap Load dataset to open the data." : OSProjects.NeedsCloud;
                return "Train is open.";
            }

            if (BrainLoops.Active == null) return "Open the course first. The brain learns from laps on the slalom.";
            JObject result = AlbieTools.Call("brain_train", new JObject());
            string error = (string)result["error"];
            if (error != null) return OSProjects.Plain(error);
            int laps = (int?)result["completed_laps"] ?? 0;
            return laps > 0 ? "Training the brain on " + laps + " laps. It takes a few seconds." : "Training the brain. It takes a few seconds.";
        }

        string DoAutopilot()
        {
            var brain = BrainLoops.Active as SlalomBrain;
            if (brain == null) return "Open the course first. The Autopilot drives the slalom.";
            bool stop = brain.State == SlalomBrain.Mode.Autopilot;
            JObject result = AlbieTools.Call(stop ? "autopilot_off" : "autopilot_on", new JObject());
            string error = (string)result["error"];
            if (error != null) return OSProjects.Plain(error);
            return stop ? "The Autopilot stopped. The truck is yours again." : "The Autopilot is driving the slalom.";
        }

        string DoDataset()
        {
            if (!ForgeCloud.Serves("/ml/")) return OSProjects.NeedsCloud;
            SetStatus("Asking Albie's cloud to open the project...");
            AlbieTools.CallAsync("ml_open", new JObject(), result =>
            {
                string error = result != null ? (string)result["error"] : null;
                SetStatus(error != null ? OSProjects.Plain(error) : "The cloud opened the project on the Train tool.");
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
            var brain = BrainLoops.Active as SlalomBrain;
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
