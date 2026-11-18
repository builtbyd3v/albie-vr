using UnityEngine;
using UnityEngine.EventSystems;

namespace Forge.OS
{
    /// <summary>
    /// The panic exit on the corner button and the wrist button: hold it (<see cref="HoldSeconds"/>) and everything closes
    /// (<see cref="OSSpaces.PanicClose"/>), without having to find any panel. While held the button shrinks a little so the
    /// hold shows; when it fires the tap that would follow on release is swallowed (<see cref="TakeFired"/>). Mouse, rays
    /// (pinch held) and pokes.
    /// </summary>
    public sealed class OSLongPress : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public const float HoldSeconds = 1.0f;
        const float HeldScale = 0.88f;

        bool down, fired;
        float downAt;
        Vector3 rest = Vector3.one;

        /// <summary>How many times it has fired (checks).</summary>
        public int Fires { get; private set; }

        /// <summary>The press that just ended was a long press: true once, so the button's tap does nothing.</summary>
        public bool TakeFired()
        {
            bool was = fired;
            fired = false;
            return was;
        }

        public void OnPointerDown(PointerEventData e)
        {
            down = true;
            fired = false;
            downAt = Time.unscaledTime;
            rest = transform.localScale;
        }

        public void OnPointerUp(PointerEventData e) => Release();

        // A ray that slides off a held button keeps the press in uGUI, so only the release ends it; a mouse leaving does too.
        public void OnPointerExit(PointerEventData e)
        {
            if (down && !fired && e != null && e.pointerPress == null) Release();
        }

        void Release()
        {
            if (!down) return;
            down = false;
            transform.localScale = rest;
        }

        void OnDisable()
        {
            if (down) transform.localScale = rest;
            down = false;
        }

        void Update()
        {
            if (!down || fired) return;
            float k = (Time.unscaledTime - downAt) / HoldSeconds;
            transform.localScale = rest * Mathf.Lerp(1f, HeldScale, Mathf.Clamp01(k));
            if (k < 1f) return;
            fired = true;
            Fires++;
            transform.localScale = rest;
            OSSounds.Close();
            Debug.Log("[OS] Long press: closing everything.");
            OSSpaces.PanicClose();
        }
    }
}
