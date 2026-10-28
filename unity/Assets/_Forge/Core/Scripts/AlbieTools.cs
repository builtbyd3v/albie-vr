using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie's tools on the headset, so he can act with no hub. Same shape as hub/agent/tools.py: each tool is
/// {name, description, parameters (JSON schema)} plus a handler. Modules register theirs from a JSON file in
/// <c>Resources/AlbieTools/</c> when Play starts. <see cref="Call"/> never throws: errors come back as
/// <c>{"error": ...}</c> so the model can explain them.
/// </summary>
public static class AlbieTools
{
    public delegate JObject Handler(JObject args);

    static readonly Dictionary<string, (JObject def, Handler run)> tools = new Dictionary<string, (JObject, Handler)>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => tools.Clear();

    /// <summary>Add the tools in <c>Resources/AlbieTools/&lt;file&gt;.json</c> (an array of definitions); each needs a handler.</summary>
    public static void Register(string file, Dictionary<string, Handler> handlers)
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
            all.Add(new JObject { ["type"] = "function", ["name"] = def["name"], ["description"] = def["description"], ["parameters"] = def["parameters"] });
        return all;
    }

    public static JObject Call(string name, JObject args)
    {
        if (!tools.TryGetValue(name, out var tool)) return Error($"Unknown tool '{name}'");
        try
        {
            return tool.run(args ?? new JObject()) ?? new JObject { ["ok"] = true };
        }
        catch (ArgumentException e)  // a tool's own "can't do that": no stack trace needed
        {
            return Error(e.Message);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            return Error(e.Message);
        }
    }

    public static JObject Call(string name, string argsJson) =>
        Call(name, string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson));

    public static JObject Error(string message) => new JObject { ["error"] = message };
}
