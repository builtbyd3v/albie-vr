using System.IO;
using UnityEditor;
using UnityEngine;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Build OS Prefabs: makes (or updates) everything the OS needs as an asset. For now that is
    /// Resources/OSShell.prefab (the shell, the window manager and the keys); later parts add theirs here. Safe to run
    /// again: it opens the existing prefab and only adds what's missing.
    /// </summary>
    public static class OSPrefabBuilder
    {
        public const string Root = "Assets/_Forge/OS";
        public const string ShellPath = Root + "/Resources/OSShell.prefab";

        [MenuItem("Forge/OS/Build OS Prefabs")]
        public static void Build()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "_Forge/OS/Resources"));
            BuildShell();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[OS] Built OS prefabs: " + ShellPath);
        }

        static void BuildShell()
        {
            GameObject root;
            bool exists = File.Exists(Path.Combine(Application.dataPath, "../" + ShellPath));
            if (exists) root = PrefabUtility.LoadPrefabContents(ShellPath);
            else root = new GameObject("OSShell");
            try
            {
                Ensure<OSShell>(root);
                Ensure<OSWindows>(root);
                Ensure<OSKeys>(root);
                PrefabUtility.SaveAsPrefabAsset(root, ShellPath);
            }
            finally
            {
                if (exists) PrefabUtility.UnloadPrefabContents(root);
                else Object.DestroyImmediate(root);
            }
        }

        /// <summary>Adds the component if the object doesn't have it yet; returns it.</summary>
        public static T Ensure<T>(GameObject go) where T : Component
        {
            var component = go.GetComponent<T>();
            return component != null ? component : go.AddComponent<T>();
        }
    }
}
