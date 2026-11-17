using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

/// <summary>
/// Albie's source control tools, on <see cref="GitHubView"/>: list and open the account's repos, open a file, show
/// pull requests, and link a repo to the open project. Every one reads; none writes to a repo.
/// Each answers when the hub has replied, so Albie speaks from what actually loaded.
/// Definitions: Resources/AlbieTools/github.json.
/// </summary>
public static class GitHubTools
{
    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register() => AlbieTools.Register("github", new Dictionary<string, AlbieTools.AsyncHandler>
    {
        ["github_list_repos"] = (_, done) => WithRepos(done, () => done(Repos())),
        ["github_open_repo"] = OpenRepo,
        ["github_open_file"] = OpenFile,
        ["github_list_prs"] = (_, done) => ListPrs(done),
        ["github_show_pr"] = ShowPr,
        ["github_close_pr"] = (_, done) => { GitHubView.ShowFiles(); done(new JObject { ["note"] = "back to the repo's files" }); },
        ["github_link_repo"] = LinkRepo,
    });

    /// <summary>Runs <paramref name="then"/> once the repo list is loaded, or answers with why it can't be.</summary>
    static void WithRepos(Action<JObject> done, Action then)
    {
        if (GitHubView.Sources.Count > 0) { then(); return; }
        GitHubView.Refresh(error =>
        {
            if (error != null) done(AlbieTools.Error(error));
            else if (GitHubView.Sources.Count == 0) done(AlbieTools.Error(GitHubView.StatusText));
            else then();
        });
    }

    static JObject Repos()
    {
        var list = new JArray();
        foreach (var s in GitHubView.Sources)
            list.Add(new JObject { ["id"] = s.id, ["name"] = s.name, ["description"] = s.description });
        string project = ProjectRepos.CurrentProject;
        return new JObject
        {
            ["repos"] = list, ["open"] = GitHubView.CurrentSource?.id,
            ["project"] = project, ["project_repo"] = ProjectRepos.RepoFor(project),
        };
    }

    static void OpenRepo(JObject args, Action<JObject> done)
    {
        string repo = (string)args["repo"] ?? "";
        WithRepos(done, () =>
        {
            var source = GitHubView.FindSource(repo);
            if (source == null)
            {
                done(AlbieTools.Error($"There is no repo '{repo}'. github_list_repos lists them."));
                return;
            }
            OpenView();
            GitHubView.SelectSource(source, error => done(error != null ? AlbieTools.Error(error) : new JObject { ["opened"] = source.id, ["top_folder"] = Folder() }));
        });
    }

    static void OpenFile(JObject args, Action<JObject> done)
    {
        string path = ((string)args["path"] ?? "").Trim();
        if (path.Length == 0) throw new ArgumentException("path is required");
        OpenView();
        GitHubView.OpenFile(path, error =>
        {
            if (error != null) done(AlbieTools.Error(error));
            else done(new JObject { ["opened"] = GitHubView.FilePath, ["repo"] = GitHubView.CurrentSource.id, ["note"] = GitHubView.FileNote });
        });
    }

    static void ListPrs(Action<JObject> done)
    {
        OpenView();
        GitHubView.ShowPrs(error =>
        {
            if (error != null) { done(AlbieTools.Error(error)); return; }
            var list = new JArray();
            foreach (var pr in GitHubView.Prs)
                list.Add(new JObject { ["number"] = pr.number, ["title"] = pr.title, ["author"] = pr.author, ["from"] = pr.head, ["into"] = pr.baseBranch });
            done(new JObject { ["repo"] = GitHubView.CurrentSource.id, ["open_pull_requests"] = list });
        });
    }

    static void ShowPr(JObject args, Action<JObject> done)
    {
        int number = args.Value<int?>("number") ?? 0;
        if (number <= 0) throw new ArgumentException("number must be a pull request number");
        OpenView();
        GitHubView.FetchPr(number, error =>
        {
            if (error != null) { done(AlbieTools.Error(error)); return; }
            var pr = GitHubView.OpenPr;
            var files = new JArray();
            foreach (var f in pr.files)
                files.Add(new JObject { ["file"] = f.filename, ["status"] = f.status, ["added"] = f.additions, ["removed"] = f.deletions });
            done(new JObject
            {
                ["number"] = pr.number, ["title"] = pr.title, ["author"] = pr.author, ["from"] = pr.head, ["into"] = pr.baseBranch,
                ["description"] = pr.body, ["added"] = pr.additions, ["removed"] = pr.deletions, ["files"] = files,
            });
        });
    }

    static void LinkRepo(JObject args, Action<JObject> done)
    {
        string repo = ((string)args["repo"] ?? "").Trim();
        WithRepos(done, () =>
        {
            var source = repo.Length == 0 ? GitHubView.CurrentSource : GitHubView.FindSource(repo);
            if (source == null)
            {
                done(AlbieTools.Error(repo.Length == 0 ? "No repo is open to link." : $"There is no repo '{repo}'. github_list_repos lists them."));
                return;
            }
            string project = ProjectRepos.CurrentProject;
            ProjectRepos.Link(project, source.id);
            if (source != GitHubView.CurrentSource) GitHubView.SelectSource(source);
            done(new JObject { ["project"] = project, ["linked_repo"] = source.id, ["note"] = "the code panel opens this repo for this project from now on" });
        });
    }

    static JArray Folder()
    {
        var names = new JArray();
        foreach (var item in GitHubView.Tree) names.Add(item.IsDir ? item.name + "/" : item.name);
        return names;
    }

    static void OpenView()
    {
        var ws = Workspace.Current;
        if (ws != null && ws.ActiveMode != "code" && ws.presence != null && ws.presence.AtDesk) ws.Open("code");
    }
}
