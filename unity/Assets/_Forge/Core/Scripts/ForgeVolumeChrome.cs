using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A close chip for a <see cref="ForgeVolume"/> whose owner says what closing means (<see cref="ForgeVolume.CloseHandler"/>):
/// a small dark plate standing just right of the volume's name label, holding the same close chip every frameless panel
/// gets (<see cref="PanelChrome"/>, styled by <see cref="ForgeLook.StyleChrome"/>), so it looks and works like every other
/// window's X: poke it, or point and pinch (it fires on the release even when the ray drifted). It is a tiny
/// <see cref="ForgePanel"/> made from the panel prefab the owner passes, so the Interaction SDK's ray and poke reach it
/// with nothing wired by hand. It keeps its real size however the volume is scaled, stands upright and turns to face the
/// user (yaw only); it allocates nothing per frame.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ForgeVolume))]
public sealed class ForgeVolumeChrome : MonoBehaviour
{
    /// <summary>The chip's plate (canvas units, mm). Its close chip comes out at 32 mm (PanelChrome's rule for a 48 mm plate).</summary>
    public const float PlateSize = 48f;
    const float Gap = 0.012f, Lift = 0.035f;  // m: from the label's right end, and up off the base

    ForgeVolume volume;
    ForgePanel chip;
    Vector3 restScale;
    float placedFor = -1f;

    /// <summary>The chip's panel (null until made). Its Chrome's CloseButton is the control.</summary>
    public ForgePanel Chip => chip;

    /// <summary>Adds (once) the close chip to <paramref name="volume"/>, made from <paramref name="panelPrefab"/>.</summary>
    public static ForgeVolumeChrome Of(ForgeVolume volume, ForgePanel panelPrefab)
    {
        if (volume == null) return null;
        var chrome = volume.GetComponent<ForgeVolumeChrome>();
        if (chrome == null) chrome = volume.gameObject.AddComponent<ForgeVolumeChrome>();
        chrome.Build(panelPrefab);
        return chrome;
    }

    void Build(ForgePanel prefab)
    {
        volume = GetComponent<ForgeVolume>();
        if (chip != null || prefab == null) { Refresh(); return; }
        chip = Instantiate(prefab, transform);
        chip.name = "Close chip";
        chip.panelId = "volume-close";
        chip.Title = "";
        chip.Minimisable = false;
        chip.SizeMeters = Vector2.one * (PlateSize * ForgePanel.MetersPerUnit);
        var titleBar = chip.transform.Find("TitleBar");
        if (titleBar != null) titleBar.gameObject.SetActive(false);  // no title bar: PanelChrome puts its close chip in
        if (chip.Content != null)
        {
            var placeholder = chip.Content.Find("Placeholder");
            if (placeholder != null) placeholder.gameObject.SetActive(false);
        }
        if (chip.GetComponent<GraphicRaycaster>() == null) chip.gameObject.AddComponent<GraphicRaycaster>();  // the mouse (DesktopRig)
        chip.CloseHandler = () => { if (volume != null) volume.RequestClose(); };
        restScale = chip.transform.localScale;
        PanelChrome.Of(chip);
        Refresh();
    }

    /// <summary>Shown while the volume can close; placed beside the label again.</summary>
    public void Refresh()
    {
        if (chip == null || volume == null) return;
        bool show = volume.Closable;
        if (chip.gameObject.activeSelf != show) chip.gameObject.SetActive(show);
        placedFor = -1f;
        if (show) Place();
    }

    /// <summary>Hides the chip (a volume on its way out) or shows it again.</summary>
    public void SetVisible(bool visible)
    {
        if (chip == null) return;
        bool show = visible && volume != null && volume.Closable;
        if (chip.gameObject.activeSelf != show) chip.gameObject.SetActive(show);
    }

    void LateUpdate()
    {
        if (chip == null || !chip.gameObject.activeSelf) return;
        float scale = Mathf.Abs(transform.lossyScale.x);
        if (Mathf.Abs(scale - placedFor) > 1e-4f) Place();
        Face();
    }

    // Beside the label's right end, standing up off the base, at its real size whatever the volume's scale.
    void Place()
    {
        float scale = Mathf.Abs(transform.lossyScale.x);
        placedFor = scale;
        if (scale < 1e-6f) return;
        float half = PlateSize * 0.5f * ForgePanel.MetersPerUnit;  // m, in the world
        Vector3 at;
        if (volume.LabelExtent(out Vector3 centre, out float halfWidth))
            at = centre + Vector3.right * (halfWidth + (Gap + half) / scale) + Vector3.up * (Lift / scale);
        else
            at = new Vector3(volume.size.x * 0.5f, Lift / scale, -volume.size.z * 0.5f);
        chip.transform.localPosition = at;
        chip.transform.localScale = restScale / scale;
    }

    // Upright, turned about the vertical to face the eyes (a canvas shows its face along -Z: forward points away from them).
    void Face()
    {
        var cam = Camera.main;
        if (cam == null) return;
        Vector3 away = chip.transform.position - cam.transform.position;
        away.y = 0f;
        if (away.sqrMagnitude < 1e-6f) return;
        chip.transform.rotation = Quaternion.LookRotation(away, Vector3.up);
    }
}
