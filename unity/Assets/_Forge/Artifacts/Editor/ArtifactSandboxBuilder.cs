using Forge.Artifacts;
using Oculus.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Forge > Build Artifacts Sandbox: Artifacts/Prefabs/ArtifactHost.prefab and Artifacts/ArtifactsSandbox.unity (desktop rig,
/// hub off, a table for the volume to stand on). Safe to run again; it overwrites both. Keys are in ArtifactSandboxKeys.
/// </summary>
public static class ArtifactSandboxBuilder
{
    const string Dir = "Assets/_Forge/Artifacts";
    const string PrefabPath = Dir + "/Prefabs/ArtifactHost.prefab";
    const string ScenePath = Dir + "/ArtifactsSandbox.unity";

    [MenuItem("Forge/Build Artifacts Sandbox")]
    public static void Build()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var prefab = BuildPrefab();

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.62f, 0.66f, 0.74f);
        RenderSettings.ambientEquatorColor = new Color(0.36f, 0.37f, 0.40f);
        RenderSettings.ambientGroundColor = new Color(0.16f, 0.16f, 0.18f);

        var sun = new GameObject("Light").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.1f;
        sun.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(52f, -32f, 0f);

        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.GetComponent<Renderer>().sharedMaterial = ForgePrefabBuilder.RoomMaterial("Floor", new Color(0.27f, 0.28f, 0.31f));
        var table = GameObject.CreatePrimitive(PrimitiveType.Cube);
        table.name = "Table";
        table.transform.position = new Vector3(0f, 0.37f, 0.9f);
        table.transform.localScale = new Vector3(1.6f, 0.74f, 0.8f);
        table.GetComponent<Renderer>().sharedMaterial = ForgePrefabBuilder.RoomMaterial("Table", new Color(0.7f, 0.53f, 0.36f));

        var rigObject = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.DesktopRigPath));
        rigObject.transform.position = new Vector3(0f, 0f, -0.1f);
        var rig = rigObject.GetComponent<DesktopRig>();
        rig.standingEyeHeight = rig.seatedEyeHeight;
        var camera = rigObject.GetComponentInChildren<Camera>(true);
        camera.tag = "MainCamera";
        // Plain desktop camera: keep it out of XR, which would drive it from the simulator's head (as in CAD's sandbox).
        var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
        if (cameraData == null) cameraData = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.allowXRRendering = false;

        // ForgePanel's ray/poke canvas asserts a PointableCanvasModule exists; not exclusive (see CAD's sandbox).
        var events = new GameObject("EventSystem", typeof(EventSystem), typeof(PointableCanvasModule));
        events.GetComponent<PointableCanvasModule>().ExclusiveMode = false;
        new GameObject("ForgeNet", typeof(ForgeNet));

        var hostObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        hostObject.transform.position = new Vector3(-0.2f, 0.74f, 0.9f);
        hostObject.AddComponent<ArtifactSandboxKeys>().host = hostObject.GetComponent<ArtifactHost>();

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[ArtifactSandboxBuilder] Built {ScenePath}. Press Play, then 1 (Stance), 2 (Slalom), 3 (broken), 4 (missing), 0 (close), 9 (forget approvals).");
    }

    static GameObject BuildPrefab()
    {
        if (!AssetDatabase.IsValidFolder(Dir + "/Prefabs")) AssetDatabase.CreateFolder(Dir, "Prefabs");
        var root = new GameObject("ArtifactHost");
        var host = root.AddComponent<ArtifactHost>();
        host.panelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.PanelPath).GetComponent<ForgePanel>();
        host.volumePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.VolumePath).GetComponent<ForgeVolume>();
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        return prefab;
    }
}
