public enum CloudState { Unknown, Online, Waking, Offline }

/// <summary>
/// Albie's cloud health from its /health answers, with no networking (<see cref="ForgeCloud"/> probes; Forge > Core >
/// Check Cloud Offline feeds it fake answers). 200 with {"ok"}: Online. A paused Space or a refused key: Offline. Any
/// other answer, or none (a sleeping Space answers 503 or a loading page while it boots, or times out): Waking, for up
/// to <see cref="WakeS"/>, then Offline (no network ends up here too, 90 s later). Offline stays Offline until a 200.
/// Albie says a change only after the user asked for a cloud feature (<see cref="Ask"/>), once per change: never on
/// every call.
/// </summary>
public class CloudHealth
{
    public const float WakeS = 90f;

    public const string WakingLine = "The workshop is waking up. It takes about a minute; I'll tell you when it's ready.";
    public const string OfflineLine = "CAD and model training are offline right now. Everything else on the headset still works.";
    public const string AwakeLine = "The workshop is awake. CAD and model training work again.";
    public const string WakingStatus = "Waking up the workshop (Albie's cloud), about a minute.";
    public const string OfflineStatus = "Albie's cloud is offline right now.";

    public CloudState State { get; private set; } = CloudState.Unknown;
    float wakeStarted;
    bool asked;  // the user asked for a cloud feature while it wasn't Online: say when that changes

    /// <summary>One /health answer (<paramref name="code"/> 0 = no answer at all). Returns the line Albie should say
    /// now, or null.</summary>
    public string Report(long code, string body, float now)
    {
        var before = State;
        body = body ?? "";
        if (code == 200 && body.Contains("\"ok\"")) State = CloudState.Online;
        else if (code == 401 || code == 403 || body.ToLowerInvariant().Contains("paused")) State = CloudState.Offline;
        else if (State != CloudState.Offline)  // booting: a 503, a 502, a 200 loading page, or a timeout (0)
        {
            if (State != CloudState.Waking) wakeStarted = now;
            State = now - wakeStarted < WakeS ? CloudState.Waking : CloudState.Offline;
        }
        if (State == before || !asked) return null;
        if (State == CloudState.Online) { asked = false; return AwakeLine; }
        return State == CloudState.Offline ? OfflineLine : null;
    }

    /// <summary>The user asked for a cloud feature. Returns what to answer them, or null when the cloud is Online.</summary>
    public string Ask()
    {
        if (State == CloudState.Online) return null;
        asked = true;
        return State == CloudState.Offline ? OfflineLine : WakingLine;  // Unknown: the first probe is still out
    }

    /// <summary>The hub serves CAD and ML: forget the cloud until the hub goes away.</summary>
    public void Standby()
    {
        State = CloudState.Unknown;
        asked = false;
    }

    /// <summary>One line for a panel's status, or null when there's nothing to say.</summary>
    public string Status => State == CloudState.Waking ? WakingStatus : State == CloudState.Offline ? OfflineStatus : null;
}
