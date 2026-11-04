using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Batch-mode Quest APK build: unity build --target Android --execute-method QuestBuild.Build -o Builds/X.apk .
public static class QuestBuild
{
    public static void Build()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, "-buildOutput");
        string path = i >= 0 && i + 1 < args.Length ? args[i + 1] : "Builds/AlbieStandalone.apk";
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
            locationPathName = path,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        });
        Debug.Log($"[QuestBuild] {report.summary.result} -> {path}");
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
