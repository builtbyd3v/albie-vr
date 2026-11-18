using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// cad.turn (hub -> Unity) and the headset's own cad_turn tool: rotate the open design about its centre (hub/cad/tools.py).
/// </summary>
public class CadTurnRequest
{
    [JsonProperty("degrees")] public float? degrees;  // turn this far from where it is (90 if nothing else is asked)
    [JsonProperty("axis")] public string axis;        // up (default): about its vertical | side: tip it toward (+) the user
    [JsonProperty("face")] public string face;        // front | back | left | right: turn so that side faces the user
    [JsonProperty("spin")] public bool? spin;         // true: keep turning slowly; false: stop
    [JsonProperty("reset")] public bool? reset;       // back to how it first stood
    [JsonProperty("part")] public string part;        // the design it is for (informational)
    [JsonProperty("version")] public int? version;
    [JsonProperty("seq")] public int seq;
}

/// <summary>
/// Turning the CAD model as a whole (voice: cad_turn; hands: <see cref="CadTurnHandle"/> on the stage, the volume's grips on
/// the desk). View only, like explode: nothing is rebuilt, and an exploded or expanded model stays exploded and expanded,
/// because neither the explode (part positions in their parents) nor the stage trip (the holder's pose) is touched.
/// - Desk: the volume's own view (<see cref="ForgeVolume.SetView"/>: yaw about the box's centre, tilt toward the user) and
///   its turntable for a spin. A grab of the volume meanwhile wins (the animation stops where the hand took over).
/// - Stage: a pivot between the stage and the model's holder, at the stage's origin, which is the model's centre (fitted)
///   or its feet (life size). Yaw turns it about its own up (the stage's tilt is undone and redone around it), tilt tips it.
/// Both ease there on a critically damped spring (about 0.3 s), so a quick second ask adds to the first. Everything here
/// is allocation free per frame. CadViewer owns one and ticks it in Update, before the demo poses the model in LateUpdate
/// (a demo's rest pose is read through its parent every frame, so turning during a demo is fine).
/// </summary>
public sealed class CadTurn
{
    public const float SpinDegreesPerSecond = 30f, MaxTilt = 75f, SmoothSeconds = 0.3f;
    const float DeskDone = 0.05f;

    readonly ForgeVolume volume;
    Transform pivot;

    // Stage: the user's turn on top of the stage's own pose.
    float yaw, yawTo, yawSpeed, tilt, tiltTo, tiltSpeed;
    float baseTilt, faceOffset;
    bool upright, spinning, dragging;
    float appliedYaw, appliedTilt;
    bool posed;  // the pivot shows (appliedYaw, appliedTilt) for the current base

    // Desk: the volume's view, eased.
    bool deskMoving;
    float dYaw, dYawTo, dYawSpeed, dTilt, dTiltTo, dTiltSpeed, setYaw, setTilt;

    public CadTurn(ForgeVolume deskVolume) => volume = deskVolume;

    /// <summary>The stage's pivot (CadViewer makes it with the stage).</summary>
    public void BindStage(Transform stagePivot)
    {
        pivot = stagePivot;
        posed = false;
    }

    /// <summary>
    /// The stage's own pose of the model (CadViewer.PlaceOnStage): its tilt toward the user and its yaw past "front facing
    /// you" (stageYaw, a corner; 0 for a Run design). <paramref name="lifeSize"/>: it stands on the floor, so it only turns
    /// about its vertical (a tip would sink it into the floor).
    /// </summary>
    public void SetStageBase(float tiltDegrees, float yawPastFront, bool lifeSize)
    {
        baseTilt = tiltDegrees;
        faceOffset = yawPastFront;
        upright = lifeSize;
        posed = false;
    }

    /// <summary>The user's turn on the stage (deg, where it is heading).</summary>
    public float StageYaw => yawTo;
    public float StageTilt => tiltTo;
    /// <summary>The volume's view on the desk (deg, where it is heading).</summary>
    public float DeskYaw => volume == null ? 0f : deskMoving ? dYawTo : volume.ViewYaw;
    public float DeskTilt => volume == null ? 0f : deskMoving ? dTiltTo : volume.ViewTilt;
    /// <summary>Still easing toward an asked-for turn (stage or desk).</summary>
    public bool Moving => deskMoving || Mathf.Abs(yawTo - yaw) > 0.01f || Mathf.Abs(tiltTo - tilt) > 0.01f;
    public bool Dragging => dragging;

    public bool Spinning(bool onStage) => onStage ? spinning : volume != null && volume.turntable;

    /// <summary>
    /// Do what <paramref name="r"/> asks, on the stage or the desk. Returns what happened in a few words, or null with
    /// <paramref name="error"/> set. Never throws and never waits: the turn eases in over the next frames.
    /// </summary>
    public string Apply(CadTurnRequest r, bool onStage, out string error)
    {
        error = null;
        if (r == null) { error = "Nothing to do: pass degrees, face, spin or reset."; return null; }
        string axis = (r.axis ?? "up").Trim().ToLowerInvariant();
        bool side;
        switch (axis)
        {
            case "": case "up": case "vertical": case "yaw": case "y": side = false; break;
            case "side": case "tilt": case "pitch": case "x": case "horizontal": side = true; break;
            default: error = $"No axis '{r.axis}': use up (turn about its vertical) or side (tip it)."; return null;
        }
        if (!onStage && volume == null) { error = "There's no model on screen to turn."; return null; }

        if (r.reset == true)
        {
            SetSpin(false, onStage);
            if (onStage) { yawTo = 0f; tiltTo = 0f; }
            else DeskTo(0f, 0f);
            return "back as it first stood";
        }
        string face = string.IsNullOrWhiteSpace(r.face) ? null : r.face.Trim().ToLowerInvariant();
        if (r.spin.HasValue && face == null && !r.degrees.HasValue)
        {
            SetSpin(r.spin.Value, onStage);
            return r.spin.Value ? "spinning slowly" : "stopped turning";
        }
        if (face != null)
        {
            if (!FaceYaw(face, onStage, out float target))
            {
                error = $"No side '{r.face}': use front, back, left or right.";
                return null;
            }
            SetSpin(false, onStage);
            if (onStage) yawTo += Mathf.DeltaAngle(yawTo, target);
            else DeskTo(DeskYaw + Mathf.DeltaAngle(DeskYaw, target), DeskTilt);
            return "turned so its " + face + " faces you";
        }
        float degrees = r.degrees ?? 90f;
        if (float.IsNaN(degrees) || float.IsInfinity(degrees)) { error = "degrees must be a number."; return null; }
        if (r.spin != true) SetSpin(false, onStage);  // an explicit turn stops a spin first
        if (side)
        {
            if (onStage && upright) { error = "At life size it stands on the floor, so it only turns about its vertical; say 'fit to stage' to tip it."; return null; }
            if (onStage) tiltTo = Mathf.Clamp(tiltTo + degrees, -MaxTilt, MaxTilt);
            else DeskTo(DeskYaw, Mathf.Clamp(DeskTilt + degrees, -MaxTilt, MaxTilt));
            return "tipped " + Mathf.Round(degrees) + " degrees";
        }
        if (onStage) yawTo += degrees;
        else DeskTo(DeskYaw + degrees, DeskTilt);
        if (r.spin == true) SetSpin(true, onStage);
        return "turned " + Mathf.Round(degrees) + " degrees";
    }

    // The user's yaw that brings a side to face them. Stage: the model faces you at a total of 180 (CadViewer.PlaceOnStage:
    // the part's front is its -Z there and the stage's +Z points away), so front is minus the stage's own offset. Desk: the
    // volume's yaw on its own (front 180, as on the stage). Left: the model's own left toward you (a quarter turn clockwise
    // seen from above).
    bool FaceYaw(string face, bool onStage, out float yawDegrees)
    {
        float front = onStage ? -faceOffset : 180f;
        switch (face)
        {
            case "front": case "me": case "you": yawDegrees = front; return true;
            case "back": case "behind": case "rear": yawDegrees = front + 180f; return true;
            case "left": yawDegrees = front + 90f; return true;
            case "right": yawDegrees = front - 90f; return true;
        }
        yawDegrees = 0f;
        return false;
    }

    void SetSpin(bool on, bool onStage)
    {
        if (onStage)
        {
            spinning = on;
            return;
        }
        if (volume == null) return;
        if (on)
        {
            deskMoving = false;
            volume.turntableDegreesPerSecond = SpinDegreesPerSecond;
        }
        volume.turntable = on;
    }

    void DeskTo(float yawDegrees, float tiltDegrees)
    {
        if (volume == null) return;
        volume.turntable = false;
        if (!deskMoving)
        {
            dYaw = volume.ViewYaw;
            dTilt = volume.ViewTilt;
            dYawSpeed = dTiltSpeed = 0f;
            setYaw = dYaw;
            setTilt = dTilt;
        }
        dYawTo = yawDegrees;
        dTiltTo = tiltDegrees;
        deskMoving = true;
    }

    /// <summary>Hands (<see cref="CadTurnHandle"/>): the stage turn follows the hand directly while it holds on.</summary>
    public void BeginDrag()
    {
        dragging = true;
        spinning = false;
    }

    public void Drag(float stageYaw) => yawTo = stageYaw;

    public void EndDrag() => dragging = false;

    /// <summary>A different design: it stands as the stage poses it again, not spinning (the desk keeps its own view).</summary>
    public void ResetStage()
    {
        yaw = yawTo = tilt = tiltTo = yawSpeed = tiltSpeed = 0f;
        spinning = dragging = false;
        posed = false;
        Pose();
    }

    /// <summary>Every frame (CadViewer.Update). <paramref name="onStage"/>: the model is up on the stage.</summary>
    public void Tick(float dt, bool onStage)
    {
        if (spinning && onStage && !dragging) yawTo += SpinDegreesPerSecond * dt;
        if (dragging || dt <= 0f)
        {
            yaw = yawTo;
            tilt = tiltTo;
        }
        else
        {
            yaw = Mathf.SmoothDamp(yaw, yawTo, ref yawSpeed, SmoothSeconds, Mathf.Infinity, dt);
            tilt = Mathf.SmoothDamp(tilt, tiltTo, ref tiltSpeed, SmoothSeconds, Mathf.Infinity, dt);
            if (Mathf.Abs(yawTo - yaw) < 0.01f) { yaw = yawTo; yawSpeed = 0f; }
            if (Mathf.Abs(tiltTo - tilt) < 0.01f) { tilt = tiltTo; tiltSpeed = 0f; }
        }
        if (Mathf.Abs(yawTo) > 720f)  // keep the numbers small: both by the same whole turns
        {
            float turns = Mathf.Floor(yawTo / 360f) * 360f;
            yawTo -= turns;
            yaw -= turns;
        }
        Pose();
        TickDesk(dt);
    }

    void Pose()
    {
        if (pivot == null || (posed && appliedYaw == yaw && appliedTilt == tilt)) return;
        Quaternion q;
        if (upright) q = Quaternion.Euler(0f, yaw, 0f);
        else
        {
            Quaternion t = Quaternion.Euler(baseTilt, 0f, 0f);
            q = Quaternion.Euler(tilt, 0f, 0f) * t * Quaternion.Euler(0f, yaw, 0f) * Quaternion.Inverse(t);
        }
        appliedYaw = yaw;
        appliedTilt = tilt;
        posed = true;
        pivot.localRotation = q;
        pivot.localPosition = Vector3.zero;
    }

    void TickDesk(float dt)
    {
        if (!deskMoving || volume == null) return;
        // Someone else turned it meanwhile (a grab of the volume, its turntable): they win, from where they left it.
        if (volume.turntable || Mathf.Abs(volume.ViewYaw - setYaw) > 0.01f || Mathf.Abs(volume.ViewTilt - setTilt) > 0.01f)
        {
            deskMoving = false;
            return;
        }
        if (dt > 0f)
        {
            dYaw = Mathf.SmoothDamp(dYaw, dYawTo, ref dYawSpeed, SmoothSeconds, Mathf.Infinity, dt);
            dTilt = Mathf.SmoothDamp(dTilt, dTiltTo, ref dTiltSpeed, SmoothSeconds, Mathf.Infinity, dt);
        }
        bool done = Mathf.Abs(dYawTo - dYaw) < DeskDone && Mathf.Abs(dTiltTo - dTilt) < DeskDone;
        if (done)
        {
            dYaw = Mathf.Repeat(dYawTo, 360f);
            dTilt = dTiltTo;
            deskMoving = false;
        }
        volume.SetView(dYaw, dTilt);
        setYaw = volume.ViewYaw;
        setTilt = volume.ViewTilt;
    }
}
