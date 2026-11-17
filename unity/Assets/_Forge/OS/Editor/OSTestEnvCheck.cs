using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Check Test Environment. No Play mode. It proves <see cref="OSTestEnv"/> does what the OS tests rely on, with the
    /// user's own values in place (they are never lost: every step runs between an Enter and a Leave that put them back), then
    /// reports the last run of every OS test: it FAILS if a test's last run left any preference or any file in the data folder
    /// different from before the run. Run an OS test (the whole OSPlayTest, say) and then this, to see what that run did.
    /// </summary>
    public static class OSTestEnvCheck
    {
        [MenuItem("Forge/OS/Check Test Environment")]
        static void Menu()
        {
            string report = Run();
            if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
        }

        public static string Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "FAIL os test environment check: leave Play mode first";
            if (!string.IsNullOrEmpty(OSTestEnv.InUse)) return "FAIL os test environment check: " + OSTestEnv.InUse + " is running";
            var sb = new StringBuilder();
            bool ok = true;
            string start = OSTestEnv.Snapshot();
            ok &= Step(sb, "a Claude address set and cleared during a run comes back exactly (or stays absent)", RestoresClaudeUrl);
            ok &= Step(sb, "an interrupted run's saved originals are what the next run saves, not the forced values", InterruptedRun);
            ok &= Step(sb, "a pref or a file a run leaves behind is reported", DetectsChanges);
            string end = OSTestEnv.Snapshot();
            string drift = OSTestEnv.Diff(start, end);
            ok &= Line(sb, "the check itself left the user's prefs and files as they were", drift.Length == 0 ? null : drift.Replace("\n", " | "));

            string reports = OSTestEnv.Reports;
            if (reports.Length == 0) sb.Append("  note no OS test has run since the editor opened, so there is no run to report on\n");
            foreach (string line in reports.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                ok &= Line(sb, "last run " + line.Substring(0, Math.Min(line.Length, 400)), line.Contains(": CHANGED") ? "the run changed the user's state" : null);
            return (ok ? "PASS" : "FAIL") + " os test environment check\n" + sb;
        }

        delegate string StepFn();

        static bool Step(StringBuilder sb, string name, StepFn fn)
        {
            string failure;
            try { failure = fn(); }
            catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; }
            return Line(sb, name, failure);
        }

        static bool Line(StringBuilder sb, string name, string failure)
        {
            sb.Append(failure == null ? "  ok   " : "  FAIL ").Append(name);
            if (failure != null) sb.Append(": ").Append(failure);
            sb.Append('\n');
            return failure == null;
        }

        static bool HasUrl() => PlayerPrefs.HasKey(ClaudeClient.UrlPref);
        static string Url() => PlayerPrefs.GetString(ClaudeClient.UrlPref, "");

        static string RestoresClaudeUrl()
        {
            bool had = HasUrl(); string value = Url();
            bool forceHad = EditorPrefs.HasKey(RigSelector.ForceDesktopPref); bool force = EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false);
            OSTestEnv.Enter("(selftest)");
            OSTestEnv.SetClaudeUrl("http://127.0.0.1:1/api/claude");
            if (Url() != "http://127.0.0.1:1/api/claude") return "the run's own address was not set";
            OSTestEnv.ClearClaudeUrl();
            if (HasUrl()) return "ClearClaudeUrl left a value";
            OSTestEnv.SetClaudeUrl("http://127.0.0.1:2/api/claude");
            if (!EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false)) return "Enter did not force desktop on";
            string diff = OSTestEnv.Leave(false);
            if (diff.Length != 0) return "Leave reports a difference: " + diff.Replace("\n", " | ");
            if (HasUrl() != had || Url() != value) return "the Claude address is not back (had=" + had + ")";
            if (EditorPrefs.HasKey(RigSelector.ForceDesktopPref) != forceHad || EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false) != force) return "the forced-desktop pref is not back";
            if (EditorPrefs.HasKey("OSTestEnv.Pending")) return "the pending marker is still set";
            return null;
        }

        static string InterruptedRun()
        {
            bool had = HasUrl(); string value = Url();
            bool force = EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false);
            bool voiceHad = EditorPrefs.HasKey(AlbieVoice.EditorOffPref); bool voice = EditorPrefs.GetBool(AlbieVoice.EditorOffPref, false);
            OSTestEnv.Enter("(selftest)");
            OSTestEnv.SetClaudeUrl("http://127.0.0.1:3/api/claude");
            // The run dies here: no Leave. The next run starts with the forced values and the loopback address in place.
            OSTestEnv.Enter("(selftest)");
            string diff = OSTestEnv.Leave(false);
            if (diff.Length != 0) return "after the second run Leave reports a difference: " + diff.Replace("\n", " | ");
            if (HasUrl() != had || Url() != value) return "the Claude address was taken from the interrupted run (had=" + had + ", now '" + Url() + "')";
            if (EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false) != force) return "the forced-desktop pref kept the forced value";
            if (EditorPrefs.HasKey(AlbieVoice.EditorOffPref) != voiceHad || EditorPrefs.GetBool(AlbieVoice.EditorOffPref, false) != voice) return "the voice-off pref is not back";
            return null;
        }

        static string DetectsChanges()
        {
            string probe = Path.Combine(Application.persistentDataPath, "ostestenv_probe.txt");
            const string prefKey = "Forge.OSTestEnvProbe";
            if (File.Exists(probe) || EditorPrefs.HasKey(prefKey)) return "a probe left by an earlier check is still there";
            try
            {
                OSTestEnv.Enter("(selftest)");
                File.WriteAllText(probe, "probe");
                EditorPrefs.SetString(prefKey, "x");
                string diff = OSTestEnv.Leave(false);
                if (!diff.Contains("ostestenv_probe.txt")) return "a new file in the data folder was not reported: " + diff;
                if (!diff.Contains(prefKey)) return "a new editor pref was not reported: " + diff;
                return null;
            }
            finally
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch (IOException) { }
                if (EditorPrefs.HasKey(prefKey)) EditorPrefs.DeleteKey(prefKey);
            }
        }
    }
}
