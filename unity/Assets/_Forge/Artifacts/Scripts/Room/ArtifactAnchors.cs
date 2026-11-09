using System;
using System.IO;
using Forge.Notebook;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Persistent spatial anchors for a course origin (C-12 6.1, PROJECT_CONTEXT 15.3). Layouts store the uuid in
    /// <c>room.anchor</c>. The device keeps a copy so Editor and mock rooms can reuse a layout with no headset; on a
    /// Quest, <see cref="Save"/> also asks <see cref="OVRSpatialAnchor"/> to persist the same pose.
    ///
    /// If the uuid is gone (tracking lost, or a check forgets it), the host makes a new layout. Old runs stay, but they
    /// no longer compare like for like.
    /// </summary>
    public static class ArtifactAnchors
    {
        static string Folder => Path.Combine(ArtifactLibrary.Root, "_anchors");

        /// <summary>Places an anchor at <paramref name="world"/> and returns its uuid. Layouts store that uuid.</summary>
        public static string Save(Pose world)
        {
            string uuid = Guid.NewGuid().ToString("D");
            Write(uuid, world);
            PersistHeadset(uuid, world);
            return uuid;
        }

        /// <summary>True when this device still has the anchor, so the layout can be reused.</summary>
        public static bool Exists(string uuid)
        {
            if (string.IsNullOrEmpty(uuid) || uuid.IndexOf('.') >= 0 || uuid.IndexOf('/') >= 0 || uuid.IndexOf('\\') >= 0)
                return false;
            return File.Exists(Path.Combine(Folder, uuid + ".json"));
        }

        /// <summary>Drops a saved anchor. The next open of that layout makes a new course (C-12: "anchor can't be found").</summary>
        public static void Forget(string uuid)
        {
            if (!Exists(uuid)) return;
            try { File.Delete(Path.Combine(Folder, uuid + ".json")); }
            catch (IOException e) { Debug.LogWarning("[Artifacts] couldn't forget anchor " + uuid + ": " + e.Message); }
        }

        static void Write(string uuid, Pose world)
        {
            var json = new JObject
            {
                ["uuid"] = uuid,
                ["pos"] = new JArray(R(world.position.x), R(world.position.y), R(world.position.z)),
                ["rot"] = new JArray(R(world.rotation.eulerAngles.x), R(world.rotation.eulerAngles.y), R(world.rotation.eulerAngles.z)),
            };
            NotebookFiles.WriteAtomic(Path.Combine(Folder, uuid + ".json"), json.ToString(Formatting.Indented));
        }

        static double R(float v) => Math.Round(v, 4);

        // Headset only. Editor and batch checks never initialize OVRPlugin, so this returns at once. The layout already
        // has a uuid and the device copy, so reruns in one session still match without waiting on the async save.
        static async void PersistHeadset(string uuid, Pose world)
        {
            if (!Application.isPlaying) return;
            try
            {
                if (!OVRPlugin.initialized) return;
                var go = new GameObject("ArtifactAnchor " + uuid);
                go.transform.SetPositionAndRotation(world.position, world.rotation);
                var anchor = go.AddComponent<OVRSpatialAnchor>();
                if (!await anchor.WhenLocalizedAsync())
                {
                    UnityEngine.Object.Destroy(go);
                    return;
                }
                await anchor.SaveAnchorAsync();
                string path = Path.Combine(Folder, uuid + ".json");
                if (!File.Exists(path)) { UnityEngine.Object.Destroy(go); return; }
                var json = NotebookFiles.Parse(File.ReadAllText(path)) as JObject;
                if (json == null) return;
                json["headset"] = anchor.Uuid.ToString("D");
                NotebookFiles.WriteAtomic(path, json.ToString(Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Artifacts] OVRSpatialAnchor.SaveAnchorAsync: " + e.Message);
            }
        }
    }
}
