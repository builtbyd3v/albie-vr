using UnityEngine;

/// <summary>
/// Keeps a <see cref="ForgeLook.Ring"/> band the size of its holder divided by the band's scale, so a ring drawn at a
/// larger scale (a wider outline than ForgeSurface's one unit) still lines up with the holder. Runs only when the holder's
/// size changes.
/// </summary>
[ExecuteAlways]
public sealed class ForgeRingFit : MonoBehaviour
{
    public RectTransform band;
    public float width = 2f;

    public void Fit()
    {
        if (band == null) return;
        var holder = (RectTransform)transform;
        band.anchorMin = band.anchorMax = band.pivot = new Vector2(0.5f, 0.5f);
        band.anchoredPosition = Vector2.zero;
        band.sizeDelta = holder.rect.size / Mathf.Max(0.5f, width);
    }

    void OnEnable() => Fit();
    void OnRectTransformDimensionsChange() => Fit();
}
