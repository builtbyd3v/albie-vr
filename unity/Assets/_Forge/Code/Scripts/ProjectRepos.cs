using System.Collections.Generic;
using System.IO;
using Forge.Notebook;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Which repo each project shows in the code panel: project id -> repo ("owner/name", or just the name).
/// Bundled defaults come from Code/Resources/ProjectRepos.json; links made on the headset are saved over them in
/// persistentDataPath/code/project_repos.json. The project record (C-09, <c>repos[]</c>) takes this over when the
/// Project module exists. Main thread only.
/// </summary>
public static class ProjectRepos
{
    static Dictionary<string, string> links;

    static string SavePath => Path.Combine(Application.persistentDataPath, "code", "project_repos.json");

    /// <summary>The open project, or the demo project before the notebook has opened one.</summary>
    public static string CurrentProject => NotebookStore.ProjectId ?? NotebookStore.DefaultProject;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => links = null;

    /// <summary>The repo linked to <paramref name="projectId"/>, or null.</summary>
    public static string RepoFor(string projectId)
    {
        Load();
        return projectId != null && links.TryGetValue(projectId, out string repo) ? repo : null;
    }

    public static void Link(string projectId, string repo)
    {
        Load();
        links[projectId] = repo;
        Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
        File.WriteAllText(SavePath, JsonConvert.SerializeObject(links, Formatting.Indented));
    }

    static void Load()
    {
        if (links != null) return;
        links = new Dictionary<string, string>();
        var bundled = Resources.Load<TextAsset>("ProjectRepos");
        if (bundled != null) Merge(bundled.text);
        if (File.Exists(SavePath)) Merge(File.ReadAllText(SavePath));
    }

    static void Merge(string json)
    {
        try
        {
            var read = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            if (read == null) return;
            foreach (var pair in read) links[pair.Key] = pair.Value;
        }
        catch (JsonException e) { Debug.LogWarning("[ProjectRepos] ignoring unreadable links: " + e.Message); }
    }
}
