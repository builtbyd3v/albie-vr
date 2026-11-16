using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Forge.Artifacts;
using Forge.Notebook;
using UnityEditor;
using UnityEngine;

namespace Forge.OS.Editor
{
    /// <summary>
    /// The one place the OS tests keep the user's own state safe. Editor PlayerPrefs and EditorPrefs are the user's real ones,
    /// and so are the files in the app's data folder, so a test must never leave them changed.
    /// <see cref="Enter"/> saves what a test is about to overwrite (the forced-desktop and voice-off editor prefs, the Claude
    /// address the Agents tests point at a local socket) the first time only: a saved copy still waiting from an interrupted run
    /// is the real original and is put back before a new run saves anything. <see cref="Leave"/> puts every one of them back
    /// exactly (including "there was no value") and returns what still differs: any editor or player pref, or any file in the
    /// data folder. The saved copies live in EditorPrefs and the "run in progress" marker in SessionState, so a domain reload
    /// keeps both, and when no run is in progress (an interrupted run, Play mode stopped by hand, the editor reopened) the
    /// originals are put back by the safety net in this class's static constructor. <see cref="UseScratch"/> sends the notebook,
    /// the artifact library, the approvals and os_settings.json of a run to a folder of its own.
    /// </summary>
    [InitializeOnLoad]
    public static class OSTestEnv
    {
        const string PendingKey = "OSTestEnv.Pending";
        const string ForceDesktopHadKey = "OSTestEnv.ForceDesktopHad", ForceDesktopValueKey = "OSTestEnv.ForceDesktopValue";
        const string VoiceOffHadKey = "OSTestEnv.VoiceOffHad", VoiceOffValueKey = "OSTestEnv.VoiceOffValue";
        const string ClaudeHadKey = "OSTestEnv.ClaudeHad", ClaudeValueKey = "OSTestEnv.ClaudeValue";
        const string InUseKey = "OSTestEnv.InUse", BeforeKey = "OSTestEnv.Before", ReportsKey = "OSTestEnv.Reports";

        // The keys that held a test's own "Was" copy before this class existed; nothing reads them any more.
        static readonly string[] LegacyTests =
        {
            "OSMenuTest", "OSWindowsTest", "OSToolsTest", "OSProjectsTest", "OSArtifactsTest", "OSPlayTest", "OSDriveTest",
            "OSPlannerTest", "OSAgentsTest", "OSSettingsTest", "OSCornerTest", "OSWatchTest"
        };

        static string scratchRoot;

        static OSTestEnv()
        {
            DeleteLegacyKeys();
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
            // Nothing is running (a domain reload keeps SessionState; a new editor session starts empty), yet a saved copy is pending.
            EditorApplication.delayCall += () =>
            {
                if (!string.IsNullOrEmpty(SessionState.GetString(InUseKey, "")) || !EditorPrefs.GetBool(PendingKey, false)) return;
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                RestoreSaved();
                Debug.Log("[OSTestEnv] a test run was interrupted: the preferences it had saved are back.");
            };
        }

        // Play mode ended with a test still marked in progress: it was stopped by hand, so its originals go back now.
        static void OnPlayMode(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredEditMode) return;
            if (string.IsNullOrEmpty(SessionState.GetString(InUseKey, ""))) return;
            string test = SessionState.GetString(InUseKey, "");
            string diff = Leave();
            Debug.Log("[OSTestEnv] Play mode ended while " + test + " was marked running; its saved preferences are back." + (diff.Length == 0 ? "" : " Still different:\n" + diff));
        }

        static void DeleteLegacyKeys()
        {
            for (int i = 0; i < LegacyTests.Length; i++)
            {
                if (EditorPrefs.HasKey(LegacyTests[i] + ".ForceDesktopWas")) EditorPrefs.DeleteKey(LegacyTests[i] + ".ForceDesktopWas");
                if (EditorPrefs.HasKey(LegacyTests[i] + ".VoiceOffWas")) EditorPrefs.DeleteKey(LegacyTests[i] + ".VoiceOffWas");
            }
        }

        /// <summary>The name of the test that is marked running, or "" when none is.</summary>
        public static string InUse => SessionState.GetString(InUseKey, "");

        /// <summary>The report of every test's last run, one line each: "OSPlayTest: unchanged" or "OSPlayTest: CHANGED ...".</summary>
        public static string Reports => SessionState.GetString(ReportsKey, "");

        // ---------------------------------------------------------------- enter and leave

        /// <summary>
        /// Starts a run: saves the forced-desktop and voice-off editor prefs and the Claude address pref (only when no saved copy
        /// is pending: a pending one is restored first, so what is saved is always the user's own), takes the "before" picture,
        /// then forces desktop on and voice off.
        /// </summary>
        public static void Enter(string test)
        {
            if (EditorPrefs.GetBool(PendingKey, false)) RestoreSaved();
            SaveOne(RigSelector.ForceDesktopPref, ForceDesktopHadKey, ForceDesktopValueKey);
            SaveOne(AlbieVoice.EditorOffPref, VoiceOffHadKey, VoiceOffValueKey);
            EditorPrefs.SetBool(ClaudeHadKey, PlayerPrefs.HasKey(ClaudeClient.UrlPref));
            EditorPrefs.SetString(ClaudeValueKey, PlayerPrefs.GetString(ClaudeClient.UrlPref, ""));
            EditorPrefs.SetBool(PendingKey, true);
            SessionState.SetString(InUseKey, test ?? "test");
            SessionState.SetString(BeforeKey, Snapshot());
            EditorPrefs.SetBool(RigSelector.ForceDesktopPref, true);
            EditorPrefs.SetBool(AlbieVoice.EditorOffPref, true);
        }

        /// <summary>
        /// Ends a run: the scratch folder is dropped, every saved value is put back exactly, and the state is compared with the
        /// "before" picture. Returns "" when nothing differs, else one line per difference. The result is also kept in
        /// <see cref="Reports"/> under the test's name unless <paramref name="record"/> is false. Safe to call twice.
        /// </summary>
        public static string Leave(bool record = true)
        {
            string test = SessionState.GetString(InUseKey, "");
            ReleaseScratch();
            if (EditorPrefs.GetBool(PendingKey, false)) RestoreSaved();
            string before = SessionState.GetString(BeforeKey, "");
            SessionState.SetString(InUseKey, "");
            SessionState.SetString(BeforeKey, "");
            if (before.Length == 0) return "";
            string diff = Diff(before, Snapshot());
            if (record && test.Length > 0) Record(test, diff);
            return diff;
        }

        static void Record(string test, string diff)
        {
            var kept = new List<string>();
            foreach (string line in Reports.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (!line.StartsWith(test + ": ", StringComparison.Ordinal)) kept.Add(line);
            kept.Add(test + ": " + (diff.Length == 0 ? "unchanged" : "CHANGED " + diff.Replace("\n", " | ")));
            SessionState.SetString(ReportsKey, string.Join("\n", kept));
        }

        static void SaveOne(string pref, string hadKey, string valueKey)
        {
            EditorPrefs.SetBool(hadKey, EditorPrefs.HasKey(pref));
            EditorPrefs.SetBool(valueKey, EditorPrefs.GetBool(pref, false));
        }

        static void RestoreOne(string pref, string hadKey, string valueKey)
        {
            if (EditorPrefs.GetBool(hadKey, false)) EditorPrefs.SetBool(pref, EditorPrefs.GetBool(valueKey, false));
            else if (EditorPrefs.HasKey(pref)) EditorPrefs.DeleteKey(pref);
            EditorPrefs.DeleteKey(hadKey);
            EditorPrefs.DeleteKey(valueKey);
        }

        // Puts the saved originals back and clears the pending marker. Called by Leave, by Enter when a copy is still pending, and by the safety net.
        static void RestoreSaved()
        {
            RestoreOne(RigSelector.ForceDesktopPref, ForceDesktopHadKey, ForceDesktopValueKey);
            RestoreOne(AlbieVoice.EditorOffPref, VoiceOffHadKey, VoiceOffValueKey);
            if (EditorPrefs.GetBool(ClaudeHadKey, false)) PlayerPrefs.SetString(ClaudeClient.UrlPref, EditorPrefs.GetString(ClaudeValueKey, ""));
            else if (PlayerPrefs.HasKey(ClaudeClient.UrlPref)) PlayerPrefs.DeleteKey(ClaudeClient.UrlPref);
            PlayerPrefs.Save();
            EditorPrefs.DeleteKey(ClaudeHadKey);
            EditorPrefs.DeleteKey(ClaudeValueKey);
            EditorPrefs.DeleteKey(PendingKey);
        }

        // ---------------------------------------------------------------- what a test may set while it runs

        /// <summary>Points the Claude address at <paramref name="url"/> for the run. <see cref="Leave"/> puts the user's own back. Needs <see cref="Enter"/>.</summary>
        public static void SetClaudeUrl(string url)
        {
            if (!EditorPrefs.GetBool(PendingKey, false)) throw new InvalidOperationException("OSTestEnv.Enter was not called, so the user's Claude address is not saved.");
            PlayerPrefs.SetString(ClaudeClient.UrlPref, url);
        }

        /// <summary>Drops the run's own Claude address (between two steps). The user's own comes back at <see cref="Leave"/>.</summary>
        public static void ClearClaudeUrl()
        {
            if (!EditorPrefs.GetBool(PendingKey, false)) throw new InvalidOperationException("OSTestEnv.Enter was not called, so the user's Claude address is not saved.");
            if (PlayerPrefs.HasKey(ClaudeClient.UrlPref)) PlayerPrefs.DeleteKey(ClaudeClient.UrlPref);
        }

        /// <summary>
        /// Sends everything a run could write to a folder of its own: os_settings.json, the artifact library, the approvals file
        /// and the notebook (the truck project open in it). Returns the folder. Call in Play mode (the OS resets its overrides
        /// when Play starts); <see cref="Leave"/> or <see cref="ReleaseScratch"/> puts the real locations back and deletes it.
        /// </summary>
        public static string UseScratch(string name)
        {
            ReleaseScratch();
            scratchRoot = Path.Combine(Path.GetTempPath(), "os_testenv_" + name);
            try { if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, true); } catch (IOException) { }
            Directory.CreateDirectory(scratchRoot);
            OSSettings.SetPathForTests(Path.Combine(scratchRoot, "os_settings.json"));
            ArtifactLibrary.RootOverride = Path.Combine(scratchRoot, "artifacts");
            ArtifactApprovals.FileOverride = Path.Combine(scratchRoot, "artifacts", "approvals.json");
            NotebookStore.Open(NotebookStore.DefaultProject, scratchRoot);
            return scratchRoot;
        }

        /// <summary>Back to the real locations (the notebook is the app's own again), the scratch folder deleted. Safe to call when none is in use.</summary>
        public static void ReleaseScratch()
        {
            if (scratchRoot == null) return;
            OSSettings.SetPathForTests(null);
            ArtifactLibrary.RootOverride = null;
            ArtifactApprovals.FileOverride = null;
            if (Application.isPlaying)
            {
                NotebookStore.Flush();
                NotebookStore.Open(NotebookStore.DefaultProject);
            }
            try { if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, true); }
            catch (Exception e) { Debug.Log("[OSTestEnv] couldn't remove " + scratchRoot + ": " + e.Message); }
            scratchRoot = null;
        }

        // ---------------------------------------------------------------- the picture of the user's state

        // Player pref names Unity itself rewrites every session; they are not the user's.
        static readonly string[] UnityOwnPrefs = { "unity.", "unity_connect.", "UnityGraphicsQuality", "client-user-id" };

        /// <summary>One line per player pref (Windows registry), per Forge/Albie editor pref, per known pref key and per file in the data folder (path, size, hash).</summary>
        public static string Snapshot()
        {
            var lines = new List<string>();
            lines.Add("pref ForceDesktop=" + Describe(EditorPrefs.HasKey(RigSelector.ForceDesktopPref), EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false).ToString()));
            lines.Add("pref VoiceOff=" + Describe(EditorPrefs.HasKey(AlbieVoice.EditorOffPref), EditorPrefs.GetBool(AlbieVoice.EditorOffPref, false).ToString()));
            lines.Add("pref ClaudeUrl=" + Describe(PlayerPrefs.HasKey(ClaudeClient.UrlPref), PlayerPrefs.GetString(ClaudeClient.UrlPref, "")));
#if UNITY_EDITOR_WIN
            AddRegistry(lines, @"Software\Unity\UnityEditor\" + Application.companyName + @"\" + Application.productName, "player pref ", UnityOwnPrefs, null);
            AddRegistry(lines, @"Software\Unity Technologies\Unity Editor 5.x", "editor pref ", null, new[] { "Forge.", "Albie." });
#endif
            AddFiles(lines);
            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines);
        }

        static string Describe(bool has, string value) => has ? "'" + value + "'" : "(absent)";

#if UNITY_EDITOR_WIN
        static void AddRegistry(List<string> lines, string subKey, string label, string[] skipPrefixes, string[] onlyPrefixes)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey))
                {
                    if (key == null) return;
                    foreach (string raw in key.GetValueNames())
                    {
                        string name = raw;
                        int h = raw.LastIndexOf("_h", StringComparison.Ordinal);
                        if (h > 0) name = raw.Substring(0, h);
                        if (!Allowed(name, skipPrefixes, onlyPrefixes)) continue;
                        object value = key.GetValue(raw);
                        string text = value is byte[] bytes ? Encoding.UTF8.GetString(bytes).TrimEnd('\0') : Convert.ToString(value);
                        lines.Add(label + name + "=" + text);
                    }
                }
            }
            catch (Exception e) { lines.Add(label + "(registry unreadable: " + e.GetType().Name + ")"); }
        }

        static bool Allowed(string name, string[] skipPrefixes, string[] onlyPrefixes)
        {
            if (skipPrefixes != null)
                for (int i = 0; i < skipPrefixes.Length; i++)
                    if (name.StartsWith(skipPrefixes[i], StringComparison.Ordinal)) return false;
            if (onlyPrefixes == null) return true;
            for (int i = 0; i < onlyPrefixes.Length; i++)
                if (name.StartsWith(onlyPrefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }
#endif

        static void AddFiles(List<string> lines)
        {
            string root = Application.persistentDataPath;
            try
            {
                if (!Directory.Exists(root)) return;
                using (var md5 = MD5.Create())
                {
                    foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    {
                        string rel = file.Substring(root.Length).Replace('\\', '/').TrimStart('/');
                        if (rel.StartsWith("Unity/", StringComparison.Ordinal)) continue;     // Unity's own analytics folder
                        var info = new FileInfo(file);
                        string hash = "";
                        if (info.Length <= 8 * 1024 * 1024)
                        {
                            using (var stream = File.OpenRead(file)) hash = BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "");
                        }
                        lines.Add("file " + rel + " size=" + info.Length + " md5=" + hash);
                    }
                }
            }
            catch (Exception e) { lines.Add("file (data folder unreadable: " + e.GetType().Name + ")"); }
        }

        /// <summary>Lines in <paramref name="after"/> that are not in <paramref name="before"/> (+) and the other way round (-). "" when equal.</summary>
        public static string Diff(string before, string after)
        {
            var b = new HashSet<string>(before.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            var a = new HashSet<string>(after.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (string line in b) if (!a.Contains(line)) sb.Append("- ").Append(line).Append('\n');
            foreach (string line in a) if (!b.Contains(line)) sb.Append("+ ").Append(line).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }
    }
}
