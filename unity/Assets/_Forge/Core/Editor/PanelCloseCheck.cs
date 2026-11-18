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
/// Also: a volume whose owner says what closing means gets a close chip that passes the same tests (at any scale) and closes
/// it; one without shows none; a panel with a throw handler hands its throw over (an artifact's panel); the hand-throw maths
/// (<see cref="HandThrow"/>) throws a fast fling away and not a placing move, a pull toward you, a slam down or two hands; and
/// the hide-all gesture reaches non-panel things registered with <see cref="WindowStash"/>.
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

            if (plain != null) failed += VolumeChip(plain.GetComponent<ForgePanel>(), made, sb, ref checkedPanels);
            if (plain != null) failed += PanelThrowHandler(plain, made, sb);
            failed += ThrowMaths(sb);
            failed += StashReachesOthers(sb);
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

    // ---------------------------------------------------------------- volumes, throws, hide all

    static int VolumeChip(ForgePanel panelPrefab, List<GameObject> made, StringBuilder sb, ref int checkedPanels)
    {
        int failed = 0;
        var volumeAsset = AssetDatabase.LoadAssetAtPath<ForgeVolume>("Assets/_Forge/Core/Prefabs/ForgeVolume.prefab");
        if (volumeAsset == null) return Line(sb, false, "Core/Prefabs/ForgeVolume.prefab is there");

        // No owner, no close: no chip (a CAD or ML model closes with its mode).
        var bare = Object.Instantiate(volumeAsset);
        made.Add(bare.gameObject);
        var bareChrome = ForgeVolumeChrome.Of(bare, panelPrefab);
        failed += Line(sb, bareChrome.Chip == null || !bareChrome.Chip.gameObject.activeSelf, "a volume nobody can close shows no close chip");

        var volume = Object.Instantiate(volumeAsset);
        made.Add(volume.gameObject);
        volume.SetSize(new Vector3(0.45f, 0.45f, 0.45f));
        int closes = 0;
        volume.CloseHandler = () => closes++;
        var chrome = ForgeVolumeChrome.Of(volume, panelPrefab);
        failed += Line(sb, chrome.Chip != null && chrome.Chip.gameObject.activeSelf, "a volume its owner can close (an artifact) shows a close chip");
        if (chrome.Chip == null) return failed;
        checkedPanels++;
        failed += CheckPanel(chrome.Chip, "volume close chip", sb, expectHeader: false);
        PanelChromeButton x = PanelChrome.Of(chrome.Chip).CloseButton;
        x.Press();
        failed += Line(sb, closes == 1, "the volume's chip closes the volume through its owner (" + closes + ")");
        failed += Line(sb, chrome.Chip.transform.localPosition.y > 0f && chrome.Chip.transform.localPosition.z < 0f,
            "the chip stands up off the base, at the front where the label is (" + chrome.Chip.transform.localPosition + ")");

        // Scaled down by hand to 0.4 and up to 3: the chip keeps its real size, so it stays at least 30 mm and in the hit box.
        foreach (float scale in new[] { 0.4f, 3f })
        {
            volume.transform.localScale = Vector3.one * scale;
            chrome.Refresh();
            checkedPanels++;
            failed += CheckPanel(chrome.Chip, $"volume close chip at {scale}x", sb, expectHeader: false);
        }
        volume.CloseHandler = null;
        failed += Line(sb, !chrome.Chip.gameObject.activeSelf, "the chip goes when the owner takes the close away");
        return failed;
    }

    static int PanelThrowHandler(GameObject plain, List<GameObject> made, StringBuilder sb)
    {
        var go = Object.Instantiate(plain);
        made.Add(go);
        var panel = go.GetComponent<ForgePanel>();
        Vector3 got = Vector3.zero;
        panel.ThrowHandler = v => got = v;
        panel.ThrowAway(new Vector3(0f, 0f, 2f));
        int failed = Line(sb, got == new Vector3(0f, 0f, 2f) && go.activeSelf, "a panel with a throw handler (an artifact's) hands the throw to it and isn't hidden on its own");
        panel.SetClosable(false, "check");
        got = Vector3.zero;
        panel.ThrowAway(new Vector3(0f, 0f, 2f));
        failed += Line(sb, got == Vector3.zero, "a panel that can't close can't be thrown either");
        return failed;
    }

    // The same rules the standalone test drives (HandThrow is plain maths), here so a change in the editor is caught too.
    static int ThrowMaths(StringBuilder sb)
    {
        int failed = 0;
        Vector3 eye = new Vector3(0f, 1.6f, 0f), fwd = new Vector3(0f, 0.15f, 1f).normalized;
        System.Func<float, float> ramp = s => Mathf.Min(3f, s / 0.15f * 3f);
        failed += Line(sb, Fling(eye, fwd, ramp, 0.18f, 0f, false), "HandThrow: a 3 m/s fling away from you throws");
        failed += Line(sb, Fling(eye, fwd, ramp, 0.18f, 0.07f, false), "HandThrow: let go 70 ms after the arm stopped still throws");
        failed += Line(sb, !Fling(eye, fwd, ramp, 0.18f, 0.25f, false), "HandThrow: let go 250 ms after the arm stopped is a put-down");
        failed += Line(sb, !Fling(eye, fwd, s => 0.5f, 0.8f, 0f, false), "HandThrow: carrying it at 0.5 m/s and letting go doesn't throw");
        failed += Line(sb, !Fling(eye, fwd, s => 1.1f, 0.4f, 0f, false), "HandThrow: pushing it off at 1.1 m/s (under the arming speed) doesn't throw");
        failed += Line(sb, !Fling(eye, Vector3.back, ramp, 0.18f, 0f, false), "HandThrow: a fast pull toward you doesn't throw");
        failed += Line(sb, !Fling(eye, new Vector3(0f, -1f, 0.2f).normalized, ramp, 0.18f, 0f, false), "HandThrow: a fast move down onto a table doesn't throw");
        failed += Line(sb, !Fling(eye, Vector3.right, ramp, 0.18f, 0f, false), "HandThrow: a fling straight sideways doesn't throw");
        failed += Line(sb, !Fling(eye, fwd, ramp, 0.18f, 0f, true), "HandThrow: two hands on it (resizing) never throw");
        failed += Line(sb, Fling(eye, fwd, s => s < 0.1f ? 1.6f : 1.0f, 0.35f, 0f, false), "HandThrow: armed at 1.6 m/s, still armed at 1.0 (hysteresis)");
        return failed;
    }

    static bool Fling(Vector3 eye, Vector3 dir, System.Func<float, float> speed, float seconds, float after, bool twoHands)
    {
        const float dt = 1f / 72f;
        var t = new HandThrow();
        float time = 0f;
        Vector3 p = new Vector3(0.1f, 1.2f, 0.45f);
        t.Begin(time, p);
        for (int i = 0; i < 30; i++) { time += dt; t.Sample(time, p); }
        for (float s = 0f; s < seconds; s += dt)
        {
            time += dt;
            p += dir * (speed(s) * dt);
            t.Sample(time, p);
            if (twoHands && s > 0.05f) t.Cancel();
        }
        for (float s = 0f; s < after; s += dt) { time += dt; t.Sample(time, p); }
        return t.Release(time, eye, p, out _);
    }

    sealed class FakeStash : WindowStash.IStashable
    {
        public int hides, shows, hidden;
        public int Hide(MonoBehaviour runner) { hides++; hidden = 1; return 1; }
        public int Show(MonoBehaviour runner) { shows++; hidden = 0; return 1; }
        public int HiddenCount => hidden;
    }

    static int StashReachesOthers(StringBuilder sb)
    {
        var fake = new FakeStash();
        var runner = new GameObject("stash check runner").AddComponent<ForgeRunner>();
        try
        {
            WindowStash.Register(fake);
            WindowStash.HideAll(runner);
            bool hid = fake.hides == 1 && WindowStash.HiddenCount >= 1;
            WindowStash.ShowAll(runner);
            return Line(sb, hid && fake.shows == 1, "hide all / show all reach the things that aren't panels (an artifact's model and course)");
        }
        finally
        {
            WindowStash.Unregister(fake);
            Object.DestroyImmediate(runner.gameObject);
        }
    }

    static bool Near(Rect r, Vector3 p) => p.x >= r.xMin - 0.5f && p.x <= r.xMax + 0.5f && p.y >= r.yMin - 0.5f && p.y <= r.yMax + 0.5f;

    static int Line(StringBuilder sb, bool ok, string what)
    {
        if (!ok) sb.Append("  FAIL ").AppendLine(what);
        return ok ? 0 : 1;
    }
}
