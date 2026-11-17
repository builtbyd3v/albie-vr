using System.Collections.Generic;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>Non-destructive inspection of imported geometry. Mesh sections are visual aids, not anatomical segmentation.</summary>
    public sealed class ArtifactExplorer : MonoBehaviour
    {
        sealed class Part
        {
            public Transform transform;
            public Vector3 position;
            public MeshFilter filter;
            public Mesh original, section;
        }
        readonly List<Part> parts = new List<Part>();
        Quaternion rotation;
        Vector3 position, scale, centre;
        bool separated, sectioned;
        public int PartCount => parts.Count;

        public void Initialize()
        {
            rotation = transform.localRotation;
            position = transform.localPosition;
            scale = transform.localScale;
            var bounds = new Bounds();
            bool first = true;
            foreach (var filter in GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || !filter.sharedMesh.isReadable) continue;
                parts.Add(new Part { transform = filter.transform, position = filter.transform.localPosition,
                    filter = filter, original = filter.sharedMesh });
                var renderer = filter.GetComponent<Renderer>();
                if (renderer == null) continue;
                if (first) { bounds = renderer.bounds; first = false; }
                else bounds.Encapsulate(renderer.bounds);
            }
            centre = transform.InverseTransformPoint(bounds.center);
        }

        public void Turn() => transform.RotateAround(transform.TransformPoint(centre), transform.up, 30f);

        public void Separate()
        {
            if (parts.Count < 2) return;
            separated = !separated;
            foreach (var part in parts)
            {
                var direction = part.transform.TransformPoint(part.original.bounds.center) - transform.TransformPoint(centre);
                part.transform.localPosition = part.position + (separated
                    ? part.transform.parent.InverseTransformVector(direction.normalized * 0.04f) : Vector3.zero);
            }
        }

        public void Section()
        {
            sectioned = !sectioned;
            foreach (var part in parts)
            {
                if (sectioned && part.section == null)
                {
                    // Keep the back half of each surface. No invented internal organs or cut-surface caps.
                    var mesh = Instantiate(part.original);
                    mesh.name = part.original.name + " inspection section";
                    var vertices = mesh.vertices;
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                        var indices = part.original.GetTriangles(sub);
                        var kept = new List<int>();
                        float plane = part.original.bounds.center.z;
                        for (int i = 0; i < indices.Length; i += 3)
                        {
                            if (vertices[indices[i]].z > plane || vertices[indices[i + 1]].z > plane || vertices[indices[i + 2]].z > plane) continue;
                            kept.Add(indices[i]); kept.Add(indices[i + 1]); kept.Add(indices[i + 2]);
                        }
                        mesh.SetTriangles(kept, sub, false);
                    }
                    part.section = mesh;
                }
                part.filter.sharedMesh = sectioned ? part.section : part.original;
            }
        }

        public void ResetView()
        {
            if (sectioned) Section();
            if (separated) Separate();
            transform.localRotation = rotation;
            transform.localScale = scale;
            transform.localPosition = position;
        }

        void OnDestroy()
        {
            foreach (var part in parts)
                if (part.section != null)
                {
                    if (Application.isPlaying) Destroy(part.section);
                    else DestroyImmediate(part.section);
                }
        }
    }
}
