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
    /// walks away, sits about 0.45 m out in the lower right of the view, lags the head (a 10 degree turn leaves it alone, a 40
    /// degree turn or half a metre of walking re-anchors it with a 0.4 s ease), opens the menu when pressed, hides while the menu is
    /// open and returns when it closes, and that the Settings window switches it off and moves it to the left or the right.
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
            EditorPrefs.SetBool(RigSelector.ForceDesktopPref, EditorPrefs.GetBool("OSCornerTest.ForceDesktopWas", false));
            EditorPrefs.SetBool(AlbieVoice.EditorOffPref, EditorPrefs.GetBool("OSCornerTest.VoiceOffWas", false));
        }

        [MenuItem("Forge/OS/Run Corner Test")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSCornerTest] Leave Play mode first."); return; }
            EditorPrefs.SetBool("OSCornerTest.ForceDesktopWas", EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false));
            EditorPrefs.SetBool("OSCornerTest.VoiceOffWas", EditorPrefs.GetBool(AlbieVoice.EditorOffPref, false));
            EditorPrefs.SetBool(RigSelector.ForceDesktopPref, true);
            EditorPrefs.SetBool(AlbieVoice.EditorOffPref, true);
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

            // ---- at the desk: hidden
            foreach (float w in Until(() => table.Shown, 6f)) yield return w;
            Check(shell.AtDesk && table.Shown, "at the desk the table button shows");
            Check(!corner.Shown, "at the desk the corner button is hidden");

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
            yield return 0.6f;
            Transform head = shell.Head;
            Check(head != null && !corner.Easing, "the first showing is not a fly-in (it is not easing)");
            Check(corner.Recenters == 0, "the first placing is not counted as a re-centre");

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

            // ---- a big turn: it eases there over about 0.4 s
            p0 = corner.Position;
            Vector3 flatNow = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            rig.LookAt(head.position + Quaternion.Euler(0f, 40f, 0f) * flatNow * 2f);
            double t0 = EditorApplication.timeSinceStartup, easeStart = -1, easeEnd = -1;
            bool partial = false, monotonic = true;
            float lastDist = 0f;
            Vector3 target = p0;
            for (int i = 0; i < 400; i++)
            {
                yield return 0f;
                double now = EditorApplication.timeSinceStartup;
                if (corner.Easing)
                {
                    if (easeStart < 0) { easeStart = now; target = corner.AnchorPosition; }
                    float travelled = Vector3.Distance(corner.Position, p0), total = Vector3.Distance(target, p0);
                    if (!partial && travelled < total * 0.9f) partial = true;
                    if (travelled < lastDist - 0.0005f) monotonic = false;
                    lastDist = travelled;
                }
                else if (easeStart >= 0) { easeEnd = now; break; }
                if (now - t0 > 3.0) break;
            }
            double easeTook = easeEnd >= 0 ? easeEnd - easeStart : -1;
            Measure($"the ease took {easeTook:F2} s (set 0.4), it moved {Vector3.Distance(corner.Position, p0):F2} m");
            Check(easeStart >= 0, "a 40 degree turn made it start easing to a new anchor");
            Check(partial, "it was seen part of the way there (it does not jump)");
            Check(monotonic, "the ease only moves forward");
            Check(easeTook >= 0.2 && easeTook <= 0.9, $"the ease lasts about 0.4 s ({easeTook:F2} s)");
            Check(!corner.Easing && corner.Recenters == r0 + 1, $"it settled and re-centred once (Recenters {corner.Recenters})");
            Check(Vector3.Distance(corner.Position, p0) > 0.15f, $"the button moved across the room ({Vector3.Distance(corner.Position, p0):F2} m)");
            Check(Vector3.Distance(corner.Position, corner.AnchorPosition) < 0.001f, "it ended on its anchor");
            Where(head, corner.Position, out side, out drop, out metres);
            Check(Mathf.Abs(side - 28f) < 2f && Mathf.Abs(drop - 22f) < 2f && metres > 0.4f && metres < 0.5f, $"and sits in the same corner of the view again ({side:F1} right, {drop:F1} down, {metres:F2} m)");

            // ---- walking half a metre re-centres too
            r0 = corner.Recenters;
            Teleport(rig, Vector3.Cross(Vector3.up, head.forward).normalized * 0.5f);
            yield return 0.8f;
            Check(corner.Recenters == r0 + 1, "walking half a metre re-centres it once");
            Teleport(rig, Vector3.Cross(Vector3.up, head.forward).normalized * 0.1f);
            yield return 0.5f;
            Check(corner.Recenters == r0 + 1, "a 0.1 m step does not");

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
            foreach (float w in Until(() => corner.Shown, 2f)) yield return w;
            Check(corner.Shown, "the corner button is back once the menu has closed behind the Settings press");

            Toggle t = ToggleRow(View(shell), "Corner button");
            Check(t != null && t.isOn, "the Corner button switch is on");
            Check(t != null && Press(t.gameObject, out why), "a pointer reaches the Corner button switch: " + why);
            yield return 0.6f;
            Check(!OSSettings.Current.cornerButton && !corner.Shown, "Settings off: the corner button is gone at once");
            Shot("settings_off");
            t = ToggleRow(View(shell), "Corner button");
            Check(t != null && Press(t.gameObject, out why), "a pointer reaches the switch again: " + why);
            yield return 0.8f;
            Check(OSSettings.Current.cornerButton && corner.Shown, "Settings on again: it is back");

            Button left = ChoiceButton(View(shell), "Corner side", "Left");
            Check(left != null && Press(left.gameObject, out why), "a pointer reaches Corner side Left: " + why);
            yield return 0.15f;
            Check(corner.Easing || corner.Recenters > 0, "choosing Left re-anchors it with an ease");
            yield return 0.8f;
            Check(OSSettings.Current.cornerOnLeft && !corner.Easing, "Settings says left and the ease is done");
            Where(head, corner.Position, out side, out drop, out metres);
            Measure($"left corner: {side:F1} degrees, {drop:F1} down, {metres:F2} m");
            Check(Mathf.Abs(side + 28f) < 2f && Mathf.Abs(drop - 22f) < 2f, $"it now sits 28 degrees to the left, 22 down ({side:F1}, {drop:F1})");
            Shot("left");
            Button right = ChoiceButton(View(shell), "Corner side", "Right");
            Check(right != null && Press(right.gameObject, out why), "a pointer reaches Corner side Right: " + why);
            yield return 1.0f;
            Where(head, corner.Position, out side, out drop, out metres);
            Check(!OSSettings.Current.cornerOnLeft && Mathf.Abs(side - 28f) < 2f, $"Right puts it back on the right ({side:F1})");
            shell.Windows.Close(OSSettingsWindow.WindowId);
            yield return 0.5f;

            // ---- back at the desk: hidden again
            presence.forceSeated = true;
            foreach (float w in Until(() => shell.AtDesk, 4f)) yield return w;
            yield return 0.5f;
            Check(shell.AtDesk && !corner.Shown, "back at the desk the corner button is hidden again");
            presence.forceSeated = false;
        }
    }
}
