// AlbieSetupCli.cs — batch-mode entry point for the reproducible Albie pet setup.
//
//   Unity -batchmode -projectPath <repo>/unity \
//         -executeMethod Forge.Pet.Editor.AlbieSetupCli.BuildAllAndQuit -logFile -
//
// Exits 0 when every step of AlbieSetup.BuildAll succeeded, 1 otherwise.

using System;
using UnityEditor;
using UnityEngine;

namespace Forge.Pet.Editor
{
    public static class AlbieSetupCli
    {
        public static void BuildAllAndQuit()
        {
            int exitCode = 0;
            try
            {
                AlbieSetup.BuildAll();
                AssetDatabase.SaveAssets();
                if (!AlbieSetup.LastBuildSucceeded)
                {
                    Debug.LogError("[AlbieSetupCli] BuildAll reported failures (see errors above).");
                    exitCode = 1;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                exitCode = 1;
            }
            Debug.Log($"[AlbieSetupCli] Exiting with code {exitCode}.");
            EditorApplication.Exit(exitCode);
        }
    }
}
