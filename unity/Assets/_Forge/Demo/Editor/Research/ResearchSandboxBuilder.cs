using System;
using System.IO;
using Oculus.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Forge.Demo.Editor
{
    /// <summary>
    /// Forge > Demo > Research: builds the station prefab (Demo/Prefabs/ResearchStation.prefab) and its sandbox scene
    /// (Demo/Sandboxes/ResearchSandbox.unity: seated camera at 1.15 m, dark floor, one light, the station, no hub), and runs the
    /// play test that fires open, cameras, models, verdict, a simulated poke and close, and saves screenshots to
    /// unity/Logs/demo/research_*.png with a report in research_report.txt. The editor is shared with the other demo stations,
    /// so both the build and the play test hold unity/Temp/demo-editor.lock while they use it.
    /// </summary>
    public static class ResearchSandboxBuilder
    {
        const string PrefabDir = "Assets/_Forge/Demo/Prefabs";
        const string PrefabPath = PrefabDir + "/ResearchStation.prefab";
        const string SandboxDir = "Assets/_Forge/Demo/Sandboxes";
        const string ScenePath = SandboxDir + "/ResearchSandbox.unity";
        const string FloorPath = SandboxDir + "/ResearchFloor.mat";
        const string Station = "demo-research";

        // ------------------------------------------------------------------ the shared-editor lock (docs/demo/briefs/_COMMON.md)

        static string LockFile => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ResearchSandbox.LockPath));

        static bool TryLock()
        {
            try
            {
                string path = LockFile;
                if (File.Exists(path))
                {
                    TimeSpan age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                    string holder = "";
                    try { holder = File.ReadAllText(path).Trim(); } catch (IOException) { }
                    if (age.TotalMinutes < 10 && holder != Station)
                    {
                        Debug.LogWarning("[ResearchSandbox] The editor is held by '" + holder + "' (" + (int)age.TotalSeconds + " s old). Try again in a minute.");
                        return false;
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Station);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ResearchSandbox] Could not take the editor lock: " + e.Message);
                return false;
            }
        }

        static void Unlock()
        {
            try
            {
                string path = LockFile;
                if (File.Exists(path) && File.ReadAllText(path).Trim() == Station) File.Delete(path);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ prefab

        [MenuItem("Forge/Demo/Research/Build Prefab")]
        public static void BuildPrefab()
        {
            EnsureFolder(PrefabDir);
            var panelPrefab = AssetDatabase.LoadAssetAtPath<ForgePanel>(ForgePrefabBuilder.PanelPath);
            if (panelPrefab == null)
            {
                Debug.LogError("[ResearchSandbox] Core/Prefabs/ForgePanel.prefab is missing; run Forge > Build Core Prefabs first.");
                return;
            }
            var go = new GameObject("ResearchStation");
            try
            {
                var station = go.AddComponent<ResearchStation>();
                var so = new SerializedObject(station);
                so.FindProperty("panelPrefab").objectReferenceValue = panelPrefab;
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
            Debug.Log("[ResearchSandbox] Built " + PrefabPath);
        }

        // ------------------------------------------------------------------ sandbox

        [MenuItem("Forge/Demo/Research/Build Sandbox")]
        public static void BuildSandbox()
        {
            if (!TryLock()) return;
            try { Build(); }
            finally { Unlock(); }
        }

        static void Build()
        {
            BuildPrefab();
            EnsureFolder(SandboxDir);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.30f, 0.32f, 0.36f);
            var sun = new GameObject("Light").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 0.9f;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // A dark floor, so the panels read the way they do against a dim room.
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.position = new Vector3(0f, -0.01f, 0f);
            floor.transform.localScale = new Vector3(8f, 0.02f, 8f);
            floor.GetComponent<Renderer>().sharedMaterial = FloorMaterial();
            UnityEngine.Object.DestroyImmediate(floor.GetComponent<Collider>());

            // The desktop rig, eyes at seated height 1.15 m.
            var rigObject = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.DesktopRigPath));
            rigObject.transform.position = Vector3.zero;
            var rigSo = new SerializedObject(rigObject.GetComponent<DesktopRig>());
            rigSo.FindProperty("standingEyeHeight").floatValue = 1.15f;
            rigSo.FindProperty("seatedEyeHeight").floatValue = 1.15f;
            rigSo.ApplyModifiedPropertiesWithoutUndo();
            Camera camera = rigObject.GetComponentInChildren<Camera>(true);
            camera.tag = "MainCamera";
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.04f, 0.05f);
            camera.nearClipPlane = 0.05f;
            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) data = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            data.allowXRRendering = false;

            var events = new GameObject("EventSystem", typeof(EventSystem), typeof(PointableCanvasModule));
            events.GetComponent<PointableCanvasModule>().ExclusiveMode = false;

            var station = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath), scene);
            station.name = "ResearchStation";
            new GameObject("ResearchSandbox").AddComponent<ResearchSandbox>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[ResearchSandbox] Built " + ScenePath + ". Press Play, then 1 open, 2 cameras, 3 models, 4 verdict, 5 poke, 6 close, 7 reset.");
        }

        static Material FloorMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(FloorPath);
            if (material != null) return material;
            material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetColor("_BaseColor", new Color(0.05f, 0.055f, 0.065f));
            material.SetFloat("_Smoothness", 0.1f);
            AssetDatabase.CreateAsset(material, FloorPath);
            return material;
        }

        // ------------------------------------------------------------------ play test

        [MenuItem("Forge/Demo/Research/Play Test")]
        public static void PlayTest()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[ResearchSandbox] Leave Play mode first.");
                return;
            }
            if (!TryLock()) return;
            try
            {
                Build();
                SessionState.SetBool(ResearchSandbox.PlayTestFlag, true);
                EditorApplication.isPlaying = true;   // the sandbox runs the test, releases the lock and leaves Play mode
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Unlock();
            }
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
