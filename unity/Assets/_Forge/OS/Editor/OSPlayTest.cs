using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Run Play Test: the real Main scene in Play mode on the desktop rig with voice off, walked through the way a
    /// user would use the Albie OS (spec 6.1). Modelled on GesturePlayTest: it sets the editor prefs for forced desktop mode and
    /// voice off, opens Main, enters Play and steps a list of named steps, each its own <c>IEnumerator&lt;float&gt;</c> (the
    /// value yielded is seconds of editor time to wait). Buttons are pressed with real pointer events (<see cref="PressButton(GameObject, string)"/>):
    /// the helper projects the control to the screen, asks the EventSystem what is under that point, fails clearly if it is
    /// not the control or the control is not interactable, then sends the pointer enter, down, up and click events the
    /// DesktopRig mouse sends. Screenshots of the head camera go to Logs/os/NN_name.png.
    /// Poll an eval with <see cref="Running"/> and <see cref="LastResult"/>; the console gets "[OSPlayTest] N passed, M failed".
    /// To add a step (spec steps 4 to 8) write a method returning <c>IEnumerator&lt;float&gt;</c> and add it to <see cref="Steps"/>.
    /// Batch (not -nographics, or there are no screenshots):
    /// <c>Unity -batchmode -projectPath unity -executeMethod Forge.OS.Editor.OSPlayTest.RunBatch -logFile -</c>
    /// </summary>
    [InitializeOnLoad]
    public static class OSPlayTest
    {
        const string ActiveKey = "OSPlayTest.Active", BatchKey = "OSPlayTest.Batch", ResultKey = "OSPlayTest.Result";
        const string ForceDesktopWas = "OSPlayTest.ForceDesktopWas", VoiceOffWas = "OSPlayTest.VoiceOffWas";
        const string ScenePath = "Assets/_Forge/Core/Main.unity";
        const double GiveUpSeconds = 300;

        // What "Bring windows here" has to achieve: every window this close to the eyes and in the front arc the window manager
        // fans them into (OSWindows.MaxYaw is 110 degrees to each side, used only when the rows in front are full), never behind.
        const float ReachMeters = 2.0f, ViewDegrees = 112f;
        // A window that stood still while the user walked away may not have moved more than this.
        const float StayMeters = 0.02f;

        static IEnumerator<float> steps;
        static double wakeAt, startedAt;
        static int passed, failed, skipped, shotCount;
        static readonly List<string> failures = new List<string>();
        static string currentStep, stepShot, firstFailStep, firstFailWhat, firstFailShot;
        static string tempRoot;

        // What the steps share, refreshed by Ready().
        static OSShell shell;
        static Workspace workspace;
        static global::DesktopRig rig;
        static OSTableButton tableButton;
        static OSMenu menu;
        static OSEntry pressedEntry;
        static readonly Dictionary<string, Vector3> homePositions = new Dictionary<string, Vector3>();
        static Vector3 artifactHome;
        static bool hadArtifactHome;

        static OSPlayTest()
        {
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        /// <summary>True from <see cref="Run"/> until the test has finished (it survives the domain reload into Play).</summary>
        public static bool Running => SessionState.GetBool(ActiveKey, false);

        /// <summary>"running" while it runs, then "PASS os play test: ..." or "FAIL os play test: ..." with the first failing step and its screenshot. Empty before the first run.</summary>
        public static string LastResult
        {
            get => SessionState.GetString(ResultKey, "");
            private set => SessionState.SetString(ResultKey, value ?? "");
        }

        /// <summary>For -executeMethod: runs, then exits the editor with 0 on a pass and 1 on a failure.</summary>
        public static void RunBatch()
        {
            SessionState.SetBool(BatchKey, true);
            Run();
        }

        [MenuItem("Forge/OS/Run Play Test")]
        public static void Run()
        {
            if (Running)
            {
                Debug.LogError("[OSPlayTest] A run is already in progress.");
                return;
            }
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Refuse("leave Play mode first");
                return;
            }
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty)
            {
                Refuse("save or discard the open scene first");
                return;
            }
            EditorPrefs.SetBool(ForceDesktopWas, EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false));
            EditorPrefs.SetBool(VoiceOffWas, EditorPrefs.GetBool(AlbieVoice.EditorOffPref, false));
            EditorPrefs.SetBool(RigSelector.ForceDesktopPref, true);
            EditorPrefs.SetBool(AlbieVoice.EditorOffPref, true);
            SessionState.SetBool(ActiveKey, true);
            LastResult = "running";
            EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorApplication.isPlaying = true;
        }

        static void Refuse(string why)
        {
            LastResult = "FAIL os play test: " + why;
            Debug.LogError("[OSPlayTest] " + why);
            if (SessionState.GetBool(BatchKey, false)) EditorApplication.Exit(1);
        }

        static void OnPlayMode(PlayModeStateChange change)
        {
            if (!Running) return;
            if (change == PlayModeStateChange.EnteredPlayMode) Begin();
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                // Play mode was stopped by hand before the script finished.
                EditorApplication.update -= Tick;
                Check(false, "Play mode stopped before the test finished");
                Finish(false);
            }
        }

        static void Begin()
        {
            Directory.CreateDirectory(OSShot.Dir);
            DeleteOldShots();
            try { File.WriteAllText(ManifestPath, ""); } catch (IOException) { }
            passed = failed = skipped = shotCount = 0;
            failures.Clear();
            homePositions.Clear();
            hadArtifactHome = false;
            currentStep = stepShot = firstFailStep = firstFailWhat = firstFailShot = null;

            // The run reads and writes its own settings file and artifact library, never the user's.
            tempRoot = Path.Combine(Application.temporaryCachePath, "os_playtest");
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch (IOException) { }
            Directory.CreateDirectory(tempRoot);
            OSSettings.SetPathForTests(Path.Combine(tempRoot, "os_settings.json"));
            Forge.Artifacts.ArtifactLibrary.RootOverride = Path.Combine(tempRoot, "artifacts");
            Forge.Artifacts.ArtifactApprovals.FileOverride = Path.Combine(tempRoot, "artifacts", "approvals.json");

            steps = Script();
            wakeAt = 0;
            startedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        // The shots of the last run are listed in a manifest, so this run clears exactly those and never another test's pictures.
        static string ManifestPath => Path.Combine(OSShot.Dir, "play_test_shots.txt");

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
            }
            catch (IOException) { }
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup - startedAt > GiveUpSeconds)
            {
                Check(false, "timed out after " + GiveUpSeconds + " s");
                Finish(true);
                return;
            }
            if (EditorApplication.timeSinceStartup < wakeAt) return;
            try
            {
                if (steps.MoveNext()) wakeAt = EditorApplication.timeSinceStartup + steps.Current;
                else Finish(true);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Check(false, "the script threw " + e.Message);
                Finish(true);
            }
        }

        static void Finish(bool stopPlay)
        {
            EditorApplication.update -= Tick;
            string summary = $"[OSPlayTest] {passed} passed, {failed} failed" + (skipped > 0 ? $" ({skipped} skipped)" : "");
            var result = new StringBuilder();
            if (failed == 0)
            {
                Debug.Log(summary);
                result.Append("PASS os play test: ").Append(passed).Append(" passed, 0 failed");
                if (skipped > 0) result.Append(" (").Append(skipped).Append(" skipped)");
            }
            else
            {
                result.Append("FAIL os play test: ").Append(passed).Append(" passed, ").Append(failed).Append(" failed");
                if (skipped > 0) result.Append(" (").Append(skipped).Append(" skipped)");
                result.Append(". First failing step: '").Append(firstFailStep).Append("': ").Append(firstFailWhat)
                      .Append(". Screenshot: ").Append(firstFailShot ?? "none");
                result.Append('\n').Append(string.Join("\n", failures));
                Debug.LogError(summary + "\nFirst failing step: " + firstFailStep + ": " + firstFailWhat + "\nScreenshot: " + (firstFailShot ?? "none") + "\n" + string.Join("\n", failures));
            }
            LastResult = result.ToString();
            try { File.WriteAllText(Path.Combine(OSShot.Dir, "play_test_result.txt"), LastResult); } catch (IOException) { }

            OSSettings.SetPathForTests(null);
            Forge.Artifacts.ArtifactLibrary.RootOverride = null;
            Forge.Artifacts.ArtifactApprovals.FileOverride = null;
            try { if (!string.IsNullOrEmpty(tempRoot) && Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch (IOException) { }

            EditorPrefs.SetBool(RigSelector.ForceDesktopPref, EditorPrefs.GetBool(ForceDesktopWas, false));
            EditorPrefs.SetBool(AlbieVoice.EditorOffPref, EditorPrefs.GetBool(VoiceOffWas, false));
            bool batch = SessionState.GetBool(BatchKey, false);
            SessionState.SetBool(ActiveKey, false);
            if (stopPlay) EditorApplication.isPlaying = false;
            if (batch) EditorApplication.Exit(failed == 0 ? 0 : 1);
        }

        // ---------------------------------------------------------------- bookkeeping

        static void Check(bool condition, string what)
        {
            Debug.Log($"[OSPlayTest] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition)
            {
                passed++;
                return;
            }
            failed++;
            failures.Add($"[{currentStep}] {what}");
            if (stepShot == null)
            {
                // One screenshot of the scene as it was at the first failure in each step.
                stepShot = (Application.isPlaying ? Shot("FAIL_" + Slug(currentStep)) : null) ?? "(no screenshot: no camera or no graphics)";
                if (firstFailStep == null)
                {
                    firstFailStep = currentStep;
                    firstFailWhat = what;
                    firstFailShot = stepShot;
                }
            }
        }

        static void Skip(string what, string why)
        {
            skipped++;
            Debug.Log($"[OSPlayTest] skip {what}: {why}");
        }

        static string Shot(string name)
        {
            string path = OSShot.Take($"{++shotCount:00}_{name}");
            if (path != null)
            {
                Debug.Log("[OSPlayTest] shot " + path);
                try { File.AppendAllText(ManifestPath, Path.GetFileName(path) + "\n"); } catch (IOException) { }
            }
            return path;
        }

        static string Slug(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text ?? "")
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
            return sb.ToString();
        }

        /// <summary>Waits (in steps of 0.1 s of editor time) until <paramref name="done"/> is true or the time is up. The caller checks which.</summary>
        static IEnumerable<float> Until(Func<bool> done, float seconds)
        {
            double end = EditorApplication.timeSinceStartup + seconds;
            while (!done() && EditorApplication.timeSinceStartup < end) yield return 0.1f;
        }

        /// <summary>Refreshes the shared references. False when the shell, the workspace or the desktop rig is missing.</summary>
        static bool Ready()
        {
            shell = OSShell.Current;
            workspace = Workspace.Current;
            rig = global::DesktopRig.Current;
            if (shell == null || workspace == null || rig == null) return false;
            tableButton = shell.GetComponent<OSTableButton>();
            menu = shell.GetComponentInChildren<OSMenu>(true);
            return true;
        }

        static Vector3 CenterOf(OSWindow window) =>
            window.Root is RectTransform rect ? rect.TransformPoint(rect.rect.center) : window.Root.position;

        static bool ArtifactOpen()
        {
            var host = Forge.Artifacts.ArtifactHost.Current;
            return host != null && host.OpenId != null;
        }

        static Vector3 ArtifactPosition()
        {
            var host = Forge.Artifacts.ArtifactHost.Current;
            if (host == null) return Vector3.zero;
            var volume = host.GetComponentInChildren<ForgeVolume>();
            return volume != null ? volume.transform.position : host.transform.position;
        }

        // ---------------------------------------------------------------- the steps

        /// <summary>
        /// The script, in spec 6.1 numbering. Add a step by writing a method that returns <c>IEnumerator&lt;float&gt;</c> and
        /// adding it here in order: 4 watch turn, 5 build something, 6 the truck, 7 agents, 8 settings. A step that fails marks
        /// the run failed; the next step still runs (each recovers from a failed press by calling the API, so it can go on).
        /// </summary>
        static List<Step> Steps() => new List<Step>
        {
            new Step("1 arrive, sit, press the table button", StepArrive),
            new Step("2a open each tab", StepTabs),
            new Step("2b open a tool, a project and an artifact", StepOpenThree),
            new Step("3a stand and walk away", StepWalkAway),
            new Step("3b corner button", StepCornerButton),
            new Step("3c menu with M, bring windows here", StepBringHere),
            new Step("3d menu over an open mode, away from a desk", StepMenuOverMode),
            // 4 watch turn      (OSWatch part)
            // 6 truck project   (Course, Drive pad laps, Train, Autopilot)
            // 7 agents          (spawn, bring home, send out)
            // 8 settings        (corner button off, it is gone)
            new Step("5 build something", StepBuild),
            new Step("9 close all", StepCloseAll),
        };

        sealed class Step
        {
            public readonly string name;
            public readonly Func<IEnumerator<float>> run;

            public Step(string name, Func<IEnumerator<float>> run)
            {
                this.name = name;
                this.run = run;
            }
        }

        // Runs the steps in order; a step that throws is reported as a failure and the run goes on with the next one.
        static IEnumerator<float> Script()
        {
            foreach (Step step in Steps())
            {
                currentStep = step.name;
                stepShot = null;
                int failedBefore = failed;
                Debug.Log("[OSPlayTest] --- step " + step.name);
                IEnumerator<float> it = step.run();
                while (true)
                {
                    bool more;
                    try
                    {
                        more = it.MoveNext();
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        Check(false, "the step threw " + e.GetType().Name + ": " + e.Message);
                        break;
                    }
                    if (!more) break;
                    yield return it.Current;
                }
                Debug.Log($"[OSPlayTest] --- step '{step.name}' {(failed == failedBefore ? "passed" : "FAILED")}");
            }
        }

        // 1. Arrive, sit at the desk, the greeting releases the desk (START), the table button appears, press it, the menu opens.
        static IEnumerator<float> StepArrive()
        {
            foreach (float w in Until(() => Workspace.Current != null && OSShell.Current != null && global::DesktopRig.Current != null, 30f)) yield return w;
            if (!Ready())
            {
                Check(false, "the OS shell, the workspace and the desktop rig are all in Main");
                yield break;
            }
            Check(true, "the OS shell is in Main (OSBootstrap added it)");
            Check(menu != null, "the menu view is on the shell");
            Check(tableButton != null, "the table button component is on the shell");

            foreach (float w in Until(() =>
            {
                if (Workspace.Current != null) Workspace.Current.presence.forceSeated = true;  // the room may still be loading
                return Workspace.Current != null && Workspace.Current.presence.AtDesk;
            }, 25f)) yield return w;
            Check(workspace.presence.AtDesk, "the user is at the desk (forced seated)");
            yield return 0.5f;
            rig.ToggleSeat();
            yield return 0.9f;
            Check(rig.Seated, "the desktop rig sat down at the desk");

            // The pet's first-arrival greeting holds the desk until START. Press START the way a user does.
            var greeting = UnityEngine.Object.FindAnyObjectByType<Forge.Pet.ArrivalGreeting>(FindObjectsInactive.Include);
            double waitFrom = EditorApplication.timeSinceStartup;
            foreach (float w in Until(() => (greeting != null && greeting.IsShown) || (!shell.Held && EditorApplication.timeSinceStartup - waitFrom > 4.0), 25f)) yield return w;
            bool pressedStart = false;
            if (greeting != null && greeting.IsShown && greeting.StartRing != null && greeting.StartRing.Button != null)
            {
                yield return 0.8f;  // let the card pop in
                pressedStart = PressButton(greeting.StartRing.Button.gameObject, "START");
                foreach (float w in Until(() => !shell.Held, 4f)) yield return w;
            }
            Check(!shell.Held, pressedStart ? "pressing START released the greeting's hold on the desk" : "no greeting is holding the desk");
            if (shell.Held) workspace.HoldDock(false);  // recover, so the rest can go on
            if (pressedStart) yield return 2.5f;        // the code mode opens by itself after START

            // The table button appears.
            foreach (float w in Until(() => tableButton != null && tableButton.Shown && tableButton.Button != null, 8f)) yield return w;
            bool shown = tableButton != null && tableButton.Shown;
            Check(shown, "the table button appears on the table (at the desk, nothing holding it)");
            yield return 0.6f;
            Shot("table_button");

            // Press it with a real pointer event.
            bool pressed = shown && PressButton(tableButton.Button.gameObject, "table button");
            foreach (float w in Until(() => shell.MenuOpen, 2f)) yield return w;
            Check(shell.MenuOpen, "pressing the table button opens the menu");
            if (!shell.MenuOpen) shell.OpenMenu(OSSummon.Code);  // recover
            else if (pressed) Check(shell.LastSummon == OSSummon.TableButton, "the menu says the table button opened it (" + shell.LastSummon + ")");
            yield return 0.6f;
            Check(menu != null && menu.Visible, "the menu is showing");
            if (menu != null && shell.Head != null)
            {
                float distance = Vector3.Distance(menu.LastCenter, shell.Head.position);
                Check(distance >= OSSettings.MinDistance - 0.02f && distance <= OSSettings.MaxDistance + 0.02f,
                    $"the menu sits within arm's reach of the eyes ({distance:F2} m)");
            }
            Shot("menu_opened");
        }

        // 2a. Each tab shows tiles (or its empty sentence).
        static IEnumerator<float> StepTabs()
        {
            if (!Ready()) { Check(false, "the OS shell is in the scene"); yield break; }
            foreach (float w in EnsureMenuOpen()) yield return w;
            OSTab[] order = { OSTab.Projects, OSTab.Artifacts, OSTab.Tools };
            foreach (OSTab tab in order)
            {
                PressButton("Tab " + tab, menu);
                yield return 0.35f;
                if (shell.Tab != tab) shell.SetTab(tab);  // recover
                Check(shell.Tab == tab, "pressing the " + tab + " tab shows it");
                var list = new List<OSEntry>();
                OSRegistry.Fill(tab, list);
                int tiles = menu != null ? menu.ActiveTiles.Count : 0;
                Check(list.Count == 0 ? tiles == 0 : tiles > 0, $"the {tab} tab shows its tiles ({tiles} on this page, {list.Count} in the list)");
                Shot("tab_" + tab.ToString().ToLowerInvariant());
            }
        }

        // 2b. A Tool (notebook), a Project (RC truck) and an Artifact (a volume template) all open from the menu, and stay open together.
        static IEnumerator<float> StepOpenThree()
        {
            if (!Ready()) { Check(false, "the OS shell is in the scene"); yield break; }

            // The Tool.
            foreach (float w in PressTile(OSTab.Tools, e => e.id == "notebook", "the Notebook tool")) yield return w;
            if (pressedEntry == null) RecoverOpen("tool", "notebook");
            foreach (float w in Until(() => shell.Windows.IsOpen("notebook"), 4f)) yield return w;
            Check(shell.Windows.IsOpen("notebook"), "the Notebook tool opened a window ('notebook')");

            // The Project.
            foreach (float w in PressTile(OSTab.Projects, e => e.id == "proj_truck", "the RC truck project")) yield return w;
            if (pressedEntry == null) RecoverOpen("project", "proj_truck");
            foreach (float w in Until(() => shell.Windows.IsOpen("project"), 5f)) yield return w;
            Check(shell.Windows.IsOpen("project"), "the RC truck project opened its window ('project')");
            Check(Forge.Notebook.NotebookStore.IsOpen && Forge.Notebook.NotebookStore.ProjectId == "proj_truck",
                "the RC truck project is the open notebook project (" + Forge.Notebook.NotebookStore.ProjectId + ")");

            // The Artifact: a volume template (the heart, or the next one that exists), so it needs no approval sheet.
            OSEntry artifact = FindVolumeArtifact();
            Check(artifact != null, "the Artifacts tab offers a volume template to open (heart, cell, brain, dna ...)" + (artifact != null ? ": " + artifact.id : ""));
            string artifactId = artifact != null ? artifact.id : null;
            if (artifact != null)
            {
                foreach (float w in PressTile(OSTab.Artifacts, e => e.id == artifactId, "the artifact tile " + artifactId)) yield return w;
                if (pressedEntry == null) RecoverOpen("artifact", artifactId);
                foreach (float w in Until(() => ArtifactOpen() || (Forge.Artifacts.ArtifactHost.Current != null && Forge.Artifacts.ArtifactHost.Current.AwaitingApproval), 8f)) yield return w;
                var host = Forge.Artifacts.ArtifactHost.Current;
                if (host != null && host.AwaitingApproval)
                    Check(false, "the artifact opened without an approval sheet (it asked for permissions)");
                else
                    Check(ArtifactOpen(), "the artifact opened (" + (host != null ? host.OpenId : "no host") + ")");
            }
            yield return 0.6f;

            bool notebook = shell.Windows.IsOpen("notebook"), project = shell.Windows.IsOpen("project"), artifactOpen = ArtifactOpen();
            Check(notebook && project && artifactOpen,
                $"three things are open at once (notebook window {notebook}, project window {project}, artifact {artifactOpen}; {shell.Windows.Count} OS windows)");
            Check(!shell.MenuOpen, "the menu closed itself after the last tile");
            yield return 0.9f;  // the adopted panel glides clear, if it had to
            Check(ArtifactPanelsClear(out string overlapping), "no panel of the artifact overlaps another window or a panel of the open mode, seen from the eyes" + (overlapping != null ? " (" + overlapping + ")" : ""));
            Shot("three_open");
        }

        // Every panel under the open artifact's host, against every other showing ForgePanel that is not the artifact's own
        // (OS windows, the open mode's panels, other artifacts'): none may overlap it in the view from the eyes. The same
        // yaw and pitch rectangles as OSWindowsTest.
        static bool ArtifactPanelsClear(out string first)
        {
            first = null;
            var host = Forge.Artifacts.ArtifactHost.Current;
            Transform head = OSShell.FindHead();
            if (host == null || head == null) return true;
            var own = host.GetComponentsInChildren<ForgePanel>(false);
            foreach (ForgePanel mine in own)
            {
                if (mine == null || !mine.gameObject.activeInHierarchy) continue;
                Rect a = Angular(mine, head);
                foreach (ForgePanel other in ForgePanel.All)
                {
                    if (other == null || other == mine || !other.gameObject.activeInHierarchy || other.transform.IsChildOf(host.transform)) continue;
                    var group = other.GetComponent<CanvasGroup>();
                    if (group != null && group.alpha < 0.05f) continue;
                    if (a.Overlaps(Angular(other, head)))
                    {
                        first = mine.name + " x " + other.name;
                        return false;
                    }
                }
            }
            return true;
        }

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

        // 3a. Stand, walk away: Away. Every window is still there, at the same world position.
        static IEnumerator<float> StepWalkAway()
        {
            if (!Ready()) { Check(false, "the OS shell is in the scene"); yield break; }
            homePositions.Clear();
            for (int i = 0; i < shell.Windows.All.Count; i++)
            {
                OSWindow w = shell.Windows.All[i];
                if (w != null && !w.IsGroup && w.Root != null) homePositions[w.Id] = CenterOf(w);
            }
            hadArtifactHome = ArtifactOpen();
            if (hadArtifactHome) artifactHome = ArtifactPosition();
            Check(homePositions.Count >= 2, $"{homePositions.Count} OS windows are open before walking away");

            // Stand up, then put the rig 2 m behind where it sat, turned to face away from the desk, and let the desk go.
            var presence = workspace.presence;
            presence.forceSeated = false;
            rig.ToggleSeat();
            yield return 1.0f;
            Vector3 back = -Vector3.ProjectOnPlane(presence.Frame.forward, Vector3.up).normalized;
            if (back.sqrMagnitude < 0.5f) back = -rig.transform.forward;
            var body = rig.GetComponent<CharacterController>();
            if (body != null) body.enabled = false;  // a CharacterController undoes a teleport
            rig.transform.position += back * 2.0f;
            if (body != null) body.enabled = true;
            rig.LookAt(rig.Head.position + back * 2f);
            foreach (float w in Until(() => !presence.AtDesk, 5f)) yield return w;
            Check(!presence.AtDesk && !shell.AtDesk, "the user is Away from the desk (state " + presence.State + ")");
            yield return 0.5f;
            Check(tableButton == null || !tableButton.Shown, "the table button is hidden away from the desk");
            // Turn round to look at the desk for the picture: the windows are still where they were; then face away again.
            Vector3 deskPoint = presence.Frame.position + Vector3.up * 0.5f;
            rig.LookAt(deskPoint);
            yield return 0.6f;
            Shot("away_windows_stay");
            rig.LookAt(rig.Head.position + back * 2f);
            yield return 0.3f;

            int missing = 0, moved = 0;
            string worst = null;
            foreach (var pair in homePositions)
            {
                OSWindow w = shell.Windows.Find(pair.Key);
                if (w == null || w.Root == null) { missing++; worst = pair.Key + " is gone"; continue; }
                float drift = Vector3.Distance(CenterOf(w), pair.Value);
                if (drift > StayMeters) { moved++; worst = $"{pair.Key} moved {drift:F2} m"; }
            }
            Check(missing == 0 && moved == 0, "every OS window is still there at the same world position after walking away" + (worst != null ? " (" + worst + ")" : ""));
            if (hadArtifactHome)
            {
                Check(ArtifactOpen(), "the artifact is still open after walking away");
                float drift = Vector3.Distance(ArtifactPosition(), artifactHome);
                Check(drift <= StayMeters, $"the artifact stayed where it was ({drift:F2} m)");
            }
        }

        // 3b. Away from the desk the corner button shows low in the corner of the view, opens the menu when pressed, and hides
        // while the menu is open (the numbers are checked by OSCornerTest).
        static IEnumerator<float> StepCornerButton()
        {
            if (!Ready()) { Check(false, "the OS shell is in the scene"); yield break; }
            var corner = shell.GetComponent<OSCornerButton>();
            Check(corner != null, "the shell has the corner button");
            if (corner == null) yield break;
            foreach (float w in Until(() => corner.Shown, 2f)) yield return w;
            Check(corner.Shown, "away from the desk the corner button shows");
            if (corner.Shown)
            {
                float metres = Vector3.Distance(corner.Position, shell.Head.position);
                Check(metres > 0.40f && metres < 0.50f, $"it sits about 0.45 m from the eyes ({metres:F2} m)");
                yield return 0.3f;
                Shot("corner_button_away");
                bool pressed = PressButton(corner.Button.gameObject, "corner button");
                if (pressed)
                {
                    foreach (float w in Until(() => shell.MenuOpen, 1.5f)) yield return w;
                    Check(shell.MenuOpen && shell.LastSummon == OSSummon.Corner, "pressing the corner button opens the menu (" + shell.LastSummon + ")");
                    yield return 0.3f;
                    Check(!corner.Shown, "the corner button hides while the menu is open");
                }
            }
            if (shell.MenuOpen) shell.CloseMenu();
            yield return 0.4f;
            Check(corner.Shown, "the corner button is back once the menu has closed");
        }

        // 3c. Away: M opens the menu; Bring windows here puts every window within reach and in view.
        static IEnumerator<float> StepBringHere()
        {
            if (!Ready()) { Check(false, "the OS shell is in the scene"); yield break; }
            if (shell.MenuOpen) shell.CloseMenu();
            yield return 0.4f;

            OSInput.Tap(Key.M);
            foreach (float w in Until(() => shell.MenuOpen, 2f)) yield return w;
            Check(shell.MenuOpen, "the M key opens the menu away from the desk");
            if (!shell.MenuOpen) shell.OpenMenu(OSSummon.Key);  // recover
            else Check(shell.LastSummon == OSSummon.Key, "the menu says the key opened it (" + shell.LastSummon + ")");
            foreach (float w in Until(() => !OSInput.Busy, 1f)) yield return w;
            yield return 0.6f;
            if (menu != null && shell.Head != null)
            {
                int blocked = OSMenu.Blocked(shell.Head.position, menu.LastCenter, menu.LastRotation, menu.SizeMeters);
                Check(blocked == 0, $"no window is in front of any part of the menu away from the desk ({blocked} of 28 plate points are behind one)");
            }
            Shot("menu_away");

            // How far each window is before the press, to show the press moved them.
            Transform head = OSShell.FindHead();
            float farBefore = 0f;
            for (int i = 0; i < shell.Windows.All.Count; i++)
            {
                OSWindow w = shell.Windows.All[i];
                if (w == null || w.IsGroup || w.Root == null) continue;
                farBefore = Mathf.Max(farBefore, Vector3.Distance(CenterOf(w), head.position));
            }

            PressButton("Bring windows here", menu);
            foreach (float w in Until(() => !shell.MenuOpen, 1.5f)) yield return w;
            if (shell.MenuOpen)
            {
                Check(false, "Bring windows here closed the menu");
                shell.Windows.BringHere();  // recover
                shell.CloseMenu();
            }
            yield return 0.6f;

            int counted = 0, outOfReach = 0, outOfView = 0;
            float nearest = float.MaxValue, farthest = 0f, widest = 0f;
            for (int i = 0; i < shell.Windows.All.Count; i++)
            {
                OSWindow w = shell.Windows.All[i];
                if (w == null || w.IsGroup || w.Root == null || !w.gameObject.activeInHierarchy) continue;
                counted++;
                Vector3 offset = CenterOf(w) - head.position;
                Vector3 flatOffset = Vector3.ProjectOnPlane(offset, Vector3.up), flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                float distance = offset.magnitude, angle = Vector3.Angle(flatForward, flatOffset);
                nearest = Mathf.Min(nearest, distance);
                farthest = Mathf.Max(farthest, distance);
                widest = Mathf.Max(widest, angle);
                if (distance > ReachMeters) outOfReach++;
                if (angle > ViewDegrees) outOfView++;
            }
            Check(counted >= 2, $"{counted} OS windows are open to bring here");
            Check(outOfReach == 0, $"every window is within {ReachMeters:F1} m of the user after Bring windows here (nearest {nearest:F2} m, farthest {farthest:F2} m)");
            Check(outOfView == 0, $"every window is in front of the user after Bring windows here (widest {widest:F0} degrees to the side of straight ahead, limit {ViewDegrees:F0})");
            Check(farBefore > farthest + 0.3f, $"the press really moved them (farthest was {farBefore:F2} m, now {farthest:F2} m)");
            Check(!shell.MenuOpen, "the menu is closed after Bring windows here");
            if (ArtifactOpen())
            {
                // Informational: an artifact is reached by Bring here only once the artifacts part adopts it as an OSWindow.
                bool tracked = false;
                for (int i = 0; i < shell.Windows.All.Count; i++)
                    if (shell.Windows.All[i] != null && shell.Windows.All[i].Volume != null) tracked = true;
                Debug.Log("[OSPlayTest] info the open artifact " + (tracked ? "is" : "is not") + " tracked as an OS window (distance " +
                          Vector3.Distance(ArtifactPosition(), head.position).ToString("F2") + " m)");
            }
            Shot("brought_here");
        }

        // 3d. The menu is the system surface. With a mode open away from a desk (Train is the worst case: its panels reach
        // nearest) the menu opens in front of every panel and a press on a tile reaches the tile, so a second tool opens.
        static IEnumerator<float> StepMenuOverMode()
        {
            if (!Ready()) { Check(false, "the OS shell is in the scene"); yield break; }
            if (shell.MenuOpen) shell.CloseMenu();
            shell.Windows.Close("notebook");  // set-up: the Notebook was opened in step 2b, so close it to open it again from the menu
            yield return 0.6f;
            Check(!shell.Windows.IsOpen("notebook"), "set-up: the Notebook window is closed");
            Check(!shell.AtDesk, "still away from the desk");

            foreach (float w in PressTile(OSTab.Tools, e => e.id == "train", "the Train tool")) yield return w;
            if (pressedEntry == null) RecoverOpen("tool", "train");
            foreach (float w in Until(() => OSModes.IsOpen("ml"), 6f)) yield return w;
            Check(OSModes.IsOpen("ml"), "the Train mode opened away from a desk (" + (workspace.ActiveMode ?? "none") + ")");
            var slots = new List<Workspace.ItemSlot>();
            foreach (float w in Until(() => { workspace.GetSlots(slots); return slots.Count >= 3; }, 6f)) yield return w;
            yield return 1.2f;  // the panels unfold
            workspace.GetSlots(slots);
            Transform head = OSShell.FindHead();
            float nearest = float.MaxValue;
            for (int i = 0; i < slots.Count; i++) nearest = Mathf.Min(nearest, Vector3.Distance(slots[i].center, head.position));
            Check(slots.Count >= 3, $"Train has its panels open ({slots.Count}, the nearest centre is {nearest:F2} m from the eyes)");
            Shot("train_open_away");
            int panelRenderersBefore = CountPanelRenderers();

            // The menu again. Every press is checked by the pointer path: the first thing under the pointer must be the tile.
            foreach (float w in EnsureMenuOpen()) yield return w;
            Check(shell.MenuOpen && menu.Visible, "the menu opened over the open Train mode");
            yield return 0.4f;
            float menuDistance = Vector3.Distance(menu.LastCenter, head.position);
            Check(menuDistance <= OSSettings.MaxDistance + 0.02f, $"the menu is within comfortable reach ({menuDistance:F2} m)");
            // The pointer press below only proves the first hit is a tile; this proves nothing is in front of any part of the plate.
            int blockedNow = OSMenu.Blocked(head.position, menu.LastCenter, menu.LastRotation, menu.SizeMeters, meshes: false);
            int meshesNow = OSMenu.Blocked(head.position, menu.LastCenter, menu.LastRotation, menu.SizeMeters);
            Debug.Log($"[OSPlayTest] info {meshesNow} of 28 plate points have a model's solids in front of them; the open menu switched off {menu.HiddenContentCount} model part(s)");
            Check(meshesNow == 0 || menu.HiddenContentCount > 0, $"solids in front of the plate ({meshesNow} points) are switched off while the menu is open ({menu.HiddenContentCount} parts)");
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            Vector3 defaultDirection = Quaternion.AngleAxis(OSSettings.Current.menuDropDegrees, new Vector3(flat.z, 0f, -flat.x)) * flat;
            int blockedDefault = OSMenu.Blocked(head.position, head.position + defaultDirection * OSSettings.Current.menuDistance,
                Quaternion.LookRotation(defaultDirection, Vector3.up), menu.SizeMeters);
            Debug.Log($"[OSPlayTest] info at the default place ({OSSettings.Current.menuDistance:F2} m, straight ahead) {blockedDefault} of 28 plate points would be behind a panel; the menu sits at {menuDistance:F2} m with {blockedNow} behind one");
            float offAxis = Vector3.Angle(head.forward, menu.LastCenter - head.position);
            Check(offAxis <= 75f, $"the menu is where the user can look at it ({offAxis:F0} degrees from where they face, limit 75)");
            Check(blockedNow == 0, $"no panel is in front of any part of the menu ({blockedNow} of 28 plate points are behind one)");
            rig.LookAt(menu.LastCenter);  // the user looks at the menu that just opened
            yield return 0.4f;
            Shot("menu_over_train");
            // Nothing in the scene may be drawn over the menu: compare each tile's pixels with every scene renderer hidden.
            TileCover cover = MeasureTileCover();
            Check(cover.tiles > 0 && cover.covered == 0,
                $"nothing is drawn over the menu: {cover.covered} of {cover.tiles} tiles are covered by scene content (worst: {cover.worstName}, {cover.worst * 100f:F1}% of its pixels differ)");
            if (cover.covered > 0) DescribeRenderersOverMenu();
            // The measure has to be able to see it: with the menu drawn at order 0 (the old way) the same measure, as information.
            SetMenuSorting(0);
            TileCover control = MeasureTileCover();
            SetMenuSorting(OSBody.SystemSortingOrder);
            Debug.Log($"[OSPlayTest] info control: with the menu at sorting order 0, {control.covered} of {control.tiles} tiles are covered (worst {control.worstName}, {control.worst * 100f:F1}%)");

            foreach (float w in PressTile(OSTab.Tools, e => e.id == "notebook", "the Notebook tool, over the open Train mode")) yield return w;
            if (pressedEntry == null) RecoverOpen("tool", "notebook");
            foreach (float w in Until(() => shell.Windows.IsOpen("notebook"), 4f)) yield return w;
            Check(shell.Windows.IsOpen("notebook"), "a second tool opened from the menu while Train was open (Notebook)");
            Check(menu.HiddenContentCount == 0, "the model parts the menu hid are back when it closes (" + menu.HiddenContentCount + " still hidden)");
            Check(CountPanelRenderers() >= panelRenderersBefore, $"every renderer of the panels is enabled again after the menu closed ({CountPanelRenderers()} now, {panelRenderersBefore} before)");
            Check(OSModes.IsOpen("ml"), "Train is still open beside it");
            yield return 0.8f;
            Shot("train_still_open");
            OSWindow notebookWindow = shell.Windows.Find("notebook");
            if (notebookWindow != null && notebookWindow.Root != null) rig.LookAt(CenterOf(notebookWindow));  // the user looks at what opened
            yield return 0.4f;
            Shot("notebook_opened");
        }

        // For a failure: the scene renderers whose bounds are on screen over the menu plate, nearest first, with what decides how
        // they are drawn (the menu is at menuDistance from the eyes).
        static void DescribeRenderersOverMenu()
        {
            Camera cam = Forge.Pet.PetUser.HeadCamera;
            if (cam == null || menu == null) return;
            Vector3 eye = cam.transform.position;
            float menuDistance = Vector3.Distance(eye, menu.LastCenter);
            var corners = new Vector3[8];
            var list = new List<(float d, string text)>();
            foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude))
            {
                if (r == null || !r.enabled || r.transform.IsChildOf(menu.transform) || !r.isVisible) continue;
                Bounds b = r.bounds;
                float nearest = Mathf.Sqrt(b.SqrDistance(eye));
                Vector3 vp = cam.WorldToViewportPoint(b.center);
                if (vp.z <= 0f || vp.x < 0.1f || vp.x > 0.9f || vp.y < 0.1f || vp.y > 0.9f) continue;
                Material m = r.sharedMaterial;
                string path = r.name;
                for (Transform t = r.transform.parent; t != null && path.Length < 120; t = t.parent) path = t.name + "/" + path;
                list.Add((nearest, $"{path}: nearest {nearest:F2} m (centre {Vector3.Distance(eye, b.center):F2} m, size {b.size.x:F2}x{b.size.y:F2}x{b.size.z:F2}), menu at {menuDistance:F2} m, queue {(m != null ? m.renderQueue : -1)}, shader {(m != null ? m.shader.name : "none")}, sortingOrder {r.sortingOrder}"));
            }
            list.Sort((x, y) => x.d.CompareTo(y.d));
            for (int i = 0; i < list.Count && i < 12; i++) Debug.Log("[OSPlayTest] info over the menu: " + list[i].text);
        }

        // Enabled, not force-hidden renderers (meshes) under the showing panels: a mode's 3D stage is among them.
        static int CountPanelRenderers()
        {
            int n = 0;
            foreach (ForgePanel panel in ForgePanel.All)
            {
                if (panel == null || !panel.gameObject.activeInHierarchy) continue;
                foreach (Renderer r in panel.GetComponentsInChildren<Renderer>(false)) if (r != null && r.enabled && !r.forceRenderingOff) n++;
            }
            return n;
        }

        static void SetMenuSorting(int order)
        {
            foreach (Canvas canvas in menu.GetComponentsInChildren<Canvas>(true))
            {
                if (!canvas.isRootCanvas) canvas.overrideSorting = true;
                canvas.sortingOrder = order;
            }
        }

        struct TileCover
        {
            public int tiles, covered;
            public float worst;
            public string worstName;
        }

        // Renders the head camera twice in the same frame, once as is and once with every scene Renderer (meshes, labels,
        // particles) switched off, and compares the middle of each showing tile. The menu is made of canvas graphics, which are
        // not Renderers, so they are in both pictures; a tile whose pixels change when the scene goes is a tile the scene was
        // drawn over (or, if the scene was only behind the plate, showing through it: the tolerance covers a little of that).
        static TileCover MeasureTileCover()
        {
            var result = new TileCover();
            Camera cam = Forge.Pet.PetUser.HeadCamera;
            if (cam == null || menu == null || menu.ActiveTiles.Count == 0) return result;
            const int W = 1280, H = 800;
            Texture2D with = OSShot.Capture(W, H);
            if (with == null) return result;
            var hidden = new List<Renderer>();
            Texture2D without = null;
            try
            {
                foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude))
                {
                    if (r == null || !r.enabled || r.transform.IsChildOf(menu.transform)) continue;
                    r.enabled = false;
                    hidden.Add(r);
                }
                without = OSShot.Capture(W, H);
            }
            finally
            {
                foreach (Renderer r in hidden) if (r != null) r.enabled = true;
            }
            if (without == null) { UnityEngine.Object.DestroyImmediate(with); return result; }
            Color32[] a = with.GetPixels32(), b = without.GetPixels32();
            UnityEngine.Object.DestroyImmediate(with);
            UnityEngine.Object.DestroyImmediate(without);

            var corners = new Vector3[4];
            IReadOnlyList<OSTileView> tiles = menu.ActiveTiles;
            for (int t = 0; t < tiles.Count; t++)
            {
                if (tiles[t] == null || !tiles[t].gameObject.activeInHierarchy) continue;
                tiles[t].rect.GetWorldCorners(corners);
                float minX = 1f, maxX = 0f, minY = 1f, maxY = 0f;
                bool visible = true;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 v = cam.WorldToViewportPoint(corners[i]);
                    if (v.z <= 0f) visible = false;
                    minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                    minY = Mathf.Min(minY, v.y); maxY = Mathf.Max(maxY, v.y);
                }
                if (!visible) continue;
                float shrinkX = (maxX - minX) * 0.2f, shrinkY = (maxY - minY) * 0.2f;  // the middle of the tile, not its rounded edge
                int x0 = Mathf.Clamp(Mathf.RoundToInt((minX + shrinkX) * W), 0, W - 1), x1 = Mathf.Clamp(Mathf.RoundToInt((maxX - shrinkX) * W), 0, W - 1);
                int y0 = Mathf.Clamp(Mathf.RoundToInt((minY + shrinkY) * H), 0, H - 1), y1 = Mathf.Clamp(Mathf.RoundToInt((maxY - shrinkY) * H), 0, H - 1);
                int count = 0, different = 0;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        Color32 p = a[y * W + x], q = b[y * W + x];
                        int d = Mathf.Max(Mathf.Abs(p.r - q.r), Mathf.Max(Mathf.Abs(p.g - q.g), Mathf.Abs(p.b - q.b)));
                        count++;
                        if (d > 24) different++;
                    }
                }
                if (count == 0) continue;
                result.tiles++;
                float fraction = different / (float)count;
                if (fraction > 0.03f) result.covered++;
                if (fraction >= result.worst)
                {
                    result.worst = fraction;
                    result.worstName = tiles[t].entry != null ? tiles[t].entry.title : tiles[t].name;
                }
            }
            return result;
        }

        // 5. Build something: menu strip, RC truck, two answers, Start; the truck project opens with the new plan saved. On a scratch
        // notebook, so this machine's own projects are never written; the full walk (Cancel, Change, voice) is OSPlannerTest.
        static IEnumerator<float> StepBuild()
        {
            if (!Ready()) { Check(false, "the OS shell is in the scene"); yield break; }
            string root = Path.Combine(tempRoot ?? Application.temporaryCachePath, "notebook");
            Directory.CreateDirectory(root);
            Forge.Notebook.NotebookStore.Open(Forge.Notebook.NotebookStore.DefaultProject, root);
            Forge.OS.OSBuildFlow.VoiceLiveOverride = false;
            shell.Windows.CloseAll();
            yield return 0.5f;
            shell.OpenMenu(OSSummon.Key);
            yield return 0.9f;
            Check(shell.MenuOpen, "the menu opened for Build something");
            if (menu != null) rig.LookAt(menu.LastCenter);
            yield return 0.4f;
            PressButton("Build something", menu);
            yield return 1.0f;
            if (!shell.Windows.IsOpen("build"))
            {
                Check(false, "the Build something button opened the card (recovering with OpenSystem)");
                shell.OpenSystem("build");
                yield return 0.8f;
            }
            OSWindow card = shell.Windows.Find("build");
            Check(card != null && Forge.OS.OSBuildFlow.Current != null, "the Build something card is open on its first question");
            if (card != null)
            {
                foreach (string label in new[] { "RC truck", "Small and quick", "Slalom around my couch" })
                {
                    PressButton(label, card);
                    yield return 0.7f;
                }
                Check(Forge.OS.OSBuildFlow.Current != null && Forge.OS.OSBuildFlow.Current.Planner.Done, "two answers after RC truck lead to the plan card");
                PressButton("Start", card);
                yield return 1.2f;
            }
            Forge.Notebook.NotebookStore.Flush();
            Check(Forge.Notebook.NotebookStore.IsOpen && Forge.Notebook.NotebookStore.ProjectId == "proj_truck", "Start opened proj_truck in the notebook");
            OSWindow project = shell.Windows.Find("project");
            Check(project != null && project.GetComponent<OSProjectWindow>().ProjectId == "proj_truck", "the Project window is open on proj_truck");
            Check(Forge.OS.OSBuildFlow.LastPlanVersion >= 1 && Forge.Notebook.NotebookStore.CurrentPlan != null, "the new plan is saved (version " + Forge.OS.OSBuildFlow.LastPlanVersion + ")");
            Check(File.Exists(Path.Combine(root, "projects", "proj_truck", "plans", "plan_v0001.json")), "plan_v0001.json is on disk in the scratch notebook");
            Check(!shell.Windows.IsOpen("build"), "the card is gone after Start");
            Shot("build_started");

            shell.Windows.CloseAll();
            Forge.Notebook.NotebookStore.Flush();
            Forge.Notebook.NotebookStore.Open(Forge.Notebook.NotebookStore.DefaultProject);  // the app's own notebook again
            Forge.OS.OSProjects.Refresh();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }

        // 9. Close all: nothing is left.
        static IEnumerator<float> StepCloseAll()
        {
            if (!Ready()) { Check(false, "the OS shell is in the scene"); yield break; }
            int before = shell.Windows.Count;
            JObjectResult closed = CallTool("os_windows", "close_all");
            Check(closed.ok, "the os_windows close_all tool answers without an error" + (closed.ok ? "" : ": " + closed.text));
            if (!closed.ok) shell.Windows.CloseAll();  // recover
            yield return 0.5f;
            Check(shell.Windows.Count == 0, $"Close all left no OS windows ({before} before, {shell.Windows.Count} after)");
            int stray = UnityEngine.Object.FindObjectsByType<OSWindow>(FindObjectsInactive.Include).Length;
            Check(stray == 0, "no OSWindow object is left in the scene (" + stray + " found)");
            bool artifactLeft = ArtifactOpen();
            Check(!artifactLeft, "Close all also closed the open artifact" + (artifactLeft ? " (it is still open: the artifacts part has to adopt it into the window manager)" : ""));
            if (artifactLeft) CallTool("artifact_close", null);  // clean up so the editor is left tidy
            Check(!OSModes.IsOpen("ml") && workspace.ActiveMode == null, "Close all also closed the open mode");
            Check(!shell.MenuOpen, "the menu is closed at the end");
            if (workspace.presence.Frame != null) rig.LookAt(workspace.presence.Frame.position + Vector3.up * 0.5f);  // look back at the empty desk
            yield return 0.6f;
            Shot("closed_all");
        }

        // ---------------------------------------------------------------- shared routines

        static void RecoverOpen(string kind, string id)
        {
            string error;
            bool ok = shell.OpenItem(kind, id, out error);
            Debug.Log($"[OSPlayTest] recovering: OpenItem({kind}, {id}) -> {ok} {error}");
        }

        struct JObjectResult
        {
            public bool ok;
            public string text;
        }

        static JObjectResult CallTool(string tool, string action)
        {
            var args = new Newtonsoft.Json.Linq.JObject();
            if (action != null) args["action"] = action;
            Newtonsoft.Json.Linq.JObject reply = AlbieTools.Call(tool, args);
            return new JObjectResult { ok = reply != null && reply["error"] == null, text = reply != null ? reply.ToString(Newtonsoft.Json.Formatting.None) : "null" };
        }

        /// <summary>Opens the menu the way a user would: the table button at a desk, the M key away from one.</summary>
        static IEnumerable<float> EnsureMenuOpen()
        {
            if (shell.MenuOpen) yield break;
            if (shell.AtDesk && tableButton != null && tableButton.Shown && tableButton.Button != null)
                PressButton(tableButton.Button.gameObject, "table button");
            else
            {
                OSInput.Tap(Key.M);
                foreach (float w in Until(() => !OSInput.Busy, 1f)) yield return w;
            }
            foreach (float w in Until(() => shell.MenuOpen, 1.5f)) yield return w;
            if (!shell.MenuOpen)
            {
                Check(false, "the menu opened when asked (recovering with OpenMenu)");
                shell.OpenMenu(OSSummon.Code);
            }
            yield return 0.5f;
        }

        /// <summary>
        /// Presses the tile of the first entry on <paramref name="tab"/> that <paramref name="want"/> accepts: opens the menu if
        /// it is closed, switches to the tab, pages to the tile, and taps it. <see cref="pressedEntry"/> is the entry that was
        /// pressed, or null when it could not be (the failure is already recorded).
        /// </summary>
        static IEnumerable<float> PressTile(OSTab tab, Func<OSEntry, bool> want, string what)
        {
            pressedEntry = null;
            if (menu == null)
            {
                Check(false, what + ": the menu view is not on the shell");
                yield break;
            }
            foreach (float w in EnsureMenuOpen()) yield return w;
            if (shell.Tab != tab)
            {
                PressButton("Tab " + tab, menu);
                yield return 0.35f;
                if (shell.Tab != tab) shell.SetTab(tab);  // recover
                yield return 0.25f;
            }
            var list = new List<OSEntry>();
            OSRegistry.Fill(tab, list);
            OSEntry entry = list.Find(e => want(e));
            if (entry == null)
            {
                Check(false, what + ": no such tile on the " + tab + " tab");
                yield break;
            }
            OSTileView tile = FindTile(entry.id);
            for (int attempt = 0; tile == null && attempt < 6 && menu.Page < menu.PageCount - 1; attempt++)
            {
                if (!PressButton("chevron_right", menu)) break;
                yield return 0.3f;
                tile = FindTile(entry.id);
            }
            if (tile == null)
            {
                Check(false, what + ": the tile '" + entry.id + "' is not on any page of the " + tab + " tab (" + menu.PageCount + " pages)");
                yield break;
            }
            if (PressButton(tile.button.gameObject, "tile " + entry.id)) pressedEntry = entry;
            yield return 0.4f;
        }

        static OSTileView FindTile(string id)
        {
            IReadOnlyList<OSTileView> tiles = menu.ActiveTiles;
            for (int i = 0; i < tiles.Count; i++)
                if (tiles[i] != null && tiles[i].entry != null && tiles[i].entry.id == id && tiles[i].gameObject.activeInHierarchy) return tiles[i];
            return null;
        }

        // The first Artifacts-tab entry that is one of the volume templates (a window-sized model that needs no permission).
        static OSEntry FindVolumeArtifact()
        {
            var list = new List<OSEntry>();
            OSRegistry.Fill(OSTab.Artifacts, list);
            string[] wanted = { "heart", "cell", "brain", "dna", "eye", "double_pendulum", "solar_system" };
            foreach (string name in wanted)
            {
                OSEntry hit = list.Find(e => e.id == name || e.id.EndsWith("_" + name, StringComparison.Ordinal)
                                          || (e.title != null && e.title.IndexOf(name.Replace('_', ' '), StringComparison.OrdinalIgnoreCase) >= 0));
                if (hit != null) return hit;
            }
            return null;
        }

        // ---------------------------------------------------------------- pressing UI the way a user does

        /// <summary>
        /// Presses the control with this label inside <paramref name="scope"/> (the whole scene when null). Controls built by
        /// OSUi are named "Button &lt;label&gt;" and "Tab &lt;label&gt;" (an icon-only button is named after its icon).
        /// </summary>
        static bool PressButton(string label, Component scope)
        {
            Selectable[] all = scope != null
                ? scope.GetComponentsInChildren<Selectable>(false)
                : UnityEngine.Object.FindObjectsByType<Selectable>(FindObjectsInactive.Exclude);
            foreach (Selectable s in all)
            {
                if (s == null) continue;
                if (s.name == "Button " + label || s.name == "Tab " + label || s.name == label) return PressButton(s.gameObject, label);
            }
            Check(false, $"press '{label}': there is no control with that label on screen");
            return false;
        }

        /// <summary>
        /// A real pointer press on a Button or Toggle. It turns the head to the control when it is out of view, projects its
        /// centre to the screen, asks the EventSystem what is under that point (the same RaycastAll the desktop rig uses),
        /// and fails, with what was under the pointer instead, when that is not the control or it is not interactable. Then it
        /// sends pointer enter, down, up, click and exit to what was hit, as DesktopRig does for the mouse. Records a failed
        /// check on failure and returns false; returns true when the click was sent (the caller asserts what it caused).
        /// </summary>
        static bool PressButton(GameObject target, string what = null)
        {
            what = what ?? (target != null ? target.name : "(nothing)");
            if (target == null) return PressFailed(what, "it is not there");
            if (!target.activeInHierarchy) return PressFailed(what, "it is not active (hidden or closed)");
            var selectable = target.GetComponent<Selectable>();
            if (selectable == null) return PressFailed(what, "it has no Button or Toggle on it");
            if (!selectable.IsInteractable()) return PressFailed(what, "it is not interactable");
            EventSystem events = EventSystem.current;
            if (events == null) return PressFailed(what, "there is no EventSystem in the scene");
            Camera cam = Forge.Pet.PetUser.HeadCamera;
            if (cam == null) return PressFailed(what, "there is no head camera");

            var rect = target.transform as RectTransform;
            Vector3 world = rect != null ? rect.TransformPoint(rect.rect.center) : target.transform.position;
            Vector3 view = cam.WorldToViewportPoint(world);
            bool inView = view.z > 0.1f && view.x > 0.08f && view.x < 0.92f && view.y > 0.08f && view.y < 0.92f;
            if (!inView && rig != null)
            {
                rig.LookAt(world);  // a user looks at what they press
                view = cam.WorldToViewportPoint(world);
            }
            if (view.z <= 0f) return PressFailed(what, "it is behind the user");
            Vector3 screen3 = cam.WorldToScreenPoint(world);
            var screen = new Vector2(screen3.x, screen3.y);

            var pointer = new PointerEventData(events) { button = PointerEventData.InputButton.Left, position = screen, pressPosition = screen };
            var hits = new List<RaycastResult>();
            events.RaycastAll(pointer, hits);
            float reach = rig != null ? rig.reach : 4f;
            RaycastResult hit = default;
            foreach (RaycastResult h in hits)
            {
                if (h.gameObject != null && h.distance <= reach) { hit = h; break; }
            }
            if (hit.gameObject == null)
                return PressFailed(what, $"nothing is under the pointer at screen ({screen.x:F0}, {screen.y:F0}); {hits.Count} raycast results, none within {reach:F1} m");
            if (!hit.gameObject.transform.IsChildOf(target.transform))
                return PressFailed(what, $"it is not under the pointer: at screen ({screen.x:F0}, {screen.y:F0}) the pointer hit {PathOf(hit.gameObject.transform)} ({hit.distance:F2} m)");

            pointer.pointerCurrentRaycast = hit;
            pointer.pointerPressRaycast = hit;
            ExecuteEvents.ExecuteHierarchy(hit.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            GameObject pressed = ExecuteEvents.ExecuteHierarchy(hit.gameObject, pointer, ExecuteEvents.pointerDownHandler)
                                 ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            pointer.pointerPress = pressed;
            pointer.rawPointerPress = hit.gameObject;
            pointer.eligibleForClick = true;
            if (pressed != null)
            {
                ExecuteEvents.Execute(pressed, pointer, ExecuteEvents.pointerUpHandler);
                if (ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject) == pressed)
                    ExecuteEvents.Execute(pressed, pointer, ExecuteEvents.pointerClickHandler);
            }
            ExecuteEvents.ExecuteHierarchy(hit.gameObject, pointer, ExecuteEvents.pointerExitHandler);
            Debug.Log($"[OSPlayTest] press {what}: pointer at ({screen.x:F0}, {screen.y:F0}) hit {PathOf(hit.gameObject.transform)}");
            return true;
        }

        static bool PressFailed(string what, string why)
        {
            Check(false, $"press '{what}': {why}");
            return false;
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 5; i++, t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }
    }
}
