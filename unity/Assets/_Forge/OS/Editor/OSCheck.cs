using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
            ok &= Check(sb, "the tools answer an error, not an exception, with no shell", ToolsWithoutShell);
            ok &= Check(sb, "the registry fills, finds, replaces and orders", Registry);
            ok &= Check(sb, "every tab has an empty-state sentence", EmptyTexts);
            ok &= Check(sb, "settings round-trip, clamp, and survive a damaged file", Settings);
            ok &= Check(sb, "the voice rules still pass", VoiceRules);
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

        static string VoiceRules(out string note)
        {
            note = null;
            string report = VoiceRulesCheck.Run();
            return report.StartsWith("PASS") ? null : report.Replace('\n', ' ');
        }
    }
}
