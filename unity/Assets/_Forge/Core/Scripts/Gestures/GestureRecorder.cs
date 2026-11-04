using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// "Show me": three timed takes of a gesture, turned into a <see cref="GestureDef"/>. Each take records features and
/// motion. Analyze decides whether it was a pose, a move (a pose then a fast movement) or a change from one pose to
/// another, then checks the takes agree and the result isn't a resting hand. Tick it once per frame.
/// </summary>
public sealed class GestureRecorder
{
    public const int Takes = 3;
    const float GetReadySeconds = 2.5f, CaptureSeconds = 1.6f, BetweenSeconds = 1.3f;
    const float MinFrameGap = 0.006f;
    const int MaxFrames = 280;  // 1.6 s, one frame per MinFrameGap at most
    const int MaxRetries = 3;

    public struct Frame
    {
        public float time;
        public float[] values;
        public HandMotion motion;
    }

    public sealed class Take
    {
        public readonly List<Frame> frames = new List<Frame>();
    }

    enum Step { GetReady, Capture, Done }

    public string Prompt { get; private set; } = "";
    public bool Finished => step == Step.Done;
    public int TakeIndex { get; private set; }
    public readonly List<Take> Recorded = new List<Take>();

    readonly float[][] pool = new float[MaxFrames][];
    Step step;
    float stepEnds, lastFrame;
    int used, retries, shownSecond = -1;
    string note = "";
    Take take;

    public GestureRecorder(float now)
    {
        for (int i = 0; i < MaxFrames; i++) pool[i] = new float[HandFeatures.Count];
        Prompt = $"Take 1 of {Takes}";
        Begin(now, GetReadySeconds);
    }

    void Begin(float now, float wait)
    {
        step = Step.GetReady;
        stepEnds = now + wait;
    }

    /// <summary>The hand to record is the one most in view; null while neither is tracked.</summary>
    public void Tick(float now, GestureHand hand)
    {
        if (step == Step.Done) return;
        if (step == Step.GetReady)
        {
            int second = Mathf.CeilToInt(stepEnds - now);
            if (second != shownSecond)
            {
                shownSecond = second;
                Prompt = $"{note}Take {TakeIndex + 1} of {Takes}: {second}";
            }
            if (now < stepEnds) return;
            step = Step.Capture;
            stepEnds = now + CaptureSeconds;
            note = "";
            take = new Take();
            used = 0;
            Prompt = "Show me now";
        }
        if (hand != null && hand.Usable && used < MaxFrames && now - lastFrame >= MinFrameGap)
        {
            lastFrame = now;
            float[] copy = pool[used++];
            Array.Copy(hand.values, copy, copy.Length);
            take.frames.Add(new Frame { time = now, values = copy, motion = hand.motion });
        }
        if (now < stepEnds) return;

        // A take needs the hand in view for most of it.
        if (take.frames.Count < 12 || take.frames[take.frames.Count - 1].time - take.frames[0].time < CaptureSeconds * 0.7f)
        {
            if (++retries > MaxRetries)
            {
                step = Step.Done;
                return;
            }
            note = "I lost your hand. ";
            Begin(now, BetweenSeconds);
            return;
        }
        // The frames share a pool that the next take reuses: keep this take's own copies.
        for (int i = 0; i < take.frames.Count; i++)
        {
            Frame f = take.frames[i];
            f.values = (float[])f.values.Clone();
            take.frames[i] = f;
        }
        Recorded.Add(take);
        TakeIndex++;
        if (TakeIndex >= Takes)
        {
            step = Step.Done;
            Prompt = "Got it";
            return;
        }
        Begin(now, BetweenSeconds);
    }

    // ---------------------------------------------------------------- analysis

    const float MoveSpeed = 0.65f;       // m/s at its peak: a take with less than this didn't move
    const float QuietSpeed = 0.2f;       // m/s: the hand has started moving once it passes this
    const float ChangedFinger = 0.35f;   // curl difference that says a finger changed between start and end of a take
    const float AgreeDirection = 0.55f;  // cosine: how close each take's direction must be to the average

    static readonly float[] relaxedValues = RelaxedValues();

    static float[] RelaxedValues()
    {
        var s = new HandSample();
        SyntheticHand.Build(s, HandShape.Relaxed, Vector3.zero, Quaternion.identity, false);
        var v = new float[HandFeatures.Count];
        HandFeatures.Compute(s, new Vector3(0f, 0.3f, -0.3f), Vector3.forward, v);
        return v;
    }

    /// <summary>Turn the takes into a gesture. On failure returns null and <paramref name="error"/> says why, to read out.</summary>
    public static GestureDef Analyze(List<Take> takes, string id, string title, out string error, out float[] center)
    {
        center = null;
        if (takes == null || takes.Count < Takes)
        {
            error = "I couldn't see your hand well enough. Keep it in front of you and try again.";
            return null;
        }

        foreach (var take in takes) IgnoreShapeChanges(take);

        // Palm or fingertip: a flick moves the fingertip much more than the palm.
        float palmPeak = 0f, tipPeak = 0f;
        foreach (var take in takes)
        {
            palmPeak += Peak(take, false);
            tipPeak += Peak(take, true);
        }
        bool fingertip = tipPeak > palmPeak * 1.35f;

        int moving = 0;
        foreach (var take in takes)
            if (Peak(take, fingertip) >= MoveSpeed) moving++;

        GestureDef def;
        if (moving >= 2) def = MoveGesture(takes, fingertip, out error);
        else def = PoseGesture(takes, out error);
        if (def == null) return null;

        def.id = id;
        def.title = title;
        center = Mid(def.start);
        if (def.start.Contains(relaxedValues) && (def.IsPlainPose || def.motion != null))
        {
            error = "That looks too much like a resting hand, so it would fire by accident. Try a more distinct shape.";
            return null;
        }
        error = null;
        return def;
    }

    // Fingers forming the shape throw the fingertips around, which isn't the hand moving. A frame where the shape is
    // changing counts as no motion, so bringing your hand up into the shape isn't read as a move.
    static void IgnoreShapeChanges(Take take)
    {
        const float Lookback = 0.1f, Changing = 0.1f;
        var quiet = new bool[take.frames.Count];
        for (int i = 0, back = 0; i < take.frames.Count; i++)
        {
            while (take.frames[i].time - take.frames[back].time > Lookback) back++;
            // The first moment too: the motion reaches back to before the take, where the shape can't be checked.
            quiet[i] = take.frames[i].time - take.frames[0].time > Lookback
                       && FingerChange(take.frames[i].values, take.frames[back].values) < Changing;
        }
        for (int i = 0; i < quiet.Length; i++)
        {
            if (quiet[i]) continue;
            Frame f = take.frames[i];
            f.motion = default;
            take.frames[i] = f;
        }
    }

    static float[] Mid(GesturePose pose)
    {
        var mid = new float[HandFeatures.Count];
        for (int i = 0; i < mid.Length; i++) mid[i] = pose.used[i] ? (pose.min[i] + pose.max[i]) * 0.5f : 0f;
        return mid;
    }

    static GestureDef MoveGesture(List<Take> takes, bool fingertip, out string error)
    {
        var dirs = new List<Vector3>();
        var starts = new List<float[]>();
        float minPeak = float.MaxValue, minTravel = float.MaxValue;
        foreach (var take in takes)
        {
            int peak = PeakFrame(take, fingertip);
            float speed = Speed(take.frames[peak], fingertip);
            if (speed < MoveSpeed) continue;  // the one take of three that didn't really move
            minPeak = Mathf.Min(minPeak, speed);

            // Direction: as far along the move as it got within a moment of the peak.
            Vector3 travel = Vector3.zero;
            for (int i = peak; i < take.frames.Count && take.frames[i].time - take.frames[peak].time < 0.15f; i++)
            {
                HandMotion m = take.frames[i].motion;
                Vector3 t = fingertip ? m.tipTravel : m.palmTravel;
                if (t.sqrMagnitude > travel.sqrMagnitude) travel = t;
            }
            dirs.Add(travel.normalized);
            minTravel = Mathf.Min(minTravel, travel.magnitude);

            int onset = peak;
            while (onset > 0 && Speed(take.frames[onset], fingertip) > QuietSpeed) onset--;
            int first = Mathf.Max(0, onset - 25);
            if (onset - first < 3) onset = Mathf.Min(first + 3, peak);
            starts.Add(Median(take, first, onset));
        }

        Vector3 mean = Vector3.zero;
        foreach (var d in dirs) mean += d;
        mean.Normalize();
        foreach (var d in dirs)
            if (Vector3.Dot(d, mean) < AgreeDirection)
            {
                error = "Those moves went different ways. Do the same movement each time.";
                return null;
            }

        var start = BandAround(starts, out error);
        if (start == null) return null;
        return new GestureDef
        {
            start = start,
            hold = 0.1f,
            window = 0.4f,
            how = $"Your hand shape, then a move {Toward(mean)}",
            motion = new GestureMotion
            {
                direction = mean,
                minSpeed = Mathf.Max(0.5f, minPeak * 0.6f),
                minDistance = Mathf.Max(0.04f, minTravel * 0.5f),
                purity = 0.55f,
                fingertip = fingertip,
            },
        };
    }

    static GestureDef PoseGesture(List<Take> takes, out string error)
    {
        var heads = new List<float[]>();
        var tails = new List<float[]>();
        foreach (var take in takes)
        {
            int n = take.frames.Count;
            heads.Add(Median(take, 0, n / 4));
            tails.Add(Median(take, n * 3 / 5, n - 1));
        }

        // The hand started somewhere else on purpose (not just coming up from rest): a change from one pose to another.
        int changed = 0;
        for (int i = 0; i < heads.Count; i++)
            if (FingerChange(heads[i], tails[i]) >= ChangedFinger && !IsRelaxed(heads[i])) changed++;
        if (changed >= 2)
        {
            var from = BandAround(heads, out error);
            if (from == null) return null;
            var to = BandAround(tails, out error);
            if (to == null) return null;
            return new GestureDef { start = from, end = to, hold = 0.12f, window = 0.8f, how = "One hand shape changing into another" };
        }

        var pose = BandAround(tails, out error);
        if (pose == null) return null;
        return new GestureDef { start = pose, hold = 0.3f, how = "Your hand shape, held" };
    }

    static string Toward(Vector3 d)
    {
        float x = Mathf.Abs(d.x), y = Mathf.Abs(d.y), z = Mathf.Abs(d.z);
        if (z >= x && z >= y) return d.z < 0f ? "toward you" : "away from you";
        if (y >= x) return d.y > 0f ? "up" : "down";
        return "sideways";
    }

    static bool IsRelaxed(float[] v) => FingerChange(v, relaxedValues) < 0.22f;

    // The biggest curl difference across the four fingers.
    static float FingerChange(float[] a, float[] b)
    {
        float most = 0f;
        for (int i = (int)HandFeature.IndexCurl; i <= (int)HandFeature.PinkyCurl; i++) most = Mathf.Max(most, Mathf.Abs(a[i] - b[i]));
        return most;
    }

    // How wide a feature's range is at the least, and which features a taught pose cares about. InView and FingersUp
    // depend on where the hand happens to be held, so a taught pose ignores them.
    static float MinHalfWidth(int feature)
    {
        switch ((HandFeature)feature)
        {
            case HandFeature.ThumbCurl: return 0.35f;
            case HandFeature.IndexCurl:
            case HandFeature.MiddleCurl:
            case HandFeature.RingCurl:
            case HandFeature.PinkyCurl: return 0.22f;
            case HandFeature.IndexFlex:
            case HandFeature.MiddleFlex:
            case HandFeature.RingFlex:
            case HandFeature.PinkyFlex: return 0.28f;
            case HandFeature.SpreadIndexMiddle:
            case HandFeature.SpreadMiddleRing:
            case HandFeature.SpreadRingPinky: return 0.45f;
            case HandFeature.Pinch:
            case HandFeature.ThumbOut: return 0.3f;
            case HandFeature.PalmUp:
            case HandFeature.PalmToHead: return 0.6f;
            default: return -1f;
        }
    }

    // A range around the takes' medians, wide enough for how much they differed. Null if a finger kept changing.
    static GesturePose BandAround(List<float[]> takes, out string error)
    {
        var pose = new GesturePose();
        for (int f = 0; f < HandFeatures.Count; f++)
        {
            float half = MinHalfWidth(f);
            if (half < 0f) continue;
            float low = float.MaxValue, high = float.MinValue;
            foreach (var t in takes)
            {
                low = Mathf.Min(low, t[f]);
                high = Mathf.Max(high, t[f]);
            }
            if (f >= (int)HandFeature.IndexCurl && f <= (int)HandFeature.PinkyCurl && high - low > 0.55f)
            {
                error = "Your fingers changed between takes. Hold the same shape each time.";
                return null;
            }
            float mid = (low + high) * 0.5f;
            half = Mathf.Max(half, (high - low) * 0.5f + 0.08f);
            pose.Range((HandFeature)f, mid - half, mid + half);
        }
        error = null;
        return pose;
    }

    static float Speed(in Frame f, bool fingertip) => (fingertip ? f.motion.tipVelocity : f.motion.palmVelocity).magnitude;

    static float Peak(Take take, bool fingertip) => Speed(take.frames[PeakFrame(take, fingertip)], fingertip);

    static int PeakFrame(Take take, bool fingertip)
    {
        int best = 0;
        float top = -1f;
        for (int i = 0; i < take.frames.Count; i++)
        {
            float s = Speed(take.frames[i], fingertip);
            if (s > top)
            {
                top = s;
                best = i;
            }
        }
        return best;
    }

    // Each feature's median over frames first..last. A median shrugs off a bad frame or two.
    static float[] Median(Take take, int first, int last)
    {
        first = Mathf.Clamp(first, 0, take.frames.Count - 1);
        last = Mathf.Clamp(last, first, take.frames.Count - 1);
        var result = new float[HandFeatures.Count];
        var column = new float[last - first + 1];
        for (int f = 0; f < result.Length; f++)
        {
            for (int i = first; i <= last; i++) column[i - first] = take.frames[i].values[f];
            Array.Sort(column);
            result[f] = column[column.Length / 2];
        }
        return result;
    }
}
