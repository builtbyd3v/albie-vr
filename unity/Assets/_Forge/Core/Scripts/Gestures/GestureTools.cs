using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie's hand gesture tools (Resources/AlbieTools/gestures.json), so talking is enough: "when I do this, open CAD",
/// show it three times, done. Each one is also on the gesture display, so none of them need a voice.
/// </summary>
public static class GestureTools
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register() => AlbieTools.Register("gestures", new Dictionary<string, AlbieTools.AsyncHandler>
    {
        ["gesture_list"] = (args, done) => done(List()),
        ["gesture_teach"] = Teach,
        ["gesture_cancel"] = (args, done) =>
        {
            Engine().CancelTeach();
            done(new JObject { ["ok"] = true });
        },
        ["gesture_bind"] = (args, done) => done(Bind(args)),
        ["gesture_forget"] = (args, done) => done(Forget(args)),
        ["gesture_display"] = (args, done) =>
        {
            Engine().Actions.ShowDisplay((bool?)args["show"] ?? true);
            done(new JObject { ["ok"] = true });
        },
    });

    static GestureEngine Engine()
    {
        var engine = GestureEngine.Current;
        if (engine == null) throw new System.ArgumentException("Hand gestures aren't running here.");
        return engine;
    }

    static JObject List()
    {
        var gestures = new JArray();
        foreach (var g in Engine().Library.All)
            gestures.Add(new JObject { ["name"] = g.title, ["how"] = g.how, ["does"] = GestureActions.Describe(g), ["built_in"] = g.builtin });
        return new JObject { ["gestures"] = gestures };
    }

    // Answers when the takes are over, so Albie can say straight away whether it worked.
    static void Teach(JObject args, System.Action<JObject> done)
    {
        string name = ((string)args["name"] ?? "").Trim();
        string action = (string)args["action"] ?? GestureActions.None;
        Engine().Teach(name, action, args["args"] as JObject, done);
    }

    static JObject Bind(JObject args)
    {
        var engine = Engine();
        var def = engine.Library.Find((string)args["name"]);
        if (def == null) return AlbieTools.Error($"I don't know a gesture called '{(string)args["name"]}'.");
        string action = (string)args["action"] ?? GestureActions.None;
        string problem = GestureActions.CheckBinding(action);
        if (problem != null) return AlbieTools.Error(problem);
        engine.Library.Bind(def, action, args["args"] as JObject);
        return new JObject { ["ok"] = true, ["gesture"] = def.title, ["does"] = GestureActions.Describe(def) };
    }

    static JObject Forget(JObject args)
    {
        var engine = Engine();
        var def = engine.Library.Find((string)args["name"]);
        if (def == null) return AlbieTools.Error($"I don't know a gesture called '{(string)args["name"]}'.");
        if (def.builtin) return AlbieTools.Error($"'{def.title}' is built in. You can rebind it to none instead.");
        engine.Actions.Confirm($"Forget the gesture '{def.title}'?", () => engine.Library.Remove(def));
        return new JObject { ["status"] = "awaiting_confirmation", ["gesture"] = def.title };
    }
}
