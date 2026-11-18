using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The design system's control states on a <see cref="ForgeSurface"/> plate: rest, hover (a ray or a fingertip near it: the
/// fill lifts and a soft glow comes up), pressed (the fill dims and the control sinks to <see cref="ForgeTheme.PressScale"/>),
/// selected, and disabled (read from the Selectable's interactable). Eased through <see cref="ForgeMotion"/>, so it costs
/// almost nothing while idle (one bool compare in LateUpdate) and allocates nothing per frame. Made by <see cref="ForgeLook.IconButton"/>, OSUi.IconButton, OSUi.ListRow.
/// Call <see cref="Refresh"/> after changing <c>interactable</c> from code.
/// </summary>
[DisallowMultipleComponent]
public sealed class ForgeButtonLook : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler, IForgeTick
{
    /// <summary>Colours of one control style in each state. Ring and glow are 0..1 (ForgeSurface outline and glow).</summary>
    [System.Serializable]
    public struct Palette
    {
        public Color rest, hover, pressed, selected, disabled;
        public float ring, ringHover, ringSelected, glowHover;
        public Color content, contentSelected, contentDisabled;
    }

    /// <summary>Quiet: no plate at rest, a soft one on hover (icon buttons in a title bar, list rows).</summary>
    public static Palette Quiet => new Palette
    {
        rest = new Color(1f, 1f, 1f, 0f), hover = new Color(1f, 1f, 1f, 0.12f), pressed = new Color(1f, 1f, 1f, 0.06f),
        selected = ForgeTheme.SelectedFill, disabled = new Color(1f, 1f, 1f, 0f),
        ring = 0f, ringHover = 0.10f, ringSelected = ForgeTheme.SelectedRing * 0.5f, glowHover = 0f,
        content = ForgeTheme.TextPrimary, contentSelected = ForgeTheme.TextPrimary, contentDisabled = ForgeTheme.TextTertiary,
    };

    /// <summary>Tonal: a faint glass plate with a hairline highlight at rest (secondary icon buttons, chips you can tap).</summary>
    public static Palette Tonal => new Palette
    {
        rest = new Color(1f, 1f, 1f, 0.08f), hover = new Color(1f, 1f, 1f, 0.16f), pressed = new Color(1f, 1f, 1f, 0.05f),
        selected = ForgeTheme.SelectedFill, disabled = new Color(1f, 1f, 1f, 0.04f),
        ring = 0.10f, ringHover = 0.22f, ringSelected = ForgeTheme.SelectedRing, glowHover = ForgeTheme.HoverGlow * 0.4f,
        content = ForgeTheme.TextPrimary, contentSelected = ForgeTheme.TextPrimary, contentDisabled = ForgeTheme.TextTertiary,
    };

    /// <summary>Raised: a card-like row (a list row, a step you can open).</summary>
    public static Palette Raised => new Palette
    {
        rest = ForgeTheme.RaisedFill, hover = ForgeTheme.RaisedHover, pressed = new Color(1f, 1f, 1f, 0.035f),
        selected = ForgeTheme.SelectedFill, disabled = new Color(1f, 1f, 1f, 0.03f),
        ring = ForgeTheme.RaisedRing, ringHover = 0.20f, ringSelected = ForgeTheme.SelectedRing, glowHover = 0f,
        content = ForgeTheme.TextPrimary, contentSelected = ForgeTheme.TextPrimary, contentDisabled = ForgeTheme.TextTertiary,
    };

    /// <summary>Danger on hover (a destructive icon button): neutral at rest, red when reached for.</summary>
    public static Palette Danger => new Palette
    {
        rest = new Color(1f, 1f, 1f, 0.08f), hover = new Color(ForgeTheme.Danger.r, ForgeTheme.Danger.g, ForgeTheme.Danger.b, 0.9f),
        pressed = new Color(ForgeTheme.Danger.r * 0.8f, ForgeTheme.Danger.g * 0.8f, ForgeTheme.Danger.b * 0.8f, 0.9f),
        selected = ForgeTheme.SelectedFill, disabled = new Color(1f, 1f, 1f, 0.04f),
        ring = 0.10f, ringHover = 0f, ringSelected = ForgeTheme.SelectedRing, glowHover = ForgeTheme.HoverGlow,
        content = ForgeTheme.TextPrimary, contentSelected = ForgeTheme.TextPrimary, contentDisabled = ForgeTheme.TextTertiary,
    };

    [SerializeField] ForgeSurface plate;
    [SerializeField] Transform depth;
    [SerializeField] Graphic[] content;
    [SerializeField] Palette palette;

    Selectable selectable;
    float hover, press, select, hoverTarget, pressTarget, selectTarget;
    int hovers;
    bool lastInteractable = true;

    /// <summary>Lit as the chosen one (a selected row, the current tab).</summary>
    public bool Selected
    {
        get => selectTarget > 0.5f;
        set
        {
            selectTarget = value ? 1f : 0f;
            ForgeMotion.Run(this);
        }
    }

    public Palette Colors
    {
        get => palette;
        set
        {
            palette = value;
            Apply();
        }
    }

    /// <summary>Wires the look: the plate that changes colour, what sinks when pressed (default: this transform) and the labels/icons tinted with the content colour.</summary>
    public void Bind(ForgeSurface plateSurface, Palette colors, Transform pressDepth, params Graphic[] contentGraphics)
    {
        plate = plateSurface;
        palette = colors;
        depth = pressDepth != null ? pressDepth : transform;
        content = contentGraphics;
        selectable = GetComponent<Selectable>();
        Apply();
    }

    /// <summary>Re-reads interactable and redraws (call after enabling or disabling the control from code).</summary>
    public void Refresh()
    {
        if (selectable == null) selectable = GetComponent<Selectable>();
        Apply();
    }

    void OnEnable() => Refresh();

    void OnDisable()
    {
        hovers = 0;
        hover = press = hoverTarget = pressTarget = 0f;
        select = selectTarget;
        if (depth != null) depth.localScale = Vector3.one;
        Apply();
    }

    public void OnPointerEnter(PointerEventData e)
    {
        hovers++;
        hoverTarget = 1f;
        ForgeMotion.Run(this);
    }

    public void OnPointerExit(PointerEventData e)
    {
        hovers = Mathf.Max(0, hovers - 1);
        if (hovers == 0) { hoverTarget = 0f; pressTarget = 0f; }
        ForgeMotion.Run(this);
    }

    public void OnPointerDown(PointerEventData e)
    {
        if (!Interactable) return;
        pressTarget = 1f;
        ForgeMotion.Run(this);
    }

    public void OnPointerUp(PointerEventData e)
    {
        pressTarget = 0f;
        ForgeMotion.Run(this);
    }

    bool Interactable => selectable == null || selectable.IsInteractable();

    public bool Tick(float dt)
    {
        if (this == null) return false;
        hover = ForgeMotion.Approach(hover, hoverTarget, ForgeTheme.MotionHover, dt);
        press = ForgeMotion.Approach(press, pressTarget, ForgeTheme.MotionPress, dt);
        select = ForgeMotion.Approach(select, selectTarget, ForgeTheme.MotionState, dt);
        Apply();
        return hover != hoverTarget || press != pressTarget || select != selectTarget;
    }

    void Apply()
    {
        bool on = Interactable;
        lastInteractable = on;
        float h = on ? ForgeMotion.Ease(hover) : 0f, p = on ? ForgeMotion.Ease(press) : 0f, s = ForgeMotion.Ease(select);
        if (plate != null)
        {
            Color fill = on ? Color.Lerp(Color.Lerp(palette.rest, palette.hover, h), palette.selected, s) : palette.disabled;
            if (on) fill = Color.Lerp(fill, palette.pressed, p);
            if (plate.color != fill) plate.color = fill;
            float ring = Mathf.Lerp(Mathf.Lerp(palette.ring, palette.ringHover, h), palette.ringSelected, s);
            if (!Mathf.Approximately(plate.Outline, ring)) plate.Outline = ring;
            float glow = palette.glowHover * h * (1f - p);
            if (!Mathf.Approximately(plate.Glow, glow)) plate.Glow = glow;
        }
        if (depth != null)
        {
            float scale = Mathf.Lerp(1f, ForgeTheme.PressScale, p);
            if (depth.localScale.x != scale) depth.localScale = new Vector3(scale, scale, 1f);
        }
        if (content != null)
        {
            Color c = on ? Color.Lerp(palette.content, palette.contentSelected, s) : palette.contentDisabled;
            for (int i = 0; i < content.Length; i++)
                if (content[i] != null && content[i].color != c) content[i].color = c;
        }
    }

    void LateUpdate()
    {
        // Disabled from code without Refresh: catch it cheaply (one bool compare a frame per control; no allocation).
        if (selectable != null && selectable.IsInteractable() != lastInteractable) Apply();
    }
}
