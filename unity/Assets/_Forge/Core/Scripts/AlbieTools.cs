using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie's tools on the headset, so he can act with no hub. Same shape as hub/agent/tools.py: each tool is
/// {name, description, parameters (JSON schema)} plus a handler. Modules register theirs from a JSON file in
/// <c>Resources/AlbieTools/</c> when Play starts. <see cref="Call"/> never throws: errors come back as
/// <c>{"error": ...}</c> so the model can explain them. A slow tool (a network call) registers an
/// <see cref="AsyncHandler"/> and answers later; <see cref="CallAsync"/> waits for it.
/// </summary>
public static class AlbieTools
{
    public delegate JObject Handler(JObject args);
    /// <summary>Calls <c>done</c> once, now or later (on the main thread), with the tool's result.</summary>
    public delegate void AsyncHandler(JObject args, Action<JObject> done);

    static readonly Dictionary<string, (JObject def, AsyncHandler run)> tools = new Dictionary<string, (JObject, AsyncHandler)>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => tools.Clear();

    /// <summary>Add the tools in <c>Resources/AlbieTools/&lt;file&gt;.json</c> (an array of definitions); each needs a handler.</summary>
    public static void Register(string file, Dictionary<string, Handler> handlers)
    {
        var wrapped = new Dictionary<string, AsyncHandler>();
        foreach (var pair in handlers)
        {
            var run = pair.Value;
            wrapped[pair.Key] = (args, done) => done(run(args));
        }
        Register(file, wrapped);
    }

    /// <summary><see cref="Register(string, Dictionary{string, Handler})"/> for slow tools.</summary>
    public static void Register(string file, Dictionary<string, AsyncHandler> handlers)
    {
        var asset = Resources.Load<TextAsset>("AlbieTools/" + file);
        if (asset == null)
        {
            Debug.LogError($"[AlbieTools] no Resources/AlbieTools/{file}.json");
            return;
        }
        foreach (JObject def in JArray.Parse(asset.text))
        {
            string name = (string)def["name"];
            if (handlers.TryGetValue(name, out var run)) tools[name] = (def, run);
            else Debug.LogWarning($"[AlbieTools] {name} has no handler; skipped.");
        }
    }

    public static bool Has(string name) => tools.ContainsKey(name);

    public static IEnumerable<string> Names => tools.Keys;

    /// <summary>Every tool as a realtime voice API function: {type: "function", name, description, parameters}.</summary>
    public static JArray Definitions()
    {
        var all = new JArray();
        foreach (var (def, _) in tools.Values)
        {
            // Keep the experimental controller handlers for sandboxes, without advertising them to voice.
            string name = (string)def["name"];
            if (name == "brain_record" || name == "brain_train" || name == "brain_status"
                || name == "autopilot_on" || name == "autopilot_off") continue;
            all.Add(new JObject { ["type"] = "function", ["name"] = def["name"], ["description"] = def["description"], ["parameters"] = def["parameters"] });
        }
        return all;
    }

    /// <summary>Run a tool now. A slow tool that hasn't answered yet returns {"ok", "pending"}; use <see cref="CallAsync"/>.</summary>
    public static JObject Call(string name, JObject args)
    {
        JObject result = null;
        CallAsync(name, args, r => result = r);
        return result ?? new JObject { ["ok"] = true, ["pending"] = true };
    }

    /// <summary>Run a tool; <paramref name="done"/> gets its result exactly once (an error too).</summary>
    public static void CallAsync(string name, JObject args, Action<JObject> done)
    {
        if (!tools.TryGetValue(name, out var tool))
        {
            done(Error($"Unknown tool '{name}'"));
            return;
        }
        bool answered = false;
        void Answer(JObject result)
        {
            if (answered) return;
            answered = true;
            done(result ?? new JObject { ["ok"] = true });
        }
        try
        {
            tool.run(args ?? new JObject(), Answer);
        }
        catch (ArgumentException e)  // a tool's own "can't do that": no stack trace needed
        {
            Answer(Error(e.Message));
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Answer(Error(e.Message));
        }
    }

    public static JObject Call(string name, string argsJson) =>
        Call(name, string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson));

    public static JObject Error(string message) => new JObject { ["error"] = message };
}
