using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Oculus.Interaction.Input;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Forge > Gestures > Run Self-Test: the gesture code against synthetic hands, no headset or Play mode. Runs each scenario
/// at the headset's 72 fps and at 30 Hz hand tracking (which repeats frames). Batch:
/// <c>Unity -batchmode -nographics -projectPath unity -executeMethod GestureSelfTest.RunBatch -logFile -</c>
/// Tests the logic, not how real hands feel: real tracking has noise and thresholds that only a headset can tune.
/// </summary>
public static class GestureSelfTest
{
    static readonly Vector3 HeadPosition = new Vector3(0f, 1.2f, 0f);
    static readonly Vector3 WristAtRest = new Vector3(0.1f, 1.03f, 0.34f);
    const float Dt = 1f / 72f;

    static int passed, failed;
    static readonly List<string> failures = new List<string>();

    public static void RunBatch()
    {
        bool ok = RunAll();
        EditorApplication.Exit(ok ? 0 : 1);
    }

    [MenuItem("Forge/Gestures/Run Self-Test")]
    public static void RunMenu() => RunAll();

    static bool RunAll()
    {
        passed = failed = 0;
        failures.Clear();
        foreach (float rate in new[] { 72f, 30f })
        {
            trackingRate = rate;
            PosesAreRecognised();
            HandSizeDoesNotMatter();
            FlickIn();
            FlickOut();
            LeftHandFlick();
            FlickNeedsPointing();
            FlickNeedsWindow();
            SlowPullIsNotAFlick();
            StopPointingIsNotAFlick();
            FistOpenAndClose();
            LeftHandDoesEveryWindow();
            HandsSettingOff();
            FlickThenCurlDoesNotClose();
            RelaxedToFistDoesNotClose();
            SlowFistOpenDoesNothing();
            PalmUpHoldFiresOnce();
            PalmUpOutOfViewDoesNothing();
            RestingHandFiresNothing();
            TrackingLossResets();
            OnlyRaisedSteadyHandsCount();
            PalmHeldOutLowCallsAlbie();
            TeachPeaceSign();
            TeachMove();
            TeachRestingHandRefused();
            TeachInconsistentRefused();
            TeachClashRefused();
        }
        SdkSkeletonReadsRight(HandSkeleton.DefaultLeftSkeleton, true);
        SdkSkeletonReadsRight(HandSkeleton.DefaultRightSkeleton, false);
        LibrarySaveAndLoad();
        BindingRules();
        string summary = $"[GestureSelfTest] {passed} passed, {failed} failed";
        if (failed == 0) Debug.Log(summary);
        else Debug.LogError(summary + "\n" + string.Join("\n", failures));
        return failed == 0;
    }

    static float trackingRate = 72f;

    static void Check(bool condition, string what)
    {
        if (condition)
        {
            passed++;
            return;
        }
        failed++;
        failures.Add($"{trackingRate} Hz: {what}");
    }

    // ---------------------------------------------------------------- a little hand-driving harness

    sealed class Sim
    {
        public readonly GestureLibrary library = new GestureLibrary(null);
        public readonly GestureHand hand = new GestureHand();
        public readonly List<GestureMatcher> matchers = new List<GestureMatcher>();
        public readonly List<string> fired = new List<string>();
        public bool hasWindow = true;
        public bool isLeft;
        public HandSplit split = HandSplit.LeftAll;
        /// <summary>Step the hand as the engine does: nothing from a hand that isn't held out, and only the low-hand gestures from one that isn't raised.</summary>
        public bool gated;
        public float size = 1f;
        public float now = 10f;
        float nextTrackerFrame;

        public Sim() => Rebuild();

        public void Rebuild()
        {
            matchers.Clear();
            foreach (var def in library.All) matchers.Add(new GestureMatcher(def));
        }

        /// <summary>Hold a shape for a while. Move, in the head's frame, is spread over the time.</summary>
        public void Run(float seconds, Func<float, HandShape> shape, Func<float, Vector3> offset = null, bool palmUp = false, bool outOfView = false, bool tracked = true, Action<GestureHand, float> eachFrame = null)
        {
            float start = now;
            float end = now + seconds;
            while (now < end)
            {
                float k = Mathf.Clamp01((now - start) / seconds);
                if (now >= nextTrackerFrame)
                {
                    nextTrackerFrame = now + 1f / trackingRate;
                    Vector3 local = offset != null ? offset(k) : Vector3.zero;
                    Vector3 wrist = (outOfView ? new Vector3(0.1f, 0.5f, -0.1f) : WristAtRest) + local;
                    if (isLeft) wrist.x = -wrist.x + 2f * HeadPosition.x;
                    Quaternion rotation = palmUp ? Quaternion.LookRotation(new Vector3(0f, -0.15f, 1f), Vector3.down) : Quaternion.identity;
                    SyntheticHand.Build(hand.sample, shape(k), wrist, rotation, isLeft, size);
                    hand.sample.tracked = tracked;
                }
                hand.Update(now, HeadPosition, Quaternion.identity);
                hand.sample.isLeft = isLeft;
                if (gated ? hand.HeldOut : hand.Usable)
                {
                    GestureDef def = GestureEngine.StepHand(matchers, hand, now, hasWindow, split, gated && !hand.Deliberate);
                    if (def != null) fired.Add(def.id);
                }
                else foreach (var m in matchers) m.Reset();
                eachFrame?.Invoke(hand, now);
                now += Dt;
            }
        }

        public void Hold(float seconds, HandShape shape) => Run(seconds, _ => shape);

        public int Count(string id)
        {
            int n = 0;
            foreach (var f in fired)
                if (f == id) n++;
            return n;
        }

        public GestureRecorder.Take RecordTake(Func<float, HandShape> shape, Func<float, Vector3> offset = null)
        {
            var take = new GestureRecorder.Take();
            Run(1.6f, shape, offset, eachFrame: (h, t) =>
            {
                if (h.Usable) take.frames.Add(new GestureRecorder.Frame { time = t, values = (float[])h.values.Clone(), motion = h.motion });
            });
            return take;
        }
    }

    // There and back over the given share of the time, starting partway in.
    static Func<float, Vector3> Stroke(Vector3 by, float begin, float span) => k =>
    {
        float t = Mathf.InverseLerp(begin, begin + span, k);
        if (k > begin + span) t = Mathf.Clamp01(1f - (k - begin - span) / (span * 3f));
        return by * t;
    };

    // ---------------------------------------------------------------- recognising

    static void PosesAreRecognised()
    {
        var s = new Sim();
        s.Hold(0.05f, HandShape.Relaxed);
        Check(GestureLibrary.PoseName(s.hand.values) == "Relaxed", "relaxed reads as Relaxed, got " + GestureLibrary.PoseName(s.hand.values));
        s.Hold(0.05f, HandShape.Point);
        Check(GestureLibrary.PoseName(s.hand.values) == "Point", "point reads as Point, got " + GestureLibrary.PoseName(s.hand.values));
        s.Hold(0.05f, HandShape.Fist);
        Check(GestureLibrary.PoseName(s.hand.values) == "Fist", "fist reads as Fist, got " + GestureLibrary.PoseName(s.hand.values));
        s.Hold(0.05f, HandShape.Open);
        Check(GestureLibrary.PoseName(s.hand.values) == "Open", "open reads as Open, got " + GestureLibrary.PoseName(s.hand.values));
        s.Run(0.05f, _ => HandShape.Open, palmUp: true);
        Check(GestureLibrary.PoseName(s.hand.values) == "Palm up", "palm up reads as Palm up, got " + GestureLibrary.PoseName(s.hand.values));
    }

    static void HandSizeDoesNotMatter()
    {
        foreach (float size in new[] { 0.8f, 1.25f })
        {
            var s = new Sim { size = size };
            s.Hold(0.05f, HandShape.Point);
            Check(GestureLibrary.PoseName(s.hand.values) == "Point", $"point at hand size {size}");
            s.Hold(0.05f, HandShape.Fist);
            Check(GestureLibrary.PoseName(s.hand.values) == "Fist", $"fist at hand size {size}");
        }
    }

    static void FlickIn()
    {
        var s = new Sim();
        s.Hold(0.4f, HandShape.Point);
        s.Run(0.5f, _ => HandShape.Point, Stroke(new Vector3(0f, 0.03f, -0.14f), 0f, 0.3f));
        Check(s.Count(GestureLibrary.FlickIn) == 1, $"a flick toward you fires once (got {s.Count(GestureLibrary.FlickIn)})");
        Check(s.Count(GestureLibrary.FlickOut) == 0, "a flick toward you isn't also a flick away, even on the way back");
    }

    static void FlickOut()
    {
        var s = new Sim();
        s.Hold(0.4f, HandShape.Point);
        s.Run(0.5f, _ => HandShape.Point, Stroke(new Vector3(0f, 0f, 0.16f), 0f, 0.3f));
        Check(s.Count(GestureLibrary.FlickOut) == 1, $"a flick away fires once (got {s.Count(GestureLibrary.FlickOut)})");
        Check(s.Count(GestureLibrary.FlickIn) == 0, "a flick away isn't also a flick toward you, even on the way back");
    }

    static void LeftHandFlick()
    {
        var s = new Sim { isLeft = true };
        s.Hold(0.4f, HandShape.Point);
        s.Run(0.5f, _ => HandShape.Point, Stroke(new Vector3(0f, 0.03f, -0.14f), 0f, 0.3f));
        Check(s.Count(GestureLibrary.FlickIn) == 0, "the left hand doesn't flick while it does every window");
        s = new Sim { isLeft = true, split = HandSplit.Off };
        s.Hold(0.4f, HandShape.Point);
        s.Run(0.5f, _ => HandShape.Point, Stroke(new Vector3(0f, 0.03f, -0.14f), 0f, 0.3f));
        Check(s.Count(GestureLibrary.FlickIn) == 1, "with the hands setting off, the left hand can flick too");
    }

    static void FlickNeedsPointing()
    {
        var s = new Sim();
        s.Hold(0.4f, HandShape.Open);
        s.Run(0.5f, _ => HandShape.Open, Stroke(new Vector3(0f, 0.03f, -0.14f), 0f, 0.3f));
        Check(s.Count(GestureLibrary.FlickIn) == 0, "an open hand moving back doesn't flick a window");
    }

    static void FlickNeedsWindow()
    {
        var s = new Sim { hasWindow = false };
        s.Hold(0.4f, HandShape.Point);
        s.Run(0.5f, _ => HandShape.Point, Stroke(new Vector3(0f, 0.03f, -0.14f), 0f, 0.3f));
        Check(s.Count(GestureLibrary.FlickIn) == 0, "no window pointed at, no flick");
    }

    static void SlowPullIsNotAFlick()
    {
        var s = new Sim();
        s.Hold(0.4f, HandShape.Point);
        s.Run(1.5f, _ => HandShape.Point, k => new Vector3(0f, 0f, -0.15f) * k);
        Check(s.Count(GestureLibrary.FlickIn) == 0, "slowly drawing the hand back isn't a flick");
    }

    static void StopPointingIsNotAFlick()
    {
        foreach (float seconds in new[] { 0.08f, 0.15f, 0.3f })
        {
            var s = new Sim();
            s.Hold(0.4f, HandShape.Point);
            s.Run(seconds, k => HandShape.Lerp(HandShape.Point, HandShape.Fist, k));
            s.Hold(0.3f, HandShape.Fist);
            Check(s.Count(GestureLibrary.FlickIn) == 0 && s.Count(GestureLibrary.FlickOut) == 0, $"curling your finger to stop pointing ({seconds} s) doesn't flick the window, fired: {string.Join(",", s.fired)}");
        }
    }

    static void OpenThenClose(Sim s)
    {
        s.Hold(0.3f, HandShape.Open);
        s.Run(0.3f, k => HandShape.Lerp(HandShape.Open, HandShape.Fist, k));
        s.Hold(0.8f, HandShape.Fist);
        s.Run(0.25f, k => HandShape.Lerp(HandShape.Fist, HandShape.Open, k));
        s.Hold(0.3f, HandShape.Open);
    }

    static void FistOpenAndClose()
    {
        var s = new Sim();
        OpenThenClose(s);
        Check(s.Count(GestureLibrary.FistClose) == 1, $"closing the right hand closes one window, once (got {s.Count(GestureLibrary.FistClose)})");
        Check(s.Count(GestureLibrary.FistOpen) == 1, $"bursting it open reopens one, once (got {s.Count(GestureLibrary.FistOpen)})");
        Check(s.Count(GestureLibrary.FistCloseAll) == 0 && s.Count(GestureLibrary.FistOpenAll) == 0, "the right hand doesn't do every window: " + string.Join(",", s.fired));
    }

    static void LeftHandDoesEveryWindow()
    {
        var s = new Sim { isLeft = true };
        OpenThenClose(s);
        Check(s.Count(GestureLibrary.FistCloseAll) == 1 && s.Count(GestureLibrary.FistOpenAll) == 1, "the left hand hides and shows every window: " + string.Join(",", s.fired));
        Check(s.Count(GestureLibrary.FistClose) == 0 && s.Count(GestureLibrary.FistOpen) == 0, "and not one window as well");
        s = new Sim { split = HandSplit.RightAll };
        OpenThenClose(s);
        Check(s.Count(GestureLibrary.FistCloseAll) == 1 && s.Count(GestureLibrary.FistClose) == 0, "swapped, the right hand does every window: " + string.Join(",", s.fired));
    }

    static void HandsSettingOff()
    {
        foreach (bool left in new[] { true, false })
        {
            var s = new Sim { isLeft = left, split = HandSplit.Off };
            OpenThenClose(s);
            Check(s.Count(GestureLibrary.FistCloseAll) == 0 && s.Count(GestureLibrary.FistClose) == 1, $"hands setting off: the {(left ? "left" : "right")} hand does one window: " + string.Join(",", s.fired));
        }
    }

    // What was going wrong: after a flick the finger curls in, and the hand opens again. Neither is a close or an open.
    static void FlickThenCurlDoesNotClose()
    {
        foreach (float seconds in new[] { 0.08f, 0.2f })
        {
            var s = new Sim();
            s.Hold(0.4f, HandShape.Point);
            s.Run(0.5f, _ => HandShape.Point, Stroke(new Vector3(0f, 0f, 0.16f), 0f, 0.3f));
            s.Run(seconds, k => HandShape.Lerp(HandShape.Point, HandShape.Fist, k));
            s.Hold(0.7f, HandShape.Fist);
            s.Run(0.2f, k => HandShape.Lerp(HandShape.Fist, HandShape.Open, k));
            s.Hold(0.3f, HandShape.Open);
            Check(s.Count(GestureLibrary.FlickOut) == 1, "the flick itself still fires");
            Check(s.Count(GestureLibrary.FistClose) == 0 && s.Count(GestureLibrary.FistOpen) == 0,
                  $"curling the finger after a flick ({seconds} s), then opening the hand, closes and reopens nothing: {string.Join(",", s.fired)}");
        }
    }

    static void RelaxedToFistDoesNotClose()
    {
        var s = new Sim();
        s.Hold(0.4f, HandShape.Relaxed);
        s.Run(0.3f, k => HandShape.Lerp(HandShape.Relaxed, HandShape.Fist, k));
        s.Hold(0.3f, HandShape.Fist);
        Check(s.Count(GestureLibrary.FistClose) == 0, "a resting hand closing isn't a close: it has to open first");
    }

    static void SlowFistOpenDoesNothing()
    {
        var s = new Sim();
        s.Hold(0.3f, HandShape.Fist);
        s.Run(2f, k => HandShape.Lerp(HandShape.Fist, HandShape.Relaxed, k));
        s.Hold(0.2f, HandShape.Relaxed);
        Check(s.Count(GestureLibrary.FistOpen) == 0, "slowly relaxing a fist isn't a burst open");
    }

    static void PalmUpHoldFiresOnce()
    {
        var s = new Sim();
        s.Run(0.3f, _ => HandShape.Open, palmUp: true);
        Check(s.Count(GestureLibrary.PalmUp) == 0, "palm up needs to be held a moment");
        s.Run(1f, _ => HandShape.Open, palmUp: true);
        Check(s.Count(GestureLibrary.PalmUp) == 1, $"palm up fires once while held (got {s.Count(GestureLibrary.PalmUp)})");
        s.Hold(0.4f, HandShape.Relaxed);
        s.Run(1f, _ => HandShape.Open, palmUp: true);
        Check(s.Count(GestureLibrary.PalmUp) == 2, "and again after turning the hand away and back");
    }

    static void PalmUpOutOfViewDoesNothing()
    {
        var s = new Sim();
        s.Run(1.5f, _ => HandShape.Open, palmUp: true, outOfView: true);
        Check(s.Count(GestureLibrary.PalmUp) == 0, "a hand resting palm up out of view doesn't call Albie");
    }

    static void RestingHandFiresNothing()
    {
        var s = new Sim();
        var random = new System.Random(7);
        // Five seconds of an idle hand: small drift around relaxed.
        for (int i = 0; i < 25; i++)
        {
            var from = Wiggle(random);
            var to = Wiggle(random);
            s.Run(0.2f, k => HandShape.Lerp(from, to, k), k => new Vector3(0.01f, 0.01f, 0.01f) * Mathf.Sin(k * 6f));
        }
        Check(s.fired.Count == 0, "an idle hand fires nothing, got: " + string.Join(", ", s.fired));
    }

    static HandShape Wiggle(System.Random r)
    {
        float W(float x) => x + (float)(r.NextDouble() - 0.5) * 0.14f;
        var b = HandShape.Relaxed;
        return new HandShape(W(b.index), W(b.middle), W(b.ring), W(b.pinky), W(b.thumb), W(b.spread));
    }

    static void TrackingLossResets()
    {
        var s = new Sim();
        s.Hold(0.4f, HandShape.Point);
        s.Run(0.3f, _ => HandShape.Point, tracked: false);
        s.Run(0.05f, _ => HandShape.Point, _ => new Vector3(0f, 0f, -0.14f));  // the hand reappears somewhere else
        Check(s.Count(GestureLibrary.FlickIn) == 0, "a hand jumping after a tracking gap isn't a flick");
    }

    // The engine matches only Deliberate hands: tracked a while, up in front of the face. Not typing, resting or just appeared.
    static void OnlyRaisedSteadyHandsCount()
    {
        var s = new Sim();
        s.Hold(0.3f, HandShape.Relaxed);
        Check(!s.hand.Deliberate, "a hand that just appeared can't gesture yet");
        s.Hold(0.4f, HandShape.Relaxed);
        Check(s.hand.Deliberate, "a hand held up in front for a moment can gesture");
        s.Run(0.8f, _ => HandShape.Relaxed, _ => new Vector3(0f, -0.35f, 0.05f));
        Check(!s.hand.Deliberate, "a hand down at keyboard height can't gesture");
        s.Run(0.8f, _ => HandShape.Relaxed, outOfView: true);
        Check(!s.hand.Deliberate, "a hand out of view can't gesture");
        s.Hold(0.8f, HandShape.Relaxed);
        s.Run(0.1f, _ => HandShape.Relaxed, tracked: false);
        s.Hold(0.2f, HandShape.Relaxed);
        Check(!s.hand.Deliberate, "after a tracking gap the hand waits again");
    }

    // A palm is offered at elbow height, under where the other gestures count from. Call Albie still has to work there,
    // and nothing else may.
    static void PalmHeldOutLowCallsAlbie()
    {
        var atElbow = new Vector3(0f, -0.28f, 0.1f);  // the palm about 0.46 m under the eyes
        var s = new Sim { gated = true };
        s.Run(1.5f, _ => HandShape.Open, _ => atElbow, palmUp: true);
        Check(!s.hand.Deliberate && s.hand.HeldOut, "a palm at elbow height is held out, not raised");
        Check(s.Count(GestureLibrary.PalmUp) == 1, $"a palm held out at elbow height calls Albie (fired: {string.Join(",", s.fired)})");

        s = new Sim { gated = true };
        s.Run(0.8f, _ => HandShape.Open, _ => atElbow);
        s.Run(0.3f, k => HandShape.Lerp(HandShape.Open, HandShape.Fist, k), _ => atElbow);
        s.Run(0.8f, _ => HandShape.Fist, _ => atElbow);
        s.Run(0.25f, k => HandShape.Lerp(HandShape.Fist, HandShape.Open, k), _ => atElbow);
        s.Run(0.3f, _ => HandShape.Open, _ => atElbow);
        Check(s.fired.Count == 0, $"opening and closing the hand down there does nothing (fired: {string.Join(",", s.fired)})");

        var inLap = new Vector3(0f, -0.55f, 0.1f);    // about 0.73 m under the eyes
        s = new Sim { gated = true };
        s.Run(1.5f, _ => HandShape.Open, _ => inLap, palmUp: true);
        Check(!s.hand.HeldOut && s.Count(GestureLibrary.PalmUp) == 0, "a palm up in the lap doesn't call Albie");
    }

    // The SDK's own reference hand, through the same joint map the headset uses. Synthetic hands share our joint numbers,
    // so only this catches the map being wrong. The skeleton is an open hand.
    static void SdkSkeletonReadsRight(HandSkeleton skeleton, bool isLeft)
    {
        string side = isLeft ? "left" : "right";
        var world = new Pose[skeleton.joints.Length];
        var done = new bool[skeleton.joints.Length];
        Pose Solve(int i)
        {
            if (done[i]) return world[i];
            HandSkeletonJoint joint = skeleton.joints[i];
            world[i] = joint.parent < 0 ? joint.pose : joint.pose.GetTransformedBy(Solve(joint.parent));
            done[i] = true;
            return world[i];
        }

        var sample = new HandSample { isLeft = isLeft };
        foreach (var (ours, sdk) in IsdkHandSource.Joints) sample.joints[ours] = Solve((int)sdk).position;

        Check(Vector3.Distance(sample.joints[HandJoint.Wrist], Vector3.zero) < 1e-4f, $"SDK {side} hand: the wrist is the root");
        float toMiddleTip = Vector3.Distance(sample.joints[HandJoint.Wrist], sample.joints[HandJoint.MiddleTip]);
        Check(toMiddleTip > 0.14f && toMiddleTip < 0.24f, $"SDK {side} hand: wrist to middle fingertip is hand-length ({toMiddleTip:F3} m)");

        Vector3 palm = HandFeatures.PalmCenter(sample);
        var values = new float[HandFeatures.Count];
        Vector3 head = palm - (sample.joints[HandJoint.Middle1] - sample.joints[HandJoint.Wrist]).normalized * 0.4f;
        HandFeatures.Compute(sample, head, (palm - head).normalized, values);
        string pose = GestureLibrary.PoseName(values);
        Check(pose == "Open" || pose == "Palm up", $"SDK {side} hand: its open hand reads as open, got {pose}: {string.Join(" ", Array.ConvertAll(values, v => v.ToString("F2")))}");

        // The palm faces the way the thumb says it does, whichever axes the SDK uses: fingers cross thumb side, mirrored for a left hand.
        Vector3 fingers = (sample.joints[HandJoint.Middle1] - sample.joints[HandJoint.Wrist]).normalized;
        Vector3 thumbSide = (sample.joints[HandJoint.Index1] - sample.joints[HandJoint.Pinky1]).normalized;
        Vector3 expected = Vector3.Cross(fingers, thumbSide) * (isLeft ? -1f : 1f);
        Check(Vector3.Dot(HandFeatures.PalmNormal(sample), expected) > 0.8f, $"SDK {side} hand: the palm normal points out of the palm");
    }

    // ---------------------------------------------------------------- teaching

    static readonly Func<float, HandShape> Peace = k => k < 0.3f ? HandShape.Lerp(HandShape.Relaxed, HandShape.Peace, k / 0.3f) : HandShape.Peace;

    static List<GestureRecorder.Take> Takes(Sim s, Func<float, HandShape> shape, Func<float, Vector3> offset = null)
    {
        var takes = new List<GestureRecorder.Take>();
        for (int i = 0; i < GestureRecorder.Takes; i++)
        {
            takes.Add(s.RecordTake(shape, offset));
            s.Hold(0.8f, HandShape.Relaxed);
        }
        return takes;
    }

    static void TeachPeaceSign()
    {
        var s = new Sim();
        var def = GestureRecorder.Analyze(Takes(s, Peace), "peace", "Peace", out string error, out float[] center);
        Check(def != null, "a peace sign can be taught: " + error);
        if (def == null) return;
        Check(def.IsPlainPose, "a held peace sign is taught as a pose, not a move");
        s.library.Add(def);
        s.Rebuild();
        s.fired.Clear();
        s.Hold(0.8f, HandShape.Peace);
        Check(s.Count("peace") == 1, $"the taught peace sign fires (got {s.Count("peace")})");
        s.Hold(0.5f, HandShape.Relaxed);
        s.fired.Clear();
        s.Hold(0.8f, HandShape.Point);
        s.Hold(0.8f, HandShape.Fist);
        s.Hold(0.8f, HandShape.Relaxed);
        s.Hold(0.8f, HandShape.Rock);
        Check(s.Count("peace") == 0, "and nothing else fires it");

        var big = new Sim { size = 1.2f };
        big.library.Add(def);
        big.Rebuild();
        big.Hold(0.8f, HandShape.Peace);
        Check(big.Count("peace") == 1, "someone with bigger hands can do it too");
    }

    static void TeachMove()
    {
        var s = new Sim();
        var up = new Vector3(0f, 0.22f, 0f);
        var def = GestureRecorder.Analyze(Takes(s, _ => HandShape.Rock, Stroke(up, 0.4f, 0.12f)), "rock_up", "Rock up", out string error, out float[] center);
        Check(def != null, "a shape then a move can be taught: " + error);
        if (def == null) return;
        Check(def.motion != null && def.motion.direction.y > 0.8f, $"it learns the move was upward (got {def.motion?.direction})");
        s.library.Add(def);
        s.Rebuild();
        s.fired.Clear();
        s.Hold(0.4f, HandShape.Rock);
        s.Run(0.5f, _ => HandShape.Rock, Stroke(up, 0f, 0.25f));
        Check(s.Count("rock_up") == 1, $"the taught move fires (got {s.Count("rock_up")})");
        s.Hold(0.8f, HandShape.Relaxed);
        s.fired.Clear();
        s.Hold(0.8f, HandShape.Rock);
        Check(s.Count("rock_up") == 0, "holding the shape still doesn't fire it");
        s.Run(0.5f, _ => HandShape.Rock, Stroke(-up, 0f, 0.25f));
        Check(s.Count("rock_up") == 0, "moving the other way doesn't fire it");
    }

    static void TeachRestingHandRefused()
    {
        var s = new Sim();
        var def = GestureRecorder.Analyze(Takes(s, _ => HandShape.Relaxed), "rest", "Rest", out string error, out _);
        Check(def == null && error != null && error.Contains("resting"), "a resting hand can't be a gesture: " + error);
    }

    static void TeachInconsistentRefused()
    {
        var s = new Sim();
        var takes = new List<GestureRecorder.Take>
        {
            s.RecordTake(_ => HandShape.Peace),
            s.RecordTake(_ => HandShape.Fist),
            s.RecordTake(_ => HandShape.Rock),
        };
        var def = GestureRecorder.Analyze(takes, "mixed", "Mixed", out string error, out _);
        Check(def == null && error != null, "three different shapes are refused");
    }

    static void TeachClashRefused()
    {
        var s = new Sim();
        var first = GestureRecorder.Analyze(Takes(s, _ => HandShape.Peace), "peace", "Peace", out _, out _);
        Check(first != null, "first peace sign taught");
        if (first == null) return;
        s.library.Add(first);
        var second = GestureRecorder.Analyze(Takes(s, _ => HandShape.Peace), "victory", "Victory", out _, out float[] center);
        Check(second != null && s.library.Clash(second, center)?.id == "peace", "teaching the same shape twice is caught");
    }

    // ---------------------------------------------------------------- saving and binding

    static void LibrarySaveAndLoad()
    {
        var s = new Sim();
        var def = GestureRecorder.Analyze(Takes(s, _ => HandShape.Peace), "peace", "Peace", out _, out _);
        def.action = GestureActions.ToolPrefix + "ui_open_view";
        def.args = new JObject { ["view"] = "cad" };
        s.library.Add(def);
        s.library.Bind(s.library.Find(GestureLibrary.FistClose), GestureActions.None, null);

        var loaded = new GestureLibrary(null);
        loaded.LoadJson(JObject.Parse(s.library.ToJson().ToString()));
        var back = loaded.Find("Peace");
        Check(back != null && back.action == def.action && (string)back.args["view"] == "cad", "a taught gesture and its action survive saving");
        Check(loaded.Find(GestureLibrary.FistClose).action == GestureActions.None, "a rebound built-in survives saving");
        Check(back != null && !back.builtin && loaded.Remove(back), "a taught gesture can be forgotten");
        Check(!loaded.Remove(loaded.Find(GestureLibrary.PalmUp)), "a built-in can't be");

        var sim = new Sim();
        sim.library.Add(GestureDef.FromJson(def.ToJson()));
        sim.Rebuild();
        sim.Hold(0.8f, HandShape.Peace);
        Check(sim.Count("peace") == 1, $"a reloaded gesture still fires (got {sim.Count("peace")}, fired {string.Join(",", sim.fired)}; values {string.Join(" ", System.Array.ConvertAll(sim.hand.values, v => v.ToString("F2")))}; def {def.ToJson()})");
    }

    static void BindingRules()
    {
        Check(GestureActions.CheckBinding(GestureActions.WindowSummon) == null, "a built-in action can be bound");
        Check(GestureActions.CheckBinding("tool:messages_confirm") != null, "sending can't be bound to a gesture");
        Check(GestureActions.CheckBinding("nonsense") != null, "an unknown action is refused");
        Check(GestureActions.CheckBinding("tool:not_a_tool") != null, "an unknown tool is refused");
        Check(GestureLibrary.Slug(" Peace Sign! ") == "peace_sign", "names make tidy ids");
    }
}
