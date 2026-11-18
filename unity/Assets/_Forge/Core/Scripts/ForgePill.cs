using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Horizon-style pill button: a dim white pill with white text when off, near-white with dark text when on, a touch
/// brighter while pointed at. Works with the mouse (DesktopRig) and Interaction SDK rays and pokes like any Button.
/// Make one with <see cref="Create"/>; it sizes itself to its label.
/// </summary>
public class ForgePill : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler, IForgeTick
{
    [SerializeField] ForgeSurface surface;
    [SerializeField] TMP_Text label;
    bool on;
    int hovers;
    // Eased state (design system): hover lifts the fill and adds a hairline highlight, a press sinks the pill
    // (ForgeTheme.PressScale) and dims it, a disabled pill (Button.interactable false) goes quiet. Driven by ForgeMotion.
    float hover, press, hoverTarget, pressTarget;
    bool wasInteractable = true;

    /// <summary>Quiet pills (title-bar toggles) show their lit state as a tinted fill with light text, so the solid near-white look stays for primary actions.</summary>
    public bool Quiet
    {
        get => quiet;
        set { quiet = value; Refresh(); }
    }
    bool quiet;

    public Button Button { get; private set; }
    public RectTransform Rect => (RectTransform)transform;

    /// <summary>Lit: the pill shows the state that's in effect (Face me while facing).</summary>
    public bool On
    {
        get => on;
        set
        {
            on = value;
            Refresh();
        }
    }

    public static ForgePill Create(RectTransform parent, string text, UnityAction onClick, float height = ForgeTheme.PillHeight, float textSize = ForgeTheme.PillText)
    {
        var rt = (RectTransform)new GameObject(text, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        var pill = rt.gameObject.AddComponent<ForgePill>();
        pill.surface = rt.gameObject.AddComponent<ForgeSurface>();
        pill.surface.Pill = true;
        pill.Button = rt.gameObject.AddComponent<Button>();
        pill.Button.targetGraphic = pill.surface;
        pill.Button.transition = Selectable.Transition.None;
        pill.Button.navigation = new Navigation { mode = Navigation.Mode.None };
        pill.Button.onClick.AddListener(onClick);

        var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
        label.rectTransform.SetParent(rt, false);
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
        if (ForgeTheme.Font != null) label.font = ForgeTheme.Font;
        label.fontSize = textSize;
        label.characterSpacing = 0.4f;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.raycastTarget = false;
        label.text = text;
        pill.label = label;
        rt.sizeDelta = new Vector2(Mathf.Max(height, label.GetPreferredValues(text).x + 2.2f * textSize), height);
        pill.Refresh();
        return pill;
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
        if (hovers == 0) hoverTarget = pressTarget = 0f;
        ForgeMotion.Run(this);
    }

    public void OnPointerDown(PointerEventData e)
    {
        if (Button != null && !Button.IsInteractable()) return;
        pressTarget = 1f;
        ForgeMotion.Run(this);
    }

    public void OnPointerUp(PointerEventData e)
    {
        pressTarget = 0f;
        ForgeMotion.Run(this);
    }

    void OnDisable()
    {
        hovers = 0;
        hover = press = hoverTarget = pressTarget = 0f;
        transform.localScale = Vector3.one;
        Refresh();
    }

    bool IForgeTick.Tick(float dt)
    {
        if (this == null) return false;
        hover = ForgeMotion.Approach(hover, hoverTarget, ForgeTheme.MotionHover, dt);
        press = ForgeMotion.Approach(press, pressTarget, ForgeTheme.MotionPress, dt);
        Refresh();
        return hover != hoverTarget || press != pressTarget;
    }

    void LateUpdate()
    {
        // interactable changed from code: one bool compare a frame, no allocation.
        if (Button != null && Button.interactable != wasInteractable) Refresh();
    }

    void Refresh()
    {
        if (surface == null || label == null) return;
        bool enabledPill = Button == null || Button.interactable;
        wasInteractable = Button == null || Button.interactable;
        bool solid = on && !quiet;
        float h = enabledPill ? ForgeMotion.Ease(hover) : 0f, p = enabledPill ? ForgeMotion.Ease(press) : 0f;
        Color fill = solid ? ForgeTheme.PillOn : on ? ForgeTheme.PillQuietOn : Color.Lerp(ForgeTheme.PillOff, ForgeTheme.PillOffHover, h);
        if (solid) fill = Color.Lerp(fill, ForgeTheme.PillOnHover, h);
        fill = Color.Lerp(fill, solid ? ForgeTheme.PillOnPressed : ForgeTheme.PillPressed, p);
        if (!enabledPill) fill = ForgeTheme.PillDisabled;
        if (surface.color != fill) surface.color = fill;
        // The lit quiet pill is told apart by a thin ring; an off pill carries a hairline highlight that brightens on hover.
        float ring = on && quiet ? 0.7f : solid ? 0f : enabledPill ? Mathf.Lerp(ForgeTheme.PillRing, ForgeTheme.PillRingHover, h) : 0f;
        if (!Mathf.Approximately(surface.Outline, ring)) surface.Outline = ring;
        Color text = solid ? ForgeTheme.PillOnText : ForgeTheme.PillOffText;
        if (!enabledPill) text = ForgeTheme.TextTertiary;
        if (label.color != text) label.color = text;
        ForgeTheme.DarkOnLight(label, solid && enabledPill, ForgeTheme.DarkPillDilate);
        float scale = Mathf.Lerp(1f, ForgeTheme.PressScale, p);
        if (transform.localScale.x != scale) transform.localScale = new Vector3(scale, scale, 1f);
    }
}
