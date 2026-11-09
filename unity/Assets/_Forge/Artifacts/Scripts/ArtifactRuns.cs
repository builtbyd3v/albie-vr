using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Forge.Notebook;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Run records (C-12 section 6.2, <c>albie.run/1</c>). Written once when a run ends, never overwritten, off the
    /// physics step. Saved at <c>artifacts/&lt;id&gt;/runs/run_0001.json</c>. Two runs compare like for like when
    /// artifact, version, scenario and layout all match (6.3).
    /// </summary>
    public static class ArtifactRuns
    {
        public const string Schema = "albie.run/1";

        public static string Folder(string artifactId) => Path.Combine(ArtifactLibrary.Folder(artifactId), "runs");

        /// <summary>
        /// The next unused <c>run_####</c> across every artifact on this device, not just this one. The notebook keeps
        /// bare run ids (C-17 <c>observed.runs</c>), so a slalom's run_0001 and a hoop course's run_0001 would be the
        /// same id there: the second was dropped as a duplicate. Called when a run is armed, never per tick.
        /// Nothing may write inside the library from another thread (the notebook's writer, say): on macOS a folder
        /// created during the listing can hide another artifact's folder from it, and its run id is handed out twice.
        /// </summary>
        public static string NextId(string artifactId)
        {
            int highest = 0;
            string root = ArtifactLibrary.Root;
            if (Directory.Exists(root))
                foreach (string artifact in Directory.GetDirectories(root))
                {
                    string folder = Path.Combine(artifact, "runs");
                    if (!Directory.Exists(folder)) continue;
                    foreach (string path in Directory.GetFiles(folder, "run_*.json"))
                        highest = Math.Max(highest, Number(Path.GetFileNameWithoutExtension(path)));
                }
            return $"run_{highest + 1:0000}";
        }

        public static void Save(JObject record)
        {
            if (record == null) return;
            string path = Path.Combine(Folder((string)record["artifact_id"]), (string)record["run_id"] + ".json");
            NotebookFiles.WriteAtomic(path, record.ToString(Formatting.Indented));
        }

        /// <summary>A run from the device library, or a bundled fixture if there isn't one on disk.</summary>
        public static JObject Load(string artifactId, string runId)
        {
            if (string.IsNullOrEmpty(artifactId) || string.IsNullOrEmpty(runId) || runId.IndexOf('.') >= 0 || runId.IndexOf('/') >= 0 || runId.IndexOf('\\') >= 0)
                return null;
            string path = Path.Combine(Folder(artifactId), runId + ".json");
            if (File.Exists(path)) return ReadFile(path);
            var asset = Resources.Load<TextAsset>($"Mocks/artifacts/{artifactId}/runs/{runId}");
            return asset != null ? ReadText(asset.text) : null;
        }

        /// <summary>Device records, newest last. Damaged files are skipped.</summary>
        public static List<JObject> List(string artifactId)
        {
            var list = new List<JObject>();
            string folder = Folder(artifactId);
            if (!Directory.Exists(folder)) return list;
            var files = Directory.GetFiles(folder, "run_*.json");
            Array.Sort(files, (a, b) => Number(Path.GetFileNameWithoutExtension(a)).CompareTo(Number(Path.GetFileNameWithoutExtension(b))));
            foreach (string path in files)
            {
                var record = ReadFile(path);
                if (record != null) list.Add(record);
            }
            return list;
        }

        public static bool LikeForLike(JObject a, JObject b) =>
            a != null && b != null
            && (string)a["artifact_id"] == (string)b["artifact_id"]
            && (int?)a["artifact_version"] == (int?)b["artifact_version"]
            && (string)a["scenario"] == (string)b["scenario"]
            && (string)a["layout_id"] == (string)b["layout_id"];

        /// <summary>
        /// Side-by-side metrics (C-12 <c>artifact_compare</c>). <paramref name="runIds"/> names the runs; otherwise the
        /// two newest device records. Anything that isn't like for like with the first is flagged, never shown as a
        /// clean comparison.
        /// </summary>
        public static JObject Compare(string artifactId, string[] runIds)
        {
            var rows = new JArray();
            JObject first = null;
            if (runIds == null || runIds.Length == 0)
            {
                var all = List(artifactId);
                if (all.Count == 0)
                    return new JObject { ["artifact_id"] = artifactId, ["runs"] = rows, ["message"] = "No runs yet." };
                int from = Math.Max(0, all.Count - 2);
                runIds = new string[all.Count - from];
                for (int i = 0; i < runIds.Length; i++) runIds[i] = (string)all[from + i]["run_id"];
            }
            for (int i = 0; i < runIds.Length; i++)
            {
                var record = Load(artifactId, runIds[i]);
                if (record == null) throw new ArgumentException($"unknown_run: no run \"{runIds[i]}\" for {artifactId}");
                bool like = first == null || LikeForLike(first, record);
                if (first == null) first = record;
                rows.Add(new JObject
                {
                    ["run_id"] = record["run_id"],
                    ["outcome"] = record["outcome"],
                    ["reason"] = record["reason"],
                    ["metrics"] = record["metrics"],
                    ["layout_id"] = record["layout_id"],
                    ["controller"] = record["controller"],
                    ["project_version"] = record["project_version"],
                    ["like_for_like"] = like,
                    ["label"] = like ? null : "different course",
                });
            }
            return new JObject { ["artifact_id"] = artifactId, ["runs"] = rows };
        }

        static JObject ReadFile(string path)
        {
            try { return ReadText(File.ReadAllText(path)); }
            catch (Exception e) when (e is IOException || e is JsonException || e is InvalidCastException)
            {
                Debug.LogWarning($"[Artifacts] skipped {path}: {e.Message}");
                return null;
            }
        }

        static JObject ReadText(string text)
        {
            var record = NotebookFiles.Parse(text) as JObject;
            return record != null && (string)record["schema"] == Schema ? record : null;
        }

        static int Number(string id) =>
            id != null && id.StartsWith("run_") && int.TryParse(id.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }
}
