using System.IO;
using Forge.Artifacts;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Forge.Demo.EditorTools
{
    /// <summary>
    /// Builds the flight station's assets in code: the materials, DemoDrone.prefab (the drone, its pad and the drone-cam window),
    /// DroneCam.prefab, and FlightSandbox.unity. Menu: Forge > Demo > Flight. Safe to run again: it rewrites the same assets.
    /// </summary>
    public static class FlightBuilder
    {
        const string Art = "Assets/_Forge/Demo/Art/Flight";
        const string Prefabs = "Assets/_Forge/Demo/Prefabs";
        const string SandboxPath = "Assets/_Forge/Demo/Sandboxes/FlightSandbox.unity";
        const string Mock = "Assets/_Forge/Mocks/cad/quadcopter/v7";
        const string LockFile = "Temp/demo-editor.lock";

        [MenuItem("Forge/Demo/Flight/Build Prefabs")]
        public static void BuildPrefabs()
        {
            Directory.CreateDirectory(Art);
            Directory.CreateDirectory(Prefabs);
            var add = UnlitMaterial("FlightAdditive", 5, 1, 0, 3000);
            var alpha = UnlitMaterial("FlightAlpha", 5, 10, 0, 3000);
            var opaque = UnlitMaterial("FlightOpaque", 1, 0, 1, 2000);
            var camBody = LitMaterial("FlightCameraBody", new Color(0.09f, 0.1f, 0.115f), 0.35f, 0.7f);
            var camLens = LitMaterial("FlightCameraLens", new Color(0.02f, 0.025f, 0.035f), 0.95f, 0.2f);

            // The drone-cam window.
            var camRoot = new GameObject("DroneCam");
            var win = camRoot.AddComponent<DroneCamWindow>();
            win.alphaMaterial = alpha;
            win.opaqueMaterial = opaque;
            var camPrefab = PrefabUtility.SaveAsPrefabAsset(camRoot, Prefabs + "/DroneCam.prefab");
            Object.DestroyImmediate(camRoot);

            // The drone.
            var root = new GameObject("DemoDrone");
            var drone = root.AddComponent<DemoDrone>();
            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(root.transform, false);
            var body = bodyGo.AddComponent<DroneBody>();
            body.glb = AssetDatabase.LoadAssetAtPath<TextAsset>(Mock + "/model.glb.bytes");
            body.metadata = AssetDatabase.LoadAssetAtPath<TextAsset>(Mock + "/metadata.json");
            body.version = 7;
            body.additive = add;
            body.cameraBody = camBody;
            body.cameraLens = camLens;
            if (body.glb == null || body.metadata == null) Debug.LogError("[FlightBuilder] the v7 quadcopter mock is missing from " + Mock);
            var camInst = (GameObject)PrefabUtility.InstantiatePrefab(camPrefab, root.transform);
            drone.body = body;
            drone.cam = camInst.GetComponent<DroneCamWindow>();
            drone.additive = add;
            PrefabUtility.SaveAsPrefabAsset(root, Prefabs + "/DemoDrone.prefab");
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[FlightBuilder] built DemoDrone.prefab and DroneCam.prefab");
        }

        [MenuItem("Forge/Demo/Flight/Build Sandbox")]
        public static void BuildSandbox()
        {
            BuildPrefabs();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
            camGo.transform.position = new Vector3(0f, 1.15f, 0f);
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.02f, 0.025f, 0.035f);
            cam.fieldOfView = 80f;
            cam.nearClipPlane = 0.05f;
            camGo.AddComponent<UniversalAdditionalCameraData>();

            var lightGo = new GameObject("Directional Light", typeof(Light));
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var light = lightGo.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.None;
            lightGo.AddComponent<UniversalAdditionalLightData>();

            // A dark floor and the mock room's furniture, so the screenshots show a room the drone has to fly round.
            var floorMat = LitMaterial("FlightFloor", new Color(0.05f, 0.055f, 0.065f), 0.1f, 0.3f);
            var itemMat = LitMaterial("FlightFurniture", new Color(0.13f, 0.14f, 0.16f), 0.1f, 0.35f);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(0.8f, 1f, 0.6f);
            floor.GetComponent<MeshRenderer>().sharedMaterial = floorMat;
            Object.DestroyImmediate(floor.GetComponent<Collider>());
            var room = new GameObject("Mock room furniture").transform;
            foreach (var item in MockFloorPlan.LivingRoom().Items)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
                c.name = item.Label;
                c.transform.SetParent(room, false);
                c.transform.localPosition = item.Centre;
                c.transform.localScale = item.Size;
                c.GetComponent<MeshRenderer>().sharedMaterial = itemMat;
                Object.DestroyImmediate(c.GetComponent<Collider>());
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + "/DemoDrone.prefab");
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            inst.transform.position = Vector3.zero;

            new GameObject("FlightPlayTest").AddComponent<FlightPlayTest>();

            Directory.CreateDirectory(Path.GetDirectoryName(SandboxPath));
            EditorSceneManager.SaveScene(scene, SandboxPath);
            Debug.Log("[FlightBuilder] built " + SandboxPath);
        }

        /// <summary>Opens the sandbox and plays the whole flight script, saving screenshots to unity/Logs/demo/flight_*.png.</summary>
        [MenuItem("Forge/Demo/Flight/Play Test")]
        public static void PlayTest()
        {
            if (File.Exists(LockFile) && (System.DateTime.UtcNow - File.GetLastWriteTimeUtc(LockFile)).TotalMinutes < 10 && !File.ReadAllText(LockFile).Contains("demo-flight"))
            {
                Debug.LogWarning("[FlightBuilder] the editor is held by '" + File.ReadAllText(LockFile).Trim() + "'; try again in a few minutes");
                return;
            }
            File.WriteAllText(LockFile, "demo-flight");
            if (!File.Exists(SandboxPath)) BuildSandbox();
            EditorSceneManager.OpenScene(SandboxPath, OpenSceneMode.Single);
            EditorPrefs.SetBool(FlightPlayTest.PrefKey, true);
            EditorApplication.isPlaying = true;
        }

        // ---- Materials -------------------------------------------------------------------------------------------------------------

        static Material UnlitMaterial(string name, int src, int dst, int zwrite, int queue)
        {
            string path = Art + "/" + name + ".mat";
            var shader = Shader.Find("Forge/Demo/FlightUnlit");
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            m.shader = shader;
            m.SetFloat("_SrcBlend", src);
            m.SetFloat("_DstBlend", dst);
            m.SetFloat("_ZWrite", zwrite);
            m.SetFloat("_Cull", 0f);
            m.SetColor("_Color", Color.white);
            m.renderQueue = queue;
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material LitMaterial(string name, Color color, float smoothness, float metallic)
        {
            string path = Art + "/" + name + ".mat";
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            m.shader = shader;
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", metallic);
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
