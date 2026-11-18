using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>The icon glyphs <see cref="ForgeLook.Glyph"/> draws from SDF bars (no icon asset needed, crisp at any size).</summary>
public enum ForgeGlyph { Close, Minimise, Plus, Check, ChevronLeft, ChevronRight, More, Dot }

/// <summary>
/// The design system's shared pieces for any canvas (Forge panels in mm, OS windows in OS units: sizes are in the parent's
/// units). Everything here is made from <see cref="ForgeSurface"/> (one shared material, SDF corners) or from one small
/// procedural atlas made once (<see cref="ShadowSprite"/>, <see cref="DiscSprite"/>), so it batches and costs no texture
/// imports. Tokens live in <see cref="ForgeTheme"/>; the language is in docs/DESIGN_SYSTEM.md.
/// </summary>
public static class ForgeLook
{
    /// <summary>The tag the UI Set theme manager leaves alone, so a colour set here survives a theme pass.</summary>
    public const string KeepColorTag = "QDSUISharedThemeColor";

    static Texture2D atlas;
    static Sprite shadow, disc;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        atlas = null;
        shadow = disc = null;
    }

    // ---------------------------------------------------------------- surfaces

    /// <summary>A rounded SDF fill stretched over its parent (inset by <paramref name="inset"/>), ignored by layout, not a ray target.</summary>
    public static ForgeSurface Fill(Transform parent, string name, Color color, float corner, float outline = 0f, float inset = 0f)
    {
        var rt = NewRect(name, parent);
        Stretch(rt, inset);
        var element = rt.gameObject.AddComponent<LayoutElement>();
        element.ignoreLayout = true;
        var s = rt.gameObject.AddComponent<ForgeSurface>();
        s.color = color;
        s.Corner = corner;
        s.Outline = outline;
        s.raycastTarget = false;
        return s;
    }

    /// <summary>
    /// A ring of <paramref name="width"/> parent units (ForgeSurface's own outline is one unit wide: the ring is drawn at
    /// 1/width scale and scaled back up, so the band is exactly <paramref name="width"/> wide and stays an SDF).
    /// Stretched over the parent, grown by <paramref name="outset"/> each side.
    /// </summary>
    public static ForgeSurface Ring(Transform parent, string name, Color color, float corner, float width = 2f, float outset = 0f, bool pill = false)
    {
        width = Mathf.Max(0.5f, width);
        var holder = NewRect(name, parent);
        Stretch(holder, -outset);
        holder.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        var rt = NewRect("Band", holder);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.localScale = new Vector3(width, width, 1f);
        // The band's rect is the holder's size divided by width: anchored to the holder with negative offsets that shrink it.
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var fit = holder.gameObject.AddComponent<ForgeRingFit>();
        fit.band = rt;
        fit.width = width;
        var s = rt.gameObject.AddComponent<ForgeSurface>();
        // A hair of fill alpha (invisible) so a CanvasGroup fade still fades the ring (the shader reads the fade from it).
        s.color = new Color(color.r, color.g, color.b, 1f / 255f);
        s.Outline = color.a;
        s.Corner = corner / width;
        s.Pill = pill;
        s.raycastTarget = false;
        fit.Fit();
        return s;
    }

    /// <summary>A soft drop shadow behind a floating surface: one 9-sliced sprite (no blur), offset down, under the parent's other children.</summary>
    public static Image Shadow(Transform parent, float spread = 18f, float drop = 8f, float strength = 0.55f)
    {
        var rt = NewRect("Shadow", parent);
        rt.SetAsFirstSibling();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(-spread, -spread - drop);
        rt.offsetMax = new Vector2(spread, spread - drop);
        rt.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        var image = rt.gameObject.AddComponent<Image>();
        image.sprite = ShadowSprite;
        image.type = Image.Type.Sliced;
        image.pixelsPerUnitMultiplier = 48f / Mathf.Max(4f, spread * 1.4f);
        image.color = new Color(0f, 0f, 0f, strength);
        image.raycastTarget = false;
        return image;
    }

    // ---------------------------------------------------------------- glyphs, icon buttons

    /// <summary>
    /// Draws a glyph from rounded bars, centred in a <paramref name="size"/> square under <paramref name="parent"/>. Returns
    /// the holder; its ForgeSurface children are the strokes (tint them all, or pass them to <see cref="ForgeButtonLook"/>).
    /// </summary>
    public static RectTransform Glyph(Transform parent, ForgeGlyph glyph, float size, Color color)
    {
        var holder = NewRect("Glyph " + glyph, parent);
        holder.anchorMin = holder.anchorMax = holder.pivot = new Vector2(0.5f, 0.5f);
        holder.sizeDelta = new Vector2(size, size);
        holder.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        float t = Mathf.Max(1.5f, size * 0.11f);  // stroke
        float l = size * 0.86f;
        switch (glyph)
        {
            case ForgeGlyph.Close:
                Bar(holder, 0f, 0f, l, t, 45f, color);
                Bar(holder, 0f, 0f, l, t, -45f, color);
                break;
            case ForgeGlyph.Minimise:
                Bar(holder, 0f, 0f, l * 0.8f, t, 0f, color);
                break;
            case ForgeGlyph.Plus:
                Bar(holder, 0f, 0f, l * 0.85f, t, 0f, color);
                Bar(holder, 0f, 0f, l * 0.85f, t, 90f, color);
                break;
            case ForgeGlyph.Check:
                Bar(holder, -size * 0.20f, -size * 0.06f, size * 0.42f, t, -45f, color);
                Bar(holder, size * 0.10f, size * 0.02f, size * 0.70f, t, 50f, color);
                break;
            case ForgeGlyph.ChevronLeft:
            case ForgeGlyph.ChevronRight:
                float sign = glyph == ForgeGlyph.ChevronLeft ? 1f : -1f;
                Bar(holder, -sign * size * 0.06f, size * 0.17f, size * 0.52f, t, sign * 45f, color);
                Bar(holder, -sign * size * 0.06f, -size * 0.17f, size * 0.52f, t, -sign * 45f, color);
                break;
            case ForgeGlyph.More:
                for (int i = -1; i <= 1; i++) Bar(holder, i * size * 0.32f, 0f, t * 1.5f, t * 1.5f, 0f, color);
                break;
            default:
                Bar(holder, 0f, 0f, size * 0.4f, size * 0.4f, 0f, color);
                break;
        }
        return holder;
    }

    static void Bar(RectTransform holder, float x, float y, float length, float thickness, float angle, Color color)
    {
        var rt = NewRect("Stroke", holder);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(length, thickness);
        rt.localRotation = Quaternion.Euler(0f, 0f, angle);
        var s = rt.gameObject.AddComponent<ForgeSurface>();
        s.Pill = true;
        s.color = color;
        s.raycastTarget = false;
    }

    /// <summary>
    /// A round icon button: a glass plate (<see cref="ForgeButtonLook.Quiet"/> by default: no plate until reached for), the glyph
    /// in the middle, hover glow and press depth. <paramref name="size"/> is in the parent's units: on a Forge panel (mm) keep
    /// it at least 30 (the minimum target); in OS units at least 46. Returns the Button (named "Button " + name).
    /// </summary>
    public static Button IconButton(Transform parent, ForgeGlyph glyph, UnityAction onClick, float size = 34f, ForgeButtonLook.Palette? palette = null, string name = null)
    {
        var rt = NewRect("Button " + (name ?? glyph.ToString()), parent);
        rt.sizeDelta = new Vector2(size, size);
        var plate = rt.gameObject.AddComponent<ForgeSurface>();
        plate.Pill = true;
        plate.raycastTarget = true;  // the whole disc is the target, even while it is clear
        var button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = plate;
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        if (onClick != null) button.onClick.AddListener(onClick);
        RectTransform g = Glyph(rt, glyph, size * 0.40f, ForgeTheme.TextPrimary);
        var strokes = g.GetComponentsInChildren<ForgeSurface>(true);
        var look = rt.gameObject.AddComponent<ForgeButtonLook>();
        look.Bind(plate, palette ?? ForgeButtonLook.Quiet, rt, strokes);
        return button;
    }

    // ---------------------------------------------------------------- panel chrome

    /// <summary>
    /// Styles a panel's built-in window buttons (<see cref="PanelChrome"/>) as quiet glass icon buttons: a faint disc with a
    /// hairline at rest, brighter on hover (a ray or a fingertip near), Close red only while pressed, Minimise a brighter
    /// disc. Look only: sizes, placement and behaviour stay PanelChrome's. Safe to call after every chrome refresh.
    /// </summary>
    public static void StyleChrome(PanelChrome chrome)
    {
        if (chrome == null) return;
        StyleChromeButton(chrome.CloseButton, true);
        StyleChromeButton(chrome.MinimiseButton, false);
    }

    static void StyleChromeButton(PanelChromeButton button, bool close)
    {
        if (button == null) return;
        button.Rest = ForgeTheme.ChromeRest;
        button.Hover = ForgeTheme.ChromeHover;
        button.Down = close ? ForgeTheme.ChromeCloseDown : ForgeTheme.ChromeDown;
        button.MarkRest = ForgeTheme.ChromeMark;
        button.MarkHover = ForgeTheme.ChromeMarkHover;
        if (button.Disc is ForgeSurface disc)
        {
            if (!Mathf.Approximately(disc.Outline, ForgeTheme.ChromeRing)) disc.Outline = ForgeTheme.ChromeRing;
            if (!Mathf.Approximately(disc.Glow, 0f)) disc.Glow = 0f;
        }
        button.Restyle();
    }

    // ---------------------------------------------------------------- text

    /// <summary>Gives a label a colour the UI Set theme pass won't overwrite.</summary>
    public static void Tint(TMP_Text text, Color color)
    {
        if (text == null) return;
        text.color = color;
        text.gameObject.tag = KeepColorTag;
    }

    /// <summary>The panel title bar's type: a bold title in full white with slightly tight tracking, and a secondary subtitle (72%, 5.4:1).</summary>
    public static void StyleHeader(TMP_Text title, TMP_Text subtitle)
    {
        if (title != null)
        {
            title.color = ForgeTheme.TextPrimary;
            ForgeTheme.Strong(title, -0.3f);  // semibold material, not fake bold (which smears on the static Inter atlas)
            title.overflowMode = TextOverflowModes.Ellipsis;
        }
        if (subtitle != null)
        {
            subtitle.color = ForgeTheme.TextSecondary;
            subtitle.overflowMode = TextOverflowModes.Ellipsis;
        }
    }

    // ---------------------------------------------------------------- the procedural atlas

    /// <summary>A soft rounded shadow, 9-sliced (48 px borders), from the shared 192 x 128 atlas made once.</summary>
    public static Sprite ShadowSprite
    {
        get
        {
            if (shadow == null) BuildAtlas();
            return shadow;
        }
    }

    /// <summary>A crisp white disc (64 px), from the same atlas: dots, nodes, status lights where a ForgeSurface can't go.</summary>
    public static Sprite DiscSprite
    {
        get
        {
            if (disc == null) BuildAtlas();
            return disc;
        }
    }

    static void BuildAtlas()
    {
        const int w = 192, h = 128;
        atlas = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "ForgeLookAtlas", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var pixels = new Color32[w * h];
        // Shadow, 0..128: a 32 px rounded core whose alpha falls off smoothly over 44 px (a Gaussian-like 1 - smoothstep).
        for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            float d = RoundBox(x + 0.5f - 64f, y + 0.5f - 64f, 16f, 16f, 12f);
            float a = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((d + 4f) / 44f));
            pixels[y * w + x] = new Color32(255, 255, 255, (byte)(a * a * 255f));
        }
        // Disc, 128..192 x 0..64.
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            float d = new Vector2(x + 0.5f - 32f, y + 0.5f - 32f).magnitude - 30f;
            float a = Mathf.Clamp01(0.5f - d);
            pixels[y * w + 128 + x] = new Color32(255, 255, 255, (byte)(a * 255f));
        }
        atlas.SetPixels32(pixels);
        atlas.Apply(false, true);
        shadow = Sprite.Create(atlas, new Rect(0, 0, 128, 128), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(48, 48, 48, 48));
        shadow.name = "ForgeShadow";
        shadow.hideFlags = HideFlags.HideAndDontSave;
        disc = Sprite.Create(atlas, new Rect(128, 0, 64, 64), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        disc.name = "ForgeDisc";
        disc.hideFlags = HideFlags.HideAndDontSave;
    }

    static float RoundBox(float px, float py, float hx, float hy, float r)
    {
        float qx = Mathf.Abs(px) - hx + r, qy = Mathf.Abs(py) - hy + r;
        return new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
    }

    // ---------------------------------------------------------------- rects

    public static RectTransform NewRect(string name, Transform parent)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        return rt;
    }

    public static void Stretch(RectTransform rt, float inset = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }
}
