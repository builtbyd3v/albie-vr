using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

/// <summary>
/// Checks the app's release identity and signing setup without building: the Player settings hold the release values
/// (name, company, Android id, version, version code), the build is IL2CPP + ARM64 only at the project's API levels,
/// keystores are git-ignored, no signing password sits in ProjectSettings, and the release build (QuestBuild.Release,
/// from chore/app-identity-release) falls back to the debug key with a loud warning when the ALBIE_KEYSTORE_* variables
/// are unset. That last step reads QuestBuild.cs (no build runs) and says "skipped" while Release isn't there.
/// Forge > Core > Check App Identity logs the report; <see cref="Run"/> returns it. PASS on the first line means every
/// step passed. Before the identity branch merges this FAILS on purpose and names each template value still set.
/// </summary>
public static class AppIdentityCheck
{
    const string Product = "Albie", Company = "Albie", AppId = "com.albie.vr", Version = "1.0.0";
    const int MinVersionCode = 2, MinApi = 32, TargetApi = 34;
    const string QuestBuildPath = "Assets/_Forge/Core/Editor/QuestBuild.cs";
    static readonly string[] EnvVars = { "ALBIE_KEYSTORE_PATH", "ALBIE_KEYSTORE_PASS", "ALBIE_KEY_ALIAS", "ALBIE_KEY_ALIAS_PASS" };

    [MenuItem("Forge/Core/Check App Identity")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    /// <summary>For -executeMethod.</summary>
    public static void RunBatch()
    {
        string report = Run();
        Debug.Log(report);
        EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        int failed = 0, skipped = 0;
        failed += Step(sb, ref skipped, "Release identity", Identity);
        failed += Step(sb, ref skipped, "IL2CPP, ARM64 only", Backend);
        failed += Step(sb, ref skipped, "Android API levels", ApiLevels);
        failed += Step(sb, ref skipped, "Keystores are git-ignored", KeystoresIgnored);
        failed += Step(sb, ref skipped, "No signing password in ProjectSettings", NoStoredPassword);
        failed += Step(sb, ref skipped, "Release build falls back to the debug key, loudly", DebugFallback);
        return (failed == 0 ? "PASS" : $"FAIL ({failed})") + $" app identity check{(skipped > 0 ? $" ({skipped} skipped)" : "")}\n" + sb;
    }

    // ---- Steps -----------------------------------------------------------------------------------------------

    static string Identity()
    {
        string id = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
        int code = PlayerSettings.Android.bundleVersionCode;
        var wrong = new List<string>();
        if (PlayerSettings.productName != Product) wrong.Add($"productName is \"{PlayerSettings.productName}\", want \"{Product}\"");
        if (PlayerSettings.companyName != Company) wrong.Add($"companyName is \"{PlayerSettings.companyName}\", want \"{Company}\"");
        if (id != AppId) wrong.Add($"Android applicationIdentifier is \"{id}\", want \"{AppId}\"");
        if (PlayerSettings.bundleVersion != Version) wrong.Add($"bundleVersion is \"{PlayerSettings.bundleVersion}\", want \"{Version}\"");
        if (code < MinVersionCode) wrong.Add($"bundleVersionCode is {code}, want {MinVersionCode} or more");
        Expect(wrong.Count == 0, string.Join("; ", wrong));
        return $"{Product} by {Company}, {AppId} {Version} (code {code})";
    }

    static string Backend()
    {
        var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android);
        var arch = PlayerSettings.Android.targetArchitectures;
        Expect(backend == ScriptingImplementation.IL2CPP, $"scripting backend is {backend}, want IL2CPP");
        Expect(arch == AndroidArchitecture.ARM64, $"target architectures are {arch}, want ARM64 only");
        return "IL2CPP, ARM64";
    }

    static string ApiLevels()
    {
        int min = (int)PlayerSettings.Android.minSdkVersion, target = (int)PlayerSettings.Android.targetSdkVersion;
        Expect(min == MinApi, $"min API {min}, want {MinApi}");
        Expect(target == TargetApi, $"target API {target}, want {TargetApi}");
        return $"min {min}, target {target}";
    }

    static string KeystoresIgnored()
    {
        string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        var patterns = new HashSet<string>();
        foreach (string file in new[] { Path.Combine(repo, ".gitignore"), Path.Combine(repo, "unity", ".gitignore") })
            if (File.Exists(file))
                foreach (string line in File.ReadAllLines(file)) patterns.Add(line.Trim());
        var missing = new List<string>();
        foreach (string p in new[] { "*.keystore", "*.jks" }) if (!patterns.Contains(p)) missing.Add(p);
        Expect(missing.Count == 0, $"{string.Join(" and ", missing)} not in .gitignore (a release key could be committed)");
        return "*.keystore and *.jks ignored";
    }

    static string NoStoredPassword()
    {
        string asset = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProjectSettings", "ProjectSettings.asset"));
        Expect(File.Exists(asset), "no ProjectSettings.asset");
        var stored = new List<string>();
        foreach (Match m in Regex.Matches(File.ReadAllText(asset), @"^[ \t]*(\w*(?:[Kk]eystore|[Kk]eyalias)\w*[Pp]ass\w*)[ \t]*:[ \t]*\S", RegexOptions.Multiline))
            stored.Add(m.Groups[1].Value);
        Expect(stored.Count == 0, $"ProjectSettings.asset stores {string.Join(", ", stored)} (values not shown)");
        return $"no keystore or key alias password in ProjectSettings.asset; custom keystore {(PlayerSettings.Android.useCustomKeystore ? "on" : "off")}";
    }

    static string DebugFallback()
    {
        var release = typeof(QuestBuild).GetMethod("Release", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        if (release == null) return "skipped (QuestBuild.Release isn't on this branch yet: chore/app-identity-release)";
        var menu = release.GetCustomAttribute<MenuItem>();
        Expect(menu != null && menu.menuItem == "Forge/Build/Release APK", "Release has no Forge/Build/Release APK menu item");
        Expect(File.Exists(QuestBuildPath), "no " + QuestBuildPath);
        string src = File.ReadAllText(QuestBuildPath);
        foreach (string v in EnvVars) Expect(src.Contains("\"" + v + "\""), $"QuestBuild doesn't read {v}");
        Expect(Regex.IsMatch(src, @"LogWarning\([^;]*DEBUG"), "no warning naming the debug key when the variables are unset");
        Expect(src.Contains("useCustomKeystore = release"), "the custom keystore isn't tied to all four variables being set");
        Expect(src.Contains("keystorePass = \"\"") && src.Contains("keyaliasPass = \"\""), "the passwords aren't cleared after the build");
        var set = new List<string>();
        foreach (string v in EnvVars) if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))) set.Add(v);
        return "source check, no build run: reads all 4 variables, warns DEBUG-signed without them, clears the passwords after; set now: " +
               (set.Count == 0 ? "none (a build now would be debug-signed)" : string.Join(", ", set));
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    sealed class Failed : Exception
    {
        public Failed(string message) : base(message) { }
    }

    static void Expect(bool condition, string message)
    {
        if (!condition) throw new Failed(message);
    }

    static int Step(StringBuilder sb, ref int skipped, string name, Func<string> check)
    {
        string tag, detail;
        try
        {
            detail = check();
            tag = detail.StartsWith("skipped") ? "skip" : "ok  ";
        }
        catch (Exception e)
        {
            detail = e is Failed ? e.Message : e.GetType().Name + ": " + e.Message;
            tag = "FAIL";
        }
        if (tag == "skip") skipped++;
        sb.Append("  ").Append(tag).Append(' ').Append(name).Append(": ").Append(detail).Append('\n');
        return tag == "FAIL" ? 1 : 0;
    }
}
