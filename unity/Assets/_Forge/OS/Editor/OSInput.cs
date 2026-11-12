using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Presses a keyboard key in Play mode the way a user's key arrives: a keyboard state event through the Input System,
    /// queued from inside the player loop so it is read in a game frame (a key queued from editor code can land in an editor
    /// update, where <c>wasPressedThisFrame</c> never sees it). Uses a virtual keyboard, so it works with the editor window
    /// unfocused. For play tests and checks.
    /// </summary>
    public static class OSInput
    {
        struct Step
        {
            public Key[] keys;  // pressed after this many frames; empty releases
            public int frame;
        }

        static readonly List<Step> steps = new List<Step>();
        static Keyboard keyboard;
        static int startFrame;
        static bool hooked;

        /// <summary>True while a tap is still being played out.</summary>
        public static bool Busy => steps.Count > 0;

        /// <summary>Presses the keys together, holds them for a few frames, and releases them. Returns at once; <see cref="Busy"/> goes false when done.</summary>
        public static void Tap(params Key[] keys)
        {
            if (!Application.isPlaying) return;
            if (keyboard == null || !keyboard.added) keyboard = InputSystem.AddDevice<Keyboard>("OSTestKeyboard");
            steps.Clear();
            startFrame = Time.frameCount;
            steps.Add(new Step { keys = keys, frame = 1 });
            steps.Add(new Step { keys = new Key[0], frame = 5 });
            if (!hooked) Hook();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            steps.Clear();
            keyboard = null;
            hooked = false;
        }

        // The system stays in the loop once added and does nothing while no tap is playing; a fresh Hook replaces a stale one.
        static void Hook()
        {
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != typeof(Update)) continue;
                var list = new List<PlayerLoopSystem>(loop.subSystemList[i].subSystemList);
                list.RemoveAll(s => s.type == typeof(OSInput));
                list.Add(new PlayerLoopSystem { type = typeof(OSInput), updateDelegate = Tick });
                loop.subSystemList[i].subSystemList = list.ToArray();
                break;
            }
            PlayerLoop.SetPlayerLoop(loop);
            hooked = true;
        }

        static void Tick()
        {
            if (steps.Count == 0) return;
            if (!Application.isPlaying) { steps.Clear(); return; }
            Step next = steps[0];
            if (Time.frameCount - startFrame < next.frame) return;
            steps.RemoveAt(0);
            InputSystem.QueueStateEvent(keyboard, next.keys.Length == 0 ? new KeyboardState() : new KeyboardState(next.keys));
        }
    }
}
