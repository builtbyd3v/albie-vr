using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Hub off: Albie speaks up on his own. A new message (notify.message, not the user's own, not while the messages view
/// is open) or a finished training run (<see cref="RunFinished"/>) becomes a short tip. At most one tip every
/// <see cref="MinGapS"/> seconds; a newer tip replaces one still waiting. Never while Albie listens, thinks or talks:
/// it waits for pet.state idle/follow and for the arrival card to go (AlbieVoice also holds an albie.say until the
/// user's turn is over).
/// Voice on: albie.say, so the voice model says it in its own words. Voice off: pet.say, the caption.
/// Hub live: off (the hub does its own). Installs itself.
/// </summary>
public class AlbieTips : MonoBehaviour
{
    public const float MinGapS = 120f;
    const string PetId = "bit";

    static AlbieTips instance;
    string pending, pendingHint;  // the caption, and what the voice model should do with it
    float lastTip = -MinGapS;
    bool busy;  // Albie is listening / thinking / working / speaking
    Forge.Pet.AlbieCaption caption;  // Albie's bubble: muted while the arrival card is up, so hold tips then

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install() => instance = new GameObject(nameof(AlbieTips)).AddComponent<AlbieTips>();

    /// <summary>A training run just finished: <paramref name="line"/> is the result in one sentence.</summary>
    public static void RunFinished(string line) => Tip("Training finished. " + line, "Offer to show the results.");

    /// <summary>Queue a tip (the newest waiting one wins). <paramref name="hint"/> is for the voice model only.</summary>
    public static void Tip(string line, string hint = null)
    {
        if (instance == null || string.IsNullOrEmpty(line) || !ForgeNet.IsMock) return;
        instance.pending = line;
        instance.pendingHint = hint;
    }

    void OnEnable()
    {
        ForgeEvents.OnNotifyMessage += OnMessage;
        ForgeEvents.OnPetState += OnPetState;
    }

    void OnDisable()
    {
        ForgeEvents.OnNotifyMessage -= OnMessage;
        ForgeEvents.OnPetState -= OnPetState;
    }

    void OnMessage(NotifyMessage m)
    {
        if (m == null || m.isMe || string.IsNullOrEmpty(m.text)) return;
        var ws = Workspace.Current;
        if (ws != null && ws.ActiveMode == "messages") return;  // it's on screen already
        string where = m.isDm ? "a direct message" : "a message in " + m.channelName;
        Tip($"{m.userName ?? "Someone"} sent {where}: \"{m.text}\"", "Mention it briefly and offer to answer it.");
    }

    void OnPetState(PetState s)
    {
        string mode = s?.mode ?? "";
        busy = mode == "listening" || mode == "thinking" || mode == "working" || mode == "speaking";
    }

    void Update()
    {
        if (pending == null || busy || Time.realtimeSinceStartup - lastTip < MinGapS) return;
        if (caption == null)
        {
            var bridge = FindAnyObjectByType<Forge.Pet.AlbieAgentBridge>();
            if (bridge != null) caption = bridge.GetComponentInChildren<Forge.Pet.AlbieCaption>();
        }
        if (caption != null && caption.Muted) return;
        lastTip = Time.realtimeSinceStartup;
        string line = pending;
        pending = null;
        Debug.Log("[AlbieTips] " + line);
        if (ForgeNet.LocalPeer is AlbieVoice) ForgeNet.Send("albie.say", new JObject { ["text"] = pendingHint == null ? line : line + " " + pendingHint });
        else ForgeNet.Inject("pet.say", new JObject { ["pet_id"] = PetId, ["text"] = line });
    }
}
