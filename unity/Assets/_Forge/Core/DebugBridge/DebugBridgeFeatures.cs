#if DEVELOPMENT_BUILD || UNITY_EDITOR
// Same guard as DebugBridge.cs: a release APK compiles none of this. None of it is an Albie tool, so the voice model
// can never reach it; only the laptop on the USB cable can (see DebugBridge.cs).

using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Debug bridge commands for the feature sweep (tools/questbridge_feature_cases.json), and the state fields its cases read.
/// <c>lifecycle {away}</c>: headset off / on (<see cref="AppLifecycle.Simulate"/>).
/// <c>desk {seated}</c>: seat the user at the nearest table (<see cref="DeskPresence.forceSeated"/>; needs a room scan with a table).
/// <c>voice_guard {on}</c>: hold Albie's voice so nothing reaches the paid voice service, and log the welcome card's
/// lines instead of speaking them. Re-held after every resume. A sweep turns it on first and off last.
/// <c>inject {type, payload}</c>: a hub event, as if the hub sent it (<see cref="ForgeNet.Inject"/>).
/// <c>first_run</c>: the welcome card's tries; <c>first_run {tap}</c> taps one, as a finger would.
/// </summary>
public partial class DebugBridge
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    static bool voiceHeld;
    static int resumes;
    static float lastAwayS = -1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetFeatureStatics()
    {
        voiceHeld = false;
        resumes = 0;
        lastAwayS = -1f;
    }

    // After AppLifecycle.ResetStatics (SubsystemRegistration), which clears Resumed.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void WatchLifecycle() => AppLifecycle.Resumed += OnResumed;

    static void OnResumed(float secondsAway)
    {
        resumes++;
        lastAwayS = secondsAway;
        Debug.Log($"[DebugBridge] resumed after {secondsAway:F1} s (resume {resumes})");
        if (voiceHeld && ForgeNet.LocalPeer is AlbieVoice voice) voice.Suspend();  // AppLifecycle just resumed it
    }

    static void HeldSay(string line, string hint) => Debug.Log("[FirstRun] Albie: " + line + " (voice held by the debug bridge)");

    // ---------------------------------------------------------------- commands (main thread)

    /// <summary>Runs a feature command; false when <paramref name="cmd"/> isn't one.</summary>
    bool RunFeature(string cmd, JToken id, Job job)
    {
        JObject req = job.req;
        switch (cmd)
        {
            case "lifecycle":
                if (req["away"]?.Type != JTokenType.Boolean) { job.Finish(Fail(id, "lifecycle needs \"away\": true or false")); return true; }
                if (FindAnyObjectByType<AppLifecycle>() == null) { job.Finish(Fail(id, "no AppLifecycle running")); return true; }
                AppLifecycle.Simulate((bool)req["away"]);
                job.Finish(Ok(id, LifecycleJson()));
                return true;

            case "desk":
                if (req["seated"]?.Type != JTokenType.Boolean) { job.Finish(Fail(id, "desk needs \"seated\": true or false")); return true; }
                var presence = Presence();
                if (presence == null) { job.Finish(Fail(id, "no DeskPresence in this scene")); return true; }
                presence.forceSeated = (bool)req["seated"];
                Debug.Log($"[DebugBridge] desk: forceSeated={presence.forceSeated}");
                job.Finish(Ok(id, DeskJson()));  // State changes on DeskPresence's next check (0.1 s): read it from state
                return true;

            case "voice_guard":
                bool on = req["on"]?.Type != JTokenType.Boolean || (bool)req["on"];
                voiceHeld = on;
                if (on) FirstRunGuide.SayHook = HeldSay;
                else if (FirstRunGuide.SayHook == (Action<string, string>)HeldSay) FirstRunGuide.SayHook = null;
                var voice = ForgeNet.LocalPeer as AlbieVoice;
                if (voice != null)
                {
                    if (on) voice.Suspend();
                    else if (!AppLifecycle.IsAway) voice.Resume();
                }
                Debug.Log($"[DebugBridge] voice guard {(on ? "on: nothing reaches the voice service" : "off")}");
                job.Finish(Ok(id, new JObject { ["on"] = on, ["voice"] = voice != null }));
                return true;

            case "inject":
                string type = (string)req["type"];
                if (string.IsNullOrEmpty(type)) { job.Finish(Fail(id, "inject needs \"type\"")); return true; }
                ForgeNet.Inject(type, req["payload"] ?? new JObject());  // handled on ForgeNet's next frame
                Debug.Log($"[DebugBridge] inject {type}");
                job.Finish(Ok(id, new JObject { ["type"] = type }));
                return true;

            case "first_run":
                job.Finish(FirstRun(id, req));
                return true;

            default:
                return false;
        }
    }

    static JObject FirstRun(JToken id, JObject req)
    {
        var tries = FirstRunGuide.Tries;
        if (req["tap"] == null)
        {
            var list = new JArray();
            foreach (var t in tries)
                list.Add(new JObject
                {
                    ["label"] = t.label, ["tool"] = t.tool, ["args"] = JObject.Parse(t.argsJson),
                    ["needs_truck"] = t.needsTruck, ["registered"] = AlbieTools.Has(t.tool),
                });
            return Ok(id, new JObject { ["present"] = FirstRunGuide.Instance != null, ["tries"] = list });
        }
        var guide = FirstRunGuide.Instance;
        if (guide == null) return Fail(id, "no first-run guide in this scene (it needs a Workspace)");
        if (req["tap"].Type != JTokenType.Integer || (int)req["tap"] < 0 || (int)req["tap"] >= tries.Length)
            return Fail(id, $"first_run tap needs an index 0 to {tries.Length - 1}");
        int index = (int)req["tap"];
        var tap = typeof(FirstRunGuide).GetMethod("Tap", Private, null, new[] { typeof(int) }, null);
        if (tap == null) return Fail(id, "FirstRunGuide changed shape: first_run tap needs updating (see DebugBridgeFeatures)");
        tap.Invoke(guide, new object[] { index });  // a slow tool reports later: read first_run.last_said from state
        return Ok(id, new JObject { ["tapped"] = tries[index].label, ["last_said"] = FirstRunGuide.LastSaid });
    }

    // ---------------------------------------------------------------- state fields

    void AddFeatureState(JObject o)
    {
        o["lifecycle"] = Safe(LifecycleJson);
        o["voice"] = Safe(VoiceJson);
        o["cloud"] = Safe(CloudJson);
        o["desk"] = Safe(DeskJson);
        o["workspace"] = Safe(() => new JObject { ["mode"] = Workspace.Current != null ? Workspace.Current.ActiveMode ?? "" : null });
        o["first_run"] = Safe(FirstRunJson);
        o["messages"] = Safe(MessagesJson);
    }

    static JToken Safe(Func<JObject> read)
    {
        try { return read(); }
        catch (Exception e) { return new JObject { ["error"] = $"{e.GetType().Name}: {e.Message}" }; }
    }

    static DeskPresence Presence() =>
        Workspace.Current != null && Workspace.Current.presence != null ? Workspace.Current.presence : FindAnyObjectByType<DeskPresence>();

    static JObject LifecycleJson() => new JObject
    {
        ["away"] = AppLifecycle.IsAway, ["time_scale"] = Time.timeScale, ["audio_paused"] = AudioListener.pause,
        ["resumes"] = resumes, ["last_away_s"] = lastAwayS < 0f ? null : (JToken)Math.Round(lastAwayS, 1),
    };

    static JObject VoiceJson()
    {
        var voice = ForgeNet.LocalPeer as AlbieVoice ?? FindAnyObjectByType<AlbieVoice>();
        if (voice == null) return new JObject { ["present"] = false, ["connected"] = false, ["held"] = voiceHeld };
        var wsField = typeof(AlbieVoice).GetField("ws", Private);
        var awayField = typeof(AlbieVoice).GetField("away", Private);
        var o = new JObject { ["present"] = true, ["held"] = voiceHeld };
        if (wsField == null || awayField == null)
        {
            o["connected"] = null;
            o["error"] = "AlbieVoice changed shape: DebugBridgeFeatures.VoiceJson needs updating";
            return o;
        }
        var ws = wsField.GetValue(voice) as ClientWebSocket;
        o["connected"] = ws != null && ws.State == WebSocketState.Open;  // open = billed
        o["away"] = (bool)awayField.GetValue(voice);
        return o;
    }

    // No URL and never the app key: only what the cases assert.
    static JObject CloudJson() => new JObject
    {
        ["configured"] = ForgeCloud.Configured, ["probed"] = ForgeCloud.Probed,
        ["state"] = ForgeCloud.State.ToString(), ["hub"] = ForgeNet.Status.ToString(),
    };

    static JObject DeskJson()
    {
        var presence = Presence();
        if (presence == null) return new JObject { ["present"] = false };
        var locator = presence.locator;
        return new JObject
        {
            ["present"] = true, ["state"] = presence.State.ToString(), ["at_desk"] = presence.AtDesk,
            ["force_seated"] = presence.forceSeated,
            ["room_loaded"] = locator != null && locator.RoomLoaded, ["tables"] = locator != null ? locator.Tables.Count : 0,
        };
    }

    static JObject FirstRunJson()
    {
        var guide = FirstRunGuide.Instance;
        var truck = typeof(FirstRunGuide).GetField("truckOut", Private);
        return new JObject
        {
            ["present"] = guide != null,
            ["card_shown"] = guide != null && guide.CardShown, ["orb_shown"] = guide != null && guide.OrbShown,
            ["truck_out"] = guide != null && truck != null ? (JToken)(bool)truck.GetValue(guide) : null,
            ["last_said"] = FirstRunGuide.LastSaid,
        };
    }

    static JObject MessagesJson()
    {
        int imChannels = 0;
        foreach (var c in ChatStore.Channels)
            if (c != null && ChatStore.Unsupported(c.source, c.id)) imChannels++;
        int imRecent = 0;
        var sources = new List<string>();
        foreach (var m in ChatStore.Recent)
        {
            if (m == null) continue;
            if (ChatStore.Unsupported(m.source, m.channel)) imRecent++;
            string s = m.source ?? "";
            if (!sources.Contains(s)) sources.Add(s);
        }
        return new JObject
        {
            ["demo"] = ChatStore.Demo, ["channels"] = ChatStore.Channels.Count, ["im_channels"] = imChannels,
            ["recent"] = ChatStore.Recent.Count, ["recent_im"] = imRecent, ["recent_sources"] = new JArray(sources),
            ["newest_ts"] = ChatStore.Recent.Count > 0 ? ChatStore.Recent[0].ts : null,
            ["open_draft"] = MessagesDemo.OpenDraft != null ? MessagesDemo.OpenDraft["draft_id"]?.DeepClone() : null,
        };
    }
}
#endif
