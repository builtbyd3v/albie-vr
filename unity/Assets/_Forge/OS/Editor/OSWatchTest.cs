using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
    /// Forge > OS > Run Watch Test: the real Main scene in Play mode on the desktop rig with voice off, on a scratch settings file
    /// (the user's real one is never read or written). The simulated left hand (the SimulatedHandSource test hook) is put in the
    /// world and turned like a wrist watch: the pose maths on made-up hands (every reason it says no), the button showing after
    /// the 0.3 s hold above the wrist and facing the eyes, a real pointer press opening the menu with the Watch summon, the
    /// debounce, hiding when the wrist turns away, the menu / drag guards, a resting hand and a tilted hand for 10 s with the
    /// engine firing nothing, palm up (0.4 s, under the engine's 0.6 s), Settings off, the hand leaving. The whole block runs once
    /// at the desk and once after walking away. Screenshots go to Logs/os/ (listed in a manifest, cleared by the next run). The
    /// result is in <see cref="LastResult"/> and a console line "[OSWatchTest] N passed, M failed".
    /// </summary>
    [InitializeOnLoad]
    public static class OSWatchTest
    {
        const string ActiveKey = "OSWatchTest.Active";
        const string ScenePath = "Assets/_Forge/Core/Main.unity";
        const double GiveUpSeconds = 300;

        /// <summary>"running", or the summary of the last run (starts with PASS or FAIL), or null if it never ran.</summary>
        public static string LastResult { get; private set; }
        /// <summary>The measurements of the last run, one per line.</summary>
        public static string Measurements { get; private set; }

        // Where the left wrist goes, in the head's own frame (x right, y up, z forward), metres.
        static readonly Vector3 Watch = new Vector3(-0.08f, -0.12f, 0.40f);
        static readonly Vector3 Desk = new Vector3(-0.20f, -0.45f, 0.35f);
        static readonly HandShape PinchShape = new HandShape(0.55f, 0.15f, 0.15f, 0.15f, 0.5f, 0.3f);

        static IEnumerator<float> steps;
        static double wakeAt, startedAt;
        static int passed, failed, shotCount;
        static readonly List<string> failures = new List<string>();
        static readonly List<string> measured = new List<string>();
        static readonly List<string> logged = new List<string>();
        static readonly List<string> leftFires = new List<string>();
        static string scratch;
        static GestureEngine hookedEngine;

        static OSWatchTest()
        {
            if (SessionState.GetBool(ActiveKey, false)) EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static bool keepPlaying
        {
            get => SessionState.GetBool("OSWatchTest.Keep", false);
            set => SessionState.SetBool("OSWatchTest.Keep", value);
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
            EditorPrefs.SetBool(RigSelector.ForceDesktopPref, EditorPrefs.GetBool("OSWatchTest.ForceDesktopWas", false));
            EditorPrefs.SetBool(AlbieVoice.EditorOffPref, EditorPrefs.GetBool("OSWatchTest.VoiceOffWas", false));
        }

        [MenuItem("Forge/OS/Run Watch Test")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSWatchTest] Leave Play mode first."); return; }
            EditorPrefs.SetBool("OSWatchTest.ForceDesktopWas", EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false));
            EditorPrefs.SetBool("OSWatchTest.VoiceOffWas", EditorPrefs.GetBool(AlbieVoice.EditorOffPref, false));
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
            leftFires.Clear();
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
            string summary = $"[OSWatchTest] {passed} passed, {failed} failed";
            LastResult = (failed == 0 ? "PASS " : "FAIL ") + summary + (failed == 0 ? "" : "\n" + string.Join("\n", failures));
            if (failed == 0) Debug.Log(summary);
            else Debug.LogError(summary + "\n" + string.Join("\n", failures));
            SessionState.SetBool(ActiveKey, false);
            if (keepPlaying) return;
            RestorePrefs();
            EditorApplication.isPlaying = false;
        }

        // The left test hand off, the grab counter back, the menu closed, the real settings file back in use, scratch deleted.
        static void Cleanup()
        {
            SimulatedHandSource.LeftTestActive = false;
            SetGrabbing(0);
            if (hookedEngine != null) hookedEngine.Fired -= OnFired;
            hookedEngine = null;
            OSShell shell = OSShell.Current;
            if (shell != null && shell.MenuOpen) shell.CloseMenu();
            OSSettings.SetPathForTests(null);
            ArtifactApprovals.FileOverride = null;
            try { if (scratch != null && Directory.Exists(scratch)) Directory.Delete(scratch, true); }
            catch (IOException) { }
            scratch = null;
        }

        static void Check(bool condition, string what)
        {
            Debug.Log($"[OSWatchTest] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition) { passed++; return; }
            failed++;
            failures.Add(what);
        }

        static void Measure(string line)
        {
            measured.Add(line);
            Debug.Log("[OSWatchTest] measured: " + line);
        }

        static string ManifestPath => Path.Combine(OSShot.Dir, "watch_test_shots.txt");

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
            string path = OSShot.Take($"{100 + ++shotCount:00}_watch_{name}");
            Debug.Log("[OSWatchTest] shot " + (path ?? "(no camera)"));
            if (path != null)
            {
                try { File.AppendAllText(ManifestPath, Path.GetFileName(path) + "\n"); } catch (IOException) { }
            }
            return path;
        }

        // ---------------------------------------------------------------- helpers

        static bool Press(GameObject target, out string why) => OSToolsTest.Press(target, out why);

        static IEnumerable<float> Until(Func<bool> done, float seconds)
        {
            double end = EditorApplication.timeSinceStartup + seconds;
            while (!done() && EditorApplication.timeSinceStartup < end) yield return 0.05f;
        }

        static IEnumerable<float> Wait(float seconds)
        {
            double end = EditorApplication.timeSinceStartup + seconds;
            while (EditorApplication.timeSinceStartup < end) yield return 0.05f;
        }

        static void Teleport(DesktopRig rig, Vector3 delta)
        {
            var body = rig.GetComponent<CharacterController>();
            if (body != null) body.enabled = false;  // a CharacterController undoes a teleport
            rig.transform.position += delta;
            if (body != null) body.enabled = true;
        }

        static void OnFired(GestureDef def, GestureHand hand)
        {
            if (hand != null && hand.sample.isLeft) leftFires.Add(def.id);
        }

        // The world pose of a left wrist at a spot in the head's frame: back of the hand toward the eyes (tilted by tiltDegrees
        // about the finger line), fingers pointing to the right; or palm up with the fingers forward.
        static void Pose(Transform head, Vector3 headLocal, float tiltDegrees, bool palmUp, out Vector3 wrist, out Quaternion rotation)
        {
            wrist = head.TransformPoint(headLocal);
            Vector3 back = (head.position - wrist).normalized;
            Vector3 fingers = Vector3.ProjectOnPlane(head.right, back).normalized;
            rotation = palmUp
                ? Quaternion.LookRotation(head.forward, Vector3.down)
                : Quaternion.LookRotation(fingers, Quaternion.AngleAxis(tiltDegrees, fingers) * back);
        }

        static void PlaceLeft(Vector3 headLocal, HandShape shape, float tiltDegrees = 0f, bool palmUp = false)
        {
            Transform head = OSShell.FindHead();
            Pose(head, headLocal, tiltDegrees, palmUp, out Vector3 wrist, out Quaternion rotation);
            SimulatedHandSource.LeftTestShape = shape;
            SimulatedHandSource.LeftTestWrist = wrist;
            SimulatedHandSource.LeftTestRotation = rotation;
            SimulatedHandSource.LeftTestActive = true;
        }

        // Keeps a resting hand alive with a centimetre of drift, like a hand that is not quite still.
        static void Drift(Vector3 headLocal, HandShape shape, float tiltDegrees, double seconds)
        {
            Transform head = OSShell.FindHead();
            Pose(head, headLocal, tiltDegrees, false, out Vector3 wrist, out Quaternion rotation);
            wrist += head.right * (0.01f * Mathf.Sin((float)seconds * 3f)) + head.up * (0.01f * Mathf.Cos((float)seconds * 2f));
            SimulatedHandSource.LeftTestShape = shape;
            SimulatedHandSource.LeftTestWrist = wrist;
            SimulatedHandSource.LeftTestRotation = rotation;
            SimulatedHandSource.LeftTestActive = true;
        }

        // PanelManipulator.Grabbing has a private setter and a real grab needs a pointer on a panel handle; the test sets the
        // counter the way a grab does (BeginGrab adds one, EndGrab takes it away).
        static readonly FieldInfo GrabField = typeof(PanelManipulator).GetField("<Grabbing>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);

        static void SetGrabbing(int value)
        {
            if (GrabField != null) GrabField.SetValue(null, value);
        }

        static WatchPose Evaluate(OSWatch watch, Transform head, Vector3 headLocal, HandShape shape, float tilt, bool palmUp, out float pinch, out float palmUpFeature)
        {
            Pose(head, headLocal, tilt, palmUp, out Vector3 wrist, out Quaternion rotation);
            var hand = new GestureHand();
            SyntheticHand.Build(hand.sample, shape, wrist, rotation, true);
            hand.Update(Time.time, head.position, head.rotation);
            pinch = hand.values[(int)HandFeature.Pinch];
            palmUpFeature = hand.values[(int)HandFeature.PalmUp];
            return watch.Evaluate(hand, head.position, head.forward, false, out _, out _);
        }

        // ---------------------------------------------------------------- the script

        static IEnumerator<float> Script()
        {
            scratch = Path.Combine(Path.GetTempPath(), "OSWatchTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            OSSettings.SetPathForTests(Path.Combine(scratch, "os_settings.json"));
            ArtifactApprovals.FileOverride = Path.Combine(scratch, "approvals.json");
            SimulatedHandSource.LeftTestActive = false;

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
            var watch = shell.GetComponent<OSWatch>();
            Check(watch != null, "the shell prefab carries the watch turn (OSWatch)");
            if (watch == null) yield break;
            if (shell.Held)
            {
                var greeting = UnityEngine.Object.FindAnyObjectByType<ArrivalGreeting>();
                if (greeting != null) greeting.Fold(true);
                workspace.HoldDock(false);
                yield return 0.5f;
            }
            workspace.Close();
            yield return 1.2f;

            GestureEngine engine = GestureEngine.Current;
            Check(engine != null, "the gesture engine is running");
            if (engine == null) yield break;
            hookedEngine = engine;
            engine.Fired += OnFired;

            // ---- preconditions
            Check(OSSettings.Current.watchTurn, "the scratch settings start with the watch turn on");
            Check(!shell.MenuOpen && !watch.Shown && PanelManipulator.Grabbing == 0, "the menu is closed, the button is hidden, no panel is held");
            Check(!engine.Hands[0].Usable, "the left hand is not tracked until a test places it (the hook defaults to off)");
            Check(!SimulatedHandSource.LeftTestActive, "the left test hand is off");
            Check(watch.ButtonMeters >= 0.022f, $"the button is at least 22 mm ({watch.ButtonMeters * 1000f:F0} mm)");

            // ---- the pose maths on made-up hands
            Transform head = shell.Head;
            Check(head != null, "there is a head");
            if (head == null) yield break;
            WatchPose p = Evaluate(watch, head, Watch, HandShape.Relaxed, 0f, false, out float pinch, out float palmUpValue);
            Measure($"made-up relaxed left hand at the watch spot: {p}, back dot {watch.LastBackDot:F2}, {watch.LastViewDegrees:F0} degrees off the gaze, {watch.LastDistance:F2} m, pinch {pinch:F2}, palm up {palmUpValue:F2}");
            Check(p == WatchPose.Ok, "a left hand with its back to the eyes, in view, at arm's length is the watch pose (" + p + ")");
            Check(watch.LastBackDot > 0.85f, $"its back faces the eyes squarely (dot {watch.LastBackDot:F2})");
            Check(Evaluate(watch, head, Watch, HandShape.Relaxed, 65f, false, out _, out _) == WatchPose.BackAway, "tilted 65 degrees away: BackAway");
            Check(Evaluate(watch, head, new Vector3(-0.50f, -0.12f, 0.40f), HandShape.Relaxed, 0f, false, out _, out _) == WatchPose.OutOfView, "well off to the side: OutOfView");
            Check(Evaluate(watch, head, new Vector3(-0.08f, -0.12f, 0.90f), HandShape.Relaxed, 0f, false, out _, out _) == WatchPose.TooFar, "at 0.9 m: TooFar");
            Check(Evaluate(watch, head, new Vector3(-0.04f, -0.06f, 0.12f), HandShape.Relaxed, 0f, false, out _, out _) == WatchPose.TooClose, "at 0.14 m: TooClose");
            p = Evaluate(watch, head, Desk, HandShape.Relaxed, 0f, false, out _, out _);
            Check(p == WatchPose.TooLow, "a hand resting on the desk, far under the eyes: TooLow (" + p + ")");
            p = Evaluate(watch, head, new Vector3(0f, 0.25f, 0.45f), HandShape.Open, 0f, true, out _, out palmUpValue);
            Check(p == WatchPose.PalmUp, $"palm up (feature {palmUpValue:F2}): PalmUp (" + p + ")");
            var values = new float[HandFeatures.Count];
            values[(int)HandFeature.Pinch] = 0.1f;
            bool pinched = watch.GestureBlocks(values);
            values[(int)HandFeature.Pinch] = 0.5f;
            bool open = watch.GestureBlocks(values);
            values[(int)HandFeature.PalmUp] = 0.8f;
            bool up = watch.GestureBlocks(values);
            Check(pinched && !open && up, $"the gate blocks a pinch and a palm up and lets an open hand through ({pinched}, {open}, {up})");
            Check(Evaluate(watch, head, Watch, HandShape.Fist, 0f, false, out _, out _) != WatchPose.NoHand, "a fist is still a usable hand to the pose maths");

            // ---- the whole block at the desk, then away
            foreach (float w in Block("at the desk", shell, watch, engine, rig, true)) yield return w;

            var presence = workspace.presence;
            presence.forceSeated = false;
            rig.ToggleSeat();
            yield return 1.0f;
            Vector3 away = -Vector3.ProjectOnPlane(presence.Frame.forward, Vector3.up).normalized;
            if (away.sqrMagnitude < 0.5f) away = -rig.transform.forward;
            Teleport(rig, away * 2.0f);
            rig.LookAt(rig.Head.position + away * 2f);
            foreach (float w in Until(() => !presence.AtDesk, 5f)) yield return w;
            Check(!shell.AtDesk, "the user is away from the desk (state " + presence.State + ")");
            yield return 0.8f;

            foreach (float w in Block("away from the desk", shell, watch, engine, rig, false)) yield return w;

            SimulatedHandSource.LeftTestActive = false;
            presence.forceSeated = true;
            foreach (float w in Until(() => shell.AtDesk, 4f)) yield return w;
            presence.forceSeated = false;
        }

        // One run of the watch turn, from the first pose to the hand leaving. `where` goes into every message.
        static IEnumerable<float> Block(string where, OSShell shell, OSWatch watch, GestureEngine engine, DesktopRig rig, bool atDesk)
        {
            string at = atDesk ? "desk" : "away";
            string why;
            Transform head = shell.Head;
            leftFires.Clear();
            if (shell.MenuOpen) shell.CloseMenu();
            SimulatedHandSource.LeftTestActive = false;
            yield return 0.6f;
            Check(!shell.MenuOpen && !watch.Shown && watch.Armed, $"[{where}] start: menu closed, button hidden, armed");
            Check(shell.AtDesk == atDesk, $"[{where}] the user really is {(atDesk ? "at" : "away from")} the desk");

            // ---- the pose is held: not at once, then the button shows above the wrist, facing the eyes
            int presses0 = watch.Presses;
            double t0 = EditorApplication.timeSinceStartup;
            PlaceLeft(Watch, HandShape.Relaxed);
            foreach (float w in Wait(0.1f)) yield return w;
            Check(!watch.Shown, $"[{where}] 0.1 s into the pose the button is not up yet (held {watch.HeldSeconds:F2} s of 0.3)");
            foreach (float w in Until(() => watch.Shown, 3f)) yield return w;
            double took = EditorApplication.timeSinceStartup - t0;
            Check(watch.Shown, $"[{where}] turning the left wrist shows the button (pose {watch.LastPose})");
            if (!watch.Shown) { SimulatedHandSource.LeftTestActive = false; yield break; }
            Measure($"[{where}] button shown {took:F2} s after the pose was set (hold 0.3 s), back dot {watch.LastBackDot:F2}, {watch.LastViewDegrees:F0} degrees off the gaze, {watch.LastDistance:F2} m");
            Check(took >= 0.25, $"[{where}] it needed the 0.3 s hold ({took:F2} s)");
            yield return 0.3f;
            GestureHand hand = engine.Hands[0];
            Vector3 handBack = -HandFeatures.PalmNormal(hand.sample);
            Vector3 wristNow = hand.sample.joints[HandJoint.Wrist];
            Vector3 expected = wristNow + handBack * (watch.liftMillimetres * 0.001f);
            float off = Vector3.Distance(watch.ButtonPosition, expected) * 1000f;
            Check(off < 8f, $"[{where}] the button floats 45 mm above the wrist, along the back of the hand ({off:F1} mm from there)");
            Check(Vector3.Dot(handBack, (head.position - wristNow).normalized) > 0.85f, $"[{where}] the back of the hand faces the eyes");
            Vector3 toButton = (watch.ButtonPosition - head.position).normalized;
            float faceDot = Vector3.Dot(watch.ButtonForward, toButton);
            Check(faceDot > 0.95f, $"[{where}] the button faces the eyes (dot {faceDot:F3})");
            Check(watch.Button != null && watch.Button.interactable, $"[{where}] it is a live UI Set button");
            var rect = (RectTransform)watch.Button.transform;
            float mm = OSToolsTest.Mm(Mathf.Min(rect.rect.width, rect.rect.height), rect);
            Measure($"[{where}] the pressable button measures {mm:F1} mm ({watch.ButtonMeters * 1000f:F0} mm set)");
            Check(mm >= 22f, $"[{where}] the pressable control is at least 22 mm ({mm:F1} mm)");
            Vector3 view = PetUser.HeadCamera.WorldToViewportPoint(watch.ButtonPosition);
            Check(view.z > 0f && view.x > 0.05f && view.x < 0.95f && view.y > 0.05f && view.y < 0.95f, $"[{where}] it is on screen (viewport {view.x:F2}, {view.y:F2})");
            bool hasIcon = false;
            foreach (Image img in watch.Button.GetComponentsInChildren<Image>(false)) if (img.sprite != null && img.gameObject.name.ToLowerInvariant().Contains("icon")) hasIcon = true;
            Check(hasIcon, $"[{where}] the button carries its icon (no placeholder)");
            Check(leftFires.Count == 0, $"[{where}] the engine fired nothing with the left hand while it was held" + (leftFires.Count == 0 ? "" : ": " + string.Join(",", leftFires)));
            Shot($"{at}_wrist_button");

            // ---- it follows the wrist
            Vector3 before = watch.ButtonPosition;
            PlaceLeft(Watch + new Vector3(0.05f, 0.03f, 0f), HandShape.Relaxed);
            foreach (float w in Wait(0.4f)) yield return w;
            Check(watch.Shown && Vector3.Distance(watch.ButtonPosition, before) > 0.03f, $"[{where}] moving the wrist moves the button with it ({Vector3.Distance(watch.ButtonPosition, before) * 1000f:F0} mm)");
            PlaceLeft(Watch, HandShape.Relaxed);
            foreach (float w in Wait(0.4f)) yield return w;

            // ---- a real press opens the menu from the wrist
            Vector3 pressedAt = watch.ButtonPosition;
            bool pressed = Press(watch.Button.gameObject, out why);
            Check(pressed, $"[{where}] a pointer reaches the watch button: " + why);
            if (!pressed) { watch.Button.onClick.Invoke(); }
            foreach (float w in Until(() => shell.MenuOpen, 2f)) yield return w;
            Check(shell.MenuOpen && shell.LastSummon == OSSummon.Watch, $"[{where}] pressing it opens the menu with the Watch summon ({shell.LastSummon})");
            Check(shell.SummonPoint.HasValue && Vector3.Distance(shell.SummonPoint.Value, pressedAt) < 0.02f, $"[{where}] the menu grows from where the button was");
            Check(!watch.Shown && !watch.Armed && watch.Presses == presses0 + 1, $"[{where}] the button hid, disarmed and counted one press (presses {watch.Presses - presses0})");
            foreach (float w in Wait(0.7f)) yield return w;
            Check(!watch.Shown, $"[{where}] with the wrist still turned and the menu open the button stays away");
            Shot($"{at}_menu_open");
            shell.CloseMenu();
            foreach (float w in Wait(0.8f)) yield return w;
            Check(!watch.Shown, $"[{where}] after the menu closes the button does not come back at once (the wrist has not turned away)");

            // ---- turning away re-arms; turning back shows it; turning away again hides it
            PlaceLeft(Watch, HandShape.Relaxed, 80f);
            foreach (float w in Wait(0.6f)) yield return w;
            Check(watch.Armed && watch.LastPose != WatchPose.Ok, $"[{where}] turning the wrist away re-arms it (pose {watch.LastPose})");
            PlaceLeft(Watch, HandShape.Relaxed);
            foreach (float w in Until(() => watch.Shown, 2f)) yield return w;
            Check(watch.Shown, $"[{where}] turning back shows the button again");
            double goneAt = EditorApplication.timeSinceStartup;
            PlaceLeft(Watch, HandShape.Relaxed, 80f);
            foreach (float w in Until(() => !watch.Shown, 2f)) yield return w;
            double goneAfter = EditorApplication.timeSinceStartup - goneAt;
            Check(!watch.Shown, $"[{where}] turning the wrist away hides the button ({goneAfter:F2} s later, hide delay 0.25 s)");

            // ---- the menu opened another way hides it; it returns on its own because nothing pressed it
            PlaceLeft(Watch, HandShape.Relaxed);
            foreach (float w in Until(() => watch.Shown, 2f)) yield return w;
            Check(watch.Shown, $"[{where}] shown again for the menu guard");
            shell.OpenMenu(OSSummon.Key);
            foreach (float w in Wait(0.2f)) yield return w;
            Check(!watch.Shown, $"[{where}] the menu opened another way hides the button at once");
            foreach (float w in Wait(0.5f)) yield return w;
            Check(!watch.Shown, $"[{where}] and it stays hidden while that menu is open");
            shell.CloseMenu();
            foreach (float w in Until(() => watch.Shown, 2f)) yield return w;
            Check(watch.Shown, $"[{where}] once that menu closes and the wrist is still turned it shows again (no press disarmed it)");

            // ---- a panel being dragged hides it and keeps it away
            if (GrabField != null)
            {
                SetGrabbing(1);
                foreach (float w in Wait(0.2f)) yield return w;
                Check(!watch.Shown, $"[{where}] a panel being dragged (PanelManipulator.Grabbing) hides the button");
                foreach (float w in Wait(0.6f)) yield return w;
                Check(!watch.Shown, $"[{where}] and it stays away while the drag lasts");
                SetGrabbing(0);
                foreach (float w in Until(() => watch.Shown, 2f)) yield return w;
                Check(watch.Shown, $"[{where}] it returns after the drag ends with the wrist still turned");
            }
            else Check(false, $"[{where}] PanelManipulator.Grabbing could not be raised from the test");

            // ---- Settings off
            OSSettings.Current.watchTurn = false;
            foreach (float w in Wait(0.3f)) yield return w;
            Check(!watch.Shown, $"[{where}] Settings off: the button is gone at once");
            PlaceLeft(Watch, HandShape.Relaxed);
            PlaceLeft(Watch, HandShape.Relaxed, 80f);
            PlaceLeft(Watch, HandShape.Relaxed);
            foreach (float w in Wait(0.9f)) yield return w;
            Check(!watch.Shown && watch.LastPose == WatchPose.NoHand, $"[{where}] with it off the held pose shows nothing (pose {watch.LastPose})");
            OSSettings.Current.watchTurn = true;
            foreach (float w in Until(() => watch.Shown, 2f)) yield return w;
            Check(watch.Shown, $"[{where}] Settings on again: the held pose shows the button");

            // ---- the hand leaves
            SimulatedHandSource.LeftTestActive = false;
            foreach (float w in Until(() => !watch.Shown, 1f)) yield return w;
            Check(!watch.Shown && !engine.Hands[0].Usable, $"[{where}] the hand leaving hides the button (left hand usable {engine.Hands[0].Usable})");

            // ---- a resting hand fires nothing over 10 s: on the desk for 5 s, then tilted away for 5 s
            leftFires.Clear();
            int presses1 = watch.Presses;
            bool everShown = false, everMenu = false;
            var poses = new List<WatchPose>();
            double restStart = EditorApplication.timeSinceStartup;
            double lastPlace = -1;
            while (EditorApplication.timeSinceStartup - restStart < 10.0)
            {
                double now = EditorApplication.timeSinceStartup - restStart;
                if (now - lastPlace >= 0.5)
                {
                    lastPlace = now;
                    if (now < 5.0)
                    {
                        Transform h = OSShell.FindHead();
                        Pose(h, Desk, 0f, false, out Vector3 dw, out _);
                        dw += h.right * (0.01f * Mathf.Sin((float)now * 3f));
                        SimulatedHandSource.LeftTestShape = HandShape.Relaxed;
                        SimulatedHandSource.LeftTestWrist = dw;
                        SimulatedHandSource.LeftTestRotation = Quaternion.identity;
                        SimulatedHandSource.LeftTestActive = true;
                    }
                    else Drift(Watch, HandShape.Relaxed, 65f, now);
                }
                if (watch.Shown) everShown = true;
                if (shell.MenuOpen) everMenu = true;
                if (poses.Count == 0 || poses[poses.Count - 1] != watch.LastPose) poses.Add(watch.LastPose);
                yield return 0.1f;
            }
            Measure($"[{where}] 10 s of resting hands: poses seen {string.Join(" > ", poses)}, left-hand gestures fired {leftFires.Count}");
            Check(!everShown, $"[{where}] a resting left hand (5 s on the desk, 5 s tilted away) never showed the button");
            Check(watch.Presses == presses1 && !everMenu, $"[{where}] and nothing pressed or opened over those 10 s");
            Check(leftFires.Count == 0, $"[{where}] the gesture engine fired nothing with the left hand over those 10 s" + (leftFires.Count == 0 ? "" : ": " + string.Join(",", leftFires)));
            Check(poses.Contains(WatchPose.TooLow) || poses.Contains(WatchPose.OutOfView), $"[{where}] the desk hand was judged too low or out of view");
            Check(poses.Contains(WatchPose.BackAway), $"[{where}] the tilted hand was judged back-away");
            Shot($"{at}_resting_tilted");

            // ---- palm up: not a watch, and shorter than the engine's 0.6 s so Albie is not called
            leftFires.Clear();
            PlaceLeft(Watch + new Vector3(0.08f, 0.35f, 0.05f), HandShape.Open, 0f, true);
            bool sawPalmUp = false;
            double upStart = EditorApplication.timeSinceStartup;
            while (EditorApplication.timeSinceStartup - upStart < 0.4)
            {
                if (watch.LastPose == WatchPose.PalmUp) sawPalmUp = true;
                if (watch.Shown) break;
                yield return 0.05f;
            }
            Check(!watch.Shown && sawPalmUp, $"[{where}] palm up is not the watch pose (pose {watch.LastPose}, shown {watch.Shown})");
            SimulatedHandSource.LeftTestActive = false;
            foreach (float w in Wait(0.3f)) yield return w;
            Check(leftFires.Count == 0, $"[{where}] palm up for 0.4 s fired no left-hand gesture" + (leftFires.Count == 0 ? "" : ": " + string.Join(",", leftFires)));

            // ---- a pinch: the same wrist and the hand's own pinch shape
            PlaceLeft(Watch, PinchShape);
            foreach (float w in Wait(0.7f)) yield return w;
            Check(!watch.Shown, $"[{where}] a pinching left hand is not the watch pose (pose {watch.LastPose})");
            SimulatedHandSource.LeftTestActive = false;
            foreach (float w in Wait(0.3f)) yield return w;
            Check(!watch.Shown && !shell.MenuOpen, $"[{where}] end: hidden, menu closed");
        }
    }
}
