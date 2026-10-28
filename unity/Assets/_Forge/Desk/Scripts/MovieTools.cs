using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie's movie tools with the hub off: movie_play / control / marks / status of hub/desk/tools.py and the hub's
/// movie.py (places, marks found by their words, volume), run on <see cref="DeskApi"/>'s copy. Definitions:
/// <c>Resources/AlbieTools/movie.json</c>. The library is <c>Resources/Mocks/movie/library.json</c> (absolute https
/// URLs, so the headset streams with no laptop). Where each movie stopped, the volume and the marks live in
/// PlayerPrefs; the mock GET /movie answers with them, so the screen picks up there after a restart.
/// </summary>
public static class MovieTools
{
    const string Pref = "Albie.Movie";
    const float RestartNearEndS = 120f;  // a movie stopped in its last two minutes starts over next time
    const float MarkNowLeadS = 5f;       // "save this spot": the user spoke after it began, so step back a little
    static readonly HashSet<string> StopWords = new HashSet<string>(("a an and at back bit go in is it jump moment movie of on part " +
        "point scene skip spot that the this to when where with").Split(' '));

    class Mark { public string mark_id, label; public float position_s; }
    class Saved
    {
        public Dictionary<string, float> places = new Dictionary<string, float>();
        public float volume = 0.8f;
        public Dictionary<string, List<Mark>> marks = new Dictionary<string, List<Mark>>();
    }

    static Saved saved;
    static List<MovieInfo> library;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { saved = null; library = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register() => AlbieTools.Register("movie", new Dictionary<string, AlbieTools.Handler>
    {
        ["movie_play"] = Play,
        ["movie_control"] = Control,
        ["movie_marks"] = Marks,
        ["movie_status"] = _ => Status(),
    });

    // Before DeskExtras' Start asks for /movie: hub off, the state this device kept.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void MockRest()
    {
        // DeskBootstrap may add DeskExtras after this runs: a scene with a ForgeNet or a Workspace gets the mocks.
        if (UnityEngine.Object.FindAnyObjectByType<ForgeNet>() == null && UnityEngine.Object.FindAnyObjectByType<Workspace>() == null) return;
        ForgeNet.MockRest("/movie", _ =>
        {
            var movie = Library()[0];
            float place = Store.places.TryGetValue(movie.movieId, out var p) ? p : 0f;
            return JsonConvert.SerializeObject(new MovieState { status = "closed", movie = movie, positionS = place, volume = Store.volume });
        });
        ForgeNet.MockRest("/movie/library", _ => JsonConvert.SerializeObject(Library()));
    }

    /// <summary>DeskApi (hub off) after each change and report: remember where this movie is and the volume.</summary>
    public static void Keep(MovieState state, float position)
    {
        if (state?.movie == null) return;
        Store.places[state.movie.movieId] = Mathf.Round(position * 10f) / 10f;
        Store.volume = state.volume;
        PlayerPrefs.SetString(Pref, JsonConvert.SerializeObject(Store));
        if (state.status != "playing") PlayerPrefs.Save();  // reports while it plays are kept on quit / pause
    }

    // ---------------------------------------------------------------- tools

    static JObject Play(JObject args) => Movie(PlayMovie((string)args["title"], args["from_start"]?.Type == JTokenType.Boolean && (bool)args["from_start"]));

    static MovieState PlayMovie(string title, bool fromStart)
    {
        var movie = Find(title);
        var now = DeskApi.Movie;
        if (fromStart || now.movie == null || now.movie.movieId != movie.movieId || now.status == "closed")
        {
            float start = fromStart ? 0f : Store.places.TryGetValue(movie.movieId, out var p) ? p : 0f;
            if (movie.durationS.HasValue && start > movie.durationS.Value - RestartNearEndS) start = 0f;
            DeskApi.MoviePlay(movie, start);
        }
        else DeskApi.MovieResume();
        return DeskApi.Movie;
    }

    static JObject Control(JObject args)
    {
        string action = (string)args["action"] ?? "";
        switch (action)
        {
            case "pause": NeedOn(); DeskApi.MoviePause(); break;
            case "resume":
                if (DeskApi.Movie.movie == null || DeskApi.Movie.status == "closed") return Movie(PlayMovie(null, false));
                DeskApi.MovieResume();
                break;
            case "seek" when !string.IsNullOrWhiteSpace((string)args["mark"]):
                var (movieId, mark) = FindMark((string)args["mark"]);
                var on = DeskApi.Movie;
                DeskApi.MoviePlay(on.movie != null && on.movie.movieId == movieId ? on.movie : Find(movieId), mark.position_s);
                var result = Movie(DeskApi.Movie);
                result["mark"] = mark.label;
                return result;
            case "seek": NeedOn(); DeskApi.MovieSeek(Num(args, "seconds")); break;
            case "skip": NeedOn(); DeskApi.MovieSkip(Num(args, "seconds")); break;
            case "volume":
                NeedOn();  // ponytail: the hub also sets it with the screen closed; DeskApi's local copy only while it's on
                float level = args["by"] != null && args["by"].Type != JTokenType.Null
                    ? DeskApi.Movie.volume + Fraction(Num(args, "by"))
                    : Fraction(Num(args, "volume"));
                DeskApi.MovieVolume(level);
                break;
            case "close": NeedOn(); DeskApi.MovieClose(); break;
            default: throw new ArgumentException($"unknown movie action '{action}'");
        }
        return Movie(DeskApi.Movie);
    }

    static JObject Marks(JObject args)
    {
        string action = (string)args["action"] ?? "";
        if (action == "save")
        {
            string label = Regex.Replace(((string)args["label"] ?? "").Trim(), @"\s+", " ");
            if (label.Length == 0) throw new ArgumentException("say what happens there");
            var movie = Find((string)args["title"]);
            bool on = IsOn(movie.movieId);
            float position;
            if (args["seconds"] == null || args["seconds"].Type == JTokenType.Null)
            {
                if (!on) throw new ArgumentException($"{movie.title} isn't on: say when in it");
                position = DeskApi.MovieNow - MarkNowLeadS;
            }
            else position = Num(args, "seconds");
            position = Mathf.Clamp(position, 0f, movie.durationS ?? float.MaxValue);
            if (!Store.marks.TryGetValue(movie.movieId, out var marks)) Store.marks[movie.movieId] = marks = new List<Mark>();
            var mark = marks.Find(m => string.Equals(m.label, label, StringComparison.OrdinalIgnoreCase));
            if (mark == null) marks.Add(mark = new Mark { mark_id = NextMarkId(), label = label });
            mark.position_s = Mathf.Round(position * 10f) / 10f;
            marks.Sort((a, b) => a.position_s.CompareTo(b.position_s));
            Save();
            return new JObject { ["saved"] = MarkJson(mark), ["movie"] = movie.title };
        }
        if (action == "list")
        {
            var movie = Find((string)args["title"]);
            return new JObject { ["movie"] = movie.title, ["marks"] = MarksJson(movie.movieId) };
        }
        if (action == "remove")
        {
            var (movieId, mark) = FindMark((string)args["label"] ?? "");
            Store.marks[movieId].Remove(mark);
            if (Store.marks[movieId].Count == 0) Store.marks.Remove(movieId);
            Save();
            return new JObject { ["removed"] = MarkJson(mark) };
        }
        throw new ArgumentException($"unknown marks action '{action}'");
    }

    static JObject Status()
    {
        var output = Movie(DeskApi.Movie);
        var titles = new JArray();
        foreach (var m in Library()) titles.Add(m.title);
        output["library"] = titles;
        if ((string)output["status"] != "closed") output["marks"] = MarksJson(DeskApi.Movie.movie.movieId);
        return output;
    }

    // ---------------------------------------------------------------- the hub's movie.py, ported

    static List<MovieInfo> Library()
    {
        if (library != null) return library;
        var asset = Resources.Load<TextAsset>("Mocks/movie/library");
        library = asset != null ? JsonConvert.DeserializeObject<List<MovieInfo>>(asset.text) : new List<MovieInfo>();
        return library;
    }

    /// <summary>By id or title (or part of it); none = the current movie, else the first.</summary>
    static MovieInfo Find(string query)
    {
        var movies = Library();
        if (movies.Count == 0) throw new ArgumentException("there are no movies");
        string q = (query ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0)
        {
            var current = DeskApi.Movie.movie;
            return movies.Find(m => current != null && m.movieId == current.movieId) ?? movies[0];
        }
        var hit = movies.Find(m => m.movieId.ToLowerInvariant() == q || m.title.ToLowerInvariant() == q)
            ?? movies.Find(m => m.title.ToLowerInvariant().Contains(q) || m.movieId.ToLowerInvariant().Contains(q));
        if (hit == null) throw new ArgumentException($"no movie called '{query}'; there's " + string.Join(", ", movies.ConvertAll(m => m.title)));
        return hit;
    }

    /// <summary>By id, label, or the words of it ("where Hildy tears it up"). Ties go to the movie that's on.</summary>
    static (string, Mark) FindMark(string reference)
    {
        string q = Regex.Replace((reference ?? "").Trim(), @"\s+", " ").ToLowerInvariant();
        if (Store.marks.Count == 0) throw new ArgumentException("no saved spots yet");
        var ordered = new List<KeyValuePair<string, List<Mark>>>(Store.marks);
        string on = DeskApi.Movie.movie != null && DeskApi.Movie.status != "closed" ? DeskApi.Movie.movie.movieId : null;
        ordered.Sort((a, b) => (a.Key != on).CompareTo(b.Key != on));
        foreach (var kv in ordered)
            foreach (var m in kv.Value)
                if (q == m.mark_id || q == m.label.ToLowerInvariant()) return (kv.Key, m);
        var words = Words(q);
        (string, Mark) best = (null, null);
        int bestShared = 0;
        float bestShare = 0f;
        foreach (var kv in ordered)
            foreach (var m in kv.Value)
            {
                var label = Words(m.label);
                int shared = 0;
                foreach (var w in words) if (label.Contains(w)) shared++;
                float share = shared / (float)Mathf.Max(1, label.Count);
                if (shared > 0 && (shared > bestShared || (shared == bestShared && share > bestShare)))
                {
                    best = (kv.Key, m);
                    bestShared = shared;
                    bestShare = share;
                }
            }
        if (best.Item2 == null)
        {
            var all = new List<string>();
            foreach (var kv in ordered) foreach (var m in kv.Value) all.Add($"{m.label} ({Clock(m.position_s)})");
            throw new ArgumentException($"no saved spot like '{reference}'; saved: {string.Join(", ", all)}");
        }
        return best;
    }

    /// <summary>The words that tell marks apart, roughly stemmed ("builds", "building" -> "build").</summary>
    static HashSet<string> Words(string text)
    {
        var output = new HashSet<string>();
        foreach (Match match in Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]+"))
        {
            string w = match.Value;
            if (w.Length < 2 || StopWords.Contains(w)) continue;
            foreach (string suffix in new[] { "ing", "ed", "es", "s", "e" })
                if (w.EndsWith(suffix) && w.Length - suffix.Length >= 3)
                {
                    w = w.Substring(0, w.Length - suffix.Length);
                    break;
                }
            output.Add(w);
        }
        return output;
    }

    static string NextMarkId()
    {
        int max = 0;
        foreach (var marks in Store.marks.Values)
            foreach (var m in marks)
                if (m.mark_id.Length > 1 && int.TryParse(m.mark_id.Substring(1), out int n)) max = Mathf.Max(max, n);
        return "m" + (max + 1);
    }

    static bool IsOn(string movieId) => DeskApi.Movie.movie != null && DeskApi.Movie.movie.movieId == movieId && DeskApi.Movie.status != "closed";

    static void NeedOn()
    {
        if (DeskApi.Movie.movie == null || DeskApi.Movie.status == "closed") throw new ArgumentException("the movie isn't on");
    }

    // ---------------------------------------------------------------- storage and shapes

    static Saved Store
    {
        get
        {
            if (saved != null) return saved;
            try { saved = JsonConvert.DeserializeObject<Saved>(PlayerPrefs.GetString(Pref, "")); }
            catch (JsonException e) { Debug.LogWarning($"[MovieTools] saved movie state unreadable ({e.Message}); starting fresh"); }
            return saved ??= new Saved();
        }
    }

    static void Save()
    {
        PlayerPrefs.SetString(Pref, JsonConvert.SerializeObject(Store));
        PlayerPrefs.Save();
    }

    static JObject Movie(MovieState s)
    {
        if (s.movie == null || s.status == "closed") return new JObject { ["status"] = "closed", ["title"] = s.movie?.title };
        float at = DeskApi.MovieNow;
        var output = new JObject { ["status"] = s.status, ["title"] = s.movie.title, ["at"] = DeskUi.Human(at), ["volume"] = $"{Mathf.RoundToInt(s.volume * 100f)}%" };
        if (s.movie.durationS.HasValue) output["left"] = DeskUi.Human(s.movie.durationS.Value - at);
        return output;
    }

    static JArray MarksJson(string movieId)
    {
        var output = new JArray();
        if (Store.marks.TryGetValue(movieId, out var marks)) foreach (var m in marks) output.Add(MarkJson(m));
        return output;
    }

    static JObject MarkJson(Mark m) => new JObject { ["label"] = m.label, ["at"] = Clock(m.position_s) };

    /// <summary>'1:02:30' / '45:00': a spot in a movie.</summary>
    static string Clock(float seconds)
    {
        int s = Mathf.Max(0, Mathf.RoundToInt(seconds)), h = s / 3600, m = s / 60 % 60;
        return h > 0 ? $"{h}:{m:00}:{s % 60:00}" : $"{m}:{s % 60:00}";
    }

    /// <summary>A volume as 0..1; a model that says 20 for 20% gets 0.2.</summary>
    static float Fraction(float value) => Mathf.Abs(value) > 1f ? value / 100f : value;

    static float Num(JObject args, string key)
    {
        var value = args[key];
        if (value == null || value.Type == JTokenType.Null) throw new ArgumentException($"{key} is missing");
        return value.Value<float>();
    }
}
