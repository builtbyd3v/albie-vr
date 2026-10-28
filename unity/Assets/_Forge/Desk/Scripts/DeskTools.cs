using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie's desk tools with the hub off: the timer_ and notes_ tools of hub/desk/tools.py, run on the device's copy in
/// <see cref="DeskApi"/>. Definitions: <c>Resources/AlbieTools/desk.json</c>. The movie tools stay hub-only (the
/// movies are files on the laptop).
/// </summary>
public static class DeskTools
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register() => AlbieTools.Register("desk", new Dictionary<string, AlbieTools.Handler>
    {
        ["timer_start"] = TimerStart,
        ["timer_control"] = TimerControl,
        ["timer_status"] = _ => TimerStatus(),
        ["notes_add"] = NotesAdd,
        ["notes_list"] = _ => new JObject { ["notes"] = new JArray(DeskApi.Notes.ConvertAll(Note).ToArray()), ["panel"] = DeskApi.NotesWidget },
        ["notes_edit"] = NotesEdit,
        ["notes_remove"] = NotesRemove,
        ["notes_view"] = NotesView,
    });

    // ---------------------------------------------------------------- timers

    static JObject TimerStart(JObject args)
    {
        float seconds = Num(args, "minutes") * 60f;
        if (seconds <= 0f || seconds > DeskApi.MaxTimerSeconds)
            throw new ArgumentException($"a timer runs between 1 second and {DeskApi.MaxTimerSeconds / 3600} hours");
        if (DeskApi.TimerStates().Count >= DeskApi.MaxTimers)
            throw new ArgumentException($"{DeskApi.MaxTimers} timers are running already; stop one first");
        var timer = DeskApi.TimerStart(Mathf.Round(seconds * 10f) / 10f, (string)args["label"]);
        var result = Timer(timer);
        result["timers"] = DeskApi.TimerStates().Count;
        return result;
    }

    static JObject TimerControl(JObject args)
    {
        string action = (string)args["action"] ?? "";
        if (Array.IndexOf(new[] { "pause", "resume", "add", "stop" }, action) < 0) throw new ArgumentException($"unknown timer action '{action}'");
        float minutes = action == "add" ? Num(args, "minutes") : 0f;
        var picked = DeskApi.TimerPick((string)args["timer"], action);
        if (action == "stop")
        {
            var stopped = new JArray();
            foreach (var t in picked)
            {
                DeskApi.TimerStop(t.timerId);
                stopped.Add(t.label);
            }
            return new JObject { ["stopped"] = stopped, ["timers"] = DeskApi.TimerStates().Count };
        }
        var timer = picked[0];
        if (action == "pause" && timer.status != "running") throw new ArgumentException($"the {timer.label} timer isn't running ({timer.status})");
        if (action == "resume" && timer.status != "paused") throw new ArgumentException($"the {timer.label} timer isn't paused ({timer.status})");
        if (action == "pause") DeskApi.TimerPause(timer.timerId);
        else if (action == "resume") DeskApi.TimerResume(timer.timerId);
        else DeskApi.TimerAdd(timer.timerId, minutes);
        return Timer(DeskApi.TimerStates().Find(t => t.timerId == timer.timerId));
    }

    static JObject TimerStatus()
    {
        var timers = DeskApi.TimerStates();
        var result = new JObject { ["timers"] = new JArray(timers.ConvertAll(Timer).ToArray()) };
        if (timers.Count == 0) result["note"] = "no timer is set";
        return result;
    }

    static JObject Timer(DeskTimer t) => new JObject
    {
        ["name"] = t.label, ["status"] = t.status, ["left"] = DeskUi.Human(t.remainingS),
        ["remaining_s"] = t.remainingS, ["length"] = DeskUi.Human(t.durationS),
    };

    // ---------------------------------------------------------------- notes

    static JObject NotesAdd(JObject args)
    {
        string text = ((string)args["text"] ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("the note is empty");
        DeskApi.NoteAdd(text);
        return new JObject { ["added"] = Note(DeskApi.Notes[0]), ["count"] = DeskApi.Notes.Count };
    }

    static JObject NotesEdit(JObject args)
    {
        var note = DeskApi.NoteFind((string)args["note"]);
        if (args["text"] != null && args["text"].Type != JTokenType.Null) DeskApi.NoteEdit(note.id, (string)args["text"]);
        if (args["done"] != null && args["done"].Type != JTokenType.Null) DeskApi.NoteDone(note.id, (bool)args["done"]);
        return new JObject { ["edited"] = Note(note) };
    }

    static JObject NotesRemove(JObject args)
    {
        if (args["undo"] != null && args["undo"].Type == JTokenType.Boolean && (bool)args["undo"])
        {
            if (DeskApi.LastRemovedNote == null) throw new ArgumentException("no note was removed");
            var back = DeskApi.LastRemovedNote;
            DeskApi.NoteUndo();
            return new JObject { ["restored"] = Note(back) };
        }
        var note = DeskApi.NoteFind((string)args["note"]);
        DeskApi.NoteRemove(note.id);
        return new JObject { ["removed"] = Note(note), ["undo"] = "notes_remove with undo=true brings it back" };
    }

    static JObject NotesView(JObject args)
    {
        string state = (string)args["state"] ?? "";
        if (Array.IndexOf(new[] { "open", "minimized", "closed" }, state) < 0) throw new ArgumentException($"unknown panel state '{state}'");
        DeskApi.SetWidget("notes", state);
        return new JObject { ["panel"] = DeskApi.NotesWidget, ["count"] = DeskApi.Notes.Count };
    }

    static JObject Note(DeskNote n) => new JObject { ["id"] = n.id, ["text"] = n.text, ["done"] = n.done };

    static float Num(JObject args, string key)
    {
        var value = args[key];
        if (value == null || value.Type == JTokenType.Null) throw new ArgumentException($"{key} is missing");
        return value.Value<float>();
    }
}
