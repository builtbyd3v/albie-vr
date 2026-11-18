using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEngine;

/// <summary>Build Main for local macOS testing without changing the Quest player configuration.</summary>
public static class DesktopBuild
{
    public static void Build()
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-buildOutput");
        string output = index >= 0 && index + 1 < args.Length ? args[index + 1] : "Builds/Albie-Laptop.app";
        var xr = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
        bool initializeXr = xr != null && xr.InitManagerOnStart;
        // Unity serializes this Apple privacy description for macOS as well as iOS.
        string microphoneDescription = PlayerSettings.iOS.microphoneUsageDescription;
        try
        {
            if (xr != null) xr.InitManagerOnStart = false;
            if (string.IsNullOrWhiteSpace(microphoneDescription))
                PlayerSettings.iOS.microphoneUsageDescription = "Albie uses the microphone for spoken requests when voice input is enabled.";
            Directory.CreateDirectory("Logs");
            string biology = BioTemplatesCheck.Run();
            File.WriteAllText("Logs/laptop-biology-check.txt", biology);
            if (!biology.StartsWith("PASS", StringComparison.Ordinal))
                throw new InvalidOperationException("Biology template checks failed. See Logs/laptop-biology-check.txt.");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/_Forge/Core/Main.unity" },
                locationPathName = output,
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.Development,
            });
            var summary = report.summary;
            string result = $"{summary.result}: {output}; errors={summary.totalErrors}; warnings={summary.totalWarnings}; bytes={summary.totalSize}";
            File.WriteAllText("Logs/laptop-build-result.txt", result);
            Debug.Log("[DesktopBuild] " + result);
            if (summary.result != BuildResult.Succeeded) throw new InvalidOperationException(result);
        }
        finally
        {
            if (xr != null) xr.InitManagerOnStart = initializeXr;
            PlayerSettings.iOS.microphoneUsageDescription = microphoneDescription;
            AssetDatabase.SaveAssets();
        }
    }
}
