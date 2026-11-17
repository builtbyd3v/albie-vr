using System;
using System.Collections.Generic;
using System.IO;
using Forge.Artifacts;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Test helper: renders the open artifact (its volume, its content root, models and panels) to a PNG with a temporary
/// camera, because the Game view is black in XR. Call it in Play mode, for example from <c>unity command eval</c>:
/// <c>ArtifactShot.Take("/tmp/three_body.png")</c>. <paramref name="yaw"/> turns the view about the artifact.
/// A room scene's content is detached from the host, so renderers come from <see cref="ArtifactHost.ContentRoot"/> too;
/// pass <paramref name="room"/> to frame what the scene put in the room (gates, scoreboard, the truck when it's the Thing) as well.
/// Nothing open gives "not shown: (why)" instead of an empty picture.
/// <para><see cref="OpenAndTake"/> opens an artifact (approving its first-open sheet against a temporary approvals file,
/// never the user's) and takes the picture once it is shown and grown in; poll <see cref="LastResult"/> for the line.
/// <see cref="TakeWhenShown"/> does the same for whatever opens next (an <c>artifact_create</c> from the voice).</para>
/// </summary>
public static class ArtifactShot
{
    const float TimeoutS = 15f;
    const float SettleS = 0.15f;  // after the reveal: one more frame of layout (panels, fitted volumes)

    /// <summary>The last <see cref="OpenAndTake"/> / <see cref="TakeWhenShown"/> line; "pending" while it waits.</summary>
    public static string LastResult { get; private set; } = "";

    static EditorApplication.CallbackFunction poll;
    static ArtifactHost watched;
    static Action<ArtifactHost> onShown;

    public static string Take(string path, float yaw = 0f, float pitch = 12f, int width = 1600, int height = 1000, float zoom = 1f, bool room = false)
    {
        var host = ArtifactHost.Current;
        if (host == null) return "no ArtifactHost";
        string why = NotShown(host);
        if (why != null) return "not shown: " + why;
        if (host.Revealing) return "not shown yet: still growing in (wait " + ForgeTheme.UnfoldTime + " s, or use TakeWhenShown)";

        var renderers = new List<Renderer>();
        var canvases = new List<Canvas>();
        var seen = new HashSet<Object>();
        Collect(host.transform, renderers, canvases, seen);
        if (host.ContentRoot != null) Collect(host.ContentRoot, renderers, canvases, seen);
        if (room)  // a floor course stands outside the host: frame what it put in the room, and the truck when it's the Thing
        {
            foreach (var spawned in host.Spawned)
                if (spawned != null && !spawned.transform.IsChildOf(host.transform)) Collect(spawned.transform, renderers, canvases, seen);
            if (host.Thing is Component thing && thing != null) Collect(thing.transform, renderers, canvases, seen);
        }
        if (renderers.Count == 0 && canvases.Count == 0) return "not shown: the open scene has no visible renderer";

        var bounds = new Bounds(host.transform.position, Vector3.zero);
        bool any = false;
        foreach (var r in renderers)
        {
            if (r is ParticleSystemRenderer) continue;
            if (!any) bounds = r.bounds; else bounds.Encapsulate(r.bounds);
            any = true;
        }
        var corners = new Vector3[4];
        foreach (var c in canvases)
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
        float radius = Mathf.Max(0.01f, bounds.extents.magnitude);
        float fit = radius / Mathf.Sin(Mathf.Deg2Rad * fov * 0.5f * Mathf.Min(1f, aspect));
        float distance = fit / Mathf.Max(0.1f, zoom);

        var go = new GameObject("ArtifactShotCam");
        try
        {
            var cam = go.AddComponent<Camera>();
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
        return $"{path} ({host.OpenTitle}, {renderers.Count} renderers, {canvases.Count} canvases, bounds {bounds.size})";
    }

    /// <summary>
    /// Opens <paramref name="id"/> on the current host and takes the picture once it is shown. A first-open permissions
    /// sheet is approved against a temporary approvals file (the user's own approvals are never touched). Returns at once;
    /// the line lands in <see cref="LastResult"/>.
    /// </summary>
    public static string OpenAndTake(string id, string path, float yaw = 0f, float pitch = 12f, float zoom = 1f)
    {
        var host = ArtifactHost.Current;
        if (!Application.isPlaying) return LastResult = "not in Play mode";
        if (host == null) return LastResult = "no ArtifactHost";
        host.Open(id);
        if (host.AwaitingApproval) ApproveForTest(host, id);
        return Wait(path, yaw, pitch, zoom, null, false, TimeoutS);
    }

    /// <summary>
    /// Waits for the next artifact to be shown (an <c>artifact_create</c> from the voice, say) and grown in, then takes
    /// the picture. <paramref name="next"/> false takes the one open now as soon as it is ready. A new refusal ends the
    /// wait with "not shown: (why)".
    /// </summary>
    public static string TakeWhenShown(string path, bool next = true, float timeoutS = 90f, float yaw = 0f, float pitch = 12f, float zoom = 1f)
    {
        if (!Application.isPlaying) return LastResult = "not in Play mode";
        var current = ArtifactHost.Current;
        if (current == null) return LastResult = "no ArtifactHost";
        return Wait(path, yaw, pitch, zoom, current.OpenId == null || next ? current.LastProblem : null, next, timeoutS);
    }

    // stale: a refusal already showing when the wait began, which isn't the answer to the open being waited for.
    // needShown: only a Shown after the wait began counts.
    static string Wait(string path, float yaw, float pitch, float zoom, string stale, bool needShown = false, float timeoutS = TimeoutS)
    {
        Cancel();
        LastResult = "pending";
        double until = EditorApplication.timeSinceStartup + timeoutS;
        double shownAt = -1;
        bool fresh = !needShown;
        watched = ArtifactHost.Current;
        onShown = _ => fresh = true;
        watched.Shown += onShown;
        poll = () =>
        {
            var host = ArtifactHost.Current;
            if (!Application.isPlaying || host == null) { Finish("no ArtifactHost (Play mode ended?)"); return; }
            if (host.AwaitingApproval) ApproveForTest(host, (string)host.OpenManifest?["id"]);
            bool shown = fresh && host.OpenId != null && !host.Revealing;
            if (shown && shownAt < 0) shownAt = EditorApplication.timeSinceStartup;
            if (shown && EditorApplication.timeSinceStartup - shownAt >= SettleS) { Finish(Take(path, yaw, pitch, 1600, 1000, zoom)); return; }
            if (host.OpenId == null && host.LastProblem != null && host.LastProblem != stale) { Finish("not shown: " + host.LastProblem); return; }
            if (EditorApplication.timeSinceStartup > until)
                Finish("not shown: timed out after " + timeoutS + " s (" + (fresh ? NotShown(host) ?? "still growing in" : "nothing new opened") + ")");
        };
        EditorApplication.update += poll;
        return LastResult;
    }

    static void Finish(string line)
    {
        Cancel();
        LastResult = line;
        Debug.Log("[ArtifactShot] " + line);
    }

    static void Cancel()
    {
        if (poll != null) EditorApplication.update -= poll;
        poll = null;
        if (watched != null && onShown != null) watched.Shown -= onShown;
        watched = null;
        onShown = null;
    }

    // A first open asks the user. A test approves it into a temporary file, reopens, and puts the user's file back.
    static void ApproveForTest(ArtifactHost host, string id)
    {
        if (string.IsNullOrEmpty(id) || host.OpenManifest == null) return;
        string before = ArtifactApprovals.FileOverride;
        string temp = Path.Combine(Path.GetTempPath(), "artifactshot_approvals.json");
        try
        {
            ArtifactApprovals.FileOverride = temp;
            ArtifactApprovals.Approve(host.OpenManifest);
            host.Close();
            host.Open(id);
        }
        finally
        {
            ArtifactApprovals.FileOverride = before;
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    static string NotShown(ArtifactHost host)
    {
        if (host.OpenId != null) return null;
        if (host.AwaitingApproval) return "waiting for the first-open approval";
        return host.LastProblem ?? "nothing open";
    }

    static void Collect(Transform root, List<Renderer> renderers, List<Canvas> canvases, HashSet<Object> seen)
    {
        foreach (var r in root.GetComponentsInChildren<Renderer>(false))
            if (seen.Add(r)) renderers.Add(r);
        foreach (var c in root.GetComponentsInChildren<Canvas>(false))
            if (seen.Add(c)) canvases.Add(c);
    }
}
