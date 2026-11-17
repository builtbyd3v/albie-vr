using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
    /// Forge > OS > Run Agents Test: the real Main scene in Play mode on the desktop rig with voice off. It spawns two agents
    /// (builder and scout), gives the builder a task that stays running offline (a local socket that never answers), opens the
    /// Agents window from the menu's strip button, brings the builder home and checks that it stands on its pad, still, with its
    /// task untouched, sends it out again, brings both home and out with the two buttons, makes Dismiss ask first (Cancel keeps
    /// the agent, Dismiss removes it), makes an agent from the window, pages through nine agents, and checks the empty state.
    /// It works with the crew egg gate shut (no Code mode open): the hatching gate is not needed. Screenshots go to Logs/os/ (the
    /// ones this test makes are listed in a manifest and cleared by its next run); the result is in <see cref="LastResult"/> and
    /// a console line "[OSAgentsTest] N passed, M failed".
    /// </summary>
    [InitializeOnLoad]
    public static class OSAgentsTest
    {
        const string ActiveKey = "OSAgentsTest.Active";
        const string ScenePath = "Assets/_Forge/Core/Main.unity";
        const double GiveUpSeconds = 420;
        const string TaskText = "Read the sample project and say what it does";

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
        static TcpListener hold;
        static string[] startingPrefs;

        static OSAgentsTest()
        {
            if (SessionState.GetBool(ActiveKey, false)) EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static bool keepPlaying
        {
            get => SessionState.GetBool("OSAgentsTest.Keep", false);
            set => SessionState.SetBool("OSAgentsTest.Keep", value);
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

        [MenuItem("Forge/OS/Run Agents Test")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSAgentsTest] Leave Play mode first."); return; }
            OSTestEnv.Enter("OSAgentsTest");
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
            string summary = $"[OSAgentsTest] {passed} passed, {failed} failed";
            LastResult = (failed == 0 ? "PASS " : "FAIL ") + summary + (failed == 0 ? "" : "\n" + string.Join("\n", failures));
            if (failed == 0) Debug.Log(summary);
            else Debug.LogError(summary + "\n" + string.Join("\n", failures));
            SessionState.SetBool(ActiveKey, false);
            if (keepPlaying) return;
            RestorePrefs();
            EditorApplication.isPlaying = false;
        }

        // The listener that holds the builder's task open, the url pref, the crew the test hatched, and the window.
        static void Cleanup()
        {
            DismissAll();
            if (OSShell.Current != null) OSShell.Current.Windows.Close(OSAgentsWindow.WindowId);
            if (hold != null) { try { hold.Stop(); } catch (SocketException) { } hold = null; }
            OSTestEnv.ClearClaudeUrl();   // the user's own address (if any) comes back at OSTestEnv.Leave
        }

        static void DismissAll()
        {
            AlbieCrew crew = AlbieCrew.Instance;
            if (crew == null) return;
            var ids = new List<string>();
            for (int i = 0; i < crew.Members.Count; i++) if (crew.Members[i] != null) ids.Add(crew.Members[i].PetId);
            foreach (string id in ids) AlbieTools.Call("dismiss_agent", new JObject { ["agent"] = id });
        }

        static void Check(bool condition, string what)
        {
            Debug.Log($"[OSAgentsTest] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition) { passed++; return; }
            failed++;
            failures.Add(what);
        }

        static void Measure(string line)
        {
            measured.Add(line);
            Debug.Log("[OSAgentsTest] measured: " + line);
        }

        // The shots of the last run are listed in a manifest, so this run clears exactly those and never another test's pictures.
        static string ManifestPath => Path.Combine(OSShot.Dir, "agents_test_shots.txt");

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
            string path = OSShot.Take($"{70 + ++shotCount:00}_agents_{name}");
            Debug.Log("[OSAgentsTest] shot " + (path ?? "(no camera)"));
            if (path != null)
            {
                try { File.AppendAllText(ManifestPath, Path.GetFileName(path) + "\n"); } catch (IOException) { }
            }
            return path;
        }

        // ---------------------------------------------------------------- helpers

        static bool Press(GameObject target, out string why) => OSToolsTest.Press(target, out why);
        static Transform FindDeep(Transform root, string name) => OSToolsTest.FindDeep(root, name);

        static OSAgentsWindow View(OSShell shell)
        {
            OSWindow w = shell.Windows.Find(OSAgentsWindow.WindowId);
            return w != null ? w.GetComponent<OSAgentsWindow>() : null;
        }

        static OSWindow Win(OSShell shell) => shell.Windows.Find(OSAgentsWindow.WindowId);

        static AlbieCrewMember Member(string id) => AlbieCrew.Instance.Find(AlbieAgentTypes.Find(id));

        static Button ButtonOf(Transform root, string name) => FindDeep(root, name)?.GetComponent<Button>();

        static Button RowButton(OSAgentsWindow window, string agent, string button)
        {
            Transform row = FindDeep(window.transform, "Agent " + agent);
            return row != null ? ButtonOf(row, "Button " + button) : null;
        }

        static string RowText(OSAgentsWindow window, string agent)
        {
            Transform row = FindDeep(window.transform, "Agent " + agent);
            if (row == null) return null;
            var parts = new List<string>();
            foreach (TMP_Text t in row.GetComponentsInChildren<TMP_Text>(false)) if (t.gameObject.activeInHierarchy) parts.Add(t.text);
            return string.Join("|", parts);
        }

        static JObject CrewEntry(string agent)
        {
            var list = AlbieTools.Call("crew_status", new JObject())["crew"] as JArray;
            if (list == null) return null;
            foreach (JToken item in list) if ((string)item["agent"] == agent) return (JObject)item;
            return null;
        }

        static void Look(Vector3 point)
        {
            DesktopRig.Current.LookAt(point);
        }

        static float Flat(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        // ---------------------------------------------------------------- the script

        static IEnumerator<float> Script()
        {
            // Everything the run could write (settings, notebook, artifacts, approvals) goes to a folder of its own first; the tests that need their own root move on from here.
            OSTestEnv.UseScratch("osagents");
            string why = null;
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

            float waited = 0f;
            while (AlbieCrew.Instance == null && waited < 8f) { waited += 0.25f; yield return 0.25f; }
            AlbieCrew crew = AlbieCrew.Instance;
            Check(crew != null, "the crew is in Main");
            if (crew == null) yield break;
            Check(OSShell.HasSystem("agents"), "the Agents window is registered under the strip's id 'agents'");
            DismissAll();
            yield return 0.8f;
            Check(crew.Members.Count == 0, "the test starts with no crew (" + crew.Members.Count + ")");

            waited = 0f;
            while (!(AlbieNavSurface.Desk != null && AlbieNavSurface.Desk.IsBuilt) && waited < 10f) { waited += 0.5f; yield return 0.5f; }
            bool deskBuilt = AlbieNavSurface.Desk != null && AlbieNavSurface.Desk.IsBuilt;
            Measure("desk surface built: " + deskBuilt + ", at desk: " + shell.AtDesk + ", crew allowed (Code mode): " + crew.CrewAllowed);
            Check(!crew.CrewAllowed, "the crew egg gate is shut (no Code mode open), so the window's hatching must not depend on it");

            // ---- empty state, before any agent
            var step = OpenFromMenu(shell, menu);
            while (step.MoveNext()) yield return step.Current;
            OSAgentsWindow view = View(shell);
            Check(view != null, "the Agents button opened the window: " + why);
            if (view == null) yield break;
            Check(!shell.MenuOpen, "the strip button closed the menu");
            yield return 0.8f;
            string all = OSToolsTest.AllText(Win(shell));
            Check(all.Contains("No agents yet."), "with no crew the window says 'No agents yet.'");
            Check(ButtonOf(view.transform, "Button Bring all home") != null && !ButtonOf(view.transform, "Button Bring all home").interactable, "with no crew Bring all home cannot be pressed");
            Check(view.RowCount == 0, "with no crew the window has no rows");
            string problem = OSToolsTest.LayoutProblem(Win(shell));
            Check(problem == null, "the empty Agents window has no clipped or overlapping text" + (problem ?? ""));

            // ---- 1. two agents; the builder holds a task that stays running offline
            hold = new TcpListener(IPAddress.Loopback, 0);
            hold.Start();
            OSTestEnv.SetClaudeUrl("http://127.0.0.1:" + ((IPEndPoint)hold.LocalEndpoint).Port + "/api/claude");
            JObject a = AlbieTools.Call("spawn_agent", new JObject { ["type"] = "builder" });
            JObject b = AlbieTools.Call("spawn_agent", new JObject { ["type"] = "scout" });
            Check((string)a["hatched"] == "builder" && (string)b["hatched"] == "scout", "spawn_agent hatched a builder and a scout: " + a + " " + b);
            Check(crew.Members.Count == 2 && crew.Members[0].PetId == "builder" && crew.Members[1].PetId == "scout", "the crew is builder, scout (" + crew.Members.Count + ")");
            // A crew member hooks onto the event source in its first Update: a task injected in the very frame it hatched would be missed.
            yield return 1.0f;
            object lastBefore = CrewTools.LastResult;
            JObject t = AlbieTools.Call("assign_task", new JObject { ["agent"] = "builder", ["task"] = TaskText, ["mode"] = "qa" });
            Check((string)t["status"] == "working", "assign_task started the builder's task: " + t);
            yield return 1.2f;
            JObject entry = CrewEntry("builder");
            Check(entry != null && (string)entry["task"] == TaskText, "crew_status lists the builder with its task: " + entry);
            Check(CrewEntry("scout") != null && (string)CrewEntry("scout")["task"] == null, "crew_status lists the scout with no task");
            AlbieCrewMember builder = Member("builder"), scout = Member("scout");
            Check(builder != null && builder.Bridge != null && builder.Bridge.Busy, "the builder's bridge is busy with the task");

            // ---- 2. the window shows both rows
            yield return 1.0f;
            view = View(shell);
            Check(view != null && view.RowCount == 2 && view.PageCount == 1, "the window shows two rows on one page (" + (view != null ? view.RowCount + " rows, " + view.PageCount + " pages" : "no window") + ")");
            string builderRow = RowText(view, "builder"), scoutRow = RowText(view, "scout");
            Check(builderRow != null && builderRow.Contains(TaskText) && builderRow.Contains("Builder") && builderRow.Contains("Walking"), "the builder's row shows its name, its real task and Walking: " + builderRow);
            Check(scoutRow != null && scoutRow.Contains("Scout") && scoutRow.Contains("Walking") && (scoutRow.Contains("Idle") || scoutRow.Contains("Arriving") || scoutRow.Contains("Working")), "the scout's row shows its name, what it is doing and Walking: " + scoutRow);
            all = OSToolsTest.AllText(Win(shell));
            Check(all.Contains("Lead") && all.Contains(PetConfig.DisplayName), "Albie is at the top as the lead (" + PetConfig.DisplayName + ")");
            Check(ButtonOf(view.transform, "Button Bring all home").interactable && !ButtonOf(view.transform, "Button Send all out").interactable, "Bring all home is on and Send all out is off when nobody is home");
            Look(OSToolsTest.CenterOf(Win(shell).Panel.transform));
            yield return 0.6f;
            problem = OSToolsTest.LayoutProblem(Win(shell));
            Check(problem == null, "the two-row window has no clipped or overlapping text" + (problem ?? ""));
            Shot("two_rows");
            CheckTouchSizes(view, "two rows");

            // ---- 3. bring the builder home with a real press
            Button bring = RowButton(view, "builder", "Bring home");
            Check(bring != null && Press(bring.gameObject, out why), "a pointer reaches the builder's Bring home: " + why);
            yield return 0.8f;
            builder = Member("builder");
            Check(builder.Home, "the builder is home");
            Check(view.StatusLine.Contains("is home") && view.StatusLine.Contains("task keeps running"), "the status line says it is home and its task keeps running: " + view.StatusLine);
            Check(RowButton(view, "builder", "Send out") != null && RowButton(view, "builder", "Bring home") == null, "the builder's button flipped to Send out");
            Check(RowText(view, "builder").Contains(deskBuilt ? "Home" : "Home, hidden"), "the builder's row says " + (deskBuilt ? "Home" : "Home, hidden") + ": " + RowText(view, "builder"));
            Check(ButtonOf(view.transform, "Button Send all out").interactable, "Send all out is on now that one is home");

            // ---- 4. at home: standing on its pad, still, task untouched (an agent walks about 15 cm a second: wait for it to get there)
            waited = 0f;
            while (waited < 40f && !(Member("builder").HomeSpot.HasValue && Flat(Member("builder").transform.position, Member("builder").HomeSpot.Value) < 0.12f) && deskBuilt)
            {
                waited += 0.5f;
                yield return 0.5f;
            }
            Measure("the builder took " + waited.ToString("F1") + " s to reach its pad");
            yield return 1f;
            builder = Member("builder");
            if (deskBuilt && shell.AtDesk)
            {
                Check(builder.HomeSpot.HasValue && !builder.BodyHidden, "the builder has a home spot on the desk and is visible");
                if (builder.HomeSpot.HasValue)
                {
                    float d = Flat(builder.transform.position, builder.HomeSpot.Value);
                    Measure("the builder stands " + d.ToString("F3") + " m from its pad centre");
                    Check(d < 0.12f, "the builder walked to its pad (" + d.ToString("F3") + " m away)");
                    Check(OSHomePad.Count == 1 && OSHomePad.Find(builder) != null, "one pad exists and it is the builder's (" + OSHomePad.Count + ")");
                    Vector3 p0 = builder.transform.position;
                    yield return 1f;
                    float moved = Vector3.Distance(p0, builder.transform.position);
                    Check(moved < 0.01f, "the builder stands still on its pad (" + moved.ToString("F4") + " m in 1 s)");
                    // The picture of the pad with the agent on it: the window is put away for it (a panel is see-through but in the way), then back.
                    windows.Close(OSAgentsWindow.WindowId);
                    yield return 0.4f;
                    Look(builder.HomeSpot.Value + Vector3.up * 0.04f);
                    yield return 0.8f;
                    Shot("home_pad");
                    OSAgentsWindow.Open();
                    yield return 0.8f;
                    view = View(shell);
                    if (Win(shell) != null) Look(OSToolsTest.CenterOf(Win(shell).Panel.transform));
                    yield return 0.4f;
                    Check(view != null && RowText(view, "builder") != null && RowText(view, "builder").Contains("Home"), "the window opened again still shows the builder at home");
                }
            }
            else
            {
                Check(builder.BodyHidden, "no desk surface here: the builder's body is hidden");
                bool allOff = true;
                foreach (Renderer r in builder.GetComponentsInChildren<Renderer>(true)) if (r != null && !r.forceRenderingOff) allOff = false;
                Check(allOff, "every renderer of the hidden builder is switched off");
                Check(builder.gameObject.activeSelf, "the hidden builder's GameObject is still active");
                Shot("home_hidden");
            }
            entry = CrewEntry("builder");
            Check(entry != null && (string)entry["task"] == TaskText, "at home the builder's task is still assigned: " + entry);
            Check(builder.Bridge.Busy && builder.Bridge.TaskInProgress, "at home the builder's bridge is still busy");
            Check(ReferenceEquals(CrewTools.LastResult, lastBefore) || CrewTools.LastResult == null || (string)CrewTools.LastResult["agent"] != "builder", "no result arrived for the builder: its task did not finish or stop");
            Check(builder.Controller.State == AlbieState.Working, "the builder's controller is still Working, not Wandering or dismissed (" + builder.Controller.State + ")");
            string homeRow = RowText(view, "builder");
            Check(homeRow != null && homeRow.Contains(TaskText), "the Agents window still shows the task on the home row: " + homeRow);
            problem = OSToolsTest.LayoutProblem(Win(shell));
            Check(problem == null, "the window with one agent home has no clipped or overlapping text" + (problem ?? ""));

            // ---- 5. send it out again with a real press; the task is still there
            Button send = RowButton(view, "builder", "Send out");
            Check(send != null && Press(send.gameObject, out why), "a pointer reaches the builder's Send out: " + why);
            yield return 0.8f;
            builder = Member("builder");
            Check(!builder.Home && !builder.BodyHidden, "the builder is out again and visible");
            Check(view.StatusLine.Contains("is out again"), "the status line says it is out again: " + view.StatusLine);
            yield return 0.5f;
            Check(OSHomePad.Find(builder) == null, "the builder's pad is gone after Send out");
            entry = CrewEntry("builder");
            Check(entry != null && (string)entry["task"] == TaskText, "after Send out the task is still assigned: " + entry);
            Check(RowButton(view, "builder", "Bring home") != null, "the builder's button is Bring home again");

            // ---- 6. the scout: home, then out, and it walks again
            Look(OSToolsTest.CenterOf(Win(shell).Panel.transform));
            bring = RowButton(view, "scout", "Bring home");
            Check(bring != null && Press(bring.gameObject, out why), "a pointer reaches the scout's Bring home: " + why);
            waited = 0f;
            while (waited < 40f && !(Member("scout").HomeSpot.HasValue && Flat(Member("scout").transform.position, Member("scout").HomeSpot.Value) < 0.12f) && deskBuilt)
            {
                waited += 0.5f;
                yield return 0.5f;
            }
            Measure("the scout took " + waited.ToString("F1") + " s to reach its pad");
            yield return 1f;
            scout = Member("scout");
            Check(scout.Home, "the scout is home");
            if (scout.HomeSpot.HasValue)
            {
                float d = Flat(scout.transform.position, scout.HomeSpot.Value);
                Check(d < 0.12f, "the scout walked to its pad (" + d.ToString("F3") + " m away)");
            }
            send = RowButton(view, "scout", "Send out");
            Check(send != null && Press(send.gameObject, out why), "a pointer reaches the scout's Send out: " + why);
            yield return 0.5f;
            Vector3 start = Member("scout").transform.position;
            float far = 0f;
            waited = 0f;
            while (waited < 20f && far < 0.08f)
            {
                yield return 0.5f;
                waited += 0.5f;
                far = Mathf.Max(far, Vector3.Distance(start, Member("scout").transform.position));
            }
            Check(far >= 0.08f, "the scout wanders again after Send out (moved " + far.ToString("F2") + " m in " + waited.ToString("F1") + " s)");

            // ---- 7. bring all home, send all out
            view = View(shell);
            Look(OSToolsTest.CenterOf(Win(shell).Panel.transform));
            yield return 0.4f;
            Check(Press(ButtonOf(view.transform, "Button Bring all home").gameObject, out why), "a pointer reaches Bring all home: " + why);
            yield return 0.8f;
            Check(OSAgentHome.HomeCount() == 2, "Bring all home brought both home (" + OSAgentHome.HomeCount() + ")");
            if (deskBuilt && shell.AtDesk && Member("builder").HomeSpot.HasValue && Member("scout").HomeSpot.HasValue)
            {
                float apart = Flat(Member("builder").HomeSpot.Value, Member("scout").HomeSpot.Value);
                Check(apart >= 0.07f, "the two pads are apart (" + apart.ToString("F3") + " m)");
            }
            Check(RowText(view, "builder").Contains("Home") && RowText(view, "scout").Contains("Home"), "both rows say Home");
            Check(!ButtonOf(view.transform, "Button Bring all home").interactable && ButtonOf(view.transform, "Button Send all out").interactable, "Bring all home is off and Send all out is on");
            Look(OSToolsTest.CenterOf(Win(shell).Panel.transform));
            yield return 0.4f;
            Shot("both_home");
            CheckTouchSizes(view, "both home");
            Look(OSToolsTest.CenterOf(Win(shell).Panel.transform));
            yield return 0.4f;
            Check(Press(ButtonOf(view.transform, "Button Send all out").gameObject, out why), "a pointer reaches Send all out: " + why);
            yield return 0.8f;
            Check(OSAgentHome.HomeCount() == 0, "Send all out sent both out (" + OSAgentHome.HomeCount() + " home)");

            // ---- 8. Dismiss asks first
            view = View(shell);
            Look(OSToolsTest.CenterOf(Win(shell).Panel.transform));
            yield return 0.4f;
            Button dismiss = RowButton(view, "scout", "Dismiss");
            Check(dismiss != null && Press(dismiss.gameObject, out why), "a pointer reaches the scout's Dismiss: " + why);
            yield return 0.6f;
            Transform card = FindDeep(view.transform, "OSConfirm");
            Check(card != null, "Dismiss opened a confirm card");
            string cardText = "";
            if (card != null) foreach (TMP_Text tt in card.GetComponentsInChildren<TMP_Text>(true)) cardText += tt.text + "|";
            Check(cardText.Contains("Dismiss Scout?") && cardText.Contains("task stops"), "the card names the agent and says its task stops: " + cardText);
            Check(Member("scout") != null && crew.Members.Count == 2, "nothing is dismissed before the answer");
            Look(OSToolsTest.CenterOf(Win(shell).Panel.transform));
            yield return 0.4f;
            Shot("dismiss_confirm");
            Button cancel = card != null ? ButtonOf(card, "Button Cancel") : null;
            Check(cancel != null && Press(cancel.gameObject, out why), "a pointer reaches Cancel: " + why);
            yield return 0.6f;
            Check(FindDeep(view.transform, "OSConfirm") == null, "Cancel closes the card");
            Check(Member("scout") != null && crew.Members.Count == 2, "Cancel keeps the scout");
            dismiss = RowButton(view, "scout", "Dismiss");
            Check(dismiss != null && Press(dismiss.gameObject, out why), "a pointer reaches Dismiss again: " + why);
            yield return 0.6f;
            card = FindDeep(view.transform, "OSConfirm");
            Button yes = card != null ? ButtonOf(card, "Button Dismiss") : null;
            Check(yes != null && Press(yes.gameObject, out why), "a pointer reaches the card's Dismiss: " + why);
            yield return 1.2f;
            Check(Member("scout") == null && crew.Members.Count == 1, "Dismiss removed the scout (" + crew.Members.Count + " left)");
            view = View(shell);
            Check(view.StatusLine.Contains("Scout is dismissed"), "the status line says the scout is dismissed: " + view.StatusLine);
            Check(view.RowCount == 1, "the window shows one row now (" + view.RowCount + ")");

            // ---- 9. dismissing the builder is what stops its task; bringing it home never did
            builder = Member("builder");
            Check(builder != null && CrewEntry("builder") != null && (string)CrewEntry("builder")["task"] == TaskText, "the builder still has its task right up to the dismissal");
            JObject gone = AlbieTools.Call("dismiss_agent", new JObject { ["agent"] = "builder" });
            Check((bool?)gone["stopped_task"] == true, "dismissing the builder stopped its task: " + gone);
            yield return 1.2f;
            Check(crew.Members.Count == 0, "the crew is empty again");

            // ---- 10. make an agent from the window, with the egg's gate shut
            view = View(shell);
            Look(OSToolsTest.CenterOf(Win(shell).Panel.transform));
            yield return 0.4f;
            Button fixer = ButtonOf(view.transform, "Button Fixer");
            Check(fixer != null, "the Make an agent row offers Fixer");
            Check(fixer != null && Press(fixer.gameObject, out why), "a pointer reaches Fixer: " + why);
            yield return 1.0f;
            Check(crew.IsHatched(AlbieAgentTypes.Find("fixer")), "Fixer hatched from the window with the code gate shut (CrewAllowed " + crew.CrewAllowed + ")");
            Check(view.StatusLine.Contains("hatched"), "the status line says it hatched: " + view.StatusLine);
            Check(view.RowCount == 1, "the window shows the new row (" + view.RowCount + ")");

            // ---- 11. paging: all nine
            foreach (AlbieAgentType type in AlbieAgentTypes.All) AlbieTools.Call("spawn_agent", new JObject { ["type"] = type.Id });
            yield return 1.2f;
            view = View(shell);
            Check(crew.Members.Count == AlbieAgentTypes.All.Length, "all nine types are out (" + crew.Members.Count + ")");
            Check(view.PageCount == 3 && view.RowCount == AlbieAgentTypes.All.Length, "nine agents take three pages (" + view.PageCount + " pages, " + view.RowCount + " rows)");
            Check(FindDeep(view.transform, "Button chevron_right") != null, "the pager shows a next button");
            Check(OSToolsTest.AllText(Win(shell)).Contains("Every kind of agent is already out."), "with all nine out the Make an agent row says so");
            Look(OSToolsTest.CenterOf(Win(shell).Panel.transform));
            yield return 0.5f;
            problem = OSToolsTest.LayoutProblem(Win(shell));
            Check(problem == null, "the first page of nine has no clipped or overlapping text" + (problem ?? ""));
            Shot("page_one_of_three");
            Look(OSToolsTest.CenterOf(Win(shell).Panel.transform));
            yield return 0.4f;
            Button next = ButtonOf(view.transform, "Button chevron_right");
            Check(next != null && Press(next.gameObject, out why), "a pointer reaches the pager's next button: " + why);
            yield return 0.6f;
            Check(view.Page == 1, "the next button went to page 2 (" + view.Page + ")");
            string firstOnPage2 = AlbieAgentTypes.All[4].Id;
            Check(FindDeep(view.transform, "Agent " + firstOnPage2) != null && FindDeep(view.transform, "Agent " + AlbieAgentTypes.All[0].Id) == null, "page 2 starts with the fifth agent (" + firstOnPage2 + ")");
            CheckTouchSizes(view, "page two");
            DismissAll();
            yield return 1.5f;
            Check(crew.Members.Count == 0, "all the agents were dismissed");
            Check(OSHomePad.Count == 0, "no pad is left behind (" + OSHomePad.Count + ")");

            // ---- 12. empty again
            yield return 0.8f;
            Check(OSToolsTest.AllText(Win(shell)).Contains("No agents yet."), "after the last agent leaves the window says 'No agents yet.' again");
            windows.Close(OSAgentsWindow.WindowId);
            yield return 0.6f;
            Check(!windows.IsOpen(OSAgentsWindow.WindowId), "the Agents window closes");
        }

        // The menu's Agents button, pressed with a pointer (the menu opens with the M key, or the shell when the editor is unfocused).
        static IEnumerator<float> OpenFromMenu(OSShell shell, OSMenu menu)
        {
            if (!shell.MenuOpen)
            {
                OSInput.Tap(Key.M);
                yield return 0.9f;
                if (!shell.MenuOpen)
                {
                    Debug.Log("[OSAgentsTest] M didn't open the menu (editor focus " + UnityEditorInternal.InternalEditorUtility.isApplicationActive + "); opening it with the shell");
                    shell.OpenMenu(OSSummon.Key);
                    yield return 0.9f;
                }
            }
            Check(shell.MenuOpen, "the menu is open");
            Look(menu.LastCenter);
            yield return 0.5f;
            Transform agents = FindDeep(menu.transform, "Button Agents");
            Check(agents != null, "the menu strip has an Agents button");
            string why = null;
            Check(agents != null && Press(agents.gameObject, out why), "a pointer reaches the Agents button: " + why);
            yield return 1.0f;
        }

        // Every button of the window is at least 22 mm both ways.
        static void CheckTouchSizes(OSAgentsWindow window, string where)
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
            Measure(where + ": smallest button " + worst + " mm");
            Check(ok, where + ": every button is at least 22 x 22 mm (smallest " + worst + " mm)");
        }
    }
}
