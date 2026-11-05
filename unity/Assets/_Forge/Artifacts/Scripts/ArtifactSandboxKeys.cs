using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Artifacts
{
    /// <summary>
    /// ArtifactsSandbox only, with the hub off: 1 Stance check (opens with no permissions, sliders change it live),
    /// 2 Slalom (permissions, then says what isn't built yet), 3 a broken artifact (the validator refuses it),
    /// 4 an artifact that doesn't exist, 5 three-body problem (live, with trails), 0 close, 9 forget every approval.
    /// </summary>
    public class ArtifactSandboxKeys : MonoBehaviour
    {
        public ArtifactHost host;

        void Update()
        {
            var k = Keyboard.current;
            if (k == null) return;
            if (k.digit1Key.wasPressedThisFrame) host.Open("stance");
            if (k.digit2Key.wasPressedThisFrame) host.Open("slalom");
            if (k.digit3Key.wasPressedThisFrame) host.Open("broken");
            if (k.digit4Key.wasPressedThisFrame) host.Open("nothing_here");
            if (k.digit5Key.wasPressedThisFrame) host.Open("three_body");
            if (k.digit0Key.wasPressedThisFrame) host.Close();
            if (k.digit9Key.wasPressedThisFrame) ArtifactApprovals.Forget();
        }
    }
}
