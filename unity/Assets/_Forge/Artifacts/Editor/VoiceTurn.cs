using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Editor-only live-test hook: types a turn into Albie's open voice session, as if the user had said it, so a harness can
/// replay requests without a microphone. It uses <c>AlbieVoice</c>'s own socket and send queue (no second connection):
/// a user message (<c>conversation.item.create</c>, <c>input_text</c>) and then one <c>response.create</c> with the same
/// per-reply instructions a spoken turn gets. A reply in progress is cut off first, as a barge-in is, and the turn counter
/// moves on, so tool results from the turn before are treated as stale (sent, no reply).
/// <para>Reaches into <c>AlbieVoice</c> by reflection so Core keeps no test code; <see cref="Check"/> says whether every
/// member it needs is still there. Each typed turn reaches the voice model and is billed: in Play mode with the voice on only.</para>
/// <para>Use from <c>unity command eval</c>: <c>return VoiceTurn.TypeTurn("make a metronome");</c></para>
/// </summary>
public static class VoiceTurn
{
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    static readonly string[] Fields = { "turnSeq", "turnOpen", "finalSent", "committed", "responseActive", "audioStarted" };
    static readonly string[] Methods = { "Send", "RequestResponse", "StopSpeech", "Emit" };

    /// <summary>Types <paramref name="text"/> as the user's next turn. Returns what it did, or why it couldn't.</summary>
    public static string TypeTurn(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "FAIL nothing to type";
        if (!Application.isPlaying) return "FAIL not in Play mode";
        string missing = Missing();
        if (missing != null) return "FAIL " + missing;
        var voice = UnityEngine.Object.FindAnyObjectByType<AlbieVoice>();
        if (voice == null)
            return $"FAIL AlbieVoice isn't running (EditorPrefs {AlbieVoice.EditorOffPref} is true, or it was turned off), so there is no session to type into";
        var t = typeof(AlbieVoice);
        text = text.Trim();

        if (Get<bool>(voice, "responseActive") || Get<bool>(voice, "audioStarted")) Call(voice, "StopSpeech", "barge_in");
        t.GetField("turnSeq", Any).SetValue(voice, Get<int>(voice, "turnSeq") + 1);
        t.GetField("turnOpen", Any).SetValue(voice, false);
        t.GetField("committed", Any).SetValue(voice, false);
        t.GetField("finalSent", Any).SetValue(voice, true);  // this turn's transcript is the typed text, sent below
        var remember = t.GetMethod("Remember", Any, null, new[] { typeof(string), typeof(string) }, null);  // feat/voice-cost
        remember?.Invoke(voice, new object[] { "User: ", text });

        Debug.Log($"[VoiceTurn] typed: {text}");
        Call(voice, "Emit", "stt.final", new JObject { ["text"] = text, ["voice"] = true }, "thinking");
        Call(voice, "Send", new JObject
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JArray(new JObject { ["type"] = "input_text", ["text"] = text }),
            },
        });
        Call(voice, "RequestResponse", (string)t.GetField("BrevityNow", Static).GetRawConstantValue());
        return $"typed turn {Get<int>(voice, "turnSeq")}: {text}";
    }

    /// <summary>Edit-mode check: every <c>AlbieVoice</c> member <see cref="TypeTurn"/> uses still exists with the expected shape.</summary>
    public static string Check()
    {
        string missing = Missing();
        return missing == null
            ? $"  ok   VoiceTurn reaches AlbieVoice: {Fields.Length} fields, {Methods.Length} methods and BrevityNow found"
            : "  FAIL VoiceTurn: " + missing;
    }

    static string Missing()
    {
        var t = typeof(AlbieVoice);
        var gone = new List<string>();
        foreach (string f in Fields)
            if (t.GetField(f, Any) == null) gone.Add("field " + f);
        if (t.GetMethod("Send", Any, null, new[] { typeof(JObject) }, null) == null) gone.Add("Send(JObject)");
        if (t.GetMethod("RequestResponse", Any, null, new[] { typeof(string) }, null) == null) gone.Add("RequestResponse(string)");
        if (t.GetMethod("StopSpeech", Any, null, new[] { typeof(string) }, null) == null) gone.Add("StopSpeech(string)");
        if (t.GetMethod("Emit", Any, null, new[] { typeof(string), typeof(JObject), typeof(string) }, null) == null) gone.Add("Emit(string, JObject, string)");
        if (!(t.GetField("BrevityNow", Static)?.GetRawConstantValue() is string)) gone.Add("const BrevityNow");
        return gone.Count == 0 ? null : "AlbieVoice has no " + string.Join(", ", gone);
    }

    static T Get<T>(object o, string field) => (T)o.GetType().GetField(field, Any).GetValue(o);

    static void Call(object o, string method, params object[] args)
    {
        var types = new Type[args.Length];
        for (int i = 0; i < args.Length; i++) types[i] = args[i].GetType();
        o.GetType().GetMethod(method, Any, null, types, null).Invoke(o, args);
    }
}
