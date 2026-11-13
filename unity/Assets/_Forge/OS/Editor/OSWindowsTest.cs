using System;
using System.Collections.Generic;
using System.IO;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Run Windows Test: the real Main scene in Play mode on the desktop rig with voice off, driving the window
    /// manager and the workspace hook the way a user would. It watches the first arrival (the greeting holds the desk, the
    /// table button stays hidden until START, then the code view opens), sits at the desk and opens two OS windows and one
    /// Workspace mode (CAD) at once, checks that none overlap, walks away (forced Away) and checks that all three are still
    /// there at the same world positions, brings them here, opens another mode while away, goes through the voice tool path,
    /// and closes everything. Screenshots go to Logs/os/; the result is in <see cref="LastResult"/> and a console line
    /// "[OSWindowsTest] N passed, M failed". Start it with <c>OSWindowsTest.Run()</c>; it opens Main.unity, enters Play and leaves Play when done.
    /// </summary>
    [InitializeOnLoad]
    public static class OSWindowsTest
    {
        const string ActiveKey = "OSWindowsTest.Active";
        const string ScenePath = "Assets/_Forge/Core/Main.unity";
        const double GiveUpSeconds = 170;

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
        static bool wasFlat;

        static OSWindowsTest()
        {
            if (SessionState.GetBool(ActiveKey, false)) EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static bool keepPlaying
        {
            get => SessionState.GetBool("OSWindowsTest.Keep", false);
            set => SessionState.SetBool("OSWindowsTest.Keep", value);
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
            EditorPrefs.SetBool(RigSelector.ForceDesktopPref, EditorPrefs.GetBool("OSWindowsTest.ForceDesktopWas", false));
            EditorPrefs.SetBool(AlbieVoice.EditorOffPref, EditorPrefs.GetBool("OSWindowsTest.VoiceOffWas", false));
        }

        [MenuItem("Forge/OS/Run Windows Test")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSWindowsTest] Leave Play mode first."); return; }
            EditorPrefs.SetBool("OSWindowsTest.ForceDesktopWas", EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false));
            EditorPrefs.SetBool("OSWindowsTest.VoiceOffWas", EditorPrefs.GetBool(AlbieVoice.EditorOffPref, false));
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

        // Errors and exceptions during the run are failures: the OS must run quietly.
        static void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                if (message.StartsWith("[MetaXRFeature]", StringComparison.Ordinal)) return;
                logged.Add(type + ": " + message);
            }
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
            Application.logMessageReceived -= OnLog;
            try
            {
                if (OSSettings.Current.flatLayout != wasFlat)
                {
                    OSSettings.Current.flatLayout = wasFlat;
                    OSSettings.Current.Save();
                }
            }
            catch (Exception e) { Debug.LogException(e); }
            Check(logged.Count == 0, "no errors or exceptions were logged during the run" + (logged.Count == 0 ? "" : ": " + logged[0]));
            Measurements = string.Join("\n", measured);
            string summary = $"[OSWindowsTest] {passed} passed, {failed} failed";
            LastResult = (failed == 0 ? "PASS " : "FAIL ") + summary + (failed == 0 ? "" : "\n" + string.Join("\n", failures));
            if (failed == 0) Debug.Log(summary);
            else Debug.LogError(summary + "\n" + string.Join("\n", failures));
            SessionState.SetBool(ActiveKey, false);
            if (keepPlaying) return;
            RestorePrefs();
            EditorApplication.isPlaying = false;
        }

        static void Check(bool condition, string what)
        {
            Debug.Log($"[OSWindowsTest] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition) { passed++; return; }
            failed++;
            failures.Add(what);
        }

        static void Measure(string line)
        {
            measured.Add(line);
            Debug.Log("[OSWindowsTest] measured: " + line);
        }

        static void Shot(string name)
        {
            string path = OSShot.Take(name);
            Debug.Log("[OSWindowsTest] shot " + (path ?? "(no camera)"));
        }

        // ---------------------------------------------------------------- real pointer input (as in the menu test)

        static bool Press(GameObject target, out string why)
        {
            why = null;
            Camera cam = PetUser.HeadCamera;
            var system = EventSystem.current;
            if (target == null) { why = "no such control"; return false; }
            if (cam == null || system == null) { why = "no camera or EventSystem"; return false; }
            var rect = (RectTransform)target.transform;
            Vector3 screen = cam.WorldToScreenPoint(rect.TransformPoint(rect.rect.center));
            if (screen.z <= 0f) { why = "behind the camera"; return false; }
            var data = new PointerEventData(system) { position = screen, button = PointerEventData.InputButton.Left };
            var hits = new List<RaycastResult>();
            system.RaycastAll(data, hits);
            if (hits.Count == 0) { why = "nothing under the pointer at " + screen; return false; }
            GameObject top = hits[0].gameObject;
            if (top != target && !top.transform.IsChildOf(target.transform))
            {
                why = "'" + top.name + "' is on top of '" + target.name + "'";
                return false;
            }
            data.pointerCurrentRaycast = hits[0];
            data.pointerPressRaycast = hits[0];
            data.pressPosition = screen;
            ExecuteEvents.ExecuteHierarchy(top, data, ExecuteEvents.pointerEnterHandler);
            GameObject pressed = ExecuteEvents.ExecuteHierarchy(top, data, ExecuteEvents.pointerDownHandler);
            data.pointerPress = pressed;
            data.rawPointerPress = top;
            if (pressed != null) ExecuteEvents.Execute(pressed, data, ExecuteEvents.pointerUpHandler);
            GameObject clickable = ExecuteEvents.GetEventHandler<IPointerClickHandler>(top);
            if (clickable != null && clickable == pressed) ExecuteEvents.Execute(clickable, data, ExecuteEvents.pointerClickHandler);
            ExecuteEvents.ExecuteHierarchy(top, data, ExecuteEvents.pointerExitHandler);
            return true;
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        static float Mm(float units, Transform t) => units * Mathf.Abs(t.lossyScale.x) * 1000f;

        // ---------------------------------------------------------------- geometry

        // The four corners' yaw and pitch seen from the eyes: the angular rectangle a panel covers.
        static Rect Angular(ForgePanel panel, Transform head)
        {
            var rect = (RectTransform)panel.transform;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Vector3 flat = head.forward;
            flat.y = 0f;
            flat.Normalize();
            float minYaw = 999f, maxYaw = -999f, minPitch = 999f, maxPitch = -999f;
            for (int i = 0; i < 4; i++)
            {
                Vector3 o = corners[i] - head.position;
                float yaw = Vector3.SignedAngle(flat, new Vector3(o.x, 0f, o.z), Vector3.up);
                float pitch = Mathf.Atan2(o.y, new Vector2(o.x, o.z).magnitude) * Mathf.Rad2Deg;
                minYaw = Mathf.Min(minYaw, yaw); maxYaw = Mathf.Max(maxYaw, yaw);
                minPitch = Mathf.Min(minPitch, pitch); maxPitch = Mathf.Max(maxPitch, pitch);
            }
            return Rect.MinMaxRect(minYaw, minPitch, maxYaw, maxPitch);
        }

        static Vector3 CenterOf(ForgePanel panel) => panel.transform.TransformPoint(((RectTransform)panel.transform).rect.center);

        static float Distance(Transform head, Vector3 p) => Vector3.Distance(head.position, p);

        static float AngleFromView(Transform head, Vector3 p) => Vector3.Angle(head.forward, p - head.position);

        // ---------------------------------------------------------------- the script

        static void Fill(ForgePanel panel, string text)
        {
            RectTransform body = OSUi.Body(panel);
            if (body == null) return;
            RectTransform column = OSUi.Column(body, 12f);
            OSUi.Text(column, text, OSUi.TitleText);
            OSUi.Text(column, "A free window. It stays where it is when you walk away from the desk.", OSUi.BodyText, true);
            OSUi.Button(column, "Primary", null, OSButtonStyle.Primary);
        }

        static IEnumerator<float> Script()
        {
            wasFlat = OSSettings.Current.flatLayout;
            string why;

            // ---- sit at the desk the way the gesture test does
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
            var table = shell.GetComponent<OSTableButton>();
            Check(table != null, "the shell carries the table button");
            Check(workspace.DockRetired && workspace.KeepOpenAway && workspace.FreeFrame != null, "the OS hooked the workspace (dock retired, keep open away, free frame)");
            Check(workspace.dock != null && !workspace.dock.IsShown, "the dock isn't showing at the desk");

            // ---- first arrival: the greeting holds the desk, then START opens the code view
            var greeting = UnityEngine.Object.FindAnyObjectByType<ArrivalGreeting>();
            float waited = 0f;
            while (greeting != null && !greeting.IsShown && waited < 14f) { waited += 0.5f; yield return 0.5f; }
            if (greeting != null && greeting.IsShown)
            {
                Check(shell.Held, "the greeting holds the desk (Held)");
                Check(table == null || !table.Shown, "the table button stays hidden while the greeting holds the desk");
                Check(!workspace.dock.IsShown, "the dock stays hidden while the greeting holds the desk");
                yield return 0.5f;
                Shot("20_arrival_greeting");
                greeting.OnStartPressed();
                waited = 0f;
                while (workspace.ActiveMode != "code" && waited < 8f) { waited += 0.5f; yield return 0.5f; }
                Check(workspace.ActiveMode == "code", "START opens the code view (" + workspace.ActiveMode + ")");
                Check(!shell.Held, "START releases the desk");
                yield return 2f;
                waited = 0f;
                while (table != null && !table.Shown && waited < 4f) { waited += 0.5f; yield return 0.5f; }
                Check(table != null && table.Shown, "the table button shows once the desk is released");
                Check(!workspace.dock.IsShown, "the dock still isn't showing after START");
                Check(OSModes.IsOpen("code") && shell.Windows.IsOpen(OSWindows.GroupId("code")), "the open code view is tracked as a window group");
                yield return 1f;
                Shot("21_arrival_after_start");
            }
            else
            {
                Debug.Log("[OSWindowsTest] the greeting did not come up in this run; the arrival checks are skipped");
                if (shell.Held) workspace.HoldDock(false);
                yield return 0.5f;
            }
            workspace.Close();
            yield return 1.2f;

            // ---- three things at once at the desk: two OS windows and CAD
            Transform head = shell.Head;
            OSWindows windows = shell.Windows;
            OSWindow w1 = windows.Open("zz_one", "First window", new Vector2(0.5f, 0.34f), p => Fill(p, "First"), "TEST", ForgeTheme.Tool);
            OSWindow w2 = windows.Open("zz_two", "Second window", new Vector2(0.5f, 0.34f), p => Fill(p, "Second"), "TEST", ForgeTheme.Tool);
            Check(w1 != null && w2 != null, "two OS windows opened");
            Check(OSModes.Open("cad"), "CAD opened with OSModes at the desk");
            yield return 2.5f;
            Check(workspace.ActiveMode == "cad" && OSModes.IsOpen("cad"), "CAD is the active mode");
            Check(windows.Count == 3 && windows.IsOpen(OSWindows.GroupId("cad")), "the window list holds the two windows and the CAD group (" + windows.Count + ")");
            Check(w1.Panel.Handles != null && !w1.Panel.Handles.Busy && !w2.Panel.Handles.Busy, "the windows have gesture handles and aren't Busy after the open animation");
            Check(w1.Panel.GetComponent<GestureWindow>() != null, "the windows carry a GestureWindow, so the flick and the minimise dot work");
            Check(w1.transform.parent == null && !w1.transform.IsChildOf(workspace.presence.Frame), "an OS window is a free root object, not under the desk frame");

            ForgePanel[] mode = ModePanels(workspace);
            Check(mode.Length >= 3, "CAD has its panels (" + mode.Length + ")");
            AssertNoOverlap("at the desk", head, w1, w2, mode);
            Vector3 p1 = CenterOf(w1.Panel), p2 = CenterOf(w2.Panel);
            var modePositions = new Vector3[mode.Length];
            for (int i = 0; i < mode.Length; i++) modePositions[i] = CenterOf(mode[i]);
            Transform cadViewer = null;
            workspace.TryGetItemTransform("cad-viewer", out cadViewer);
            Vector3 viewerAt = cadViewer != null ? cadViewer.position : Vector3.zero;
            Measure($"at the desk: window 1 {Distance(head, p1):F2} m / {AngleFromView(head, p1):F0} deg, window 2 {Distance(head, p2):F2} m / {AngleFromView(head, p2):F0} deg from the eyes");
            Shot("10_desk_three");

            // the header's close button: size and a real press (on a third window)
            OSWindow w3 = windows.Open("zz_three", "Third window", new Vector2(0.4f, 0.3f), p => Fill(p, "Third"), "TEST", ForgeTheme.Tool);
            yield return 0.6f;
            Transform closeButton = FindDeep(w3.transform, "Button close");
            Check(closeButton != null, "an OS window has a close button in its header");
            if (closeButton != null)
            {
                var r = (RectTransform)closeButton;
                Measure($"close button: {Mm(r.rect.width, r):F0} x {Mm(r.rect.height, r):F0} mm");
                Check(Mm(r.rect.width, r) >= 22f && Mm(r.rect.height, r) >= 22f, "the close button is at least 22 x 22 mm");
                DesktopRig.Current.LookAt(CenterOf(w3.Panel));
                yield return 0.3f;
                Shot("11_close_button");
                Check(Press(closeButton.gameObject, out why), "a pointer reaches the close button: " + why);
                yield return 0.6f;
                Check(!windows.IsOpen("zz_three") && w3 == null, "pressing the close button closes the window");
            }
            else if (w3 != null) w3.Close();
            DesktopRig.Current.LookAt(workspace.presence.Frame.position + Vector3.up * 0.3f);
            yield return 0.3f;

            // the minimise dot: a flick away on a window shrinks it into a dot; it stays through leaving the desk
            var gesture = GestureWindow.Of(w2.Panel);
            string said = gesture.Send(windows, head);
            yield return 1f;
            Check(said == null && gesture.IsMinimized && !w2.gameObject.activeSelf, "a flick away minimises an OS window into a dot (" + (said ?? "ok") + ")");

            // ---- away from the desk: nothing moves, nothing folds
            workspace.presence.forceSeated = false;
            DesktopRig.Current.ToggleSeat();
            yield return 1f;
            var rig = DesktopRig.Current;
            var body = rig.GetComponent<CharacterController>();
            if (body != null) body.enabled = false;
            Transform frame = workspace.presence.Frame;
            Vector3 deskPoint = frame.position;
            Vector3 away = -Vector3.ProjectOnPlane(frame.forward, Vector3.up).normalized * 2.0f;
            rig.transform.position += away;
            if (body != null) body.enabled = true;
            waited = 0f;
            while (workspace.presence.AtDesk && waited < 6f) { waited += 0.25f; yield return 0.25f; }
            Check(!workspace.presence.AtDesk, "walking away puts the user away from the desk");
            yield return 1f;
            Check(w1 != null && w1.gameObject.activeInHierarchy && windows.IsOpen("zz_one"), "window 1 is still there and active away from the desk");
            Check(w2 != null && gesture != null && gesture.IsMinimized, "the minimised window's dot stays away from the desk");
            Check(workspace.ActiveMode == "cad" && windows.IsOpen(OSWindows.GroupId("cad")), "CAD is still open away from the desk");
            Check(Vector3.Distance(CenterOf(w1.Panel), p1) < 0.01f, "window 1 stayed at the same world position (" + Vector3.Distance(CenterOf(w1.Panel), p1).ToString("F3") + " m)");
            bool modeStayed = true, modeActive = true;
            for (int i = 0; i < mode.Length; i++)
            {
                if (mode[i] == null) { modeStayed = modeActive = false; continue; }
                if (!mode[i].gameObject.activeInHierarchy) modeActive = false;
                if (Vector3.Distance(CenterOf(mode[i]), modePositions[i]) > 0.01f) modeStayed = false;
            }
            Check(modeActive, "CAD's panels are all still active away from the desk");
            Check(modeStayed, "CAD's panels stayed at the same world positions");
            if (cadViewer != null) Check(cadViewer != null && Vector3.Distance(cadViewer.position, viewerAt) < 0.01f, "CAD's volume stayed too");
            Check(!workspace.dock.IsShown && (table == null || !table.Shown), "away: no dock and no table button");
            DesktopRig.Current.LookAt(deskPoint + Vector3.up * 0.6f);
            yield return 0.3f;
            Shot("12_away_same_place");

            // ---- bring everything here (the minimised window comes back first, as the menu would do)
            windows.Open("zz_two", "Second window", new Vector2(0.5f, 0.34f), null);
            yield return 0.8f;
            Check(w2 != null && w2.gameObject.activeSelf && !gesture.IsMinimized, "opening a window that is a dot brings it back");
            // face away from the old desk so the old places are behind the user
            rig.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(-(deskPoint - rig.transform.position), Vector3.up), Vector3.up);
            yield return 0.3f;
            windows.BringHere();
            yield return 2f;
            head = shell.Head;
            Vector3 c1 = CenterOf(w1.Panel), c2 = CenterOf(w2.Panel);
            ForgePanel[] modeNow = ModePanels(workspace);
            bool inReach = Distance(head, c1) < 1.3f && Distance(head, c2) < 1.3f;
            bool inView = AngleFromView(head, c1) < 100f && AngleFromView(head, c2) < 100f;
            float farthest = Mathf.Max(Distance(head, c1), Distance(head, c2));
            float widest = Mathf.Max(AngleFromView(head, c1), AngleFromView(head, c2));
            for (int i = 0; i < modeNow.Length; i++)
            {
                Vector3 c = CenterOf(modeNow[i]);
                inReach &= Distance(head, c) < 1.3f;
                inView &= AngleFromView(head, c) < 100f;
                farthest = Mathf.Max(farthest, Distance(head, c));
                widest = Mathf.Max(widest, AngleFromView(head, c));
            }
            Measure($"after Bring here: farthest {farthest:F2} m, widest {widest:F0} deg from where the user faces (mode panels: {modeNow.Length})");
            Check(inReach, "after Bring here every window and CAD panel is within 1.3 m of the head");
            Check(inView, "after Bring here every window and CAD panel is in view (within 100 degrees of where the user faces)");
            Check(Vector3.Distance(c1, p1) > 1f, "window 1 left its old place (" + Vector3.Distance(c1, p1).ToString("F1") + " m)");
            Check(Vector3.Distance(deskPoint, c1) > 2f, "window 1 is around the user, not at the old desk");
            AssertNoOverlap("after Bring here", head, w1, w2, modeNow);
            Check(w1.Panel.Handles != null && !w1.Panel.Handles.Busy, "the windows aren't Busy once they have landed");
            Measure("the room is known at the user's place (walls are fitted against): " + RoomFit.Begin(head, Vector3.zero));
            Vector3 centroid = c1 + c2;
            for (int i = 0; i < modeNow.Length; i++) centroid += CenterOf(modeNow[i]);
            centroid /= 2 + modeNow.Length;
            DesktopRig.Current.LookAt(centroid);
            yield return 0.3f;
            Shot("13_after_bring_here");

            // ---- open another mode while away
            Check(OSModes.Open("ml"), "OSModes.Open(ml) works away from the desk");
            yield return 2.5f;
            Check(workspace.ActiveMode == "ml" && !OSModes.IsOpen("cad") && windows.IsOpen(OSWindows.GroupId("ml")) && !windows.IsOpen(OSWindows.GroupId("cad")), "ML replaced CAD as the one open mode and as the group entry");
            ForgePanel[] ml = ModePanels(workspace);
            bool mlNear = ml.Length > 0, mlFront = ml.Length > 0;
            float mlFar = 0f;
            for (int i = 0; i < ml.Length; i++)
            {
                Vector3 c = CenterOf(ml[i]);
                mlNear &= Distance(head, c) < 1.6f && Vector3.Distance(c, deskPoint) > 2f;
                mlFront &= AngleFromView(head, c) < 110f;
                mlFar = Mathf.Max(mlFar, Distance(head, c));
            }
            Measure($"ML away: {ml.Length} panels, farthest {mlFar:F2} m from the head");
            Check(mlNear, "ML's panels are around the user and not at the old desk");
            Check(mlFront, "ML's panels are in front of the user");
            Shot("14_ml_away");

            // ---- opening another mode when no free frame can be made leaves the open mode exactly as it was
            Workspace.FreeFramePose realPose = workspace.FreeFrame;
            workspace.FreeFrame = (out Vector3 position, out Quaternion rotation) => { position = default; rotation = default; return false; };
            workspace.Open("cad");
            yield return 3f;
            workspace.FreeFrame = realPose;
            Check(workspace.ActiveMode == "ml" && workspace.InFreeFrame, "Open with no free frame to make leaves the old mode active in its frame (" + workspace.ActiveMode + ")");
            Check(ModePanels(workspace).Length == ml.Length, "and its panels are not folded away (" + ModePanels(workspace).Length + " of " + ml.Length + ")");

            // ---- the voice tool path (hub off): ui_open_view works away from a desk
            JObject result = AlbieTools.Call("ui_open_view", new JObject { ["view"] = "messages" });
            Check(result != null && result["error"] == null && workspace.ActiveMode == "messages", "ui_open_view messages returns ok away from the desk and opens it (" + result + ")");
            yield return 1.5f;
            result = AlbieTools.Call("ui_open_view", new JObject { ["view"] = "cad" });
            Check(result != null && result["error"] == null && workspace.ActiveMode == "cad", "ui_open_view cad returns ok away from the desk (" + result + ")");
            yield return 1.5f;
            result = AlbieTools.Call("os_windows", new JObject { ["action"] = "recenter" });
            Check(result != null && result["error"] == null, "os_windows recenter returns ok (" + result + ")");
            yield return 1.5f;

            // ---- the settings' flat layout reaches the workspace
            OSSettings.Current.flatLayout = true;
            OSSettings.Current.Save();
            Check(workspace.layout == WorkspaceLayout.Flat, "the flat layout setting reaches the workspace");
            OSSettings.Current.flatLayout = wasFlat;
            OSSettings.Current.Save();
            Check(workspace.layout == (wasFlat ? WorkspaceLayout.Flat : WorkspaceLayout.Curved), "and back");
            yield return 1f;

            // ---- close everything
            result = AlbieTools.Call("os_windows", new JObject { ["action"] = "close_all" });
            Check(result != null && result["error"] == null, "os_windows close_all returns ok");
            yield return 2.5f;
            Check(windows.Count == 0, "Close all leaves no windows (" + windows.Count + ")");
            Check(workspace.ActiveMode == null, "Close all closes the open mode");
            bool leftovers = false;
            foreach (var p in ForgePanel.All)
                if (p != null && (p.panelId != null && p.panelId.StartsWith("zz_", StringComparison.Ordinal))) leftovers = true;
            Check(!leftovers && w1 == null && w2 == null, "Close all leaves none of the test windows");
            Check(ModePanels(workspace).Length == 0, "Close all leaves none of the mode's panels");

            // ---- back at a desk: a mode opens in the desk frame again, and nothing is left over from the free frame
            workspace.presence.forceSeated = true;
            waited = 0f;
            while (!workspace.presence.AtDesk && waited < 3f) { waited += 0.25f; yield return 0.25f; }
            yield return 0.5f;
            Check(OSModes.Open("code") && !workspace.InFreeFrame, "at the desk again a mode opens in the desk frame");
            yield return 1.5f;
            OSModes.Close();
            yield return 1f;
        }

        // The panels of the open mode (those the workspace says are its items).
        static ForgePanel[] ModePanels(Workspace workspace)
        {
            var list = new List<ForgePanel>();
            foreach (var p in ForgePanel.All)
                if (p != null && workspace.IsOpenItem(p.gameObject)) list.Add(p);
            return list.ToArray();
        }

        static void AssertNoOverlap(string when, Transform head, OSWindow a, OSWindow b, ForgePanel[] mode)
        {
            var panels = new List<ForgePanel> { a.Panel, b.Panel };
            panels.AddRange(mode);
            var gesture = UnityEngine.Object.FindAnyObjectByType<GesturePanel>();
            bool clear = true;
            string first = null;
            for (int i = 0; i < 2; i++)
            {
                Rect ri = Angular(panels[i], head);
                for (int j = 0; j < panels.Count; j++)
                {
                    if (j == i || (j < 2 && j < i)) continue;
                    Rect rj = Angular(panels[j], head);
                    if (ri.Overlaps(rj)) { clear = false; first = first ?? (panels[i].name + " " + ri + " x " + panels[j].name + " " + rj); }
                }
                if (gesture != null && gesture.gameObject.activeInHierarchy)
                {
                    var gp = gesture.GetComponent<ForgePanel>();
                    if (gp != null && ri.Overlaps(Angular(gp, head))) { clear = false; first = first ?? (panels[i].name + " x the gesture display"); }
                }
            }
            Check(clear, "no OS window overlaps another window or a panel of the open mode " + when + (clear ? "" : " (" + first + ")"));
        }
    }
}
