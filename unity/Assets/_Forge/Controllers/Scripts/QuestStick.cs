using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// A Touch controller's thumbstick as a manual input source (<c>stick.x</c>, <c>stick.y</c>, contract section 3.2):
    /// whichever of the two sticks is pushed further. A stopgap so a Thing can be driven by hand on a headset before
    /// Person 1's hands transmitter lands. It reads zero with hands (no stick), and with no OVRManager in the scene
    /// (the Editor sandboxes), since OVRManager is what updates OVRInput.
    /// </summary>
    public static class QuestStick
    {
        /// <summary>Checks only: stands in for the controllers (a check has no headset).</summary>
        public static System.Func<Vector2> Override;

        /// <summary>x +1 right, y +1 forward.</summary>
        public static Vector2 Read()
        {
            if (Override != null) return Override();
            if (OVRManager.instance == null) return Vector2.zero;
            var left = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            var right = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            return right.sqrMagnitude >= left.sqrMagnitude ? right : left;
        }
    }
}
