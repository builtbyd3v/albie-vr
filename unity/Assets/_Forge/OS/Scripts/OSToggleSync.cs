using System.Collections;
using Oculus.Interaction.Samples;
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
        Animator animator;
        AnimatorOverrideLayerWeight layer;
        int layerIndex = -1;

        void Awake()
        {
            toggle = GetComponent<Toggle>();
            animator = GetComponent<Animator>();
            layer = GetComponent<AnimatorOverrideLayerWeight>();
            if (toggle != null) toggle.onValueChanged.AddListener(OnChanged);
        }

        void OnEnable() => StartCoroutine(Snap(0f));

        void OnChanged(bool on)
        {
            if (isActiveAndEnabled) StartCoroutine(Snap(Settle));
        }

        /// <summary>Lights or dims the toggle to match its current <c>isOn</c>.</summary>
        public void Apply()
        {
            if (!isActiveAndEnabled) return;  // the UI Set and OnEnable re-sync from isOn when it is shown again
            if (layer != null) layer.SetOverrideLayerActive(toggle != null && toggle.isOn);
            StartCoroutine(Snap(Settle));
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
