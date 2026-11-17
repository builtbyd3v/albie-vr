using UnityEngine;
using UnityEngine.SceneManagement;

namespace Forge.OS
{
    /// <summary>
    /// Puts the Albie OS shell (Resources/OSShell) into any scene with a <see cref="Workspace"/> that has none, so Main
    /// gets the OS without anyone editing Main.unity, the way ArtifactBootstrap and DeskBootstrap add theirs. Sandboxes
    /// and checks have no Workspace and are left alone. The shell is a root object of the workspace's scene, and it gets its
    /// <see cref="OSShell.workspace"/> before it is enabled.
    /// </summary>
    public static class OSBootstrap
    {
        public const string Resource = "OSShell";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Ensure();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Ensure();

        /// <summary>Adds the shell if the scene needs one. Returns the shell, or null when this isn't a Workspace scene.</summary>
        public static OSShell Ensure()
        {
            var existing = Object.FindAnyObjectByType<OSShell>(FindObjectsInactive.Include);
            if (existing != null) return existing;
            var workspace = Object.FindAnyObjectByType<Workspace>();
            if (workspace == null) return null;
            var prefab = Resources.Load<OSShell>(Resource);
            if (prefab == null)
            {
                Debug.LogWarning("[OS] Resources/OSShell is missing (Forge > OS > Build OS Prefabs); no menu in this scene.");
                return null;
            }

            // OnEnable runs when the shell leaves the inactive holder, so the workspace is set by then.
            var holder = new GameObject("OSShell (setup)");
            SceneManager.MoveGameObjectToScene(holder, workspace.gameObject.scene);
            holder.SetActive(false);
            var shell = Object.Instantiate(prefab, holder.transform);
            shell.name = prefab.name;
            shell.workspace = workspace;
            shell.transform.SetParent(null, false);
            Object.Destroy(holder);
            Debug.Log("[OS] Albie OS shell added to " + workspace.gameObject.scene.name);
            return shell;
        }
    }
}
