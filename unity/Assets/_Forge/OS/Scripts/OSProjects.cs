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
    /// The Projects tab. Registers a source with <see cref="OSRegistry"/> that lists the built-in starters (RC truck,
    /// Quadcopter, Rocket, Image classifier) and every other folder under the notebook's <c>projects/</c> folder. The
    /// folder listing and each project's status line are cached: they are read again when something asks (a registry
    /// change, a notebook write, <see cref="Refresh"/>) or after a few seconds, never every frame. A project is open when
    /// <see cref="NotebookStore.ProjectId"/> matches; <see cref="OpenProject"/> always leaves a project open (it switches
    /// with <c>NotebookStore.Open</c> and never closes first) and shows the Project window.
    /// </summary>
    public static class OSProjects
    {
        /// <summary>The honest line shown for a project whose data lives in the cloud when the cloud isn't reachable.</summary>
        public const string NeedsCloud = "Needs Albie's cloud to load the dataset. Not available offline.";
        /// <summary>Why the Send action is off: nothing on the device can send the truck model.</summary>
        public const string SendUnavailable = "Needs Albie's cloud messenger to send the model. Not available offline.";

        /// <summary>One artifact whose manifest links to a project, with its newest run for that project.</summary>
        public sealed class LinkedArtifact
        {
            public string id, title, template, thing;
            public int version;
            /// <summary>Newest run record for this project, or null (no run yet).</summary>
            public string runId, runOutcome, runReason;
            public float? runSeconds;
        }

        sealed class Starter
        {
            public string id, title, icon, part;
            public OSProjectKind kind;
        }

        const float RescanSeconds = 4f;

        static readonly Starter[] starters =
        {
            new Starter { id = NotebookStore.DefaultProject, title = "RC truck", icon = "truck", kind = OSProjectKind.Truck },
            new Starter { id = "proj_quadcopter", title = "Quadcopter", icon = "quadcopter", kind = OSProjectKind.Cad, part = "quadcopter" },
            new Starter { id = "proj_rocket", title = "Rocket", icon = "rocket", kind = OSProjectKind.Cad, part = "rocket" },
            new Starter { id = "proj_classifier", title = "Image classifier", icon = "classifier", kind = OSProjectKind.Ml },
        };

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
            scratchLinked.Clear();
            dirty = true;
            lastScan = -1000f;
        }

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

            try
            {
                string dir = Path.Combine(NotebookStore.Root ?? NotebookStore.DefaultRoot, "projects");
                if (Directory.Exists(dir))
                    foreach (string folder in Directory.GetDirectories(dir))
                    {
                        string name = Path.GetFileName(folder);
                        if (!string.IsNullOrEmpty(name) && FindStarter(name) == null) folderIds.Add(name);
                    }
                folderIds.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.Log("[OS] Couldn't list the project folders: " + e.Message);
            }

            for (int i = 0; i < starters.Length; i++) AddEntry(starters[i].id, starters[i].title, starters[i].icon);
            for (int i = 0; i < folderIds.Count; i++) AddEntry(folderIds[i], Pretty(folderIds[i]), "projects");
        }

        static void AddEntry(string id, string title, string icon)
        {
            statusById[id] = ComputeStatus(id, null);
            string captured = id;
            entries.Add(new OSEntry
            {
                id = id,
                title = title,
                icon = icon,
                tab = OSTab.Projects,
                status = () => statusById.TryGetValue(captured, out string s) ? s : "Ready to start",
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

        /// <summary>What the project is about: the truck, a CAD design, the ML project, or anything else.</summary>
        public static OSProjectKind KindOf(string id) => FindStarter(id)?.kind ?? OSProjectKind.Generic;

        /// <summary>The CAD design a starter opens ("quadcopter", "rocket"), or null.</summary>
        public static string DesignOf(string id) => FindStarter(id)?.part;

        /// <summary>The name on the tile and the window title.</summary>
        public static string TitleFor(string id)
        {
            var starter = FindStarter(id);
            return starter != null ? starter.title : Pretty(id);
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
            return parts.Count == 0 ? (FindStarter(id) != null ? "Ready to start" : "not started") : string.Join(" · ", parts);
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
