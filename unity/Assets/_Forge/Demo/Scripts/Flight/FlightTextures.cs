using UnityEngine;

namespace Forge.Demo
{
    /// <summary>Small procedural textures and sprites for the flight station, made once on first use (no art files to lose).</summary>
    public static class FlightTextures
    {
        static Texture2D softDot, ring;
        static Sprite rounded, roundedSmall, white;

        /// <summary>A white dot that fades smoothly to nothing at the rim.</summary>
        public static Texture2D SoftDot()
        {
            if (softDot != null) return softDot;
            const int n = 64;
            var t = New(n, n, "FlightSoftDot");
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n * 0.5f, n * 0.5f)) / (n * 0.5f);
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a * (3f - 2f * a);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return softDot = t;
        }

        /// <summary>A thin bright ring with a soft glow inside it.</summary>
        public static Texture2D Ring()
        {
            if (ring != null) return ring;
            const int n = 128;
            var t = New(n, n, "FlightRing");
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n * 0.5f, n * 0.5f)) / (n * 0.5f);
                    float line = Mathf.Exp(-Mathf.Pow((d - 0.9f) / 0.035f, 2f));
                    float glow = d < 0.9f ? 0.2f * Mathf.Pow(d / 0.9f, 3f) : 0f;
                    float a = Mathf.Clamp01(line + glow) * (d < 1f ? 1f : 0f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return ring = t;
        }

        /// <summary>A 9-slice rounded rectangle, white, anti-aliased. Use as an Image (sliced) at pixelsPerUnit 1: the corner radius is then in canvas units (mm).</summary>
        public static Sprite Rounded(bool small)
        {
            if (small ? roundedSmall != null : rounded != null) return small ? roundedSmall : rounded;
            int r = small ? 14 : 24, n = r * 2 + 4;
            var t = New(n, n, small ? "FlightRoundedSmall" : "FlightRounded");
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // signed distance to a rounded box filling the texture
                    float cx = Mathf.Abs(x + 0.5f - n * 0.5f) - (n * 0.5f - r);
                    float cy = Mathf.Abs(y + 0.5f - n * 0.5f) - (n * 0.5f - r);
                    float d = Mathf.Sqrt(Mathf.Max(cx, 0f) * Mathf.Max(cx, 0f) + Mathf.Max(cy, 0f) * Mathf.Max(cy, 0f)) + Mathf.Min(Mathf.Max(cx, cy), 0f) - r;
                    float a = Mathf.Clamp01(0.5f - d);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            t.SetPixels32(px);
            t.filterMode = FilterMode.Bilinear;
            t.Apply(false, true);
            var s = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect, new Vector4(r + 1, r + 1, r + 1, r + 1));
            s.name = t.name;
            if (small) roundedSmall = s; else rounded = s;
            return s;
        }

        /// <summary>A corner cover: white outside a quarter circle of radius r (bottom left corner), clear inside it. Flip it for the other corners.</summary>
        public static Sprite InverseCorner(int r)
        {
            var t = New(r, r, "FlightCorner");
            var px = new Color32[r * r];
            for (int y = 0; y < r; y++)
                for (int x = 0; x < r; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                    px[y * r + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(d - r + 0.5f) * 255f));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return Sprite.Create(t, new Rect(0, 0, r, r), Vector2.zero, 1f);
        }

        /// <summary>A plain white square sprite, for solid bars and dots.</summary>
        public static Sprite White()
        {
            if (white != null) return white;
            var t = New(4, 4, "FlightWhite");
            var px = new Color32[16];
            for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
            t.SetPixels32(px);
            t.Apply(false, true);
            white = Sprite.Create(t, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            return white;
        }

        /// <summary>A round dot (REC light).</summary>
        public static Sprite Dot()
        {
            const int n = 32;
            var t = New(n, n, "FlightDot");
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n * 0.5f, n * 0.5f));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(n * 0.5f - d) * 255f));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 1f);
        }

        static Texture2D New(int w, int h, string name)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            return t;
        }
    }

    /// <summary>The drone's two sounds, made in code: a low motor hum that loops, and a small click. Quiet by design.</summary>
    public static class FlightSound
    {
        const int Rate = 24000;

        public static AudioClip Hum()
        {
            // One second, so each partial has a whole number of cycles and the loop is seamless.
            var data = new float[Rate];
            for (int i = 0; i < data.Length; i++)
            {
                float t = i / (float)Rate;
                float w = Mathf.Sin(2f * Mathf.PI * 96f * t) * 0.5f + Mathf.Sin(2f * Mathf.PI * 192f * t) * 0.28f + Mathf.Sin(2f * Mathf.PI * 288f * t) * 0.14f
                          + Mathf.Sin(2f * Mathf.PI * 384f * t) * 0.07f;
                float am = 1f + 0.25f * Mathf.Sin(2f * Mathf.PI * 24f * t);
                data[i] = w * am * 0.5f;
            }
            var c = AudioClip.Create("DroneHum", data.Length, 1, Rate, false);
            c.SetData(data, 0);
            return c;
        }

        public static AudioClip Click()
        {
            int n = Rate / 12;
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float env = Mathf.Exp(-t * 90f);
                data[i] = (Mathf.Sin(2f * Mathf.PI * 1900f * t) * 0.6f + Mathf.Sin(2f * Mathf.PI * 3100f * t) * 0.3f) * env;
            }
            var c = AudioClip.Create("DroneClick", n, 1, Rate, false);
            c.SetData(data, 0);
            return c;
        }
    }
}
