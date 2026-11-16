using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie's GitHub tools: list repos, open a repo, open a file and show PRs.
/// Push / merge always requires the user to confirm through Albie (never a silent action).
/// Tool definitions: Resources/AlbieTools/github.json.
/// </summary>
public static class GitHubTools
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register() => AlbieTools.Register("github", new Dictionary<string, AlbieTools.Handler>
    {
        ["github_list_repos"]  = _ => ListRepos(),
        ["github_open_repo"]   = OpenRepo,
        ["github_open_file"]   = OpenFile,
        ["github_list_prs"]    = _ => ListPrs(),
        ["github_show_pr"]     = ShowPr,
        ["github_close_pr"]    = _ => ClosePr(),
    });

    static JObject ListRepos()
    {
        OpenView();
        var list = new JArray();
        foreach (var s in GitHubView.Sources)
            list.Add(new JObject { ["id"] = s.id, ["name"] = s.name, ["description"] = s.description });
        if (list.Count == 0)
            return new JObject { ["note"] = "No repos found. The hub needs GITHUB_TOKEN in .env.", ["repos"] = list };
        return new JObject
        {
            ["repos"] = list,
            ["current"] = GitHubView.CurrentSource?.id,
            ["note"] = $"Showing {list.Count} repo{(list.Count == 1 ? "" : "s")}.",
        };
    }

    static JObject OpenRepo(JObject args)
    {
        string id = (string)args["repo"] ?? "";
        if (!GitHubView.SelectSourceById(id))
            throw new System.ArgumentException($"Repo '{id}' not found. Use github_list_repos to see what's available.");
        OpenView();
        return new JObject { ["opened"] = GitHubView.CurrentSource.id, ["note"] = "Repo opened in the code panel." };
    }

    static JObject OpenFile(JObject args)
    {
        string path = ((string)args["path"] ?? "").Trim();
        if (path.Length == 0) throw new System.ArgumentException("path is required");
        OpenView();
        GitHubView.OpenFile(path);
        return new JObject { ["opening"] = path, ["note"] = "File is loading in the code panel." };
    }

    static JObject ListPrs()
    {
        OpenView();
        GitHubView.FetchPrs();
        var list = new JArray();
        foreach (var pr in GitHubView.Prs)
            list.Add(new JObject
            {
                ["number"] = pr.number,
                ["title"] = pr.title,
                ["author"] = pr.author,
                ["additions"] = pr.additions,
                ["deletions"] = pr.deletions,
                ["changed_files"] = pr.changedFiles,
            });
        return new JObject
        {
            ["prs"] = list,
            ["repo"] = GitHubView.CurrentSource?.id,
            ["note"] = list.Count == 0 ? "No open pull requests." : $"{list.Count} open PR{(list.Count == 1 ? "" : "s")}.",
        };
    }

    static JObject ShowPr(JObject args)
    {
        int number = args.Value<int?>("number") ?? 0;
        if (number <= 0) throw new System.ArgumentException("number must be a positive integer PR number");
        OpenView();
        GitHubView.FetchPr(number);
        return new JObject
        {
            ["loading_pr"] = number,
            ["repo"] = GitHubView.CurrentSource?.id,
            ["note"] = $"Loading PR #{number} — the diff appears in the code panel.",
        };
    }

    static JObject ClosePr()
    {
        GitHubView.ClosePr();
        return new JObject { ["note"] = "Back to file view." };
    }

    static void OpenView()
    {
        var ws = Workspace.Current;
        if (ws != null && ws.ActiveMode != "code" && ws.presence != null && ws.presence.AtDesk)
            ws.Open("code");
    }
}
