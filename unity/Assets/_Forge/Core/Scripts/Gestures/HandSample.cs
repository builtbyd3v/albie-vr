using UnityEngine;

/// <summary>
/// Our own joint numbers, so this code takes no Meta types. Not the same numbers as the SDK's HandJointId (which is
/// OpenXR's order, with a palm joint first): <see cref="IsdkHandSource"/> maps between them by name. Thumb1 is the base
/// of the thumb; 1, 2 and 15 are only used to draw the simulated hand.
/// </summary>
public static class HandJoint
{
    public const int Wrist = 0, Thumb1 = 3, Thumb2 = 4, Thumb3 = 5;
    public const int Index1 = 6, Index2 = 7, Index3 = 8, Middle1 = 9, Middle2 = 10, Middle3 = 11;
    public const int Ring1 = 12, Ring2 = 13, Ring3 = 14, Pinky1 = 16, Pinky2 = 17, Pinky3 = 18;
    public const int ThumbTip = 19, IndexTip = 20, MiddleTip = 21, RingTip = 22, PinkyTip = 23;
    public const int Count = 24;
}

/// <summary>One hand's joints this frame, world space. Filled by an <see cref="IHandSource"/>.</summary>
public sealed class HandSample
{
    public readonly Vector3[] joints = new Vector3[HandJoint.Count];
    public bool isLeft;
    public bool tracked;
    public bool highConfidence;
}

/// <summary>Where hand data comes from: the headset (<see cref="IsdkHandSource"/>) or the sandbox (<see cref="SimulatedHandSource"/>).</summary>
public interface IHandSource
{
    /// <summary>Fill <paramref name="sample"/> for hand 0 (left) or 1 (right); tracked false when there is no hand.</summary>
    void Read(int hand, HandSample sample);
}

/// <summary>What a hand pose is measured in. Size-independent, so a gesture recorded by one person works for another.</summary>
public enum HandFeature
{
    ThumbCurl, IndexCurl, MiddleCurl, RingCurl, PinkyCurl,
    IndexFlex, MiddleFlex, RingFlex, PinkyFlex,
    SpreadIndexMiddle, SpreadMiddleRing, SpreadRingPinky,
    Pinch, ThumbOut,
    PalmUp, PalmToHead, InView, FingersUp,
}

/// <summary>
/// Turns joints into the numbers gestures are matched on. Curl is how far a finger's two outer joints are bent, flex is
/// how far its knuckle is bent (0 straight, 1 fully bent). Spread is the angle between neighbouring fingers. Pinch and
/// ThumbOut are distances in hand lengths. PalmUp, PalmToHead, InView and FingersUp are -1..1 directions. Uses joint
/// positions only, so it doesn't depend on any bone-axis convention. No allocations.
/// </summary>
public static class HandFeatures
{
    public const int Count = 18;

    public static readonly string[] Names =
    {
        "thumb_curl", "index_curl", "middle_curl", "ring_curl", "pinky_curl",
        "index_flex", "middle_flex", "ring_flex", "pinky_flex",
        "spread_index_middle", "spread_middle_ring", "spread_ring_pinky",
        "pinch", "thumb_out",
        "palm_up", "palm_to_head", "in_view", "fingers_up",
    };

    /// <summary>The direction out of the palm (right hand, palm down: world down).</summary>
    public static Vector3 PalmNormal(HandSample s)
    {
        Vector3[] j = s.joints;
        Vector3 n = Vector3.Cross(j[HandJoint.Index1] - j[HandJoint.Wrist], j[HandJoint.Pinky1] - j[HandJoint.Wrist]).normalized;
        return s.isLeft ? n : -n;
    }

    public static Vector3 PalmCenter(HandSample s) => (s.joints[HandJoint.Wrist] + s.joints[HandJoint.Middle1]) * 0.5f;

    public static void Compute(HandSample s, Vector3 headPosition, Vector3 headForward, float[] output)
    {
        Vector3[] j = s.joints;
        output[(int)HandFeature.ThumbCurl] = Mathf.Clamp01(Bend(j[HandJoint.Thumb1], j[HandJoint.Thumb2], j[HandJoint.Thumb3], j[HandJoint.ThumbTip]) / 90f);
        Finger(j, HandJoint.Index1, HandJoint.IndexTip, HandFeature.IndexCurl, HandFeature.IndexFlex, output);
        Finger(j, HandJoint.Middle1, HandJoint.MiddleTip, HandFeature.MiddleCurl, HandFeature.MiddleFlex, output);
        Finger(j, HandJoint.Ring1, HandJoint.RingTip, HandFeature.RingCurl, HandFeature.RingFlex, output);
        Finger(j, HandJoint.Pinky1, HandJoint.PinkyTip, HandFeature.PinkyCurl, HandFeature.PinkyFlex, output);

        output[(int)HandFeature.SpreadIndexMiddle] = Spread(j, HandJoint.Index1, HandJoint.Middle1);
        output[(int)HandFeature.SpreadMiddleRing] = Spread(j, HandJoint.Middle1, HandJoint.Ring1);
        output[(int)HandFeature.SpreadRingPinky] = Spread(j, HandJoint.Ring1, HandJoint.Pinky1);

        float size = Mathf.Max(0.01f, Vector3.Distance(j[HandJoint.Wrist], j[HandJoint.Middle1]));
        output[(int)HandFeature.Pinch] = Vector3.Distance(j[HandJoint.ThumbTip], j[HandJoint.IndexTip]) / size;
        output[(int)HandFeature.ThumbOut] = Vector3.Distance(j[HandJoint.ThumbTip], j[HandJoint.Index1]) / size;

        Vector3 normal = PalmNormal(s);
        Vector3 palm = PalmCenter(s);
        Vector3 fromHead = (palm - headPosition).normalized;
        output[(int)HandFeature.PalmUp] = normal.y;
        output[(int)HandFeature.PalmToHead] = -Vector3.Dot(normal, fromHead);
        output[(int)HandFeature.InView] = Vector3.Dot(headForward, fromHead);
        output[(int)HandFeature.FingersUp] = (j[HandJoint.Middle1] - j[HandJoint.Wrist]).normalized.y;
    }

    // The tip joint id, not an offset from the knuckle: the SDK's tips sit together at the end of its joint list.
    static void Finger(Vector3[] j, int knuckle, int tip, HandFeature curl, HandFeature flex, float[] output)
    {
        Vector3 k = j[knuckle], m = j[knuckle + 1], d = j[knuckle + 2], t = j[tip];
        output[(int)curl] = Mathf.Clamp01(Bend(k, m, d, t) / 160f);
        output[(int)flex] = Mathf.Clamp01(Vector3.Angle(k - j[HandJoint.Wrist], m - k) / 90f);
    }

    static float Bend(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => Vector3.Angle(b - a, c - b) + Vector3.Angle(c - b, d - c);

    static float Spread(Vector3[] j, int a, int b) => Vector3.Angle(j[a + 1] - j[a], j[b + 1] - j[b]) / 30f;
}
