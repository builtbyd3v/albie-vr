using UnityEngine;

/// <summary>
/// A progress rail's fill (<see cref="ForgeLook"/> / OSUi.ProgressRail): set <see cref="Value"/> (0..1) and the fill eases
/// there over <see cref="ForgeTheme.MotionProgress"/> through <see cref="ForgeMotion"/> (nothing runs once it arrives).
/// The fill is a RectTransform anchored to the left of the rail; its right anchor is moved.
/// </summary>
public sealed class ForgeProgress : MonoBehaviour, IForgeTick
{
    public RectTransform fill;
    float shown, target;

    /// <summary>The progress shown, 0..1.</summary>
    public float Value
    {
        get => target;
        set
        {
            target = Mathf.Clamp01(value);
            ForgeMotion.Run(this);
        }
    }

    /// <summary>Jumps to a value without easing (first build).</summary>
    public void Snap(float value)
    {
        target = shown = Mathf.Clamp01(value);
        Apply();
    }

    public bool Tick(float dt)
    {
        if (this == null) return false;
        shown = ForgeMotion.Approach(shown, target, ForgeTheme.MotionProgress, dt);
        Apply();
        return shown != target;
    }

    void Apply()
    {
        if (fill == null) return;
        fill.gameObject.SetActive(shown > 0.001f);
        fill.anchorMax = new Vector2(shown, 1f);
    }
}
