using Forge.Notebook;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds Assets/_Forge/Notebook/NotebookSandbox.unity in code: a camera and the <see cref="NotebookSandbox"/>
/// readout. Hub off. Open it, press Play, and use the keys listed on screen.
/// </summary>
public static class NotebookSandboxBuilder
{
    const string ScenePath = "Assets/_Forge/Notebook/NotebookSandbox.unity";

    [MenuItem("Forge/Notebook/Build Sandbox")]
    public static void Build()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var camera = new GameObject("Camera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.025f, 0.04f, 0.07f);
        new GameObject("Notebook sandbox").AddComponent<NotebookSandbox>();
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
        Debug.Log("[Notebook] built " + ScenePath);
    }
}
