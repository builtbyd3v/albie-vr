using System.IO;
using Forge.Artifacts;
using UnityEngine;

/// <summary>
/// Test helper: renders the open artifact (its volume, models and panels) to a PNG with a temporary camera, because the
/// Game view is black in XR. Call it in Play mode, for example from <c>unity command eval</c>:
/// <c>ArtifactShot.Take("/tmp/three_body.png")</c>. <paramref name="yaw"/> turns the view about the artifact.
/// </summary>
public static class ArtifactShot
{
    public static string Take(string path, float yaw = 0f, float pitch = 12f, int width = 1600, int height = 1000, float zoom = 1f)
    {
        var host = ArtifactHost.Current;
        if (host == null) return "no ArtifactHost";
        var renderers = host.GetComponentsInChildren<Renderer>(false);
        var canvases = host.GetComponentsInChildren<RectTransform>(false);
        if (renderers.Length == 0 && canvases.Length == 0) return "nothing open";
        var bounds = new Bounds(host.transform.position, Vector3.zero);
        bool any = false;
        foreach (var r in renderers)
        {
            if (r is ParticleSystemRenderer) continue;
            if (!any) bounds = r.bounds; else bounds.Encapsulate(r.bounds);
            any = true;
        }
        var corners = new Vector3[4];
        foreach (var c in host.GetComponentsInChildren<Canvas>(false))
        {
            ((RectTransform)c.transform).GetWorldCorners(corners);
            foreach (var p in corners)
            {
                if (!any) bounds = new Bounds(p, Vector3.zero); else bounds.Encapsulate(p);
                any = true;
            }
        }

        var main = Camera.main;
        Vector3 forward = main != null ? main.transform.forward : Vector3.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
        var look = Quaternion.Euler(pitch, Quaternion.LookRotation(forward).eulerAngles.y + yaw, 0f);
        const float fov = 40f;
        float aspect = (float)width / height;
        float radius = bounds.extents.magnitude;
        float fit = radius / Mathf.Sin(Mathf.Deg2Rad * fov * 0.5f * Mathf.Min(1f, aspect));
        float distance = fit / Mathf.Max(0.1f, zoom);

        var go = new GameObject("ArtifactShotCam");
        try
        {
            var cam = go.AddComponent<Camera>();
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.fieldOfView = fov;
            cam.aspect = aspect;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.19f, 1f);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 50f;
            cam.allowHDR = true;
            cam.allowMSAA = true;
            go.transform.rotation = look;
            go.transform.position = bounds.center - look * Vector3.forward * distance;
            var rt = new RenderTexture(width, height, 24) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
        return $"{path} ({host.OpenTitle}, {renderers.Length} renderers, bounds {bounds.size})";
    }
}
