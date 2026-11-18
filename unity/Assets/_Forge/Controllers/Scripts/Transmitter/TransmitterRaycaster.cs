using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace Forge.Controllers
{
    /// <summary>
    /// Puts the transmitter's parts (body, stick caps, buttons, mode switch) into the EventSystem's raycast for a mouse:
    /// DesktopRig's pointer (crosshair or free cursor) or a plain mouse input module in a sandbox, as <c>NeuronRaycaster</c>
    /// does for the neurons. The hit object is the part's collider, and the pointer events go up to
    /// <see cref="RcTransmitter"/>, which tells the parts apart. Controller and hand rays reach it through its
    /// RayInteractable instead (<see cref="TransmitterIsdk"/>). No allocations: a few Collider.Raycast calls.
    /// </summary>
    public sealed class TransmitterRaycaster : BaseRaycaster
    {
        const float MaxDistance = 6f;
        RcTransmitter owner;

        public void Init(RcTransmitter tx) => owner = tx;

        public override Camera eventCamera => Camera.main;

        public override void Raycast(PointerEventData e, List<RaycastResult> results)
        {
            if (owner == null || !owner.Shown) return;
            Ray ray;
            var rig = DesktopRig.Current;
            if (rig != null && rig.OwnsPointer(e)) ray = rig.PointerRay;
            else if (e is ExtendedPointerEventData x && x.pointerType == UIPointerType.MouseOrPen && Camera.main != null)
                ray = Camera.main.ScreenPointToRay(e.position);
            else return;

            Collider[] parts = owner.Parts;
            float nearest = MaxDistance;
            Collider best = null;
            RaycastHit bestHit = default;
            for (int i = 0; i < parts.Length; i++)
            {
                Collider c = parts[i];
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                if (!c.Raycast(ray, out RaycastHit hit, nearest)) continue;
                // The body's box is big: a cap, button or switch just behind its surface still wins.
                float d = i == 0 ? hit.distance + 0.03f : hit.distance;
                if (d >= nearest) continue;
                nearest = d;
                best = c;
                bestHit = hit;
            }
            if (best == null) return;
            results.Add(new RaycastResult
            {
                gameObject = best.gameObject, module = this, distance = bestHit.distance, worldPosition = bestHit.point,
                worldNormal = bestHit.normal, screenPosition = e.position, index = results.Count,
            });
        }
    }
}
