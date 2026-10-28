using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Everything the desk panels change goes through here. Hub live: a POST (hub README "Desk"), and the hub answers with
/// the event that redraws the panels. Hub off (mocks): the same change made on a local copy of the hub's state and
/// raised as that event (<see cref="ForgeNet.Emit"/>), so the sandbox and desktop mode work without it; timers even
/// ring. <see cref="DeskExtras"/> keeps the copy in step with what arrives (<see cref="Seen"/>).
/// </summary>
public static class DeskApi
{
    static UiWidgets widgets = new UiWidgets();
    static readonly List<DeskTimer> timers = new List<DeskTimer>();
    static float timersAt;                      // realtimeSinceStartup when `timers` was true
    static NotesUpdated notes = new NotesUpdated();
    static MovieState movie = new MovieState();
    static float movieAt;
    static int mockIds;

    static bool Live => ForgeNet.Status == ForgeNetStatus.Live;
    static float Now => Time.realtimeSinceStartup;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        widgets = new UiWidgets();
        timers.Clear();
        notes = new NotesUpdated();
        movie = new MovieState();
    }

    // ---------------------------------------------------------------- the local copy

    public static void Seen(UiWidgets value) => widgets = value ?? new UiWidgets();

    public static void Seen(TimersUpdated value)
    {
        timers.Clear();
        if (value?.timers != null)
            foreach (var t in value.timers) timers.Add(Copy(t));
        timersAt = Now;
    }

    public static void Seen(NotesUpdated value) => notes = value ?? new NotesUpdated();

    public static void Seen(MovieState value)
    {
        movie = value ?? new MovieState();
        movieAt = Now;
    }

    // ---------------------------------------------------------------- widgets

    /// <summary>Open, minimize or close one: "timer", "notes" or "movie". Closing the timers stops them all.</summary>
    public static void SetWidget(string widget, string state)
    {
        if (Live)
        {
            ForgeNet.Post("/ui/widgets", new Dictionary<string, string> { [widget] = state });
            return;
        }
        if (widget == "timer" && state == "closed" && timers.Count > 0)
        {
            timers.Clear();
            EmitTimers();
        }
        if (widget == "movie" && state == "closed" && movie.status != "closed")
        {
            movie.positionS = MoviePosition();
            movie.status = "closed";
            EmitMovie();
        }
        bool hasContent = widget == "timer" ? timers.Count > 0 : widget == "movie" ? movie.status != "closed" : true;
        if (state != "closed" && !hasContent) return;
        if (widget == "timer") widgets.timer = state;
        else if (widget == "notes") widgets.notes = state;
        else if (widget == "movie") widgets.movie = state;
        ForgeNet.Emit(ForgeEvents.UiWidgetsType, widgets);
    }

    // ---------------------------------------------------------------- timers

    public static void TimerPause(string id) => Timer(id, "pause", t =>
    {
        if (t.status != "running") return false;
        t.status = "paused";
        return true;
    });

    public static void TimerResume(string id) => Timer(id, "resume", t =>
    {
        if (t.status != "paused") return false;
        t.status = "running";
        return true;
    });

    /// <summary>More time; on a timer that rang it runs again for that long.</summary>
    public static void TimerAdd(string id, float minutes) => Timer(id, "add", t =>
    {
        float add = minutes * 60f;
        if (t.status == "ringing")
        {
            t.status = "running";
            t.remainingS = add;
        }
        else t.remainingS = Mathf.Max(1f, t.remainingS + add);
        t.durationS = Mathf.Max(t.remainingS, t.durationS + add);
        return true;
    }, new { minutes });

    /// <summary>Cancel it, or turn off its ring.</summary>
    public static void TimerStop(string id)
    {
        if (Live)
        {
            ForgeNet.Post($"/timers/{id}/stop", null);
            return;
        }
        Advance();
        if (timers.RemoveAll(t => t.timerId == id) == 0) return;
        EmitTimers();
    }

    /// <summary>A new timer (the sandbox's keys; Bit starts them through the hub).</summary>
    public static DeskTimer TimerStart(float seconds, string label)
    {
        if (Live)
        {
            ForgeNet.Post("/timers", new { seconds, label });
            return null;
        }
        Advance();
        var timer = new DeskTimer { timerId = "m" + (++mockIds), label = TimerName(label), status = "running", durationS = seconds, remainingS = seconds };
        timers.Add(timer);
        EmitTimers();
        return Copy(timer);
    }

    static void Timer(string id, string action, System.Func<DeskTimer, bool> change, object body = null)
    {
        if (Live)
        {
            ForgeNet.Post($"/timers/{id}/{action}", body);
            return;
        }
        Advance();
        var timer = timers.Find(t => t.timerId == id);
        if (timer == null || !change(timer)) return;
        EmitTimers();
    }

    /// <summary>Hub off: ring the timers whose time is up, as the hub would. Call every frame.</summary>
    public static void Tick()
    {
        if (Live || ForgeNet.Status != ForgeNetStatus.Mock || timers.Count == 0) return;
        float elapsed = Now - timersAt;
        DeskTimer rang = null;
        foreach (var t in timers)
            if (t.status == "running" && t.remainingS - elapsed <= 0f) rang = t;
        if (rang == null) return;
        Advance();
        rang.status = "ringing";
        rang.remainingS = 0f;
        EmitTimers();
        string line = rang.label.ToLowerInvariant() == "focus"
            ? $"Time's up! That's {DeskUi.Human(rang.durationS)} of focus."
            : $"Time's up! Your {rang.label} timer is done.";
        ForgeNet.Emit(ForgeEvents.PetSayType, new PetSay { petId = "bit", text = line });
        if (widgets.timer == "minimized") SetWidget("timer", "open");
    }

    // Count the running timers down to now.
    static void Advance()
    {
        float elapsed = Now - timersAt;
        foreach (var t in timers)
            if (t.status == "running") t.remainingS = Mathf.Max(0f, t.remainingS - elapsed);
        timersAt = Now;
    }

    // As the hub sends them: ringing first, then by time left; the widget closes with the last one and opens with
    // the first.
    static void EmitTimers()
    {
        timers.Sort((a, b) =>
        {
            int ra = a.status == "ringing" ? 0 : 1, rb = b.status == "ringing" ? 0 : 1;
            return ra != rb ? ra.CompareTo(rb) : a.remainingS.CompareTo(b.remainingS);
        });
        var payload = new TimersUpdated();
        foreach (var t in timers) payload.timers.Add(Copy(t));
        ForgeNet.Emit(ForgeEvents.TimersUpdatedType, payload);
        string want = timers.Count == 0 ? "closed" : widgets.timer == "closed" ? "open" : widgets.timer;
        if (want != widgets.timer)
        {
            widgets.timer = want;
            ForgeNet.Emit(ForgeEvents.UiWidgetsType, widgets);
        }
    }

    static DeskTimer Copy(DeskTimer t) =>
        new DeskTimer { timerId = t.timerId, label = t.label, status = t.status, durationS = t.durationS, remainingS = t.remainingS };

    // ---------------------------------------------------------------- notes

    public static void NoteDone(string id, bool done)
    {
        if (Live)
        {
            ForgeNet.Post($"/notes/{id}/edit", new { done });
            return;
        }
        var note = notes.notes.Find(n => n.id == id);
        if (note == null) return;
        note.done = done;
        NotesChanged();
    }

    public static void NoteRemove(string id)
    {
        if (Live)
        {
            ForgeNet.Post($"/notes/{id}/remove", null);
            return;
        }
        var note = notes.notes.Find(n => n.id == id);
        if (note == null) return;
        notes.notes.Remove(note);
        notes.lastRemoved = note;
        NotesChanged();
    }

    public static void NoteUndo()
    {
        if (Live)
        {
            ForgeNet.Post("/notes/undo", null);
            return;
        }
        if (notes.lastRemoved == null) return;
        notes.notes.Add(notes.lastRemoved);
        notes.notes.Sort((a, b) => b.createdAt.CompareTo(a.createdAt));
        notes.lastRemoved = null;
        NotesChanged();
    }

    /// <summary>A new note (the sandbox's keys; Bit writes them through the hub).</summary>
    public static void NoteAdd(string text)
    {
        if (Live)
        {
            ForgeNet.Post("/notes", new { text });
            return;
        }
        double now = (System.DateTime.UtcNow - new System.DateTime(1970, 1, 1)).TotalSeconds;
        // Unique across restarts: saved notes come back with their ids (LoadSavedNotes).
        notes.notes.Insert(0, new DeskNote { id = System.Guid.NewGuid().ToString("N").Substring(0, 8), text = text, createdAt = now, updatedAt = now });
        NotesChanged();
        if (widgets.notes == "closed") SetWidget("notes", "open");
    }

    // Hub off, the notes live on the device: saved on every change, read back by DeskExtras at start.
    static string NotesFile => System.IO.Path.Combine(Application.persistentDataPath, "notes.json");

    static void NotesChanged()
    {
        ForgeNet.Emit(ForgeEvents.NotesUpdatedType, notes);
        try { System.IO.File.WriteAllText(NotesFile, Newtonsoft.Json.JsonConvert.SerializeObject(notes)); }
        catch (System.Exception e) { Debug.LogWarning($"[DeskApi] couldn't save the notes: {e.Message}"); }
    }

    public static bool LoadSavedNotes(out NotesUpdated saved)
    {
        saved = null;
        try
        {
            if (System.IO.File.Exists(NotesFile))
                saved = Newtonsoft.Json.JsonConvert.DeserializeObject<NotesUpdated>(System.IO.File.ReadAllText(NotesFile));
        }
        catch (System.Exception e) { Debug.LogWarning($"[DeskApi] couldn't read the saved notes: {e.Message}"); }
        return saved != null;
    }

    // ---------------------------------------------------------------- hub off: what Albie's tools read (DeskTools)

    public const int MaxTimers = 5;
    public const float MaxTimerSeconds = 10 * 3600;

    public static List<DeskTimer> TimerStates()
    {
        Advance();
        return timers.ConvertAll(Copy);
    }

    /// <summary>A timer by id or name (or part of one), as the hub's desk/timer.py finds it.</summary>
    public static DeskTimer TimerFind(string reference)
    {
        string r = (reference ?? "").Trim();
        var byId = timers.Find(t => t.timerId == r);
        if (byId != null) return byId;
        string words = r.ToLowerInvariant();
        var hits = timers.FindAll(t => t.label.ToLowerInvariant() == words);
        if (hits.Count == 0 && words.Length > 0) hits = timers.FindAll(t => t.label.ToLowerInvariant().Contains(words));
        if (hits.Count == 1) return hits[0];
        if (timers.Count == 0) throw new System.ArgumentException("no timer is set");
        if (hits.Count == 0) throw new System.ArgumentException($"no timer called '{r}'");
        throw new System.ArgumentException("which one? " + TimerNames(hits));
    }

    /// <summary>The timers an action means: by name, "all", or with none the only one it can apply to (stop: the ringing ones).</summary>
    public static List<DeskTimer> TimerPick(string reference, string action)
    {
        Advance();
        if (!string.IsNullOrWhiteSpace(reference))
            return reference.Trim().ToLowerInvariant() == "all" ? new List<DeskTimer>(timers) : new List<DeskTimer> { TimerFind(reference) };
        string[] fits = action == "pause" ? new[] { "running" } : action == "resume" ? new[] { "paused" }
            : action == "stop" ? new[] { "ringing" } : new[] { "running", "paused", "ringing" };
        var hits = timers.FindAll(t => System.Array.IndexOf(fits, t.status) >= 0);
        if (action == "stop" && hits.Count > 0) return hits;
        if (action == "stop") hits = new List<DeskTimer>(timers);
        if (hits.Count == 1) return hits;
        if (timers.Count == 0) throw new System.ArgumentException("no timer is set");
        if (hits.Count == 0) throw new System.ArgumentException($"no timer to {action}");
        throw new System.ArgumentException("which one? " + TimerNames(hits));
    }

    /// <summary>The label as given ("Focus" if none), made unique: a second "tea" is "tea 2".</summary>
    public static string TimerName(string label)
    {
        string trimmed = string.Join(" ", (label ?? "").Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries));
        string name = trimmed.Length == 0 ? "Focus" : trimmed.Length > 40 ? trimmed.Substring(0, 40) : trimmed;
        string unique = name;
        for (int n = 2; timers.Exists(t => t.label.ToLowerInvariant() == unique.ToLowerInvariant()); n++) unique = $"{name} {n}";
        return unique;
    }

    static string TimerNames(List<DeskTimer> list) =>
        string.Join(", ", list.ConvertAll(t => $"{t.label} ({DeskUi.Human(t.remainingS)} left)"));

    public static List<DeskNote> Notes => notes.notes;

    public static string NotesWidget => widgets.notes;

    public static DeskNote LastRemovedNote => notes.lastRemoved;

    /// <summary>A note by id, or by a few words of its text ("the one about the demo").</summary>
    public static DeskNote NoteFind(string reference)
    {
        string r = (reference ?? "").Trim();
        var byId = notes.notes.Find(n => n.id == r);
        if (byId != null) return byId;
        string words = r.ToLowerInvariant();
        var hits = words.Length == 0 ? new List<DeskNote>() : notes.notes.FindAll(n => n.text.ToLowerInvariant().Contains(words));
        if (hits.Count == 1) return hits[0];
        if (hits.Count == 0) throw new System.ArgumentException($"no note matches '{r}'");
        throw new System.ArgumentException($"{hits.Count} notes match '{r}': " +
            string.Join("; ", hits.GetRange(0, Mathf.Min(5, hits.Count)).ConvertAll(n => n.text.Length > 40 ? n.text.Substring(0, 40) : n.text)));
    }

    public static void NoteEdit(string id, string text)
    {
        var note = notes.notes.Find(n => n.id == id);
        if (note == null || string.IsNullOrWhiteSpace(text)) return;
        note.text = text.Trim();
        note.updatedAt = (System.DateTime.UtcNow - new System.DateTime(1970, 1, 1)).TotalSeconds;
        NotesChanged();
    }

    // ---------------------------------------------------------------- movie

    public static void MoviePlay(MovieInfo info)
    {
        if (Live)
        {
            ForgeNet.Post("/movie/play", new { movie_id = info?.movieId });
            return;
        }
        if (info != null && (movie.movie == null || movie.movie.movieId != info.movieId))
        {
            movie.movie = info;
            movie.positionS = 0f;
            movie.seekRev++;
        }
        if (movie.movie == null) return;
        if (movie.status == "closed") movie.seekRev++;  // back where it was
        MovieSet("playing");
    }

    public static void MoviePause() => MovieAction("/movie/pause", null, () => MovieSet("paused"));
    public static void MovieResume() => MovieAction("/movie/resume", null, () => MovieSet("playing"));
    public static void MovieClose() => MovieAction("/movie/close", null, () =>
    {
        MovieSet("closed");
        SetWidget("movie", "closed");
    });

    public static void MovieSeek(float position) => MovieAction("/movie/seek", new { position_s = position }, () => Jump(position));
    public static void MovieSkip(float seconds) => MovieAction("/movie/seek", new { delta_s = seconds }, () => Jump(MoviePosition() + seconds));

    public static void MovieVolume(float volume) => MovieAction("/movie/volume", new { volume = Mathf.Clamp01(volume) }, () =>
    {
        movie.positionS = MoviePosition();
        movieAt = Now;
        movie.volume = Mathf.Clamp01(volume);
        EmitMovie();
    });

    /// <summary>The player says where it is (every few seconds, on pause, at the end). No event back.</summary>
    public static void MovieReport(float position, float duration, bool ended)
    {
        if (Live)
        {
            ForgeNet.Post("/movie/position", new { position_s = Mathf.Max(0f, position), duration_s = duration > 0f ? duration : (float?)null, ended });
            return;
        }
        if (movie.status == "closed") return;
        movie.positionS = position;
        movieAt = Now;
        if (ended && movie.status == "playing") MovieSet("paused");
    }

    static void MovieAction(string path, object body, System.Action local)
    {
        if (Live)
        {
            ForgeNet.Post(path, body);
            return;
        }
        if (movie.movie == null || (movie.status == "closed" && path != "/movie/resume")) return;
        local();
    }

    static void MovieSet(string status)
    {
        movie.positionS = MoviePosition();
        movieAt = Now;
        movie.status = status;
        EmitMovie();
        if (status != "closed" && widgets.movie == "closed") SetWidget("movie", "open");
    }

    static void Jump(float position)
    {
        float length = movie.movie?.durationS ?? float.MaxValue;
        movie.positionS = Mathf.Clamp(position, 0f, length);
        movieAt = Now;
        movie.seekRev++;
        EmitMovie();
    }

    static float MoviePosition() => movie.positionS + (movie.status == "playing" ? Now - movieAt : 0f);

    static void EmitMovie() => ForgeNet.Emit(ForgeEvents.MovieStateType, movie);
}
