using Oculus.Interaction.Input;
using UnityEngine;

/// <summary>
/// Reads real hands from the Interaction SDK rig. Uses each hand's plain Hand component, leaving out the controller-driven
/// ones (they pretend to be hands while you hold controllers) and the SDK's modified copies: the filtered and synthetic
/// hands lag or are held back from going through surfaces, which isn't what gestures should read.
/// </summary>
public sealed class IsdkHandSource : IHandSource
{
    const float RescanSeconds = 2f;

    /// <summary>Our joint numbers and the SDK's, by name: its numbering depends on how the SDK was compiled. Public for the self-test.</summary>
    public static readonly (int ours, HandJointId sdk)[] Joints =
    {
        (HandJoint.Wrist, HandJointId.HandWristRoot),
        (HandJoint.Thumb1, HandJointId.HandThumb1), (HandJoint.Thumb2, HandJointId.HandThumb2), (HandJoint.Thumb3, HandJointId.HandThumb3), (HandJoint.ThumbTip, HandJointId.HandThumbTip),
        (HandJoint.Index1, HandJointId.HandIndex1), (HandJoint.Index2, HandJointId.HandIndex2), (HandJoint.Index3, HandJointId.HandIndex3), (HandJoint.IndexTip, HandJointId.HandIndexTip),
        (HandJoint.Middle1, HandJointId.HandMiddle1), (HandJoint.Middle2, HandJointId.HandMiddle2), (HandJoint.Middle3, HandJointId.HandMiddle3), (HandJoint.MiddleTip, HandJointId.HandMiddleTip),
        (HandJoint.Ring1, HandJointId.HandRing1), (HandJoint.Ring2, HandJointId.HandRing2), (HandJoint.Ring3, HandJointId.HandRing3), (HandJoint.RingTip, HandJointId.HandRingTip),
        (HandJoint.Pinky1, HandJointId.HandPinky1), (HandJoint.Pinky2, HandJointId.HandPinky2), (HandJoint.Pinky3, HandJointId.HandPinky3), (HandJoint.PinkyTip, HandJointId.HandPinkyTip),
    };

    readonly Hand[] hands = new Hand[2];
    float nextScan;

    public void Read(int hand, HandSample sample)
    {
        sample.tracked = false;
        sample.highConfidence = false;
        if (hands[hand] == null && Time.time >= nextScan) Scan();
        Hand source = hands[hand];
        if (source == null) return;
        // Asked too early, a Hand can report the wrong side: drop it and look again.
        if (source.Handedness != (hand == 0 ? Handedness.Left : Handedness.Right))
        {
            hands[hand] = null;
            return;
        }
        if (!source.isActiveAndEnabled || !source.IsConnected || !ForgePointer.HandsActive) return;

        for (int i = 0; i < Joints.Length; i++)
        {
            if (!source.GetJointPose(Joints[i].sdk, out Pose pose)) return;
            sample.joints[Joints[i].ours] = pose.position;
        }
        sample.tracked = true;
        sample.highConfidence = source.IsHighConfidence;
    }

    // ponytail: rescan (allocates) every 2 s while a hand is missing, as GatorHandPoke does, instead of hooking the rig's setup.
    void Scan()
    {
        nextScan = Time.time + RescanSeconds;
        foreach (var candidate in Object.FindObjectsByType<Hand>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (candidate.GetType() != typeof(Hand) || IsControllerDriven(candidate.transform)) continue;
            int side = candidate.Handedness == Handedness.Left ? 0 : 1;
            if (hands[side] == null) hands[side] = candidate;
        }
    }

    static bool IsControllerDriven(Transform t)
    {
        for (; t != null; t = t.parent)
            if (t.name.Contains("Controller")) return true;
        return false;
    }
}
