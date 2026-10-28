using Forge.Pet;
using Oculus.Interaction;
using UnityEngine;

/// <summary>
/// Hands only: touch Albie (or a crew gator) with a fingertip to poke him, as a mouse click or controller trigger does
/// (<see cref="GatorInteraction"/>). Every Interaction SDK poke interactor's fingertip is checked against each gator's
/// collider; entering it pokes once. Lives in Core because the Pet scripts take no Meta types. Installs itself.
/// </summary>
public class GatorHandPoke : MonoBehaviour
{
    const float TouchRadius = 0.015f;  // m: the fingertip counts as in when this close to the collider
    const float RescanSeconds = 2f;

    PokeInteractor[] pokes = new PokeInteractor[0];
    bool[] inside = new bool[0];
    float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install() => new GameObject(nameof(GatorHandPoke)).AddComponent<GatorHandPoke>();

    void Update()
    {
        // ponytail: rescan (allocates) every 2 s while there are none, instead of hooking the SDK rig's setup
        if (pokes.Length == 0 && Time.time >= nextScan)
        {
            nextScan = Time.time + RescanSeconds;
            pokes = FindObjectsByType<PokeInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None);  // hands start off while controllers are in use
            inside = new bool[pokes.Length];
        }
        for (int i = 0; i < pokes.Length; i++)
            inside[i] = pokes[i] != null && pokes[i].isActiveAndEnabled && Touch(pokes[i].Origin, inside[i]);
    }

    /// <summary>
    /// A fingertip at <paramref name="tip"/>: pokes the gator it touches if it wasn't touching one last frame
    /// (<paramref name="wasInside"/>). Returns whether it touches one now. Public for Play-mode tests.
    /// </summary>
    public static bool Touch(Vector3 tip, bool wasInside)
    {
        for (int g = 0; g < GatorInteraction.All.Count; g++)
        {
            var gator = GatorInteraction.All[g];
            var body = gator.Body;
            if (body == null || (body.ClosestPoint(tip) - tip).sqrMagnitude > TouchRadius * TouchRadius) continue;
            if (!wasInside) gator.Poke();
            return true;
        }
        return false;
    }
}
