using UnityEngine;

/// <summary>
/// Move, turn and scale a <see cref="ForgeVolume"/> with the mouse (DesktopRig) or a controller / hand ray.
/// <see cref="Workspace"/> adds it to every volume it opens; it makes its own grab boxes, so prefabs need nothing.
/// - Drag the base: slide it around (on the desk). Scroll wheel / thumbstick: raise or lower it.
/// - Drag the model: spin it (sideways) and tilt it toward you (up and down). Scroll wheel / thumbstick: scale it.
/// Hands have no thumbstick: reaching out (past a small dead zone) does what pushing the stick forward does.
/// A line of help fades in under the label while it's pointed at (<see cref="ForgeVolume.SetHint"/>).
/// - Throw: let go mid-fling (the hand or controller moving fast, away from you: <see cref="HandThrow"/>) and a volume
///   whose owner says what throwing means (<see cref="ForgeVolume.ThrowHandler"/>, an artifact) is thrown away. Measured
///   from the hand's own speed (the ray's origin), not how fast the ray swings, so a far-off placing drag never throws.
/// </summary>
[RequireComponent(typeof(ForgeVolume))]
public class VolumeManipulator : MonoBehaviour
{
    public enum Grab { None, Move, Turn }

    const float TurnGain = 3f;          // degrees of spin per degree of pointer swing
    const float MaxTilt = 80f;
    const float ScalePerNotch = 1.08f;
    const float MinScale = 0.4f, MaxScale = 3f;
    const float RaisePerNotch = 0.03f;  // m
    const float ReachPerNotch = 0.02f;  // hands: 2 cm of reaching out = one notch
    const float ReachDeadZone = 0.05f;  // hands: the arm drifts while it drags, so the first 5 cm do nothing

    public Transform Head { get; private set; }
    /// <summary>Moved, turned or scaled since it opened (Workspace remembers these per mode).</summary>
    public bool UserPlaced { get; private set; }
    /// <summary>Set while the workspace animates the volume; grabs wait until it's done.</summary>
    public bool Busy { get; set; }
    /// <summary>Set while the model is away from the volume (CAD's Inspect view): its grab box steps aside, the base stays.</summary>
    public bool ModelAway
    {
        get => modelAway;
        set
        {
            modelAway = value;
            if (value && grab == Grab.Turn) Cancel();
            if (modelHandle != null) modelHandle.Box.enabled = !value;
        }
    }

    ForgeVolume volume;
    bool modelAway;
    Transform frame;
    VolumeHandle baseHandle, modelHandle;
    Grab hinted;
    Vector3 builtSize;

    Grab grab;
    VolumeHandle heldHandle;
    ForgePointer pointer;
    Vector3 grabOffset;      // move: volume origin minus the grab point
    float grabHeight, raised;
    float yawAtGrab, tiltAtGrab, rayYawAtGrab, rayPitchAtGrab;
    float reachAtGrab, reachUsed;
    readonly HandThrow fling = new HandThrow();

    /// <summary>Set up the grab boxes. <paramref name="eyes"/>: the player's head; <paramref name="deskFrame"/>: the desk top it rests on.</summary>
    public void Init(Transform eyes, Transform deskFrame)
    {
        Head = eyes;
        frame = deskFrame;
        volume = GetComponent<ForgeVolume>();
        if (baseHandle == null)
        {
            baseHandle = NewHandle("GrabBase", Grab.Move);
            modelHandle = NewHandle("GrabModel", Grab.Turn);
        }
        modelHandle.Box.enabled = !modelAway;
        FitBoxes();
    }

    public void SetPlacement(bool byUser)
    {
        Cancel();
        UserPlaced = byUser;
    }

    /// <summary>Drop the volume if it's held (never a throw).</summary>
    public void Cancel()
    {
        fling.End();
        if (grab != Grab.None) EndGrab(heldHandle);
    }

    void OnDisable() => Cancel();

    VolumeHandle NewHandle(string name, Grab kind)
    {
        var go = new GameObject(name, typeof(BoxCollider));
        go.transform.SetParent(transform, false);
        var handle = go.AddComponent<VolumeHandle>();
        handle.Init(this, kind);
        return handle;
    }

    // Base: a flat slab around the base ring. Model: the volume's box above it.
    void FitBoxes()
    {
        Vector3 s = volume.size;
        builtSize = s;
        baseHandle.Box.center = new Vector3(0f, 0.01f, -0.015f);
        baseHandle.Box.size = new Vector3(s.x + 0.04f, 0.03f, s.z + 0.07f);  // reaches the label in front
        modelHandle.Box.center = new Vector3(0f, 0.03f + s.y * 0.5f, 0f);
        modelHandle.Box.size = new Vector3(s.x * 0.8f, s.y, s.z * 0.8f);
    }

    public void BeginGrab(VolumeHandle handle, ForgePointer by)
    {
        if (grab != Grab.None || Busy || Head == null || by == null) return;
        pointer = by;
        grab = handle.grab;
        heldHandle = handle;
        UserPlaced = true;
        reachAtGrab = pointer.Reach;
        reachUsed = 0f;
        Ray r = pointer.Ray;
        if (pointer.source == ForgePointer.Source.Ray) fling.Begin(Time.unscaledTime, r.origin);  // the hand or controller
        else fling.End();
        if (grab == Grab.Move)
        {
            // Slide in the horizontal plane through the grab point.
            handle.Box.Raycast(r, out RaycastHit hit, 10f);
            Vector3 point = hit.collider != null ? hit.point : transform.position;
            grabHeight = point.y;
            grabOffset = transform.position - point;
            raised = 0f;
        }
        else
        {
            yawAtGrab = volume.ViewYaw;
            tiltAtGrab = volume.ViewTilt;
            Angles(r.direction, out rayYawAtGrab, out rayPitchAtGrab);
            volume.SetView(yawAtGrab, tiltAtGrab);  // stops the turntable where it is
        }
    }

    /// <summary>Let go (the ray's release, or the release was lost): a fling away from the user throws it away.</summary>
    public void EndGrab(VolumeHandle handle)
    {
        if (grab == Grab.None || handle != heldHandle) return;
        bool thrown = false;
        Vector3 velocity = Vector3.zero;
        if (fling.Holding && volume.ThrowHandler != null && Head != null)
            thrown = fling.Release(Time.unscaledTime, Head.position, transform.position, out velocity);
        fling.End();
        grab = Grab.None;
        pointer = null;
        heldHandle = null;
        if (!thrown) return;
        Debug.Log($"[VolumeManipulator] {name} thrown away at {velocity.magnitude:0.0} m/s.");
        volume.ThrowAway(velocity);
    }

    void LateUpdate()
    {
        if (baseHandle == null) return;
        if (volume.size != builtSize) FitBoxes();
        ShowHover();
        if (grab == Grab.None) return;
        if (!pointer.Held)
        {
            EndGrab(heldHandle);
            return;
        }
        Ray r = pointer.Ray;
        fling.Sample(Time.unscaledTime, r.origin);
        float notches = pointer.Notches(Time.deltaTime) + ReachNotches();
        if (grab == Grab.Move) Move(r, notches);
        else Turn(r, notches);
    }

    // Hands: notches from how far the hand has reached out (+) or pulled in (-) since the grab, past the dead zone.
    float ReachNotches()
    {
        if (!pointer.IsHand) return 0f;
        float d = pointer.Reach - reachAtGrab;
        float past = Mathf.Sign(d) * Mathf.Max(0f, Mathf.Abs(d) - ReachDeadZone);
        float notches = (past - reachUsed) / ReachPerNotch;
        reachUsed = past;
        return notches;
    }

    void Move(Ray r, float notches)
    {
        raised += notches * RaisePerNotch;
        var plane = new Plane(Vector3.up, new Vector3(0f, grabHeight, 0f));
        // Pointing at (or above) the horizon would send it off to infinity: hold it until the pointer comes back.
        if (!plane.Raycast(r, out float enter) || enter > 4f) enter = -1f;
        Vector3 p = enter > 0f ? r.GetPoint(enter) + grabOffset : transform.position;
        float floor = frame != null ? frame.position.y : p.y;  // never sink into the desk top
        p.y = Mathf.Max(floor, grabHeight + grabOffset.y + raised);
        raised = p.y - grabHeight - grabOffset.y;
        transform.position = p;
    }

    void Turn(Ray r, float notches)
    {
        Angles(r.direction, out float rayYaw, out float rayPitch);
        float yaw = yawAtGrab - Mathf.DeltaAngle(rayYawAtGrab, rayYaw) * TurnGain;
        float tilt = Mathf.Clamp(tiltAtGrab - Mathf.DeltaAngle(rayPitchAtGrab, rayPitch) * TurnGain, -MaxTilt, MaxTilt);
        volume.SetView(yaw, tilt);
        if (notches != 0f)
        {
            float s = Mathf.Clamp(transform.localScale.x * Mathf.Pow(ScalePerNotch, notches), MinScale, MaxScale);
            transform.localScale = Vector3.one * s;
        }
    }

    // Light up the base ring while pointed at, and say what dragging does on the part that's pointed at (or held).
    void ShowHover()
    {
        Grab over = grab != Grab.None ? grab : modelHandle.Hovered ? Grab.Turn : baseHandle.Hovered ? Grab.Move : Grab.None;
        if (over == hinted) return;
        hinted = over;
        volume.SetHint(Hint(over));
        volume.SetHighlight(over != Grab.None);
    }

    static string Hint(Grab over)
    {
        if (over == Grab.None) return null;
        var rig = DesktopRig.Current;
        bool mouse = rig != null && rig.isActiveAndEnabled;
        bool hands = !mouse && ForgePointer.HandsActive;
        if (over == Grab.Move)
            return mouse ? "Drag to move  ·  Scroll to raise" : hands ? "Pinch to move  ·  Reach out to raise" : "Grab to move  ·  Thumbstick to raise";
        return mouse ? "Drag to turn  ·  Scroll to resize" : hands ? "Pinch to turn  ·  Reach out to grow" : "Grab to turn  ·  Thumbstick to resize";
    }

    static void Angles(Vector3 dir, out float yaw, out float pitch)
    {
        dir.Normalize();
        yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        pitch = -Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
    }
}
