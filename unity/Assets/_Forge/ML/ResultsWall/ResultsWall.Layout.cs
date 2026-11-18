using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    public partial class ResultsWall
    {
        const float ScrollGutter = 12f;
        readonly RectTransform[] pageBodies = new RectTransform[TabNames.Length];
        readonly RectTransform[] pageViews = new RectTransform[TabNames.Length];
        readonly ScrollRect[] pageScrolls = new ScrollRect[TabNames.Length];
        Vector2 layoutSize, requestedSize;
        float resizeAt;

        void BuildPages()
        {
            for (int i = 0; i < TabNames.Length; i++)
            {
                var next = (Tab)i;
                tabPills[i] = MLUI.Pill(root, TabNames[i], 0, 0, TabMax, TabBar, () => ShowTab(next));
                tabPills[i].name = "Tab " + TabNames[i];
                var p = pages[i] = MLUI.Rect(TabNames[i] + " page", root, 0, BodyTop, 100, 100);
                var view = pageViews[i] = MLUI.Rect("Viewport", p, 0, 0, 100, 100);
                view.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
                view.gameObject.AddComponent<RectMask2D>();
                pageBodies[i] = MLUI.Rect("Scroll content", view, 0, 0, 100, 100);
                var scroll = pageScrolls[i] = p.gameObject.AddComponent<ScrollRect>();
                scroll.viewport = view; scroll.content = pageBodies[i];
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 28f;
                scroll.verticalScrollbar = ScrollbarFor(p, "Vertical scroll", true);
                scroll.horizontalScrollbar = ScrollbarFor(p, "Horizontal scroll", false);
            }
            runLabel = MLUI.Caps(root, "", 0, 9, 100, 14, 9);
            runLabel.alignment = TMPro.TextAlignmentOptions.TopRight;
            runLabel.color = MLUI.Faint; runLabel.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            images = pageBodies[(int)Tab.Images];
        }

        static Scrollbar ScrollbarFor(RectTransform parent, string name, bool vertical)
        {
            var track = MLUI.Rect(name, parent, 0, 0, 7, 100);
            track.gameObject.AddComponent<ForgeSurface>().color = new Color(1, 1, 1, 0.06f);
            var thumb = MLUI.Rect("Thumb", track, 0, 0, 7, 20);
            thumb.anchorMin = Vector2.zero; thumb.anchorMax = Vector2.one;
            thumb.offsetMin = thumb.offsetMax = Vector2.zero;
            var ink = thumb.gameObject.AddComponent<ForgeSurface>();
            ink.color = new Color(1, 1, 1, 0.4f); ink.Pill = true;
            var bar = track.gameObject.AddComponent<Scrollbar>();
            bar.handleRect = thumb; bar.targetGraphic = ink;
            bar.direction = vertical ? Scrollbar.Direction.BottomToTop : Scrollbar.Direction.LeftToRight;
            bar.navigation = new Navigation { mode = Navigation.Mode.None };
            return bar;
        }

        void LayoutPages(bool rebuildImages)
        {
            layoutSize = requestedSize = root.rect.size;
            float w = Mathf.Max(1f, layoutSize.x);
            int columns = Mathf.Clamp(Mathf.FloorToInt((w + TabGap) / (TabMin + TabGap)), 1, TabNames.Length);
            float tabWidth = Mathf.Min(TabMax, (w - (columns - 1) * TabGap) / columns);
            int rows = Mathf.CeilToInt(TabNames.Length / (float)columns);
            float bodyTop = rows * (TabBar + TabGap) + 6;
            for (int i = 0; i < TabNames.Length; i++)
            {
                tabPills[i].Rect.anchoredPosition = new Vector2((i % columns) * (tabWidth + TabGap), -(i / columns) * (TabBar + TabGap));
                tabPills[i].Rect.sizeDelta = new Vector2(tabWidth, TabBar);
                pages[i].anchoredPosition = new Vector2(0, -bodyTop);
                pages[i].sizeDelta = new Vector2(w, Mathf.Max(1f, layoutSize.y - bodyTop));
                ConfigureBody(i);
                pageScrolls[i].normalizedPosition = new Vector2(0, 1);
            }
            float runX = TabNames.Length * (tabWidth + TabGap) + 8;
            float runWidth = Mathf.Max(0, w - runX);
            runLabel.rectTransform.anchoredPosition = new Vector2(runX, -9);
            runLabel.rectTransform.sizeDelta = new Vector2(runWidth, 14);
            runLabel.gameObject.SetActive(rows == 1 && runWidth >= 24);
            if (rebuildImages)
            {
                MLUI.Clear(images);
                BuildImages();
            }
        }

        void ConfigureBody(int index)
        {
            var p = pages[index]; var scroll = pageScrolls[index];
            float viewWidth = Mathf.Max(1f, p.rect.width - ScrollGutter);
            float bodyWidth = index == (int)Tab.Overview ? Mathf.Max(240f, viewWidth) : Mathf.Max(720f, viewWidth);
            bool horizontal = bodyWidth > viewWidth + 0.5f;
            float viewHeight = Mathf.Max(1f, p.rect.height - (horizontal ? ScrollGutter : 0));
            float minimumHeight = index == (int)Tab.Overview ? OverviewHeight(bodyWidth)
                : index == (int)Tab.Classes ? Mathf.Max(360f, 46f + (report != null ? report.classes : 0) * 31f)
                : index == (int)Tab.Images ? Mathf.Max(430f, 220f + (report != null ? report.classes : 0) * 18f) : 360f;
            float bodyHeight = Mathf.Max(viewHeight, minimumHeight);
            pageViews[index].sizeDelta = new Vector2(viewWidth, viewHeight);
            pageBodies[index].sizeDelta = new Vector2(bodyWidth, bodyHeight);
            scroll.horizontal = horizontal; scroll.vertical = bodyHeight > viewHeight + 0.5f;
            var vertical = (RectTransform)scroll.verticalScrollbar.transform;
            vertical.anchoredPosition = new Vector2(p.rect.width - 7, 0);
            vertical.sizeDelta = new Vector2(7, viewHeight);
            vertical.gameObject.SetActive(scroll.vertical);
            var bottom = (RectTransform)scroll.horizontalScrollbar.transform;
            bottom.anchoredPosition = new Vector2(0, -viewHeight - 4);
            bottom.sizeDelta = new Vector2(viewWidth, 7);
            bottom.gameObject.SetActive(horizontal);
        }

        void CheckResize()
        {
            if (root == null || pageBodies[0] == null) return;
            if (root.rect.size != requestedSize)
            {
                requestedSize = root.rect.size;
                resizeAt = Time.unscaledTime + 0.12f;
            }
            if (requestedSize == layoutSize || Time.unscaledTime < resizeAt) return;
            LayoutPages(true); Refresh();
            if (tab != Tab.Images) DrawPage();
        }
    }
}
