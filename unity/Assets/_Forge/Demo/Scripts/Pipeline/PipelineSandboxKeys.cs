using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Demo
{
    /// <summary>
    /// PipelineSandbox only: fires the pipeline actions by hand. 1 open, 2 pull, 3 train, 4 push (then poke Confirm with the mouse),
    /// 5 close, 0 reset. The Director does this in the real run.
    /// </summary>
    public sealed class PipelineSandboxKeys : MonoBehaviour
    {
        void Update()
        {
            var k = Keyboard.current;
            if (k == null) return;
            if (k.digit1Key.wasPressedThisFrame) DemoActions.Fire(PipelineDemo.Open);
            if (k.digit2Key.wasPressedThisFrame) DemoActions.Fire(PipelineDemo.Pull);
            if (k.digit3Key.wasPressedThisFrame) DemoActions.Fire(PipelineDemo.Train);
            if (k.digit4Key.wasPressedThisFrame) DemoActions.Fire(PipelineDemo.Push);
            if (k.digit5Key.wasPressedThisFrame) DemoActions.Fire(PipelineDemo.Close);
            if (k.digit0Key.wasPressedThisFrame) DemoActions.Fire(DemoActions.Reset);
        }
    }
}
