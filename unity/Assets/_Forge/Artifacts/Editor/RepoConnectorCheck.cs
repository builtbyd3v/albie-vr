using System;
using System.IO;
using System.Text;
using Forge.Artifacts;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless checks for the local fixture connector (<see cref="LocalRepoConnector"/>), the stand-in for Person 3's
/// connector host: git gives a valid, real snapshot; repo_activity opens on it as connector data, not sample data; it
/// answers only its own source; it never replaces a provider someone else installed; with no git it reads the saved copy,
/// and Albie says which day that's from; with neither, the bundled sample comes back, still labelled. The build step
/// writes a git-ignored copy. Forge > Artifacts > Check Repo Connector.
/// </summary>
public static class RepoConnectorCheck
{
    [MenuItem("Forge/Artifacts/Check Repo Connector")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    public static void RunBatch()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
        EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
    }

    public static string Run()
    {
        string root = Path.Combine(Application.temporaryCachePath, "repo_connector_check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string beforeRoot = ArtifactLibrary.RootOverride;
        var beforeProvider = ArtifactData.Provider;
        ArtifactLibrary.RootOverride = root;
        var sb = new StringBuilder();
        bool ok = true;
        try
        {
            ok &= Check(sb, "git gives a valid snapshot of this repo's real commits", Git);
            ok &= Check(sb, "repo_activity opens on it as connector data, not sample data", Opens);
            ok &= Check(sb, "it answers only source_control/list_commits", OnlyItsSource);
            ok &= Check(sb, "it never replaces a provider someone else installed (the real connector host)", StepsAside);
            ok &= Check(sb, "with no git it reads the saved copy, and Albie says which day it's from", SavedCopy);
            ok &= Check(sb, "with neither, the bundled sample comes back, still labelled as sample", Fallback);
            ok &= Check(sb, "the build step writes a copy git ignores", BuildStep);
        }
        finally
        {
            ArtifactLibrary.RootOverride = beforeRoot;
            ArtifactData.Provider = beforeProvider;
            LocalRepoConnector.RepoOverride = null;
            LocalRepoConnector.SkipGit = LocalRepoConnector.SkipSaved = false;
            LocalRepoConnector.ClearCache();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return (ok ? "PASS" : "FAIL") + " repo connector check\n" + sb;
    }

    delegate string CheckFn(out string note);

    static bool Check(StringBuilder sb, string name, CheckFn fn)
    {
        string failure, note;
        LocalRepoConnector.ClearCache();
        try { failure = fn(out note); }
        catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; note = null; }
        sb.Append(failure == null ? "  ok   " : "  FAIL ").Append(name);
        if (failure != null) sb.Append(": ").Append(failure);
        else if (!string.IsNullOrEmpty(note)) sb.Append(": ").Append(note);
        sb.Append('\n');
        return failure == null;
    }

    static string Git(out string note)
    {
        note = null;
        var snap = LocalRepoConnector.FromGit(DateTime.UtcNow);
        if (snap == null) return "no snapshot: is git on PATH?";
        var table = ArtifactTable.Parse("commits", snap, out string error);
        if (table == null) return "it doesn't parse: " + error;
        if (table.Sample) return "marked as sample";
        var sample = ArtifactData.Bundled(LocalRepoConnector.Source);
        var want = (JArray)sample["columns"];
        for (int c = 0; c < want.Count; c++)
            if (table.Column((string)want[c]["name"]) != c) return "columns don't match the bundled sample's: " + string.Join(", ", table.Columns);
        if (table.Rows.Count == 0) return "no commits in the last 7 days";
        if (table.Rows.Count > LocalRepoConnector.MaxCommits) return table.Rows.Count + " rows";
        string head = table.Text(0, table.Column("sha"));
        string authors = string.Join(", ", Distinct(table, table.Column("author")));
        int lines = table.Column("lines_changed");
        double most = 0;
        int stubs = 0;
        for (int r = 0; r < table.Rows.Count; r++)
        {
            double v = table.Number(r, lines);
            if (double.IsNaN(v)) stubs++;
            else most = Math.Max(most, v);
        }
        note = $"{table.Rows.Count} commits, newest {head}; authors {authors}; biggest {most:F0} lines; {stubs} binary-only (grey stubs)";
        return null;
    }

    static System.Collections.Generic.List<string> Distinct(ArtifactTable t, int column)
    {
        var seen = new System.Collections.Generic.List<string>();
        for (int r = 0; r < t.Rows.Count; r++)
        {
            string a = t.Text(r, column);
            if (a != null && !seen.Contains(a)) seen.Add(a);
        }
        return seen;
    }

    static string Opens(out string note)
    {
        note = null;
        ArtifactData.Provider = null;
        if (!LocalRepoConnector.InstallIfFree()) return "didn't install with no provider set";
        var made = ArtifactTools.Create(new JObject { ["template"] = "repo_activity", ["open"] = false });
        var package = ArtifactPackage.Load((string)made["id"], out string error);
        if (package == null) return "no package: " + error;
        var tables = ArtifactData.Resolve(package, out string problem);
        if (tables == null) return "no data: " + problem;
        var t = tables["commits"];
        if (t.From != "connector" || t.Sample) return $"from {t.From}, sample {t.Sample}";
        string say = ArtifactData.Say(tables);
        if (say != null && say.Contains("sample")) return "Albie would call it sample data: " + say;
        var sources = ArtifactData.Sources();
        foreach (var s in sources)
            if ((string)s["source"] == LocalRepoConnector.Source && (bool)s["sample_only"]) return "artifact_list still says sample only";
        note = $"from {t.From}, {t.Rows.Count} rows, no sample label; artifact_list no longer says sample only";
        return null;
    }

    static string OnlyItsSource(out string note)
    {
        note = null;
        if (LocalRepoConnector.Provide("connector:issues/list_issues") != null) return "it answered issues/list_issues";
        if (LocalRepoConnector.Provide(null) != null) return "it answered a null source";
        note = "issues/list_issues and null get nothing, so those still fall back as before";
        return null;
    }

    static string StepsAside(out string note)
    {
        note = null;
        Func<string, JObject> real = source => null;
        ArtifactData.Provider = real;
        if (LocalRepoConnector.InstallIfFree()) return "it replaced the installed provider";
        if (ArtifactData.Provider != real) return "the provider changed";
        note = "with a provider already set, InstallIfFree leaves it alone";
        return null;
    }

    static string SavedCopy(out string note)
    {
        note = null;
        if (LocalRepoConnector.Saved() == null)
        {
            string saved = RepoSnapshotBuild.Save();
            if (LocalRepoConnector.Saved() == null) return "no saved copy after the build step: " + saved;
        }
        ArtifactData.Provider = null;
        LocalRepoConnector.InstallIfFree();
        LocalRepoConnector.SkipGit = true;
        var made = ArtifactTools.Create(new JObject { ["template"] = "repo_activity", ["open"] = false });
        var tables = ArtifactData.Resolve(ArtifactPackage.Load((string)made["id"], out _), out string problem);
        if (tables == null) return "no data: " + problem;
        var t = tables["commits"];
        if (t.Sample || !t.SavedCopy) return $"sample {t.Sample}, saved copy {t.SavedCopy}";
        string say = ArtifactData.Say(tables);
        if (say == null || !say.StartsWith("This is a saved copy of the data, from ")) return "Albie says: " + (say ?? "nothing");
        note = $"{t.Rows.Count} saved rows; Albie says \"{say}\"";
        return null;
    }

    static string Fallback(out string note)
    {
        note = null;
        ArtifactData.Provider = null;
        LocalRepoConnector.InstallIfFree();
        LocalRepoConnector.SkipGit = LocalRepoConnector.SkipSaved = true;
        var made = ArtifactTools.Create(new JObject { ["template"] = "repo_activity", ["open"] = false });
        var tables = ArtifactData.Resolve(ArtifactPackage.Load((string)made["id"], out _), out string problem);
        if (tables == null) return "no data: " + problem;
        var t = tables["commits"];
        if (t.From != "bundled" || !t.Sample) return $"from {t.From}, sample {t.Sample}";
        string say = ArtifactData.Say(tables);
        if (say == null || !say.Contains("sample")) return "Albie doesn't say it's sample data: " + say;
        note = "from bundled, sample, and Albie says so";
        return null;
    }

    static string BuildStep(out string note)
    {
        note = null;
        string said = RepoSnapshotBuild.Save();
        if (!said.StartsWith("saved")) return said;
        var copy = LocalRepoConnector.Saved();
        if (copy == null || copy.Value<bool?>("saved_copy") != true || copy.Value<bool?>("sample") != false) return "the copy isn't marked saved and real";
        string ignore = Path.Combine(RepoSnapshotBuild.Folder, ".gitignore");
        if (!File.Exists(ignore) || !File.ReadAllText(ignore).Contains("*")) return "no .gitignore beside the copy";
        note = said;
        return null;
    }
}
