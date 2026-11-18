using Forge.Controllers;
using Oculus.Interaction.Samples;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The Drive window's on-screen pad, the mouse and accessibility fallback for the handheld <see cref="RcTransmitter"/>
    /// (which writes the same channels): a two-axis square with a knob. Press it (mouse, or a poke or a ray through the same canvas
    /// events) and the knob follows the pointer, clamped to the square; up is throttle, down is reverse, left and right
    /// steer. While it is held it writes the manual channels <c>stick.x</c> (right is positive) and <c>stick.y</c> (up is
    /// positive) every frame, the same two channels a Touch thumbstick feeds (see <c>SlalomBrain.Keys</c>), so recording and
    /// the Autopilot need no change. Nothing zeroes <see cref="ManualInputs"/> by itself, so the pad writes zero, once, when
    /// the pointer lets go, when the pad is switched off or destroyed, and when Stop is pressed (<see cref="Release"/>), and
    /// the knob springs back to the middle in about <see cref="SpringSeconds"/>. A pointer that is lost without an up event (a
    /// poke or ray that goes away, a pointer module that stops) is noticed: while the pad is held it keeps the pressing pointer's
    /// event data and lets go by itself once that no longer says the pad is pressed (the pointer's press was cleared or moved to
    /// another object) or once the pad's canvas is no longer shown. Built from the UI Set's rounded box and the
    /// kit's chevrons (<see cref="Create"/>). It does nothing per frame beyond a few float writes.
    ///
    /// A Thing with four axes (the drone) gets two pads: <see cref="SetChannels"/> points the second one at
    /// <c>stick2.x</c> and <c>stick2.y</c>. Each pad takes one pointer, so two hands hold the two pads at once. While any pad
    /// is held, <see cref="HandsTransmitter.PadsHeld"/> says so, and the pinch sticks stand down.
    /// </summary>
    public sealed class OSDrivePad : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler, IEndDragHandler
    {
        /// <summary>How long the knob takes to spring back to the middle.</summary>
        public const float SpringSeconds = 0.12f;

        static readonly int StickX = ManualInputs.IndexOf("stick.x");
        static readonly int StickY = ManualInputs.IndexOf("stick.y");

        /// <summary>The throttle a full push up writes to <c>stick.y</c>. A Touch stick and the W key write this much on the brain
        /// truck (the speed a hand can weave the cones at), so laps driven here train like theirs; 1 for a truck with no such limit.</summary>
        public float ThrottleScale = 1f;

        int channelX = StickX, channelY = StickY;

        /// <summary>
        /// Which two manual inputs the pad writes: sideways then up, by name (<see cref="ManualInputs.Names"/>). The default
        /// is <c>stick.x</c>, <c>stick.y</c>. A held pad lets go first, so the old channels are left at zero.
        /// </summary>
        public void SetChannels(string x, string y)
        {
            int nx = ManualInputs.IndexOf(x), ny = ManualInputs.IndexOf(y);
            if (nx == channelX && ny == channelY) return;
            if (held || dirty) Release();
            channelX = nx;
            channelY = ny;
        }

        RectTransform rect, knob;
        Image knobImage;
        float travel = 100f;       // units the knob can move from the middle, each way
        Vector2 value;             // what is being written: -1 to 1 on each axis (y before ThrottleScale)
        Vector2 shown;             // where the knob is drawn, the same scale; springs back after a release
        bool held, dirty;          // dirty: the last write to the channels was not zero
        int pointer = int.MinValue;
        PointerEventData pressing;  // the event data of the pointer that pressed: its pointerPress says whether it still presses us
        Canvas canvas;              // the pad's own canvas (and its root), for "is the pad still shown"
        int pressFrame = -1;        // the frame of the press: the module sets pointerPress just after our handler returns
        Color idleColor = new Color(1f, 1f, 1f, 0.82f), heldColor = Color.white;

        /// <summary>The pad's pointer position, -1 to 1 on each axis (x right, y up). Zero when nobody holds it.</summary>
        public Vector2 Value => value;

        /// <summary>True while a pointer holds the pad.</summary>
        public bool Held => held;

        /// <summary>Where the knob is drawn, -1 to 1 on each axis (it is still on its way home just after a release).</summary>
        public Vector2 Shown => shown;

        /// <summary>The pad's size in OS units, and how far the knob can travel from the middle each way.</summary>
        public float Travel => travel;

        /// <summary>The knob, for pictures and checks.</summary>
        public RectTransform Knob => knob;

        /// <summary>
        /// Builds a pad <paramref name="size"/> OS units square under <paramref name="parent"/>: the rounded plate (the
        /// pointer target), a faint cross, four chevrons and the knob. The pad is the returned component's own object.
        /// </summary>
        public static OSDrivePad Create(Transform parent, float size, float knobSize)
        {
            OSKit kit = OSKit.Current;
            var go = new GameObject("Drive pad", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var pad = go.AddComponent<OSDrivePad>();
            pad.rect = (RectTransform)go.transform;
            pad.rect.anchorMin = pad.rect.anchorMax = pad.rect.pivot = new Vector2(0.5f, 0.5f);
            OSUi.Size(go.transform, size, size);

            Material rounded = RoundedMaterial(kit);
            var plate = go.AddComponent<Image>();
            plate.color = kit != null && kit.theme != null ? kit.theme.secondaryButton.normal : new Color(0.29f, 0.29f, 0.29f, 1f);
            if (rounded != null) plate.material = rounded;
            plate.raycastTarget = true;  // the pointer target: a mouse, a ray and a poke all reach the handlers through it
            go.AddComponent<RoundedBoxUIProperties>().borderRadius = new Vector4(size * 0.16f, size * 0.16f, size * 0.16f, size * 0.16f);

            // A faint cross marks the middle, and four chevrons say which way is which.
            Color faint = new Color(1f, 1f, 1f, 0.14f);
            Line(go.transform, "Cross across", new Vector2(size - 56f, 2f), faint);
            Line(go.transform, "Cross down", new Vector2(2f, size - 56f), faint);
            float edge = size * 0.5f - 26f;
            Chevron(go.transform, "Up", new Vector2(0f, edge), 90f);
            Chevron(go.transform, "Down", new Vector2(0f, -edge), -90f);
            Chevron(go.transform, "Left", new Vector2(-edge, 0f), 180f);
            Chevron(go.transform, "Right", new Vector2(edge, 0f), 0f);

            var knobGo = new GameObject("Knob", typeof(RectTransform));
            knobGo.transform.SetParent(go.transform, false);
            pad.knob = (RectTransform)knobGo.transform;
            pad.knob.anchorMin = pad.knob.anchorMax = pad.knob.pivot = new Vector2(0.5f, 0.5f);
            pad.knob.sizeDelta = new Vector2(knobSize, knobSize);
            pad.knobImage = knobGo.AddComponent<Image>();
            if (rounded != null) pad.knobImage.material = rounded;
            pad.knobImage.raycastTarget = false;  // the plate takes the pointer, so the knob never changes what is pressed
            knobGo.AddComponent<RoundedBoxUIProperties>().borderRadius = new Vector4(knobSize, knobSize, knobSize, knobSize);
            if (kit != null && kit.theme != null)
            {
                pad.idleColor = kit.theme.primaryButton.normal;
                pad.heldColor = kit.theme.primaryButton.highlighted;
            }
            pad.knobImage.color = pad.idleColor;
            pad.travel = Mathf.Max(10f, size * 0.5f - knobSize * 0.5f - 10f);
            return pad;
        }

        // The UI Set's rounded-box material, taken from one of its button plates (a plain Image would be square).
        static Material RoundedMaterial(OSKit kit)
        {
            if (kit == null || kit.secondaryButton == null) return null;
            Transform background = kit.secondaryButton.transform.Find("Content/Background");
            var image = background != null ? background.GetComponent<Image>() : null;
            return image != null ? image.material : null;
        }

        static void Line(Transform parent, string name, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        static void Chevron(Transform parent, string name, Vector2 at, float degrees)
        {
            Sprite sprite = OSUi.Icon("chevron_right");
            if (sprite == null) return;
            var go = new GameObject("Chevron " + name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(34f, 34f);
            rect.anchoredPosition = at;
            rect.localRotation = Quaternion.Euler(0f, 0f, degrees);
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.color = new Color(1f, 1f, 1f, 0.45f);
            image.raycastTarget = false;
        }

        // ---------------------------------------------------------------- the pointer

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            if (held) return;  // one pointer at a time: a second finger or a hand's ray doesn't take the knob
            pointer = eventData.pointerId;
            pressing = eventData;
            canvas = GetComponentInParent<Canvas>();
            pressFrame = Time.frameCount;
            held = true;
            HandsTransmitter.PadsHeld++;
            Follow(eventData);
            Write(value);
            if (knobImage != null) knobImage.color = heldColor;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!held || eventData.pointerId != pointer) return;
            Follow(eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (held && eventData.pointerId == pointer) Release();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (held && eventData.pointerId == pointer) Release();
        }

        // The pointer's place on the pad. The camera is the one the canvas event came through (a mouse's, or the Interaction
        // SDK's pointer camera for a ray or a poke), which is what makes the screen position a point on the pad.
        void Follow(PointerEventData e)
        {
            Camera cam = EventCamera(e);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, e.position, cam, out Vector2 local)) return;
            Vector2 offset = local - rect.rect.center;
            value.x = Mathf.Clamp(offset.x / travel, -1f, 1f);
            value.y = Mathf.Clamp(offset.y / travel, -1f, 1f);
            shown = value;
            PlaceKnob();
        }

        static Camera EventCamera(PointerEventData e)
        {
            BaseRaycaster module = e.pointerPressRaycast.module;
            if (module == null) module = e.pointerCurrentRaycast.module;
            Camera cam = module != null ? module.eventCamera : null;
            return cam != null ? cam : Camera.main;
        }

        void PlaceKnob()
        {
            if (knob != null) knob.anchoredPosition = shown * travel;
        }

        // ---------------------------------------------------------------- the channels

        void Write(Vector2 v)
        {
            if (channelX >= 0) ManualInputs.Values[channelX] = Mathf.Clamp(v.x, -1f, 1f);
            if (channelY >= 0) ManualInputs.Values[channelY] = Mathf.Clamp(v.y * ThrottleScale, -1f, 1f);
            dirty = v.x != 0f || v.y != 0f;
        }

        /// <summary>
        /// Lets go: nobody holds the pad, both channels get zero (once), and the knob springs back. Safe to call when the pad
        /// is not held (it then writes nothing unless its last write was not zero). Stop, a disabled pad and a pointer up all
        /// come here.
        /// </summary>
        public void Release()
        {
            if (held && HandsTransmitter.PadsHeld > 0) HandsTransmitter.PadsHeld--;
            held = false;
            pointer = int.MinValue;
            pressing = null;
            value = Vector2.zero;
            if (dirty) Write(Vector2.zero);
            if (knobImage != null) knobImage.color = idleColor;
        }

        void Update()
        {
            if (held)
            {
                // A press whose pointer went away without an up (nothing else would ever zero the channels): let go.
                if (Time.frameCount != pressFrame && !StillPressed())
                {
                    Release();
                    return;
                }
                Write(value);  // every frame while held, so a lap's own sources and EndLap's Clear never leave the truck without input
                return;
            }
            if (shown == Vector2.zero) return;
            // Springs back to the middle in SpringSeconds, whatever the time scale (a test may run Play mode faster).
            shown = Vector2.MoveTowards(shown, Vector2.zero, Time.unscaledDeltaTime / SpringSeconds);
            PlaceKnob();
        }

        // True while the pointer that pressed still presses this pad and the pad is on screen. No allocations: a few reads.
        bool StillPressed()
        {
            PointerEventData p = pressing;
            if (p == null || p.pointerPress != gameObject) return false;  // cleared by the module on release or cancel, or given to another object
            Canvas c = canvas;
            return c == null || (c.isActiveAndEnabled && c.rootCanvas.isActiveAndEnabled);
        }

        void OnDisable()
        {
            if (held || dirty) Release();
            shown = Vector2.zero;
            PlaceKnob();
        }
    }
}
