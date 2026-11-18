using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// On the brain truck (added by <see cref="Forge.Artifacts.SlalomBrain.Spawn"/>): keeps its driving model
    /// (<see cref="ExampleTruckBody.ApplyDesign"/>) and its look (<see cref="TruckShell.SetDesign"/>) on the open project's
    /// truck design (<see cref="TruckDesignStore"/>). Applied at once on every change, also mid-run; the project is re-checked
    /// once a second (nothing per frame otherwise).
    /// As <see cref="DroneDesign"/> follows the CAD quadcopter, a new version of the CAD library's <c>truck</c> design (a
    /// slider or preset in the CAD panel, Albie's cad_set_params; not just opening it) is taken into the live design: the
    /// same parameter names. Offline, with no CAD at all, the device's design is the truth.
    /// </summary>
    public sealed class TruckDesignLink : MonoBehaviour
    {
        ExampleTruckBody body;
        TruckShell shell;
        float nextPoll;
        CadSession hooked;
        int lastCadVersion = -1;
        Forge.Artifacts.SlalomBrain brain;
        float rolledAt = -1f;
        /// <summary>Between laps (free driving with the transmitter), a rolled-over truck is put back on its wheels after this long (s).</summary>
        public const float RightAfterSeconds = 1.5f;

        void OnEnable()
        {
            TruckDesignStore.Changed -= Apply;
            TruckDesignStore.Changed += Apply;
            Apply();
        }

        void OnDisable()
        {
            Hook(null);
            TruckDesignStore.Changed -= Apply;
            TruckDesignStore.Flush();
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) TruckDesignStore.Flush();
        }

        void Update()
        {
            float now = Time.unscaledTime;
            RightIfRolled(now);
            if (now < nextPoll) return;
            nextPoll = now + 1f;
            TruckDesignStore.Poll();
            if (CadSession.Instance != hooked) Hook(CadSession.Instance);
        }

        // Free driving between laps (RcTransmitter.FreeDrive ticks the truck while the brain is idle) has no run to end and
        // re-place it, so a truck that rolled over there is set back on its wheels where it lies. A lap's or the Autopilot's
        // roll-over is the run's crash: the brain ends that run and puts it at the start itself.
        void RightIfRolled(float now)
        {
            if (body == null || !body.RolledOver || brain == null || brain.State != Forge.Artifacts.SlalomBrain.Mode.Idle) { rolledAt = -1f; return; }
            if (rolledAt < 0f) { rolledAt = now; return; }
            if (now - rolledAt < RightAfterSeconds) return;
            rolledAt = -1f;
            body.Place(transform.position, transform.rotation);
        }

        void Hook(CadSession session)
        {
            if (hooked != null) hooked.ModelChanged -= OnCad;
            hooked = session;
            if (hooked != null) hooked.ModelChanged += OnCad;
        }

        // A changed CAD truck: its parameters become the live design (one Changed for all of them).
        void OnCad(CadUpdate update)
        {
            var info = update?.info;
            if (info == null || info.part != "truck" || info.parameters == null) return;
            if (info.source == "open" || info.source == "startup" || !info.previousVersion.HasValue || info.version == lastCadVersion) return;
            lastCadVersion = info.version;
            int set = 0;
            foreach (var pair in info.parameters)
            {
                if (TruckDesign.IndexOf(pair.Key) < 0 || pair.Value == null) continue;
                try
                {
                    TruckDesignStore.Set(pair.Key, System.Convert.ToSingle(pair.Value, System.Globalization.CultureInfo.InvariantCulture), false);
                    set++;
                }
                catch (System.Exception) { }
            }
            if (set > 0) TruckDesignStore.Raise();
        }

        /// <summary>Applies the current design to this truck now.</summary>
        public void Apply()
        {
            if (body == null) body = GetComponent<ExampleTruckBody>();
            if (shell == null) shell = GetComponentInChildren<TruckShell>(true);
            if (brain == null) brain = GetComponent<Forge.Artifacts.SlalomBrain>();
            var design = TruckDesignStore.Current;
            if (body != null) body.ApplyDesign(design.Derive());
            if (shell != null) shell.SetDesign(design);
        }
    }
}
