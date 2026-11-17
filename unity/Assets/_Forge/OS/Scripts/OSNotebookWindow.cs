using System.Collections.Generic;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The notebook window: what the open project is for and how it is going. It shows the plan (goal, test, milestones),
    /// the newest attempts with their outcomes, the project's versions and the lessons Albie has written down, all read
    /// from <see cref="NotebookStore"/>. It is read-only except for two buttons, "Mark worked" and "Mark failed", on the
    /// newest attempt that has no outcome yet. The window rebuilds itself when the store changes (its events), when the
    /// project changes, and when it is opened again; it does nothing per frame beyond a few integer comparisons.
    /// </summary>
    public sealed class OSNotebookWindow : MonoBehaviour
    {
        /// <summary>Window id; opening it again brings the existing window forward.</summary>
        public const string WindowId = "notebook";

        // How many of each list the window shows; a taller page than the window steps down one row of these (see CheckFit).
        static readonly int[] MilestoneCaps = { 5, 4, 3, 2 };
        static readonly int[] AttemptCaps = { 4, 3, 2, 1 };
        static readonly int[] VersionCaps = { 3, 2, 2, 1 };
        static readonly int[] LessonCaps = { 3, 2, 1, 1 };
        const float BodySize = OSUi.BodyText, SmallSize = OSUi.SmallText;
        static readonly Vector2 WindowSize = new Vector2(0.72f, 0.86f);  // metres

        // The last project name made, so the tile and the header don't build a string every time they ask.
        static string nameFor, nameText;

        ForgePanel panel;
        RectTransform column;
        bool dirty, fitPending;
        int level;  // 0 shows the most; each step shows fewer rows so the page fits the window (there is no scrolling)
        string notice;
        string shownProject;
        int shownEntries = -1, shownVersions = -1, shownPlans = -1, shownLessons = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            nameFor = null;
            nameText = null;
        }

        /// <summary>
        /// Opens the notebook window for the open project, or brings the existing one forward (and rereads). Null when
        /// this scene has no shell or no panel prefab.
        /// </summary>
        public static OSWindow Open()
        {
            OSShell shell = OSShell.Current;
            if (shell == null)
            {
                Debug.Log("[OS] The notebook needs the Albie menu, and this scene has none.");
                return null;
            }
            OSWindow window = shell.Windows.Open(WindowId, "Notebook", WindowSize, Build);
            if (window != null)
            {
                OSNotebookWindow view = window.GetComponent<OSNotebookWindow>();
                if (view != null) view.dirty = true;  // opened again or brought forward: read it all again
            }
            return window;
        }

        /// <summary>
        /// The words for a project: "RC truck" for the demo project, else its id made readable ("proj_quadcopter" becomes
        /// "Quadcopter"). The Projects list can use the same names.
        /// </summary>
        public static string ProjectName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            if (id == NotebookStore.DefaultProject) return "RC truck";
            if (id == nameFor) return nameText;
            string name = id.StartsWith("proj_") ? id.Substring(5) : id;
            name = name.Replace('_', ' ').Trim();
            if (name.Length == 0) name = id;
            name = char.ToUpperInvariant(name[0]) + name.Substring(1);
            nameFor = id;
            nameText = name;
            return name;
        }

        static void Build(ForgePanel forgePanel)
        {
            OSNotebookWindow view = forgePanel.gameObject.AddComponent<OSNotebookWindow>();
            view.Init(forgePanel);
        }

        void Init(ForgePanel forgePanel)
        {
            panel = forgePanel;
            RectTransform body = OSUi.Body(panel);
            column = OSUi.Column(body, 14f);
            Rebuild();
        }

        void OnEnable()
        {
            NotebookStore.EntryCreated += OnStoreChanged;
            NotebookStore.EntryUpdated += OnStoreChanged;
            NotebookStore.VersionCut += OnStoreChanged;
            NotebookStore.PlanUpdated += OnStoreChanged;
            if (column != null) dirty = true;  // back from being put away: events were missed meanwhile
        }

        void OnDisable()
        {
            NotebookStore.EntryCreated -= OnStoreChanged;
            NotebookStore.EntryUpdated -= OnStoreChanged;
            NotebookStore.VersionCut -= OnStoreChanged;
            NotebookStore.PlanUpdated -= OnStoreChanged;
        }

        void OnStoreChanged(JObject record) => dirty = true;

        void Update()
        {
            if (column == null) return;
            // A project switch or a reload of the same project changes no event, but it changes these.
            if (!dirty && NotebookStore.ProjectId == shownProject && NotebookStore.Entries.Count == shownEntries
                && NotebookStore.Versions.Count == shownVersions && NotebookStore.Plans.Count == shownPlans
                && NotebookStore.AllLessons.Count == shownLessons) return;
            level = 0;
            Rebuild();
        }

        // After a rebuild the layout has its sizes: if the page is taller than the window, show fewer rows and build again.
        void LateUpdate()
        {
            if (!fitPending || column == null) return;
            fitPending = false;
            var host = column.parent as RectTransform;
            if (host == null) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(column);
            if (LayoutUtility.GetPreferredHeight(column) <= host.rect.height - 6f || level >= AttemptCaps.Length - 1) return;
            level++;
            Rebuild();
        }

        // ---------------------------------------------------------------- building

        void Rebuild()
        {
            dirty = false;
            fitPending = true;
            shownProject = NotebookStore.ProjectId;
            shownEntries = NotebookStore.Entries.Count;
            shownVersions = NotebookStore.Versions.Count;
            shownPlans = NotebookStore.Plans.Count;
            shownLessons = NotebookStore.AllLessons.Count;

            OSUi.Clear(column);
            if (!NotebookStore.IsOpen)
            {
                panel.Subtitle = "";
                Line(column, "No project is open. Pick one on the Projects tab.", BodySize, true);
                return;
            }
            panel.Subtitle = ProjectName(NotebookStore.ProjectId);

            if (!string.IsNullOrEmpty(notice)) Line(column, notice, SmallSize, true);
            BuildPlan();
            BuildAttempts();
            BuildVersions();
            BuildLessons();
        }

        void BuildPlan()
        {
            JObject plan = NotebookStore.CurrentPlan;
            if (plan == null)
            {
                RectTransform empty = OSUi.Section(column, "Plan");
                Line(empty, "No plan yet. Ask Albie to write one with you.", BodySize, true);
                return;
            }
            RectTransform section = OSUi.Section(column, "Plan v" + ((int?)plan["version"] ?? 1));
            string goal = Clip((string)plan["goal"], 150);
            Line(section, goal.Length > 0 ? "Goal: " + goal : "No goal written yet.", BodySize, goal.Length == 0);
            string test = Clip((string)plan["test"], 150);
            if (test.Length > 0) Line(section, "Test: " + test, BodySize, false);

            var milestones = plan["milestones"] as JArray;
            if (milestones == null || milestones.Count == 0)
            {
                Line(section, "No milestones yet.", SmallSize, true);
                return;
            }
            int shown = Mathf.Min(MilestoneCaps[level], milestones.Count);
            for (int i = 0; i < shown; i++)
            {
                bool done = (bool?)milestones[i]["done"] == true;
                Line(section, (done ? "Done: " : "To do: ") + Clip((string)milestones[i]["text"], 100), BodySize, done);
            }
            if (milestones.Count > shown) Line(section, "and " + (milestones.Count - shown) + " more", SmallSize, true);
        }

        void BuildAttempts()
        {
            RectTransform section = OSUi.Section(column, "Recent attempts");
            IReadOnlyList<JObject> entries = NotebookStore.Entries;
            if (entries.Count == 0)
            {
                Line(section, "No attempts yet. Drive the truck or run a test and it shows up here.", BodySize, true);
                return;
            }

            // The newest attempt with no outcome yet comes first, with the two buttons that settle it.
            JObject waiting = NotebookStore.NewestOpen();
            if (waiting != null)
            {
                Line(section, "Waiting for your verdict", SmallSize, true);
                Line(section, Clip((string)waiting["change"], 110), BodySize, false);
                Line(section, Describe(waiting), SmallSize, true);
                string waitingId = (string)waiting["id"];
                RectTransform buttons = OSUi.Row(section, 48f, 16f);
                OSUi.Button(buttons, "Mark worked", () => MarkNewest(waitingId, "worked"), OSButtonStyle.Primary);
                OSUi.Button(buttons, "Mark failed", () => MarkNewest(waitingId, "failed"), OSButtonStyle.Secondary);
            }

            int shown = 0;
            for (int i = entries.Count - 1; i >= 0 && shown < AttemptCaps[level]; i--)
            {
                JObject entry = entries[i];
                if (ReferenceEquals(entry, waiting)) continue;
                Line(section, Clip((string)entry["change"], 110), BodySize, false);
                Line(section, Describe(entry), SmallSize, true);
                shown++;
            }
            if (waiting == null && entries.Count > shown) Line(section, "and " + (entries.Count - shown) + " older", SmallSize, true);
            else if (waiting != null && entries.Count - 1 > shown) Line(section, "and " + (entries.Count - 1 - shown) + " older", SmallSize, true);
        }

        void BuildVersions()
        {
            RectTransform section = OSUi.Section(column, "Versions");
            IReadOnlyList<JObject> versions = NotebookStore.Versions;
            if (versions.Count == 0)
            {
                Line(section, "No versions yet. One is cut when the project's design or code changes.", BodySize, true);
                return;
            }
            int shown = Mathf.Min(VersionCaps[level], versions.Count);
            for (int i = 0; i < shown; i++)
            {
                JObject version = versions[versions.Count - 1 - i];
                string summary = Clip((string)version["summary"], 90);
                Line(section, "v" + ((int?)version["version"] ?? 0) + "  ·  " + (summary.Length > 0 ? summary : "no summary"), BodySize, false);
            }
            if (versions.Count > shown) Line(section, "and " + (versions.Count - shown) + " older", SmallSize, true);
        }

        void BuildLessons()
        {
            RectTransform section = OSUi.Section(column, "Lessons");
            IReadOnlyList<Lesson> lessons = NotebookStore.Lessons(null, LessonCaps[level]);
            if (lessons.Count == 0)
            {
                Line(section, "No lessons yet. One is saved when an attempt is marked with what was learned.", BodySize, true);
                return;
            }
            for (int i = 0; i < lessons.Count; i++)
            {
                Lesson lesson = lessons[i];
                Line(section, Clip(lesson.Text, 120), BodySize, false);
                string[] tags = lesson.Tags;
                string from = lesson.FromVersion > 0 ? "from v" + lesson.FromVersion : "";
                string tagText = tags.Length > 0 ? string.Join(", ", tags) : "";
                string meta = from.Length > 0 && tagText.Length > 0 ? from + "  ·  " + tagText : from + tagText;
                if (meta.Length > 0) Line(section, meta, SmallSize, true);
            }
        }

        // ---------------------------------------------------------------- marking

        // A person's verdict on the newest open attempt. Marking is reversible (it can be marked again), so it needs no
        // confirm step; a refusal is shown in the window.
        void MarkNewest(string entryId, string outcome)
        {
            string error = NotebookStore.Mark(entryId, outcome, "person");
            if (error == null)
            {
                notice = null;
            }
            else
            {
                int at = error.IndexOf(": ", System.StringComparison.Ordinal);
                notice = "Couldn't mark it: " + (at >= 0 ? error.Substring(at + 2) : error);
                OSSounds.Error();
            }
            dirty = true;  // the store's event also says so; this covers a refusal
        }

        // ---------------------------------------------------------------- text helpers

        // "v2  ·  attempt  ·  no outcome yet" or "v2  ·  attempt  ·  failed (you marked it)".
        static string Describe(JObject entry)
        {
            string outcome = (string)entry["outcome"];
            string by = (string)entry["outcome_by"];
            string verdict;
            switch (outcome)
            {
                case "worked": verdict = "worked"; break;
                case "partly": verdict = "partly worked"; break;
                case "failed": verdict = "failed"; break;
                default: verdict = "no outcome yet"; break;
            }
            if (outcome == "worked" || outcome == "partly" || outcome == "failed")
            {
                if (by == "person") verdict += " (you marked it)";
                else if (by == "albie") verdict += " (Albie marked it)";
                else if (by == "run") verdict += " (from a run)";
            }
            return "v" + ((int?)entry["project_version"] ?? 0) + "  ·  " + ((string)entry["kind"] ?? "attempt") + "  ·  " + verdict;
        }

        // One line of text in the window's own style: no rich-text parsing (the words come from people and Albie), wrapped.
        static TMP_Text Line(Transform parent, string text, float size, bool secondary)
        {
            TMP_Text label = OSUi.Text(parent, text, size, secondary);
            if (label == null) return null;
            label.richText = false;
            label.textWrappingMode = TextWrappingModes.Normal;
            return label;
        }

        static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Trim();
            if (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0) text = text.Replace("\r", "").Replace('\n', ' ');
            return text.Length <= max ? text : text.Substring(0, max - 3).TrimEnd() + "...";
        }
    }
}
