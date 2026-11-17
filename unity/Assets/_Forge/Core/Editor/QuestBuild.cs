using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Batch-mode Quest APK build: unity build --target Android --execute-method QuestBuild.Build -o Builds/X.apk .
// Add -dev for a development build as com.albie.devbuild ("Dev's Build"); ProjectSettings is left as it was.
// Release APK for the store channel: Forge > Build > Release APK, or -executeMethod QuestBuild.Release (docs/RELEASE.md).
public static class QuestBuild
{
    const string MainScene = "Assets/_Forge/Core/Main.unity";

    public static void Build()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, "-buildOutput");
        string path = i >= 0 && i + 1 < args.Length ? args[i + 1] : "Builds/AlbieStandalone.apk";
        // -dev: a development build that installs next to the release app (own id and name, this build only).
        bool dev = System.Array.IndexOf(args, "-dev") >= 0;
        string oldId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android), oldName = PlayerSettings.productName;
        BuildReport report;
        try
        {
            if (dev)
            {
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.albie.devbuild");
                PlayerSettings.productName = "Dev's Build";
            }
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = path,
                target = BuildTarget.Android,
                options = dev ? BuildOptions.Development : BuildOptions.None,
            });
        }
        finally
        {
            if (dev)
            {
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, oldId);
                PlayerSettings.productName = oldName;
            }
        }
        Debug.Log($"[QuestBuild] {report.summary.result}{(dev ? " (development, com.albie.devbuild)" : "")} -> {path}");
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    // Release APK: Main.unity only, ARM64 IL2CPP, not a development build, written to
    // Builds/Albie-<bundleVersion>-<versionCode>.apk. Signs with the keystore named by ALBIE_KEYSTORE_PATH,
    // ALBIE_KEYSTORE_PASS, ALBIE_KEY_ALIAS and ALBIE_KEY_ALIAS_PASS; with any of them unset it falls back to the
    // debug key. The signing values are put back afterwards so no path or password lands in ProjectSettings.
    [MenuItem("Forge/Build/Release APK")]
    public static void Release()
    {
        bool ok = false;
        try { ok = BuildRelease(); }
        finally
        {
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }
    }

    static bool BuildRelease()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) != ScriptingImplementation.IL2CPP ||
            PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
        {
            Debug.LogError("[QuestBuild] Release needs IL2CPP and ARM64 only (Project Settings > Player > Android). Not building.");
            return false;
        }

        string path = $"Builds/Albie-{PlayerSettings.bundleVersion}-{PlayerSettings.Android.bundleVersionCode}.apk";
        string keystore = System.Environment.GetEnvironmentVariable("ALBIE_KEYSTORE_PATH");
        string storePass = System.Environment.GetEnvironmentVariable("ALBIE_KEYSTORE_PASS");
        string alias = System.Environment.GetEnvironmentVariable("ALBIE_KEY_ALIAS");
        string aliasPass = System.Environment.GetEnvironmentVariable("ALBIE_KEY_ALIAS_PASS");
        bool release = !string.IsNullOrEmpty(keystore) && !string.IsNullOrEmpty(storePass) &&
                       !string.IsNullOrEmpty(alias) && !string.IsNullOrEmpty(aliasPass);
        if (release && !System.IO.File.Exists(keystore))
        {
            Debug.LogError($"[QuestBuild] ALBIE_KEYSTORE_PATH points to a missing file: {keystore}. Not building.");
            return false;
        }
        if (!release)
            Debug.LogWarning("[QuestBuild] ******** DEBUG-SIGNED: ALBIE_KEYSTORE_PATH / ALBIE_KEYSTORE_PASS / ALBIE_KEY_ALIAS / " +
                             "ALBIE_KEY_ALIAS_PASS are not all set. Fine for side-loading; the store channel needs a release key. ********");

        bool oldCustom = PlayerSettings.Android.useCustomKeystore;
        string oldStore = PlayerSettings.Android.keystoreName, oldAlias = PlayerSettings.Android.keyaliasName;
        bool oldBundle = EditorUserBuildSettings.buildAppBundle;
        try
        {
            EditorUserBuildSettings.buildAppBundle = false;
            PlayerSettings.Android.useCustomKeystore = release;
            if (release)
            {
                PlayerSettings.Android.keystoreName = keystore;
                PlayerSettings.Android.keystorePass = storePass;
                PlayerSettings.Android.keyaliasName = alias;
                PlayerSettings.Android.keyaliasPass = aliasPass;
            }

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { MainScene },
                locationPathName = path,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            });
            var s = report.summary;
            long bytes = System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length : 0;
            Debug.Log($"[QuestBuild] Release {s.result}: {path}, {bytes / (1024f * 1024f):F1} MB, " +
                      $"signing: {(release ? "release key (" + alias + ")" : "DEBUG key")}, " +
                      $"{PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android)} {PlayerSettings.bundleVersion} ({PlayerSettings.Android.bundleVersionCode}), " +
                      $"{s.totalErrors} errors, {s.totalTime.TotalMinutes:F1} min.");
            return s.result == BuildResult.Succeeded;
        }
        finally
        {
            PlayerSettings.Android.useCustomKeystore = oldCustom;
            PlayerSettings.Android.keystoreName = oldStore;
            PlayerSettings.Android.keyaliasName = oldAlias;
            PlayerSettings.Android.keystorePass = "";
            PlayerSettings.Android.keyaliasPass = "";
            EditorUserBuildSettings.buildAppBundle = oldBundle;
        }
    }
}
