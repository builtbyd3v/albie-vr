using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Forge.Demo.Editor
{
    /// <summary>
    /// Forge > Demo > Build Quest APK: the same build as Core/Editor/QuestBuild.cs (enabled scenes, Android) but from the open editor,
    /// which QuestBuild cannot do because it quits the editor when it ends. Writes Builds/AlbieDemo.apk, and the verdict to
    /// unity/Logs/demo/apk_result.txt. Demo mode is whatever Resources/demo_config.json says (Forge > Demo > Toggle Demo Mode).
    /// </summary>
    public static class DemoApkBuild
    {
        const string OutPath = "Builds/AlbieDemo.apk";
        const string ResultPath = "Logs/demo/apk_result.txt";

        [MenuItem("Forge/Demo/Build Quest APK")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[DemoApk] leave Play mode first."); return; }
            Directory.CreateDirectory("Logs/demo");
            File.WriteAllText(ResultPath, "running\n");
            // Let the command that asked for this return first: the build blocks the editor for minutes.
            EditorApplication.delayCall += Build;
        }

        public static void Build()
        {
            string result;
            try
            {
                Directory.CreateDirectory("Builds");
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                    locationPathName = OutPath,
                    target = BuildTarget.Android,
                    options = BuildOptions.None,
                });
                result = report.summary.result + " " + OutPath + " " + report.summary.totalSize + " bytes, " +
                         report.summary.totalErrors + " errors, " + report.summary.totalWarnings + " warnings, " +
                         report.summary.totalTime.TotalSeconds.ToString("F0") + " s";
                if (report.summary.result != BuildResult.Succeeded)
                {
                    foreach (var step in report.steps)
                        foreach (var m in step.messages)
                            if (m.type == LogType.Error || m.type == LogType.Exception) result += "\n  " + m.content;
                }
            }
            catch (System.Exception e) { result = "Exception " + e.Message; }
            File.WriteAllText(ResultPath, result + "\n");
            Debug.Log("[DemoApk] " + result);
        }
    }
}
