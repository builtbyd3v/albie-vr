using Forge.Pet;
using TMPro;
using UnityEngine;

/// <summary>
/// What a hand sees of the gestures: a thin beam from a pointing finger that lands on the window it's aimed at (the
/// window's rim lights up, as under any pointer), and a short line of text floating above the hand when something fires.
/// </summary>
public class GestureFeedback : MonoBehaviour
{
    const float BeamFree = 1.2f;       // m: how long the beam is with nothing to land on
    const float SayHold = 1.1f, SayFade = 0.4f;
    const float LabelAbove = 0.085f;   // m above the palm

    static readonly Color BeamNear = new Color(1f, 1f, 1f, 0.75f);
    static readonly Color BeamFar = new Color(1f, 1f, 1f, 0.05f);
    static readonly Color BeamNearOff = new Color(1f, 1f, 1f, 0.3f);
    static readonly Color BeamFarOff = new Color(1f, 1f, 1f, 0f);

    GestureEngine engine;
    readonly LineRenderer[] beams = new LineRenderer[2];
    readonly TextMeshPro[] labels = new TextMeshPro[2];
    readonly float[] labelUntil = new float[2];
    readonly ForgePanel[] aimed = new ForgePanel[2];
    readonly Vector3[] beamPoints = new Vector3[2];

    public void Init(GestureEngine owner)
    {
        engine = owner;
        var material = new Material(Shader.Find("Sprites/Default"));
        for (int i = 0; i < 2; i++)
        {
            var beam = new GameObject("GestureBeam").AddComponent<LineRenderer>();
            beam.transform.SetParent(transform, false);
            beam.sharedMaterial = material;
            beam.positionCount = 2;
            beam.startWidth = 0.0035f;
            beam.endWidth = 0.002f;
            beam.useWorldSpace = true;
            beam.numCapVertices = 2;
            beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            beam.receiveShadows = false;
            beam.enabled = false;
            beams[i] = beam;

            var label = new GameObject("GestureLabel").AddComponent<TextMeshPro>();
            label.transform.SetParent(transform, false);
            label.rectTransform.sizeDelta = new Vector2(30f, 6f);
            label.transform.localScale = Vector3.one * 0.01f;
            if (ForgeTheme.Font != null) label.font = ForgeTheme.Font;
            label.fontSize = 22f;
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.color = ForgeTheme.PanelText;
            label.gameObject.SetActive(false);
            labels[i] = label;
        }
    }

    /// <summary>A line above the hand for a moment.</summary>
    public void Say(GestureHand hand, string text)
    {
        int i = hand.sample.isLeft ? 0 : 1;
        Set(i, text);
        labelUntil[i] = Time.time + SayHold + SayFade;
    }

    /// <summary>A line above the hand for just this frame (keep calling it to keep it up).</summary>
    public void Show(GestureHand hand, string text)
    {
        int i = hand.sample.isLeft ? 0 : 1;
        Set(i, text);
        labelUntil[i] = Time.time + SayFade + 0.05f;
    }

    void Set(int i, string text)
    {
        if (labels[i].text != text) labels[i].text = text;
        labels[i].gameObject.SetActive(true);
    }

    void LateUpdate()
    {
        Transform head = PetUser.Head;
        if (engine == null || head == null) return;
        for (int i = 0; i < 2; i++)
        {
            GestureHand hand = engine.Hands[i];
            UpdateBeam(i, hand, head);
            UpdateLabel(i, hand, head);
        }
    }

    void UpdateBeam(int i, GestureHand hand, Transform head)
    {
        bool aiming = hand.Usable && GestureLibrary.IsAiming(hand.values);
        ForgePanel target = aiming ? hand.Target : null;
        if (aimed[i] != target)
        {
            if (aimed[i] != null && aimed[i].Handles != null) aimed[i].Handles.Aimed = false;
            aimed[i] = target;
        }
        if (target != null && target.Handles != null) target.Handles.Aimed = true;

        LineRenderer beam = beams[i];
        if (beam.enabled != aiming) beam.enabled = aiming;
        if (!aiming) return;

        Ray ray = hand.AimRay(head.position);
        float length = BeamFree;
        if (target != null && new Plane(target.transform.forward, target.transform.position).Raycast(ray, out float hit)) length = hit;
        beamPoints[0] = ray.origin;
        beamPoints[1] = ray.GetPoint(length);
        beam.SetPositions(beamPoints);
        beam.startColor = target != null ? BeamNear : BeamNearOff;
        beam.endColor = target != null ? BeamFar : BeamFarOff;
    }

    void UpdateLabel(int i, GestureHand hand, Transform head)
    {
        TextMeshPro label = labels[i];
        if (!label.gameObject.activeSelf) return;
        float left = labelUntil[i] - Time.time;
        if (left <= 0f)
        {
            label.gameObject.SetActive(false);
            return;
        }
        Color c = label.color;
        c.a = Mathf.Clamp01(left / SayFade) * 0.95f;
        label.color = c;
        if (!hand.Usable) return;  // stays where the hand last was
        Vector3 at = hand.Palm + Vector3.up * LabelAbove;
        label.transform.SetPositionAndRotation(at, Quaternion.LookRotation(at - head.position, Vector3.up));
    }
}
