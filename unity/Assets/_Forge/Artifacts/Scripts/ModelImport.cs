using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// A downloaded open-licensed model as an artifact: the file is the artifact's own device copy
    /// (<c>assets/model.glb</c>), listed in the manifest with its sha256, and the asset entry also says who made it,
    /// under what licence and where it came from (<c>name, author, source, source_url, license_url</c> beside the
    /// contract's <c>path, sha256, license</c>). The panel shows that credit (<see cref="Credits"/>): CC BY needs it.
    /// Where models come from and what the server refuses: hub/models/service.py.
    /// </summary>
    public static class ModelImport
    {
        public const string AssetPath = "assets/model.glb";
        /// <summary>An imported model is scaled so the sphere round it has this radius (scene metres), inside the view's 0.3.</summary>
        public const float FitRadius = 0.26f, ViewRadius = 0.3f;

        /// <summary>The licences a model may come under, as SPDX ids. The server filters too; this is checked again here.</summary>
        public static readonly HashSet<string> Licences = new HashSet<string> { "CC0-1.0", "CC-BY-4.0", "CC-BY-SA-4.0" };

        static readonly string[] CreditKeys = { "license", "name", "author", "source", "source_url", "license_url" };

        /// <summary>What the server says about a fetched model (GET /models/fetch).</summary>
        public sealed class Meta
        {
            public string id, name, author, licence, licence_url, source, source_url, sha256, file;
            public long size_bytes;
        }

        /// <summary>The artifact id an imported model is saved under: the same model always lands in the same folder.</summary>
        public static string ArtifactId(Meta meta)
        {
            string slug = ArtifactValidator.Slug(meta.name) ?? "";
            if (slug.Length > 32) slug = slug.Substring(0, 32).TrimEnd('_', '-');
            int colon = meta.id.IndexOf(':');
            string uid = colon >= 0 ? meta.id.Substring(colon + 1) : meta.id;
            return "model_" + (slug.Length > 0 ? slug + "_" : "") + (uid.Length > 6 ? uid.Substring(0, 6) : uid);
        }

        /// <summary>
        /// Checks the bytes against what the server said and against this headset's limits, then saves them as an
        /// artifact. Returns null with <paramref name="problem"/> ("code: message") when anything is off; nothing is
        /// left on the device then.
        /// </summary>
        public static ArtifactLibrary.Created Save(Meta meta, byte[] glb, out string problem)
        {
            problem = Refusal(meta, glb);
            if (problem != null) return null;
            var manifest = new JObject
            {
                ["id"] = ArtifactId(meta),
                ["title"] = meta.name,
                ["kind"] = "standalone",
                ["category"] = "viz",
                ["assets"] = new JArray(new JObject
                {
                    ["path"] = AssetPath, ["sha256"] = ArtifactAssets.Sha256(glb), ["license"] = meta.licence,
                    ["name"] = meta.name, ["author"] = meta.author, ["source"] = meta.source,
                    ["source_url"] = meta.source_url, ["license_url"] = meta.licence_url,
                }),
            };
            var scene = new JObject
            {
                ["space"] = "volume",
                ["view"] = new JObject { ["radius"] = ViewRadius },
                ["nodes"] = new JArray(
                    // Turned to face the person, as the bundled models are: a glTF's front is +z, and they look along +z.
                    new JObject { ["id"] = "model", ["kind"] = "gltf", ["asset"] = AssetPath, ["rot"] = new JArray(0, 180, 0) },
                    new JObject { ["id"] = "about", ["kind"] = "panel", ["title"] = meta.name, ["widgets"] = new JArray() }),
            };
            ArtifactTools.Fill(ref manifest, scene);
            var made = ArtifactLibrary.FromModel(manifest, scene, AssetPath, glb, out var errors);
            if (made == null) problem = errors.Count > 0 ? errors[0].ToString() : "cant_save: the model couldn't be saved";
            return made;
        }

        /// <summary>Why these bytes can't be this model, as "code: message", or null.</summary>
        public static string Refusal(Meta meta, byte[] glb)
        {
            if (meta == null || string.IsNullOrEmpty(meta.id) || string.IsNullOrEmpty(meta.name))
                return "bad_value: the server's answer has no model in it";
            if (string.IsNullOrEmpty(meta.licence) || !Licences.Contains(meta.licence))
                return $"licence_refused: its licence ({meta.licence ?? "none given"}) isn't CC0, CC BY or CC BY-SA";
            if (string.IsNullOrEmpty(meta.author) || string.IsNullOrEmpty(meta.source_url))
                return "licence_refused: there's nobody to credit for it";
            if (glb == null || glb.Length == 0) return "asset_missing: no file came back";
            if (glb.Length > ArtifactSchema.MaxAssetBytes)
                return $"too_big: it is {glb.Length / (1024 * 1024f):0.0} MB, over the limit of {ArtifactSchema.MaxAssetBytes / (1024 * 1024)} MB";
            if (!string.Equals(ArtifactAssets.Sha256(glb), meta.sha256, StringComparison.OrdinalIgnoreCase))
                return "hash_mismatch: the file that arrived isn't the one the server checked";
            var stats = ModelImportGlb.Inspect(glb, out string unreadable);
            if (stats == null) return "bad_value: " + unreadable;
            if (stats.Triangles == 0) return "bad_value: it has nothing to show";
            // A Sketchfab export says inside the file what that copy was published under: it has to be an open licence too.
            string embedded = stats.License != null ? stats.License.Split(' ')[0] : null;
            if (!string.IsNullOrEmpty(embedded) && !Licences.Contains(embedded))
                return $"licence_refused: this copy was published as {embedded}";
            string heavy = ModelImportGlb.TooHeavy(stats);
            return heavy != null ? "too_big: " + heavy : null;
        }

        /// <summary>The manifest's entry for <paramref name="path"/> if that asset was imported (it says where from), else null.</summary>
        public static JObject Imported(JObject manifest, string path)
        {
            if (!(manifest?["assets"] is JArray assets)) return null;
            foreach (var asset in assets)
                if (asset is JObject entry && entry.Value<string>("path") == path && Has(entry, "source") && Has(entry, "author"))
                    return entry;
            return null;
        }

        static bool Has(JObject o, string key) => o[key] != null && o[key].Type == JTokenType.String && ((string)o[key]).Length > 0;

        /// <summary>
        /// The credit lines for every imported model in a manifest, one per model, or null when there are none:
        /// <c>"Jet engine" by Ada · CC-BY-4.0 · sketchfab.com/3d-models/...</c>
        /// </summary>
        public static string Credits(JObject manifest)
        {
            if (!(manifest?["assets"] is JArray assets)) return null;
            StringBuilder lines = null;
            foreach (var asset in assets)
            {
                if (!(asset is JObject entry) || !Has(entry, "source") || !Has(entry, "author")) continue;
                if (lines == null) lines = new StringBuilder();
                else lines.Append('\n');
                lines.Append(Credit(entry));
            }
            return lines?.ToString();
        }

        public static string Credit(JObject entry)
        {
            string name = entry.Value<string>("name"), url = entry.Value<string>("source_url") ?? "";
            if (url.StartsWith("https://", StringComparison.Ordinal)) url = url.Substring(8);
            string line = (string.IsNullOrEmpty(name) ? "Model" : "\"" + name + "\"") + " by " + entry.Value<string>("author")
                          + " · " + entry.Value<string>("license");
            return url.Length > 0 ? line + "\n" + url : line;
        }

        /// <summary>
        /// A rewritten artifact keeps the credit of a model it still uses: for each asset with the same path and hash as
        /// in <paramref name="saved"/> (the manifest on the device), the saved licence and credit fields replace whatever
        /// <paramref name="manifest"/> says. The agent can't drop or change an attribution.
        /// </summary>
        public static void KeepCredits(JObject manifest, JObject saved)
        {
            if (!(manifest?["assets"] is JArray assets) || !(saved?["assets"] is JArray before)) return;
            foreach (var asset in assets)
            {
                if (!(asset is JObject entry)) continue;
                foreach (var old in before)
                {
                    if (!(old is JObject was) || !Has(was, "source") || was.Value<string>("path") != entry.Value<string>("path") ||
                        !string.Equals(was.Value<string>("sha256"), entry.Value<string>("sha256"), StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string key in CreditKeys)
                        if (was[key] != null) entry[key] = was[key].DeepClone();
                }
            }
        }

        /// <summary>
        /// Scales and moves a loaded model (the holder glTFast built it under) so it sits centred on its node and inside
        /// a sphere of <see cref="FitRadius"/>, whatever units and origin its author used. False if there was nothing to measure.
        /// </summary>
        public static bool Fit(Transform holder)
        {
            var renderers = holder.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            var toHolder = holder.worldToLocalMatrix;
            foreach (var renderer in renderers)
            {
                // A mesh's own box is exact however the node is turned; anything else (skinned) gives its world box.
                Bounds box;
                Matrix4x4 matrix;
                if (renderer is MeshRenderer && renderer.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
                {
                    box = filter.sharedMesh.bounds;
                    matrix = toHolder * renderer.transform.localToWorldMatrix;
                }
                else
                {
                    box = renderer.bounds;
                    matrix = toHolder;
                }
                for (int i = 0; i < 8; i++)
                {
                    var e = box.extents;
                    var corner = matrix.MultiplyPoint3x4(box.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                    if (float.IsNaN(corner.x) || float.IsNaN(corner.y) || float.IsNaN(corner.z) ||
                        float.IsInfinity(corner.x) || float.IsInfinity(corner.y) || float.IsInfinity(corner.z)) continue;
                    if (!any) { min = max = corner; any = true; }
                    else { min = Vector3.Min(min, corner); max = Vector3.Max(max, corner); }
                }
            }
            float radius = (max - min).magnitude * 0.5f;
            if (!any || radius < 1e-7f) return false;
            float scale = FitRadius / radius;
            holder.localScale = Vector3.one * scale;
            holder.localPosition = -((min + max) * 0.5f) * scale;
            return true;
        }
    }
}
