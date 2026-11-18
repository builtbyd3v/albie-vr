using System.Collections.Generic;
using System.IO;
using System.Text;
using Forge.Artifacts;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Forge.OS
{
    /// <summary>
    /// The Artifacts tab. It lists what is in the artifact library (each with the project it is for and the latest run
    /// result), then a "New" section with one tile per template. Every open, create and close goes through the same
    /// Albie tools the voice uses (artifact_open, artifact_create, artifact_close), so placement, the first-time Allow
    /// sheet and FollowsUser behave exactly as by voice. It also makes whatever the artifact host shows movable and
    /// tracked by the window manager (see <see cref="AdoptPanels"/>), so Bring windows here and Close all reach it.
    /// The list is cached and rebuilt only when something changed, never per frame.
    /// </summary>
    public static class OSArtifacts
    {
        /// <summary>The section heading the template tiles sit under.</summary>
        public const string Section = "New";
        /// <summary>The three-body simulation is bundled but is not a template: it opens, it is not created.</summary>
        public const string ThreeBodyId = "three_body";

        sealed class Row
        {
            public string id, title, template, project, projectName, runWord;
            public bool room;
        }

        // What the template tiles say. A template added later falls back to its manifest title.
        static readonly Dictionary<string, string> FriendlyTitles = new Dictionary<string, string>
        {
            { "slalom", "Slalom" },
            { "hoop_course", "Hoop course" },
            { "dash", "Dash" },
            { "park", "Parking" },
            { "hover", "Hover" },
            { "landing", "Landing" },
            { "repo_activity", "What shipped?" },
            { "cell", "Cell" },
            { "heart", "Heart" },
            { "brain", "Brain" },
            { "plant_cell", "Plant cell" },
            { "eye", "Eye" },
            { "dna", "DNA" },
            { "double_pendulum", "Double pendulum" },
            { "solar_system", "Solar system" },
            { ThreeBodyId, "Three-body" },
        };

        // Room artifacts stand on the real floor where the room code puts them; the rest open as windows.
        static readonly HashSet<string> RoomTemplates = new HashSet<string> { "slalom", "hoop_course", "dash", "park", "hover", "landing" };

        /// <summary>The tile that leads to a course or artifact of the user's own: Albie asks for the idea and makes it.</summary>
        public const string OwnId = "new_own";
        /// <summary>What Albie is told when the tile is tapped (the voice model says it in its own words, then listens).</summary>
        public const string OwnPrompt =
            "The user tapped Make your own in the Artifacts tab. Ask them in one short sentence what they want made. "
            + "For a course, find out what it is for (the truck or the drone), where (the floor or their table) and what is on it "
            + "(a straight, cones to weave, gates, hoops, a spot to park or land on, a box to hover in). "
            + "Then make it with artifact_create: a course goes in 'course' as a list of elements, never as a scene.";

        static readonly List<OSEntry> cached = new List<OSEntry>();
        static readonly Dictionary<string, string> projectNames = new Dictionary<string, string>();  // one rebuild's answers (OSRegistry.Find allocates)
        static readonly Dictionary<string, string> notices = new Dictionary<string, string>();
        static readonly List<ForgePanel> panelScratch = new List<ForgePanel>();
        static readonly List<ForgeVolume> volumeScratch = new List<ForgeVolume>();
        static readonly StringBuilder line = new StringBuilder();

        static System.Action<List<OSEntry>> source;
        static bool registered, dirty = true, rebuilding;
        static int windowCounter;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            cached.Clear();
            projectNames.Clear();
            notices.Clear();
            panelScratch.Clear();
            volumeScratch.Clear();
            line.Length = 0;
            source = null;
            registered = false;
            dirty = true;
            rebuilding = false;
            windowCounter = 0;
        }

        /// <summary>Adds the Artifacts source to the registry (the app does this at start; a check can call it by hand). Safe to call twice.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register()
        {
            if (registered) return;
            registered = true;
            source = Fill;
            OSRegistry.RegisterSource(OSTab.Artifacts, source);
            OSRegistry.Changed -= MarkDirty;
            OSRegistry.Changed += MarkDirty;
            NotebookStore.EntryUpdated -= OnNotebookEntry;
            NotebookStore.EntryUpdated += OnNotebookEntry;
        }

        /// <summary>Takes the Artifacts source out again (a check that lists its own sample artifacts). <see cref="Register"/> puts it back.</summary>
        public static void Unregister()
        {
            if (!registered) return;
            registered = false;
            OSRegistry.UnregisterSource(source);
            OSRegistry.Changed -= MarkDirty;
            NotebookStore.EntryUpdated -= OnNotebookEntry;
            source = null;
        }

        // The watcher that tracks the host is added to any scene with a Workspace, the way ArtifactBootstrap adds the host.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureWatcher();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => EnsureWatcher();

        static void EnsureWatcher()
        {
            if (Object.FindAnyObjectByType<OSArtifactWatcher>(FindObjectsInactive.Include) != null) return;
            if (Object.FindAnyObjectByType<Workspace>() == null) return;
            var go = new GameObject("OSArtifactWatcher");
            go.AddComponent<OSArtifactWatcher>();
        }

        static void MarkDirty() => dirty = true;

        // A run finished and was attached to the notebook: the latest result on the tile changes.
        static void OnNotebookEntry(JObject entry)
        {
            dirty = true;
            OSRegistry.NotifyChanged();
        }

        /// <summary>Something about the host changed (it opened, closed or rebuilt): re-read the list and adopt its panels.</summary>
        internal static void OnHostChanged(ArtifactHost host)
        {
            dirty = true;
            AdoptPanels(host);
            OSRegistry.NotifyChanged();
        }

        internal static void OnShown(ArtifactHost host) => OnHostChanged(host);

        // ---------------------------------------------------------------- the list

        static void Fill(List<OSEntry> into)
        {
            if (dirty && !rebuilding) Rebuild();
            into.AddRange(cached);
        }

        static void Rebuild()
        {
            rebuilding = true;
            cached.Clear();
            projectNames.Clear();
            try
            {
                var rows = new List<Row>();
                JArray list = ArtifactLibrary.List();
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i] as JObject;
                    if (item == null) continue;
                    string id = (string)item["id"];
                    if (string.IsNullOrEmpty(id)) continue;
                    rows.Add(ReadRow(id, item));
                }
                rows.Sort((a, b) => string.Compare(a.title, b.title, System.StringComparison.OrdinalIgnoreCase));

                bool hasThreeBody = false;
                for (int i = 0; i < rows.Count; i++)
                {
                    cached.Add(ExistingEntry(rows[i]));
                    if (rows[i].id == ThreeBodyId) hasThreeBody = true;
                }
                AddNewTiles(hasThreeBody);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                rebuilding = false;
                dirty = false;
            }
        }

        static Row ReadRow(string id, JObject item)
        {
            var row = new Row
            {
                id = id,
                title = string.IsNullOrEmpty((string)item["title"]) ? Pretty(id) : (string)item["title"],
                template = (string)item["template"],
            };

            // ArtifactLibrary.List() has no links, so read the project this one is for from its manifest.
            try
            {
                string path = Path.Combine(ArtifactLibrary.Folder(id), "manifest.json");
                if (File.Exists(path) && NotebookFiles.Parse(File.ReadAllText(path)) is JObject manifest && manifest["links"] is JObject)
                {
                    // An older artifact linked to a sample it has nothing to do with (a heart that says proj_truck because the
                    // app's truck notebook was open) shows as standalone: never "for RC truck" on something unrelated.
                    string project = OSProjects.EffectiveProject(manifest);
                    if (!string.IsNullOrEmpty(project))
                    {
                        row.project = project;
                        row.projectName = ProjectName(project);
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.Log("[OS] Couldn't read the links of artifact '" + id + "': " + e.Message);
            }

            string key = row.template ?? id;
            row.room = FriendlyTitles.ContainsKey(key) ? RoomTemplates.Contains(key) : SpaceOf(id) == "room";

            JObject newest = NewestRun(id, null);
            if (newest != null) row.runWord = RunWord((string)newest["outcome"]);
            return row;
        }

        /// <summary>
        /// The newest run record of an artifact, or the newest one made for <paramref name="projectId"/> when that is given.
        /// <see cref="ArtifactRuns.List"/> reads and parses every run file; the lists only want the last one, so this walks
        /// the run files newest first (by their number) and stops at the first valid match. Null when there is none.
        /// </summary>
        public static JObject NewestRun(string artifactId, string projectId)
        {
            if (string.IsNullOrEmpty(artifactId)) return null;
            string folder = ArtifactRuns.Folder(artifactId);
            string[] files;
            try
            {
                if (!Directory.Exists(folder)) return null;
                files = Directory.GetFiles(folder, "run_*.json");
            }
            catch (System.Exception e) when (e is IOException || e is System.UnauthorizedAccessException) { return null; }

            var numbers = new int[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]);
                numbers[i] = name.Length > 4 && int.TryParse(name.Substring(4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : 0;
            }
            System.Array.Sort(numbers, files);  // oldest first, the same order ArtifactRuns.List uses
            for (int i = files.Length - 1; i >= 0; i--)
            {
                JObject record = ArtifactRuns.Load(artifactId, Path.GetFileNameWithoutExtension(files[i]));
                if (record == null) continue;  // damaged or another schema: skipped, as List skips it
                if (projectId != null && (string)record["project_id"] != projectId) continue;
                return record;
            }
            return null;
        }

        static string SpaceOf(string id)
        {
            try
            {
                ArtifactPackage package = ArtifactPackage.Load(id, out _);
                return package?.Scene?.Value<string>("space");
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        static string RunWord(string outcome)
        {
            switch (outcome)
            {
                case "completed": return "passed";
                case "failed": return "failed";
                case "aborted": return "stopped";
                default: return string.IsNullOrEmpty(outcome) ? null : outcome;
            }
        }

        // The project's display name from the Projects tab, so the tile says "for RC truck" and not "for proj_truck".
        static string ProjectName(string projectId)
        {
            if (projectNames.TryGetValue(projectId, out string known)) return known;
            string name = ProjectNameUncached(projectId);
            projectNames[projectId] = name;
            return name;
        }

        static string ProjectNameUncached(string projectId)
        {
            OSEntry entry = OSRegistry.Find(OSTab.Projects, projectId);
            if (entry != null && !string.IsNullOrEmpty(entry.title)) return entry.title;
            if (projectId == NotebookStore.DefaultProject) return "RC truck";
            return Pretty(projectId.StartsWith("proj_") ? projectId.Substring(5) : projectId);
        }

        static string Pretty(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string text = id.Replace('_', ' ');
            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        static OSEntry ExistingEntry(Row row)
        {
            return new OSEntry
            {
                id = row.id,
                title = row.title,
                icon = IconFor(row.template ?? row.id),
                tab = OSTab.Artifacts,
                status = () => StatusFor(row),
                isOpen = () => IsOpen(row.id),
                open = () => Toggle(row.id),
            };
        }

        static void AddNewTiles(bool hasThreeBody)
        {
            foreach (string template in ArtifactLibrary.Templates)
            {
                AddNewTile(template);
                // The other courses sit with the slalom and the hoops: the truck's dash and parking, the drone's hover and landing.
                if (template == "hoop_course")
                    foreach (string course in ArtifactCourses.Templates) AddNewTile(course);
            }

            // The user's own idea: Albie asks what they want and makes it (a course from a description, or anything else).
            cached.Add(new OSEntry
            {
                id = OwnId,
                title = "Make your own",
                icon = "plus",
                tab = OSTab.Artifacts,
                section = Section,
                outline = true,
                status = () => NoticeOr(OwnId, "tell Albie your idea"),
                open = () => MakeYourOwn(),
            });

            // Three-body is a bundled simulation, not a template: it can't be created, only opened. Listed only if it opens here.
            if (!hasThreeBody && ThreeBodyWorks())
            {
                cached.Add(new OSEntry
                {
                    id = ThreeBodyId,
                    title = FriendlyTitle(ThreeBodyId),
                    icon = IconFor(ThreeBodyId),
                    tab = OSTab.Artifacts,
                    section = Section,
                    outline = true,
                    status = () => NoticeOr(ThreeBodyId, IsOpen(ThreeBodyId) ? "open" : "opens as a window"),
                    isOpen = () => IsOpen(ThreeBodyId),
                    open = () => Toggle(ThreeBodyId),
                });
            }
        }

        static void AddNewTile(string name)
        {
            string key = "new_" + name;
            bool room = RoomTemplates.Contains(name);
            cached.Add(new OSEntry
            {
                id = key,
                title = FriendlyTitle(name),
                icon = IconFor(name),
                tab = OSTab.Artifacts,
                section = Section,
                outline = true,
                status = () => NoticeOr(key, room ? "on your floor" : "opens as a window"),
                open = () => Create(name),
            });
        }

        /// <summary>
        /// The Make your own tile: Albie asks the user for their idea out loud and listens (the same albie.say Build
        /// something uses for its questions), then makes it with artifact_create. There is no keyboard: with the voice
        /// off the tile says so. Null when Albie was asked, else why not.
        /// </summary>
        public static string MakeYourOwn()
        {
            notices.Clear();
            if (!OSBuildFlow.VoiceLive)
            {
                notices[OwnId] = "needs Albie's voice on";
                Refresh();
                return "Albie's voice is off, so he can't hear your idea. Turn it on in Settings.";
            }
            if (Application.isPlaying) ForgeNet.Send("albie.say", new JObject { ["text"] = OwnPrompt });
            notices[OwnId] = "Albie is listening";
            Refresh();
            return null;
        }

        static bool ThreeBodyWorks()
        {
            try
            {
                ArtifactPackage package = ArtifactPackage.Load(ThreeBodyId, out _);
                if (package == null) return false;
                if (ArtifactValidator.Validate(package.Manifest, package.Scene).Count > 0) return false;
                return ArtifactNodes.Missing(package.Scene).Count == 0;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        static string FriendlyTitle(string template)
        {
            if (FriendlyTitles.TryGetValue(template, out string title)) return title;
            if (ArtifactLibrary.TryTemplate(template, out JObject manifest, out _) && !string.IsNullOrEmpty((string)manifest["title"]))
                return (string)manifest["title"];
            return Pretty(template);
        }

        static string IconFor(string key)
        {
            switch (key)
            {
                case "slalom":
                case "dash":
                case "park":
                case "dash_desk":
                case "park_desk":
                case "slalom_desk": return "slalom";
                case "hoop_course":
                case "hover":
                case "landing":
                case "hover_desk":
                case "landing_desk":
                case "hoop_course_desk": return "hoops";
                case "repo_activity": return "repo";
                case "cell":
                case "heart":
                case "brain":
                case "plant_cell":
                case "eye":
                case "dna": return "biology";
                case "double_pendulum":
                case "solar_system":
                case ThreeBodyId: return "physics";
                default: return "artifact";
            }
        }

        static string NoticeOr(string key, string fallback) => notices.TryGetValue(key, out string notice) ? notice : fallback;

        // One line under an artifact's title: what it is doing now, who it is for, how the last run went, where it stands.
        static string StatusFor(Row row)
        {
            if (notices.TryGetValue(row.id, out string notice)) return notice;
            ArtifactHost host = ArtifactHost.Current;
            if (host != null && host.AwaitingApproval && OpenId() == row.id) return "tap Allow on the sheet";

            // The tile has room for about two dozen characters, so the parts are taken in order of importance until they stop
            // fitting, then written in reading order: open, who it is for, the last run, where it stands.
            string forWho = row.project != null ? "for " + row.projectName : null;
            string open = IsOpen(row.id) ? "open" : null;
            string floor = row.room ? "on your floor" : null;
            int used = 0;
            bool takeFor = Fits(ref used, forWho), takeOpen = Fits(ref used, open), takeRun = Fits(ref used, row.runWord), takeFloor = Fits(ref used, floor);

            line.Length = 0;
            Part(takeOpen ? open : null);
            Part(takeFor ? forWho : null);
            Part(takeRun ? row.runWord : null);
            Part(takeFloor ? floor : null);
            return line.ToString();
        }

        const int StatusChars = 24;

        static bool Fits(ref int used, string text)
        {
            if (text == null) return false;
            int add = text.Length + (used > 0 ? 3 : 0);
            if (used + add > StatusChars) return false;
            used += add;
            return true;
        }

        static void Part(string text)
        {
            if (text == null) return;
            if (line.Length > 0) line.Append(" · ");
            line.Append(text);
        }

        // ---------------------------------------------------------------- what a tap does

        /// <summary>The id of the artifact showing now (or waiting on its Allow sheet), or null.</summary>
        public static string OpenId()
        {
            ArtifactHost host = ArtifactHost.Current;
            if (host == null) return null;
            string id = host.OpenId;
            if (id != null) return id;
            return host.AwaitingApproval ? host.OpenManifest?.Value<string>("id") : null;
        }

        /// <summary>True when this artifact is the one showing (or waiting for its Allow tap).</summary>
        public static bool IsOpen(string id) => !string.IsNullOrEmpty(id) && OpenId() == id;

        /// <summary>A tap on an existing artifact: closes it if it is the one open, opens it otherwise. Null on success, else what went wrong.</summary>
        public static string Toggle(string id) => IsOpen(id) ? CloseArtifact() : Open(id);

        /// <summary>Opens an artifact through the artifact_open tool (placement, the Allow sheet and FollowsUser as by voice). Null on success, else what went wrong.</summary>
        public static string Open(string id)
        {
            notices.Clear();
            JObject result = AlbieTools.Call("artifact_open", new JObject { ["id"] = id });
            string problem = ProblemOf(result);
            if (problem != null)
            {
                Fail("Couldn't open it", problem, id);
                return problem;
            }
            Succeeded(id, result);
            return null;
        }

        /// <summary>Closes the open artifact through the artifact_close tool. Null on success.</summary>
        public static string CloseArtifact()
        {
            notices.Clear();
            JObject result = AlbieTools.Call("artifact_close", new JObject());
            string problem = ProblemOf(result);
            if (problem != null)
            {
                Fail("Couldn't close it", problem, OpenId() ?? "");
                return problem;
            }
            Refresh();
            return null;
        }

        /// <summary>
        /// Makes an artifact from a template and opens it. Only a course links to a project (the slalom for the truck keeps
        /// links.thing and the brain truck appears); a heart or a sim is standalone whatever project is open. Null on success, else
        /// what went wrong: "couldn't make it" when nothing was written, "made it, but it wouldn't open" when it was.
        /// </summary>
        public static string Create(string template)
        {
            notices.Clear();
            string tile = "new_" + template;
            EnsureProjectOpen(template);

            // Created without opening, so an error here means nothing was written. (artifact_create with open:true loses the id
            // when the open is refused after the write.) Opening is the same artifact_open the voice path ends with.
            JObject made = AlbieTools.Call("artifact_create", new JObject { ["template"] = template, ["open"] = false });
            string problem = ProblemOf(made);
            if (problem != null)
            {
                Fail("Couldn't make it", problem, tile);
                return problem;
            }
            string id = (string)made["id"] ?? template;
            dirty = true;

            JObject opened = AlbieTools.Call("artifact_open", new JObject { ["id"] = id });
            problem = ProblemOf(opened);
            if (problem != null)
            {
                Fail("Made it, but it wouldn't open", problem, id, tile);
                return "Made it, but it wouldn't open: " + problem;
            }
            Succeeded(id, opened);
            return null;
        }

        // Only a course can belong to a project: the person's open project when it runs that Thing, or (with none chosen) the
        // sample the course belongs to. Anything else is standalone, never linked by default to the truck (OSProjects.PrepareForArtifact).
        static void EnsureProjectOpen(string template)
        {
            try
            {
                OSProjects.PrepareForArtifact(template);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }

        static string ProblemOf(JObject result)
        {
            if (result == null) return "Albie's tools aren't ready yet.";
            string error = (string)result["error"];
            if (error != null) return Reason(error);
            // A course that didn't compile comes back as data (status invalid, nothing saved), not as an error string.
            if ((string)result["status"] == "invalid")
                return result["errors"] is JArray errors && errors.Count > 0 ? (string)errors[0]["message"] ?? "it wasn't valid." : "it wasn't valid.";
            return null;
        }

        // "no_floor: there is no free floor" becomes "there is no free floor".
        static string Reason(string error)
        {
            if (string.IsNullOrEmpty(error)) return "something went wrong.";
            int colon = error.IndexOf(':');
            if (colon > 0 && error.IndexOf(' ') == colon + 1) return error.Substring(colon + 2);
            return error;
        }

        static void Fail(string lead, string problem, params string[] keys)
        {
            string text = Short(lead + ": " + problem);
            for (int i = 0; i < keys.Length; i++)
                if (!string.IsNullOrEmpty(keys[i])) notices[keys[i]] = text;
            Debug.Log("[OS] " + lead + ": " + problem);
            OSSounds.Error();
            Refresh();
        }

        // The open worked; Albie's own line about the layout (a shrunk course, a missing couch) shows under the tile.
        static void Succeeded(string id, JObject result)
        {
            string say = (string)result["say"];
            // Where the host landed ("It's on the table to your right.") is for the voice and the project window: the tile keeps
            // its own line (open, who it is for), because the person sees the artifact in front of them.
            ArtifactHost host = ArtifactHost.Current;
            string placed = host != null ? host.PlacementNote : null;
            if (!string.IsNullOrEmpty(say) && !string.IsNullOrEmpty(placed) && say.EndsWith(placed, System.StringComparison.Ordinal))
                say = say.Substring(0, say.Length - placed.Length).TrimEnd();
            if (!string.IsNullOrEmpty(say)) notices[id] = Short(say);
            Refresh();
        }

        static string Short(string text) => text.Length <= 80 ? text : text.Substring(0, 77) + "...";

        static void Refresh()
        {
            dirty = true;
            OSRegistry.NotifyChanged();
        }

        // ---------------------------------------------------------------- movable artifact panels

        /// <summary>
        /// Makes what the host shows movable and tracked: every ForgePanel under the host gets a PanelManipulator (Init'd with the
        /// head and the desk frame, as GesturePanel does), every ForgeVolume a VolumeManipulator, and each is adopted into the
        /// window manager so Bring windows here and Close all reach it. Closing any of them closes the artifact. Safe to call
        /// again: what is already adopted is left alone. A room course itself (unparented, on the floor) is not a window, nor
        /// is its marker or a volume's close chip.
        /// </summary>
        internal static void AdoptPanels(ArtifactHost host)
        {
            OSShell shell = OSShell.Current;
            if (shell == null || host == null) return;
            OSWindows windows = shell.Windows;
            if (windows == null) return;
            TrackWhole(host, windows);
            Transform head = OSShell.FindHead();
            if (head == null) return;
            Transform frame = DeskFrame(head);
            string artifactId = OpenId();

            host.GetComponentsInChildren<ForgePanel>(true, panelScratch);
            for (int i = 0; i < panelScratch.Count; i++)
            {
                ForgePanel panel = panelScratch[i];
                if (panel == null || panel.GetComponent<OSWindow>() != null) continue;
                // A volume's close chip is part of the volume, not a window to move about (and the course marker isn't under the host).
                if (panel.GetComponentInParent<ForgeVolumeChrome>() != null || panel.GetComponent<ArtifactCourseMarker>() != null) continue;
                PanelManipulator handles = panel.GetComponent<PanelManipulator>();
                if (handles == null) handles = panel.gameObject.AddComponent<PanelManipulator>();
                handles.Init(head, frame);
                GameObject root = panel.gameObject;
                OSWindow adopted = windows.Adopt(NextWindowId(artifactId), panel.Title, root, () => CloseFromWindow(host, root));
                if (adopted != null) windows.ClearOf(adopted, host.transform);  // the host put it down without knowing what else stands there
            }
            panelScratch.Clear();

            host.GetComponentsInChildren<ForgeVolume>(true, volumeScratch);
            for (int i = 0; i < volumeScratch.Count; i++)
            {
                ForgeVolume volume = volumeScratch[i];
                if (volume == null || volume.GetComponent<OSWindow>() != null) continue;
                VolumeManipulator grips = volume.GetComponent<VolumeManipulator>();
                if (grips == null) grips = volume.gameObject.AddComponent<VolumeManipulator>();
                grips.Init(head, frame);
                GameObject root = volume.gameObject;
                windows.Adopt(NextWindowId(artifactId), volume.Title, root, () => CloseFromWindow(host, root));
            }
            bool hadVolume = volumeScratch.Count > 0;
            volumeScratch.Clear();
            // A model stands there now, labels and all: windows that were already open and cover it move aside.
            if (hadVolume) windows.ClearAllSoon(host.transform);
        }

        /// <summary>The window id the open artifact as a whole is tracked under (see <see cref="TrackWhole"/>), plus its id.</summary>
        public const string WholePrefix = "open-artifact:";

        static readonly List<OSWindow> wholeScratch = new List<OSWindow>();

        /// <summary>
        /// The open artifact (or the one waiting on its Allow sheet) as one group entry in the window manager, whatever it put
        /// in the world: its panels and volume are adopted one by one below, but a room course stands on the floor outside the
        /// host and its truck or drone are separate objects, and a course with no panel left nothing in the window list at
        /// all, so Close all never closed it (the bug the headset showed). Closing the entry closes the artifact, which takes
        /// its course down and puts its Thing away. An entry for an artifact that is no longer the open one is let go of.
        /// Its id does not start with "artifact:" (those are the movable panels and volumes).
        /// </summary>
        static void TrackWhole(ArtifactHost host, OSWindows windows)
        {
            string id = OpenId();
            string want = id != null ? WholePrefix + id : null;
            wholeScratch.Clear();
            for (int i = 0; i < windows.All.Count; i++)
            {
                OSWindow w = windows.All[i];
                if (w != null && w.IsGroup && w.Id.StartsWith(WholePrefix, System.StringComparison.Ordinal) && w.Id != want) wholeScratch.Add(w);
            }
            for (int i = 0; i < wholeScratch.Count; i++) wholeScratch[i].Detach();
            wholeScratch.Clear();
            if (want == null || windows.IsOpen(want)) return;
            string title = host.OpenTitle;
            windows.Adopt(want, string.IsNullOrEmpty(title) ? id : title, null, () =>
            {
                if (OpenId() == id) CloseArtifact();  // already closed by one of its own windows: nothing to do
            });
        }

        // A fresh id each time: a rebuilt artifact's old windows are destroyed at the end of the frame, so a reused id would still find them.
        static string NextWindowId(string artifactId)
        {
            windowCounter++;
            return "artifact:" + (string.IsNullOrEmpty(artifactId) ? "sheet" : artifactId) + ":" + windowCounter;
        }

        // Closing one window closes the artifact. A window left over from an artifact that is already gone (detached by the host's
        // teardown) must not close the next one.
        static void CloseFromWindow(ArtifactHost host, GameObject root)
        {
            if (host == null || root == null || !root.transform.IsChildOf(host.transform)) return;
            CloseArtifact();
        }

        static Transform DeskFrame(Transform head)
        {
            Workspace workspace = Workspace.Current;
            return workspace != null && workspace.presence != null && workspace.presence.Frame != null ? workspace.presence.Frame : head;
        }
    }
}
