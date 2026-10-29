using System.IO;
using UnityEngine;

/// <summary>Test helper: render a panel to a PNG with a temporary camera aimed at its RectTransform (the Game view is black in XR).</summary>
public static class CodeShot
{
    public static string Panel(RectTransform rt, string path, int width = 1600)
    {
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        Vector3 center = (corners[0] + corners[2]) * 0.5f;
        float w = Vector3.Distance(corners[0], corners[3]), h = Vector3.Distance(corners[0], corners[1]);
        var go = new GameObject("CodeShotCam");
        var cam = go.AddComponent<Camera>();
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.orthographic = true;
        cam.orthographicSize = h * 0.5f * 1.04f;
        cam.aspect = w / h;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.35f, 0.38f, 0.42f, 1f);
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 2f;
        go.transform.rotation = rt.rotation;
        go.transform.position = center - rt.forward * 0.5f;
        int height = Mathf.RoundToInt(width * h / w);
        var rtex = new RenderTexture(width, height, 24);
        cam.targetTexture = rtex;
        cam.Render();
        RenderTexture.active = rtex;
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        cam.targetTexture = null;
        Object.DestroyImmediate(rtex);
        Object.DestroyImmediate(tex);
        Object.DestroyImmediate(go);
        return path;
    }
}
