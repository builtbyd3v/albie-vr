using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// Layout pieces of the Project window that <see cref="OSUi"/> doesn't have: the one big primary button (the next step),
    /// and spacers. Sizes are OS units (0.65 mm). Built from <see cref="OSUi"/> controls, so the look, sound and input are the
    /// same as every other button (mouse, ray and poke).
    /// </summary>
    public static class OSProjectLayout
    {
        /// <summary>The big button is 72 units tall (47 mm) and at least 380 wide (247 mm), its label 26 units (the title size).</summary>
        public const float HeroHeight = 72f, HeroMinWidth = 380f, HeroText = 26f;
        /// <summary>Space between the groups of the window (lead, big button, steps, bottom row): 28 units, 18 mm.</summary>
        public const float GroupGap = 28f;

        /// <summary>The window's one big primary button, alone on its row. Named "Button " + label like every OSUi button.</summary>
        public static Button Hero(Transform parent, string label, Action onClick, string icon)
        {
            RectTransform row = OSUi.Row(parent, HeroHeight, 0f);
            OSUi.Rigid(row);
            Button button = OSUi.Button(row, label, onClick, OSButtonStyle.Primary, icon);
            if (button == null) return null;
            bool hasIcon = button.transform.Find("Content/Background/Elements/Icon") is Transform i && i.gameObject.activeSelf;
            float width = HeroMinWidth;
            Transform labelTransform = button.transform.Find("Content/Background/Elements/Text/Label");
            TMP_Text text = labelTransform != null ? labelTransform.GetComponent<TMP_Text>() : null;
            if (text != null && !string.IsNullOrEmpty(label))
            {
                text.fontSize = HeroText;
                float labelWidth = text.GetPreferredValues(label).x;
                float fit = Mathf.Ceil(labelWidth) + 2f;
                ((RectTransform)text.transform).sizeDelta = new Vector2(fit, 36f);
                ((RectTransform)text.transform.parent).sizeDelta = new Vector2(fit, 40f);
                width = Mathf.Max(HeroMinWidth, labelWidth + 64f + (hasIcon ? 40f : 0f));
            }
            OSUi.Size(button.transform, width, HeroHeight);
            return button;
        }

        /// <summary>An empty element that takes the spare height of a column (pushes what follows to the bottom).</summary>
        public static void FlexibleHeight(Transform parent)
        {
            var go = new GameObject("Spacer", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var element = go.AddComponent<LayoutElement>();
            element.flexibleHeight = 1f;
            element.minHeight = 0f;
        }

        /// <summary>An empty element that takes the spare width of a row (pushes what follows to the right).</summary>
        public static void FlexibleWidth(Transform parent)
        {
            var go = new GameObject("Spacer", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.AddComponent<LayoutElement>().flexibleWidth = 1f;
        }

        /// <summary>Gives a button made by <see cref="OSUi.Button"/> its own width at <paramref name="height"/> units tall.</summary>
        public static void Fit(Button button, float height)
        {
            if (button == null) return;
            var layout = button.GetComponent<LayoutElement>();
            OSUi.Size(button.transform, layout != null ? layout.preferredWidth : OSUi.ButtonMinWidth, height);
        }
    }
}
