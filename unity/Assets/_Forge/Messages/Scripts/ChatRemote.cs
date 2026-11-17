using System;
using UnityEngine;

/// <summary>
/// Albie opens a conversation ("open my chat with Adrian", "pull up #general": the hub's messages_open tool). The hub's
/// ws <c>messages.open {pet_id, request_id, channel, channel_name, kind}</c> shows it on the chat panel
/// (<see cref="ChatPanel.ForChannel"/>), opening MSG mode when it is closed (like tapping
/// a Mentions card does), and this answers <c>messages.opened {pet_id, request_id, channel, ok, reason?}</c> in the same frame, so Albie says what really happened. reason: <c>not_at_desk</c> (the
/// Workspace only opens at a desk) or <c>no_workspace</c>. A conversation the list doesn't have yet (a DM the hub just
/// opened) gets a row right away (<see cref="ChatStore.Provisional"/>). Installs itself on scene load into any scene
/// with a DeskPresence, like <see cref="DraftPresenter"/>. A conversation the app does not show
/// (<see cref="ChatStore.Unsupported"/>) gets no answer at all; the hub's own wait for the reply times out.
/// </summary>
public class ChatRemote : MonoBehaviour
{
    const string OpenType = "messages.open", OpenedType = "messages.opened", MessagesMode = "messages";

    IDisposable subscription;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindAnyObjectByType<ChatRemote>() != null || FindAnyObjectByType<DeskPresence>() == null) return;
        new GameObject("MessagesRemote").AddComponent<ChatRemote>();
    }

    void OnEnable() => subscription = ForgeNet.On<ChatOpenRequest>(OpenType, OnOpen);

    void OnDisable()
    {
        subscription?.Dispose();
        subscription = null;
    }

    void OnOpen(ChatOpenRequest req)
    {
        if (req == null || string.IsNullOrEmpty(req.channel) || ChatStore.Unsupported(null, req.channel)) return;
        string reason = Show(req.channel, req.channelName, req.kind);
        ForgeNet.Send(OpenedType, new ChatOpened
        {
            petId = string.IsNullOrEmpty(req.petId) ? "bit" : req.petId,
            requestId = req.requestId ?? "",
            channel = req.channel,
            ok = reason == null,
            reason = reason,
        });
        Debug.Log($"[ChatRemote] Albie opened {req.channelName ?? req.channel}: {(reason ?? "ok")}.");
    }

    /// <summary>
    /// Show a conversation on its panel, opening MSG mode when it is closed. Null when it shows, else why not:
    /// <c>not_at_desk</c> or <c>no_workspace</c>. Also used by Albie's on-device messages_open (<see cref="MessagesTools"/>).
    /// </summary>
    public static string Show(string channel, string channelName, string kind)
    {
        ChatStore.Provisional(channel, channelName, kind);
        string reason = null;
        var ws = Workspace.Current;
        var panel = ChatPanel.ForChannel(channel);
        if (panel != null) panel.Open(channel);
        else if (ws == null) reason = "no_workspace";
        else if (ws.presence == null || !ws.presence.AtDesk) reason = "not_at_desk";
        else
        {
            ChatPanel.PendingChannel = channel;  // the panel opens it as it comes up
            ws.Open(MessagesMode);
            if (ws.ActiveMode != MessagesMode) reason = "not_at_desk";
        }
        if (reason != null && panel == null) ChatPanel.PendingChannel = null;
        return reason;
    }
}
