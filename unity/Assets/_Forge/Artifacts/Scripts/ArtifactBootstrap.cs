using UnityEngine;
using UnityEngine.SceneManagement;

namespace Forge.Artifacts
{
    /// <summary>
    /// Puts an <see cref="ArtifactHost"/> (Resources/ArtifactHost) into any scene with a <see cref="Workspace"/> that has
    /// none, so the app's Main can show artifacts without anyone editing Main.unity, the way DeskBootstrap adds the desk
    /// extras. Without it, "make me a slalom" on the headset answers <c>no_host</c>. A host it adds isn't placed by
    /// anyone, so it follows the user: each open from Albie's tools brings it in front of whoever is looking
    /// (<see cref="ArtifactHost.ComeToUser"/>). Room courses still stand where the room generator put them.
    /// Sandboxes and checks place their own host and have no Workspace, so they're left alone.
    /// </summary>
    public static class ArtifactBootstrap
    {
        public const string Resource = "ArtifactHost";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Ensure();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Ensure();

        /// <summary>Adds the host if the scene needs one. Returns the scene's host, or null when it's not a Workspace scene.</summary>
        public static ArtifactHost Ensure()
        {
            var existing = Object.FindFirstObjectByType<ArtifactHost>(FindObjectsInactive.Include);
            if (existing != null) return existing;
            if (Object.FindFirstObjectByType<Workspace>() == null) return null;
            var prefab = Resources.Load<ArtifactHost>(Resource);
            if (prefab == null)
            {
                Debug.LogWarning("[Artifacts] Resources/ArtifactHost missing (Forge > Build Artifacts Sandbox); no artifacts in this scene.");
                return null;
            }
            var host = Object.Instantiate(prefab);
            host.name = prefab.name;
            host.FollowsUser = true;
            host.ComeToUser();
            Debug.Log("[Artifacts] Artifact host added to " + host.gameObject.scene.name);
            return host;
        }
    }
}
