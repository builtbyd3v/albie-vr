using System;
using System.Text;
using Forge.Controllers;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless checks for the hands transmitter (pinch to drive, tilt to steer) on synthetic hand joints: no headset, no
/// Interaction SDK. Forge > Controllers > Check Hands Transmitter logs the report; <see cref="Run"/> returns it (for
/// `unity command eval`). PASS on the first line means every check passed. The lap on the brain truck that these
/// inputs drive is in Check Brain Loop (Play).
/// </summary>
public static class HandsTransmitterCheck
{
    [MenuItem("Forge/Controllers/Check Hands Transmitter")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    /// <summary>For -executeMethod.</summary>
    public static void RunBatch()
    {
        string report = Run();
        Debug.Log(report);
        EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        bool ok = true;
        var hands = new FakeHands();
        HandsTransmitter.Source = hands;
        HandsTransmitter.Override = null;
        try
        {
            ok &= Check(sb, "no hands, nothing", () => NoHands(hands));
            ok &= Check(sb, "an open hand drives nothing", () => OpenHand(hands));
            ok &= Check(sb, "a pinch drives forward", () => Pinch(hands));
            ok &= Check(sb, "a light pinch crawls", () => LightPinch(hands));
            ok &= Check(sb, "pinky side down steers right, thumb side down steers left", () => Tilt(hands));
            ok &= Check(sb, "the left hand's pinch reverses at half throttle", () => LeftReverse(hands));
            ok &= Check(sb, "both hands pinching: the right one wins", () => BothHands(hands));
            ok &= Check(sb, "a hand the tracker doesn't trust is ignored", () => LowConfidence(hands));
            ok &= Check(sb, "no allocations per read", () => NoAllocations(hands));
        }
        finally
        {
            HandsTransmitter.Source = null;
            HandsTransmitter.Override = null;
        }
        return (ok ? "PASS" : "FAIL") + " hands transmitter check\n" + sb;
    }

    static bool Check(StringBuilder sb, string name, Func<string> check)
    {
        string detail;
        bool ok;
        try { detail = check(); ok = true; }
        catch (Exception e) { detail = e.Message; ok = false; }
        sb.Append(ok ? "  ok   " : "  FAIL ").Append(name).Append(": ").Append(detail).Append('\n');
        return ok;
    }

    static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static string NoHands(FakeHands hands)
    {
        hands.Clear();
        HandsTransmitter.Read(out float t, out float r);
        Expect(t == 0f && r == 0f && !HandsTransmitter.Driving, $"throttle {t}, roll {r}");
        return "throttle 0, roll 0, not driving";
    }

    static string OpenHand(FakeHands hands)
    {
        hands.Clear();
        hands.Set(1, FakeHands.Right(pinch: false, tiltDegrees: 0f));
        HandsTransmitter.Read(out float t, out float r);
        float pinch = HandsTransmitter.Pinch(hands.Sample(1));
        Expect(t == 0f && r == 0f, $"throttle {t}, roll {r} with pinch distance {pinch:0.00}");
        return $"pinch distance {pinch:0.00} hand lengths (open above {HandsTransmitter.PinchOpen}): throttle 0";
    }

    static string Pinch(FakeHands hands)
    {
        hands.Clear();
        hands.Set(1, FakeHands.Right(pinch: true, tiltDegrees: 0f));
        HandsTransmitter.Read(out float t, out float r);
        float pinch = HandsTransmitter.Pinch(hands.Sample(1));
        Expect(Mathf.Approximately(t, 1f) && Mathf.Abs(r) < 0.05f && HandsTransmitter.Driving, $"throttle {t}, roll {r:0.00} with pinch distance {pinch:0.00}");
        return $"pinch distance {pinch:0.00} (closed below {HandsTransmitter.PinchClosed}): throttle 1, roll {r:0.00}";
    }

    static string LightPinch(FakeHands hands)
    {
        hands.Clear();
        float gap = (HandsTransmitter.PinchClosed + HandsTransmitter.PinchOpen) * 0.5f;
        hands.Set(1, FakeHands.Right(pinch: true, tiltDegrees: 0f, pinchHandLengths: gap));
        HandsTransmitter.Read(out float t, out _);
        Expect(t > 0.4f && t < 0.6f, $"throttle {t:0.00} at pinch distance {gap:0.00}");
        return $"pinch distance {gap:0.00} gives throttle {t:0.00}";
    }

    static string Tilt(FakeHands hands)
    {
        hands.Clear();
        hands.Set(1, FakeHands.Right(pinch: true, tiltDegrees: HandsTransmitter.FullLockDegrees));
        var s = hands.Sample(1);
        Expect(s.joints[HandJoint.Pinky1].y < s.joints[HandJoint.Index1].y - 0.01f, "the fake hand's pinky side should be lower than its index side");
        HandsTransmitter.Read(out float t1, out float right);
        hands.Set(1, FakeHands.Right(pinch: true, tiltDegrees: -HandsTransmitter.FullLockDegrees));
        HandsTransmitter.Read(out float t2, out float left);
        hands.Set(1, FakeHands.Right(pinch: true, tiltDegrees: 20f));
        HandsTransmitter.Read(out _, out float part);
        Expect(t1 == 1f && t2 == 1f && right > 0.95f && left < -0.95f && part > 0.3f && part < 0.6f,
            $"roll {right:0.00} at +{HandsTransmitter.FullLockDegrees} deg, {left:0.00} at -{HandsTransmitter.FullLockDegrees}, {part:0.00} at 20 deg");
        return $"roll {right:0.00} pinky down {HandsTransmitter.FullLockDegrees} deg, {left:0.00} thumb down, {part:0.00} at 20 deg; throttle stays 1";
    }

    static string LeftReverse(FakeHands hands)
    {
        hands.Clear();
        hands.Set(0, FakeHands.Left(pinch: true, tiltDegrees: 0f));
        HandsTransmitter.Read(out float t, out float r);
        Expect(Mathf.Approximately(t, -HandsTransmitter.ReverseThrottle) && Mathf.Abs(r) < 0.05f, $"throttle {t}, roll {r:0.00}");
        return $"throttle {t}, roll {r:0.00}";
    }

    static string BothHands(FakeHands hands)
    {
        hands.Clear();
        hands.Set(0, FakeHands.Left(pinch: true, tiltDegrees: -HandsTransmitter.FullLockDegrees));
        hands.Set(1, FakeHands.Right(pinch: true, tiltDegrees: HandsTransmitter.FullLockDegrees));
        HandsTransmitter.Read(out float t, out float r);
        Expect(t == 1f && r > 0.95f, $"throttle {t}, roll {r:0.00}");
        return $"throttle {t}, roll {r:0.00} (the right hand's)";
    }

    static string LowConfidence(FakeHands hands)
    {
        hands.Clear();
        var s = FakeHands.Right(pinch: true, tiltDegrees: 0f);
        s.highConfidence = false;
        hands.Set(1, s);
        HandsTransmitter.Read(out float t, out float r);
        Expect(t == 0f && r == 0f, $"throttle {t}, roll {r}");
        return "throttle 0, roll 0";
    }

    static string NoAllocations(FakeHands hands)
    {
        hands.Clear();
        hands.Set(1, FakeHands.Right(pinch: true, tiltDegrees: 30f));
        HandsTransmitter.Read(out _, out _);
        long bytes = AllocationCounter.FewestBytes(3, () => { }, () =>
        {
            for (int i = 0; i < 1000; i++) HandsTransmitter.Read(out _, out _);
        });
        Expect(bytes == 0, $"{bytes} bytes over 1000 reads");
        return "0 bytes over 1000 reads";
    }

    /// <summary>Hand joints built by hand: a hand held out in front, palm down, fingers forward (+z). Enough joints for
    /// pinch (thumb and index tips, wrist, middle knuckle) and the palm normal (wrist, index and pinky knuckles).</summary>
    sealed class FakeHands : IHandSource
    {
        readonly HandSample[] hands = { null, null };

        public void Clear() { hands[0] = null; hands[1] = null; }
        public void Set(int hand, HandSample sample) => hands[hand] = sample;
        public HandSample Sample(int hand) => hands[hand];

        public void Read(int hand, HandSample sample)
        {
            var from = hands[hand];
            if (from == null) { sample.tracked = false; sample.highConfidence = false; return; }
            Array.Copy(from.joints, sample.joints, from.joints.Length);
            sample.isLeft = from.isLeft;
            sample.tracked = from.tracked;
            sample.highConfidence = from.highConfidence;
        }

        public static HandSample Right(bool pinch, float tiltDegrees, float pinchHandLengths = 0.1f) => Build(false, pinch, tiltDegrees, pinchHandLengths);
        public static HandSample Left(bool pinch, float tiltDegrees, float pinchHandLengths = 0.1f) => Build(true, pinch, tiltDegrees, pinchHandLengths);

        // tiltDegrees > 0 rolls the hand so its pinky side goes down (a right turn on the wheel), for either hand.
        static HandSample Build(bool isLeft, bool pinch, float tiltDegrees, float pinchHandLengths)
        {
            const float size = 0.09f;  // wrist to middle knuckle
            float side = isLeft ? -1f : 1f;  // the pinky is on the right of a palm-down right hand, on the left of a left hand
            var s = new HandSample { isLeft = isLeft, tracked = true, highConfidence = true };
            var j = s.joints;
            var wrist = new Vector3(0.2f * side, 1.2f, 0.4f);
            j[HandJoint.Wrist] = Vector3.zero;
            j[HandJoint.Middle1] = new Vector3(0f, 0f, size);
            j[HandJoint.Index1] = new Vector3(-0.022f * side, 0f, size);
            j[HandJoint.Pinky1] = new Vector3(0.03f * side, 0f, 0.085f);
            j[HandJoint.Thumb1] = new Vector3(-0.03f * side, -0.01f, 0.03f);
            j[HandJoint.IndexTip] = new Vector3(-0.022f * side, 0f, size + 0.075f);
            j[HandJoint.ThumbTip] = pinch
                ? j[HandJoint.IndexTip] + new Vector3(0f, -pinchHandLengths * size, 0f)
                : new Vector3(-0.07f * side, 0f, 0.09f);
            // Roll about the forward axis: +tilt takes the pinky side (at +x*side) downwards.
            var roll = Quaternion.AngleAxis(-tiltDegrees * side, Vector3.forward);
            for (int i = 0; i < j.Length; i++) j[i] = wrist + roll * j[i];
            return s;
        }
    }
}
