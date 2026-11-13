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
            // 5 build something (OSPlanner / OSBuildFlow part)
            // 6 truck project   (Course, Drive pad laps, Train, Autopilot)
            // 7 agents          (spawn, bring home, send out)
            // 8 settings        (corner button off, it is gone)
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
            Shot("three_open");
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

        // 3b. STUB. The corner button comes with its own part (spec 5.12): away from a desk it appears low in the corner of
        // the view and opens the menu, and it hides while the menu is open. When OSCornerButton exists, replace the Skip with:
        // wait until it is Shown, Check it appeared, PressButton(its Button), Check shell.MenuOpen and LastSummon == Corner,
        // Check it hides while the menu is open, then shell.CloseMenu().
        static IEnumerator<float> StepCornerButton()
        {
            Skip("corner button", "skipped: corner button not built yet");
            yield break;
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

            // The menu again. Every press is checked by the pointer path: the first thing under the pointer must be the tile.
            foreach (float w in EnsureMenuOpen()) yield return w;
            Check(shell.MenuOpen && menu.Visible, "the menu opened over the open Train mode");
            yield return 0.4f;
            float menuDistance = Vector3.Distance(menu.LastCenter, head.position);
            Check(menuDistance <= OSSettings.MaxDistance + 0.02f, $"the menu is within comfortable reach ({menuDistance:F2} m)");
            // The pointer press below only proves the first hit is a tile; this proves nothing is in front of any part of the plate.
            int blockedNow = OSMenu.Blocked(head.position, menu.LastCenter, menu.LastRotation, menu.SizeMeters);
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

            foreach (float w in PressTile(OSTab.Tools, e => e.id == "notebook", "the Notebook tool, over the open Train mode")) yield return w;
            if (pressedEntry == null) RecoverOpen("tool", "notebook");
            foreach (float w in Until(() => shell.Windows.IsOpen("notebook"), 4f)) yield return w;
            Check(shell.Windows.IsOpen("notebook"), "a second tool opened from the menu while Train was open (Notebook)");
            Check(OSModes.IsOpen("ml"), "Train is still open beside it");
            yield return 0.8f;
            Shot("train_still_open");
            OSWindow notebookWindow = shell.Windows.Find("notebook");
            if (notebookWindow != null && notebookWindow.Root != null) rig.LookAt(CenterOf(notebookWindow));  // the user looks at what opened
            yield return 0.4f;
            Shot("notebook_opened");
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
