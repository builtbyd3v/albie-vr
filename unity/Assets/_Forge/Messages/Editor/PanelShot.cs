using System.IO;
using UnityEngine;

/// <summary>
/// Test helper: render one ForgePanel (by panel id) to a PNG with a temporary camera, since the Game view is black in
/// XR and the simulator window can't be captured. Call from eval in Play: <c>PanelShot.Save("messages", "Logs/x.png")</c>.
/// </summary>
public static class PanelShot
{
    public static string Save(string panelId, string path, int width = 1600)
    {
        ForgePanel panel = null;
        foreach (var p in Object.FindObjectsByType<ForgePanel>(FindObjectsSortMode.None))
            if (p.panelId == panelId && p.isActiveAndEnabled) panel = p;
        if (panel == null) return $"no active panel {panelId}";
        return SaveRect((RectTransform)panel.transform, path, width);
    }

    public static string SaveRect(RectTransform rect, string path, int width = 1600)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);  // bl, tl, tr, br
        Vector3 center = (corners[0] + corners[2]) * 0.5f;
        float w = Vector3.Distance(corners[0], corners[3]), h = Vector3.Distance(corners[0], corners[1]);
        Vector3 normal = rect.forward;  // the canvas faces -forward toward the user
        var go = new GameObject("PanelShotCamera");
        var cam = go.AddComponent<Camera>();
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.orthographic = true;
        cam.orthographicSize = h * 0.5f * 1.04f;
        cam.aspect = w / h;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.32f, 0.33f, 0.35f);  // a mid grey room behind the translucent glass
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 2f;
        go.transform.SetPositionAndRotation(center - normal * 0.5f, Quaternion.LookRotation(normal, rect.up));
        int height = Mathf.Max(1, Mathf.RoundToInt(width * h / w));
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        cam.targetTexture = rt;
        cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);
        Object.DestroyImmediate(go);
        return $"saved {path} {width}x{height}";
    }
}
