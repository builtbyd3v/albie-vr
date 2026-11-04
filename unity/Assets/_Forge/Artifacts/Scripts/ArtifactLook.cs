using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// How generated scenes look, kept in one place because the host owns the look: the agent says "an orange cone" and
    /// gets a good one. Meshes and materials are shared, so a scene of many copies stays cheap on the Quest.
    /// </summary>
    public static class ArtifactLook
    {
        public const string NoValue = "--";

        static readonly Regex Metric = new Regex(@"\{[a-z0-9_]+\}");
        static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
        static Material trail;
        static Mesh cone, cylinder;

        public static Color Color(string hex, Color fallback) =>
            hex != null && ColorUtility.TryParseHtmlString(hex, out var color) ? color : fallback;

        /// <summary>
        /// One shared material per colour: soft and slightly glossy so it sits well on passthrough, or self-lit with
        /// <paramref name="glow"/>. Cloned from templates in Resources, not Shader.Find: a shader no asset uses is stripped
        /// from a Quest build, and Find would return null there while working fine in the Editor.
        /// </summary>
        public static Material Material(string hex, bool glow = false)
        {
            string key = (hex ?? "") + (glow ? "+" : "");
            if (materials.TryGetValue(key, out var material) && material != null) return material;
            material = new Material(Template(glow ? "ArtifactGlow" : "ArtifactLit")) { name = "Artifact " + key };
            material.SetColor("_BaseColor", Color(hex, ForgeTheme.Tool));
            materials[key] = material;
            return material;
        }

        /// <summary>Transparent and vertex-coloured, so a trail can fade along its length.</summary>
        public static Material Trail() => trail != null ? trail : trail = Template("ArtifactTrail");

        static Material Template(string name) => Resources.Load<Material>("ArtifactLook/" + name);

        public static GameObject Shape(string shape, bool collider)
        {
            GameObject go;
            if (shape == "cone" || shape == "cylinder")
            {
                go = new GameObject(shape, typeof(MeshFilter), typeof(MeshRenderer));
                go.GetComponent<MeshFilter>().sharedMesh = shape == "cone" ? Cone() : Cylinder();
                if (collider) go.AddComponent<MeshCollider>().convex = true;
                return go;
            }
            go = GameObject.CreatePrimitive(PrimitiveFor(shape));
            go.name = shape;
            if (!collider) DestroyCollider(go);
            return go;
        }

        static void DestroyCollider(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (Application.isPlaying) Object.Destroy(c);
            else Object.DestroyImmediate(c);
        }

        static PrimitiveType PrimitiveFor(string shape)
        {
            switch (shape)
            {
                case "sphere": return PrimitiveType.Sphere;
                case "capsule": return PrimitiveType.Capsule;
                case "plane": return PrimitiveType.Plane;
                default: return PrimitiveType.Cube;
            }
        }

        /// <summary>
        /// The local scale that makes a shape <paramref name="size"/> metres across. Unity's capsule is 2 m tall and its
        /// plane is 10 m wide, so a <c>size</c> means the same thing for every shape.
        /// </summary>
        public static Vector3 ShapeScale(string shape, Vector3 size)
        {
            switch (shape)
            {
                case "capsule": return new Vector3(size.x, size.y * 0.5f, size.z);
                case "plane": return new Vector3(size.x * 0.1f, 1f, size.z * 0.1f);
                default: return size;
            }
        }

        /// <summary>Text with its <c>{metric}</c> holes shown as "no value yet". Never a made-up zero.</summary>
        public static string WithoutMetrics(string text) => Metric.Replace(text ?? "", NoValue);

        static Mesh Cone() => cone != null ? cone : cone = Lathe("Artifact cone", 0f);
        static Mesh Cylinder() => cylinder != null ? cylinder : cylinder = Lathe("Artifact cylinder", 0.5f);

        // 1 m wide and 1 m tall, centred on its middle like Unity's own shapes. Unity's cylinder has 20 sides, which shows
        // on anything big. Caps have their own vertices, so their edges stay sharp while the sides stay smooth.
        static Mesh Lathe(string name, float topRadius)
        {
            const int Sides = 64;
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int i = 0; i <= Sides; i++)
            {
                vertices.Add(Rim(i, Sides, 0.5f, -0.5f));
                vertices.Add(Rim(i, Sides, topRadius, 0.5f));
            }
            for (int i = 0; i < Sides; i++)
            {
                int v = i * 2;
                triangles.Add(v); triangles.Add(v + 1); triangles.Add(v + 2);
                triangles.Add(v + 2); triangles.Add(v + 1); triangles.Add(v + 3);
            }
            Cap(vertices, triangles, Sides, 0.5f, -0.5f, false);
            if (topRadius > 0f) Cap(vertices, triangles, Sides, topRadius, 0.5f, true);
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Vector3 Rim(int i, int sides, float radius, float y)
        {
            float a = i * Mathf.PI * 2f / sides;
            return new Vector3(Mathf.Sin(a) * radius, y, Mathf.Cos(a) * radius);
        }

        static void Cap(List<Vector3> vertices, List<int> triangles, int sides, float radius, float y, bool up)
        {
            int centre = vertices.Count;
            vertices.Add(new Vector3(0f, y, 0f));
            for (int i = 0; i <= sides; i++) vertices.Add(Rim(i, sides, radius, y));
            for (int i = 0; i < sides; i++)
            {
                triangles.Add(centre);
                triangles.Add(centre + 1 + (up ? i : i + 1));
                triangles.Add(centre + 1 + (up ? i + 1 : i));
            }
        }
    }
}
