using UnityEditor;
using UnityEngine;

/// <summary>
/// GitHub features are built into CodePanel — use "Forge > Code > Build Code Panel Prefab" instead.
/// This item is kept so the menu doesn't vanish; it just redirects.
/// </summary>
public static class GitHubPanelBuilder
{
    [MenuItem("Forge/Code/Build GitHub Panel Prefab")]
    public static void Build()
    {
        EditorUtility.DisplayDialog(
            "GitHub features moved",
            "GitHub file browsing and PR diff view are now built into CodePanel.\n\n" +
            "Run  Forge > Code > Build Code Panel Prefab  to rebuild the panel.",
            "OK");
    }
}
