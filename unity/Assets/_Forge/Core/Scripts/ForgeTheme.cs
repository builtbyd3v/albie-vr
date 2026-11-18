using TMPro;
using UnityEngine;

/// <summary>
/// The Forge look in one place: light drawn on the desk (dock, volume bases) and graphite panels with white pills.
/// Colors, sizes (mm, 1 canvas unit = 1 mm) and timings shared by <see cref="DeskDock"/>, <see cref="ForgePanel"/>,
/// <see cref="PanelManipulator"/> and <see cref="VolumeManipulator"/>. Assets (the Inter font, the light materials)
/// load from Core/Resources/ForgeLook, built by Forge > Build Look Assets.
/// </summary>
public static class ForgeTheme
{
    // Mode hues: only a faint tint of these shows (HueStrength); the light itself is white.
    public static readonly Color ML = Hex(0x4D8DFF);
    public static readonly Color CAD = Hex(0xFF8A3D);
    public static readonly Color Code = Hex(0x22CF86);
    public static readonly Color Messages = Hex(0xA77BFF);
    public static readonly Color Tool = Hex(0xCFE0FF);
    public const float HueStrength = 0.10f;

    // Hub status light.
    public static readonly Color HubLive = Hex(0x4BE38A), HubLiveGlow = Hex(0x22C46A);
    public static readonly Color HubMock = Hex(0xFFC04D), HubMockGlow = Hex(0xFFB52E);
    public static readonly Color HubConnecting = Hex(0xA3A9B3), HubConnectingGlow = Hex(0x7C838F);

    // Desk dock (mm).
    public const float DockModeRing = 72f, DockToolRing = 59f, DockGap = 23f, DockPadding = 25f, DockHeight = 115f;
    public const float DockCorner = 28f, DockDivider = 46f, DockRise = 25f, DockModeLabel = 15.5f, DockToolLabel = 10.7f;
    public const float DockTilt = 22f;           // how far a raised ring turns toward the head (deg)
    public const float DockNearRange = 110f;     // pointer distance at which a ring starts to brighten (mm)
    public const float DockNudgeRing = 44f, DockNudgeLabel = 22f;  // the ‹ › rings at the ends that slide the dock

    // Timings (s).
    public const float HoverFade = 0.3f, PressTime = 0.12f, LiftTime = 0.6f, HeadSmoothing = 0.35f, SelectFade = 0.45f;
    // Dock entrance: the frame slides in along the front edge, then the rings plop on one by one.
    public const float DockSlideTime = 0.6f, DockPlopTime = 0.25f, DockPlopStagger = 0.08f;
    public const float DockNudgeTime = 0.3f;  // ‹ / ›: one step along the desk

    // Motion (s). A mode unfolds out of the dock ring that was pressed and folds back into it; Flat / Curve and Reset
    // spring the items to their new places. Items go centre out (Main, the pair beside it, the outer pair, Above) and
    // fold outermost first; switching modes, the new one starts SwitchDelay after the old one starts folding.
    public const float UnfoldTime = 0.55f, UnfoldStagger = 0.06f, FoldTime = 0.3f, FoldStagger = 0.04f, SwitchDelay = 0.1f;
    public const float LayoutTime = 0.45f, LayoutStagger = 0.03f;
    public const float UnfoldFade = 0.4f;    // share of the unfold over which a panel fades in
    public const float UnfoldScale = 0.04f;  // a panel's scale as it leaves (and goes back into) the ring

    // Graphite panels.
    // Matched to the Albie OS menu (the UI Set backplate as measured in a screenshot: top #3A3C40, bottom #252629, opaque),
    // so the older windows and the new ones sit on the same plate. Was 33,36,42 / 19,21,25 at alpha 245, text 0.95, subtext 0.5, corner 26.
    // Keep in step with Resources/ForgeLook/PanelSurface.mat (_Top, _Bottom), which draws the plate.
    // Design system (docs/DESIGN_SYSTEM.md): deep graphite glass, a touch cool, 95% opaque so the room shows faintly at the
    // edges without ever eating the text. The plate is drawn from these (PanelSurface is a runtime copy of the .mat with them).
    // Looked at in the look set (OSLookShots): the plate's own graphic alpha (about 0.9) multiplies these, so 252 lands near 89%
    // over a light wall: enough to keep text off the room, still glass at the edges. Was #34373F / #1B1C21 at 242 (reads 85%).
    // Opaque since the final build: at 89% the text of a window behind read through the one in front (two windows open
    // together on the headset), which is worse than losing the faint glass at the edges. Same colours.
    public static readonly Color PanelTop = new Color32(48, 51, 59, 255);     // #30333B
    public static readonly Color PanelBottom = new Color32(22, 23, 28, 255);  // #16171C
    public static readonly Color PanelText = new Color(1f, 1f, 1f, 1f);
    public static readonly Color PanelSubtext = new Color(1f, 1f, 1f, 0.72f);
    public static readonly Color Hairline = new Color(1f, 1f, 1f, 0.08f);

    // ---- Design tokens (one place to tune; see docs/DESIGN_SYSTEM.md) ----
    // Text. Worst case (95% plate over a white room, on a raised card): primary 8.7:1, secondary 5.4:1. Tertiary is for
    // disabled and decorative text only (3.5:1).
    public static readonly Color TextPrimary = new Color(1f, 1f, 1f, 1f);
    public static readonly Color TextSecondary = new Color(1f, 1f, 1f, 0.72f);
    public static readonly Color TextTertiary = new Color(1f, 1f, 1f, 0.50f);
    public static readonly Color TextOnLight = new Color32(39, 39, 39, 255);   // on the white primary button: 14.9:1
    public static readonly Color TextOnAccent = new Color32(0x12, 0x17, 0x2B, 255);  // ink on the lime accent: 11.7:1

    /// <summary>The OS accent: Albie lime. Current step, progress, focus, the "open" dot. As text: 5.7:1 worst case.</summary>
    public static readonly Color Accent = Hex(0xC8DC5A);

    // Semantic. Fills use the base colour; text uses the *Text variant (each >= 4.5:1 worst case).
    public static readonly Color Success = Hex(0x3DDC97), SuccessText = Hex(0x3DDC97);   // 4.9:1
    public static readonly Color Warning = Hex(0xFFB547), WarningText = Hex(0xFFB547);   // 4.9:1
    public static readonly Color Danger = Hex(0xFF5D6C), DangerText = Hex(0xFFA8AF);     // 4.7:1
    public static readonly Color Info = Hex(0x5B9BFF), InfoText = Hex(0x9CC3FF);         // 4.8:1

    // Elevation. Surface = the window plate. Raised = a card on it. Inset = a well (charts, fields). Floating = above
    // everything (toasts, confirm strips): an opaque fill, a soft dark halo and a light ring.
    // Raised surfaces are drawn lit from above (ForgeSurface.Kind.Raised): a faint sheen in the top of the fill and a rim that is
    // brightest along the top edge and fades down the sides, so a card reads as a lifted pane, not an outlined box.
    public static readonly Color RaisedFill = new Color(1f, 1f, 1f, 0.06f);
    public const float RaisedRing = 0.16f;
    public static readonly Color RaisedHover = new Color(1f, 1f, 1f, 0.10f);
    public static readonly Color InsetFill = new Color(0f, 0f, 0f, 0.24f);
    public static readonly Color FloatingFill = new Color32(46, 49, 56, 252);
    public const float FloatingRing = 0.14f, FloatingHalo = 0.75f;
    public static readonly Color SelectedFill = new Color(0.784f, 0.863f, 0.353f, 0.16f);  // Accent at 16%
    public const float SelectedRing = 0.60f;

    // Radius scale. Canvas mm (Forge panels) and OS units (0.65 mm, windows built with OSUi).
    public const float RadiusPanelMm = 24f, RadiusCardMm = 14f, RadiusControlMm = 10f;
    public const float RadiusCard = 22f, RadiusControl = 16f, RadiusSmall = 10f;  // OS units; chips are full pills

    // Spacing scale, OS units on a 4/8 grid.
    public const float Space1 = 4f, Space2 = 8f, Space3 = 12f, Space4 = 16f, Space5 = 24f, Space6 = 32f, Space7 = 48f;

    // Type scale, OS units (cap height in mm at the 0.65 mm unit with Inter, cap 0.734 em): display 34 (16.2 mm),
    // title 26 (12.4 mm), body 22 (10.5 mm), caption 20 (9.5 mm). Headings bold, body regular.
    public const float TypeDisplay = 34f, TypeTitle = 26f, TypeBody = 22f, TypeCaption = 20f;
    /// <summary>Extra line spacing for wrapped text (TextMeshPro units, % of the em).</summary>
    public const float LineSpacing = 6f;
    /// <summary>Tighter tracking for big headings (TextMeshPro character spacing).</summary>
    public const float HeadingTracking = -0.6f, CapsTracking = 5f;
    /// <summary>A comfortable line: wrapped paragraphs are kept to at most this many OS units wide (about 65 characters).</summary>
    public const float MaxLineWidth = 760f;

    // Motion (s). Panel open/close (OSWindows) is 0.2 / 0.18; controls ease with these.
    public const float MotionPress = 0.08f, MotionHover = 0.16f, MotionState = 0.22f, MotionProgress = 0.35f;
    /// <summary>A pressed control sinks to this scale (and back), the press "depth".</summary>
    public const float PressScale = 0.965f;
    /// <summary>Glow while a hand or ray is near (0..1, ForgeSurface glow).</summary>
    public const float HoverGlow = 0.35f;
    public const float PanelCorner = 24f, PanelHeader = 64f, PanelInset = 18f;
    /// <summary>The title-bar mode badge: accent fill alpha, its ring, and how far its label is lifted toward white (>= 5.3:1 for every mode hue).</summary>
    public const float BadgeFill = 0.20f, BadgeRing = 0f, BadgeTextLift = 0.55f;

    // Title-bar window buttons (PanelChrome: Close, Minimise). Quiet glass discs that come up when reached for; Close
    // turns red only while pressed. Owners of PanelChromeButton can read these.
    public static readonly Color ChromeRest = new Color(1f, 1f, 1f, 0.10f);
    public static readonly Color ChromeHover = new Color(1f, 1f, 1f, 0.22f);
    public static readonly Color ChromeDown = new Color(1f, 1f, 1f, 0.34f);
    public static readonly Color ChromeCloseDown = new Color(1f, 0.36f, 0.42f, 0.92f);
    public static readonly Color ChromeMark = new Color(1f, 1f, 1f, 0.82f);
    public static readonly Color ChromeMarkHover = Color.white;
    /// <summary>The window buttons' hairline ring (ForgeSurface outline alpha, in the disc's colour: white).</summary>
    public const float ChromeRing = 0.18f;

    // Horizon-style pills: dim when off, near-white with dark text when on.
    public static readonly Color PillOff = new Color(1f, 1f, 1f, 0.09f);
    public static readonly Color PillOffHover = new Color(1f, 1f, 1f, 0.17f);
    public static readonly Color PillPressed = new Color(1f, 1f, 1f, 0.05f);
    public static readonly Color PillDisabled = new Color(1f, 1f, 1f, 0.04f);
    public static readonly Color PillOnHover = new Color(0.93f, 0.95f, 0.98f, 1f);
    public static readonly Color PillOnPressed = new Color(0.80f, 0.82f, 0.86f, 1f);
    /// <summary>The hairline highlight of an off pill at rest and on hover (ForgeSurface outline alpha).</summary>
    public const float PillRing = 0.10f, PillRingHover = 0.24f;
    // The on pill and its dark text are the OS primary button: solid white, dark text #272727 (the OS theme's inverted text colour).
    // Was off text 0.85, on 0.92, on text #1C1E21.
    public static readonly Color PillOffText = new Color(1f, 1f, 1f, 1f);
    // Lit quiet pill (title bar): a dark-grey tint so the white label keeps >= 4.5:1 on the dark header.
    public static readonly Color PillQuietOn = new Color(1f, 1f, 1f, 0.13f);
    public static readonly Color PillOn = new Color(1f, 1f, 1f, 1f);
    public static readonly Color PillOnText = new Color32(39, 39, 39, 255);
    public const float PillHeight = 30f, PillText = 13f;

    // Handles: white bars that glow brighter while pointed at or held.
    public static readonly Color HandleRest = new Color(1f, 1f, 1f, 0.62f);
    public static readonly Color HandleLit = new Color(1f, 1f, 1f, 1f);

    /// <summary>Mode hue at the given strength multiple (1 = the faint tint, 2.2 = the halo).</summary>
    public static Color Tint(Color hue, float alpha) => new Color(hue.r, hue.g, hue.b, Mathf.Clamp01(alpha));

    // ---- Assets ----

    static TMP_FontAsset font;
    static Material glowText, frame, pool, column, ring, panel;

    /// <summary>Inter (SIL OFL). Core text only; modules keep their own fonts.</summary>
    public static TMP_FontAsset Font => font != null ? font : font = Load<TMP_FontAsset>("Inter SDF");
    /// <summary>Inter with a soft white glow, for text drawn as light (dock labels, volume labels).</summary>
    public static Material GlowText => glowText != null ? glowText : glowText = Load<Material>("Inter Glow");
    public static Material FrameLight => frame != null ? frame : frame = Load<Material>("NeonFrame");
    public static Material PoolLight => pool != null ? pool : pool = Load<Material>("LightPool");
    public static Material ColumnLight => column != null ? column : column = Load<Material>("NeonColumn");
    public static Material RingLight => ring != null ? ring : ring = Load<Material>("NeonRing");
    /// <summary>UI material for graphite panel backgrounds (rounded, gradient, rim light).</summary>
    /// <remarks>A runtime copy of Resources/ForgeLook/PanelSurface.mat with its _Top / _Bottom set from <see cref="PanelTop"/> and
    /// <see cref="PanelBottom"/> (straight colour and alpha: the shader composites them), so the tokens drive the plate. Never saved.</remarks>
    public static Material PanelSurface
    {
        get
        {
            if (panel != null) return panel;
            var asset = Load<Material>("PanelSurface");
            if (asset == null) return null;
            panel = new Material(asset) { name = "PanelSurface (tokens)", hideFlags = HideFlags.DontSave };
            panel.SetColor("_Top", PanelTop);
            panel.SetColor("_Bottom", PanelBottom);
            return panel;
        }
    }


    // ---- Type weight ----
    // Only Inter Regular is in the project (one static SDF atlas). TextMeshPro's fake bold on it adds 7% of the em between
    // letters (the asset's boldSpacing) and its own dilation, and the old dark-on-light label added 0.4 face dilation on top:
    // the strokes ran into each other and the counters filled in (the "double-struck" New project / Projects / Primary).
    // Weight now comes from one shared material per dilation (crisp, no extra tracking): Strong for headings and emphasis,
    // and a little more for dark text on a light fill, which reads thinner than white on dark.
    /// <summary>Face dilate of the strong (semibold) face: headings, section titles, tags (TextMeshPro units, -1 to 1).</summary>
    public const float StrongDilate = 0.10f;
    /// <summary>Face dilate of a medium face (tab labels at rest).</summary>
    public const float MediumDilate = 0.05f;
    /// <summary>Face dilate of a dark label on a light fill (the primary button, the lit tab). Was 0.4 with fake bold: smeared.</summary>
    public const float DarkLabelDilate = 0.13f;
    /// <summary>The same for the small pill labels of the older windows (13 mm or so).</summary>
    public const float DarkPillDilate = 0.10f;

    static readonly System.Collections.Generic.Dictionary<(TMP_FontAsset, int), Material> heavyMaterials =
        new System.Collections.Generic.Dictionary<(TMP_FontAsset, int), Material>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetHeavy()
    {
        foreach (var m in heavyMaterials.Values) if (m != null) Object.Destroy(m);
        heavyMaterials.Clear();
    }

    /// <summary>
    /// Draws a label for a dark-on-light fill (<paramref name="dark"/> true) with a heavier face, or back with the font's own
    /// material (false). Safe to call every time a button changes state; it only swaps a shared material. Never fake bold.
    /// </summary>
    public static void DarkOnLight(TMP_Text label, bool dark, float dilate = DarkLabelDilate)
    {
        if (label == null) return;
        if ((label.fontStyle & FontStyles.Bold) != 0) label.fontStyle &= ~FontStyles.Bold;
        Weight(label, dark ? dilate : 0f);
    }

    /// <summary>
    /// The design system's strong face: no fake bold (it adds tracking and smears on the static atlas), a shared semibold
    /// material instead, and the tracking given. Keeps UpperCase / SmallCaps if they are set.
    /// </summary>
    public static void Strong(TMP_Text label, float tracking = 0f)
    {
        if (label == null) return;
        if ((label.fontStyle & FontStyles.Bold) != 0) label.fontStyle &= ~FontStyles.Bold;
        label.characterSpacing = tracking;
        Weight(label, StrongDilate);
    }

    /// <summary>Gives a label its font's material with <paramref name="dilate"/> face dilation (0 = the font's own material). Shared per font and value.</summary>
    public static void Weight(TMP_Text label, float dilate)
    {
        if (label == null || label.font == null) return;
        TMP_FontAsset font = label.font;
        if (dilate <= 0.001f)
        {
            if (label.fontSharedMaterial != font.material) label.fontSharedMaterial = font.material;
            return;
        }
        var key = (font, Mathf.RoundToInt(dilate * 1000f));
        if (!heavyMaterials.TryGetValue(key, out Material heavy) || heavy == null)
        {
            heavy = new Material(font.material) { name = font.name + " Weight " + dilate.ToString("0.00"), hideFlags = HideFlags.DontSave };
            heavy.SetFloat(ShaderUtilities.ID_FaceDilate, dilate);
            heavyMaterials[key] = heavy;
        }
        if (label.fontSharedMaterial != heavy) label.fontSharedMaterial = heavy;
    }

    /// <summary>A mode's white dock icon (Resources/ForgeLook/DockIcons/{modeId}.svg, imported as a sprite), or null.</summary>
    public static Sprite DockIcon(string modeId) => Resources.Load<Sprite>("ForgeLook/DockIcons/" + modeId);

    static T Load<T>(string name) where T : Object
    {
        var asset = Resources.Load<T>("ForgeLook/" + name);
        if (asset == null) Debug.LogWarning($"[ForgeTheme] Missing Resources/ForgeLook/{name}. Run Forge > Build Look Assets.");
        return asset;
    }

    static Color Hex(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);

    // A unit quad (-0.5..0.5, facing -Z like a canvas) shared by every light layer; scale its transform to size it.
    static Mesh quad;
    public static Mesh Quad
    {
        get
        {
            if (quad != null) return quad;
            quad = new Mesh { name = "ForgeQuad" };
            quad.SetVertices(new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) });
            quad.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) });
            quad.SetColors(new[] { Color.white, Color.white, Color.white, Color.white });
            quad.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            quad.RecalculateBounds();
            return quad;
        }
    }
}
