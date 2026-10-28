using UnityEditor;
using UnityEngine;

/// <summary>Forge > Code > Build Code Panel Prefab: Code/Resources/CodePanel.prefab, the Core ForgePanel plus <see cref="CodePanel"/>.</summary>
public static class CodePanelBuilder
{
    const string Source = "Assets/_Forge/Core/Prefabs/ForgePanel.prefab";
    const string Target = "Assets/_Forge/Code/Resources/CodePanel.prefab";

    [MenuItem("Forge/Code/Build Code Panel Prefab")]
    public static void Build()
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(Source);
        var go = (GameObject)PrefabUtility.InstantiatePrefab(source);
        try
        {
            go.name = "CodePanel";
            var panel = go.GetComponent<ForgePanel>();
            panel.panelId = "code-main";
            panel.Title = "Code";
            panel.SizeMeters = new Vector2(0.9f, 0.55f);
            if (go.GetComponent<CodePanel>() == null) go.AddComponent<CodePanel>();
            PrefabUtility.SaveAsPrefabAsset(go, Target);
            Debug.Log($"[CodePanelBuilder] saved {Target}");
        }
        finally { Object.DestroyImmediate(go); }
    }
}
