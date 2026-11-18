using System.Collections.Generic;
using Forge.Notebook;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The project bar (window id "project_rail"): a slim window that stays with the person while a project is open. Its
    /// header is the project's name and one line (why a tapped stage is locked, the last result, or what to do now); its one
    /// row is a home button (the project window, the overview), the stages left to right (done ones quiet with a check,
    /// stale ones with a warning mark, the one at hand ringed, locked ones faded but tappable) and one Next button ("Next:
    /// Train", "Fly v8", "Go to Fly" after a locked stage was tapped). A stage tap is <see cref="OSProjectSession.Go"/>: the
    /// tool of the stage before is put away and this stage's opens, so moving between stages, and going back, is one tap.
    ///
    /// Where it sits: docked under the window the stage at hand opened (the Drive window, the Truck design window, the
    /// brain trainer) or under the Workspace mode's panels (the ML desk; in a mode with a model on the table, CAD, under
    /// the panel furthest to one side, clear of the model and the table button), pulled in along the line of sight to within a
    /// seated arm's reach so a finger reaches it without it covering the tool; when the stage has no tool up, or docking
    /// would put it too low, it floats low in front of the person. It follows its tool when that moves; once the person
    /// moves the bar it stays where they put it until the stage changes, and if it has been out of sight for a couple of
    /// seconds it comes back in front. Its close button closes the project (the bar and the project window; the tools stay).
    /// Placement runs five times a second and allocates nothing.
    /// </summary>
    public sealed class OSProjectRail : MonoBehaviour
    {
        public const string WindowId = "project_rail";
        /// <summary>The name of the home button (an icon button: the project window).</summary>
        public const string HomeIcon = "home";

        /// <summary>How far from the eyes the docked bar sits at most (m): a seated arm's reach.</summary>
        internal const float Reach = 0.55f;
        const float PlaceSeconds = 0.2f, DockGap = 0.05f, FloatDistance = 0.5f, FloatDrop = 0.28f;
        const float MaxDownSine = 0.77f;    // about 50 degrees below straight ahead: lower than that, the bar floats instead
        const float MovedBy = 0.02f, FollowStep = 0.015f, SettleSeconds = 1.2f, LostSeconds = 2f, ViewAngle = 55f;
        const float MinWidthMm = 400f, FallbackHeightMm = 150f;

        static OSProjectRail current;

        /// <summary>The bar while it is up, or null.</summary>
        public static OSProjectRail Current => current != null ? current : null;

        OSProjectSession session;
        ForgePanel panel;
        OSWindow window;
        int builtKey = int.MinValue;
        bool placed, pinned, force = true;
        int lastAnchor = int.MinValue;
        float nextPlace, settleUntil, outOfViewSince = -1f;
        Vector3 lastPlaced;
        readonly List<Workspace.ItemSlot> slots = new List<Workspace.ItemSlot>();
        readonly List<Button> chips = new List<Button>(6);

        /// <summary>The stage chips as last built, in order (checks read them).</summary>
        public IReadOnlyList<Button> Chips => chips;
        /// <summary>The Next button as last built, or null when there is nothing to offer.</summary>
        public Button NextButton { get; private set; }
        /// <summary>True while the bar sits under the tool of the stage at hand.</summary>
        public bool Docked { get; private set; }

        /// <summary>Puts the bar up for the session's project (or refreshes it). Null when the scene has no shell.</summary>
        public static OSWindow Show(OSProjectSession session)
        {
            OSShell shell = OSShell.Current;
            if (shell == null || session == null || !session.Active) return null;
            OSWindow w = shell.Windows.Find(WindowId);
            if (w == null)
            {
                w = shell.Windows.Open(WindowId, OSProjects.TitleFor(session.ProjectId), new Vector2(MinWidthMm / 1000f, FallbackHeightMm / 1000f), p =>
                {
                    var rail = p.gameObject.AddComponent<OSProjectRail>();
                    rail.Init(p, session);
                });
                if (w == null) return null;
                w.Closed += session.End;  // its close button closes the project
            }
            else shell.Windows.Restore(w);
            var bar = w.GetComponent<OSProjectRail>();
            if (bar != null)
            {
                bar.window = w;
                bar.builtKey = int.MinValue;
                bar.Refresh();
                bar.Redock();
            }
            return w;
        }

        /// <summary>Takes the bar down.</summary>
        public static void Hide()
        {
            OSShell shell = OSShell.Current;
            if (shell != null) shell.Windows.Close(WindowId);
        }

        void Init(ForgePanel owner, OSProjectSession s)
        {
            panel = owner;
            session = s;
            current = this;
            session.Changed += Refresh;
            Refresh();
        }

        void OnDestroy()
        {
            if (session != null) session.Changed -= Refresh;
            if (current == this) current = null;
        }

        /// <summary>Places the bar again now (a stage just opened its tool), even where the person had put it.</summary>
        public void Redock()
        {
            force = true;
            pinned = false;
            settleUntil = Time.unscaledTime + SettleSeconds;
            nextPlace = 0f;
        }

        // ---------------------------------------------------------------- the row

        void Refresh()
        {
            if (panel == null || session == null || !session.Active) return;
            panel.Title = OSProjects.TitleFor(session.ProjectId);
            panel.Subtitle = HeaderLine(session.BarLine());
            int key = Key();
            if (key == builtKey) return;
            builtKey = key;
            Build();
        }

        /// <summary>The bar's header holds one short line: the first sentence of the bar's line, cut to fit beside the title.</summary>
        public const int HeaderChars = 46;

        static string HeaderLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return "";
            int stop = line.IndexOf(". ", System.StringComparison.Ordinal);
            if (stop > 0) line = line.Substring(0, stop + 1);
            return ProjectStages.Clip(line, HeaderChars);
        }

        // What the row shows: every stage's status, the one at hand, Next, the notice.
        int Key()
        {
            IReadOnlyList<StageState> stages = session.Stages;
            unchecked
            {
                int key = 17 + (session.ProjectId?.GetHashCode() ?? 0);
                for (int i = 0; i < stages.Count; i++)
                    key = key * 31 + (int)stages[i].Status * 4 + (stages[i].IsCurrent ? 1 : 0) + (stages[i].IsNext ? 2 : 0);
                string next = session.NextLabel();
                key = key * 31 + (next?.GetHashCode() ?? 0);
                return key;
            }
        }

        void Build()
        {
            RectTransform body = OSUi.Body(panel);
            if (body == null) return;
            OSUi.Clear(body);
            chips.Clear();
            NextButton = null;
            RectTransform column = OSUi.Column(body, 0f);
            int pad = Mathf.RoundToInt(OSProjectLayout.RailPadding);
            var layout = column.GetComponent<VerticalLayoutGroup>();
            if (layout != null) layout.padding = new RectOffset(pad, pad, pad, pad);
            RectTransform row = OSUi.Row(column, OSUi.ChipHeight, OSProjectLayout.RailGap);
            OSUi.Rigid(row);

            OSUi.IconButton(row, HomeIcon, session.ShowHome, OSUi.ChipHeight, true);  // tonal: it reads as a button, not a picture

            IReadOnlyList<StageState> stages = session.Stages;
            for (int i = 0; i < stages.Count; i++)
            {
                string id = stages[i].Id;
                Button chip = OSProjectLayout.RailChip(row, stages[i], () => session.Go(id));
                if (chip != null) chips.Add(chip);
            }
            string next = session.NextLabel();
            if (next != null)
            {
                NextButton = OSUi.Button(row, next, () => session.GoNext(), OSButtonStyle.Primary, "chevron_right");
                OSProjectLayout.Fit(NextButton, OSUi.ChipHeight);
            }
            FitPanel(row, pad);
        }

        // The window as wide as its row and as tall as one chip row under the header.
        void FitPanel(RectTransform row, int pad)
        {
            float units = 0f;
            int n = 0;
            for (int i = 0; i < row.childCount; i++)
            {
                var e = row.GetChild(i).GetComponent<LayoutElement>();
                if (e == null || e.ignoreLayout) continue;
                units += e.preferredWidth;
                n++;
            }
            units += OSProjectLayout.RailGap * Mathf.Max(0, n - 1) + pad * 2f;
            float heightUnits = OSUi.ChipHeight + pad * 2f;
            OSKit kit = OSKit.Current;
            float mmPerUnit = (kit != null ? kit.unitMeters : 0.00065f) / ForgePanel.MetersPerUnit;
            var rect = (RectTransform)panel.transform;
            Vector2 frame = panel.Content != null ? rect.sizeDelta - panel.Content.rect.size : new Vector2(36f, 100f);
            float width = Mathf.Max(MinWidthMm, units * mmPerUnit + frame.x);
            float height = heightUnits * mmPerUnit + frame.y;
            panel.SizeMeters = new Vector2(width, height) * ForgePanel.MetersPerUnit;
        }

        // ---------------------------------------------------------------- where it sits

        void Update()
        {
            if (session == null || !session.Active || window == null) return;
            float now = Time.unscaledTime;
            if (now < nextPlace) return;
            nextPlace = now + PlaceSeconds;
            OSShell shell = OSShell.Current;
            Transform head = OSShell.FindHead();
            if (shell == null || head == null) return;

            Vector3 eye = head.position;
            Vector3 here = CenterNow();
            if (placed && now > settleUntil && (here - lastPlaced).sqrMagnitude > MovedBy * MovedBy) pinned = true;  // the person moved it

            bool anchored = session.AnchorFor(eye, slots, out Vector3 bottom, out Vector3 top, out Vector3 up, out int anchorId);
            if (!anchored) anchorId = 0;
            if (anchorId != lastAnchor)
            {
                lastAnchor = anchorId;
                force = true;
                pinned = false;
                settleUntil = now + SettleSeconds;
            }

            Vector3 toHere = here - eye;
            bool inView = toHere.sqrMagnitude < 1.69f && Vector3.Angle(head.forward, toHere) < ViewAngle;
            if (inView) outOfViewSince = -1f;
            else if (outOfViewSince < 0f) outOfViewSince = now;
            bool lost = outOfViewSince >= 0f && now - outOfViewSince > LostSeconds;
            if (lost) pinned = false;
            if (pinned && !force) return;

            Vector3 target = Vector3.zero;
            bool docked = anchored && DockedAt(eye, bottom, top, up, out target);
            if (!docked)
            {
                if (!force && !lost && placed) return;  // floating: it stays put until its tool changes or it is out of sight
                target = FloatAt(head);
            }
            if (!force && placed && (target - lastPlaced).sqrMagnitude < FollowStep * FollowStep) return;

            Vector3 toward = target - eye;
            Quaternion facing = toward.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(toward, Vector3.up);
            shell.Windows.PlaceAt(window, target, facing);
            lastPlaced = CenterNow();
            placed = true;
            force = false;
            Docked = docked;
            if (lost) outOfViewSince = -1f;
        }

        // Under the tool, pulled toward the eyes to arm's reach along the line of sight, so its top edge lines up just
        // under the tool's bottom edge as seen from the eyes. False when that is too far down to look at comfortably.
        bool DockedAt(Vector3 eye, Vector3 bottom, Vector3 top, Vector3 up, out Vector3 target)
        {
            target = Vector3.zero;
            Vector3 middle = (bottom + top) * 0.5f;
            float distance = Vector3.Distance(eye, middle);
            if (distance < 0.05f) return false;
            float pull = Mathf.Max(1f, distance / Reach);
            float halfHeight = panel != null ? panel.SizeMeters.y * 0.5f : 0.06f;
            Vector3 below = bottom - up * (DockGap + halfHeight * pull);
            Vector3 direction = (below - eye).normalized;
            if (direction.y < -MaxDownSine) return false;
            target = eye + direction * Mathf.Min(distance, Reach);
            return true;
        }

        // Low in front, within reach.
        static Vector3 FloatAt(Transform head)
        {
            Vector3 flat = head.forward;
            flat.y = 0f;
            flat = flat.sqrMagnitude < 1e-4f ? Vector3.forward : flat.normalized;
            return head.position + flat * FloatDistance + Vector3.down * FloatDrop;
        }

        Vector3 CenterNow()
        {
            var rect = transform as RectTransform;
            return rect != null ? rect.TransformPoint(rect.rect.center) : transform.position;
        }
    }
}
