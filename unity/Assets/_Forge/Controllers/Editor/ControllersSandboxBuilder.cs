using Forge.Controllers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds Assets/_Forge/Controllers/ControllersSandbox.unity in code: a top-down desktop view of the mock truck on the
/// 5-cone slalom, driven through ControllerHost with the mock controller files. Hub off, no XR rig yet (the hands
/// transmitter is Person 1's; it writes the same ManualInputs). Open the scene and press Play.
/// </summary>
public static class ControllersSandboxBuilder
{
    const string Folder = "Assets/_Forge/Controllers/";
    const string ScenePath = Folder + "ControllersSandbox.unity";

    [MenuItem("Forge/Controllers/Build Sandbox")]
    public static void Build()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var floorMat = Mat("Floor", new Color(0.10f, 0.12f, 0.15f));
        var wallMat = Mat("Wall", new Color(0.30f, 0.33f, 0.38f));
        var coneMat = Mat("Cone", new Color(1.00f, 0.45f, 0.10f));
        var truckMat = Mat("Truck", new Color(0.13f, 0.81f, 0.53f));
        var finishMat = Mat("Finish", new Color(0.95f, 0.95f, 0.95f));
        var rayMat = Mat("Ray", new Color(0.30f, 0.55f, 1.00f), unlit: true);

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.5f);

        var camera = new GameObject("Top-down camera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.orthographic = true;
        camera.orthographicSize = 3.3f;
        camera.transform.SetPositionAndRotation(new Vector3(4.65f, 8f, 0f), Quaternion.Euler(90f, 0f, 0f));
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.025f, 0.04f, 0.07f);

        var light = new GameObject("Light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(60f, -30f, 0f);

        // The course, in sim coordinates: sim x → world x, sim y → world z.
        float midX = (MockTruckSim.WallXMin + MockTruckSim.WallXMax) / 2f, lenX = MockTruckSim.WallXMax - MockTruckSim.WallXMin;
        float lenY = MockTruckSim.WallYMax - MockTruckSim.WallYMin;
        Box("Floor", new Vector3(midX, -0.01f, 0f), new Vector3(lenX, 0.02f, lenY), floorMat);
        Box("Wall -y", new Vector3(midX, 0.05f, MockTruckSim.WallYMin), new Vector3(lenX, 0.1f, 0.04f), wallMat);
        Box("Wall +y", new Vector3(midX, 0.05f, MockTruckSim.WallYMax), new Vector3(lenX, 0.1f, 0.04f), wallMat);
        Box("Wall -x", new Vector3(MockTruckSim.WallXMin, 0.05f, 0f), new Vector3(0.04f, 0.1f, lenY), wallMat);
        Box("Wall +x", new Vector3(MockTruckSim.WallXMax, 0.05f, 0f), new Vector3(0.04f, 0.1f, lenY), wallMat);
        Box("Finish", new Vector3(MockTruckSim.FinishX, 0.001f, 0f), new Vector3(0.03f, 0.002f, lenY), finishMat);
        var cones = new GameObject("Cones").transform;
        foreach (float x in MockTruckSim.ConeX)
        {
            var cone = Primitive(PrimitiveType.Cylinder, "Cone", coneMat);
            cone.transform.SetParent(cones);
            cone.transform.position = new Vector3(x, 0.05f, 0f);
            cone.transform.localScale = new Vector3(MockTruckSim.ConeRadius * 2f, 0.05f, MockTruckSim.ConeRadius * 2f);
        }

        // The truck: a body plus a nose so its heading reads from above. Local +x is forward.
        var truck = new GameObject("Mock truck").transform;
        var body = Primitive(PrimitiveType.Cube, "Body", truckMat);
        body.transform.SetParent(truck);
        body.transform.localScale = new Vector3(0.36f, 0.08f, 0.22f);
        var nose = Primitive(PrimitiveType.Cube, "Nose", finishMat);
        nose.transform.SetParent(truck);
        nose.transform.localPosition = new Vector3(0.15f, 0.05f, 0f);
        nose.transform.localScale = new Vector3(0.06f, 0.02f, 0.16f);

        var sandbox = new GameObject("Controllers sandbox").AddComponent<ControllersSandbox>();
        sandbox.truck = truck;
        sandbox.rays = new LineRenderer[MockTruckSim.Rays];
        for (int i = 0; i < MockTruckSim.Rays; i++)
        {
            var ray = new GameObject($"Ray d{i}").AddComponent<LineRenderer>();
            ray.transform.SetParent(sandbox.transform);
            ray.sharedMaterial = rayMat;
            ray.positionCount = 2;
            ray.widthMultiplier = 0.012f;
            ray.shadowCastingMode = ShadowCastingMode.Off;
            ray.receiveShadows = false;
            sandbox.rays[i] = ray;
        }

        EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("[Controllers] Built " + ScenePath + ". Press Play: 1 manual, 2 cruise, WASD drive, [ ] max_speed, R reset.");
    }

    static GameObject Primitive(PrimitiveType type, string name, Material material)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());   // the sim does its own collisions
        go.GetComponent<MeshRenderer>().sharedMaterial = material;
        return go;
    }

    static void Box(string name, Vector3 position, Vector3 size, Material material)
    {
        var go = Primitive(PrimitiveType.Cube, name, material);
        go.transform.position = position;
        go.transform.localScale = size;
    }

    static Material Mat(string name, Color color, bool unlit = false)
    {
        string path = Folder + "Materials/" + name + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        var shader = Shader.Find(unlit ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit");
        if (mat == null)
        {
            if (!AssetDatabase.IsValidFolder(Folder + "Materials")) AssetDatabase.CreateFolder(Folder.TrimEnd('/'), "Materials");
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = shader;
        mat.SetColor("_BaseColor", color);
        EditorUtility.SetDirty(mat);
        return mat;
    }
}
