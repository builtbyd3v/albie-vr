using System.Collections.Generic;
using Forge.OS;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.Demo
{
    /// <summary>
    /// Small drawing helpers for the Research station: positioned text, rounded fills (<see cref="ForgeSurface"/>), pictures
    /// with rounded corners, check and cross glyphs drawn from bars (no font dependence) and neutral placeholder pictures
    /// until the demo-content station's renders exist. Everything is placed from the top left of its parent in canvas units
    /// (1 unit = 1 mm on a ForgePanel). Setup code only: nothing here runs per frame.
    /// </summary>
    public static class ResearchUi
    {
        // One accent (the ML blue the OS already uses); green and red are status only.
        public static readonly Color Accent = ForgeTheme.ML;
        public static readonly Color Pass = new Color32(0x4B, 0xE3, 0x8A, 255);
        public static readonly Color PassText = new Color32(0x86, 0xF0, 0xB2, 255);
        public static readonly Color Fail = new Color32(0xFF, 0x6B, 0x6B, 255);
        public static readonly Color FailText = new Color32(0xFF, 0x9A, 0x9A, 255);
        public static readonly Color Amber = new Color32(0xFF, 0xC0, 0x4D, 255);
        public static readonly Color Ink = new Color(1f, 1f, 1f, 0.96f);
        public static readonly Color Sub = new Color(1f, 1f, 1f, 0.70f);
        public static readonly Color CardFill = new Color(1f, 1f, 1f, 0.075f);
        public static readonly Color ChipFill = new Color(1f, 1f, 1f, 0.10f);
        public static readonly Color PendingFill = new Color(1f, 1f, 1f, 0.07f);
        public static readonly Color PendingText = new Color(1f, 1f, 1f, 0.42f);

        // ------------------------------------------------------------------ font

        static TMP_FontAsset font;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            font = null;
            filled.Clear();
            placeholders.Clear();
        }

        /// <summary>The font the OS uses (its theme's medium text font), else the Forge font.</summary>
        public static TMP_FontAsset Typeface
        {
            get
            {
                if (font != null) return font;
                OSKit kit = OSKit.Current;
                if (kit != null && kit.theme != null && kit.theme.textFontMedium != null) font = kit.theme.textFontMedium;
                else font = ForgeTheme.Font;
                return font;
            }
        }

        // ------------------------------------------------------------------ layout

        /// <summary>A rect anchored to the parent's top left, placed x to the right and y down, w by h.</summary>
        public static RectTransform Box(Transform parent, string name, float x, float y, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        public static void Place(RectTransform rt, float x, float y)
        {
            rt.anchoredPosition = new Vector2(x, -y);
        }

        /// <summary>A rect that fills the panel's Content area. Hides the prefab's placeholder text.</summary>
        public static RectTransform Body(RectTransform content)
        {
            var placeholder = content.Find("Placeholder");
            if (placeholder != null) placeholder.gameObject.SetActive(false);
            var go = new GameObject("Body", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(content, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        // ------------------------------------------------------------------ text

        public static TMP_Text Label(Transform parent, string text, float size, Color color, float x, float y, float w, float h,
            FontStyles style = FontStyles.Normal, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
        {
            RectTransform rt = Box(parent, "Text", x, y, w, h);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (Typeface != null) t.font = Typeface;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.alignment = align;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            t.text = text ?? "";
            return t;
        }

        // ------------------------------------------------------------------ surfaces

        public static ForgeSurface Fill(Transform parent, string name, float x, float y, float w, float h, Color color, float corner, bool pill = false)
        {
            RectTransform rt = Box(parent, name, x, y, w, h);
            var s = rt.gameObject.AddComponent<ForgeSurface>();
            s.Style = ForgeSurface.Kind.Fill;
            s.Corner = corner;
            s.Pill = pill;
            s.color = color;
            s.raycastTarget = false;
            return s;
        }

        public static CanvasGroup Group(GameObject go, float alpha)
        {
            var g = go.GetComponent<CanvasGroup>();
            if (g == null) g = go.AddComponent<CanvasGroup>();
            g.alpha = alpha;
            g.blocksRaycasts = false;
            return g;
        }

        // ------------------------------------------------------------------ pictures

        static readonly Dictionary<int, Sprite> filled = new Dictionary<int, Sprite>();
        const int SpriteSize = 64, SpriteRadius = 24;

        static Sprite RoundedSprite()
        {
            if (filled.TryGetValue(SpriteRadius, out Sprite cached) && cached != null) return cached;
            var texture = new Texture2D(SpriteSize, SpriteSize, TextureFormat.RGBA32, false) { name = "ResearchRounded", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[SpriteSize * SpriteSize];
            float half = SpriteSize * 0.5f;
            for (int y = 0; y < SpriteSize; y++)
            {
                for (int x = 0; x < SpriteSize; x++)
                {
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - SpriteRadius);
                    float qy = Mathf.Abs(y + 0.5f - half) - (half - SpriteRadius);
                    float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - SpriteRadius;
                    pixels[y * SpriteSize + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(0.5f - outside) * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, SpriteSize, SpriteSize), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(SpriteRadius, SpriteRadius, SpriteRadius, SpriteRadius));
            sprite.name = "ResearchRounded";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            filled[SpriteRadius] = sprite;
            return sprite;
        }

        /// <summary>Sets a sliced image so its corners come out <paramref name="radiusMm"/> large, whatever the canvas's pixels per unit.</summary>
        public static void SetCorner(Image image, float radiusMm, int borderPixels)
        {
            Canvas canvas = image.canvas;
            float reference = canvas != null ? canvas.referencePixelsPerUnit : 100f;
            float spritePpu = image.sprite != null ? image.sprite.pixelsPerUnit : 100f;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = borderPixels / Mathf.Max(0.5f, radiusMm * spritePpu / reference);
        }

        /// <summary>A picture with rounded corners at the given place. The texture comes from <see cref="Picture"/>.</summary>
        public static RawImage RoundedPicture(Transform parent, Texture texture, float x, float y, float w, float h, float radiusMm)
        {
            RectTransform holder = Box(parent, "Picture", x, y, w, h);
            var mask = holder.gameObject.AddComponent<Image>();
            mask.sprite = RoundedSprite();
            mask.raycastTarget = false;
            SetCorner(mask, radiusMm, SpriteRadius);
            holder.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            var childGo = new GameObject("Image", typeof(RectTransform));
            var child = (RectTransform)childGo.transform;
            child.SetParent(holder, false);
            child.anchorMin = Vector2.zero;
            child.anchorMax = Vector2.one;
            child.offsetMin = child.offsetMax = Vector2.zero;
            var raw = childGo.AddComponent<RawImage>();
            raw.texture = texture;
            raw.raycastTarget = false;
            return raw;
        }

        static readonly Dictionary<string, Texture2D> placeholders = new Dictionary<string, Texture2D>();

        /// <summary>
        /// The picture for an image id ("cam_a", "model_a"): Resources/DemoImages/{id} when the demo-content station has
        /// rendered it, otherwise a neutral drawn placeholder (a dark tile with a lens for cameras, three linked dots for models).
        /// </summary>
        public static Texture Picture(string id)
        {
            if (string.IsNullOrEmpty(id)) id = "none";
            Texture2D real = Resources.Load<Texture2D>("DemoImages/" + id);
            if (real != null) return real;
            if (placeholders.TryGetValue(id, out Texture2D cached) && cached != null) return cached;
            Texture2D tex = MakePlaceholder(id);
            placeholders[id] = tex;
            return tex;
        }

        static Texture2D MakePlaceholder(string id)
        {
            const int n = 96;
            bool model = id.StartsWith("model", System.StringComparison.Ordinal);
            int seed = 0;
            for (int i = 0; i < id.Length; i++) seed = seed * 31 + id[i];
            float shift = (seed & 7) / 7f;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "ResearchPlaceholder " + id, hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            var top = Color.Lerp(new Color(0.22f, 0.24f, 0.29f), new Color(0.20f, 0.27f, 0.36f), shift);
            var bottom = new Color(0.10f, 0.11f, 0.14f);
            var line = new Color(0.62f, 0.68f, 0.78f);
            for (int y = 0; y < n; y++)
            {
                Color row = Color.Lerp(bottom, top, y / (float)(n - 1));
                for (int x = 0; x < n; x++)
                {
                    Color c = row;
                    float dx = x + 0.5f - n * 0.5f, dy = y + 0.5f - n * 0.5f;
                    if (!model)
                    {
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        c = Blend(c, line, Ring(r, 26f, 3f) * 0.9f);
                        c = Blend(c, new Color(0.05f, 0.06f, 0.08f), Mathf.Clamp01(21f - r) * 0.9f);
                        c = Blend(c, line, Mathf.Clamp01(9f - r) * 0.55f);
                    }
                    else
                    {
                        c = Blend(c, line, Dot(dx, dy, -24f, -14f, 9f) * 0.85f);
                        c = Blend(c, line, Dot(dx, dy, 22f, -22f, 9f) * 0.85f);
                        c = Blend(c, line, Dot(dx, dy, 4f, 24f, 11f) * 0.95f);
                        c = Blend(c, line, Segment(dx, dy, -24f, -14f, 22f, -22f, 1.6f) * 0.6f);
                        c = Blend(c, line, Segment(dx, dy, -24f, -14f, 4f, 24f, 1.6f) * 0.6f);
                        c = Blend(c, line, Segment(dx, dy, 22f, -22f, 4f, 24f, 1.6f) * 0.6f);
                    }
                    px[y * n + x] = c;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        static Color Blend(Color under, Color over, float a) => Color.Lerp(under, over, Mathf.Clamp01(a));
        static float Ring(float r, float radius, float width) => Mathf.Clamp01(width * 0.5f + 0.5f - Mathf.Abs(r - radius));
        static float Dot(float dx, float dy, float cx, float cy, float radius) => Mathf.Clamp01(radius - Mathf.Sqrt((dx - cx) * (dx - cx) + (dy - cy) * (dy - cy)) + 0.5f);

        static float Segment(float px, float py, float ax, float ay, float bx, float by, float width)
        {
            float abx = bx - ax, aby = by - ay;
            float t = Mathf.Clamp01(((px - ax) * abx + (py - ay) * aby) / (abx * abx + aby * aby));
            float cx = ax + abx * t, cy = ay + aby * t;
            float d = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            return Mathf.Clamp01(width * 0.5f + 0.5f - d);
        }

        // ------------------------------------------------------------------ glyphs

        /// <summary>A check mark drawn from two rounded bars, centred in a size by size box at (cx, cy) in the parent's top-left space.</summary>
        public static RectTransform Check(Transform parent, float cx, float cy, float size, Color color)
        {
            RectTransform root = Box(parent, "Check", cx - size * 0.5f, cy - size * 0.5f, size, size);
            float t = size * 0.17f;
            Bar(root, size * 0.5f - size * 0.20f, size * 0.5f + size * 0.07f, size * 0.36f, t, -45f, color);
            Bar(root, size * 0.5f + size * 0.14f, size * 0.5f + size * 0.00f, size * 0.64f, t, 45f, color);
            return root;
        }

        /// <summary>A cross drawn from two rounded bars.</summary>
        public static RectTransform Cross(Transform parent, float cx, float cy, float size, Color color)
        {
            RectTransform root = Box(parent, "Cross", cx - size * 0.5f, cy - size * 0.5f, size, size);
            float t = size * 0.17f;
            Bar(root, size * 0.5f, size * 0.5f, size * 0.62f, t, 45f, color);
            Bar(root, size * 0.5f, size * 0.5f, size * 0.62f, t, -45f, color);
            return root;
        }

        static void Bar(RectTransform root, float cx, float cy, float length, float thickness, float angle, Color color)
        {
            ForgeSurface bar = Fill(root, "Bar", cx - length * 0.5f, cy - thickness * 0.5f, length, thickness, color, thickness * 0.5f, true);
            bar.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            bar.rectTransform.anchoredPosition = new Vector2(cx, -cy);
            bar.rectTransform.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        /// <summary>Recolours every bar under a glyph made by <see cref="Check"/> or <see cref="Cross"/>.</summary>
        public static void Tint(RectTransform glyph, Color color)
        {
            if (glyph == null) return;
            for (int i = 0; i < glyph.childCount; i++)
            {
                var s = glyph.GetChild(i).GetComponent<ForgeSurface>();
                if (s != null) s.color = color;
            }
        }
    }
}
