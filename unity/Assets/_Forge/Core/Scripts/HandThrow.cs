using UnityEngine;

/// <summary>
/// Tells a throw from a put-down, from where the hand was over the last fraction of a second (a palm, a pinch point, a
/// controller or hand ray's origin: whatever moves with the hand, in metres, world space). Feed it with <see cref="Begin"/>
/// when something is picked up, <see cref="Sample"/> every frame it is held, and ask <see cref="Release"/> when it is let go.
/// <para>
/// A throw is all of: held at least <see cref="MinHold"/>; the hand's speed over the last <see cref="Window"/> reached
/// <see cref="ArmSpeed"/> and had not dropped under <see cref="KeepSpeed"/> for more than <see cref="ArmGrace"/> when it let
/// go (hysteresis: a pinch opens a little after the arm stops, and the speed has to fall well below the arming speed to
/// disarm); the hand covered at least <see cref="MinTravel"/> in the last <see cref="TravelWindow"/>; and it was moving away
/// from the eyes on the floor plan (within about 70 degrees of the way from the eyes to where it was picked up) and not
/// mostly downwards (that is putting it down on a table).
/// A step faster than <see cref="MaxSpeed"/> is a tracking jump, not a hand: the history starts again from there.
/// <see cref="Cancel"/> (two hands on it, resizing) rules out a throw until the next <see cref="Begin"/>.
/// </para>
/// Uses only Vector3 arithmetic, no Unity objects, and allocates nothing after construction, so it runs every frame and
/// the checks (and a standalone test with no engine) can drive it with made-up hands.
/// </summary>
public sealed class HandThrow
{
    /// <summary>Velocity is measured over this much of the hand's latest path (s).</summary>
    public const float Window = 0.10f;
    /// <summary>The hand has to reach this speed (m/s) to arm a throw. Placing and carrying stay under about 0.8 m/s.</summary>
    public const float ArmSpeed = 1.4f;
    /// <summary>Once armed, the throw stays armed while the hand is at least this fast (m/s).</summary>
    public const float KeepSpeed = 0.8f;
    /// <summary>A release this soon (s) after the hand slowed under <see cref="KeepSpeed"/> still throws.</summary>
    public const float ArmGrace = 0.12f;
    /// <summary>The hand must have covered this much ground (m) in the last <see cref="TravelWindow"/> seconds.</summary>
    public const float MinTravel = 0.12f;
    public const float TravelWindow = 0.25f;
    /// <summary>Held for less than this (s) is a tap or a fumble, never a throw.</summary>
    public const float MinHold = 0.20f;
    /// <summary>Cosine of the widest angle (on the floor plan) from straight away from the eyes a throw may go (0.34: about 70 degrees).</summary>
    public const float AwayCos = 0.34f;
    /// <summary>The throw direction's up component must be above this (-0.7: no steeper than about 45 degrees down).</summary>
    public const float MaxDown = -0.7f;
    /// <summary>A single step faster than this (m/s) is a tracking jump: ignored.</summary>
    public const float MaxSpeed = 12f;

    const int Size = 48;  // 0.25 s at 120 Hz, with room to spare
    readonly float[] times = new float[Size];
    readonly Vector3[] points = new Vector3[Size];
    int count, next;
    float grabbedAt, lastFast;
    bool armed, cancelled, begun;
    Vector3 peak, start;
    float peakSpeed;

    /// <summary>Armed now: a release this frame would be a throw if the direction and travel are right.</summary>
    public bool Armed => armed && !cancelled;
    /// <summary>Picked up and not yet let go.</summary>
    public bool Holding => begun;
    /// <summary>The hand's speed over the last <see cref="Window"/> (m/s), as of the last sample.</summary>
    public float Speed { get; private set; }

    /// <summary>Picked up at <paramref name="time"/> (s) with the hand at <paramref name="hand"/>.</summary>
    public void Begin(float time, Vector3 hand)
    {
        count = 0;
        next = 0;
        armed = false;
        cancelled = false;
        begun = true;
        peak = Vector3.zero;
        peakSpeed = 0f;
        Speed = 0f;
        grabbedAt = time;
        start = hand;
        Push(time, hand);
    }

    /// <summary>Two hands on it, or something else that is not a throw: no throw until the next <see cref="Begin"/>.</summary>
    public void Cancel()
    {
        cancelled = true;
        armed = false;
    }

    /// <summary>Forget the grab (dropped without a release to judge).</summary>
    public void End()
    {
        begun = false;
        armed = false;
        count = 0;
    }

    /// <summary>The hand is at <paramref name="hand"/> at <paramref name="time"/>, still holding.</summary>
    public void Sample(float time, Vector3 hand)
    {
        if (!begun) return;
        if (count > 0)
        {
            int newest = (next - 1 + Size) % Size;
            float dt = time - times[newest];
            if (dt <= 1e-5f) return;  // the same frame twice
            if ((hand - points[newest]).magnitude / dt > MaxSpeed)
            {
                // A tracking jump: start the path again from here, keep the hold time.
                count = 0;
                next = 0;
                armed = false;
                Push(time, hand);
                return;
            }
        }
        Push(time, hand);
        Vector3 v = VelocityOver(Window);
        float speed = v.magnitude;
        Speed = speed;
        if (cancelled) return;
        if (speed >= ArmSpeed || (armed && speed >= KeepSpeed))
        {
            if (!armed) peakSpeed = 0f;
            armed = true;
            lastFast = time;
            if (speed > peakSpeed)
            {
                peakSpeed = speed;
                peak = v;
            }
        }
        else if (armed && time - lastFast > ArmGrace)
        {
            armed = false;
        }
    }

    /// <summary>
    /// Let go at <paramref name="time"/>: true when it was a throw, with <paramref name="velocity"/> the hand's fastest
    /// velocity while armed (m/s). <paramref name="eye"/> is where the user looks from and <paramref name="held"/> where
    /// the thrown thing is, for "away from the user". Ends the grab either way.
    /// </summary>
    public bool Release(float time, Vector3 eye, Vector3 held, out Vector3 velocity)
    {
        velocity = Vector3.zero;
        bool was = begun;
        begun = false;
        if (!was || cancelled || !armed) return false;
        if (time - grabbedAt < MinHold) return false;
        if (time - lastFast > ArmGrace) return false;
        if (Travel(TravelWindow) < MinTravel) return false;
        if (peakSpeed <= 1e-4f) return false;
        Vector3 dir = peak / peakSpeed;
        if (dir.y < MaxDown) return false;
        // Away: on the floor plan, from the eyes toward where the hand picked it up (or the thing itself, if the hand was
        // right at the eyes). The hand's path itself would bend that toward wherever it was thrown.
        Vector3 away = Flat(start - eye);
        if (away.magnitude < 0.05f) away = Flat(held - eye);
        Vector3 flatDir = Flat(dir);
        float far = away.magnitude, along = flatDir.magnitude;
        if (far > 1e-4f)
        {
            if (along < 0.2f) return false;  // straight up or down: a toss in place, not away
            if (Vector3.Dot(flatDir / along, away / far) < AwayCos) return false;
        }
        velocity = peak;
        return true;
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    void Push(float time, Vector3 hand)
    {
        times[next] = time;
        points[next] = hand;
        next = (next + 1) % Size;
        if (count < Size) count++;
    }

    // From the newest sample back to the first one at least `span` seconds older (or the oldest there is).
    Vector3 VelocityOver(float span)
    {
        if (count < 2) return Vector3.zero;
        int newest = (next - 1 + Size) % Size;
        int oldest = newest;
        for (int i = 1; i < count; i++)
        {
            int k = (newest - i + Size) % Size;
            oldest = k;
            if (times[newest] - times[k] >= span) break;
        }
        float dt = times[newest] - times[oldest];
        return dt > 1e-4f ? (points[newest] - points[oldest]) / dt : Vector3.zero;
    }

    // How far the hand went in a straight line over the last `span` seconds.
    float Travel(float span)
    {
        if (count < 2) return 0f;
        int newest = (next - 1 + Size) % Size;
        int oldest = newest;
        for (int i = 1; i < count; i++)
        {
            int k = (newest - i + Size) % Size;
            if (times[newest] - times[k] > span) break;
            oldest = k;
        }
        return (points[newest] - points[oldest]).magnitude;
    }
}
