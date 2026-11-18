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

        /// <summary>Checks only: stands in for both sticks in <see cref="ReadBoth"/> (left, then right).</summary>
        public static System.Func<(Vector2 left, Vector2 right)> OverrideBoth;

        /// <summary>
        /// Both thumbsticks, for a Thing with four axes (the drone: the left stick climbs and turns, the right flies
        /// forward and sideways). Each: x +1 right, y +1 forward. Zero with hands, or with no OVRManager in the scene.
        /// </summary>
        public static void ReadBoth(out Vector2 left, out Vector2 right)
        {
            if (OverrideBoth != null) { (left, right) = OverrideBoth(); return; }
            if (OVRManager.instance == null) { left = right = Vector2.zero; return; }
            left = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            right = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        }
    }
}
