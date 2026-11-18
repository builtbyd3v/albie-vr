using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The window buttons every <see cref="ForgePanel"/> gets without asking: a Close (X) and a Minimise (−) at the right end
/// of the title bar, always shown (not only while pointed at), 44 mm targets 6 mm apart, drawn above everything else on the
/// panel so content can never cover them. A panel with no title bar (a frameless card, an orb, a toast, the first-run dot)
/// gets a small close chip in its top-right corner instead, inside its bounds so the Interaction SDK's ray and poke reach it.
/// The buttons are <see cref="PanelChromeButton"/>s: they act on the release of a pinch even when the ray has drifted.
/// <para>
/// What the buttons do is up to the panel (<see cref="ForgePanel.RequestClose"/>, <see cref="ForgePanel.Minimise"/>). This
/// class also keeps what was closed by the default close (hidden, so the Spaces list can open it again) and what was thrown
/// away with a flick (hidden for <see cref="UndoSeconds"/>, then really closed, unless Spaces brings it back first).
/// </para>
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ForgePanel))]
public class PanelChrome : MonoBehaviour
{
    /// <summary>Title-bar button hit size and visible disc (canvas units: mm on a 1 mm canvas).</summary>
    public const float ButtonSize = 44f, ButtonDisc = 34f, ButtonGap = 6f, RightMargin = 10f;
    /// <summary>The close chip of a panel with no title bar.</summary>
    public const float ChipSize = 40f, ChipDisc = 28f, ChipInset = 4f;
    /// <summary>The smallest a close control may be in the world (m), whatever the panel's scale.</summary>
    public const float MinWorldSize = 0.032f;
    /// <summary>A thrown panel can be brought back from the Spaces list this long (s) before it really closes.</summary>
    public const float UndoSeconds = 20f;

    const float HideSeconds = 0.18f, ThrowSeconds = 0.32f, ThrowMeters = 0.6f;

    ForgePanel panel;
    RectTransform root;
    PanelChromeButton close, minimise;
    Transform titleBar, background;
    bool header, lastHeader, built;
    float placedScale;  // the panel's world scale the buttons were last sized for
    float placedSize = ButtonSize;  // the title-bar buttons' size then (canvas units)

    public PanelChromeButton CloseButton => close;
    public PanelChromeButton MinimiseButton => minimise;
    /// <summary>The object holding the buttons (last child of the panel).</summary>
    public RectTransform Root => root;
    /// <summary>The buttons sit in the title bar (else: the corner chip).</summary>
    public bool InHeader => header;

    /// <summary>
    /// How much of the header's right end the buttons use (canvas units), for the title and the Face me / Straight pills
    /// to keep clear of. 0 when there is no title bar.
    /// </summary>
    public float ReservedWidth
    {
        get
        {
            if (!header) return 0f;
            float w = RightMargin;
            if (close != null && close.gameObject.activeSelf) w += placedSize + ButtonGap;
            if (minimise != null && minimise.gameObject.activeSelf) w += placedSize + ButtonGap;
            return w > RightMargin ? w + 2f : 0f;
        }
    }

    public static PanelChrome Of(ForgePanel panel)
    {
        if (panel == null) return null;
        var chrome = panel.GetComponent<PanelChrome>();
        if (chrome == null) chrome = panel.gameObject.AddComponent<PanelChrome>();
        chrome.Build();
        return chrome;
    }

    void Awake() => panel = GetComponent<ForgePanel>();

    /// <summary>Builds the buttons (once) and lays them out for the panel as it is now.</summary>
    public void Build()
    {
        if (panel == null) panel = GetComponent<ForgePanel>();
        if (!built)
        {
            built = true;
            titleBar = transform.Find("TitleBar");
            background = transform.Find("Background");
            root = NewRect("PanelChrome", (RectTransform)transform);
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = root.offsetMax = Vector2.zero;
            close = NewButton("Close", root, cross: true);
            close.Pressed += () => { if (panel != null) panel.RequestClose(); };
            minimise = NewButton("Minimise", root, cross: false);
            minimise.Pressed += () => { if (panel != null) panel.Minimise(); };
        }
        Refresh();
    }

    /// <summary>Shows, hides and places the buttons for the panel's title bar and its Closable / Minimisable now.</summary>
    public void Refresh()
    {
        if (!built || panel == null) return;
        header = HasHeader();
        lastHeader = header;
        // An orb (a minimised window's dot) closes only once its owner has said what that means: closing what it stands for.
        bool canClose = panel.Closable && (panel.CloseHandler != null || GetComponent<PanelOrb>() == null);
        bool canMin = header && panel.Minimisable;
        close.gameObject.SetActive(canClose);
        minimise.gameObject.SetActive(canMin);

        Layout(canClose, canMin);
        root.SetAsLastSibling();
        panel.OnChromeChanged();
    }

    // Sizes and places the buttons for the panel's world scale now (at least MinWorldSize in the world).
    void Layout(bool canClose, bool canMin)
    {
        float unitsPerMeter = UnitsPerMeter();
        placedScale = Mathf.Abs(transform.lossyScale.x);
        if (header)
        {
            float size = Mathf.Max(ButtonSize, MinWorldSize * unitsPerMeter);
            placedSize = size;
            float disc = size * (ButtonDisc / ButtonSize);
            float y = -ForgeTheme.PanelHeader * 0.5f;
            float x = -RightMargin - size * 0.5f;
            if (canClose)
            {
                Place(close, new Vector2(x, y), size, disc);
                x -= size + ButtonGap;
            }
            if (canMin) Place(minimise, new Vector2(x, y), size, disc);
        }
        else if (canClose)
        {
            Vector2 panelSize = ((RectTransform)transform).rect.size;
            float size = Mathf.Max(ChipSize, MinWorldSize * unitsPerMeter);
            // Never more than half the panel's short side (a 64 mm orb keeps most of itself to tap).
            float shortSide = Mathf.Min(panelSize.x, panelSize.y);
            if (shortSide > 1f) size = Mathf.Min(size, Mathf.Max(MinWorldSize * unitsPerMeter, shortSide * 0.5f));
            float disc = size * (ChipDisc / ChipSize);
            float inset = ChipInset + size * 0.5f;
            Place(close, new Vector2(-inset, -inset), size, disc);
        }
    }

    // The canvas units in a metre where the panel stands now (1000 on a 1 mm canvas at scale 1).
    float UnitsPerMeter()
    {
        float metersPerUnit = Mathf.Abs(transform.lossyScale.x);
        return metersPerUnit > 1e-7f ? 1f / metersPerUnit : 1f / ForgePanel.MetersPerUnit;
    }

    bool HasHeader()
    {
        if (titleBar == null || !titleBar.gameObject.activeSelf) return false;
        if (background != null && !background.gameObject.activeSelf) return false;
        var rt = titleBar as RectTransform;
        return rt == null || rt.rect.height >= ForgeTheme.PanelHeader * 0.6f;
    }

    // Cheap, allocation-free: stay the top-most child (so nothing added later covers the buttons) and follow a title bar
    // a module hid or showed after the panel was built.
    void LateUpdate()
    {
        if (!built) return;
        if (root.GetSiblingIndex() != transform.childCount - 1) root.SetAsLastSibling();
        if (HasHeader() != lastHeader) Refresh();
        // The 32 mm minimum is worked out from the panel's world scale. A panel built while it was still unfolding (a mode
        // grows out of its dock ring from 4% scale) had its buttons sized for that tiny scale: 25 times too big once it
        // settled (the giant X discs over the CAD, Code, Train and Messages panels in the look set). Size them again
        // whenever the scale has moved on by more than 2% (one float compare a frame, nothing allocated).
        else if (Mathf.Abs(Mathf.Abs(transform.lossyScale.x) - placedScale) > placedScale * 0.02f)
        {
            float before = placedSize;
            Layout(close.gameObject.activeSelf, minimise.gameObject.activeSelf);
            // The buttons' width changed (only while a panel scales through the 32 mm minimum, an unfold): the title and the
            // pills make room again, or the title stays squeezed to nothing (it did, on every mode panel).
            if (Mathf.Abs(placedSize - before) > 0.5f) panel.OnChromeChanged();
        }
    }

    static void Place(PanelChromeButton button, Vector2 center, float size, float disc)
    {
        RectTransform rt = button.Rect;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = center;
        rt.sizeDelta = new Vector2(size, size);
        if (button.Disc != null) button.Disc.rectTransform.sizeDelta = new Vector2(disc, disc);
        if (button.Marks == null) return;
        float k = disc / ButtonDisc;
        bool cross = button.Marks.Length == 2;
        for (int i = 0; i < button.Marks.Length; i++)
            button.Marks[i].rectTransform.sizeDelta = cross ? new Vector2(16f * k, 2.6f * k) : new Vector2(14f * k, 2.6f * k);
    }

    // A clear hit square (the target), a round disc inside it and the glyph: two crossed bars, or one bar for minimise.
    static PanelChromeButton NewButton(string name, RectTransform parent, bool cross)
    {
        RectTransform rt = NewRect(name, parent);
        var hit = rt.gameObject.AddComponent<Image>();
        hit.color = Color.clear;  // still takes rays, pokes and the mouse
        var button = rt.gameObject.AddComponent<PanelChromeButton>();

        RectTransform discRect = NewRect("Disc", rt);
        var disc = discRect.gameObject.AddComponent<ForgeSurface>();
        disc.Pill = true;
        disc.Outline = 0.35f;
        disc.raycastTarget = false;
        button.Disc = disc;

        int marks = cross ? 2 : 1;
        var strokes = new Graphic[marks];
        for (int i = 0; i < marks; i++)
        {
            RectTransform m = NewRect(cross ? "Stroke" : "Bar", rt);
            if (cross) m.localRotation = Quaternion.Euler(0f, 0f, i == 0 ? 45f : -45f);
            var bar = m.gameObject.AddComponent<ForgeSurface>();
            bar.Pill = true;
            bar.raycastTarget = false;
            strokes[i] = bar;
        }
        button.Marks = strokes;
        if (cross) button.Down = new Color(1f, 0.42f, 0.38f, 0.9f);
        else button.Down = new Color(1f, 1f, 1f, 0.45f);
        button.Restyle();
        return button;
    }

    static RectTransform NewRect(string name, RectTransform parent)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        return rt;
    }

    // ---------------------------------------------------------------- closed and thrown panels

    /// <summary>A panel the default close hid, or one thrown away and waiting to really close.</summary>
    public sealed class Closed
    {
        public ForgePanel panel;
        public string title;
        public bool thrown;
        public float at;
    }

    static readonly List<Closed> closed = new List<Closed>();
    const int MaxRemembered = 12;
    static ForgeRunner runner;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        closed.Clear();
        runner = null;
    }

    /// <summary>
    /// What was closed and can still come back, newest last: panels the default close hid, and thrown ones that have not
    /// closed for real yet. Stale entries (destroyed, or shown again by their owner) are dropped as it is read.
    /// </summary>
    public static IReadOnlyList<Closed> Remembered
    {
        get
        {
            Prune();
            return closed;
        }
    }

    static void Prune()
    {
        for (int i = closed.Count - 1; i >= 0; i--)
            if (closed[i].panel == null || closed[i].panel.gameObject.activeSelf) closed.RemoveAt(i);
    }

    /// <summary>The default close: the panel fades and shrinks a touch, then is hidden, and remembered for Spaces to reopen.</summary>
    public static void HideClosed(ForgePanel p)
    {
        if (p == null) return;
        Remember(p, false);
        Fade(p, HideSeconds, Vector3.zero, () => { if (p != null) p.gameObject.SetActive(false); });
    }

    /// <summary>
    /// Thrown away with a flick: it flies off along <paramref name="direction"/> (world, m/s) fading out, is hidden, and
    /// really closes <see cref="UndoSeconds"/> later unless it was brought back (<see cref="Reopen"/>) first.
    /// </summary>
    public static void Throw(ForgePanel p, Vector3 direction)
    {
        if (p == null) return;
        Closed entry = Remember(p, true);
        Vector3 travel = direction.sqrMagnitude > 1e-6f ? direction.normalized * ThrowMeters : Vector3.zero;
        Fade(p, ThrowSeconds, travel, () =>
        {
            if (p == null) return;
            p.gameObject.SetActive(false);
            Run(CloseLater(entry));
        });
    }

    /// <summary>Opens a remembered panel again where it was. False if it is gone.</summary>
    public static bool Reopen(ForgePanel p)
    {
        for (int i = closed.Count - 1; i >= 0; i--)
            if (closed[i].panel == p) closed.RemoveAt(i);
        if (p == null) return false;
        p.gameObject.SetActive(true);
        var group = p.GetComponent<CanvasGroup>();
        if (group != null)
        {
            group.alpha = 1f;
            group.blocksRaycasts = true;
        }
        if (p.Handles != null) p.Handles.Busy = false;
        return true;
    }

    /// <summary>Opens the newest remembered panel again (undo the last close or throw). False if there is none.</summary>
    public static bool ReopenLast()
    {
        Prune();
        return closed.Count > 0 && Reopen(closed[closed.Count - 1].panel);
    }

    /// <summary>Thrown panels close for real now (Close everything), and the record of hidden ones is dropped.</summary>
    public static void FinishAll()
    {
        Prune();
        for (int i = closed.Count - 1; i >= 0; i--)
        {
            Closed c = closed[i];
            closed.RemoveAt(i);
            if (c.thrown && c.panel != null) c.panel.CloseNow();
        }
        closed.Clear();
    }

    /// <summary>Stops remembering a panel (closed for real by its owner, or from the Spaces list).</summary>
    public static void Forget(ForgePanel p)
    {
        for (int i = closed.Count - 1; i >= 0; i--)
            if (closed[i].panel == p) closed.RemoveAt(i);
    }

    static Closed Remember(ForgePanel p, bool thrown)
    {
        Forget(p);
        var entry = new Closed { panel = p, title = string.IsNullOrEmpty(p.Title) ? p.name : p.Title, thrown = thrown, at = Time.unscaledTime };
        closed.Add(entry);
        while (closed.Count > MaxRemembered)
        {
            Closed oldest = closed[0];
            closed.RemoveAt(0);
            if (oldest.thrown && oldest.panel != null) oldest.panel.CloseNow();
        }
        return entry;
    }

    static IEnumerator CloseLater(Closed entry)
    {
        float until = Time.unscaledTime + UndoSeconds;
        while (Time.unscaledTime < until) yield return null;
        if (!closed.Contains(entry)) yield break;  // brought back, or already finished
        closed.Remove(entry);
        if (entry.panel != null && !entry.panel.gameObject.activeSelf) entry.panel.CloseNow();
    }

    static void Fade(ForgePanel p, float seconds, Vector3 travel, System.Action done)
    {
        var handles = p.Handles;
        if (handles != null) handles.Cancel();
        if (!Application.isPlaying || !p.gameObject.activeInHierarchy)
        {
            done();
            return;
        }
        var group = p.GetComponent<CanvasGroup>();
        if (group == null) group = p.gameObject.AddComponent<CanvasGroup>();
        Transform t = p.transform;
        Vector3 scale = t.localScale, from = t.position;
        float alpha = group.alpha;
        group.blocksRaycasts = false;  // on its way out: presses go past it
        if (handles != null) handles.Busy = true;
        Run(ForgeTween.Run(seconds, 0f, k =>
        {
            if (t == null) return;
            float e = ForgeTween.EaseInCubic(k);
            t.localScale = scale * Mathf.Lerp(1f, 0.92f, e);
            t.position = from + travel * ForgeTween.EaseOutCubic(k);
            group.alpha = alpha * (1f - Mathf.SmoothStep(0f, 1f, k));
        }, () =>
        {
            if (t == null) return;
            // Put back as it was, hidden, so it comes back looking right.
            t.localScale = scale;
            t.position = from;
            group.alpha = alpha;
            group.blocksRaycasts = true;
            if (handles != null) handles.Busy = false;
            done();
        }));
    }

    static void Run(IEnumerator routine)
    {
        if (!Application.isPlaying) return;
        if (runner == null)
        {
            var go = new GameObject("PanelChromeRunner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            runner = go.AddComponent<ForgeRunner>();
        }
        runner.StartCoroutine(routine);
    }

}
