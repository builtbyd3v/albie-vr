using System;
using System.Collections.Generic;
using System.Text;
using Forge.Controllers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The code view on the headset. When hub is live it shows the connected GitHub repo (file tree, file viewer,
/// PR diff); when hub is off it falls back to the bundled sample project. Either way the THING CONTROLLER section
/// stays at the bottom of the left column so Satya's ML truck controller always works.
///
/// Albie drives it through <see cref="CodeTools"/> (local files) and <see cref="GitHubTools"/> (hub files/PRs).
/// Lives on Code/Resources/CodePanel.prefab (a ForgePanel; Forge > Code > Build Code Panel Prefab).
/// UI is built in code — no scene editing needed.
/// </summary>
[RequireComponent(typeof(ForgePanel))]
public class CodePanel : MonoBehaviour
{
    const float SideWidth = 150f, BarHeight = 40f, TabH = 34f, Gap = 10f, FontSize = 12.5f, Pad = 8f;
    static readonly Color CodeBg = new Color(0f, 0f, 0f, 0.28f);
    static readonly Color ErrorTint = new Color(1f, 0.35f, 0.35f, 0.22f);

    ForgePanel panel;
    RectTransform side, viewport, content, bar;
    TMP_Text code, note, status, projectLabel;
    Image highlight;
    ScrollRect scroll;
    ForgePill undo, reset;
    TMP_InputField controllerSpeed;
    TMP_Text controllerState;
    ForgePill controllerApply;
    float controllerPoll;
    Forge.Controllers.ControllerFile shownFile;
    float shownSpeed = float.NaN;
    // Local (CodeProject) file pills
    readonly List<(ForgePill pill, CodeProject.File file)> pills = new List<(ForgePill, CodeProject.File)>();
    bool dirty = true, resetArmed;
    float resetArmedUntil;
    string shownText, shownPath;
    int shownStart = -1;

    // GitHub/PR tab row (shown only when hub is live)
    RectTransform tabRow;
    ForgePill filesTab, prTab;
    // GitHub file tree / PR file pills (rebuilt on path or mode change)
    readonly List<(ForgePill pill, object data)> ghPills = new List<(ForgePill, object)>();
    string shownGhPath, shownGhFile, shownGhPrFile;
    GitHubView.ViewMode shownGhMode = GitHubView.ViewMode.Files;
    bool ghDirty;

    // ---------------------------------------------------------------- lifecycle

    void Awake()
    {
        panel = GetComponent<ForgePanel>();
        if (panel.Content == null) return;
        var placeholder = panel.Content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);
        Build();
    }

    void OnEnable()
    {
        CodeProject.Changed += MarkDirty;
        GitHubView.Changed += MarkGhDirty;
        ForgeNet.StatusChanged += OnNetStatus;
        dirty = ghDirty = true;
        bool live = ForgeNet.Status == ForgeNetStatus.Live;
        if (live && GitHubView.CurrentSource == null) GitHubView.Refresh();
        if (tabRow != null) tabRow.gameObject.SetActive(live);
    }

    void OnDisable()
    {
        CodeProject.Changed -= MarkDirty;
        GitHubView.Changed -= MarkGhDirty;
        ForgeNet.StatusChanged -= OnNetStatus;
    }

    void MarkDirty() => dirty = true;
    void MarkGhDirty() => ghDirty = true;

    void OnNetStatus(ForgeNetStatus s)
    {
        bool live = s == ForgeNetStatus.Live;
        if (live && GitHubView.CurrentSource == null) GitHubView.Refresh();
        if (tabRow != null) tabRow.gameObject.SetActive(live);
        if (live) ghDirty = true; else dirty = true;
    }

    void LateUpdate()
    {
        if (resetArmed && Time.unscaledTime > resetArmedUntil) ArmReset(false);
        if (controllerState != null && Time.unscaledTime >= controllerPoll)
        {
            controllerPoll = Time.unscaledTime + 0.5f;
            RefreshControllerState();
        }
        if (code == null) return;
        bool liveHub = ForgeNet.Status == ForgeNetStatus.Live && GitHubView.CurrentSource != null;
        if (liveHub && (dirty || ghDirty))
        {
            dirty = ghDirty = false;
            RefreshGitHub();
        }
        else if (!liveHub && dirty)
        {
            dirty = false;
            Refresh();
        }
        else
        {
            ghDirty = false;
        }
    }

    // ---------------------------------------------------------------- layout

    void Build()
    {
        RectTransform root = panel.Content;

        // Tab row at the top (FILES | PR — shown when hub live)
        tabRow = Rect("Tabs", root, new Vector2(0f, 1f), new Vector2(1f, 1f));
        tabRow.pivot = new Vector2(0f, 1f);
        tabRow.offsetMin = new Vector2(0f, -TabH);
        tabRow.offsetMax = Vector2.zero;
        tabRow.gameObject.SetActive(false);
        filesTab = ForgePill.Create(tabRow, "FILES", () => GitHubView.ClosePr());
        filesTab.Rect.anchorMin = filesTab.Rect.anchorMax = filesTab.Rect.pivot = new Vector2(0f, 0.5f);
        filesTab.Rect.anchoredPosition = new Vector2(0f, 0f);
        filesTab.Rect.sizeDelta = new Vector2(80f, TabH - 6f);
        prTab = ForgePill.Create(tabRow, "PRs", () => GitHubView.FetchPrs());
        prTab.Rect.anchorMin = prTab.Rect.anchorMax = prTab.Rect.pivot = new Vector2(0f, 0.5f);
        prTab.Rect.anchoredPosition = new Vector2(88f, 0f);
        prTab.Rect.sizeDelta = new Vector2(80f, TabH - 6f);

        // Left column (tab row takes the top strip)
        side = Rect("Files", root, new Vector2(0f, 0f), new Vector2(0f, 1f));
        side.pivot = new Vector2(0f, 1f);
        side.offsetMin = new Vector2(0f, BarHeight + Gap);
        side.offsetMax = new Vector2(SideWidth, -(TabH + Gap));
        projectLabel = Text("Project", side, 11f, ForgeTheme.PanelSubtext);
        projectLabel.characterSpacing = 2f;
        Place(projectLabel.rectTransform, 0f, 18f);

        // Right code pane
        var codeBox = Rect("Code", root, Vector2.zero, Vector2.one);
        codeBox.offsetMin = new Vector2(SideWidth + Gap, BarHeight + Gap);
        codeBox.offsetMax = new Vector2(0f, -(TabH + Gap));
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

    void BuildFileList()
    {
        projectLabel.text = CodeProject.Name.ToUpperInvariant();
        float y = 26f;
        foreach (var f in CodeProject.Files)
        {
            var file = f;
            var pill = ForgePill.Create(side, f.path, () => CodeProject.Show(file));
            pill.Rect.anchorMin = pill.Rect.anchorMax = pill.Rect.pivot = new Vector2(0f, 1f);
            pill.Rect.sizeDelta = new Vector2(SideWidth - 6f, 34f);
            pill.Rect.anchoredPosition = new Vector2(0f, -y);
            y += 40f;
            pills.Add((pill, file));
        }
        // Controller code is a second live surface in the general Code panel. Intentionally optional:
        // when no Thing is running the field stays visible and the value applies on the next run.
        var controllerTitle = Text("Controller", side, 10f, ForgeTheme.PanelSubtext);
        controllerTitle.text = "THING CONTROLLER";
        controllerTitle.characterSpacing = 1.5f;
        Place(controllerTitle.rectTransform, 0f, y + 2f);
        controllerSpeed = Forge.ML.MLUI.Input(side, "max_speed", "max_speed", 0f, y + 22f, SideWidth - 6f, 28f);
        controllerSpeed.text = "1.5";
        controllerApply = Forge.ML.MLUI.Pill(side, "Apply max_speed", 0f, y + 56f, SideWidth - 6f, 28f, ApplyControllerSpeed);
        controllerState = Text("Controller state", side, 10f, ForgeTheme.PanelSubtext);
        controllerState.textWrappingMode = TextWrappingModes.Normal;
        controllerState.rectTransform.sizeDelta = new Vector2(SideWidth - 10f, 38f);
        Place(controllerState.rectTransform, 0f, y + 90f);
    }

    // ---------------------------------------------------------------- local mode refresh

    void Refresh()
    {
        // Re-show local pills if they were hidden by a GitHub session
        foreach (var (p, _) in ghPills) if (p != null) p.gameObject.SetActive(false);
        foreach (var (p, _) in pills) if (p != null) p.gameObject.SetActive(true);
        if (projectLabel != null) projectLabel.gameObject.SetActive(true);

        if (pills.Count == 0) BuildFileList();
        var open = CodeProject.Open;
        if (open == null) return;

        int problems = 0;
        foreach (var (pill, file) in pills)
        {
            pill.On = file == open;
            int n = CodeProject.Check(file).Count;
            if (file == open) problems = n;
            var label = pill.GetComponentInChildren<TMP_Text>();
            string text = file.path + (file.text != file.original ? " *" : "") + (n > 0 ? $"  <color=#FF7A7A>{n}</color>" : "");
            if (label.text != text) label.text = text;
        }
        string subtitle = open.path + (problems == 0 ? "" : $" · {problems} error{(problems == 1 ? "" : "s")}");
        if (panel.Subtitle != subtitle) panel.Subtitle = subtitle;
        status.text = CodeProject.Status;
        undo.Button.interactable = open.undo.Count > 0;
        undo.Rect.gameObject.SetActive(open.undo.Count > 0);
        reset.Rect.gameObject.SetActive(true);

        bool moved = open.path != shownPath || CodeProject.HighlightStart != shownStart;
        if (open.text != shownText || open.path != shownPath)
        {
            code.text = Render(open, CodeProject.Check(open));
            code.ForceMeshUpdate();
            Vector2 size = code.GetPreferredValues();
            code.rectTransform.sizeDelta = size;
            content.sizeDelta = new Vector2(Mathf.Max(size.x + 40f, viewport.rect.width), size.y + 8f);
            shownText = open.text;
        }
        shownPath = open.path;
        shownStart = CodeProject.HighlightStart;
        PlaceHighlight(moved);
        RefreshControllerState();
    }

    // ---------------------------------------------------------------- GitHub mode refresh

    void RefreshGitHub()
    {
        bool isPr = GitHubView.Mode == GitHubView.ViewMode.PrDiff;

        // Tabs
        if (filesTab != null) filesTab.On = !isPr;
        if (prTab != null)
        {
            prTab.On = isPr;
            string prLabel = GitHubView.OpenPr != null ? $"PR #{GitHubView.OpenPr.number}" : "PRs";
            var t = prTab.GetComponentInChildren<TMP_Text>();
            if (t != null && t.text != prLabel) t.text = prLabel;
        }

        // Status + subtitle
        if (status != null) status.text = GitHubView.StatusText;
        string subtitle = isPr && GitHubView.OpenPr != null
            ? $"PR #{GitHubView.OpenPr.number} · {GitHubView.OpenPr.title}"
            : (GitHubView.FilePath ?? GitHubView.CurrentSource?.name ?? "");
        if (panel.Subtitle != subtitle) panel.Subtitle = subtitle;

        // Undo/Reset not relevant in GitHub mode
        if (undo != null) undo.Rect.gameObject.SetActive(false);
        if (reset != null) reset.Rect.gameObject.SetActive(false);
        if (highlight != null) highlight.gameObject.SetActive(false);

        // Left column: rebuild when path or mode changes
        bool needRebuild = GitHubView.CurrentPath != shownGhPath || GitHubView.Mode != shownGhMode;
        if (needRebuild) RebuildGhPills();
        shownGhPath = GitHubView.CurrentPath;
        shownGhMode = GitHubView.Mode;

        // Update selected-file highlighting on pills without a full rebuild
        if (!isPr && GitHubView.FilePath != shownGhFile)
        {
            foreach (var (pill, data) in ghPills)
                if (data is GitHubView.TreeItem item)
                    pill.On = item.path == GitHubView.FilePath;
            shownGhFile = GitHubView.FilePath;
        }
        if (isPr && GitHubView.OpenPrFile?.filename != shownGhPrFile)
        {
            foreach (var (pill, data) in ghPills)
                if (data is GitHubView.PrFile f)
                    pill.On = f.filename == GitHubView.OpenPrFile?.filename;
            shownGhPrFile = GitHubView.OpenPrFile?.filename;
        }

        // Right pane
        string newText = isPr ? RenderGhDiff() : RenderGhFile();
        if (newText == null || newText == code.text) return;
        code.text = newText;
        code.ForceMeshUpdate();
        Vector2 sz = code.GetPreferredValues();
        code.rectTransform.sizeDelta = sz;
        content.sizeDelta = new Vector2(Mathf.Max(sz.x + 40f, viewport.rect.width), sz.y + 8f);
        content.anchoredPosition = Vector2.zero;
        scroll.velocity = Vector2.zero;
    }

    void RebuildGhPills()
    {
        foreach (var (p, _) in ghPills) if (p != null) Destroy(p.gameObject);
        ghPills.Clear();

        // Hide local pills and project label
        foreach (var (p, _) in pills) if (p != null) p.gameObject.SetActive(false);
        if (projectLabel != null) projectLabel.gameObject.SetActive(false);

        bool isPr = GitHubView.Mode == GitHubView.ViewMode.PrDiff;
        float y = 8f;

        if (!isPr)
        {
            if (GitHubView.CurrentPath.Length > 0)
            {
                int slash = GitHubView.CurrentPath.LastIndexOf('/');
                string parent = slash > 0 ? GitHubView.CurrentPath.Substring(0, slash) : "";
                var back = ForgePill.Create(side, "← ..", () => GitHubView.OpenPath(parent));
                PosGhPill(back.Rect, y); y += 36f;
                ghPills.Add((back, null));
            }
            foreach (var item in GitHubView.Tree)
            {
                var it = item;
                string label = (it.type == "dir" ? "▸ " : "") + it.name;
                var pill = ForgePill.Create(side, label, it.type == "dir"
                    ? (Action)(() => GitHubView.OpenPath(it.path))
                    : () => GitHubView.OpenFile(it.path));
                pill.On = it.path == GitHubView.FilePath;
                PosGhPill(pill.Rect, y); y += 36f;
                ghPills.Add((pill, it));
            }
        }
        else if (GitHubView.OpenPr == null)
        {
            foreach (var pr in GitHubView.Prs)
            {
                var p = pr;
                string label = $"#{p.number} {Trunc(p.title, 18)}";
                var pill = ForgePill.Create(side, label, () => GitHubView.FetchPr(p.number));
                PosGhPill(pill.Rect, y); y += 36f;
                ghPills.Add((pill, p));
            }
        }
        else
        {
            var backPill = ForgePill.Create(side, "← PRs", GitHubView.FetchPrs);
            PosGhPill(backPill.Rect, y); y += 36f;
            ghPills.Add((backPill, null));
            foreach (var f in GitHubView.OpenPr.files)
            {
                var file = f;
                string icon = f.status switch { "added" => "+ ", "removed" => "- ", "renamed" => "→ ", _ => "~ " };
                string label = icon + Trunc(System.IO.Path.GetFileName(f.filename), 18);
                var pill = ForgePill.Create(side, label, () => GitHubView.ShowPrFile(file));
                pill.On = f.filename == GitHubView.OpenPrFile?.filename;
                PosGhPill(pill.Rect, y); y += 36f;
                ghPills.Add((pill, f));
            }
        }
    }

    void PosGhPill(RectTransform rt, float y)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(SideWidth - 6f, 32f);
        rt.anchoredPosition = new Vector2(0f, -y);
    }

    // ---------------------------------------------------------------- GitHub rendering

    string RenderGhFile()
    {
        if (GitHubView.Loading) return $"<color={LineNoColor}>{GitHubView.StatusText}</color>";
        if (GitHubView.FileContent == null)
            return $"<color={LineNoColor}>Select a file to view it.</color>";
        string path = GitHubView.FilePath ?? "";
        string text = GitHubView.FileContent;
        bool python = path.EndsWith(".py");
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        int width = lines.Length.ToString().Length;
        var sb = new StringBuilder(text.Length * 2);
        sb.Append("<mspace=0.6em>");
        string triple = null;
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append("<color=").Append(LineNoColor).Append('>')
              .Append((i + 1).ToString().PadLeft(width)).Append("</color>  ");
            if (python)
                triple = Python(sb, lines[i], triple);
            else
            {
                string l = lines[i].TrimStart();
                if (l.StartsWith("//") || l.StartsWith("#")) Colored(sb, CommentColor, lines[i]);
                else Escaped(sb, lines[i]);
            }
            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    string RenderGhDiff()
    {
        if (GitHubView.OpenPr == null)
            return $"<color={LineNoColor}>Loading…</color>";
        if (GitHubView.OpenPrFile == null)
        {
            var pr = GitHubView.OpenPr;
            var sb = new StringBuilder();
            sb.Append("<mspace=0.6em>");
            Colored(sb, HeadingColor, $"PR #{pr.number}: {pr.title}\n");
            Colored(sb, LineNoColor, $"by {pr.author}  ·  {pr.head} → {pr.baseBranch}\n");
            sb.Append("+"); Colored(sb, "#22CF86", pr.additions.ToString());
            sb.Append("  -"); Colored(sb, "#FF7A7A", pr.deletions.ToString());
            sb.Append($"  {pr.changedFiles} file{(pr.changedFiles == 1 ? "" : "s")}\n\n");
            if (pr.body.Length > 0) Colored(sb, LineNoColor, pr.body + "\n\n");
            Colored(sb, LineNoColor, "Select a changed file on the left to see the diff.");
            return sb.ToString();
        }
        var pf = GitHubView.OpenPrFile;
        if (string.IsNullOrEmpty(pf.patch))
        {
            string msg = pf.status switch
            {
                "added" => $"New file: {pf.filename}  (+{pf.additions} lines)",
                "removed" => $"Deleted: {pf.filename}  (-{pf.deletions} lines)",
                "renamed" => $"Renamed: {pf.filename}",
                _ => $"{pf.filename}  (no diff available)",
            };
            return $"<mspace=0.6em><color={LineNoColor}>{EscapeStr(msg)}</color>";
        }
        var sb2 = new StringBuilder();
        sb2.Append("<mspace=0.6em>");
        Colored(sb2, HeadingColor, $"--- {pf.filename}  +{pf.additions} -{pf.deletions}\n");
        foreach (string raw in pf.patch.Split('\n'))
        {
            if (raw.StartsWith("@@")) Colored(sb2, NameColor, raw + "\n");
            else if (raw.StartsWith("+") && !raw.StartsWith("+++")) Colored(sb2, "#22CF86", raw + "\n");
            else if (raw.StartsWith("-") && !raw.StartsWith("---")) Colored(sb2, "#FF7A7A", raw + "\n");
            else Colored(sb2, LineNoColor, raw + "\n");
        }
        return sb2.ToString();
    }

    // ---------------------------------------------------------------- controller (ML truck, Satya's work)

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
            CodeProject.SetStatus("max_speed must be a number.");
            return;
        }
        var host = Truck();
        string error = host == null ? "No truck is running." : host.SetTunable("max_speed", value);
        CodeProject.SetStatus(error ?? $"{host.File.Name} max_speed hot reloaded to {value:0.0} m/s.");
        RefreshControllerState(true);
    }

    // ---------------------------------------------------------------- local rendering

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
        if (!resetArmed) { ArmReset(true); return; }
        ArmReset(false);
        CodeProject.Reset();
    }

    void ArmReset(bool on)
    {
        resetArmed = on;
        resetArmedUntil = Time.unscaledTime + 3f;
        reset.On = on;
        reset.GetComponentInChildren<TMP_Text>().text = on ? "Tap again to reset" : "Reset";
    }

    // ---------------------------------------------------------------- Python syntax

    static readonly HashSet<string> Keywords = new HashSet<string>
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif",
        "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or",
        "pass", "raise", "return", "try", "while", "with", "yield",
    };
    const string KeywordColor = "#C792EA", StringColor = "#C3E88D", CommentColor = "#6F7A88", NumberColor = "#F78C6C",
        NameColor = "#82AAFF", LineNoColor = "#5C6370", ErrorNoColor = "#FF7A7A", HeadingColor = "#FFCB6B";

    static string Render(CodeProject.File f, List<CodeProject.Problem> problems)
    {
        var bad = new HashSet<int>();
        foreach (var p in problems) bad.Add(p.line);
        bool python = f.path.EndsWith(".py");
        string[] lines = f.text.Split('\n');
        int width = lines.Length.ToString().Length;
        var sb = new StringBuilder(f.text.Length * 2);
        sb.Append("<mspace=0.6em>");
        string triple = null;
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append("<color=").Append(bad.Contains(i + 1) ? ErrorNoColor : LineNoColor).Append('>')
              .Append((i + 1).ToString().PadLeft(width)).Append("</color>  ");
            if (python) triple = Python(sb, lines[i], triple);
            else if (lines[i].StartsWith("#")) Colored(sb, HeadingColor, lines[i]);
            else Escaped(sb, lines[i]);
            if (i < lines.Length - 1) sb.Append('\n');
        }
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

    static string EscapeStr(string text) =>
        text.IndexOf('<') < 0 ? text : text.Replace("<", "<noparse><</noparse>");

    static string Trunc(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

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
