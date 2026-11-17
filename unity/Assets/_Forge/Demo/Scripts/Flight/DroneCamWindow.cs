using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace Forge.Demo
{
    /// <summary>
    /// The drone-cam window: a small rounded window (about 18 x 15 cm) floating at the left wrist (the head-relative spot beside the
    /// body when that hand isn't tracked), turned to face the head, showing what the drone's nose camera sees. Passthrough can't be
    /// seen by a Unity camera, so the camera draws the virtual room instead (clean wireframe on dark, from <see cref="DroneRoom"/>)
    /// plus a head-and-shoulders marker where the user is. Only this camera sees those: they sit on layer 31 and are switched on just
    /// for the render. A tracking box follows the marker (projected into the picture) with a confidence label, and a small HUD shows
    /// distance and altitude. The picture is 512 x 384 at 30 fps. No allocations per frame.
    /// </summary>
    public sealed class DroneCamWindow : MonoBehaviour
    {
        public const int Layer = 31;

        [Header("Materials (Forge/Demo/FlightUnlit)")]
        public Material alphaMaterial;
        public Material opaqueMaterial;

        [Header("Picture")]
        public int width = 512;
        public int height = 384;
        public float fps = 30f;
        public float fieldOfView = 70f;

        // Layout, in canvas units (1 unit = 1 mm).
        const float FrameW = 188f, FrameH = 176f, VideoW = 168f, VideoH = 126f, VideoY = 0f, Strip = 25f, Corner = 14f;
        static readonly Color Accent = new Color(0.45f, 0.88f, 1f, 1f);
        static readonly Color Amber = new Color(1f, 0.74f, 0.25f, 1f);

        public bool Visible => visible;
        public float Confidence { get; private set; } = 0.97f;
        public bool Searching { get; private set; }
        /// <summary>The picture, for screenshots in the play test.</summary>
        public RenderTexture Picture => rt;
        public Rect BoxViewport => boxTarget;

        DemoDrone drone;
        DroneRoom room;
        Camera cam;
        RenderTexture rt;
        Canvas canvas;
        CanvasGroup group;
        RawImage video;
        RectTransform overlay;
        Image[] bars = new Image[8];
        Image labelPill, recDot;
        TMP_Text label, distText, altText, recText;
        Renderer[] roomRenderers = new Renderer[3];
        Transform markerHead, markerTorso;
        Renderer[] markerRenderers = new Renderer[2];
        Material wireMat, wallMat, boxMat, markerMat, markerHeadMat;

        bool visible, placed, mounted;
        float shown;                 // 0..1, eased
        Vector3 winVel;
        float nextRender, nextHud, nextConfidence;
        float lostFor;
        Rect boxTarget;              // viewport, set at each render
        Vector4 boxNow, boxVel;      // xMin, yMin, xMax, yMax in video units, smoothed
        bool boxInit;
        Vector2 labelPos, labelVel;
        readonly Vector3[] corners = new Vector3[8];

        void Awake()
        {
            BuildCamera();
            BuildUi();
            BuildWorldParts();
            SetShown(0f);
        }

        void OnDestroy()
        {
            if (rt != null) { rt.Release(); Destroy(rt); }
            if (wireMat != null) Destroy(wireMat);
            if (wallMat != null) Destroy(wallMat);
            if (boxMat != null) Destroy(boxMat);
            if (markerMat != null) Destroy(markerMat);
            if (markerHeadMat != null) Destroy(markerHeadMat);
        }

        // ---- Setup ---------------------------------------------------------------------------------------------------------

        void BuildCamera()
        {
            rt = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32) { name = "DroneCam", antiAliasing = 2, useMipMap = false };
            rt.Create();
            var go = new GameObject("DroneCam camera");
            go.transform.SetParent(transform, false);
            cam = go.AddComponent<Camera>();
            cam.enabled = false;                // rendered by hand, 30 times a second
            cam.targetTexture = rt;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.035f, 0.05f, 0.065f, 1f);
            cam.cullingMask = 1 << Layer;
            cam.fieldOfView = fieldOfView;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 40f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.aspect = width / (float)height;
            cam.depth = -100f;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data != null)
            {
                data.renderPostProcessing = false;
                data.antialiasing = AntialiasingMode.None;
                data.renderShadows = false;
                data.requiresDepthOption = CameraOverrideOption.Off;
                data.requiresColorOption = CameraOverrideOption.Off;
            }
        }

        void BuildUi()
        {
            var cgo = new GameObject("Window", typeof(RectTransform));
            cgo.transform.SetParent(transform, false);
            canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 5;
            group = cgo.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            var rt0 = (RectTransform)cgo.transform;
            rt0.sizeDelta = new Vector2(FrameW, FrameH);
            rt0.localScale = Vector3.one * 0.001f;

            var frame = NewImage("Frame", rt0, FlightTextures.Rounded(false), new Color(0.145f, 0.15f, 0.165f, 1f));
            frame.type = Image.Type.Sliced;
            Stretch((RectTransform)frame.transform);

            var vgo = new GameObject("Video", typeof(RectTransform));
            vgo.transform.SetParent(rt0, false);
            var vr = (RectTransform)vgo.transform;
            vr.anchoredPosition = new Vector2(0f, VideoY);
            vr.sizeDelta = new Vector2(VideoW, VideoH);
            video = vgo.AddComponent<RawImage>();
            video.texture = rt;
            video.raycastTarget = false;

            // Overlay: the tracking box and its label, clipped to the picture by RectMask2D (rectangular), with the picture's rounded
            // corners drawn over it in the frame colour, so the edge is smooth.
            var ogo = new GameObject("Overlay", typeof(RectTransform));
            ogo.transform.SetParent(vr, false);
            overlay = (RectTransform)ogo.transform;
            Stretch(overlay);
            ogo.AddComponent<RectMask2D>();

            for (int i = 0; i < bars.Length; i++)
            {
                bars[i] = NewImage("Bracket", overlay, FlightTextures.White(), Accent);
                bars[i].enabled = false;
            }
            labelPill = NewImage("LabelPill", overlay, FlightTextures.Rounded(true), new Color(0.04f, 0.06f, 0.08f, 0.72f));
            labelPill.type = Image.Type.Sliced;
            ((RectTransform)labelPill.transform).sizeDelta = new Vector2(112f, 24f);
            label = NewText("Label", labelPill.transform, 16f, Accent, TextAlignmentOptions.Center);
            Stretch((RectTransform)label.transform);
            labelPill.enabled = false;
            label.enabled = false;

            for (int c = 0; c < 4; c++)
            {
                var cover = NewImage("Corner", vr, FlightTextures.InverseCorner((int)Corner), frame.color);
                var cr = (RectTransform)cover.transform;
                float ax = (c & 1) == 0 ? 0f : 1f, ay = (c & 2) == 0 ? 0f : 1f;
                cr.anchorMin = cr.anchorMax = new Vector2(ax, ay);
                cr.pivot = Vector2.zero;
                cr.anchoredPosition = Vector2.zero;
                cr.sizeDelta = new Vector2(Corner, Corner);
                cr.localScale = new Vector3(ax > 0f ? -1f : 1f, ay > 0f ? -1f : 1f, 1f);
            }

            // Top strip: the REC light and the window's name. Bottom strip: distance and altitude.
            float rowY = FrameH * 0.5f - Strip * 0.5f;
            recDot = NewImage("RecDot", rt0, FlightTextures.Dot(), new Color(1f, 0.32f, 0.3f, 1f));
            var rd = (RectTransform)recDot.transform;
            rd.anchoredPosition = new Vector2(-FrameW * 0.5f + 22f, rowY);
            rd.sizeDelta = new Vector2(9f, 9f);
            recText = NewText("Rec", rt0, 14f, new Color(1f, 1f, 1f, 0.95f), TextAlignmentOptions.MidlineLeft);
            var rtx = (RectTransform)recText.transform;
            rtx.pivot = new Vector2(0f, 0.5f);
            rtx.anchoredPosition = new Vector2(-FrameW * 0.5f + 33f, rowY);
            rtx.sizeDelta = new Vector2(50f, 20f);
            recText.SetText("REC");
            var nameText = NewText("Name", rt0, 14f, new Color(1f, 1f, 1f, 0.7f), TextAlignmentOptions.MidlineRight);
            var nr = (RectTransform)nameText.transform;
            nr.pivot = new Vector2(1f, 0.5f);
            nr.anchoredPosition = new Vector2(FrameW * 0.5f - 14f, rowY);
            nr.sizeDelta = new Vector2(100f, 20f);
            nameText.SetText("Drone cam");

            distText = NewText("Distance", rt0, 14f, new Color(1f, 1f, 1f, 0.9f), TextAlignmentOptions.MidlineLeft);
            var dr = (RectTransform)distText.transform;
            dr.pivot = new Vector2(0f, 0.5f);
            dr.anchoredPosition = new Vector2(-VideoW * 0.5f + 4f, -rowY);
            dr.sizeDelta = new Vector2(82f, 20f);
            altText = NewText("Altitude", rt0, 14f, new Color(1f, 1f, 1f, 0.9f), TextAlignmentOptions.MidlineRight);
            var ar = (RectTransform)altText.transform;
            ar.pivot = new Vector2(1f, 0.5f);
            ar.anchoredPosition = new Vector2(VideoW * 0.5f - 4f, -rowY);
            ar.sizeDelta = new Vector2(82f, 20f);
            distText.SetText("0.0 m away");
            altText.SetText("0.0 m high");
        }

        static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        static Image NewImage(string n, Transform parent, Sprite sprite, Color color)
        {
            var go = new GameObject(n, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        static TMP_Text NewText(string n, Transform parent, float size, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(n, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            var font = ForgeTheme.Font;
            if (font != null) t.font = font;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.raycastTarget = false;
            return t;
        }

        void BuildWorldParts()
        {
            if (alphaMaterial != null)
            {
                wireMat = new Material(alphaMaterial) { name = "dronecam wire" };
                wallMat = new Material(alphaMaterial) { name = "dronecam wall" };
                markerMat = new Material(alphaMaterial) { name = "dronecam marker" };
                markerHeadMat = new Material(alphaMaterial) { name = "dronecam marker head" };
                markerMat.SetColor("_Color", new Color(0.45f, 0.88f, 1f, 0.38f));
                markerHeadMat.SetColor("_Color", new Color(0.7f, 0.95f, 1f, 0.6f));
            }
            if (opaqueMaterial != null) boxMat = new Material(opaqueMaterial) { name = "dronecam furniture" };

            var root = new GameObject("DroneCam world").transform;
            root.SetParent(transform, false);
            roomRenderers[0] = MakeLayer("Room edges", root, wireMat);
            roomRenderers[1] = MakeLayer("Room walls", root, wallMat);
            roomRenderers[2] = MakeLayer("Room furniture", root, boxMat);
            markerHead = MakeMarker("Marker head", root, PrimitiveType.Sphere, markerHeadMat, out markerRenderers[0]);
            markerTorso = MakeMarker("Marker torso", root, PrimitiveType.Capsule, markerMat, out markerRenderers[1]);
            SetWorldVisible(false);
        }

        static Renderer MakeLayer(string n, Transform parent, Material m)
        {
            var go = new GameObject(n, typeof(MeshFilter), typeof(MeshRenderer));
            go.layer = Layer;
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        static Transform MakeMarker(string n, Transform parent, PrimitiveType type, Material m, out Renderer r)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.name = n;
            go.layer = Layer;
            go.transform.SetParent(parent, false);
            r = go.GetComponent<MeshRenderer>();
            if (m != null) r.sharedMaterial = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        void SetWorldVisible(bool on)
        {
            for (int i = 0; i < roomRenderers.Length; i++) if (roomRenderers[i] != null) roomRenderers[i].enabled = on;
            for (int i = 0; i < markerRenderers.Length; i++) if (markerRenderers[i] != null) markerRenderers[i].enabled = on;
        }

        // ---- Control ---------------------------------------------------------------------------------------------------------

        public void SetRoom(DroneRoom r)
        {
            room = r;
            if (r == null) return;
            roomRenderers[0].GetComponent<MeshFilter>().sharedMesh = r.Edges;
            roomRenderers[1].GetComponent<MeshFilter>().sharedMesh = r.WallFill;
            roomRenderers[2].GetComponent<MeshFilter>().sharedMesh = r.BoxFill;
            // Edges are lines: they need their own topology, which the mesh carries. Nothing else to set.
        }

        public void OnMounted() => mounted = true;

        public void Show(DemoDrone d)
        {
            drone = d;
            if (room == null && d != null && d.Room != null) SetRoom(d.Room);
            visible = true;
            placed = false;
            boxInit = false;
            lostFor = 0f;
            nextRender = 0f;
            canvas.enabled = true;
        }

        public void Hide() => visible = false;

        public void ResetState()
        {
            visible = false;
            shown = 0f;
            placed = false;
            mounted = false;
            room = null;
            SetShown(0f);
        }

        // ---- Per frame ---------------------------------------------------------------------------------------------------------

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            float to = visible ? 1f : 0f;
            if (!Mathf.Approximately(shown, to)) shown = Mathf.MoveTowards(shown, to, dt / 0.3f);
            if (shown <= 0.001f && !visible) { if (canvas.enabled) SetShown(0f); return; }
            SetShown(shown);
            PlaceWindow(dt);
            if (!visible || drone == null || drone.body == null || drone.body.Nose == null) return;
            if (Time.unscaledTime >= nextRender)
            {
                nextRender = Time.unscaledTime + 1f / Mathf.Max(1f, fps);
                RenderPicture();
            }
            UpdateBox(dt);
            UpdateHud();
        }

        void SetShown(float s)
        {
            canvas.enabled = s > 0.001f;
            group.alpha = Mathf.SmoothStep(0f, 1f, s);
            float k = Mathf.Lerp(0.88f, 1f, 1f - (1f - s) * (1f - s));
            transform.localScale = Vector3.one * k;
        }

        void PlaceWindow(float dt)
        {
            var headT = DemoLayout.Head;
            if (headT == null) return;
            Vector3 head = headT.position;
            Vector3 target;
            var e = GestureEngine.Current;
            if (e != null && e.Hands.Length > 0 && e.Hands[0].Usable)
            {
                // Above the wrist, nudged toward the eyes so the hand doesn't cover it.
                Vector3 wrist = e.Hands[0].sample.joints[HandJoint.Wrist];
                Vector3 toHead = (head - wrist).normalized;
                target = wrist + Vector3.up * 0.13f + toHead * 0.06f;
            }
            else
            {
                // No hand: where a left wrist would be, held in front of the body.
                Vector3 f = headT.forward; f.y = 0f;
                f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
                Vector3 right = Vector3.Cross(Vector3.up, f);
                target = head + f * 0.55f - right * 0.2f + Vector3.down * 0.15f;
            }
            if (!placed) { transform.position = target; winVel = Vector3.zero; placed = true; }
            else transform.position = Vector3.SmoothDamp(transform.position, target, ref winVel, 0.15f, 3f, dt);
            Vector3 away = transform.position - head;
            if (away.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
        }

        void RenderPicture()
        {
            var headT = DemoLayout.Head;
            if (headT == null) return;
            Vector3 head = headT.position;
            Transform nose = drone.body.Nose;

            // The camera sits on the nose and is stabilised: it looks level along the drone's heading, tilting to hold the head in view.
            Vector3 toHead = (head + Vector3.down * 0.15f) - nose.position;
            Vector3 flat = drone.Drone.forward;
            Vector3 look = Vector3.Slerp(flat, toHead.normalized, 0.9f);
            cam.transform.position = nose.position + flat * 0.01f;
            cam.transform.rotation = Quaternion.LookRotation(look, Vector3.up);

            // The marker: head and shoulders at the user's head, turned the way they face.
            Vector3 hf = headT.forward; hf.y = 0f;
            Quaternion yaw = hf.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(hf.normalized, Vector3.up) : Quaternion.identity;
            markerHead.SetPositionAndRotation(head, yaw);
            markerHead.localScale = new Vector3(0.17f, 0.22f, 0.2f);
            markerTorso.SetPositionAndRotation(head + Vector3.down * 0.42f, yaw);
            markerTorso.localScale = new Vector3(0.42f, 0.24f, 0.24f);

            SetWorldVisible(true);
            cam.Render();
            SetWorldVisible(false);

            // The box: head and shoulders, projected into this picture.
            Vector3 c = head + Vector3.down * 0.22f;
            var h = new Vector3(0.28f, 0.34f, 0.22f);
            float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f;
            bool front = true;
            for (int i = 0; i < 8; i++)
            {
                corners[i] = c + yaw * new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z);
                Vector3 vp = cam.WorldToViewportPoint(corners[i]);
                if (vp.z < 0.1f) { front = false; continue; }
                minX = Mathf.Min(minX, vp.x); maxX = Mathf.Max(maxX, vp.x);
                minY = Mathf.Min(minY, vp.y); maxY = Mathf.Max(maxY, vp.y);
            }
            Vector3 cv = cam.WorldToViewportPoint(c);
            bool inView = front && cv.z > 0.2f && cv.x > 0.04f && cv.x < 0.96f && cv.y > 0.04f && cv.y < 0.96f;
            boxTarget = Rect.MinMaxRect(Mathf.Clamp01(minX), Mathf.Clamp01(minY), Mathf.Clamp01(maxX), Mathf.Clamp01(maxY));
            if (inView) lostFor = 0f; else lostFor += 1f / Mathf.Max(1f, fps);
            Searching = lostFor > 0.25f;
        }

        void UpdateBox(float dt)
        {
            Rect b = boxTarget;
            Vector4 target = new Vector4((b.xMin - 0.5f) * VideoW, (b.yMin - 0.5f) * VideoH, (b.xMax - 0.5f) * VideoW, (b.yMax - 0.5f) * VideoH);
            if (!boxInit) { boxNow = target; boxVel = Vector4.zero; boxInit = true; }
            const float st = 0.07f;
            boxNow.x = Mathf.SmoothDamp(boxNow.x, target.x, ref boxVel.x, st, 1000f, dt);
            boxNow.y = Mathf.SmoothDamp(boxNow.y, target.y, ref boxVel.y, st, 1000f, dt);
            boxNow.z = Mathf.SmoothDamp(boxNow.z, target.z, ref boxVel.z, st, 1000f, dt);
            boxNow.w = Mathf.SmoothDamp(boxNow.w, target.w, ref boxVel.w, st, 1000f, dt);

            bool has = b.width > 0.005f && b.height > 0.005f;
            Color col = Searching ? Amber : Accent;
            float w = boxNow.z - boxNow.x, hgt = boxNow.w - boxNow.y;
            float len = Mathf.Clamp(Mathf.Min(w, hgt) * 0.3f, 6f, 16f), thick = 2.5f;
            for (int c = 0; c < 4; c++)
            {
                float sx = (c & 1) == 0 ? -1f : 1f, sy = (c & 2) == 0 ? -1f : 1f;
                float px = sx < 0f ? boxNow.x : boxNow.z, py = sy < 0f ? boxNow.y : boxNow.w;
                var hb = bars[c * 2];
                var vb = bars[c * 2 + 1];
                hb.enabled = vb.enabled = has;
                hb.color = vb.color = col;
                var hr = (RectTransform)hb.transform;
                hr.sizeDelta = new Vector2(len, thick);
                hr.anchoredPosition = new Vector2(px - sx * len * 0.5f, py);
                var vr = (RectTransform)vb.transform;
                vr.sizeDelta = new Vector2(thick, len);
                vr.anchoredPosition = new Vector2(px, py - sy * len * 0.5f);
            }

            // Label above the box's top left corner, kept inside the picture.
            var lr = (RectTransform)labelPill.transform;
            Vector2 lp = new Vector2(boxNow.x + lr.sizeDelta.x * 0.5f, boxNow.w + 15f);
            lp.x = Mathf.Clamp(lp.x, -VideoW * 0.5f + lr.sizeDelta.x * 0.5f + 4f, VideoW * 0.5f - lr.sizeDelta.x * 0.5f - 4f);
            lp.y = Mathf.Clamp(lp.y, -VideoH * 0.5f + 16f, VideoH * 0.5f - 14f);
            if (!has) lp = new Vector2(0f, VideoH * 0.5f - 16f);
            labelPos = Vector2.SmoothDamp(labelPos, lp, ref labelVel, 0.05f, 2000f, dt);
            lr.anchoredPosition = labelPos;
            labelPill.enabled = label.enabled = true;
            label.color = col;
            recDot.color = new Color(1f, 0.32f, 0.3f, 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 4.5f));
        }

        void UpdateHud()
        {
            if (Time.unscaledTime < nextHud) return;
            nextHud = Time.unscaledTime + 0.2f;
            var headT = DemoLayout.Head;
            if (headT == null || drone == null) return;
            float d = Vector3.Distance(drone.Drone.position, headT.position);
            float floor = room != null ? room.FloorY : 0f;
            float alt = drone.Drone.position.y - floor;
            distText.SetText("{0:1} m away", d);
            altText.SetText("{0:1} m high", alt);
            if (Time.unscaledTime >= nextConfidence)
            {
                nextConfidence = Time.unscaledTime + 0.18f;
                Confidence = 0.95f + 0.03f * Mathf.PerlinNoise(Time.unscaledTime * 1.3f, 5.5f);
            }
            if (Searching) label.SetText("SEARCHING");
            else label.SetText("LOCKED {0:2}", Confidence);
        }

        /// <summary>Writes the current picture to a PNG (play-test screenshots). Allocates; never called per frame.</summary>
        public bool SavePicture(string path)
        {
            if (rt == null) return false;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            System.IO.File.WriteAllBytes(path, t.EncodeToPNG());
            Destroy(t);
            return true;
        }
    }
}
