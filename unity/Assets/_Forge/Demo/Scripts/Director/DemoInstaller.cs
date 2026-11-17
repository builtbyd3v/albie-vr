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
            new GameObject("DemoDirector").AddComponent<DemoDirector>();
            Debug.Log("[Demo] demo mode is on (Resources/demo_config.json): Director installed.");
        }
    }
}
