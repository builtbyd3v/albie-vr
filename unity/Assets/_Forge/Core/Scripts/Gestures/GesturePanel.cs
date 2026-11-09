using System.Collections.Generic;
using Forge.Pet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The gesture display: live bars of what each hand's fingers are doing and the word for the pose, then every gesture
/// Albie knows with how to make it and what it does. A row flashes when its gesture fires. The pill on a row steps through
/// what that gesture does, the "Teach a gesture" pill runs the same three takes as asking Albie, and a taught gesture has
/// a "Forget" pill, so all of it works by hand, without talking. Built at runtime inside the workspace's ForgePanel.
/// </summary>
public class GesturePanel : MonoBehaviour
{
    public const string PanelId = "gestures";

    const float Width = 720f, RowHeight = 44f, HandColumn = 214f, Pad = 18f, BarWidth = 13f, BarStep = 18f, BarHeight = 86f;
    const float MinHeight = 400f, Footer = 58f;
    const float FlashSeconds = 1.2f;
    static readonly string[] FingerLetters = { "T", "I", "M", "R", "P" };

    class Row
    {
        public RectTransform rect;
        public ForgeSurface flash;
        public TMP_Text title, how;
        public ForgePill action, forget;
        public GestureDef def;
        public float flashUntil;
    }

    class HandMeter
    {
        public TMP_Text name, pose;
        public RectTransform[] fills = new RectTransform[5];
        public float[] shown = { -1f, -1f, -1f, -1f, -1f };
        public string shownPose;
    }

    GestureEngine engine;
    ForgePanel panel;
    readonly List<Row> rows = new List<Row>();
    readonly HandMeter[] meters = { new HandMeter(), new HandMeter() };
    TMP_Text status;
    ForgePill teachPill;
    RectTransform rowsRoot;
    string shownStatus;
    string note = "Try a gesture.";
    float noteUntil;
    bool placed;

    /// <summary>Make the display from the workspace's panel prefab. Null if there is no panel prefab in this scene.</summary>
    public static GesturePanel Create(GestureEngine engine)
    {
        ForgePanel prefab = engine.panelPrefab;
        if (prefab == null)
        {
            var workspace = Workspace.Current != null ? Workspace.Current : FindAnyObjectByType<Workspace>();
            if (workspace != null) prefab = workspace.panelPrefab;
        }
        if (prefab == null)
        {
            Debug.LogWarning("[Gestures] no panel prefab here, so no gesture display.");
            return null;
        }
        ForgePanel panel = Instantiate(prefab);
        panel.name = "GesturePanel";
        panel.panelId = PanelId;
        var display = panel.gameObject.AddComponent<GesturePanel>();
        display.Build(engine, panel);
        return display;
    }

    public void Show(bool show)
    {
        if (show && !placed) Place();
        gameObject.SetActive(show);
    }

    void Build(GestureEngine owner, ForgePanel forgePanel)
    {
        engine = owner;
        panel = forgePanel;
        panel.Title = "Hands";
        panel.Subtitle = "What Albie sees your hands doing";
        panel.SetBadge("HANDS", ForgeTheme.Tool);
        var placeholder = panel.Content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);

        Transform head = PetUser.Head;
        var handles = panel.gameObject.AddComponent<PanelManipulator>();
        var workspace = Workspace.Current;
        handles.Init(head, workspace != null && workspace.presence != null ? workspace.presence.Frame : head);

        for (int i = 0; i < 2; i++) BuildMeter(meters[i], i, i == 0 ? "Left" : "Right");

        rowsRoot = Rect("Rows", panel.Content);
        rowsRoot.anchorMin = new Vector2(0f, 0f);
        rowsRoot.anchorMax = new Vector2(1f, 1f);
        rowsRoot.offsetMin = new Vector2(HandColumn + Pad * 2f, Footer);
        rowsRoot.offsetMax = new Vector2(-Pad, -Pad);

        status = Text("Status", panel.Content, 14f, ForgeTheme.PanelSubtext);
        status.rectTransform.anchorMin = new Vector2(0f, 0f);
        status.rectTransform.anchorMax = new Vector2(1f, 0f);
        status.rectTransform.pivot = new Vector2(0f, 0f);
        status.rectTransform.offsetMin = new Vector2(Pad, 14f);
        status.rectTransform.offsetMax = new Vector2(-190f, 44f);
        status.alignment = TextAlignmentOptions.MidlineLeft;

        teachPill = ForgePill.Create(panel.Content, "Teach a gesture", TeachByHand, 34f, 14f);
        teachPill.Rect.anchorMin = teachPill.Rect.anchorMax = teachPill.Rect.pivot = new Vector2(1f, 0f);
        teachPill.Rect.anchoredPosition = new Vector2(-Pad, 12f);

        engine.Library.Changed += Rebuild;
        engine.Fired += OnFired;
        Rebuild();
    }

    void OnDestroy()
    {
        if (engine == null) return;
        engine.Library.Changed -= Rebuild;
        engine.Fired -= OnFired;
    }

    // To the left of where you're looking, facing you, and level with your eyes.
    void Place()
    {
        placed = true;
        Transform head = PetUser.Head;
        if (head == null) return;
        Vector3 flat = head.forward;
        flat.y = 0f;
        if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
        Vector3 direction = Quaternion.Euler(0f, -34f, 0f) * flat.normalized;
        Vector3 center = head.position + direction * 0.95f + Vector3.down * 0.05f;
        transform.rotation = Quaternion.LookRotation(center - head.position, Vector3.up);
        transform.position += center - transform.TransformPoint(((RectTransform)transform).rect.center);
    }

    // ---------------------------------------------------------------- the hands

    void BuildMeter(HandMeter meter, int index, string handName)
    {
        float left = Pad + index * (HandColumn * 0.5f + 8f);
        meter.name = Text(handName + " name", panel.Content, 13f, ForgeTheme.PanelSubtext);
        Pin(meter.name.rectTransform, left, 22f, 100f, 20f);
        meter.name.text = handName;

        meter.pose = Text(handName + " pose", panel.Content, 16f, ForgeTheme.PanelText);
        meter.pose.fontStyle = FontStyles.Bold;
        Pin(meter.pose.rectTransform, left, 46f, 100f, 24f);

        for (int f = 0; f < 5; f++)
        {
            float x = left + f * BarStep;
            var track = Rect("Track", panel.Content);
            Pin(track, x, 82f, BarWidth, BarHeight);
            var trackSurface = track.gameObject.AddComponent<ForgeSurface>();
            trackSurface.Pill = true;
            trackSurface.color = new Color(1f, 1f, 1f, 0.09f);
            trackSurface.raycastTarget = false;

            var fill = Rect("Fill", track);
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(1f, 0f);
            fill.pivot = new Vector2(0.5f, 0f);
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = new Vector2(0f, 14f);
            var fillSurface = fill.gameObject.AddComponent<ForgeSurface>();
            fillSurface.Pill = true;
            fillSurface.color = ForgeTheme.PanelText;
            fillSurface.raycastTarget = false;
            meter.fills[f] = fill;

            var letter = Text("Letter", panel.Content, 11f, ForgeTheme.PanelSubtext);
            letter.alignment = TextAlignmentOptions.Center;
            Pin(letter.rectTransform, x - 3f, 82f + BarHeight + 4f, BarWidth + 6f, 16f);
            letter.text = FingerLetters[f];
        }
    }

    void Update()
    {
        for (int h = 0; h < 2; h++) UpdateMeter(meters[h], engine.Hands[h]);

        float now = Time.time;
        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];
            float left = row.flashUntil - now;
            float alpha = left > 0f ? 0.26f * left / FlashSeconds : 0f;
            if (!Mathf.Approximately(row.flash.color.a, alpha)) row.flash.color = new Color(1f, 1f, 1f, alpha);
        }

        string prompt = engine.TeachPrompt;
        if (prompt == null && now > noteUntil && engine.Actions.AlbieOnHand) prompt = "Albie's on your hand. Talk to him.";
        string line = prompt ?? note;
        if (!ReferenceEquals(line, shownStatus) && line != shownStatus)
        {
            shownStatus = line;
            status.text = line;
            status.color = prompt != null ? ForgeTheme.PanelText : ForgeTheme.PanelSubtext;
        }
    }

    static void UpdateMeter(HandMeter meter, GestureHand hand)
    {
        string pose = hand.Usable ? GestureLibrary.PoseName(hand.values) : "No hand";
        if (!ReferenceEquals(pose, meter.shownPose))
        {
            meter.shownPose = pose;
            meter.pose.text = pose;
            meter.pose.color = hand.Usable ? ForgeTheme.PanelText : ForgeTheme.PanelSubtext;
        }
        for (int f = 0; f < 5; f++)
        {
            float value = hand.Usable ? hand.values[(int)HandFeature.ThumbCurl + f] : 0f;
            if (Mathf.Abs(value - meter.shown[f]) < 0.01f) continue;
            meter.shown[f] = value;
            // Never less than a dot: a pill narrower than it is tall stops looking like a pill.
            meter.fills[f].offsetMax = new Vector2(0f, Mathf.Lerp(14f, BarHeight, value));
        }
    }

    // ---------------------------------------------------------------- the list

    void Rebuild()
    {
        var all = engine.Library.All;
        while (rows.Count < all.Count) rows.Add(BuildRow(rows.Count));
        for (int i = 0; i < rows.Count; i++)
        {
            bool used = i < all.Count;
            rows[i].rect.gameObject.SetActive(used);
            if (!used) continue;
            Row row = rows[i];
            row.def = all[i];
            row.title.text = row.def.title;
            row.how.text = row.def.how;
            SetActionLabel(row);
            row.forget.gameObject.SetActive(!row.def.builtin);
            row.flashUntil = 0f;
        }
        float height = Mathf.Max(MinHeight, ForgeTheme.PanelHeader + Pad * 2f + Footer + all.Count * RowHeight);
        panel.SizeMeters = new Vector2(Width, height) * ForgePanel.MetersPerUnit;
    }

    Row BuildRow(int index)
    {
        var row = new Row { rect = Rect("Row", rowsRoot) };
        row.rect.anchorMin = new Vector2(0f, 1f);
        row.rect.anchorMax = new Vector2(1f, 1f);
        row.rect.pivot = new Vector2(0.5f, 1f);
        row.rect.sizeDelta = new Vector2(0f, RowHeight - 4f);
        row.rect.anchoredPosition = new Vector2(0f, -index * RowHeight);

        row.flash = row.rect.gameObject.AddComponent<ForgeSurface>();
        row.flash.Corner = 10f;
        row.flash.color = new Color(1f, 1f, 1f, 0f);
        row.flash.raycastTarget = false;

        row.title = Text("Title", row.rect, 14.5f, ForgeTheme.PanelText);
        row.title.fontStyle = FontStyles.Bold;
        Fill(row.title.rectTransform, 10f, 3f, 150f, 20f);
        row.how = Text("How", row.rect, 11.5f, ForgeTheme.PanelSubtext);
        Fill(row.how.rectTransform, 10f, 22f, 150f, 16f);

        Row captured = row;
        row.action = ForgePill.Create(row.rect, "Nothing", () => StepAction(captured), 26f, 12f);
        PinRight(row.action.Rect, 8f);
        row.forget = ForgePill.Create(row.rect, "Forget", () => Forget(captured), 26f, 12f);
        PinRight(row.forget.Rect, 138f);
        return row;
    }

    void SetActionLabel(Row row)
    {
        string text = GestureActions.Describe(row.def);
        var label = row.action.GetComponentInChildren<TMP_Text>();
        label.text = text;
        row.action.Rect.sizeDelta = new Vector2(Mathf.Max(112f, label.GetPreferredValues(text).x + 24f), 26f);
    }

    void StepAction(Row row)
    {
        var presets = GestureActions.Presets;
        string current = GestureActions.Describe(row.def);
        int at = -1;
        for (int i = 0; i < presets.Length; i++)
            if (presets[i].label == current) at = i;
        var next = presets[(at + 1) % presets.Length];
        engine.Library.Bind(row.def, next.action, next.Args());
    }

    void Forget(Row row)
    {
        string title = row.def.title;
        engine.Actions.Confirm($"Forget the gesture '{title}'?", () =>
        {
            engine.Library.Remove(engine.Library.Find(title));
            Note($"Forgot {title}.");
        });
        Note("Tap Yes on Albie's card to forget it.");
    }

    void OnFired(GestureDef def, GestureHand hand)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].def == def) rows[i].flashUntil = Time.time + FlashSeconds;
        Note($"{(hand.sample.isLeft ? "Left" : "Right")} hand: {def.title}.");
    }

    void TeachByHand()
    {
        if (engine.TeachPrompt != null)
        {
            engine.CancelTeach();
            return;
        }
        int n = 1;
        while (engine.Library.Find("Gesture " + n) != null) n++;
        string name = "Gesture " + n;
        engine.Teach(name, GestureActions.None, null, result =>
        {
            Note(result["error"] != null ? (string)result["error"] : $"Learned {name}. Use its pill to choose what it does.");
        });
    }

    void Note(string text)
    {
        note = text;
        noteUntil = Time.time + 2f;
    }

    // ---------------------------------------------------------------- layout helpers

    static RectTransform Rect(string name, RectTransform parent)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        return rt;
    }

    static TMP_Text Text(string name, RectTransform parent, float size, Color color)
    {
        var text = Rect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
        if (ForgeTheme.Font != null) text.font = ForgeTheme.Font;
        text.fontSize = size;
        text.color = color;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        return text;
    }

    // Placed from the content's top left, in mm.
    static void Pin(RectTransform rt, float left, float top, float width, float height)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(left, -top);
        rt.sizeDelta = new Vector2(width, height);
    }

    // Across a row from its left edge, leaving the right side for the pills.
    static void Fill(RectTransform rt, float left, float top, float right, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(left, -top - height);
        rt.offsetMax = new Vector2(-right, -top);
    }

    static void PinRight(RectTransform rt, float right)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0.5f);
        rt.anchoredPosition = new Vector2(-right, 0f);
    }
}
