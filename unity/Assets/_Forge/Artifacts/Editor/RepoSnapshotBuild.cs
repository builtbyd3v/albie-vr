using System;
using System.IO;
using Forge.Artifacts;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Saves this repo's commits into the build, so "What shipped this week?" on a Quest (which has no git) shows the team's
/// real commits as of the build, labelled as a saved copy. It runs before every player build and from
/// <b>Forge > Artifacts > Save Repo Commits For Quest</b>. The file is git-ignored (a <c>.gitignore</c> in its folder), so
/// builds don't leave a changed file behind. It never fails a build: with no git, the headset falls back to the sample.
/// </summary>
public class RepoSnapshotBuild : IPreprocessBuildWithReport
{
    public const string Folder = "Assets/_Forge/Artifacts/Resources/ArtifactData/Repo";
    public static string AssetPath => Folder + "/source_control_list_commits.json";

    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        try { Debug.Log("[RepoSnapshotBuild] " + Save()); }
        catch (Exception e) { Debug.LogWarning("[RepoSnapshotBuild] skipped, the headset will show sample commits: " + e.Message); }
    }

    [MenuItem("Forge/Artifacts/Save Repo Commits For Quest")]
    static void Menu() => Debug.Log("[RepoSnapshotBuild] " + Save());

    /// <summary>Writes the saved copy and returns what it did.</summary>
    public static string Save()
    {
        var live = LocalRepoConnector.FromGit(DateTime.UtcNow);
        if (live == null) return "no git here, so nothing saved; the headset will show sample commits";
        var copy = LocalRepoConnector.ForSaving(live);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(AssetPath, copy.ToString(Formatting.Indented) + "\n");
        AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceSynchronousImport);
        LocalRepoConnector.ClearCache();
        return $"saved {((Newtonsoft.Json.Linq.JArray)copy["rows"]).Count} commits from the last {LocalRepoConnector.Days} days to {AssetPath}";
    }
}
