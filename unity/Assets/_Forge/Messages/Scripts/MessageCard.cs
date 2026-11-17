using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// One message on a translucent slate card (MessageCard.prefab, pooled by its owners; 1 canvas unit = 1 mm).
/// Two styles: <see cref="Style.Inbox"/> (Mentions panel): initials bubble, name + channel,
/// relative time, up to two lines of text; hover lifts it 1 cm toward the viewer and makes it opaque; click opens the
/// conversation. <see cref="Style.Thread"/> (the chat panel's message list): bubble, name, time, the whole text,
/// "N replies" when the hub reports a thread, the user's own rows tinted. Mentions of the user get a thin lime strip and,
/// on Inbox cards, the lime "@you" sticker tag (<see cref="ChatUi.MentionTag"/>).
/// Owners size the card: <see cref="Measure"/> gives the height for a width, <see cref="Layout"/> places the children.
/// Hover and click arrive as uGUI pointer events from the mouse (DesktopRig) and Interaction SDK rays / pokes alike.
/// </summary>
public class MessageCard : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
    public enum Style { Inbox, Thread }

    [SerializeField] ForgeSurface shadow;
    [SerializeField] ForgeSurface frame;      // the slate fill; alpha RestAlpha at rest, 1 on hover (Inbox)
    [SerializeField] ForgeSurface accent;     // thin strip on the left, on for mentions
    [SerializeField] ForgeSurface bubble;     // sender initials disc
    [SerializeField] TMP_Text initial;
    [SerializeField] TMP_Text nameText;
    [SerializeField] TMP_Text timeText;
    [SerializeField] TMP_Text body;
    [SerializeField] TMP_Text meta;           // Thread: "3 replies"; Inbox: hidden
    [SerializeField] RectTransform mentionTag; // Inbox: the "@you" sticker on mentions (null on a prefab built before it)

    public const float InboxHeight = 72f;
    public const float LiftMm = 10f;          // 1 cm toward the viewer (canvas -Z)
    const float HoverSeconds = 0.18f;
    // Thread geometry (mm).
    const float PadX = 10f, BubbleT = 28f, NameX = 48f, NameY = 7f, LineH = 18f, BodyY = 25f, BodyBottom = 9f, MetaH = 15f, TimeW = 44f;
    // Inbox geometry.
    const float BubbleI = 32f, NameXI = 52f;

    public NotifyMessage Message { get; private set; }
    public Style CardStyle { get; private set; }
    /// <summary>The user clicked or poked the card.</summary>
    public event Action<MessageCard> Clicked;

    RectTransform rect;
    Coroutine anim;
    string rendered;
    float width = -1f, measured;
    bool hovered;

    void Awake()
    {
        rect = (RectTransform)transform;
        if (body != null) body.textWrappingMode = TextWrappingModes.Normal;
        ChatUi.ApplyFontAll(gameObject);
    }

    /// <summary>
    /// Show <paramref name="m"/>. <paramref name="renderedBody"/> is its text as TMP rich text (<see cref="ChatText.Render"/>);
    /// <paramref name="channelLabel"/> ("#forge-team", "DM") sits after the name in the Inbox style only.
    /// Binding the same message again in the same style is a no-op, so pooled rows refill cheaply.
    /// </summary>
    public void Bind(NotifyMessage m, Style style, string renderedBody, string channelLabel = null)
    {
        if (Message == m && CardStyle == style && rendered == renderedBody) return;
        Message = m;
        CardStyle = style;
        rendered = renderedBody ?? "";
        width = -1f;  // re-measure

        string name = ChatText.SenderName(m);
        if (initial != null) initial.text = ChatText.Initials(m);
        if (bubble != null) bubble.color = ChatUi.BubbleColor(string.IsNullOrEmpty(m.user) ? name : m.user);
        if (nameText != null)
        {
            nameText.text = style == Style.Inbox && !string.IsNullOrEmpty(channelLabel)
                ? name + "  <alpha=#80><size=85%>" + channelLabel
                : m.isMe ? name + "  <alpha=#80><size=85%>you" : name;
        }
        body.text = rendered;
        body.color = Color.white;
        body.fontSize = ChatUi.Body;
        body.overflowMode = style == Style.Inbox ? TextOverflowModes.Ellipsis : TextOverflowModes.Overflow;
        if (accent != null) ChatUi.Show(accent, m.mentionsMe && !m.isMe);
        if (mentionTag != null) ChatUi.Show(mentionTag, style == Style.Inbox && m.mentionsMe && !m.isMe);
        if (meta != null)
        {
            bool replies = style == Style.Thread && m.replyCount.HasValue && m.replyCount.Value > 0;
            ChatUi.Show(meta, replies);
            if (replies) meta.text = m.replyCount.Value == 1 ? "1 reply" : m.replyCount.Value + " replies";
        }
        RefreshTime();
        hovered = false;
        if (anim != null) { StopCoroutine(anim); anim = null; }
        ApplyRest();
    }

    public void RefreshTime()
    {
        if (Message != null && timeText != null) timeText.text = ChatText.RelativeTime(Message.ts) + (Message.edited ? " · edited" : "");
    }

    /// <summary>Height (mm) the card needs at <paramref name="w"/> mm wide; measured once per bind and width.</summary>
    public float Measure(float w)
    {
        if (CardStyle == Style.Inbox) return InboxHeight;
        if (Mathf.Approximately(w, width)) return measured;
        width = w;
        float textW = Mathf.Max(20f, w - NameX - PadX);
        float h = BodyY + ChatUi.Height(body, rendered, textW) + BodyBottom;
        if (meta != null && meta.gameObject.activeSelf) h += MetaH;
        measured = Mathf.Max(BubbleT + 16f, h);
        return measured;
    }

    /// <summary>Place the children for a card <paramref name="w"/> x <paramref name="h"/> (the owner set the card's rect).</summary>
    public void Layout(float w, float h)
    {
        bool inbox = CardStyle == Style.Inbox;
        float b = inbox ? BubbleI : BubbleT;
        float nameX = inbox ? NameXI : NameX;
        if (bubble != null) ChatUi.Place(bubble.rectTransform, PadX, inbox ? 10f : 8f, b, b);
        if (accent != null) ChatUi.Place(accent.rectTransform, 2f, 10f, 3f, Mathf.Max(8f, h - 20f));
        if (timeText != null) ChatUi.PlaceRight(timeText.rectTransform, PadX, inbox ? 9f : NameY + 1f, TimeW + 30f, LineH);
        if (nameText != null) ChatUi.Place(nameText.rectTransform, nameX, inbox ? 8f : NameY, Mathf.Max(20f, w - nameX - PadX - TimeW), LineH);
        float bodyY = inbox ? 26f : BodyY;
        float bodyH = inbox ? h - bodyY - 8f : Mathf.Max(12f, h - bodyY - BodyBottom - (meta != null && meta.gameObject.activeSelf ? MetaH : 0f));
        float tagW = 0f;
        if (mentionTag != null && mentionTag.gameObject.activeSelf)
        {
            // Bottom right, clear of its own hard shadow; the text gives up the tag's width.
            Vector2 t = mentionTag.sizeDelta;
            ChatUi.PlaceRight(mentionTag, PadX + MessagesLook.TagShadow, h - 8f - MessagesLook.TagShadow - t.y, t.x, t.y);
            tagW = t.x + MessagesLook.TagShadow + 6f;
        }
        ChatUi.Place(body.rectTransform, nameX, bodyY, Mathf.Max(20f, w - nameX - PadX - tagW), bodyH);
        if (meta != null && meta.gameObject.activeSelf) ChatUi.Place(meta.rectTransform, nameX, bodyY + bodyH, Mathf.Max(20f, w - nameX - PadX), MetaH);
    }

    public void OnPointerEnter(PointerEventData eventData) => SetHover(true);
    public void OnPointerExit(PointerEventData eventData) => SetHover(false);
    public void OnPointerClick(PointerEventData eventData) => Clicked?.Invoke(this);

    void OnDisable()
    {
        hovered = false;
        if (anim != null) { StopCoroutine(anim); anim = null; }
        ApplyRest();
    }

    Color RestFill => Message != null && Message.isMe ? ChatUi.MeFill : ChatUi.CardFill;

    void ApplyRest()
    {
        if (frame != null) frame.color = RestFill;
        if (rect != null)
        {
            Vector3 p = rect.localPosition;
            p.z = 0f;
            rect.localPosition = p;
        }
    }

    void SetHover(bool on)
    {
        if (Message == null || on == hovered) return;
        hovered = on;
        if (CardStyle == Style.Thread)
        {
            // List rows: just a touch brighter, no motion (the list may be scrolling under the pointer).
            Color c = RestFill;
            if (on) c.a = Mathf.Min(1f, c.a + 0.12f);
            frame.color = c;
            return;
        }
        Color rest = RestFill;
        float a1 = on ? 1f : rest.a, z1 = on ? -LiftMm : 0f;
        if (anim != null) { StopCoroutine(anim); anim = null; }
        if (!isActiveAndEnabled)
        {
            Apply(a1, z1);
            return;
        }
        float a0 = frame.color.a, z0 = rect.localPosition.z;
        anim = StartCoroutine(ForgeTween.Run(HoverSeconds, 0f, k =>
        {
            float e = ForgeTween.EaseOutCubic(k);
            Apply(Mathf.Lerp(a0, a1, e), Mathf.Lerp(z0, z1, e));
        }, () => anim = null));
    }

    void Apply(float alpha, float zMm)
    {
        Color c = RestFill;
        c.a = alpha;
        frame.color = c;
        Vector3 p = rect.localPosition;  // the owner places x/y; z is ours
        p.z = zMm;
        rect.localPosition = p;
    }
}
