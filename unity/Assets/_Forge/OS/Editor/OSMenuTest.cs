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
            EditorPrefs.SetBool(RigSelector.ForceDesktopPref, EditorPrefs.GetBool("OSMenuTest.ForceDesktopWas", false));
            EditorPrefs.SetBool(AlbieVoice.EditorOffPref, EditorPrefs.GetBool("OSMenuTest.VoiceOffWas", false));
            EditorApplication.isPlaying = false;
        }

        [MenuItem("Forge/OS/Run Menu Test")]
        public static void Run()
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSMenuTest] Leave Play mode first."); return; }
            EditorPrefs.SetBool("OSMenuTest.ForceDesktopWas", EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false));
            EditorPrefs.SetBool("OSMenuTest.VoiceOffWas", EditorPrefs.GetBool(AlbieVoice.EditorOffPref, false));
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
            string summary = $"[OSMenuTest] {passed} passed, {failed} failed";
            LastResult = (failed == 0 ? "PASS " : "FAIL ") + summary + (failed == 0 ? "" : "\n" + string.Join("\n", failures));
            if (failed == 0) Debug.Log(summary);
            else Debug.LogError(summary + "\n" + string.Join("\n", failures));
            SessionState.SetBool(ActiveKey, false);
            if (keepPlaying) return;
            EditorPrefs.SetBool(RigSelector.ForceDesktopPref, EditorPrefs.GetBool("OSMenuTest.ForceDesktopWas", false));
            EditorPrefs.SetBool(AlbieVoice.EditorOffPref, EditorPrefs.GetBool("OSMenuTest.VoiceOffWas", false));
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
            foreach (string real in new[] { "cad", "code", "train", "notebook", "messages", "gestures" }) OSRegistry.UnregisterTool(real);
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
            Check(Mathf.Abs(distance - OSSettings.Current.menuDistance) < 0.01f, "the menu is menuDistance from the eyes");
            Check(Mathf.Abs(drop - OSSettings.Current.menuDropDegrees) < 1f, "the menu is menuDropDegrees below eye level");
            Check(fov < 70f, "the menu fits in 70 degrees of horizontal view (" + fov.ToString("F1") + ")");
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
            foreach (string label in new[] { "Button Build something", "Button Agents", "Button Settings", "Button Bring windows here", "Button Close", "Tab Tools" })
            {
                Transform t = FindDeep(menu.transform, label);
                Check(t != null, "found " + label);
                if (t == null) continue;
                var r = (RectTransform)t;
                Measure($"{label}: {Mm(r.rect.width, r):F0} x {Mm(r.rect.height, r):F0} mm");
                Check(Mm(r.rect.width, r) >= 22f && Mm(r.rect.height, r) >= 22f, label + " is at least 22 x 22 mm");
            }
            Shot("01_menu_desk_tools");

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
            Check(cad != null && Press(cad.gameObject, out why), "a pointer reaches the CAD tile: " + why);
            yield return 0.6f;
            Check(openedTool == "cad", "pressing the CAD tile ran its open action (" + openedTool + ")");
            Check(!shell.MenuOpen, "pressing a tile closes the menu");
            yield return 0.3f;
            Check(!menu.Visible, "the menu has gone");

            // ---- the M key, then the Close buttons
            OSInput.Tap(Key.M);
            yield return 0.8f;
            Check(shell.MenuOpen && shell.LastSummon == OSSummon.Key, "M opens the menu");
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
            Check(shell.MenuOpen || OSShell.HasSystem("agents"), "Agents with no page registered leaves the menu open and doesn't throw");
            shell.CloseMenu();
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
            yield return 0.8f;
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
