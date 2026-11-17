using TMPro;
using UnityEngine;

/// <summary>
/// The Messages panels' look ("Graphite + Albie") in one place, to tune after a headset test: today's graphite
/// ForgePanel, Fredoka text, lime and orange accents and Albie's stickers. Rules:
/// - A white sticker with an ink border and a lime hard shadow means Albie is talking (his summary line, his draft).
/// - A lime sticker with an ink border and an ink hard shadow marks a mention of the user.
/// - Orange fills only the primary action (Send); it sits close to the CAD hue (#FF8A3D), so it is never a mark or text.
/// - Hard offset shadows, no blur. ForgeSurface only draws a 1 mm outline in its own colour, so a sticker is three
///   flat fills: shadow (offset), border, fill (inset); see <see cref="ChatUi.Sticker"/>.
/// Sizes are canvas mm (1 unit = 1 mm). Colours are opaque unless noted.
/// </summary>
public static class MessagesLook
{
    // ---- Brand colours ----
    public static readonly Color Lime = Hex(0xC8DC5A);         // Albie and mention marks
    public static readonly Color Orange = Hex(0xFA4616);       // the primary action fill (Send) only
    public static readonly Color OrangeHover = Hex(0xFF6A3D);
    public static readonly Color Ink = Hex(0x12172B);          // text on stickers, lime and orange; sticker borders
    public static readonly Color InkSoft = Hex(0x4B4F60);      // secondary text on a white sticker
    public static readonly Color StickerWhite = Color.white;

    // The same, as TMP rich-text hex (ChatText marks the user's own @mentions with them).
    public const string MentionInkHex = "#12172B", MentionMarkHex = "#C8DC5A";

    // ---- Text on the graphite panel ----
    public static readonly Color Text = new Color(1f, 1f, 1f, 0.95f);
    public static readonly Color Subtext = new Color(1f, 1f, 1f, 0.62f);  // ForgeTheme's is 50%; raised here only

    // ---- Type scale (mm). Body text is at least 14 mm (about 0.8 degrees tall at 1 m). ----
    public const float Title = 17f, Name = 14f, Body = 14f, Small = 12f, Overline = 11f, Time = 11f, Pill = 13f;
    public const float RowName = 14f, RowBody = 14f, RowTime = 11f;  // ChatRow (the conversation's messages)

    // ---- Stickers (mm) ----
    public const float StickerCorner = 16f, StickerBorder = 3f, StickerShadow = 6f, StickerPad = 10f;
    public const float TagCorner = 7f, TagBorder = 1.5f, TagShadow = 2.5f, TagH = 20f, TagText = 11f;  // the mention tag
    public const string MentionTagText = "@you";

    // ---- Targets (mm): fingertip sized ----
    public const float ButtonH = 36f, ButtonText = 14f;

    // ---- Font: Fredoka (SIL OFL 1.1), the Pet module's dynamic TMP asset from Resources/PetFonts ----

    /// <summary>
    /// Fredoka at runtime (shared with Albie's caption: <see cref="Forge.Pet.AlbieCaption.DefaultFont"/>, a dynamic
    /// atlas that adds glyphs, the ellipsis included, as they show up). Null outside Play mode, so prefabs built in the
    /// Editor keep Core's Inter asset (a runtime font asset can't be saved into a prefab); the panels swap to Fredoka
    /// in Awake (<see cref="ChatUi.ApplyFontAll"/>).
    /// </summary>
    public static TMP_FontAsset Font => Application.isPlaying ? Forge.Pet.AlbieCaption.DefaultFont() : null;

    // ---- Albie's picture (Resources/AlbiePortrait.png: his render, trimmed, 1024 x 750, transparent) ----

    /// <summary>Resources path of Albie's render. Loaded as a plain Texture2D (drawn by a RawImage), so the default
    /// texture import works; no sprite import needed. Missing = nothing is drawn.</summary>
    public const string AlbiePortraitPath = "AlbiePortrait";
    /// <summary>Width / height of the render as saved. Rects use it (not the imported texture's size, which a
    /// power-of-two import may change), so his proportions hold whatever the import does.</summary>
    public const float PortraitAspect = 1024f / 750f;
    // The peek over the chat panel's top edge (mm, from the panel's top right): how wide he is, how much of him shows.
    public const float PeekWidth = 250f, PeekShows = 92f, PeekRight = 70f;
    // The summary sticker beside him, above the panel (mm).
    public const float SummaryLeft = 40f, SummaryGap = 12f, SummaryAbove = 14f, SummaryMaxLines = 3f;
    // His picture on the draft card, top right of the card's content (mm).
    public const float DraftFaceWidth = 70f;

    static Texture2D portrait;
    static bool portraitLooked;

    public static Texture2D AlbiePortrait
    {
        get
        {
            if (!portraitLooked) { portrait = Resources.Load<Texture2D>(AlbiePortraitPath); portraitLooked = true; }
            return portrait;
        }
    }

    // ---- Contrast (WCAG 2), for the Editor check ----

    /// <summary>The background the panels sit on, taken as opaque: ForgeTheme's graphite panel bottom.</summary>
    public static Color PanelGround => Opaque(ForgeTheme.PanelBottom);

    /// <summary><paramref name="top"/> composited over an opaque <paramref name="under"/>.</summary>
    public static Color Over(Color top, Color under) =>
        new Color(Mathf.Lerp(under.r, top.r, top.a), Mathf.Lerp(under.g, top.g, top.a), Mathf.Lerp(under.b, top.b, top.a), 1f);

    public static float Contrast(Color text, Color ground)
    {
        Color g = Opaque(ground);
        float a = Luminance(Over(text, g)), b = Luminance(g);
        return (Mathf.Max(a, b) + 0.05f) / (Mathf.Min(a, b) + 0.05f);
    }

    static float Luminance(Color c) => 0.2126f * Lin(c.r) + 0.7152f * Lin(c.g) + 0.0722f * Lin(c.b);
    static float Lin(float v) => v <= 0.04045f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
    static Color Opaque(Color c) => c.a >= 1f ? c : Over(c, Color.black);
    static Color Hex(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
}
