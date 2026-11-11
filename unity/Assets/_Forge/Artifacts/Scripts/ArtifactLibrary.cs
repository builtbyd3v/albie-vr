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
            // The folder is the id, so a template's id would overwrite the copy made from that template.
            if (manifest?["id"]?.Type == JTokenType.String && Array.IndexOf(Templates, (string)manifest["id"]) >= 0)
                errors.Add(new ArtifactError("bad_value", "id", $"\"{manifest["id"]}\" is a template's id",
                    $"use a new id, like \"my_{manifest["id"]}\"; to change a template's numbers, call artifact_create with template and inputs", Templates));
            if (errors.Count > 0) return null;
            var missing = ArtifactNodes.Missing(scene);
            if (missing.Count > 0)
            {
                errors.Add(new ArtifactError("cant_build", "scene", "valid, but this version can't build: " + string.Join(", ", missing)));
                return null;
            }
            errors.AddRange(DryOpen(manifest, scene));
            if (errors.Count > 0) return null;
            manifest = (JObject)manifest.DeepClone();
            scene = (JObject)scene.DeepClone();
            manifest["scene"] = "scene.json";  // the only file Write saves, so the manifest has to name it
            if (NotebookStore.IsOpen && manifest.Value<string>("kind") != "standalone")
            {
                if (!(manifest["links"] is JObject)) manifest["links"] = new JObject();
                manifest["links"]["project"] = NotebookStore.ProjectId;
                manifest["links"]["project_version"] = NotebookStore.CurrentVersion();
            }
            return Write(manifest, scene);
        }

        /// <summary>
        /// What opening it would refuse, found before it's saved (nothing broken is ever written): every data table must
        /// load, every gltf asset must be bundled and match its hash, every table array must name real columns, and the
        /// scene must stay within <see cref="ArtifactSchema.MaxSceneCopies"/> copies. Where a room course is placed is
        /// left to the open, because it depends on the room. Runs only on a valid scene that this host can build.
        /// </summary>
        public static List<ArtifactError> DryOpen(JObject manifest, JObject scene)
        {
            var errors = new List<ArtifactError>();
            var package = ArtifactPackage.InMemory(manifest.Value<string>("id"), manifest, scene);
            var tables = ArtifactData.Resolve(package, out string dataProblem);
            if (tables == null)
                errors.Add(new ArtifactError("no_data", "data", dataProblem,
                    "use a source that artifact_list names under data_sources; a written artifact can't bring its own data file"));
            if (ArtifactAssets.Resolve(package, out var assetError) == null)
            {
                if (assetError.hint == null && (assetError.code == "asset_missing" || assetError.code == "asset_not_listed"))
                    assetError.hint = "only models bundled on this headset load, and a written artifact can't bring a .glb: build the shape from primitives";
                errors.Add(assetError);
            }
            if (!(scene["nodes"] is JArray nodes)) return errors;

            int copies = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!(nodes[i] is JObject node) || node.Value<string>("kind") != "array") continue;
                if (node["source"] is JObject table)
                {
                    if (tables == null) continue;  // already refused above
                    string problem = ArtifactArray.Problem(node, tables);
                    if (problem != null) errors.Add(new ArtifactError("unknown_ref", $"nodes[{i}].source", problem, null, Names(tables.Keys)));
                    else copies += Math.Min(tables[table.Value<string>("table")].Rows.Count, ArtifactSchema.MaxArrayCopies);
                }
                else copies += SlotCopies(manifest, scene, node.Value<string>("source"));
            }
            if (copies > ArtifactSchema.MaxSceneCopies)
                errors.Add(new ArtifactError("too_big", "nodes", $"its arrays make up to {copies} copies, over the limit of {ArtifactSchema.MaxSceneCopies} in a scene",
                    "use fewer rows, a lower max on the input that sets the count, or fewer arrays"));
            return errors;
        }

        static string[] Names(IEnumerable<string> keys) => new List<string>(keys).ToArray();

        // The most poses a generator slot can hold: one for start and finish; for a list, the count the generator is asked
        // for, at the top of its input's range when a slider sets it (the slider can go there without a new layout check).
        static int SlotCopies(JObject manifest, JObject scene, string source)
        {
            int dot = source?.IndexOf('.') ?? -1;
            if (dot < 0) return 0;
            if (ArtifactSchema.SingletonSlots.Contains(source.Substring(dot + 1))) return 1;
            string id = source.Substring(0, dot);
            if (!(scene["generators"] is JArray generators)) return 0;
            foreach (var g in generators)
            {
                if (g.Value<string>("id") != id) continue;
                string type = g.Value<string>("type");
                string key = type == "room_hoops" ? "hoops" : "gates";
                int fallback = type == "room_hoops" ? RoomHoops.Settings.Default.Hoops : FloorSlalom.Settings.Default.Gates;
                var count = g["params"]?[key];
                if (count == null) return fallback;
                if (count.Type == JTokenType.Integer || count.Type == JTokenType.Float) return (int)Math.Round((double)count);
                string name = count.Type == JTokenType.String ? ((string)count).TrimStart('$') : null;
                if (manifest["inputs"] is JArray inputs)
                    foreach (var input in inputs)
                        if (input.Value<string>("name") == name)
                            return (int)Math.Round(input.Value<double?>("max") ?? input.Value<double?>("default") ?? fallback);
                return fallback;
            }
            return 0;
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
