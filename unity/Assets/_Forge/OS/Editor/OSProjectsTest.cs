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
            OSTileView tile = null;
            for (int i = 0; i < menu.ActiveTiles.Count; i++)
                if (menu.ActiveTiles[i].entry != null && menu.ActiveTiles[i].entry.id == id) tile = menu.ActiveTiles[i];
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

        static Button FindButton(OSWindow window, string label)
        {
            Transform t = OSToolsTest.FindDeep(window.transform, "Button " + label);
            return t != null ? t.GetComponent<Button>() : null;
        }

        static string Listing()
        {
            var list = new List<OSEntry>();
            OSRegistry.Fill(OSTab.Projects, list);
            var sb = new System.Text.StringBuilder();
            foreach (var e in list)
                sb.Append(e.id).Append('=').Append(e.status()).Append(e.isOpen != null && e.isOpen() ? " (open)" : "").Append(" | ");
            return sb.ToString();
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
            Check(listing == "proj_truck=not started (open) | proj_quadcopter=not started | proj_rocket=not started | proj_classifier=not started | ",
                "fresh install: the Projects tab lists the four starters, the truck open, every line 'not started' (" + listing + ")");

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
            foreach (string needle in new[] { "RC truck", "Plan", "Versions", "Artifacts", "Continue", "Course", "Drive", "Train", "Autopilot", "Code", "Send", "Notebook", "No plan written yet", "Nothing linked yet" })
                Check(text.Contains(needle), "the truck window shows '" + needle + "'");
            Check(OSRegistry.Find(OSTab.Tools, "drive") != null && !text.Contains("Coming with") && !text.Contains(OSProjectWindow.DriveMissing), "Drive is a real step: the tool is registered and the window carries no 'coming soon' or 'not found' line");
            Check(text.Contains(OSProjects.SendUnavailable), "Send says honestly that it needs the cloud messenger");
            Check(!text.Contains("TMP") && !text.Contains("New Text"), "no default text left in the window");
            string problem = OSToolsTest.LayoutProblem(win);
            Check(problem == null, "the truck window text is inside it and doesn't overlap" + (problem ?? ""));
            foreach (string label in new[] { "Course", "Drive", "Train", "Autopilot", "Code", "Send", "Notebook" })
            {
                Button b = FindButton(win, label);
                Check(b != null, "the " + label + " button exists");
                if (b == null) continue;
                var r = (RectTransform)b.transform;
                float w = OSToolsTest.Mm(r.rect.width, r), h = OSToolsTest.Mm(r.rect.height, r);
                if (label == "Course") Measure($"Course button {w:F0} x {h:F0} mm");
                Check(w >= 22f && h >= 22f, label + " is at least 22 x 22 mm (" + w.ToString("F0") + " x " + h.ToString("F0") + ")");
            }
            Check(FindButton(win, "Send") != null && !FindButton(win, "Send").interactable, "Send is off (nothing can send the model offline)");
            Shot("51_truck_window_fresh");

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
            if (host != null && host.OpenManifest != null) ArtifactApprovals.Approve(host.OpenManifest);
            AlbieTools.Call("artifact_close", new JObject());
            yield return 0.5f;
            win = windows.Find(OSProjectWindow.WindowId);
            view = View(shell);
            course = FindButton(win, "Course");
            Check(course != null && OSToolsTest.Press(course.gameObject, out why), "a pointer reaches Course again: " + why);
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
            DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
            yield return 0.6f;
            text = OSToolsTest.AllText(win);
            Check(text.Contains("Slalom") && text.Contains("no runs yet"), "the Artifacts section lists the slalom with 'no runs yet'");
            Check(text.Contains("Course: open") && text.Contains("brain truck ready"), "the live brain line says the course is open and the truck is ready");
            problem = OSToolsTest.LayoutProblem(win);
            Check(problem == null, "after Course the window text is inside it and doesn't overlap" + (problem ?? ""));
            Shot("52_truck_window_course");

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
            Check(listing.Split('|').Length == 6, "the list has the four starters and the folder");
            yield return 0.8f;
            text = OSToolsTest.AllText(win);
            Check(text.Contains("12.4 s") && text.Contains("last run passed"), "the Artifacts section shows the run: 'last run passed · 12.4 s'");
            problem = OSToolsTest.LayoutProblem(win);
            Check(problem == null, "with data the window text is inside it and doesn't overlap" + (problem ?? ""));
            Shot("53_truck_window_run");

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
            Check(text.Contains("Plan v1") && text.Contains("[x] Drives on the real floor") && text.Contains("[ ] A clean lap at 2 m/s or more") && text.Contains("and 3 more"), "the plan card shows the goal, the milestones (done or not) and how many more");
            Check(text.Contains("v4 · Softer steering near the cones, tuned on the real floor · worked") && text.Contains("and 1 earlier"), "the versions list shows the newest first with each version's outcome");
            problem = OSToolsTest.LayoutProblem(win);
            Check(problem == null, "the fullest truck window text is inside it and doesn't overlap" + (problem ?? ""));
            Shot("58_truck_window_full");

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
            Check(view != null && view.StatusLine == OSProjects.NeedsCloud, "Image classifier shows the honest offline line (" + (view != null ? view.StatusLine : "no window") + ")");
            win = windows.Find(OSProjectWindow.WindowId);
            if (win != null)
            {
                Button dataset = FindButton(win, "Load dataset");
                Check(dataset != null && !dataset.interactable, "Load dataset is off while the cloud isn't reachable");
                text = OSToolsTest.AllText(win);
                Check(text.Contains(OSProjects.NeedsCloud), "the classifier window says it needs the cloud, in words");
                Check(OSModes.IsOpen("ml"), "the Train tool opened for the classifier");
                DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
                yield return 0.5f;
                problem = OSToolsTest.LayoutProblem(win);
                Check(problem == null, "the classifier window text is inside it and doesn't overlap" + (problem ?? ""));
                Shot("56_classifier_window");
                if (dataset != null) { dataset.onClick.Invoke(); yield return 0.3f; }
                Check(View(shell).StatusLine == OSProjects.NeedsCloud, "pressing Load dataset offline changes nothing and throws nothing");
            }

            foreach (string part in new[] { "quadcopter", "rocket" })
            {
                windows.CloseAll();
                OSModes.Close();
                yield return 1.5f;
                OSProjects.OpenProject("proj_" + part);
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
                    DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
                    yield return 0.5f;
                    problem = OSToolsTest.LayoutProblem(win);
                    Check(problem == null, "the quadcopter window text is inside it and doesn't overlap" + (problem ?? ""));
                    Shot("57_quadcopter_window");
                }
            }

            // ---- a bad id never leaves the notebook closed and never throws
            string before = NotebookStore.ProjectId;
            OSProjects.OpenProject("bad/id");
            yield return 0.3f;
            Check(NotebookStore.IsOpen && NotebookStore.ProjectId == before, "a bad id leaves the previous project open (" + NotebookStore.ProjectId + ")");
            Check(Listing().Split('|').Length == 6, "the list is unchanged after a bad id");

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
