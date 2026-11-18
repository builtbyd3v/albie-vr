using System.Collections;
using Oculus.Interaction;
using Oculus.Interaction.Samples;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// Keeps a UI Set toggle's lit look (its "Selected Layer" animator weight) in step with <c>isOn</c>. The UI Set only
    /// moves that layer when the toggle's value changes while it is running, and a re-enabled animator forgets it, so a
    /// toggle built switched on, set from code, or shown again after the menu was hidden could be left half lit. This lets
    /// the UI Set's own smooth transition run, then snaps the layer to where <c>isOn</c> says it belongs.
    /// </summary>
    public sealed class OSToggleSync : MonoBehaviour
    {
        const string LayerName = "Selected Layer";
        const float Settle = 0.3f;  // a little longer than the UI Set's 0.2 s transition

        Toggle toggle;
        TMP_Text label;  // a tab's title: dark on the light lit fill, light on the dark one (see SetLabelColor)
        Animator animator;
        AnimatorOverrideLayerWeight layer;
        int layerIndex = -1;

        void Awake()
        {
            toggle = GetComponent<Toggle>();
            animator = GetComponent<Animator>();
            layer = GetComponent<AnimatorOverrideLayerWeight>();
            Transform title = transform.Find("Content/Background/Elements/" + OSUi.TabTitleName);
            label = title != null ? title.GetComponent<TMP_Text>() : null;
            if (toggle != null) toggle.onValueChanged.AddListener(OnChanged);
        }

        void OnEnable()
        {
            SetLabelColor();
            StartCoroutine(Snap(0f));
        }

        void OnChanged(bool on)
        {
            SetLabelColor();
            if (isActiveAndEnabled) StartCoroutine(Snap(Settle));
        }

        /// <summary>Lights or dims the toggle to match its current <c>isOn</c>.</summary>
        public void Apply()
        {
            SetLabelColor();
            if (!isActiveAndEnabled) return;  // the UI Set and OnEnable re-sync from isOn when it is shown again
            if (layer != null) layer.SetOverrideLayerActive(toggle != null && toggle.isOn);
            StartCoroutine(Snap(Settle));
        }

        // The title's colour for the state, from the theme: the inverted (dark) text colour on the lit light fill, the normal one
        // on the dark fill, solid. The UI Set's own label animation is not used for it (OSUi.Tab renames the title so the
        // animator no longer finds it): that animation leaves the text 10% transparent, and thin dark strokes on a light plate
        // that are not fully covered and 10% clear read as a pale grey.
        void SetLabelColor()
        {
            if (label == null || toggle == null) return;
            OSKit kit = OSKit.Current;
            UITheme theme = kit != null ? kit.theme : null;
            Color color = toggle.isOn
                ? (theme != null ? theme.textPrimaryInvertedColor : new Color(0.15f, 0.15f, 0.15f, 1f))
                : (theme != null ? theme.textPrimaryColor : Color.white);
            color.a = 1f;
            label.color = color;
            // Dark on the light lit fill: a heavier face so it is as solid as the white labels; medium at rest. Shared materials, no fake bold.
            if (toggle.isOn) ForgeTheme.DarkOnLight(label, true);
            else ForgeTheme.Weight(label, ForgeTheme.MediumDilate);
        }

        IEnumerator Snap(float delay)
        {
            yield return null;  // after the UI Set's Start and OnEnable
            if (delay > 0f) yield return new WaitForSeconds(delay);
            if (animator == null || toggle == null) yield break;
            if (layerIndex < 0) layerIndex = animator.GetLayerIndex(LayerName);
            if (layerIndex >= 0) animator.SetLayerWeight(layerIndex, toggle.isOn ? 1f : 0f);
        }
    }
}
