using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>One tile of the menu grid: a UI Set text tile showing an entry's icon, title and one status line. Pooled by <see cref="OSMenu"/>.</summary>
    public sealed class OSTileView : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler, IPointerEnterHandler
    {
        // The UI Set tile animator lights the plate on press (white 20%, brighter than rest and hover), so a press read as
        // a lit state. A press must read as pushed in: while the pointer is down on the tile the plate is darkened under
        // its resting fill (the animator's small scale-down stays). The caption keeps a colour that passes 4.5:1 in rest,
        // hover and pressed, which the animator's own dim grey does not.
        static readonly Color PressedFill = new Color(0f, 0f, 0f, 0.30f);
        static readonly Color CaptionColor = new Color(1f, 1f, 1f, 0.88f);

        Image plate;
        bool down, inside;

        public Button button;
        public RectTransform rect;
        public Image icon;
        public TMP_Text title, status;
        public Image outline;
        [System.NonSerialized] public OSEntry entry;

        void Awake()
        {
            Transform bg = transform.Find("Content/Background");
            if (bg != null) plate = bg.GetComponent<Image>();
        }

        void OnDisable() => down = inside = false;

        public void OnPointerDown(PointerEventData e) { down = true; inside = true; }
        public void OnPointerUp(PointerEventData e) => down = false;
        public void OnPointerEnter(PointerEventData e) => inside = true;
        public void OnPointerExit(PointerEventData e) => inside = false;

        // After the animator has written this frame's colours.
        void LateUpdate()
        {
            if (plate != null && down && inside && button != null && button.interactable) plate.color = PressedFill;
            if (status != null && status.color != CaptionColor) status.color = CaptionColor;
        }

        /// <summary>Shows an entry: icon, title, and its status line (or "open" when it is open and says nothing else).</summary>
        public void Show(OSEntry e)
        {
            entry = e;
            if (icon != null)
            {
                Sprite sprite = OSUi.Icon(e.icon);
                icon.sprite = sprite;
                icon.enabled = sprite != null;
            }
            if (title != null) title.text = e.title ?? e.id;
            string line = null;
            try
            {
                if (e.status != null) line = e.status();
                if (string.IsNullOrEmpty(line) && e.isOpen != null && e.isOpen()) line = "open";
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
            }
            if (status != null) status.text = line ?? "";
        }
    }
}
