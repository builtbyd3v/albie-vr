using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Pet
{
    /// <summary>
    /// Keyboard testing for the pet sandbox:
    /// 1-9 = AlbieState Intro..Error (enum order), P = poke, J/B/S = Jump/Bounce/Spin,
    /// C = ComeToUser, M = cycle moods, T = test caption, H = hop onto the user's desk, G = hop back to the floor,
    /// L = lay down (rest), R = roll sideways (off the desk edge -> fall + stun + hop back). P x3 within 4 s also rolls.
    /// Note: S also walks backwards on DesktopRig; rebind spinKey if that bothers you.
    /// </summary>
    public class PetSandboxHotkeys : MonoBehaviour
    {
        [SerializeField, Tooltip("Albie to drive. Empty = first AlbieController found at Awake.")]
        AlbieController albie;
        [SerializeField] Key jumpKey = Key.J;
        [SerializeField] Key bounceKey = Key.B;
        [SerializeField] Key spinKey = Key.K; // not S: that walks the DesktopRig backwards
        [SerializeField] Key pokeKey = Key.P;
        [SerializeField] Key comeKey = Key.C;
        [SerializeField] Key moodKey = Key.M;
        [SerializeField] Key captionKey = Key.T;
        [SerializeField, Tooltip("AlbieController.HopToUserDesk().")] Key hopDeskKey = Key.H;
        [SerializeField, Tooltip("AlbieController.HopToFloor().")] Key hopFloorKey = Key.G;
        [SerializeField, Tooltip("AlbieController.Rest(RestPose.Lay).")] Key layKey = Key.L;
        [SerializeField, Tooltip("AlbieController.StartRoll().")] Key rollKey = Key.R;

        AlbieInteraction interaction;
        AlbieExpressions expressions;
        AlbieCaption caption;
        int moodIndex;

        void Awake()
        {
            if (albie == null) albie = FindAnyObjectByType<AlbieController>();
            if (albie == null) { Debug.LogWarning("[PetSandboxHotkeys] No AlbieController in scene.", this); return; }
            interaction = albie.GetComponent<AlbieInteraction>();
            expressions = albie.GetComponent<AlbieExpressions>();
            caption = albie.GetComponent<AlbieCaption>();
        }

        void Update()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null || albie == null) return;

            // 1..9 (top row or numpad) -> AlbieState 0..8.
            for (int i = 0; i < 9; i++)
            {
                if (kb[Key.Digit1 + i].wasPressedThisFrame || kb[Key.Numpad1 + i].wasPressedThisFrame)
                {
                    albie.SetState((AlbieState)i);
                    Debug.Log("[PetSandboxHotkeys] State -> " + (AlbieState)i, this);
                }
            }

            if (kb[jumpKey].wasPressedThisFrame) albie.PlayEmote("Jump");
            if (kb[bounceKey].wasPressedThisFrame) albie.PlayEmote("Bounce");
            if (kb[spinKey].wasPressedThisFrame) albie.PlayEmote("Spin");
            if (kb[comeKey].wasPressedThisFrame) albie.ComeToUser();
            if (kb[hopDeskKey].wasPressedThisFrame) albie.HopToUserDesk();
            if (kb[hopFloorKey].wasPressedThisFrame) albie.HopToFloor();
            if (kb[layKey].wasPressedThisFrame) albie.Rest(RestPose.Lay);
            if (kb[rollKey].wasPressedThisFrame) albie.StartRoll();
            if (kb[pokeKey].wasPressedThisFrame && interaction != null) interaction.Poke();

            if (kb[moodKey].wasPressedThisFrame && expressions != null)
            {
                moodIndex = (moodIndex + 1) % 8;               // Mood has 8 values (incl. Sleepy)
                expressions.SetMood((Mood)moodIndex);
            }
            if (kb[captionKey].wasPressedThisFrame && caption != null)
                caption.SetText("Hi! I'm Albie. Ask me anything about the project.", false);
        }
    }
}
