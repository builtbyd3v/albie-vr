using System.Collections.Generic;
using Forge.Artifacts;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace Forge.Demo
{
    /// <summary>
    /// The room the flight test happens in, as the drone sees it: where the floor, ceiling and furniture are (for flying round
    /// them) and the room drawn as clean geometry (for the drone-cam picture and the scan shimmer). The source is the headset's
    /// scan through <see cref="MrukFloorPlan"/> (the simulator's room in the editor); with no scan it is the mock living room
    /// from <see cref="MockFloorPlan"/>, centred on the head, so the desktop rig and the sandbox run the same code.
    /// Geometry is built once; nothing here allocates per frame.
    /// </summary>
    public sealed class DroneRoom
    {
        public readonly IFloorPlan Plan;
        public readonly bool FromScan;
        public readonly float FloorY;
        public readonly float Ceiling;      // world y of the ceiling
        public readonly Mesh Edges = new Mesh { name = "DroneRoom edges" };      // lines
        public readonly Mesh WallFill = new Mesh { name = "DroneRoom walls" };   // faint wall faces
        public readonly Mesh BoxFill = new Mesh { name = "DroneRoom furniture" }; // solid furniture, slightly inset

        static readonly Color Edge = new Color(0.45f, 0.88f, 1f, 0.9f);
        static readonly Color EdgeDim = new Color(0.45f, 0.88f, 1f, 0.28f);
        static readonly Color Wall = new Color(0.45f, 0.88f, 1f, 0.07f);
        static readonly Color Solid = new Color(0.07f, 0.11f, 0.14f, 1f);

        readonly List<Vector3> edgeV = new List<Vector3>(512);
        readonly List<Color> edgeC = new List<Color>(512);
        readonly List<int> edgeI = new List<int>(512);
        readonly List<Vector3> wallV = new List<Vector3>(64);
        readonly List<int> wallI = new List<int>(96);
        readonly List<Color> wallC = new List<Color>(64);
        readonly List<Vector3> boxV = new List<Vector3>(256);
        readonly List<int> boxI = new List<int>(384);
        readonly List<Color> boxC = new List<Color>(256);

        static readonly Vector3[] Ring = new Vector3[8];
        Bounds bounds;

        public Bounds FloorBounds => bounds;

        DroneRoom(IFloorPlan plan, bool fromScan, float floorY, float ceiling)
        {
            Plan = plan;
            FromScan = fromScan;
            FloorY = floorY;
            Ceiling = ceiling;
        }

        /// <summary>The scanned room if MRUK has one, else the mock living room around <paramref name="head"/>.</summary>
        public static DroneRoom Create(Vector3 head)
        {
            MrukFloorPlan scan = null;
            try { scan = MrukFloorPlan.Current(); } catch (System.Exception) { /* MRUK not running */ }
            if (scan != null && scan.HasFloor)
            {
                float floor = scan.FloorY;
                float ceil = float.IsNaN(scan.CeilingHeight) ? floor + 2.6f : floor + scan.CeilingHeight;
                var room = new DroneRoom(scan, true, floor, ceil);
                room.bounds = scan.FloorBounds;
                room.BuildFromScan(scan.Room);
                return room;
            }
            var mock = MockFloorPlan.LivingRoom();
            var placed = new MockFloorPlan(mock.Id, mock.Size, true, new Vector3(head.x, 0f, head.z), 0f);
            foreach (var item in mock.Items) placed.Items.Add(item);
            var r = new DroneRoom(placed, false, 0f, placed.Ceiling);
            r.bounds = placed.FloorBounds;
            r.BuildFromMock(placed);
            return r;
        }

        /// <summary>True when the drone may be at this point (x, z): inside the walls and clear of furniture by <paramref name="clearance"/> m.</summary>
        public bool Free(Vector3 p, float clearance) => Plan.IsFree(p, clearance);

        // ---- Geometry --------------------------------------------------------------------------------------------------------

        void BuildFromMock(MockFloorPlan plan)
        {
            var m = plan.RoomToWorld;
            float h = plan.Ceiling;
            float hx = plan.Size.x * 0.5f, hz = plan.Size.y * 0.5f;
            var a = m.MultiplyPoint3x4(new Vector3(-hx, 0f, -hz));
            var b = m.MultiplyPoint3x4(new Vector3(hx, 0f, -hz));
            var c = m.MultiplyPoint3x4(new Vector3(hx, 0f, hz));
            var d = m.MultiplyPoint3x4(new Vector3(-hx, 0f, hz));
            Vector3 up = Vector3.up * h;
            AddWall(a, b, up);
            AddWall(b, c, up);
            AddWall(c, d, up);
            AddWall(d, a, up);
            AddFloorGrid(a, b, c, d);
            foreach (var item in plan.Items)
            {
                var t = m * Matrix4x4.TRS(item.Centre, Quaternion.Euler(0f, item.Yaw, 0f), Vector3.one);
                AddBox(t, item.Size);
            }
            Commit();
        }

        void BuildFromScan(MRUKRoom room)
        {
            foreach (var anchor in room.Anchors)
            {
                if (anchor == null) continue;
                if (anchor.VolumeBounds.HasValue)
                {
                    var vb = anchor.VolumeBounds.Value;
                    // A volume anchor's local x and y lie on the floor and z is its height (see MrukFloorPlan).
                    var t = anchor.transform.localToWorldMatrix * Matrix4x4.Translate(vb.center);
                    AddBox(t, vb.size);
                }
                else if (anchor.PlaneRect.HasValue && anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE))
                {
                    var r = anchor.PlaneRect.Value;
                    var tr = anchor.transform;
                    Vector3 p0 = tr.TransformPoint(new Vector3(r.xMin, r.yMin, 0f));
                    Vector3 p1 = tr.TransformPoint(new Vector3(r.xMax, r.yMin, 0f));
                    Vector3 p2 = tr.TransformPoint(new Vector3(r.xMax, r.yMax, 0f));
                    Vector3 p3 = tr.TransformPoint(new Vector3(r.xMin, r.yMax, 0f));
                    AddQuad(p0, p1, p2, p3);
                }
            }
            var fb = bounds;
            var f0 = new Vector3(fb.min.x, FloorY, fb.min.z);
            var f1 = new Vector3(fb.max.x, FloorY, fb.min.z);
            var f2 = new Vector3(fb.max.x, FloorY, fb.max.z);
            var f3 = new Vector3(fb.min.x, FloorY, fb.max.z);
            AddFloorGrid(f0, f1, f2, f3);
            Commit();
        }

        void AddLine(Vector3 a, Vector3 b, Color ca, Color cb)
        {
            int n = edgeV.Count;
            edgeV.Add(a); edgeC.Add(ca);
            edgeV.Add(b); edgeC.Add(cb);
            edgeI.Add(n); edgeI.Add(n + 1);
        }

        void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            AddLine(a, b, Edge, Edge); AddLine(b, c, Edge, Edge); AddLine(c, d, Edge, Edge); AddLine(d, a, Edge, Edge);
            int n = wallV.Count;
            wallV.Add(a); wallV.Add(b); wallV.Add(c); wallV.Add(d);
            for (int i = 0; i < 4; i++) wallC.Add(Wall);
            wallI.Add(n); wallI.Add(n + 1); wallI.Add(n + 2);
            wallI.Add(n); wallI.Add(n + 2); wallI.Add(n + 3);
        }

        void AddWall(Vector3 from, Vector3 to, Vector3 up) => AddQuad(from, to, to + up, from + up);

        void AddFloorGrid(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            // A line every half metre along both edges: it gives the drone cam something to slide past.
            float lenAB = Vector3.Distance(a, b), lenAD = Vector3.Distance(a, d);
            int nAB = Mathf.Clamp(Mathf.FloorToInt(lenAB / 0.5f), 1, 24);
            int nAD = Mathf.Clamp(Mathf.FloorToInt(lenAD / 0.5f), 1, 24);
            for (int i = 0; i <= nAB; i++)
            {
                float t = i / (float)nAB;
                AddLine(Vector3.Lerp(a, b, t), Vector3.Lerp(d, c, t), EdgeDim, EdgeDim);
            }
            for (int i = 0; i <= nAD; i++)
            {
                float t = i / (float)nAD;
                AddLine(Vector3.Lerp(a, d, t), Vector3.Lerp(b, c, t), EdgeDim, EdgeDim);
            }
        }

        /// <summary>A box: <paramref name="size"/> metres about the matrix's origin. Edges at full size, the solid fill 1 cm inside so the lines show.</summary>
        void AddBox(Matrix4x4 m, Vector3 size)
        {
            Vector3 h = size * 0.5f;
            for (int i = 0; i < 8; i++)
                Ring[i] = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z));
            // 12 edges
            int[,] e = { {0,1},{2,3},{4,5},{6,7},{0,2},{1,3},{4,6},{5,7},{0,4},{1,5},{2,6},{3,7} };
            for (int i = 0; i < 12; i++) AddLine(Ring[e[i, 0]], Ring[e[i, 1]], Edge, Edge);

            Vector3 inset = Vector3.one * 0.01f;
            Vector3 hi = Vector3.Max(h - inset, Vector3.one * 0.005f);
            var c8 = new Vector3[8];
            for (int i = 0; i < 8; i++)
                c8[i] = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? -hi.x : hi.x, (i & 2) == 0 ? -hi.y : hi.y, (i & 4) == 0 ? -hi.z : hi.z));
            int[,] faces = { {0,1,3,2},{4,6,7,5},{0,4,5,1},{2,3,7,6},{0,2,6,4},{1,5,7,3} };
            for (int f = 0; f < 6; f++)
            {
                int n = boxV.Count;
                for (int k = 0; k < 4; k++) { boxV.Add(c8[faces[f, k]]); boxC.Add(Solid); }
                boxI.Add(n); boxI.Add(n + 1); boxI.Add(n + 2);
                boxI.Add(n); boxI.Add(n + 2); boxI.Add(n + 3);
            }
        }

        void Commit()
        {
            Edges.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            Edges.SetVertices(edgeV);
            Edges.SetColors(edgeC);
            Edges.SetIndices(edgeI, MeshTopology.Lines, 0);
            WallFill.SetVertices(wallV);
            WallFill.SetColors(wallC);
            WallFill.SetTriangles(wallI, 0);
            BoxFill.SetVertices(boxV);
            BoxFill.SetColors(boxC);
            BoxFill.SetTriangles(boxI, 0);
            // Both draw double-sided with no lighting; bounds only matter for culling.
            Edges.RecalculateBounds();
            WallFill.RecalculateBounds();
            BoxFill.RecalculateBounds();
            edgeV.Clear(); edgeC.Clear(); edgeI.Clear();
            wallV.Clear(); wallC.Clear(); wallI.Clear();
            boxV.Clear(); boxC.Clear(); boxI.Clear();
        }

        public void Dispose()
        {
            if (Edges != null) Object.Destroy(Edges);
            if (WallFill != null) Object.Destroy(WallFill);
            if (BoxFill != null) Object.Destroy(BoxFill);
        }
    }
}
