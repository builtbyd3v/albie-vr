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
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Run Drive Test: the real Main scene in Play mode on the desktop rig, voice off, hub off, on a scratch root
    /// (no real notebook, projects, artifacts or approvals are read or written), in the simulated living room. It opens the
    /// RC truck project, Course (with the Allow step), opens the Drive window, and then drives the brain truck only through the
    /// pad: real PointerEventData (a press, drags with the screen position of a point on the pad, a release) sent through
    /// the pad's own handlers, never by writing the manual channels. It checks that the truck moves forward with the knob up
    /// and turns left and right, that letting go writes zero and the truck stops, that the knob springs back, that Stop works
    /// (with the knob still held, on a recording, and on the Autopilot), and then the whole flow: two laps recorded by driving
    /// the pad through the slalom (a pursuit controller in the test turns the next gate into pad positions), Train, Autopilot
    /// completes the slalom. The result is in <see cref="LastResult"/>; <see cref="Level"/> says how far the flow got. A line
    /// "[OSDriveTest] N passed, M failed" goes to the console.
    /// </summary>
    [InitializeOnLoad]
    public static class OSDriveTest
    {
        const string ActiveKey = "OSDriveTest.Active";
        const string ScenePath = "Assets/_Forge/Core/Main.unity";
        const double GiveUpSeconds = 520;

        /// <summary>"running", or the summary of the last run (starts with PASS or FAIL), or null if it never ran.</summary>
        public static string LastResult { get; private set; }
        /// <summary>The measurements of the last run, one per line.</summary>
        public static string Measurements { get; private set; }
        /// <summary>
        /// How far the last run got: "full" (laps recorded by driving the pad, trained on them, the Autopilot completed the
        /// slalom), "pad laps, pretrained brain" (laps recorded through the pad, the Autopilot run on the pretrained brain),
        /// "pad only" (the pad drives and records laps, the Autopilot not shown), or "not reached".
        /// </summary>
        public static string Level { get; private set; }

        static IEnumerator<float> steps;
        static double wakeAt, startedAt;
        static int passed, failed;
        static readonly List<string> failures = new List<string>();
        static readonly List<string> measured = new List<string>();
        static readonly List<string> logged = new List<string>();
        static string scratch;
        static float timeScaleWas = 1f;

        static OSDriveTest()
        {
            if (SessionState.GetBool(ActiveKey, false)) EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static bool keepPlaying
        {
            get => SessionState.GetBool("OSDriveTest.Keep", false);
            set => SessionState.SetBool("OSDriveTest.Keep", value);
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

        [MenuItem("Forge/OS/Run Drive Test")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSDriveTest] Leave Play mode first."); return; }
            OSTestEnv.Enter("OSDriveTest");
            SessionState.SetBool(ActiveKey, true);
            LastResult = "running";
            Level = "not reached";
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
            timeScaleWas = Time.timeScale;
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
            Measure("level reached: " + Level);
            Measurements = string.Join("\n", measured);
            if (!keepPlaying)
            {
                string envDiff = OSTestEnv.Leave();
                Check(envDiff.Length == 0, "the user's own preferences and files are exactly as they were before the run" + (envDiff.Length == 0 ? "" : ": " + envDiff.Replace("\n", " | ")));
            }
            string summary = $"[OSDriveTest] {passed} passed, {failed} failed (level: {Level})";
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
            if (pad != null && padDown) PadUp();
            RestoreRoomColliders();
            ManualInputs.Clear();
            Time.timeScale = timeScaleWas;
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
                catch (Exception e) { Debug.Log("[OSDriveTest] couldn't remove " + scratch + ": " + e.Message); }
                scratch = null;
            }
        }

        static void Check(bool condition, string what)
        {
            Debug.Log($"[OSDriveTest] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition) { passed++; return; }
            failed++;
            failures.Add(what);
        }

        static void Measure(string line)
        {
            measured.Add(line);
            Debug.Log("[OSDriveTest] measured: " + line);
        }

        static void Shot(string name)
        {
            string path = OSShot.Take(name);
            Debug.Log("[OSDriveTest] shot " + (path ?? "(no camera)"));
        }

        // ---------------------------------------------------------------- the pad, by real pointer events

        static OSDrivePad pad;
        static PointerEventData ped;
        static GameObject pressedObject, dragObject;
        static bool padDown;
        static string padWhy;

        // The point of the pad at a knob position n (-1 to 1 on each axis), as a screen position of the camera the canvas events use.
        static bool ScreenOf(Vector2 n, out Vector2 screen, out Camera cam)
        {
            screen = default;
            cam = null;
            var rect = (RectTransform)pad.transform;
            Vector3 world = rect.TransformPoint(rect.rect.center + n * pad.Travel);
            cam = PetUser.HeadCamera;
            if (cam == null) return false;
            Vector3 s = cam.WorldToScreenPoint(world);
            screen = s;
            return s.z > 0f;
        }

        static bool Hit(Vector2 screen, out RaycastResult hit)
        {
            hit = default;
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = screen }, hits);
            if (hits.Count == 0) return false;
            hit = hits[0];
            return true;
        }

        // Presses the pad at a knob position, the way the DesktopRig mouse (and the Interaction SDK's pointer module) does:
        // pointer down, then begin drag. The top hit must be the pad itself, or it fails with a reason.
        static bool PadPress(Vector2 n)
        {
            padWhy = null;
            if (pad == null) { padWhy = "no pad"; return false; }
            if (!ScreenOf(n, out Vector2 screen, out _)) { padWhy = "the pad is behind the camera"; return false; }
            if (!Hit(screen, out RaycastResult hit)) { padWhy = "nothing under the pointer at " + screen; return false; }
            if (hit.gameObject != pad.gameObject) { padWhy = "'" + hit.gameObject.name + "' is on top of the pad"; return false; }
            ped = new PointerEventData(EventSystem.current) { position = screen, pressPosition = screen, button = PointerEventData.InputButton.Left };
            ped.pointerCurrentRaycast = hit;
            ped.pointerPressRaycast = hit;
            ExecuteEvents.ExecuteHierarchy(hit.gameObject, ped, ExecuteEvents.pointerEnterHandler);
            pressedObject = ExecuteEvents.ExecuteHierarchy(hit.gameObject, ped, ExecuteEvents.pointerDownHandler);
            ped.pointerPress = pressedObject;
            ped.rawPointerPress = hit.gameObject;
            ped.eligibleForClick = true;
            dragObject = ExecuteEvents.GetEventHandler<IDragHandler>(hit.gameObject);
            ped.pointerDrag = dragObject;
            if (dragObject != null)
            {
                ExecuteEvents.Execute(dragObject, ped, ExecuteEvents.initializePotentialDrag);
                ExecuteEvents.Execute(dragObject, ped, ExecuteEvents.beginDragHandler);
                ped.dragging = true;
            }
            padDown = true;
            return true;
        }

        // Drags to a knob position: a new screen position, and the drag event.
        static void PadMove(Vector2 n)
        {
            if (!padDown || !ScreenOf(n, out Vector2 screen, out _)) return;
            ped.delta = screen - ped.position;
            ped.position = screen;
            if (Hit(screen, out RaycastResult hit)) ped.pointerCurrentRaycast = hit;
            if (dragObject != null) ExecuteEvents.Execute(dragObject, ped, ExecuteEvents.dragHandler);
        }

        static void PadUp()
        {
            if (!padDown) return;
            padDown = false;
            if (pressedObject != null) ExecuteEvents.Execute(pressedObject, ped, ExecuteEvents.pointerUpHandler);
            if (ped.dragging && dragObject != null) ExecuteEvents.Execute(dragObject, ped, ExecuteEvents.endDragHandler);
            ped.dragging = false;
            pressedObject = dragObject = null;
        }

        static float Chan(string name) => ManualInputs.Values[ManualInputs.IndexOf(name)];

        // ---------------------------------------------------------------- the truck

        // The pursuit: the next gate as pad positions. The same weave and look-ahead the scripted driver uses on a room slalom.
        // Internal so the play test (OSPlayTest step 6) drives the same pursuit through the pad.
        internal sealed class Pursuit
        {
            readonly MockCourse course = new MockCourse { Name = "open slalom" };
            readonly ArtifactHost host;
            readonly SlalomBrain brain;

            public Pursuit(ArtifactHost host, SlalomBrain brain)
            {
                this.host = host;
                this.brain = brain;
                var slots = host.Build.Slots;
                if (slots.TryGetValue("course.cones", out var cones)) foreach (var c in cones) course.AddCone(c.position.z, -c.position.x);
                if (slots.TryGetValue("course.gates", out var gates)) foreach (var g in gates) course.AddGate(g.position.z, -g.position.x);
                if (slots.TryGetValue("course.finish", out var finish) && finish.Count > 0) course.SetFinish(finish[0].position.z, -finish[0].position.x);
            }

            public int Gates => course.Gates;

            // Where the knob goes now: x from the steering a driver would command, y at full push (the pad's own limit is the walking pace).
            public Vector2 Knob()
            {
                var origin = host.SceneOrigin;
                var toCourse = Quaternion.Inverse(origin.rotation);
                Vector3 local = toCourse * (brain.transform.position - origin.position);
                Vector3 forward = toCourse * brain.transform.forward;
                float x = local.z, y = -local.x;
                float heading = Mathf.Atan2(-forward.x, forward.z);

                float tx = x + MockDriver.RoomLookahead, ty = course.PathY(tx, MockDriver.RoomWeave);
                float alpha = Mathf.Atan2(ty - y, tx - x) - heading;
                float distance = Mathf.Sqrt((tx - x) * (tx - x) + (ty - y) * (ty - y));
                float maxSteer = MockTruckSim.MaxSteerDeg * Mathf.Deg2Rad;
                float steer = Mathf.Clamp(Mathf.Atan(2f * MockTruckSim.Wheelbase * Mathf.Sin(alpha) / Mathf.Max(distance, 0.3f)) / maxSteer, -1f, 1f);
                // truck_manual: stick.x steers through a dead zone of 0.1 and a curve of 1.3, negated (right is positive).
                float knobX = 0f;
                if (Mathf.Abs(steer) > 0.002f) knobX = -Mathf.Sign(steer) * Mathf.Min(1f, 0.1f + 0.9f * Mathf.Pow(Mathf.Abs(steer), 1f / 1.3f));
                return new Vector2(knobX, 1f);
            }
        }

        static string Lap(SlalomBrain brain) => brain.State + " laps " + brain.CompletedLaps + " target " + brain.LapTarget;

        // The desktop rig's simulated room has wall and furniture colliders (MRUK effect meshes) where the mock living room's strip of
        // floor is, so a truck put down on the slalom's start is already touching them and crashes at once. Their colliders are
        // switched off while the pad drives, and back on after (the course's own cones stay: they are what the truck must avoid).
        static readonly List<Collider> switchedOff = new List<Collider>();

        static void SwitchOffRoomColliders()
        {
            switchedOff.Clear();
            foreach (Collider c in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!c.enabled || !c.name.EndsWith("_EffectMesh", StringComparison.Ordinal)) continue;
                c.enabled = false;
                switchedOff.Add(c);
            }
            Measure("the simulated room's colliders switched off for the driving: " + switchedOff.Count);
        }

        static void RestoreRoomColliders()
        {
            foreach (Collider c in switchedOff) if (c != null) c.enabled = true;
            switchedOff.Clear();
        }

        // The colliders inside the truck's body sphere (what ExampleTruckBody calls a crash), by name.
        static string Touching(SlalomBrain brain)
        {
            var found = Physics.OverlapSphere(brain.transform.position + brain.transform.up * brain.Body.sensorHeight, MockTruckSim.TruckRadius, ~0, QueryTriggerInteraction.Ignore);
            string names = "";
            foreach (var c in found) names += c.name + " (" + (c.transform.parent != null ? c.transform.parent.name : "root") + "), ";
            return found.Length == 0 ? "nothing" : names;
        }

        // What the truck's controller sees, for a failure message.
        static string Diag(SlalomBrain brain)
        {
            var host = brain.Thing.Host;
            return "controller " + (host.File != null ? host.File.Name + "/" + host.File.Kind : "none") + ", fault " + (host.Fault ?? "none") + ", crashed " + brain.Body.Crashed
                + ", ticking " + brain.Thing.tickInFixedUpdate + ", frames recorded " + brain.Thing.Recorder.Frames + ", actuators " + string.Join(",", host.Commanded) + ", speed " + brain.Body.Speed.ToString("F2")
                + ", stick.y " + Chan("stick.y").ToString("F3") + ", fixed time " + Time.fixedTime.ToString("F2") + ", touching " + Touching(brain);
        }

        // ---------------------------------------------------------------- helpers

        static UnityEngine.UI.Button FindButton(OSWindow window, string label)
        {
            Transform t = OSToolsTest.FindDeep(window.transform, "Button " + label);
            return t != null ? t.GetComponent<UnityEngine.UI.Button>() : null;
        }

        static OSProjectWindow ProjectView(OSShell shell)
        {
            OSWindow w = shell.Windows.Find(OSProjectWindow.WindowId);
            return w != null ? w.GetComponent<OSProjectWindow>() : null;
        }

        static OSDriveWindow DriveView(OSShell shell)
        {
            OSWindow w = shell.Windows.Find(OSDriveWindow.WindowId);
            return w != null ? w.GetComponent<OSDriveWindow>() : null;
        }

        static void LookAtPad(OSDrivePad p) => DesktopRig.Current.LookAt(OSToolsTest.CenterOf(p.transform));

        // ---------------------------------------------------------------- the script

        static IEnumerator<float> Script()
        {
            // Everything the run could write (settings, notebook, artifacts, approvals) goes to a folder of its own first; the tests that need their own root move on from here.
            OSTestEnv.UseScratch("osdrive");
            string why = null;
            pad = null;
            padDown = false;
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

            // ---- a fresh install: scratch storage, the truck project open
            scratch = Path.Combine(Path.GetTempPath(), "os_drive_test");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
            Directory.CreateDirectory(scratch);
            ArtifactLibrary.RootOverride = Path.Combine(scratch, "artifacts");
            ArtifactApprovals.FileOverride = Path.Combine(scratch, "artifacts", "approvals.json");
            NotebookStore.Open(NotebookStore.DefaultProject, scratch);
            OSProjects.Register();
            OSProjects.Refresh();
            ManualInputs.Clear();

            // ---- the Tools tab: seven tiles, Drive after Gestures, a true status line
            var tools = new List<OSEntry>();
            OSRegistry.Fill(OSTab.Tools, tools);
            string order = "";
            foreach (var e in tools) order += e.id + ";";
            Check(order == "cad;code;train;notebook;messages;gestures;drive;", "the Tools tab lists seven tools, Drive after Gestures (" + order + ")");
            OSEntry driveEntry = OSRegistry.Find(OSTab.Tools, "drive");
            Check(driveEntry != null && driveEntry.title == "Drive" && OSUi.Icon(driveEntry.icon) != null && driveEntry.open != null && driveEntry.status != null && driveEntry.isOpen != null,
                "the Drive entry has a title, a kit icon, a status line, an open flag and an action");
            string tileLine = driveEntry != null ? driveEntry.status() : "";
            Measure("Drive tile status with no truck: '" + tileLine + "'");
            Check(tileLine == "no truck yet", "with no truck the tile says so (" + tileLine + ")");

            shell.OpenMenu(OSSummon.Key);
            yield return 0.9f;
            shell.SetTab(OSTab.Tools);
            yield return 0.5f;
            DesktopRig.Current.LookAt(menu.LastCenter);
            yield return 0.4f;
            string tileIds = "";
            for (int i = 0; i < menu.ActiveTiles.Count; i++) tileIds += (menu.ActiveTiles[i].entry != null ? menu.ActiveTiles[i].entry.id : "?") + ",";
            Check(shell.MenuOpen && menu.ActiveTiles.Count == 7, "the menu shows seven tiles on the Tools tab (" + menu.ActiveTiles.Count + ": " + tileIds + ")");
            OSTileView driveTile = null;
            for (int i = 0; i < menu.ActiveTiles.Count; i++)
                if (menu.ActiveTiles[i].entry != null && menu.ActiveTiles[i].entry.id == "drive") driveTile = menu.ActiveTiles[i];
            Check(driveTile != null && driveTile.title != null && driveTile.title.text == "Drive", "one of them is Drive");
            Shot("70_tools_seven_tiles");
            Check(driveTile != null && OSToolsTest.Press(driveTile.gameObject, out why), "a pointer reaches the Drive tile: " + why);
            yield return 1.0f;

            // ---- the window with no truck: honest, with the pad and a big Stop
            OSWindow win = windows.Find(OSDriveWindow.WindowId);
            OSDriveWindow drive = DriveView(shell);
            Check(win != null && drive != null, "the Drive tile opened the Drive window");
            if (win == null || drive == null) yield break;
            pad = drive.Pad;
            Check(pad != null, "the window has the pad");
            Check(driveEntry.isOpen(), "the tile now shows Drive as open");
            Check(drive.ThingLine.StartsWith("No truck to drive yet.", StringComparison.Ordinal), "with no truck the window says so in words (" + drive.ThingLine + ")");
            Check(drive.StateLine == "Idle", "the state line says Idle (" + drive.StateLine + ")");
            var padRect = (RectTransform)pad.transform;
            float padMm = OSToolsTest.Mm(padRect.rect.width, padRect);
            var stopRect = drive.StopButton != null ? (RectTransform)drive.StopButton.transform : null;
            float stopW = stopRect != null ? OSToolsTest.Mm(stopRect.rect.width, stopRect) : 0f, stopH = stopRect != null ? OSToolsTest.Mm(stopRect.rect.height, stopRect) : 0f;
            Measure($"pad {padMm:F0} x {OSToolsTest.Mm(padRect.rect.height, padRect):F0} mm, Stop {stopW:F0} x {stopH:F0} mm");
            Check(padMm >= 120f && OSToolsTest.Mm(padRect.rect.height, padRect) >= 120f, "the pad is at least 120 mm square (" + padMm.ToString("F0") + ")");
            Check(stopW >= 44f && stopH >= 44f, "Stop is at least 44 x 44 mm (" + stopW.ToString("F0") + " x " + stopH.ToString("F0") + ")");
            string text = OSToolsTest.AllText(win);
            Check(text.Contains("Stop") && text.Contains("Drive") && !text.Contains("TMP") && !text.Contains("New Text"), "the window shows Stop and no default text");
            string problem = OSToolsTest.LayoutProblem(win);
            Check(problem == null, "the window text is inside it and doesn't overlap" + (problem ?? ""));
            shell.CloseMenu();
            yield return 0.6f;
            LookAtPad(pad);
            yield return 0.6f;
            Shot("71_drive_no_truck");

            // The pad with no truck: it still takes the pointer and writes the two channels; letting go zeroes them. Nothing moves.
            Check(PadPress(new Vector2(0.5f, 0.5f)), "a press reaches the pad with no truck: " + padWhy);
            yield return 0.2f;
            Check(Mathf.Abs(Chan("stick.x") - 0.5f) < 0.06f && Mathf.Abs(Chan("stick.y") - 0.5f) < 0.06f, "the pad writes stick.x and stick.y (" + Chan("stick.x").ToString("F2") + ", " + Chan("stick.y").ToString("F2") + ")");
            PadUp();
            Check(Chan("stick.x") == 0f && Chan("stick.y") == 0f, "letting go writes zero at once");

            // ---- the truck project, Course (the Allow step first), the brain truck
            OSProjects.OpenProject(NotebookStore.DefaultProject);
            yield return 1.2f;
            OSProjectWindow project = ProjectView(shell);
            Check(project != null && project.ProjectId == NotebookStore.DefaultProject, "the RC truck project window is open");
            if (project == null) yield break;
            ArtifactHost host = ArtifactHost.Current ?? ArtifactBootstrap.Ensure();
            // The simulated living room of the desktop rig has no clear strip of floor for a slalom: lay the course out in the mock room.
            if (host != null) host.Room = MockFloorPlan.LivingRoom();
            Check(BrainLoops.Active == null, "no brain truck before Course");
            string asked = project.Do("course");
            Check(asked.StartsWith("The slalom asks first.", StringComparison.Ordinal), "Course asks for permission first (" + asked + ")");
            if (host != null && host.OpenManifest != null) ArtifactApprovals.Approve(host.OpenManifest);  // Allow
            AlbieTools.Call("artifact_close", new JObject());
            yield return 0.5f;
            project.Do("course");
            float waited = 0f;
            while (BrainLoops.Active == null && waited < 8f) { waited += 0.5f; yield return 0.5f; }
            yield return 0.8f;
            var brain = BrainLoops.Active as SlalomBrain;
            Check(brain != null, "Course gave the brain truck");
            if (brain == null) yield break;
            string thing = host.OpenManifest != null ? (string)host.OpenManifest["links"]?["thing"] : null;
            Check(thing == "truck", "the slalom is linked to the truck (" + thing + ")");
            Check(drive.ThingLine == "Drives: the truck (the brain truck on the slalom)", "the Drive window says which Thing it drives (" + drive.ThingLine + ")");
            Check(drive.StateLine == "Idle", "the state line says Idle with the truck at the start (" + drive.StateLine + ")");
            Check(Mathf.Abs(pad.ThrottleScale - OSDriveWindow.HandThrottle) < 1e-4f, "the pad holds the hand speed on the brain truck (" + pad.ThrottleScale.ToString("F3") + ")");
            Check(driveEntry.status() == "truck ready", "the tile says the truck is ready (" + driveEntry.status() + ")");
            SwitchOffRoomColliders();
            brain.PlaceAtStart();
            var pursuit = new Pursuit(host, brain);
            Check(pursuit.Gates >= 3, "the pursuit sees the slalom's gates (" + pursuit.Gates + ")");
            LookAtPad(pad);
            yield return 0.6f;
            Shot("72_drive_idle_truck");

            // ---- the Drive action of the truck project: opens Drive and starts a two-lap recording
            project = ProjectView(shell);
            OSWindow projectWindow = windows.Find(OSProjectWindow.WindowId);
            UnityEngine.UI.Button driveButton = FindButton(projectWindow, "Drive");
            Check(driveButton != null && driveButton.interactable, "the project window's Drive action is on now that the tool exists");
            DesktopRig.Current.LookAt(OSToolsTest.CenterOf(projectWindow.Panel.transform));
            yield return 0.5f;
            Check(driveButton != null && OSToolsTest.Press(driveButton.gameObject, out why), "a pointer reaches the Drive action: " + why);
            yield return 0.8f;
            Check(brain.State == SlalomBrain.Mode.Lap && brain.LapTarget == 2, "Drive started a two-lap recording (" + Lap(brain) + ")");
            Check(project.StatusLine.StartsWith("Recording 2 laps.", StringComparison.Ordinal), "the project window says so (" + project.StatusLine + ")");
            Check(windows.IsOpen(OSDriveWindow.WindowId) && windows.Count >= 2, "the Drive window is still the one open, beside the project window");
            yield return 0.4f;
            Check(drive.StateLine == "Recording lap 1 of 2", "the Drive window's state line says 'Recording lap 1 of 2' (" + drive.StateLine + ")");
            Check(driveEntry.status() == "recording a lap", "and the tile says it too (" + driveEntry.status() + ")");

            // ---- the pad drives the truck: knob up, through the real pointer path
            LookAtPad(pad);
            yield return 0.6f;
            brain.PlaceAtStart();
            Vector3 start = brain.transform.position, startForward = brain.transform.forward;
            Check(PadPress(new Vector2(0f, 1f)), "a press at the top of the pad reaches it: " + padWhy);
            yield return 0.25f;
            float expected = OSDriveWindow.HandThrottle;
            Check(pad.Held && Mathf.Abs(Chan("stick.y") - expected) < 0.01f && Mathf.Abs(Chan("stick.x")) < 0.06f,
                "knob up: stick.y is the hand throttle and stick.x about zero (" + Chan("stick.y").ToString("F3") + ", " + Chan("stick.x").ToString("F3") + ")");
            Check(pad.Knob.anchoredPosition.y > pad.Travel * 0.85f && Mathf.Abs(pad.Knob.anchoredPosition.x) < pad.Travel * 0.1f, "the knob is drawn at the top of the pad");
            float until = Time.time + 1.0f;
            while (Time.time < until) yield return 0f;
            Vector3 moved = brain.transform.position - start;
            float along = Vector3.Dot(moved, startForward);
            Measure($"knob up for 1 s: moved {moved.magnitude:F2} m, {along:F2} m forward, speed {brain.Body.Speed:F2} m/s; " + Diag(brain));
            Check(along > 0.1f && brain.Body.Speed > 0.2f && Vector3.Angle(moved, startForward) < 15f, "knob up: the truck moves forward (" + along.ToString("F2") + " m, " + brain.Body.Speed.ToString("F2") + " m/s)");
            Check(Mathf.Abs(Vector3.SignedAngle(startForward, brain.transform.forward, Vector3.up)) < 4f, "knob straight up: the truck goes straight");
            Check(brain.State == SlalomBrain.Mode.Lap, "the lap is still being recorded (" + Lap(brain) + ")");

            PadUp();
            Check(Chan("stick.x") == 0f && Chan("stick.y") == 0f, "letting go writes zero to stick.x and stick.y at once");
            float peak = brain.Body.Speed;
            yield return 0.15f;
            Check(Chan("stick.x") == 0f && Chan("stick.y") == 0f && !pad.Held, "the pad stays at zero after the release");
            float stopBy = Time.time + 6f;
            while (Time.time < stopBy && Mathf.Abs(brain.Body.Speed) > 0.05f && brain.State == SlalomBrain.Mode.Lap) yield return 0f;
            Measure($"after the release the speed fell from {peak:F2} to {brain.Body.Speed:F2} m/s in {6f - (stopBy - Time.time):F1} s");
            Check(Mathf.Abs(brain.Body.Speed) <= 0.05f, "released: the truck stops (" + brain.Body.Speed.ToString("F3") + " m/s)");

            // ---- the knob springs back
            Check(PadPress(new Vector2(0.8f, 0.7f)), "the pad takes a second press: " + padWhy);
            yield return 0.2f;
            Check(Mathf.Abs(pad.Shown.x - 0.8f) < 0.07f && Mathf.Abs(pad.Shown.y - 0.7f) < 0.07f, "the knob follows the pointer (" + pad.Shown.ToString("F2") + ")");
            PadUp();
            float released = Time.unscaledTime;
            while (pad.Shown != Vector2.zero && Time.unscaledTime - released < 1f) yield return 0f;
            float springed = Time.unscaledTime - released;
            Measure($"the knob took {springed:F2} s to spring back from (0.8, 0.7)");
            Check(pad.Shown == Vector2.zero && pad.Knob.anchoredPosition == Vector2.zero, "the knob springs back to the middle");
            Check(springed < 0.35f, "and does it in about 0.12 s (" + springed.ToString("F2") + " s at this frame rate)");

            // ---- steering: knob up and left, then up and right
            LookAtPad(pad);
            yield return 0.3f;
            Vector3 f0 = brain.transform.forward;
            Check(PadPress(new Vector2(-0.9f, 0.8f)), "a press up and left reaches the pad: " + padWhy);
            until = Time.time + 0.8f;
            while (Time.time < until) yield return 0f;
            float left = Vector3.SignedAngle(f0, brain.transform.forward, Vector3.up);
            PadUp();
            Measure($"knob up-left for 0.8 s: heading changed {left:F0} degrees (negative is left), stick.x was set from the pad");
            Check(left < -8f, "knob left: the truck turns left (" + left.ToString("F0") + " degrees)");
            yield return 0.3f;
            Vector3 f1 = brain.transform.forward;
            Check(PadPress(new Vector2(0.9f, 0.8f)), "a press up and right reaches the pad: " + padWhy);
            until = Time.time + 1.0f;
            while (Time.time < until) yield return 0f;
            float right = Vector3.SignedAngle(f1, brain.transform.forward, Vector3.up);
            PadUp();
            Measure($"knob up-right for 1.0 s: heading changed {right:F0} degrees");
            Check(right > 8f, "knob right: the truck turns right (" + right.ToString("F0") + " degrees)");
            Check(Chan("stick.x") == 0f && Chan("stick.y") == 0f, "after both turns the channels are at zero");

            // ---- the picture while driving: knob off centre, the truck has moved
            if (brain.State != SlalomBrain.Mode.Lap) { brain.StopRecording(); brain.Record(2, false); yield return 0.3f; }
            LookAtPad(pad);
            yield return 0.4f;
            Check(PadPress(new Vector2(-0.55f, 0.85f)), "a press for the picture: " + padWhy);
            yield return 0.6f;
            Check(drive.StateLine.StartsWith("Recording lap", StringComparison.Ordinal), "the state line says a lap is being recorded (" + drive.StateLine + ")");
            Shot("73_drive_driving");
            DesktopRig.Current.LookAt(brain.transform.position + Vector3.up * 0.05f);
            yield return 0.5f;
            Shot("74_drive_truck_moved");
            Measure($"truck {(brain.transform.position - start).magnitude:F2} m from where the pad run began");
            LookAtPad(pad);
            yield return 0.4f;

            // ---- Stop while the knob is still held: stops the recording, zeroes the inputs, parks the truck
            Check(brain.State == SlalomBrain.Mode.Lap, "still recording when Stop is pressed (" + Lap(brain) + ")");
            UnityEngine.UI.Button stopButton = drive.StopButton;
            Check(stopButton != null && OSToolsTest.Press(stopButton.gameObject, out why), "a pointer reaches Stop: " + why);
            yield return 0.2f;
            Check(brain.State == SlalomBrain.Mode.Idle, "Stop ended the recording (" + Lap(brain) + ")");
            Check(!pad.Held && Chan("stick.x") == 0f && Chan("stick.y") == 0f, "Stop zeroed the inputs and let go of the pad though the pointer is still down");
            Vector3 parked = brain.transform.position;
            PadMove(new Vector2(-0.2f, 1f));
            yield return 0.4f;
            Check(Chan("stick.y") == 0f && Chan("stick.x") == 0f, "a pointer still dragging after Stop writes nothing");
            Check((brain.transform.position - parked).magnitude < 0.01f, "the truck stays where Stop left it (" + (brain.transform.position - parked).magnitude.ToString("F3") + " m)");
            PadUp();
            yield return 0.4f;
            Check(drive.StateLine.StartsWith("Idle", StringComparison.Ordinal), "the state line says Idle after Stop (" + drive.StateLine + ")");
            Check(driveEntry.status() == "truck ready", "and so does the tile (" + driveEntry.status() + ")");
            Shot("75_drive_after_stop");

            // ---- the whole flow: laps recorded by driving the pad through the slalom
            int lapsBefore = brain.CompletedLaps;
            DesktopRig.Current.LookAt(OSToolsTest.CenterOf(projectWindow.Panel.transform));
            yield return 0.5f;
            projectWindow = windows.Find(OSProjectWindow.WindowId);
            driveButton = FindButton(projectWindow, "Drive");
            Check(driveButton != null && OSToolsTest.Press(driveButton.gameObject, out why), "Drive starts the recording again: " + why);
            yield return 0.8f;
            Check(brain.State == SlalomBrain.Mode.Lap && brain.LapTarget == lapsBefore + 2, "two laps to record (" + Lap(brain) + ")");
            LookAtPad(pad);
            yield return 0.6f;

            bool holding = false;
            float deadline = Time.time + 150f;
            float lastLookAt = 0f;
            int shownLap = 0;
            while (brain.State == SlalomBrain.Mode.Lap && Time.time < deadline)
            {
                Vector2 knob = pursuit.Knob();
                if (!holding)
                {
                    if (!PadPress(knob)) { Check(false, "the pad could not be pressed during the laps: " + padWhy); break; }
                    holding = true;
                }
                else PadMove(knob);
                if (Time.time - lastLookAt > 1.5f) { LookAtPad(pad); lastLookAt = Time.time; }
                if (brain.CompletedLaps + 1 != shownLap && brain.State == SlalomBrain.Mode.Lap)
                {
                    shownLap = brain.CompletedLaps + 1;
                    Debug.Log("[OSDriveTest] pursuit: " + drive.StateLine + " (" + Lap(brain) + ")");
                }
                yield return 0f;
            }
            if (holding) PadUp();
            Check(Chan("stick.x") == 0f && Chan("stick.y") == 0f, "the laps over, the pad let go and the channels are zero");
            int pads = brain.CompletedLaps - lapsBefore;
            Measure($"pad laps: {pads} completed, state {brain.State}, attempts so far " + brain.Attempts.Count);
            bool lapsOk = pads >= 2 && brain.State == SlalomBrain.Mode.Idle;
            Check(lapsOk, "two laps of the slalom were completed by driving the pad (" + pads + " completed, " + Lap(brain) + ")");
            if (lapsOk) Level = "pad only";
            yield return 0.4f;

            var runs = ArtifactRuns.List("slalom");
            string lastRun = runs.Count > 0 ? (string)runs[runs.Count - 1]["outcome"] : null;
            Measure("slalom run records: " + runs.Count + ", the last was " + lastRun);
            Check(runs.Count >= 2 && lastRun == "completed", "the laps are scored runs and the last one completed (" + lastRun + ")");

            if (!lapsOk)
            {
                // Fallback 2 stays: the pad drives and records laps, the Autopilot is not shown. Say so.
                Measure("FALLBACK: the pad laps did not finish two clean laps in time; the Autopilot was not run on them");
                yield break;
            }

            // ---- Train, then Autopilot, from the project window's own buttons
            Check(project.Do("train").StartsWith("Training the brain", StringComparison.Ordinal), "Train starts on the pad laps");
            waited = 0f;
            while (brain.State == SlalomBrain.Mode.Idle && waited < 2f) { waited += 0.1f; yield return 0.1f; }
            until = Time.time + 90f;
            while (brain.State == SlalomBrain.Mode.Training && Time.time < until) yield return 0.25f;
            Check(brain.State == SlalomBrain.Mode.Idle && brain.Cloners != null && brain.Cloners[0].Done, "the brain trained on the pad laps (" + brain.State + ", " + brain.Status + ")");
            Measure("training: " + brain.Status);
            windows.Close("brain");  // the trainer window and the ML desk are not part of this test's pictures
            OSModes.Close();
            yield return 0.8f;

            string auto = project.Do("autopilot");
            Check(auto.StartsWith("The Autopilot is driving", StringComparison.Ordinal), "Autopilot starts (" + auto + ")");
            yield return 0.5f;
            Check(drive.StateLine == "Autopilot driving" || brain.State == SlalomBrain.Mode.Idle, "the Drive window says the Autopilot is driving (" + drive.StateLine + ")");
            until = Time.time + 80f;
            while (brain.State == SlalomBrain.Mode.Autopilot && Time.time < until) yield return 0.25f;
            yield return 0.5f;
            runs = ArtifactRuns.List("slalom");
            lastRun = runs.Count > 0 ? (string)runs[runs.Count - 1]["outcome"] : null;
            Measure("autopilot: drove " + brain.Drove + "; attempts " + string.Join(", ", brain.Attempts) + "; last run " + lastRun);
            bool autoOk = brain.State == SlalomBrain.Mode.Idle && !string.IsNullOrEmpty(brain.Drove) && brain.Drove != "nothing" && lastRun == "completed";
            Check(autoOk, "the Autopilot completed the slalom (" + brain.Drove + ", last run " + lastRun + ")");
            if (autoOk)
            {
                bool fallbackDrove = brain.Drove == "the pretrained fallback";
                Level = fallbackDrove ? "pad laps, pretrained brain" : "full";
                Measure("who drove: " + brain.Drove);
            }
            LookAtPad(pad);
            yield return 0.4f;
            Shot("76_autopilot_done");

            // ---- Stop on the Autopilot
            if (brain.CompletedLaps >= 2 && brain.Cloners != null)
            {
                string again = project.Do("autopilot");
                yield return 1.0f;
                bool driving = brain.State == SlalomBrain.Mode.Autopilot;
                Check(driving && drive.StateLine == "Autopilot driving", "the Autopilot drives again (" + again + ", " + drive.StateLine + ")");
                if (driving)
                {
                    Check(OSToolsTest.Press(drive.StopButton.gameObject, out why), "a pointer reaches Stop while the Autopilot drives: " + why);
                    yield return 0.4f;
                    Check(brain.State == SlalomBrain.Mode.Idle && brain.Attempts.Count > 0 && brain.Attempts[brain.Attempts.Count - 1].EndsWith("stopped", StringComparison.Ordinal), "Stop ended the Autopilot (" + brain.State + ", " + (brain.Attempts.Count > 0 ? brain.Attempts[brain.Attempts.Count - 1] : "") + ")");
                    Vector3 here = brain.transform.position;
                    yield return 0.5f;
                    Check((brain.transform.position - here).magnitude < 0.01f, "the truck is parked after Stop");
                }
            }

            // ---- close
            RestoreRoomColliders();
            windows.CloseAll();
            AlbieTools.Call("artifact_close", new JObject());
            yield return 0.8f;
            Check(Chan("stick.x") == 0f && Chan("stick.y") == 0f, "the channels end at zero");
            Check(!windows.IsOpen(OSDriveWindow.WindowId), "the Drive window closed");
        }
    }
}
