using System.Collections.Generic;
using UnityEngine;

/// <summary>Something <see cref="ForgeMotion"/> ticks while it animates. Return false once it has settled.</summary>
public interface IForgeTick
{
    bool Tick(float dt);
}

/// <summary>
/// One shared runner for the design system's small control animations (hover glow, press depth, selected fades, progress
/// fills). A control registers itself when its target changes and drops out as soon as it settles, so an idle UI costs
/// nothing per frame and there is no per-frame allocation (one list, reused). Outside Play mode a registered control is
/// settled at once (the gallery and editor builders see the end state).
/// </summary>
public sealed class ForgeMotion : MonoBehaviour
{
    static ForgeMotion runner;
    static readonly List<IForgeTick> active = new List<IForgeTick>(64);
    static readonly HashSet<IForgeTick> queued = new HashSet<IForgeTick>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        runner = null;
        active.Clear();
        queued.Clear();
    }

    /// <summary>Ticks <paramref name="item"/> every frame until it returns false. Calling again while it runs does nothing.</summary>
    public static void Run(IForgeTick item)
    {
        if (item == null) return;
        if (!Application.isPlaying)
        {
            for (int i = 0; i < 64 && item.Tick(1f); i++) { }
            return;
        }
        if (!queued.Add(item)) return;
        active.Add(item);
        if (runner == null)
        {
            var go = new GameObject("ForgeMotion") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            runner = go.AddComponent<ForgeMotion>();
        }
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        for (int i = active.Count - 1; i >= 0; i--)
        {
            IForgeTick item = active[i];
            bool keep;
            try
            {
                keep = item != null && !(item is Object o && o == null) && item.Tick(dt);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                keep = false;
            }
            if (keep) continue;
            int last = active.Count - 1;
            active[i] = active[last];
            active.RemoveAt(last);
            if (item != null) queued.Remove(item);
        }
    }

    /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> so a full 0..1 change takes <paramref name="duration"/> seconds.</summary>
    public static float Approach(float current, float target, float duration, float dt) =>
        Mathf.MoveTowards(current, target, duration <= 0f ? 1f : dt / duration);

    /// <summary>Smooth in-out ease for a 0..1 value.</summary>
    public static float Ease(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }
}
