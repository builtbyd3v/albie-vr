using System.Collections.Generic;
using Forge.Artifacts;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Forge.Controllers
{
    /// <summary>
    /// A real camera on the drone's nose, rendering to a small <see cref="Texture"/> (<see cref="Width"/> x <see cref="Height"/>)
    /// at a reduced rate (<see cref="framesPerSecond"/>: the camera is switched on for one frame at a time), for the drone
    /// cam window. Off by default and costs nothing while off. What it can see is the VIRTUAL scene only: passthrough is
    /// not available to a Unity camera, so the real room is not in the picture. To give the picture the room, it draws the
    /// room scan as a wireframe (walls and furniture volumes from MRUK, else the floor plan's bounds) and an orange wire box
    /// at the user's KNOWN headset position, both only into this camera (Graphics.DrawMesh with the camera). The box is where
    /// the headset says the user is; nothing is detected in the image. No allocations per frame.
    /// </summary>
    public sealed class DroneCam : MonoBehaviour
    {
        public const int Width = 320, Height = 240;
        [Tooltip("How often the drone cam renders, frames a second.")] public float framesPerSecond = 12f;
        [Tooltip("Field of view, degrees (vertical).")] public float fieldOfView = 70f;

        /// <summary>The drone cam that is on now, or null.</summary>
        public static DroneCam Live { get; private set; }

        public bool On { get; private set; }
        public RenderTexture Texture { get; private set; }
        /// <summary>The floor plan to draw when there is no MRUK room (the Editor's mock rooms). Set by the drone's spawner.</summary>
        public IFloorPlan Room;
        /// <summary>True when the wireframe drawn is the MRUK scan (walls and furniture); false for the floor plan's bounds or nothing.</summary>
        public bool DrawsScan { get; private set; }

        static readonly Color RoomColor = new Color(0.45f, 0.88f, 1f, 1f);
        static readonly Color RoomDim = new Color(0.45f, 0.88f, 1f, 0.35f);
        static readonly Color UserColor = new Color(1f, 0.55f, 0.15f, 1f);
        static Material lineMaterial;
        static Mesh userBox;

        Transform mount;
        Camera cam;
        Mesh roomMesh;
        object roomSource;
        float nextFrame, nextRoomCheck;

        DroneBody body;

        public void Init(Transform nose, DroneBody owner)
        {
            mount = nose;
            body = owner;
        }

        public void SetOn(bool on)
        {
            if (on == On) return;
            On = on;
            if (on)
            {
                EnsureCamera();
                if (Texture == null)
                {
                    Texture = new RenderTexture(Width, Height, 16, RenderTextureFormat.ARGB32) { name = "Drone cam", antiAliasing = 1 };
                    Texture.Create();
                }
                cam.targetTexture = Texture;
                nextFrame = nextRoomCheck = 0f;
                Live = this;
            }
            else
            {
                if (cam != null) { cam.enabled = false; cam.targetTexture = null; }
                if (Texture != null) { Texture.Release(); Destroy(Texture); Texture = null; }
                if (Live == this) Live = null;
            }
        }

        void EnsureCamera()
        {
            if (cam != null) return;
            var go = new GameObject("Drone cam");
            go.transform.SetParent(mount != null ? mount : transform, false);
            cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.fieldOfView = fieldOfView;
            cam.nearClipPlane = 0.03f;
            cam.farClipPlane = 25f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.07f, 0.09f, 1f);
            cam.cullingMask = ~(1 << 5);  // not the UI layer: the windows (and this picture's own window) stay out
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data != null)
            {
                data.renderShadows = false;
                data.renderPostProcessing = false;
                data.antialiasing = AntialiasingMode.None;
                data.requiresDepthTexture = false;
                data.requiresColorTexture = false;
            }
        }

        void OnDisable() => SetOn(false);

        void OnDestroy()
        {
            SetOn(false);
            if (roomMesh != null) Destroy(roomMesh);
        }

        void Update()
        {
            if (!On || cam == null) return;
            if (cam.enabled) cam.enabled = false;  // it rendered last frame
            float now = Time.unscaledTime;
            if (now >= nextRoomCheck)
            {
                nextRoomCheck = now + 2f;
                CheckRoom();
            }
            if (now < nextFrame) return;
            nextFrame = now + 1f / Mathf.Max(1f, framesPerSecond);
            cam.enabled = true;  // renders this frame, into the texture
            if (lineMaterial == null) lineMaterial = MakeLineMaterial();
            if (lineMaterial == null) return;
            if (roomMesh != null) Graphics.DrawMesh(roomMesh, Matrix4x4.identity, lineMaterial, 0, cam);
            var head = Camera.main;
            if (head == null || head == cam) return;
            if (userBox == null) userBox = MakeUserBox();
            Vector3 h = head.transform.position;
            float floor = body != null ? body.transform.position.y - body.HeightAboveFloor : h.y - 1.6f;
            float tall = Mathf.Max(0.3f, h.y - floor + 0.12f);
            var at = new Vector3(h.x, floor + tall * 0.5f, h.z);
            var turn = Quaternion.Euler(0f, head.transform.eulerAngles.y, 0f);
            Graphics.DrawMesh(userBox, Matrix4x4.TRS(at, turn, new Vector3(0.5f, tall, 0.32f)), lineMaterial, 0, cam);
        }

        // The room to draw: MRUK's current room (the scan, or the simulator's room), else the floor plan's bounds.
        void CheckRoom()
        {
            object source = null;
            MRUKRoom mruk = null;
            if (MRUK.Instance != null && MRUK.Instance.IsInitialized)
            {
                mruk = MRUK.Instance.GetCurrentRoom();
                if (mruk == null && MRUK.Instance.Rooms.Count > 0) mruk = MRUK.Instance.Rooms[0];
            }
            if (mruk != null) source = mruk;
            else if (Room != null && Room.HasFloor) source = Room;
            if (ReferenceEquals(source, roomSource)) return;
            roomSource = source;
            if (roomMesh != null) { Destroy(roomMesh); roomMesh = null; }
            DrawsScan = mruk != null;
            if (mruk != null) roomMesh = BuildScan(mruk);
            else if (Room != null && Room.HasFloor) roomMesh = BuildBounds(Room);
        }

        // ---- Meshes (built once per room) --------------------------------------------------------------------------------

        static Mesh BuildScan(MRUKRoom room)
        {
            var v = new List<Vector3>(512);
            var c = new List<Color>(512);
            var i = new List<int>(512);
            foreach (var anchor in room.Anchors)
            {
                if (anchor == null) continue;
                var t = anchor.transform;
                if (anchor.VolumeBounds.HasValue)
                {
                    var vb = anchor.VolumeBounds.Value;
                    Box(v, c, i, t.localToWorldMatrix * Matrix4x4.TRS(vb.center, Quaternion.identity, vb.size), RoomColor);
                }
                else if (anchor.PlaneRect.HasValue)
                {
                    var r = anchor.PlaneRect.Value;
                    bool floorOrCeiling = anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.CEILING);
                    Color col = floorOrCeiling ? RoomDim : RoomColor;
                    Vector3 p0 = t.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)), p1 = t.TransformPoint(new Vector3(r.xMax, r.yMin, 0f));
                    Vector3 p2 = t.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)), p3 = t.TransformPoint(new Vector3(r.xMin, r.yMax, 0f));
                    Line(v, c, i, p0, p1, col); Line(v, c, i, p1, p2, col); Line(v, c, i, p2, p3, col); Line(v, c, i, p3, p0, col);
                    if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR))
                        for (float u = 0.5f; u < r.width; u += 0.5f)
                            Line(v, c, i, t.TransformPoint(new Vector3(r.xMin + u, r.yMin, 0f)), t.TransformPoint(new Vector3(r.xMin + u, r.yMax, 0f)), RoomDim);
                }
            }
            return Commit("Drone cam room (scan)", v, c, i);
        }

        static Mesh BuildBounds(IFloorPlan plan)
        {
            var v = new List<Vector3>(64);
            var c = new List<Color>(64);
            var i = new List<int>(64);
            var b = plan.FloorBounds;
            float h = float.IsNaN(plan.CeilingHeight) ? 2.4f : plan.CeilingHeight;
            var centre = new Vector3(b.center.x, plan.FloorY + h * 0.5f, b.center.z);
            Box(v, c, i, Matrix4x4.TRS(centre, Quaternion.identity, new Vector3(b.size.x, h, b.size.z)), RoomDim);
            return Commit("Drone cam room (plan bounds)", v, c, i);
        }

        static Mesh MakeUserBox()
        {
            var v = new List<Vector3>(32);
            var c = new List<Color>(32);
            var i = new List<int>(32);
            Box(v, c, i, Matrix4x4.identity, UserColor);
            // A cross at head height (the box's top is 12 cm over the headset).
            float top = 0.5f - 0.12f;
            Line(v, c, i, new Vector3(-0.5f, top, 0f), new Vector3(0.5f, top, 0f), UserColor);
            return Commit("Drone cam user box", v, c, i);
        }

        // A unit box about the origin, through m.
        static void Box(List<Vector3> v, List<Color> c, List<int> i, Matrix4x4 m, Color col)
        {
            for (int a = 0; a < 12; a++)
            {
                int e0 = Edges[a * 2], e1 = Edges[a * 2 + 1];
                Line(v, c, i, m.MultiplyPoint3x4(Corner(e0)), m.MultiplyPoint3x4(Corner(e1)), col);
            }
        }

        static readonly int[] Edges = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 };
        static Vector3 Corner(int k) => new Vector3((k & 1) == 0 ? -0.5f : 0.5f, (k & 2) == 0 ? -0.5f : 0.5f, (k & 4) == 0 ? -0.5f : 0.5f);

        static void Line(List<Vector3> v, List<Color> c, List<int> i, Vector3 a, Vector3 b, Color col)
        {
            int n = v.Count;
            v.Add(a); v.Add(b);
            c.Add(col); c.Add(col);
            i.Add(n); i.Add(n + 1);
        }

        static Mesh Commit(string name, List<Vector3> v, List<Color> c, List<int> i)
        {
            var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetIndices(i, MeshTopology.Lines, 0);
            m.RecalculateBounds();
            return m;
        }

        // Unlit, vertex-coloured: Sprites/Default is in the always-included shaders.
        static Material MakeLineMaterial()
        {
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("UI/Default");
            return shader != null ? new Material(shader) { name = "Drone cam lines" } : null;
        }
    }
}
