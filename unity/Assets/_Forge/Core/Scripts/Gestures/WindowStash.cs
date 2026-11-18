using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every window at once, for the hand that opens and closes all of them: <see cref="HideAll"/> fades out each open
/// <see cref="ForgePanel"/> and remembers it, <see cref="ShowAll"/> brings back the ones still there. A window the
/// workspace folds away meanwhile is gone for good, and one it opens meanwhile is simply shown.
/// Things that are not panels (an artifact's 3D model, its room course and the truck or drone on it) take part through
/// an <see cref="IStashable"/> their owner registers (<see cref="Register"/>).
/// </summary>
public static class WindowStash
{
    /// <summary>Something besides panels that hides with every window and comes back with them.</summary>
    public interface IStashable
    {
        /// <summary>Hide what is showing now; returns how many things it hid (0: nothing to hide).</summary>
        int Hide(MonoBehaviour runner);
        /// <summary>Bring back what <see cref="Hide"/> hid and is still there; returns how many came back.</summary>
        int Show(MonoBehaviour runner);
        /// <summary>How many things are hidden and could come back.</summary>
        int HiddenCount { get; }
    }

    static readonly List<IStashable> others = new List<IStashable>();

    /// <summary>Adds an <see cref="IStashable"/> (once).</summary>
    public static void Register(IStashable stashable)
    {
        if (stashable != null && !others.Contains(stashable)) others.Add(stashable);
    }

    /// <summary>Takes an <see cref="IStashable"/> out again (a check's stand-in).</summary>
    public static void Unregister(IStashable stashable) => others.Remove(stashable);

    const float HideSeconds = 0.18f, ShowSeconds = 0.28f;
    const float HiddenScale = 0.92f;

    static readonly List<ForgePanel> hidden = new List<ForgePanel>();
    static readonly List<ForgePanel> scratch = new List<ForgePanel>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => hidden.Clear();

    /// <summary>Windows hidden by <see cref="HideAll"/> that can still come back.</summary>
    public static int HiddenCount
    {
        get
        {
            hidden.RemoveAll(p => p == null);
            int count = hidden.Count;
            for (int i = 0; i < others.Count; i++) count += others[i].HiddenCount;
            return count;
        }
    }

    /// <summary>Hide every open window. Returns what to say at the hand, or null for the gesture's own title.</summary>
    public static string HideAll(MonoBehaviour runner)
    {
        scratch.Clear();
        scratch.AddRange(ForgePanel.All);  // the list changes as they hide
        int count = 0;
        foreach (ForgePanel panel in scratch)
        {
            if (panel == null || !panel.gameObject.activeInHierarchy || hidden.Contains(panel)) continue;
            hidden.Add(panel);
            Fade(runner, panel, false);
            count++;
        }
        scratch.Clear();
        for (int i = 0; i < others.Count; i++)
        {
            try { count += others[i].Hide(runner); }
            catch (System.Exception e) { Debug.LogException(e); }
        }
        if (count > 0) return null;
        return HiddenCount > 0 ? "Already hidden" : "No windows open";
    }

    /// <summary>Show the windows <see cref="HideAll"/> hid. Returns what to say at the hand, or null.</summary>
    public static string ShowAll(MonoBehaviour runner)
    {
        int count = 0;
        for (int i = 0; i < hidden.Count; i++)
        {
            ForgePanel panel = hidden[i];
            if (panel == null || panel.gameObject.activeSelf) continue;
            Fade(runner, panel, true);
            count++;
        }
        hidden.Clear();
        for (int i = 0; i < others.Count; i++)
        {
            try { count += others[i].Show(runner); }
            catch (System.Exception e) { Debug.LogException(e); }
        }
        return count > 0 ? null : "Nothing hidden";
    }

    static void Fade(MonoBehaviour runner, ForgePanel panel, bool show)
    {
        var handles = panel.Handles;
        var group = panel.GetComponent<CanvasGroup>();
        if (group == null) group = panel.gameObject.AddComponent<CanvasGroup>();
        Transform t = panel.transform;
        Vector3 scale = t.localScale;
        float alpha = group.alpha;  // put back at the end, so a window that hides comes back as it was
        if (handles != null)
        {
            handles.Cancel();
            handles.Busy = true;
        }
        if (show)
        {
            panel.gameObject.SetActive(true);
            group.alpha = 0f;
            t.localScale = scale * HiddenScale;
        }
        group.blocksRaycasts = show;
        runner.StartCoroutine(ForgeTween.Run(show ? ShowSeconds : HideSeconds, 0f, k =>
        {
            if (panel == null) return;
            float f = show ? ForgeTween.SpringDamped(k, 0.8f, 1.4f) : ForgeTween.EaseInOutCubic(k);
            t.localScale = scale * (show ? Mathf.LerpUnclamped(HiddenScale, 1f, f) : Mathf.Lerp(1f, HiddenScale, f));
            group.alpha = alpha * (show ? Mathf.Clamp01(k * 2f) : 1f - f);
        }, () =>
        {
            if (panel == null) return;
            t.localScale = scale;
            group.alpha = alpha;
            group.blocksRaycasts = true;
            if (handles != null) handles.Busy = false;
            if (!show) panel.gameObject.SetActive(false);
        }));
    }
}
