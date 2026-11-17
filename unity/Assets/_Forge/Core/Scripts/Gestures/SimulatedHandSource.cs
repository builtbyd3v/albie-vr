using Forge.Pet;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A pretend right hand for testing with no headset: the engine adds it when the desktop rig is in use. It floats in
/// front of you and aims where the crosshair aims. Keys: 1 relaxed, 2 point, 3 fist, 4 open, 5 palm up, 6 peace sign;
/// Q flick toward you, Y flick away. Only the keys are simulated: everything after the joints is the real gesture code.
/// </summary>
public class SimulatedHandSource : MonoBehaviour, IHandSource
{
    const float ShapeSpeed = 9f;
    static readonly Vector3 Rest = new Vector3(0.1f, -0.17f, 0.34f);  // head space

    HandShape shape = HandShape.Relaxed, wanted = HandShape.Relaxed;
    bool palmUp;
    Vector3 moveBy;
    float moveStart = -10f, moveSeconds = 0.14f;

    readonly Transform[] dots = new Transform[HandJoint.Count];

    /// <summary>Aim here instead of along the crosshair. For the play-mode test, which has no mouse.</summary>
    public Vector3? AimPoint { get; set; }

    // ---- Test hook for the left hand (used by the OS watch-turn play test) ----
    // Off by default: the left hand is never tracked in the sandbox. A test sets LeftTestActive = true and a world pose; Read
    // then reports hand 0 as a tracked, high confidence left hand built by SyntheticHand with that shape, wrist and rotation.
    // In its wrist frame the hand has fingers along +Z and the back of the hand along +Y (identity = left palm down, fingers
    // forward). Set it back to false when done. Reset when the app starts, so it never leaks between Play sessions.

    /// <summary>True: hand 0 (left) reads as tracked at <see cref="LeftTestWrist"/>. False (the default): it is not tracked.</summary>
    public static bool LeftTestActive;
    /// <summary>World position of the test left wrist.</summary>
    public static Vector3 LeftTestWrist;
    /// <summary>World rotation of the test left wrist (identity: palm down, fingers along world +Z).</summary>
    public static Quaternion LeftTestRotation = Quaternion.identity;
    /// <summary>Finger shape of the test left hand.</summary>
    public static HandShape LeftTestShape = HandShape.Relaxed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetLeftTest()
    {
        LeftTestActive = false;
        LeftTestWrist = Vector3.zero;
        LeftTestRotation = Quaternion.identity;
        LeftTestShape = HandShape.Relaxed;
    }

    /// <summary>Change hand shape, as the number keys do. Public for the play-mode test.</summary>
    public void SetShape(HandShape next, bool isPalmUp = false)
    {
        wanted = next;
        palmUp = isPalmUp;
    }

    /// <summary>A quick move, head space, then back, as Q and Y do.</summary>
    public void Flick(Vector3 by, float seconds = 0.14f)
    {
        moveBy = by;
        moveSeconds = seconds;
        moveStart = Time.time;
    }

    void Awake()
    {
        Material material = null;
        for (int i = 0; i < dots.Length; i++)
        {
            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(dot.GetComponent<Collider>());
            dot.name = "SimJoint";
            dot.transform.SetParent(transform, false);
            dot.transform.localScale = Vector3.one * 0.012f;
            var renderer = dot.GetComponent<Renderer>();
            if (material == null)
            {
                material = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.95f, 0.85f, 0.7f) };
            }
            renderer.sharedMaterial = material;
            dots[i] = dot.transform;
        }
    }

    void Update()
    {
        var k = Keyboard.current;
        if (k == null) return;
        if (k.digit1Key.wasPressedThisFrame) SetShape(HandShape.Relaxed);
        if (k.digit2Key.wasPressedThisFrame) SetShape(HandShape.Point);
        if (k.digit3Key.wasPressedThisFrame) SetShape(HandShape.Fist);
        if (k.digit4Key.wasPressedThisFrame) SetShape(HandShape.Open);
        if (k.digit5Key.wasPressedThisFrame) SetShape(HandShape.Open, true);
        if (k.digit6Key.wasPressedThisFrame) SetShape(HandShape.Peace);
        if (k.qKey.wasPressedThisFrame) Flick(new Vector3(0f, 0.03f, -0.14f));
        if (k.yKey.wasPressedThisFrame) Flick(new Vector3(0f, 0f, 0.16f));
    }

    public void Read(int hand, HandSample sample)
    {
        sample.tracked = false;
        sample.highConfidence = false;
        if (hand == 0)
        {
            // Only a test sets this; otherwise the left hand stays untracked, exactly as before.
            if (LeftTestActive) SyntheticHand.Build(sample, LeftTestShape, LeftTestWrist, LeftTestRotation, true);
            return;
        }
        if (hand != 1) return;
        Transform head = PetUser.Head;
        if (head == null) return;

        shape = HandShape.Lerp(shape, wanted, 1f - Mathf.Exp(-ShapeSpeed * 3f * Time.deltaTime));

        // There and back again: out over the flick, back over three times as long.
        float t = Time.time - moveStart;
        float amount = t < moveSeconds ? t / moveSeconds : Mathf.Clamp01(1f - (t - moveSeconds) / (moveSeconds * 3f));
        Vector3 local = Rest + moveBy * amount;
        Vector3 wrist = head.TransformPoint(local);

        var rig = DesktopRig.Current;
        Vector3 aimAt = AimPoint ?? (rig != null ? rig.PointerRay.GetPoint(1f) : head.position + head.forward);
        Vector3 aim = aimAt - head.TransformPoint(Rest);  // from where the hand rests, so a flick doesn't swing the finger
        Quaternion rotation = palmUp
            ? Quaternion.LookRotation(head.forward + Vector3.down * 0.15f, Vector3.down)
            : Quaternion.LookRotation(aim, head.up);
        SyntheticHand.Build(sample, shape, wrist, rotation, false);
        for (int i = 0; i < dots.Length; i++) dots[i].position = sample.joints[i];
    }
}
