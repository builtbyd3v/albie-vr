using System.Collections.Generic;

namespace Forge.Artifacts
{
    /// <summary>
    /// What the C-12 contract allows (docs/ARTIFACT_CONTRACT.md). This is the law the validator enforces. What this host
    /// can actually build today is a separate question, answered by <see cref="ArtifactNodes"/>.
    /// </summary>
    public static class ArtifactSchema
    {
        public const string Manifest = "albie.artifact/1";
        public const string Scene = "albie.scene/1";
        public const string Run = "albie.run/1";
        public const int HostRuntime = 1;

        public const int MaxNodes = 512, MaxZones = 64, MaxRules = 64, MaxSceneChars = 256 * 1024;
        public const int MaxBodies = 16, MaxTrailPoints = 2048;

        public static readonly HashSet<string> NodeKinds = new HashSet<string>
            { "primitive", "text", "panel", "array", "zone", "gltf", "line", "timeline", "sim" };

        /// <summary>Kinds the contract reserves but refuses in Phase 1.</summary>
        public static readonly HashSet<string> ReservedKinds = new HashSet<string> { "camera_rig" };

        public static readonly HashSet<string> Shapes = new HashSet<string>
            { "box", "sphere", "cylinder", "capsule", "cone", "plane" };

        public static readonly HashSet<string> Widgets = new HashSet<string> { "stat", "runs", "button", "slider", "note" };
        public static readonly HashSet<string> Rules = new HashSet<string> { "metric", "on_input", "on_zone", "animate" };
        public static readonly HashSet<string> MetricOps = new HashSet<string>
            { "elapsed", "zone_count", "hit_count", "max", "min", "mean", "last", "sim" };
        public static readonly HashSet<string> Probes = new HashSet<string> { "speed_ms", "upright", "height_m" };
        public static readonly HashSet<string> Spaces = new HashSet<string> { "room", "volume", "panel" };
        public static readonly HashSet<string> Kinds = new HashSet<string> { "project", "code", "standalone" };
        public static readonly HashSet<string> Categories = new HashSet<string> { "viz", "tool", "sim" };
        public static readonly HashSet<string> InputTypes = new HashSet<string> { "number", "integer", "boolean", "choice" };
        public static readonly HashSet<string> Permissions = new HashSet<string>
            { "network", "connectors", "storage", "write_project", "room", "things" };

        /// <summary>Solvers a <c>sim</c> node can name, and the values each one publishes to <c>metric</c> rules.</summary>
        public static readonly Dictionary<string, string[]> Solvers = new Dictionary<string, string[]>
        {
            { "n_body", new[] { "time", "energy_drift_pct", "closest_approach", "escaped" } },
        };

        /// <summary>Generator types and the slots each one fills, as <c>&lt;id&gt;.&lt;slot&gt;</c>.</summary>
        public static readonly Dictionary<string, string[]> Generators = new Dictionary<string, string[]>
        {
            { "floor_slalom", new[] { "cones", "gates", "start", "finish" } },
            { "room_hoops", new[] { "hoops", "start", "finish" } },
        };
    }
}
