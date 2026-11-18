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
            host.Shown += h => EnsureBrain(h);
            host.Shown += h => EnsureDrone(h);
            host.Shown += h => DroneBrain.Ensure(h);  // the drone's learning loop (CourseBrainDrone.cs), after the drone
            host.ComeToUser();
            Debug.Log("[Artifacts] Artifact host added to " + host.gameObject.scene.name);
            return host;
        }

        /// <summary>
        /// In the app, a slalom for the truck gets ML A's brain truck (<see cref="SlalomBrain"/>) at its start, so "drive
        /// it twice and I'll learn it" (the <c>brain_*</c> and <c>autopilot_*</c> tools, ML B's brain panel) drives a
        /// real truck on the course, not just the panel's preview. It drives by the Touch thumbstick or the hands
        /// transmitter, and its sensors see the cones and the room's colliders (Main's EffectMesh). Called each time the
        /// host builds a scene. Once spawned, the truck follows the host itself (<see cref="SlalomBrain.Spawn"/>): it hides
        /// when the scene comes down and is back at the start when a truck course is built. Stands in for Person 2's
        /// Thing runtime until that's on main.
        /// </summary>
        public static SlalomBrain EnsureBrain(ArtifactHost host)
        {
            if (host == null || !host.FollowsUser) return null;
            bool truckCourse = host.Run != null && (string)host.OpenManifest?["links"]?["thing"] == "truck";
            if (!truckCourse) return null;
            var brain = Object.FindFirstObjectByType<SlalomBrain>();
            return brain != null ? brain : SlalomBrain.Spawn(host, keyHints: false);
        }

        /// <summary>
        /// In the app, a course for the drone (<c>links.thing</c> is <c>"drone"</c>, as the hoop course) gets the drone
        /// (<see cref="ArtifactDrone"/>) standing at its start, bound as the host's Thing, flown from the Drive window or
        /// by the <c>drone_*</c> tools. Called each time the host builds a scene. Once spawned, the drone follows the host
        /// itself (<see cref="ArtifactDrone.Spawn"/>): it hides when the host shows anything else and is back at the
        /// start, landed, when a drone course is built. A drone course needs no run block to get its drone: an unscored
        /// one can still be flown.
        /// </summary>
        public static ArtifactDrone EnsureDrone(ArtifactHost host)
        {
            if (host == null || !host.FollowsUser) return null;
            if (!ArtifactDrone.IsDroneCourse(host)) return null;
            var drone = Object.FindFirstObjectByType<ArtifactDrone>();
            return drone != null ? drone : ArtifactDrone.Spawn(host);
        }
    }
}
