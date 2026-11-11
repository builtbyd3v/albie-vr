using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Forge.Artifacts
{
    /// <summary>
    /// The stand-in for Person 3's connector host (C-18) that section 16.5 asks for: "a local fixture connector". It answers
    /// one source, <c>connector:source_control/list_commits</c>, with this repo's real commits from the last 7 days, so
    /// "What shipped this week?" shows the team's work instead of made-up rows:
    /// <list type="number">
    /// <item>On a computer (the Editor, a desktop build), it runs <c>git log</c> on the checkout.</item>
    /// <item>On a Quest there's no git, so it reads the copy the build step (<c>Editor/RepoSnapshotBuild.cs</c>) saved
    ///       into <c>Resources/ArtifactData/Repo/</c>, marked <c>saved_copy</c> with the day it was taken.</item>
    /// <item>Otherwise it returns null, and the host falls back to the bundled sample, labelled "Sample data".</item>
    /// </list>
    /// It only installs itself when nothing else has: when Person 3's connector host sets <see cref="ArtifactData.Provider"/>,
    /// theirs is used and this one steps aside.
    /// </summary>
    public static class LocalRepoConnector
    {
        public const string Source = "connector:source_control/list_commits";
        public const string SavedResource = "ArtifactData/Repo/source_control_list_commits";
        public const int Days = 7, MaxCommits = 120;
        const float CacheSeconds = 60f;

        /// <summary>Checks point this at a folder of their own; null means this checkout.</summary>
        public static string RepoOverride;
        /// <summary>Checks set this to test the Quest path on a computer.</summary>
        public static bool SkipGit;
        /// <summary>Checks set this to test the fallback with no saved copy.</summary>
        public static bool SkipSaved;

        static JObject cached;
        static float cachedAt = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            RepoOverride = null;
            SkipGit = SkipSaved = false;
            cached = null;
            cachedAt = -1f;
        }

        // After ArtifactData clears its Provider, and before any scene asks for data.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install() => InstallIfFree();

        /// <summary>Becomes the provider unless another one (the real connector host) is already set. True if it did.</summary>
        public static bool InstallIfFree()
        {
            if (ArtifactData.Provider != null) return false;
            ArtifactData.Provider = Provide;
            return true;
        }

        public static bool Installed => ArtifactData.Provider == Provide;

        /// <summary>The <see cref="ArtifactData.Provider"/> function: a snapshot for our one source, else null.</summary>
        public static JObject Provide(string source)
        {
            if (source != Source) return null;
            if (cached != null && Time.realtimeSinceStartup - cachedAt < CacheSeconds) return (JObject)cached.DeepClone();
            var snapshot = SkipGit ? null : FromGit(DateTime.UtcNow);
            if (snapshot == null && !SkipSaved) snapshot = Saved();
            if (snapshot == null) return null;
            cached = snapshot;
            cachedAt = Time.realtimeSinceStartup;
            return (JObject)snapshot.DeepClone();
        }

        public static void ClearCache()
        {
            cached = null;
            cachedAt = -1f;
        }

        /// <summary>The copy saved into the build, or null.</summary>
        public static JObject Saved()
        {
            var text = Resources.Load<TextAsset>(SavedResource);
            if (text == null) return null;
            try { return JObject.Parse(text.text); }
            catch (Exception e)
            {
                Debug.LogWarning("[LocalRepoConnector] the saved copy doesn't parse: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// The last <see cref="Days"/> days of commits from <c>git log</c>, newest first, merges left out, as a C-12 section 7
        /// snapshot with the bundled sample's columns. <c>lines_changed</c> adds the lines added and removed in text files,
        /// leaving out Unity's <c>.meta</c> bookkeeping; a commit that only touched binary files has null (a grey stub), never
        /// a made-up 0. Null if there's no git or no repo here.
        /// </summary>
        public static JObject FromGit(DateTime now)
        {
            if (Application.platform == RuntimePlatform.Android) return null;
            string root = RepoOverride ?? Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string output = Git(root, $"log --no-merges --since={Days}.days --format=@%h%x09%aN%x09%aI%x09%s --numstat -n {MaxCommits}");
            if (output == null) return null;

            var rows = new JArray();
            JArray row = null;
            long lines = 0;
            bool counted = false;
            void Flush()
            {
                if (row == null) return;
                row[3] = counted ? new JValue(lines) : JValue.CreateNull();
                rows.Add(row);
            }
            foreach (var raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("@"))
                {
                    Flush();
                    var f = line.Substring(1).Split('\t');
                    if (f.Length < 4) { row = null; continue; }
                    string day = DateTimeOffset.TryParse(f[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                        ? at.ToString("ddd", CultureInfo.InvariantCulture) : null;
                    row = new JArray(f[0], f[1], f[3], JValue.CreateNull(), day);
                    lines = 0;
                    counted = false;
                    continue;
                }
                var n = line.Split('\t');
                if (row == null || n.Length != 3 || n[2].EndsWith(".meta")) continue;
                if (long.TryParse(n[0], out long added) && long.TryParse(n[1], out long removed))
                {
                    lines += added + removed;
                    counted = true;
                }
            }
            Flush();

            return new JObject
            {
                ["fetched_at"] = now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["source"] = Source,
                ["sample"] = false,
                ["saved_copy"] = false,
                ["origin"] = "git log on this computer (the local fixture connector, until the connector host lands)",
                ["columns"] = new JArray(
                    Column("sha", "string"), Column("author", "string"), Column("message", "string"),
                    Column("lines_changed", "number"), Column("day", "string")),
                ["rows"] = rows,
            };
        }

        static JObject Column(string name, string type) => new JObject { ["name"] = name, ["type"] = type };

        static string Git(string root, string args)
        {
            if (!Directory.Exists(root)) return null;
            try
            {
                var info = new ProcessStartInfo("git", $"-C \"{root}\" {args}")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                };
                using (var p = Process.Start(info))
                {
                    if (p == null) return null;
                    string text = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(5000) || p.ExitCode != 0) return null;
                    return text;
                }
            }
            catch (Exception)
            {
                return null;  // no git on this machine
            }
        }

        /// <summary>A copy for a headset with no git: the same snapshot, marked as saved, with the day it was taken.</summary>
        public static JObject ForSaving(JObject live)
        {
            if (live == null) return null;
            var copy = (JObject)live.DeepClone();
            copy["saved_copy"] = true;
            copy["origin"] = "git log, saved into the build (the local fixture connector, until the connector host lands)";
            return copy;
        }
    }
}
