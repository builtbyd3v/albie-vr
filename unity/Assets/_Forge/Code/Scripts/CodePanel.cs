using System.Collections.Generic;
using System.Text;
using Forge.Controllers;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// The code view on the headset. The left column lists files, the right pane shows one (monospace, line numbers,
/// simple Python colors), and the bottom bar has the status line. It has three views:
/// <list type="bullet">
/// <item>Sample: the bundled project in <see cref="CodeProject"/>, with Albie's highlight and note, Undo and Reset
/// (tap twice). Albie drives it through <see cref="CodeTools"/>. With the hub off this is the only view.</item>
/// <item>Repo and PRs (hub live): the project's GitHub repo and its pull requests as diffs, read-only, from
/// <see cref="GitHubView"/>. Albie drives them through <see cref="GitHubTools"/>.</item>
/// </list>
/// The view follows whichever of the two Albie last acted on; the tabs switch it by hand. The THING CONTROLLER block
/// stays at the bottom of the left column in every view.
/// Lives on Code/Resources/CodePanel.prefab (a ForgePanel; Forge > Code > Build Code Panel Prefab). UI is built in code.
/// Redraws only when something changes, never per frame.
/// </summary>
[RequireComponent(typeof(ForgePanel))]
public class CodePanel : MonoBehaviour
{
    enum View { None, Sample, Repo, Prs }

    const float SideWidth = 150f, BarHeight = 40f, TabHeight = 30f, Gap = 10f, FontSize = 12.5f, Pad = 8f;
    const float TitleHeight = 26f, ControllerHeight = 130f;
    // One TMP mesh holds the whole file, so a long one is cut here to keep the Quest's frame time.
    const int MaxLines = 500, LabelChars = 19;
    static readonly Color CodeBg = new Color(0f, 0f, 0f, 0.28f);
    static readonly Color ErrorTint = new Color(1f, 0.35f, 0.35f, 0.22f);

    ForgePanel panel;
    RectTransform tabs, side, codeBox, listContent, viewport, content, bar;
    TMP_Text code, note, status, listTitle;
    Image highlight;
    ScrollRect scroll;
    ForgePill sampleTab, repoTab, prsTab, undo, reset, approve, merge;
    // A long line runs past the right edge: a fade there says "more", and a thin bar along the bottom shows where you are.
    Image edgeFade, hTrack, hThumb;
    static Sprite fadeSprite;
    TMP_InputField controllerSpeed;
    TMP_Text controllerState;
    ForgePill controllerApply;
    // The running truck's controller, re-read twice a second: a Thing can start or swap controllers while the panel is
    // open, and the code panel only refreshes when its own files change.
    float controllerPoll;
    ControllerFile shownFile;
    float shownSpeed = float.NaN;
    readonly List<(ForgePill pill, CodeProject.File file)> pills = new List<(ForgePill, CodeProject.File)>();
    readonly List<ForgePill> repoPills = new List<ForgePill>();
    bool dirty = true, connected;
    // A pill that acts on the second tap (Reset, Approve, Merge) while it is armed.
    ForgePill armed;
    string armedLabel;
    float armedUntil;
    string shownText, shownPath;
    int shownStart = -1;
    View view = View.Sample, shownView = View.None;
    int shownVersion = -1;
    string shownFolder;
    GitHubView.PrDetail shownPr;
    // What the sample project last showed, to tell "Albie opened or edited a sample file" from a status line.
    CodeProject.File seenOpen;
    string seenText;
    int seenStart;
    // A short message (the controller's) that takes the status line in any view.
    string notice;
    float noticeUntil;

    void Awake()
    {
        panel = GetComponent<ForgePanel>();
        if (panel.Content == null) return;
        var placeholder = panel.Content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);
        Build();
        var _ = CodeProject.Files;  // loads the sample, so its first load doesn't read as Albie opening a file
        SampleMoved();
    }

    void OnEnable()
    {
        CodeProject.Changed += MarkDirty;
        GitHubView.Changed += MarkDirty;
        ForgeNet.StatusChanged += OnHub;
        dirty = true;
    }

    void OnDisable()
    {
        CodeProject.Changed -= MarkDirty;
        GitHubView.Changed -= MarkDirty;
        ForgeNet.StatusChanged -= OnHub;
    }

    void MarkDirty() => dirty = true;
    void OnHub(ForgeNetStatus _) => dirty = true;

    void LateUpdate()
    {
        if (armed != null && Time.unscaledTime > armedUntil) Disarm();
        if (controllerState != null && Time.unscaledTime >= controllerPoll)
        {
            controllerPoll = Time.unscaledTime + 0.5f;
            RefreshControllerState();
        }
        UpdateScrollCues();
        if (code == null) return;
        GitHubView.SyncProject();
        if (notice != null && Time.unscaledTime > noticeUntil)
        {
            notice = null;
            dirty = true;
        }
        if (!dirty) return;
        dirty = false;
        Refresh();
    }

    void BuildScrollCues(RectTransform codeBox)
    {
        if (fadeSprite == null)
        {
            var tex = new Texture2D(32, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int i = 0; i < 32; i++) tex.SetPixel(i, 0, new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, i / 31f)));
            tex.Apply();
            fadeSprite = Sprite.Create(tex, new Rect(0f, 0f, 32f, 1f), new Vector2(0.5f, 0.5f), 100f);
        }
        edgeFade = Rect("EdgeFade", codeBox, new Vector2(1f, 0f), Vector2.one).gameObject.AddComponent<Image>();
        edgeFade.sprite = fadeSprite;
        edgeFade.color = new Color(0.16f, 0.165f, 0.17f, 1f);
        edgeFade.raycastTarget = false;
        var frt = edgeFade.rectTransform;
        frt.pivot = new Vector2(1f, 0.5f);
        frt.offsetMin = new Vector2(-90f, Pad);
        frt.offsetMax = new Vector2(-Pad, -Pad);
        hTrack = Rect("HTrack", codeBox, new Vector2(0f, 0f), new Vector2(1f, 0f)).gameObject.AddComponent<Image>();
        hTrack.color = new Color(1f, 1f, 1f, 0.12f);
        hTrack.raycastTarget = false;
        hTrack.rectTransform.pivot = new Vector2(0.5f, 0f);
        hTrack.rectTransform.offsetMin = new Vector2(Pad + 6f, 3f);
        hTrack.rectTransform.offsetMax = new Vector2(-Pad - 6f, 7f);
        hThumb = Rect("HThumb", hTrack.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f)).gameObject.AddComponent<Image>();
        hThumb.color = new Color(1f, 1f, 1f, 0.55f);
        hThumb.raycastTarget = false;
        hThumb.rectTransform.pivot = new Vector2(0f, 0.5f);
    }

    void UpdateScrollCues()
    {
        if (edgeFade == null || viewport == null) return;
        float view = viewport.rect.width, total = content.sizeDelta.x;
        bool wide = total > view + 1f;
        if (edgeFade.enabled != wide) { edgeFade.enabled = wide; hTrack.enabled = wide; hThumb.enabled = wide; }
        if (!wide) return;
        float maxX = total - view;
        float x = Mathf.Clamp(-content.anchoredPosition.x, 0f, maxX);
        edgeFade.color = new Color(0.16f, 0.165f, 0.17f, x >= maxX - 2f ? 0f : 1f);
        float trackW = hTrack.rectTransform.rect.width;
        float thumbW = Mathf.Max(24f, trackW * view / total);
        var trt = hThumb.rectTransform;
        trt.sizeDelta = new Vector2(thumbW, 0f);
        trt.anchoredPosition = new Vector2((trackW - thumbW) * (x / maxX), 0f);
    }

    // ---------------------------------------------------------------- layout

    void Build()
    {
        RectTransform root = panel.Content;

        tabs = Rect("Tabs", root, new Vector2(0f, 1f), new Vector2(1f, 1f));
        tabs.offsetMin = new Vector2(0f, -TabHeight);
        sampleTab = Tab("Sample", 0f, 80f, () => { view = View.Sample; dirty = true; });
        repoTab = Tab("Repo", 88f, 150f, GitHubView.ShowFiles);
        prsTab = Tab("PRs", 246f, 70f, () => GitHubView.ShowPrs());
        tabs.gameObject.SetActive(false);

        side = Rect("Files", root, new Vector2(0f, 0f), new Vector2(0f, 1f));
        side.pivot = new Vector2(0f, 1f);
        side.offsetMin = new Vector2(0f, BarHeight + Gap);
        side.offsetMax = new Vector2(SideWidth, 0f);
        listTitle = Text("Title", side, 11f, ForgeTheme.PanelSubtext);
        listTitle.characterSpacing = 2f;
        Place(listTitle.rectTransform, 0f, 18f);

        var list = Rect("List", side, Vector2.zero, Vector2.one);
        list.offsetMin = new Vector2(0f, ControllerHeight + Gap);
        list.offsetMax = new Vector2(0f, -TitleHeight);
        list.gameObject.AddComponent<RectMask2D>();
        list.gameObject.AddComponent<Image>().color = Color.clear;  // a target for drag-to-scroll
        listContent = Rect("Content", list, new Vector2(0f, 1f), new Vector2(1f, 1f));
        listContent.pivot = new Vector2(0f, 1f);
        var listScroll = list.gameObject.AddComponent<ScrollRect>();
        listScroll.viewport = list;
        listScroll.content = listContent;
        listScroll.horizontal = false;
        listScroll.movementType = ScrollRect.MovementType.Clamped;
        listScroll.scrollSensitivity = 20f;

        // Controller code is a second live surface in the general Code panel. It is intentionally optional: when the
        // Thing runtime is not open, the field remains visible and explains that the value will apply on the next run.
        var controller = Rect("Controller", side, Vector2.zero, new Vector2(1f, 0f));
        controller.offsetMax = new Vector2(0f, ControllerHeight);
        var controllerTitle = Text("Title", controller, 10f, ForgeTheme.PanelSubtext);
        controllerTitle.text = "THING CONTROLLER";
        controllerTitle.characterSpacing = 1.5f;
        Place(controllerTitle.rectTransform, 0f, 16f);
        controllerSpeed = Forge.ML.MLUI.Input(controller, "max_speed", "max_speed", 0f, 20f, SideWidth - 6f, 28f);
        controllerSpeed.text = "1.5";
        controllerApply = Forge.ML.MLUI.Pill(controller, "Apply max_speed", 0f, 54f, SideWidth - 6f, 28f, ApplyControllerSpeed);
        controllerState = Text("State", controller, 10f, ForgeTheme.PanelSubtext);
        controllerState.textWrappingMode = TextWrappingModes.Normal;
        Place(controllerState.rectTransform, 88f, 38f);

        codeBox = Rect("Code", root, Vector2.zero, Vector2.one);
        codeBox.offsetMin = new Vector2(SideWidth + Gap, BarHeight + Gap);
        codeBox.offsetMax = Vector2.zero;
        var bg = codeBox.gameObject.AddComponent<ForgeSurface>();
        bg.Style = ForgeSurface.Kind.Fill;
        bg.Corner = 12f;
        bg.color = CodeBg;

        viewport = Rect("Viewport", codeBox, Vector2.zero, Vector2.one);
        viewport.offsetMin = new Vector2(Pad, Pad);
        viewport.offsetMax = new Vector2(-Pad, -Pad);
        viewport.gameObject.AddComponent<RectMask2D>();
        var hit = viewport.gameObject.AddComponent<Image>();  // a target for drag-to-scroll
        hit.color = Color.clear;

        content = Rect("Content", viewport, new Vector2(0f, 1f), new Vector2(0f, 1f));
        content.pivot = new Vector2(0f, 1f);

        highlight = Rect("Highlight", content, new Vector2(0f, 1f), new Vector2(0f, 1f)).gameObject.AddComponent<Image>();
        highlight.raycastTarget = false;
        highlight.rectTransform.pivot = new Vector2(0f, 1f);
        note = Text("Note", highlight.rectTransform, 11f, Color.white);
        note.alignment = TextAlignmentOptions.MidlineRight;
        note.fontStyle = FontStyles.Bold;
        var nrt = note.rectTransform;
        nrt.anchorMin = new Vector2(0f, 0f);
        nrt.anchorMax = new Vector2(1f, 1f);
        nrt.offsetMin = Vector2.zero;
        nrt.offsetMax = new Vector2(-8f, 0f);

        code = Text("Text", content, FontSize, ForgeTheme.PanelText);
        code.alignment = TextAlignmentOptions.TopLeft;
        code.richText = true;
        code.rectTransform.anchorMin = code.rectTransform.anchorMax = code.rectTransform.pivot = new Vector2(0f, 1f);
        code.rectTransform.anchoredPosition = Vector2.zero;

        BuildScrollCues(codeBox);

        BuildScrollCues(codeBox);

        scroll = codeBox.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 20f;

        bar = Rect("Bar", root, Vector2.zero, new Vector2(1f, 0f));
        bar.pivot = new Vector2(0.5f, 0f);
        bar.offsetMin = Vector2.zero;
        bar.offsetMax = new Vector2(0f, BarHeight);
        reset = ForgePill.Create(bar, "Reset", OnReset);
        reset.Rect.anchorMin = reset.Rect.anchorMax = reset.Rect.pivot = new Vector2(1f, 0.5f);
        reset.Rect.anchoredPosition = Vector2.zero;
        reset.Rect.sizeDelta = new Vector2(150f, reset.Rect.sizeDelta.y);
        undo = ForgePill.Create(bar, "Undo", OnUndo);
        undo.Rect.anchorMin = undo.Rect.anchorMax = undo.Rect.pivot = new Vector2(1f, 0.5f);
        undo.Rect.anchoredPosition = new Vector2(-160f, 0f);
        undo.Rect.sizeDelta = new Vector2(80f, undo.Rect.sizeDelta.y);
        approve = ForgePill.Create(bar, "Approve", OnApprove);
        approve.Rect.anchorMin = approve.Rect.anchorMax = approve.Rect.pivot = new Vector2(1f, 0.5f);
        approve.Rect.anchoredPosition = Vector2.zero;
        approve.Rect.sizeDelta = new Vector2(125f, approve.Rect.sizeDelta.y);
        merge = ForgePill.Create(bar, "Merge", OnMerge);
        merge.Rect.anchorMin = merge.Rect.anchorMax = merge.Rect.pivot = new Vector2(1f, 0.5f);
        merge.Rect.anchoredPosition = new Vector2(-133f, 0f);
        merge.Rect.sizeDelta = new Vector2(125f, merge.Rect.sizeDelta.y);
        status = Text("Status", bar, 13f, ForgeTheme.PanelSubtext);
        status.alignment = TextAlignmentOptions.MidlineLeft;
        status.textWrappingMode = TextWrappingModes.NoWrap;
        status.overflowMode = TextOverflowModes.Ellipsis;
        var srt = status.rectTransform;
        srt.anchorMin = Vector2.zero;
        srt.anchorMax = Vector2.one;
        srt.offsetMin = new Vector2(4f, 0f);
        srt.offsetMax = new Vector2(-260f, 0f);
    }

    ForgePill Tab(string label, float x, float width, UnityAction click)
    {
        var tab = ForgePill.Create(tabs, label, click, TabHeight - 4f, 11.5f);
        tab.Rect.anchorMin = tab.Rect.anchorMax = tab.Rect.pivot = new Vector2(0f, 0.5f);
        tab.Rect.anchoredPosition = new Vector2(x, 0f);
        tab.Rect.sizeDelta = new Vector2(width, TabHeight - 4f);
        return tab;
    }

    // The tab row only exists with a repo to show; without it the columns take the full height, as before.
    void ShowTabs(bool on)
    {
        tabs.gameObject.SetActive(on);
        float top = on ? -(TabHeight + Gap) : 0f;
        side.offsetMax = new Vector2(SideWidth, top);
        codeBox.offsetMax = new Vector2(0f, top);
    }

    ForgePill ListPill(string label, float y, float height, UnityAction click)
    {
        var pill = ForgePill.Create(listContent, label, click);
        pill.Rect.anchorMin = pill.Rect.anchorMax = pill.Rect.pivot = new Vector2(0f, 1f);
        pill.Rect.sizeDelta = new Vector2(SideWidth - 6f, height);
        pill.Rect.anchoredPosition = new Vector2(0f, -y);
        return pill;
    }

    void ClearList()
    {
        foreach (var (pill, _) in pills) Destroy(pill.gameObject);
        foreach (var pill in repoPills) Destroy(pill.gameObject);
        pills.Clear();
        repoPills.Clear();
    }

    void SetListHeight(float height, bool toTop)
    {
        listContent.sizeDelta = new Vector2(0f, height);
        if (toTop) listContent.anchoredPosition = Vector2.zero;
    }

    void SetCode(string rich)
    {
        code.text = rich;
        code.ForceMeshUpdate();
        Vector2 size = code.GetPreferredValues();
        code.rectTransform.sizeDelta = size;
        content.sizeDelta = new Vector2(Mathf.Max(size.x + 40f, viewport.rect.width), size.y + 8f);
    }

    // ---------------------------------------------------------------- refresh

    void Refresh()
    {
        bool sampleMoved = SampleMoved();
        bool repoMoved = GitHubView.Version != shownVersion;
        bool nowConnected = ForgeNet.Status == ForgeNetStatus.Live && GitHubView.CurrentSource != null;
        if (nowConnected != connected)
        {
            connected = nowConnected;
            ShowTabs(connected);
            view = connected ? RepoView : View.Sample;
        }
        else if (connected)
        {
            // The view follows what Albie (or a tab) last acted on.
            if (sampleMoved) view = View.Sample;
            else if (repoMoved) view = RepoView;
        }

        bool entered = view != shownView;
        if (view == View.Sample) RefreshSample(entered);
        else if (entered || repoMoved) RefreshRepo();
        shownView = view;
        shownVersion = GitHubView.Version;

        if (connected)
        {
            sampleTab.On = view == View.Sample;
            repoTab.On = view == View.Repo;
            prsTab.On = view == View.Prs;
            SetLabel(repoTab, Tail(GitHubView.CurrentSource.name, LabelChars));
        }
        bool sample = view == View.Sample;
        var open = CodeProject.Open;
        undo.Button.interactable = sample && open != null && open.undo.Count > 0;
        undo.Rect.gameObject.SetActive(undo.Button.interactable);
        reset.Rect.gameObject.SetActive(sample);
        bool pullRequest = view == View.Prs && GitHubView.OpenPr != null;
        approve.Rect.gameObject.SetActive(pullRequest);
        merge.Rect.gameObject.SetActive(pullRequest);
        // With no repo on screen, a source control problem (no token, no repos) still has to be readable somewhere.
        string line = sample ? CodeProject.Status : GitHubView.StatusText;
        if (sample && !connected && string.IsNullOrEmpty(line) && ForgeNet.Status == ForgeNetStatus.Live) line = GitHubView.StatusText;
        status.text = notice ?? line;
        RefreshControllerState();
    }

    static View RepoView => GitHubView.Mode == GitHubView.ViewMode.Prs ? View.Prs : View.Repo;

    bool SampleMoved()
    {
        var open = CodeProject.Open;
        string text = open?.text;
        int start = CodeProject.HighlightStart;
        bool moved = open != seenOpen || !ReferenceEquals(text, seenText) || start != seenStart;
        seenOpen = open;
        seenText = text;
        seenStart = start;
        return moved;
    }

    static void SetLabel(ForgePill pill, string text)
    {
        var label = pill.GetComponentInChildren<TMP_Text>();
        if (label.text != text) label.text = text;
    }

    void RefreshSample(bool entered)
    {
        if (entered)
        {
            ClearList();
            listTitle.text = CodeProject.Name.ToUpperInvariant();
            float y = 0f;
            foreach (var f in CodeProject.Files)
            {
                var file = f;
                pills.Add((ListPill(f.path, y, 34f, () => CodeProject.Show(file)), file));
                y += 40f;
            }
            SetListHeight(y, true);
            shownText = shownPath = null;
        }
        var open = CodeProject.Open;
        if (open == null) return;

        int problems = 0;
        foreach (var (pill, file) in pills)
        {
            pill.On = file == open;
            int n = CodeProject.Check(file).Count;
            if (file == open) problems = n;
            SetLabel(pill, file.path + (file.text != file.original ? " *" : "") + (n > 0 ? $"  <color=#FF7A7A>{n}</color>" : ""));
        }
        string subtitle = open.path + (problems == 0 ? "" : $" · {problems} error{(problems == 1 ? "" : "s")}");
        if (panel.Subtitle != subtitle) panel.Subtitle = subtitle;

        bool moved = open.path != shownPath || CodeProject.HighlightStart != shownStart;
        if (open.text != shownText || open.path != shownPath)
        {
            SetCode(Render(open.path, open.text, CodeProject.Check(open)));
            shownText = open.text;
        }
        shownPath = open.path;
        shownStart = CodeProject.HighlightStart;
        PlaceHighlight(moved);
    }

    // ---------------------------------------------------------------- repo and pull requests

    void RefreshRepo()
    {
        var source = GitHubView.CurrentSource;
        var pr = GitHubView.OpenPr;
        bool prs = view == View.Prs;
        string folder = prs ? null : GitHubView.CurrentPath;
        bool newList = view != shownView || folder != shownFolder || pr != shownPr;
        Disarm();  // what an armed Approve or Merge would act on may have just changed
        shownFolder = folder;
        shownPr = pr;

        ClearList();
        float y = 0f;
        string title, subtitle;
        if (!prs)
        {
            title = folder.Length == 0 ? source.name : folder;
            subtitle = GitHubView.FilePath ?? source.fullName;
            if (folder.Length > 0)
            {
                int slash = folder.LastIndexOf('/');
                string parent = slash < 0 ? "" : folder.Substring(0, slash);
                RepoPill("..", ref y, false, () => GitHubView.OpenPath(parent));
            }
            foreach (var item in GitHubView.Tree)
            {
                string path = item.path;
                if (item.IsDir) RepoPill(Tail(item.name, LabelChars - 1) + "/", ref y, false, () => GitHubView.OpenPath(path));
                else RepoPill(Tail(item.name, LabelChars), ref y, path == GitHubView.FilePath, () => GitHubView.OpenFile(path));
            }
        }
        else if (pr == null)
        {
            title = "PULL REQUESTS";
            subtitle = source.fullName + " · pull requests";
            foreach (var p in GitHubView.Prs)
            {
                int number = p.number;
                string label = $"#{number} {p.title}";
                RepoPill(label.Length > LabelChars ? label.Substring(0, LabelChars - 2) + ".." : label, ref y, false, () => GitHubView.FetchPr(number));
            }
        }
        else
        {
            title = $"PR #{pr.number}";
            subtitle = $"PR #{pr.number} · {pr.title}";
            RepoPill("..", ref y, false, GitHubView.BackToPrList);
            RepoPill("Summary", ref y, GitHubView.OpenPrFile == null, () => GitHubView.ShowPrFile(null));
            foreach (var f in pr.files)
            {
                var file = f;
                string mark = f.status == "added" ? "+ " : f.status == "removed" ? "- " : "~ ";
                RepoPill(mark + Tail(f.filename, LabelChars - 2), ref y, f == GitHubView.OpenPrFile, () => GitHubView.ShowPrFile(file));
            }
        }
        SetListHeight(y, newList);
        listTitle.text = Tail(title, LabelChars).ToUpperInvariant();
        if (panel.Subtitle != subtitle) panel.Subtitle = subtitle;

        string rich = !prs ? RenderRepoFile() : pr == null ? RenderPrList() : GitHubView.OpenPrFile == null ? RenderPrSummary(pr) : RenderDiff(GitHubView.OpenPrFile);
        if (rich != code.text)
        {
            SetCode(rich);
            content.anchoredPosition = Vector2.zero;
            scroll.velocity = Vector2.zero;
        }
        highlight.gameObject.SetActive(false);
    }

    void RepoPill(string label, ref float y, bool on, UnityAction click)
    {
        var pill = ListPill(label, y, 30f, click);
        pill.On = on;
        repoPills.Add(pill);
        y += 34f;
    }

    /// <summary>The end of a long name, since the end (the file name, the extension) is what tells entries apart.</summary>
    static string Tail(string text, int max) => text.Length <= max ? text : ".." + text.Substring(text.Length - (max - 2));

    static string Plain(string message)
    {
        var sb = new StringBuilder("<mspace=0.6em>");
        Colored(sb, LineNoColor, message);
        return sb.ToString();
    }

    static string RenderRepoFile()
    {
        if (GitHubView.FileContent == null) return Plain("Pick a file on the left.");
        if (GitHubView.FileContent.Length == 0) return Plain(GitHubView.FileNote.Length > 0 ? GitHubView.FileNote : "This file is empty.");
        return Render(GitHubView.FilePath, GitHubView.FileContent.Replace("\r\n", "\n"), null, MaxLines);
    }

    static string RenderPrList()
    {
        if (GitHubView.Prs.Count == 0) return Plain(GitHubView.Loading ? "Loading pull requests..." : "No open pull requests.");
        var sb = new StringBuilder("<mspace=0.6em>");
        foreach (var p in GitHubView.Prs)
        {
            Colored(sb, HeadingColor, $"#{p.number}  {p.title}\n");
            Colored(sb, LineNoColor, $"    {p.author}   {p.head} -> {p.baseBranch}\n\n");
        }
        return sb.ToString();
    }

    static string RenderPrSummary(GitHubView.PrDetail pr)
    {
        var sb = new StringBuilder("<mspace=0.6em>");
        Colored(sb, HeadingColor, $"#{pr.number}  {pr.title}\n");
        Colored(sb, LineNoColor, $"{pr.author}   {pr.head} -> {pr.baseBranch}\n\n");
        Colored(sb, AddColor, $"+{pr.additions}");
        sb.Append("  ");
        Colored(sb, RemoveColor, $"-{pr.deletions}");
        sb.Append($"  in {pr.changedFiles} file{(pr.changedFiles == 1 ? "" : "s")}\n");
        if (pr.files.Count < pr.changedFiles) Colored(sb, LineNoColor, $"Only the first {pr.files.Count} files are listed.\n");
        sb.Append('\n');
        if (pr.body.Length > 0)
        {
            Escaped(sb, pr.body.Replace("\r\n", "\n"));
            sb.Append("\n\n");
        }
        foreach (var f in pr.files)
        {
            Colored(sb, AddColor, ("+" + f.additions).PadLeft(5));
            Colored(sb, RemoveColor, ("-" + f.deletions).PadLeft(5));
            sb.Append("  ");
            Escaped(sb, f.filename);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static string RenderDiff(GitHubView.PrFile file)
    {
        var sb = new StringBuilder("<mspace=0.6em>");
        Colored(sb, HeadingColor, $"{file.filename}   {file.status}   +{file.additions} -{file.deletions}\n\n");
        if (file.patch.Length == 0)
        {
            Colored(sb, LineNoColor, "No text diff for this file (binary, renamed without changes, or too large).");
            return sb.ToString();
        }
        foreach (string line in file.patch.Split('\n'))
        {
            string color = line.StartsWith("@@") ? NameColor : line.StartsWith("+") ? AddColor : line.StartsWith("-") ? RemoveColor : null;
            if (color == null) Escaped(sb, line);
            else Colored(sb, color, line);
            sb.Append('\n');
        }
        if (file.truncated) Colored(sb, LineNoColor, "\nThe rest of this diff is too long to show.");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- controller

    static ControllerHost Truck() => ThingHosts.Find(TemplateTools.DefaultThing);

    void RefreshControllerState(bool force = false)
    {
        if (controllerState == null) return;
        var file = Truck()?.File;
        float speed = file != null && file.Tunables.TryGetValue("max_speed", out var tune) ? tune.Value : float.NaN;
        bool same = file == shownFile && (speed == shownSpeed || (float.IsNaN(speed) && float.IsNaN(shownSpeed)));
        if (same && !force) return;
        shownFile = file;
        shownSpeed = speed;
        if (file == null)
        {
            controllerState.text = "truck\nno truck running";
            controllerState.color = ForgeTheme.PanelSubtext;
            return;
        }
        if (float.IsNaN(speed))
            controllerState.text = $"{file.Name}\nmax_speed unavailable";
        else
        {
            controllerState.text = $"{file.Name}\nmax_speed {speed:0.0} m/s";
            if (!controllerSpeed.isFocused)
                controllerSpeed.text = speed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }
        controllerState.color = ForgeTheme.Code;
    }

    void ApplyControllerSpeed()
    {
        if (controllerSpeed == null || !float.TryParse(controllerSpeed.text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value))
        {
            Say("max_speed must be a number.");
            return;
        }
        var host = Truck();
        string error = host == null ? "No truck is running." : host.SetTunable("max_speed", value);
        Say(error ?? $"{host.File.Name} max_speed hot reloaded to {value:0.0} m/s.");
        RefreshControllerState(true);
    }

    void Say(string text)
    {
        CodeProject.SetStatus(text);
        Notice(text);
    }

    // ---------------------------------------------------------------- sample highlight, undo, reset

    void PlaceHighlight(bool scrollTo)
    {
        int start = CodeProject.HighlightStart, end = CodeProject.HighlightEnd;
        var info = code.textInfo;
        bool show = start > 0 && info != null && info.lineCount > 0;
        highlight.gameObject.SetActive(show);
        float targetY = 0f;
        if (show)
        {
            var first = info.lineInfo[Mathf.Clamp(start - 1, 0, info.lineCount - 1)];
            var last = info.lineInfo[Mathf.Clamp(end - 1, 0, info.lineCount - 1)];
            float top = first.ascender + 2f, bottom = last.descender - 2f;
            var rt = highlight.rectTransform;
            rt.anchoredPosition = new Vector2(0f, top);
            rt.sizeDelta = new Vector2(Mathf.Max(content.sizeDelta.x, viewport.rect.width), top - bottom);
            bool error = CodeProject.Note.Length == 0 && IsProblemLine(start);
            highlight.color = error ? ErrorTint : ForgeTheme.Tint(ForgeTheme.Code, 0.2f);
            note.text = CodeProject.Note;
            note.color = Color.Lerp(error ? Color.red : ForgeTheme.Code, Color.white, 0.45f);
            note.rectTransform.offsetMax = new Vector2(-(rt.sizeDelta.x - viewport.rect.width) - 8f, 0f);
            targetY = -top - viewport.rect.height * 0.3f;
        }
        if (!scrollTo) return;
        float maxY = Mathf.Max(0f, content.sizeDelta.y - viewport.rect.height);
        content.anchoredPosition = new Vector2(0f, Mathf.Clamp(targetY, 0f, maxY));
        scroll.velocity = Vector2.zero;
    }

    bool IsProblemLine(int line)
    {
        foreach (var p in CodeProject.Check(CodeProject.Open)) if (p.line == line) return true;
        return false;
    }

    void OnUndo()
    {
        if (CodeProject.Open != null && CodeProject.Undo(CodeProject.Open)) CodeProject.SetStatus("Undone.");
    }

    void OnReset()
    {
        if (Confirmed(reset, "Reset", "Tap again to reset")) CodeProject.Reset();
    }

    // Writes to the repo: the second tap is the user's confirmation.
    void OnApprove()
    {
        if (Confirmed(approve, "Approve", "Tap again: approve")) GitHubView.ApprovePr(error => Notice(error ?? "Approved."));
    }

    void OnMerge()
    {
        if (Confirmed(merge, "Merge", "Tap again: merge")) GitHubView.MergePr(error => Notice(error ?? "Merged."));
    }

    /// <summary>True on the second tap within three seconds; the first tap arms the pill and relabels it.</summary>
    bool Confirmed(ForgePill pill, string label, string ask)
    {
        if (armed == pill)
        {
            Disarm();
            return true;
        }
        Disarm();
        armed = pill;
        armedLabel = label;
        armedUntil = Time.unscaledTime + 3f;
        pill.On = true;
        SetLabel(pill, ask);
        return false;
    }

    void Disarm()
    {
        if (armed == null) return;
        armed.On = false;
        SetLabel(armed, armedLabel);
        armed = null;
    }

    void Notice(string text)
    {
        notice = text;
        noticeUntil = Time.unscaledTime + 4f;
        dirty = true;
    }

    // ---------------------------------------------------------------- Python syntax

    static readonly HashSet<string> Keywords = new HashSet<string>
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif",
        "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or",
        "pass", "raise", "return", "try", "while", "with", "yield",
    };
    const string KeywordColor = "#C792EA", StringColor = "#C3E88D", CommentColor = "#6F7A88", NumberColor = "#F78C6C",
        NameColor = "#82AAFF", LineNoColor = "#5C6370", ErrorNoColor = "#FF7A7A", HeadingColor = "#FFCB6B",
        AddColor = "#22CF86", RemoveColor = "#FF7A7A";

    static string Render(string path, string text, List<CodeProject.Problem> problems, int maxLines = int.MaxValue)
    {
        var bad = new HashSet<int>();
        if (problems != null) foreach (var p in problems) bad.Add(p.line);
        bool python = path.EndsWith(".py");
        string hashColor = path.EndsWith(".md") ? HeadingColor : CommentColor;
        string[] lines = text.Split('\n');
        int shown = Mathf.Min(lines.Length, maxLines);
        int width = shown.ToString().Length;
        var sb = new StringBuilder(text.Length * 2);
        sb.Append("<mspace=0.6em>");
        string triple = null;
        for (int i = 0; i < shown; i++)
        {
            sb.Append("<color=").Append(bad.Contains(i + 1) ? ErrorNoColor : LineNoColor).Append('>')
              .Append((i + 1).ToString().PadLeft(width)).Append("</color>  ");
            if (python) triple = Python(sb, lines[i], triple);
            else if (lines[i].StartsWith("#")) Colored(sb, hashColor, lines[i]);
            else Escaped(sb, lines[i]);
            if (i < shown - 1) sb.Append('\n');
        }
        if (shown < lines.Length) Colored(sb, LineNoColor, $"\n\n{lines.Length - shown} more lines not shown.");
        return sb.ToString();
    }

    static string Python(StringBuilder sb, string line, string triple)
    {
        int i = 0;
        bool afterDef = false;
        while (i < line.Length)
        {
            char c = line[i];
            if (triple != null)
            {
                int close = line.IndexOf(triple, i, System.StringComparison.Ordinal);
                int stop = close < 0 ? line.Length : close + 3;
                Colored(sb, StringColor, line.Substring(i, stop - i));
                i = stop;
                if (close >= 0) triple = null;
                continue;
            }
            if (c == '#') { Colored(sb, CommentColor, line.Substring(i)); break; }
            if (c == '"' || c == '\'')
            {
                if (i + 2 < line.Length && line[i + 1] == c && line[i + 2] == c)
                {
                    triple = new string(c, 3);
                    Colored(sb, StringColor, triple);
                    i += 3;
                    continue;
                }
                int j = i + 1;
                while (j < line.Length && line[j] != c) j += line[j] == '\\' ? 2 : 1;
                j = Mathf.Min(j + 1, line.Length);
                Colored(sb, StringColor, line.Substring(i, j - i));
                i = j;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                string word = line.Substring(i, j - i);
                if (j < line.Length && (line[j] == '"' || line[j] == '\'') && word.Length <= 2 && "fFrRbBuU".IndexOf(word[0]) >= 0)
                {
                    Colored(sb, StringColor, word);
                    i = j;
                    continue;
                }
                if (Keywords.Contains(word)) Colored(sb, KeywordColor, word);
                else if (afterDef) Colored(sb, NameColor, word);
                else Escaped(sb, word);
                afterDef = word == "def" || word == "class";
                i = j;
                continue;
            }
            if (char.IsDigit(c))
            {
                int j = i;
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '.')) j++;
                Colored(sb, NumberColor, line.Substring(i, j - i));
                i = j;
                continue;
            }
            if (c != ' ') afterDef = false;
            Escaped(sb, c.ToString());
            i++;
        }
        return triple;
    }

    static void Colored(StringBuilder sb, string color, string text)
    {
        sb.Append("<color=").Append(color).Append('>');
        Escaped(sb, text);
        sb.Append("</color>");
    }

    static void Escaped(StringBuilder sb, string text)
    {
        if (text.IndexOf('<') < 0) sb.Append(text);
        else sb.Append(text.Replace("<", "<noparse><</noparse>"));
    }

    // ---------------------------------------------------------------- helpers

    static RectTransform Rect(string name, RectTransform parent, Vector2 anchorMin, Vector2 anchorMax)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return rt;
    }

    static TMP_Text Text(string name, RectTransform parent, float size, Color color)
    {
        var t = new GameObject(name, typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
        t.rectTransform.SetParent(parent, false);
        if (ForgeTheme.Font != null) t.font = ForgeTheme.Font;
        t.fontSize = size;
        t.color = color;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.overflowMode = TextOverflowModes.Overflow;
        t.raycastTarget = false;
        return t;
    }

    static void Place(RectTransform rt, float top, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.offsetMin = new Vector2(0f, -(top + height));
        rt.offsetMax = new Vector2(0f, -top);
    }
}
