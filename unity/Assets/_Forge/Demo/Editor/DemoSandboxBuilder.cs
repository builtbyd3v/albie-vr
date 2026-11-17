using System.Collections.Generic;
using System.IO;
using Forge.Pet;
using Oculus.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

namespace Forge.Demo.Editor
{
    /// <summary>
    /// Forge > Demo > Build Demo Sandbox: DemoSandbox.unity, the whole story on the desktop rig. Seated head at 1.15 m facing +Z,
    /// a dark floor, a sun, the Director (its prefab), Albie (with a runtime NavMesh, like the pet sandbox), and every other prefab
    /// the stations keep in Demo/Prefabs (research, pipeline, flight: whatever exists when this runs). Pieces that install
    /// themselves at runtime need nothing here. ForgeNet is in mock mode, so a laptop remote is simulated with ForgeNet.Inject.
    /// </summary>
    public static class DemoSandboxBuilder
    {
        public const string ScenePath = "Assets/_Forge/Demo/Sandboxes/DemoSandbox.unity";
        public const string DirectorPrefabPath = "Assets/_Forge/Demo/Prefabs/DemoDirector.prefab";
        const string FloorMaterialPath = "Assets/_Forge/Demo/Sandboxes/DemoFloor.mat";
        const string AlbiePrefabPath = "Assets/_Forge/Pet/Prefabs/AlbieAssistant.prefab";
        const string PrefabsDir = "Assets/_Forge/Demo/Prefabs";
        const float HeadHeight = 1.15f;

        [MenuItem("Forge/Demo/Build Demo Sandbox")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[DemoSandbox] leave Play mode first.");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            try { Debug.Log("[DemoSandbox] built " + BuildScene()); }
            catch (System.Exception e) { Debug.LogError("[DemoSandbox] FAILED: " + e.Message + "\n" + e.StackTrace); }
        }

        /// <summary>Builds (or rebuilds) Prefabs/DemoDirector.prefab: the Director component and nothing else.</summary>
        [MenuItem("Forge/Demo/Build Director Prefab")]
        public static GameObject BuildDirectorPrefab()
        {
            EnsureFolder(PrefabsDir);
            var go = new GameObject("DemoDirector");
            go.AddComponent<DemoDirector>();
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(go, DirectorPrefabPath);
            Object.DestroyImmediate(go);
            Debug.Log("[DemoSandbox] " + DirectorPrefabPath);
            return saved;
        }

        public const string PiecesPrefabPath = "Assets/_Forge/Demo/Resources/DemoPieces.prefab";

        /// <summary>
        /// Builds Resources/DemoPieces.prefab: every station prefab in Demo/Prefabs (research, pipeline, drone, drone cam) nested
        /// under one root, so DemoInstaller can add the whole demo to Main (and the APK) at runtime with no scene edit.
        /// </summary>
        [MenuItem("Forge/Demo/Build Runtime Pieces")]
        public static void BuildRuntimePieces()
        {
            EnsureFolder("Assets/_Forge/Demo/Resources");
            var root = new GameObject("DemoPieces");
            var names = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { PrefabsDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path == DirectorPrefabPath) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
                instance.name = prefab.name;
                names.Add(prefab.name);
            }
            PrefabUtility.SaveAsPrefabAsset(root, PiecesPrefabPath);
            Object.DestroyImmediate(root);
            Debug.Log("[DemoSandbox] " + PiecesPrefabPath + " (" + string.Join(", ", names) + ")");
        }

        static string BuildScene()
        {
            EnsureFolder("Assets/_Forge/Demo/Sandboxes");
            GameObject directorPrefab = BuildDirectorPrefab();
            BuildRuntimePieces();
            var albiePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AlbiePrefabPath);
            if (albiePrefab == null) throw new FileNotFoundException("Albie prefab missing: " + AlbiePrefabPath + " (Forge > Pet > Build All)");
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.DesktopRigPath);
            if (rigPrefab == null) throw new FileNotFoundException("Desktop rig prefab missing: " + ForgePrefabBuilder.DesktopRigPath + " (Forge > Build Core Prefabs)");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
            RenderSettings.sun = sun;
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.34f, 0.35f, 0.4f);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(2f, 1f, 2f);   // 20 x 20 m
            floor.GetComponent<Renderer>().sharedMaterial = FloorMaterial();

            var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(PointableCanvasModule));
            eventSystem.GetComponent<PointableCanvasModule>().ExclusiveMode = false;

            var net = new GameObject("ForgeNet").AddComponent<ForgeNet>();
            net.mode = ForgeNetMode.Mock;

            // The user: seated head height, facing the room.
            var rigGo = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);
            rigGo.name = "DesktopRig";
            rigGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var rig = rigGo.GetComponent<DesktopRig>();
            if (rig != null) { rig.standingEyeHeight = HeadHeight; rig.seatedEyeHeight = HeadHeight; }
            rigGo.SetActive(true);

            // Albie, and the NavMesh he walks on.
            var albie = (GameObject)PrefabUtility.InstantiatePrefab(albiePrefab);
            albie.name = "AlbieAssistant";
            albie.transform.SetPositionAndRotation(new Vector3(1.1f, 0f, 1.5f), Quaternion.Euler(0f, 200f, 0f));
            var controller = albie.GetComponent<AlbieController>();

            var navGo = new GameObject("Navigation");
            var floorNavGo = new GameObject("FloorNav");
            floorNavGo.transform.SetParent(navGo.transform, false);
            var floorNav = floorNavGo.AddComponent<AlbieNavSurface>();
            var nav = navGo.AddComponent<AlbieSandboxNav>();
            var so = new SerializedObject(nav);
            so.FindProperty("floor").objectReferenceValue = floorNav;
            so.FindProperty("controller").objectReferenceValue = controller;
            so.FindProperty("floorCenter").vector3Value = Vector3.zero;
            so.FindProperty("floorSize").vector2Value = new Vector2(20f, 20f);
            so.ApplyModifiedPropertiesWithoutUndo();

            var director = (GameObject)PrefabUtility.InstantiatePrefab(directorPrefab);
            director.name = "DemoDirector";

            var pieces = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { PrefabsDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path == DirectorPrefabPath) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.name = prefab.name;
                pieces.Add(prefab.name);
            }

            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new System.Exception("could not save " + ScenePath);
            return ScenePath + " (pieces: " + (pieces.Count == 0 ? "none yet" : string.Join(", ", pieces)) + ")";
        }

        static Material FloorMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(FloorMaterialPath);
            if (mat != null) return mat;
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) throw new System.Exception("URP Lit shader not found");
            mat = new Material(lit);
            mat.SetColor("_BaseColor", new Color(0.075f, 0.08f, 0.095f));
            mat.SetFloat("_Smoothness", 0.25f);
            AssetDatabase.CreateAsset(mat, FloorMaterialPath);
            return mat;
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
