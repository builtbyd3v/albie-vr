using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless check that Albie's voice rules (<c>AlbieVoice</c>'s tool rules) only name tools the headset has: every
/// <c>name_like_this</c> is a tool, argument or template in some <c>Resources/AlbieTools/*.json</c>, and every
/// <c>prefix_</c> starts at least one tool. A renamed tool would otherwise leave a demo phrase pointing at nothing. Forge > Artifacts > Check Voice Rules.
/// </summary>
public static class VoiceRulesCheck
{
    // Make a course, "that didn't work", v1 vs v2, "drive it twice and I'll learn it", train the template.
    static readonly string[] DemoTools = { "artifact_create", "notebook_mark", "notebook_compare", "brain_record", "template_train" };

    [MenuItem("Forge/Artifacts/Check Voice Rules")]
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
        var field = typeof(AlbieVoice).GetField("ToolRules", BindingFlags.NonPublic | BindingFlags.Static);
        string rules = field?.GetRawConstantValue() as string;
        if (string.IsNullOrEmpty(rules)) return "FAIL voice rules check\n  AlbieVoice.ToolRules not found";

        var tools = new List<string>();
        var args = new HashSet<string>();
        var descriptions = new StringBuilder();
        foreach (var file in Resources.LoadAll<TextAsset>("AlbieTools"))
            foreach (JObject tool in JArray.Parse(file.text))
            {
                tools.Add((string)tool["name"]);
                descriptions.Append(tool["description"]).Append('\n');
                if (tool["parameters"]?["properties"] is JObject props)
                    foreach (var p in props) args.Add(p.Key);
            }
        string described = descriptions.ToString();

        var sb = new StringBuilder();
        bool ok = true;
        var named = new List<string>();
        foreach (Match m in Regex.Matches(rules, @"\b[a-z]+_[a-z_]*"))
        {
            string word = m.Value;
            if (named.Contains(word)) continue;
            named.Add(word);
            bool found = word.EndsWith("_")
                ? tools.Exists(t => t.StartsWith(word))
                : tools.Contains(word) || args.Contains(word) || Regex.IsMatch(described, @"\b" + word + @"\b");
            if (!found) { ok = false; sb.Append("  FAIL the rules name ").Append(word).Append(", which is no tool, argument or template\n"); }
        }
        foreach (string tool in DemoTools)
            if (!rules.Contains(tool)) { ok = false; sb.Append("  FAIL no rule routes a demo beat to ").Append(tool).Append('\n'); }
        if (ok) sb.Append("  ok   ").Append(named.Count).Append(" tool names and prefixes in the rules, all real (").Append(tools.Count).Append(" tools on the headset); every demo beat is routed\n");
        return (ok ? "PASS" : "FAIL") + " voice rules check\n" + sb;
    }
}
