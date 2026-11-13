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
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Run Tools Test: the real Main scene in Play mode on the desktop rig with voice off. For each Tools tile
    /// (cad, code, train, notebook, messages, gestures) it opens the real menu with the M key, presses the tile with a
    /// pointer, and checks that the tool opened, at the desk and away from it. It then runs the Train tile with a brain
    /// truck (a slalom made through the same tool the voice path uses) and checks that the brain trainer window opens, and
    /// checks the notebook window against the real proj_truck notebook: the plan, versions and attempts show, "Mark worked"
    /// and "Mark failed" write through to the store, and no text is clipped or overlapping. Screenshots go to Logs/os/; the
    /// result is in <see cref="LastResult"/> and a console line "[OSToolsTest] N passed, M failed".
    /// </summary>
    [InitializeOnLoad]
    public static class OSToolsTest
    {
        const string ActiveKey = "OSToolsTest.Active";
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
        static string pressWhy;
        static bool pressOk;
        static bool notebookWasEmpty;
        static string notebookRoot;
        static string approvalsFile;

        static OSToolsTest()
        {
            if (SessionState.GetBool(ActiveKey, false)) EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static bool keepPlaying
        {
            get => SessionState.GetBool("OSToolsTest.Keep", false);
            set => SessionState.SetBool("OSToolsTest.Keep", value);
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
            EditorPrefs.SetBool(RigSelector.ForceDesktopPref, EditorPrefs.GetBool("OSToolsTest.ForceDesktopWas", false));
            EditorPrefs.SetBool(AlbieVoice.EditorOffPref, EditorPrefs.GetBool("OSToolsTest.VoiceOffWas", false));
        }

        [MenuItem("Forge/OS/Run Tools Test")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSToolsTest] Leave Play mode first."); return; }
            EditorPrefs.SetBool("OSToolsTest.ForceDesktopWas", EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false));
            EditorPrefs.SetBool("OSToolsTest.VoiceOffWas", EditorPrefs.GetBool(AlbieVoice.EditorOffPref, false));
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
            string summary = $"[OSToolsTest] {passed} passed, {failed} failed";
            LastResult = (failed == 0 ? "PASS " : "FAIL ") + summary + (failed == 0 ? "" : "\n" + string.Join("\n", failures));
            if (failed == 0) Debug.Log(summary);
            else Debug.LogError(summary + "\n" + string.Join("\n", failures));
            SessionState.SetBool(ActiveKey, false);
            if (keepPlaying) return;
            RestorePrefs();
            EditorApplication.isPlaying = false;
        }

        // The test makes the proj_truck notebook, the slalom artifact and Albie's lessons if this machine has none yet; it takes
        // the files it made away again (a machine that already has them keeps them, and so does the notebook's own data).
        static readonly string[] OwnFolders = { "projects", "artifacts", "user" };
        static readonly List<string> toRemove = new List<string>();

        static void NoteFolders()
        {
            toRemove.Clear();
            foreach (string name in OwnFolders)
            {
                string path = Path.Combine(NotebookStore.DefaultRoot, name);
                if (!Directory.Exists(path)) toRemove.Add(path);
            }
        }

        static void Cleanup()
        {
            ArtifactApprovals.FileOverride = null;
            if (approvalsFile != null && File.Exists(approvalsFile)) File.Delete(approvalsFile);
            NotebookStore.Flush();
            if (notebookRoot != null)
            {
                NotebookStore.Open(NotebookStore.DefaultProject);  // the app's own notebook again
                try { if (Directory.Exists(notebookRoot)) Directory.Delete(notebookRoot, true); }
                catch (Exception e) { Debug.Log("[OSToolsTest] couldn't remove " + notebookRoot + ": " + e.Message); }
                notebookRoot = null;
            }
            foreach (string path in toRemove)
            {
                if (!Directory.Exists(path)) continue;
                try { Directory.Delete(path, true); Debug.Log("[OSToolsTest] removed the test's " + path); }
                catch (Exception e) { Debug.Log("[OSToolsTest] couldn't remove " + path + ": " + e.Message); }
            }
            toRemove.Clear();
        }

        static void Check(bool condition, string what)
        {
            Debug.Log($"[OSToolsTest] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition) { passed++; return; }
            failed++;
            failures.Add(what);
        }

        static void Measure(string line)
        {
            measured.Add(line);
            Debug.Log("[OSToolsTest] measured: " + line);
        }

        static void Shot(string name)
        {
            string path = OSShot.Take(name);
            Debug.Log("[OSToolsTest] shot " + (path ?? "(no camera)"));
        }

        // ---------------------------------------------------------------- real pointer input

        internal static bool Press(GameObject target, out string why)
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

        internal static Transform FindDeep(Transform root, string name)
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

        internal static Vector3 CenterOf(Transform t) => t is RectTransform r ? r.TransformPoint(r.rect.center) : t.position;

        internal static float Mm(float units, Transform t) => units * Mathf.Abs(t.lossyScale.x) * 1000f;

        // ---------------------------------------------------------------- the script

        // Opens the menu with the M key on the Tools tab, looks at it, and presses the tile of this tool. Sets pressOk / pressWhy.
        static IEnumerator<float> PressTile(OSShell shell, OSMenu menu, string toolId, string shotName)
        {
            pressOk = false;
            pressWhy = null;
            shell.SetTab(OSTab.Tools);
            if (!shell.MenuOpen)
            {
                OSInput.Tap(Key.M);
                yield return 0.9f;
                if (!shell.MenuOpen)
                {
                    // The Input System drops queued key events while the editor window is unfocused; the M key itself is checked by OSMenuTest.
                    Debug.Log("[OSToolsTest] M didn't open the menu (editor focus " + UnityEditorInternal.InternalEditorUtility.isApplicationActive + "); opening it with the shell");
                    shell.OpenMenu(OSSummon.Key);
                    yield return 0.9f;
                }
            }
            if (!shell.MenuOpen) { pressWhy = "M didn't open the menu"; yield break; }
            DesktopRig.Current.LookAt(menu.LastCenter);
            yield return 0.4f;
            OSTileView tile = null;
            for (int i = 0; i < menu.ActiveTiles.Count; i++)
                if (menu.ActiveTiles[i].entry != null && menu.ActiveTiles[i].entry.id == toolId) tile = menu.ActiveTiles[i];
            if (tile == null) { pressWhy = "no tile for " + toolId; yield break; }
            if (shotName != null) Shot(shotName);
            pressOk = Press(tile.gameObject, out pressWhy);
            if (!pressOk) Shot("99_press_failed_" + toolId);
            yield return 0.9f;
        }

        // Every text of a window lies inside its panel, none of them overlap, and none is empty or placeholder.
        internal static string LayoutProblem(OSWindow window)
        {
            var panelRect = (RectTransform)window.Panel.transform;
            var texts = window.GetComponentsInChildren<TMP_Text>(false);
            var rects = new List<Rect>();
            var names = new List<string>();
            var corners = new Vector3[4];
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text t = texts[i];
                if (!t.gameObject.activeInHierarchy || string.IsNullOrWhiteSpace(t.text)) continue;
                if (InsideNamed(t.transform, "Manipulation")) continue;
                string lower = t.text.ToLowerInvariant();
                if (lower.Contains("lorem") || lower.Contains("todo") || lower.Contains("placeholder") || lower.StartsWith("sample text")) return "placeholder text '" + t.text + "'";
                t.ForceMeshUpdate();
                Bounds b = t.textBounds;  // the rendered text, in the text's local space
                if (b.size.sqrMagnitude > 1e8f || float.IsNaN(b.size.x)) continue;  // nothing rendered (empty mesh)
                var tr = t.rectTransform;
                Vector3 a = panelRect.InverseTransformPoint(tr.TransformPoint(b.min));
                Vector3 c = panelRect.InverseTransformPoint(tr.TransformPoint(b.max));
                var r = Rect.MinMaxRect(Mathf.Min(a.x, c.x), Mathf.Min(a.y, c.y), Mathf.Max(a.x, c.x), Mathf.Max(a.y, c.y));
                if (r.xMin < panelRect.rect.xMin - 0.5f || r.xMax > panelRect.rect.xMax + 0.5f || r.yMin < panelRect.rect.yMin - 0.5f || r.yMax > panelRect.rect.yMax + 0.5f)
                    return "text outside the window: '" + Short(t.text) + "' " + r + " in " + panelRect.rect;
                rects.Add(r);
                names.Add(Short(t.text));
            }
            for (int i = 0; i < rects.Count; i++)
                for (int j = i + 1; j < rects.Count; j++)
                {
                    // Overlap by more than a hair (the rendered boxes of neighbouring lines may touch).
                    float ox = Mathf.Min(rects[i].xMax, rects[j].xMax) - Mathf.Max(rects[i].xMin, rects[j].xMin);
                    float oy = Mathf.Min(rects[i].yMax, rects[j].yMax) - Mathf.Max(rects[i].yMin, rects[j].yMin);
                    if (ox > 1f && oy > 1.5f) return "text overlaps: '" + names[i] + "' and '" + names[j] + "'";
                }
            return null;
        }

        static bool InsideNamed(Transform t, string name)
        {
            for (Transform p = t; p != null; p = p.parent) if (p.name == name) return true;
            return false;
        }

        static string Short(string s) => s.Length <= 40 ? s : s.Substring(0, 40);

        internal static string AllText(OSWindow window)
        {
            var texts = window.GetComponentsInChildren<TMP_Text>(true);
            var parts = new List<string>();
            foreach (var t in texts) if (t.gameObject.activeInHierarchy) parts.Add(t.text);
            return string.Join("|", parts);
        }

        static IEnumerator<float> OpenEachTool(OSShell shell, OSMenu menu, Workspace workspace, string where)
        {
            OSWindows windows = shell.Windows;
            string[] ids = { "cad", "code", "train", "notebook", "messages", "gestures" };
            foreach (string id in ids)
            {
                windows.CloseAll();
                OSModes.Close();
                yield return 1.6f;
                var step = PressTile(shell, menu, id, id == "cad" && where == "desk" ? "30_tools_tab_desk" : null);
                while (step.MoveNext()) yield return step.Current;
                Check(pressOk, where + ": a pointer reaches the " + id + " tile: " + pressWhy);
                yield return 1.2f;
                OSEntry entry = OSRegistry.Find(OSTab.Tools, id);
                Check(entry != null && entry.isOpen != null && entry.isOpen(), where + ": the " + id + " tile reports it open (status '" + (entry != null && entry.status != null ? entry.status() : "?") + "')");
                switch (id)
                {
                    case "cad":
                    case "code":
                    case "messages":
                        Check(OSModes.IsOpen(id) && windows.IsOpen(OSWindows.GroupId(id)), where + ": " + id + " is the open mode and a window group");
                        Check(ModePanelCount(workspace) > 0, where + ": " + id + " has panels (" + ModePanelCount(workspace) + ")");
                        break;
                    case "train":
                        Check(OSModes.IsOpen("ml") && ModePanelCount(workspace) > 0, where + ": the ML desk is open (" + ModePanelCount(workspace) + " panels)");
                        Check(BrainLoops.Active != null || !windows.IsOpen(OSToolsCatalog.BrainId), where + ": the brain window opens only when a truck exists");
                        break;
                    case "notebook":
                        Check(windows.IsOpen(OSNotebookWindow.WindowId), where + ": the notebook window is open");
                        break;
                    case "gestures":
                        Check(windows.IsOpen(OSToolsCatalog.GesturesId), where + ": the gesture display is a tracked window");
                        var display = UnityEngine.Object.FindAnyObjectByType<GesturePanel>();
                        Check(display != null && display.gameObject.activeInHierarchy, where + ": the gesture display is showing");
                        break;
                }
                if (id == "notebook" && where == "away")
                {
                    OSWindow nb = windows.Find(OSNotebookWindow.WindowId);
                    if (nb != null) { DesktopRig.Current.LookAt(CenterOf(nb.Panel.transform)); yield return 0.4f; Shot("41_notebook_away"); }
                }
                if (id == "gestures")
                {
                    OSWindow g = windows.Find(OSToolsCatalog.GesturesId);
                    if (g != null && g.Panel != null) { DesktopRig.Current.LookAt(CenterOf(g.Panel.transform)); yield return 0.4f; Shot(where == "desk" ? "34_gestures_desk" : "44_gestures_away"); }
                }
                if (id == "cad" || id == "messages" || id == "code")
                {
                    DesktopRig.Current.LookAt(menu.LastCenter);
                    yield return 0.3f;
                    if (where == "away" && id == "cad") Shot("42_cad_away");
                }
            }
            windows.CloseAll();
            OSModes.Close();
            yield return 1.6f;
        }

        static int ModePanelCount(Workspace workspace)
        {
            int n = 0;
            foreach (var p in ForgePanel.All) if (p != null && workspace.IsOpenItem(p.gameObject)) n++;
            return n;
        }

        static IEnumerator<float> Script()
        {
            string why = null;
            // ---- sit at the desk
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

            // ---- the catalog
            var tiles = new List<OSEntry>();
            OSRegistry.Fill(OSTab.Tools, tiles);
            string order = "";
            foreach (var e in tiles) order += e.id + ";";
            Check(order.StartsWith("cad;code;train;notebook;messages;gestures", StringComparison.Ordinal), "the Tools tab lists cad, code, train, notebook, messages, gestures (" + order + ")");
            foreach (var e in tiles)
                Check(!string.IsNullOrEmpty(e.title) && OSUi.Icon(e.icon) != null && e.open != null && e.status != null && e.isOpen != null, "tile " + e.id + " has a title, a kit icon, a status, an open flag and an action");

            // ---- the real notebook of proj_truck: seed the demo story only if this machine has none yet
            ArtifactApprovals.FileOverride = approvalsFile = Path.Combine(Path.GetTempPath(), "os_tools_test_approvals.json");
            NoteFolders();
            // proj_truck is opened from a scratch root, so a machine's own notebook is never written to; it is the real store
            // and the real project id, filled with the demo story the app's own mock mode uses (NotebookStore.SeedFixtures).
            notebookRoot = Path.Combine(Path.GetTempPath(), "os_tools_test_notebook");
            if (Directory.Exists(notebookRoot)) Directory.Delete(notebookRoot, true);
            NotebookStore.Open(NotebookStore.DefaultProject, notebookRoot);
            notebookWasEmpty = NotebookStore.Entries.Count == 0 && NotebookStore.Versions.Count == 0;
            if (notebookWasEmpty) NotebookStore.SeedFixtures();
            if (NotebookStore.CurrentPlan == null) NotebookStore.RevisePlan("Starting the truck project", "Get the RC truck through the slalom without hitting a cone.", "Three clean laps in a row.", null);
            int cut = NotebookStore.CutVersion("thing", "Softer steering near the cones");
            string attemptId = NotebookStore.Log("attempt", "Cut the top speed in the tight corners", "The truck was clipping the third cone.", "albie", "A clean lap", new[] { "vehicle" });
            Check(!string.IsNullOrEmpty(attemptId) && cut > 0, "the notebook has a new version (v" + cut + ") and a new attempt (" + attemptId + ")");
            Measure("proj_truck: " + NotebookStore.Entries.Count + " entries, " + NotebookStore.Versions.Count + " versions, " + NotebookStore.AllLessons.Count + " lessons, plan v" + ((int?)NotebookStore.CurrentPlan?["version"] ?? 0) + ", seeded " + notebookWasEmpty);

            // ---- at the desk: every tile from the real menu
            var each = OpenEachTool(shell, menu, workspace, "desk");
            while (each.MoveNext()) yield return each.Current;

            // ---- the notebook window: real data, marking writes through
            var step = PressTile(shell, menu, "notebook", null);
            while (step.MoveNext()) yield return step.Current;
            Check(pressOk, "desk: the notebook tile can be pressed again: " + pressWhy);
            yield return 0.8f;
            OSWindow nbw = windows.Find(OSNotebookWindow.WindowId);
            Check(nbw != null && nbw.GetComponent<OSNotebookWindow>() != null, "the notebook window carries the notebook view");
            if (nbw != null)
            {
                DesktopRig.Current.LookAt(CenterOf(nbw.Panel.transform));
                yield return 0.5f;
                string text = AllText(nbw);
                Check(text.Contains("Cut the top speed in the tight corners"), "the notebook lists the new attempt");
                Check(text.Contains("Waiting for your verdict"), "the newest attempt with no outcome waits for a verdict");
                Check(text.Contains("v" + cut) && text.Contains("Softer steering near the cones"), "the notebook lists the new version v" + cut);
                Check(text.Contains("Plan v"), "the notebook shows the plan");
                Check(text.Contains(OSNotebookWindow.ProjectName(NotebookStore.ProjectId)), "the project's name is in the window header (" + OSNotebookWindow.ProjectName(NotebookStore.ProjectId) + ")");
                Check(!text.Contains("TMP") && !text.Contains("New Text"), "no default text left in the window");
                string problem = LayoutProblem(nbw);
                Check(problem == null, "the notebook text is inside the window and doesn't overlap" + (problem ?? ""));
                Transform worked = FindDeep(nbw.transform, "Button Mark worked");
                Transform failedButton = FindDeep(nbw.transform, "Button Mark failed");
                Check(worked != null && failedButton != null, "Mark worked and Mark failed buttons are there");
                if (worked != null)
                {
                    var r = (RectTransform)worked;
                    Measure($"Mark worked: {Mm(r.rect.width, r):F0} x {Mm(r.rect.height, r):F0} mm");
                    Check(Mm(r.rect.width, r) >= 22f && Mm(r.rect.height, r) >= 22f, "Mark worked is at least 22 x 22 mm");
                }
                Shot("31_notebook_desk");
                string before = (string)NotebookStore.Entry(attemptId)?["outcome"];
                Check(worked != null && Press(worked.gameObject, out why), "a pointer reaches Mark worked: " + why);
                yield return 0.6f;
                JObject marked = NotebookStore.Entry(attemptId);
                Check((string)marked?["outcome"] == "worked" && (string)marked?["outcome_by"] == "person", "Mark worked wrote through to the store (was " + before + ", now " + (string)marked?["outcome"] + " by " + (string)marked?["outcome_by"] + ")");
                text = AllText(nbw);
                Check(text.Contains("worked (you marked it)"), "the window now says 'worked (you marked it)' without reopening");
                JObject waitingNow = NotebookStore.NewestOpen();
                Check(waitingNow == null || (string)waitingNow["id"] != attemptId, "the marked attempt no longer waits for a verdict (next waiting: " + ((string)waitingNow?["id"] ?? "none") + ")");
                Check(!text.Contains("Cut the top speed in the tight corners") || text.IndexOf("worked (you marked it)", StringComparison.Ordinal) > text.IndexOf("Cut the top speed in the tight corners", StringComparison.Ordinal), "the marked attempt now sits in the list with its outcome");
                Shot("32_notebook_marked");

                // Mark failed on a second new attempt
                string second = NotebookStore.Log("attempt", "Wider turning circle on the last cone", null, null, null, new[] { "vehicle" });
                yield return 0.6f;
                Transform fail = FindDeep(nbw.transform, "Button Mark failed");
                Check(fail != null && Press(fail.gameObject, out why), "a pointer reaches Mark failed on the next attempt: " + why);
                yield return 0.6f;
                Check((string)NotebookStore.Entry(second)?["outcome"] == "failed", "Mark failed wrote through to the store");
                Check(AllText(nbw).Contains("failed (you marked it)"), "the window says 'failed (you marked it)'");
                problem = LayoutProblem(nbw);
                Check(problem == null, "after marking, the notebook text is still inside the window and doesn't overlap" + (problem ?? ""));
                Shot("33_notebook_after_fail");
            }
            windows.CloseAll();
            yield return 1.2f;

            Check(BrainLoops.Active == null, "no brain truck yet");
            float waited = 0f;
            // ---- away from the desk
            workspace.presence.forceSeated = false;
            DesktopRig.Current.ToggleSeat();
            yield return 1f;
            var rig = DesktopRig.Current;
            var body = rig.GetComponent<CharacterController>();
            if (body != null) body.enabled = false;
            Transform frame = workspace.presence.Frame;
            Vector3 away = frame != null ? -Vector3.ProjectOnPlane(frame.forward, Vector3.up).normalized * 3.5f : Vector3.back * 3.5f;
            rig.transform.position += away;
            if (body != null) body.enabled = true;
            waited = 0f;
            while (workspace.presence.AtDesk && waited < 6f) { waited += 0.25f; yield return 0.25f; }
            Check(!workspace.presence.AtDesk, "walking away puts the user away from the desk");
            yield return 0.8f;

            each = OpenEachTool(shell, menu, workspace, "away");
            while (each.MoveNext()) yield return each.Current;

            // ---- away: a slalom (the truck needs clear floor, so not at the desk), then Train with a truck, Bring here and Close all
            var make = MakeTruck();
            while (make.MoveNext()) yield return make.Current;
            Check(BrainLoops.Active != null, "away: the slalom spawned a brain truck (" + truckNote + ")");
            workspace.Close();
            yield return 1.2f;
            step = PressTile(shell, menu, "notebook", "43_tools_tab_away");
            while (step.MoveNext()) yield return step.Current;
            Check(pressOk && windows.IsOpen(OSNotebookWindow.WindowId), "away: the notebook opens with a truck around (" + pressWhy + ")");
            step = PressTile(shell, menu, "gestures", null);
            while (step.MoveNext()) yield return step.Current;
            Check(pressOk && windows.IsOpen(OSToolsCatalog.GesturesId), "away: the gesture display opens beside it (" + pressWhy + ")");
            // Train last: the ML desk's panels fill the view and sit in front of where the menu opens, so the menu can't be used while it is open.
            step = PressTile(shell, menu, "train", null);
            while (step.MoveNext()) yield return step.Current;
            Check(pressOk, "away: a pointer reaches the Train tile with a truck: " + pressWhy);
            yield return 1.5f;
            Check(OSModes.IsOpen("ml") && windows.IsOpen(OSToolsCatalog.BrainId), "away: Train with a truck opens the ML desk and the brain trainer");
            yield return 1f;
            windows.BringHere();
            yield return 2f;
            Transform head = shell.Head;
            bool near = true;
            foreach (var w in windows.All)
            {
                if (w == null || w.IsGroup || w.Panel == null) continue;
                float d = Vector3.Distance(head.position, CenterOf(w.Panel.transform));
                if (d > 1.6f) near = false;
            }
            Check(near, "Bring windows here brings the tool windows (notebook, brain, gestures) within 1.6 m");
            OSWindow brainAway = windows.Find(OSToolsCatalog.BrainId);
            if (brainAway != null)
            {
                DesktopRig.Current.LookAt(CenterOf(brainAway.Panel.transform));
                yield return 0.6f;
                Shot("45_brain_window_away");
                string awayProblem = LayoutProblem(brainAway);
                Check(awayProblem == null, "away: the brain trainer text is inside its window and doesn't overlap" + (awayProblem ?? ""));
            }
            windows.CloseAll();
            yield return 2f;
            var display2 = UnityEngine.Object.FindAnyObjectByType<GesturePanel>();
            Check(!windows.IsOpen(OSToolsCatalog.GesturesId) && (display2 == null || !display2.gameObject.activeInHierarchy), "Close all closes the gesture display too");
            Check(windows.Count == 0 && !OSModes.IsOpen("ml"), "Close all leaves no windows and no mode");

            // ---- back at the desk with the truck still there: Train opens the ML desk and the brain trainer
            workspace.presence.forceSeated = true;
            waited = 0f;
            while (!workspace.presence.AtDesk && waited < 4f) { waited += 0.25f; yield return 0.25f; }
            yield return 0.8f;
            step = PressTile(shell, menu, "train", null);
            while (step.MoveNext()) yield return step.Current;
            Check(pressOk, "desk: a pointer reaches the Train tile with a truck: " + pressWhy);
            yield return 1.5f;
            Check(OSModes.IsOpen("ml"), "desk: Train with a truck opens the ML desk");
            OSWindow brain = windows.Find(OSToolsCatalog.BrainId);
            Check(brain != null, "desk: Train with a truck opens the brain trainer window");
            string trainStatus = OSRegistry.Find(OSTab.Tools, "train").status();
            Measure("train status with a truck: " + trainStatus);
            Check(trainStatus.StartsWith("open · brain ready", StringComparison.Ordinal), "the Train tile says 'open · brain ready' (" + trainStatus + ")");
            if (brain != null)
            {
                Check(brain.GetComponentInChildren<Forge.ML.ControllerBrainView>(true) != null, "the brain window carries the controller brain view");
                DesktopRig.Current.LookAt(CenterOf(brain.Panel.transform));
                yield return 0.6f;
                Shot("35_brain_window");
                string problem = LayoutProblem(brain);
                Check(problem == null, "the brain trainer text is inside its window and doesn't overlap" + (problem ?? ""));
            }
            windows.CloseAll();
            OSModes.Close();
            AlbieTools.Call("artifact_close", new JObject());
            yield return 1.5f;
        }

        static string truckNote;

        // A slalom through the tool the voice path uses; the first time it asks to be allowed, which the test accepts. The
        // simulated living room of the desktop rig has no clear 4.6 x 0.8 m strip of floor, so the course is laid out in the
        // slalom sandbox's mock living room instead (the host's Room), and the host goes back to the real room afterwards.
        static IEnumerator<float> MakeTruck()
        {
            ArtifactHost host = ArtifactHost.Current ?? ArtifactBootstrap.Ensure();
            if (host != null) host.Room = MockFloorPlan.LivingRoom();
            JObject made = AlbieTools.Call("artifact_create", new JObject { ["template"] = "slalom" });
            string artifactId = (string)made?["id"];
            if ((string)made?["opened"]?["status"] == "awaiting_approval" || (string)made?["status"] == "awaiting_approval")
            {
                if (host != null && host.OpenManifest != null) ArtifactApprovals.Approve(host.OpenManifest);
                made = AlbieTools.Call("artifact_open", new JObject { ["id"] = artifactId });
            }
            truckNote = made == null ? "no answer" : ((string)made["error"] ?? (string)made["status"] ?? (string)made["opened"]?["status"] ?? "ok");
            Debug.Log("[OSToolsTest] slalom: " + truckNote);
            float waited = 0f;
            while (BrainLoops.Active == null && waited < 8f) { waited += 0.5f; yield return 0.5f; }
            if (host != null) host.Room = null;
        }
    }
}
