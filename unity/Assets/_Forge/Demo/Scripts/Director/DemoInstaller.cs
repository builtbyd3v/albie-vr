using UnityEngine;

namespace Forge.Demo
{
    /// <summary>
    /// Puts the Director into any scene when Resources/demo_config.json says "enabled": true (Forge > Demo > Toggle Demo Mode),
    /// so Main needs no edit. Off by default: judges never see the scripted Director. A scene that already holds a Director
    /// (the demo sandbox, or the DemoDirector prefab) keeps its own.
    /// </summary>
    public static class DemoInstaller
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!DemoConfig.Load().enabled) return;
            if (Object.FindAnyObjectByType<DemoDirector>() != null) return;
            // The stations (research, pipeline, drone, drone cam) ride in one Resources prefab so they are in the APK.
            var pieces = Resources.Load<GameObject>("DemoPieces");
            if (pieces != null) Object.Instantiate(pieces).name = "DemoPieces";
            else Debug.LogWarning("[Demo] Resources/DemoPieces.prefab is missing (Forge > Demo > Build Runtime Pieces): only the Director runs.");
            new GameObject("DemoDirector").AddComponent<DemoDirector>();
            Debug.Log("[Demo] demo mode is on (Resources/demo_config.json): Director installed.");
        }
    }
}
