using Oculus.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Forge.Demo.PipelineTools
{
    /// <summary>
    /// Forge > Demo > Pipeline > Build Sandbox: Demo/Prefabs/PipelineStation.prefab (the piece, with the panel prefab wired) and
    /// Demo/Sandboxes/PipelineSandbox.unity (desktop rig at a seated 1.15 m, a dark floor, a light, the piece, hub off).
    /// Press Play, then 1 open, 2 pull, 3 train, 4 push (poke Confirm with the mouse: Esc frees the cursor), 5 close, 0 reset.
    /// Safe to run again; it overwrites both.
    /// </summary>
    public static class PipelineSandboxBuilder
    {
        public const string PrefabPath = "Assets/_Forge/Demo/Prefabs/PipelineStation.prefab";
        public const string ScenePath = "Assets/_Forge/Demo/Sandboxes/PipelineSandbox.unity";
        const string PanelPrefabPath = "Assets/_Forge/Core/Prefabs/ForgePanel.prefab";
        const string DesktopRigPath = "Assets/_Forge/Core/Prefabs/DesktopRig.prefab";

        [MenuItem("Forge/Demo/Pipeline/Build Sandbox")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[Demo] Stop Play mode before building the pipeline sandbox.");
                return;
            }
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            BuildPrefab();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.30f, 0.31f, 0.34f);

            var sun = new GameObject("Light").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 0.9f;
            sun.transform.rotation = Quaternion.Euler(52f, -32f, 0f);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.GetComponent<Renderer>().sharedMaterial = ForgePrefabBuilder.RoomMaterial("DemoFloor", new Color(0.07f, 0.075f, 0.09f));

            var rigObject = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(DesktopRigPath));
            rigObject.transform.position = new Vector3(0f, 0f, 0f);
            var rig = rigObject.GetComponent<DesktopRig>();
            rig.standingEyeHeight = 1.15f;
            rig.seatedEyeHeight = 1.15f;
            var camera = rigObject.GetComponentInChildren<Camera>(true);
            camera.tag = "MainCamera";
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.02f, 0.025f, 0.035f);
            var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData == null) cameraData = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.allowXRRendering = false;  // a plain desktop camera, not driven by the simulator's head

            // ForgePanel's ray/poke canvas wants a PointableCanvasModule in the scene; not exclusive, so the mouse still works.
            var events = new GameObject("EventSystem", typeof(EventSystem), typeof(PointableCanvasModule));
            events.GetComponent<PointableCanvasModule>().ExclusiveMode = false;
            new GameObject("ForgeNet", typeof(ForgeNet));

            var station = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath), scene);
            station.AddComponent<PipelineSandboxKeys>();

            if (!AssetDatabase.IsValidFolder("Assets/_Forge/Demo/Sandboxes")) AssetDatabase.CreateFolder("Assets/_Forge/Demo", "Sandboxes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[Demo] Built " + ScenePath + ". Press Play: 1 open, 2 pull, 3 train, 4 push (poke Confirm), 5 close, 0 reset.");
        }

        static void BuildPrefab()
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Forge/Demo/Prefabs")) AssetDatabase.CreateFolder("Assets/_Forge/Demo", "Prefabs");
            var root = new GameObject("Pipeline Station");
            var demo = root.AddComponent<PipelineDemo>();
            demo.panelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PanelPrefabPath)?.GetComponent<ForgePanel>();
            if (demo.panelPrefab == null) Debug.LogWarning("[Demo] " + PanelPrefabPath + " is missing; the station will look for the Workspace's panel prefab.");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
        }
    }
}
