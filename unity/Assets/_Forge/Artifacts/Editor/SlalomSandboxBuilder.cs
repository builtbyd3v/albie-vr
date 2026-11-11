using Forge.Artifacts;
using Meta.XR.MRUtilityKit;
using Oculus.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Forge > Artifacts > Build Slalom Sandbox: Artifacts/SlalomSandbox.unity. Hub off, no headset: MRUK loads its own
/// prefab rooms (living rooms and bedrooms), or the sandbox draws the checks' mock rooms, and "Make me a slalom" lays a
/// course out in the free floor. An overhead camera shows the whole room; O switches to the first-person desktop rig.
/// Uses the ArtifactHost prefab as built by Forge > Build Artifacts Sandbox. Keys are in SlalomSandbox.
/// </summary>
public static class SlalomSandboxBuilder
{
    const string ScenePath = "Assets/_Forge/Artifacts/SlalomSandbox.unity";
    const string HostPath = "Assets/_Forge/Artifacts/Resources/ArtifactHost.prefab";
    const string MrukPath = "Packages/com.meta.xr.mrutilitykit/Core/Tools/MRUK.prefab";
    const string Rooms = "Packages/com.meta.xr.mrutilitykit/Core/Rooms/Prefabs/";
    static readonly string[] RoomPrefabs =
    {
        "LivingRoom/LivingRoom00", "LivingRoom/LivingRoom01", "LivingRoom/LivingRoom02", "LivingRoom/LivingRoom03", "LivingRoom/LivingRoom04",
        "Bedroom/Bedroom00", "Bedroom/Bedroom01", "Office/Office00",
    };

    [MenuItem("Forge/Artifacts/Build Slalom Sandbox")]
    public static void Build()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.62f, 0.66f, 0.74f);
        RenderSettings.ambientEquatorColor = new Color(0.36f, 0.37f, 0.40f);
        RenderSettings.ambientGroundColor = new Color(0.16f, 0.16f, 0.18f);
        var sun = new GameObject("Light").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.1f;
        sun.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(62f, -32f, 0f);

        // First-person desktop rig: panels face it, and it can walk the course.
        var rigObject = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.DesktopRigPath));
        rigObject.transform.position = new Vector3(0f, 0f, -1.2f);
        var camera = rigObject.GetComponentInChildren<Camera>(true);
        camera.tag = "MainCamera";
        NoXr(camera);

        // The overview: straight down on the whole room, drawn over the rig's view while it's on.
        var overhead = new GameObject("Overhead camera").AddComponent<Camera>();
        overhead.orthographic = true;
        overhead.orthographicSize = 4.6f;
        overhead.depth = 10f;
        overhead.clearFlags = CameraClearFlags.SolidColor;
        overhead.backgroundColor = new Color(0.025f, 0.04f, 0.07f);
        overhead.transform.SetPositionAndRotation(new Vector3(0f, 12f, 0f), Quaternion.Euler(90f, 0f, 0f));
        NoXr(overhead);

        var events = new GameObject("EventSystem", typeof(EventSystem), typeof(PointableCanvasModule));
        events.GetComponent<PointableCanvasModule>().ExclusiveMode = false;
        new GameObject("ForgeNet", typeof(ForgeNet));

        // MRUK, loading prefab rooms on demand (never the device): the real room code path with no headset. It wants a
        // camera rig to exist; a disabled stand-in says so without adding XR cameras to a desktop scene.
        new GameObject("Camera rig stand-in (MRUK)").AddComponent<SandboxCameraRig>().enabled = false;
        var mruk = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(MrukPath));
        var so = new SerializedObject(mruk.GetComponent<MRUK>());
        so.FindProperty("SceneSettings.DataSource").enumValueIndex = (int)MRUK.SceneDataSource.Prefab;
        so.FindProperty("SceneSettings.LoadSceneOnStartup").boolValue = false;
        so.ApplyModifiedPropertiesWithoutUndo();
        var rooms = new GameObject[RoomPrefabs.Length];
        for (int i = 0; i < rooms.Length; i++) rooms[i] = AssetDatabase.LoadAssetAtPath<GameObject>(Rooms + RoomPrefabs[i] + ".prefab");
        var view = new GameObject("MRUK room view");
        Effect(view.transform, "Floor", MRUKAnchor.SceneLabels.FLOOR, ForgePrefabBuilder.RoomMaterial("Floor", new Color(0.34f, 0.31f, 0.29f)));
        Effect(view.transform, "Walls", MRUKAnchor.SceneLabels.WALL_FACE, ForgePrefabBuilder.RoomMaterial("Walls", new Color(0.86f, 0.86f, 0.88f)));
        Effect(view.transform, "Table", MRUKAnchor.SceneLabels.TABLE, ForgePrefabBuilder.RoomMaterial("Table", new Color(0.7f, 0.53f, 0.36f)));
        Effect(view.transform, "Furniture", MRUKAnchor.SceneLabels.COUCH | MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.STORAGE
            | MRUKAnchor.SceneLabels.OTHER | MRUKAnchor.SceneLabels.LAMP | MRUKAnchor.SceneLabels.PLANT | MRUKAnchor.SceneLabels.SCREEN,
            ForgePrefabBuilder.RoomMaterial("Furniture", new Color(0.58f, 0.6f, 0.64f)));

        var hostObject = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(HostPath), scene);
        hostObject.transform.position = new Vector3(0f, 0f, -1.2f);
        var sandbox = hostObject.AddComponent<SlalomSandbox>();
        sandbox.host = hostObject.GetComponent<ArtifactHost>();
        sandbox.mrukRooms = rooms;
        sandbox.overhead = overhead;
        sandbox.floorMaterial = ForgePrefabBuilder.RoomMaterial("Floor", new Color(0.34f, 0.31f, 0.29f));
        sandbox.wallMaterial = ForgePrefabBuilder.RoomMaterial("Walls", new Color(0.86f, 0.86f, 0.88f));
        sandbox.furnitureMaterial = ForgePrefabBuilder.RoomMaterial("Furniture", new Color(0.58f, 0.6f, 0.64f));

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[SlalomSandboxBuilder] Built {ScenePath}. Press Play, then 2 (make me a slalom), 3 (arm a run), 4 (scripted lap), WASD (drive), M / N (rooms).");
    }

    static void NoXr(Camera camera)
    {
        var data = camera.GetComponent<UniversalAdditionalCameraData>();
        if (data == null) data = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
        data.allowXRRendering = false;
    }

    static void Effect(Transform parent, string name, MRUKAnchor.SceneLabels labels, Material material)
    {
        var effect = new GameObject(name).AddComponent<EffectMesh>();
        effect.transform.SetParent(parent, false);
        effect.MeshMaterial = material;
        effect.Labels = labels;
        effect.Colliders = false;
        var so = new SerializedObject(effect);
        so.FindProperty("castShadows").boolValue = false;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
