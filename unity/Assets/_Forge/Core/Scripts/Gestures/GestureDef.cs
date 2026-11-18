using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>A hand shape: an allowed range per <see cref="HandFeature"/>. Features with no range don't matter.</summary>
public sealed class GesturePose
{
    public readonly float[] min = new float[HandFeatures.Count];
    public readonly float[] max = new float[HandFeatures.Count];
    public readonly bool[] used = new bool[HandFeatures.Count];

    public GesturePose Range(HandFeature feature, float low, float high)
    {
        int i = (int)feature;
        min[i] = low;
        max[i] = high;
        used[i] = true;
        return this;
    }

    /// <summary>In the ranges. <paramref name="widen"/> grows each range by that share of its width on both sides (hysteresis).</summary>
    public bool Contains(float[] values, float widen = 0f)
    {
        for (int i = 0; i < HandFeatures.Count; i++)
        {
            if (!used[i]) continue;
            float pad = (max[i] - min[i]) * widen;
            if (values[i] < min[i] - pad || values[i] > max[i] + pad) return false;
        }
        return true;
    }

    public JObject ToJson()
    {
        var json = new JObject();
        for (int i = 0; i < HandFeatures.Count; i++)
            if (used[i]) json[HandFeatures.Names[i]] = new JArray(Math.Round(min[i], 3), Math.Round(max[i], 3));
        return json;
    }

    public static GesturePose FromJson(JObject json)
    {
        if (json == null) return null;
        var pose = new GesturePose();
        for (int i = 0; i < HandFeatures.Count; i++)
            if (json[HandFeatures.Names[i]] is JArray r && r.Count == 2) pose.Range((HandFeature)i, (float)r[0], (float)r[1]);
        return pose;
    }
}

/// <summary>A hand movement, in the head's frame (+Z where you're looking, -Z toward you, +X to the right for a right hand).</summary>
public sealed class GestureMotion
{
    public Vector3 direction = Vector3.forward;
    public float minSpeed = 0.8f;      // m/s along the direction, over the last moments
    public float minDistance = 0.05f;  // m along the direction, over a little longer
    public float purity = 0.5f;        // how much of the movement has to go that way (0..1)
    public bool fingertip;             // track the index fingertip (a wrist flick) rather than the palm

    public bool Test(in HandMotion m)
    {
        Vector3 v = fingertip ? m.tipVelocity : m.palmVelocity;
        Vector3 d = fingertip ? m.tipTravel : m.palmTravel;
        float along = Vector3.Dot(v, direction);
        return along >= minSpeed && Vector3.Dot(d, direction) >= minDistance && along >= purity * v.magnitude;
    }

    public JObject ToJson() => new JObject
    {
        ["direction"] = new JArray(Math.Round(direction.x, 3), Math.Round(direction.y, 3), Math.Round(direction.z, 3)),
        ["min_speed"] = Math.Round(minSpeed, 3),
        ["min_distance"] = Math.Round(minDistance, 3),
        ["purity"] = Math.Round(purity, 3),
        ["fingertip"] = fingertip,
    };

    public static GestureMotion FromJson(JObject json)
    {
        if (json == null) return null;
        var d = json["direction"] as JArray;
        return new GestureMotion
        {
            direction = d != null && d.Count == 3 ? new Vector3((float)d[0], (float)d[1], (float)d[2]).normalized : Vector3.forward,
            minSpeed = (float?)json["min_speed"] ?? 0.8f,
            minDistance = (float?)json["min_distance"] ?? 0.05f,
            purity = (float?)json["purity"] ?? 0.5f,
            fingertip = (bool?)json["fingertip"] ?? false,
        };
    }
}

/// <summary>Which hand a built-in answers to, by the Hands setting (<see cref="HandSplit"/>). Taught gestures are Any.</summary>
public enum GestureRole { Any, AllWindows, OneWindow }

/// <summary>
/// The Hands setting: which hand's open and close works on every window at once. The other hand's open, close and
/// flicks work on one window. Off: no hand does every window, and either hand does one.
/// </summary>
public enum HandSplit { LeftAll, RightAll, Off }

/// <summary>
/// One gesture as data, the same for built-ins and ones the user teaches: hold <see cref="start"/>, then (optionally)
/// move (<see cref="motion"/>) and/or reach <see cref="end"/> within <see cref="window"/> seconds. Start alone is a plain
/// pose. <see cref="action"/> is what it does (<see cref="GestureActions"/>).
/// </summary>
public sealed class GestureDef
{
    public string id;
    public string title;
    public string how;       // one line for the display: "Point at a window, flick toward you"
    public bool builtin;
    public GesturePose start;
    public GesturePose end;
    public GestureMotion motion;
    public float hold = 0.25f;
    public float window = 0.4f;
    public string action = GestureActions.None;
    public JObject args;
    /// <summary>Built-ins only, not saved: which hand it answers to.</summary>
    public GestureRole role = GestureRole.Any;
    /// <summary>Built-ins only, not saved: it can't start just after the hand was pointing (curling the finger after a flick isn't a fist).</summary>
    public bool quietAfterPoint;
    /// <summary>Built-ins only, not saved: and its start pose has to be made afresh after pointing, not just held on (a
    /// finger curled after a flick leaves a fist, and bursting that open isn't meant).</summary>
    public bool freshAfterPoint;
    /// <summary>Built-ins only, not saved: it also counts from a hand held out lower than the others need
    /// (<see cref="GestureHand.HeldOut"/>). For a pose a typing or resting hand doesn't make.</summary>
    public bool lowHand;

    public bool IsPlainPose => motion == null && end == null;
    /// <summary>It acts on the window you're pointing at, so it can't start without one.</summary>
    public bool NeedsTarget => action == GestureActions.WindowSummon || action == GestureActions.WindowMinimize;

    /// <summary>Whether this hand may make it, under the Hands setting.</summary>
    public bool AllowedOn(bool isLeft, HandSplit split)
    {
        switch (role)
        {
            case GestureRole.AllWindows: return split != HandSplit.Off && isLeft == (split == HandSplit.LeftAll);
            case GestureRole.OneWindow: return split == HandSplit.Off || isLeft != (split == HandSplit.LeftAll);
            default: return true;
        }
    }

    public JObject ToJson()
    {
        var json = new JObject
        {
            ["id"] = id,
            ["title"] = title,
            ["how"] = how,
            ["start"] = start.ToJson(),
            ["hold"] = Math.Round(hold, 3),
            ["window"] = Math.Round(window, 3),
            ["action"] = action,
        };
        if (end != null) json["end"] = end.ToJson();
        if (motion != null) json["motion"] = motion.ToJson();
        if (args != null) json["args"] = args;
        return json;
    }

    public static GestureDef FromJson(JObject json)
    {
        var start = GesturePose.FromJson(json["start"] as JObject);
        string id = (string)json["id"];
        if (start == null || string.IsNullOrEmpty(id)) return null;
        return new GestureDef
        {
            id = id,
            title = (string)json["title"] ?? id,
            how = (string)json["how"] ?? "",
            start = start,
            end = GesturePose.FromJson(json["end"] as JObject),
            motion = GestureMotion.FromJson(json["motion"] as JObject),
            hold = (float?)json["hold"] ?? 0.25f,
            window = (float?)json["window"] ?? 0.4f,
            action = (string)json["action"] ?? GestureActions.None,
            args = json["args"] as JObject,
        };
    }
}

/// <summary>How a hand has been moving, head frame, mirrored for the left hand so a gesture works with either.</summary>
public struct HandMotion
{
    public Vector3 palmVelocity, tipVelocity;  // over the last 90 ms
    public Vector3 palmTravel, tipTravel;      // over the last 220 ms
}

/// <summary>
/// The state of one gesture on one hand. Step once per frame. Plain poses fire once and wait for the hand to leave the
/// pose; moves and transitions go Hold, Armed, fire, then a short cooldown. Ranges widen once inside them, so a pose at
/// the edge doesn't flicker.
/// </summary>
public sealed class GestureMatcher
{
    const float StayWiden = 0.25f;
    const float Cooldown = 0.6f;

    enum Phase { Idle, Holding, Armed, Latched }

    public readonly GestureDef def;
    Phase phase;
    float holdStart, lastInStart, cooldownUntil;

    public GestureMatcher(GestureDef def) => this.def = def;

    /// <summary>Holding the start pose long enough; a move or a transition can fire now.</summary>
    public bool Armed => phase == Phase.Armed;

    public void Reset() => phase = Phase.Idle;

    /// <summary>Start over, and if the hand is already in the start pose, wait for it to leave and come back.</summary>
    public void HoldOff(float[] values) => phase = def.start.Contains(values, StayWiden) ? Phase.Latched : Phase.Idle;

    /// <summary>
    /// True on the frame it fires. <paramref name="canStart"/> false stops it starting (no window pointed at); once
    /// armed it carries on, because a flick swings the finger off the window it started on.
    /// </summary>
    public bool Step(float[] values, in HandMotion motion, float now, bool canStart)
    {
        if (now < cooldownUntil) return false;
        bool inStart;
        switch (phase)
        {
            case Phase.Idle:
                if (canStart && def.start.Contains(values))
                {
                    phase = Phase.Holding;
                    holdStart = now;
                }
                return false;

            case Phase.Holding:
                if (!canStart || !def.start.Contains(values, StayWiden))
                {
                    phase = Phase.Idle;
                    return false;
                }
                if (now - holdStart < def.hold) return false;
                if (def.IsPlainPose)
                {
                    phase = Phase.Latched;
                    return true;
                }
                phase = Phase.Armed;
                lastInStart = now;
                return false;

            case Phase.Armed:
                inStart = def.start.Contains(values, StayWiden);
                if (inStart) lastInStart = now;
                // A move has to be the hand moving with the shape held: curling a finger to stop pointing also
                // whips the fingertip back, and mustn't count. A change of pose is allowed to leave the start, of course.
                bool moved = def.motion == null || (def.motion.Test(motion) && (inStart || def.end != null));
                bool arrived = def.end == null || def.end.Contains(values);
                if (moved && arrived)
                {
                    phase = Phase.Idle;
                    cooldownUntil = now + Cooldown;
                    return true;
                }
                if (!inStart && now - lastInStart > def.window) phase = Phase.Idle;
                return false;

            default:  // Latched: fires again only after the hand leaves the pose
                if (!def.start.Contains(values, StayWiden)) phase = Phase.Idle;
                return false;
        }
    }
}
