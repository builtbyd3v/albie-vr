using Forge.Pet;
using UnityEngine;

/// <summary>
/// Albie in your hand. Hold your hand out palm up and he jumps onto it, sits and listens. He stays on your palm as it
/// moves. Close your fist, turn your hand over or take it out of view and he hops back where he came from. The mic opens
/// once when he lands and again after each answer; if nothing is said it stays closed until you call him again, so this
/// never leaves a paid voice session listening to the room.
/// </summary>
public sealed class GesturePerch
{
    const float FollowRate = 16f;       // 1/s: how tightly he follows the palm; lower is steadier, higher is snappier
    const float PalmLift = 0.012f;      // m above the palm
    const float LostSeconds = 1.2f;     // hand out of tracking this long and he hops down
    const float TurnedSeconds = 0.5f;   // hand turned over this long and he hops down
    const float ListenAgainDelay = 0.6f;
    const float NoSpeechSeconds = 15f;

    readonly GestureEngine engine;
    GatorController gator;
    PushToTalk talk;
    GestureHand hand;
    Transform anchor;
    bool landed, keepListening;
    float lostAt, turnedAt, listenAt;

    public bool Active => hand != null;

    public GesturePerch(GestureEngine engine) => this.engine = engine;

    GatorController Gator()
    {
        if (gator != null) return gator;
        var crew = Object.FindAnyObjectByType<GatorCrew>();
        if (crew != null && crew.Lead != null) gator = crew.Lead;
        if (gator == null) gator = Object.FindAnyObjectByType<GatorController>();
        return gator;
    }

    public string Call(GestureHand fromHand)
    {
        if (Active) return null;
        GatorController g = Gator();
        if (g == null) return "No Albie here";

        if (anchor == null) anchor = new GameObject("AlbiePalm").transform;
        MoveAnchor(fromHand, 1f);
        if (!g.PerchOn(anchor)) return "Albie's busy";

        hand = fromHand;
        landed = false;
        keepListening = true;
        lostAt = turnedAt = -1f;
        g.PerchArrived -= OnArrived;
        g.PerchArrived += OnArrived;
        return "Come here, Albie";
    }

    public string Release()
    {
        if (!Active) return null;
        Leave();
        return "Off you go";
    }

    public string Come()
    {
        GatorController g = Gator();
        if (g == null) return "No Albie here";
        g.ReleaseStation();
        if (Active) Leave(g.ComeToUser);  // he has to land first: the walk needs his agent back
        else g.ComeToUser();
        return null;
    }

    public void Tick(Transform head)
    {
        if (!Active) return;
        float now = Time.time;

        if (!hand.Usable)
        {
            if (lostAt < 0f) lostAt = now;
            else if (now - lostAt > LostSeconds) Leave();
            return;
        }
        lostAt = -1f;

        if (GestureLibrary.CanHoldPerch(hand.values)) turnedAt = -1f;
        else if (turnedAt < 0f) turnedAt = now;
        else if (now - turnedAt > TurnedSeconds)
        {
            Leave();
            return;
        }

        MoveAnchor(hand, 1f - Mathf.Exp(-FollowRate * Time.deltaTime));
        Listen(now);
    }

    void MoveAnchor(GestureHand from, float blend)
    {
        Vector3 target = from.Palm + HandFeatures.PalmNormal(from.sample) * PalmLift;
        anchor.position = Vector3.Lerp(anchor.position, target, blend);
    }

    void OnArrived()
    {
        landed = true;
        listenAt = Time.time;
    }

    // Open the mic when he has landed, and after each answer. One silence and it stays closed.
    void Listen(float now)
    {
        if (!landed || !keepListening || now < listenAt) return;
        if (talk == null) talk = Object.FindAnyObjectByType<PushToTalk>();
        if (talk == null || talk.SessionActive) return;
        GatorState state = gator.State;
        if (state == GatorState.Thinking || state == GatorState.Answering || state == GatorState.Working) return;
        if (!talk.CanListenHandsFree || !talk.BeginHandsFree(NoSpeechSeconds))
        {
            listenAt = now + 2f;
            return;
        }
        talk.SessionEnded -= OnSessionEnded;
        talk.SessionEnded += OnSessionEnded;
    }

    void OnSessionEnded(bool answered)
    {
        talk.SessionEnded -= OnSessionEnded;
        if (answered) listenAt = Time.time + ListenAgainDelay;
        else keepListening = false;
    }

    void Leave(System.Action thenDo = null)
    {
        hand = null;
        keepListening = false;
        if (talk != null)
        {
            talk.SessionEnded -= OnSessionEnded;
            if (talk.HandsFree) talk.CancelSession();
        }
        if (gator != null)
        {
            gator.PerchArrived -= OnArrived;
            gator.LeavePerch(thenDo);
        }
    }
}
