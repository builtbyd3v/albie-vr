using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Check OS: the OS's own checks, with no Play mode. Each part adds its checks here. Run() returns
    /// "PASS os check ..." or "FAIL os check ..." with a line per check; RunBatch() is for -executeMethod.
    /// </summary>
    public static class OSCheck
    {
        [MenuItem("Forge/OS/Check OS")]
        static void Menu()
        {
            string report = Run();
            if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
        }

        /// <summary>For -executeMethod.</summary>
        public static void RunBatch()
        {
            string report = Run();
            if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
            EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
        }

        delegate string CheckFn(out string note);

        public static string Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            ok &= Check(sb, "the OSShell prefab has the shell, the windows and the keys", ShellPrefab);
            ok &= Check(sb, "os.json parses and its four tools have handlers", ToolDefinitions);
            ok &= Check(sb, "os_build.json defines os_answer and it has a handler", BuildToolDefinition);
            ok &= Check(sb, "the tools answer an error, not an exception, with no shell", ToolsWithoutShell);
            ok &= Check(sb, "the registry fills, finds, replaces and orders", Registry);
            ok &= Check(sb, "every tab has an empty-state sentence", EmptyTexts);
            ok &= Check(sb, "settings round-trip, clamp, and survive a damaged file", Settings);
            ok &= Check(sb, "the kit asset loads and every UI Set reference resolves", KitReferences);
            ok &= Check(sb, "every friendly icon name resolves to the Filled atlas, and the builder maps them all", Icons);
            ok &= Check(sb, "the Albie theme is derived from the UI Set dark theme", Theme);
            ok &= Check(sb, "the menu prefab and the shell's menu and table button exist", MenuPrefab);
            ok &= Check(sb, "touch targets and gaps at the OS unit are at least 22 mm and 12 mm", TouchSizes);
            ok &= Check(sb, "OSUi builds each control from the kit and a press runs its handler", UiControls);
            ok &= Check(sb, "sounds are silent and safe with no shell", SoundsWithoutShell);
            ok &= Check(sb, "the voice rules still pass", VoiceRules);
            ok &= Check(sb, "the workspace hook is inert until the OS sets it, and the OS names resolve", WorkspaceHook);
            ok &= Check(sb, "OSModes is quiet with no scene and the OS window pieces are in place", ModesAndWindows);
ok &= Check(sb, "the OS kit has every prefab, theme and icon the OS asks for", KitComplete);
ok &= Check(sb, "every entry has a snake_case id, a title, an icon the kit resolves and an open action", EntriesComplete);
ok &= Check(sb, "ids and titles are unique on each tab", EntriesUnique);
ok &= Check(sb, "every status line and open flag answers without throwing and reads like the UI", StatusDelegates);
ok &= Check(sb, "the tabs hold the seven tools, the four starter projects and every artifact template", TabsHoldWhatTheSpecSays);
ok &= Check(sb, "os_open's description names every tool id and kind", OsOpenDescription);
ok &= Check(sb, "the table button is a finger-sized target", TableButtonSize);
ok &= Check(sb, "the planner tree ends in a plan within three questions", PlannerTree);
ok &= Check(sb, "the Agents page registers under the strip's id and the crew's Bring home hook is in place", AgentsPage);
            return (ok ? "PASS" : "FAIL") + " os check\n" + sb;
        }

        static bool Check(StringBuilder sb, string name, CheckFn fn)
        {
            string failure, note;
            try { failure = fn(out note); }
            catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; note = null; }
            sb.Append(failure == null ? "  ok   " : "  FAIL ").Append(name);
            if (failure != null) sb.Append(": ").Append(failure);
            else if (note != null) sb.Append(" (").Append(note).Append(')');
            sb.Append('\n');
            return failure == null;
        }

        // ---------------------------------------------------------------- the checks

        static string ShellPrefab(out string note)
        {
            note = null;
            var prefab = Resources.Load<GameObject>(OSBootstrap.Resource);
            if (prefab == null) return "Resources/OSShell.prefab is missing (Forge > OS > Build OS Prefabs)";
            if (prefab.GetComponent<OSShell>() == null) return "no OSShell on it";
            if (prefab.GetComponent<OSWindows>() == null) return "no OSWindows on it";
            if (prefab.GetComponent<OSKeys>() == null) return "no OSKeys on it";
if (prefab.GetComponent<OSTableButton>() == null) return "no OSTableButton on it";
if (prefab.GetComponentInChildren<OSMenu>(true) == null) return "no OSMenu under it";
            return null;
        }

        static string BuildToolDefinition(out string note)
        {
            note = null;
            var asset = Resources.Load<TextAsset>("AlbieTools/os_build");
            if (asset == null) return "Resources/AlbieTools/os_build.json is missing";
            var defined = new List<string>();
            foreach (JObject def in JArray.Parse(asset.text))
            {
                string name = (string)def["name"];
                defined.Add(name);
                if (string.IsNullOrEmpty((string)def["description"])) return name + " has no description";
                if ((string)def["parameters"]?["type"] != "object") return name + " has no parameters schema";
            }
            foreach (string name in Forge.OS.OSBuildTools.Names)
                if (!defined.Contains(name)) return "os_build.json doesn't define " + name;
            if (defined.Count != Forge.OS.OSBuildTools.Names.Length) return "os_build.json defines " + defined.Count + " tools, the code has " + Forge.OS.OSBuildTools.Names.Length;
            if (!AlbieTools.Has(Forge.OS.OSBuildTools.Names[0])) Forge.OS.OSBuildTools.Register();
            foreach (string name in Forge.OS.OSBuildTools.Names)
                if (!AlbieTools.Has(name)) return name + " is defined but has no handler";
            note = "os_answer";
            return null;
        }

        static string ToolDefinitions(out string note)
        {
            note = null;
            var asset = Resources.Load<TextAsset>("AlbieTools/os");
            if (asset == null) return "Resources/AlbieTools/os.json is missing";
            var defined = new List<string>();
            foreach (JObject def in JArray.Parse(asset.text))
            {
                string name = (string)def["name"];
                defined.Add(name);
                if (string.IsNullOrEmpty((string)def["description"])) return name + " has no description";
                if ((string)def["parameters"]?["type"] != "object") return name + " has no parameters schema";
            }
            foreach (string name in OSTools.Names)
            {
                if (!defined.Contains(name)) return "os.json doesn't define " + name;
            }
            if (defined.Count != OSTools.Names.Length) return "os.json defines " + defined.Count + " tools, the code has " + OSTools.Names.Length;

            // Registering is what the app does at start; in the editor it has to be done by hand.
            if (!AlbieTools.Has(OSTools.Names[0])) OSTools.Register();
            foreach (string name in OSTools.Names)
                if (!AlbieTools.Has(name)) return name + " is defined but has no handler";
            note = defined.Count + " tools";
            return null;
        }

        // The Workspace hook the OS uses: with nothing set it behaves as it always did (a mode needs a desk), and it opens
        // away from one only when the OS supplies a free frame. No Play mode: Workspace has no edit-mode callbacks.
        static string WorkspaceHook(out string note)
        {
            note = null;
            // In a preview scene, so the open scene isn't marked as changed.
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var go = new GameObject("OSCheck workspace");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            try
            {
                var ws = go.AddComponent<Workspace>();
                if (ws.KeepOpenAway || ws.DockRetired || ws.FreeFrame != null || ws.InFreeFrame) return "a new Workspace has an OS hook set";
                if (ws.CanOpen) return "a Workspace with no presence says it can open";
                ws.presence = go.AddComponent<DeskPresence>();
                if (ws.CanOpen) return "away from a desk, with no OS free frame, the Workspace says it can open";
                ws.FreeFrame = (out Vector3 position, out Quaternion rotation) => { position = Vector3.zero; rotation = Quaternion.identity; return true; };
                if (!ws.CanOpen) return "with an OS free frame the Workspace still can't open away from a desk";
                ws.FreeFrame = null;
                if (ws.CanOpen) return "dropping the free frame didn't put the desk rule back";
                var slots = new List<Workspace.ItemSlot>();
                ws.GetSlots(slots);
                if (slots.Count != 0 || ws.IsOpenItem(go)) return "a Workspace with no mode open reports open items";
                var gesture = go.AddComponent<GestureWindow>();
                if (gesture.StaysAway) return "a GestureWindow stays away by default";
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            }
            return null;
        }

        static string ModesAndWindows(out string note)
        {
            note = null;
            if (OSShell.Current != null) { note = "skipped: a shell is running"; return null; }
            string[] expected = { "cad", "ml", "code", "messages" };
            if (OSModes.ModeIds.Length != expected.Length) return "OSModes lists " + OSModes.ModeIds.Length + " modes";
            for (int i = 0; i < expected.Length; i++)
            {
                if (OSModes.ModeIds[i] != expected[i]) return "mode " + i + " is " + OSModes.ModeIds[i];
                if (string.IsNullOrEmpty(OSModes.Title(expected[i]))) return "no title for " + expected[i];
                if (OSModes.IsOpen(expected[i])) return expected[i] + " reports open with no scene";
                if (OSModes.Open(expected[i])) return "OSModes.Open(" + expected[i] + ") worked with no workspace";
                if (OSWindows.GroupId(expected[i]) != "mode:" + expected[i]) return "the group id of " + expected[i] + " is unexpected";
            }
            OSModes.Close();  // no workspace: nothing to do, no exception
            if (OSModes.Open("") || OSModes.Open(null)) return "OSModes.Open opened an empty id";
            return null;
        }

        static string ToolsWithoutShell(out string note)
        {
            note = null;
            if (OSShell.Current != null) { note = "skipped: a shell is running"; return null; }
            var calls = new (string tool, JObject args)[]
            {
                ("os_menu", new JObject { ["open"] = true }),
                ("os_tab", new JObject { ["tab"] = "projects" }),
                ("os_open", new JObject { ["kind"] = "tool", ["id"] = "cad" }),
                ("os_windows", new JObject { ["action"] = "recenter" }),
            };
            foreach (var call in calls)
            {
                JObject result = AlbieTools.Call(call.tool, call.args);
                if (result["error"] == null) return call.tool + " answered without an error: " + result;
                if (string.IsNullOrEmpty((string)result["error"])) return call.tool + " gave an empty error";
            }
            return null;
        }

        static string Registry(out string note)
        {
            note = null;
            Action<List<OSEntry>> source = null;
            try
            {
                OSRegistry.RegisterTool(new OSEntry { id = "zz_check_a", title = "A", icon = "cube", open = () => { } });
                OSRegistry.RegisterTool(new OSEntry { id = "zz_check_b", title = "B", icon = "cube" });
                OSRegistry.RegisterTool(new OSEntry { id = "zz_check_a", title = "A2", icon = "cube", open = () => { } });
                source = list =>
                {
                    list.Add(new OSEntry { id = "zz_check_art", title = "Art", icon = "cube" });
                    list.Add(null);
                };
                int changes = 0;
                Action count = () => changes++;
                OSRegistry.Changed += count;
                OSRegistry.RegisterSource(OSTab.Artifacts, source);
                OSRegistry.NotifyChanged();
                OSRegistry.Changed -= count;
                if (changes != 2) return "Changed fired " + changes + " times, expected 2";

                var list = new List<OSEntry> { new OSEntry { id = "junk" } };
                OSRegistry.Fill(OSTab.Tools, list);
                if (list.Exists(e => e.id == "junk")) return "Fill didn't clear the list it was given";
                int a = list.FindIndex(e => e.id == "zz_check_a"), b = list.FindIndex(e => e.id == "zz_check_b");
                if (a < 0 || b < 0) return "fixed tools are missing from the Tools tab";
                if (list.FindAll(e => e.id == "zz_check_a").Count != 1) return "registering the same id twice made two entries";
                if (list[a].title != "A2") return "the second registration didn't replace the first";
                if (a > b) return "tools are out of registration order";
                if (list.Exists(e => e.id == "zz_check_art")) return "a source for Artifacts leaked into Tools";

                OSRegistry.Fill(OSTab.Artifacts, list);
                if (list.Exists(e => e.id == "zz_check_a")) return "a tool leaked into Artifacts";
                if (!list.Exists(e => e.id == "zz_check_art")) return "the Artifacts source's entry is missing";
                if (list.Exists(e => e == null)) return "a null entry came through";
                if (list.Find(e => e.id == "zz_check_art").tab != OSTab.Artifacts) return "a source entry wasn't given its tab";

                if (OSRegistry.Find(OSTab.Tools, "zz_check_b") == null) return "Find missed a tool";
                if (OSRegistry.Find(OSTab.Tools, "nope") != null) return "Find invented a tool";
                if (OSRegistry.Find(OSTab.Artifacts, "zz_check_art") == null) return "Find missed a source entry";
                if (OSRegistry.Find(OSTab.Projects, "zz_check_art") != null) return "Find looked in the wrong tab";

                // A shell can't open a "tool" that has no action, and says why.
                return null;
            }
            finally
            {
                OSRegistry.UnregisterTool("zz_check_a");
                OSRegistry.UnregisterTool("zz_check_b");
                if (source != null) OSRegistry.UnregisterSource(source);
            }
        }

        static string EmptyTexts(out string note)
        {
            note = null;
            foreach (OSTab tab in Enum.GetValues(typeof(OSTab)))
            {
                string text = OSRegistry.EmptyText(tab);
                if (string.IsNullOrWhiteSpace(text)) return tab + " has no empty-state text";
                if (!text.EndsWith(".")) return tab + "'s text isn't a sentence: " + text;
            }
            return null;
        }

        static string Settings(out string note)
        {
            note = null;
            string path = Path.Combine(Application.temporaryCachePath, "os_check", "os_settings.json");
            var logs = Debug.unityLogger.logEnabled;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path)) File.Delete(path);
                OSSettings.SetPathForTests(path);

                var s = OSSettings.Current;
                if (!s.tableButton || !s.watchTurn || !s.cornerButton || s.cornerOnLeft || s.gestureDisplay || s.flatLayout) return "the defaults are wrong";
                if (!Mathf.Approximately(s.menuDistance, 0.5f) || !Mathf.Approximately(s.menuDropDegrees, 15f)) return "the menu placement defaults are wrong";

                s.tableButton = false;
                s.cornerOnLeft = true;
                s.menuDistance = 0.62f;
                s.menuDropDegrees = 22f;
                s.flatLayout = true;
                int changed = 0;
                Action count = () => changed++;
                OSSettings.Changed += count;
                s.Save();
                OSSettings.Reload();
                OSSettings.Changed -= count;
                if (changed != 2) return "Changed fired " + changed + " times, expected 2";
                if (!File.Exists(path)) return "Save wrote no file";
                var back = OSSettings.Current;
                if (ReferenceEquals(back, s)) return "Reload kept the old object";
                if (back.tableButton || !back.cornerOnLeft || !back.flatLayout || !back.watchTurn) return "the values didn't round-trip";
                if (!Mathf.Approximately(back.menuDistance, 0.62f) || !Mathf.Approximately(back.menuDropDegrees, 22f)) return "the sliders didn't round-trip";

                back.menuDistance = 9f;
                back.menuDropDegrees = -4f;
                back.Save();
                OSSettings.Reload();
                if (!Mathf.Approximately(OSSettings.Current.menuDistance, OSSettings.MaxDistance)) return "an over-range distance wasn't clamped";
                if (!Mathf.Approximately(OSSettings.Current.menuDropDegrees, OSSettings.MinDrop)) return "an under-range drop wasn't clamped";

                // Damaged, empty and half-there files: the defaults (or the part that's there), no exception. The one warning is expected, so keep it out of the console.
                Debug.unityLogger.logEnabled = false;
                File.WriteAllText(path, "{ this is not json");
                OSSettings.Reload();
                if (!OSSettings.Current.tableButton || !Mathf.Approximately(OSSettings.Current.menuDistance, 0.5f)) return "a damaged file didn't give the defaults";
                File.WriteAllText(path, "");
                OSSettings.Reload();
                if (!OSSettings.Current.tableButton) return "an empty file didn't give the defaults";
                File.WriteAllText(path, "{\"cornerOnLeft\": true, \"menuDistance\": 0.4}");
                OSSettings.Reload();
                Debug.unityLogger.logEnabled = logs;
                if (!OSSettings.Current.cornerOnLeft || !Mathf.Approximately(OSSettings.Current.menuDistance, 0.4f) || !OSSettings.Current.tableButton)
                    return "a partial file didn't keep what it had and default the rest";

                OSSettings.Current.tableButton = false;
                OSSettings.ResetToDefaults();
                if (!OSSettings.Current.tableButton) return "ResetToDefaults didn't";
                note = "file " + new FileInfo(path).Length + " bytes";
                return null;
            }
            finally
            {
                Debug.unityLogger.logEnabled = logs;
                OSSettings.SetPathForTests(null);
                try { Directory.Delete(Path.GetDirectoryName(path), true); } catch (IOException) { }
            }
        }

        const string AtlasPath = "Packages/com.meta.xr.sdk.interaction/Runtime/Sample/Textures/OCUIIconSet/OCUI_24_Filled_2x.png";

        static string KitReferences(out string note)
        {
            note = null;
            var kit = Resources.Load<OSKit>(OSKit.ResourceName);
            if (kit == null) return "Resources/OSKit.asset is missing (Forge > OS > Build OS Prefabs)";
            string[] buttonPaths = { "Content/Background/Elements/Icon", "Content/Background/Elements/Text/Label" };
            var prefabs = new (string name, GameObject prefab, string[] paths)[]
            {
                ("backplate", kit.backplate, new[] { "CanvasRoot/UIBackplate" }),
                ("primaryButton", kit.primaryButton, buttonPaths),
                ("secondaryButton", kit.secondaryButton, buttonPaths),
                ("borderlessButton", kit.borderlessButton, buttonPaths),
                ("destructiveButton", kit.destructiveButton, buttonPaths),
                ("textTile", kit.textTile, new[] { "Content/Background/Elements/Icon", "Content/Background/Elements/Label", "Content/Background/Elements/Label (1)" }),
                ("tabToggle", kit.tabToggle, new[] { "Content/Background/Elements/Icon", "Content/Background/Elements/Label" }),
                ("switchToggle", kit.switchToggle, new string[0]),
                ("slider", kit.slider, new string[0]),
            };
            foreach (var (name, prefab, paths) in prefabs)
            {
                if (prefab == null) return name + " is not set";
                foreach (string path in paths)
                    if (prefab.transform.Find(path) == null) return name + " has no child '" + path + "' (the UI Set changed?)";
            }
            if (kit.primaryButton.GetComponent<UnityEngine.UI.Button>() == null) return "primaryButton isn't the Button-based prefab";
            if (kit.textTile.GetComponent<UnityEngine.UI.Button>() == null) return "textTile isn't the Button-based prefab";
            if (kit.tabToggle.GetComponent<UnityEngine.UI.Toggle>() == null) return "tabToggle has no Toggle";
            if (kit.switchToggle.GetComponent<UnityEngine.UI.Toggle>() == null) return "switchToggle has no Toggle";
            if (kit.slider.GetComponent<UnityEngine.UI.Slider>() == null) return "slider has no Slider";
            if (kit.backplate.GetComponent<Oculus.Interaction.PokeInteractable>() == null || kit.backplate.GetComponent<Oculus.Interaction.RayInteractable>() == null
                || kit.backplate.GetComponent<Oculus.Interaction.PointableCanvas>() == null) return "the backplate lost its poke, ray or pointable canvas";
            if (kit.theme == null) return "theme is not set";
            if (kit.font == null) return "font is not set";
            if (kit.pressClip == null || kit.releaseClip == null || kit.openClip == null || kit.closeClip == null || kit.errorClip == null) return "a sound clip is not set";
            if (!Mathf.Approximately(kit.unitMeters, 0.00065f)) return "unitMeters is " + kit.unitMeters + ", not 0.00065";
            // Referenced, not copied: every prefab and clip is still the package's own file.
            foreach (UnityEngine.Object o in new UnityEngine.Object[] { kit.backplate, kit.primaryButton, kit.textTile, kit.tabToggle, kit.slider, kit.pressClip })
                if (!AssetDatabase.GetAssetPath(o).StartsWith("Packages/com.meta.xr.sdk.interaction/")) return o.name + " isn't the package's file: " + AssetDatabase.GetAssetPath(o);
            note = "9 prefabs, theme, font, 5 clips";
            return null;
        }

        static string Icons(out string note)
        {
            note = null;
            var kit = Resources.Load<OSKit>(OSKit.ResourceName);
            if (kit == null) return "Resources/OSKit.asset is missing";
            foreach (string name in OSKit.RequiredIcons)
            {
                Sprite sprite = kit.Icon(name);
                if (sprite == null) return "icon '" + name + "' doesn't resolve";
                if (AssetDatabase.GetAssetPath(sprite) != AtlasPath) return "icon '" + name + "' isn't from the Filled atlas";
                if (!sprite.name.EndsWith("_24_Filled")) return "icon '" + name + "' is '" + sprite.name + "', not a 24 px Filled sprite";
                bool mapped = false;
                foreach (var entry in OSPrefabBuilder.IconMap) if (entry.name == name) mapped = true;
                if (!mapped) return "the builder has no mapping for '" + name + "'";
            }
            if (kit.Icon("nope") != null) return "an unknown icon name resolved";
            if (kit.Icon(null) != null) return "a null icon name resolved";
            note = OSKit.RequiredIcons.Length + " icons";
            return null;
        }

        static string Theme(out string note)
        {
            note = null;
            var theme = AssetDatabase.LoadAssetAtPath<Oculus.Interaction.UITheme>(OSPrefabBuilder.ThemePath);
            var dark = AssetDatabase.LoadAssetAtPath<Oculus.Interaction.UITheme>("Packages/com.meta.xr.sdk.interaction/Runtime/Sample/Objects/UISet/Themes/UIThemeQuest_Dark.asset");
            if (theme == null) return OSPrefabBuilder.ThemePath + " is missing";
            if (dark == null) return "the UI Set dark theme isn't there";
            if (theme == dark) return "the Albie theme is the package's own asset, not a copy";
            if (theme.acPrimaryButton != dark.acPrimaryButton || theme.acToggleButton != dark.acToggleButton || theme.acSecondaryButton != dark.acSecondaryButton)
                return "the Albie theme's animators differ from the dark theme's";
            if (theme.textFontMedium == null) return "the theme has no font";
            var kit = Resources.Load<OSKit>(OSKit.ResourceName);
            if (kit == null || kit.theme != theme) return "the kit doesn't point at the Albie theme";
            return null;
        }

        static string MenuPrefab(out string note)
        {
            note = null;
            var menuPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(OSPrefabBuilder.MenuPath);
            if (menuPrefab == null) return OSPrefabBuilder.MenuPath + " is missing";
            var menu = menuPrefab.GetComponent<OSMenu>();
            if (menu == null) return "no OSMenu on the menu prefab";
            var so = new SerializedObject(menu);
            foreach (string field in new[] { "visual", "canvasRoot", "plate", "group", "themeManager" })
                if (so.FindProperty(field).objectReferenceValue == null) return "OSMenu." + field + " isn't wired";
            var manager = menuPrefab.GetComponentInChildren<Oculus.Interaction.UIThemeManager>(true);
            var themes = manager != null ? new SerializedObject(manager).FindProperty("_themes") : null;
            if (themes == null || themes.arraySize != 1 || themes.GetArrayElementAtIndex(0).objectReferenceValue == null) return "the menu's theme manager has no theme";
            if (!PrefabUtility.IsAnyPrefabInstanceRoot(menuPrefab.transform.Find("Backplate").gameObject)) return "the backplate isn't a nested UI Set prefab";

            var shell = Resources.Load<GameObject>(OSBootstrap.Resource);
            if (shell == null) return "Resources/OSShell.prefab is missing";
            if (shell.GetComponentInChildren<OSMenu>(true) == null) return "the shell prefab has no menu";
            if (shell.GetComponent<OSTableButton>() == null) return "the shell prefab has no table button";
            if (shell.GetComponent<AudioSource>() == null) return "the shell prefab has no AudioSource for OSSounds";
            return null;
        }

        static string TouchSizes(out string note)
        {
            float unit = OSKit.Current != null ? OSKit.Current.unitMeters : 0.00065f;
            float target = OSUi.MinTarget * unit * 1000f, gap = OSUi.MinGap * unit * 1000f;
            note = target.ToString("F1") + " mm target, " + gap.ToString("F1") + " mm gap";
            if (target < 22f) return "the minimum target is " + target + " mm";
            if (gap < 12f) return "the minimum gap is " + gap + " mm";
            if (OSUi.ButtonHeight < OSUi.MinTarget) return "buttons are shorter than the minimum target";
            if (OSUi.BodyText < 16f || OSUi.SmallText < 14f) return "text sizes are under the contract's 16 and 14";
            return null;
        }

        static string UiControls(out string note)
        {
            note = null;
            if (OSKit.Current == null) return "no kit";
            var root = new GameObject("OSCheck UI", typeof(RectTransform));
            try
            {
                var parent = root.transform;
                int clicks = 0, toggles = 0;
                foreach (OSButtonStyle style in System.Enum.GetValues(typeof(OSButtonStyle)))
                {
                    var button = OSUi.Button(parent, "Label " + style, () => clicks++, style, "check");
                    if (button == null) return style + " button wasn't built";
                    var rect = (RectTransform)button.transform;
                    if (rect.sizeDelta.x < OSUi.MinTarget || rect.sizeDelta.y < OSUi.MinTarget) return style + " button is smaller than a finger target: " + rect.sizeDelta;
                    var label = rect.Find("Content/Background/Elements/Text/Label")?.GetComponent<TMPro.TMP_Text>();
                    if (label == null || label.text != "Label " + style) return style + " button has the wrong label";
                    button.onClick.Invoke();
                }
                if (clicks != 4) return "onClick ran the handler " + clicks + " times for 4 buttons";
                var iconOnly = OSUi.Button(parent, null, null, OSButtonStyle.Borderless, "close");
                if (iconOnly == null || iconOnly.transform.Find("Content/Background/Elements/Icon").GetComponent<UnityEngine.UI.Image>().sprite == null) return "an icon-only button has no icon";
                var toggle = OSUi.Toggle(parent, "A switch", true, on => toggles++);
                if (toggle == null || !toggle.isOn) return "the switch wasn't built on";
                toggle.isOn = false;
                if (toggles != 1) return "the switch handler ran " + toggles + " times";
                var slider = OSUi.Slider(parent, "A slider", 0f, 10f, 4f, v => { });
                if (slider == null || !Mathf.Approximately(slider.value, 4f)) return "the slider wasn't built at its value";
                var group = root.AddComponent<UnityEngine.UI.ToggleGroup>();
                var tab = OSUi.Tab(parent, "Tab", "tools", group, () => { });
                if (tab == null) return "the tab wasn't built";
                var tile = OSUi.Tile(parent, true, 188f, 108f);
                if (tile == null || tile.title == null || tile.status == null || tile.outline == null) return "the outline tile wasn't built";
                tile.Show(new OSEntry { id = "x", title = "Title", icon = "cube", status = () => "status" });
                if (tile.title.text != "Title" || tile.status.text != "status" || tile.icon.sprite == null) return "a tile didn't show its entry";
                var text = OSUi.Text(parent, "text", 16f, true);
                if (text == null || text.fontSize < 14f) return "text wasn't built";
                var row = OSUi.Row(parent, 40f);
                var column = OSUi.Column(parent);
                var section = OSUi.Section(parent, "Heading");
                if (row == null || column == null || section == null) return "a layout helper returned null";
                OSUi.Clear(section);
                note = "4 buttons, icon button, switch, slider, tab, tile, text, row, column, section";
                return null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static string SoundsWithoutShell(out string note)
        {
            note = null;
            if (OSShell.Current != null) { note = "skipped: a shell is running"; return null; }
            OSSounds.Open();
            OSSounds.Close();
            OSSounds.Press();
            OSSounds.Error();
            return null;
        }

        static string VoiceRules(out string note)
        {
            note = null;
            string report = VoiceRulesCheck.Run();
            return report.StartsWith("PASS") ? null : report.Replace('\n', ' ');
        }

        static readonly (OSTab tab, string type)[] PartTypes =
        {
            (OSTab.Tools, "Forge.OS.OSToolsCatalog"),
            (OSTab.Projects, "Forge.OS.OSProjects"),
            (OSTab.Artifacts, "Forge.OS.OSArtifacts"),
        };

        static void EnsureRegistered()
        {
            var probe = new List<OSEntry>();
            foreach (var part in PartTypes)
            {
                OSRegistry.Fill(part.tab, probe);
                if (probe.Count > 0) continue;
                Type type = Type.GetType(part.type + ", Assembly-CSharp");
                MethodInfo register = type != null
                    ? type.GetMethod("Register", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null)
                    : null;
                if (register != null) register.Invoke(null, null);
            }
        }

        static readonly Regex SnakeCase = new Regex("^[a-z0-9]+(_[a-z0-9]+)*$");
        // The UI never says a brand (contract rule) and never says a placeholder word.
        static readonly Regex Banned = new Regex(@"\b(Slack|VS ?Code|Meta|Quest|Sketchfab|TODO|lorem)\b", RegexOptions.IgnoreCase);

        static string KitComplete(out string note)
        {
            note = null;
            OSKit kit = OSKit.Current;
            if (kit == null) return "Resources/OSKit.asset is missing (Forge > OS > Build OS Prefabs)";
            var missing = new List<string>();
            if (kit.backplate == null) missing.Add("backplate");
            if (kit.primaryButton == null) missing.Add("primaryButton");
            if (kit.secondaryButton == null) missing.Add("secondaryButton");
            if (kit.borderlessButton == null) missing.Add("borderlessButton");
            if (kit.destructiveButton == null) missing.Add("destructiveButton");
            if (kit.textTile == null) missing.Add("textTile");
            if (kit.tabToggle == null) missing.Add("tabToggle");
            if (kit.switchToggle == null) missing.Add("switchToggle");
            if (kit.slider == null) missing.Add("slider");
            if (kit.theme == null) missing.Add("theme");
            foreach (string icon in OSKit.RequiredIcons)
                if (kit.Icon(icon) == null) missing.Add("icon '" + icon + "'");
            if (missing.Count > 0) return "the kit lacks: " + string.Join(", ", missing);
            note = OSKit.RequiredIcons.Length + " icons";
            return null;
        }

        static string EntriesComplete(out string note)
        {
            note = null;
            EnsureRegistered();
            OSKit kit = OSKit.Current;
            if (kit == null) return "Resources/OSKit.asset is missing";
            var bad = new List<string>();
            var list = new List<OSEntry>();
            int total = 0;
            foreach (OSTab tab in Enum.GetValues(typeof(OSTab)))
            {
                OSRegistry.Fill(tab, list);
                foreach (OSEntry e in list)
                {
                    total++;
                    string who = tab + "/" + (string.IsNullOrEmpty(e.id) ? "(no id)" : e.id);
                    if (string.IsNullOrWhiteSpace(e.id)) bad.Add(who + " has no id");
                    else if (!SnakeCase.IsMatch(e.id)) bad.Add(who + " is not snake_case");
                    if (string.IsNullOrWhiteSpace(e.title)) bad.Add(who + " has no title");
                    if (string.IsNullOrWhiteSpace(e.icon)) bad.Add(who + " has no icon name");
                    else if (kit.Icon(e.icon) == null) bad.Add(who + " icon '" + e.icon + "' is not in the kit");
                    if (e.open == null) bad.Add(who + " has no open action");
                    if (e.tab != tab) bad.Add(who + " carries tab " + e.tab);
                }
            }
            if (total == 0) return "no entries are registered on any tab";
            if (bad.Count > 0) return string.Join("; ", bad);
            note = total + " entries";
            return null;
        }

        static string EntriesUnique(out string note)
        {
            note = null;
            EnsureRegistered();
            var list = new List<OSEntry>();
            foreach (OSTab tab in Enum.GetValues(typeof(OSTab)))
            {
                OSRegistry.Fill(tab, list);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (OSEntry e in list)
                {
                    if (!ids.Add(e.id ?? "")) return tab + " has the id '" + e.id + "' twice";
                    if (!titles.Add(e.title ?? "")) return tab + " has two tiles titled '" + e.title + "'";
                }
            }
            return null;
        }

        static string StatusDelegates(out string note)
        {
            note = null;
            EnsureRegistered();
            var problems = new List<string>();
            var list = new List<OSEntry>();
            int checkedCount = 0;
            foreach (OSTab tab in Enum.GetValues(typeof(OSTab)))
            {
                OSRegistry.Fill(tab, list);
                foreach (OSEntry e in list)
                {
                    string who = tab + "/" + e.id;
                    checkedCount++;
                    try
                    {
                        string line = e.status != null ? e.status() : null;
                        if (line != null)
                        {
                            if (line.Length > 40) problems.Add(who + " status is " + line.Length + " characters: '" + line + "'");
                            if (Banned.IsMatch(line)) problems.Add(who + " status says a brand or placeholder word: '" + line + "'");
                        }
                        if (e.isOpen != null) e.isOpen();
                    }
                    catch (Exception ex)
                    {
                        problems.Add(who + " threw " + ex.GetType().Name + ": " + ex.Message);
                    }
                    if (e.title != null && Banned.IsMatch(e.title)) problems.Add(who + " title says a brand or placeholder word: '" + e.title + "'");
                }
            }
            if (problems.Count > 0) return string.Join("; ", problems);
            note = checkedCount + " entries";
            return null;
        }

        static string TabsHoldWhatTheSpecSays(out string note)
        {
            note = null;
            EnsureRegistered();
            var missing = new List<string>();

            // Drive (the hand transmitter, spec 5.8) is its own part: it is required once its window exists.
            bool driveBuilt = Type.GetType("Forge.OS.OSDriveWindow, Assembly-CSharp") != null;
            foreach (string id in new[] { "cad", "code", "train", "notebook", "drive", "messages", "gestures" })
            {
                if (id == "drive" && !driveBuilt) continue;
                if (OSRegistry.Find(OSTab.Tools, id) == null) missing.Add("tool " + id);
            }

            foreach (string id in new[] { "proj_truck", "proj_quadcopter", "proj_rocket", "proj_classifier" })
                if (OSRegistry.Find(OSTab.Projects, id) == null) missing.Add("project " + id);

            // Every artifact template is reachable: some entry has its id, an id ending "_<template>", or its title.
            var artifacts = new List<OSEntry>();
            OSRegistry.Fill(OSTab.Artifacts, artifacts);
            foreach (string template in Forge.Artifacts.ArtifactLibrary.Templates)
            {
                JObject manifest, scene;
                string title = Forge.Artifacts.ArtifactLibrary.TryTemplate(template, out manifest, out scene) ? (string)manifest["title"] : null;
                bool found = artifacts.Exists(e => e.id == template
                    || (e.id != null && e.id.EndsWith("_" + template, StringComparison.Ordinal))
                    || (title != null && string.Equals(e.title, title, StringComparison.OrdinalIgnoreCase)));
                if (!found) missing.Add("artifact template " + template);
            }

            if (missing.Count > 0) return "missing: " + string.Join(", ", missing);
            if (!driveBuilt) note = "Drive is not built yet";
            return null;
        }

        static string OsOpenDescription(out string note)
        {
            note = null;
            EnsureRegistered();
            var asset = Resources.Load<TextAsset>("AlbieTools/os");
            if (asset == null) return "Resources/AlbieTools/os.json is missing";
            JObject open = null;
            foreach (JObject def in JArray.Parse(asset.text))
                if ((string)def["name"] == "os_open") open = def;
            if (open == null) return "os.json has no os_open";
            string description = (string)open["description"] ?? "";
            var tools = new List<OSEntry>();
            OSRegistry.Fill(OSTab.Tools, tools);
            var absent = new List<string>();
            foreach (OSEntry e in tools)
                if (description.IndexOf(e.id, StringComparison.Ordinal) < 0) absent.Add(e.id);
            if (absent.Count > 0) return "os_open's description doesn't name: " + string.Join(", ", absent);
            JArray kinds = open["parameters"]?["properties"]?["kind"]?["enum"] as JArray;
            if (kinds == null) return "os_open has no kind enum";
            foreach (string kind in new[] { "tool", "project", "artifact", "system" })
            {
                bool has = false;
                foreach (JToken k in kinds) if ((string)k == kind) has = true;
                if (!has) return "os_open's kind enum lacks " + kind;
            }
            return null;
        }

        static string TableButtonSize(out string note)
        {
            note = null;
            var prefab = Resources.Load<GameObject>(OSBootstrap.Resource);
            var button = prefab != null ? prefab.GetComponent<OSTableButton>() : null;
            if (button == null) return "the shell prefab has no OSTableButton";
            float mm = button.ButtonMeters * 1000f;
            if (mm < 22f) return "the table button is " + mm.ToString("F1") + " mm, the minimum is 22";
            note = mm.ToString("F0") + " mm";
            return null;
        }

        static string AgentsPage(out string note)
        {
            note = null;
            Forge.OS.OSAgentsWindow.Register();
            if (!OSShell.HasSystem("agents")) return "Register() did not register the 'agents' page";
            var member = typeof(Forge.Pet.AlbieCrewMember);
            if (member.GetMethod("SetHome") == null) return "AlbieCrewMember has no SetHome (the Pet hook is missing)";
            if (member.GetProperty("Home") == null || member.GetProperty("HomeSpot") == null || member.GetProperty("BodyHidden") == null) return "AlbieCrewMember is missing Home, HomeSpot or BodyHidden";
            if (typeof(Forge.Pet.AlbieCrew).GetMethod("NotifyHomeChanged") == null) return "AlbieCrew has no NotifyHomeChanged";
            // With no scene there is no crew: nothing is home, nothing is brought, and asking for a spot is a plain no.
            if (Forge.Pet.AlbieCrew.Instance != null) { note = "skipped the no-crew calls: a crew is running"; return null; }
            if (Forge.OS.OSAgentHome.HomeCount() != 0) return "HomeCount() is not 0 with no crew";
            if (Forge.OS.OSAgentHome.BringHome(null)) return "BringHome(null) said it found a pad";
            if (Forge.OS.OSAgentHome.TryPadSpot(null, out _)) return "TryPadSpot found a spot with no shell";
            if (Forge.OS.OSAgentHome.BringAllHome() != 0 || Forge.OS.OSAgentHome.SendAllOut() != 0) return "Bring all home or Send all out moved someone with no crew";
            return null;
        }

        static string PlannerTree(out string note)
        {
            note = null;
            List<string> problems = Forge.OS.OSPlanner.Validate();
            if (problems.Count > 0)
                return problems[0] + (problems.Count > 1 ? " (and " + (problems.Count - 1) + " more)" : "");
            note = Forge.OS.OSPlanner.PathCount() + " paths";
            return null;
        }
    }
}
