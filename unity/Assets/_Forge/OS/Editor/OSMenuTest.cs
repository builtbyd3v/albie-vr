using System;
using System.Collections.Generic;
using System.IO;
using Forge.Pet;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Run Menu Test: the real Main scene in Play mode on the desktop rig with voice off, driving the menu the way a
    /// user does: sit at the desk, press the table button, switch tabs, press a tile, close, and again away from the desk.
    /// Every press goes through the EventSystem (a raycast against the real canvases, then pointer enter/down/up/click on what
    /// it hit), never by calling the menu's methods. Throwaway sample entries are registered for the run and removed after.
    /// Screenshots go to Logs/os/; the result is in <see cref="LastResult"/> and a console line "[OSMenuTest] N passed, M failed".
    /// Start it with <c>OSMenuTest.Run()</c>; it opens Main.unity, enters Play and leaves Play when done.
    /// </summary>
    [InitializeOnLoad]
    public static class OSMenuTest
    {
        const string ActiveKey = "OSMenuTest.Active";
        const string ScenePath = "Assets/_Forge/Core/Main.unity";
        const double GiveUpSeconds = 150;

        /// <summary>"running", or the summary of the last run (starts with PASS or FAIL), or null if it never ran.</summary>
        public static string LastResult { get; private set; }
        /// <summary>The measurements of the last run, one per line.</summary>
        public static string Measurements { get; private set; }

        static IEnumerator<float> steps;
        static double wakeAt, startedAt;
        static int passed, failed;
        static readonly List<string> failures = new List<string>();
        static readonly List<string> measured = new List<string>();
        static readonly List<Action> cleanup = new List<Action>();
        static string openedTool;

        static OSMenuTest()
        {
            if (SessionState.GetBool(ActiveKey, false)) EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static bool keepPlaying
        {
            get => SessionState.GetBool("OSMenuTest.Keep", false);
            set => SessionState.SetBool("OSMenuTest.Keep", value);
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
            OSTestEnv.Leave();
            EditorApplication.isPlaying = false;
        }

        [MenuItem("Forge/OS/Run Menu Test")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSMenuTest] Leave Play mode first."); return; }
            OSTestEnv.Enter("OSMenuTest");
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
            cleanup.Clear();
            steps = Script();
            wakeAt = 0;
            startedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
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
            foreach (var undo in cleanup) { try { undo(); } catch (Exception e) { Debug.LogException(e); } }
            cleanup.Clear();
            Measurements = string.Join("\n", measured);
            if (!keepPlaying)
            {
                string envDiff = OSTestEnv.Leave();
                Check(envDiff.Length == 0, "the user's own preferences and files are exactly as they were before the run" + (envDiff.Length == 0 ? "" : ": " + envDiff.Replace("\n", " | ")));
            }
            string summary = $"[OSMenuTest] {passed} passed, {failed} failed";
            LastResult = (failed == 0 ? "PASS " : "FAIL ") + summary + (failed == 0 ? "" : "\n" + string.Join("\n", failures));
            if (failed == 0) Debug.Log(summary);
            else Debug.LogError(summary + "\n" + string.Join("\n", failures));
            SessionState.SetBool(ActiveKey, false);
            if (keepPlaying) return;
            OSTestEnv.Leave();
            EditorApplication.isPlaying = false;
        }

        static void Check(bool condition, string what)
        {
            Debug.Log($"[OSMenuTest] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition) { passed++; return; }
            failed++;
            failures.Add(what);
        }

        static void Measure(string line)
        {
            measured.Add(line);
            Debug.Log("[OSMenuTest] measured: " + line);
        }

        // The pet's first-arrival greeting (START) can show up late; it isn't part of what is being looked at.
        static void ClearGreeting()
        {
            var greeting = UnityEngine.Object.FindAnyObjectByType<ArrivalGreeting>();
            if (greeting != null && greeting.IsShown) greeting.Fold(true);
            var workspace = Workspace.Current;
            if (workspace != null && workspace.DockHeld) workspace.HoldDock(false);
        }

        static void Shot(string name)
        {
            ClearGreeting();
            string path = OSShot.Take(name);
            Debug.Log("[OSMenuTest] shot " + (path ?? "(no camera)"));
        }

        // ---------------------------------------------------------------- real pointer input

        /// <summary>
        /// Presses a UI object like a mouse would: finds its screen position, asks the EventSystem which raycast hit is on top
        /// there, and plays pointer enter, down, up and click on it. Fails (false) if what is on top isn't the target or its child.
        /// </summary>
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

        // ---------------------------------------------------------------- samples (this run only)

        static void RegisterSamples()
        {
            // The real Tools entries step aside for the run (the test counts and presses its own sample tiles) and come back after.
            foreach (string real in new[] { "cad", "code", "train", "notebook", "messages", "gestures", "drive", "agents" }) OSRegistry.UnregisterTool(real);
            cleanup.Add(OSToolsCatalog.Register);
            OSProjects.Unregister();  // the real Projects list steps aside too, for the same reason
            cleanup.Add(OSProjects.Register);
            OSArtifacts.Unregister();  // and the real Artifacts list
            cleanup.Add(OSArtifacts.Register);
            string[] toolIds = { "cad", "code", "train", "notebook", "drive", "messages", "gestures" };
            string[] toolTitles = { "CAD", "Code", "Train", "Notebook", "Drive", "Messages", "Gestures" };
            string[] toolIcons = { "cube", "code", "train", "notebook", "drive", "messages", "gestures" };
            string[] toolStatus = { "3D parts", "write and run", "teach the truck", "RC truck notes", "hand transmitter", "unread: 2", "teach a pose" };
            for (int i = 0; i < toolIds.Length; i++)
            {
                string id = toolIds[i];
                OSRegistry.RegisterTool(new OSEntry
                {
                    id = "zz_" + id, title = toolTitles[i], icon = toolIcons[i], status = Const(toolStatus[i]),
                    isOpen = id == "notebook" ? (Func<bool>)(() => true) : null,
                    open = () => openedTool = id,
                });
            }

            Action<List<OSEntry>> projects = list =>
            {
                list.Add(new OSEntry { id = "zz_truck", title = "RC truck", icon = "truck", status = Const("v2 · slalom passed"), open = () => openedTool = "truck" });
                list.Add(new OSEntry { id = "zz_quad", title = "Quadcopter", icon = "quadcopter", status = Const("v1 · hover"), open = () => openedTool = "quad" });
                list.Add(new OSEntry { id = "zz_rocket", title = "Rocket", icon = "rocket", status = Const("v1"), open = () => openedTool = "rocket" });
                list.Add(new OSEntry { id = "zz_class", title = "Image classifier", icon = "classifier", status = Const("v3 · 94% accurate"), open = () => openedTool = "classifier" });
                list.Add(new OSEntry { id = "zz_new", title = "New project", icon = "plus", status = Const("start with Albie"), section = "New", outline = true, open = () => openedTool = "new" });
            };
            OSRegistry.RegisterSource(OSTab.Projects, projects);
            cleanup.Add(() => OSRegistry.UnregisterSource(projects));

            Action<List<OSEntry>> artifacts = list =>
            {
                string[] titles = { "Slalom", "Hoop course", "Scoreboard", "Cell", "Heart", "Double pendulum", "Solar system", "Three bodies", "What shipped this week?" };
                string[] icons = { "slalom", "hoops", "scoreboard", "biology", "biology", "physics", "physics", "physics", "repo" };
                string[] status = { "linked to RC truck", "9 hoops", "best 12.4 s", "membrane, nucleus", "four chambers", "chaos", "eight planets", "orbits", "7 days of commits" };
                for (int i = 0; i < titles.Length; i++)
                {
                    string title = titles[i];
                    list.Add(new OSEntry { id = "zz_art" + i, title = title, icon = icons[i], status = Const(status[i]), open = () => openedTool = title });
                }
            };
            OSRegistry.RegisterSource(OSTab.Artifacts, artifacts);
            sampleArtifacts = artifacts;
            cleanup.Add(() =>
            {
                foreach (string id in toolIds) OSRegistry.UnregisterTool("zz_" + id);
                OSRegistry.UnregisterSource(artifacts);
            });
        }

        static Action<List<OSEntry>> sampleArtifacts;

        static Func<string> Const(string text) => () => text;

        // ---------------------------------------------------------------- the script

        static IEnumerator<float> Script()
        {
            // Everything the run could write (settings, notebook, artifacts, approvals) goes to a folder of its own first; the tests that need their own root move on from here.
            OSTestEnv.UseScratch("osmenu");
            // Sit at the desk the way the gesture test does.
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
            var table = shell.GetComponent<OSTableButton>();
            Check(menu != null && table != null, "the shell carries the menu and the table button");
            if (menu == null || table == null) yield break;
            RegisterSamples();

            // ---- the table button (the pet's first-arrival greeting holds the desk; the test lets it go, as pressing START would)
            if (shell.Held)
            {
                // Pressing START would also open the code view; the menu test wants an empty desk, so end the greeting quietly.
                Debug.Log("[OSMenuTest] the greeting holds the desk, so the table button is hidden; ending the greeting");
                var greeting = UnityEngine.Object.FindAnyObjectByType<ArrivalGreeting>();
                if (greeting != null) greeting.Fold(true);
                workspace.HoldDock(false);
                yield return 0.5f;
            }
            float waited = 0f;
            while (!table.Shown && waited < 6f) { waited += 0.25f; yield return 0.25f; }
            Check(table.Shown, "the table button shows at the desk (AtDesk " + shell.AtDesk + ", Held " + shell.Held + ", setting " + OSSettings.Current.tableButton + ", frame " + (workspace.presence.Frame != null) + ")");
            Check(table.Button != null, "the table button has a UI Set button");
            if (table.Button == null) yield break;
            Measure($"table button: plate {table.DiameterMeters * 1000f:F0} mm, button {table.ButtonMeters * 1000f:F0} mm");
            Shot("00_table_button");

            string why;
            Check(!shell.MenuOpen, "the menu starts closed");
            // The menu is built a few seconds after the shell starts, before anybody opens it (no hitch on the first open).
            for (float builtWait = 0f; builtWait < OSShell.PrewarmDelay + 4f && !menu.IsBuilt; builtWait += 0.25f) yield return 0.25f;
            Check(menu.IsBuilt && !menu.Visible && !shell.MenuOpen, "the menu was built ahead of its first open and stays hidden (built " + menu.IsBuilt + ", visible " + menu.Visible + ")");
            // A tab chosen before the menu was ever built (a voice os_tab) is the tab the first build shows, not Tools.
            shell.SetTab(OSTab.Projects);
            Check(Press(table.Button.gameObject, out why), "a pointer reaches the table button: " + why);
            yield return 0.8f;
            Check(shell.MenuOpen && shell.LastSummon == OSSummon.TableButton, "pressing the table button opens the menu (summon " + shell.LastSummon + ")");
            Check(menu.Visible, "the menu is visible");
            var firstTabs = FindDeep(menu.transform, "Tab Projects")?.GetComponent<Toggle>();
            var toolsTab = FindDeep(menu.transform, "Tab Tools")?.GetComponent<Toggle>();
            Check(shell.Tab == OSTab.Projects, "a tab set before the menu was first built survives the build (shell tab " + shell.Tab + ")");
            Check(firstTabs != null && firstTabs.isOn && toolsTab != null && !toolsTab.isOn, "the first build lights the shell's tab (Projects), not Tools");
            shell.SetTab(OSTab.Tools);
            yield return 0.4f;
            Check(toolsTab != null && toolsTab.isOn && firstTabs != null && !firstTabs.isOn, "setting the tab back to Tools lights Tools");
            yield return 0.5f;  // the lit tab's animation settles
            SelectedTabContrast(toolsTab);

            // Where it landed and how big it is.
            Transform head = shell.Head;
            var canvasRoot = (RectTransform)FindDeep(menu.transform, "CanvasRoot");
            float widthM = canvasRoot.rect.width * Mathf.Abs(canvasRoot.lossyScale.x);
            float heightM = canvasRoot.rect.height * Mathf.Abs(canvasRoot.lossyScale.y);
            float distance = Vector3.Distance(head.position, menu.LastCenter);
            float drop = Mathf.Atan2(head.position.y - menu.LastCenter.y, new Vector2(menu.LastCenter.x - head.position.x, menu.LastCenter.z - head.position.z).magnitude) * Mathf.Rad2Deg;
            float fov = 2f * Mathf.Atan(widthM * 0.5f / distance) * Mathf.Rad2Deg;
            Measure($"menu: {widthM:F3} m wide, {heightM:F3} m tall, {distance:F2} m from the eyes, {drop:F1} degrees below eye level, {fov:F1} degrees of horizontal view");
            OSMenu.ReachSetting(out float wantDistance, out float wantDrop);
            Check(Mathf.Abs(distance - wantDistance) < 0.01f, $"the menu is menuDistance from the eyes ({distance:F2} m, want {wantDistance:F2})");
            Check(drop <= wantDrop + 1f && drop >= OSMenu.MinDropDegrees - 1f, $"the menu is menuDropDegrees below eye level, or a little less to clear the desk ({drop:F1}, want {wantDrop:F1})");
            Check(distance >= 0.4f && distance <= 0.55f, $"the menu is within easy poking reach, 0.4 to 0.55 m from the eyes ({distance:F2} m)");
            Check(drop >= 10f && drop <= 30f, $"the menu is below eye level, 10 to 30 degrees down ({drop:F1})");
            Check(fov < 70f, "the menu fits in 70 degrees of horizontal view (" + fov.ToString("F1") + ")");
            if (workspace.presence.Frame != null)
            {
                float bottom = menu.LastCenter.y - heightM * 0.5f * Mathf.Cos(drop * Mathf.Deg2Rad);
                Check(bottom > workspace.presence.Frame.position.y + 0.02f, $"the menu's lower edge is above the desk top, clear of resting hands ({bottom - workspace.presence.Frame.position.y:F2} m above it)");
            }
            Vector3 menuPositionBefore = menu.transform.position;
            yield return 0.3f;

            // Sizes: a tile, and each kind of button, in millimetres.
            if (menu.ActiveTiles.Count > 0)
            {
                var tileRect = menu.ActiveTiles[0].rect;
                Measure($"tile: {Mm(tileRect.rect.width, tileRect):F0} x {Mm(tileRect.rect.height, tileRect):F0} mm");
                Check(Mm(tileRect.rect.width, tileRect) >= 22f && Mm(tileRect.rect.height, tileRect) >= 22f, "tiles are at least 22 x 22 mm");
                if (menu.ActiveTiles.Count > 1)
                {
                    var a = menu.ActiveTiles[0].rect; var b = menu.ActiveTiles[1].rect;
                    float gap = Vector3.Distance(a.TransformPoint(a.rect.center), b.TransformPoint(b.rect.center)) * 1000f - Mm(a.rect.width, a);
                    Measure($"gap between tiles: {gap:F1} mm");
                    Check(gap >= 12f, "tiles are at least 12 mm apart");
                }
            }
            foreach (string label in new[] { "Button New project", "Button Agents", "Button Settings", "Button Spaces", "Button Bring here", "Button Close", "Tab Tools" })
            {
                Transform t = FindDeep(menu.transform, label);
                Check(t != null, "found " + label);
                if (t == null) continue;
                var r = (RectTransform)t;
                Measure($"{label}: {Mm(r.rect.width, r):F0} x {Mm(r.rect.height, r):F0} mm");
                Check(Mm(r.rect.width, r) >= 28f && Mm(r.rect.height, r) >= 28f, label + " is a hand-sized target, at least 28 x 28 mm");
                // Inside the plate: a button pushed off its edge by a long label could not be reached.
                Vector3 local = canvasRoot.InverseTransformPoint(r.TransformPoint(r.rect.max));
                Check(local.x <= canvasRoot.rect.xMax + 0.5f, label + " stays inside the plate");
            }
            Shot("01_menu_desk_tools");
            LabelWeights(menu);

            // ---- tabs
            Check(Press(FindDeep(menu.transform, "Tab Projects")?.gameObject, out why), "a pointer reaches the Projects tab: " + why);
            yield return 0.4f;
            Check(shell.Tab == OSTab.Projects, "the Projects tab is selected");
            Check(menu.ActiveTiles.Count == 5, "Projects shows its five sample tiles (" + menu.ActiveTiles.Count + ")");
            Shot("02_menu_desk_projects");

            Check(Press(FindDeep(menu.transform, "Tab Artifacts")?.gameObject, out why), "a pointer reaches the Artifacts tab: " + why);
            yield return 0.4f;
            Check(shell.Tab == OSTab.Artifacts, "the Artifacts tab is selected");
            Check(menu.PageCount == 2 && menu.ActiveTiles.Count == 8, "nine artifacts make two pages of 8 and 1 (" + menu.PageCount + " pages, " + menu.ActiveTiles.Count + " tiles)");
            Shot("03_menu_desk_artifacts");
            Check(Press(FindNextButton(menu), out why), "a pointer reaches the next-page button: " + why);
            yield return 0.4f;
            Check(menu.Page == 1 && menu.ActiveTiles.Count == 1, "next shows the second page with one tile");
            Shot("04_menu_desk_artifacts_page2");

            // ---- open an item
            Check(Press(FindDeep(menu.transform, "Tab Tools")?.gameObject, out why), "a pointer reaches the Tools tab: " + why);
            yield return 0.4f;
            Check(shell.Tab == OSTab.Tools && menu.ActiveTiles.Count == 7, "the Tools tab shows seven tiles (" + menu.ActiveTiles.Count + ")");
            Check(menuPositionBefore == menu.transform.position, "the menu stayed where it was summoned (world-locked)");
            openedTool = null;
            OSTileView cad = menu.ActiveTiles.Count > 0 ? menu.ActiveTiles[0] : null;
            // The caption colour is set once, not every frame: the kit animator must not reach the label (it is renamed away from the path its curves name).
            bool captionsHold = menu.ActiveTiles.Count > 0;
            for (int i = 0; i < menu.ActiveTiles.Count; i++)
            {
                OSTileView t = menu.ActiveTiles[i];
                if (t.status == null || t.status.gameObject.name != OSUi.TileCaptionName || t.status.color != new Color(1f, 1f, 1f, 0.88f)) captionsHold = false;
                if (t.status != null) t.status.color = Color.red;  // if the animator still wrote it, the next frames would put grey back
            }
            yield return 0.3f;
            for (int i = 0; i < menu.ActiveTiles.Count; i++)
                if (menu.ActiveTiles[i].status != null && menu.ActiveTiles[i].status.color != Color.red) captionsHold = false;
            Check(captionsHold, "a tile's caption is renamed, holds its colour, and nothing rewrites it each frame (the animator no longer reaches it)");
            for (int i = 0; i < menu.ActiveTiles.Count; i++)
                if (menu.ActiveTiles[i].status != null) menu.ActiveTiles[i].status.color = new Color(1f, 1f, 1f, 0.88f);
            Check(cad != null && Press(cad.gameObject, out why), "a pointer reaches the CAD tile: " + why);
            yield return 0.6f;
            Check(openedTool == "cad", "pressing the CAD tile ran its open action (" + openedTool + ")");
            Check(!shell.MenuOpen, "pressing a tile closes the menu");
            yield return 0.3f;
            Check(!menu.Visible, "the menu has gone");

            // ---- the M key, then the Close buttons
            OSInput.Tap(Key.M);
            // The key is played out over game frames; an editor that is not focused ticks slowly, so wait for the menu, not a fixed time.
            for (float waitedForM = 0f; waitedForM < 4f && !shell.MenuOpen; waitedForM += 0.25f) yield return 0.25f;
            yield return 0.4f;
            Check(shell.MenuOpen && shell.LastSummon == OSSummon.Key, "M opens the menu");
            // A fixed menu may be beside existing work. Look at its anchor before aiming a pointer at its controls.
            DesktopRig.Current.LookAt(menu.LastCenter);
            yield return 0.3f;
            Check(Press(FindDeep(menu.transform, "Button Close")?.gameObject, out why), "a pointer reaches the Close button: " + why);
            yield return 0.5f;
            Check(!shell.MenuOpen, "the strip's Close button closes the menu");

            shell.OpenMenu(OSSummon.Code);
            yield return 0.6f;
            Check(Press(FindCloseX(menu), out why), "a pointer reaches the tab bar's close button: " + why);
            yield return 0.5f;
            Check(!shell.MenuOpen, "the tab bar's close button closes the menu");

            // ---- the strip buttons without their pages: they press, log, and don't throw
            shell.OpenMenu(OSSummon.Code);
            yield return 0.6f;
            Check(Press(FindDeep(menu.transform, "Button Agents")?.gameObject, out why), "a pointer reaches Agents: " + why);
            yield return 0.3f;
            shell.Windows.Close(OSAgentsWindow.WindowId);   // the strip now opens the real Agents window; put it away so it does not stay into the next step
            Check(shell.MenuOpen || OSShell.HasSystem("agents"), "Agents with no page registered leaves the menu open and doesn't throw");
            shell.CloseMenu();
            yield return 0.4f;

            // ---- Spaces: the strip opens the list where the menu was, within reach
            shell.OpenMenu(OSSummon.Code);
            yield return 0.6f;
            Vector3 menuAt = menu.LastCenter;
            Check(Press(FindDeep(menu.transform, "Button Spaces")?.gameObject, out why), "a pointer reaches Spaces: " + why);
            yield return 0.8f;
            OSWindow spaces = shell.Windows.Find(OSSpaces.WindowId);
            Check(spaces != null && !shell.MenuOpen, "Spaces opens the Spaces list and closes the menu");
            if (spaces != null)
            {
                var spacesRect = (RectTransform)spaces.Panel.transform;
                Vector3 spacesAt = spacesRect.TransformPoint(spacesRect.rect.center);
                float reach = Vector3.Distance(spacesAt, shell.Head.position);
                Measure($"Spaces list: {reach:F2} m from the eyes, {Vector3.Distance(spacesAt, menuAt):F2} m from where the menu was");
                Check(reach >= 0.4f && reach <= 0.6f && Vector3.Distance(spacesAt, menuAt) < 0.05f, $"the Spaces list opens where the menu was, within reach ({reach:F2} m)");
                yield return 1.2f;  // it reads the list once a second
                Transform everything = FindDeep(spaces.Panel.transform, "Button Close everything");
                Check(everything != null, "the Spaces list has Close everything");
                if (everything != null)
                {
                    var r = (RectTransform)everything;
                    Check(Mm(r.rect.height, r) >= 28f, $"its buttons are hand-sized ({Mm(r.rect.height, r):F0} mm tall)");
                }
                Shot("05a_spaces_list");
                shell.Windows.Close(OSSpaces.WindowId);
                yield return 0.4f;
            }

            // ---- the open menu stays anchored when the user turns well away from it
            shell.OpenMenu(OSSummon.Code);
            yield return 0.6f;
            int refaces = menu.Refaces;
            Vector3 anchoredPosition = menu.transform.position;
            Quaternion anchoredRotation = menu.transform.rotation;
            Transform turnHead = shell.Head;
            Vector3 facing0 = Vector3.ProjectOnPlane(turnHead.forward, Vector3.up).normalized;
            Vector3 turned = Quaternion.Euler(0f, 70f, 0f) * facing0;
            DesktopRig.Current.LookAt(turnHead.position + turned * 2f);
            yield return 1.6f;
            Check(menu.Refaces == refaces && Vector3.Distance(menu.transform.position, anchoredPosition) < 0.001f
                && Quaternion.Angle(menu.transform.rotation, anchoredRotation) < 0.1f, "turning 70 degrees leaves the menu anchored in the room");
            Check(Vector3.Distance(menu.transform.position, menu.LastCenter) < 0.01f, "and it has landed there");
            shell.CloseMenu();
            yield return 0.3f;
            DesktopRig.Current.LookAt(turnHead.position + facing0 * 2f);  // face the desk again for what follows
            yield return 0.4f;

            // ---- an empty tab
            OSRegistry.UnregisterSource(sampleArtifacts);
            shell.SetTab(OSTab.Artifacts);
            shell.OpenMenu(OSSummon.Code);
            yield return 0.7f;
            Check(menu.ActiveTiles.Count == 0, "the empty Artifacts tab has no tiles");
            Shot("05_menu_desk_empty");
            shell.CloseMenu();
            yield return 0.4f;
            shell.SetTab(OSTab.Tools);

            // ---- away from the desk
            workspace.presence.forceSeated = false;
            DesktopRig.Current.ToggleSeat();  // stand
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
            yield return 0.5f;
            Check(!table.Shown, "the table button is gone away from the desk");

            OSInput.Tap(Key.M);
            // The key is played out over game frames; an editor that is not focused ticks slowly, so wait for the menu, not a fixed time.
            for (float waitedForM = 0f; waitedForM < 4f && !shell.MenuOpen; waitedForM += 0.25f) yield return 0.25f;
            yield return 0.4f;
            Check(shell.MenuOpen, "M opens the menu away from the desk");
            Check(Press(FindDeep(menu.transform, "Tab Projects")?.gameObject, out why), "away: a pointer reaches the Projects tab: " + why);
            yield return 0.3f;
            Check(Press(FindDeep(menu.transform, "Tab Tools")?.gameObject, out why), "away: a pointer reaches the Tools tab: " + why);
            yield return 0.4f;
            Shot("06_menu_away");
            openedTool = null;
            Check(menu.ActiveTiles.Count > 1 && Press(menu.ActiveTiles[1].gameObject, out why), "away: a pointer reaches the Code tile: " + why);
            yield return 0.6f;
            Check(openedTool == "code" && !shell.MenuOpen, "away: a tile opens its item and closes the menu");
            OSInput.Tap(Key.M);
            yield return 0.7f;
            Check(Press(FindDeep(menu.transform, "Button Close")?.gameObject, out why), "away: a pointer reaches Close: " + why);
            yield return 0.5f;
            Check(!shell.MenuOpen, "away: Close closes the menu");
            yield return 0.3f;
        }

        // The lit tab is a light plate: its label and icon must be dark enough to read on it (WCAG contrast at least 4.5:1,
        // for text; the icon is held to the same).
        static void SelectedTabContrast(Toggle tab)
        {
            if (tab == null) { Check(false, "the selected tab exists to measure"); return; }
            Transform elements = FindDeep(tab.transform, "Elements");
            var label = elements != null ? elements.Find(OSUi.TabTitleName)?.GetComponent<TMPro.TMP_Text>() : null;
            var icon = elements != null ? elements.Find("Icon")?.GetComponent<Image>() : null;
            var fill = tab.transform.Find("Content/Background")?.GetComponent<Image>();
            if (label == null || icon == null || fill == null) { Check(false, "the selected tab has a label, an icon and a fill"); return; }
            Color plate = new Color(0.14f, 0.14f, 0.15f, 1f);  // what the lit fill sits on
            Color ground = Over(fill.color, plate);
            float labelContrast = Contrast(Over(label.color, ground), ground);
            float iconContrast = Contrast(Over(icon.color, ground), ground);
            Measure($"selected tab: fill {Hex(ground)}, label {Hex(Over(label.color, ground))} ({labelContrast:F1}:1), icon {Hex(Over(icon.color, ground))} ({iconContrast:F1}:1)");
            Check(labelContrast >= 4.5f, "the selected tab's label reads on its light fill (contrast " + labelContrast.ToString("F1") + ":1, at least 4.5)");
            Check(iconContrast >= 4.5f, "the selected tab's icon reads on its light fill (contrast " + iconContrast.ToString("F1") + ":1, at least 4.5)");

            // And as drawn: thin dark strokes on a light plate never reach the text colour, so the darkest pixel of the label
            // in the head camera's picture has to be dark enough too (the colours above passed while the label still looked pale).
            Camera cam = PetUser.HeadCamera;
            Texture2D picture = cam != null ? OSShot.Capture(1280, 800) : null;
            if (picture == null) { Debug.Log("[OSMenuTest] skip drawn label contrast: no camera or no graphics"); return; }
            var corners = new Vector3[4];
            label.rectTransform.GetWorldCorners(corners);
            float minX = 1f, maxX = 0f, minY = 1f, maxY = 0f;
            for (int i = 0; i < 4; i++)
            {
                Vector3 v = cam.WorldToViewportPoint(corners[i]);
                minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                minY = Mathf.Min(minY, v.y); maxY = Mathf.Max(maxY, v.y);
            }
            // Only the label's text, not the whole box: its preferred width centred in the box.
            float textFraction = Mathf.Clamp01(label.preferredWidth / Mathf.Max(1f, label.rectTransform.rect.width));
            float midX = (minX + maxX) * 0.5f, halfX = (maxX - minX) * 0.5f * textFraction;
            Color32[] pixels = picture.GetPixels32();
            UnityEngine.Object.DestroyImmediate(picture);
            float darkest = 1f, lightest = 0f;
            for (int y = Mathf.RoundToInt(minY * 800f); y <= Mathf.RoundToInt(maxY * 800f); y++)
                for (int x = Mathf.RoundToInt((midX - halfX) * 1280f); x <= Mathf.RoundToInt((midX + halfX) * 1280f); x++)
                {
                    if (x < 0 || x >= 1280 || y < 0 || y >= 800) continue;
                    Color32 p = pixels[y * 1280 + x];
                    float l = Luminance(new Color(p.r / 255f, p.g / 255f, p.b / 255f));
                    darkest = Mathf.Min(darkest, l);
                    lightest = Mathf.Max(lightest, l);
                }
            float drawn = (lightest + 0.05f) / (darkest + 0.05f);
            Measure($"selected tab label as drawn: darkest pixel luminance {darkest:F3}, fill {lightest:F3}, contrast {drawn:F1}:1");
            Check(drawn >= 5.5f, "the selected tab's label, as drawn, is dark on the light fill (contrast of its darkest pixel " + drawn.ToString("F1") + ":1, at least 5.5; it was 5.0 with the pale label)");
        }

        // How solid labels are drawn, from the head camera's picture. Ink is the average coverage of the label's text box: each pixel's
        // distance from the fill, as a share of the distance from the fill to the text colour, in display values. A thin face has less.
        // Dark labels on a light fill must be at least as solid as the white labels on the dark one (the SDF font draws dark-on-light thinner).
        static void LabelWeights(OSMenu menu)
        {
            Camera cam = PetUser.HeadCamera;
            Texture2D picture = cam != null ? OSShot.Capture(1280, 800) : null;
            if (picture == null) { Debug.Log("[OSMenuTest] skip label weights: no camera or no graphics"); return; }
            Color32[] pixels = picture.GetPixels32();
            UnityEngine.Object.DestroyImmediate(picture);
            float whiteSum = 0f, darkSum = 0f;
            int whites = 0, darks = 0;
            // (control, label path under it, dark text)
            string[][] labels =
            {
                new[] { "Tab Tools", "Content/Background/Elements/" + OSUi.TabTitleName, "dark" },
                new[] { "Tab Projects", "Content/Background/Elements/" + OSUi.TabTitleName, "white" },
                new[] { "Tab Artifacts", "Content/Background/Elements/" + OSUi.TabTitleName, "white" },
                new[] { "Button New project", "Content/Background/Elements/Text/Label", "dark" },
                new[] { "Button Agents", "Content/Background/Elements/Text/Label", "white" },
                new[] { "Button Settings", "Content/Background/Elements/Text/Label", "white" },
                new[] { "Button Bring here", "Content/Background/Elements/Text/Label", "white" },
            };
            foreach (var entry in labels)
            {
                Transform control = FindDeep(menu.transform, entry[0]);
                var label = control != null ? control.Find(entry[1])?.GetComponent<TMPro.TMP_Text>() : null;
                if (label == null) { Check(false, "found the label of " + entry[0]); continue; }
                bool dark = entry[2] == "dark";
                float ink = Ink(cam, pixels, label, dark);
                Measure($"label ink, {entry[0]} ({(dark ? "dark on light" : "white on dark")}): {ink:F3}");
                if (dark) { darkSum += ink; darks++; } else { whiteSum += ink; whites++; }
            }
            if (darks == 0 || whites == 0) return;
            float darkInk = darkSum / darks, whiteInk = whiteSum / whites;
            Measure($"label ink: dark on light {darkInk:F3}, white on dark {whiteInk:F3}, ratio {darkInk / Mathf.Max(0.0001f, whiteInk):F2}");
            Check(darkInk >= whiteInk, "the dark-on-light labels (selected tab, Build something) are drawn at least as solid as the white ones (ink " + darkInk.ToString("F3") + " against " + whiteInk.ToString("F3") + ")");
        }

        static float Ink(Camera cam, Color32[] pixels, TMPro.TMP_Text label, bool dark)
        {
            var corners = new Vector3[4];
            label.rectTransform.GetWorldCorners(corners);
            float minX = 1f, maxX = 0f, minY = 1f, maxY = 0f;
            for (int i = 0; i < 4; i++)
            {
                Vector3 v = cam.WorldToViewportPoint(corners[i]);
                minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                minY = Mathf.Min(minY, v.y); maxY = Mathf.Max(maxY, v.y);
            }
            float textFraction = Mathf.Clamp01(label.preferredWidth / Mathf.Max(1f, label.rectTransform.rect.width));
            float midX = (minX + maxX) * 0.5f, halfX = (maxX - minX) * 0.5f * textFraction;
            // Only the text's own height: cap line down to the baseline, centred in the box.
            float midY = (minY + maxY) * 0.5f, halfY = (maxY - minY) * 0.5f * Mathf.Clamp01(label.preferredHeight / Mathf.Max(1f, label.rectTransform.rect.height)) * 0.8f;
            int x0 = Mathf.Max(0, Mathf.RoundToInt((midX - halfX) * 1280f)), x1 = Mathf.Min(1279, Mathf.RoundToInt((midX + halfX) * 1280f));
            int y0 = Mathf.Max(0, Mathf.RoundToInt((midY - halfY) * 800f)), y1 = Mathf.Min(799, Mathf.RoundToInt((midY + halfY) * 800f));
            var values = new List<float>();
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    Color32 p = pixels[y * 1280 + x];
                    values.Add((0.299f * p.r + 0.587f * p.g + 0.114f * p.b) / 255f);
                }
            if (values.Count == 0) return 0f;
            var sorted = new List<float>(values);
            sorted.Sort();
            float fill = sorted[sorted.Count / 2];                    // the fill is most of the box
            float text = dark ? 0.153f : 1f;                           // the theme's text colours, in display values (0.153 is #272727's grey)
            float span = Mathf.Abs(text - fill);
            if (span < 0.05f) return 0f;
            float sum = 0f;
            foreach (float v in values) sum += Mathf.Clamp01(Mathf.Abs(v - fill) / span);
            return sum / values.Count;
        }

        static Color Over(Color top, Color under) => new Color(
            top.r * top.a + under.r * (1f - top.a), top.g * top.a + under.g * (1f - top.a), top.b * top.a + under.b * (1f - top.a), 1f);

        static float Luminance(Color c)
        {
            float R = Linear(c.r), G = Linear(c.g), B = Linear(c.b);
            return 0.2126f * R + 0.7152f * G + 0.0722f * B;
        }

        static float Linear(float v) => v <= 0.03928f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);

        static float Contrast(Color a, Color b)
        {
            float la = Luminance(a), lb = Luminance(b);
            return (Mathf.Max(la, lb) + 0.05f) / (Mathf.Min(la, lb) + 0.05f);
        }

        static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);

        static GameObject FindNextButton(OSMenu menu)
        {
            foreach (var b in menu.GetComponentsInChildren<Button>(false))
                if (b.name == "Button chevron_right") return b.gameObject;
            return null;
        }

        static GameObject FindCloseX(OSMenu menu)
        {
            foreach (var b in menu.GetComponentsInChildren<Button>(false))
                if (b.name == "Button close") return b.gameObject;
            return null;
        }
    }
}
