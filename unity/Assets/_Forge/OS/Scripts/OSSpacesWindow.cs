using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The Spaces window (window id "spaces"): every thing open around the user, one row each (icon, name, where it is, and
    /// Here / Away / Close), under three buttons for everything at once (Bring everything here, Send everything away, Close
    /// everything). Rows come from <see cref="OSSpaces.Collect"/>, five a page with a pager. It opens within reach, where
    /// the menu was when it is opened from the menu strip, else at the menu's place in front of the user (the voice, the
    /// os_open system page), because the user reaches out and touches it. It re-reads the list once a second (and at once
    /// when the windows change or a button was pressed), rebuilding the rows only when what is open changed, else just
    /// updating where each thing is. Buttons are OS UI Set buttons: mouse, ray and poke all reach them, 31 mm tall.
    /// </summary>
    public sealed class OSSpacesWindow : MonoBehaviour
    {
        public const string WindowId = OSSpaces.WindowId;
        /// <summary>Rows on a page.</summary>
        public const int RowsPerPage = 5;

        static readonly Vector2 SizeMeters = new Vector2(0.6f, 0.46f);
        const float RowHeight = 52f, IconUnits = 36f, WhereUnits = 190f, LookSeconds = 1f, AfterPressSeconds = 0.35f;

        ForgePanel panel;
        OSShell hookedShell;
        int page;
        bool dirty = true;
        float nextLook;
        string signature = "";
        string pendingQuestion;
        Action pendingClose;
        string pendingLabel;
        readonly List<SpaceItem> items = new List<SpaceItem>();
        readonly List<TMP_Text> whereTexts = new List<TMP_Text>();
        readonly List<SpaceItem> whereItems = new List<SpaceItem>();
        readonly StringBuilder key = new StringBuilder();

        /// <summary>The open Spaces window's view, or null.</summary>
        public static OSSpacesWindow Current { get; private set; }

        /// <summary>What the list showed when it was last read (for checks).</summary>
        public IReadOnlyList<SpaceItem> Items => items;
        public int Page => page;
        public int PageCount => Mathf.Max(1, (items.Count + RowsPerPage - 1) / RowsPerPage);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        /// <summary>Opens the Spaces list within reach in front of the user, or brings it forward. Null with no shell.</summary>
        public static OSWindow Open()
        {
            OSShell shell = OSShell.Current;
            if (shell == null) return null;
            OSMenu.ReachPose(shell.Head, out Vector3 center, out Quaternion rotation);
            return Open(center, rotation);
        }

        /// <summary>Opens the Spaces list with its centre at <paramref name="center"/> facing along <paramref name="rotation"/> (the menu's place), or brings it forward.</summary>
        public static OSWindow Open(Vector3 center, Quaternion rotation)
        {
            OSShell shell = OSShell.Current;
            if (shell == null)
            {
                Debug.Log("[OS] The Spaces list needs the Albie menu, and this scene has none.");
                return null;
            }
            bool fresh = !shell.Windows.IsOpen(WindowId);
            OSWindow window = shell.Windows.Open(WindowId, "Spaces", SizeMeters, Build, "SPACES");
            if (window == null) return null;
            if (fresh && center != Vector3.zero) shell.Windows.PlaceAt(window, center, rotation);
            var view = window.GetComponent<OSSpacesWindow>();
            if (view != null) view.Poke();
            return window;
        }

        /// <summary>Opens the list with the Close everything question on it (asked by voice when a run would be lost).</summary>
        public static void AskToCloseEverything()
        {
            OSWindow window = Open();
            var view = window != null ? window.GetComponent<OSSpacesWindow>() : null;
            if (view != null) view.Ask(ClosingQuestion(), "Close everything", () => OSSpaces.CloseEverything(true));
        }

        /// <summary>Opens the list with the question for closing one thing on it.</summary>
        public static void AskToClose(SpaceItem item)
        {
            if (item == null || item.close == null) return;
            OSWindow window = Open();
            var view = window != null ? window.GetComponent<OSSpacesWindow>() : null;
            if (view != null) view.Ask(item.closeQuestion ?? ("Close " + item.title + "?"), item.closeLabel, item.close);
        }

        static void Build(ForgePanel forgePanel)
        {
            var view = forgePanel.gameObject.AddComponent<OSSpacesWindow>();
            view.panel = forgePanel;
        }

        void OnEnable()
        {
            Current = this;
            Hook(OSShell.Current);
            Poke();
        }

        void OnDisable()
        {
            Hook(null);
            if (Current == this) Current = null;
        }

        void Hook(OSShell shell)
        {
            if (hookedShell != null) hookedShell.Windows.Changed -= Poke;
            hookedShell = shell;
            if (hookedShell != null) hookedShell.Windows.Changed += Poke;
        }

        /// <summary>Read the list again soon (something opened, closed or was pressed).</summary>
        public void Poke()
        {
            dirty = true;
            nextLook = 0f;
        }

        // Once a second (or right after a change): read what is open; rebuild the rows if that changed, else update "where".
        void Update()
        {
            if (panel == null || Time.unscaledTime < nextLook) return;
            nextLook = Time.unscaledTime + LookSeconds;
            OSSpaces.Collect(items);
            string now = Signature();
            if (dirty || now != signature)
            {
                dirty = false;
                signature = now;
                Rebuild();
            }
            else UpdateWhere();
        }

        string Signature()
        {
            key.Length = 0;
            for (int i = 0; i < items.Count; i++) key.Append(items[i].key).Append(items[i].away ? '-' : '+').Append(items[i].closeQuestion != null ? '!' : '.').Append(';');
            return key.ToString();
        }

        /// <summary>Goes to a page (0-based) and rebuilds.</summary>
        public void SetPage(int value)
        {
            value = Mathf.Clamp(value, 0, PageCount - 1);
            if (value == page) return;
            page = value;
            Rebuild();
        }

        // After a press: what it did shows on the next read, a moment later (a dot shrinking, a window gliding).
        void Pressed(Action action)
        {
            try { action?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
            dirty = true;
            nextLook = Time.unscaledTime + AfterPressSeconds;
        }

        void Ask(string question, string label, Action onYes)
        {
            pendingQuestion = question;
            pendingLabel = label;
            pendingClose = onYes;
            Poke();
        }

        static string ClosingQuestion()
        {
            return OSSpaces.AtRisk()
                ? "Close everything? The run going on now stops and isn't kept."
                : "Close everything? The open artifact, its course and the truck go too.";
        }

        // ---------------------------------------------------------------- the page

        void Rebuild()
        {
            RectTransform body = OSUi.Body(panel);
            if (body == null) return;
            OSUi.Clear(body);
            whereTexts.Clear();
            whereItems.Clear();
            page = Mathf.Clamp(page, 0, PageCount - 1);
            RectTransform column = OSUi.Column(body, 10f);
            var columnLayout = column.GetComponent<VerticalLayoutGroup>();
            columnLayout.padding = new RectOffset(8, 8, 4, 4);

            RectTransform top = OSUi.Row(column, OSUi.ButtonHeight, 16f);
            top.name = "Everything";
            OSUi.Rigid(top);
            OSUi.Button(top, "Bring everything here", () => Pressed(OSSpaces.BringEverything), OSButtonStyle.Secondary);
            OSUi.Button(top, "Send everything away", () => Pressed(() => OSSpaces.SendEverythingAway()), OSButtonStyle.Secondary);
            OSUi.Button(top, "Close everything", CloseEverythingPressed, OSButtonStyle.Destructive);

            int closable = 0;
            for (int i = 0; i < items.Count; i++) if (items[i].kind != SpaceKind.Albie) closable++;
            TMP_Text summary = OSUi.Text(column, closable == 0 ? "Nothing is open around you." : closable == 1 ? "One thing is open around you." : closable + " things are open around you.", OSUi.SmallText, true);
            OSUi.Rigid(summary);

            Transform head = OSShell.FindHead();
            int start = page * RowsPerPage, end = Mathf.Min(items.Count, start + RowsPerPage);
            for (int i = start; i < end; i++) BuildRow(column, items[i], head);

            var spacer = new GameObject("Spacer", typeof(RectTransform));
            spacer.transform.SetParent(column, false);
            spacer.AddComponent<LayoutElement>().flexibleHeight = 1f;
            if (PageCount > 1) BuildPager(column);

            if (pendingClose != null)
            {
                Action yes = pendingClose;
                string question = pendingQuestion, label = pendingLabel;
                pendingClose = null;
                OSUi.Confirm(panel.transform, question, label ?? "Close", () => Pressed(yes));
            }
        }

        void CloseEverythingPressed()
        {
            // Closing the artifact, its course or the truck is more than closing windows: say what goes and ask first.
            bool more = OSArtifacts.OpenId() != null || OSSpaces.AtRisk();
            if (!more)
            {
                Pressed(() => OSSpaces.CloseEverything(true));
                return;
            }
            OSUi.Confirm(panel.transform, ClosingQuestion(), "Close everything", () => Pressed(() => OSSpaces.CloseEverything(true)));
        }

        void BuildRow(RectTransform column, SpaceItem item, Transform head)
        {
            RectTransform row = OSUi.Row(column, RowHeight, 12f);
            row.name = "Space " + item.title;
            OSUi.Rigid(row);

            var iconObject = new GameObject("Icon", typeof(RectTransform));
            iconObject.transform.SetParent(row, false);
            var icon = iconObject.AddComponent<Image>();
            icon.sprite = OSUi.Icon(item.icon);
            icon.enabled = icon.sprite != null;
            icon.color = new Color(1f, 1f, 1f, 0.9f);
            icon.raycastTarget = false;
            icon.preserveAspect = true;
            OSUi.Size(iconObject.transform, IconUnits, IconUnits);

            TMP_Text title = OSUi.Text(row, item.title, OSUi.BodyText);
            title.textWrappingMode = TextWrappingModes.NoWrap;
            title.overflowMode = TextOverflowModes.Ellipsis;
            title.alignment = TextAlignmentOptions.MidlineLeft;
            title.GetComponent<LayoutElement>().minWidth = 120f;

            TMP_Text where = OSUi.Text(row, OSSpaces.Where(item, head), OSUi.SmallText, true);
            where.textWrappingMode = TextWrappingModes.NoWrap;
            where.overflowMode = TextOverflowModes.Ellipsis;
            where.alignment = TextAlignmentOptions.MidlineLeft;
            var whereLayout = where.GetComponent<LayoutElement>();
            whereLayout.minWidth = whereLayout.preferredWidth = WhereUnits;
            whereLayout.flexibleWidth = 0f;
            whereTexts.Add(where);
            whereItems.Add(item);

            Button here = OSUi.Button(row, "Here", () => Pressed(item.bring), OSButtonStyle.Secondary);
            if (here != null) here.interactable = item.bring != null;
            Button middle = item.away
                ? OSUi.Button(row, "Back", () => Pressed(item.restore), OSButtonStyle.Secondary)
                : OSUi.Button(row, "Away", () => Pressed(item.send), OSButtonStyle.Secondary);
            if (middle != null) middle.interactable = item.away ? item.restore != null : item.send != null;
            Button close = OSUi.Button(row, item.closeLabel, () => ClosePressed(item), OSButtonStyle.Secondary);
            if (close != null) close.interactable = item.close != null;
        }

        void ClosePressed(SpaceItem item)
        {
            if (item.close == null) return;
            if (item.closeQuestion == null && item.kind != SpaceKind.Artifact && item.kind != SpaceKind.Course)
            {
                Pressed(item.close);
                return;
            }
            string question = item.closeQuestion ?? (item.kind == SpaceKind.Course ? "Close the course? " + item.title.Replace(" course", "") + " closes with it." : "Close " + item.title + "?");
            OSUi.Confirm(panel.transform, question, item.closeLabel, () => Pressed(item.close));
        }

        void UpdateWhere()
        {
            Transform head = OSShell.FindHead();
            for (int i = 0; i < whereTexts.Count && i < whereItems.Count; i++)
            {
                if (whereTexts[i] == null) continue;
                // The rows were built from an earlier read: find the same thing in this one to say where it is now.
                SpaceItem now = Same(whereItems[i].key);
                if (now == null) continue;
                string text = OSSpaces.Where(now, head);
                if (whereTexts[i].text != text) whereTexts[i].text = text;
            }
        }

        SpaceItem Same(string itemKey)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].key == itemKey) return items[i];
            return null;
        }

        void BuildPager(RectTransform column)
        {
            RectTransform row = OSUi.Row(column, OSUi.ButtonHeight, 16f);
            row.name = "Pager";
            OSUi.Rigid(row);
            row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
            Button back = OSUi.Button(row, null, () => SetPage(page - 1), OSButtonStyle.Borderless, "chevron_left");
            TMP_Text label = OSUi.Text(row, (page + 1) + " of " + PageCount, OSUi.BodyText, true);
            label.alignment = TextAlignmentOptions.Center;
            Button next = OSUi.Button(row, null, () => SetPage(page + 1), OSButtonStyle.Borderless, "chevron_right");
            if (back != null) back.interactable = page > 0;
            if (next != null) next.interactable = page < PageCount - 1;
        }
    }
}
