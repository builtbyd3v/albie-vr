using UnityEngine;
using UnityEngine.UI;

namespace Forge.Demo
{
    /// <summary>Small mesh helpers shared by the pipeline panel's drawn graphics. None of them allocates once the canvas pools are warm.</summary>
    static class PipelineMesh
    {
        public static void Quad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color32 col)
        {
            int i = vh.currentVertCount;
            vh.AddVert(a, col, Vector2.zero);
            vh.AddVert(b, col, Vector2.zero);
            vh.AddVert(c, col, Vector2.zero);
            vh.AddVert(d, col, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i, i + 2, i + 3);
        }

        public static void Rect(VertexHelper vh, float x0, float y0, float x1, float y1, Color32 col) =>
            Quad(vh, new Vector2(x0, y0), new Vector2(x0, y1), new Vector2(x1, y1), new Vector2(x1, y0), col);

        public static void Segment(VertexHelper vh, Vector2 p, Vector2 q, float width, Color32 col)
        {
            Vector2 d = q - p;
            float len = d.magnitude;
            if (len < 1e-4f) return;
            Vector2 n = new Vector2(-d.y, d.x) * (width * 0.5f / len);
            Quad(vh, p - n, p + n, q + n, q - n, col);
        }

        public static void RectOutline(VertexHelper vh, float x0, float y0, float x1, float y1, float t, Color32 col)
        {
            Rect(vh, x0, y0, x1, y0 + t, col);
            Rect(vh, x0, y1 - t, x1, y1, col);
            Rect(vh, x0, y0 + t, x0 + t, y1 - t, col);
            Rect(vh, x1 - t, y0 + t, x1, y1 - t, col);
        }

        /// <summary>A filled ellipse; any vertex below <paramref name="floorY"/> is pulled up to it (a shape cut off by the bottom of a frame).</summary>
        public static void Ellipse(VertexHelper vh, Vector2 c, float rx, float ry, Color32 col, int segments, float floorY = float.NegativeInfinity)
        {
            int centre = vh.currentVertCount;
            vh.AddVert(new Vector2(c.x, Mathf.Max(c.y, floorY)), col, Vector2.zero);
            for (int i = 0; i <= segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                vh.AddVert(new Vector2(c.x + Mathf.Cos(a) * rx, Mathf.Max(c.y + Mathf.Sin(a) * ry, floorY)), col, Vector2.zero);
            }
            for (int i = 1; i <= segments; i++) vh.AddTriangle(centre, centre + i, centre + i + 1);
        }

        /// <summary>A rounded rectangle, a fan from its centre, shaded from <paramref name="bottom"/> to <paramref name="top"/>.</summary>
        public static void RoundedRect(VertexHelper vh, float x0, float y0, float x1, float y1, float radius, Color32 bottom, Color32 top)
        {
            radius = Mathf.Min(radius, (x1 - x0) * 0.5f, (y1 - y0) * 0.5f);
            const int arc = 6;
            int centre = vh.currentVertCount;
            vh.AddVert(new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f), Color32.Lerp(bottom, top, 0.5f), Vector2.zero);
            int ring = 0;
            for (int corner = 0; corner < 4; corner++)
            {
                // Bottom-right, top-right, top-left, bottom-left.
                float cx = corner == 0 || corner == 1 ? x1 - radius : x0 + radius;
                float cy = corner == 0 || corner == 3 ? y0 + radius : y1 - radius;
                float start = corner * Mathf.PI * 0.5f - Mathf.PI * 0.5f;
                for (int i = 0; i <= arc; i++)
                {
                    float a = start + i * Mathf.PI * 0.5f / arc;
                    float x = cx + Mathf.Cos(a) * radius, y = cy + Mathf.Sin(a) * radius;
                    vh.AddVert(new Vector2(x, y), Color32.Lerp(bottom, top, Mathf.InverseLerp(y0, y1, y)), Vector2.zero);
                    ring++;
                }
            }
            for (int i = 0; i < ring; i++) vh.AddTriangle(centre, centre + 1 + i, centre + 1 + (i + 1) % ring);
        }
    }

    /// <summary>
    /// A live line chart in the panel's canvas: one or two curves that draw left to right up to a fractional epoch, so the line grows
    /// smoothly between epochs. The data arrays are set once; <see cref="SetProgress"/> only marks the mesh dirty.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class PipelineChart : MaskableGraphic
    {
        public float[] seriesA, seriesB;
        public Color colorA = Color.white, colorB = new Color(1f, 1f, 1f, 0.45f);
        public float yMin, yMax = 1f;
        public bool fillA = true;
        public float lineWidth = 3.4f;

        float shown;

        /// <summary>Epochs drawn so far (0 to the epoch count), fractional.</summary>
        public void SetProgress(float epochsDone)
        {
            if (Mathf.Abs(epochsDone - shown) < 0.0005f) return;
            shown = epochsDone;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            for (int g = 0; g <= 4; g++)
            {
                float y = r.yMin + r.height * g / 4f;
                PipelineMesh.Rect(vh, r.xMin, y - 0.5f, r.xMax, y + 0.5f, new Color(1f, 1f, 1f, g == 0 ? 0.20f : 0.07f));
            }
            Series(vh, r, seriesB, colorB, false);
            Series(vh, r, seriesA, colorA, fillA);
        }

        Vector2 Point(float[] data, int k, int whole, float s, float frac, Rect r)
        {
            float x, v;
            if (k <= whole) { x = k; v = data[k]; }
            else { x = s; v = Mathf.Lerp(data[whole], data[Mathf.Min(whole + 1, data.Length - 1)], frac); }
            return new Vector2(r.xMin + x / (data.Length - 1) * r.width,
                r.yMin + Mathf.Clamp01(Mathf.InverseLerp(yMin, yMax, v)) * r.height);
        }

        void Series(VertexHelper vh, Rect r, float[] data, Color color, bool fill)
        {
            if (data == null || data.Length < 2 || shown < 1f) return;
            float s = Mathf.Clamp(shown - 1f, 0f, data.Length - 1);
            int whole = Mathf.Min(Mathf.FloorToInt(s), data.Length - 1);
            float frac = s - whole;
            int count = whole + 1 + (frac > 0.001f && whole < data.Length - 1 ? 1 : 0);
            Vector2 last = Point(data, count - 1, whole, s, frac, r);
            if (count >= 2)
            {
                if (fill)
                {
                    Color top = new Color(color.r, color.g, color.b, 0.20f), bottom = new Color(color.r, color.g, color.b, 0f);
                    int b = vh.currentVertCount;
                    for (int k = 0; k < count; k++)
                    {
                        Vector2 p = Point(data, k, whole, s, frac, r);
                        vh.AddVert(p, top, Vector2.zero);
                        vh.AddVert(new Vector2(p.x, r.yMin), bottom, Vector2.zero);
                        if (k > 0)
                        {
                            int i = b + (k - 1) * 2;
                            vh.AddTriangle(i, i + 1, i + 2);
                            vh.AddTriangle(i + 1, i + 3, i + 2);
                        }
                    }
                }
                int start = vh.currentVertCount;
                float half = lineWidth * 0.5f;
                for (int k = 0; k < count; k++)
                {
                    Vector2 p = Point(data, k, whole, s, frac, r);
                    Vector2 d1 = k > 0 ? p - Point(data, k - 1, whole, s, frac, r) : Vector2.zero;
                    Vector2 d2 = k < count - 1 ? Point(data, k + 1, whole, s, frac, r) - p : Vector2.zero;
                    if (d1.sqrMagnitude < 1e-6f) d1 = d2;
                    if (d2.sqrMagnitude < 1e-6f) d2 = d1;
                    d1.Normalize();
                    d2.Normalize();
                    Vector2 n1 = new Vector2(-d1.y, d1.x), n2 = new Vector2(-d2.y, d2.x);
                    Vector2 n = n1 + n2;
                    n = n.sqrMagnitude < 1e-6f ? n1 : n.normalized;
                    float miter = half / Mathf.Max(0.55f, Vector2.Dot(n, n1));
                    vh.AddVert(p + n * miter, color, Vector2.zero);
                    vh.AddVert(p - n * miter, color, Vector2.zero);
                    if (k > 0)
                    {
                        int i = start + (k - 1) * 2;
                        vh.AddTriangle(i, i + 1, i + 2);
                        vh.AddTriangle(i + 1, i + 3, i + 2);
                    }
                }
            }
            PipelineMesh.Ellipse(vh, last, 10f, 10f, new Color(color.r, color.g, color.b, 0.22f), 14);
            PipelineMesh.Ellipse(vh, last, 4.6f, 4.6f, color, 12);
        }
    }

    /// <summary>
    /// One sample frame: a made-up headset-camera still (a shaded backdrop and an abstract head-and-shoulders), with the model's
    /// tracking box drawn over it. <see cref="SetLearn"/> (0 to 1) tightens the box onto the face, steadies it and lifts the confidence;
    /// a wrong detection on the background fades away as it goes.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class PipelineSampleFrame : MaskableGraphic
    {
        public Color top = new Color(0.2f, 0.25f, 0.3f), bottom = new Color(0.1f, 0.12f, 0.16f), subject = new Color(0.5f, 0.55f, 0.6f);
        public Vector3 head = new Vector3(0.5f, 0.4f, 1f);
        public float[] truth = { 0.3f, 0.12f, 0.4f, 0.46f }, start = { 0.05f, 0.05f, 0.9f, 0.8f }, falseBox;
        public float conf0 = 0.35f, conf1 = 0.97f;
        public Color accent = new Color(0.30f, 0.55f, 1f);
        public float seed;

        float learn;

        public float Confidence => Mathf.Lerp(conf0, conf1, Settle(learn));

        static float Settle(float t) { t = Mathf.Clamp01(t / 0.85f); return t * t * (3f - 2f * t); }

        public void SetLearn(float value)
        {
            value = Mathf.Clamp01(value);
            if (Mathf.Abs(value - learn) < 0.0005f) return;
            learn = value;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            float w = r.width, h = r.height;
            PipelineMesh.RoundedRect(vh, r.xMin, r.yMin, r.xMax, r.yMax, 16f, bottom, top);

            // A faint horizon line and a window-ish block, so the frame reads as a room and not a gradient.
            PipelineMesh.Rect(vh, r.xMin + 8f, r.yMin + h * 0.30f, r.xMax - 8f, r.yMin + h * 0.30f + 1.2f, new Color(1f, 1f, 1f, 0.06f));
            var dark = new Color(subject.r * 0.55f, subject.g * 0.55f, subject.b * 0.55f, 1f);
            Vector2 centre = new Vector2(r.xMin + head.x * w, r.yMax - head.y * h);
            float s = head.z;
            PipelineMesh.Ellipse(vh, centre + new Vector2(0f, -h * 0.40f * s), w * 0.28f * s, h * 0.22f * s, dark, 20, r.yMin + 6f);
            PipelineMesh.Ellipse(vh, centre, w * 0.105f * s, h * 0.15f * s, subject, 22);
            PipelineMesh.Ellipse(vh, centre + new Vector2(0f, -h * 0.18f * s), w * 0.04f * s, h * 0.06f * s, dark, 10);

            float e = Settle(learn);
            if (falseBox != null && falseBox.Length >= 4)
            {
                float fade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.12f, 0.55f, learn));
                if (fade > 0.02f) Box(vh, r, falseBox[0], falseBox[1], falseBox[2], falseBox[3], 2f, new Color(1f, 1f, 1f, 0.5f * fade));
            }
            float jitter = (1f - e) * 0.045f;
            float wob = learn * 61f + seed * 13f;
            float x = Mathf.Lerp(start[0], truth[0], e) + Mathf.Sin(wob) * jitter;
            float y = Mathf.Lerp(start[1], truth[1], e) + Mathf.Cos(wob * 1.3f) * jitter;
            float bw = Mathf.Lerp(start[2], truth[2], e) + Mathf.Sin(wob * 0.7f + 1f) * jitter;
            float bh = Mathf.Lerp(start[3], truth[3], e) + Mathf.Cos(wob * 0.9f + 2f) * jitter;
            Box(vh, r, x, y, bw, bh, 2.8f, accent);
        }

        // A box given as fractions of the frame (y from the top), kept inside the frame.
        static void Box(VertexHelper vh, Rect r, float fx, float fy, float fw, float fh, float t, Color32 col)
        {
            float x0 = r.xMin + Mathf.Clamp01(fx) * r.width, x1 = r.xMin + Mathf.Clamp01(fx + fw) * r.width;
            float y1 = r.yMax - Mathf.Clamp01(fy) * r.height, y0 = r.yMax - Mathf.Clamp01(fy + fh) * r.height;
            if (x1 - x0 < t * 3f || y1 - y0 < t * 3f) return;
            PipelineMesh.RectOutline(vh, x0, y0, x1, y1, t, col);
        }
    }

    /// <summary>A padlock, drawn: a rounded body and a shackle arc. It stays in the graphic's rect and colour.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class PipelineLock : MaskableGraphic
    {
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            float w = r.width, h = r.height;
            float bodyTop = r.yMin + h * 0.58f;
            PipelineMesh.RoundedRect(vh, r.xMin + w * 0.12f, r.yMin, r.xMax - w * 0.12f, bodyTop, w * 0.14f, color, color);
            float cx = r.center.x, outer = w * 0.30f, inner = w * 0.18f, baseY = bodyTop - 1f;
            const int segs = 12;
            int b = vh.currentVertCount;
            for (int i = 0; i <= segs; i++)
            {
                float a = Mathf.PI * i / segs;
                float cos = Mathf.Cos(a), sin = Mathf.Sin(a);
                vh.AddVert(new Vector2(cx + cos * outer, baseY + h * 0.06f + sin * outer * 1.25f), color, Vector2.zero);
                vh.AddVert(new Vector2(cx + cos * inner, baseY + h * 0.06f + sin * inner * 1.25f), color, Vector2.zero);
                if (i > 0)
                {
                    int k = b + (i - 1) * 2;
                    vh.AddTriangle(k, k + 1, k + 2);
                    vh.AddTriangle(k + 1, k + 3, k + 2);
                }
            }
            // The legs of the shackle down into the body.
            PipelineMesh.Rect(vh, cx + inner, baseY - h * 0.08f, cx + outer, baseY + h * 0.06f, color);
            PipelineMesh.Rect(vh, cx - outer, baseY - h * 0.08f, cx - inner, baseY + h * 0.06f, color);
        }
    }
}
