using System;
using System.Collections;
using System.Collections.Generic;
using Oculus.Interaction;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The Albie menu view. A UI Set backplate (world-space canvas with poke, ray and mouse input) holding a tab bar (Tools,
    /// Projects, Artifacts, Close), a grid of tiles (4 columns by 2 rows a page, with paging when there are more) read from
    /// <see cref="OSRegistry"/>, and a bottom strip (Build something, Agents, Settings, Bring windows here, Close). It
    /// follows <see cref="OSShell"/> events: when the shell opens the menu it appears where it was summoned (a fixed
    /// distance and drop from the eyes, facing the user, never following the head), and grows from the summon point.
    /// Sizes are OS units (0.65 mm). Tiles are pooled; nothing allocates per frame (there is no Update).
    /// </summary>
    public sealed class OSMenu : MonoBehaviour
    {
        // ---- layout, in OS units (one unit is OSKit.unitMeters)
        const float Pad = 28f, TabHeight = 64f, TabWidth = 150f, Gap = 20f;
        const float TileWidth = 188f, TileHeight = 108f, TileGap = 20f, HeadingHeight = 30f;
        const int Columns = 4, Rows = 2;
        const float GridWidth = Columns * TileWidth + (Columns - 1) * TileGap;
        const float GridHeight = Rows * TileHeight + (Rows - 1) * TileGap + HeadingHeight;
        const float StripHeight = 52f;
        const float PlateWidth = GridWidth + 2f * Pad;
        const float PlateHeight = Pad + TabHeight + Gap + GridHeight + Gap + StripHeight + Pad;
        const float GridTop = Pad + TabHeight + Gap;
        const float StripTop = GridTop + GridHeight + Gap;

        // ---- motion
        const float OpenSeconds = 0.22f, CloseSeconds = 0.12f, StartScale = 0.3f;

        [SerializeField] GameObject visual;
        [SerializeField] RectTransform canvasRoot, plate;
        [SerializeField] CanvasGroup group;
        [SerializeField] UIThemeManager themeManager;

        struct Slot
        {
            public int entry;      // index into entries; -1 for a heading
            public int page;
            public float x, y;     // in the grid, units from its top-left
            public string heading;
        }

        OSShell shell;
        bool built, announcedMissingKit;
        Coroutine motion;

        readonly List<OSEntry> entries = new List<OSEntry>();
        readonly List<Slot> slots = new List<Slot>();
        readonly List<OSTileView> tiles = new List<OSTileView>(), outlineTiles = new List<OSTileView>();
        readonly List<TMP_Text> headings = new List<TMP_Text>();
        int page, pageCount = 1;
        bool needTheme;

        Toggle[] tabs;
        Button prevButton, nextButton, closeX;
        TMP_Text pageText, emptyText;

        /// <summary>Where the menu last opened (for checks and tests).</summary>
        public Vector3 LastCenter { get; private set; }
        /// <summary>The plate's size in metres at the OS unit.</summary>
        public Vector2 SizeMeters => new Vector2(PlateWidth, PlateHeight) * (OSKit.Current != null ? OSKit.Current.unitMeters : 0.00065f);
        public bool Visible => visual != null && visual.activeSelf;
        public int PageCount => pageCount;
        public int Page => page;
        public IReadOnlyList<OSTileView> ActiveTiles => activeTiles;
        readonly List<OSTileView> activeTiles = new List<OSTileView>();

        void Awake()
        {
            shell = GetComponentInParent<OSShell>();
            if (visual != null) visual.SetActive(false);
        }

        void OnEnable()
        {
            if (shell == null) shell = GetComponentInParent<OSShell>();
            if (shell == null) return;
            shell.MenuChanged += OnMenuChanged;
            shell.TabChanged += OnTabChanged;
            shell.Windows.Changed += OnDataChanged;
            OSRegistry.Changed += OnDataChanged;
            if (shell.MenuOpen) OnMenuChanged(true);
        }

        void OnDisable()
        {
            if (shell != null)
            {
                shell.MenuChanged -= OnMenuChanged;
                shell.TabChanged -= OnTabChanged;
                if (shell.Windows != null) shell.Windows.Changed -= OnDataChanged;
            }
            OSRegistry.Changed -= OnDataChanged;
            if (motion != null) StopCoroutine(motion);
            motion = null;
            if (visual != null) visual.SetActive(false);
        }

        // ---------------------------------------------------------------- shell events

        void OnMenuChanged(bool open)
        {
            if (motion != null) StopCoroutine(motion);
            motion = null;
            if (open) Show(); else Hide();
        }

        void OnTabChanged(OSTab tab)
        {
            page = 0;
            if (Visible) Refresh();
        }

        void OnDataChanged()
        {
            if (Visible && shell != null && shell.MenuOpen) Refresh();
        }

        // ---------------------------------------------------------------- show and hide

        void Show()
        {
            if (!Build()) return;
            page = 0;
            Vector3 to; Quaternion rotation;
            Pose(out to, out rotation);
            LastCenter = to;
            Vector3 from = shell.SummonPoint ?? to;
            transform.SetPositionAndRotation(from, rotation);
            transform.localScale = Vector3.one * StartScale;
            group.alpha = 0f;
            group.interactable = true;
            group.blocksRaycasts = true;
            visual.SetActive(true);  // before Refresh: the tab toggles' animators need to be running
            Refresh();
            OSSounds.Open();
            motion = StartCoroutine(Animate(from, to, OpenSeconds, true));
        }

        void Hide()
        {
            if (!Visible) return;
            OSSounds.Close();
            ClearSelection();
            group.interactable = false;
            group.blocksRaycasts = false;
            motion = StartCoroutine(Animate(transform.position, transform.position, CloseSeconds, false));
        }

        IEnumerator Animate(Vector3 from, Vector3 to, float seconds, bool opening)
        {
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                Step(from, to, Mathf.Clamp01(t / seconds), opening);
                yield return null;
            }
            Step(from, to, 1f, opening);
            if (!opening) visual.SetActive(false);
            motion = null;
        }

        void Step(Vector3 from, Vector3 to, float k, bool opening)
        {
            if (opening)
            {
                float eased = ForgeTween.EaseOutCubic(k);
                transform.position = Vector3.LerpUnclamped(from, to, eased);
                transform.localScale = Vector3.one * Mathf.Lerp(StartScale, 1f, eased);
                group.alpha = Mathf.Clamp01(k * 1.6f);  // the fade lands a little before the grow does
            }
            else
            {
                float eased = ForgeTween.EaseInCubic(k);
                transform.localScale = Vector3.one * Mathf.Lerp(1f, 0.94f, eased);
                group.alpha = 1f - eased;
            }
        }

        /// <summary>
        /// Where the menu sits: <c>menuDistance</c> from the eyes, <c>menuDropDegrees</c> below eye level, straight ahead of
        /// where the head faces now, turned to face the eyes. World-locked: nothing moves it afterwards.
        /// </summary>
        void Pose(out Vector3 position, out Quaternion rotation)
        {
            OSSettings settings = OSSettings.Current;
            Transform head = shell.Head;
            Vector3 eye = head != null ? head.position : Vector3.zero;
            Vector3 flat = head != null ? head.forward : Vector3.forward;
            flat.y = 0f;
            flat = flat.sqrMagnitude < 1e-4f ? Vector3.forward : flat.normalized;
            Vector3 right = new Vector3(flat.z, 0f, -flat.x);
            Vector3 direction = Quaternion.AngleAxis(settings.menuDropDegrees, right) * flat;
            position = eye + direction * settings.menuDistance;
            rotation = Quaternion.LookRotation(direction, Vector3.up);
        }

        // ---------------------------------------------------------------- building

        /// <summary>Builds the plate's contents from the kit on first use. False when the kit is missing.</summary>
        bool Build()
        {
            if (built) return true;
            OSKit kit = OSKit.Current;
            if (kit == null || kit.tabToggle == null || kit.textTile == null || canvasRoot == null || plate == null)
            {
                if (!announcedMissingKit)
                {
                    announcedMissingKit = true;
                    Debug.Log("[OS] The menu can't open: Resources/OSKit.asset is missing or incomplete (Forge > OS > Build OS Prefabs).");
                }
                return false;
            }
            built = true;

            // The backplate prefab lays itself out; this plate is placed by hand.
            foreach (var layout in canvasRoot.GetComponents<LayoutGroup>()) layout.enabled = false;
            foreach (var layout in plate.GetComponents<LayoutGroup>()) layout.enabled = false;
            canvasRoot.sizeDelta = new Vector2(PlateWidth, PlateHeight);
            canvasRoot.localScale = Vector3.one * kit.unitMeters;
            plate.anchorMin = plate.anchorMax = plate.pivot = new Vector2(0.5f, 0.5f);
            plate.anchoredPosition = Vector2.zero;
            plate.sizeDelta = new Vector2(PlateWidth, PlateHeight);
            var rounded = plate.GetComponent<Oculus.Interaction.Samples.RoundedBoxUIProperties>();
            if (rounded != null) rounded.borderRadius = new Vector4(36f, 36f, 36f, 36f);
            OSBody.EnsureCanvas(canvasRoot.GetComponent<Canvas>());
            // The menu plays its own press sound for every control, so the plate's poke sound would double it.
            var wrapper = GetComponentInChildren<PointableCanvasUnityEventWrapper>(true);
            if (wrapper != null) wrapper.enabled = false;

            // Tab bar
            var group2 = plate.gameObject.AddComponent<ToggleGroup>();
            group2.allowSwitchOff = false;
            tabs = new Toggle[3];
            string[] labels = { "Tools", "Projects", "Artifacts" };
            string[] icons = { "tools", "projects", "artifacts" };
            for (int i = 0; i < 3; i++)
            {
                OSTab tab = (OSTab)i;
                tabs[i] = OSUi.Tab(plate, labels[i], icons[i], group2, () => { if (shell != null) shell.SetTab(tab); }, TabWidth, TabHeight);
                PlaceTopLeft(tabs[i].transform, Pad + i * (TabWidth + Gap), Pad);
            }

            float closeSize = 56f;
            closeX = OSUi.Button(plate, null, CloseMenu, OSButtonStyle.Borderless, "close");
            OSUi.Size(closeX.transform, closeSize, closeSize);
            PlaceTopLeft(closeX.transform, PlateWidth - Pad - closeSize, Pad + (TabHeight - closeSize) * 0.5f);

            // Paging: < 1 / 2 >, only shown when there is more than a page.
            float x = PlateWidth - Pad - closeSize - Gap - 56f;
            nextButton = OSUi.Button(plate, null, () => SetPage(page + 1), OSButtonStyle.Borderless, "chevron_right");
            OSUi.Size(nextButton.transform, 56f, 56f);
            PlaceTopLeft(nextButton.transform, x, Pad + (TabHeight - 56f) * 0.5f);
            pageText = OSUi.Text(plate, "1 / 2", OSUi.BodyText, true);
            pageText.GetComponent<LayoutElement>().ignoreLayout = true;
            pageText.alignment = TextAlignmentOptions.Center;
            pageText.textWrappingMode = TextWrappingModes.NoWrap;
            PlaceTopLeft(pageText.transform, x - 76f, Pad + (TabHeight - 24f) * 0.5f);
            ((RectTransform)pageText.transform).sizeDelta = new Vector2(76f, 24f);
            prevButton = OSUi.Button(plate, null, () => SetPage(page - 1), OSButtonStyle.Borderless, "chevron_left");
            OSUi.Size(prevButton.transform, 56f, 56f);
            PlaceTopLeft(prevButton.transform, x - 76f - 56f, Pad + (TabHeight - 56f) * 0.5f);

            // Empty-state sentence, centred in the grid area.
            emptyText = OSUi.Text(plate, "", 20f, true);
            emptyText.GetComponent<LayoutElement>().ignoreLayout = true;
            emptyText.alignment = TextAlignmentOptions.Center;
            var emptyRect = (RectTransform)emptyText.transform;
            PlaceTopLeft(emptyRect, Pad + GridWidth * 0.2f, GridTop);
            emptyRect.sizeDelta = new Vector2(GridWidth * 0.6f, GridHeight);

            // Bottom strip
            float stripX = Pad;
            stripX = Strip("Build something", "build", OSButtonStyle.Primary, () => OpenSystem("build"), stripX);
            stripX = Strip("Agents", "agents", OSButtonStyle.Secondary, () => OpenSystem("agents"), stripX);
            stripX = Strip("Settings", "settings", OSButtonStyle.Secondary, () => OpenSystem("settings"), stripX);
            stripX = Strip("Bring windows here", "bring", OSButtonStyle.Secondary, BringWindows, stripX);
            Button close = OSUi.Button(plate, "Close", CloseMenu, OSButtonStyle.Secondary);
            var closeRect = (RectTransform)close.transform;
            PlaceTopLeft(closeRect, PlateWidth - Pad - closeRect.sizeDelta.x, StripTop + (StripHeight - OSUi.ButtonHeight) * 0.5f);

            if (themeManager != null) themeManager.ApplyCurrentTheme();
            return true;
        }

        float Strip(string label, string icon, OSButtonStyle style, Action onClick, float x)
        {
            Button button = OSUi.Button(plate, label, onClick, style, icon);
            var rect = (RectTransform)button.transform;
            PlaceTopLeft(rect, x, StripTop + (StripHeight - OSUi.ButtonHeight) * 0.5f);
            return x + rect.sizeDelta.x + Gap;
        }

        static void PlaceTopLeft(Transform t, float x, float y)
        {
            var rect = (RectTransform)t;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);  // from the plate's top-left corner
        }

        // ---------------------------------------------------------------- strip actions

        void CloseMenu()
        {
            if (shell != null) shell.CloseMenu();
        }

        void OpenSystem(string id)
        {
            if (shell == null) return;
            shell.OpenSystem(id);
            if (OSShell.HasSystem(id)) shell.CloseMenu();
        }

        void BringWindows()
        {
            if (shell == null) return;
            shell.Windows.BringHere();
            shell.CloseMenu();
        }

        void SetPage(int value)
        {
            value = Mathf.Clamp(value, 0, pageCount - 1);
            if (value == page) return;
            page = value;
            Refresh();
        }

        // ---------------------------------------------------------------- data

        /// <summary>Reads the current tab from the registry and lays the page out. Reuses tile objects.</summary>
        public void Refresh()
        {
            if (!built || shell == null) return;
            OSTab tab = shell.Tab;
            for (int i = 0; i < tabs.Length; i++) OSUi.Select(tabs[i], i == (int)tab);

            OSRegistry.Fill(tab, entries);
            LayoutSlots();
            page = Mathf.Clamp(page, 0, pageCount - 1);

            for (int i = 0; i < tiles.Count; i++) tiles[i].gameObject.SetActive(false);
            for (int i = 0; i < outlineTiles.Count; i++) outlineTiles[i].gameObject.SetActive(false);
            for (int i = 0; i < headings.Count; i++) headings[i].gameObject.SetActive(false);
            activeTiles.Clear();

            int tileIndex = 0, outlineIndex = 0, headingIndex = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot slot = slots[i];
                if (slot.page != page) continue;
                if (slot.entry < 0)
                {
                    TMP_Text heading = Pooled(headings, ref headingIndex);
                    heading.text = slot.heading;
                    PlaceTopLeft(heading.transform, Pad, GridTop + slot.y);
                    ((RectTransform)heading.transform).sizeDelta = new Vector2(GridWidth, HeadingHeight);
                    continue;
                }
                OSEntry entry = entries[slot.entry];
                OSTileView tile = entry.outline ? PooledTile(outlineTiles, ref outlineIndex, true) : PooledTile(tiles, ref tileIndex, false);
                if (tile == null) continue;
                tile.Show(entry);
                PlaceTopLeft(tile.rect, Pad + slot.x, GridTop + slot.y);
                activeTiles.Add(tile);
            }

            emptyText.gameObject.SetActive(entries.Count == 0);
            if (entries.Count == 0) emptyText.text = OSRegistry.EmptyText(tab);

            bool paged = pageCount > 1;
            prevButton.gameObject.SetActive(paged);
            nextButton.gameObject.SetActive(paged);
            pageText.gameObject.SetActive(paged);
            if (paged)
            {
                prevButton.interactable = page > 0;
                nextButton.interactable = page < pageCount - 1;
                pageText.text = (page + 1) + " / " + pageCount;
            }

            if (needTheme && themeManager != null)
            {
                needTheme = false;
                themeManager.ApplyCurrentTheme();
            }
        }

        TMP_Text Pooled(List<TMP_Text> pool, ref int index)
        {
            if (index < pool.Count) return Activate(pool[index++]);
            TMP_Text heading = OSUi.Text(plate, "", 18f, true);
            heading.GetComponent<LayoutElement>().ignoreLayout = true;
            heading.fontStyle = FontStyles.Bold;
            heading.textWrappingMode = TextWrappingModes.NoWrap;
            pool.Add(heading);
            index++;
            needTheme = true;
            return heading;
        }

        static TMP_Text Activate(TMP_Text text)
        {
            text.gameObject.SetActive(true);
            return text;
        }

        OSTileView PooledTile(List<OSTileView> pool, ref int index, bool outline)
        {
            if (index < pool.Count)
            {
                OSTileView existing = pool[index++];
                existing.gameObject.SetActive(true);
                return existing;
            }
            OSTileView tile = OSUi.Tile(plate, outline, TileWidth, TileHeight);
            if (tile == null) return null;
            tile.GetComponent<LayoutElement>().ignoreLayout = true;
            OSTileView captured = tile;
            tile.button.onClick.AddListener(() => OnTile(captured));
            pool.Add(tile);
            index++;
            needTheme = true;
            return tile;
        }

        void OnTile(OSTileView tile)
        {
            OSEntry entry = tile.entry;
            if (entry == null) return;
            if (entry.open == null)
            {
                OSSounds.Error();
                Debug.Log("[OS] '" + entry.title + "' can't be opened right now.");
                return;
            }
            try
            {
                entry.open();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            ClearSelection();
            if (shell != null) shell.CloseMenu();
        }

        // A pressed UI Set control stays "selected" in the EventSystem and keeps its highlight; the next visit shouldn't.
        static void ClearSelection()
        {
            var system = UnityEngine.EventSystems.EventSystem.current;
            if (system != null && system.currentSelectedGameObject != null) system.SetSelectedGameObject(null);
        }

        // Works out which page, column and row every entry lands on. A section heading starts a new row; a page holds two rows
        // of tiles and at most one heading, and a heading is repeated at the top of a page its section carries onto.
        void LayoutSlots()
        {
            slots.Clear();
            int pageIndex = 0, column = 0, rows = 0;
            float y = 0f;
            bool headingOnPage = false, pageStart = true;
            string previous = null;
            for (int i = 0; i < entries.Count; i++)
            {
                OSEntry e = entries[i];
                string section = e.section;
                bool changed = !pageStart && section != previous;
                if (changed && column != 0)
                {
                    y += TileHeight + TileGap;
                    column = 0;
                }
                if (column == 0)
                {
                    bool wantHeading = section != null && (pageStart || changed);
                    if (!pageStart && (rows >= Rows || (wantHeading && headingOnPage)))
                    {
                        pageIndex++;
                        y = 0f;
                        rows = 0;
                        headingOnPage = false;
                        pageStart = true;
                        wantHeading = section != null;
                    }
                    if (wantHeading)
                    {
                        slots.Add(new Slot { entry = -1, page = pageIndex, x = 0f, y = y, heading = section });
                        y += HeadingHeight;
                        headingOnPage = true;
                    }
                    rows++;
                }
                slots.Add(new Slot { entry = i, page = pageIndex, x = column * (TileWidth + TileGap), y = y });
                column++;
                if (column == Columns)
                {
                    column = 0;
                    y += TileHeight + TileGap;
                }
                pageStart = false;
                previous = section;
            }
            pageCount = entries.Count == 0 ? 1 : pageIndex + 1;
        }
    }
}
