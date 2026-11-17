using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Demo
{
    /// <summary>
    /// The contract between the video-demo Director and the pieces it drives (docs/demo/TONIGHT_PLAN.md, section 4).
    /// The Director reads the beat list and fires named actions; each piece registers the actions it owns
    /// (research.*, pipeline.*, drone.*, dronecam.*) in OnEnable and unregisters in OnDisable.
    /// Pieces tell the Director they finished something with <see cref="Raise"/> (for example "research.verdict.shown"),
    /// which a beat can wait on. Owned by the demo-director station; propose changes, don't silently change them.
    /// </summary>
    public sealed class DemoCue
    {
        public string Action;
        public JObject Args;   // never null when a handler sees it
        public int Beat;       // index of the beat that fired it, -1 when fired by hand
    }

    public static class DemoActions
    {
        static readonly Dictionary<string, Action<DemoCue>> handlers = new Dictionary<string, Action<DemoCue>>();

        /// <summary>A piece reports progress ("research.verdict.shown", "pipeline.pushed", "drone.mounted").</summary>
        public static event Action<string> Signal;

        public static void Register(string action, Action<DemoCue> handler)
        {
            if (string.IsNullOrEmpty(action) || handler == null) return;
            handlers.TryGetValue(action, out var existing);
            handlers[action] = existing + handler;
        }

        public static void Unregister(string action, Action<DemoCue> handler)
        {
            if (string.IsNullOrEmpty(action) || handler == null) return;
            if (!handlers.TryGetValue(action, out var existing)) return;
            existing -= handler;
            if (existing == null) handlers.Remove(action); else handlers[action] = existing;
        }

        public static bool Has(string action) => action != null && handlers.ContainsKey(action);

        /// <summary>Fires an action. Returns false (and logs once) when nobody owns it, so a missing piece never stops the run.</summary>
        public static bool Fire(string action, JObject args = null, int beat = -1)
        {
            if (!handlers.TryGetValue(action ?? "", out var h))
            {
                Debug.LogWarning($"[Demo] no handler for '{action}'");
                return false;
            }
            var cue = new DemoCue { Action = action, Args = args ?? new JObject(), Beat = beat };
            try { h(cue); }
            catch (Exception e) { Debug.LogException(e); }
            return true;
        }

        public static void Raise(string signal)
        {
            if (string.IsNullOrEmpty(signal)) return;
            Debug.Log($"[Demo] signal {signal}");
            Signal?.Invoke(signal);
        }

        /// <summary>Every piece registers "demo.reset" and puts itself back to the start of the script.</summary>
        public const string Reset = "demo.reset";
    }

    /// <summary>
    /// Shared places for the seated / reclined user (2 ft reach, FoV-aware). Poses are captured from the head's yaw
    /// when <see cref="Capture"/> is called (the Director does it at the start of the run and on reset), so panels
    /// don't swing when the user looks around. Pitch is ignored on purpose: a reclined head looks up.
    /// </summary>
    public static class DemoLayout
    {
        public enum Slot { Center, Left, Right, Low, Drone }

        static Vector3 origin;
        static Quaternion yaw = Quaternion.identity;
        static bool captured;

        public static Transform Head
        {
            get
            {
                var cam = Camera.main;
                return cam != null ? cam.transform : null;
            }
        }

        public static void Capture()
        {
            var head = Head;
            if (head == null) return;
            var fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            yaw = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            origin = head.position;
            captured = true;
        }

        /// <summary>A pose facing the user. Distances in metres from the captured head position.</summary>
        public static Pose Get(Slot slot)
        {
            if (!captured) Capture();
            Vector3 local;
            switch (slot)
            {
                case Slot.Left:   local = new Vector3(-0.42f, -0.08f, 0.62f); break;
                case Slot.Right:  local = new Vector3(0.42f, -0.08f, 0.62f); break;
                case Slot.Low:    local = new Vector3(0f, -0.38f, 0.48f); break;
                case Slot.Drone:  local = new Vector3(0f, -0.05f, 0.85f); break;
                default:          local = new Vector3(0f, -0.06f, 0.7f); break;
            }
            var pos = origin + yaw * local;
            var look = pos - origin; look.y = 0f;
            var rot = look.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(look.normalized, Vector3.up) : yaw;
            return new Pose(pos, rot);
        }
    }
}
