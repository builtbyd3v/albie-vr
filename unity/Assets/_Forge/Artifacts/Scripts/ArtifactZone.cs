using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// A <c>zone</c> node (C-12 3.2): a box that a run tests the bound Thing's position against each FixedUpdate, with
    /// no trigger colliders (section 5). When visible it shows as an outline on the floor, so a course reads from above.
    /// </summary>
    public sealed class ArtifactZone : MonoBehaviour
    {
        public Vector3 Size;
        LineRenderer outline;

        /// <summary>Whether a world point is inside the box. No allocations: safe in FixedUpdate.</summary>
        public bool Contains(Vector3 world)
        {
            var local = transform.InverseTransformPoint(world);
            var half = Size * 0.5f;
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
        }

        public void SetColor(Color color)
        {
            if (outline != null) outline.startColor = outline.endColor = color;
        }

        public static GameObject Build(JObject n, ArtifactBuild build)
        {
            var go = new GameObject(n.Value<string>("id"));
            var zone = go.AddComponent<ArtifactZone>();
            var line = go.AddComponent<LineRenderer>();
            zone.outline = line;
            line.useWorldSpace = false;
            line.loop = true;
            line.positionCount = 4;
            line.sharedMaterial = ArtifactLook.Trail();
            line.numCornerVertices = 2;
            line.alignment = LineAlignment.View;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.widthMultiplier = 0.015f;
            zone.SetColor(Tint(n.Value<string>("id")));
            var size = n["size"];
            build.Apply(() =>
            {
                zone.Size = build.Inputs.Vector(size, new Vector3(0.5f, 0.5f, 0.5f));
                // Flat on the bottom face, a few millimetres up so it doesn't fight the floor.
                float y = -zone.Size.y * 0.5f + 0.004f, x = zone.Size.x * 0.5f, z = zone.Size.z * 0.5f;
                line.SetPosition(0, new Vector3(-x, y, -z));
                line.SetPosition(1, new Vector3(-x, y, z));
                line.SetPosition(2, new Vector3(x, y, z));
                line.SetPosition(3, new Vector3(x, y, -z));
            }, ArtifactInputs.IsBound(size));
            return go;
        }

        // The start reads as "go", the finish as the line to cross, everything else in the tool colour.
        static Color Tint(string id)
        {
            if (id == "start") return new Color(0.49f, 1f, 0.70f, 0.9f);
            if (id == "finish") return new Color(1f, 1f, 1f, 0.9f);
            var c = ForgeTheme.Tool;
            return new Color(c.r, c.g, c.b, 0.75f);
        }
    }

    /// <summary>
    /// An <c>array</c> node (C-12 3.2). Over a generator slot: one copy of the template on each pose, with the template's
    /// own <c>pos</c> and <c>rot</c> as offsets from the slot (a cone template lifts itself by half its height).
    /// Over a table (<c>source: {table, grid}</c>): one copy per row, on a grid centred on the node and standing on it.
    /// <c>map.height</c> and <c>map.width</c> scale a number column linearly into <c>range</c>; <c>map.color</c> gives each
    /// distinct value of a column its own colour. A null cell is grey at the bottom of the range, never zero (9.8).
    /// </summary>
    public static class ArtifactArray
    {
        public const string NoDataColor = "#5A5F66";
        static readonly string[] Palette =
            { "#4C8DFF", "#FF7A1A", "#7CFFB2", "#FFD24C", "#C77DFF", "#FF5C8A", "#4CE0E0", "#B8E04C" };

        /// <summary>Why a table array can't be built from these tables (a missing table or column), or null.</summary>
        public static string Problem(JObject n, Dictionary<string, ArtifactTable> tables)
        {
            if (!(n["source"] is JObject source)) return null;
            string id = n.Value<string>("id"), name = source.Value<string>("table");
            if (name == null || !tables.TryGetValue(name, out var table)) return $"\"{id}\" reads the table \"{name}\", which didn't load.";
            if (n["map"] is JObject map)
                foreach (var pair in map)
                {
                    string column = pair.Value?.Value<string>("column");
                    int c = table.Column(column);
                    if (c < 0) return $"\"{id}\" maps {pair.Key} to the column \"{column}\", and \"{name}\" has no such column (it has {string.Join(", ", table.Columns)}).";
                    if ((pair.Key == "height" || pair.Key == "width") && table.Types[c] != "number") return $"\"{id}\" maps {pair.Key} to \"{column}\", which isn't a number column.";
                }
            return null;
        }

        public static GameObject Build(JObject n, ArtifactBuild build)
        {
            string id = n.Value<string>("id");
            var go = new GameObject(id);
            if (n["source"] is JObject) return FromTable(n, build, go);
            string source = n.Value<string>("source");
            var template = (JObject)n["template"];
            if (source == null || !build.Slots.TryGetValue(source, out var poses)) return go;
            var copies = new List<GameObject>(poses.Count);
            for (int i = 0; i < poses.Count; i++)
            {
                var copy = (JObject)template.DeepClone();
                copy["id"] = $"{id}_{i}";
                var child = ArtifactNodes.Build(copy, build);
                child.transform.SetParent(go.transform, false);
                var pose = poses[i];
                var pos = template["pos"];
                var rot = template["rot"];
                build.Apply(() =>
                {
                    child.transform.localPosition = pose.position + pose.rotation * build.Inputs.Vector(pos, Vector3.zero);
                    child.transform.localRotation = pose.rotation * Quaternion.Euler(build.Inputs.Vector(rot, Vector3.zero));
                }, ArtifactInputs.IsBound(pos) || ArtifactInputs.IsBound(rot));
                copies.Add(child);
            }
            build.Arrays[id] = copies;
            return go;
        }

        static GameObject FromTable(JObject n, ArtifactBuild build, GameObject go)
        {
            string id = n.Value<string>("id");
            var source = (JObject)n["source"];
            var template = (JObject)n["template"];
            var copies = new List<GameObject>();
            build.Arrays[id] = copies;
            if (!build.Tables.TryGetValue(source.Value<string>("table") ?? "", out var table)) return go;

            int count = Mathf.Min(table.Rows.Count, ArtifactSchema.MaxArrayCopies);
            if (table.Rows.Count > count) build.Notes.Add($"There are {table.Rows.Count} rows, so I'm showing the first {count}.");
            var grid = source["grid"] as JObject;
            int columns = Mathf.Max(1, grid?.Value<int?>("columns") ?? Mathf.CeilToInt(Mathf.Sqrt(count)));
            float gap = grid?.Value<float?>("gap_m") ?? 0.02f;
            var size = build.Inputs.Vector(template["size"], Vector3.one * 0.1f);
            var offset = build.Inputs.Vector(template["pos"], Vector3.zero);
            int rows = Mathf.CeilToInt(count / (float)columns);

            var map = n["map"] as JObject;
            var height = Scale(map?["height"], table);
            var width = Scale(map?["width"], table);
            int colorColumn = table.Column(map?["color"]?.Value<string>("column"));
            var colors = new Dictionary<string, string>();

            for (int i = 0; i < count; i++)
            {
                var copy = (JObject)template.DeepClone();
                copy["id"] = $"{id}_{i}";
                bool noData = false;
                float h = size.y, w = size.x;
                if (height.Column >= 0) h = height.At(table, i, ref noData);
                if (width.Column >= 0) w = width.At(table, i, ref noData);
                copy["size"] = new JArray(w, h, size.z);
                if (colorColumn >= 0)
                {
                    string key = table.Text(i, colorColumn);
                    if (key == null) noData = true;
                    else if (!colors.TryGetValue(key, out _)) colors[key] = Palette[colors.Count % Palette.Length];
                    if (key != null) copy["color"] = colors[key];
                }
                if (noData) copy["color"] = NoDataColor;
                var child = ArtifactNodes.Build(copy, build);
                child.name = copy.Value<string>("id");
                child.transform.SetParent(go.transform, false);
                int col = i % columns, row = i / columns;
                child.transform.localPosition = offset + new Vector3(
                    (col - (columns - 1) * 0.5f) * (size.x + gap), h * 0.5f, (row - (rows - 1) * 0.5f) * (size.z + gap));
                copies.Add(child);
            }
            return go;
        }

        struct Linear
        {
            public int Column;
            public float From, To;
            public double Min, Max;

            /// <summary>The cell scaled into range; a null is the bottom of the range and marks the copy as no data.</summary>
            public float At(ArtifactTable table, int row, ref bool noData)
            {
                double v = table.Number(row, Column);
                if (double.IsNaN(v)) { noData = true; return From; }
                if (Max - Min < 1e-9) return To;
                return From + (float)((v - Min) / (Max - Min)) * (To - From);
            }
        }

        static Linear Scale(JToken spec, ArtifactTable table)
        {
            var s = new Linear { Column = table.Column(spec?.Value<string>("column")), Min = double.PositiveInfinity, Max = double.NegativeInfinity };
            if (s.Column < 0) return s;
            var range = spec["range"] as JArray;
            s.From = range != null && range.Count == 2 ? (float)range[0] : 0.02f;
            s.To = range != null && range.Count == 2 ? (float)range[1] : 0.4f;
            for (int r = 0; r < table.Rows.Count; r++)
            {
                double v = table.Number(r, s.Column);
                if (double.IsNaN(v)) continue;
                if (v < s.Min) s.Min = v;
                if (v > s.Max) s.Max = v;
            }
            return s;
        }
    }
}
