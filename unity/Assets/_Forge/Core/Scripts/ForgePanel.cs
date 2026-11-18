using Oculus.Interaction.Surfaces;
using TMPro;
using UnityEngine;

/// <summary>
/// A floating flat window: world-space canvas with a graphite frame, a header and a content area.
/// Put your module's UI under <see cref="Content"/>. 1 canvas unit = 1 mm, so a 600 x 400 rect is 60 x 40 cm.
/// The header shows <see cref="Title"/>, an optional <see cref="Subtitle"/> and the mode badge (<see cref="SetBadge"/>);
/// the look comes from <see cref="ForgeTheme"/>.
/// The prefab (Core/Prefabs/ForgePanel.prefab) already has Interaction SDK ray + poke wired to the canvas,
/// so Buttons, Sliders, ScrollRects etc. work with hands and controllers.
/// To resize it, set <see cref="SizeMeters"/> (not the transform's scale): that keeps the ray/poke hit area in step.
/// In the desk workspace, <see cref="PanelManipulator"/> lets the user move, resize and turn it.
/// </summary>
[RequireComponent(typeof(Canvas))]
public class ForgePanel : MonoBehaviour
{
    public const float MetersPerUnit = 0.001f;

    /// <summary>Every enabled panel, for hands aiming at windows (<see cref="GestureEngine"/>).</summary>
    public static readonly System.Collections.Generic.List<ForgePanel> All = new System.Collections.Generic.List<ForgePanel>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => All.Clear();

    PanelManipulator handles;
    /// <summary>This panel's move and resize handles, or null if it has none (a card, a timer, a minimized dot).</summary>
    public PanelManipulator Handles => handles != null ? handles : handles = GetComponent<PanelManipulator>();

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    // ---------------------------------------------------------------- close and minimise
    // Every panel gets a Close (X) and, with a title bar, a Minimise (−) without asking (PanelChrome builds them in Start).

    bool closable = true, minimisable = true;

    /// <summary>
    /// Whether the panel shows a Close button and can be closed by hand (the X, a throw). On by default; turn it off only for
    /// something that must not close, with <see cref="SetClosable"/> and a reason (the dock, a modal sheet).
    /// </summary>
    public bool Closable
    {
        get => closable;
        set
        {
            closable = value;
            if (chrome != null) chrome.Refresh();
        }
    }

    /// <summary>Why <see cref="Closable"/> is off (for the checks and the Spaces list).</summary>
    public string NotClosableReason { get; private set; }

    /// <summary>Turns the Close off (or on again) and says why, e.g. SetClosable(false, "the dock: it goes with the desk").</summary>
    public void SetClosable(bool value, string reason)
    {
        NotClosableReason = value ? null : reason;
        Closable = value;
    }

    /// <summary>Whether a panel with a title bar shows the Minimise (−) button. On by default.</summary>
    public bool Minimisable
    {
        get => minimisable;
        set
        {
            minimisable = value;
            if (chrome != null) chrome.Refresh();
        }
    }

    /// <summary>Raised whenever the panel is closed by the user (X, throw, Spaces), before it closes. Listen to clean up.</summary>
    public event System.Action CloseRequested;

    /// <summary>
    /// Replaces what closing does (a module that closes its own way: an artifact, a mode, a card). When null, the default
    /// runs: the OS window manager closes a window it tracks (<see cref="CloseRouter"/>), the workspace closes an item of the
    /// open mode, and anything else is hidden and remembered so the Spaces list can open it again.
    /// </summary>
    public System.Action CloseHandler
    {
        get => closeHandler;
        set
        {
            closeHandler = value;
            if (chrome != null) chrome.Refresh();  // an orb shows its close chip only once someone says what closing it means
        }
    }
    System.Action closeHandler;

    /// <summary>Set by the OS: closes a panel it tracks and returns true, or returns false to let the default go on.</summary>
    public static System.Func<ForgePanel, bool> CloseRouter;

    PanelChrome chrome;
    /// <summary>The built-in window buttons (null until the panel's first frame, or call <see cref="PanelChrome.Of"/>).</summary>
    public PanelChrome Chrome => chrome != null ? chrome : chrome = GetComponent<PanelChrome>();

    /// <summary>The user asked to close it (the X, a throw, the Spaces list): does nothing when it isn't <see cref="Closable"/>.</summary>
    public void RequestClose()
    {
        if (!closable) return;
        CloseNow();
    }

    /// <summary>
    /// Closes it whatever <see cref="Closable"/> says (a thrown panel whose undo ran out, Close everything): raises
    /// <see cref="CloseRequested"/>, then runs <see cref="CloseHandler"/> or the default.
    /// </summary>
    public void CloseNow()
    {
        PanelChrome.Forget(this);
        var gesture = GetComponent<GestureWindow>();
        if (gesture != null) gesture.ForgetDot();  // closing a minimised panel takes its dot too
        try
        {
            CloseRequested?.Invoke();
            if (closeHandler != null)
            {
                closeHandler();
                return;
            }
            if (CloseRouter != null && CloseRouter(this)) return;
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
        }
        var workspace = Workspace.Current;
        if (workspace != null && workspace.CloseItem(gameObject)) return;
        if (gameObject.activeSelf) PanelChrome.HideClosed(this);
    }

    /// <summary>Thrown away with a flick (<see cref="PanelManipulator"/>): flies off, hidden, and closes for real a little later
    /// unless the Spaces list brings it back (<see cref="PanelChrome.UndoSeconds"/>). <paramref name="velocity"/> in m/s.</summary>
    public void ThrowAway(Vector3 velocity)
    {
        if (!closable || !gameObject.activeInHierarchy) return;
        if (ThrowHandler != null)
        {
            PanelChrome.Forget(this);
            try { ThrowHandler(velocity); }
            catch (System.Exception e) { Debug.LogException(e); }
            return;
        }
        PanelChrome.Throw(this, velocity);
    }

    /// <summary>Replaces what a throw does (an artifact's panel throws the whole artifact away, with its own undo). Null: the default throw.</summary>
    public System.Action<Vector3> ThrowHandler { get; set; }

    /// <summary>Into a dot where it is (tap the dot to open it again), or the panel's own minimise if it has one (the notes).</summary>
    public bool Minimise()
    {
        if (!gameObject.activeInHierarchy) return false;
        var engine = GestureEngine.Current;
        var workspace = Workspace.Current;
        MonoBehaviour runner = engine != null && engine.isActiveAndEnabled ? (MonoBehaviour)engine
                             : workspace != null && workspace.isActiveAndEnabled ? workspace : null;
        Transform head = workspace != null && workspace.presence != null && workspace.presence.head != null ? workspace.presence.head
                       : Camera.main != null ? Camera.main.transform : null;
        string said = runner != null && head != null ? GestureWindow.Of(this).Close(runner, head) : "Nowhere to minimize to";
        if (said == null) return true;
        if (said != "Nowhere to minimize to") return false;  // moving, or already a dot
        PanelChrome.HideClosed(this);  // no dot to make in this scene: hidden, and the Spaces list brings it back
        return true;
    }

    /// <summary>Out of its dot again, if it was minimised.</summary>
    public void Restore()
    {
        var gesture = GetComponent<GestureWindow>();
        if (gesture == null || !gesture.IsMinimized) return;
        var engine = GestureEngine.Current;
        MonoBehaviour runner = engine != null && engine.isActiveAndEnabled ? (MonoBehaviour)engine : Workspace.Current;
        if (runner != null) gesture.Restore(runner);
        else gameObject.SetActive(true);
    }

    /// <summary>Minimised into its dot.</summary>
    public bool IsMinimised
    {
        get
        {
            var gesture = GetComponent<GestureWindow>();
            return gesture != null && gesture.IsMinimized;
        }
    }

    void Start()
    {
        if (Application.isPlaying) chrome = PanelChrome.Of(this);
    }

    /// <summary>PanelChrome moved or showed its buttons: the title and the manipulator's pills keep clear of them.</summary>
    internal void OnChromeChanged()
    {
        ForgeLook.StyleChrome(Chrome);  // design system: the window buttons' colours (look only)
        if (Handles != null) Handles.LayoutTitleButtons();  // the pills first: the title then keeps clear of where they are
        LayoutHeader();
    }

    const float HeaderLeft = 20f, BadgeSize = 40f, BadgePadding = 12f, BadgeGap = 12f, HeaderRight = 180f;

    [Tooltip("Matches the panel IDs in PROJECT_CONTEXT section 7 (e.g. ml-results, cad-params).")]
    public string panelId;

    [SerializeField] TMP_Text titleText;
    [SerializeField] TMP_Text subtitleText;
    [SerializeField] ForgeSurface badge;
    [SerializeField] TMP_Text badgeText;
    [SerializeField] RectTransform content;

    BoundsClipper[] hitAreas;
    float hitMarginMm;
    RectTransform emptyState;
    ForgeSurface emptyRing;
    TMP_Text emptyMark, emptyHeading, emptyCaption;

    public RectTransform Content => content;

    public string Title
    {
        get => titleText != null ? titleText.text : "";
        set
        {
            if (titleText != null) titleText.text = value;
            LayoutHeader();  // a longer title may need the Straight / Face me pills out of its way
        }
    }

    /// <summary>Small second line under the title (e.g. "rocket v1"). Empty hides it.</summary>
    public string Subtitle
    {
        get => subtitleText != null ? subtitleText.text : "";
        set
        {
            if (subtitleText != null) subtitleText.text = value;
            LayoutHeader();
        }
    }

    /// <summary>The mode color this panel belongs to (set with <see cref="SetBadge"/>), for modules to accent with.</summary>
    public Color Accent { get; private set; } = Color.white;

    /// <summary>
    /// The mode badge at the left of the header: a small tile tinted with the mode's color and its short label
    /// (CAD, ML, ...), matching the dock ring. Null or empty hides it.
    /// </summary>
    public void SetBadge(string label, Color accent)
    {
        Accent = accent;
        if (badge == null) return;
        bool show = !string.IsNullOrEmpty(label);
        badge.gameObject.SetActive(show);
        if (show)
        {
            badge.color = ForgeTheme.Tint(accent, ForgeTheme.BadgeFill);
            badge.Outline = ForgeTheme.BadgeRing;
            badge.Pill = true;  // a soft tinted chip, not a framed square that reads as a button
            var rt = (RectTransform)badge.transform;
            float width = BadgeSize;
            if (badgeText != null)
            {
                badgeText.text = label;
                badgeText.color = Color.Lerp(accent, Color.white, ForgeTheme.BadgeTextLift);
                // Wider for longer labels (CODE), so it never truncates.
                width = Mathf.Max(BadgeSize, badgeText.GetPreferredValues(label).x + 2f * BadgePadding);
            }
            rt.sizeDelta = new Vector2(width, BadgeSize);
            rt.anchoredPosition = new Vector2(HeaderLeft + width * 0.5f, rt.anchoredPosition.y);
        }
        LayoutHeader();
    }

    // Title (and subtitle) after the badge, clear of the Straight / Face me pills on the right.
    /// <summary>How far in from the left edge the title and subtitle reach at their natural width (canvas units): what
    /// <see cref="PanelManipulator"/> keeps clear of. Only on a layout change.</summary>
    public float TitleSpan
    {
        get
        {
            if (titleText == null) return 0f;
            bool hasBadge = badge != null && badge.gameObject.activeSelf;
            float left = HeaderLeft + (hasBadge ? ((RectTransform)badge.transform).sizeDelta.x + BadgeGap : 0f);
            float title = string.IsNullOrEmpty(titleText.text) ? 0f : titleText.GetPreferredValues(titleText.text).x;
            float sub = subtitleText != null && !string.IsNullOrEmpty(subtitleText.text) ? subtitleText.GetPreferredValues(subtitleText.text).x : 0f;
            return left + Mathf.Max(title, sub);
        }
    }

    void LayoutHeader()
    {
        if (titleText == null) return;
        if (Handles != null) Handles.LayoutTitleButtons();  // the pills first (in the header, or above it): the title keeps clear
        bool hasBadge = badge != null && badge.gameObject.activeSelf;
        bool hasSubtitle = subtitleText != null && !string.IsNullOrEmpty(subtitleText.text);
        float left = HeaderLeft + (hasBadge ? ((RectTransform)badge.transform).sizeDelta.x + BadgeGap : 0f);
        float right = HeaderReserve(left);
        PlaceInHeader(titleText.rectTransform, left, right, hasSubtitle ? 25f : ForgeTheme.PanelHeader * 0.5f, 22f);
        if (subtitleText != null)
        {
            subtitleText.gameObject.SetActive(hasSubtitle);
            PlaceInHeader(subtitleText.rectTransform, left, right, 44f, 16f);
        }
    }

    // What the header keeps free on the right for the Straight / Face me pills: all of HeaderRight on a wide panel, less on
    // a narrow one so the title always has at least MinHeaderText units (a card with no pills has nothing to clear).
    float HeaderReserve(float left)
    {
        const float MinHeaderText = 120f;
        float width = ((RectTransform)transform).sizeDelta.x;
        // The window buttons (close, minimise) are always there, so the title always clears them.
        float buttons = Chrome != null ? Chrome.ReservedWidth : 0f;
        float reserve = buttons + Mathf.Clamp(width - left - MinHeaderText - buttons, 20f, HeaderRight);
        // The Straight / Face me pills are wider than HeaderRight on some panels (the title ran into "Straight" in the look
        // set): keep clear of where they really are, plus a gap, when that leaves the title its minimum.
        float pills = TitlePillsExtent();
        if (pills > 0f) reserve = Mathf.Max(reserve, Mathf.Min(pills + 12f, width - left - MinHeaderText));
        return reserve;
    }

    static readonly System.Collections.Generic.List<ForgePill> pillScratch = new System.Collections.Generic.List<ForgePill>();

    // How far in from the right edge the title-bar pills reach (canvas units), 0 without any. Only on a layout change.
    float TitlePillsExtent()
    {
        float extent = 0f;
        GetComponentsInChildren(false, pillScratch);
        for (int i = 0; i < pillScratch.Count; i++)
        {
            ForgePill pill = pillScratch[i];
            if (!pill.Quiet || pill.transform.parent == null || pill.transform.parent.parent != transform) continue;  // the manipulator's own pills
            RectTransform rt = pill.Rect;
            if (rt.anchorMin.x < 0.99f || rt.anchorMin.y < 0.99f) continue;  // only the ones laid out from the top-right corner
            if (rt.anchoredPosition.y < -ForgeTheme.PanelHeader || rt.anchoredPosition.y > 0f) continue;  // above the panel: nothing to clear
            extent = Mathf.Max(extent, -rt.anchoredPosition.x + rt.sizeDelta.x * rt.pivot.x);
        }
        pillScratch.Clear();
        return extent;
    }

    static void PlaceInHeader(RectTransform rt, float left, float right, float centerFromTop, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(-(left + right), height);
        rt.anchoredPosition = new Vector2((left - right) * 0.5f, -centerFromTop);
    }

    /// <summary>
    /// A calm placeholder for a panel whose module isn't plugged in yet: a ring in the mode's color around its short
    /// label, the panel's name and a line about what will show up here. Replaces the prefab's placeholder text.
    /// Call after <see cref="SetBadge"/> so it picks up the mode color.
    /// </summary>
    public void ShowEmptyState(string heading, string caption)
    {
        if (content == null) return;
        var placeholder = content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);
        if (emptyState == null) BuildEmptyState();
        emptyRing.color = ForgeTheme.Tint(Accent, 0.08f);
        emptyMark.text = badgeText != null ? badgeText.text : "";
        emptyMark.color = Color.Lerp(Accent, Color.white, 0.55f);
        emptyHeading.text = heading;
        emptyCaption.text = caption;
        emptyState.gameObject.SetActive(true);
    }

    void BuildEmptyState()
    {
        emptyState = NewRect("EmptyState", content);
        emptyState.anchorMin = emptyState.anchorMax = new Vector2(0.5f, 0.5f);
        emptyState.sizeDelta = new Vector2(360f, 150f);
        emptyState.anchoredPosition = new Vector2(0f, 10f);

        var ring = NewRect("Ring", emptyState);
        ring.anchorMin = ring.anchorMax = new Vector2(0.5f, 1f);
        ring.sizeDelta = new Vector2(52f, 52f);
        ring.anchoredPosition = new Vector2(0f, -26f);
        emptyRing = ring.gameObject.AddComponent<ForgeSurface>();
        emptyRing.Pill = true;
        emptyRing.Outline = 0.8f;
        emptyRing.raycastTarget = false;

        emptyMark = EmptyText("Mark", ring, 12f, FontStyles.Bold, new Color(1f, 1f, 1f, 0.9f));
        emptyMark.characterSpacing = 3f;
        emptyMark.rectTransform.anchorMin = Vector2.zero;
        emptyMark.rectTransform.anchorMax = Vector2.one;
        emptyMark.rectTransform.sizeDelta = Vector2.zero;

        emptyHeading = EmptyText("Heading", emptyState, 18f, FontStyles.Bold, ForgeTheme.PanelText);
        emptyHeading.rectTransform.anchoredPosition = new Vector2(0f, -76f);
        emptyHeading.rectTransform.sizeDelta = new Vector2(360f, 24f);

        emptyCaption = EmptyText("Caption", emptyState, 12.5f, FontStyles.Normal, ForgeTheme.PanelSubtext);
        emptyCaption.rectTransform.anchoredPosition = new Vector2(0f, -100f);
        emptyCaption.rectTransform.sizeDelta = new Vector2(330f, 40f);
        emptyCaption.textWrappingMode = TextWrappingModes.Normal;
        emptyCaption.alignment = TextAlignmentOptions.Top;
    }

    static TMP_Text EmptyText(string name, RectTransform parent, float size, FontStyles style, Color color)
    {
        var text = NewRect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
        text.rectTransform.anchorMin = text.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        text.rectTransform.pivot = new Vector2(0.5f, 1f);
        if (ForgeTheme.Font != null) text.font = ForgeTheme.Font;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = color;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.raycastTarget = false;
        return text;
    }

    static RectTransform NewRect(string name, RectTransform parent)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        return rt;
    }

    public Vector2 SizeMeters
    {
        get => ((RectTransform)transform).sizeDelta * MetersPerUnit;
        set
        {
            ((RectTransform)transform).sizeDelta = value / MetersPerUnit;
            SyncHitArea();
        }
    }

    /// <summary>Resizing keeps the width:height ratio (a video stream, say).</summary>
    public bool KeepAspect { get; set; }

    /// <summary>Extra ray/poke hit area around the edges (mm), for handles drawn just outside the panel.</summary>
    public float HitMarginMm
    {
        get => hitMarginMm;
        set
        {
            hitMarginMm = value;
            SyncHitArea();
        }
    }

    void Awake()
    {
        FitPlate();
        SyncHitArea();
        ForgeLook.StyleHeader(titleText, subtitleText);  // design system: the title bar's type
        LayoutHeader();
    }

    // The plate's corner comes from ForgeTheme.PanelCorner, not from what the prefab saved, so one constant restyles every
    // window (the sandboxes too). Its graphic is opaque too: the prefab's 0.9 alpha let the text of a window behind show
    // through this one (Hands read through Play area on the headset).
    void FitPlate()
    {
        var background = transform.Find("Background");
        var plate = background != null ? background.GetComponent<ForgeSurface>() : null;
        if (plate == null || plate.Style != ForgeSurface.Kind.Panel) return;
        plate.Corner = ForgeTheme.PanelCorner;
        Color c = plate.color;
        if (c.a < 1f) plate.color = new Color(c.r, c.g, c.b, 1f);
    }

    // The Interaction SDK only lets rays and pokes hit inside its clippers' boxes, which the prefab saved at the
    // original size. Fit them to the panel (plus the margin) whenever the size changes.
    void SyncHitArea()
    {
        if (hitAreas == null) hitAreas = GetComponentsInChildren<BoundsClipper>(true);
        Vector2 size = ((RectTransform)transform).sizeDelta + Vector2.one * (2f * hitMarginMm);
        foreach (var clipper in hitAreas) clipper.Size = new Vector3(size.x, size.y, clipper.Size.z);
    }
}
