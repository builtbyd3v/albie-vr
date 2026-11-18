using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Forge.Artifacts
{
    /// <summary>
    /// What a binary glTF holds, read from its header, its JSON chunk and the first bytes of each embedded image.
    /// Nothing is decoded and nothing is built, so a model that is too heavy for a Quest is refused before glTFast
    /// touches it. The server runs the same checks (hub/models/glb.py); the headset doesn't take its word for it.
    /// </summary>
    public static class ModelImportGlb
    {
        /// <summary>The most triangles one model may draw. The bundled anatomy models are up to about 105,000.</summary>
        public const int MaxTriangles = 200000;

        const uint JsonChunk = 0x4E4F534A, BinChunk = 0x004E4942;
        // glTFast has no decoder package for these in this project (Packages/manifest.json), so such a file can't load.
        static readonly HashSet<string> Undecodable = new HashSet<string>
            { "KHR_draco_mesh_compression", "EXT_meshopt_compression", "KHR_texture_basisu", "EXT_texture_webp" };

        public sealed class Stats
        {
            public int Triangles, Vertices, Meshes;
            /// <summary>The longest side of any embedded PNG or JPEG, in pixels. 0 when there are none.</summary>
            public int MaxTexture;
            /// <summary>Required extensions this headset can't decode.</summary>
            public readonly List<string> Unsupported = new List<string>();
            /// <summary>What the file says about itself (<c>asset.extras</c>, as Sketchfab's exports write it), or null.</summary>
            public string License, Author, Source, Title;
        }

        /// <summary>The model's numbers, or null with <paramref name="problem"/> when it isn't a well-formed glTF 2 binary.</summary>
        public static Stats Inspect(byte[] data, out string problem)
        {
            problem = null;
            if (data == null || data.Length < 20 || data[0] != 'g' || data[1] != 'l' || data[2] != 'T' || data[3] != 'F')
            {
                problem = "it isn't a binary glTF (.glb)";
                return null;
            }
            uint version = BitConverter.ToUInt32(data, 4), length = BitConverter.ToUInt32(data, 8);
            if (version != 2) { problem = $"it's glTF version {version}, and only version 2 loads"; return null; }
            if (length != (uint)data.Length) { problem = "the file is cut short"; return null; }
            uint jsonLength = BitConverter.ToUInt32(data, 12);
            if (BitConverter.ToUInt32(data, 16) != JsonChunk || 20L + jsonLength > data.Length)
            {
                problem = "its first chunk isn't JSON";
                return null;
            }
            try
            {
                var doc = JObject.Parse(Encoding.UTF8.GetString(data, 20, (int)jsonLength));
                long binStart = -1, binLength = 0;
                long at = 20L + jsonLength + ((4 - jsonLength % 4) % 4);
                if (at + 8 <= data.Length && BitConverter.ToUInt32(data, (int)at + 4) == BinChunk)
                {
                    binLength = BitConverter.ToUInt32(data, (int)at);
                    binStart = at + 8;
                    if (binStart + binLength > data.Length) binStart = -1;
                }
                return Read(doc, data, binStart, binLength);
            }
            catch (Exception e) when (e is JsonException || e is InvalidCastException || e is ArgumentException ||
                                      e is NullReferenceException || e is FormatException || e is OverflowException ||
                                      e is IndexOutOfRangeException)
            {
                problem = "its JSON doesn't follow glTF 2";
                return null;
            }
        }

        /// <summary>
        /// Why this model is too heavy to show, or null. A file this can't read is left to the loader, so nothing that
        /// loaded before is refused here.
        /// </summary>
        public static string TooHeavy(byte[] data)
        {
            var stats = Inspect(data, out _);
            return stats == null ? null : TooHeavy(stats);
        }

        public static string TooHeavy(Stats stats)
        {
            if (stats.Unsupported.Count > 0)
                return "it's compressed in a way this headset can't decode (" + string.Join(", ", stats.Unsupported) + ")";
            if (stats.Triangles > MaxTriangles)
                return $"it has {stats.Triangles:N0} triangles, over the limit of {MaxTriangles:N0}";
            if (stats.MaxTexture > ArtifactSchema.MaxTextureSize)
                return $"it has a {stats.MaxTexture} px texture, over the limit of {ArtifactSchema.MaxTextureSize} px";
            return null;
        }

        static Stats Read(JObject doc, byte[] data, long binStart, long binLength)
        {
            var stats = new Stats();
            var accessors = doc["accessors"] as JArray ?? new JArray();
            var meshes = doc["meshes"] as JArray ?? new JArray();
            int[] triangles = new int[meshes.Count], vertices = new int[meshes.Count];
            for (int m = 0; m < meshes.Count; m++)
            {
                if (!(meshes[m]["primitives"] is JArray primitives)) continue;
                foreach (var primitive in primitives)
                {
                    var position = primitive["attributes"]?["POSITION"];
                    int count = position != null ? (int)accessors[(int)position]["count"] : 0;
                    vertices[m] += count;
                    var indices = primitive["indices"];
                    int n = indices != null ? (int)accessors[(int)indices]["count"] : count;
                    int mode = primitive.Value<int?>("mode") ?? 4;
                    if (mode == 4) triangles[m] += n / 3;
                    else if (mode == 5 || mode == 6) triangles[m] += Math.Max(n - 2, 0);
                }
            }
            // A mesh drawn by several nodes costs that many times over.
            bool used = false;
            if (doc["nodes"] is JArray nodes)
                foreach (var node in nodes)
                {
                    int? mesh = (node as JObject)?.Value<int?>("mesh");
                    if (mesh == null) continue;
                    used = true;
                    stats.Triangles += triangles[mesh.Value];
                    stats.Vertices += vertices[mesh.Value];
                }
            if (!used)
                for (int m = 0; m < meshes.Count; m++)
                {
                    stats.Triangles += triangles[m];
                    stats.Vertices += vertices[m];
                }
            stats.Meshes = meshes.Count;

            if (doc["images"] is JArray images && doc["bufferViews"] is JArray views && binStart >= 0)
                foreach (var image in images)
                {
                    int? view = (image as JObject)?.Value<int?>("bufferView");
                    if (view == null) continue;
                    long start = binStart + (views[view.Value].Value<long?>("byteOffset") ?? 0);
                    long size = views[view.Value].Value<long>("byteLength");
                    if (start < binStart || size < 0 || start + size > binStart + binLength) continue;
                    stats.MaxTexture = Math.Max(stats.MaxTexture, ImageSide(data, (int)start, (int)size));
                }

            if (doc["extensionsRequired"] is JArray required)
                foreach (var extension in required)
                    if (extension.Type == JTokenType.String && Undecodable.Contains((string)extension)) stats.Unsupported.Add((string)extension);

            if (doc["asset"]?["extras"] is JObject extras)
            {
                stats.License = Text(extras, "license");
                stats.Author = Text(extras, "author");
                stats.Source = Text(extras, "source");
                stats.Title = Text(extras, "title");
            }
            return stats;
        }

        static string Text(JObject o, string key) => o[key] != null && o[key].Type == JTokenType.String ? (string)o[key] : null;

        /// <summary>The longer side of a PNG or JPEG at <paramref name="start"/>, from its header; 0 for anything else.</summary>
        public static int ImageSide(byte[] d, int start, int size)
        {
            int end = start + size;
            if (size >= 24 && d[start] == 0x89 && d[start + 1] == 'P' && d[start + 2] == 'N' && d[start + 3] == 'G')
                return Math.Max(Big32(d, start + 16), Big32(d, start + 20));
            if (size < 4 || d[start] != 0xFF || d[start + 1] != 0xD8) return 0;
            int at = start + 2;
            while (at + 9 <= end)
            {
                if (d[at] != 0xFF) { at++; continue; }
                int marker = d[at + 1];
                if (marker == 0xFF) { at++; continue; }
                if ((marker >= 0xD0 && marker <= 0xD9) || marker == 0x01) { at += 2; continue; }
                int length = (d[at + 2] << 8) | d[at + 3];
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    return Math.Max((d[at + 5] << 8) | d[at + 6], (d[at + 7] << 8) | d[at + 8]);
                at += 2 + length;
            }
            return 0;
        }

        static int Big32(byte[] d, int at)
        {
            long value = ((long)d[at] << 24) | ((long)d[at + 1] << 16) | ((long)d[at + 2] << 8) | d[at + 3];
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }
    }
}
