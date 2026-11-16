using UnityEditor;
using UnityEngine;

/// <summary>Forge > Code > Build GitHub Panel Prefab: Code/Resources/GitHubPanel.prefab.</summary>
public static class GitHubPanelBuilder
{
    const string Source = "Assets/_Forge/Core/Prefabs/ForgePanel.prefab";
    const string Target = "Assets/_Forge/Code/Resources/GitHubPanel.prefab";

    [MenuItem("Forge/Code/Build GitHub Panel Prefab")]
    public static void Build()
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(Source);
        var go = (GameObject)PrefabUtility.InstantiatePrefab(source);
        try
        {
            go.name = "GitHubPanel";
            var panel = go.GetComponent<ForgePanel>();
            panel.panelId = "code-github";
            panel.Title = "source control";
            panel.SizeMeters = new Vector2(1.0f, 0.60f);
            if (go.GetComponent<GitHubPanel>() == null) go.AddComponent<GitHubPanel>();
            PrefabUtility.SaveAsPrefabAsset(go, Target);
            Debug.Log($"[GitHubPanelBuilder] saved {Target}");
        }
        finally { Object.DestroyImmediate(go); }
    }
}
