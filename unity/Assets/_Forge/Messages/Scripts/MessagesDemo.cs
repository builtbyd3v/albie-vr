using System;
using System.Collections;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Hub off: the messenger's own back end on the device, over the demo conversations in <c>Resources/MessagesDemo.json</c>.
/// Answers the panels' GETs (/messages/channels, /messages/users/me, /messages/history) through
/// <see cref="ForgeNet.MockRest"/>, so <see cref="SlackStore"/> reads them the same way it reads the hub. Each message's
/// <c>ago_min</c> becomes a ts that many minutes before the app started, so the times read naturally.
/// It also stands in for the hub's draft endpoints (hub/messaging): one open draft at a time, shown through the same
/// notify.draft / notify.sent / notify.draft_cancelled events, and a confirmed send lands in the local conversation as
/// the user's own message. Nothing leaves the device. <see cref="MessagesTools"/> (Albie) and the panels both use it.
/// The demo's <c>incoming</c> message arrives once, <c>after_s</c> seconds after the hub-off start, as a notify.message.
/// </summary>
public static class MessagesDemo
{
    const string MeId = "U0ME";

    static JObject demo;
    static double startedAt;
    static int seq;
    static bool hooked, incomingStarted;

    /// <summary>Albie's draft waiting for Send / Cancel: {draft_id, channel, channel_name, text, thread_ts, reply_to}.</summary>
    public static JObject OpenDraft { get; private set; }

    /// <summary>Seconds after the hub-off start until the scripted message arrives; null = the JSON's after_s. Tests set it.</summary>
    public static float? IncomingDelayOverride;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        demo = null;
        OpenDraft = null;
        seq = 0;
        if (hooked) ForgeNet.StatusChanged -= OnStatus;
        hooked = incomingStarted = false;
        IncomingDelayOverride = null;
    }

    // The scripted incoming message runs whether or not the messenger was ever opened.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Hook()
    {
        if (UnityEngine.Object.FindAnyObjectByType<DeskPresence>() == null) return;
        ForgeNet.StatusChanged += OnStatus;
        hooked = true;
        if (ForgeNet.Status == ForgeNetStatus.Mock) OnStatus(ForgeNetStatus.Mock);
    }

    static void OnStatus(ForgeNetStatus status)
    {
        if (status != ForgeNetStatus.Mock || incomingStarted) return;
        Install();
        if (demo?["incoming"] is JObject incoming)
        {
            incomingStarted = true;
            ForgeNet.Instance.StartCoroutine(Deliver(incoming));
        }
    }

    /// <summary>Answer the messages GETs from the demo file (once per Play).</summary>
    public static void Install()
    {
        if (demo != null) return;
        var asset = Resources.Load<TextAsset>("MessagesDemo");
        if (asset == null)
        {
            Debug.LogWarning("[MessagesDemo] no Resources/MessagesDemo.json");
            return;
        }
        demo = JObject.Parse(asset.text);
        startedAt = Now;
        ForgeNet.MockRest("/messages/users/me", _ => demo["me"].ToString(Formatting.None));
        ForgeNet.MockRest("/messages/channels", _ => Channels().ToString(Formatting.None));
        ForgeNet.MockRest("/messages/history", History);
    }

    static double Now => (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;

    static JObject Channels()
    {
        var list = new JArray();
        foreach (JObject c in demo["channels"]) list.Add(Summary(c));
        return new JObject { ["channels"] = list };
    }

    static JObject Summary(JObject c)
    {
        var copy = new JObject(c) { ["source"] = "slack" };
        copy.Remove("messages");
        var messages = (JArray)c["messages"];
        if (messages.Count > 0)
        {
            var last = (JObject)messages[messages.Count - 1];
            copy["last_ts"] = Ts(last);
            copy["last_text"] = last["text"];
        }
        return copy;
    }

    // GET /messages/history?channel=<id>&limit=50: the whole demo conversation (it's short), oldest first.
    static string History(string path)
    {
        string id = Query(path, "channel");
        var channel = ById(id);
        var messages = new JArray();
        if (channel != null && Query(path, "before") == null)
            foreach (JObject m in channel["messages"]) messages.Add(Message(channel, m));
        return new JObject
        {
            ["channel"] = id, ["channel_name"] = channel?["name"], ["kind"] = channel?["kind"], ["messages"] = messages, ["has_more"] = false,
        }.ToString(Formatting.None);
    }

    static JObject Message(JObject channel, JObject m) => new JObject
    {
        ["source"] = "slack", ["channel"] = channel["id"], ["channel_name"] = channel["name"],
        ["is_dm"] = (string)channel["kind"] == "dm", ["user"] = m["user"], ["user_name"] = m["user_name"],
        ["text"] = m["text"], ["ts"] = Ts(m), ["mentions_me"] = m["mentions_me"] ?? false, ["is_me"] = m["is_me"] ?? false,
    };

    static string Ts(JObject message) => (string)message["ts"] ??
        (startedAt - (double)(message["ago_min"] ?? 0) * 60).ToString("F6", CultureInfo.InvariantCulture);

    static string NewTs() => Math.Max(Now, startedAt + ++seq * 0.001).ToString("F6", CultureInfo.InvariantCulture);

    static string Query(string path, string key)
    {
        int q = path.IndexOf('?');
        if (q < 0) return null;
        foreach (string pair in path.Substring(q + 1).Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0 && pair.Substring(0, eq) == key) return Uri.UnescapeDataString(pair.Substring(eq + 1));
        }
        return null;
    }

    // ---------------------------------------------------------------- reading (Albie's tools)

    static JObject ById(string id)
    {
        if (demo == null || string.IsNullOrEmpty(id)) return null;
        foreach (JObject c in demo["channels"]) if ((string)c["id"] == id) return c;
        return null;
    }

    /// <summary>
    /// A conversation by id, '#name', name, or a person's name (their DM; a new DM when they have none yet but have
    /// written somewhere). Throws ArgumentException (the tool's error) when nothing or several match.
    /// </summary>
    public static JObject Find(string query)
    {
        Install();
        string q = (query ?? "").Trim();
        if (q.Length == 0) throw new ArgumentException("say which conversation");
        var byId = ById(q);
        if (byId != null) return byId;
        string name = q.TrimStart('#', '@').ToLowerInvariant();
        JObject exact = null, partial = null;
        int partials = 0;
        foreach (JObject c in demo["channels"])
        {
            string n = ((string)c["name"] ?? "").ToLowerInvariant();
            if (n == name) exact = c;
            else if (n.Contains(name) || (n.Split(' ')[0] == name)) { partial = c; partials++; }
        }
        if (exact != null) return exact;
        if (partials == 1) return partial;
        if (partials > 1) throw new ArgumentException($"'{q}' could be several conversations; say the full name");
        // A person who has written in a channel but has no DM yet: open one, as Slack would.
        foreach (JObject c in demo["channels"])
            foreach (JObject m in c["messages"])
            {
                string who = (string)m["user_name"] ?? "";
                if ((bool?)m["is_me"] == true || who.Length == 0) continue;
                string low = who.ToLowerInvariant();
                if (low != name && low.Split(' ')[0] != name) continue;
                var dm = new JObject { ["id"] = "D" + m["user"], ["name"] = who, ["kind"] = "dm", ["messages"] = new JArray() };
                ((JArray)demo["channels"]).Add(dm);
                return dm;
            }
        throw new ArgumentException($"no conversation or person called '{q}'");
    }

    /// <summary>Every conversation: id, name, kind, unread, mentions, last message.</summary>
    public static JArray Summaries()
    {
        Install();
        var all = new JArray();
        foreach (JObject c in demo["channels"])
        {
            var s = Summary(c);
            all.Add(new JObject
            {
                ["id"] = s["id"], ["name"] = s["name"], ["kind"] = s["kind"], ["unread"] = s["unread"] ?? 0,
                ["mentions"] = s["mentions"] ?? 0, ["last_text"] = s["last_text"],
            });
        }
        return all;
    }

    /// <summary>The last <paramref name="n"/> messages of a conversation, oldest first (who, text, ts, is_me).</summary>
    public static JArray Read(JObject channel, int n)
    {
        var messages = (JArray)channel["messages"];
        var list = new JArray();
        for (int i = Math.Max(0, messages.Count - n); i < messages.Count; i++)
        {
            var m = (JObject)messages[i];
            list.Add(new JObject { ["who"] = m["user_name"], ["text"] = m["text"], ["ts"] = Ts(m), ["is_me"] = m["is_me"] ?? false });
        }
        return list;
    }

    /// <summary>A message by ts, and its conversation; null when the ts is unknown.</summary>
    public static JObject FindMessage(string ts, out JObject channel)
    {
        Install();
        channel = null;
        if (string.IsNullOrEmpty(ts)) return null;
        foreach (JObject c in demo["channels"])
            foreach (JObject m in c["messages"])
                if (Ts(m) == ts) { channel = c; return m; }
        return null;
    }

    /// <summary>The panel opened a conversation: its counts clear here too, so the list and Albie agree.</summary>
    public static void Seen(string channelId)
    {
        var c = ById(channelId);
        if (c == null) return;
        c["unread"] = 0;
        c["mentions"] = 0;
    }

    // ---------------------------------------------------------------- drafts and sends (the hub's draft endpoints)

    /// <summary>Albie writes a draft for <paramref name="channel"/>; it shows in the composer or a floating card. Not sent.</summary>
    public static JObject CreateDraft(JObject channel, string text, JObject replyTo)
    {
        if (OpenDraft != null) CancelDraft((string)OpenDraft["draft_id"], "superseded");
        bool dm = (string)channel["kind"] == "dm";
        OpenDraft = new JObject
        {
            ["source"] = "slack", ["draft_id"] = "local-" + ++seq, ["channel"] = channel["id"], ["channel_name"] = channel["name"],
            ["text"] = text, ["thread_ts"] = null, ["edited"] = false, ["attachments"] = new JArray(),
        };
        if (replyTo != null) OpenDraft["reply_to"] = Message(channel, replyTo);
        ForgeNet.Inject(ForgeEvents.NotifyDraftType, OpenDraft.DeepClone());
        Debug.Log($"[MessagesDemo] draft {OpenDraft["draft_id"]} for {(dm ? "" : "#")}{channel["name"]}: {text}");
        return OpenDraft;
    }

    /// <summary>Reword the open draft (Albie, or the user editing it in the composer when <paramref name="echo"/> is false).</summary>
    public static JObject EditDraft(string text, bool echo = true)
    {
        if (OpenDraft == null) throw new ArgumentException("there's no open draft (it was sent or cancelled); write a new one with messages_send");
        OpenDraft["text"] = text;
        OpenDraft["edited"] = true;
        if (echo) ForgeNet.Inject(ForgeEvents.NotifyDraftType, OpenDraft.DeepClone());
        return OpenDraft;
    }

    /// <summary>Send the open draft: it lands in its conversation as the user's own message (notify.sent). Returns its ts.</summary>
    public static string SendDraft(string draftId = null)
    {
        var d = Take(draftId);
        string channel = (string)d["channel"], text = (string)d["text"];
        string ts = Post(channel, text);
        ForgeNet.Inject(ForgeEvents.NotifySentType, new JObject
        {
            ["draft_id"] = d["draft_id"], ["channel"] = channel, ["channel_name"] = d["channel_name"], ["text"] = text, ["ts"] = ts,
        });
        Debug.Log($"[MessagesDemo] sent {d["draft_id"]} to {d["channel_name"]} (on the device only): {text}");
        return ts;
    }

    public static void CancelDraft(string draftId = null, string reason = "cancelled")
    {
        var d = Take(draftId);
        ForgeNet.Inject(ForgeEvents.NotifyDraftCancelledType, new JObject { ["draft_id"] = d["draft_id"], ["reason"] = reason });
    }

    static JObject Take(string draftId)
    {
        if (OpenDraft == null || (!string.IsNullOrEmpty(draftId) && draftId != (string)OpenDraft["draft_id"]))
            throw new ArgumentException("that draft is gone (sent, cancelled or replaced)");
        var d = OpenDraft;
        OpenDraft = null;
        return d;
    }

    /// <summary>The user's own message into a demo conversation (typed and sent in the composer, or a confirmed draft). Returns its ts.</summary>
    public static string Post(string channelId, string text)
    {
        Install();
        var c = ById(channelId) ?? throw new ArgumentException($"no conversation '{channelId}'");
        string ts = NewTs();
        ((JArray)c["messages"]).Add(new JObject { ["user"] = MeId, ["user_name"] = "You", ["text"] = text, ["ts"] = ts, ["is_me"] = true });
        return ts;
    }

    // ---------------------------------------------------------------- the scripted incoming message

    static IEnumerator Deliver(JObject incoming)
    {
        float after = (float)(incoming["after_s"] ?? 180), start = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - start < (IncomingDelayOverride ?? after)) yield return null;
        if (ForgeNet.Status != ForgeNetStatus.Mock) yield break;  // the hub came up: it has the real messages
        var c = ById((string)incoming["channel"]);
        if (c == null) yield break;
        var m = new JObject
        {
            ["user"] = incoming["user"], ["user_name"] = incoming["user_name"], ["text"] = incoming["text"], ["ts"] = NewTs(),
            ["mentions_me"] = (string)c["kind"] == "dm" || (bool?)incoming["mentions_me"] == true,
        };
        ((JArray)c["messages"]).Add(m);
        c["unread"] = (int)(c["unread"] ?? 0) + 1;
        if ((bool)m["mentions_me"]) c["mentions"] = (int)(c["mentions"] ?? 0) + 1;
        Debug.Log($"[MessagesDemo] new message from {m["user_name"]} in {c["name"]}: {m["text"]}");
        ForgeNet.Inject(ForgeEvents.NotifyMessageType, Message(c, m));
    }
}
