using System;
using System.Collections.Generic;
using System.IO;
using Forge.Artifacts;
using Forge.Controllers;
using Forge.Notebook;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Run Projects Test: the real Main scene in Play mode on the desktop rig with voice off and the hub off.
    /// Everything runs on a scratch root (a fresh install: no projects folder, no artifacts, no approvals), so this
    /// machine's own notebook and artifact library are never read or written. It checks the Projects tab (starters, a
    /// project folder, true status lines from a cut version and a run record), opens RC truck from the real tile, presses
    /// Course with a pointer (first the Allow ask, then the slalom with the brain truck: BrainLoops.Active and
    /// links.thing == "truck"), and opens the other starters (CAD on the quadcopter and the rocket, the image
    /// classifier's honest offline line). The windows are checked for clipped or overlapping text and for finger-sized
    /// buttons; screenshots go to Logs/os/. The result is in <see cref="LastResult"/> and a console line
    /// "[OSProjectsTest] N passed, M failed".
    /// </summary>
    [InitializeOnLoad]
    public static class OSProjectsTest
    {
        const string ActiveKey = "OSProjectsTest.Active";
        const string ScenePath = "Assets/_Forge/Core/Main.unity";
        const double GiveUpSeconds = 230;

        /// <summary>"running", or the summary of the last run (starts with PASS or FAIL), or null if it never ran.</summary>
        public static string LastResult { get; private set; }
        /// <summary>The measurements of the last run, one per line.</summary>
        public static string Measurements { get; private set; }

        static IEnumerator<float> steps;
        static double wakeAt, startedAt;
        static int passed, failed;
        static readonly List<string> failures = new List<string>();
        static readonly List<string> measured = new List<string>();
        static readonly List<string> logged = new List<string>();
        static string scratch;

        static OSProjectsTest()
        {
            if (SessionState.GetBool(ActiveKey, false)) EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static bool keepPlaying
        {
            get => SessionState.GetBool("OSProjectsTest.Keep", false);
            set => SessionState.SetBool("OSProjectsTest.Keep", value);
        }

        /// <summary>For looking around by hand: runs the test, then stays in Play mode (call <see cref="Stop"/> to leave).</summary>
        public static void RunAndStay()
        {
            keepPlaying = true;
            Run();
        }

        /// <summary>Leaves Play mode after <see cref="RunAndStay"/>, and puts the editor prefs back.</summary>
        public static void Stop()
        {
            keepPlaying = false;
            RestorePrefs();
            EditorApplication.isPlaying = false;
        }

        static void RestorePrefs()
        {
            OSTestEnv.Leave();
        }

        [MenuItem("Forge/OS/Run Projects Test")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSProjectsTest] Leave Play mode first."); return; }
            OSTestEnv.Enter("OSProjectsTest");
            SessionState.SetBool(ActiveKey, true);
            LastResult = "running";
            EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorApplication.isPlaying = true;
        }

        static void OnPlayMode(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode) return;
            Directory.CreateDirectory(OSShot.Dir);
            passed = failed = 0;
            failures.Clear();
            measured.Clear();
            logged.Clear();
            Application.logMessageReceived += OnLog;
            steps = Script();
            wakeAt = 0;
            startedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
        }

        static void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                if (message.StartsWith("[MetaXRFeature]", StringComparison.Ordinal)) return;
                logged.Add(type + ": " + message);
            }
            else if (type == LogType.Warning && message.StartsWith("[OS]", StringComparison.Ordinal)) logged.Add("Warning: " + message);
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup - startedAt > GiveUpSeconds)
            {
                Check(false, "timed out");
                Finish();
                return;
            }
            if (EditorApplication.timeSinceStartup < wakeAt) return;
            try
            {
                if (steps.MoveNext()) wakeAt = EditorApplication.timeSinceStartup + steps.Current;
                else Finish();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Check(false, "threw " + e.Message);
                Finish();
            }
        }

        static void Finish()
        {
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            try { Cleanup(); }
            catch (Exception e) { Debug.LogException(e); }
            Application.logMessageReceived -= OnLog;
            Check(logged.Count == 0, "no errors, exceptions or [OS] warnings were logged during the run" + (logged.Count == 0 ? "" : ": " + logged[0]));
            Measurements = string.Join("\n", measured);
            if (!keepPlaying)
            {
                string envDiff = OSTestEnv.Leave();
                Check(envDiff.Length == 0, "the user's own preferences and files are exactly as they were before the run" + (envDiff.Length == 0 ? "" : ": " + envDiff.Replace("\n", " | ")));
            }
            string summary = $"[OSProjectsTest] {passed} passed, {failed} failed";
            LastResult = (failed == 0 ? "PASS " : "FAIL ") + summary + (failed == 0 ? "" : "\n" + string.Join("\n", failures));
            if (failed == 0) Debug.Log(summary);
            else Debug.LogError(summary + "\n" + string.Join("\n", failures));
            SessionState.SetBool(ActiveKey, false);
            if (keepPlaying) return;
            RestorePrefs();
            EditorApplication.isPlaying = false;
        }

        static void Cleanup()
        {
            OSShell shell = OSShell.Current;
            if (shell != null) shell.Windows.CloseAll();
            OSModes.Close();
            AlbieTools.Call("artifact_close", new JObject());
            ArtifactHost host = ArtifactHost.Current;
            if (host != null) host.Room = null;
            ArtifactLibrary.RootOverride = null;
            ArtifactApprovals.FileOverride = null;
            NotebookStore.Flush();
            NotebookStore.Open(NotebookStore.DefaultProject);  // the app's own notebook again
            OSProjects.Refresh();
            if (scratch != null)
            {
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
                catch (Exception e) { Debug.Log("[OSProjectsTest] couldn't remove " + scratch + ": " + e.Message); }
                scratch = null;
            }
        }

        static void Check(bool condition, string what)
        {
            Debug.Log($"[OSProjectsTest] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition) { passed++; return; }
            failed++;
            failures.Add(what);
        }

        static void Measure(string line)
        {
            measured.Add(line);
            Debug.Log("[OSProjectsTest] measured: " + line);
        }

        static void Shot(string name)
        {
            string path = OSShot.Take(name);
            Debug.Log("[OSProjectsTest] shot " + (path ?? "(no camera)"));
        }

        // ---------------------------------------------------------------- helpers

        static string pressWhy;
        static bool pressOk;

        // Opens the menu on the Projects tab (the shell, not the M key: OSMenuTest checks the key) and presses a tile with a pointer.
        static IEnumerator<float> PressTile(OSShell shell, OSMenu menu, string id, string shotName)
        {
            pressOk = false;
            pressWhy = null;
            if (!shell.MenuOpen)
            {
                shell.OpenMenu(OSSummon.Key);
                yield return 0.9f;
            }
            if (!shell.MenuOpen) { pressWhy = "the menu didn't open"; yield break; }
            shell.SetTab(OSTab.Projects);  // after the open: the menu's first build sets its tab bar to Tools
            yield return 0.3f;
            DesktopRig.Current.LookAt(menu.LastCenter);
            yield return 0.4f;
            // From the first page: the tab keeps one row there and puts the rest (older projects, the samples, ML) a page turn away.
            for (int back = 0; back < 8 && menu.Page > 0; back++)
            {
                if (!PageButton(menu, "chevron_left")) break;
                yield return 0.3f;
            }
            firstPageTiles = menu.ActiveTiles.Count;
            OSTileView tile = FindTile(menu, id);
            for (int turn = 0; tile == null && turn < 6 && menu.Page < menu.PageCount - 1; turn++)
            {
                if (!PageButton(menu, "chevron_right")) break;
                yield return 0.3f;
                tile = FindTile(menu, id);
            }
            if (tile == null)
            {
                string have = "";
                for (int i = 0; i < menu.ActiveTiles.Count; i++) have += menu.ActiveTiles[i].entry?.id + ",";
                pressWhy = "no tile for " + id + " (tab " + shell.Tab + ", menu open " + shell.MenuOpen + ", tiles: " + have + ")";
                yield break;
            }
            if (shotName != null) Shot(shotName);
            pressOk = OSToolsTest.Press(tile.gameObject, out pressWhy);
            if (!pressOk) Shot("99_press_failed_" + id);
            yield return 0.9f;
        }

        static int firstPageTiles;

        static OSTileView FindTile(OSMenu menu, string id)
        {
            for (int i = 0; i < menu.ActiveTiles.Count; i++)
                if (menu.ActiveTiles[i].entry != null && menu.ActiveTiles[i].entry.id == id) return menu.ActiveTiles[i];
            return null;
        }

        static bool PageButton(OSMenu menu, string icon)
        {
            Transform t = OSToolsTest.FindDeep(menu.transform, "Button " + icon);
            Button b = t != null ? t.GetComponent<Button>() : null;
            if (b == null || !b.interactable) return false;
            b.onClick.Invoke();
            return true;
        }

        // Shows the Details page (true) or the overview (false) and waits for the window to rebuild.
        static IEnumerable<float> ShowDetails(OSShell shell, bool on)
        {
            OSProjectWindow v = View(shell);
            if (v == null) yield break;
            v.DetailsOpen = on;
            yield return 0.4f;
        }

        // The buttons a person can press in the window right now.
        static int Targets(OSWindow window)
        {
            int n = 0;
            OSBody body = window.GetComponentInChildren<OSBody>(true);
            if (body == null) return -1;
            foreach (Button b in body.GetComponentsInChildren<Button>(false)) if (b.interactable) n++;
            return n;
        }

        static Button FindButton(OSWindow window, string label)
        {
            Transform t = OSToolsTest.FindDeep(window.transform, "Button " + label);
            return t != null ? t.GetComponent<Button>() : null;
        }

        static OSWindow Rail(OSShell shell) => shell.Windows.Find(OSProjectRail.WindowId);

        // Looks at a control, then presses it with the pointer (the project bar sits low, under the tool or in front).
        static IEnumerable<float> LookAndPress(Button b)
        {
            pressOk = false;
            pressWhy = b == null ? "no such control" : null;
            if (b == null) yield break;
            DesktopRig.Current.LookAt(OSToolsTest.CenterOf(b.transform));
            yield return 0.5f;
            pressOk = OSToolsTest.Press(b.gameObject, out pressWhy);
        }

        static string Listing()
        {
            var list = new List<OSEntry>();
            OSRegistry.Fill(OSTab.Projects, list);
            var sb = new System.Text.StringBuilder();
            foreach (var e in list)
                if (e.section != OSMLProjects.Section)  // the Machine learning page is OSMLProjectsCheck's
                    sb.Append(e.id).Append('=').Append(e.status()).Append(e.isOpen != null && e.isOpen() ? " (open)" : "").Append(" | ");
            return sb.ToString();
        }

        static JObject ReadManifest(string id)
        {
            string path = Path.Combine(ArtifactLibrary.Folder(id), "manifest.json");
            try { return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null; }
            catch (Exception) { return null; }
        }

        static OSProjectWindow View(OSShell shell)
        {
            OSWindow w = shell.Windows.Find(OSProjectWindow.WindowId);
            return w != null ? w.GetComponent<OSProjectWindow>() : null;
        }

        // ---------------------------------------------------------------- the script

        static IEnumerator<float> Script()
        {
            // Everything the run could write (settings, notebook, artifacts, approvals) goes to a folder of its own first; the tests that need their own root move on from here.
            OSTestEnv.UseScratch("osprojects");
            string why = null;
            Workspace workspace = null;
            while (workspace == null || !workspace.presence.AtDesk)
            {
                yield return 0.25f;
                workspace = Workspace.Current;
                if (workspace != null) workspace.presence.forceSeated = true;
            }
            yield return 0.5f;
            DesktopRig.Current.ToggleSeat();
            yield return 1.2f;

            OSShell shell = OSShell.Current;
            Check(shell != null, "the OS shell is in Main");
            if (shell == null) yield break;
            var menu = shell.GetComponentInChildren<OSMenu>(true);
            Check(menu != null, "the shell has the menu");
            if (menu == null) yield break;
            if (shell.Held)
            {
                var greeting = UnityEngine.Object.FindAnyObjectByType<ArrivalGreeting>();
                if (greeting != null) greeting.Fold(true);
                workspace.HoldDock(false);
                yield return 0.5f;
            }
            workspace.Close();
            yield return 1.2f;
            OSWindows windows = shell.Windows;
            windows.CloseAll();

            // ---- a fresh install: a scratch root with no projects folder, no artifacts, no approvals
            scratch = Path.Combine(Path.GetTempPath(), "os_projects_test");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
            Directory.CreateDirectory(scratch);
            ArtifactLibrary.RootOverride = Path.Combine(scratch, "artifacts");
            ArtifactApprovals.FileOverride = Path.Combine(scratch, "artifacts", "approvals.json");
            NotebookStore.Open(NotebookStore.DefaultProject, scratch);
            OSProjects.Register();  // the app does this at start; doing it again must not list anything twice
            OSProjects.Refresh();
            Check(!Directory.Exists(Path.Combine(scratch, "projects")), "fresh install: there is no projects folder");

            // ---- the tab, from nothing
            string listing = Listing();
            Measure("fresh: " + listing);
            Check(listing == OSProjects.StartOwnId + "=" + OSProjects.NewProjectLine + " | proj_truck=Design, drive, train (open) | proj_quadcopter=Design, fly, learn | proj_rocket=Design, sim, launch | proj_classifier=Find, train, publish | ",
                "fresh install: the Projects tab lists New project first, then the four samples, each saying what it shows (" + listing + ")");
            var fresh = new List<OSEntry>();
            OSRegistry.Fill(OSTab.Projects, fresh);
            bool samplesHeaded = fresh.Count >= 5;
            for (int i = 1; i < 5 && i < fresh.Count; i++) samplesHeaded &= fresh[i].section == OSProjects.SampleSection;
            Check(samplesHeaded && fresh[0].outline && fresh[0].section == null && fresh[0].title == OSProjects.NewProjectTitle,
                "New project leads the person's own row, and the four samples sit under the '" + OSProjects.SampleSection + "' heading");

            // ---- RC truck from the real tile
            var step = PressTile(shell, menu, "proj_truck", "50_projects_tab_fresh");
            while (step.MoveNext()) yield return step.Current;
            Check(pressOk, "a pointer reaches the RC truck tile: " + pressWhy);
            yield return 0.9f;
            OSWindow win = windows.Find(OSProjectWindow.WindowId);
            OSProjectWindow view = View(shell);
            Check(win != null && view != null && view.ProjectId == NotebookStore.DefaultProject, "the Project window is open on proj_truck");
            Check(NotebookStore.IsOpen && NotebookStore.ProjectId == NotebookStore.DefaultProject, "the notebook has proj_truck open");
            if (win == null || view == null) yield break;
            DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
            yield return 0.6f;
            string text = OSToolsTest.AllText(win);
            string problem = OSToolsTest.LayoutProblem(win);
            Check(problem == null, "the truck overview text is inside it and doesn't overlap" + (problem ?? ""));
            // The overview: one sentence, the big Next button, the steps row, Make my own and More. Nothing else.
            foreach (string needle in new[] { "RC truck", "Next: Course", "Design", "Course", "Drive", "Train", "Autopilot", "Code", OSProjectWindow.MoreLabel,
                                              OSProjects.SampleShows(NotebookStore.DefaultProject), OSProjects.MakeOwnLabel(NotebookStore.DefaultProject) })
                Check(text.Contains(needle), "the truck overview shows '" + needle + "'");
            foreach (string tucked in new[] { "Versions", "No plan written yet", "No artifacts yet", OSProjects.SampleLine, "Notebook" })
                Check(!text.Contains(tucked), "the truck overview keeps '" + tucked + "' tucked away");
            int targets = Targets(win);
            Measure("overview targets: " + targets);
            Check(targets > 0 && targets <= 9, "the overview has at most 9 targets: Next, six stages, Make my own, More (" + targets + ")");
            OSProjectSession session = OSProjectSession.Current;
            Check(session != null && session.ProjectId == NotebookStore.DefaultProject && session.CurrentStage == "course",
                "a fresh truck lands on its first stage to do: Course (" + (session != null ? session.CurrentStage : "no session") + ")");
            OSWindow rail = Rail(shell);
            Check(rail != null && OSProjectRail.Current != null && OSProjectRail.Current.Chips.Count == 6, "the project bar is up with the six stages (" + (OSProjectRail.Current != null ? OSProjectRail.Current.Chips.Count : -1) + ")");
            Check(OSProjectRail.Current != null && OSProjectRail.Current.NextButton != null && OSProjectRail.Current.NextButton.name == "Button Next: Course", "the bar's Next says Next: Course");
            if (rail != null)
            {
                foreach (Button chip in OSProjectRail.Current.Chips)
                {
                    var r = (RectTransform)chip.transform;
                    float cw = OSToolsTest.Mm(r.rect.width, r), ch = OSToolsTest.Mm(r.rect.height, r);
                    Check(cw >= 30f && ch >= 30f, "the bar's " + chip.name + " is at least 30 x 30 mm (" + cw.ToString("F0") + " x " + ch.ToString("F0") + ")");
                }
                Check(OSToolsTest.LayoutProblem(rail) == null, "the bar's text is inside it and doesn't overlap" + (OSToolsTest.LayoutProblem(rail) ?? ""));
                Check(session != null && session.TryStage("drive", out StageState driveStage) && driveStage.Status == StageStatus.Locked && driveStage.Unlock == "course",
                    "Drive is locked until there is a course, and says to go to Course");
            }
            foreach (float w in ShowDetails(shell, true)) yield return w;
            text = OSToolsTest.AllText(win);
            foreach (string needle in new[] { "Plan", "Versions", "No plan written yet", "No artifacts yet", OSProjectWindow.SampleTagLine, OSProjects.SampleLine, OSProjectWindow.BackLabel })
                Check(text.Contains(needle), "the truck's Details page shows '" + needle + "'");
            problem = OSToolsTest.LayoutProblem(win);
            Check(problem == null, "the truck's Details page text is inside it and doesn't overlap" + (problem ?? ""));
            Shot("51b_truck_details_fresh");
            foreach (float w in ShowDetails(shell, false)) yield return w;
            view.MoreOpen = true;
            yield return 0.4f;
            text = OSToolsTest.AllText(win);
            Check(!text.Contains("Make a copy") && !text.Contains("Start my own"), "the sample offers one way to make your own (Make my own), not Make a copy and Start my own");
            Button makeOwn = FindButton(win, OSProjects.MakeOwnLabel(NotebookStore.DefaultProject));
            Check(makeOwn != null && makeOwn.interactable, "the sample's one call to action is a real button: " + OSProjects.MakeOwnLabel(NotebookStore.DefaultProject));
            Check(win.Panel.Subtitle.StartsWith(OSProjects.SampleTag, StringComparison.Ordinal), "the sample's header says it is a sample project (" + win.Panel.Subtitle + ")");
            Check(OSRegistry.Find(OSTab.Tools, "drive") != null && !text.Contains("Coming with") && !text.Contains(OSProjectWindow.DriveMissing), "Drive is a real step: the tool is registered and the window carries no 'coming soon' or 'not found' line");
            Check(!text.Contains(OSProjects.SendUnavailable), "the window carries no line about a Send step that can never run");
            Check(!text.Contains("TMP") && !text.Contains("New Text"), "no default text left in the window");
            problem = OSToolsTest.LayoutProblem(win);
            Check(problem == null, "the truck window text (More open) is inside it and doesn't overlap" + (problem ?? ""));
            foreach (string label in new[] { "Course", "Drive", "Train", "Autopilot", "Code", "Send", "Notebook", OSProjectWindow.DetailsLabel })
            {
                Button b = FindButton(win, label);
                Check(b != null, "the " + label + " button exists");
                if (b == null) continue;
                var r = (RectTransform)b.transform;
                float w = OSToolsTest.Mm(r.rect.width, r), h = OSToolsTest.Mm(r.rect.height, r);
                if (label == "Course") Measure($"Course button {w:F0} x {h:F0} mm");
                Check(w >= 28f && h >= 28f, label + " is at least 28 x 28 mm (" + w.ToString("F0") + " x " + h.ToString("F0") + ")");
            }
            Check(FindButton(win, "Send") != null, "there is a Send step under More (it drafts to the project's channel; the message panel confirms)");
            Shot("51_truck_window_fresh");
            view.MoreOpen = false;
            yield return 0.4f;

            // ---- Course: the first press asks (nothing approved on a fresh install), the second opens the slalom with the brain truck
            ArtifactHost host = ArtifactHost.Current ?? ArtifactBootstrap.Ensure();
            // The simulated living room of the desktop rig has no clear strip of floor for a slalom (see OSToolsTest.MakeTruck).
            if (host != null) host.Room = MockFloorPlan.LivingRoom();
            Check(BrainLoops.Active == null, "no brain truck before Course");
            Button course = FindButton(win, "Course");
            Check(course != null && OSToolsTest.Press(course.gameObject, out why), "a pointer reaches Course: " + why);
            yield return 0.8f;
            Measure("course, first press: " + view.StatusLine);
            Check(view.StatusLine.StartsWith("The slalom asks first.", StringComparison.Ordinal), "fresh install: Course shows the Allow ask in plain words (" + view.StatusLine + ")");
            Check(host != null && host.OpenManifest != null, "the slalom is waiting for approval");
            Check(!win.gameObject.activeSelf && OSProjectSession.HomeTucked, "going to a stage tucks the project window away");
            if (host != null && host.OpenManifest != null) ArtifactApprovals.Approve(host.OpenManifest);
            AlbieTools.Call("artifact_close", new JObject());
            yield return 0.5f;
            win = windows.Find(OSProjectWindow.WindowId);
            view = View(shell);
            rail = Rail(shell);
            course = rail != null ? FindButton(rail, "Course") : null;
            foreach (float w in LookAndPress(course)) yield return w;
            Check(pressOk, "a pointer reaches Course on the project bar: " + pressWhy);
            float waited = 0f;
            while (BrainLoops.Active == null && waited < 8f) { waited += 0.5f; yield return 0.5f; }
            yield return 0.6f;
            Measure("course, second press: " + view.StatusLine);
            Check(BrainLoops.Active != null, "Course gives a slalom with the brain truck (BrainLoops.Active != null)");
            Check(BrainLoops.Active is SlalomBrain, "the brain truck is the slalom brain");
            Check(host != null && host.OpenId == "slalom", "the slalom is the open artifact");
            string thing = host != null && host.OpenManifest != null ? (string)host.OpenManifest["links"]?["thing"] : null;
            string project = host != null && host.OpenManifest != null ? (string)host.OpenManifest["links"]?["project"] : null;
            Check(thing == "truck", "the open manifest has links.thing == \"truck\" (" + thing + ")");
            Check(project == NotebookStore.DefaultProject, "the open manifest links to proj_truck (" + project + ")");
            Check(view.StatusLine.StartsWith("The slalom is open. The brain truck is at the start.", StringComparison.Ordinal), "the status line says so (" + view.StatusLine + ")");
            Check(ArtifactLibrary.Root.StartsWith(scratch, StringComparison.Ordinal), "the slalom was made in the scratch library, not this machine's");
            yield return 0.6f;
            Check(session.TryStage("course", out StageState courseStage) && courseStage.Status == StageStatus.Done && session.NextId == "drive",
                "Course is done and the next stage is Drive");
            Check(OSProjectRail.Current != null && OSProjectRail.Current.NextButton != null && OSProjectRail.Current.NextButton.name == "Button Next: Drive", "the bar offers Next: Drive");
            Button homeButton = rail != null ? FindButton(rail, OSProjectRail.HomeIcon) : null;
            foreach (float w in LookAndPress(homeButton)) yield return w;
            Check(pressOk, "a pointer reaches the bar's home button: " + pressWhy);
            yield return 0.6f;
            Check(win.gameObject.activeSelf, "the bar's home button brings the project window back");
            DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
            yield return 0.6f;
            problem = OSToolsTest.LayoutProblem(win);
            Check(problem == null, "after Course the overview text is inside it and doesn't overlap" + (problem ?? ""));
            Check(FindButton(win, "Course") != null && FindButton(win, "Course").interactable, "Course can still be tapped once it is done");
            Shot("52_truck_window_course");
            foreach (float w in ShowDetails(shell, true)) yield return w;
            text = OSToolsTest.AllText(win);
            Check(text.Contains("Slalom v1") && text.Contains("no runs yet"), "Details lists the slalom under Artifacts and 'no runs yet' as its last run");
            Check(text.Contains("Course: open") && text.Contains("brain truck ready"), "Details' live truck line says the course is open and the truck is ready");
            problem = OSToolsTest.LayoutProblem(win);
            Check(problem == null, "after Course the Details text is inside it and doesn't overlap" + (problem ?? ""));
            Shot("52b_truck_details_course");

            // ---- linking: a heart made while the truck is open is standalone, and an older heart linked to it shows standalone
            JObject heart = AlbieTools.Call("artifact_create", new JObject { ["template"] = "heart", ["open"] = false });
            JObject heartManifest = ReadManifest("heart");
            Check(heart["error"] == null && heartManifest != null && heartManifest["links"]?["project"]?.Type != JTokenType.String && (string)heartManifest["kind"] == "standalone",
                "a heart made while the RC truck is open links to no project (" + (heartManifest != null ? heartManifest["links"]?.ToString(Newtonsoft.Json.Formatting.None) : (string)heart["error"]) + ")");
            JObject slalomManifest = ReadManifest("slalom");
            Check(slalomManifest != null && OSProjects.EffectiveProject(slalomManifest) == NotebookStore.DefaultProject, "the slalom the truck's Course made belongs to the truck project");
            if (heartManifest != null)
            {
                var legacy = (JObject)heartManifest.DeepClone();
                legacy["id"] = "old_heart";
                legacy["kind"] = "project";
                legacy["links"]["project"] = NotebookStore.DefaultProject;
                string legacyFolder = ArtifactLibrary.Folder("old_heart");
                Directory.CreateDirectory(legacyFolder);
                File.WriteAllText(Path.Combine(legacyFolder, "manifest.json"), legacy.ToString());
                var linkedNow = new List<OSProjects.LinkedArtifact>();
                OSProjects.LinkedArtifacts(NotebookStore.DefaultProject, linkedNow);
                bool hasOld = false, hasSlalom = false;
                foreach (var a in linkedNow) { hasOld |= a.id == "old_heart"; hasSlalom |= a.id == "slalom"; }
                Check(OSProjects.EffectiveProject(legacy) == null && !hasOld && hasSlalom, "an older heart saved as linked to proj_truck counts as standalone; the slalom still counts");
                Directory.Delete(legacyFolder, true);
            }

            // ---- Autopilot with no laps recorded must not throw, and says what happened
            string autopilotLine = view.Do("autopilot");
            Measure("autopilot: " + autopilotLine);
            Check(!string.IsNullOrWhiteSpace(autopilotLine), "Autopilot answers with a sentence (" + autopilotLine + ")");
            view.Do("autopilot");  // leave it as it was if it started
            yield return 0.5f;

            // ---- real data in the status lines: a cut version, a run record, a project folder on disk
            NotebookStore.CutVersion("thing", "Softer steering near the cones");
            var record = new JObject
            {
                ["schema"] = ArtifactRuns.Schema,
                ["artifact_id"] = "slalom",
                ["run_id"] = ArtifactRuns.NextId("slalom"),
                ["project_id"] = NotebookStore.DefaultProject,
                ["outcome"] = "completed",
                ["metrics"] = new JObject { ["completion_time_s"] = 12.4 },
            };
            ArtifactRuns.Save(record);
            Directory.CreateDirectory(Path.Combine(scratch, "projects", "proj_demo_folder"));
            OSProjects.Refresh();
            yield return 0.3f;
            listing = Listing();
            Measure("with data: " + listing);
            Check(listing.Contains("proj_truck=v1 · slalom passed (open)"), "RC truck says its version and its newest run: 'v1 · slalom passed' (" + listing + ")");
            Check(listing.Contains("proj_demo_folder=not started"), "a folder on disk is listed (proj_demo_folder)");
            Check(listing.Split('|').Length == 7, "the list has New project, the folder and the four samples");
            Check(listing.IndexOf("proj_demo_folder=", StringComparison.Ordinal) < listing.IndexOf("proj_truck=", StringComparison.Ordinal), "the person's own projects come before the samples");
            yield return 0.8f;
            text = OSToolsTest.AllText(win);
            Check(text.Contains("12.4 s") && text.Contains("last run passed"), "Details shows the run: 'last run passed · 12.4 s'");
            problem = OSToolsTest.LayoutProblem(win);
            Check(problem == null, "with data the window text is inside it and doesn't overlap" + (problem ?? ""));
            Shot("53_truck_window_run");

            // the newest run is found without parsing the older ones, and a project filter walks back to its own run
            string firstRun = (string)record["run_id"];
            ArtifactRuns.Save(new JObject
            {
                ["schema"] = ArtifactRuns.Schema,
                ["artifact_id"] = "slalom",
                ["run_id"] = ArtifactRuns.NextId("slalom"),
                ["project_id"] = "proj_someone_else",
                ["outcome"] = "failed",
            });
            JObject newestAny = OSArtifacts.NewestRun("slalom", null), newestMine = OSArtifacts.NewestRun("slalom", NotebookStore.DefaultProject);
            Check(newestAny != null && (string)newestAny["outcome"] == "failed", "NewestRun with no project is the last record written");
            Check(newestMine != null && (string)newestMine["run_id"] == firstRun && (string)newestMine["outcome"] == "completed", "NewestRun for the truck project walks back past another project's newer run");
            Check(OSArtifacts.NewestRun("slalom", "proj_nobody") == null && OSArtifacts.NewestRun("no_such_artifact", null) == null, "NewestRun is null with no match and for an artifact with no runs");

            // ---- the fullest window: a long plan with many milestones, many versions, an attempt with an outcome
            var milestones = new JArray();
            string[] steps8 = { "Drives on the real floor", "Finishes the slalom by hand", "Autopilot finishes the slalom", "A clean lap at 2 m/s or more", "Three clean laps in a row", "Send the model to a friend", "Tune the steering near the cones" };
            for (int i = 0; i < steps8.Length; i++) milestones.Add(new JObject { ["id"] = "m" + (i + 1), ["text"] = steps8[i], ["done"] = i < 3 });
            NotebookStore.RevisePlan("Making the plan longer for the window check",
                "An RC truck that does a slalom around the couch and the coffee table without touching a single cone, at walking speed.",
                "Finish the slalom three times in a row with no cones hit and no hand on the controls.", milestones);
            NotebookStore.CutVersion("thing", "Autopilot max speed raised to 2.2 m/s");
            NotebookStore.CutVersion("thing", "Wider track and a lower centre of mass");
            NotebookStore.CutVersion("thing", "Softer steering near the cones, tuned on the real floor");
            string attempt = NotebookStore.Log("attempt", "Cut the top speed in the tight corners", "The truck was clipping the third cone.", "albie", "A clean lap", new[] { "vehicle" });
            NotebookStore.Mark(attempt, "worked", "person");
            yield return 1.0f;
            text = OSToolsTest.AllText(win);
            Check(text.Contains("Plan v1 · 3 of 7 milestones done · next: A clean lap at 2 m/s or more"), "the summary's Plan line says how many milestones are done and which is next");
            Check(text.Contains("v4 · Softer steering near the cones, tuned on the real floor · worked (4 in all)"), "the summary's Versions line shows the newest with its outcome and how many there are");
            problem = OSToolsTest.LayoutProblem(win);
            Check(problem == null, "the fullest truck Details text is inside it and doesn't overlap" + (problem ?? ""));
            Shot("58_truck_details_full");
            foreach (float w in ShowDetails(shell, false)) yield return w;
            problem = OSToolsTest.LayoutProblem(win);
            Check(problem == null, "the fullest truck overview text is inside it and doesn't overlap" + (problem ?? ""));
            Shot("58b_truck_window_full");

            AlbieTools.Call("artifact_close", new JObject());
            yield return 0.8f;
            Measure("brain truck after the course closes: " + (BrainLoops.Active != null ? "still there" : "gone"));

            // ---- the tab with a folder in it
            windows.CloseAll();
            yield return 0.8f;
            shell.CloseMenu();
            yield return 0.8f;
            step = PressTile(shell, menu, "proj_demo_folder", "54_projects_tab");
            while (step.MoveNext()) yield return step.Current;
            Check(pressOk, "a pointer reaches the folder's tile: " + pressWhy);
            Check(firstPageTiles > 0 && firstPageTiles <= 4, "with a project of the person's own the tab's first page is one row: New project and the newest projects (" + firstPageTiles + " tiles)");
            yield return 0.9f;
            view = View(shell);
            Check(view != null && view.ProjectId == "proj_demo_folder" && NotebookStore.ProjectId == "proj_demo_folder", "the folder opens as the open project and its window shows it");
            win = windows.Find(OSProjectWindow.WindowId);
            if (win != null)
            {
                DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
                yield return 0.5f;
                text = OSToolsTest.AllText(win);
                Check(text.Contains("Demo folder") && text.Contains("CAD") && text.Contains("Train"), "a plain project window offers CAD and Train and the title 'Demo folder'");
                problem = OSToolsTest.LayoutProblem(win);
                Check(problem == null, "the plain project window text is inside it and doesn't overlap" + (problem ?? ""));
                Shot("55_generic_window");
            }
            Check(Listing().Contains("proj_demo_folder=not started (open)"), "the tab marks the folder as open");

            // ---- the starters
            OSProjects.OpenProject("proj_classifier");
            yield return 1.2f;
            view = View(shell);
            Check(view != null && view.ProjectId == "proj_classifier" && NotebookStore.ProjectId == "proj_classifier", "Image classifier opens as the open project");
            win = windows.Find(OSProjectWindow.WindowId);
            if (win != null)
            {
                Check(OSProjectSession.Current.CurrentStage == "data" && FindButton(win, "Next: Data") != null, "the classifier lands on Data with Next: Data");
                // The loop is Data, Train, Results, Publish: Data and Train work offline (the bundled images), Results waits for a
                // run, and Publish needs the cloud and says so.
                foreach (string label in new[] { "Data", "Train", "Results", "Publish" })
                    Check(FindButton(win, label) != null, "the classifier loop has " + label);
                Check(OSProjectSession.Current.TryStage("train", out StageState mlTrain) && mlTrain.Status == StageStatus.Locked && mlTrain.Unlock == "data",
                    "Train waits for Data and says so");
                Button dataset = FindButton(win, "Publish");
                Check(OSProjectSession.Current.TryStage("publish", out StageState publish) && publish.Status == StageStatus.Locked && publish.Unlock == null,
                    "Publish is off while the cloud isn't reachable");
                text = OSToolsTest.AllText(win);
                Check(text.Contains(OSProjectWindow.PublishNeedsCloud), "the classifier window says Publish needs the cloud, in words");
                DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
                yield return 0.5f;
                problem = OSToolsTest.LayoutProblem(win);
                Check(problem == null, "the classifier window text is inside it and doesn't overlap" + (problem ?? ""));
                Shot("56_classifier_window");
                Button nextData = FindButton(win, "Next: Data");
                if (nextData != null) nextData.onClick.Invoke();
                yield return 1.0f;
                Check(View(shell).StatusLine.Contains("bundled images"), "Data opens on-device training on the bundled images (" + View(shell).StatusLine + ")");
                Check(OSModes.IsOpen("ml"), "the Train tool opened for the classifier");
                Check(OSProjectSession.Current.TryStage("train", out mlTrain) && mlTrain.Status == StageStatus.Ready && OSProjectSession.Current.NextId == "train",
                    "after Data, Train is ready and next");
                if (dataset != null) { dataset.onClick.Invoke(); yield return 0.3f; }
                Check(View(shell).StatusLine.Contains("bundled images") && OSProjectSession.Current.Notice != null,
                    "pressing Publish offline only says why on the bar; nothing else changes");
            }

            foreach (string part in new[] { "quadcopter", "rocket" })
            {
                windows.CloseAll();
                OSModes.Close();
                yield return 1.5f;
                OSProjects.OpenProject("proj_" + part);
                yield return 0.6f;
                Check(OSProjectSession.Current.CurrentStage == "design" && OSModes.IsOpen("cad") == false, part + ": opening the project opens no tool by itself and lands on Design");
                Button nextDesign = FindButton(windows.Find(OSProjectWindow.WindowId), "Next: Design");
                Check(nextDesign != null, part + ": the window offers Next: Design");
                if (nextDesign != null) nextDesign.onClick.Invoke();
                waited = 0f;
                CadSession cad = null;
                while (waited < 12f)
                {
                    cad = CadSession.Instance;
                    if (cad != null && cad.Ready && cad.Params != null && cad.Params.part == part) break;
                    waited += 0.5f;
                    yield return 0.5f;
                }
                Check(cad != null && cad.Params != null && cad.Params.part == part, part + ": CAD opened on the " + part + " design (" + (cad != null && cad.Params != null ? cad.Params.part : "no session") + ")");
                Check(OSModes.IsOpen("cad"), part + ": the CAD mode is open");
                yield return 0.5f;
                view = View(shell);
                Check(view != null && view.ProjectId == "proj_" + part && view.StatusLine.StartsWith("CAD is open", StringComparison.Ordinal), part + ": the window says CAD is open (" + (view != null ? view.StatusLine : "no window") + ")");
                win = windows.Find(OSProjectWindow.WindowId);
                if (win != null && part == "quadcopter")
                {
                    Check(!win.gameObject.activeSelf, "quadcopter: going to Design tucks the project window away");
                    OSProjectSession.Current.ShowHome();
                    yield return 0.6f;
                    DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
                    yield return 0.5f;
                    problem = OSToolsTest.LayoutProblem(win);
                    Check(problem == null, "the quadcopter window text is inside it and doesn't overlap" + (problem ?? ""));
                    Shot("57_quadcopter_window");
                }
                if (part == "quadcopter")
                {
                    OSProjectSession q = OSProjectSession.Current;
                    yield return 1.0f;
                    Check(q.TryStage("design", out StageState qd) && qd.Status == StageStatus.Done && qd.Result.StartsWith("v", StringComparison.Ordinal),
                        "quadcopter: Design is done with its version once CAD shows it (" + qd.Result + ")");
                    Check(q.NextId == "course" && q.TryStage("fly", out StageState qf) && qf.Status == StageStatus.Locked && qf.Unlock == "course",
                        "quadcopter: next is Course, Fly waits for a course");
                    OSWindow qRail = Rail(shell);
                    Button designChip = qRail != null ? FindButton(qRail, "Design") : null;
                    Check(designChip != null && designChip.interactable, "quadcopter: the Design stage can be tapped again on the bar after it is done");
                    Shot("57b_quadcopter_bar_cad");
                }
            }

            // ---- a bad id never leaves the notebook closed and never throws
            string before = NotebookStore.ProjectId;
            OSProjects.OpenProject("bad/id");
            yield return 0.3f;
            Check(NotebookStore.IsOpen && NotebookStore.ProjectId == before, "a bad id leaves the previous project open (" + NotebookStore.ProjectId + ")");
            Check(Listing().Split('|').Length == 7, "the list is unchanged after a bad id");

            // ---- Make my own: Build something with the sample's kind picked, then a project of the person's own with its own plan
            windows.CloseAll();
            OSModes.Close();
            yield return 0.6f;
            OSProjects.OpenProject(OSProjects.RocketId);
            yield return 1.0f;
            view = View(shell);
            string madeLine = view != null ? view.Do("make_own") : "no window";
            yield return 0.8f;
            OSBuildFlow flow = OSBuildFlow.Current;
            Check(flow != null && flow.Planner.Asked == 1 && flow.Planner.Current != null && flow.Planner.Current.id == "rocket_aim" && flow.PresetKind == OSPlanner.Kind.Rocket,
                "Make my own rocket opens Build something on the rocket's own question (" + madeLine + ")");
            Check(NotebookStore.ProjectId == OSProjects.RocketId && !Directory.Exists(Path.Combine(scratch, "projects", "proj_my_rocket")), "nothing is made before Start, and the sample is still the open project");
            OSWindow card = windows.Find(OSBuildFlow.WindowId);
            if (card != null)
            {
                DesktopRig.Current.LookAt(OSToolsTest.CenterOf(card.Panel.transform));
                yield return 0.5f;
                problem = OSToolsTest.LayoutProblem(card);
                Check(problem == null, "the pre-answered card's text is inside it and doesn't overlap" + (problem ?? ""));
            }
            string ownId = null;
            if (flow != null)
            {
                flow.Pick(0);
                flow.Pick(0);
                Check(flow.StartBuild(out string startError), "Start on the rocket plan works: " + startError);
                ownId = OSBuildFlow.LastProjectId;
            }
            yield return 1.2f;
            Check(ownId == "proj_my_rocket", "Start makes proj_my_rocket (" + ownId + ")");
            if (ownId != null)
            {
                Check(NotebookStore.ProjectId == ownId && OSProjects.TitleFor(ownId) == "My rocket" && OSProjects.CopyOf(ownId) == OSProjects.RocketId,
                    "the new project is open, titled 'My rocket' (" + OSProjects.TitleFor(ownId) + "), and follows the Rocket sample");
                Check(!OSProjects.IsSample(ownId) && OSProjectWindow.LoopOf(ownId) == OSProjectWindow.Loop.Rocket && OSProjects.DesignOf(ownId) == "rocket",
                    "it is the person's own project with the rocket's loop and design link");
                string goal = (string)NotebookStore.CurrentPlan?["goal"];
                Check(goal != null && goal.Contains("flies straight") && NotebookStore.Versions.Count == 0, "it has the plan from the answers (" + goal + ") and no versions");
                OSWindow ownWin = windows.Find(OSProjectWindow.WindowId);
                string ownText = ownWin != null ? OSToolsTest.AllText(ownWin) : "";
                Check(ownText.Contains("flies straight") && !ownText.Contains("Make my own"), "its window shows its goal and offers no Make my own");
                foreach (float w in ShowDetails(shell, true)) yield return w;
                ownText = ownWin != null ? OSToolsTest.AllText(ownWin) : "";
                Check(ownText.Contains(OSProjectWindow.OwnTagLine), "its Details page says it is your project");
                foreach (float w in ShowDetails(shell, false)) yield return w;
                if (ownWin != null)
                {
                    DesktopRig.Current.LookAt(OSToolsTest.CenterOf(ownWin.Panel.transform));
                    yield return 0.5f;
                    problem = OSToolsTest.LayoutProblem(ownWin);
                    Check(problem == null, "the own project's window text is inside it and doesn't overlap" + (problem ?? ""));
                    Shot("59_own_rocket_window");
                }
                Check(OSProjects.Resolve("my rocket") == ownId && OSProjects.Resolve("rocket") == OSProjects.RocketId, "'my rocket' is the person's project, 'rocket' is still the sample");
                OSProjects.Refresh();
                string ownListing = Listing();
                Check(ownListing.Contains(ownId + "=") && ownListing.IndexOf(ownId + "=", StringComparison.Ordinal) < ownListing.IndexOf("proj_truck=", StringComparison.Ordinal),
                    "it is listed with the person's own projects, before the samples");
            }
            JObject viaTool = AlbieTools.Call("project_copy", new JObject { ["name"] = "rocket" });
            yield return 0.6f;
            Check(viaTool["error"] == null && OSBuildFlow.Current != null && OSBuildFlow.Current.Planner.Asked == 1,
                "project_copy (the old name) opens Make my own: the card on the rocket's question (" + viaTool.ToString(Newtonsoft.Json.Formatting.None) + ")");
            if (OSBuildFlow.Current != null) OSBuildFlow.Current.Cancel();
            yield return 0.4f;
            Check(OSProjects.FreeOwnId(OSProjects.RocketId) == "proj_my_rocket_2" && !Directory.Exists(Path.Combine(scratch, "projects", "proj_my_rocket_2")),
                "cancelled, it leaves nothing; the next one would be proj_my_rocket_2");
            Check(OSProjects.Resolve(OSProjects.NewProjectTitle) == null, "the New project tile is never taken for a project");

            // ---- the real menu still shows the tab after all of it
            windows.CloseAll();
            OSModes.Close();
            yield return 1.2f;
            shell.CloseMenu();
            yield return 0.6f;
            step = PressTile(shell, menu, "proj_truck", null);
            while (step.MoveNext()) yield return step.Current;
            Check(pressOk && NotebookStore.ProjectId == NotebookStore.DefaultProject, "back to RC truck from the tab");
            windows.CloseAll();
            AlbieTools.Call("artifact_close", new JObject());
            yield return 1f;
        }
    }
}
