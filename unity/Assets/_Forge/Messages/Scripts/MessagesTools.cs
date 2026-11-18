using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie's messaging tools with the hub off: the behaviour of hub/messaging/tools.py over the device's demo messenger
/// (<see cref="MessagesDemo"/>). Definitions: <c>Resources/AlbieTools/messages.json</c>. Sending is two steps on
/// purpose: messages_send only writes a draft (the composer or a floating card shows it with Send / Cancel), and
/// messages_confirm (or a tap) sends or drops it. A sent message stays on the device, in the demo conversation.
/// </summary>
public static class MessagesTools
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register() => AlbieTools.Register("messages", new Dictionary<string, AlbieTools.Handler>
    {
        ["messages_list_channels"] = _ => Guard(() => new JObject { ["channels"] = MessagesDemo.Summaries() }),
        ["messages_read_channel"] = ReadChannel,
        ["messages_open"] = Open,
        ["messages_send"] = Send,
        ["messages_edit_draft"] = EditDraft,
        ["messages_confirm"] = Confirm,
    });

    const string Next = "Tell the user what you wrote and ask them to tap Send or say 'send it'. Not sent yet.";

    // With the hub live its own messaging tools answer; these only run over the device's conversations.
    static JObject Guard(Func<JObject> run)
    {
        if (ForgeNet.Status == ForgeNetStatus.Live) throw new ArgumentException("the hub handles messages right now");
        return run();
    }

    static JObject ReadChannel(JObject args) => Guard(() =>
    {
        var c = MessagesDemo.Find((string)args["channel"]);
        int n = Mathf.Clamp(args.Value<int?>("n") ?? 10, 1, 50);
        return WithProject(c, new JObject { ["channel"] = c["id"], ["channel_name"] = c["name"], ["kind"] = c["kind"], ["messages"] = MessagesDemo.Read(c, n) });
    });

    // A project channel says which project it is about, and how to open it (os_open), so Albie can offer that.
    static JObject WithProject(JObject channel, JObject result)
    {
        string project = (string)channel["project"] ?? MessagesShare.ProjectForChannel((string)channel["name"]);
        if (string.IsNullOrEmpty(project)) return result;
        result["project"] = new JObject
        {
            ["id"] = project, ["title"] = Forge.OS.OSProjects.TitleFor(project),
            ["open_with"] = new JObject { ["tool"] = "os_open", ["kind"] = "project", ["id"] = project },
        };
        return result;
    }

    static JObject Open(JObject args) => Guard(() =>
    {
        var c = MessagesDemo.Find((string)args["channel"]);
        string reason = ChatRemote.Show((string)c["id"], (string)c["name"], (string)c["kind"]);
        if (reason != null) throw new ArgumentException(reason == "not_at_desk" ? "the user has to sit down at the desk first" : "there's no workspace to show it in");
        return WithProject(c, new JObject { ["opened"] = c["name"], ["kind"] = c["kind"] });
    });

    static JObject Send(JObject args) => Guard(() =>
    {
        string text = ((string)args["text"] ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("text is empty");
        string replyTs = (string)args["reply_to_ts"], target = (string)args["channel"];
        JObject channel = null, replyTo = null;
        if (!string.IsNullOrEmpty(replyTs))
        {
            replyTo = MessagesDemo.FindMessage(replyTs, out channel);
            if (replyTo == null) throw new ArgumentException($"no message with ts {replyTs}");
        }
        else if (!string.IsNullOrEmpty(target)) channel = MessagesDemo.Find(target);
        else if (ChatPanel.Active != null && ChatPanel.Active.Selected != null) channel = MessagesDemo.Find(ChatPanel.Active.Selected);
        else throw new ArgumentException("say who the message is for");
        ChatStore.Provisional((string)channel["id"], (string)channel["name"], (string)channel["kind"]);  // a DM made just now

        var result = new JObject();
        if (args.Value<bool?>("open_messenger") == true && ChatPanel.Active == null)
        {
            string reason = ChatRemote.Show((string)channel["id"], (string)channel["name"], (string)channel["kind"]);
            if (reason != null) result["view_error"] = reason;
        }
        var d = MessagesDemo.CreateDraft(channel, text, replyTo);
        result["draft_id"] = d["draft_id"];
        result["status"] = "awaiting_confirmation";
        result["channel_name"] = d["channel_name"];
        result["text"] = d["text"];
        result["next"] = Next;
        return result;
    });

    static JObject EditDraft(JObject args) => Guard(() =>
    {
        string text = ((string)args["text"] ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("text is empty");
        var d = MessagesDemo.EditDraft(text);
        return new JObject
        {
            ["draft_id"] = d["draft_id"], ["status"] = "awaiting_confirmation", ["channel_name"] = d["channel_name"], ["text"] = d["text"],
            ["next"] = "Say what changed and ask them to tap Send or say 'send it'. Not sent yet.",
        };
    });

    static JObject Confirm(JObject args) => Guard(() =>
    {
        var open = MessagesDemo.OpenDraft;
        string id = (string)args["draft_id"];
        if (open == null) throw new ArgumentException("there's no open draft (it was sent or cancelled)");
        if (args.Value<bool?>("send") == true)
        {
            string channelName = (string)open["channel_name"], text = (string)open["text"];
            string ts = MessagesDemo.SendDraft(id);
            return new JObject
            {
                ["status"] = "sent", ["channel_name"] = channelName, ["text"] = text, ["ts"] = ts,
                ["note"] = "This is the sample conversation on the headset: the message stayed on the device.",
            };
        }
        string draftId = (string)open["draft_id"];
        MessagesDemo.CancelDraft(id);
        return new JObject { ["status"] = "cancelled", ["draft_id"] = draftId };
    });
}
