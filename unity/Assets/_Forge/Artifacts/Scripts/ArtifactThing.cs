using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The probes a run reads from the bound Thing (C-12 section 4 and question 1). Person 2's Thing runtime can
    /// implement this; until it does, <see cref="ArtifactThing"/> is the stand-in the slalom sandbox drives.
    /// Values come from the body, not from C-19 sensor channels, so a truck with no speed sensor still gets metrics.
    /// </summary>
    public interface IArtifactThing
    {
        string Name { get; }
        int Version { get; }
        string ControllerName { get; }
        string ControllerKind { get; }
        int ControllerVersion { get; }
        Vector3 Position { get; }
        float SpeedMs { get; }
        /// <summary>Dot of the body's up and world up, -1 to 1. Same meaning as C-19's <c>&lt;id&gt;.upright</c>.</summary>
        float Upright { get; }
        float HeightM { get; }
        /// <summary>Half-width used for cone hits, metres. The mock truck is 0.22 m wide.</summary>
        float Radius { get; }
        bool Bound { get; }
    }

    /// <summary>
    /// A kinematic stand-in Thing for sandboxes and checks. Move it with <see cref="Place"/> or by transforming it;
    /// speed is the distance travelled since the last Update.
    /// </summary>
    public sealed class ArtifactThing : MonoBehaviour, IArtifactThing
    {
        public string ThingName = "truck";
        public int ThingVersion = 2;
        public string ControllerName = "truck_manual";
        public string ControllerKind = "manual";
        public int ControllerVersion = 1;
        public float Radius = 0.12f;

        Vector3 last;
        float speed;
        bool seeded;

        string IArtifactThing.Name => ThingName;
        int IArtifactThing.Version => ThingVersion;
        string IArtifactThing.ControllerName => ControllerName;
        string IArtifactThing.ControllerKind => ControllerKind;
        int IArtifactThing.ControllerVersion => ControllerVersion;
        /// <summary>
        /// The body's own speed, for a body that moves in FixedUpdate (a physics truck): runs tick in FixedUpdate, and a
        /// per-frame position difference reads 0 on every frame without a physics step. Null: measured from the position.
        /// </summary>
        public System.Func<float> SpeedSource;

        /// <summary>
        /// How far above the transform (along its up) the run reads the Thing, metres. 0 for a stand-in whose transform is
        /// its centre; a body whose transform is at the floor sets its height, or it sits exactly on zones' bottom faces.
        /// </summary>
        public float ProbeHeight;

        public Vector3 Position => ProbeHeight == 0f ? transform.position : transform.position + transform.up * ProbeHeight;
        public float SpeedMs => SpeedSource != null ? SpeedSource() : speed;
        public float Upright => Vector3.Dot(transform.up, Vector3.up);
        public float HeightM => transform.position.y;
        float IArtifactThing.Radius => Radius;
        public bool Bound => isActiveAndEnabled;

        void OnEnable()
        {
            last = transform.position;
            seeded = true;
            speed = 0f;
        }

        void Update()
        {
            var now = transform.position;
            float dt = Time.deltaTime;
            speed = seeded && dt > 1e-6f ? (now - last).magnitude / dt : 0f;
            last = now;
            seeded = true;
        }

        /// <summary>Puts the Thing at a pose and zeroes its measured speed. Used when a run is armed.</summary>
        public void Place(Vector3 world, Quaternion rotation)
        {
            transform.SetPositionAndRotation(world, rotation);
            last = world;
            speed = 0f;
            seeded = true;
        }
    }
}
