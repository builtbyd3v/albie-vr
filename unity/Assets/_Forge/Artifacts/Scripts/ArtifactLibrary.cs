using System;
using System.Collections.Generic;
using System.IO;
using Forge.Notebook;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The artifacts on this device (<c>persistentDataPath/artifacts/&lt;id&gt;/</c>) and the built-in templates they can be
    /// made from (<c>Resources/ArtifactTemplates/&lt;name&gt;/</c>). A template needs no model output, so "Make me a
    /// slalom" works with no network (C-12 section 10). Writes go through a temp file.
    /// </summary>
    public static class ArtifactLibrary
    {
        /// <summary>The built-in templates.</summary>
        public static readonly string[] Templates = { "slalom", "hoop_course", "repo_activity", "cell", "heart", "brain", "plant_cell", "eye", "dna", "double_pendulum", "solar_system" };

        /// <summary>Checks point this at a scratch folder.</summary>
        public static string RootOverride;
        public static string Root => RootOverride ?? Path.Combine(Application.persistentDataPath, "artifacts");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => RootOverride = null;

        public static string Folder(string id) => Path.Combine(Root, id);

        public static bool TryTemplate(string name, out JObject manifest, out JObject scene)
        {
            manifest = scene = null;
            if (Array.IndexOf(Templates, name) < 0) return false;
            var m = Resources.Load<TextAsset>($"ArtifactTemplates/{name}/manifest");
            var s = Resources.Load<TextAsset>($"ArtifactTemplates/{name}/scene");
            if (m == null || s == null) return false;
            manifest = (JObject)NotebookFiles.Parse(m.text);
            scene = (JObject)NotebookFiles.Parse(s.text);
            return true;
        }

        public sealed class Created
        {
            public string Id;
            public int Version;
            public string Change;  // "created", "updated" or "unchanged"
        }

        /// <summary>
        /// Makes an artifact from a template in the library, linked to the open notebook project if there is one
        /// (standalone otherwise). <paramref name="inputs"/> become the inputs' defaults; out of range is refused, not
        /// clamped. Making the same thing again changes nothing; a different one bumps the version.
        /// Bad input throws "code: message".
        /// </summary>
        public static Created FromTemplate(string template, JObject inputs)
        {
            if (!TryTemplate(template, out var manifest, out var scene))
                throw new ArgumentException($"unknown_template: no template \"{template}\": use one of {string.Join(", ", Templates)}");
            if (inputs != null)
                foreach (var pair in inputs)
                {
                    JObject input = null;
                    foreach (JObject i in (JArray)manifest["inputs"]) if (i.Value<string>("name") == pair.Key) input = i;
                    if (input == null) throw new ArgumentException($"unknown_input: {template} has no input \"{pair.Key}\"");
                    if (pair.Value.Type != JTokenType.Integer && pair.Value.Type != JTokenType.Float)
                        throw new ArgumentException($"bad_value: {pair.Key} must be a number");
                    double v = (double)pair.Value, min = input.Value<double>("min"), max = input.Value<double>("max");
                    if (v < min || v > max) throw new ArgumentException($"bad_value: {pair.Key} {v} is outside {min} to {max}");
                    if (input.Value<string>("type") == "integer" && Math.Abs(v - Math.Round(v)) > 1e-9) throw new ArgumentException($"bad_value: {pair.Key} must be a whole number");
                    input["default"] = pair.Value.DeepClone();
                }

            if (NotebookStore.IsOpen)
            {
                manifest["kind"] = "project";
                manifest["links"]["project"] = NotebookStore.ProjectId;
                manifest["links"]["project_version"] = NotebookStore.CurrentVersion();
            }
            else
            {
                manifest["kind"] = "standalone";
                foreach (var link in ((JObject)manifest["links"]).Properties()) link.Value = JValue.CreateNull();
            }
            return Write(manifest, scene);
        }

        /// <summary>
        /// Saves an artifact Albie wrote itself (<c>artifact_create {manifest, scene}</c>). Nothing is written unless the
        /// validator passes and this host can build every part; otherwise <paramref name="errors"/> says what to fix, as
        /// data (C-12 section 9). A project or code artifact made while a notebook is open is linked to that project.
        /// </summary>
        public static Created FromScene(JObject manifest, JObject scene, out List<ArtifactError> errors)
        {
            errors = ArtifactValidator.Validate(manifest, scene);
            if (errors.Count > 0) return null;
            var missing = ArtifactNodes.Missing(scene);
            if (missing.Count > 0)
            {
                errors.Add(new ArtifactError("cant_build", "scene", "valid, but this version can't build: " + string.Join(", ", missing)));
                return null;
            }
            manifest = (JObject)manifest.DeepClone();
            scene = (JObject)scene.DeepClone();
            if (NotebookStore.IsOpen && manifest.Value<string>("kind") != "standalone")
            {
                if (!(manifest["links"] is JObject)) manifest["links"] = new JObject();
                manifest["links"]["project"] = NotebookStore.ProjectId;
                manifest["links"]["project_version"] = NotebookStore.CurrentVersion();
            }
            return Write(manifest, scene);
        }

        // Same artifact again: unchanged. Different: the next version. Links following a project don't count as different.
        static Created Write(JObject manifest, JObject scene)
        {
            string id = manifest.Value<string>("id");
            string folder = Folder(id);
            int version = 1;
            string change = "created";
            string oldManifestPath = Path.Combine(folder, "manifest.json"), oldScenePath = Path.Combine(folder, "scene.json");
            if (File.Exists(oldManifestPath))
            {
                try
                {
                    var oldManifest = (JObject)NotebookFiles.Parse(File.ReadAllText(oldManifestPath));
                    var oldScene = File.Exists(oldScenePath) ? NotebookFiles.Parse(File.ReadAllText(oldScenePath)) : null;
                    int oldVersion = oldManifest.Value<int?>("version") ?? 0;
                    if (JToken.DeepEquals(Comparable(oldManifest), Comparable(manifest)) && JToken.DeepEquals(oldScene, scene))
                    {
                        // Same artifact. If it now belongs to a project (or a newer version of it), the links follow.
                        if (!JToken.DeepEquals(oldManifest["links"], manifest["links"]) || (string)oldManifest["kind"] != (string)manifest["kind"])
                        {
                            manifest["version"] = oldVersion;
                            NotebookFiles.WriteAtomic(oldManifestPath, manifest.ToString(Formatting.Indented));
                        }
                        return new Created { Id = id, Version = oldVersion, Change = "unchanged" };
                    }
                    version = oldVersion + 1;
                    change = "updated";
                }
                catch (Exception e) when (e is JsonException || e is IOException || e is InvalidCastException)
                {
                    Debug.LogWarning($"[Artifacts] {oldManifestPath} didn't load ({e.Message}); writing a new version");
                }
            }
            manifest["version"] = version;
            NotebookFiles.WriteAtomic(Path.Combine(folder, "scene.json"), scene.ToString(Formatting.Indented));
            NotebookFiles.WriteAtomic(oldManifestPath, manifest.ToString(Formatting.Indented));
            if (Application.isPlaying)
                ForgeNet.Emit("artifact.updated", new JObject { ["project_id"] = manifest["links"]?["project"], ["artifact_id"] = id, ["version"] = version });
            return new Created { Id = id, Version = version, Change = change };
        }

        /// <summary>The artifacts on this device: id, title, version, kind.</summary>
        public static JArray List()
        {
            var list = new JArray();
            if (!Directory.Exists(Root)) return list;
            foreach (string folder in Directory.GetDirectories(Root))
            {
                string path = Path.Combine(folder, "manifest.json");
                if (!File.Exists(path)) continue;
                try
                {
                    var m = (JObject)NotebookFiles.Parse(File.ReadAllText(path));
                    list.Add(new JObject { ["id"] = m["id"], ["title"] = m["title"], ["version"] = m["version"], ["kind"] = m["kind"], ["template"] = m["template"] });
                }
                catch (Exception e) when (e is JsonException || e is IOException || e is InvalidCastException) { }
            }
            return list;
        }

        // Version, kind and links say where an artifact lives, not what it is: a new project version alone doesn't bump it.
        static JObject Comparable(JObject manifest)
        {
            var copy = (JObject)manifest.DeepClone();
            copy.Remove("version");
            copy.Remove("kind");
            copy.Remove("links");
            return copy;
        }
    }
}
