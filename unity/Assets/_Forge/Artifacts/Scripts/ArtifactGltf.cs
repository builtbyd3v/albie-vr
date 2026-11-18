using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The <c>gltf</c> node (C-12 3.2): a binary glTF from the artifact's own <c>assets/</c>, loaded with glTFast. The
    /// bytes are read and their sha256 checked before anything is built (<see cref="ArtifactAssets"/>), so a node only
    /// ever loads a file the manifest lists with a matching hash. The model appears once it's loaded.
    /// </summary>
    public sealed class ArtifactGltf : MonoBehaviour
    {
        public bool Loaded { get; private set; }
        public ArtifactExplorer Explorer { get; private set; }
        /// <summary>Why the model didn't load, or null.</summary>
        public string Problem { get; private set; }
        public Task<bool> Loading { get; private set; }

        GltfImport import;
        IReadOnlyDictionary<string, GameObject> sceneNodes;  // the artifact's nodes, for the explorer's label hiding
        bool fit;
        readonly CancellationTokenSource cancel = new CancellationTokenSource();

        public static GameObject Build(JObject n, ArtifactBuild build)
        {
            var go = new GameObject(n.Value<string>("id"));
            var model = go.AddComponent<ArtifactGltf>();
            model.sceneNodes = build.Nodes;
            string asset = n.Value<string>("asset");
            // A model brought in with model_import comes in its author's units and origin: it is fitted to the view.
            model.fit = ModelImport.Imported(build.Host != null ? build.Host.OpenManifest : null, asset) != null;
            if (asset != null && build.Assets.TryGetValue(asset, out var bytes)) model.Loading = model.Load(bytes, build.Host);
            else model.Problem = $"\"{asset}\" wasn't read";
            return go;
        }

        async Task<bool> Load(byte[] glb, ArtifactHost host)
        {
            var holder = new GameObject("model");
            holder.transform.SetParent(transform, false);
            try
            {
                // In play mode glTFast spreads the load over frames; its frame-budget agent can't exist outside play mode.
                import = new GltfImport(deferAgent: Application.isPlaying ? null : new UninterruptedDeferAgent());
                var settings = new ImportSettings { NodeNameMethod = NameImportMethod.Original, GenerateMipMaps = true };
                if (!await import.Load(glb, importSettings: settings, cancellationToken: cancel.Token))
                    return Fail("glTFast couldn't read it");
                if (this == null) return Fail("closed while loading");
                if (!await import.InstantiateMainSceneAsync(holder.transform, cancel.Token))
                    return Fail("glTFast couldn't build its scene");
            }
            catch (OperationCanceledException) { return Fail("closed while loading"); }
            catch (Exception e) { return Fail(e.Message); }
            if (this == null) return Fail("closed while loading");
            for (int i = 0; i < import.TextureCount; i++)
            {
                var texture = import.GetTexture(i);
                if (texture != null && Mathf.Max(texture.width, texture.height) > ArtifactSchema.MaxTextureSize)
                    Debug.LogWarning($"[Artifacts] {name}: a {texture.width}x{texture.height} texture is over {ArtifactSchema.MaxTextureSize} px");
            }
            if (fit) ModelImport.Fit(holder.transform);  // before the explorer notes where the model rests
            Explorer = holder.AddComponent<ArtifactExplorer>();
            Explorer.SceneNodes = sceneNodes;
            // What its parts are called when people talk (a template's parts.json); an imported model has none.
            var manifest = host != null ? host.OpenManifest : null;
            Explorer.Words = ArtifactExplorer.WordsFor(manifest?.Value<string>("template") ?? manifest?.Value<string>("id"));
            Explorer.Initialize();
            // What "this" is: the part pointed or looked at, for "show me this lobe".
            holder.AddComponent<ArtifactPartPointer>().Bind(Explorer);
            Loaded = true;
            if (host != null) host.ModelLoaded();
            return true;
        }

        bool Fail(string why)
        {
            Problem = why;
            Debug.LogWarning($"[Artifacts] {name}: {why}");
            return false;
        }

        void OnDestroy()
        {
            cancel.Cancel();
            import?.Dispose();
            import = null;
        }
    }

    /// <summary>Reads and checks the assets a scene's <c>gltf</c> nodes use (C-12 sections 2 and 9).</summary>
    public static class ArtifactAssets
    {
        /// <summary>
        /// Each asset a <c>gltf</c> node names, by path, or null with <paramref name="error"/>: <c>asset_missing</c>,
        /// <c>too_big</c> (bytes, triangles or texture size), <c>hash_mismatch</c>, or <c>bad_value</c> for a file that isn't a binary glTF.
        /// </summary>
        public static Dictionary<string, byte[]> Resolve(ArtifactPackage package, out ArtifactError error)
        {
            error = null;
            var read = new Dictionary<string, byte[]>();
            if (!(package.Scene["nodes"] is JArray nodes)) return read;
            var listed = package.Manifest["assets"] as JArray ?? new JArray();
            foreach (var node in nodes)
            {
                if (node.Value<string>("kind") != "gltf") continue;
                string path = node.Value<string>("asset");
                if (path == null || read.ContainsKey(path)) continue;
                int index = -1;
                for (int i = 0; i < listed.Count; i++)
                    if (listed[i].Value<string>("path") == path) index = i;
                if (index < 0)
                {
                    error = new ArtifactError("asset_not_listed", "assets", $"\"{path}\" isn't in the manifest");
                    return null;
                }
                string at = $"assets[{index}]";
                var bytes = package.ReadAsset(path, out string why);
                if (bytes == null)
                {
                    error = new ArtifactError("asset_missing", at, why);
                    return null;
                }
                if (bytes.Length > ArtifactSchema.MaxAssetBytes)
                {
                    error = new ArtifactError("too_big", at, $"{path} is {bytes.Length / (1024 * 1024f):0.0} MB, over the limit of {ArtifactSchema.MaxAssetBytes / (1024 * 1024)} MB");
                    return null;
                }
                if (!string.Equals(Sha256(bytes), listed[index].Value<string>("sha256"), StringComparison.OrdinalIgnoreCase))
                {
                    error = new ArtifactError("hash_mismatch", at, "sha256 doesn't match");
                    return null;
                }
                if (bytes.Length < 12 || bytes[0] != 'g' || bytes[1] != 'l' || bytes[2] != 'T' || bytes[3] != 'F')
                {
                    error = new ArtifactError("bad_value", at, $"{path} isn't a binary glTF (.glb)");
                    return null;
                }
                // Quest limits, from the file's own header before anything is built: triangles, texture size, compression.
                string heavy = ModelImportGlb.TooHeavy(bytes);
                if (heavy != null)
                {
                    error = new ArtifactError("too_big", at, $"{path} is too heavy for the headset: {heavy}", "pick a lighter model");
                    return null;
                }
                read[path] = bytes;
            }
            return read;
        }

        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }
    }
}
