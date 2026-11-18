using System.Collections.Generic;
using System.Text;
using Oculus.Interaction.Surfaces;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Forge > Core > Check Panel Close: every panel can be closed with hands. Instantiates every prefab that holds a
/// <see cref="ForgePanel"/> (module panels, cards, the orb), plus the plain panel framed, frameless and scaled down, builds
/// its <see cref="PanelChrome"/>, and checks that a closable panel has a Close control that is shown, at least 30 mm in the
/// world both ways, inside the panel's bounds and inside every Interaction SDK hit box (so a ray or poke reaches it), the
/// top-most thing on the panel (nothing drawn over it takes the press), and that a press on it closes the panel even when the
/// pinching ray has drifted off it by the release. Edit mode; everything it makes is destroyed again.
/// </summary>
public static class PanelCloseCheck
{
    const float MinMeters = 0.030f;

    [MenuItem("Forge/Core/Check Panel Close")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        int failed = 0, checkedPanels = 0;
        var made = new List<GameObject>();
        try
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Forge" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null || asset.GetComponentInChildren<ForgePanel>(true) == null) continue;
                var go = Object.Instantiate(asset);
                made.Add(go);
                foreach (ForgePanel panel in go.GetComponentsInChildren<ForgePanel>(true))
                {
                    checkedPanels++;
                    failed += CheckPanel(panel, path, sb);
                }
            }

            var plain = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Forge/Core/Prefabs/ForgePanel.prefab");
            if (plain != null)
            {
                // The smallest a panel can be resized to, a frameless card, and a panel drawn at 60 % scale.
                var small = Object.Instantiate(plain);
                made.Add(small);
                small.GetComponent<ForgePanel>().SizeMeters = PanelManipulator.MinSize;
                checkedPanels++;
                failed += CheckPanel(small.GetComponent<ForgePanel>(), "plain panel at MinSize", sb);

                var frameless = Object.Instantiate(plain);
                made.Add(frameless);
                frameless.transform.Find("TitleBar")?.gameObject.SetActive(false);
                frameless.transform.Find("Background")?.gameObject.SetActive(false);
                frameless.GetComponent<ForgePanel>().SizeMeters = new Vector2(0.3f, 0.18f);
                checkedPanels++;
                failed += CheckPanel(frameless.GetComponent<ForgePanel>(), "frameless card", sb, expectHeader: false);

                var scaled = Object.Instantiate(plain);
                made.Add(scaled);
                scaled.transform.localScale *= 0.6f;
                checkedPanels++;
                failed += CheckPanel(scaled.GetComponent<ForgePanel>(), "panel at 60 % scale", sb);

                var locked = Object.Instantiate(plain);
                made.Add(locked);
                var lockedPanel = locked.GetComponent<ForgePanel>();
                lockedPanel.SetClosable(false, "check: must not close");
                PanelChrome chrome = PanelChrome.Of(lockedPanel);
                bool hidden = chrome.CloseButton != null && !chrome.CloseButton.gameObject.activeSelf;
                int before = 0;
                lockedPanel.CloseHandler = () => before++;
                lockedPanel.RequestClose();
                failed += Line(sb, hidden && before == 0, "a panel with Closable off shows no X and RequestClose does nothing");
            }
            else failed += Line(sb, false, "Core/Prefabs/ForgePanel.prefab is there");
        }
        finally
        {
            foreach (GameObject go in made)
                if (go != null) Object.DestroyImmediate(go);
        }
        return (failed == 0 ? "PASS" : $"FAIL ({failed})") + $" panel close check ({checkedPanels} panels)\n" + sb;
    }

    static int CheckPanel(ForgePanel panel, string where, StringBuilder sb, bool? expectHeader = null)
    {
        string name = where + (panel.gameObject.name.Length > 0 ? " / " + panel.gameObject.name : "");
        // An orb shows its chip once its owner says what closing it means (GestureWindow, WorkspaceExtras, the help dot).
        if (panel.GetComponent<PanelOrb>() != null) panel.CloseHandler = () => { };
        panel.SizeMeters = panel.SizeMeters;  // fit the Interaction SDK hit boxes, as Awake does at runtime
        PanelChrome chrome = PanelChrome.Of(panel);
        if (!panel.Closable) return Line(sb, !string.IsNullOrEmpty(panel.NotClosableReason), name + ": not closable, and says why (" + panel.NotClosableReason + ")");

        int failed = 0;
        PanelChromeButton close = chrome.CloseButton;
        if (close == null || !close.gameObject.activeSelf) return Line(sb, false, name + ": has a Close control");
        if (expectHeader.HasValue) failed += Line(sb, chrome.InHeader == expectHeader.Value, name + (expectHeader.Value ? ": X in the title bar" : ": close chip (no title bar)"));

        // Size in the world.
        var corners = new Vector3[4];
        close.Rect.GetWorldCorners(corners);
        float w = Vector3.Distance(corners[0], corners[3]), h = Vector3.Distance(corners[0], corners[1]);
        failed += Line(sb, w >= MinMeters - 1e-4f && h >= MinMeters - 1e-4f, $"{name}: Close is at least 30 mm ({w * 1000f:0} x {h * 1000f:0} mm)");

        // Inside the panel and inside every ray / poke hit box.
        var panelRect = (RectTransform)panel.transform;
        bool inside = true, inClipper = true;
        foreach (Vector3 c in corners)
        {
            Vector3 local = panelRect.InverseTransformPoint(c);
            if (!panelRect.rect.Contains(new Vector2(local.x, local.y) + Vector2.zero, true) && !Near(panelRect.rect, local)) inside = false;
        }
        Vector3 center = (corners[0] + corners[2]) * 0.5f;
        foreach (BoundsClipper clipper in panel.GetComponentsInChildren<BoundsClipper>(true))
        {
            if (!clipper.GetLocalBounds(clipper.transform, out Bounds b)) continue;
            Vector3 p = clipper.transform.InverseTransformPoint(center);
            p.z = b.center.z;
            if (!b.Contains(p)) inClipper = false;
        }
        failed += Line(sb, inside, name + ": Close lies inside the panel");
        failed += Line(sb, inClipper, name + ": Close is inside the Interaction SDK hit area (rays and pokes reach it)");

        // Top-most: the chrome is the panel's last child, and the hit graphic takes raycasts.
        var hit = close.GetComponent<Graphic>();
        failed += Line(sb, chrome.Root.GetSiblingIndex() == panel.transform.childCount - 1 && hit != null && hit.raycastTarget,
            name + ": Close is drawn over everything on the panel and takes the press");

        // A pinch that drifts off before the release still closes; a press that never started on it doesn't.
        System.Action saved = panel.CloseHandler;
        int closes = 0;
        panel.CloseHandler = () => closes++;
        var e = new PointerEventData(null) { pointerCurrentRaycast = new RaycastResult() };
        ExecuteEvents.Execute(close.gameObject, e, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.Execute(close.gameObject, e, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.Execute(close.gameObject, new PointerEventData(null), ExecuteEvents.pointerUpHandler);  // a stray release: nothing
        failed += Line(sb, closes == 1, name + ": a pinch on the X closes it, even released off the button (" + closes + ")");
        panel.CloseHandler = saved;

        if (chrome.InHeader && panel.Minimisable)
        {
            PanelChromeButton min = chrome.MinimiseButton;
            failed += Line(sb, min != null && min.gameObject.activeSelf && min.Rect.anchoredPosition.x < close.Rect.anchoredPosition.x - close.Rect.sizeDelta.x * 0.5f,
                name + ": Minimise sits left of Close, not on it");
        }
        return failed;
    }

    static bool Near(Rect r, Vector3 p) => p.x >= r.xMin - 0.5f && p.x <= r.xMax + 0.5f && p.y >= r.yMin - 0.5f && p.y <= r.yMax + 0.5f;

    static int Line(StringBuilder sb, bool ok, string what)
    {
        if (!ok) sb.Append("  FAIL ").AppendLine(what);
        return ok ? 0 : 1;
    }
}
