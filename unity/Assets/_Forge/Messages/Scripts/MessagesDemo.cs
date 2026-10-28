using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Hub off: demo conversations for the messages panels, from <c>Resources/MessagesDemo.json</c>. Answers the panels' own
/// GETs (/messages/channels, /messages/users/me, /messages/history) through <see cref="ForgeNet.MockRest"/>, so
/// <see cref="SlackStore"/> reads them the same way it reads the hub. Each message's <c>ago_min</c> becomes a ts that
/// many minutes before the app started, so the times read naturally. Nothing is sent anywhere.
/// </summary>
public static class MessagesDemo
{
    static JObject demo;
    static double startedAt;

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
        startedAt = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        ForgeNet.MockRest("/messages/users/me", _ => demo["me"].ToString(Formatting.None));
        ForgeNet.MockRest("/messages/channels", _ => Channels().ToString(Formatting.None));
        ForgeNet.MockRest("/messages/history", History);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => demo = null;

    static JObject Channels()
    {
        var list = new JArray();
        foreach (JObject c in demo["channels"])
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
            list.Add(copy);
        }
        return new JObject { ["channels"] = list };
    }

    // GET /messages/history?channel=<id>&limit=50: the whole demo conversation (it's short), oldest first.
    static string History(string path)
    {
        string id = Query(path, "channel");
        JObject channel = null;
        foreach (JObject c in demo["channels"])
            if ((string)c["id"] == id) channel = c;
        var messages = new JArray();
        if (channel != null && Query(path, "before") == null)
            foreach (JObject m in channel["messages"])
                messages.Add(new JObject
                {
                    ["source"] = "slack", ["channel"] = id, ["channel_name"] = channel["name"],
                    ["is_dm"] = (string)channel["kind"] == "dm", ["user"] = m["user"], ["user_name"] = m["user_name"],
                    ["text"] = m["text"], ["ts"] = Ts(m), ["mentions_me"] = m["mentions_me"] ?? false, ["is_me"] = m["is_me"] ?? false,
                });
        return new JObject
        {
            ["channel"] = id, ["channel_name"] = channel?["name"], ["kind"] = channel?["kind"], ["messages"] = messages, ["has_more"] = false,
        }.ToString(Formatting.None);
    }

    static string Ts(JObject message) =>
        (startedAt - (double)(message["ago_min"] ?? 0) * 60).ToString("F6", CultureInfo.InvariantCulture);

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
}
