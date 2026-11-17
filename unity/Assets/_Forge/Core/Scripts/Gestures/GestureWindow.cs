using UnityEngine;

/// <summary>
/// A window's gesture state, added to a <see cref="ForgePanel"/> the first time a gesture touches it. Flick toward you:
/// it comes to arm's length, and remembers where it was. Flick away: a window that came to you goes back; otherwise it
/// minimizes, by its own "−" if it has one (the notes), else into a <see cref="PanelOrb"/> where it was. Tap the orb to
/// open it again, and the orb goes with the window if the workspace folds it away.
/// </summary>
[RequireComponent(typeof(ForgePanel))]
public class GestureWindow : MonoBehaviour
{
    const float MoveSeconds = 0.45f, ShrinkSeconds = 0.3f, GrowSeconds = 0.4f;
    const float NearMin = 0.6f, NearMax = 0.75f;   // m from the eyes: close, with room for a hand in front of it
    const float NearHalfAngle = 40f;               // how wide it looks close up (deg either side)
    const float NearDrop = 0.06f;                  // m below the eyes
    const float MovedOff = 0.06f;                  // m: moved this far since it came to you, so it stays where it is now
    const float OrbScale = 0.06f;

    ForgePanel panel;
    PanelManipulator handles;
    CanvasGroup group;
    PanelOrb orb;
    Vector3 restScale;
    bool near;
    Vector3 homePosition, nearPosition;
    Quaternion homeRotation;
    PanelFacing homeFacing;
    bool homePlaced;

    public bool IsNear => near;
    public bool IsMinimized => orb != null;

    /// <summary>Set for a window the OS tracks: its dot stays when the user leaves the desk (such a window doesn't fold either).</summary>
    public bool StaysAway { get; set; }

    public static GestureWindow Of(ForgePanel panel)
    {
        if (panel == null) return null;
        var window = panel.GetComponent<GestureWindow>();
        return window != null ? window : panel.gameObject.AddComponent<GestureWindow>();
    }

    void Awake()
    {
        panel = GetComponent<ForgePanel>();
        handles = GetComponent<PanelManipulator>();
        group = GetComponent<CanvasGroup>();
        if (group == null) group = gameObject.AddComponent<CanvasGroup>();
    }

    void OnEnable()
    {
        // The workspace showed it again while it was minimized: the dot has nothing left to do.
        if (orb != null && !restoring)
        {
            DropOrb();
            if (group != null) group.blocksRaycasts = true;
        }
    }

    void OnDestroy()
    {
        WatchDesk(false);
        DropOrb();
    }

    // Standing up hides the workspace's own dots, and this one would be left floating in the room. The workspace
    // decides when the panel comes back.
    void WatchDesk(bool watch)
    {
        var presence = Workspace.Current != null ? Workspace.Current.presence : null;
        if (presence == null) return;
        presence.stateChanged.RemoveListener(OnDeskState);
        if (watch) presence.stateChanged.AddListener(OnDeskState);
    }

    void OnDeskState(DeskState state)
    {
        if (state != DeskState.Away) return;
        // The OS keeps its windows, and a mode it keeps open, where they are: their dots stay too.
        var workspace = Workspace.Current;
        if (StaysAway || (workspace != null && workspace.KeepOpenAway)) return;
        WatchDesk(false);
        DropOrb();
        if (group != null) group.blocksRaycasts = true;
    }

    /// <summary>Where it was before it came to you, if it's still close (the workspace remembers that, not the close-up).</summary>
    public bool TryGetHome(out Vector3 localPosition, out Quaternion localRotation)
    {
        localPosition = homePosition;
        localRotation = homeRotation;
        return near && Vector3.Distance(transform.localPosition, nearPosition) < MovedOff;
    }

    bool Blocked => handles != null && handles.Busy;

    Vector3 Center => transform.TransformPoint(((RectTransform)transform).rect.center);

    /// <summary>Flicked toward you. Returns what to say at the hand.</summary>
    public string ToggleNear(MonoBehaviour runner, Transform head)
    {
        if (Blocked || head == null) return "Window is moving";
        if (near && Vector3.Distance(transform.localPosition, nearPosition) < MovedOff) return "Already here";
        // Already this close: it would only shuffle, and could never go back to where it was.
        if (!near && Vector3.Distance(Center, head.position) < NearMin + 0.02f) return "Already here";

        homePosition = transform.localPosition;
        homeRotation = transform.localRotation;
        homePlaced = handles != null && handles.UserPlaced;
        homeFacing = handles != null ? handles.Facing : PanelFacing.FaceMe;

        float width = panel.SizeMeters.x;
        float distance = Mathf.Clamp(width * 0.5f / Mathf.Tan(NearHalfAngle * Mathf.Deg2Rad), NearMin, NearMax);
        Vector3 center = head.position + head.forward * distance + Vector3.down * NearDrop;
        Quaternion rotation = Quaternion.LookRotation(center - head.position, Vector3.up);
        Vector3 pivotFromCenter = rotation * Quaternion.Inverse(transform.rotation) * (transform.position - Center);
        Glide(runner, center + pivotFromCenter, rotation, () =>
        {
            near = true;
            nearPosition = transform.localPosition;
        });
        return null;
    }

    /// <summary>Flicked away. Returns what to say at the hand.</summary>
    public string Send(MonoBehaviour runner, Transform head)
    {
        if (Blocked) return "Window is moving";
        if (near && Vector3.Distance(transform.localPosition, nearPosition) < MovedOff)
        {
            near = false;
            Transform parent = transform.parent;
            Vector3 position = parent != null ? parent.TransformPoint(homePosition) : homePosition;
            Quaternion rotation = parent != null ? parent.rotation * homeRotation : homeRotation;
            Glide(runner, position, rotation, () =>
            {
                if (handles != null) handles.SetPlacement(homeFacing, homePlaced);
            });
            return "Back it goes";
        }
        near = false;
        if (handles != null && handles.PressMinimize()) return null;
        return Minimize(runner, head) ? null : "Nowhere to minimize to";
    }

    void Glide(MonoBehaviour runner, Vector3 toPosition, Quaternion toRotation, System.Action landed)
    {
        if (handles != null)
        {
            handles.Cancel();
            handles.Busy = true;
        }
        Vector3 fromPosition = transform.position;
        Quaternion fromRotation = transform.rotation;
        runner.StartCoroutine(ForgeTween.Run(MoveSeconds, 0f, k =>
        {
            if (this == null) return;
            float e = ForgeTween.SpringDamped(k, 0.8f, 1.4f);
            transform.SetPositionAndRotation(Vector3.LerpUnclamped(fromPosition, toPosition, e), Quaternion.SlerpUnclamped(fromRotation, toRotation, e));
        }, () =>
        {
            if (this == null) return;
            if (handles != null) handles.Busy = false;
            landed?.Invoke();
        }));
    }

    // ---------------------------------------------------------------- minimized

    bool restoring;

    // Shrinks into its centre with a dot popping out there. At the end the panel is hidden back at its own place and
    // size, so a workspace that folds or re-lays it out while minimized finds nothing odd.
    bool Minimize(MonoBehaviour runner, Transform head)
    {
        PanelOrb prefab = OrbPrefab();
        if (prefab == null || head == null) return false;

        Vector3 center = Center;
        orb = Instantiate(prefab, transform.parent);
        orb.name = name + " orb";
        orb.Init(head);
        orb.SetGlyph();
        Vector3 orbRest = orb.transform.localScale;
        orb.transform.SetPositionAndRotation(center, Quaternion.LookRotation(center - head.position, Vector3.up));
        orb.transform.localScale = orbRest * 0.4f;
        orb.Tapped += () => Restore(runner);
        WatchDesk(true);

        if (handles != null)
        {
            handles.Cancel();
            handles.Busy = true;
        }
        restScale = transform.localScale;
        Vector3 startPosition = transform.position;
        Vector3 toPivot = startPosition - center;
        float startAlpha = group.alpha;
        group.blocksRaycasts = false;
        PanelOrb thisOrb = orb;
        runner.StartCoroutine(ForgeTween.Run(ShrinkSeconds, 0f, k =>
        {
            if (this == null) return;
            float f = ForgeTween.EaseInOutCubic(k);
            float s = Mathf.Lerp(1f, OrbScale, f);
            transform.localScale = restScale * s;
            transform.position = center + toPivot * s;
            group.alpha = startAlpha * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 1f, k)));
            if (thisOrb != null) thisOrb.transform.localScale = orbRest * Mathf.Lerp(0.4f, 1f, ForgeTween.SpringDamped(k, 0.6f, 1.4f));
        }, () =>
        {
            if (this == null) return;
            transform.localScale = restScale;
            transform.position = startPosition;
            group.alpha = startAlpha;
            if (handles != null) handles.Busy = false;
            gameObject.SetActive(false);
        }));
        return true;
    }

    /// <summary>Open a minimized window again, from the dot.</summary>
    public void Restore(MonoBehaviour runner)
    {
        if (orb == null || restoring) return;
        restoring = true;
        Vector3 from = orb.transform.position;
        if (handles != null) handles.Busy = true;
        gameObject.SetActive(true);
        restoring = false;
        Vector3 to = transform.position;
        Vector3 toPivot = to - Center;
        Vector3 scale = transform.localScale;
        float alpha = group.alpha;
        group.blocksRaycasts = true;
        WatchDesk(false);
        DropOrb();
        runner.StartCoroutine(ForgeTween.Run(GrowSeconds, 0f, k =>
        {
            if (this == null) return;
            float g = ForgeTween.SpringDamped(k, 0.74f, 1.35f);
            float s = Mathf.LerpUnclamped(OrbScale, 1f, g);
            transform.localScale = scale * s;
            transform.position = Vector3.LerpUnclamped(from, to - toPivot, g) + toPivot * s;
            group.alpha = alpha * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.4f, k));
        }, () =>
        {
            if (this == null) return;
            transform.localScale = scale;
            transform.position = to;
            group.alpha = alpha;
            if (handles != null) handles.Busy = false;
        }));
    }

    void DropOrb()
    {
        if (orb != null) Destroy(orb.gameObject);
        orb = null;
    }

    PanelOrb OrbPrefab()
    {
        var engine = GestureEngine.Current;
        if (engine != null && engine.orbPrefab != null) return engine.orbPrefab;
        var desk = FindAnyObjectByType<DeskExtras>();
        return desk != null ? desk.orbPrefab : null;
    }
}
