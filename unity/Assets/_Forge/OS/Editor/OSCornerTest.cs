using System;
using System.Collections.Generic;
using System.IO;
using Forge.Artifacts;
using Forge.Pet;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Run Corner Test: the real Main scene in Play mode on the desktop rig with voice off, on a scratch settings file
    /// (the user's real one is never read or written). It checks that the corner button is hidden at the desk, shows once the user
    /// walks away, sits about 0.45 m out in the lower right of the view, stays fixed through head turns and walking,
    /// opens the menu when pressed, hides while a task or RC controls are open, and that Settings changes its side.
    /// Screenshots go to Logs/os/ (listed in a manifest, cleared by the next run). The result is in <see cref="LastResult"/> and a
    /// console line "[OSCornerTest] N passed, M failed".
    /// </summary>
    [InitializeOnLoad]
    public static class OSCornerTest
    {
        const string ActiveKey = "OSCornerTest.Active";
        const string ScenePath = "Assets/_Forge/Core/Main.unity";
        const double GiveUpSeconds = 240;

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

        static OSCornerTest()
        {
            if (SessionState.GetBool(ActiveKey, false)) EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static bool keepPlaying
        {
            get => SessionState.GetBool("OSCornerTest.Keep", false);
            set => SessionState.SetBool("OSCornerTest.Keep", value);
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

        [MenuItem("Forge/OS/Run Corner Test")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSCornerTest] Leave Play mode first."); return; }
            OSTestEnv.Enter("OSCornerTest");
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
            string summary = $"[OSCornerTest] {passed} passed, {failed} failed";
            LastResult = (failed == 0 ? "PASS " : "FAIL ") + summary + (failed == 0 ? "" : "\n" + string.Join("\n", failures));
            if (failed == 0) Debug.Log(summary);
            else Debug.LogError(summary + "\n" + string.Join("\n", failures));
            SessionState.SetBool(ActiveKey, false);
            if (keepPlaying) return;
            RestorePrefs();
            EditorApplication.isPlaying = false;
        }

        // Windows closed, the menu closed, the real settings file back in use, scratch deleted.
        static void Cleanup()
        {
            OSShell shell = OSShell.Current;
            if (shell != null)
            {
                shell.Windows.Close(OSSettingsWindow.WindowId);
                if (shell.MenuOpen) shell.CloseMenu();
            }
            OSSettings.SetPathForTests(null);
            ArtifactApprovals.FileOverride = null;
            try { if (scratch != null && Directory.Exists(scratch)) Directory.Delete(scratch, true); }
            catch (IOException) { }
            scratch = null;
        }

        static void Check(bool condition, string what)
        {
            Debug.Log($"[OSCornerTest] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition) { passed++; return; }
            failed++;
            failures.Add(what);
            var shell = OSShell.Current;
            if (shell != null)
            {
                var ids = new List<string>();
                foreach (var window in shell.Windows.All) if (window != null) ids.Add(window.Id);
                Debug.Log("[OSCornerTest] state: windows=" + string.Join(",", ids) + ", menu=" + shell.MenuOpen
                    + ", rc=" + shell.RcControlsOpen + ", held=" + shell.Held + ", desk=" + shell.AtDesk);
            }
        }

        static void Measure(string line)
        {
            measured.Add(line);
            Debug.Log("[OSCornerTest] measured: " + line);
        }

        static string ManifestPath => Path.Combine(OSShot.Dir, "corner_test_shots.txt");

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
            string path = OSShot.Take($"{90 + ++shotCount:00}_corner_{name}");
            Debug.Log("[OSCornerTest] shot " + (path ?? "(no camera)"));
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

        // Side (degrees, positive to the right) and drop (degrees below the gaze line) of a point, in the head's roll-free frame.
        static void Where(Transform head, Vector3 point, out float side, out float drop, out float metres)
        {
            Vector3 d = point - head.position;
            metres = d.magnitude;
            Vector3 local = Quaternion.Inverse(Quaternion.LookRotation(head.forward, Vector3.up)) * d;
            side = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            drop = -Mathf.Asin(Mathf.Clamp(local.y / Mathf.Max(metres, 1e-4f), -1f, 1f)) * Mathf.Rad2Deg;
        }

        static IEnumerable<float> Until(Func<bool> done, float seconds)
        {
            double end = EditorApplication.timeSinceStartup + seconds;
            while (!done() && EditorApplication.timeSinceStartup < end) yield return 0.1f;
        }

        static void Teleport(DesktopRig rig, Vector3 delta)
        {
            var body = rig.GetComponent<CharacterController>();
            if (body != null) body.enabled = false;  // a CharacterController undoes a teleport
            rig.transform.position += delta;
            if (body != null) body.enabled = true;
        }

        // ---------------------------------------------------------------- the script

        static IEnumerator<float> Script()
        {
            // Everything the run could write (settings, notebook, artifacts, approvals) goes to a folder of its own first; the tests that need their own root move on from here.
            OSTestEnv.UseScratch("oscorner");
            string why = null;
            scratch = Path.Combine(Path.GetTempPath(), "OSCornerTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            OSSettings.SetPathForTests(Path.Combine(scratch, "os_settings.json"));
            ArtifactApprovals.FileOverride = Path.Combine(scratch, "approvals.json");

            // ---- sit at the desk, like the other OS tests
            Workspace workspace = null;
            while (workspace == null || !workspace.presence.AtDesk)
            {
                yield return 0.25f;
                workspace = Workspace.Current;
                if (workspace != null) workspace.presence.forceSeated = true;
            }
            yield return 0.5f;
            DesktopRig rig = DesktopRig.Current;
            rig.ToggleSeat();
            yield return 1.2f;

            OSShell shell = OSShell.Current;
            Check(shell != null, "the OS shell is in Main");
            if (shell == null) yield break;
            var menu = shell.GetComponentInChildren<OSMenu>(true);
            var table = shell.GetComponent<OSTableButton>();
            var corner = shell.GetComponent<OSCornerButton>();
            Check(menu != null && table != null && corner != null, "the shell prefab carries the menu, the table button and the corner button");
            if (menu == null || table == null || corner == null) yield break;
            Check(OSCornerButton.Current == corner, "OSCornerButton.Current is the shell's corner button");
            shell.Windows.CloseAll();
            if (GestureEngine.Current != null && GestureEngine.Current.Actions != null)
                GestureEngine.Current.Actions.ShowDisplay(false);
            if (shell.Held)
            {
                var greeting = UnityEngine.Object.FindAnyObjectByType<ArrivalGreeting>();
                if (greeting != null) greeting.Fold(true);
                workspace.HoldDock(false);
                yield return 0.5f;
            }
            workspace.Close();
            yield return 1.2f;
            OSSettings s = OSSettings.Current;
            Check(s.cornerButton && !s.cornerOnLeft, "the scratch settings start with the corner button on, on the right");

            // ---- at the desk: only the fixed desk launcher is shown
            foreach (float w in Until(() => table.Shown, 6f)) yield return w;
            Check(shell.AtDesk && table.Shown, "at the desk the table button shows");
            Check(!corner.Shown, "at the desk the duplicate floating launcher stays hidden");

            // ---- stand and walk away
            var presence = workspace.presence;
            presence.forceSeated = false;
            rig.ToggleSeat();
            yield return 1.0f;
            Vector3 back = -Vector3.ProjectOnPlane(presence.Frame.forward, Vector3.up).normalized;
            if (back.sqrMagnitude < 0.5f) back = -rig.transform.forward;
            Teleport(rig, back * 2.0f);
            rig.LookAt(rig.Head.position + back * 2f);
            foreach (float w in Until(() => !presence.AtDesk, 5f)) yield return w;
            Check(!shell.AtDesk, "the user is away from the desk (state " + presence.State + ")");
            foreach (float w in Until(() => corner.Shown, 2f)) yield return w;
            Check(corner.Shown, "away from the desk the corner button shows");
            Check(!table.Shown, "away from the desk the table button is hidden");
            foreach (float w in Until(() => !corner.Easing, 2f)) yield return w;
            yield return 0.6f;
            Transform head = shell.Head;
            Check(head != null && !corner.Easing, "the shortcut appears at its anchor without following the head");
            Check(corner.Recenters == 0, "showing the shortcut after leaving the desk starts a fresh stationary anchor");

            // ---- where it sits
            Where(head, corner.Position, out float side, out float drop, out float metres);
            Measure($"corner button at {metres:F3} m, {side:F1} degrees right, {drop:F1} degrees down");
            Check(metres >= 0.40f && metres <= 0.50f, $"it is about 0.45 m from the eyes ({metres:F3} m)");
            Check(Mathf.Abs(side - 28f) < 2f, $"it sits 28 degrees to the right of the gaze line ({side:F1})");
            Check(Mathf.Abs(drop - 22f) < 2f, $"it sits 22 degrees below the gaze line ({drop:F1})");
            Check(corner.ButtonMeters >= 0.030f && corner.PlateMeters > corner.ButtonMeters, $"the button is {corner.ButtonMeters * 1000f:F0} mm on a {corner.PlateMeters * 1000f:F0} mm plate");
            Check(corner.Button != null, "the button was built");
            if (corner.Button == null) yield break;
            var buttonRect = (RectTransform)corner.Button.transform;
            float mm = OSToolsTest.Mm(Mathf.Min(buttonRect.rect.width, buttonRect.rect.height), buttonRect);
            Measure($"the pressable button measures {mm:F1} mm");
            Check(mm >= 22f, $"the pressable control is at least 22 mm ({mm:F1} mm)");
            Vector3 view = PetUser.HeadCamera.WorldToViewportPoint(corner.Position);
            Check(view.z > 0f && view.x > 0.5f && view.x < 0.98f && view.y > 0.02f && view.y < 0.5f, $"it is on screen, low and to the right (viewport {view.x:F2}, {view.y:F2})");
            Vector3 toHead = (head.position - corner.Position).normalized;
            GameObject root = corner.Button.transform.root.gameObject;
            Check(root.transform.parent == null && root.name == "OSCornerButton", "the button is a scene root, never parented to the head");
            Check(Vector3.Angle(-root.transform.forward, toHead) < 5f, "the button faces the head");
            bool hasIcon = false;
            foreach (Image img in corner.Button.GetComponentsInChildren<Image>(false)) if (img.sprite != null && img.gameObject.name.ToLowerInvariant().Contains("icon")) hasIcon = true;
            Check(hasIcon, "the button carries its icon (no empty placeholder)");
            Shot("away");

            // ---- lags the head: a small turn leaves it where it was
            int r0 = corner.Recenters;
            Vector3 p0 = corner.Position;
            rig.LookAt(head.position + Quaternion.Euler(0f, 10f, 0f) * head.forward * 2f);
            yield return 0.6f;
            Check(corner.Recenters == r0, "a 10 degree turn does not re-centre it");
            Check(Vector3.Distance(corner.Position, p0) < 0.001f, "a 10 degree turn leaves the button exactly where it was in the room (" + Vector3.Distance(corner.Position, p0).ToString("F4") + " m)");
            Where(head, corner.Position, out float side2, out float drop2, out float m2);
            Check(side2 < side - 5f, $"so it has slid toward the middle of the view as the head turned ({side:F1} to {side2:F1} degrees)");

            // ---- larger head turns and walking must not drag the shortcut across the view
            p0 = corner.Position;
            Quaternion rotation0 = root.transform.rotation;
            Vector3 flatNow = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            rig.LookAt(head.position + Quaternion.Euler(0f, 40f, 0f) * flatNow * 2f);
            yield return 0.8f;
            Check(corner.Recenters == r0 && !corner.Easing, "a 40 degree turn does not re-anchor the button");
            Check(Vector3.Distance(corner.Position, p0) < 0.001f, "a large head turn leaves its position fixed");
            Check(Quaternion.Angle(root.transform.rotation, rotation0) < 0.1f, "a head turn leaves its rotation fixed too");
            Teleport(rig, Vector3.Cross(Vector3.up, head.forward).normalized * 0.5f);
            yield return 0.8f;
            Check(corner.Recenters == r0 && Vector3.Distance(corner.Position, p0) < 0.001f,
                "walking or leaning does not pull the button after the user");
            rig.LookAt(corner.Position);
            yield return 0.3f;

            // ---- press: the menu opens, the button hides, then returns
            Check(Press(corner.Button.gameObject, out why), "a pointer reaches the corner button: " + why);
            foreach (float w in Until(() => shell.MenuOpen, 1.5f)) yield return w;
            Check(shell.MenuOpen && shell.LastSummon == OSSummon.Corner, "pressing it opens the menu with the Corner summon (" + shell.LastSummon + ")");
            yield return 0.5f;
            Check(!corner.Shown, "it hides while the menu is open");
            Shot("menu_open_corner_hidden");
            shell.CloseMenu();
            yield return 0.5f;
            Check(corner.Shown && !corner.Easing && corner.Recenters == 0, "it returns when the menu closes, in place (no fly-in)");
            Where(head, corner.Position, out side, out drop, out metres);
            Check(Mathf.Abs(side - 28f) < 2f && Mathf.Abs(drop - 22f) < 2f, $"and is back in its corner ({side:F1}, {drop:F1})");

            // ---- opening a task dismisses Tools; explicitly reopening Tools keeps the task unobscured
            shell.OpenMenu(OSSummon.Key);
            yield return 0.4f;
            var task = shell.Windows.Open("comfort-task", "Stationary task", new Vector2(0.6f, 0.4f), null);
            yield return 0.5f;
            Check(task != null && !shell.MenuOpen && !menu.Visible, "opening a task dismisses the Tools menu");
            Check(!corner.Shown, "an open task suppresses the floating shortcut");
            if (task != null)
            {
                Vector3 taskPosition = task.Root.position;
                rig.LookAt(OSToolsTest.CenterOf(task.Panel.transform));
                yield return 0.4f;
                shell.OpenMenu(OSSummon.Key);
                yield return 0.5f;
                Check(OSMenu.Blocked(head.position, menu.LastCenter, menu.LastRotation, menu.SizeMeters,
                    protectWorkspace: true) == 0, "reopened Tools sits clear of the task even when the task is farther away");
                Shot("tools_beside_task");
                Vector3 menuPosition = menu.transform.position;
                Quaternion menuRotation = menu.transform.rotation;
                rig.LookAt(head.position + Quaternion.Euler(0f, 40f, 0f) * head.forward);
                yield return 0.5f;
                Check(Vector3.Distance(menu.transform.position, menuPosition) < 0.001f
                    && Quaternion.Angle(menu.transform.rotation, menuRotation) < 0.1f, "Tools stays fixed through head turns");
                Check(Vector3.Distance(task.Root.position, taskPosition) < 0.001f, "opening Tools leaves the task in its original place");
                shell.CloseMenu();
                task.Close();
                yield return 0.5f;
            }

            var transmitter = Forge.Controllers.RcTransmitter.Instance ?? Forge.Controllers.RcTransmitter.Ensure();
            Check(transmitter != null, "the RC transmitter is available for the comfort check");
            if (transmitter != null)
            {
                shell.OpenMenu(OSSummon.Key);
                yield return 0.4f;
                transmitter.Show();
                yield return 0.5f;
                Check(!shell.MenuOpen && !menu.Visible, "bringing out RC controls dismisses an already-open menu");
                Check(!corner.Shown, "RC controls suppress the floating menu shortcut without needing a Drive window");
                shell.OpenMenu(OSSummon.Key);
                yield return 0.4f;
                Check(shell.MenuOpen, "the user can still explicitly summon Tools while RC controls are out");
                shell.CloseMenu();
                transmitter.Hide();
                yield return 0.5f;
                Check(corner.Shown, "putting away RC controls restores the shortcut when no task is open");
            }

            // ---- Settings: switch it off, bring it back, pick the other side
            OSInput.Tap(Key.M);
            yield return 0.9f;
            if (!shell.MenuOpen) { shell.OpenMenu(OSSummon.Key); yield return 0.9f; }
            rig.LookAt(menu.LastCenter);
            yield return 0.5f;
            Transform settings = FindDeep(menu.transform, "Button Settings");
            Check(settings != null && Press(settings.gameObject, out why), "a pointer reaches the Settings button: " + why);
            yield return 1.0f;
            OSSettingsWindow view2 = View(shell);
            Check(view2 != null, "the Settings window opened");
            if (view2 == null) yield break;
            rig.LookAt(OSToolsTest.CenterOf(Win(shell).Panel.transform));
            yield return 0.8f;
            Check(!corner.Shown, "the floating shortcut stays hidden while the Settings task is open");

            Toggle t = ToggleRow(View(shell), "Corner button");
            Check(t != null && t.isOn, "the Corner button switch is on");
            Check(t != null && Press(t.gameObject, out why), "a pointer reaches the Corner button switch: " + why);
            yield return 0.6f;
            Check(!OSSettings.Current.cornerButton && !corner.Shown, "Settings off: the corner button is gone at once");
            Shot("settings_off");
            t = ToggleRow(View(shell), "Corner button");
            Check(t != null && Press(t.gameObject, out why), "a pointer reaches the switch again: " + why);
            yield return 0.8f;
            Check(OSSettings.Current.cornerButton && !corner.Shown, "Settings on again: it stays hidden while the task is open");

            Button left = ChoiceButton(View(shell), "Corner side", "Left");
            Check(left != null && Press(left.gameObject, out why), "a pointer reaches Corner side Left: " + why);
            yield return 0.15f;
            Check(OSSettings.Current.cornerOnLeft && !corner.Shown, "choosing Left saves the side without showing a second menu button");
            shell.Windows.Close(OSSettingsWindow.WindowId);
            yield return 0.8f;
            Check(corner.Shown && !corner.Easing, "closing the task restores one stationary shortcut");
            Where(head, corner.Position, out side, out drop, out metres);
            Measure($"left corner: {side:F1} degrees, {drop:F1} down, {metres:F2} m");
            // It stays in the room after appearing; looking away from Settings may change its view-relative angle.
            // The exact initial 28/22-degree placement was checked above before any head movement.
            Check(side < 0f && drop > 0f, $"choosing Left restores the anchored shortcut to the left and below the eyes ({side:F1}, {drop:F1})");
            Shot("left");
            OSSettingsWindow.Open();
            yield return 0.8f;
            rig.LookAt(OSToolsTest.CenterOf(Win(shell).Panel.transform));
            yield return 0.3f;
            Button right = ChoiceButton(View(shell), "Corner side", "Right");
            Check(right != null && Press(right.gameObject, out why), "a pointer reaches Corner side Right: " + why);
            shell.Windows.Close(OSSettingsWindow.WindowId);
            yield return 1.0f;
            Where(head, corner.Position, out side, out drop, out metres);
            Check(!OSSettings.Current.cornerOnLeft && side > 0f, $"Right puts it back on the right ({side:F1})");
            shell.Windows.Close(OSSettingsWindow.WindowId);
            yield return 0.5f;

            // ---- back at the desk: the fixed desk launcher replaces it
            presence.forceSeated = true;
            foreach (float w in Until(() => shell.AtDesk, 4f)) yield return w;
            yield return 0.5f;
            Check(shell.AtDesk && !corner.Shown, "back at the desk the duplicate floating shortcut stays hidden");

            // ---- both buttons are scene objects of their own (the corner one a root, the table one under the desk frame): they go with the shell
            yield return 0.3f;
            Check(CountNamed("OSCornerButton") == 1 && CountNamed("OSTableButton") == 1, "one corner button and one table button exist");
            var hookedField = typeof(OSTableButton).GetField("hooked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Check(hookedField != null && hookedField.GetValue(table) == (object)presence, "the table button keeps the presence it listens to");
            // The workspace lets go of its presence (null) in the same frame the button is switched off: it still lets go of the one it had.
            var held = workspace.presence;
            workspace.presence = null;
            table.enabled = false;
            Check(hookedField != null && hookedField.GetValue(table) == null, "switched off with the workspace's presence gone: the listener is removed from the presence it was added to");
            workspace.presence = held;
            table.enabled = true;
            Check(hookedField != null && hookedField.GetValue(table) == (object)presence, "switched on again: it listens to the presence again");
            UnityEngine.Object.Destroy(corner);
            UnityEngine.Object.Destroy(table);
            yield return 0.3f;
            Check(CountNamed("OSCornerButton") == 0, "destroying the corner button component takes its button down with it");
            Check(CountNamed("OSTableButton") == 0, "destroying the table button component takes its button down with it");
            shell.gameObject.AddComponent<OSTableButton>();
            shell.gameObject.AddComponent<OSCornerButton>();
            yield return 0.8f;
            Check(CountNamed("OSTableButton") == 1 && shell.GetComponent<OSTableButton>().Shown, "a table button put back on the shell shows again at the desk");
            presence.forceSeated = false;
        }

        static int CountNamed(string name)
        {
            int n = 0;
            foreach (Transform t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
                if (t.name == name) n++;
            return n;
        }
    }
}
