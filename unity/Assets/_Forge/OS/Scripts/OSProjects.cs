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
    /// The Projects tab. One way to start a project: the New project tile (Build something: a few questions, then a plan).
    /// The tab's first page is one row under "Your projects": New project and the person's three newest projects (every
    /// other folder under the notebook's <c>projects/</c> folder; the open one first). The rest is on the pages after it,
    /// each under its own heading: "All your projects" (the older ones), "Samples" (RC truck, Quadcopter, Rocket, Image
    /// classifier in one row: finished examples to look around, each tile saying what it shows in one short line), and
    /// Machine learning (<see cref="OSMLProjects"/>). A sample is never copied: its window's Make my own
    /// (<see cref="MakeOwn"/>) opens the same New project questions with the sample's kind already picked, so the person gets
    /// the choices and a real project with its own plan ("My RC truck", which follows the sample's loop). The
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

        /// <summary>The heading over the four samples on the Projects tab (a page of their own, one row).</summary>
        public const string SampleSection = "Samples";
        /// <summary>The heading of the tab's first page: New project and the person's newest projects.</summary>
        public const string OwnSection = "Your projects";
        /// <summary>The heading of the person's other projects, on the pages after the first.</summary>
        public const string MoreOwnSection = "All your projects";
        /// <summary>How many of the person's own projects the first page shows beside New project (one row of four).</summary>
        public const int OwnOnFirstPage = 3;
        /// <summary>The tag a sample's window and voice answers carry.</summary>
        public const string SampleTag = "Sample";
        /// <summary>What a sample is for, in one line (the window and the welcome card say it).</summary>
        public const string SampleLine = "A worked example to look around. Try any step; to keep your work, make your own.";
        /// <summary>The New project tile, first on the tab: it opens Build something. (The id is kept from the old Start my own tile.)</summary>
        public const string StartOwnId = "start_own";
        public const string NewProjectTitle = "New project";
        /// <summary>Old name of <see cref="NewProjectTitle"/>.</summary>
        public const string StartOwnTitle = NewProjectTitle;
        const string ReadyToStart = "Ready to start";

        sealed class Starter
        {
            public string id, title, icon, part;
            /// <summary>What this sample shows: the short tile line and the full sentence (window, welcome card, voice).</summary>
            public string tileLine, shows;
            /// <summary>The goal and test of the sample (what it is, in a line).</summary>
            public string goal, test;
            /// <summary>What Build something calls this kind (its first question's answer).</summary>
            public OSPlanner.Kind plannerKind;
            public OSProjectKind kind;
            /// <summary>Other names people use for it ("drone", "quad"), matched as whole words by <see cref="Resolve"/>.</summary>
            public string[] aliases;
            /// <summary>The <c>links.thing</c> of the courses that belong to it ("truck", "drone", "rocket"), or null.</summary>
            public string thing;
        }

        const float RescanSeconds = 4f;

        static readonly Starter[] starters =
        {
            new Starter { id = NotebookStore.DefaultProject, title = "RC truck", icon = "truck", kind = OSProjectKind.Truck, thing = "truck", plannerKind = OSPlanner.Kind.Truck,
                tileLine = "Design, drive, train", shows = "Design, course, drive, train an autopilot",
                goal = "An RC truck that does a slalom around the couch.", test = "Finish the slalom with no cones hit.",
                aliases = new[] { "truck", "rc truck", "rc car", "car", "slalom" } },
            new Starter { id = QuadcopterId, title = "Quadcopter", icon = "quadcopter", kind = OSProjectKind.Cad, part = "quadcopter", thing = "drone", plannerKind = OSPlanner.Kind.Drone,
                tileLine = "Design, fly, learn", shows = "A CAD design that flies, follows you and learns a course",
                goal = "A quadcopter that flies a hoop course in the room.", test = "Fly through every hoop and land.",
                aliases = new[] { "quadcopter", "quad", "drone", "quadrotor", "copter" } },
            new Starter { id = RocketId, title = "Rocket", icon = "rocket", kind = OSProjectKind.Cad, part = "rocket", thing = "rocket", plannerKind = OSPlanner.Kind.Rocket,
                tileLine = "Design, sim, launch", shows = "Design, simulate and launch in your room",
                goal = "A model rocket that flies straight and lands near the pad.", test = "A simulated flight that stays stable.",
                aliases = new[] { "rocket", "model rocket" } },
            new Starter { id = ClassifierId, title = "Image classifier", icon = "classifier", kind = OSProjectKind.Ml, plannerKind = OSPlanner.Kind.Classifier,
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
            recent.Clear();
            touched.Clear();
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
            ArtifactLibrary.LinkRule = ProjectForNew;  // a new artifact links to a project only when it belongs to it
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

            // A calm first page: one row under "Your projects", New project and the person's three newest projects (the open one
            // first); before the person has any, New project alone with the samples' row under it. Every heading after it starts a page of its own (the menu puts one heading on a page), so the rest is a
            // page turn away and never on screen with the first row: the person's other projects ("All your projects"), then
            // the four samples in one row ("Samples"), then one Machine learning page: the ML projects saved on the headset,
            // and (from OSMLProjects, registered after this source) the hub's or cloud's. Every entry stays in the registry,
            // so voice (os_open, project_open) and the checks find each one.
            entries.Add(new OSEntry
            {
                id = StartOwnId,
                title = NewProjectTitle,
                icon = "plus",
                tab = OSTab.Projects,
                // With no projects of the person's own yet, the samples share the first page (New project, then their row).
                section = folderIds.Count == 0 ? null : OwnSection,
                outline = true,
                status = () => NewProjectLine,
                open = StartOwn,
            });
            NewestFirst(folderIds, recent);
            for (int i = 0; i < recent.Count; i++)
                AddEntry(recent[i], TitleFor(recent[i]), IconFor(recent[i], "projects"), i < OwnOnFirstPage ? OwnSection : MoreOwnSection);
            for (int i = 0; i < starters.Length; i++) AddEntry(starters[i].id, starters[i].title, starters[i].icon, SampleSection);
            for (int i = 0; i < mlFolderIds.Count; i++) AddEntry(mlFolderIds[i], TitleFor(mlFolderIds[i]), "classifier", OSMLProjects.Section);
        }

        static readonly List<string> recent = new List<string>();
        static readonly List<KeyValuePair<string, DateTime>> touched = new List<KeyValuePair<string, DateTime>>();

        // The person's projects, the open one first, then the most recently written (its folder or the folders right inside it).
        // Read on a rescan only, never per frame.
        static void NewestFirst(List<string> ids, List<string> into)
        {
            into.Clear();
            touched.Clear();
            string root = Path.Combine(NotebookStore.Root ?? NotebookStore.DefaultRoot, "projects");
            for (int i = 0; i < ids.Count; i++)
            {
                DateTime when = DateTime.MinValue;
                try
                {
                    string folder = Path.Combine(root, ids[i]);
                    when = Directory.GetLastWriteTimeUtc(folder);
                    foreach (string sub in Directory.GetDirectories(folder))
                    {
                        DateTime t = Directory.GetLastWriteTimeUtc(sub);
                        if (t > when) when = t;
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                if (IsOpen(ids[i])) when = DateTime.MaxValue;
                touched.Add(new KeyValuePair<string, DateTime>(ids[i], when));
            }
            touched.Sort((a, b) =>
            {
                int byTime = b.Value.CompareTo(a.Value);
                return byTime != 0 ? byTime : StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key);
            });
            for (int i = 0; i < touched.Count; i++) into.Add(touched[i].Key);
        }

        /// <summary>The New project tile's line.</summary>
        public const string NewProjectLine = "Plan it with Albie";

        /// <summary>New project: the Build something card (the same flow as the menu strip's button).</summary>
        public static void StartOwn() => OSBuildFlow.Open();

        /// <summary>
        /// Make my own: the New project questions with this sample's kind already picked ("RC truck"), so the person answers
        /// the rest and Start makes their own project with a plan ("My RC truck"). False when it isn't a sample or the card
        /// can't open (a scene with no menu).
        /// </summary>
        public static bool MakeOwn(string sampleId)
        {
            var s = FindStarter(sampleId);
            if (s == null) return false;
            return OSBuildFlow.OpenFor(s.plannerKind) != null;
        }

        /// <summary>"Make my own RC truck": the sample window's one call to action. Null for a project that isn't a sample.</summary>
        public static string MakeOwnLabel(string sampleId)
        {
            var s = FindStarter(sampleId);
            return s == null ? null : "Make my own " + Lower(s.title);
        }

        // "RC truck" stays, "Quadcopter" becomes "quadcopter".
        static string Lower(string title) =>
            title.Length > 1 && char.IsUpper(title[1]) ? title : char.ToLowerInvariant(title[0]) + title.Substring(1);

        /// <summary>The sample Build something's kind follows (truck, drone, rocket, model), or null for "something else".</summary>
        public static string SampleForKind(OSPlanner.Kind kind)
        {
            for (int i = 0; i < starters.Length; i++)
                if (starters[i].plannerKind == kind) return starters[i].id;
            return null;
        }

        // A project of the person's own that follows a sample keeps the sample's icon; anything else gets the fallback.
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
            var starter = FindStarter(id) ?? FindStarter(CopyOf(id));  // a project that follows a sample works like the sample
            if (starter != null) return starter.kind;
            return ProjectMeta.Get(id, "kind") == "ml" || (id != null && id.StartsWith(MlPrefix, StringComparison.Ordinal))
                ? OSProjectKind.Ml : OSProjectKind.Generic;
        }

        /// <summary>The CAD design a sample (or a project that follows one) opens ("quadcopter", "rocket"), or null.</summary>
        public static string DesignOf(string id) => (FindStarter(id) ?? FindStarter(CopyOf(id)))?.part;

        // ---------------------------------------------------------------- sample projects

        /// <summary>True for the four sample projects (RC truck, Quadcopter, Rocket, Image classifier): finished examples to look around; Make my own starts the person's own.</summary>
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

        /// <summary>The sample a project of the person's own follows (<see cref="MarkOwn"/>; older ones were copies), or null.</summary>
        public static string CopyOf(string id) => string.IsNullOrEmpty(id) || FindStarter(id) != null ? null : ProjectMeta.Get(id, "copy_of");

        /// <summary>
        /// The id for a new project of the person's own that follows <paramref name="sampleId"/>: <c>proj_my_truck</c>, then
        /// <c>proj_my_truck_2</c>..., the first no folder, record or open project has. Nothing is written.
        /// </summary>
        public static string FreeOwnId(string sampleId)
        {
            var s = FindStarter(sampleId);
            if (s == null) return null;
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

        /// <summary>"My RC truck", "My quadcopter": the title of a project of the person's own that follows a sample.</summary>
        public static string OwnTitle(string sampleId)
        {
            var s = FindStarter(sampleId);
            return s == null ? null : "My " + Lower(s.title);
        }

        /// <summary>
        /// Records that <paramref name="id"/> is the person's own project following <paramref name="sampleId"/> (its title, the
        /// sample, and "ml" for the classifier), so it gets the sample's loop and design. False when it can't be written.
        /// </summary>
        public static bool MarkOwn(string id, string sampleId)
        {
            var s = FindStarter(sampleId);
            if (s == null || FindStarter(id) != null) return false;
            bool ok = ProjectMeta.Update(id, meta =>
            {
                meta["title"] = OwnTitle(sampleId);
                meta["copy_of"] = s.id;
                if (s.kind == OSProjectKind.Ml) meta["kind"] = "ml";
            });
            dirty = true;
            return ok;
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

        /// <summary>The sample a Build something kind follows: the truck, the quadcopter, the rocket or the classifier; null for
        /// "something else". Build something never writes into it: Start makes the person's own project that follows it.</summary>
        public static string StarterFor(OSPlanner.Kind kind) => SampleForKind(kind);

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
        /// Gets the notebook ready for an artifact made from the Artifacts tab. Only a course for a Thing can belong to a
        /// project: it stays with the project the person has open when that project runs the same Thing (a slalom for "My RC
        /// truck"); with no project chosen it goes to the sample it belongs to (the slalom to the RC truck, a drone course to
        /// the Quadcopter, the launch pad to the Rocket). Everything else (a heart, a sim, repo activity) is made standalone, and
        /// nothing is opened or closed for it. Returns the project it will link to, or null. The library applies the same rule
        /// (<see cref="ProjectForNew"/>).
        /// </summary>
        public static string PrepareForArtifact(string template)
        {
            string thing = ThingOfTemplate(template);
            if (thing == null) return null;
            if (ChosenOpen) return ThingOfProject(NotebookStore.ProjectId) == thing ? NotebookStore.ProjectId : null;
            string starter = StarterForThing(thing);
            if (starter == null) return null;
            if (EnsureOpen(starter, out string error)) return starter;
            Debug.Log("[OS] Couldn't open " + starter + " for the new artifact: " + error);
            return null;
        }

        /// <summary>
        /// The Thing a project runs ("truck", "drone", "rocket"): its sample's, or the one its own sample follows; for any other
        /// project that is open, the Thing of the course its plan names. Null when it runs none (the classifier, most others).
        /// </summary>
        public static string ThingOfProject(string id)
        {
            var starter = FindStarter(id) ?? FindStarter(CopyOf(id));
            if (starter != null) return starter.thing;
            if (!IsOpen(id)) return null;
            JToken course = NotebookStore.CurrentPlan?["course"];
            return course != null && course.Type == JTokenType.String ? ThingOfTemplate((string)course) : null;
        }

        /// <summary>The Thing an artifact is a course for: its <c>links.thing</c>, or what its template is for (the launch pad: "rocket"); null for none.</summary>
        public static string ThingOfArtifact(JObject manifest)
        {
            JToken thing = manifest?["links"]?["thing"];
            if (thing != null && thing.Type == JTokenType.String && !string.IsNullOrEmpty((string)thing)) return (string)thing;
            JToken template = manifest?["template"];
            if (template == null || template.Type != JTokenType.String) return null;
            string name = (string)template;
            if (name == OSProjectWindow.RocketLaunchTemplate) return "rocket";
            return ArtifactCourses.IsTemplate(name) ? ArtifactCourses.ThingOf(name) : null;
        }

        /// <summary>
        /// The project a saved artifact really belongs to, from its manifest: its <c>links.project</c>, except that an older
        /// artifact linked to a sample it has nothing to do with (a heart made while the app's RC truck notebook was open says
        /// proj_truck) counts as standalone. Only a course for the sample's own Thing belongs to a sample. Nothing is rewritten.
        /// </summary>
        public static string EffectiveProject(JObject manifest)
        {
            JToken link = manifest?["links"]?["project"];
            if (link == null || link.Type != JTokenType.String) return null;
            string project = (string)link;
            if (string.IsNullOrEmpty(project)) return null;
            var starter = FindStarter(project);
            if (starter == null) return project;
            string thing = ThingOfArtifact(manifest);
            return thing != null && thing == starter.thing ? project : null;
        }

        /// <summary>
        /// The library's linking rule (<see cref="ArtifactLibrary.LinkRule"/>), for a new artifact: the project a Project
        /// window step is making it for (<see cref="ArtifactLibrary.ForProject{T}"/>); otherwise, only while the person is
        /// working in a project they opened themselves (not the notebook the app opens at start), a course for that project's
        /// Thing, or an artifact Albie wrote that asks for the project (kind "project" or links.project naming it). Everything
        /// else is standalone: a biology model, an imported model, a sim or a chart made while a project is open is not that
        /// project's.
        /// </summary>
        public static string ProjectForNew(JObject manifest)
        {
            if (!NotebookStore.IsOpen) return null;
            string open = NotebookStore.ProjectId;
            string scope = ArtifactLibrary.ScopeProject;
            if (scope != null) return scope == open ? open : null;
            if (!ChosenOpen) return null;
            string thing = ThingOfArtifact(manifest);
            if (thing != null) return thing == ThingOfProject(open) ? open : null;
            JToken asked = manifest?["links"]?["project"];
            bool namesIt = asked != null && asked.Type == JTokenType.String && (string)asked == open;
            bool isTemplate = manifest?["template"]?.Type == JTokenType.String;
            return !isTemplate && (namesIt || manifest?.Value<string>("kind") == "project") ? open : null;
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
            // "My RC truck" is the person's own project, not the sample: an exact title of their own project wins before "my" is dropped.
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
                if (EffectiveProject(manifest) != projectId) continue;  // an old artifact linked to a sample by accident isn't the sample's

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
        /// Makes this project the open one, puts up its project bar (<see cref="OSProjectRail"/>) on the stage it was left at,
        /// and shows its window. The notebook is switched with <c>NotebookStore.Open</c> directly (never closed first, so a
        /// project is always open). Nothing is opened by itself. Nothing here throws, with the hub off or on.
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
            // The project bar comes up on the stage it was left at (or the first one that needs doing), and the project
            // window shows where it stands. No tool is opened by itself: the window's Next (or a stage) does that.
            OSProjectSession.Ensure().Begin(id);
            OSProjectWindow.Show(id);
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
            // The body's own scaled values, so a desk course (truck at 1:4 to 1:10) asks about the truck's real sphere.
            Vector3 centre = at.position + at.rotation * Vector3.up * brain.Body.SenseHeight;
            int n = brain.gameObject.scene.GetPhysicsScene().OverlapSphere(centre, brain.Body.CrashRadius, startBlockers, brain.Body.obstacles, QueryTriggerInteraction.Ignore);
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
