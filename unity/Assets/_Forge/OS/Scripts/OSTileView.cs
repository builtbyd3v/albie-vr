using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>One tile of the menu grid: a UI Set text tile showing an entry's icon, title and one status line. Pooled by <see cref="OSMenu"/>.</summary>
    public sealed class OSTileView : MonoBehaviour
    {
        public Button button;
        public RectTransform rect;
        public Image icon;
        public TMP_Text title, status;
        public Image outline;
        public OSEntry entry;

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
