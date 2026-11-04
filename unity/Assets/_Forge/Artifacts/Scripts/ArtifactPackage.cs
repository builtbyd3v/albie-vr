using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// An artifact's manifest and scene, read from the device (persistentDataPath/artifacts/&lt;id&gt;) or, failing that, from
    /// the bundled fixtures (Resources/Mocks/artifacts/&lt;id&gt;). Both work with no hub and no network, so it doesn't go through
    /// ForgeNet. Reading isn't trusting: run <see cref="ArtifactValidator"/> before showing anything.
    /// </summary>
    public sealed class ArtifactPackage
    {
        static readonly Regex SafeId = new Regex("^[a-z0-9_-]+$");
        static readonly Regex SafeFile = new Regex(@"^[a-z0-9_-]+\.json$");

        public string Id { get; private set; }
        public JObject Manifest { get; private set; }
        public JObject Scene { get; private set; }

        public string Title => Manifest?.Value<string>("title") ?? Id;

        /// <summary>The artifact, or null with <paramref name="error"/> saying why.</summary>
        public static ArtifactPackage Load(string id, out string error)
        {
            error = null;
            // Ids can come from the agent, and they become part of a path.
            if (id == null || !SafeId.IsMatch(id))
            {
                error = $"\"{id}\" isn't a valid artifact id";
                return null;
            }
            string manifestText = Read(id, "manifest.json");
            if (manifestText == null)
            {
                error = $"There's no artifact called \"{id}\"";
                return null;
            }
            var package = new ArtifactPackage { Id = id };
            try
            {
                package.Manifest = Parse(manifestText);
                string sceneFile = package.Manifest.Value<string>("scene");
                if (sceneFile == null || !SafeFile.IsMatch(sceneFile))
                {
                    error = "The manifest's scene has to be a plain file name, like scene.json";
                    return null;
                }
                string sceneText = Read(id, sceneFile);
                if (sceneText == null)
                {
                    error = $"{sceneFile} is missing";
                    return null;
                }
                package.Scene = Parse(sceneText);
            }
            catch (JsonException e)
            {
                error = "It isn't valid JSON: " + e.Message;
                return null;
            }
            return package;
        }

        // Dates stay strings: this data is passed along, not interpreted.
        static JObject Parse(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        static string Read(string id, string file)
        {
            string path = Path.Combine(Application.persistentDataPath, "artifacts", id, file);
            if (File.Exists(path)) return File.ReadAllText(path);
            var asset = Resources.Load<TextAsset>($"Mocks/artifacts/{id}/{Path.GetFileNameWithoutExtension(file)}");
            return asset != null ? asset.text : null;
        }
    }
}
