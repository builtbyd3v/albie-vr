using System;
using System.Collections.Generic;
using System.IO;
using Forge.Artifacts;
using Forge.Controllers;
using Forge.Notebook;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Run Artifacts Test: the real Main scene in Play mode on the desktop rig with voice off and the hub off,
    /// on a scratch root (a fresh install: no artifacts, no approvals, no projects), so this machine's own notebook and
    /// artifact library are never read or written. The simulated room has no clear strip of floor, so room artifacts are
    /// laid out in the mock living room through <see cref="ArtifactHost.Room"/>. It checks the Artifacts tab (the New
    /// section, then what was made), presses the real tiles with a pointer to make and open the heart, the solar system,
    /// repo activity (through its Allow sheet) and a slalom for the truck project (the brain truck appears and the tile
    /// says "for RC truck"), closes each from its tile, checks that every artifact panel and volume is movable and tracked
    /// as a window (Bring windows here and Close all reach it), and checks the text of the open windows. Screenshots go to
    /// Logs/os/. The result is in <see cref="LastResult"/> and a console line "[OSArtifactsTest] N passed, M failed".
    /// </summary>
    [InitializeOnLoad]
    public static class OSArtifactsTest
    {
        const string ActiveKey = "OSArtifactsTest.Active";
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

        static OSArtifactsTest()
        {
            if (SessionState.GetBool(ActiveKey, false)) EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static bool keepPlaying
        {
            get => SessionState.GetBool("OSArtifactsTest.Keep", false);
            set => SessionState.SetBool("OSArtifactsTest.Keep", value);
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

        [MenuItem("Forge/OS/Run Artifacts Test")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSArtifactsTest] Leave Play mode first."); return; }
            OSTestEnv.Enter("OSArtifactsTest");
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
            string summary = $"[OSArtifactsTest] {passed} passed, {failed} failed";
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
            OSRegistry.NotifyChanged();
            if (scratch != null)
            {
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
                catch (Exception e) { Debug.Log("[OSArtifactsTest] couldn't remove " + scratch + ": " + e.Message); }
                scratch = null;
            }
        }

        static void Check(bool condition, string what)
        {
            Debug.Log($"[OSArtifactsTest] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition) { passed++; return; }
            failed++;
            failures.Add(what);
        }

        static void Measure(string line)
        {
            measured.Add(line);
            Debug.Log("[OSArtifactsTest] measured: " + line);
        }

        static void Shot(string name)
        {
            string path = OSShot.Take(name);
            Debug.Log("[OSArtifactsTest] shot " + (path ?? "(no camera)"));
        }

        // ---------------------------------------------------------------- helpers

        static string pressWhy;
        static bool pressOk;

        // Opens the menu on the Artifacts tab (the shell, not the M key: OSMenuTest checks the key), goes to the page the tile is on
        // and presses it with a pointer. A null id only opens the tab.
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
            shell.SetTab(OSTab.Artifacts);  // after the open: the menu's first build sets its tab bar to Tools
            yield return 0.3f;
            DesktopRig.Current.LookAt(menu.LastCenter);
            yield return 0.4f;
            if (id == null) { pressOk = true; yield break; }
            // back to the first page, then forward until the tile is on the page
            for (int guard = 0; guard < 8 && menu.Page > 0; guard++)
            {
                GameObject previous = FindPageButton(menu, "Button chevron_left");
                if (previous == null || !OSToolsTest.Press(previous, out _)) break;
                yield return 0.3f;
            }
            OSTileView tile = FindTile(menu, id);
            for (int guard = 0; guard < 8 && tile == null && menu.Page < menu.PageCount - 1; guard++)
            {
                GameObject next = FindPageButton(menu, "Button chevron_right");
                if (next == null || !OSToolsTest.Press(next, out _)) break;
                yield return 0.3f;
                tile = FindTile(menu, id);
            }
            if (tile == null)
            {
                string have = "";
                for (int i = 0; i < menu.ActiveTiles.Count; i++) have += menu.ActiveTiles[i].entry?.id + ",";
                pressWhy = "no tile for " + id + " (page " + menu.Page + " of " + menu.PageCount + ", tiles: " + have + ")";
                yield break;
            }
            if (shotName != null) Shot(shotName);
            pressOk = OSToolsTest.Press(tile.gameObject, out pressWhy);
            if (!pressOk) Shot("99_press_failed_" + id);
            yield return 0.9f;
        }

        static OSTileView FindTile(OSMenu menu, string id)
        {
            for (int i = 0; i < menu.ActiveTiles.Count; i++)
                if (menu.ActiveTiles[i].entry != null && menu.ActiveTiles[i].entry.id == id) return menu.ActiveTiles[i];
            return null;
        }

        static GameObject FindPageButton(OSMenu menu, string name)
        {
            foreach (var b in menu.GetComponentsInChildren<Button>(false))
                if (b.name == name) return b.gameObject;
            return null;
        }

        static string Status(string id)
        {
            OSEntry e = OSRegistry.Find(OSTab.Artifacts, id);
            return e == null ? "(no tile)" : e.status != null ? e.status() : "";
        }

        static bool TileOpen(string id)
        {
            OSEntry e = OSRegistry.Find(OSTab.Artifacts, id);
            return e != null && e.isOpen != null && e.isOpen();
        }

        static string OpenId() => ArtifactHost.Current != null ? ArtifactHost.Current.OpenId : null;

        // The tracked windows of the open artifact, and whether each one can be moved.
        static string WindowReport(OSShell shell, out int count, out bool allMovable)
        {
            count = 0;
            allMovable = true;
            var sb = new System.Text.StringBuilder();
            foreach (var w in shell.Windows.All)
            {
                if (w == null || !w.Id.StartsWith("artifact:", StringComparison.Ordinal)) continue;
                count++;
                PanelManipulator pm = w.Panel != null ? w.Panel.GetComponent<PanelManipulator>() : null;
                VolumeManipulator vm = w.Volume != null ? w.Volume.GetComponent<VolumeManipulator>() : null;
                bool movable = pm != null ? (w.Panel.Handles != null && !pm.Busy) : vm != null && !vm.Busy;
                if (!movable) allMovable = false;
                sb.Append(w.Id).Append(' ').Append(w.Title).Append(pm != null ? " panel" : vm != null ? " volume" : " NOT MOVABLE").Append(movable ? " movable; " : " BUSY; ");
            }
            return sb.ToString();
        }

        static string[] Brands = { "meta", "oculus", "quest", "openai", "anthropic", "claude", "grok", "unity", "gpt" };

        static string BrandIn(string text)
        {
            string lower = (text ?? "").ToLowerInvariant();
            foreach (string b in Brands)
            {
                int at = lower.IndexOf(b, StringComparison.Ordinal);
                if (at < 0) continue;
                bool startOk = at == 0 || !char.IsLetter(lower[at - 1]);
                bool endOk = at + b.Length >= lower.Length || !char.IsLetter(lower[at + b.Length]);
                if (startOk && endOk) return b;
            }
            return null;
        }

        // Presses a button by its label inside a panel window; null if there is none.
        static Button FindPill(OSWindow window, string label)
        {
            foreach (var b in window.GetComponentsInChildren<Button>(false))
            {
                var text = b.GetComponentInChildren<TMPro.TMP_Text>(false);
                if (text != null && text.text == label) return b;
            }
            return null;
        }

        static OSWindow FirstArtifactPanelWindow(OSShell shell)
        {
            foreach (var w in shell.Windows.All)
                if (w != null && w.Id.StartsWith("artifact:", StringComparison.Ordinal) && w.Panel != null) return w;
            return null;
        }

        // ---------------------------------------------------------------- the script

        static IEnumerator<float> Script()
        {
            // Everything the run could write (settings, notebook, artifacts, approvals) goes to a folder of its own first; the tests that need their own root move on from here.
            OSTestEnv.UseScratch("osartifacts");
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

            // ---- a fresh install: a scratch root with no artifacts, no approvals, the truck project open
            scratch = Path.Combine(Path.GetTempPath(), "os_artifacts_test");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
            Directory.CreateDirectory(scratch);
            ArtifactLibrary.RootOverride = Path.Combine(scratch, "artifacts");
            ArtifactApprovals.FileOverride = Path.Combine(scratch, "artifacts", "approvals.json");
            NotebookStore.Open(NotebookStore.DefaultProject, scratch);
            OSProjects.Refresh();
            OSArtifacts.Register();  // the app does this at start; doing it again must not list anything twice
            OSRegistry.NotifyChanged();
            ArtifactHost host = ArtifactHost.Current ?? ArtifactBootstrap.Ensure();
            Check(host != null, "the artifact host is there");
            if (host == null) yield break;
            host.Room = MockFloorPlan.LivingRoom();  // the desktop rig's simulated room has no clear strip of floor for a slalom
            Check(UnityEngine.Object.FindAnyObjectByType<OSArtifactWatcher>() != null, "the watcher that follows the host is in the scene");

            // ---- the tab, from nothing
            var list = new List<OSEntry>();
            OSRegistry.Fill(OSTab.Artifacts, list);
            string ids = "";
            foreach (var e in list) ids += e.id + ";";
            Measure("fresh: " + ids);
            int templates = ArtifactLibrary.Templates.Length;
            Check(list.Count == templates + 1, "fresh install: " + templates + " New tiles for the templates plus Three-body (" + list.Count + ")");
            bool allNew = true, allGood = true;
            string badTile = "";
            foreach (var e in list)
            {
                if (e.section != OSArtifacts.Section || !e.outline) allNew = false;
                string brand = BrandIn(e.title) ?? BrandIn(e.status != null ? e.status() : null);
                if (string.IsNullOrEmpty(e.title) || OSUi.Icon(e.icon) == null || e.open == null || e.status == null || string.IsNullOrEmpty(e.status()) || brand != null)
                {
                    allGood = false;
                    badTile += e.id + "(" + e.title + "," + e.icon + "," + (e.status != null ? e.status() : null) + "," + brand + ") ";
                }
            }
            Check(allNew, "with nothing made yet, every tile is in the New section in the outline style");
            Check(allGood, "every tile has a title, a kit icon, a status line, an action and no brand name " + badTile);
            foreach (string t in new[] { "new_slalom", "new_hoop_course", "new_repo_activity", "new_heart", "new_solar_system", "new_double_pendulum", "three_body" })
                Check(OSRegistry.Find(OSTab.Artifacts, t) != null, "the New section has " + t);
            Check(Status("new_slalom") == "on your floor" && Status("new_heart") == "opens as a window", "a New tile says where it will go (slalom '" + Status("new_slalom") + "', heart '" + Status("new_heart") + "')");

            // ---- the heart: a volume, no approval
            var step = PressTile(shell, menu, "new_heart", "60_artifacts_tab_new");
            while (step.MoveNext()) yield return step.Current;
            Check(pressOk, "a pointer reaches the New Heart tile: " + pressWhy);
            yield return 1.2f;
            Check(OpenId() == "heart", "tapping New Heart makes the heart and opens it (open: " + (OpenId() ?? "none") + ", problem: " + host.LastProblem + ")");
            Check(Directory.Exists(Path.Combine(scratch, "artifacts", "heart")), "the heart was written to the artifact library");
            Check(OSRegistry.Find(OSTab.Artifacts, "heart") != null && TileOpen("heart"), "the library now lists the heart as open");
            string windowsText = WindowReport(shell, out int count, out bool movable);
            Check(count >= 1 && movable, "the heart is a movable window the manager tracks: " + windowsText);
            Check(shell.Windows.All.Count > 0 && FirstVolume(shell) != null, "the heart is a volume window");
            string heartStatus = Status("heart");
            Check(heartStatus.StartsWith("open", StringComparison.Ordinal), "the heart tile says open (" + heartStatus + ")");
            Check(heartStatus.Contains("for RC truck"), "made while the truck project is open, the heart is linked and the tile says for RC truck (" + heartStatus + ")");
            windows.BringHere();  // the desk's walls are in the way of where the host puts things in this simulated room
            yield return 2.5f;
            Shot("61_heart_open");

            // closing it from its tile, then opening it again
            step = PressTile(shell, menu, "heart", null);
            while (step.MoveNext()) yield return step.Current;
            yield return 1f;
            Check(pressOk && OpenId() == null, "tapping the Heart tile while it is open closes it (open: " + (OpenId() ?? "none") + ", " + pressWhy + ")");
            WindowReport(shell, out count, out movable);
            yield return 0.5f;
            Check(count == 0, "no artifact windows are left after closing the heart (" + count + ")");
            step = PressTile(shell, menu, "heart", null);
            while (step.MoveNext()) yield return step.Current;
            yield return 1.2f;
            Check(pressOk && OpenId() == "heart", "tapping the Heart tile again opens it (" + pressWhy + ")");
            windows.CloseAll();
            yield return 1f;
            Check(OpenId() == null, "Close all closes the heart");

            // ---- the host on a table (main's room-aware placement): the heart stands on a desk in front, Albie says so, the tile keeps its own line
            {
                Camera eyes = Camera.main;
                Check(eyes != null, "there is a camera to place the artifact in front of");
                if (eyes != null)
                {
                    Vector3 eye = eyes.transform.position;
                    Vector3 ahead = Vector3.ProjectOnPlane(eyes.transform.forward, Vector3.up).normalized;
                    float top = eye.y - 0.6f;
                    host.Room = new MockFloorPlan("os desk", new Vector2(12f, 12f), true, new Vector3(eye.x, 0f, eye.z))
                        .Add("TABLE", new Vector2(ahead.x * 0.8f, ahead.z * 0.8f), new Vector3(3f, top, 3f));
                    step = PressTile(shell, menu, "heart", null);
                    while (step.MoveNext()) yield return step.Current;
                    yield return 1.5f;
                    Check(pressOk && OpenId() == "heart", "tapping the Heart tile opens it with a desk in front (open: " + (OpenId() ?? "none") + ", " + pressWhy + ")");
                    Check(Mathf.Abs(host.transform.position.y - top) < 0.01f, "the host stands on the desk top (host y " + host.transform.position.y.ToString("F2") + ", desk top " + top.ToString("F2") + ")");
                    Check(host.PlacementNote != null && host.PlacementNote.StartsWith("It's on the table", StringComparison.Ordinal), "Albie is told where it went (" + (host.PlacementNote ?? "nothing") + ")");
                    string onDesk = Status("heart");
                    Check(onDesk.StartsWith("open", StringComparison.Ordinal) && !onDesk.Contains("table"), "the tile keeps its own line, not the placement note (" + onDesk + ")");
                    WindowReport(shell, out count, out movable);
                    Check(count >= 1 && movable, "the heart on the desk is still a movable, tracked window");
                    windows.CloseAll();
                    yield return 1f;
                    Check(OpenId() == null, "Close all closes the heart on the desk");
                    host.Room = MockFloorPlan.LivingRoom();
                }
            }

            // ---- the solar system
            step = PressTile(shell, menu, "new_solar_system", null);
            while (step.MoveNext()) yield return step.Current;
            yield return 1.2f;
            Check(pressOk && OpenId() == "solar_system", "tapping New Solar system opens it (open: " + (OpenId() ?? "none") + ", problem: " + host.LastProblem + ")");
            windowsText = WindowReport(shell, out count, out movable);
            Check(count >= 1 && movable, "the solar system windows are movable: " + windowsText);
            OSWindow solarPanel = FirstArtifactPanelWindow(shell);
            windows.BringHere();
            yield return 2.5f;
            if (solarPanel != null)
            {
                DesktopRig.Current.LookAt(OSToolsTest.CenterOf(solarPanel.Panel.transform));
                yield return 0.5f;
                Shot("62_solar_system_open");
                string text = OSToolsTest.AllText(solarPanel);
                string problem = OSToolsTest.LayoutProblem(solarPanel);
                Check(problem == null, "the solar system panel text is inside it and doesn't overlap" + (problem ?? ""));
                Check(BrandIn(text) == null, "no brand name in the solar system panel (" + text + ")");
            }
            step = PressTile(shell, menu, "solar_system", null);
            while (step.MoveNext()) yield return step.Current;
            yield return 1f;
            Check(pressOk && OpenId() == null, "tapping the Solar system tile closes it");

            // ---- repo activity: asks first (the Allow sheet is a movable window too)
            step = PressTile(shell, menu, "new_repo_activity", null);
            while (step.MoveNext()) yield return step.Current;
            yield return 1.2f;
            Check(pressOk && host.AwaitingApproval, "tapping New What shipped this week? asks first (awaiting " + host.AwaitingApproval + ", " + pressWhy + ")");
            Check(Status("repo_activity") == "tap Allow on the sheet", "the tile says 'tap Allow on the sheet' (" + Status("repo_activity") + ")");
            windowsText = WindowReport(shell, out count, out movable);
            Check(count >= 1 && movable, "the Allow sheet is a movable window: " + windowsText);
            OSWindow sheet = FirstArtifactPanelWindow(shell);
            Check(sheet != null, "the Allow sheet is tracked as a window");
            windows.BringHere();
            yield return 2.5f;
            if (sheet != null)
            {
                DesktopRig.Current.LookAt(OSToolsTest.CenterOf(sheet.Panel.transform));
                yield return 0.5f;
                Shot("63_allow_sheet");
                string problem = OSToolsTest.LayoutProblem(sheet);
                Check(problem == null, "the Allow sheet text is inside it and doesn't overlap" + (problem ?? ""));
                Button allow = FindPill(sheet, "Allow");
                Check(allow != null && OSToolsTest.Press(allow.gameObject, out why), "a pointer reaches the Allow pill: " + why);
                yield return 1.5f;
            }
            Check(OpenId() == "repo_activity" && !host.AwaitingApproval, "after Allow, repo activity is open (open: " + (OpenId() ?? "none") + ", problem: " + host.LastProblem + ")");
            windowsText = WindowReport(shell, out count, out movable);
            Check(count >= 1 && movable, "repo activity's panel and volume are movable windows: " + windowsText);
            windows.BringHere();
            yield return 2.5f;
            OSWindow repoPanel = FirstArtifactPanelWindow(shell);
            if (repoPanel != null)
            {
                DesktopRig.Current.LookAt(OSToolsTest.CenterOf(repoPanel.Panel.transform));
                yield return 0.5f;
                Shot("64_repo_activity_open");
                string problem = OSToolsTest.LayoutProblem(repoPanel);
                Check(problem == null, "the repo activity panel text is inside it and doesn't overlap" + (problem ?? ""));
            }
            step = PressTile(shell, menu, "repo_activity", null);
            while (step.MoveNext()) yield return step.Current;
            yield return 1f;
            Check(pressOk && OpenId() == null, "tapping the repo activity tile closes it");

            // ---- the slalom for the truck: a New slalom with the truck project open
            Check(NotebookStore.ProjectId == NotebookStore.DefaultProject, "the truck project is open (" + NotebookStore.ProjectId + ")");
            Check(BrainLoops.Active == null, "no brain truck before the slalom");
            step = PressTile(shell, menu, "new_slalom", null);
            while (step.MoveNext()) yield return step.Current;
            yield return 1.2f;
            Check(pressOk && host.AwaitingApproval, "tapping New Slalom asks first (awaiting " + host.AwaitingApproval + ", problem " + host.LastProblem + ", " + pressWhy + ")");
            sheet = FirstArtifactPanelWindow(shell);
            Allow(sheet, out why);
            yield return 2f;
            Check(OpenId() == "slalom", "after Allow the slalom is open (open: " + (OpenId() ?? "none") + ", problem: " + host.LastProblem + ")");
            float waited = 0f;
            while (BrainLoops.Active == null && waited < 8f) { waited += 0.5f; yield return 0.5f; }
            Check(BrainLoops.Active != null, "the brain truck appears");
            JObject manifest = host.OpenManifest;
            Check((string)manifest?["links"]?["thing"] == "truck", "links.thing is kept: truck (" + (string)manifest?["links"]?["thing"] + ")");
            Check((string)manifest?["links"]?["project"] == NotebookStore.DefaultProject, "the slalom links to the truck project (" + (string)manifest?["links"]?["project"] + ")");
            string slalomStatus = Status("slalom");
            Measure("slalom tile: " + slalomStatus);
            Check(slalomStatus.Contains("open") && slalomStatus.Contains("for RC truck"), "the Slalom tile says it is open and for RC truck (" + slalomStatus + ")");
            Check(slalomStatus.Length <= 26, "the status line is short enough for the tile (" + slalomStatus.Length + " characters)");
            windowsText = WindowReport(shell, out count, out movable);
            Check(count >= 1 && movable, "the slalom's scoreboard is a movable window: " + windowsText);
            windows.BringHere();
            yield return 2.5f;
            OSWindow board = FirstArtifactPanelWindow(shell);
            if (board != null)
            {
                DesktopRig.Current.LookAt(OSToolsTest.CenterOf(board.Panel.transform));
                yield return 0.5f;
                Shot("65_slalom_open");
                string problem = OSToolsTest.LayoutProblem(board);
                Check(problem == null, "the slalom panel text is inside it and doesn't overlap" + (problem ?? ""));
                Check(BrandIn(OSToolsTest.AllText(board)) == null, "no brand name in the slalom panel");
            }

            // ---- the brain truck hides while no truck course is up: the OS offers no truck then, and it is back with the slalom
            {
                var brainTruck = BrainLoops.Active as SlalomBrain;
                Check(brainTruck != null && !brainTruck.Hidden && OSProjects.LiveLoop != null, "while the slalom is open the OS offers the brain truck");
                step = PressTile(shell, menu, "slalom", null);
                while (step.MoveNext()) yield return step.Current;
                yield return 1f;
                Check(pressOk && OpenId() == null, "tapping the Slalom tile closes it");
                Check(brainTruck != null && brainTruck.Hidden && OSProjects.LiveLoop == null, "with the slalom closed the brain truck is hidden and the OS offers no truck");
                step = PressTile(shell, menu, "slalom", null);
                while (step.MoveNext()) yield return step.Current;
                yield return 2f;
                Check(pressOk && OpenId() == "slalom", "tapping the Slalom tile opens it again (" + (OpenId() ?? "none") + ")");
                Check(brainTruck != null && !brainTruck.Hidden && OSProjects.LiveLoop != null, "the brain truck is back with the slalom");
            }

            // ---- the tab with things made: user's artifacts first, then New
            yield return 0.3f;
            step = PressTile(shell, menu, null, null);
            while (step.MoveNext()) yield return step.Current;
            OSRegistry.Fill(OSTab.Artifacts, list);
            string order = "";
            foreach (var e in list) order += (e.section != null ? "*" : "") + e.id + ";";
            Measure("with data: " + order);
            int firstNew = list.FindIndex(e => e.section != null);
            bool madeFirst = firstNew == 4 && list[0].section == null && list[1].section == null && list[2].section == null && list[3].section == null;
            Check(madeFirst, "the artifacts made (heart, slalom, solar system, repo activity are in the library) come before the New row (" + order + ")");
            Check(list.Exists(e => e.id == "heart") && list.Exists(e => e.id == "slalom"), "the library lists the heart and the slalom");
            Check(menu.PageCount >= 2, "the tab has more than one page (" + menu.PageCount + ")");
            DesktopRig.Current.LookAt(menu.LastCenter);
            yield return 0.4f;
            Shot("66_artifacts_tab_page1");
            for (int p = 1; p < menu.PageCount; p++)
            {
                GameObject next = FindPageButton(menu, "Button chevron_right");
                Check(next != null && OSToolsTest.Press(next, out why), "a pointer reaches the next-page button: " + why);
                yield return 0.4f;
                Shot("67_artifacts_tab_page" + (p + 1));
            }
            GameObject first = FindPageButton(menu, "Button chevron_left");
            while (menu.Page > 0 && first != null && OSToolsTest.Press(first, out _)) yield return 0.3f;

            // ---- Bring windows here reaches the artifact windows, and they can be moved
            OSWindow far = FirstArtifactPanelWindow(shell);
            Check(far != null, "an artifact window is there to bring here");
            if (far != null)
            {
                Transform head = shell.Head;
                far.Root.position = head.position + Vector3.right * 6f;
                float before = Vector3.Distance(far.Root.position, head.position);
                windows.BringHere();
                yield return 2.5f;
                float after = Vector3.Distance(far.Root.position, head.position);
                Measure($"bring here: {before:F1} m -> {after:F1} m");
                Check(before > 5f && after < 2f, "Bring windows here reaches an artifact window (" + before.ToString("F1") + " m -> " + after.ToString("F1") + " m)");
                var handles = far.Panel.GetComponent<PanelManipulator>();
                Check(handles != null && far.Panel.Handles == handles, "the artifact panel has its manipulator and the panel knows it");
                Check(handles != null && !handles.Busy, "the manipulator is free again after the glide");
            }

            // ---- Close all closes the artifact
            windows.CloseAll();
            yield return 1.5f;
            Check(OpenId() == null, "Close all closes the slalom (open: " + (OpenId() ?? "none") + ")");
            WindowReport(shell, out count, out movable);
            Check(count == 0, "no artifact windows are left after Close all (" + count + ")");
            slalomStatus = Status("slalom");
            Check(slalomStatus.Contains("for RC truck") && !slalomStatus.StartsWith("open", StringComparison.Ordinal), "the slalom tile still says for RC truck and no longer says open (" + slalomStatus + ")");

            // ---- the honest error path
            string problemText = OSArtifacts.Open("no_such_thing");
            Check(!string.IsNullOrEmpty(problemText), "opening something that isn't there answers with the reason (" + problemText + ")");
            Check(OpenId() == null, "and nothing opened");
            windows.CloseAll();
            AlbieTools.Call("artifact_close", new JObject());
            yield return 1f;
        }

        static void Allow(OSWindow sheet, out string why)
        {
            why = "no sheet";
            if (sheet == null) return;
            Button allow = FindPill(sheet, "Allow");
            if (allow == null) { why = "no Allow pill"; return; }
            OSToolsTest.Press(allow.gameObject, out why);
        }

        static OSWindow FirstVolume(OSShell shell)
        {
            foreach (var w in shell.Windows.All)
                if (w != null && w.Id.StartsWith("artifact:", StringComparison.Ordinal) && w.Volume != null) return w;
            return null;
        }
    }
}
