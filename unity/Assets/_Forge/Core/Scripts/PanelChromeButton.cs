using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// One of a panel's built-in window buttons (<see cref="PanelChrome"/>: close, minimise). Made for hands first:
/// <list type="bullet">
/// <item>It acts on the release of a press that started on it, wherever a pinching hand's ray has drifted by then
/// (a pinch twitches the ray, and uGUI's click wants the release over the same object, so a plain Button often does
/// nothing on the headset). A press held longer than <see cref="CancelSeconds"/> and let go away from it cancels.</item>
/// <item>It owns the drag of its own press, so neither the title bar's move handle nor a ScrollRect behind it can take the
/// press over when the ray moves a few pixels (which would cancel a Button's click).</item>
/// </list>
/// Works the same with the mouse (DesktopRig), an Interaction SDK ray (pinch or trigger) and a poke. Its own RectTransform is
/// the hit area (at least 30 mm); the visible disc inside it can be smaller.
/// </summary>
public class PanelChromeButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler,
    IBeginDragHandler, IDragHandler, IEndDragHandler
{
    /// <summary>A press held this long (s) and let go away from the button does nothing.</summary>
    public const float CancelSeconds = 1.2f;
    /// <summary>Released within this distance of the button's centre (in its canvas units, mm) still counts as on it.</summary>
    const float SlopUnits = 60f;

    /// <summary>Pressed and let go: do what the button is for.</summary>
    public event Action Pressed;

    /// <summary>The visible disc; brightens while pointed at, more while pressed.</summary>
    public Graphic Disc { get; set; }
    /// <summary>The glyph strokes (an X, a bar); drawn brighter while pointed at.</summary>
    public Graphic[] Marks { get; set; }
    public Color Rest { get; set; } = new Color(1f, 1f, 1f, 0.14f);
    public Color Hover { get; set; } = new Color(1f, 1f, 1f, 0.26f);
    public Color Down { get; set; } = new Color(1f, 0.42f, 0.38f, 0.9f);
    public Color MarkRest { get; set; } = new Color(1f, 1f, 1f, 0.85f);
    public Color MarkHover { get; set; } = Color.white;

    /// <summary>How many times it has fired (checks and tests).</summary>
    public int Fired { get; private set; }

    bool armed;
    float downAt;
    int hovers;

    public RectTransform Rect => (RectTransform)transform;

    /// <summary>Press it from code (voice, tests): same as a tap.</summary>
    public void Press() => Fire();

    public void OnPointerEnter(PointerEventData e)
    {
        hovers++;
        Refresh();
    }

    public void OnPointerExit(PointerEventData e)
    {
        hovers = Mathf.Max(0, hovers - 1);
        Refresh();
    }

    public void OnPointerDown(PointerEventData e)
    {
        armed = true;
        downAt = Time.unscaledTime;
        Refresh();
    }

    public void OnPointerUp(PointerEventData e)
    {
        if (!armed) return;
        armed = false;
        Refresh();
        if (Time.unscaledTime - downAt <= CancelSeconds || ReleasedOnMe(e)) Fire();
    }

    // Taking the drag keeps the press ours (see the summary); nothing moves.
    public void OnBeginDrag(PointerEventData e) { }
    public void OnDrag(PointerEventData e) { }
    public void OnEndDrag(PointerEventData e) { }

    bool ReleasedOnMe(PointerEventData e)
    {
        GameObject over = e.pointerCurrentRaycast.gameObject;
        if (over != null && over.transform.IsChildOf(transform)) return true;
        if (!e.pointerCurrentRaycast.isValid) return false;
        Vector3 local = transform.InverseTransformPoint(e.pointerCurrentRaycast.worldPosition);
        return ((Vector2)local - Rect.rect.center).sqrMagnitude < SlopUnits * SlopUnits;
    }

    void Fire()
    {
        Fired++;
        try
        {
            Pressed?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }

    void OnDisable()
    {
        armed = false;
        hovers = 0;
        Refresh();
    }

    void Refresh()
    {
        if (Disc != null) Disc.color = armed ? Down : hovers > 0 ? Hover : Rest;
        if (Marks == null) return;
        Color mark = armed || hovers > 0 ? MarkHover : MarkRest;
        for (int i = 0; i < Marks.Length; i++)
            if (Marks[i] != null) Marks[i].color = mark;
    }

    /// <summary>Restyle (the look lives with ForgeTheme; this is so a module or the LOOK pass can retint it).</summary>
    public void Restyle() => Refresh();
}
