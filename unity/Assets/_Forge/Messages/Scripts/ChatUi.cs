using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Messages module's look and small UI factories, in the style of CAD's CadUi: Hudson's graphite ForgePanel frame
/// outside, and inside it the module's own card fill (the slightly lighter translucent slate the digest cards always had:
/// 0.12 / 0.15 / 0.21 at 0.8 alpha, opaque Fredoka text, soft shadow, rounded) for message rows, conversation rows, the
/// composer and the Mentions rows. The brand look (Fredoka, lime, orange, stickers, type scale) is <see cref="MessagesLook"/>;
/// the module accent stays ForgeTheme.Messages.
/// 1 canvas unit = 1 mm; rects are placed from their parent's top left with y down.
/// </summary>
public static class ChatUi
{
    // ---- Colours ----

    public static Color Accent => ForgeTheme.Messages;                        // #A77BFF
    public static readonly Color CardFill = new Color(0.12f, 0.15f, 0.21f, 0.8f);
    public static readonly Color CardFillHover = new Color(0.14f, 0.17f, 0.24f, 0.92f);
    public static readonly Color CardFillOn = new Color(0.17f, 0.20f, 0.28f, 1f);
    public static readonly Color Shadow = new Color(0f, 0f, 0f, 0.28f);
    public static Color TextColor => MessagesLook.Text;                        // white 95%
    public static Color Subtext => MessagesLook.Subtext;                       // white 62%
    public static readonly Color Muted = new Color(1f, 1f, 1f, 0.68f);
    public static readonly Color Ink = ForgeTheme.PillOnText;                   // dark text on a light / accent pill
    public static Color White(float a) => new Color(1f, 1f, 1f, a);
    public static Color AccentA(float a) => new Color(Accent.r, Accent.g, Accent.b, a);
    /// <summary>The user's own rows: the card fill nudged toward the accent.</summary>
    public static Color MeFill => new Color(Mathf.Lerp(CardFill.r, Accent.r, 0.14f), Mathf.Lerp(CardFill.g, Accent.g, 0.14f), Mathf.Lerp(CardFill.b, Accent.b, 0.14f), CardFill.a);

    // Initials bubbles, by a stable hash of the sender.
    public static readonly Color[] BubbleColors =
    {
        new Color(0.25f, 0.55f, 1f), new Color(0.85f, 0.4f, 0.9f), new Color(0.3f, 0.8f, 0.45f),
        new Color(1f, 0.55f, 0.2f), new Color(0.95f, 0.35f, 0.4f), new Color(0.25f, 0.8f, 0.85f),
    };
    public static Color BubbleColor(string key) => BubbleColors[ChatText.StableHash(key) % BubbleColors.Length];

    // ---- Messenger (the conversations panel inside the graphite frame: Forge's own look, graphite with the MSG violet) ----

    public static readonly Color MsgWindow = new Color(0.125f, 0.122f, 0.157f);      // graphite with a violet cast: sidebar and window
    public static readonly Color MsgSideText = new Color(0.88f, 0.87f, 0.93f, 0.74f); // read conversations
    public static readonly Color MsgSideHover = new Color(1f, 1f, 1f, 0.07f);
    public static readonly Color MsgSideOn = new Color(0.267f, 0.212f, 0.392f);      // the open conversation: MSG violet, dimmed
    public static readonly Color MsgSideOnHover = new Color(0.31f, 0.245f, 0.45f);
    public static readonly Color MsgBadge = new Color(0.655f, 0.482f, 1f);            // unread count pill: #A77BFF
    public static readonly Color MsgPane = new Color(0.082f, 0.086f, 0.102f);        // the conversation
    public static readonly Color MsgPaneHover = new Color(0.115f, 0.118f, 0.14f);    // a pointed-at message
    public static readonly Color MsgText = new Color(0.84f, 0.84f, 0.86f);
    public static readonly Color MsgName = new Color(0.973f, 0.973f, 0.973f);
    public static readonly Color MsgMuted = new Color(0.66f, 0.66f, 0.70f);
    public static readonly Color MsgLine = new Color(1f, 1f, 1f, 0.09f);
    public static readonly Color MsgLink = new Color(0.78f, 0.66f, 1f);              // #C7A8FF, links and #channels
    public static readonly Color MsgNew = new Color(1f, 0.48f, 0.42f);               // #FF7A6B, the "New" line
    public static Color MsgSend => MessagesLook.Orange;                              // Send: brand orange
    public static Color MsgSendHover => MessagesLook.OrangeHover;
    public static readonly Color MsgBorder = new Color(1f, 1f, 1f, 0.16f);           // the composer's edge
    public static readonly Color MsgBorderFocus = new Color(0.655f, 0.482f, 1f, 0.75f);
    public static readonly Color MsgField = new Color(0.115f, 0.118f, 0.14f);
    public static Color Clear => new Color(1f, 1f, 1f, 0f);

    // ---- Icons (Resources/ChatIcons/*.png, imported as sprites; white, tinted by Image.color) ----

    static readonly System.Collections.Generic.Dictionary<string, Sprite> icons = new System.Collections.Generic.Dictionary<string, Sprite>();

    public static Sprite IconSprite(string name)
    {
        if (!icons.TryGetValue(name, out var sprite))
        {
            sprite = Resources.Load<Sprite>("ChatIcons/" + name);
            if (sprite == null) Debug.LogWarning($"[ChatUi] Resources/ChatIcons/{name}.png missing or not imported as a sprite.");
            icons[name] = sprite;
        }
        return sprite;
    }

    /// <summary>A white icon (send, lock, caret, sparkle) tinted <paramref name="color"/>; place it like any rect.</summary>
    public static Image Icon(Transform parent, string name, string icon, Color color)
    {
        var rt = Rect(parent, name);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = IconSprite(icon);
        if (img.sprite == null) img.enabled = false;   // a missing icon draws nothing, never a flat coloured square
        img.preserveAspect = true;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    // ---- Type (mm) ----

    public const float Title = MessagesLook.Title, Name = MessagesLook.Name, Body = MessagesLook.Body, Small = MessagesLook.Small,
        Overline = MessagesLook.Overline, Time = MessagesLook.Time, Pill = MessagesLook.Pill;
    public const float CardCorner = 14f, RowCorner = 10f;

    // ---- Rects ----

    public static RectTransform Rect(Transform parent, string name)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>x, y from the parent's top left (y down), w x h.</summary>
    public static void Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x + w * 0.5f, -(y + h * 0.5f));
    }

    /// <summary>Hung from the parent's top right: <paramref name="right"/> from its right edge to the rect's.</summary>
    public static void PlaceRight(RectTransform rt, float right, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(-(right + w * 0.5f), -(y + h * 0.5f));
    }

    /// <summary>The parent's width less <paramref name="left"/> and <paramref name="right"/>, <paramref name="y"/> from its top.</summary>
    public static void Band(RectTransform rt, float y, float h, float left = 0f, float right = 0f)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(left, -(y + h));
        rt.offsetMax = new Vector2(-right, -y);
    }

    /// <summary>Fills the parent less insets.</summary>
    public static void Stretch(RectTransform rt, float left, float top, float right, float bottom)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }

    /// <summary>A top-anchored strip whose height the owner sets (scroll contents, columns).</summary>
    public static void TopStrip(RectTransform rt, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(0f, -height);
        rt.offsetMax = Vector2.zero;
    }

    // ---- Text ----

    public static TextMeshProUGUI Text(Transform parent, string name, float size, Color color, bool bold = false,
        TextAlignmentOptions align = TextAlignmentOptions.Left, bool wrap = false)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        ApplyFont(t);
        t.fontSize = size;
        t.color = color;
        SetBold(t, bold);
        t.alignment = align;
        t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        // Overflow, not Ellipsis, by default: TMP hides a line outright when its box is a hair shorter than the font's
        // line height. Rows that must cut off set Ellipsis themselves.
        t.overflowMode = TextOverflowModes.Overflow;
        t.richText = true;
        t.raycastTarget = false;
        t.text = "";
        return t;
    }

    /// <summary>
    /// Bold, without most of TMP's faux-bold letter spacing: Inter ships one weight here, and TMP widens bold text by the
    /// font asset's boldSpacing (7), which reads as tracked-out caps next to tight bold names.
    /// For text that is bold as a whole; inline &lt;b&gt; keeps TMP's spacing.
    /// </summary>
    public static void SetBold(TMP_Text t, bool on)
    {
        t.fontStyle = on ? t.fontStyle | FontStyles.Bold : t.fontStyle & ~FontStyles.Bold;
        t.characterSpacing = on ? -BoldTighten : 0f;
    }

    public const float BoldTighten = 5.5f;

    /// <summary>The panels' font: Fredoka in Play mode (<see cref="MessagesLook.Font"/>), else Core's Inter, else TMP's default.</summary>
    public static TMP_FontAsset Font => MessagesLook.Font != null ? MessagesLook.Font : ForgeTheme.Font;

    public static void ApplyFont(TMP_Text t)
    {
        if (t == null) return;
        var font = Font;
        if (font != null && t.font != font)
        {
            t.font = font;
            t.fontSharedMaterial = font.material;
        }
    }

    /// <summary>
    /// Every text under <paramref name="root"/> (the ForgePanel header included) in the panels' font. Prefab texts are
    /// saved with Inter (see <see cref="MessagesLook.Font"/>), so the panels call this once in Awake. Not per frame.
    /// </summary>
    public static void ApplyFontAll(GameObject root)
    {
        if (root == null || MessagesLook.Font == null) return;
        foreach (var t in root.GetComponentsInChildren<TMP_Text>(true)) ApplyFont(t);
        foreach (var f in root.GetComponentsInChildren<TMP_InputField>(true)) f.fontAsset = Font;
    }

    /// <summary>Overline: 11 mm caps, wide tracking, 50% ("CHANNELS", "NEW FOR YOU").</summary>
    public static TextMeshProUGUI OverlineText(Transform parent, string name)
    {
        var t = Text(parent, name, Overline, Subtext, bold: true);
        t.characterSpacing = 10f;
        t.fontStyle |= FontStyles.UpperCase;
        return t;
    }

    public static float Width(TMP_Text t, string s) => string.IsNullOrEmpty(s) ? 0f : t.GetPreferredValues(s).x;

    public static float Height(TMP_Text t, string s, float width) =>
        string.IsNullOrEmpty(s) ? 0f : t.GetPreferredValues(s, Mathf.Max(1f, width), 0f).y;

    // ---- Surfaces ----

    public static ForgeSurface Surface(Transform parent, string name, Color color, float corner, float outline = 0f)
    {
        var rt = Rect(parent, name);
        var s = rt.gameObject.AddComponent<ForgeSurface>();
        s.Style = ForgeSurface.Kind.Fill;
        s.Corner = corner;
        s.Outline = outline;
        s.color = color;
        s.raycastTarget = false;
        return s;
    }

    public static ForgeSurface PillSurface(Transform parent, string name, Color color, float glow = 0f)
    {
        var s = Surface(parent, name, color, 0f);
        s.Pill = true;
        s.Glow = glow;
        return s;
    }

    /// <summary>
    /// The module's card: a soft shadow (a dark fill with a glow, set a little low) under the translucent slate fill.
    /// Returns the fill; the shadow is its sibling just behind it, stretched to the same rect. Both clip under a
    /// RectMask2D. <paramref name="raycast"/> makes the fill take the pointer (a clickable row).
    /// </summary>
    public static ForgeSurface Card(RectTransform rect, float corner, bool raycast)
    {
        var shadow = Surface(rect, "Shadow", Shadow, corner);
        shadow.Glow = 0.7f;
        Stretch(shadow.rectTransform, 2f, 3f, 2f, -1f);
        var fill = Surface(rect, "Fill", CardFill, corner);
        Stretch(fill.rectTransform, 0f, 0f, 0f, 0f);
        fill.raycastTarget = raycast;
        return fill;
    }

    /// <summary>
    /// A sticker on <paramref name="rect"/>: three flat fills, no blur. A hard shadow offset down-right by
    /// <paramref name="shadowOffset"/> mm, an ink border, and the fill inset by <paramref name="border"/> mm. Returns the
    /// fill (put the text in <paramref name="rect"/> over it). Albie's sticker: white with a lime shadow; a mention tag:
    /// lime with an ink shadow (<see cref="MessagesLook"/>). The shadow reaches outside the rect, so leave it room.
    /// </summary>
    public static ForgeSurface Sticker(RectTransform rect, Color fillColor, Color shadowColor, float corner, float border,
        float shadowOffset)
    {
        var shadow = Surface(rect, "StickerShadow", shadowColor, corner);
        Stretch(shadow.rectTransform, shadowOffset, shadowOffset, -shadowOffset, -shadowOffset);
        var edge = Surface(rect, "StickerBorder", MessagesLook.Ink, corner);
        Stretch(edge.rectTransform, 0f, 0f, 0f, 0f);
        var fill = Surface(rect, "StickerFill", fillColor, Mathf.Max(0f, corner - border));
        Stretch(fill.rectTransform, border, border, border, border);
        return fill;
    }

    /// <summary>
    /// Albie's render, <paramref name="width"/> mm wide at his own proportions (<see cref="MessagesLook.PortraitAspect"/>),
    /// placed by the caller. Never takes the pointer, so rays and pokes reach what is behind him. Null (nothing drawn)
    /// when the texture is missing.
    /// </summary>
    public static RawImage Portrait(Transform parent, string name, float width)
    {
        var tex = MessagesLook.AlbiePortrait;
        if (tex == null) return null;
        var img = Rect(parent, name).gameObject.AddComponent<RawImage>();
        img.texture = tex;
        img.raycastTarget = false;
        img.rectTransform.sizeDelta = new Vector2(width, width / MessagesLook.PortraitAspect);
        return img;
    }

    /// <summary>Albie's white sticker with the lime shadow.</summary>
    public static ForgeSurface AlbieSticker(RectTransform rect) =>
        Sticker(rect, MessagesLook.StickerWhite, MessagesLook.Lime, MessagesLook.StickerCorner, MessagesLook.StickerBorder,
            MessagesLook.StickerShadow);

    /// <summary>The mention tag ("@you"): a small lime sticker with an ink shadow and ink text. Returns its root.</summary>
    public static RectTransform MentionTag(Transform parent, string name)
    {
        var rt = Rect(parent, name);
        Sticker(rt, MessagesLook.Lime, MessagesLook.Ink, MessagesLook.TagCorner, MessagesLook.TagBorder, MessagesLook.TagShadow);
        var label = Text(rt, "Label", MessagesLook.TagText, MessagesLook.Ink, bold: true, align: TextAlignmentOptions.Center);
        label.text = MessagesLook.MentionTagText;
        Stretch(label.rectTransform, 0f, 0f, 0f, 0f);
        rt.sizeDelta = new Vector2(Width(label, label.text) + 16f, MessagesLook.TagH);
        return rt;
    }

    public static ChatHitArea Hit(Transform parent, string name)
    {
        var rt = Rect(parent, name);
        var hit = rt.gameObject.AddComponent<ChatHitArea>();
        hit.raycastTarget = true;
        return hit;
    }

    public static CanvasGroup Group(GameObject go)
    {
        var g = go.GetComponent<CanvasGroup>();
        return g != null ? g : go.AddComponent<CanvasGroup>();
    }

    public static void Show(Component c, bool on)
    {
        if (c != null && c.gameObject.activeSelf != on) c.gameObject.SetActive(on);
    }

    /// <summary>
    /// A vertical scroll list: an invisible viewport that takes the pointer (so dragging empty space scrolls), a
    /// RectMask2D and a top-anchored content strip whose height the owner sets after laying its rows out. Mouse drags
    /// (DesktopRig) and Interaction SDK ray / poke drags both scroll it; a press that moves is a drag, not a click
    /// (both input paths cancel the click past their slop), so dragging the list by a row never presses the row.
    /// </summary>
    public static ScrollRect ScrollList(Transform parent, string name, out RectTransform content)
    {
        var viewport = Hit(parent, name).rectTransform;
        viewport.gameObject.AddComponent<RectMask2D>();
        content = Rect(viewport, "Content");
        TopStrip(content, 100f);
        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Elastic;
        scroll.elasticity = 0.08f;
        scroll.inertia = true;
        scroll.decelerationRate = 0.12f;
        scroll.scrollSensitivity = 18f;
        return scroll;
    }
}
