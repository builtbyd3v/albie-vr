using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Shared state for the GitHub code browser. Fetches repos, file trees, file content and PR diffs from the
/// hub's /code endpoints (hub/code_browser/__init__.py). The hub holds the GitHub token; the device never
/// sees it (D-04, D-30). Main thread only. <see cref="Changed"/> fires whenever the visible state changes.
/// </summary>
public static class GitHubView
{
    public sealed class Source
    {
        public string id, owner, name, fullName, defaultBranch, description;
        public bool writable;
    }

    public sealed class TreeItem
    {
        public string name, path, type;  // type: "file" or "dir"
        public int size;
    }

    public sealed class PrSummary
    {
        public int number, additions, deletions, changedFiles;
        public string title, author, state, head, baseBranch, body;
    }

    public sealed class PrFile
    {
        public string filename, status, patch;
        public int additions, deletions;
    }

    public sealed class PrDetail
    {
        public int number, additions, deletions, changedFiles;
        public string title, author, state, head, baseBranch, body;
        public bool? mergeable;
        public List<PrFile> files = new List<PrFile>();
    }

    // ---------------------------------------------------------------- state

    public static string GitHubUser { get; private set; } = "";
    public static IReadOnlyList<Source> Sources => sources;
    public static Source CurrentSource { get; private set; }
    public static IReadOnlyList<TreeItem> Tree => tree;
    public static string CurrentPath { get; private set; } = "";
    public static string FileContent { get; private set; }
    public static string FilePath { get; private set; }
    public static IReadOnlyList<PrSummary> Prs => prs;
    public static PrDetail OpenPr { get; private set; }
    public static PrFile OpenPrFile { get; private set; }  // which changed file is shown in the diff viewer
    public static bool Loading { get; private set; }
    public static string StatusText { get; private set; } = "";
    public static ViewMode Mode { get; private set; } = ViewMode.Files;

    public enum ViewMode { Files, PrDiff }

    public static event Action Changed;

    static readonly List<Source> sources = new List<Source>();
    static readonly List<TreeItem> tree = new List<TreeItem>();
    static readonly List<PrSummary> prs = new List<PrSummary>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        GitHubUser = "";
        sources.Clear();
        CurrentSource = null;
        tree.Clear();
        CurrentPath = "";
        FileContent = FilePath = null;
        prs.Clear();
        OpenPr = null;
        OpenPrFile = null;
        Loading = false;
        StatusText = "";
        Mode = ViewMode.Files;
        Changed = null;
    }

    // ---------------------------------------------------------------- public API (called by GitHubTools and GitHubPanel)

    /// <summary>Load sources from the hub and auto-select the first repo. Called on hub connect.</summary>
    public static void Refresh()
    {
        SetLoading("Connecting to source control…");
        ForgeNet.Get("/code/sources", text =>
        {
            var json = JObject.Parse(text);
            GitHubUser = json.Value<string>("user") ?? "";
            sources.Clear();
            foreach (JObject s in json["sources"] ?? new JArray())
                sources.Add(ParseSource(s));
            Loading = false;
            StatusText = sources.Count == 0 ? "No repos found. Add repos to the demo account." : "";
            if (sources.Count > 0 && CurrentSource == null)
                SelectSource(sources[0]);
            else
                Fire();
        },
        err =>
        {
            Loading = false;
            StatusText = "Could not reach source control.";
            Fire();
        });
    }

    /// <summary>Switch to a different repo by id (owner/name) or just name.</summary>
    public static bool SelectSourceById(string id)
    {
        id = (id ?? "").Trim().ToLower();
        var s = sources.Find(x => x.id.ToLower() == id || x.name.ToLower() == id);
        if (s == null) return false;
        SelectSource(s);
        return true;
    }

    /// <summary>Fetch a directory. Empty path = repo root.</summary>
    public static void OpenPath(string path)
    {
        if (CurrentSource == null) return;
        path = (path ?? "").Trim('/');
        SetLoading($"Loading {(path.Length == 0 ? "root" : path)}…");
        ForgeNet.Get($"/code/{CurrentSource.owner}/{CurrentSource.name}/tree?path={Uri.EscapeDataString(path)}", text =>
        {
            var json = JObject.Parse(text);
            CurrentPath = path;
            tree.Clear();
            FileContent = FilePath = null;
            Mode = ViewMode.Files;
            foreach (JObject item in json["items"] ?? new JArray())
                tree.Add(new TreeItem
                {
                    name = item.Value<string>("name"),
                    path = item.Value<string>("path"),
                    type = item.Value<string>("type"),
                    size = item.Value<int>("size"),
                });
            // Dirs first, then files alphabetically.
            tree.Sort((a, b) =>
            {
                int cmp = string.Compare(a.type, b.type, StringComparison.Ordinal);  // "dir" < "file"
                return cmp != 0 ? cmp : string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            });
            Loading = false;
            StatusText = "";
            Fire();
        },
        err =>
        {
            Loading = false;
            StatusText = $"Could not load {path}.";
            Fire();
        });
    }

    /// <summary>Fetch and display a file's content.</summary>
    public static void OpenFile(string path)
    {
        if (CurrentSource == null) return;
        SetLoading($"Opening {path}…");
        ForgeNet.Get($"/code/{CurrentSource.owner}/{CurrentSource.name}/file?path={Uri.EscapeDataString(path)}", text =>
        {
            var json = JObject.Parse(text);
            FilePath = json.Value<string>("path");
            FileContent = json.Value<string>("content") ?? "";
            Mode = ViewMode.Files;
            Loading = false;
            StatusText = json.Value<bool>("truncated") ? "File truncated at 256 KB." : "";
            Fire();
        },
        err =>
        {
            Loading = false;
            StatusText = $"Could not open {path}.";
            Fire();
        });
    }

    /// <summary>Load the open PR list for the current repo.</summary>
    public static void FetchPrs()
    {
        if (CurrentSource == null) return;
        SetLoading("Loading pull requests…");
        ForgeNet.Get($"/code/{CurrentSource.owner}/{CurrentSource.name}/prs", text =>
        {
            var json = JObject.Parse(text);
            prs.Clear();
            foreach (JObject pr in json["prs"] ?? new JArray())
                prs.Add(ParsePrSummary(pr));
            Loading = false;
            StatusText = prs.Count == 0 ? "No open pull requests." : $"{prs.Count} open PR{(prs.Count == 1 ? "" : "s")}";
            Fire();
        },
        err =>
        {
            Loading = false;
            StatusText = "Could not load pull requests.";
            Fire();
        });
    }

    /// <summary>Load a PR and switch to PR diff view.</summary>
    public static void FetchPr(int number)
    {
        if (CurrentSource == null) return;
        SetLoading($"Loading PR #{number}…");
        ForgeNet.Get($"/code/{CurrentSource.owner}/{CurrentSource.name}/pr/{number}", text =>
        {
            var json = JObject.Parse(text);
            OpenPr = ParsePrDetail(json);
            OpenPrFile = OpenPr.files.Count > 0 ? OpenPr.files[0] : null;
            Mode = ViewMode.PrDiff;
            Loading = false;
            StatusText = $"PR #{OpenPr.number} · +{OpenPr.additions} -{OpenPr.deletions}";
            Fire();
        },
        err =>
        {
            Loading = false;
            StatusText = $"Could not load PR #{number}.";
            Fire();
        });
    }

    /// <summary>Switch which changed file is shown in the diff viewer.</summary>
    public static void ShowPrFile(PrFile f)
    {
        OpenPrFile = f;
        Fire();
    }

    /// <summary>Close the PR diff and return to file view.</summary>
    public static void ClosePr()
    {
        OpenPr = null;
        OpenPrFile = null;
        Mode = ViewMode.Files;
        StatusText = "";
        Fire();
    }

    // ---------------------------------------------------------------- private

    static void SelectSource(Source s)
    {
        CurrentSource = s;
        tree.Clear();
        CurrentPath = "";
        FileContent = FilePath = null;
        prs.Clear();
        OpenPr = null;
        OpenPrFile = null;
        Mode = ViewMode.Files;
        Fire();
        OpenPath("");
    }

    static void SetLoading(string status)
    {
        Loading = true;
        StatusText = status;
        Fire();
    }

    static void Fire() => Changed?.Invoke();

    static Source ParseSource(JObject j) => new Source
    {
        id = j.Value<string>("id"),
        owner = j.Value<string>("owner"),
        name = j.Value<string>("name"),
        fullName = j.Value<string>("full_name"),
        defaultBranch = j.Value<string>("default_branch"),
        description = j.Value<string>("description") ?? "",
        writable = j.Value<bool>("writable"),
    };

    static PrSummary ParsePrSummary(JObject j) => new PrSummary
    {
        number = j.Value<int>("number"),
        title = j.Value<string>("title"),
        author = j.Value<string>("author"),
        state = j.Value<string>("state"),
        head = j.Value<string>("head"),
        baseBranch = j.Value<string>("base"),
        body = j.Value<string>("body") ?? "",
        additions = j.Value<int>("additions"),
        deletions = j.Value<int>("deletions"),
        changedFiles = j.Value<int>("changed_files"),
    };

    static PrDetail ParsePrDetail(JObject j)
    {
        var d = new PrDetail
        {
            number = j.Value<int>("number"),
            title = j.Value<string>("title"),
            author = j.Value<string>("author"),
            state = j.Value<string>("state"),
            head = j.Value<string>("head"),
            baseBranch = j.Value<string>("base"),
            body = j.Value<string>("body") ?? "",
            additions = j.Value<int>("additions"),
            deletions = j.Value<int>("deletions"),
            changedFiles = j.Value<int>("changed_files"),
            mergeable = j["mergeable"]?.Type == JTokenType.Boolean ? j.Value<bool>("mergeable") : (bool?)null,
        };
        foreach (JObject f in j["files"] ?? new JArray())
            d.files.Add(new PrFile
            {
                filename = f.Value<string>("filename"),
                status = f.Value<string>("status"),
                additions = f.Value<int>("additions"),
                deletions = f.Value<int>("deletions"),
                patch = f.Value<string>("patch") ?? "",
            });
        return d;
    }
}
