using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Check ML Projects (or -executeMethod Forge.OS.Editor.OSMLProjectsCheck.RunBatch): the Projects
    /// tab's Machine learning section from a sample <c>GET /ml/projects</c> answer, with no hub and no cloud. Each project
    /// gets a tile with a true status line; offline the starters and Import from GitHub, which could only say they need
    /// the cloud, are one "New ML model" tile that says so. Console line: "[OSMLProjectsCheck] N passed, M failed".
    /// </summary>
    public static class OSMLProjectsCheck
    {
        const string Sample = @"{""projects"": [
            {""name"": ""template"", ""title"": ""template"", ""kind"": ""example"", ""status"": ""ready"", ""open"": true, ""training"": false, ""runs"": 2, ""best_metric"": 0.712},
            {""name"": ""hf-imdb-1117"", ""title"": ""DistilBERT fine-tuned on imdb"", ""kind"": ""new"", ""status"": ""ready"", ""open"": false, ""training"": false, ""runs"": 1, ""best_metric"": 0.874},
            {""name"": ""Sentiment-Analysis-IMDb-Reviews"", ""title"": ""Sentiment Analysis IMDb Reviews"", ""kind"": ""github"", ""status"": ""not set up"", ""open"": false, ""training"": false, ""runs"": 0, ""best_metric"": null},
            {""name"": ""lstm-imdb"", ""title"": ""LSTM text classifier on imdb"", ""kind"": ""new"", ""status"": ""ready"", ""open"": false, ""training"": true, ""runs"": 1, ""best_metric"": null}
        ]}";

        static int passed, failed;

        [MenuItem("Forge/OS/Check ML Projects")]
        public static void Menu() => Debug.Log(Run());

        public static void RunBatch()
        {
            string report = Run();
            Debug.Log(report);
            EditorApplication.Exit(failed == 0 ? 0 : 1);
        }

        public static string Run()
        {
            passed = failed = 0;
            OSMLProjects.Register();
            try
            {
                Check(OSMLProjects.Parse(Sample), "a /ml/projects answer is read");
                Check(!OSMLProjects.Parse(Sample), "the same answer again changes nothing, so the menu isn't rebuilt");
                Check(!OSMLProjects.Parse("not json"), "a broken answer is ignored");

                var tiles = new List<OSEntry>();
                OSRegistry.Fill(OSTab.Projects, tiles);
                var byId = new Dictionary<string, OSEntry>();
                // The section also holds the ML projects saved on the headset (OSProjects): count the cloud's tiles only.
                foreach (var t in tiles) if (t.section == OSMLProjects.Section && t.id.StartsWith("ml_", System.StringComparison.Ordinal)) byId[t.id] = t;

                // No hub and no cloud in this check: the three starters and Import can only say they need the cloud, so one tile says it.
                Check(byId.Count == 4 + 1, $"four projects and one offline 'New ML model' tile ({byId.Count} tiles)");
                Check(Status(byId, "ml_project:hf-imdb-1117") == "87.4% · built by Albie", "a built project shows its score");
                Check(Status(byId, "ml_project:Sentiment-Analysis-IMDb-Reviews") == "not trained yet · from source control", "a cloned repo says it hasn't trained");
                Check(Status(byId, "ml_project:lstm-imdb") == "training now", "a project that's training says so");
                Check(byId.TryGetValue("ml_project:template", out var open) && open.isOpen(), "the open project shows as open");
                Check(byId.TryGetValue("ml_project:Sentiment-Analysis-IMDb-Reviews", out var repo) && repo.icon == "repo", "a cloned repo has the repo icon");
                Check(byId.TryGetValue(OSMLProjects.OfflineId, out var offline) && offline.outline && offline.title == OSMLProjects.OfflineTitle,
                    "offline, one new-style tile stands in for the starters and Import");
                Check(offline != null && offline.status() == OSMLProjects.NeedsCloudShort, "it says it needs the cloud");
                foreach (var id in new[] { "ml_new_distilbert", "ml_new_rnn", "ml_new_cnn", "ml_import_github" })
                    Check(!byId.ContainsKey(id), id + " isn't listed offline (it could only say it needs the cloud)");
            }
            finally
            {
                OSMLProjects.Unregister();
            }
            return $"[OSMLProjectsCheck] {passed} passed, {failed} failed";
        }

        static string Status(Dictionary<string, OSEntry> tiles, string id) =>
            tiles.TryGetValue(id, out var t) && t.status != null ? t.status() : null;

        static void Check(bool ok, string what)
        {
            if (ok) passed++;
            else { failed++; Debug.LogError("[OSMLProjectsCheck] FAIL " + what); }
        }
    }
}
