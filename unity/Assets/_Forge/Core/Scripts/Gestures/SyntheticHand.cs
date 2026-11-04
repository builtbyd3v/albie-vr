using UnityEngine;

/// <summary>A finger-by-finger description of a hand shape, for <see cref="SyntheticHand"/>.</summary>
public struct HandShape
{
    public float index, middle, ring, pinky;  // 0 straight, 1 fully bent (knuckle and outer joints together)
    public float thumb;                       // 0 out, 1 folded across the palm
    public float spread;                      // 0 together, 1 wide

    public HandShape(float index, float middle, float ring, float pinky, float thumb, float spread)
    {
        this.index = index;
        this.middle = middle;
        this.ring = ring;
        this.pinky = pinky;
        this.thumb = thumb;
        this.spread = spread;
    }

    public static readonly HandShape Open = new HandShape(0.05f, 0.05f, 0.05f, 0.05f, 0.1f, 0.5f);
    public static readonly HandShape Relaxed = new HandShape(0.38f, 0.4f, 0.42f, 0.45f, 0.4f, 0.2f);
    public static readonly HandShape Fist = new HandShape(0.95f, 0.95f, 0.95f, 0.95f, 0.9f, 0f);
    public static readonly HandShape Point = new HandShape(0.05f, 0.85f, 0.9f, 0.9f, 0.7f, 0f);
    public static readonly HandShape Peace = new HandShape(0.05f, 0.05f, 0.9f, 0.9f, 0.85f, 0.6f);
    public static readonly HandShape Rock = new HandShape(0.05f, 0.9f, 0.9f, 0.05f, 0.85f, 0.4f);

    public static HandShape Lerp(HandShape a, HandShape b, float t) => new HandShape(
        Mathf.Lerp(a.index, b.index, t), Mathf.Lerp(a.middle, b.middle, t), Mathf.Lerp(a.ring, b.ring, t),
        Mathf.Lerp(a.pinky, b.pinky, t), Mathf.Lerp(a.thumb, b.thumb, t), Mathf.Lerp(a.spread, b.spread, t));
}

/// <summary>
/// Makes a plausible 24-joint hand from a <see cref="HandShape"/> and a wrist pose. It stands in for hand tracking in the
/// self-test and the sandbox. In its wrist frame, fingers run along +Z, the back of the hand is +Y and, on a right hand,
/// the little finger is on the +X side. A wrist rotation of identity is a right hand palm down, fingers forward.
/// </summary>
public static class SyntheticHand
{
    public static void Build(HandSample s, in HandShape shape, Vector3 wrist, Quaternion rotation, bool isLeft, float size = 1f)
    {
        s.isLeft = isLeft;
        s.tracked = true;
        s.highConfidence = true;
        float side = isLeft ? -1f : 1f;
        var j = s.joints;
        j[HandJoint.Wrist] = wrist;
        j[1] = wrist + rotation * Local(0f, 0f, -0.05f, side, size);  // forearm stub

        Finger(s, rotation, wrist, HandJoint.Index1, HandJoint.IndexTip, new Vector3(-0.026f, 0f, 0.094f), shape.index, -1.5f * shape.spread, 0.045f, 0.027f, 0.024f, side, size);
        Finger(s, rotation, wrist, HandJoint.Middle1, HandJoint.MiddleTip, new Vector3(-0.005f, 0f, 0.098f), shape.middle, -0.5f * shape.spread, 0.05f, 0.03f, 0.025f, side, size);
        Finger(s, rotation, wrist, HandJoint.Ring1, HandJoint.RingTip, new Vector3(0.015f, 0f, 0.09f), shape.ring, 0.5f * shape.spread, 0.046f, 0.029f, 0.025f, side, size);
        Finger(s, rotation, wrist, HandJoint.Pinky1, HandJoint.PinkyTip, new Vector3(0.033f, 0f, 0.08f), shape.pinky, 1.5f * shape.spread, 0.035f, 0.021f, 0.022f, side, size);

        // The thumb: from the base of the palm out toward the index side, folding across the palm.
        Vector3 t1 = new Vector3(-0.03f, -0.012f, 0.03f);
        Vector3 d1 = Vector3.Lerp(new Vector3(-0.75f, -0.1f, 0.65f), new Vector3(0.1f, -0.75f, 0.65f), shape.thumb).normalized;
        Vector3 d2 = Vector3.Lerp(d1, new Vector3(0.75f, -0.35f, 0.55f).normalized, shape.thumb * 0.7f).normalized;
        Vector3 d3 = Vector3.Lerp(d2, new Vector3(0.95f, 0.1f, 0.3f).normalized, shape.thumb * 0.7f).normalized;
        Vector3 t2 = t1 + d1 * 0.04f, t3 = t2 + d2 * 0.032f, tt = t3 + d3 * 0.028f;
        j[2] = wrist + rotation * Local(-0.02f, -0.01f, 0.015f, side, size);
        j[HandJoint.Thumb1] = wrist + rotation * Local(t1.x, t1.y, t1.z, side, size);
        j[HandJoint.Thumb2] = wrist + rotation * Local(t2.x, t2.y, t2.z, side, size);
        j[HandJoint.Thumb3] = wrist + rotation * Local(t3.x, t3.y, t3.z, side, size);
        j[HandJoint.ThumbTip] = wrist + rotation * Local(tt.x, tt.y, tt.z, side, size);
        j[15] = wrist + rotation * Local(0.02f, 0f, 0.03f, side, size);  // pinky metacarpal
    }

    static void Finger(HandSample s, Quaternion rotation, Vector3 wrist, int knuckleJoint, int tipJoint, Vector3 knuckle,
                       float bend, float spreadUnits, float a, float b, float c, float side, float size)
    {
        // Knuckle up to 85 deg, then the middle joint up to 100 and the outer joint up to 60 on top of it.
        float t1 = bend * 85f * Mathf.Deg2Rad, t2 = t1 + bend * 100f * Mathf.Deg2Rad, t3 = t2 + bend * 60f * Mathf.Deg2Rad;
        float yaw = spreadUnits * 10f * Mathf.Deg2Rad;
        Vector3 p1 = knuckle + Dir(t1, yaw) * a, p2 = p1 + Dir(t2, yaw) * b, p3 = p2 + Dir(t3, yaw) * c;
        var j = s.joints;
        j[knuckleJoint] = wrist + rotation * Local(knuckle.x, knuckle.y, knuckle.z, side, size);
        j[knuckleJoint + 1] = wrist + rotation * Local(p1.x, p1.y, p1.z, side, size);
        j[knuckleJoint + 2] = wrist + rotation * Local(p2.x, p2.y, p2.z, side, size);
        j[tipJoint] = wrist + rotation * Local(p3.x, p3.y, p3.z, side, size);
    }

    static Vector3 Dir(float pitch, float yaw) => new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), -Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch));

    static Vector3 Local(float x, float y, float z, float side, float size) => new Vector3(x * side, y, z) * size;
}
