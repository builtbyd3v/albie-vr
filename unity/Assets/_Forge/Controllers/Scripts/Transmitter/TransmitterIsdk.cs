using System;
using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.GrabAPI;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.Input;
using Oculus.Interaction.Surfaces;
using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// The transmitter's Interaction SDK pieces, added in code (as <c>VolumeHandle</c> and <c>NeuronTouch</c> add theirs):
    /// - a <see cref="HandGrabInteractable"/> (pinch and palm, the SDK's default rules) on a kinematic rigidbody, so the rig's
    ///   HandGrabInteractors (OVRComprehensiveInteractionRig has them) can take it. It is used only to say which hand holds
    ///   it: there is no Grabbable, so the SDK moves nothing, and <see cref="RcTransmitter"/> places it from the hands itself
    ///   (each thumb has to stay free to work its stick). The transmitter's own grip test from the hand joints runs too;
    ///   either one holding is a hold.
    /// - a <see cref="RayInteractable"/> on the body: point a hand or controller ray at it and select (pinch or trigger), and
    ///   it comes to you (<see cref="RcTransmitter.Show"/>).
    /// If any of it fails to set up, the transmitter still works from the joints alone; the warning says why.
    /// The colliders are triggers on the Ignore Raycast layer, so the Things' sensors and the room's raycasts never see them.
    /// </summary>
    public static class TransmitterIsdk
    {
        public static void Add(RcTransmitter tx, GameObject model, BoxCollider body)
        {
            try
            {
                var rb = model.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
                var grab = model.AddComponent<HandGrabInteractable>();
                grab.InjectAllHandGrabInteractable(GrabTypeFlags.All, rb, GrabbingRule.DefaultPinchRule, GrabbingRule.DefaultPalmRule);
                grab.WhenSelectingInteractorAdded.Action += i => tx.SdkGrip(HandOf(i), true);
                grab.WhenSelectingInteractorRemoved.Action += i => tx.SdkGrip(HandOf(i), false);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Transmitter] no Interaction SDK hand grab (" + e.Message + "); the hand-joint grip still works.");
            }
            try
            {
                var surface = body.gameObject.AddComponent<ColliderSurface>();
                surface.InjectAllColliderSurface(body);
                var ray = body.gameObject.AddComponent<RayInteractable>();
                ray.InjectAllRayInteractable(surface);
                ray.WhenSelectingInteractorAdded.Action += i => { if (!tx.Held) tx.Show(); };
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Transmitter] no Interaction SDK ray (" + e.Message + ").");
            }
        }

        static int HandOf(HandGrabInteractor interactor)
        {
            IHand hand = interactor != null ? interactor.Hand : null;
            if (hand == null) return -1;
            return hand.Handedness == Handedness.Left ? 0 : 1;
        }
    }
}
