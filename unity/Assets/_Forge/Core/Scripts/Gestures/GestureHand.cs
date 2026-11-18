using UnityEngine;

/// <summary>
/// One tracked hand through time: its joints, features, how it's been moving and the window it's aimed at. The engine
/// updates it once per frame. Times come in as arguments so tests can run it at any frame rate. No allocations.
/// </summary>
public sealed class GestureHand
{
    const int History = 40;
    const float VelocityWindow = 0.09f, TravelWindow = 0.22f;
    const float MinSampleGap = 0.006f;
    // A hand only means a gesture when it is held up in front of you. Typing, resting or putting controllers down does not.
    const float SteadySeconds = 0.5f;   // tracked with high confidence this long first
    const float MaxBelowHead = 0.35f;   // m: a palm lower than this under the eyes is at keyboard or lap height
    // A palm offered to Albie sits at elbow height, about 0.45 to 0.55 m under the eyes. A hand in the lap is lower still.
    const float MaxBelowHeadHeldOut = 0.6f;
    const float MinInView = 0.5f;       // cos 60 degrees: the palm is within this of where the head faces
    const float MinHeadUpright = 0.5f;  // cos 60 degrees: a headset tipped further over (lying on a table) takes no gestures

    public readonly HandSample sample = new HandSample();
    public readonly float[] values = new float[HandFeatures.Count];
    public HandMotion motion;

    /// <summary>Tracked, and the tracker trusts this frame.</summary>
    public bool Usable { get; private set; }
    public float UsableSince { get; private set; }
    /// <summary>Usable for a while and raised in front of the face: only then can a gesture fire.</summary>
    public bool Deliberate { get; private set; }
    /// <summary>
    /// As <see cref="Deliberate"/>, but as low as a hand held out at elbow height. Enough for a gesture a typing or
    /// resting hand doesn't make (<see cref="GestureDef.lowHand"/>): holding the palm out for Albie.
    /// </summary>
    public bool HeldOut { get; private set; }

    /// <summary>The window this hand has aimed at, kept a moment after the aim leaves it, so a flick still knows.</summary>
    public ForgePanel Target { get; set; }
    public float TargetSeenAt { get; set; }

    /// <summary>When this hand last held a point (the engine sets it), so curling the finger after a flick isn't a fist.</summary>
    public float PointedAt { get; set; } = -10f;
    /// <summary>A gesture just fired on this hand: no other starts before this time.</summary>
    public float QuietUntil { get; set; }

    readonly Vector3[] palm = new Vector3[History], tip = new Vector3[History];
    readonly float[] times = new float[History];
    int newest = -1, count;

    public Vector3 Palm => HandFeatures.PalmCenter(sample);
    public Vector3 IndexTip => sample.joints[HandJoint.IndexTip];

    /// <summary>The pointing line: half the finger's own direction, half eye through fingertip, which is how people aim.</summary>
    public Ray AimRay(Vector3 headPosition)
    {
        Vector3 finger = (sample.joints[HandJoint.IndexTip] - sample.joints[HandJoint.Index1]).normalized;
        Vector3 eye = (sample.joints[HandJoint.IndexTip] - headPosition).normalized;
        return new Ray(sample.joints[HandJoint.IndexTip], (finger + eye).normalized);
    }

    public void Update(float now, Vector3 headPosition, Quaternion headRotation)
    {
        bool usable = sample.tracked && sample.highConfidence;
        if (!usable)
        {
            Usable = false;
            Deliberate = false;
            HeldOut = false;
            newest = -1;
            count = 0;
            motion = default;
            return;
        }
        if (!Usable) UsableSince = now;
        Usable = true;

        HandFeatures.Compute(sample, headPosition, headRotation * Vector3.forward, values);
        bool steadyInView = now - UsableSince >= SteadySeconds
            && values[(int)HandFeature.InView] >= MinInView && (headRotation * Vector3.up).y >= MinHeadUpright;
        float belowHead = headPosition.y - Palm.y;
        Deliberate = steadyInView && belowHead <= MaxBelowHead;
        HeldOut = steadyInView && belowHead <= MaxBelowHeadHeldOut;

        // The history has to span TravelWindow at any frame rate (the editor can run far above a headset).
        if (count == 0 || now - times[newest] >= MinSampleGap)
        {
            newest = (newest + 1) % History;
            count = Mathf.Min(count + 1, History);
            palm[newest] = Palm;
            tip[newest] = IndexTip;
            times[newest] = now;
        }

        Quaternion toHead = Quaternion.Inverse(headRotation);
        motion.palmVelocity = HeadFrame(toHead, Rate(palm, now));
        motion.tipVelocity = HeadFrame(toHead, Rate(tip, now));
        motion.palmTravel = HeadFrame(toHead, Travel(palm, now));
        motion.tipTravel = HeadFrame(toHead, Travel(tip, now));
    }

    Vector3 HeadFrame(Quaternion toHead, Vector3 world)
    {
        Vector3 v = toHead * world;
        if (sample.isLeft) v.x = -v.x;
        return v;
    }

    Vector3 Rate(Vector3[] track, float now)
    {
        int oldest = Oldest(now - VelocityWindow);
        float dt = times[newest] - times[oldest];
        return dt > 1e-4f ? (track[newest] - track[oldest]) / dt : Vector3.zero;
    }

    Vector3 Travel(Vector3[] track, float now) => track[newest] - track[Oldest(now - TravelWindow)];

    // The oldest sample no older than `since`. The tracker can repeat a frame or run at 30 Hz: this works in time, not frames.
    int Oldest(float since)
    {
        int best = newest;
        for (int back = 1; back < count; back++)
        {
            int i = (newest - back + History) % History;
            if (times[i] < since) break;
            best = i;
        }
        return best;
    }
}
