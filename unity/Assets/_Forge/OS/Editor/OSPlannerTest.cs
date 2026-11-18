using System;
using System.Collections.Generic;
using System.IO;
using Forge.Artifacts;
using Forge.Notebook;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Run Planner Test: the real Main scene in Play mode on the desktop rig with voice off and the hub off,
    /// on a scratch root (no real notebook, projects or artifact folders are read or written). It walks Build something
    /// with a pointer: the menu strip's Build something button, RC truck, two answers, the plan card, Change, Start (the
    /// truck project opens with plan v1 saved), Cancel from a question, from the plan card and with the window's X (no file
    /// appears or changes), a new project id (a drone, then a second one), the voice rows and spoken answers with the voice
    /// forced live, and Albie walking over. Cards are checked for clipped or overlapping text, finger-sized buttons,
    /// placeholder text and brand names; screenshots go to Logs/os/ (60 to 63). The result is in <see cref="LastResult"/>
    /// and a console line "[OSPlannerTest] N passed, M failed".
    /// </summary>
    [InitializeOnLoad]
    public static class OSPlannerTest
    {
        const string ActiveKey = "OSPlannerTest.Active";
        const string ScenePath = "Assets/_Forge/Core/Main.unity";
        const double GiveUpSeconds = 200;
        static readonly string[] Brands = { "Claude", "Anthropic", "Meta", "Quest", "Grok", "Unity", "OpenAI", "ChatGPT", "Gemini" };

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
        static readonly List<string> madeShots = new List<string>();
        static string scratch;

        static OSPlannerTest()
        {
            if (SessionState.GetBool(ActiveKey, false)) EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static bool keepPlaying
        {
            get => SessionState.GetBool("OSPlannerTest.Keep", false);
            set => SessionState.SetBool("OSPlannerTest.Keep", value);
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

        [MenuItem("Forge/OS/Run Planner Test")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSPlannerTest] Leave Play mode first."); return; }
            OSTestEnv.Enter("OSPlannerTest");
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
            madeShots.Clear();
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
            string summary = $"[OSPlannerTest] {passed} passed, {failed} failed";
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
            OSBuildFlow.VoiceLiveOverride = null;
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
                catch (Exception e) { Debug.Log("[OSPlannerTest] couldn't remove " + scratch + ": " + e.Message); }
                scratch = null;
            }
        }

        static void Check(bool condition, string what)
        {
            Debug.Log($"[OSPlannerTest] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition) { passed++; return; }
            failed++;
            failures.Add(what);
        }

        static void Measure(string line)
        {
            measured.Add(line);
            Debug.Log("[OSPlannerTest] measured: " + line);
        }

        static void Shot(string name)
        {
            string path = OSShot.Take(name);
            if (path != null) madeShots.Add(path);
            Debug.Log("[OSPlannerTest] shot " + (path ?? "(no camera)"));
        }

        // ---------------------------------------------------------------- helpers

        static bool tapOk;
        static string tapWhy;

        // A pointer press on "Button <label>" of the Build something window: looks at the card first, waits for the card to rebuild.
        static IEnumerator<float> Tap(OSShell shell, string label)
        {
            tapOk = false;
            tapWhy = null;
            OSWindow win = shell.Windows.Find(OSBuildFlow.WindowId);
            if (win == null) { tapWhy = "the Build something window is not open"; yield break; }
            DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
            yield return 0.25f;
            Transform t = OSToolsTest.FindDeep(win.transform, "Button " + label);
            if (t == null) { tapWhy = "no 'Button " + label + "' on the card: " + OSToolsTest.AllText(win); yield break; }
            tapOk = OSToolsTest.Press(t.gameObject, out tapWhy);
            if (!tapOk) Shot("99_build_press_failed");
            yield return 0.6f;
        }

        static IEnumerator<float> OpenFromMenu(OSShell shell, OSMenu menu)
        {
            tapOk = false;
            tapWhy = null;
            shell.OpenMenu(OSSummon.Key);
            yield return 0.9f;
            if (!shell.MenuOpen) { tapWhy = "the menu didn't open"; yield break; }
            DesktopRig.Current.LookAt(menu.LastCenter);
            yield return 0.4f;
            Transform b = OSToolsTest.FindDeep(menu.transform, "Button New project");
            if (b == null) { tapWhy = "no Build something button in the menu"; yield break; }
            tapOk = OSToolsTest.Press(b.gameObject, out tapWhy);
            yield return 1.0f;
        }

        static OSWindow BuildWindow(OSShell shell) => shell.Windows.Find(OSBuildFlow.WindowId);

        static string Text(OSShell shell)
        {
            OSWindow w = BuildWindow(shell);
            return w != null ? OSToolsTest.AllText(w) : "(no window)";
        }

        static bool Has(string text, params string[] needles)
        {
            foreach (string n in needles) if (!text.Contains(n)) return false;
            return true;
        }

        static string Missing(string text, params string[] needles)
        {
            foreach (string n in needles) if (!text.Contains(n)) return "'" + n + "' is not on the card: " + text;
            return null;
        }

        static string Brand(string text)
        {
            foreach (string b in Brands) if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b" + b + @"\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return b;
            return null;
        }

        // Every button of the card is at least 22 mm tall and 22 mm wide; returns the smallest height, or -1 with no buttons.
        static float SmallestButton(OSWindow win, out string smallName, out float smallestWidth)
        {
            smallName = null;
            smallestWidth = float.MaxValue;
            float min = float.MaxValue;
            foreach (Button b in win.GetComponentsInChildren<Button>(false))
            {
                if (!b.gameObject.activeInHierarchy) continue;
                var rect = (RectTransform)b.transform;
                float h = OSToolsTest.Mm(rect.rect.height, rect);
                float w = OSToolsTest.Mm(rect.rect.width, rect);
                if (w < smallestWidth) smallestWidth = w;
                if (h < min) { min = h; smallName = b.name; }
            }
            return min == float.MaxValue ? -1f : min;
        }

        static int Count(OSWindow win, string name)
        {
            int n = 0;
            foreach (Button b in win.GetComponentsInChildren<Button>(false)) if (b.name == name) n++;
            return n;
        }

        // Every file under the scratch root with its length, sorted: what "leaves no files behind" compares.
        static string Listing()
        {
            NotebookStore.Flush();
            if (!Directory.Exists(scratch)) return "";
            string[] files = Directory.GetFiles(scratch, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            foreach (string f in files) sb.Append(f.Substring(scratch.Length)).Append('=').Append(new FileInfo(f).Length).Append('\n');
            return sb.ToString();
        }

        static string ProjectsFolder => Path.Combine(scratch, "projects");

        // ---------------------------------------------------------------- the script

        static IEnumerator<float> Script()
        {
            // Everything the run could write (settings, notebook, artifacts, approvals) goes to a folder of its own first; the tests that need their own root move on from here.
            OSTestEnv.UseScratch("osplanner");
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

            // ---- a fresh install: a scratch root with no projects folder, no artifacts, no approvals
            scratch = Path.Combine(Path.GetTempPath(), "os_planner_test");
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
            Directory.CreateDirectory(scratch);
            ArtifactLibrary.RootOverride = Path.Combine(scratch, "artifacts");
            ArtifactApprovals.FileOverride = Path.Combine(scratch, "artifacts", "approvals.json");
            NotebookStore.Open(NotebookStore.DefaultProject, scratch);
            OSProjects.Refresh();
            OSBuildFlow.VoiceLiveOverride = false;  // voice off: only the taps
            Check(!Directory.Exists(ProjectsFolder), "fresh install: there is no projects folder");

            // ---- A. the tree, without the UI
            List<string> problems = OSPlanner.Validate();
            Check(problems.Count == 0, "OSPlanner.Validate() returns no problems" + (problems.Count == 0 ? " (" + OSPlanner.PathCount() + " paths)" : ": " + problems[0]));
            Check(OSPlanner.First.choices.Length == 5 && OSPlanner.First.text == "What are we building?", "the first question has five answers");
            var dry = new OSPlanner();
            dry.Answer(0); dry.Answer(0); dry.Answer(0);
            Check(dry.Done && dry.Asked == 3 && dry.Result != null && dry.Result.IsTruck, "three answers end in a truck plan");
            Check(!dry.Answer(0), "a finished plan takes no fourth answer");
            Check(OSPlanner.Match(OSPlanner.First, "the rocket") == 2 && OSPlanner.Match(OSPlanner.First, "a boat") == -1, "'the rocket' picks Rocket and 'a boat' picks nothing");
            Check(OSPlanner.ParseCommand("start over") == OSPlanner.Command.Change && OSPlanner.ParseCommand("go ahead") == OSPlanner.Command.Start, "'start over' is Change, 'go ahead' is Start");
            Check(AlbieTools.Has("os_answer"), "the os_answer voice tool is registered");
            Check(OSShell.HasSystem("build"), "the Build something page is registered with the shell");

            // ---- B. open from the menu strip with a pointer
            var step = OpenFromMenu(shell, menu);
            while (step.MoveNext()) yield return step.Current;
            Check(tapOk, "a pointer reaches the Build something button in the menu strip: " + tapWhy);
            OSWindow win = BuildWindow(shell);
            Check(win != null && OSBuildFlow.Current != null, "Build something opened its window ('build')");
            Check(!shell.MenuOpen, "the menu closed itself after Build something");
            if (win == null || OSBuildFlow.Current == null)
            {
                OSBuildFlow.Open();  // keep going on the API, so one failed press does not hide the rest
                yield return 0.8f;
                win = BuildWindow(shell);
                if (win == null) yield break;
            }
            DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
            yield return 0.6f;
            string text = Text(shell);
            string missing = Missing(text, "Question 1 of 3", "What are we building?", "RC truck", "Drone", "Rocket", "A model that recognises things", "Something else", "Cancel");
            Check(missing == null, "question 1 shows the question, five answers and Cancel" + (missing != null ? ": " + missing : ""));
            Check(!text.Contains("Here is the plan") && !text.Contains("Start"), "question 1 is not the plan card");
            Check(Count(win, "Button Something else") == 1, "'Something else' is there once, as the fifth answer, and no voice row (voice off)");
            float smallest = SmallestButton(win, out string smallName, out float smallestW);
            Measure("question 1: smallest button " + smallName + " " + smallest.ToString("F1") + " mm tall, narrowest " + smallestW.ToString("F1") + " mm wide");
            Check(smallest >= 22f && smallestW >= 22f, "every button on question 1 is at least 22 mm (smallest " + smallest.ToString("F1") + " mm, " + smallName + ")");
            string layout = OSToolsTest.LayoutProblem(win);
            Check(layout == null, "question 1: no clipped or overlapping text, no placeholder text" + (layout != null ? ": " + layout : ""));
            Check(Brand(text) == null, "question 1 has no brand names" + (Brand(text) != null ? ": " + Brand(text) : ""));
            Shot("60_build_q1");
            Check(!Directory.Exists(ProjectsFolder), "nothing is created while the first question is on screen");
            string untouched = Listing();

            // ---- C. RC truck, two answers, the plan card
            step = Tap(shell, "RC truck");
            while (step.MoveNext()) yield return step.Current;
            Check(tapOk, "a pointer reaches 'RC truck': " + tapWhy);
            text = Text(shell);
            missing = Missing(text, "Question 2 of 3", "How big and how fast should the truck be?", "Small and quick", "Big and steady", "Somewhere in between");
            Check(missing == null, "question 2 is the truck's size question with three answers" + (missing != null ? ": " + missing : ""));
            layout = OSToolsTest.LayoutProblem(BuildWindow(shell));
            Check(layout == null, "question 2: no clipped or overlapping text" + (layout != null ? ": " + layout : ""));
            Shot("60_build_q2");
            step = Tap(shell, "Small and quick");
            while (step.MoveNext()) yield return step.Current;
            Check(tapOk, "a pointer reaches 'Small and quick': " + tapWhy);
            text = Text(shell);
            missing = Missing(text, "Question 3 of 3", "How do we test it?", "Slalom around my couch", "Dash across the room", "Park on a marked spot");
            Check(missing == null, "question 3 asks how it is tested" + (missing != null ? ": " + missing : ""));
            step = Tap(shell, "Slalom around my couch");
            while (step.MoveNext()) yield return step.Current;
            Check(tapOk, "a pointer reaches 'Slalom around my couch': " + tapWhy);
            win = BuildWindow(shell);
            yield return 0.4f;
            DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
            yield return 0.4f;
            text = Text(shell);
            missing = Missing(text, "Here is the plan", "Goal", "Build a self-driving RC truck that is small and quick.", "Parts", "The truck and its controller",
                "Test", "Drive the slalom around my couch", "Milestones", "1. Set up the course", "4. The autopilot passes the slalom around my couch",
                "Start", "Change", "Cancel", "Nothing is saved until you tap Start.");
            Check(missing == null, "the plan card shows goal, parts, test, four milestones, Start, Change, Cancel and the note" + (missing != null ? ": " + missing : ""));
            layout = OSToolsTest.LayoutProblem(win);
            Check(layout == null, "the plan card: no clipped or overlapping text, no placeholder text" + (layout != null ? ": " + layout : ""));
            smallest = SmallestButton(win, out smallName, out smallestW);
            Measure("plan card: smallest button " + smallName + " " + smallest.ToString("F1") + " mm tall, narrowest " + smallestW.ToString("F1") + " mm wide");
            Check(smallest >= 22f && smallestW >= 22f, "every button on the plan card is at least 22 mm (smallest " + smallest.ToString("F1") + " mm tall, " + smallestW.ToString("F1") + " mm wide)");
            Check(Brand(text) == null, "the plan card has no brand names" + (Brand(text) != null ? ": " + Brand(text) : ""));
            Shot("61_build_plan");
            Check(Listing() == untouched && !Directory.Exists(ProjectsFolder), "the plan card has written nothing: the scratch root is exactly as it was");

            // ---- D. Change: back to question 1, keeping nothing
            step = Tap(shell, "Change");
            while (step.MoveNext()) yield return step.Current;
            Check(tapOk, "a pointer reaches Change: " + tapWhy);
            text = Text(shell);
            Check(Has(text, "What are we building?", "Question 1 of 3") && OSBuildFlow.Current != null && OSBuildFlow.Current.Planner.Asked == 0 && OSBuildFlow.LastStep == "changed",
                "Change goes back to question 1 with nothing kept (asked " + (OSBuildFlow.Current != null ? OSBuildFlow.Current.Planner.Asked : -1) + ", step " + OSBuildFlow.LastStep + ")");
            foreach (string label in new[] { "RC truck", "Small and quick", "Slalom around my couch" })
            {
                step = Tap(shell, label);
                while (step.MoveNext()) yield return step.Current;
                Check(tapOk, "a pointer reaches '" + label + "' again: " + tapWhy);
            }
            Check(Has(Text(shell), "Here is the plan"), "the same three answers give the plan card again");

            // ---- E. Start
            step = Tap(shell, "Start");
            while (step.MoveNext()) yield return step.Current;
            Check(tapOk, "a pointer reaches Start: " + tapWhy);
            yield return 1.2f;
            Check(NotebookStore.IsOpen && NotebookStore.ProjectId == "proj_my_truck", "Start made the person's own truck project, not the sample, and opened it (" + NotebookStore.ProjectId + ")");
            Check(OSProjects.TitleFor("proj_my_truck") == "My RC truck" && OSProjects.CopyOf("proj_my_truck") == NotebookStore.DefaultProject && OSProjectWindow.LoopOf("proj_my_truck") == OSProjectWindow.Loop.Truck,
                "it is 'My RC truck' (" + OSProjects.TitleFor("proj_my_truck") + "), with the truck's loop");
            Check(!File.Exists(Path.Combine(ProjectsFolder, "proj_truck", "plans", "plan_v0001.json")), "the RC truck sample got no plan from Start");
            Check(!windows.IsOpen("build") && OSBuildFlow.Current == null, "the Build something card closed");
            OSWindow pw = windows.Find(OSProjectWindow.WindowId);
            OSProjectWindow view = pw != null ? pw.GetComponent<OSProjectWindow>() : null;
            Check(view != null && view.ProjectId == "proj_my_truck", "the Project window is open on proj_my_truck");
            if (pw != null)
            {
                DesktopRig.Current.LookAt(OSToolsTest.CenterOf(pw.Panel.transform));
                yield return 0.6f;
                string ptext = OSToolsTest.AllText(pw);
                // The overview: the goal and the big first step; the suggestion that only repeats it stays in the status, not on screen.
                missing = Missing(ptext, "Build a self-driving RC truck that is small and quick.", "Next: Course");
                Check(missing == null, "the Project window shows its goal and the first step as its big button" + (missing != null ? ": " + missing : ""));
                Check(view != null && view.StatusLine.StartsWith("Next step: tap Course", StringComparison.Ordinal), "Start offered the first step (" + (view != null ? view.StatusLine : "no window") + ")");
                Check(!ptext.Contains("No plan yet"), "the Project window no longer says there is no plan");
                layout = OSToolsTest.LayoutProblem(pw);
                Check(layout == null, "the Project window with the new plan: no clipped or overlapping text" + (layout != null ? ": " + layout : ""));
                Shot("62_build_started");
                if (view != null)
                {
                    view.DetailsOpen = true;
                    yield return 0.4f;
                    ptext = OSToolsTest.AllText(pw);
                    Check(ptext.Contains("Plan v1") && !ptext.Contains("No plan written yet"), "its Details page shows plan v1");
                    layout = OSToolsTest.LayoutProblem(pw);
                    Check(layout == null, "the new plan's Details page: no clipped or overlapping text" + (layout != null ? ": " + layout : ""));
                    view.DetailsOpen = false;
                    yield return 0.3f;
                }
            }
            NotebookStore.Flush();
            string planFile = Path.Combine(ProjectsFolder, "proj_my_truck", "plans", "plan_v0001.json");
            Check(File.Exists(planFile), "plan_v0001.json is on disk under proj_my_truck");
            if (File.Exists(planFile))
            {
                JObject plan = JObject.Parse(File.ReadAllText(planFile));
                Check((string)plan["schema"] == "albie.plan/1" && (int)plan["version"] == 1, "the saved plan has the notebook's schema and version 1");
                Check(((string)plan["goal"]).Contains("small and quick") && ((string)plan["test"]).Contains("slalom around my couch"), "the saved plan has the goal and the test");
                var answers = plan["answers"] as JArray;
                Check(answers != null && answers.Count == 3 && (string)answers[0]["answer"] == "RC truck" && (string)answers[1]["answer"] == "Small and quick" && (string)answers[2]["answer"] == "Slalom around my couch",
                    "the saved plan records the three answers");
                Check(plan["parts"] is JArray pa && pa.Count >= 4, "the saved plan has its parts");
                Check(plan["milestones"] is JArray ma && ma.Count == 4 && (bool)ma[0]["done"] == false, "the saved plan has four milestones, none done");
            }
            JObject post = null;
            foreach (JObject entry in NotebookStore.Entries) if ((string)entry["kind"] == "postmortem") post = entry;
            Check(post != null && post.ToString().Contains("Small and quick"), "the notebook's postmortem entry holds the answers");
            Check(OSBuildFlow.LastProjectId == "proj_my_truck" && OSBuildFlow.LastPlanVersion == 1 && OSBuildFlow.LastStep == "started",
                "the flow reports proj_my_truck, plan 1, started (" + OSBuildFlow.LastProjectId + ", " + OSBuildFlow.LastPlanVersion + ", " + OSBuildFlow.LastStep + ")");

            // ---- F. Cancel leaves the scratch root exactly as it was
            windows.CloseAll();
            yield return 0.5f;
            string before = Listing();
            Check(before.Length > 0, "the scratch root holds the truck project now (the baseline for Cancel)");
            OSProjects.Refresh();
            step = OpenFromMenu(shell, menu);
            while (step.MoveNext()) yield return step.Current;
            Check(tapOk && BuildWindow(shell) != null, "Build something opens again from the menu: " + tapWhy);
            step = Tap(shell, "Drone");
            while (step.MoveNext()) yield return step.Current;
            Check(Has(Text(shell), "What should the drone do first?"), "Drone leads to the drone question");
            step = Tap(shell, "Cancel");
            while (step.MoveNext()) yield return step.Current;
            Check(tapOk, "a pointer reaches Cancel on a question: " + tapWhy);
            yield return 0.5f;
            Check(!windows.IsOpen("build") && OSBuildFlow.Current == null && OSBuildFlow.LastStep == "cancelled", "Cancel on a question closes the card (" + OSBuildFlow.LastStep + ")");
            Check(Listing() == before, "Cancel on a question leaves no file behind and changes none");

            OSBuildFlow.Open();
            yield return 0.8f;
            foreach (string label in new[] { "Drone", "Hover steadily", "Hover for one minute" })
            {
                step = Tap(shell, label);
                while (step.MoveNext()) yield return step.Current;
            }
            Check(Has(Text(shell), "Here is the plan", "Build a small drone that hovers steadily."), "the drone plan card shows its goal");
            step = Tap(shell, "Cancel");
            while (step.MoveNext()) yield return step.Current;
            yield return 0.5f;
            Check(!windows.IsOpen("build") && OSBuildFlow.Current == null && OSBuildFlow.LastStep == "cancelled", "Cancel on the plan card closes it");
            Check(Listing() == before && NotebookStore.ProjectId == "proj_my_truck", "Cancel on the plan card leaves no file behind and My RC truck open");

            OSBuildFlow.Open();
            yield return 0.8f;
            step = Tap(shell, "Rocket");
            while (step.MoveNext()) yield return step.Current;
            BuildWindow(shell)?.Close();  // the window's own X
            yield return 0.5f;
            Check(!windows.IsOpen("build") && OSBuildFlow.Current == null && OSBuildFlow.LastStep == "cancelled", "closing the window with its X ends the flow as cancelled");
            Check(Listing() == before, "closing with the X leaves no file behind");

            // ---- G0. a Start that fails keeps nothing: no folder, no file, the project that was open is open again
            {
                OSBuildFlow.Open();
                yield return 0.8f;
                foreach (string label in new[] { "Drone", "Hover steadily", "Hover for one minute" })
                {
                    step = Tap(shell, label);
                    while (step.MoveNext()) yield return step.Current;
                }
                OSBuildFlow flow = OSBuildFlow.Current;
                OSPlanner.Plan failing = flow != null ? flow.Planner.Result : null;
                Check(failing != null, "the drone plan card is up");
                if (failing != null)
                {
                    string keepReason = failing.reason;
                    string newFolder = Path.Combine(ProjectsFolder, "proj_my_" + failing.projectBase);
                    failing.reason = "";  // RevisePlan refuses a plan with no reason
                    string error;
                    bool started = flow.StartBuild(out error);
                    Check(!started && !string.IsNullOrEmpty(error), "Start with a plan the notebook refuses returns false with a sentence: " + error);
                    Check(!Directory.Exists(newFolder) && Listing() == before && NotebookStore.ProjectId == "proj_my_truck", "...and nothing was kept: no folder, no file, My RC truck is open again (" + NotebookStore.ProjectId + ")");
                    Check(OSBuildFlow.Current == flow && windows.IsOpen("build"), "...and the card is still open, so the user can go on");
                    // The notebook made the folder before the plan failed: Start takes down what it made.
                    OSBuildFlow.OpenedForTests = f => { Directory.CreateDirectory(f); File.WriteAllText(Path.Combine(f, "stray.txt"), "x"); };
                    started = flow.StartBuild(out error);
                    OSBuildFlow.OpenedForTests = null;
                    Check(!started, "Start fails again with the folder made first");
                    Check(!Directory.Exists(newFolder) && Listing() == before && NotebookStore.ProjectId == "proj_my_truck", "...the folder this Start made is removed and My RC truck is open again");
                    // A truck plan now makes a second project of the person's own (proj_my_truck_2): a failed one never touches the first.
                    failing.kind = OSPlanner.Kind.Truck;
                    string truckFolder = Path.Combine(ProjectsFolder, "proj_my_truck");
                    Check(Directory.Exists(truckFolder), "My RC truck's folder exists from before");
                    started = flow.StartBuild(out error);
                    Check(!started && Directory.Exists(truckFolder) && !Directory.Exists(Path.Combine(ProjectsFolder, "proj_my_truck_2")) && Listing() == before && NotebookStore.ProjectId == "proj_my_truck",
                        "a failed truck Start keeps My RC truck exactly and leaves no proj_my_truck_2");
                    failing.kind = OSPlanner.Kind.Drone;
                    failing.reason = keepReason;
                }
                step = Tap(shell, "Cancel");
                while (step.MoveNext()) yield return step.Current;
                yield return 0.5f;
                Check(!windows.IsOpen("build") && Listing() == before, "Cancel after the failed Starts leaves the scratch root as it was");
            }

            // ---- G. a drone plan makes the person's own quadcopter project, and a second one makes another (never the sample)
            string[][] droneAnswers = { new[] { "Drone", "Hover steadily", "Hover for one minute" } };
            for (int round = 0; round < 2; round++)
            {
                OSBuildFlow.Open();
                yield return 0.8f;
                foreach (string label in droneAnswers[0])
                {
                    step = Tap(shell, label);
                    while (step.MoveNext()) yield return step.Current;
                }
                step = Tap(shell, "Start");
                while (step.MoveNext()) yield return step.Current;
                yield return 1.2f;
                string want = round == 0 ? "proj_my_quadcopter" : "proj_my_quadcopter_2";
                string wantPlan = "plan_v0001.json";
                Check(NotebookStore.ProjectId == want, "building a drone" + (round == 0 ? "" : " again") + " makes " + want + " (" + NotebookStore.ProjectId + ")");
                Check(!Directory.Exists(Path.Combine(ProjectsFolder, OSProjects.QuadcopterId, "plans")), "the Quadcopter sample got no plan");
                NotebookStore.Flush();
                Check(File.Exists(Path.Combine(ProjectsFolder, want, "plans", wantPlan)), want + " has " + wantPlan + " on disk");
                OSProjects.Refresh();
                yield return 0.3f;
                OSEntry entry = OSRegistry.Find(OSTab.Projects, want);
                Check(entry != null && !string.IsNullOrEmpty(entry.title) && !entry.title.Contains("proj_"), "the Projects tab lists " + want + " as '" + (entry != null ? entry.title : "(missing)") + "'");
                OSWindow pwin = windows.Find(OSProjectWindow.WindowId);
                Check(pwin != null && pwin.GetComponent<OSProjectWindow>().ProjectId == want, "the Project window is open on " + want);
            }
            windows.CloseAll();
            yield return 0.4f;

            // ---- H. the voice rows and spoken answers, with the voice forced live
            OSBuildFlow.VoiceLiveOverride = true;
            OSBuildFlow.Open();
            yield return 0.8f;
            win = BuildWindow(shell);
            Check(win != null && Count(win, "Button Something else") == 1, "voice live, question 1: 'Something else' is the fifth answer only");
            step = Tap(shell, "Rocket");
            while (step.MoveNext()) yield return step.Current;
            win = BuildWindow(shell);
            Check(win != null && Count(win, "Button Something else") == 1 && Has(Text(shell), "What matters most for the rocket?", "You can say an answer, or tap it."),
                "voice live, question 2: a 'Something else' row and the hint appear");
            DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
            yield return 0.4f;
            layout = OSToolsTest.LayoutProblem(win);
            Check(layout == null, "question 2 with the voice row: no clipped or overlapping text" + (layout != null ? ": " + layout : ""));
            smallest = SmallestButton(win, out smallName, out smallestW);
            Check(smallest >= 22f, "every button with the voice row is at least 22 mm (smallest " + smallest.ToString("F1") + " mm, " + smallName + ")");
            Shot("63_build_voice_row");
            Check(OSBuildFlow.Choose("flying straight"), "a spoken 'flying straight' counts as tapping it");
            yield return 0.5f;
            Check(Has(Text(shell), "How do we test it?"), "the rocket test question follows a spoken answer");
            Check(!OSBuildFlow.Choose("purple") && OSBuildFlow.ChooseProblem != null && OSBuildFlow.ChooseProblem.Contains("isn't one of the choices"), "'purple' is refused and the choices are named (" + OSBuildFlow.ChooseProblem + ")");
            Check(Brand(OSBuildFlow.ChooseProblem ?? "") == null, "the refusal names no brand");
            JObject viaTool = AlbieTools.Call("os_answer", new JObject { ["answer"] = "something else" });
            Check(viaTool != null && (bool?)viaTool["ok"] == true && OSBuildFlow.Current.FreeMode, "os_answer 'something else' puts the card in free mode");
            Check(OSBuildFlow.Choose("a test with a stopwatch"), "after 'something else' any words are the answer");
            yield return 0.5f;
            Check(Has(Text(shell), "Here is the plan", "In my words") && OSBuildFlow.Current.Planner.Result.test.Length > 0, "the free words reached the plan card and the test is still set");
            win = BuildWindow(shell);
            DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
            yield return 0.4f;
            layout = win != null ? OSToolsTest.LayoutProblem(win) : "no window";
            Check(layout == null, "the plan card with the user's own words: no clipped or overlapping text" + (layout != null ? ": " + layout : ""));
            Shot("64_build_plan_free_words");
            Check(OSBuildFlow.Choose("start") && NotebookStore.ProjectId == "proj_my_rocket", "'start' on the plan card makes the person's own rocket: proj_my_rocket (" + NotebookStore.ProjectId + ")");
            yield return 0.8f;
            windows.CloseAll();
            JObject noCard = AlbieTools.Call("os_answer", new JObject { ["answer"] = "drone" });
            Check(noCard != null && noCard["error"] != null, "os_answer with no card open answers an error line");

            OSBuildFlow.VoiceLiveOverride = false;
            OSBuildFlow.Open();
            yield return 0.8f;
            step = Tap(shell, "Rocket");
            while (step.MoveNext()) yield return step.Current;
            win = BuildWindow(shell);
            Check(win != null && Count(win, "Button Something else") == 0, "voice off, question 2: there is no 'Something else' row");
            BuildWindow(shell)?.Close();
            yield return 0.5f;
            windows.CloseAll();

            // ---- the longest plan card (the image model's parts line) still fits
            OSBuildFlow.Open();
            yield return 0.8f;
            foreach (string label in new[] { "A model that recognises things", "Things around my room", "Right 8 times in 10" })
            {
                step = Tap(shell, label);
                while (step.MoveNext()) yield return step.Current;
            }
            win = BuildWindow(shell);
            Check(win != null && Has(Text(shell), "Here is the plan", "Train a model that recognises things around my room."), "the image model's plan card shows its goal");
            if (win != null)
            {
                DesktopRig.Current.LookAt(OSToolsTest.CenterOf(win.Panel.transform));
                yield return 0.4f;
                layout = OSToolsTest.LayoutProblem(win);
                Check(layout == null, "the image model's plan card: no clipped or overlapping text" + (layout != null ? ": " + layout : ""));
                Shot("65_build_plan_model");
                win.Close();
            }
            yield return 0.5f;
            windows.CloseAll();

            // ---- I. Albie comes
            AlbieController albie = UnityEngine.Object.FindAnyObjectByType<AlbieController>();
            Transform head = OSShell.FindHead();
            if (albie == null || head == null)
            {
                Debug.Log("[OSPlannerTest] skip Albie comes: this scene has no Albie or no head");
            }
            else
            {
                // Park Albie to the side of the user first (a station: he walks there and stays), so coming over is a real walk.
                albie.StationAt(albie.transform.position + head.right * 0.7f);
                yield return 3.5f;
                Vector3 a0 = albie.transform.position;
                float d0 = Vector2.Distance(new Vector2(a0.x, a0.z), new Vector2(head.position.x, head.position.z));
                AlbieState s0 = albie.State;
                OSBuildFlow.Open();
                yield return 4.0f;
                Vector3 a1 = albie.transform.position;
                float d1 = Vector2.Distance(new Vector2(a1.x, a1.z), new Vector2(head.position.x, head.position.z));
                Measure("Albie: " + s0 + " parked " + d0.ToString("F2") + " m from the user, four seconds after opening Build something " + albie.State + " at " + d1.ToString("F2") + " m, moved " + Vector3.Distance(a0, a1).ToString("F2") + " m");
                Check(Vector3.Distance(a0, a1) > 0.1f && d1 < d0,
                    "Albie walked over to the user when Build something opened (" + d0.ToString("F2") + " m to " + d1.ToString("F2") + " m, now " + albie.State + ")");
                BuildWindow(shell)?.Close();
                yield return 0.4f;
            }
        }
    }
}
