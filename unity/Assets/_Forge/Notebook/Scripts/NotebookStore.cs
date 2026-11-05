using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Forge.Artifacts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Notebook
{
    /// <summary>
    /// The lab notebook (C-17, docs/NOTEBOOK_CONTRACT.md): the engineering log Albie keeps for a project, on the device
    /// with no network. One project is open at a time. Entries, project versions and lessons live in memory and are
    /// written one file per record under <see cref="Root"/> through <see cref="NotebookFiles"/>, so the notebook
    /// survives a restart and a crash loses at most the record being written.
    ///
    /// Everything here runs on the main thread. Errors come back as "code: message" strings (C-17 section 9), so the
    /// agent's tools can pass them on unchanged.
    /// </summary>
    public static class NotebookStore
    {
        public const string EntrySchema = "albie.notebook.entry/1";
        public const string VersionSchema = "albie.project_version/1";
        public const string LessonSchema = "albie.lesson/1";
        public const string PlanSchema = "albie.plan/1";
        /// <summary>The demo project, opened at Play start until the Project module (C-09) opens projects itself.</summary>
        public const string DefaultProject = "proj_truck";

        /// <summary>The closed tag list (C-17 section 5). Changing it is a small contract PR.</summary>
        public static readonly string[] Tags = { "vehicle", "aircraft", "rocket", "stability", "control", "power", "ml", "build" };
        public static readonly string[] Kinds = { "attempt", "training", "postmortem" };
        public static readonly string[] Outcomes = { "worked", "partly", "failed" };

        static readonly List<JObject> entries = new List<JObject>();
        static readonly List<JObject> versions = new List<JObject>();
        static readonly List<JObject> lessons = new List<JObject>();
        static readonly List<JObject> plans = new List<JObject>();
        static readonly List<string> problems = new List<string>();
        static int nextEntry = 1, nextLesson = 1, nextPlan = 1;

        /// <summary>The clock for timestamps (UTC). Checks replace it to get fixed times.</summary>
        public static Func<DateTime> Clock = () => DateTime.UtcNow;

        /// <summary>A new entry was created (the full entry).</summary>
        public static event Action<JObject> EntryCreated;
        /// <summary>An entry changed: an outcome, a lesson, a run or an incident.</summary>
        public static event Action<JObject> EntryUpdated;
        /// <summary>A project version was cut (the manifest).</summary>
        public static event Action<JObject> VersionCut;
        /// <summary>A plan was written (the new <c>albie.plan/1</c> record).</summary>
        public static event Action<JObject> PlanUpdated;

        public static string ProjectId { get; private set; }
        public static string Root { get; private set; }
        public static bool IsOpen => ProjectId != null;
        public static IReadOnlyList<JObject> Entries => entries;
        public static IReadOnlyList<JObject> Versions => versions;
        public static IReadOnlyList<JObject> Plans => plans;
        /// <summary>The newest plan, or null before the first one.</summary>
        public static JObject CurrentPlan => plans.Count == 0 ? null : plans[plans.Count - 1];
        /// <summary>Every lesson, hidden ones too. <see cref="Lessons"/> is what Albie reads.</summary>
        public static IReadOnlyList<JObject> AllLessons => lessons;
        /// <summary>Files that wouldn't load at the last <see cref="Open"/>, moved to <c>damaged/</c>.</summary>
        public static IReadOnlyList<string> LoadProblems => problems;

        public static string DefaultRoot => Application.persistentDataPath;
        public static string ProjectFolder => Path.Combine(Root, "projects", ProjectId);
        static string NotebookFolder => Path.Combine(ProjectFolder, "notebook");
        static string VersionsFolder => Path.Combine(ProjectFolder, "versions");
        static string PlansFolder => Path.Combine(ProjectFolder, "plans");
        static string LessonsPath => Path.Combine(Root, "user", "lessons.json");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Clear();
            Root = null;
            EntryCreated = null;
            EntryUpdated = null;
            VersionCut = null;
            PlanUpdated = null;
            Clock = () => DateTime.UtcNow;
        }

        // ---- Open and close -------------------------------------------------------------------------------------

        /// <summary>
        /// Opens <paramref name="projectId"/>'s notebook from <paramref name="root"/> (default <see cref="DefaultRoot"/>),
        /// after writing out anything still queued. A file that won't parse is moved to <c>damaged/</c> and listed in
        /// <see cref="LoadProblems"/>; the rest still opens.
        /// </summary>
        public static void Open(string projectId, string root = null)
        {
            if (string.IsNullOrEmpty(projectId) || projectId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"bad_value: project id \"{projectId}\" can't be a folder name");
            NotebookFiles.Flush();
            Clear();
            Root = root ?? DefaultRoot;
            ProjectId = projectId;

            nextEntry = 1 + Math.Max(LoadFolder(NotebookFolder, "ent_", EntrySchema, entries), HighestId(Path.Combine(NotebookFolder, "damaged"), "ent_"));
            entries.Sort((a, b) => IdNumber((string)a["id"], "ent_").CompareTo(IdNumber((string)b["id"], "ent_")));
            LoadFolder(VersionsFolder, "v", VersionSchema, versions);
            versions.Sort((a, b) => ((int)a["version"]).CompareTo((int)b["version"]));
            LoadFolder(PlansFolder, "plan_v", PlanSchema, plans);
            plans.Sort((a, b) => ((int)a["version"]).CompareTo((int)b["version"]));
            nextPlan = 1 + (plans.Count == 0 ? 0 : (int)plans[plans.Count - 1]["version"]);
            LoadLessons();
            foreach (string problem in problems) Debug.LogWarning("[Notebook] " + problem);
        }

        /// <summary>Writes out anything queued and closes the project. The next <see cref="Open"/> reads it all back.</summary>
        public static void Close()
        {
            NotebookFiles.Flush();
            Clear();
        }

        /// <summary>Waits until every queued write is on disk. Called on quit; checks call it before reading files.</summary>
        public static bool Flush() => NotebookFiles.Flush();

        static void Clear()
        {
            ProjectId = null;
            entries.Clear();
            versions.Clear();
            lessons.Clear();
            plans.Clear();
            problems.Clear();
            nextEntry = nextLesson = nextPlan = 1;
        }

        static int LoadFolder(string folder, string prefix, string schema, List<JObject> into)
        {
            int highest = 0;
            if (!Directory.Exists(folder)) return 0;
            foreach (string path in Directory.GetFiles(folder, "*.json"))
            {
                highest = Math.Max(highest, IdNumber(Path.GetFileNameWithoutExtension(path), prefix));
                string why = null;
                JObject record = null;
                try
                {
                    record = NotebookFiles.Parse(File.ReadAllText(path)) as JObject;
                    if (record == null) why = "not a JSON object";
                    else if ((string)record["schema"] != schema) why = $"schema is \"{record["schema"]}\", not {schema}";
                    else if (schema == EntrySchema && IdNumber((string)record["id"], "ent_") <= 0) why = "no valid id";
                    else if (schema == VersionSchema && record["version"]?.Type != JTokenType.Integer) why = "no version number";
                    else if (schema == PlanSchema && record["version"]?.Type != JTokenType.Integer) why = "no version number";
                }
                catch (Exception e) when (e is JsonException || e is IOException) { why = e.Message; }
                if (why == null)
                {
                    into.Add(record);
                    continue;
                }
                string moved;
                try { moved = NotebookFiles.MoveToDamaged(path); }
                catch (IOException e) { moved = $"(couldn't move it: {e.Message})"; }
                problems.Add($"{Path.GetFileName(path)} didn't load ({why}); moved to {moved}");
            }
            return highest;
        }

        static int HighestId(string folder, string prefix)
        {
            int highest = 0;
            if (!Directory.Exists(folder)) return 0;
            foreach (string path in Directory.GetFiles(folder, "*.json"))
                highest = Math.Max(highest, IdNumber(Path.GetFileNameWithoutExtension(path), prefix));
            return highest;
        }

        static void LoadLessons()
        {
            if (!File.Exists(LessonsPath)) return;
            try
            {
                if (!(NotebookFiles.Parse(File.ReadAllText(LessonsPath)) is JArray array)) throw new JsonReaderException("not a JSON array");
                foreach (var token in array)
                    if (token is JObject lesson && (string)lesson["schema"] == LessonSchema && IdNumber((string)lesson["id"], "les_") > 0)
                    {
                        lessons.Add(lesson);
                        nextLesson = Math.Max(nextLesson, IdNumber((string)lesson["id"], "les_") + 1);
                    }
                    else problems.Add($"lessons.json: skipped a record that isn't a lesson ({Clip(token.ToString(Formatting.None), 60)})");
            }
            catch (Exception e) when (e is JsonException || e is IOException)
            {
                string moved;
                try { moved = NotebookFiles.MoveToDamaged(LessonsPath); }
                catch (IOException io) { moved = $"(couldn't move it: {io.Message})"; }
                problems.Add($"lessons.json didn't load ({e.Message}); moved to {moved}");
            }
        }

        // ---- Versions (C-17 section 4) --------------------------------------------------------------------------

        /// <summary>The open project's newest version, or 0 before the first cut. Any other project is read from disk.</summary>
        public static int CurrentVersion(string projectId)
        {
            if (projectId == ProjectId) return versions.Count == 0 ? 0 : (int)versions[versions.Count - 1]["version"];
            string folder = Path.Combine(Root ?? DefaultRoot, "projects", projectId ?? "", "versions");
            return HighestId(folder, "v");
        }

        public static int CurrentVersion() => CurrentVersion(ProjectId);

        /// <summary>
        /// Cuts a project version: a Thing spec change (<c>thing</c>) or a commit (<c>commit</c>), nothing else (4.1).
        /// The manifest pins what it's given and carries everything else over from the version before. Opens a new
        /// <c>attempt</c> with <paramref name="summary"/> as its change. Returns the new version number.
        /// </summary>
        public static int CutVersion(string cutBy, string summary, JObject pins = null, string rationale = null, string rationaleBy = null)
        {
            Require();
            if (cutBy != "thing" && cutBy != "commit") throw new ArgumentException($"bad_value: cut_by \"{cutBy}\": use thing or commit");
            if (string.IsNullOrWhiteSpace(summary)) throw new ArgumentException("bad_value: a version needs a summary");
            var previous = versions.Count == 0 ? null : versions[versions.Count - 1];
            int version = previous == null ? 1 : (int)previous["version"] + 1;
            var manifest = new JObject
            {
                ["schema"] = VersionSchema,
                ["project_id"] = ProjectId,
                ["version"] = version,
                ["parent"] = previous == null ? JValue.CreateNull() : previous["version"],
                ["cut_by"] = cutBy,
                ["summary"] = summary.Trim(),
            };
            foreach (string key in new[] { "things", "controllers", "artifacts", "cad", "repos", "datasets", "checkpoints" })
            {
                var map = previous?[key] is JObject old ? (JObject)old.DeepClone() : new JObject();
                if (pins?[key] is JObject given) foreach (var pin in given) map[pin.Key] = pin.Value.DeepClone();
                manifest[key] = map;
            }
            manifest["created_at"] = Now();
            versions.Add(manifest);
            NotebookFiles.Write(Path.Combine(VersionsFolder, $"v{version:0000}.json"), manifest.ToString(Formatting.Indented));
            VersionCut?.Invoke(manifest);
            Emit("project.version", new JObject { ["project_id"] = ProjectId, ["version"] = version, ["cut_by"] = cutBy, ["summary"] = manifest["summary"] });
            Log("attempt", summary, rationale, rationaleBy, null, null);
            return version;
        }

        // ---- Plans (C-17 section 6, plan_revise) ----------------------------------------------------------------

        /// <summary>
        /// Writes a postmortem and a new plan together. The old plan is kept. <paramref name="goal"/>, <paramref name="test"/>
        /// and <paramref name="milestones"/> copy forward when omitted. A project with no plan yet needs a goal.
        /// Returns the new plan version, or throws "code: message".
        /// </summary>
        public static int RevisePlan(string reason, string goal = null, string test = null, JArray milestones = null)
        {
            Require();
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("bad_value: say why the plan is changing");
            var previous = CurrentPlan;
            if (previous == null && string.IsNullOrWhiteSpace(goal))
                throw new ArgumentException("bad_value: a new plan needs a goal");
            int version = previous == null ? 1 : (int)previous["version"] + 1;
            string postmortem = Log("postmortem",
                previous == null ? "Wrote the plan." : "Plan revised: " + reason.Trim(),
                reason.Trim(), "albie", null, new[] { "build" });
            var plan = new JObject
            {
                ["schema"] = PlanSchema,
                ["project_id"] = ProjectId,
                ["version"] = version,
                ["goal"] = string.IsNullOrWhiteSpace(goal) ? previous["goal"] : goal.Trim(),
                ["parts"] = previous?["parts"] is JArray parts ? parts.DeepClone() : new JArray(),
                ["test"] = string.IsNullOrWhiteSpace(test) ? (previous?["test"] ?? JValue.CreateNull()) : (JToken)test.Trim(),
                ["milestones"] = milestones != null ? (JArray)milestones.DeepClone() : previous?["milestones"] is JArray old ? (JArray)old.DeepClone() : new JArray(),
                ["supersedes"] = previous == null ? JValue.CreateNull() : previous["version"],
                ["postmortem"] = postmortem,
                ["created_at"] = Now(),
            };
            nextPlan = version + 1;
            plans.Add(plan);
            NotebookFiles.Write(Path.Combine(PlansFolder, $"plan_v{version:0000}.json"), plan.ToString(Formatting.Indented));
            PlanUpdated?.Invoke(plan);
            Emit("plan.updated", new JObject { ["project_id"] = ProjectId, ["version"] = version });
            return version;
        }

        // ---- Entries (C-17 sections 2 and 3) --------------------------------------------------------------------

        public static JObject Entry(string id)
        {
            foreach (var entry in entries) if ((string)entry["id"] == id) return entry;
            return null;
        }

        /// <summary>The newest entry with no outcome yet, or null.</summary>
        public static JObject NewestOpen()
        {
            for (int i = entries.Count - 1; i >= 0; i--) if ((string)entries[i]["outcome"] == "open") return entries[i];
            return null;
        }

        public static JObject Newest() => entries.Count == 0 ? null : entries[entries.Count - 1];

        /// <summary>Opens a new entry on the current project version. Returns its id. Bad input throws "code: message".</summary>
        public static string Log(string kind, string change, string rationale, string rationaleBy, string expected, string[] tags)
        {
            Require();
            if (Array.IndexOf(Kinds, kind) < 0) throw new ArgumentException($"bad_value: kind \"{kind}\": use attempt, training or postmortem");
            if (string.IsNullOrWhiteSpace(change)) throw new ArgumentException("bad_value: say what changed");
            if (rationaleBy != null && rationaleBy != "person" && rationaleBy != "albie")
                throw new ArgumentException($"bad_value: rationale_by \"{rationaleBy}\": use person or albie");
            string tagError = CheckTags(tags);
            if (tagError != null) throw new ArgumentException(tagError);

            string now = Now();
            var entry = new JObject
            {
                ["schema"] = EntrySchema,
                ["id"] = $"ent_{nextEntry++:0000}",
                ["project_id"] = ProjectId,
                ["project_version"] = CurrentVersion(),
                ["kind"] = kind,
                ["change"] = change.Trim(),
                ["rationale"] = Text(rationale),
                ["rationale_by"] = string.IsNullOrWhiteSpace(rationale) ? JValue.CreateNull() : (JToken)(rationaleBy ?? "albie"),
                ["expected"] = Text(expected),
                ["observed"] = new JObject { ["runs"] = new JArray(), ["incidents"] = new JArray(), ["training"] = null, ["note"] = null },
                ["outcome"] = "open",
                ["outcome_by"] = null,
                ["lesson"] = null,
                ["lesson_id"] = null,
                ["tags"] = new JArray(Distinct(tags)),
                ["created_at"] = now,
                ["updated_at"] = now,
            };
            entries.Add(entry);
            Save(entry);
            EntryCreated?.Invoke(entry);
            Emit("notebook.entry", entry);
            return (string)entry["id"];
        }

        /// <summary>
        /// Sets an entry's outcome, and a note and a lesson if given. Returns null, or why it was refused.
        /// A person's mark always wins: a run or Albie never changes a person's outcome, and a run never changes
        /// Albie's. Marking the same outcome again only adds the note or lesson ("that didn't work", then Albie asks
        /// one question and writes the lesson).
        /// </summary>
        /// <param name="by"><c>person</c>, <c>albie</c> or <c>run</c>: who judged the outcome.</param>
        /// <param name="lessonBy">Who wrote the lesson's words. Albie writes them from what the person said.</param>
        public static string Mark(string entryId, string outcome, string by, string note = null, string lesson = null,
                                  string[] tags = null, string lessonBy = "albie")
        {
            if (!IsOpen) return "no_project: no project is open";
            var entry = Entry(entryId);
            if (entry == null) return $"unknown_entry: no entry {entryId} in this project";
            if (Array.IndexOf(Outcomes, outcome) < 0) return $"bad_outcome: outcome \"{outcome}\": use worked, partly or failed";
            if (Rank(by) < 0) return $"bad_value: by \"{by}\": use person, albie or run";
            if (by == "run" && outcome == "partly") return "bad_outcome: a run can't judge partly; only a person or Albie can";
            string tagError = CheckTags(tags);
            if (tagError != null) return tagError;
            lesson = string.IsNullOrWhiteSpace(lesson) ? null : lesson.Trim();
            if (lesson != null && by == "run") return "bad_value: a run can't write a lesson";
            var lessonTags = tags != null && tags.Length > 0 ? tags : ToArray(entry["tags"]);
            if (lesson != null && lessonTags.Length == 0) return "lesson_needs_tag: a lesson needs at least one tag";

            string current = (string)entry["outcome"], currentBy = (string)entry["outcome_by"];
            if (current != "open" && current != outcome && Rank(by) < Rank(currentBy))
                return currentBy == "person"
                    ? $"person_marked: a person marked {entryId} {current}, so {(by == "run" ? "a run" : "Albie")} can't change it"
                    : $"albie_marked: Albie marked {entryId} {current}, so a run can't change it";

            if (current == "open" || current != outcome || Rank(by) > Rank(currentBy))
            {
                entry["outcome"] = outcome;
                entry["outcome_by"] = by;
            }
            if (Text(note) is JValue noteText) entry["observed"]["note"] = noteText;
            if (tags != null) foreach (string tag in tags) AddTag(entry, tag);
            if (lesson != null) WriteLesson(entry, lesson, lessonTags, lessonBy == "person" ? "person" : "albie");
            Touch(entry);
            return null;
        }

        /// <summary>
        /// Called by the artifact host when a run ends (C-12): adds the run id to the newest entry on this version that
        /// is still open or was only judged by a run, or opens a new attempt if there is none. <c>completed</c>
        /// suggests <c>worked</c>, <c>failed</c> suggests <c>failed</c>, and <c>aborted</c> changes nothing.
        /// Returns the entry id.
        /// </summary>
        public static string AttachRun(string runId, string runOutcome)
        {
            if (!IsOpen) { Debug.LogWarning($"[Notebook] run {runId} ended with no project open; not logged"); return null; }
            if (string.IsNullOrEmpty(runId)) throw new ArgumentException("bad_value: a run needs its run_id");
            var entry = Target() ?? Entry(Log("attempt", $"Ran {runId} on v{CurrentVersion()}.", null, null, null, null));
            var runs = (JArray)entry["observed"]["runs"];
            bool known = false;
            foreach (var run in runs) known |= (string)run == runId;
            if (!known) runs.Add(runId);
            if (runOutcome == "completed" || runOutcome == "failed")
            {
                string refused = Mark((string)entry["id"], runOutcome == "completed" ? "worked" : "failed", "run");
                if (refused == null) return (string)entry["id"];  // Mark saved it
            }
            Touch(entry);
            return (string)entry["id"];
        }

        /// <summary>
        /// A Thing incident (the proposed <c>thing.incident</c>, C-17 question 1), added to the same entry a run would be.
        /// It doesn't change the outcome on its own. Returns the entry id.
        /// </summary>
        public static string AddIncident(JObject incident)
        {
            Require();
            if (incident == null || string.IsNullOrEmpty((string)incident["kind"])) throw new ArgumentException("bad_value: an incident needs a kind");
            var entry = Target() ?? Entry(Log("attempt", $"Driving v{CurrentVersion()}.", null, null, null, null));
            var copy = (JObject)incident.DeepClone();
            copy.Remove("project_id");
            if (copy["at"] == null) copy["at"] = Now();
            ((JArray)entry["observed"]["incidents"]).Add(copy);
            Touch(entry);
            return (string)entry["id"];
        }

        /// <summary>
        /// Called when a training run ends (ML A, through <c>Forge.Controllers.TrainingLog</c>): a <c>training</c> entry
        /// on the current version with the controller's training block copied as it is (C-19 section 3.4).
        /// Returns the entry id, or null when no project is open.
        /// </summary>
        public static string LogTraining(string controller, int controllerVersion, JObject training)
        {
            if (!IsOpen) { Debug.LogWarning($"[Notebook] {controller} v{controllerVersion} trained with no project open; not logged"); return null; }
            var block = training == null ? new JObject() : (JObject)training.DeepClone();
            block["controller"] = controller;
            block["controller_version"] = controllerVersion;
            int laps = block["laps"] is JArray lapList ? lapList.Count : block["laps"]?.Type == JTokenType.Integer ? (int)block["laps"] : 0;
            string id = Log("training", laps > 0 ? $"Trained {controller} on {laps} {(laps == 1 ? "lap" : "laps")}." : $"Trained {controller}.",
                            "Learn to drive from the recorded laps.", "albie", "The Autopilot drives the course.", new[] { "ml", "control" });
            var entry = Entry(id);
            entry["observed"]["training"] = block;
            entry["observed"]["note"] = TrainingNote(block);
            Touch(entry);
            return id;
        }

        // Every number in the note comes from the training block (C-17: the model never invents data).
        static string TrainingNote(JObject block)
        {
            var parts = new List<string>();
            if (block["samples"] != null) parts.Add($"{block["samples"]} samples");
            if (block["epochs"] != null) parts.Add($"{block["epochs"]} epochs");
            if (block["candidates"] is JArray candidates) parts.Add($"{candidates.Count} candidates");
            if (block["val_loss"]?.Type == JTokenType.Float || block["val_loss"]?.Type == JTokenType.Integer)
                parts.Add("validation loss " + ((double)block["val_loss"]).ToString("0.0000", CultureInfo.InvariantCulture));
            if ((block["seconds"]?.Type == JTokenType.Float || block["seconds"]?.Type == JTokenType.Integer) && (double)block["seconds"] >= 0.05)
                parts.Add(((double)block["seconds"]).ToString("0.0", CultureInfo.InvariantCulture) + " s of training");
            return parts.Count == 0 ? null : string.Join(", ", parts) + ".";
        }

        // Where a run or an incident goes: the newest entry on this version that's still open or only judged by a run.
        static JObject Target()
        {
            int version = CurrentVersion();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if ((int)e["project_version"] != version) continue;
                if ((string)e["outcome"] == "open" || (string)e["outcome_by"] == "run") return e;
            }
            return null;
        }

        // ---- Lessons (C-17 section 5) ---------------------------------------------------------------------------

        /// <summary>Lessons with any of <paramref name="tags"/> (all of them for none), newest first, skipping hidden ones.</summary>
        public static IReadOnlyList<Lesson> Lessons(string[] tags, int limit = 10)
        {
            var found = new List<Lesson>();
            for (int i = lessons.Count - 1; i >= 0 && found.Count < limit; i--)
            {
                var lesson = lessons[i];
                if ((bool?)lesson["hidden"] == true) continue;
                if (tags != null && tags.Length > 0 && !Overlaps(ToArray(lesson["tags"]), tags)) continue;
                found.Add(new Lesson(lesson));
            }
            return found;
        }

        /// <summary>Hides a wrong lesson from what Albie reads (or shows it again). The record stays. Returns null, or why not.</summary>
        public static string SetLessonHidden(string lessonId, bool hidden)
        {
            if (!IsOpen) return "no_project: no project is open";
            foreach (var lesson in lessons)
                if ((string)lesson["id"] == lessonId)
                {
                    lesson["hidden"] = hidden;
                    SaveLessons();
                    return null;
                }
            return $"unknown_lesson: no lesson {lessonId}";
        }

        static void WriteLesson(JObject entry, string text, string[] tags, string by)
        {
            JObject lesson = null;
            string existing = (string)entry["lesson_id"];
            if (existing != null) foreach (var l in lessons) if ((string)l["id"] == existing) lesson = l;
            if (lesson == null)
            {
                lesson = new JObject
                {
                    ["schema"] = LessonSchema,
                    ["id"] = $"les_{nextLesson++:0000}",
                    ["text"] = text,
                    ["tags"] = new JArray(Distinct(tags)),
                    ["by"] = by,
                    ["from"] = new JObject { ["project_id"] = entry["project_id"], ["project_version"] = entry["project_version"], ["entry"] = entry["id"] },
                    ["seen"] = 1,
                    ["hidden"] = false,
                    ["created_at"] = Now(),
                };
                lessons.Add(lesson);
            }
            else
            {
                lesson["text"] = text;
                lesson["tags"] = new JArray(Distinct(tags));
                lesson["by"] = by;
            }
            entry["lesson"] = text;
            entry["lesson_id"] = lesson["id"];
            SaveLessons();
        }

        // ---- Session start (C-17 section 5, notebook_read) ------------------------------------------------------

        /// <summary>
        /// What Albie reads at session start: the project, its version, the current plan, the last <paramref name="last"/>
        /// entries and up to 10 lessons matching the project's tags, in at most <paramref name="maxChars"/> characters of
        /// JSON (oldest entries are dropped first). <c>say</c> is the line to speak about a matching lesson (C-17 section 5).
        /// </summary>
        public static JObject Read(int last = 5, int maxChars = 2000)
        {
            Require();
            var projectTags = new List<string>();
            foreach (var entry in entries) foreach (string tag in ToArray(entry["tags"])) if (!projectTags.Contains(tag)) projectTags.Add(tag);
            var recent = new JArray();
            for (int i = Math.Max(0, entries.Count - Math.Max(1, last)); i < entries.Count; i++)
            {
                var e = entries[i];
                var brief = new JObject
                {
                    ["id"] = e["id"],
                    ["version"] = e["project_version"],
                    ["kind"] = e["kind"],
                    ["change"] = e["change"],
                    ["outcome"] = e["outcome"],
                };
                if ((string)e["outcome_by"] != null) brief["outcome_by"] = e["outcome_by"];
                if ((string)e["observed"]?["note"] != null) brief["note"] = Clip((string)e["observed"]["note"], 160);
                if ((string)e["lesson"] != null) brief["lesson"] = e["lesson"];
                int runs = (e["observed"]?["runs"] as JArray)?.Count ?? 0, incidents = (e["observed"]?["incidents"] as JArray)?.Count ?? 0;
                if (runs > 0) brief["runs"] = runs;
                if (incidents > 0) brief["incidents"] = incidents;
                recent.Add(brief);
            }
            var lessonList = new JArray();
            foreach (var lesson in Lessons(projectTags.ToArray()))
                lessonList.Add(new JObject { ["id"] = lesson.Id, ["text"] = lesson.Text, ["tags"] = new JArray(lesson.Tags), ["seen"] = lesson.Seen });
            string say = null;
            if (lessonList.Count > 0)
            {
                var first = (JObject)lessonList[0];
                string text = (string)first["text"];
                say = ((int?)first["seen"] ?? 1) <= 1 ? "Last time: " + text : text;
            }
            var result = new JObject
            {
                ["project_id"] = ProjectId,
                ["version"] = CurrentVersion(),
                ["plan"] = PlanBrief(),
                ["say"] = say,
                ["entries"] = recent,
                ["lessons"] = lessonList,
            };
            while (result.ToString(Formatting.None).Length > maxChars && (recent.Count > 1 || lessonList.Count > 0 || say != null))
            {
                if (recent.Count > 1) recent.RemoveAt(0);
                else if (lessonList.Count > 0) lessonList.RemoveAt(lessonList.Count - 1);
                else { result["say"] = say = null; }
            }
            return result;
        }

        static JToken PlanBrief()
        {
            var plan = CurrentPlan;
            if (plan == null) return JValue.CreateNull();
            int done = 0, total = 0;
            if (plan["milestones"] is JArray list)
            {
                total = list.Count;
                foreach (var m in list) if ((bool?)m["done"] == true) done++;
            }
            return new JObject
            {
                ["version"] = plan["version"],
                ["goal"] = Clip((string)plan["goal"], 120),
                ["test"] = Clip((string)plan["test"], 80),
                ["done"] = done,
                ["milestones"] = total,
            };
        }

        /// <summary>
        /// Side by side for two project versions (16.3 Nov 12, C-12 question 7). A version with an incident and no run
        /// is labelled "free drive, not this course": v1 flipped before the slalom existed. Numbers come from the
        /// notebook and the run records, never invented.
        /// </summary>
        public static JObject CompareVersions(int fromVersion, int toVersion, string artifactId = "slalom")
        {
            Require();
            if (fromVersion == toVersion) throw new ArgumentException("bad_value: compare two different versions");
            var from = VersionSide(fromVersion, artifactId);
            var to = VersionSide(toVersion, artifactId);
            return new JObject
            {
                ["from"] = from,
                ["to"] = to,
                ["say"] = CompareSay(from, to),
            };
        }

        static JObject VersionSide(int version, string artifactId)
        {
            JObject manifest = null;
            foreach (var v in versions) if ((int)v["version"] == version) manifest = v;
            var incidents = new JArray();
            var runIds = new List<string>();
            string outcome = null, outcomeBy = null, note = null;
            foreach (var e in entries)
            {
                if ((int)e["project_version"] != version) continue;
                outcome = (string)e["outcome"];
                outcomeBy = (string)e["outcome_by"];
                if ((string)e["observed"]?["note"] != null) note = (string)e["observed"]["note"];
                if (e["observed"]?["incidents"] is JArray listed)
                    foreach (var incident in listed) incidents.Add(incident.DeepClone());
                if (e["observed"]?["runs"] is JArray runs)
                    foreach (var run in runs) if (!runIds.Contains((string)run)) runIds.Add((string)run);
            }
            var runRows = new JArray();
            foreach (string id in runIds)
            {
                var record = ArtifactRuns.Load(artifactId, id);
                if (record == null)
                {
                    runRows.Add(new JObject { ["run_id"] = id, ["label"] = "different course" });
                    continue;
                }
                runRows.Add(new JObject
                {
                    ["run_id"] = record["run_id"],
                    ["outcome"] = record["outcome"],
                    ["reason"] = record["reason"],
                    ["metrics"] = record["metrics"],
                    ["layout_id"] = record["layout_id"],
                    ["project_version"] = record["project_version"],
                });
            }
            string label = runIds.Count == 0 && incidents.Count > 0 ? "free drive, not this course" : null;
            return new JObject
            {
                ["version"] = version,
                ["summary"] = manifest?["summary"],
                ["cut_by"] = manifest?["cut_by"],
                ["outcome"] = outcome,
                ["outcome_by"] = outcomeBy,
                ["note"] = note,
                ["label"] = label,
                ["incidents"] = incidents,
                ["runs"] = runRows,
            };
        }

        // Every number in this line comes from the records (C-17: the model never invents data).
        static string CompareSay(JObject from, JObject to)
        {
            var parts = new List<string>();
            parts.Add(SideSay("v" + from["version"], from));
            parts.Add(SideSay("v" + to["version"], to));
            return string.Join(" ", parts);
        }

        static string SideSay(string name, JObject side)
        {
            if ((string)side["label"] == "free drive, not this course")
            {
                string kind = (string)(side["incidents"] as JArray)?[0]?["kind"];
                return kind == "rolled_over"
                    ? name + " rolled over on a free drive, not this course."
                    : name + " has no run on this course.";
            }
            JObject completed = null;
            if (side["runs"] is JArray runs)
                foreach (var run in runs)
                    if (completed == null && (string)run["outcome"] == "completed") completed = (JObject)run;
            if (completed?["metrics"]?["completion_time_s"] != null && completed["metrics"]["completion_time_s"].Type != JTokenType.Null)
            {
                double s = (double)completed["metrics"]["completion_time_s"];
                return name + " finished the slalom in " + s.ToString("0.0", CultureInfo.InvariantCulture) + " s.";
            }
            string outcome = (string)side["outcome"];
            return outcome == "failed" ? name + " failed." : outcome == "worked" ? name + " worked." : name + " is " + (outcome ?? "open") + ".";
        }

        // ---- Fixtures (C-17 section 10) -------------------------------------------------------------------------

        /// <summary>
        /// Fills an empty notebook with the demo story from <c>Mocks/notebook/</c> (entries, versions, lessons), for
        /// sandboxes and mock mode. It does nothing to a notebook that already has entries. Returns how many entries it added.
        /// </summary>
        public static int SeedFixtures()
        {
            Require();
            if (entries.Count > 0 || versions.Count > 0) return 0;
            foreach (JObject version in Fixture("versions"))
            {
                version["project_id"] = ProjectId;
                versions.Add(version);
                NotebookFiles.Write(Path.Combine(VersionsFolder, $"v{(int)version["version"]:0000}.json"), version.ToString(Formatting.Indented));
            }
            foreach (JObject entry in Fixture("entries"))
            {
                entry["project_id"] = ProjectId;
                entries.Add(entry);
                nextEntry = Math.Max(nextEntry, IdNumber((string)entry["id"], "ent_") + 1);
                Save(entry);
            }
            if (lessons.Count == 0)
            {
                foreach (JObject lesson in Fixture("lessons"))
                {
                    lessons.Add(lesson);
                    nextLesson = Math.Max(nextLesson, IdNumber((string)lesson["id"], "les_") + 1);
                }
                SaveLessons();
            }
            if (plans.Count == 0)
            {
                foreach (JObject plan in Fixture("plans"))
                {
                    plan["project_id"] = ProjectId;
                    plans.Add(plan);
                    nextPlan = Math.Max(nextPlan, (int)plan["version"] + 1);
                    NotebookFiles.Write(Path.Combine(PlansFolder, $"plan_v{(int)plan["version"]:0000}.json"), plan.ToString(Formatting.Indented));
                }
            }
            return entries.Count;
        }

        static JArray Fixture(string name)
        {
            var asset = Resources.Load<TextAsset>("Mocks/notebook/" + name);
            if (asset == null) throw new InvalidOperationException($"no Resources/Mocks/notebook/{name}.json");
            return (JArray)NotebookFiles.Parse(asset.text);
        }

        // ---- Helpers --------------------------------------------------------------------------------------------

        static void Require()
        {
            if (!IsOpen) throw new ArgumentException("no_project: no project is open");
        }

        static void Touch(JObject entry)
        {
            entry["updated_at"] = Now();
            Save(entry);
            EntryUpdated?.Invoke(entry);
            Emit("notebook.updated", new JObject
            {
                ["project_id"] = entry["project_id"],
                ["entry_id"] = entry["id"],
                ["outcome"] = entry["outcome"],
                ["lesson_id"] = entry["lesson_id"],
            });
        }

        static void Save(JObject entry) =>
            NotebookFiles.Write(Path.Combine(NotebookFolder, (string)entry["id"] + ".json"), entry.ToString(Formatting.Indented));

        static void SaveLessons() => NotebookFiles.Write(LessonsPath, new JArray(lessons).ToString(Formatting.Indented));

        // Events reach panels through ForgeNet with no hub (C-17 section 9). Edit-mode checks have no ForgeNet.
        static void Emit(string type, JObject payload)
        {
            if (!Application.isPlaying) return;
            try { ForgeNet.Emit(type, payload.DeepClone()); }
            catch (Exception e) { Debug.LogException(e); }
        }

        static string CheckTags(string[] tags)
        {
            if (tags == null) return null;
            foreach (string tag in tags)
                if (Array.IndexOf(Tags, tag) < 0) return $"unknown_tag: tag \"{tag}\": use one of {string.Join(", ", Tags)}";
            return null;
        }

        static void AddTag(JObject entry, string tag)
        {
            var list = (JArray)entry["tags"];
            foreach (var t in list) if ((string)t == tag) return;
            list.Add(tag);
        }

        static int Rank(string by) => by == "run" ? 0 : by == "albie" ? 1 : by == "person" ? 2 : -1;

        static readonly Regex IdPattern = new Regex(@"^(?:ent_|les_|plan_v|v)(\d+)", RegexOptions.CultureInvariant);

        /// <summary>The number in an id or file name with that prefix (ent_0012 → 12), or 0.</summary>
        static int IdNumber(string id, string prefix)
        {
            if (id == null || !id.StartsWith(prefix, StringComparison.Ordinal)) return 0;
            var match = IdPattern.Match(id);
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
        }

        static string[] ToArray(JToken token)
        {
            if (!(token is JArray array)) return new string[0];
            var result = new string[array.Count];
            for (int i = 0; i < result.Length; i++) result[i] = (string)array[i];
            return result;
        }

        static string[] Distinct(string[] tags)
        {
            var list = new List<string>();
            if (tags != null) foreach (string tag in tags) if (!list.Contains(tag)) list.Add(tag);
            return list.ToArray();
        }

        static bool Overlaps(string[] a, string[] b)
        {
            foreach (string x in a) if (Array.IndexOf(b, x) >= 0) return true;
            return false;
        }

        static JValue Text(string s) => string.IsNullOrWhiteSpace(s) ? null : new JValue(s.Trim());

        static string Clip(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        static string Now() => Clock().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}
