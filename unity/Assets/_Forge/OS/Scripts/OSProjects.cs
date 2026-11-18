using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Forge.Artifacts;
using Forge.Notebook;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>What a project is about, so its window offers the right Continue actions.</summary>
    public enum OSProjectKind { Truck, Cad, Ml, Generic }

    /// <summary>
    /// The Projects tab. Registers a source with <see cref="OSRegistry"/> that lists the four sample projects (RC truck,
    /// Quadcopter, Rocket, Image classifier: worked examples under the "Sample projects" heading, each tile saying what it
    /// shows until it has progress of its own), a Start my own tile (Build something), and every other folder under the
    /// notebook's <c>projects/</c> folder (the person's own projects, copies of a sample among them). The
    /// folder listing and each project's status line are cached: they are read again when something asks (a registry
    /// change, a notebook write, <see cref="Refresh"/>) or after a few seconds, never every frame. A project is open when
    /// <see cref="NotebookStore.ProjectId"/> matches; <see cref="OpenProject"/> always leaves a project open (it switches
    /// with <c>NotebookStore.Open</c> and never closes first) and shows the Project window.
    /// </summary>
    public static class OSProjects
    {
        /// <summary>The honest line shown for a project whose data lives in the cloud when the cloud isn't reachable.</summary>
        public const string NeedsCloud = "Needs Albie's cloud to load the dataset. Not available offline.";
        /// <summary>Kept for old callers; the Send step now drafts to the project's channel (<c>MessagesShare.DraftForProject</c>).</summary>
        public const string SendUnavailable = "Sending the model isn't available in this build.";

        /// <summary>One artifact whose manifest links to a project, with its newest run for that project.</summary>
        public sealed class LinkedArtifact
        {
            public string id, title, template, thing;
            public int version;
            /// <summary>Newest run record for this project, or null (no run yet).</summary>
            public string runId, runOutcome, runReason;
            public float? runSeconds;
        }

        /// <summary>The heading over the four sample projects on the Projects tab, and the tag on their windows.</summary>
        public const string SampleSection = "Sample projects";
        /// <summary>The tag a sample project's window and voice answers carry.</summary>
        public const string SampleTag = "Sample project";
        /// <summary>What a sample project is for, in one line (the window and the welcome card say it).</summary>
        public const string SampleLine = "A worked example. Explore it, keep going, or copy it to make it yours.";
        /// <summary>The "Start my own" tile beside the samples: it opens Build something.</summary>
        public const string StartOwnId = "start_own";
        public const string StartOwnTitle = "Start my own";
        const string ReadyToStart = "Ready to start";

        sealed class Starter
        {
            public string id, title, icon, part;
            /// <summary>What this sample shows: the short tile line and the full sentence (window, welcome card, voice).</summary>
            public string tileLine, shows;
            /// <summary>The goal and test a copy starts with when the sample has no plan saved on this device.</summary>
            public string goal, test;
            public OSProjectKind kind;
            /// <summary>Other names people use for it ("drone", "quad"), matched as whole words by <see cref="Resolve"/>.</summary>
            public string[] aliases;
            /// <summary>The <c>links.thing</c> of the courses that belong to it ("truck", "drone", "rocket"), or null.</summary>
            public string thing;
        }

        const float RescanSeconds = 4f;

        static readonly Starter[] starters =
        {
            new Starter { id = NotebookStore.DefaultProject, title = "RC truck", icon = "truck", kind = OSProjectKind.Truck, thing = "truck",
                tileLine = "Design, drive, train", shows = "Design, course, drive, train an autopilot",
                goal = "An RC truck that does a slalom around the couch.", test = "Finish the slalom with no cones hit.",
                aliases = new[] { "truck", "rc truck", "rc car", "car", "slalom" } },
            new Starter { id = QuadcopterId, title = "Quadcopter", icon = "quadcopter", kind = OSProjectKind.Cad, part = "quadcopter", thing = "drone",
                tileLine = "Design, fly, learn", shows = "A CAD design that flies, follows you and learns a course",
                goal = "A quadcopter that flies a hoop course in the room.", test = "Fly through every hoop and land.",
                aliases = new[] { "quadcopter", "quad", "drone", "quadrotor", "copter" } },
            new Starter { id = RocketId, title = "Rocket", icon = "rocket", kind = OSProjectKind.Cad, part = "rocket", thing = "rocket",
                tileLine = "Design, sim, launch", shows = "Design, simulate and launch in your room",
                goal = "A model rocket that flies straight and lands near the pad.", test = "A simulated flight that stays stable.",
                aliases = new[] { "rocket", "model rocket" } },
            new Starter { id = ClassifierId, title = "Image classifier", icon = "classifier", kind = OSProjectKind.Ml,
                tileLine = "Find, train, publish", shows = "Find a model, train it, publish",
                goal = "A model that tells pictures apart.", test = "Most test pictures named right.",
                aliases = new[] { "classifier", "image classifier", "image model", "model", "ml", "machine learning" } },
        };

        public const string QuadcopterId = "proj_quadcopter", RocketId = "proj_rocket", ClassifierId = "proj_classifier";
        /// <summary>The id prefix of a project made for a cloud or hub ML project (<see cref="OpenMlProject"/>).</summary>
        public const string MlPrefix = "proj_ml_";

        // The project the person opened through the Projects tab, a project window, Build something or voice. The notebook
        // also opens proj_truck by itself at Play start; that one isn't "chosen", so nothing is linked to it unasked.
        static string chosen;

        static readonly List<OSEntry> entries = new List<OSEntry>();
        static readonly Dictionary<string, string> statusById = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly List<string> folderIds = new List<string>();
        static readonly List<LinkedArtifact> scratchLinked = new List<LinkedArtifact>();
        static bool dirty = true;
        static float lastScan = -1000f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            entries.Clear();
            statusById.Clear();
            folderIds.Clear();
            mlFolderIds.Clear();
            scratchLinked.Clear();
            dirty = true;
            lastScan = -1000f;
            chosen = null;
        }

        static readonly List<string> mlFolderIds = new List<string>();

        /// <summary>Adds the Projects source to the registry. The app does this at start; a check does it by hand.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register()
        {
            OSRegistry.UnregisterSource(Fill);
            OSRegistry.Changed -= MarkDirty;
            OSRegistry.Changed += MarkDirty;
            NotebookStore.EntryCreated -= OnNotebook;
            NotebookStore.EntryUpdated -= OnNotebook;
            NotebookStore.VersionCut -= OnNotebook;
            NotebookStore.PlanUpdated -= OnNotebook;
            NotebookStore.EntryCreated += OnNotebook;
            NotebookStore.EntryUpdated += OnNotebook;
            NotebookStore.VersionCut += OnNotebook;
            NotebookStore.PlanUpdated += OnNotebook;
            OSRegistry.RegisterSource(OSTab.Projects, Fill);
        }

        /// <summary>Takes the Projects source out again (a check that lists its own sample projects). <see cref="Register"/> puts it back.</summary>
        public static void Unregister()
        {
            OSRegistry.UnregisterSource(Fill);
            OSRegistry.Changed -= MarkDirty;
            NotebookStore.EntryCreated -= OnNotebook;
            NotebookStore.EntryUpdated -= OnNotebook;
            NotebookStore.VersionCut -= OnNotebook;
            NotebookStore.PlanUpdated -= OnNotebook;
        }

        static void MarkDirty() => dirty = true;

        static void OnNotebook(JObject _)
        {
            dirty = true;
            if (Application.isPlaying) OSRegistry.NotifyChanged();  // the tiles' status lines changed
        }

        /// <summary>Asks for the list and the status lines to be read again the next time the menu asks. Call when the menu opens.</summary>
        public static void Refresh() => dirty = true;

        // ---------------------------------------------------------------- the list

        static void Fill(List<OSEntry> into)
        {
            if (dirty || entries.Count == 0 || Time.realtimeSinceStartup - lastScan > RescanSeconds) Rescan();
            into.AddRange(entries);
        }

        static void Rescan()
        {
            dirty = false;
            lastScan = Time.realtimeSinceStartup;
            entries.Clear();
            statusById.Clear();
            folderIds.Clear();
            mlFolderIds.Clear();

            try
            {
                string dir = Path.Combine(NotebookStore.Root ?? NotebookStore.DefaultRoot, "projects");
                if (Directory.Exists(dir))
                    foreach (string folder in Directory.GetDirectories(dir))
                    {
                        string name = Path.GetFileName(folder);
                        if (string.IsNullOrEmpty(name) || FindStarter(name) != null) continue;
                        if (KindOf(name) == OSProjectKind.Ml)
                        {
                            // A cloud ML project the Machine learning section lists itself (live) isn't listed twice.
                            string ml = MlProjectOf(name);
                            if (ml == null || !OSMLProjects.HasCard(ml)) mlFolderIds.Add(name);
                        }
                        else folderIds.Add(name);
                    }
                folderIds.Sort(StringComparer.OrdinalIgnoreCase);
                mlFolderIds.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.Log("[OS] Couldn't list the project folders: " + e.Message);
            }

            // The four sample projects first, under their own heading; then the person's own projects on the next row, led by
            // Start my own (Build something); then one Machine learning section: the ML projects saved on the headset, and
            // (from OSMLProjects, registered after this source) the hub's or cloud's ML projects.
            for (int i = 0; i < starters.Length; i++) AddEntry(starters[i].id, starters[i].title, starters[i].icon, SampleSection);
            entries.Add(new OSEntry
            {
                id = StartOwnId,
                title = StartOwnTitle,
                icon = "plus",
                tab = OSTab.Projects,
                outline = true,
                status = () => "Build something new",
                open = StartOwn,
            });
            for (int i = 0; i < folderIds.Count; i++) AddEntry(folderIds[i], TitleFor(folderIds[i]), IconFor(folderIds[i], "projects"), null);
            for (int i = 0; i < mlFolderIds.Count; i++) AddEntry(mlFolderIds[i], TitleFor(mlFolderIds[i]), "classifier", OSMLProjects.Section);
        }

        /// <summary>Start my own: the Build something card (the same flow as the menu strip's button).</summary>
        public static void StartOwn() => OSBuildFlow.Open();

        // A copy of a sample keeps the sample's icon; anything else gets the fallback.
        static string IconFor(string id, string fallback)
        {
            var from = FindStarter(CopyOf(id));
            return from != null ? from.icon : fallback;
        }

        static void AddEntry(string id, string title, string icon, string section)
        {
            string line = ComputeStatus(id, null);
            var starter = FindStarter(id);
            if (starter != null && line == ReadyToStart) line = starter.tileLine;  // a sample nobody has touched says what it shows
            statusById[id] = line;
            string captured = id;
            entries.Add(new OSEntry
            {
                id = id,
                title = title,
                icon = icon,
                tab = OSTab.Projects,
                section = section,
                status = () => statusById.TryGetValue(captured, out string s) ? s : ReadyToStart,
                isOpen = () => NotebookStore.IsOpen && NotebookStore.ProjectId == captured,
                open = () => OpenProject(captured),
            });
        }

        static Starter FindStarter(string id)
        {
            for (int i = 0; i < starters.Length; i++)
                if (starters[i].id == id) return starters[i];
            return null;
        }

        /// <summary>What the project is about: the truck, a CAD design, an ML project (the classifier, or one made for a
        /// cloud ML project), or anything else.</summary>
        public static OSProjectKind KindOf(string id)
        {
            var starter = FindStarter(id) ?? FindStarter(CopyOf(id));  // a copy of a sample works like the sample
            if (starter != null) return starter.kind;
            return ProjectMeta.Get(id, "kind") == "ml" || (id != null && id.StartsWith(MlPrefix, StringComparison.Ordinal))
                ? OSProjectKind.Ml : OSProjectKind.Generic;
        }

        /// <summary>The CAD design a starter (or a copy of one) opens ("quadcopter", "rocket"), or null.</summary>
        public static string DesignOf(string id) => (FindStarter(id) ?? FindStarter(CopyOf(id)))?.part;

        // ---------------------------------------------------------------- sample projects

        /// <summary>True for the four sample projects (RC truck, Quadcopter, Rocket, Image classifier): worked examples to explore or copy.</summary>
        public static bool IsSample(string id) => FindStarter(id) != null;

        /// <summary>What a sample project shows, in one sentence ("Design, simulate and launch in your room"); null for any other project.</summary>
        public static string SampleShows(string id) => FindStarter(id)?.shows;

        /// <summary>The short line a sample's tile shows ("Design, drive, train"); null for any other project.</summary>
        public static string SampleTileLine(string id) => FindStarter(id)?.tileLine;

        /// <summary>The sample ids, in the order the tab lists them.</summary>
        public static void SampleIds(List<string> into)
        {
            into.Clear();
            for (int i = 0; i < starters.Length; i++) into.Add(starters[i].id);
        }

        /// <summary>The sample a project was copied from (<see cref="CopySample"/>), or null.</summary>
        public static string CopyOf(string id) => string.IsNullOrEmpty(id) || FindStarter(id) != null ? null : ProjectMeta.Get(id, "copy_of");

        /// <summary>
        /// Make a copy: a new project of the person's own from a sample, "My RC truck" (id <c>proj_my_truck</c>, then
        /// <c>proj_my_truck_2</c>...). It keeps the sample's plan (goal, test, parts, course; every milestone not done yet)
        /// and its design link and loop, and none of its history (versions, runs, notes). The copy is opened with its window.
        /// Returns the new id, or null with a plain sentence in <paramref name="error"/>.
        /// </summary>
        public static string CopySample(string sampleId, out string error)
        {
            error = null;
            var s = FindStarter(sampleId);
            if (s == null)
            {
                error = "Only a sample project can be copied.";
                return null;
            }
            JObject plan = null;
            if (EnsureOpen(s.id, out _) && NotebookStore.CurrentPlan != null) plan = (JObject)NotebookStore.CurrentPlan.DeepClone();

            string id = FreeCopyId(s);
            string title = "My " + (s.title.Length > 1 && char.IsUpper(s.title[1]) ? s.title : char.ToLowerInvariant(s.title[0]) + s.title.Substring(1));
            if (!ProjectMeta.Update(id, meta =>
                {
                    meta["title"] = title;
                    meta["copy_of"] = s.id;
                    if (s.kind == OSProjectKind.Ml) meta["kind"] = "ml";
                }))
            {
                error = "The copy couldn't be made.";
                return null;
            }
            if (!EnsureOpen(id, out error)) return null;

            string goal = (string)plan?["goal"];
            string test = plan?["test"] != null && plan["test"].Type == JTokenType.String ? (string)plan["test"] : null;
            var milestones = new JArray();
            if (plan?["milestones"] is JArray old)
                foreach (JToken m in old)
                    if (m is JObject item)
                    {
                        var copy = (JObject)item.DeepClone();
                        copy["done"] = false;
                        milestones.Add(copy);
                    }
            try
            {
                int version = NotebookStore.RevisePlan("Copied from the sample " + s.title + ".",
                    string.IsNullOrWhiteSpace(goal) ? s.goal : goal, string.IsNullOrWhiteSpace(test) ? s.test : test, milestones);
                JObject saved = NotebookStore.CurrentPlan;
                if (plan != null && saved != null && (int?)saved["version"] == version && (plan["parts"] is JArray || plan["course"] != null))
                {
                    if (plan["parts"] is JArray parts) saved["parts"] = parts.DeepClone();
                    if (plan["course"] != null && plan["course"].Type == JTokenType.String) saved["course"] = plan["course"].DeepClone();
                    NotebookFiles.Write(Path.Combine(NotebookStore.ProjectFolder, "plans", "plan_v" + version.ToString("0000") + ".json"), saved.ToString(Formatting.Indented));
                }
            }
            catch (Exception e)
            {
                if (!(e is ArgumentException)) Debug.LogException(e);
                Debug.Log("[OS] The copy of " + s.id + " has no plan: " + e.Message);
            }
            dirty = true;
            OpenProject(id);
            return id;
        }

        // proj_my_truck, then proj_my_truck_2: the first id no folder, record or open project has.
        static string FreeCopyId(Starter s)
        {
            NotebookStore.Flush();
            string folder = Path.Combine(NotebookStore.Root ?? NotebookStore.DefaultRoot, "projects");
            string baseId = "proj_my_" + (s.id.StartsWith("proj_", StringComparison.Ordinal) ? s.id.Substring(5) : s.id);
            string id = baseId;
            for (int n = 2; n < 1000; n++)
            {
                bool taken = FindStarter(id) != null || NotebookStore.ProjectId == id || ProjectMeta.Read(id) != null
                    || Directory.Exists(Path.Combine(folder, id));
                if (!taken) break;
                id = baseId + "_" + n;
            }
            return id;
        }

        /// <summary>The cloud or hub ML project (its <c>GET /ml/projects</c> name) this project is for, or null.</summary>
        public static string MlProjectOf(string id) => ProjectMeta.Get(id, "ml_project");

        /// <summary>The project an ML project was made from (an <c>ml_new</c> started while it was open), or null.</summary>
        public static string ParentOf(string id) => ProjectMeta.Get(id, "parent");

        /// <summary>The name on the tile and the window title.</summary>
        public static string TitleFor(string id)
        {
            var starter = FindStarter(id);
            if (starter != null) return starter.title;
            return ProjectMeta.Get(id, "title") ?? Pretty(id);
        }

        /// <summary>The starter Build something fills for this kind: the truck, the quadcopter, the rocket or the
        /// classifier; null for "something else", which gets a new project.</summary>
        public static string StarterFor(OSPlanner.Kind kind)
        {
            switch (kind)
            {
                case OSPlanner.Kind.Truck: return NotebookStore.DefaultProject;
                case OSPlanner.Kind.Drone: return QuadcopterId;
                case OSPlanner.Kind.Rocket: return RocketId;
                case OSPlanner.Kind.Classifier: return ClassifierId;
                default: return null;
            }
        }

        /// <summary>The starter whose courses have this <c>links.thing</c> ("truck", "drone", "rocket"), or null.</summary>
        public static string StarterForThing(string thing)
        {
            if (string.IsNullOrEmpty(thing)) return null;
            for (int i = 0; i < starters.Length; i++)
                if (starters[i].thing == thing) return starters[i].id;
            return null;
        }

        /// <summary>The <c>links.thing</c> an artifact template is for ("truck", "drone", "rocket"), or null.</summary>
        public static string ThingOfTemplate(string template)
        {
            if (string.IsNullOrEmpty(template)) return null;
            if (ArtifactCourses.IsTemplate(template)) return ArtifactCourses.ThingOf(template);
            if (template == OSProjectWindow.RocketLaunchTemplate) return "rocket";
            try
            {
                if (ArtifactLibrary.TryTemplate(template, out JObject manifest, out _))
                {
                    JToken thing = manifest?["links"]?["thing"];
                    return thing != null && thing.Type == JTokenType.String ? (string)thing : null;
                }
            }
            catch (Exception e)
            {
                Debug.Log("[OS] Couldn't read the template " + template + ": " + e.Message);
            }
            return null;
        }

        /// <summary>
        /// Gets the notebook ready for an artifact made from the Artifacts tab, so the library links it to the right project
        /// or to none, never by default to the truck: a project the person opened stays open and gets it; otherwise a
        /// course goes to the starter it belongs to (a drone course to the Quadcopter, the slalom to the RC truck, the
        /// launch pad to the Rocket); anything else is made standalone (the notebook the app opened by itself is closed, and
        /// any project window opens it again). Returns the project it will link to, or null.
        /// </summary>
        public static string PrepareForArtifact(string template)
        {
            if (ChosenOpen) return NotebookStore.ProjectId;
            string starter = StarterForThing(ThingOfTemplate(template));
            if (starter != null)
            {
                if (EnsureOpen(starter, out string error)) return starter;
                Debug.Log("[OS] Couldn't open " + starter + " for the new artifact: " + error);
                return NotebookStore.ProjectId;
            }
            if (NotebookStore.IsOpen)
            {
                Debug.Log("[OS] No project was chosen, so the new " + template + " is standalone (closed the notebook's default '" + NotebookStore.ProjectId + "').");
                NotebookStore.Close();
                dirty = true;
            }
            return null;
        }

        /// <summary>True while the open notebook project is one the person opened (not only the truck the notebook opens at start).</summary>
        public static bool ChosenOpen => NotebookStore.IsOpen && chosen != null && chosen == NotebookStore.ProjectId;

        /// <summary>
        /// Which project a spoken or typed name means: an id ("proj_truck", "truck"), a starter's alias ("drone",
        /// "quad", "the rocket"), or a project's title, whole or in part ("my rover"). Null when nothing matches or
        /// two projects match equally.
        /// </summary>
        public static string Resolve(string said)
        {
            if (string.IsNullOrWhiteSpace(said)) return null;
            string raw = said.Trim();
            if (FindStarter(raw) != null) return raw;
            if (FindStarter("proj_" + raw) != null) return "proj_" + raw;
            if (dirty || entries.Count == 0) Rescan();
            for (int i = 0; i < entries.Count; i++)
                if (!entries[i].outline && string.Equals(entries[i].id, raw, StringComparison.OrdinalIgnoreCase)) return entries[i].id;

            string n = OSPlanner.Normalize(raw);
            if (n.StartsWith("proj ", StringComparison.Ordinal)) n = n.Substring(5);
            // "My RC truck" is the person's copy, not the sample: an exact title of their own project wins before "my" is dropped.
            for (int i = 0; i < entries.Count; i++)
                if (!entries[i].outline && FindStarter(entries[i].id) == null && OSPlanner.Normalize(entries[i].title) == n) return entries[i].id;
            string[] fillers = { "the ", "my ", "our ", "a " };
            for (int f = 0; f < fillers.Length; f++)
                if (n.StartsWith(fillers[f], StringComparison.Ordinal)) n = n.Substring(fillers[f].Length);
            if (n.EndsWith(" project", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 8);
            if (n.Length == 0) return null;
            string padded = " " + n + " ";

            // Exact title or alias first.
            for (int i = 0; i < starters.Length; i++)
            {
                if (OSPlanner.Normalize(starters[i].title) == n) return starters[i].id;
                for (int a = 0; a < starters[i].aliases.Length; a++)
                    if (starters[i].aliases[a] == n) return starters[i].id;
            }
            for (int i = 0; i < entries.Count; i++)
                if (!entries[i].outline && (OSPlanner.Normalize(entries[i].title) == n || OSPlanner.Normalize(entries[i].id).Replace("proj ", "") == n)) return entries[i].id;

            // Then a title inside what was said, or what was said inside a title (the longest wins; a tie is no answer).
            string best = null;
            int bestLength = 0;
            bool tie = false;
            void Consider(string id, string text)
            {
                if (text.Length < 3) return;
                bool hit = padded.IndexOf(" " + text + " ", StringComparison.Ordinal) >= 0
                           || (n.Length >= 4 && (" " + text + " ").IndexOf(padded, StringComparison.Ordinal) >= 0);
                if (!hit) return;
                if (text.Length > bestLength) { best = id; bestLength = text.Length; tie = false; }
                else if (text.Length == bestLength && best != id) tie = true;
            }
            for (int i = 0; i < starters.Length; i++)
            {
                Consider(starters[i].id, OSPlanner.Normalize(starters[i].title));
                for (int a = 0; a < starters[i].aliases.Length; a++) Consider(starters[i].id, starters[i].aliases[a]);
            }
            for (int i = 0; i < entries.Count; i++)
                if (!entries[i].outline && FindStarter(entries[i].id) == null) Consider(entries[i].id, OSPlanner.Normalize(entries[i].title));
            return tie ? null : best;
        }

        /// <summary>Every project id the tab lists from the headset (the starters and the saved folders).</summary>
        public static void AllIds(List<string> into)
        {
            into.Clear();
            if (dirty || entries.Count == 0) Rescan();
            for (int i = 0; i < starters.Length; i++) into.Add(starters[i].id);
            into.AddRange(folderIds);
            into.AddRange(mlFolderIds);
        }

        /// <summary>The project id made for the cloud or hub ML project <paramref name="mlName"/>: "proj_ml_" and its name as a folder name.</summary>
        public static string MlProjectId(string mlName)
        {
            if (string.IsNullOrEmpty(mlName)) return null;
            var sb = new System.Text.StringBuilder(MlPrefix, MlPrefix.Length + mlName.Length);
            for (int i = 0; i < mlName.Length && sb.Length < 64; i++)
            {
                char c = char.ToLowerInvariant(mlName[i]);
                sb.Append((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Opens the project of a cloud or hub ML project: made the first time (its record names the ML project, its title,
        /// and the project it was made from when it was linked to one), then opened like any project, with its window. The
        /// window's first step (Data) opens it on the ML desk.
        /// </summary>
        public static void OpenMlProject(string mlName, string title, string mlKind = null)
        {
            string id = MlProjectId(mlName);
            if (id == null) return;
            if (ProjectMeta.Get(id, "ml_project") != mlName || (!string.IsNullOrEmpty(title) && ProjectMeta.Get(id, "title") != title)
                || (!string.IsNullOrEmpty(mlKind) && ProjectMeta.Get(id, "ml_kind") != mlKind))
            {
                var ids = new List<string>();
                AllIds(ids);
                string parent = ProjectMeta.ParentOfMl(ids, mlName, title);
                ProjectMeta.Update(id, meta =>
                {
                    meta["kind"] = "ml";
                    meta["ml_project"] = mlName;
                    if (!string.IsNullOrEmpty(title)) meta["title"] = title;
                    if (!string.IsNullOrEmpty(mlKind)) meta["ml_kind"] = mlKind;  // "new" (built by Albie), "github", "example"
                    if (parent != null && parent != id) meta["parent"] = parent;
                });
            }
            OpenProject(id);
        }

        static string Pretty(string id)
        {
            if (string.IsNullOrEmpty(id)) return "Project";
            string s = id.StartsWith("proj_", StringComparison.OrdinalIgnoreCase) ? id.Substring(5) : id;
            s = s.Replace('_', ' ').Replace('-', ' ').Trim();
            if (s.Length == 0) return id;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        /// <summary>True while this project's notebook is the open one.</summary>
        public static bool IsOpen(string id) => NotebookStore.IsOpen && NotebookStore.ProjectId == id;

        // ---------------------------------------------------------------- status lines from real data

        /// <summary>
        /// "v2 · slalom passed": the project's newest version and the newest run of an artifact linked to it; for the open
        /// project with no run yet, the outcome of its last judged attempt. "not started" when the notebook has neither.
        /// Pass the project's linked artifacts if they are at hand, or null to read them.
        /// </summary>
        public static string ComputeStatus(string id, List<LinkedArtifact> linked)
        {
            int version = 0;
            try { version = NotebookStore.CurrentVersion(id); }
            catch (Exception) { }

            if (linked == null)
            {
                LinkedArtifacts(id, scratchLinked);
                linked = scratchLinked;
            }

            LinkedArtifact newest = null;
            for (int i = 0; i < linked.Count; i++)
            {
                if (linked[i].runId == null) continue;
                if (newest == null || string.CompareOrdinal(linked[i].runId, newest.runId) > 0) newest = linked[i];
            }

            var parts = new List<string>(3);
            if (version > 0) parts.Add("v" + version);
            if (newest != null) parts.Add((newest.title ?? newest.id).ToLowerInvariant() + " " + RunWord(newest.runOutcome));
            else if (IsOpen(id))
            {
                var all = NotebookStore.Entries;
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    string outcome = (string)all[i]["outcome"];
                    if (string.IsNullOrEmpty(outcome) || outcome == "open") continue;
                    parts.Add("last attempt " + outcome);
                    break;
                }
            }
            return parts.Count == 0 ? (FindStarter(id) != null ? ReadyToStart : "not started") : string.Join(" · ", parts);
        }

        /// <summary>A run record's outcome as a word for a status line.</summary>
        public static string RunWord(string outcome)
        {
            switch (outcome)
            {
                case "completed": return "passed";
                case "failed": return "failed";
                case "aborted": return "stopped";
                default: return string.IsNullOrEmpty(outcome) ? "has no result" : outcome;
            }
        }

        /// <summary>
        /// The artifacts on this device whose manifest links to <paramref name="projectId"/>, each with its newest run for
        /// that project. Reads the library from disk: call it when a window builds, not every frame.
        /// </summary>
        public static void LinkedArtifacts(string projectId, List<LinkedArtifact> into)
        {
            into.Clear();
            string root = ArtifactLibrary.Root;
            if (string.IsNullOrEmpty(projectId) || !Directory.Exists(root)) return;

            string[] folders;
            try { folders = Directory.GetDirectories(root); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; }

            for (int i = 0; i < folders.Length; i++)
            {
                string path = Path.Combine(folders[i], "manifest.json");
                if (!File.Exists(path)) continue;
                JObject manifest;
                try { manifest = NotebookFiles.Parse(File.ReadAllText(path)) as JObject; }
                catch (Exception e) when (e is JsonException || e is IOException) { continue; }
                if (manifest == null || !(manifest["links"] is JObject links)) continue;
                if ((string)links["project"] != projectId) continue;

                var item = new LinkedArtifact
                {
                    id = (string)manifest["id"] ?? Path.GetFileName(folders[i]),
                    title = (string)manifest["title"],
                    template = (string)manifest["template"],
                    thing = (string)links["thing"],
                    version = (int?)manifest["version"] ?? 0,
                };
                if (string.IsNullOrEmpty(item.title)) item.title = item.id;

                try
                {
                    JObject run = OSArtifacts.NewestRun(item.id, projectId);  // the newest run for this project, found without parsing the older ones
                    if (run != null)
                    {
                        item.runId = (string)run["run_id"];
                        item.runOutcome = (string)run["outcome"];
                        item.runReason = (string)run["reason"];
                        double? seconds = (double?)run["metrics"]?["completion_time_s"];
                        item.runSeconds = seconds.HasValue ? (float?)seconds.Value : null;
                    }
                }
                catch (Exception e)
                {
                    Debug.Log("[OS] Couldn't read the runs of " + item.id + ": " + e.Message);
                }
                into.Add(item);
            }
            into.Sort((a, b) => string.CompareOrdinal(a.title, b.title));
        }

        // ---------------------------------------------------------------- opening

        /// <summary>
        /// Makes this project the open one and shows its window. The notebook is switched with <c>NotebookStore.Open</c>
        /// directly (never closed first, so a project is always open); a starter then does its own first step (CAD on its
        /// design, or the Train tool for the ML project). Nothing here throws, with the hub off or on.
        /// </summary>
        public static void OpenProject(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            id = id.Trim();
            if (!EnsureOpen(id, out string error))
            {
                Debug.Log("[OS] Couldn't open project '" + id + "': " + error);
                return;
            }
            dirty = true;
            OSWindow window = OSProjectWindow.Open(id);
            var view = window != null ? window.GetComponent<OSProjectWindow>() : null;
            if (view != null) view.RunOpenAction();
            OSRegistry.NotifyChanged();
        }

        /// <summary>
        /// Makes sure <paramref name="id"/> is the open notebook project, switching with <c>NotebookStore.Open</c> (which
        /// keeps the notebook's current root). A bad id comes back as a plain sentence, and a project is still left open.
        /// </summary>
        public static bool EnsureOpen(string id, out string error)
        {
            error = null;
            try
            {
                if (!NotebookStore.IsOpen || NotebookStore.ProjectId != id) NotebookStore.Open(id, NotebookStore.Root);
                chosen = id;
                return true;
            }
            catch (ArgumentException e)
            {
                error = Plain(e.Message);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                error = "The notebook couldn't open that project.";
            }
            if (!NotebookStore.IsOpen)
            {
                try { NotebookStore.Open(NotebookStore.DefaultProject, NotebookStore.Root); }
                catch (Exception e) { Debug.LogException(e); }
            }
            return false;
        }

        /// <summary>
        /// The brain loop the OS should offer, or null. The brain truck stays spawned after it first appears but hides itself
        /// (<see cref="SlalomBrain.Hidden"/>) while the host shows something that is not a truck course, or nothing; a hidden
        /// truck is "no truck yet" to every OS window, so Drive and Train never run an unseen truck.
        /// </summary>
        public static Forge.Controllers.IBrainLoop LiveLoop
        {
            get
            {
                Forge.Controllers.IBrainLoop loop = Forge.Controllers.BrainLoops.Active;
                var brain = loop as SlalomBrain;
                return brain != null && brain.Hidden ? null : loop;
            }
        }

        static readonly Collider[] startBlockers = new Collider[4];

        /// <summary>
        /// True when something solid sits where the brain truck is put at the start of a run: the truck would crash on its first
        /// step, every time, so the Autopilot (which tries each trained brain and the pretrained one from that pose) would fail all
        /// of them. <paramref name="what"/> is the collider's name for the log. This asks the physics scene exactly what the
        /// truck's own crash test asks (<c>ExampleTruckBody.Apply</c>: a sphere at the sensor height, the body's obstacle mask,
        /// triggers ignored), so it refuses only a run that would crash at once. False when there is no course open.
        /// </summary>
        public static bool StartBlocked(SlalomBrain brain, out string what)
        {
            what = null;
            ArtifactHost host = ArtifactHost.Current;
            if (brain == null || brain.Body == null || host == null || host.Run == null) return false;
            Pose at = host.SceneOrigin;
            Vector3 centre = at.position + at.rotation * Vector3.up * brain.Body.sensorHeight;
            int n = brain.gameObject.scene.GetPhysicsScene().OverlapSphere(centre, Forge.Controllers.MockTruckSim.TruckRadius, startBlockers, brain.Body.obstacles, QueryTriggerInteraction.Ignore);
            if (n == 0) return false;
            what = startBlockers[0] != null ? startBlockers[0].name : "something";
            for (int i = 0; i < startBlockers.Length; i++) startBlockers[i] = null;
            return true;
        }

        /// <summary>The first tool registered under any of these ids on the Tools tab, or null.</summary>
        public static OSEntry FindTool(params string[] ids)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                OSEntry entry = OSRegistry.Find(OSTab.Tools, ids[i]);
                if (entry != null && entry.open != null) return entry;
            }
            return null;
        }

        /// <summary>Opens a Tools-tab entry; false with a plain reason when it is missing or throws.</summary>
        public static bool OpenTool(OSEntry tool, out string error)
        {
            error = null;
            if (tool == null || tool.open == null)
            {
                error = "That tool isn't available yet.";
                return false;
            }
            try
            {
                tool.open();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                error = "Opening " + tool.title + " failed: " + Plain(e.Message);
                return false;
            }
        }

        /// <summary>
        /// Opens the CAD tool on one design with the hub off: the CAD mode first (<c>OSModes.Open("cad")</c>), then the
        /// design from the stored mocks, once the CAD session is ready. <paramref name="report"/> gets one plain sentence
        /// when that is done (or when it can't be).
        /// </summary>
        public static void OpenDesign(string part, Action<string> report)
        {
            if (string.IsNullOrEmpty(part))
            {
                report?.Invoke("This project has no design to open.");
                return;
            }
            bool opened;
            try { opened = OSModes.Open("cad"); }
            catch (Exception e)
            {
                Debug.LogException(e);
                opened = false;
            }
            if (!opened)
            {
                report?.Invoke("CAD couldn't open right now.");
                return;
            }
            MonoBehaviour host = OSShell.Current;  // any live object can run the wait; the shell is always there when a window is
            if (host == null || !host.isActiveAndEnabled)
            {
                report?.Invoke("CAD is open.");
                return;
            }
            host.StartCoroutine(OpenDesignWhenReady(part, report));
        }

        static IEnumerator OpenDesignWhenReady(string part, Action<string> report)
        {
            float until = Time.realtimeSinceStartup + 10f;
            CadSession session = null;
            while (Time.realtimeSinceStartup < until)
            {
                CadSession s = CadSession.Instance;
                if (s != null && s.Ready) { session = s; break; }
                yield return null;
            }
            if (session == null)
            {
                report?.Invoke("CAD is still starting. Tap Open design again in a moment.");
                yield break;
            }
            if (session.IsMock && (session.mocks == null || session.mocks.IndexOf(part) < 0))
            {
                report?.Invoke("There is no offline sample of the " + part + " design.");
                yield break;
            }
            try
            {
                session.OpenPart(part);
                report?.Invoke(session.IsMock
                    ? "CAD is open on the " + part + " design (the offline sample)."
                    : "CAD is open. Asked for the " + part + " design.");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                report?.Invoke("CAD is open, but the " + part + " design didn't load.");
            }
        }

        // ---------------------------------------------------------------- text helpers

        /// <summary>
        /// A tool's error as a plain sentence: a leading "code: " is dropped, the first letter is capitalised and a full
        /// stop is added. Never raw JSON.
        /// </summary>
        public static string Plain(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "That didn't work.";
            string m = message.Trim();
            if (m.StartsWith("{") || m.StartsWith("[")) return "That didn't work.";
            int colon = m.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && colon <= 28)
            {
                bool code = true;
                for (int i = 0; i < colon; i++)
                    if (!((m[i] >= 'a' && m[i] <= 'z') || m[i] == '_')) { code = false; break; }
                if (code && colon + 2 < m.Length) m = m.Substring(colon + 2).Trim();
            }
            if (m.Length == 0) return "That didn't work.";
            m = char.ToUpperInvariant(m[0]) + m.Substring(1);
            char last = m[m.Length - 1];
            if (last != '.' && last != '!' && last != '?') m += ".";
            return m;
        }
    }
}
