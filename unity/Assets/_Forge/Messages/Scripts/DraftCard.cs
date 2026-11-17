using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// "Send this?" card (DraftCard.prefab, a ForgePanel variant, panel id <c>messages-draft</c>). Shows a reply Albie wrote
/// but has NOT sent (notify.draft, PROJECT_CONTEXT 7 / 8.8): who it goes to, what it answers, the exact text, and
/// Send / Cancel. Send -> POST /messages/drafts/{id}/send (the hub posts it under the user's own name);
/// Cancel -> POST /messages/drafts/{id}/cancel. The card closes itself on notify.sent / notify.draft_cancelled for
/// its draft, so saying "send it" to Albie (the messages_confirm tool) or a newer draft (reason superseded) dismisses it
/// too. Spawned and placed by <see cref="DraftPresenter"/> only while the chat panel is closed (open, the draft goes to
/// its composer instead). Buttons are uGUI Buttons (ChatButton pills), so mouse (DesktopRig) and Interaction SDK
/// ray/poke (wired on the ForgePanel prefab) both work. Look: the module's translucent slate card inside the graphite frame,
/// the draft itself on Albie's white sticker (he wrote it) with his picture at the top right, Send in brand orange. The card is the confirmation: one tap
/// of Send posts it, Cancel drops it.
/// </summary>
public class DraftCard : MonoBehaviour
{
    /// <summary>Panel size (m); the builder and the presenter's docking math both use it.</summary>
    public static readonly Vector2 SizeMeters = new Vector2(0.42f, 0.26f);
    const float LingerSeconds = 0.8f, SentLingerSeconds = 1.8f;  // "Sent to #x" stays long enough to read
    static readonly char[] LineBreaks = { '\n', '\r' };

    [SerializeField] ForgeSurface sheet;   // translucent slate card inside the panel (text stays opaque)
    [SerializeField] TMP_Text header;      // "#forge-team" / "DM to Hudson"
    [SerializeField] TMP_Text replying;    // "Replying to Hudson: is the build ready?"
    [SerializeField] TMP_Text body;        // the exact text that will be sent
    [SerializeField] RectTransform bubble; // Albie's sticker around the body (null on a prefab built before it)
    [SerializeField] TMP_Text status;      // "Sending…", "Sent", "Cancelled", errors
    [SerializeField] Button send;
    [SerializeField] Button cancel;
    TMP_Text files;                        // the draft's attachments (see ShowFiles); null until a draft has some

    /// <summary>Editor checks only: gets each POST path instead of the hub (null = the real ForgeNet.Post).</summary>
    public static System.Action<string> PostHook;

    public NotifyDraft Draft { get; private set; }
    bool busy;  // a Send or Cancel is on its way: a second tap waits for the hub's answer
    string recipient;  // "#forge-team" / "Hudson": where Send posts it
    Color statusColor;
    bool statusColorKnown;
    static readonly Color SentColor = new Color(0.17f, 0.67f, 0.46f);  // green: it went out
    /// <summary>Sent or cancelled; the card is on its way out.</summary>
    public bool Closing { get; private set; }
    /// <summary>What to destroy when the card closes (the presenter's holder); defaults to this object.</summary>
    public GameObject Root { get; set; }

    void Awake()
    {
        ChatUi.ApplyFontAll(gameObject);
        AddFace();
        send.onClick.AddListener(Send);
        cancel.onClick.AddListener(Cancel);
    }

    // Albie's picture at the top right of the content, above his sticker; the two lines beside him give up its width.
    // Made at runtime so a missing texture just leaves it out. Takes no pointer.
    void AddFace()
    {
        var parent = header.rectTransform.parent;
        var face = ChatUi.Portrait(parent, "AlbieFace", MessagesLook.DraftFaceWidth);
        if (face == null) return;
        Vector2 size = face.rectTransform.sizeDelta;
        ChatUi.PlaceRight(face.rectTransform, StickerSide, StickerTop - 5f - size.y, size.x, size.y);
        float inset = StickerSide + size.x + 8f;
        foreach (var t in new[] { header, replying })
            t.rectTransform.offsetMax = new Vector2(-inset, t.rectTransform.offsetMax.y);
    }

    void OnEnable()
    {
        ForgeEvents.OnNotifySent += OnSent;
        ForgeEvents.OnNotifyDraftCancelled += OnCancelled;
    }

    void OnDisable()
    {
        ForgeEvents.OnNotifySent -= OnSent;
        ForgeEvents.OnNotifyDraftCancelled -= OnCancelled;
    }

    public void Bind(NotifyDraft d)
    {
        Draft = d;
        Closing = false;
        busy = false;
        NotifyMessage to = d.replyTo;
        // The hub names a DM draft after the person (channels stay bare), so the kind comes from the conversation list
        // (by channel id) or the message being answered, never from the name.
        var conversation = ChatStore.Find(d.channel);
        bool dm = conversation != null ? ChatText.IsDmKind(conversation.kind)
            : (to != null && to.isDm) || d.channelName == "dm" || d.channelName == "group-dm";
        if (dm)
        {
            recipient = !string.IsNullOrEmpty(d.channelName) && d.channelName != "dm" && d.channelName != "group-dm" ? d.channelName
                : to != null && !string.IsNullOrEmpty(to.userName) ? to.userName : null;
            header.text = recipient != null ? $"DM to {recipient}" : "Direct message";
        }
        else
        {
            recipient = string.IsNullOrEmpty(d.channelName) ? null : $"#{ChatText.Bare(d.channelName)}";
            header.text = recipient ?? "Message";
        }

        bool hasReply = to != null && !string.IsNullOrEmpty(to.text);
        replying.gameObject.SetActive(hasReply);
        if (hasReply) replying.text = $"Replying to {ChatText.SenderName(to)}: {FirstLine(to.text)}";

        body.text = d.text ?? "";
        ShowFiles(ChatText.Attachments(d.attachments));
        if (!statusColorKnown) { statusColor = status.color; statusColorKnown = true; }
        status.color = statusColor;
        status.text = d.edited ? "Edited by Albie" : "Albie wrote this";
        SetButtons(true);
    }

    // The draft's files (Addendum H), one line above the buttons: "4 files · STEP, STL, Drawing PDF, Parts list · 1.2 MB".
    // Made here rather than in the prefab, which predates attachments; the body gives up the line's height while it shows.
    // Shared with the builder: the sticker's insets in Content (mm), above the 36 mm buttons and clear of its shadow.
    public const float StickerTop = 50f, StickerSide = 14f, StickerBottom = 58f, BodyPad = 10f;
    const float FilesBottom = 54f, FilesH = 18f, BodyInset = StickerBottom;

    void ShowFiles(string line)
    {
        if (files == null && line.Length > 0)
        {
            files = ChatUi.Text(bubble != null ? bubble.parent : body.transform.parent, "Files", ChatUi.Small, ChatUi.AccentA(0.95f));
            files.overflowMode = TextOverflowModes.Ellipsis;
            var rt = files.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.offsetMin = new Vector2(14f, FilesBottom);
            rt.offsetMax = new Vector2(-14f, FilesBottom + FilesH);
        }
        if (files == null) return;
        files.text = line;
        files.gameObject.SetActive(line.Length > 0);
        var box = bubble != null ? bubble : body.rectTransform;  // the sticker moves up for the files line
        box.offsetMin = new Vector2(box.offsetMin.x, line.Length > 0 ? FilesBottom + FilesH + 4f + MessagesLook.StickerShadow : BodyInset);
    }

    public void Send()
    {
        if (Draft == null || Closing || busy) return;
        busy = true;
        SetButtons(false);
        status.text = "Sending…";
        if (Hooked($"/messages/drafts/{Draft.draftId}/send")) return;
        if (ChatStore.Demo) { Demo(() => MessagesDemo.SendDraft(Draft.draftId)); return; }  // notify.sent closes the card
        ForgeNet.Post($"/messages/drafts/{Draft.draftId}/send", null, null, Fail);  // confirmation arrives as notify.sent
    }

    public void Cancel()
    {
        if (Draft == null || Closing || busy) return;
        busy = true;
        SetButtons(false);
        status.text = "Cancelling…";
        if (Hooked($"/messages/drafts/{Draft.draftId}/cancel")) return;
        if (ChatStore.Demo) { Demo(() => MessagesDemo.CancelDraft(Draft.draftId)); return; }
        ForgeNet.Post($"/messages/drafts/{Draft.draftId}/cancel", null, null, Fail);  // arrives as notify.draft_cancelled
    }

    static bool Hooked(string path)
    {
        if (PostHook == null) return false;
        PostHook(path);
        return true;
    }

    // Hub off: the device's own drafts (MessagesDemo); its events close the card like the hub's.
    void Demo(System.Action act)
    {
        try { act(); }
        catch (System.ArgumentException e) { Close(e.Message); }
    }

    void Fail(string error)
    {
        if (Closing) return;
        busy = false;
        Debug.LogWarning($"[DraftCard] {error}");
        status.text = "Hub didn't answer, try again";
        SetButtons(true);
    }

    void OnSent(NotifySent s)
    {
        if (Draft == null || Closing) return;
        if (!string.IsNullOrEmpty(s.draftId) && s.draftId != Draft.draftId) return;  // no draft_id = the open draft
        status.color = SentColor;
        Close(recipient != null ? $"Sent to {recipient}" : "Sent", SentLingerSeconds);
    }

    void OnCancelled(NotifyDraftCancelled c)
    {
        if (Draft == null || Closing || c.draftId != Draft.draftId) return;
        Close(c.reason == "superseded" ? "Replaced" : c.reason == "expired" ? "Expired" : "Cancelled");
    }

    void Close(string label, float linger = LingerSeconds)
    {
        Closing = true;
        SetButtons(false);
        status.text = label;
        StartCoroutine(CloseLater(linger));
    }

    IEnumerator CloseLater(float linger)
    {
        yield return new WaitForSeconds(linger);
        DigestTrigger.FoldOut(this, Root != null ? Root : gameObject);
    }

    void SetButtons(bool on)
    {
        send.interactable = on;
        cancel.interactable = on;
    }

    static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int cut = text.IndexOfAny(LineBreaks);
        return cut < 0 ? text : text.Substring(0, cut);
    }
}
