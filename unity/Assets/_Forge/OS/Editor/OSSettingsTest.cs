using System;
using System.Collections.Generic;
using System.IO;
using Forge.Artifacts;
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
    /// Forge > OS > Run Settings Test: the real Main scene in Play mode on the desktop rig with voice off, on a scratch settings
    /// file and a scratch approvals file (the real ones are never read or written). It opens Settings from the menu strip, flips
    /// the three menu-button switches and the corner side, reads the Voice line, picks the flat and curved layout, toggles the
    /// gesture display, drags the two menu sliders and opens the menu to see it sit where they say, makes both resets ask first
    /// (Cancel keeps everything, the confirm tap does it), checks the file on disk after each change, reloads it, and scans every
    /// control for the 22 mm minimum. Screenshots of every page and of the confirm card go to Logs/os/ (listed in a manifest,
    /// cleared by the next run). The result is in <see cref="LastResult"/> and a console line "[OSSettingsTest] N passed, M failed".
    /// </summary>
    [InitializeOnLoad]
    public static class OSSettingsTest
    {
        const string ActiveKey = "OSSettingsTest.Active";
        const string ScenePath = "Assets/_Forge/Core/Main.unity";
        const double GiveUpSeconds = 300;

        /// <summary>"running", or the summary of the last run (starts with PASS or FAIL), or null if it never ran.</summary>
        public static string LastResult { get; private set; }
        /// <summary>The measurements of the last run, one per line.</summary>
        public static string Measurements { get; private set; }

        static IEnumerator<float> steps;
        static double wakeAt, startedAt;
        static int passed, failed, shotCount;
        static readonly List<string> failures = new List<string>();
        static readonly List<string> measured = new List<string>();
        static readonly List<string> logged = new List<string>();
        static string scratch;

        static OSSettingsTest()
        {
            if (SessionState.GetBool(ActiveKey, false)) EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static bool keepPlaying
        {
            get => SessionState.GetBool("OSSettingsTest.Keep", false);
            set => SessionState.SetBool("OSSettingsTest.Keep", value);
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

        [MenuItem("Forge/OS/Run Settings Test")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSSettingsTest] Leave Play mode first."); return; }
            OSTestEnv.Enter("OSSettingsTest");
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
            DeleteOldShots();
            passed = failed = shotCount = 0;
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
            string summary = $"[OSSettingsTest] {passed} passed, {failed} failed";
            LastResult = (failed == 0 ? "PASS " : "FAIL ") + summary + (failed == 0 ? "" : "\n" + string.Join("\n", failures));
            if (failed == 0) Debug.Log(summary);
            else Debug.LogError(summary + "\n" + string.Join("\n", failures));
            SessionState.SetBool(ActiveKey, false);
            if (keepPlaying) return;
            RestorePrefs();
            EditorApplication.isPlaying = false;
        }

        // Window closed, the layout and gesture display put back, the real settings and approvals files back in use, scratch deleted.
        static void Cleanup()
        {
            OSShell shell = OSShell.Current;
            if (shell != null) shell.Windows.Close(OSSettingsWindow.WindowId);
            Workspace ws = Workspace.Current;
            if (ws != null && ws.layout != WorkspaceLayout.Curved) ws.SetLayout(WorkspaceLayout.Curved);
            GestureEngine engine = GestureEngine.Current;
            if (engine != null && engine.Actions != null) engine.Actions.ShowDisplay(false);
            OSSettings.SetPathForTests(null);
            ArtifactApprovals.FileOverride = null;
            try { if (scratch != null && Directory.Exists(scratch)) Directory.Delete(scratch, true); }
            catch (IOException) { }
            scratch = null;
        }

        static void Check(bool condition, string what)
        {
            Debug.Log($"[OSSettingsTest] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition) { passed++; return; }
            failed++;
            failures.Add(what);
        }

        static void Measure(string line)
        {
            measured.Add(line);
            Debug.Log("[OSSettingsTest] measured: " + line);
        }

        // The shots of the last run are listed in a manifest, so this run clears exactly those and never another test's pictures.
        static string ManifestPath => Path.Combine(OSShot.Dir, "settings_test_shots.txt");

        static void DeleteOldShots()
        {
            try
            {
                if (!File.Exists(ManifestPath)) return;
                foreach (string name in File.ReadAllLines(ManifestPath))
                {
                    string file = Path.Combine(OSShot.Dir, name.Trim());
                    if (name.Trim().Length > 0 && File.Exists(file)) File.Delete(file);
                }
                File.WriteAllText(ManifestPath, "");
            }
            catch (IOException) { }
        }

        static string Shot(string name)
        {
            string path = OSShot.Take($"{80 + ++shotCount:00}_settings_{name}");
            Debug.Log("[OSSettingsTest] shot " + (path ?? "(no camera)"));
            if (path != null)
            {
                try { File.AppendAllText(ManifestPath, Path.GetFileName(path) + "\n"); } catch (IOException) { }
            }
            return path;
        }

        // ---------------------------------------------------------------- helpers

        static bool Press(GameObject target, out string why) => OSToolsTest.Press(target, out why);
        static Transform FindDeep(Transform root, string name) => OSToolsTest.FindDeep(root, name);
        static OSWindow Win(OSShell shell) => shell.Windows.Find(OSSettingsWindow.WindowId);
        static OSSettingsWindow View(OSShell shell) => Win(shell) != null ? Win(shell).GetComponentInChildren<OSSettingsWindow>(true) : null;
        static void Look(Vector3 point) => DesktopRig.Current.LookAt(point);
        static void LookAtWindow(OSShell shell) => Look(OSToolsTest.CenterOf(Win(shell).Panel.transform));

        static Toggle ToggleRow(OSSettingsWindow view, string label)
        {
            Transform row = FindDeep(view.transform, "Toggle " + label);
            return row != null ? row.GetComponentInChildren<Toggle>(false) : null;
        }

        static Button ChoiceButton(OSSettingsWindow view, string choice, string button)
        {
            Transform row = FindDeep(view.transform, "Choice " + choice);
            Transform b = row != null ? FindDeep(row, "Button " + button) : null;
            return b != null ? b.GetComponent<Button>() : null;
        }

        static Button ButtonOf(Transform root, string name) => FindDeep(root, name)?.GetComponent<Button>();

        static Slider SliderOf(OSSettingsWindow view, string name)
        {
            Transform col = FindDeep(view.transform, "Slider " + name);
            return col != null ? col.GetComponentInChildren<Slider>(false) : null;
        }

        static string TextOf(Transform t)
        {
            if (t == null) return "";
            var parts = new List<string>();
            foreach (TMP_Text x in t.GetComponentsInChildren<TMP_Text>(false)) if (x.gameObject.activeInHierarchy) parts.Add(x.text);
            return string.Join("|", parts);
        }

        static string FileText() => File.Exists(OSSettings.Path) ? File.ReadAllText(OSSettings.Path) : "";

        // Every control of the window is at least 22 mm both ways (buttons and toggle switches), sliders 22 mm tall.
        static void CheckTouchSizes(OSSettingsWindow window, string where)
        {
            bool ok = true;
            string worst = "";
            float smallest = 999f;
            foreach (Button b in window.GetComponentsInChildren<Button>(false))
            {
                if (b == null || !b.gameObject.activeInHierarchy || b.name == "OSConfirm") continue;
                var r = (RectTransform)b.transform;
                float w = OSToolsTest.Mm(r.rect.width, r), h = OSToolsTest.Mm(r.rect.height, r);
                float small = Mathf.Min(w, h);
                if (small < smallest) { smallest = small; worst = b.name + " " + w.ToString("F0") + " x " + h.ToString("F0"); }
                if (w < 22f || h < 22f) ok = false;
            }
            // A switch is tapped through its whole row (a Button, scanned above), so the little switch itself is not a target.
            foreach (Slider s in window.GetComponentsInChildren<Slider>(false))
            {
                if (s == null || !s.gameObject.activeInHierarchy) continue;
                var r = (RectTransform)s.transform;
                float h = OSToolsTest.Mm(r.rect.height, r);
                if (h < smallest) { smallest = h; worst = "slider height " + h.ToString("F0"); }
                if (h < 22f) ok = false;
            }
            Measure(where + ": smallest control " + worst + " mm");
            Check(ok, where + ": every control is at least 22 mm (smallest " + worst + " mm)");
        }

        // ---------------------------------------------------------------- the script

        static IEnumerator<float> Script()
        {
            // Everything the run could write (settings, notebook, artifacts, approvals) goes to a folder of its own first; the tests that need their own root move on from here.
            OSTestEnv.UseScratch("ossettings");
            string why = null;
            // ---- scratch settings and approvals: the user's own files are never touched
            scratch = Path.Combine(Path.GetTempPath(), "OSSettingsTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            OSSettings.SetPathForTests(Path.Combine(scratch, "os_settings.json"));
            ArtifactApprovals.FileOverride = Path.Combine(scratch, "approvals.json");
            var manifest = new JObject
            {
                ["id"] = "settings_test_artifact",
                ["permissions"] = new JObject { ["room"] = "read" }
            };
            ArtifactApprovals.Approve(manifest);
            Check(!ArtifactApprovals.NeedsApproval(manifest), "the scratch approvals file holds one approved artifact");

            // ---- sit at the desk, like the other OS tests
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
            var table = shell.GetComponent<OSTableButton>();
            Check(menu != null && table != null, "the shell has the menu and the table button");
            if (menu == null || table == null) yield break;
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
            Check(OSShell.HasSystem("settings"), "Settings is registered under the strip's id 'settings'");
            OSSettings s = OSSettings.Current;
            Check(s.tableButton && s.watchTurn && s.cornerButton && !s.cornerOnLeft && !s.flatLayout, "the scratch file starts at the defaults");
            float waited = 0f;
            while (!table.Shown && waited < 6f) { waited += 0.25f; yield return 0.25f; }
            Check(table.Shown, "the table button shows at the desk before any change");

            // ---- open from the strip
            var step = OpenFromMenu(shell, menu);
            while (step.MoveNext()) yield return step.Current;
            OSSettingsWindow view = View(shell);
            Check(view != null, "the Settings button opened the window");
            if (view == null) yield break;
            Check(!shell.MenuOpen, "the strip button closed the menu");
            Check(view.Page == 0 && OSSettingsWindow.PageCount == 3, "it opens on page 1 of 3");
            yield return 0.8f;
            LookAtWindow(shell);
            yield return 0.5f;

            // ---- page 1: menu button
            string text = TextOf(view.transform);
            Check(text.Contains("Table button") && text.Contains("Watch turn") && text.Contains("Corner button") && text.Contains("Corner side") && text.Contains("Hands-free") && text.Contains("1 of 3"),
                "page 1 shows the three switches, the corner side, the Hands-free line and '1 of 3': " + text);
            string problem = OSToolsTest.LayoutProblem(Win(shell));
            Check(problem == null, "page 1 has no clipped or overlapping text" + (problem ?? ""));
            Check(text.Contains("Voice is off in this session"), "the Voice line honestly says voice is off in this session (no fake switch)");
            Check(FindDeep(view.transform, "Toggle Hands-free") == null && FindDeep(view.transform, "Voice state") != null, "Hands-free is a read-only line, not a switch");
            CheckTouchSizes(view, "page 1");
            Shot("page1");

            Toggle tTable = ToggleRow(view, "Table button");
            Check(tTable != null && tTable.isOn, "the Table button switch is on");
            Check(tTable != null && Press(tTable.gameObject, out why), "a pointer reaches the Table button switch: " + why);
            yield return 0.6f;
            Check(!OSSettings.Current.tableButton, "Table button off: the setting is false");
            Check(FileText().Contains("\"tableButton\": false"), "Table button off: the file on disk says false");
            Check(!table.Shown, "Table button off: the table button is gone from the desk at once, no restart");
            Check(!ToggleRow(View(shell), "Table button").isOn, "the switch still shows off");
            tTable = ToggleRow(View(shell), "Table button");
            Check(Press(tTable.gameObject, out why), "a pointer reaches the Table button switch again: " + why);
            yield return 0.8f;
            Check(OSSettings.Current.tableButton && FileText().Contains("\"tableButton\": true"), "Table button on again: setting and file say true");
            waited = 0f;
            while (!table.Shown && waited < 4f) { waited += 0.25f; yield return 0.25f; }
            Check(table.Shown, "Table button on again: the button is back on the desk");

            foreach (string label in new[] { "Watch turn", "Corner button" })
            {
                Toggle t = ToggleRow(View(shell), label);
                Check(t != null && Press(t.gameObject, out why), "a pointer reaches the " + label + " switch: " + why);
                yield return 0.5f;
                bool now = label == "Watch turn" ? OSSettings.Current.watchTurn : OSSettings.Current.cornerButton;
                string key = label == "Watch turn" ? "\"watchTurn\": false" : "\"cornerButton\": false";
                Check(!now && FileText().Contains(key), label + " off: setting and file say false");
                t = ToggleRow(View(shell), label);
                Check(t != null && Press(t.gameObject, out why), "a pointer reaches the " + label + " switch again: " + why);
                yield return 0.5f;
                now = label == "Watch turn" ? OSSettings.Current.watchTurn : OSSettings.Current.cornerButton;
                Check(now, label + " on again");
            }

            Button right = ChoiceButton(View(shell), "Corner side", "Right");
            Check(right != null && Press(right.gameObject, out why), "a pointer reaches Corner side Right: " + why);
            yield return 0.6f;
            Check(OSSettings.Current.cornerOnLeft == false && !FileText().Contains("\"cornerOnLeft\": true"), "Right is the default side, so choosing it changes nothing");
            Button left = ChoiceButton(View(shell), "Corner side", "Left");
            Check(left != null && Press(left.gameObject, out why), "a pointer reaches Corner side Left: " + why);
            yield return 0.6f;
            Check(OSSettings.Current.cornerOnLeft && FileText().Contains("\"cornerOnLeft\": true"), "Corner side Left: setting and file say left");
            right = ChoiceButton(View(shell), "Corner side", "Right");
            Check(right != null && Press(right.gameObject, out why), "a pointer reaches Corner side Right again: " + why);
            yield return 0.6f;
            Check(!OSSettings.Current.cornerOnLeft && FileText().Contains("\"cornerOnLeft\": false"), "Corner side Right again: setting and file say right");

            // ---- page 2: windows, menu position
            view = View(shell);
            Button next = ButtonOf(view.transform, "Button chevron_right");
            Check(next != null && Press(next.gameObject, out why), "a pointer reaches the pager's next button: " + why);
            yield return 0.6f;
            view = View(shell);
            Check(view.Page == 1, "the next button went to page 2");
            text = TextOf(view.transform);
            Check(text.Contains("Layout") && text.Contains("Gesture display") && text.Contains("Distance") && text.Contains("Height") && text.Contains("2 of 3"), "page 2 shows Layout, Gesture display, Distance, Height and '2 of 3': " + text);
            Check(text.Contains("0.50 m") && text.Contains("15 degrees down"), "the sliders read '0.50 m' and '15 degrees down': " + text);
            LookAtWindow(shell);
            yield return 0.4f;
            problem = OSToolsTest.LayoutProblem(Win(shell));
            Check(problem == null, "page 2 has no clipped or overlapping text" + (problem ?? ""));
            CheckTouchSizes(view, "page 2");
            Shot("page2");

            Button flat = ChoiceButton(view, "Layout", "Flat");
            Check(flat != null && Press(flat.gameObject, out why), "a pointer reaches Layout Flat: " + why);
            yield return 1.0f;
            Check(workspace.layout == WorkspaceLayout.Flat, "Flat: the workspace is flat now, no restart (" + workspace.layout + ")");
            Check(OSSettings.Current.flatLayout && FileText().Contains("\"flatLayout\": true"), "Flat: setting and file say flat");
            view = View(shell);
            Button curved = ChoiceButton(view, "Layout", "Curved");
            Check(curved != null && Press(curved.gameObject, out why), "a pointer reaches Layout Curved: " + why);
            yield return 1.0f;
            Check(workspace.layout == WorkspaceLayout.Curved && !OSSettings.Current.flatLayout && FileText().Contains("\"flatLayout\": false"), "Curved: the workspace and the file are curved again");

            GestureEngine engine = GestureEngine.Current;
            if (engine != null && engine.Actions != null)
            {
                view = View(shell);
                GesturePanel gp = UnityEngine.Object.FindAnyObjectByType<GesturePanel>(FindObjectsInactive.Include);
                bool was = gp != null && gp.gameObject.activeInHierarchy;
                Toggle g = ToggleRow(view, "Gesture display");
                Check(g != null && g.isOn == was, "the Gesture display switch shows the real state of the panel (" + (was ? "on" : "off") + ")");
                Check(g != null && Press(g.gameObject, out why), "a pointer reaches the Gesture display switch: " + why);
                yield return 0.8f;
                Check(gp != null && gp.gameObject.activeInHierarchy == !was, "Gesture display flipped: the gesture panel is " + (!was ? "showing" : "gone"));
                Check(OSSettings.Current.gestureDisplay == !was && FileText().Contains("\"gestureDisplay\": " + (!was ? "true" : "false")), "Gesture display flipped: setting and file agree");
                view = View(shell);
                g = ToggleRow(view, "Gesture display");
                Check(g != null && g.isOn == !was, "the switch follows the panel");
                LookAtWindow(shell);
                yield return 0.3f;
                Check(g != null && Press(g.gameObject, out why), "a pointer reaches the Gesture display switch again: " + why);
                yield return 0.8f;
                Check(gp != null && gp.gameObject.activeInHierarchy == was, "Gesture display flipped back: the panel is as it was");
                Check(OSSettings.Current.gestureDisplay == was, "Gesture display flipped back: the setting agrees");
            }
            else Measure("no gesture engine in this scene: the Gesture display switch only saves the preference (skipped)");

            // ---- a relaunch with the flag saved on and no gesture panel made yet: the switch tells the truth, then the saved flag takes effect
            if (engine != null && engine.Actions != null)
            {
                GesturePanel old = UnityEngine.Object.FindAnyObjectByType<GesturePanel>(FindObjectsInactive.Include);
                bool panelWas = old != null && old.gameObject.activeInHierarchy;
                if (old != null) UnityEngine.Object.Destroy(old.gameObject);
                yield return 0.4f;
                OSSettings saved = OSSettings.Current;
                saved.gestureDisplay = true;
                saved.Save();
                yield return 0.6f;  // the window sees the file change and rebuilds
                Check(UnityEngine.Object.FindAnyObjectByType<GesturePanel>(FindObjectsInactive.Include) == null, "a fresh launch: there is no gesture panel yet");
                view = View(shell);
                Toggle fresh = ToggleRow(view, "Gesture display");
                Check(OSSettings.Current.gestureDisplay && fresh != null && !fresh.isOn, "the flag is saved on and nothing is shown: the switch shows OFF, the real state");
                Check(OSToolsCatalog.ApplySavedGestureDisplay(), "the saved flag is applied once the gesture engine exists");
                yield return 0.5f;
                GesturePanel made = UnityEngine.Object.FindAnyObjectByType<GesturePanel>(FindObjectsInactive.Include);
                Check(made != null && made.gameObject.activeInHierarchy, "the saved flag put the gesture display on screen");
                view = View(shell);
                view.SetPage(0);
                view.SetPage(1);
                yield return 0.4f;
                fresh = ToggleRow(View(shell), "Gesture display");
                Check(fresh != null && fresh.isOn, "now the switch shows ON");
                saved = OSSettings.Current;
                saved.gestureDisplay = false;
                saved.Save();
                if (made != null && !panelWas) engine.Actions.ShowDisplay(false);
                OSSettings.Current.gestureDisplay = false;
                Check(OSToolsCatalog.ApplySavedGestureDisplay() && (made == null || made.gameObject.activeInHierarchy == panelWas), "with the flag off, applying it neither shows nor hides anything");
                yield return 0.3f;
            }

            view = View(shell);
            Slider distance = SliderOf(view, "Distance"), height = SliderOf(view, "Height");
            Check(distance != null && height != null, "both menu sliders exist");
            distance.value = 0.6f;
            height.value = 20f;
            yield return 0.4f;
            Check(Mathf.Approximately(OSSettings.Current.menuDistance, 0.6f), "Distance 0.6: the setting is " + OSSettings.Current.menuDistance);
            Check(Mathf.Approximately(OSSettings.Current.menuDropDegrees, 20f), "Height 20: the setting is " + OSSettings.Current.menuDropDegrees);
            Check(FileText().Contains("\"menuDistance\": 0.6") && FileText().Contains("\"menuDropDegrees\": 20"), "the file on disk holds 0.6 and 20");
            string dText = TextOf(FindDeep(View(shell).transform, "Slider Distance")), hText = TextOf(FindDeep(View(shell).transform, "Slider Height"));
            Check(dText.Contains("0.60 m") && hText.Contains("20 degrees down"), "the readouts say '0.60 m' and '20 degrees down': " + dText + " / " + hText);
            Check(View(shell).Page == 1 && SliderOf(View(shell), "Distance") == distance, "dragging a slider does not rebuild the page under the finger");
            Shot("page2_sliders");

            // The menu opens at the new place (with the window put away, nothing is in the menu's way).
            windows.Close(OSSettingsWindow.WindowId);
            yield return 0.6f;
            shell.OpenMenu(OSSummon.Key);
            yield return 1.0f;
            Transform head = shell.Head;
            Vector3 toMenu = menu.LastCenter - head.position;
            float dist = toMenu.magnitude;
            float below = Mathf.Asin(Mathf.Clamp(-toMenu.y / dist, -1f, 1f)) * Mathf.Rad2Deg;
            Measure("the menu opened " + dist.ToString("F3") + " m from the eyes, " + below.ToString("F1") + " degrees below level");
            Check(Mathf.Abs(dist - 0.6f) < 0.05f, "the menu opens about 0.6 m away (" + dist.ToString("F3") + ")");
            Check(Mathf.Abs(below - 20f) < 3f, "the menu opens about 20 degrees below level (" + below.ToString("F1") + ")");
            step = OpenFromMenu(shell, menu);
            while (step.MoveNext()) yield return step.Current;
            view = View(shell);
            Check(view != null && view.Page == 0, "Settings reopened from the strip, on page 1");
            yield return 0.6f;
            Check(Mathf.Approximately(SliderOf(SetPageAndGet(shell, 1), "Distance").value, 0.6f), "reopened, the Distance slider shows the saved 0.6");

            // ---- page 3: resets, about
            view = SetPageAndGet(shell, 2);
            yield return 0.6f;
            LookAtWindow(shell);
            yield return 0.4f;
            text = TextOf(view.transform);
            Check(text.Contains("Window positions") && text.Contains("Artifact permissions") && text.Contains("About") && text.Contains("version") && text.Contains("3 of 3"), "page 3 shows both resets, About, the version and '3 of 3': " + text);
            Check(text.Contains("Grab3D") && text.Contains("Quirky Series") && text.Contains("Fredoka") && text.Contains("Meta"), "About credits the CC0 models, the gator, the font and the UI Set");
            problem = OSToolsTest.LayoutProblem(Win(shell));
            Check(problem == null, "page 3 has no clipped or overlapping text" + (problem ?? ""));
            CheckTouchSizes(view, "page 3");
            Shot("page3");

            // Artifact permissions: asks first
            Button forget = ButtonOf(view.transform, "Button Forget permissions");
            Check(forget != null && Press(forget.gameObject, out why), "a pointer reaches Forget permissions: " + why);
            yield return 0.6f;
            Transform card = FindDeep(view.transform, "OSConfirm");
            Check(card != null, "Forget permissions opened a confirm card");
            Check(TextOf(card).Contains("Forget every permission"), "the card asks the question: " + TextOf(card));
            Check(!ArtifactApprovals.NeedsApproval(manifest), "nothing is forgotten before the answer");
            LookAtWindow(shell);
            yield return 0.4f;
            Shot("confirm_permissions");
            Button cancel = card != null ? ButtonOf(card, "Button Cancel") : null;
            Check(cancel != null && Press(cancel.gameObject, out why), "a pointer reaches Cancel: " + why);
            yield return 0.6f;
            Check(FindDeep(view.transform, "OSConfirm") == null && !ArtifactApprovals.NeedsApproval(manifest) && view.ResetNote.Length == 0, "Cancel closes the card and forgets nothing");
            forget = ButtonOf(view.transform, "Button Forget permissions");
            Check(forget != null && Press(forget.gameObject, out why), "a pointer reaches Forget permissions again: " + why);
            yield return 0.6f;
            card = FindDeep(view.transform, "OSConfirm");
            Button yes = card != null ? ButtonOf(card, "Button Forget them") : null;
            Check(yes != null && Press(yes.gameObject, out why), "a pointer reaches Forget them: " + why);
            yield return 0.8f;
            Check(ArtifactApprovals.NeedsApproval(manifest), "the confirm tap forgot the approval: the artifact asks again");
            Check(view.ResetNote.Contains("permissions cleared") && TextOf(view.transform).Contains("Artifact permissions cleared"), "the page says what was done: " + view.ResetNote);
            LookAtWindow(shell);
            yield return 0.3f;
            problem = OSToolsTest.LayoutProblem(Win(shell));
            Check(problem == null, "page 3 with the note has no clipped or overlapping text" + (problem ?? ""));
            Shot("page3_after_reset");

            // Window positions: asks first, then the windows come back
            OSWindow w = Win(shell);
            Vector3 home = w.Root.position;
            w.Root.position = home + head.right * 0.35f + Vector3.up * 0.1f;
            Vector3 moved = w.Root.position;
            LookAtWindow(shell);
            yield return 0.4f;
            view = View(shell);
            Button bring = ButtonOf(view.transform, "Button Bring windows here");
            Check(bring != null && Press(bring.gameObject, out why), "a pointer reaches Bring windows here: " + why);
            yield return 0.6f;
            card = FindDeep(view.transform, "OSConfirm");
            Check(card != null && TextOf(card).Contains("comes back in front of you"), "Bring windows here opened a confirm card: " + TextOf(card));
            Check(Vector3.Distance(w.Root.position, moved) < 0.01f, "the window did not move before the answer");
            cancel = card != null ? ButtonOf(card, "Button Cancel") : null;
            Check(cancel != null && Press(cancel.gameObject, out why), "a pointer reaches Cancel: " + why);
            yield return 0.5f;
            Check(Vector3.Distance(w.Root.position, moved) < 0.01f, "Cancel left the window where it was");
            bring = ButtonOf(view.transform, "Button Bring windows here");
            Check(bring != null && Press(bring.gameObject, out why), "a pointer reaches Bring windows here again: " + why);
            yield return 0.6f;
            card = FindDeep(view.transform, "OSConfirm");
            yes = card != null ? ButtonOf(card, "Button Bring them here") : null;
            Check(yes != null && Press(yes.gameObject, out why), "a pointer reaches Bring them here: " + why);
            yield return 2.5f;
            float travelled = Vector3.Distance(w.Root.position, moved);
            Measure("Bring them here moved the window " + travelled.ToString("F2") + " m");
            Check(travelled > 0.1f, "the confirm tap brought the window back (" + travelled.ToString("F2") + " m)");
            Check(View(shell).ResetNote.Contains("Windows brought back"), "the page says the windows came back: " + View(shell).ResetNote);

            // ---- the file: round trip and a reload that the open window follows
            string path = OSSettings.Path;
            Check(File.Exists(path), "os_settings.json exists at the scratch path");
            OSSettings.Current.flatLayout = false;
            OSSettings.Current.menuDistance = 0.45f;
            OSSettings.Current.Save();
            OSSettings.Reload();
            Check(Mathf.Approximately(OSSettings.Current.menuDistance, 0.45f), "Save then Reload reads back 0.45");
            Check(FileText().Contains("\"menuDistance\": 0.44999") && FileText().Contains("\"cornerOnLeft\": false") && FileText().Contains("\"tableButton\": true"), "the file text holds the whole set: " + FileText().Replace(System.Environment.NewLine, " "));
            yield return 0.5f;
            view = SetPageAndGet(shell, 1);
            yield return 0.4f;
            Check(Mathf.Approximately(SliderOf(view, "Distance").value, 0.45f) && TextOf(FindDeep(view.transform, "Slider Distance")).Contains("0.45 m"), "after a reload the open window shows the new value, 0.45 m");

            windows.Close(OSSettingsWindow.WindowId);
            yield return 0.6f;
            Check(!windows.IsOpen(OSSettingsWindow.WindowId), "the Settings window closes");
        }

        static OSSettingsWindow SetPageAndGet(OSShell shell, int page)
        {
            OSSettingsWindow v = View(shell);
            if (v != null) v.SetPage(page);
            return View(shell);
        }

        // The menu's Settings button, pressed with a pointer (the menu opens with the M key, or the shell when the editor is unfocused).
        static IEnumerator<float> OpenFromMenu(OSShell shell, OSMenu menu)
        {
            if (!shell.MenuOpen)
            {
                OSInput.Tap(Key.M);
                yield return 0.9f;
                if (!shell.MenuOpen)
                {
                    Debug.Log("[OSSettingsTest] M didn't open the menu (editor focus " + UnityEditorInternal.InternalEditorUtility.isApplicationActive + "); opening it with the shell");
                    shell.OpenMenu(OSSummon.Key);
                    yield return 0.9f;
                }
            }
            Check(shell.MenuOpen, "the menu is open");
            Look(menu.LastCenter);
            yield return 0.5f;
            Transform settings = FindDeep(menu.transform, "Button Settings");
            Check(settings != null, "the menu strip has a Settings button");
            string why = null;
            Check(settings != null && Press(settings.gameObject, out why), "a pointer reaches the Settings button: " + why);
            yield return 1.0f;
        }
    }
}
