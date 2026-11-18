using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Play mode, every window open now (each <see cref="ForgePanel"/>, any module): text that overlaps other text, text that
/// overlaps a button, toggle or slider it isn't part of, and text that runs outside its window. Measured on what is drawn
/// (each text's rendered bounds, clipped to its scroll area), in the window's own canvas units, with the window's hover
/// controls (Straight / Face me) shown, as they are whenever a ray points at the header. On the headset the Welcome card's
/// pills sat on its title: this is the check that would have caught it. <see cref="Forge.OS.Editor.OSLookShots"/> runs it on
/// every screen it captures (overlap_audit.txt); <c>unity command eval 'return WindowOverlapAudit.Report("now");'</c> runs it
/// on whatever is open.
/// </summary>
public static class WindowOverlapAudit
{
    /// <summary>Overlaps smaller than this (canvas units, both ways) are a glyph's edge, not a collision.</summary>
    public const float MinOverlap = 2f;
    /// <summary>How far text may reach past its window's edge before it counts as outside.</summary>
    public const float OutsideSlack = 4f;

    struct Item
    {
        public Rect rect;
        public Transform t;
        public Selectable owner;  // the control a text belongs to, or the control itself
        public string label;
    }

    static readonly List<Item> texts = new List<Item>();
    static readonly List<Item> controls = new List<Item>();
    static readonly List<TMP_Text> textScratch = new List<TMP_Text>();
    static readonly List<Selectable> controlScratch = new List<Selectable>();
    static readonly Vector3[] corners = new Vector3[4];
    static readonly FieldInfo HoverGroup = typeof(PanelManipulator).GetField("controls", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>All findings for the windows open now, one line each ("screen | window | KIND ..."), empty when clean.</summary>
    public static List<string> Run(string screen)
    {
        var found = new List<string>();
        foreach (ForgePanel panel in Object.FindObjectsByType<ForgePanel>())
        {
            if (panel == null || !panel.isActiveAndEnabled) continue;
            try { AuditPanel(screen, panel, found); }
            catch (System.Exception e) { found.Add($"{screen} | {panel.gameObject.name} | AUDIT ERROR {e.GetType().Name}: {e.Message}"); }  // never stops a look set
        }
        return found;
    }

    /// <summary>For an eval: the findings joined, or "clean".</summary>
    public static string Report(string screen)
    {
        var found = Run(screen);
        return found.Count == 0 ? "clean: no overlaps in " + Object.FindObjectsByType<ForgePanel>().Length + " windows" : string.Join("\n", found);
    }

    static void AuditPanel(string screen, ForgePanel panel, List<string> found)
    {
        var root = (RectTransform)panel.transform;
        // The hover controls as they look when pointed at (their group sits at alpha 0 otherwise).
        CanvasGroup hover = panel.Handles != null && HoverGroup != null ? HoverGroup.GetValue(panel.Handles) as CanvasGroup : null;
        float hoverAlpha = hover != null ? hover.alpha : 1f;
        if (hover != null) hover.alpha = 1f;
        try
        {
            Canvas.ForceUpdateCanvases();
            Collect(root);
            Rect bounds = root.rect;
            string where = panel.gameObject.name + (string.IsNullOrEmpty(panel.Title) ? "" : " [" + panel.Title + "]");
            for (int i = 0; i < texts.Count; i++)
            {
                Item a = texts[i];
                for (int j = i + 1; j < texts.Count; j++)
                {
                    Item b = texts[j];
                    if (a.owner != null && a.owner == b.owner) continue;  // a button's own label and icon
                    if (a.t.IsChildOf(b.t) || b.t.IsChildOf(a.t)) continue;
                    if (Overlap(a.rect, b.rect, out Vector2 size))
                        found.Add($"{screen} | {where} | TEXT x TEXT \"{a.label}\" x \"{b.label}\" ({size.x:F0}x{size.y:F0})");
                }
                foreach (Item c in controls)
                {
                    if (a.owner == c.owner || a.t.IsChildOf(c.t)) continue;
                    if (Overlap(a.rect, c.rect, out Vector2 size))
                        found.Add($"{screen} | {where} | TEXT x CONTROL \"{a.label}\" x [{c.label}] ({size.x:F0}x{size.y:F0})");
                }
                bool hoverPart = hover != null && a.t.IsChildOf(hover.transform);  // the pills may sit above the window on purpose
                if (!hoverPart && (a.rect.xMin < bounds.xMin - OutsideSlack || a.rect.xMax > bounds.xMax + OutsideSlack
                                   || a.rect.yMin < bounds.yMin - OutsideSlack || a.rect.yMax > bounds.yMax + OutsideSlack))
                    found.Add($"{screen} | {where} | OUTSIDE \"{a.label}\"");
            }
        }
        finally
        {
            if (hover != null) hover.alpha = hoverAlpha;
            texts.Clear();
            controls.Clear();
        }
    }

    static void Collect(RectTransform root)
    {
        root.GetComponentsInChildren(false, textScratch);
        foreach (TMP_Text t in textScratch)
        {
            if (t == null || string.IsNullOrWhiteSpace(t.text) || !t.gameObject.activeInHierarchy || Alpha(t.transform) * t.color.a < 0.05f) continue;
            t.ForceMeshUpdate();
            Bounds b = t.textBounds;
            if (b.size.x < 0.5f || b.size.y < 0.5f) continue;
            Rect r = ToPanel(root, t.transform, b.min, b.max);
            if (!Clip(root, t.transform, ref r)) continue;  // scrolled out of view
            texts.Add(new Item { rect = r, t = t.transform, owner = t.GetComponentInParent<Selectable>(), label = Short(t.text) });
        }
        textScratch.Clear();
        float area = root.rect.width * root.rect.height;
        root.GetComponentsInChildren(false, controlScratch);
        foreach (Selectable s in controlScratch)
        {
            if (s == null || !s.isActiveAndEnabled || Alpha(s.transform) < 0.05f) continue;
            if (s.targetGraphic != null && s.targetGraphic.color.a < 0.05f && s.GetComponentInChildren<TMP_Text>() == null) continue;  // an invisible hit area
            var rt = (RectTransform)s.transform;
            rt.GetLocalCorners(corners);
            Rect r = ToPanel(root, rt, corners[0], corners[2]);
            if (r.width * r.height > area * 0.6f) continue;  // a whole-window surface, not a control in it
            if (!Clip(root, rt, ref r)) continue;
            var label = s.GetComponentInChildren<TMP_Text>();
            controls.Add(new Item { rect = r, t = s.transform, owner = s, label = s.GetType().Name + (label != null ? " " + Short(label.text) : "") });
        }
        controlScratch.Clear();
    }

    // A local box of `t` (min, max corners) as a rect in the window's own space.
    static Rect ToPanel(RectTransform root, Transform t, Vector3 min, Vector3 max)
    {
        Vector3 a = root.InverseTransformPoint(t.TransformPoint(min));
        Vector3 b = root.InverseTransformPoint(t.TransformPoint(max));
        return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
    }

    // Trims to every scroll area (RectMask2D / Mask) above it inside the window; false when nothing is left in view.
    static bool Clip(RectTransform root, Transform t, ref Rect r)
    {
        for (Transform p = t.parent; p != null && p != root.parent; p = p.parent)
        {
            if (p.GetComponent<RectMask2D>() == null && p.GetComponent<Mask>() == null) continue;
            var mrt = (RectTransform)p;
            mrt.GetLocalCorners(corners);
            Rect m = ToPanel(root, mrt, corners[0], corners[2]);
            r = Rect.MinMaxRect(Mathf.Max(r.xMin, m.xMin), Mathf.Max(r.yMin, m.yMin), Mathf.Min(r.xMax, m.xMax), Mathf.Min(r.yMax, m.yMax));
            if (r.width < 1f || r.height < 1f) return false;
        }
        return true;
    }

    static bool Overlap(Rect a, Rect b, out Vector2 size)
    {
        size = new Vector2(Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin), Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin));
        return size.x > MinOverlap && size.y > MinOverlap;
    }

    // What the canvas groups above it leave of its alpha (one ignoring its parents stops the walk).
    static float Alpha(Transform t)
    {
        float alpha = 1f;
        for (Transform p = t; p != null; p = p.parent)
        {
            var g = p.GetComponent<CanvasGroup>();
            if (g == null) continue;
            alpha *= g.alpha;
            if (g.ignoreParentGroups) break;
        }
        return alpha;
    }

    static string Short(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace('\n', ' ');
        return text.Length > 36 ? text.Substring(0, 36) + "…" : text;
    }
}
