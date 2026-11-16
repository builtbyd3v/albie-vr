using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The IDE panel for the GitHub code browser and PR diff viewer (PROJECT_CONTEXT section 7.14).
/// Installed by <see cref="GitHubMode"/> instead of the old <see cref="CodePanel"/> when the hub is live.
///
/// Layout (all in code, no scene editing needed):
///   header row  — repo name + branch; "PR #N" label when a PR is open
///   tab bar     — FILES | PRs
///   left column — file tree (FILES tab) or changed-files list (PRs tab)
///   right pane  — file content with syntax colouring (FILES) or unified diff (PRs)
///   status bar  — status text, Undo / Back pills
///
/// Forge > Code > Build GitHub Panel creates/updates the prefab.
/// </summary>
[RequireComponent(typeof(ForgePanel))]
public class GitHubPanel : MonoBehaviour
{
    // ---------------------------------------------------------------- sizing (mm in world-space canvas)
    const float SideWidth = 170f, TabH = 32f, HeaderH = 28f, BarH = 36f, Gap = 8f, FontSize = 12f, Pad = 8f;
    static readonly Color CodeBg = new Color(0f, 0f, 0f, 0.28f);
    static readonly Color AddedBg = new Color(0.13f, 0.81f, 0.52f, 0.12f);
    static readonly Color RemovedBg = new Color(1f, 0.48f, 0.48f, 0.12f);

    // ---------------------------------------------------------------- refs
    ForgePanel panel;
    TMP_Text repoLabel, branchLabel, statusText;
    ForgePill filesTab, prsTab;
    RectTransform leftCol, viewport, content, bar;
    TMP_Text mainText;
    ScrollRect scroll;
    readonly List<(ForgePill pill, object data)> leftPills = new List<(ForgePill, object)>();

    bool dirty = true;
    string shownRepo, shownFile, shownPrFile;
    GitHubView.ViewMode shownMode;

    // ---------------------------------------------------------------- lifecycle

    void Awake()
    {
        panel = GetComponent<ForgePanel>();
        if (panel.Content == null) return;
        var ph = panel.Content.Find("Placeholder");
        if (ph != null) ph.gameObject.SetActive(false);
        Build();
    }

    void OnEnable()
    {
        GitHubView.Changed += MarkDirty;
        ForgeNet.StatusChanged += OnNetStatus;
        dirty = true;
        if (ForgeNet.Status == ForgeNetStatus.Live && GitHubView.CurrentSource == null)
            GitHubView.Refresh();
    }

    void OnDisable()
    {
        GitHubView.Changed -= MarkDirty;
        ForgeNet.StatusChanged -= OnNetStatus;
    }

    void OnNetStatus(ForgeNetStatus s)
    {
        if (s == ForgeNetStatus.Live) GitHubView.Refresh();
        MarkDirty();
    }

    void MarkDirty() => dirty = true;

    void LateUpdate()
    {
        if (!dirty) return;
        dirty = false;
        Refresh();
    }

    // ---------------------------------------------------------------- layout

    void Build()
    {
        RectTransform root = panel.Content;

        // header row
        var header = Rect("Header", root, new Vector2(0f, 1f), new Vector2(1f, 1f));
        header.pivot = new Vector2(0f, 1f);
        header.offsetMin = new Vector2(0f, -HeaderH);
        header.offsetMax = Vector2.zero;
        repoLabel = Text("Repo", header, 13f, ForgeTheme.PanelText);
        repoLabel.fontStyle = FontStyles.Bold;
        repoLabel.alignment = TextAlignmentOptions.MidlineLeft;
        repoLabel.rectTransform.anchorMin = Vector2.zero;
        repoLabel.rectTransform.anchorMax = new Vector2(0.6f, 1f);
        repoLabel.rectTransform.offsetMin = new Vector2(4f, 0f);
        repoLabel.rectTransform.offsetMax = Vector2.zero;
        branchLabel = Text("Branch", header, 11f, ForgeTheme.PanelSubtext);
        branchLabel.alignment = TextAlignmentOptions.MidlineRight;
        branchLabel.rectTransform.anchorMin = new Vector2(0.6f, 0f);
        branchLabel.rectTransform.anchorMax = Vector2.one;
        branchLabel.rectTransform.offsetMin = Vector2.zero;
        branchLabel.rectTransform.offsetMax = new Vector2(-4f, 0f);

        // tab bar
        var tabs = Rect("Tabs", root, new Vector2(0f, 1f), new Vector2(1f, 1f));
        tabs.pivot = new Vector2(0f, 1f);
        tabs.offsetMin = new Vector2(0f, -(HeaderH + Gap + TabH));
        tabs.offsetMax = new Vector2(0f, -HeaderH - Gap);
        filesTab = ForgePill.Create(tabs, "FILES", () => { GitHubView.ClosePr(); });
        Place(filesTab.Rect, 0f, 0f, 90f, TabH);
        prsTab = ForgePill.Create(tabs, "PRs", () => { GitHubView.FetchPrs(); });
        Place(prsTab.Rect, 96f, 0f, 90f, TabH);

        // left column
        float topOffset = HeaderH + Gap + TabH + Gap;
        leftCol = Rect("Left", root, new Vector2(0f, 0f), new Vector2(0f, 1f));
        leftCol.pivot = new Vector2(0f, 1f);
        leftCol.offsetMin = new Vector2(0f, BarH + Gap);
        leftCol.offsetMax = new Vector2(SideWidth, -topOffset);

        // right pane
        var codeBox = Rect("Code", root, Vector2.zero, Vector2.one);
        codeBox.offsetMin = new Vector2(SideWidth + Gap, BarH + Gap);
        codeBox.offsetMax = new Vector2(0f, -topOffset);
        var bg = codeBox.gameObject.AddComponent<ForgeSurface>();
        bg.Style = ForgeSurface.Kind.Fill;
        bg.Corner = 12f;
        bg.color = CodeBg;

        viewport = Rect("Viewport", codeBox, Vector2.zero, Vector2.one);
        viewport.offsetMin = new Vector2(Pad, Pad);
        viewport.offsetMax = new Vector2(-Pad, -Pad);
        viewport.gameObject.AddComponent<RectMask2D>();
        var hit = viewport.gameObject.AddComponent<Image>();
        hit.color = Color.clear;

        content = Rect("Content", viewport, new Vector2(0f, 1f), new Vector2(0f, 1f));
        content.pivot = new Vector2(0f, 1f);

        mainText = Text("Text", content, FontSize, ForgeTheme.PanelText);
        mainText.alignment = TextAlignmentOptions.TopLeft;
        mainText.richText = true;
        mainText.rectTransform.anchorMin = mainText.rectTransform.anchorMax = mainText.rectTransform.pivot = new Vector2(0f, 1f);
        mainText.rectTransform.anchoredPosition = Vector2.zero;

        scroll = codeBox.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 20f;

        // bottom bar
        bar = Rect("Bar", root, Vector2.zero, new Vector2(1f, 0f));
        bar.pivot = new Vector2(0.5f, 0f);
        bar.offsetMin = Vector2.zero;
        bar.offsetMax = new Vector2(0f, BarH);
        statusText = Text("Status", bar, 11.5f, ForgeTheme.PanelSubtext);
        statusText.alignment = TextAlignmentOptions.MidlineLeft;
        statusText.textWrappingMode = TextWrappingModes.NoWrap;
        statusText.overflowMode = TextOverflowModes.Ellipsis;
        statusText.rectTransform.anchorMin = Vector2.zero;
        statusText.rectTransform.anchorMax = Vector2.one;
        statusText.rectTransform.offsetMin = new Vector2(4f, 0f);
        statusText.rectTransform.offsetMax = new Vector2(-8f, 0f);
    }

    // ---------------------------------------------------------------- refresh

    void Refresh()
    {
        if (mainText == null) return;

        // Header
        string repoName = GitHubView.CurrentSource?.name ?? "source control";
        string branch = GitHubView.Mode == GitHubView.ViewMode.PrDiff && GitHubView.OpenPr != null
            ? $"PR #{GitHubView.OpenPr.number}: {GitHubView.OpenPr.head} → {GitHubView.OpenPr.baseBranch}"
            : GitHubView.CurrentSource?.defaultBranch ?? "";
        if (repoLabel.text != repoName) repoLabel.text = repoName;
        if (branchLabel.text != branch) branchLabel.text = branch;

        // Panel subtitle
        string subtitle = GitHubView.Mode == GitHubView.ViewMode.PrDiff && GitHubView.OpenPr != null
            ? GitHubView.OpenPr.title
            : GitHubView.FilePath ?? (GitHubView.CurrentPath.Length > 0 ? GitHubView.CurrentPath : "");
        if (panel.Subtitle != subtitle) panel.Subtitle = subtitle;

        // Tabs
        bool isPr = GitHubView.Mode == GitHubView.ViewMode.PrDiff;
        filesTab.On = !isPr;
        prsTab.On = isPr;
        string prTabLabel = GitHubView.OpenPr != null ? $"PR #{GitHubView.OpenPr.number}" : "PRs";
        var prTabText = prsTab.GetComponentInChildren<TMP_Text>();
        if (prTabText != null && prTabText.text != prTabLabel) prTabText.text = prTabLabel;

        // Status bar
        statusText.text = GitHubView.StatusText;

        // Rebuild left column if the source or mode changed
        bool modeChanged = GitHubView.Mode != shownMode || GitHubView.CurrentSource?.id != shownRepo;
        bool fileChanged = GitHubView.FilePath != shownFile;
        bool prFileChanged = GitHubView.OpenPrFile?.filename != shownPrFile;

        if (modeChanged) RebuildLeft();
        shownMode = GitHubView.Mode;
        shownRepo = GitHubView.CurrentSource?.id;

        // Rebuild right pane if content changed
        if (modeChanged || fileChanged || prFileChanged) RebuildRight();
        shownFile = GitHubView.FilePath;
        shownPrFile = GitHubView.OpenPrFile?.filename;
    }

    void RebuildLeft()
    {
        foreach (var (pill, _) in leftPills) if (pill != null) Destroy(pill.gameObject);
        leftPills.Clear();

        if (GitHubView.Mode == GitHubView.ViewMode.Files)
            RebuildFileTree();
        else
            RebuildPrFileList();
    }

    void RebuildFileTree()
    {
        float y = 0f;
        // "Up" pill when not at root
        if (GitHubView.CurrentPath.Length > 0)
        {
            int slash = GitHubView.CurrentPath.LastIndexOf('/');
            string parent = slash > 0 ? GitHubView.CurrentPath.Substring(0, slash) : "";
            var back = ForgePill.Create(leftCol, "← ..", () => GitHubView.OpenPath(parent));
            PlaceLeft(back.Rect, y);
            leftPills.Add((back, null));
            y += 36f;
        }
        foreach (var item in GitHubView.Tree)
        {
            var it = item;
            string icon = it.type == "dir" ? "▸ " : "  ";
            string label = icon + it.name;
            Action action = it.type == "dir"
                ? (Action)(() => GitHubView.OpenPath(it.path))
                : () => GitHubView.OpenFile(it.path);
            var pill = ForgePill.Create(leftCol, label, action);
            PlaceLeft(pill.Rect, y);
            pill.On = it.path == GitHubView.FilePath;
            leftPills.Add((pill, it));
            y += 36f;
        }
    }

    void RebuildPrFileList()
    {
        float y = 0f;
        if (GitHubView.OpenPr == null)
        {
            // Show PR list
            foreach (var pr in GitHubView.Prs)
            {
                var p = pr;
                string label = $"#{p.number}  {Trunc(p.title, 22)}";
                var pill = ForgePill.Create(leftCol, label, () => GitHubView.FetchPr(p.number));
                PlaceLeft(pill.Rect, y);
                leftPills.Add((pill, p));
                y += 36f;
            }
        }
        else
        {
            // Show changed files in this PR
            var back = ForgePill.Create(leftCol, "← PRs", GitHubView.FetchPrs);
            PlaceLeft(back.Rect, y);
            leftPills.Add((back, null));
            y += 36f;
            foreach (var f in GitHubView.OpenPr.files)
            {
                var file = f;
                string statusIcon = f.status switch
                {
                    "added" => "+ ",
                    "removed" => "- ",
                    "renamed" => "→ ",
                    _ => "~ ",
                };
                string label = statusIcon + Trunc(System.IO.Path.GetFileName(f.filename), 20);
                var pill = ForgePill.Create(leftCol, label, () => GitHubView.ShowPrFile(file));
                PlaceLeft(pill.Rect, y);
                pill.On = f.filename == GitHubView.OpenPrFile?.filename;
                leftPills.Add((pill, f));
                y += 36f;
            }
        }
    }

    void RebuildRight()
    {
        string text;
        if (GitHubView.Loading)
        {
            text = $"<color=#5C6370>{GitHubView.StatusText}</color>";
        }
        else if (GitHubView.Mode == GitHubView.ViewMode.PrDiff)
        {
            text = GitHubView.OpenPrFile != null
                ? RenderDiff(GitHubView.OpenPrFile)
                : RenderPrSummary();
        }
        else if (GitHubView.FileContent != null)
        {
            text = RenderFile(GitHubView.FilePath ?? "", GitHubView.FileContent);
        }
        else
        {
            text = $"<color=#5C6370>Select a file to view it.</color>";
        }

        mainText.text = text;
        mainText.ForceMeshUpdate();
        Vector2 size = mainText.GetPreferredValues();
        mainText.rectTransform.sizeDelta = size;
        content.sizeDelta = new Vector2(Mathf.Max(size.x + 40f, viewport.rect.width), size.y + 8f);
        content.anchoredPosition = Vector2.zero;
        scroll.velocity = Vector2.zero;
    }

    // ---------------------------------------------------------------- rendering

    string RenderPrSummary()
    {
        if (GitHubView.OpenPr == null) return "";
        var pr = GitHubView.OpenPr;
        var sb = new StringBuilder();
        sb.Append("<mspace=0.6em>");
        Colored(sb, "#FFCB6B", $"PR #{pr.number}: {pr.title}\n");
        Colored(sb, "#5C6370", $"by {pr.author}  ·  {pr.head} → {pr.baseBranch}\n");
        sb.Append($"+{pr.additions}  ");
        Colored(sb, "#FF7A7A", $"-{pr.deletions}");
        sb.Append($"  {pr.changedFiles} file{(pr.changedFiles == 1 ? "" : "s")}\n\n");
        if (pr.body.Length > 0)
        {
            Colored(sb, "#A8B5C1", pr.body + "\n\n");
        }
        Colored(sb, "#5C6370", "Select a changed file on the left to see the diff.");
        return sb.ToString();
    }

    static string RenderDiff(GitHubView.PrFile f)
    {
        if (string.IsNullOrEmpty(f.patch))
        {
            string msg = f.status switch
            {
                "added" => $"New file: {f.filename}  (+{f.additions} lines)",
                "removed" => $"Deleted: {f.filename}  (-{f.deletions} lines)",
                "renamed" => $"Renamed: {f.filename}",
                _ => $"{f.filename}  (no diff available)",
            };
            return $"<mspace=0.6em><color=#5C6370>{Escaped(msg)}</color>";
        }

        var sb = new StringBuilder();
        sb.Append("<mspace=0.6em>");
        // File header
        string header = $"--- {f.filename}  +{f.additions} -{f.deletions}";
        Colored(sb, "#FFCB6B", header + "\n");

        string[] lines = f.patch.Split('\n');
        foreach (string raw in lines)
        {
            if (raw.StartsWith("@@"))
                Colored(sb, "#82AAFF", Escaped(raw) + "\n");
            else if (raw.StartsWith("+") && !raw.StartsWith("+++"))
                Colored(sb, "#22CF86", Escaped(raw) + "\n");
            else if (raw.StartsWith("-") && !raw.StartsWith("---"))
                Colored(sb, "#FF7A7A", Escaped(raw) + "\n");
            else
                Colored(sb, "#7A8390", Escaped(raw) + "\n");
        }
        return sb.ToString();
    }

    static string RenderFile(string path, string content)
    {
        bool isPy = path.EndsWith(".py");
        bool isCs = path.EndsWith(".cs");
        bool isJson = path.EndsWith(".json");
        string[] lines = content.Replace("\r\n", "\n").Split('\n');
        int width = lines.Length.ToString().Length;
        var sb = new StringBuilder(content.Length * 2);
        sb.Append("<mspace=0.6em>");
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append("<color=#5C6370>").Append((i + 1).ToString().PadLeft(width)).Append("</color>  ");
            if (isPy || isCs) SimpleSyntax(sb, lines[i], isPy);
            else sb.Append(Escaped(lines[i]));
            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    static void SimpleSyntax(StringBuilder sb, string line, bool python)
    {
        // Very lightweight: color whole line if it starts with a comment; otherwise escape.
        string trim = line.TrimStart();
        if ((python && trim.StartsWith("#")) || (!python && trim.StartsWith("//")))
            Colored(sb, "#6F7A88", Escaped(line));
        else
            sb.Append(Escaped(line));
    }

    static void Colored(StringBuilder sb, string color, string text)
    {
        sb.Append("<color=").Append(color).Append('>').Append(text).Append("</color>");
    }

    static string Escaped(string s)
    {
        return s.IndexOf('<') < 0 ? s : s.Replace("<", "<noparse><</noparse>");
    }

    static string Trunc(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    // ---------------------------------------------------------------- layout helpers

    static RectTransform Rect(string name, RectTransform parent, Vector2 ancMin, Vector2 ancMax)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        rt.anchorMin = ancMin;
        rt.anchorMax = ancMax;
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

    static void Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    static void PlaceLeft(RectTransform rt, float y)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(0f, 34f);
        rt.offsetMin = new Vector2(0f, -(y + 34f));
        rt.offsetMax = new Vector2(-4f, -y);
    }
}
