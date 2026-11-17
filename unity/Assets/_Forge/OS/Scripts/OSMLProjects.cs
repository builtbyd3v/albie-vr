using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// The "Machine learning" section of the Projects tab. It lists every ML project the hub or Albie's cloud has for
    /// this headset (<c>GET /ml/projects</c>): the ones Albie built from a Hugging Face dataset (<c>ml_new</c>), the
    /// repos he cloned from GitHub (<c>ml_github</c>) and the example, each with its status and last score. Tapping
    /// one opens it on the ML desk (<c>ml_open</c>). Under them are one-tap starters that build a new project with
    /// no typing (<c>ml_new</c>), and Import from GitHub, which opens the ML desk's repository field and presets.
    /// The list is fetched only when the hub or the cloud serves <c>/ml/</c>, never from mocks, and at most every
    /// <see cref="RefreshSeconds"/>; each tap's outcome shows on its tile for a while.
    /// </summary>
    public static class OSMLProjects
    {
        public const string Section = "Machine learning";
        const float RefreshSeconds = 10f;
        const float NoticeSeconds = 12f;

        sealed class Starter
        {
            public string id, title, line;
            public JObject args;
        }

        // What a voice request would send for these, so a tap and "Albie, fine-tune DistilBERT on movie reviews" match.
        static readonly Starter[] starters =
        {
            new Starter { id = "ml_new_distilbert", title = "Fine-tune DistilBERT", line = "on movie reviews, from Hugging Face",
                args = new JObject { ["request"] = "fine-tune distilbert on movie reviews", ["model"] = "hf",
                    ["base_model"] = "distilbert-base-uncased", ["task"] = "text_classification" } },
            new Starter { id = "ml_new_rnn", title = "RNN on movie reviews", line = "an LSTM trained from scratch on IMDb",
                args = new JObject { ["request"] = "train an RNN on movie reviews", ["model"] = "lstm", ["task"] = "text_classification" } },
            new Starter { id = "ml_new_cnn", title = "CNN on handwritten digits", line = "MNIST, trained from scratch",
                args = new JObject { ["request"] = "build a CNN on handwritten digits", ["model"] = "cnn", ["task"] = "image_classification" } },
        };

        const string ImportId = "ml_import_github";

        sealed class Card
        {
            public string name, title, kind, status, line;
            public bool open, training;
            public float best = -1f;
            public int runs;
        }

        static readonly List<Card> cards = new List<Card>();
        static readonly List<OSEntry> entries = new List<OSEntry>();
        static readonly Dictionary<string, string> notices = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly Dictionary<string, float> noticeAt = new Dictionary<string, float>(StringComparer.Ordinal);
        static float lastFetch = -1000f;
        static bool fetching, built;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            cards.Clear();
            entries.Clear();
            notices.Clear();
            noticeAt.Clear();
            lastFetch = -1000f;
            fetching = built = false;
        }

        /// <summary>Adds the section to the Projects tab, after the projects saved on the headset.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void Register()
        {
            OSRegistry.UnregisterSource(Fill);
            OSRegistry.RegisterSource(OSTab.Projects, Fill);
        }

        public static void Unregister() => OSRegistry.UnregisterSource(Fill);

        /// <summary>Whether the hub or Albie's cloud can run ML for real right now.</summary>
        static bool Live => ForgeCloud.Serves("/ml/");

        static void Fill(List<OSEntry> into)
        {
            if (Live && !fetching && Time.realtimeSinceStartup - lastFetch > RefreshSeconds) Fetch();
            if (!built) Build();
            into.AddRange(entries);
        }

        /// <summary>Reads the list again on the next look (after a tap that makes or opens a project).</summary>
        public static void Refresh() => lastFetch = -1000f;

        static void Fetch()
        {
            fetching = true;
            lastFetch = Time.realtimeSinceStartup;
            ForgeNet.Get("/ml/projects", text =>
            {
                fetching = false;
                if (Parse(text)) { built = false; OSRegistry.NotifyChanged(); }
            }, error =>
            {
                fetching = false;
                Debug.Log("[OS] Couldn't list the ML projects: " + error);
            });
        }

        /// <summary>Reads a <c>GET /ml/projects</c> answer into the cards; true when something changed. Checks call it directly.</summary>
        public static bool Parse(string json)
        {
            JArray list;
            try { list = JObject.Parse(json)["projects"] as JArray; }
            catch (Exception) { return false; }
            if (list == null) return false;
            var next = new List<Card>(list.Count);
            foreach (var item in list)
            {
                if (!(item is JObject o) || string.IsNullOrEmpty((string)o["name"])) continue;
                next.Add(new Card
                {
                    name = (string)o["name"],
                    title = (string)o["title"] ?? (string)o["name"],
                    kind = (string)o["kind"] ?? "example",
                    status = (string)o["status"] ?? "",
                    open = o.Value<bool?>("open") == true,
                    training = o.Value<bool?>("training") == true,
                    best = o["best_metric"] != null && o["best_metric"].Type != JTokenType.Null ? o.Value<float>("best_metric") : -1f,
                    runs = o.Value<int?>("runs") ?? 0,
                });
            }
            bool same = next.Count == cards.Count;
            for (int i = 0; same && i < next.Count; i++)
            {
                var a = next[i]; var b = cards[i];
                same = a.name == b.name && a.title == b.title && a.status == b.status && a.open == b.open
                       && a.training == b.training && a.best == b.best && a.runs == b.runs;
            }
            if (same) return false;
            cards.Clear();
            cards.AddRange(next);
            return true;
        }

        static void Build()
        {
            built = true;
            entries.Clear();
            foreach (var card in cards)
            {
                var c = card;
                string id = "ml_project:" + c.name;
                c.line = StatusLine(c);  // built once here: the menu reads status lines every frame
                entries.Add(new OSEntry
                {
                    id = id,
                    title = c.title,
                    icon = c.kind == "github" ? "repo" : "classifier",
                    tab = OSTab.Projects,
                    section = Section,
                    status = () => NoticeOr(id, c.line),
                    isOpen = () => c.open,
                    open = () => OpenProject(id, c.name),
                });
            }
            foreach (var starter in starters)
            {
                var s = starter;
                entries.Add(new OSEntry
                {
                    id = s.id,
                    title = s.title,
                    icon = "plus",
                    tab = OSTab.Projects,
                    section = Section,
                    outline = true,
                    status = () => NoticeOr(s.id, Live ? s.line : OSProjects.NeedsCloud),
                    open = () => New(s),
                });
            }
            entries.Add(new OSEntry
            {
                id = ImportId,
                title = "Import from GitHub",
                icon = "repo",
                tab = OSTab.Projects,
                section = Section,
                outline = true,
                status = () => NoticeOr(ImportId, Live ? "or say \"Albie, pull the pytorch cifar repo\"" : OSProjects.NeedsCloud),
                open = Import,
            });
        }

        /// <summary>The line under a project's tile: training, setting up, its score, or where it came from.</summary>
        static string StatusLine(Card c)
        {
            if (c.training) return "training now";
            string from = c.kind == "github" ? "from GitHub" : c.kind == "new" ? "built by Albie" : "example";
            switch (c.status)
            {
                case "scanning": case "classifying": case "adapting": case "building": return "setting up · " + from;
                case "needs_approval": return "waiting for you to accept setup";
                case "failed": return "setup failed · " + from;
            }
            if (c.best >= 0f) return $"{c.best * 100f:0.#}% · {from}";
            return c.runs > 0 ? from : "not trained yet · " + from;
        }

        static void OpenProject(string id, string name)
        {
            if (!Live) { Notice(id, OSProjects.NeedsCloud); return; }
            Notice(id, "opening on the ML desk...");
            AlbieTools.CallAsync("ml_open", new JObject { ["project"] = name }, result =>
            {
                string error = result != null ? (string)result["error"] : "no answer";
                Notice(id, error != null ? OSProjects.Plain(error) : "open on the ML desk");
                Refresh();
            });
        }

        static void New(Starter s)
        {
            if (!Live) { Notice(s.id, OSProjects.NeedsCloud); return; }
            Notice(s.id, "asking Albie to build it...");
            AlbieTools.CallAsync("ml_new", (JObject)s.args.DeepClone(), result =>
            {
                string error = result != null ? (string)result["error"] : "no answer";
                string building = result != null ? (string)result["building"] : null;
                Notice(s.id, error != null ? OSProjects.Plain(error)
                    : building != null ? "building " + building + "; the ML desk opens by itself" : "building it now");
                Refresh();
            });
        }

        static void Import()
        {
            if (!Live) { Notice(ImportId, OSProjects.NeedsCloud); return; }
            // The ML desk has the repository field and one-tap GitHub presets (TrainingDash); open it there.
            OSEntry tool = OSProjects.FindTool("train", "ml");
            string error = null;
            bool opened = tool != null && OSProjects.OpenTool(tool, out error);
            Notice(ImportId, opened ? "paste a link or pick a preset on the ML desk" : error ?? "the ML desk couldn't open");
        }

        static void Notice(string id, string line)
        {
            notices[id] = line;
            noticeAt[id] = Time.realtimeSinceStartup;
            OSRegistry.NotifyChanged();
        }

        static string NoticeOr(string id, string otherwise) =>
            notices.TryGetValue(id, out string line) && Time.realtimeSinceStartup - noticeAt[id] < NoticeSeconds ? line : otherwise;
    }
}
