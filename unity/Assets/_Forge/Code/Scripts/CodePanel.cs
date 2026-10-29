using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The code view on the headset: the sample project's files on the left, the open file on the right (monospace, line
/// numbers, simple Python colors, the highlighted lines with Albie's note), and a bottom bar with the status line, Undo
/// and Reset (tap twice). Shows <see cref="CodeProject"/>; Albie drives it through <see cref="CodeTools"/>.
/// Lives on Code/Resources/CodePanel.prefab (a ForgePanel; Forge > Code > Build Code Panel Prefab). UI is built in code.
/// Redraws only when the project changes, never per frame.
/// </summary>
[RequireComponent(typeof(ForgePanel))]
public class CodePanel : MonoBehaviour
{
    const float SideWidth = 150f, BarHeight = 40f, Gap = 10f, FontSize = 12.5f, Pad = 8f;
    static readonly Color CodeBg = new Color(0f, 0f, 0f, 0.28f);
    static readonly Color ErrorTint = new Color(1f, 0.35f, 0.35f, 0.22f);

    ForgePanel panel;
    RectTransform side, viewport, content, bar;
    TMP_Text code, note, status, projectLabel;
    Image highlight;
    ScrollRect scroll;
    ForgePill undo, reset;
    readonly List<(ForgePill pill, CodeProject.File file)> pills = new List<(ForgePill, CodeProject.File)>();
    bool dirty = true, resetArmed;
    float resetArmedUntil;
    string shownText, shownPath;
    int shownStart = -1;

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
        dirty = true;
    }

    void OnDisable() => CodeProject.Changed -= MarkDirty;

    void MarkDirty() => dirty = true;

    void LateUpdate()
    {
        if (resetArmed && Time.unscaledTime > resetArmedUntil) ArmReset(false);
        if (!dirty || code == null) return;
        dirty = false;
        Refresh();
    }

    // ---------------------------------------------------------------- layout

    void Build()
    {
        RectTransform root = panel.Content;

        side = Rect("Files", root, new Vector2(0f, 0f), new Vector2(0f, 1f));
        side.pivot = new Vector2(0f, 1f);
        side.offsetMin = new Vector2(0f, BarHeight + Gap);
        side.offsetMax = new Vector2(SideWidth, 0f);
        projectLabel = Text("Project", side, 11f, ForgeTheme.PanelSubtext);
        projectLabel.characterSpacing = 2f;
        Place(projectLabel.rectTransform, 0f, 18f);

        var codeBox = Rect("Code", root, Vector2.zero, Vector2.one);
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
    }

    // ---------------------------------------------------------------- refresh

    void Refresh()
    {
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
    }

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
            // Keep the note in view: its right edge at the viewport's right edge.
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
        if (!resetArmed)
        {
            ArmReset(true);
            return;
        }
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

    // ---------------------------------------------------------------- rendering

    static readonly HashSet<string> Keywords = new HashSet<string>
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif",
        "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or",
        "pass", "raise", "return", "try", "while", "with", "yield",
    };
    const string KeywordColor = "#C792EA", StringColor = "#C3E88D", CommentColor = "#6F7A88", NumberColor = "#F78C6C",
        NameColor = "#82AAFF", LineNoColor = "#5C6370", ErrorNoColor = "#FF7A7A", HeadingColor = "#FFCB6B";

    /// <summary>Rich text for TMP: monospaced, numbered, colored. ponytail: a line-by-line tokenizer, not a parser.</summary>
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
            if (c == '#')
            {
                Colored(sb, CommentColor, line.Substring(i));
                break;
            }
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
                // A string prefix (f"...", r'...'): leave it to the string branch with the prefix colored too.
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

    // TMP reads '<' as a tag: wrap each one in noparse.
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
