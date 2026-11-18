using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Turning the expanded model by hand: the ring of light under it on the stage is a lazy Susan. Grab the ring (a controller
/// or pinching-hand ray, or the mouse) and swing the pointer round it: the model turns with it about its own vertical
/// (<see cref="CadTurn.Drag"/>), exploded or not, and stays where it is let go. Parts are still taken off by grabbing the
/// parts themselves (<see cref="CadTakeApart"/>); the ring sits under them, so pointing at a part takes the part.
/// Input as for <see cref="CadExpandButton"/>: Interaction SDK rays through a RayInteractable on its collider; a mouse
/// through <see cref="CadPartRaycaster"/>. CadViewer makes it and places it every frame with the ring
/// (<see cref="Place"/>); it is only there while the model is settled on the stage.
/// </summary>
public class CadTurnHandle : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler
{
    const int IgnoreRaycastLayer = 2;
    const float Thickness = 0.03f;
    static readonly List<CadTurnHandle> active = new List<CadTurnHandle>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => active.Clear();

    CadTurn turn;
    BoxCollider box;
    RayInteractable ray;
    CadPointer pointer;    // while held
    float grabAngle, grabYaw, lastAngle, swung;
    int mouseHovers;

    public static int Count => active.Count;
    public bool Held => pointer != null;
    public bool Hovered => mouseHovers > 0 || (ray != null && (ray.State == InteractableState.Hover || ray.State == InteractableState.Select));

    /// <summary>Nearest turn ring the ray hits within <paramref name="maxDistance"/>.</summary>
    public static bool Raycast(Ray r, float maxDistance, out RaycastHit hit, out CadTurnHandle handle)
    {
        hit = default;
        handle = null;
        foreach (var h in active)
        {
            if (h.box == null || !h.box.Raycast(r, out RaycastHit candidate, maxDistance)) continue;
            maxDistance = candidate.distance;
            hit = candidate;
            handle = h;
        }
        return handle != null;
    }

    /// <summary>The player's eyes.</summary>
    Transform Head => Camera.main != null ? Camera.main.transform : transform;

    public static CadTurnHandle Create(Transform parent, CadTurn turn)
    {
        var go = new GameObject("CadTurnRing");
        go.layer = IgnoreRaycastLayer;
        go.transform.SetParent(parent, false);
        go.SetActive(false);
        var handle = go.AddComponent<CadTurnHandle>();
        handle.turn = turn;
        handle.Build();
        return handle;
    }

    void Build()
    {
        box = gameObject.AddComponent<BoxCollider>();
        box.size = new Vector3(0.3f, Thickness, 0.3f);
        box.excludeLayers = ~0;  // pointing only: it bumps into nothing
        var surface = gameObject.AddComponent<ColliderSurface>();
        surface.InjectAllColliderSurface(box);
        ray = gameObject.AddComponent<RayInteractable>();
        ray.InjectAllRayInteractable(surface);
        ray.WhenSelectingInteractorAdded.Action += OnRaySelect;
    }

    /// <summary>The ring under the model: its centre (world) and radius (m). <paramref name="on"/> false takes it away.</summary>
    public void Place(Vector3 centre, float radius, bool on)
    {
        if (!on)
        {
            if (gameObject.activeSelf) gameObject.SetActive(false);
            return;
        }
        if (!gameObject.activeSelf) gameObject.SetActive(true);
        transform.SetPositionAndRotation(centre, Quaternion.identity);
        float d = Mathf.Max(0.12f, radius * 2.3f);
        if (Mathf.Abs(box.size.x - d) > 0.002f) box.size = new Vector3(d, Thickness, d);
    }

    void OnEnable() => active.Add(this);

    void OnDisable()
    {
        active.Remove(this);
        mouseHovers = 0;
        Release();
    }

    void OnDestroy()
    {
        if (ray != null) ray.WhenSelectingInteractorAdded.Action -= OnRaySelect;
    }

    void OnRaySelect(RayInteractor interactor) => BeginGrab(CadPointer.FromRay(interactor, Head));

    public void OnPointerEnter(PointerEventData e) => mouseHovers++;
    public void OnPointerExit(PointerEventData e) => mouseHovers = Mathf.Max(0, mouseHovers - 1);
    public void OnPointerDown(PointerEventData e) => BeginGrab(CadPointer.FromEvent(e, Head));

    void BeginGrab(CadPointer by)
    {
        if (by == null || pointer != null || turn == null) return;
        if (!AngleOf(by.Ray, out float angle)) return;
        pointer = by;
        grabAngle = lastAngle = angle;
        swung = 0f;
        grabYaw = turn.StageYaw;
        turn.BeginDrag();
    }

    void Release()
    {
        if (pointer == null) return;
        pointer = null;
        turn?.EndDrag();
    }

    void Update()
    {
        if (pointer == null) return;
        if (!pointer.Held) { Release(); return; }
        if (!AngleOf(pointer.Ray, out float angle)) return;
        swung += Mathf.DeltaAngle(lastAngle, angle);  // unwrapped, so several laps add up
        lastAngle = angle;
        turn.Drag(grabYaw + swung);
    }

    // Where the pointer points round the ring's centre (deg, clockwise seen from above, 0 = away from the user): on the
    // ring's plane, or, for a ray almost along it, its own heading.
    bool AngleOf(Ray r, out float angle)
    {
        Vector3 c = transform.position;
        Vector3 d = r.direction;
        Vector3 at;
        if (Mathf.Abs(d.y) > 0.05f)
        {
            float t = (c.y - r.origin.y) / d.y;
            at = t > 0f ? r.origin + d * t : r.origin + d;
        }
        else at = r.origin + d * Vector3.Distance(r.origin, c);
        Vector3 v = at - c;
        v.y = 0f;
        if (v.sqrMagnitude < 1e-6f) { angle = 0f; return false; }
        angle = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
        return true;
    }
}
