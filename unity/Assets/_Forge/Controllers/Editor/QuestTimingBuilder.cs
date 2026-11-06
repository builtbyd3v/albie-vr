using Forge.Controllers;
using Oculus.Interaction;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// The Quest timing kit (<see cref="QuestTimingKit"/>). Safe to run again; each overwrites its output.
/// Forge > Controllers > Build Quest Timing Panel: Prefabs/QuestTimingPanel.prefab, a variant of Core's ForgePanel with
/// the kit and its report text (it can go into Main later, to time training under Main's own load).
/// Forge > Controllers > Build Quest Timing Scene: ControllersQuestTiming.unity, a passthrough rig with the panel 1 m
/// in front of the eyes. Play it in the Editor for Editor numbers.
/// Forge > Controllers > Build Quest Timing APK: builds that scene alone to Builds/ControllersTiming.apk. It doesn't
/// touch Main or the build settings' scene list.
/// </summary>
public static class QuestTimingBuilder
{
    const string ScenePath = "Assets/_Forge/Controllers/ControllersQuestTiming.unity";
    const string PrefabPath = "Assets/_Forge/Controllers/Prefabs/QuestTimingPanel.prefab";
    const string ForgePanelPath = "Assets/_Forge/Core/Prefabs/ForgePanel.prefab";
    const string CameraRigPath = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
    public const string ApkPath = "Builds/ControllersTiming.apk";

    [MenuItem("Forge/Controllers/Build Quest Timing Panel")]
    public static void BuildPanel()
    {
        // A variant keeps Core's frame, title bar and ray/poke wiring, and picks up Hudson's later changes.
        var root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePanelPath));
        root.name = "QuestTimingPanel";
        var panel = root.GetComponent<ForgePanel>();
        panel.Title = "Quest timing";
        panel.SizeMeters = new Vector2(1.0f, 0.65f);
        var placeholder = panel.Content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);

        var go = new GameObject("Report", typeof(RectTransform), typeof(TextMeshProUGUI));
        var rect = (RectTransform)go.transform;
        rect.SetParent(panel.Content, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(24f, 16f);
        rect.offsetMax = new Vector2(-24f, -16f);
        var text = go.GetComponent<TextMeshProUGUI>();
        if (ForgeTheme.Font != null) text.font = ForgeTheme.Font;
        text.fontSize = 20f; // mm: readable at 1 m
        text.color = ForgeTheme.PanelText;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.raycastTarget = false;
        text.text = "Quest timing kit";

        root.AddComponent<QuestTimingKit>().text = text;

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PrefabPath));
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        Debug.Log("[QuestTimingBuilder] Built " + PrefabPath);
    }

    [MenuItem("Forge/Controllers/Build Quest Timing Scene")]
    public static void BuildScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        BuildPanel();

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Passthrough rig, as in Main. Eye-level origin, so (0, 0, 1) is 1 m straight ahead at startup.
        var rig = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(CameraRigPath));
        var manager = rig.GetComponent<OVRManager>();
        manager.isInsightPassthroughEnabled = true;
        manager.trackingOriginType = OVRManager.TrackingOrigin.EyeLevel;
        rig.AddComponent<OVRPassthroughLayer>();
        foreach (var cam in rig.GetComponentsInChildren<Camera>(true))
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.clear;
        }

        // ForgePanel's canvas registers with the Interaction SDK's canvas module, which needs an EventSystem.
        var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(PointableCanvasModule));
        eventSystem.GetComponent<PointableCanvasModule>().ExclusiveMode = false;

        var panel = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
        panel.transform.position = new Vector3(0f, 0f, 1f);

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("[QuestTimingBuilder] Built " + ScenePath + ". Forge > Controllers > Build Quest Timing APK makes the headset build.");
    }

    [MenuItem("Forge/Controllers/Build Quest Timing APK")]
    public static void BuildApk()
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null) BuildScene();
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = ApkPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
        });
        var s = report.summary;
        long bytes = System.IO.File.Exists(ApkPath) ? new System.IO.FileInfo(ApkPath).Length : 0;
        Debug.Log($"[QuestTimingBuilder] APK {s.result}: {ApkPath}, {bytes / (1024 * 1024)} MB, {s.totalErrors} errors, {s.totalTime.TotalMinutes:F1} min. " +
                  $"Install: adb install -r {ApkPath}");
    }
}
