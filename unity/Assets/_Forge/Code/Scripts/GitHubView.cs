using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// What the code panel shows from source control: the demo account's repos, one repo's folders and files, and its
/// pull requests. Everything is read through the /code routes (hub/code_browser), served by Albie's cloud with no
/// laptop (cloud-py/app.py) or by the laptop hub; the server holds the token and the device never sees it
/// (D-04, D-30). The only writes are <see cref="ApprovePr"/> and <see cref="MergePr"/>; call them only after the
/// user confirmed. It loads itself when the cloud or hub becomes reachable, and follows the <c>code.view</c> event
/// (Albie's github_ tools, hub/code_browser/tools.py). Main thread only. <see cref="Changed"/> fires on every change;
/// <see cref="Version"/> goes up when what is on screen changed (not for a status line alone).
/// </summary>
public static class GitHubView
{
    public sealed class Source
    {
        public string id, owner, name, fullName, defaultBranch, description;
    }

    public sealed class TreeItem
    {
        public string name, path, type;  // type: "file", "dir", "symlink" or "submodule"
        public bool IsDir => type == "dir";
    }

    public sealed class PrSummary
    {
        public int number;
        public string title, author, head, baseBranch, body;
    }

    public sealed class PrFile
    {
        public string filename, status, patch;
        public int additions, deletions;
        public bool truncated;
    }

    public sealed class PrDetail
    {
        public int number, additions, deletions, changedFiles;
        public string title, author, head, baseBranch, body;
        public readonly List<PrFile> files = new List<PrFile>();
    }

    public enum ViewMode { Files, Prs }

    const int KSources = 0, KTree = 1, KFile = 2, KPr = 3;

    public static IReadOnlyList<Source> Sources => sources;
    public static Source CurrentSource { get; private set; }
    public static IReadOnlyList<TreeItem> Tree => tree;
    public static string CurrentPath { get; private set; } = "";
    public static string FilePath { get; private set; }
    public static string FileContent { get; private set; }
    /// <summary>Why a file shows nothing or only part: too large, binary, truncated. Empty otherwise.</summary>
    public static string FileNote { get; private set; } = "";
    public static IReadOnlyList<PrSummary> Prs => prs;
    /// <summary>The open pull request; null in <see cref="ViewMode.Prs"/> means the list is showing.</summary>
    public static PrDetail OpenPr { get; private set; }
    /// <summary>The changed file whose diff is showing; null = the pull request's summary.</summary>
    public static PrFile OpenPrFile { get; private set; }
    public static ViewMode Mode { get; private set; }
    public static bool Loading => pending > 0;
    public static string StatusText { get; private set; } = "";
    public static int Version { get; private set; }

    public static event Action Changed;

    static readonly List<Source> sources = new List<Source>();
    static List<TreeItem> tree = new List<TreeItem>();
    static List<PrSummary> prs = new List<PrSummary>();
    // A reply is dropped when a newer request of its kind has started, so a slow one can't overwrite a newer one.
    static readonly int[] tickets = new int[4];
    static int pending;
    static string appliedProject;
    static IDisposable viewEvents, cloudReady;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        sources.Clear();
        tree = new List<TreeItem>();
        prs = new List<PrSummary>();
        CurrentSource = null;
        CurrentPath = "";
        FilePath = FileContent = null;
        FileNote = "";
        OpenPr = null;
        OpenPrFile = null;
        Mode = ViewMode.Files;
        StatusText = "";
        Version = 0;
        pending = 0;
        appliedProject = null;
        Array.Clear(tickets, 0, tickets.Length);
        Changed = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Start()
    {
        ForgeNet.StatusChanged -= OnStatus;
        ForgeNet.StatusChanged += OnStatus;
        viewEvents?.Dispose();
        viewEvents = ForgeNet.On("code.view", OnView);
        cloudReady?.Dispose();
        cloudReady = ForgeNet.On("cloud.ready", _ => { if (sources.Count == 0 && pending == 0) Refresh(); });
        if (Reachable) Refresh();
    }

    static void OnStatus(ForgeNetStatus status)
    {
        if (Reachable) Refresh();
        else Fire(false);
    }

    // code.view {repo, view?, path?, file?, pr?, link_project?}: what Albie asked the panel to show.
    static void OnView(JToken payload)
    {
        if (!(payload is JObject view)) return;
        if (FindSource((string)view["repo"]) != null) Show(view);
        else Refresh(error => { if (error == null) Show(view); });  // a repo made since the list was loaded
    }

    static void Show(JObject view)
    {
        Source source = FindSource((string)view["repo"]);
        if (source == null) return;
        if ((bool?)view["link_project"] == true) ProjectRepos.Link(ProjectRepos.CurrentProject, source.id);
        bool switched = source != CurrentSource;
        if (switched) SelectSource(source);

        string file = (string)view["file"], path = (string)view["path"], tab = (string)view["view"];
        int? pr = (int?)view["pr"];
        if (pr != null) FetchPr(pr.Value, null, file);
        else if (tab == "prs") ShowPrs();
        else
        {
            if (path != null && !(switched && path.Length == 0)) OpenPath(path);
            if (file != null) OpenFile(file);
            else if (Mode != ViewMode.Files) ShowFiles();
        }
    }

    /// <summary>Source control can be read now: through the laptop hub, or Albie's cloud with no hub.</summary>
    public static bool Reachable => ForgeCloud.Serves("/code/sources");

    // ---------------------------------------------------------------- actions (the panel)
    // Each takes an optional done(error): null = it worked.

    /// <summary>Load the account's repos. The first time, opens the project's linked repo, or the newest one.</summary>
    public static void Refresh(Action<string> done = null)
    {
        Fetch(KSources, "/code/sources", "Connecting to source control...", "Could not reach source control.", json =>
        {
            var read = new List<Source>();
            foreach (JObject s in Items(json, "sources"))
                read.Add(new Source
                {
                    id = (string)s["id"], owner = (string)s["owner"], name = (string)s["name"], fullName = (string)s["full_name"],
                    defaultBranch = (string)s["default_branch"], description = (string)s["description"] ?? "",
                });
            sources.Clear();
            sources.AddRange(read);
            string note = (string)json["note"] ?? "";
            StatusText = note.Length > 0 ? note : sources.Count == 0 ? "The source control account has no repos." : "";

            Source keep = CurrentSource == null ? null : FindSource(CurrentSource.id);
            if (keep != null)
            {
                CurrentSource = keep;
                return;
            }
            appliedProject = ProjectRepos.CurrentProject;
            string linked = ProjectRepos.RepoFor(appliedProject);
            Source pick = (linked == null ? null : FindSource(linked)) ?? (sources.Count > 0 ? sources[0] : null);
            string missing = linked != null && pick != null && FindSource(linked) == null ? MissingRepo(linked, pick) : null;
            if (pick != null) SelectSource(pick, missing == null ? (Action<string>)null : e => { if (e == null) { StatusText = missing; Fire(false); } });
            else CurrentSource = null;
        }, done);
    }

    /// <summary>Open the repo linked to the open project when the project changes. Cheap; the panel calls it every frame.</summary>
    public static void SyncProject()
    {
        string project = ProjectRepos.CurrentProject;
        if (project == appliedProject || sources.Count == 0) return;
        appliedProject = project;
        string linked = ProjectRepos.RepoFor(project);
        Source source = linked == null ? null : FindSource(linked);
        if (source != null && source != CurrentSource) SelectSource(source);
        else if (linked != null && source == null && CurrentSource != null) { StatusText = MissingRepo(linked, CurrentSource); Fire(false); }
    }

    // The open project's linked repo isn't on the connected account: say so instead of quietly showing another one.
    static string MissingRepo(string linked, Source shown) => $"This project's repo {linked} isn't on the connected account. Showing {shown.name}.";

    /// <summary>A repo by "owner/name" or just its name; null if the account doesn't have it.</summary>
    public static Source FindSource(string repo)
    {
        repo = (repo ?? "").Trim();
        foreach (var s in sources)
            if (string.Equals(s.id, repo, StringComparison.OrdinalIgnoreCase) || string.Equals(s.name, repo, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    /// <summary>Switch to another repo and load its top folder.</summary>
    public static void SelectSource(Source source, Action<string> done = null)
    {
        CurrentSource = source;
        tree = new List<TreeItem>();
        prs = new List<PrSummary>();
        CurrentPath = "";
        FilePath = FileContent = null;
        FileNote = "";
        OpenPr = null;
        OpenPrFile = null;
        Mode = ViewMode.Files;
        tickets[KTree]++;
        tickets[KFile]++;
        tickets[KPr]++;
        OpenPath("", done);
    }

    /// <summary>Show a folder ("" = the top of the repo).</summary>
    public static void OpenPath(string path, Action<string> done = null)
    {
        if (!HasRepo(done)) return;
        path = (path ?? "").Trim('/');
        string label = path.Length == 0 ? CurrentSource.name : path;
        Fetch(KTree, $"{Repo}/tree?path={Uri.EscapeDataString(path)}", $"Loading {label}...", $"Could not load {label}.", json =>
        {
            var read = new List<TreeItem>();
            foreach (JObject item in Items(json, "items"))
                read.Add(new TreeItem { name = (string)item["name"], path = (string)item["path"], type = (string)item["type"] });
            read.Sort((a, b) => a.IsDir != b.IsDir ? (a.IsDir ? -1 : 1) : string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            tree = read;
            CurrentPath = path;
        }, done);
    }

    /// <summary>Show a file.</summary>
    public static void OpenFile(string path, Action<string> done = null)
    {
        if (!HasRepo(done)) return;
        path = (path ?? "").Trim('/');
        if (Mode != ViewMode.Files)
        {
            Mode = ViewMode.Files;
            Version++;
        }
        Fetch(KFile, $"{Repo}/file?path={Uri.EscapeDataString(path)}", $"Opening {path}...", $"Could not open {path}.", json =>
        {
            string content = (string)json["content"] ?? "";
            FilePath = (string)json["path"] ?? path;
            FileContent = content;
            FileNote = (string)json["note"] ?? "";
            StatusText = FileNote;
        }, done);
    }

    /// <summary>Show the repo's open pull requests.</summary>
    public static void ShowPrs(Action<string> done = null)
    {
        if (!HasRepo(done)) return;
        Mode = ViewMode.Prs;
        OpenPr = null;
        OpenPrFile = null;
        Version++;
        Fetch(KPr, $"{Repo}/prs", "Loading pull requests...", "Could not load pull requests.", json =>
        {
            var read = new List<PrSummary>();
            foreach (JObject pr in Items(json, "prs"))
                read.Add(new PrSummary
                {
                    number = (int)pr["number"], title = (string)pr["title"], author = (string)pr["author"],
                    head = (string)pr["head"], baseBranch = (string)pr["base"], body = (string)pr["body"] ?? "",
                });
            prs = read;
            StatusText = prs.Count == 0 ? "No open pull requests." : $"{prs.Count} open pull request{(prs.Count == 1 ? "" : "s")}.";
        }, done);
    }

    /// <summary>Show one pull request: its summary, or the diff of the changed file named <paramref name="file"/>.</summary>
    public static void FetchPr(int number, Action<string> done = null, string file = null)
    {
        if (!HasRepo(done)) return;
        Fetch(KPr, $"{Repo}/pr/{number}", $"Loading pull request #{number}...", $"Could not load pull request #{number}.", json =>
        {
            var pr = new PrDetail
            {
                number = (int)json["number"], title = (string)json["title"], author = (string)json["author"],
                head = (string)json["head"], baseBranch = (string)json["base"], body = (string)json["body"] ?? "",
                additions = (int?)json["additions"] ?? 0, deletions = (int?)json["deletions"] ?? 0, changedFiles = (int?)json["changed_files"] ?? 0,
            };
            foreach (JObject f in Items(json, "files"))
                pr.files.Add(new PrFile
                {
                    filename = (string)f["filename"], status = (string)f["status"], patch = (string)f["patch"] ?? "",
                    additions = (int?)f["additions"] ?? 0, deletions = (int?)f["deletions"] ?? 0, truncated = (bool?)f["truncated"] ?? false,
                });
            OpenPr = pr;
            OpenPrFile = file == null ? null : pr.files.Find(f => f.filename == file);
            Mode = ViewMode.Prs;
            StatusText = $"Pull request #{pr.number}: +{pr.additions} -{pr.deletions} in {pr.files.Count} file{(pr.files.Count == 1 ? "" : "s")}.";
        }, done);
    }

    /// <summary>In an open pull request: show one changed file's diff, or the summary (null).</summary>
    public static void ShowPrFile(PrFile file)
    {
        OpenPrFile = file;
        Fire();
    }

    /// <summary>From an open pull request back to the list.</summary>
    public static void BackToPrList()
    {
        if (prs.Count == 0) { ShowPrs(); return; }
        OpenPr = null;
        OpenPrFile = null;
        Fire();
    }

    /// <summary>Approve the open pull request as the demo account. Only after the user confirmed.</summary>
    public static void ApprovePr(Action<string> done = null) => Write("approve", "Approving", done);

    /// <summary>Merge the open pull request, then show the list again. Only after the user confirmed.</summary>
    public static void MergePr(Action<string> done = null) => Write("merge", "Merging", done);

    static void Write(string action, string doing, Action<string> done)
    {
        if (!HasRepo(done)) return;
        if (OpenPr == null || !Reachable)
        {
            done?.Invoke(OpenPr == null ? "No pull request is open." : "Source control needs Albie's cloud, and it isn't reachable right now.");
            return;
        }
        int number = OpenPr.number;
        pending++;
        StatusText = $"{doing} pull request #{number}...";
        Fire(false);
        ForgeNet.Post($"{Repo}/pr/{number}/{action}", new JObject { ["confirm"] = true }, text =>
        {
            pending--;
            StatusText = "";
            Fire(false);
            if (action == "merge") ShowPrs();
            done?.Invoke(null);
        },
        error =>
        {
            pending--;
            Debug.LogWarning($"[GitHubView] {action} #{number}: {error}");
            // The server answers a refusal with {"detail": GitHub's reason}; ForgeNet puts that body at the end.
            string reason = null;
            int brace = error.IndexOf('{');
            if (brace >= 0)
                try { reason = (string)JObject.Parse(error.Substring(brace))["detail"]; }
                catch (Exception e) when (e is JsonException || e is ArgumentException || e is InvalidCastException) { }
            StatusText = reason ?? $"Could not {action} pull request #{number}.";
            Fire(false);
            done?.Invoke(StatusText);
        });
    }

    /// <summary>Back to the repo's folders and files.</summary>
    public static void ShowFiles()
    {
        Mode = ViewMode.Files;
        StatusText = "";
        Fire();
    }

    // ---------------------------------------------------------------- private

    static string Repo => $"/code/{CurrentSource.owner}/{CurrentSource.name}";

    static bool HasRepo(Action<string> done)
    {
        if (CurrentSource != null) return true;
        done?.Invoke(Reachable ? "No repo is open." : "Source control needs Albie's cloud, and it isn't reachable right now.");
        return false;
    }

    static IEnumerable<JToken> Items(JObject json, string key) => json[key] as JArray ?? new JArray();

    static void Fetch(int kind, string path, string busy, string failed, Action<JObject> apply, Action<string> done)
    {
        if (!Reachable)
        {
            done?.Invoke("Source control needs Albie's cloud, and it isn't reachable right now.");
            return;
        }
        int ticket = ++tickets[kind];
        pending++;
        StatusText = busy;
        Fire(false);
        ForgeNet.Get(path, text =>
        {
            pending--;
            if (ticket != tickets[kind])
            {
                Fire(false);
                done?.Invoke("A newer request replaced this one.");
                return;
            }
            string error = null;
            StatusText = "";
            try { apply(JObject.Parse(text)); }
            catch (Exception e) when (e is JsonException || e is ArgumentException || e is InvalidCastException || e is FormatException)
            {
                Debug.LogWarning($"[GitHubView] {path}: {e.Message}");
                StatusText = error = failed;
            }
            Fire(error == null);
            done?.Invoke(error);
        },
        error =>
        {
            pending--;
            Debug.LogWarning($"[GitHubView] {path}: {error}");
            if (ticket == tickets[kind]) StatusText = failed;
            Fire(false);
            done?.Invoke(failed);
        });
    }

    static void Fire(bool screenChanged = true)
    {
        if (screenChanged) Version++;
        Changed?.Invoke();
    }
}
