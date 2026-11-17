using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The chat panel's composer, a rounded message box: a rounded field with a grey edge ("Message #general"),
/// a toolbar inside it (Ask Albie on the left, the green Send arrow on the right, grey while there is nothing to send),
/// a status line under it ("Sending…", "Sent", errors) and, for a draft Albie wrote, a banner above it ("Albie drafted
/// this", Cancel). Built at runtime under the panel's Content by <see cref="ChatPanel"/>, which lays it out and ticks it.
/// Sending: text you typed becomes a draft (POST /messages/drafts with the open conversation) that is sent at once
/// (POST .../send), since you wrote it yourself. A draft from Albie (notify.draft) lands in the field as editable text
/// with Send / Cancel; Send PATCHes the text first when you changed it, and its files (a CAD design's STEP, STL, drawing,
/// parts list) show on the banner and go up with it. Albie rewording the open draft (messages_edit_draft) replaces the
/// text in the field. Nothing leaves without a Send. What was sent goes into the conversation at once
/// (<see cref="ChatStore.AddOwn"/>), without waiting for the provider.
/// Keyboard: on the Quest TMP_InputField opens the system keyboard when poked; on desktop the keyboard types into it.
/// The Interaction SDK's PointableCanvasModule never sends OnUpdateSelected (that is where TMP reads key events), so
/// <see cref="Tick"/> sends it while the field is focused; <see cref="ForgeInput.TextFieldFocused"/> mirrors the focus
/// so other key handlers can stand down.
/// </summary>
public class ChatComposer
{
    const float BoxH = 84f, FieldTop = 9f, FieldH = 40f, ToolH = 26f, BannerH = 30f, FilesH = 18f, CaptionH = 18f, Gap = 6f;
    const float Corner = 9f, Edge = 1.2f, SendW = 34f, StatusSeconds = 2.5f, TextSize = 13f;

    /// <summary>Height (mm) the composer takes: the box and status line, plus the banner while Albie's draft is in it.</summary>
    public float Height => (HasDraft ? BannerHeight + Gap : 0f) + BoxH + CaptionH;

    // The banner grows by a line while the draft carries files.
    float BannerHeight => BannerH + (filesLine.Length > 0 ? FilesH : 0f);

    readonly ChatPanel owner;
    readonly RectTransform root, box, fieldRect, banner, bannerText;
    readonly ForgeSurface edge, fill, bannerFill;
    readonly TMP_InputField field;
    readonly TextMeshProUGUI text, placeholder, caption, bannerLabel, files;
    readonly Image sendIcon;
    string filesLine = "";                     // the Albie draft's attachments in one line (on the banner), "" for none
    readonly ChatButton send, askAlbie, cancel;
    BaseEventData updateEvent;

    ChatChannel channel;
    string draftId, draftText, draftChannel, draftThread, draftName;  // the Albie draft in the field, if any
    string ownText, ownChannel, ownDraftId;    // the draft we created ourselves (its notify.draft must not land in the field)
    string closedDraftId;                      // the last Albie draft sent or cancelled here: late echoes of it are ignored
    bool sending, focused, canSend;
    float statusTimer;
    string statusText;

    public bool HasDraft => draftId != null;
    public RectTransform Root => root;

    public ChatComposer(Transform parent, ChatPanel owner)
    {
        this.owner = owner;
        root = ChatUi.Rect(parent, "Composer");

        // Albie's draft banner (shown only with a draft).
        banner = ChatUi.Rect(root, "Banner");
        bannerFill = ChatUi.Surface(banner, "Fill", ChatUi.AccentA(0.16f), 8f);
        ChatUi.Stretch(bannerFill.rectTransform, 0f, 0f, 0f, 0f);
        var sparkle = ChatUi.Icon(banner, "Sparkle", "sparkle", ChatUi.Accent);
        ChatUi.Place(sparkle.rectTransform, 10f, (BannerH - 15f) * 0.5f, 15f, 15f);
        bannerLabel = ChatUi.Text(banner, "Label", 12f, ChatUi.MsgText);
        bannerLabel.overflowMode = TextOverflowModes.Ellipsis;
        bannerText = bannerLabel.rectTransform;
        files = ChatUi.Text(banner, "Files", 11f, ChatUi.Accent);
        files.overflowMode = TextOverflowModes.Ellipsis;
        ChatUi.Show(files, false);
        cancel = ChatButton.CreatePill(banner, "Cancel", ChatButton.Ghost, Cancel, 22f, 11.5f);
        ChatUi.Show(banner, false);

        // The box: a grey edge around the field colour, the text area and the toolbar inside.
        box = ChatUi.Rect(root, "Box");
        edge = ChatUi.Surface(box, "Edge", ChatUi.MsgBorder, Corner);
        ChatUi.Stretch(edge.rectTransform, 0f, 0f, 0f, 0f);
        fill = ChatUi.Surface(box, "Fill", ChatUi.MsgField, Corner - Edge);
        ChatUi.Stretch(fill.rectTransform, Edge, Edge, Edge, Edge);

        fieldRect = ChatUi.Rect(box, "Field");
        var hit = fieldRect.gameObject.AddComponent<ChatHitArea>();  // a press anywhere on the field focuses it
        hit.raycastTarget = true;
        var area = ChatUi.Rect(fieldRect, "TextArea");
        ChatUi.Stretch(area, 0f, 0f, 0f, 0f);
        area.gameObject.AddComponent<RectMask2D>();
        text = ChatUi.Text(area, "Text", TextSize, ChatUi.MsgName, align: TextAlignmentOptions.TopLeft, wrap: true);
        text.richText = false;
        ChatUi.Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
        placeholder = ChatUi.Text(area, "Placeholder", TextSize, ChatUi.MsgMuted, align: TextAlignmentOptions.TopLeft, wrap: true);
        placeholder.text = "Message";
        placeholder.overflowMode = TextOverflowModes.Ellipsis;
        ChatUi.Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);

        field = fieldRect.gameObject.AddComponent<TMP_InputField>();
        field.targetGraphic = hit;
        field.transition = Selectable.Transition.None;
        field.navigation = new Navigation { mode = Navigation.Mode.None };
        field.textViewport = area;
        field.textComponent = text;
        field.placeholder = placeholder;
        field.lineType = TMP_InputField.LineType.MultiLineSubmit;  // Enter sends; the Quest keyboard's Done too
        if (ForgeTheme.Font != null) field.fontAsset = ForgeTheme.Font;
        field.pointSize = TextSize;
        field.richText = false;
        field.customCaretColor = true;
        field.caretColor = Color.white;
        field.caretWidth = 1;
        field.selectionColor = new Color(ChatUi.MsgLink.r, ChatUi.MsgLink.g, ChatUi.MsgLink.b, 0.35f);
        field.restoreOriginalTextOnEscape = false;
        field.resetOnDeActivation = false;
        field.characterLimit = 4000;
        field.onSubmit.AddListener(_ => Send());
        field.onValueChanged.AddListener(_ => RefreshButtons());

        // Toolbar inside the box.
        askAlbie = ChatButton.Create(box, "AskAlbie", ChatButton.Ghost, 6f, AskAlbie);
        var askIcon = ChatUi.Icon(askAlbie.transform, "Sparkle", "sparkle", ChatUi.Accent);
        ChatUi.Place(askIcon.rectTransform, 7f, (ToolH - 13f) * 0.5f, 13f, 13f);
        var askLabel = ChatUi.Text(askAlbie.transform, "Label", 11.5f, ChatUi.MsgMuted, bold: true);
        askLabel.text = "Ask Albie";
        float aw = ChatUi.Width(askLabel, askLabel.text);
        ChatUi.Place(askLabel.rectTransform, 25f, 0f, aw + 2f, ToolH);
        askAlbie.Rect.sizeDelta = new Vector2(25f + aw + 9f, ToolH);

        send = ChatButton.Create(box, "Send", ChatButton.SendOff, 6f, Send);
        sendIcon = ChatUi.Icon(send.transform, "Arrow", "send", ChatUi.MsgMuted);
        ChatUi.Place(sendIcon.rectTransform, (SendW - 15f) * 0.5f, (ToolH - 15f) * 0.5f, 15f, 15f);

        caption = ChatUi.Text(root, "Caption", 10.5f, ChatUi.MsgMuted, align: TextAlignmentOptions.Right);
        caption.overflowMode = TextOverflowModes.Ellipsis;
        RefreshButtons();
    }

    /// <summary>Place the composer at (x, y) in its parent, <paramref name="w"/> wide, <see cref="Height"/> tall.</summary>
    public void Layout(float x, float y, float w)
    {
        ChatUi.Place(root, x, y, w, Height);
        float by = 0f;
        if (HasDraft)
        {
            ChatUi.Place(banner, 0f, 0f, w, BannerHeight);
            Vector2 c = cancel.Rect.sizeDelta;
            ChatUi.PlaceRight(cancel.Rect, 6f, (BannerH - c.y) * 0.5f, c.x, c.y);
            ChatUi.Place(bannerText, 32f, 0f, Mathf.Max(20f, w - 32f - c.x - 16f), BannerH);
            ChatUi.Place(files.rectTransform, 32f, BannerH - 6f, Mathf.Max(20f, w - 44f), FilesH);
            by = BannerHeight + Gap;
        }
        ChatUi.Place(box, 0f, by, w, BoxH);
        ChatUi.Place(fieldRect, 14f, FieldTop, w - 28f, FieldH);
        float ty = BoxH - ToolH - 6f;
        Vector2 a = askAlbie.Rect.sizeDelta;
        ChatUi.Place(askAlbie.Rect, 6f, ty, a.x, ToolH);
        ChatUi.PlaceRight(send.Rect, 8f, ty, SendW, ToolH);
        ChatUi.Place(caption.rectTransform, 4f, by + BoxH + 2f, w - 8f, CaptionH - 3f);
    }

    /// <summary>The open conversation (what Send posts to and Ask Albie reads from).</summary>
    public void SetChannel(ChatChannel c)
    {
        channel = c;
        placeholder.text = c == null ? "Pick a conversation" : "Message " + ChatText.DisplayName(c);
        RefreshButtons();
    }

    public void Tick(float dt)
    {
        bool f = field != null && field.isFocused;
        if (f != focused)
        {
            focused = f;
            ForgeInput.TextFieldFocused = f;
            edge.color = f ? ChatUi.MsgBorderFocus : ChatUi.MsgBorder;
        }
        if (f)
        {
            // PointableCanvasModule (Main's only input module) never calls the selected object's OnUpdateSelected, where
            // TMP_InputField pops its key events; drive it like StandaloneInputModule would. Harmless when another module
            // already does (the queue is then empty).
            var events = EventSystem.current;
            if (events != null)
            {
                if (updateEvent == null) updateEvent = new BaseEventData(events);
                updateEvent.Reset();
                ExecuteEvents.Execute(field.gameObject, updateEvent, ExecuteEvents.updateSelectedHandler);
            }
        }
        if (statusTimer > 0f)
        {
            statusTimer -= dt;
            if (statusTimer <= 0f) { statusText = null; RefreshCaption(); }
        }
    }

    public void Disabled()
    {
        if (focused) { focused = false; ForgeInput.TextFieldFocused = false; }
        if (field != null && field.isFocused) field.DeactivateInputField();
    }

    // ---- Sending ----

    void Send()
    {
        if (sending) return;
        string body = field.text != null ? field.text.Trim() : "";
        if (body.Length == 0) { Status("Nothing to send yet"); return; }
        string target = draftId != null ? draftChannel : channel?.id;
        if (ChatStore.Demo && !string.IsNullOrEmpty(target)) { DemoSend(target, body); return; }
        if (ForgeNet.Status != ForgeNetStatus.Live) { Status("Connecting…"); return; }
        if (string.IsNullOrEmpty(target)) { Status("Pick a conversation first"); return; }

        sending = true;
        RefreshButtons();
        Status("Sending…", sticky: true);
        if (draftId != null)
        {
            string id = draftId, thread = draftThread;
            if (body != draftText)
                ChatNet.Patch<NotifyDraft>($"/messages/drafts/{id}", new JObject { ["text"] = body }, _ => { draftText = body; PostSend(id, target, body, thread); }, Fail);
            else PostSend(id, target, body, thread);
        }
        else
        {
            ownText = body;
            ownChannel = target;
            ownDraftId = null;
            ForgeNet.Post<NotifyDraft>("/messages/drafts", new JObject { ["channel"] = target, ["text"] = body }, d =>
            {
                if (d == null || string.IsNullOrEmpty(d.draftId)) { Fail("no draft id"); return; }
                ownDraftId = d.draftId;
                PostSend(d.draftId, target, body, null);
            }, Fail);
        }
    }

    // Hub off: the message stays on the device (MessagesDemo's conversation), as the user's own.
    void DemoSend(string target, string body)
    {
        string thread = draftThread, ts;
        try
        {
            if (draftId != null)
            {
                if (body != draftText) MessagesDemo.EditDraft(body, echo: false);
                ts = MessagesDemo.SendDraft(draftId);
            }
            else ts = MessagesDemo.Post(target, body);
        }
        catch (System.ArgumentException e)
        {
            Status(e.Message);
            return;
        }
        sending = true;  // so Sent() runs for a typed message too
        ChatStore.AddOwn(target, body, ts, thread);
        Sent();
    }

    // The hub answers with the post's ts: the message goes into the conversation right away (notify.sent follows).
    void PostSend(string id, string target, string body, string thread) =>
        ForgeNet.Post<ChatSendReply>($"/messages/drafts/{id}/send", null, reply =>
        {
            if (reply != null) ChatStore.AddOwn(target, body, reply.ts, thread);
            Sent();
        }, Fail);

    void Sent()
    {
        if (!sending && draftId == null) return;
        sending = false;
        field.SetTextWithoutNotify("");
        ClearDraft();
        // ownText / ownChannel / ownDraftId stay until the next Send: the hub's notify.draft for a typed message can
        // land after its send completed, and must still be recognised as ours.
        Status("Sent");
        RefreshButtons();
    }

    void Fail(string error)
    {
        Debug.LogWarning($"[ChatComposer] {error}");
        sending = false;
        Status(error != null && error.Contains("404") ? "That draft is gone (sent, cancelled or expired)" : "Hub didn't answer, try again");
        RefreshButtons();
    }

    void Cancel()
    {
        if (draftId == null) return;
        string id = draftId;
        if (ChatStore.Demo)
        {
            try { MessagesDemo.CancelDraft(id); }
            catch (System.ArgumentException) { }  // already gone: clearing the field is all that's left
        }
        else ForgeNet.Post($"/messages/drafts/{id}/cancel", null, null, Fail);  // notify.draft_cancelled follows
        field.SetTextWithoutNotify("");
        ClearDraft();
        Status("Cancelled");
        RefreshButtons();
    }

    /// <summary>"Ask Albie": messages.focus on the conversation's latest message, so Albie reads it aloud and offers to answer.</summary>
    void AskAlbie()
    {
        var m = owner.LatestMessage();
        if (m == null || string.IsNullOrEmpty(m.ts)) { Status("Nothing here for Albie to read yet"); return; }
        if (!owner.FocusAlbie(m)) Status("Albie can't hear right now");
        else Status("Albie is reading it…");
    }

    // ---- Hub events (routed by ChatPanel) ----

    /// <summary>notify.draft: Albie's draft lands in the field (the panel switches to its conversation); our own drafts are skipped.</summary>
    public void ReceiveDraft(NotifyDraft d)
    {
        if (d == null || string.IsNullOrEmpty(d.draftId)) return;
        if (d.draftId == draftId)
        {
            if (!d.edited) return;
            // Our own PATCH echoed back (the field already says it), or Albie reworded it ("rephrase this",
            // messages_edit_draft): then the field takes his words. Mid-send the field is locked and the echo is ours.
            if (!sending && d.text != null && d.text != (field.text ?? ""))
            {
                field.SetTextWithoutNotify(d.text);
                Status("Albie reworded it");
            }
            draftText = d.text;
            SetFiles(d);
            return;
        }
        if (d.draftId == closedDraftId) return;  // a late echo of a draft already sent or cancelled here
        if (d.draftId == ownDraftId || (ownText != null && d.text == ownText && d.channel == ownChannel)) return;  // typed here, on its way out
        draftId = d.draftId;
        draftText = d.text ?? "";
        draftChannel = d.channel;
        draftThread = d.threadTs;
        draftName = d.channelName;
        sending = false;
        field.SetTextWithoutNotify(draftText);
        SetFiles(d);
        ChatUi.Show(banner, true);
        statusText = null;
        RefreshCaption();
        RefreshButtons();
        if (!string.IsNullOrEmpty(d.channel)) owner.Open(d.channel);
        owner.MarkLayoutDirty();
    }

    public void OnSent(NotifySent s)
    {
        if (s == null) return;
        bool ours = (draftId != null && (string.IsNullOrEmpty(s.draftId) || s.draftId == draftId))
                    || (ownDraftId != null && s.draftId == ownDraftId)
                    || (sending && draftId == null && ownText != null && s.text == ownText);
        if (ours) Sent();
    }

    public void OnDraftCancelled(NotifyDraftCancelled c)
    {
        if (c == null || draftId == null || c.draftId != draftId) return;
        field.SetTextWithoutNotify("");
        ClearDraft();
        sending = false;
        Status(c.reason == "superseded" ? "Replaced by a newer draft" : c.reason == "expired" ? "Draft expired" : "Cancelled");
        RefreshButtons();
    }

    /// <summary>A short line under the box (the panel's message toolbar reports through it).</summary>
    public void ShowStatus(string s) => Status(s);

    // ---- View ----

    /// <summary>The files line for Albie's draft (its attachments go up with the text on Send); hidden without any.</summary>
    void SetFiles(NotifyDraft d)
    {
        string line = d != null ? ChatText.Attachments(d.attachments) : "";
        if (line == filesLine) return;
        filesLine = line;
        files.text = line;
        ChatUi.Show(files, line.Length > 0);
        owner.MarkLayoutDirty();  // the banner grows or shrinks by the line
    }

    void ClearDraft()
    {
        if (draftId != null) closedDraftId = draftId;
        draftId = draftText = draftChannel = draftThread = draftName = null;
        SetFiles(null);
        ChatUi.Show(banner, false);
        RefreshCaption();
        owner.MarkLayoutDirty();
    }

    void Status(string s, bool sticky = false)
    {
        statusText = s;
        statusTimer = sticky ? 0f : StatusSeconds;
        RefreshCaption();
    }

    void RefreshCaption()
    {
        caption.text = statusText ?? "";
        if (draftId != null)
        {
            var c = ChatStore.Find(draftChannel);
            string to = c != null ? ChatText.DisplayName(c) : string.IsNullOrEmpty(draftName) ? "this conversation" : draftName;
            bannerLabel.text = ChatText.BoldOpen + "Albie drafted this" + ChatText.BoldClose + " for " + to + ". Edit it, then Send.";
        }
    }

    void RefreshButtons()
    {
        bool live = ForgeNet.Status == ForgeNetStatus.Live || ChatStore.Demo;
        bool can = live && !sending && (channel != null || draftId != null) && !string.IsNullOrWhiteSpace(field.text);
        if (can != canSend)
        {
            canSend = can;
            send.SetLook(can ? ChatButton.SendOn : ChatButton.SendOff);
            sendIcon.color = can ? Color.white : ChatUi.MsgMuted;
        }
        askAlbie.Interactable = live && channel != null;
        cancel.Interactable = live && !sending;
        field.interactable = !sending;
    }

    /// <summary>The hub came or went: buttons follow.</summary>
    public void OnStatus() => RefreshButtons();
}
