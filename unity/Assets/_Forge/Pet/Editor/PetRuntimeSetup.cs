// PetRuntimeSetup.cs — Forge/Pet/8. Build PetRuntime Prefab
//
// Assembles Assets/_Forge/Pet/Prefabs/PetRuntime.prefab, the ONE prefab Core's MainSceneBuilder drops into Main.unity
// (by that path: it is a contract). Everything Albie needs at the desk, nothing scene-specific:
//
//   PetRuntime
//   ├─ AlbieAssistant      nested AlbieAssistant.prefab instance + the in-scene additions PetMain.unity used to carry:
//   │   │                  WakeListener, AlbieTalkAnimator, AlbieSummon, AlbieFollowUp, AlbieFetch, AlbieFixCard
//   │   └─ Voice           AudioSource + StreamedPcmTextToSpeech, wired into AlbieAgentBridge.tts
//   ├─ WhistleButton       desk puck (+ Interaction SDK poke / ray), the AlbieMrukAdapter's whistleTemplate (one per table)
//   ├─ AlbieCrew           lead = AlbieAssistant, crewPrefab = AlbieAssistant.prefab
//   ├─ AlbieHatchery       the egg + picker (+ Interaction SDK poke / ray)
//   ├─ ArrivalGreeting     the first-arrival card + START ring (ArrivalSetup; inactive until used; ring = Core DockRing
//   │                      + Interaction SDK ray / poke canvas interaction)
//   └─ (root) ArrivalDirector   DeskPresence → hop / greeting / digest / START / code mode choreography, workspace.mode to the hub
//
// Built in a preview scene from the same helpers the "Add ... To Open Scene" menus use (WhistleSetup, CrewSetup) with Undo
// off. Idempotent: re-running overwrites the prefab (GUID kept). Also the last step of Forge/Pet/Build All.
// -executeMethod Forge.Pet.Editor.PetRuntimeSetup.BuildPetRuntimePrefab

using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Forge.Pet.Editor
{
    public static class PetRuntimeSetup
    {
        /// Contract with Core: MainSceneBuilder instantiates this path.
        public const string PrefabPath = "Assets/_Forge/Pet/Prefabs/PetRuntime.prefab";
        /// Where Albie starts inside PetRuntime (the root sits at the room origin; the floor NavMesh is built around him at runtime).
        static readonly Vector3 AlbieSpawn = new Vector3(1f, 0f, 0.5f);

        [MenuItem("Forge/Pet/8. Build PetRuntime Prefab", priority = 8)]
        public static void BuildPetRuntimePrefab()
        {
            try
            {
                string msg = Build();
                AssetDatabase.SaveAssets();
                Debug.Log("[PetRuntimeSetup] " + msg);
            }
            catch (Exception e)
            {
                Debug.LogError("[PetRuntimeSetup] FAILED: " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
            }
        }

        /// Builds (or rebuilds) the prefab; returns a one-line summary. Throws on failure.
        public static string Build()
        {
            GameObject albiePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AlbieSetup.PrefabPath);
            if (albiePrefab == null)
            {
                AlbieSetup.BuildPrefab();
                albiePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AlbieSetup.PrefabPath);
            }
            if (albiePrefab == null) throw new Exception("AlbieAssistant prefab missing: " + AlbieSetup.PrefabPath);

            Scene preview = EditorSceneManager.NewPreviewScene();
            bool undoWas = WhistleSetup.UseUndo;
            WhistleSetup.UseUndo = false;
            string msg;
            try
            {
                var root = new GameObject("PetRuntime");
                SceneManager.MoveGameObjectToScene(root, preview);

                // ---- Albie: nested prefab instance ----
                var albie = (GameObject)PrefabUtility.InstantiatePrefab(albiePrefab, preview);
                albie.name = "AlbieAssistant";
                albie.transform.SetParent(root.transform, false);
                albie.transform.localPosition = AlbieSpawn;
                albie.transform.localRotation = Quaternion.identity;
                var controller = albie.GetComponent<AlbieController>();
                if (controller == null) throw new Exception("AlbieAssistant prefab has no AlbieController.");
                var bridge = albie.GetComponent<AlbieAgentBridge>();
                var adapter = albie.GetComponent<AlbieMrukAdapter>();
                if (adapter == null) Debug.LogWarning("[PetRuntimeSetup] AlbieAssistant has no AlbieMrukAdapter; the desk NavMesh won't be built in Main.");

                // ---- Voice path (what PetMain.unity had on the instance) ----
                StreamedPcmTextToSpeech voice = WhistleSetup.AddVoice(albie);
                WhistleSetup.AddTalkAnimator(albie, voice);
                if (albie.GetComponent<WakeListener>() == null) albie.AddComponent<WakeListener>();
                if (albie.GetComponent<AlbieFetch>() == null) albie.AddComponent<AlbieFetch>();  // "hand me a fin"
                var fixCard = albie.GetComponent<AlbieFixCard>();  // Fix it / Not now on Albie's fix offers
                if (fixCard == null) fixCard = albie.AddComponent<AlbieFixCard>();
                WhistleSetup.SetRef(fixCard, "panelPrefab", AssetDatabase.LoadAssetAtPath<ForgePanel>(ForgePrefabBuilder.PanelPath));

                // ---- Whistle ----
                WhistleButton button = WhistleSetup.CreateButton(controller, root.transform);
                string whistleIsdk = WhistleSetup.AddInteractionSdk(button, warnNoInteractors: false);
                WhistleSetup.AddSummon(albie, button, voice);
                WhistleSetup.AddFollowUp(albie, voice);
                if (adapter != null) WhistleSetup.SetRef(adapter, "whistleTemplate", button);

                // ---- Crew ----
                CrewSetup.CreateCrew(controller, albiePrefab, root.transform);
                AlbieHatchery egg = CrewSetup.CreateHatchery(controller, root.transform);
                string eggIsdk = CrewSetup.AddInteraction(egg);

                // ---- Arrival choreography: greeting card + START ring, and the director ----
                ArrivalGreeting greeting = ArrivalSetup.CreateGreeting(root.transform);
                string greetingIsdk = ArrivalSetup.AddCanvasInteraction(greeting);
                var director = root.AddComponent<ArrivalDirector>();
                WhistleSetup.SetRef(director, "controller", controller);
                if (bridge != null) WhistleSetup.SetRef(director, "bridge", bridge);
                if (adapter != null) WhistleSetup.SetRef(director, "adapter", adapter);
                WhistleSetup.SetRef(director, "greeting", greeting);

                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool success);
                if (!success || saved == null) throw new Exception("PrefabUtility.SaveAsPrefabAsset failed for " + PrefabPath);

                int albieComponents = albie.GetComponents<MonoBehaviour>().Length;
                msg = $"{PrefabPath} (AlbieAssistant with {albieComponents} scripts + Voice; WhistleButton ({whistleIsdk}); AlbieCrew; AlbieHatchery ({eggIsdk}); ArrivalGreeting ({greetingIsdk}); ArrivalDirector)";
            }
            finally
            {
                WhistleSetup.UseUndo = undoWas;
                EditorSceneManager.ClosePreviewScene(preview);
            }
            return msg;
        }
    }
}
