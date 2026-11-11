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
        public const int HostRuntime = 6;  // 2: room_hoops. 3: arrays from data tables. 4: gltf, timeline, animate. 5: versions widget. 6: pendulum solver, line through

        public const int MaxNodes = 512, MaxZones = 64, MaxRules = 64, MaxSceneChars = 256 * 1024;
        public const int MaxBodies = 16, MaxTrailPoints = 2048;
        public const int MaxArrayCopies = 256, MaxSceneCopies = 1024;
        public const int MaxAssetBytes = 8 * 1024 * 1024, MaxTextureSize = 2048, MaxKeys = 64;

        /// <summary>What an <c>animate</c> rule can move, and the value each key holds.</summary>
        public static readonly Dictionary<string, string> AnimateProperties = new Dictionary<string, string>
        {
            { "pos", "[x, y, z]" }, { "rot", "[x, y, z]" }, { "scale", "[x, y, z] or a number" },
            { "visible", "true or false" }, { "color", "#RRGGBB" },
        };

        public static readonly HashSet<string> TimelineActions = new HashSet<string> { "play", "pause", "restart" };

        public static readonly HashSet<string> NodeKinds = new HashSet<string>
            { "primitive", "text", "panel", "array", "zone", "gltf", "line", "timeline", "sim" };

        /// <summary>Kinds the contract reserves but refuses in Phase 1.</summary>
        public static readonly HashSet<string> ReservedKinds = new HashSet<string> { "camera_rig" };

        public static readonly HashSet<string> Shapes = new HashSet<string>
            { "box", "sphere", "cylinder", "capsule", "cone", "plane" };

        public static readonly HashSet<string> Widgets = new HashSet<string> { "stat", "runs", "versions", "button", "slider", "note" };
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
            { "pendulum", new[] { "time", "energy_drift_pct", "flips", "angle_1", "angle_2" } },
        };

        /// <summary>Generator types and the slots each one fills, as <c>&lt;id&gt;.&lt;slot&gt;</c>.</summary>
        public static readonly Dictionary<string, string[]> Generators = new Dictionary<string, string[]>
        {
            { "floor_slalom", new[] { "cones", "gates", "start", "finish" } },
            { "room_hoops", new[] { "hoops", "start", "finish" } },
        };

        /// <summary>Slots that hold one pose, so a node can stand on them with <c>at</c>. The others are lists, for arrays.</summary>
        public static readonly HashSet<string> SingletonSlots = new HashSet<string> { "start", "finish" };

        /// <summary>Input types this host builds a control for. The contract also names boolean and choice (not built yet).</summary>
        public static readonly HashSet<string> BuiltInputTypes = new HashSet<string> { "number", "integer" };

        public static readonly HashSet<string> RunVerbs = new HashSet<string> { "arm", "reset" };
        public static readonly HashSet<string> ZoneEvents = new HashSet<string> { "enter", "exit" };

        /// <summary>The values each permission takes (C-12 section 8). network and connectors are lists, checked apart.</summary>
        public static readonly Dictionary<string, string[]> PermissionValues = new Dictionary<string, string[]>
        {
            { "storage", new[] { "artifact-local" } }, { "write_project", new[] { "none", "confirm" } },
            { "room", new[] { "none", "read" } }, { "things", new[] { "none", "read" } },
        };

        /// <summary>
        /// The keys each object takes (C-12 sections 2 to 5, 7 and 14). A key outside its table is <c>unknown_field</c>:
        /// the host would ignore it, so the agent would think it said something it didn't. Keys are listed in the order a
        /// hint shows them.
        /// </summary>
        public static class Keys
        {
            public static readonly string[] Manifest = { "schema", "id", "title", "version", "kind", "category", "template", "links", "runtime", "scene", "script", "inputs", "scenarios", "data", "assets", "permissions", "outputs" };
            public static readonly string[] Links = { "project", "project_version", "repo", "thing" };
            public static readonly string[] Runtime = { "type", "min_runtime" };
            public static readonly string[] Input = { "name", "type", "min", "max", "default", "label", "options" };
            public static readonly string[] Scenario = { "id", "seed", "params" };
            public static readonly string[] Data = { "name", "source", "snapshot" };
            public static readonly string[] Asset = { "path", "sha256", "license" };
            public static readonly string[] Output = { "name", "type", "unit", "label" };

            public static readonly string[] Scene = { "schema", "space", "generators", "nodes", "rules", "run", "view" };
            public static readonly string[] View = { "radius" };
            public static readonly string[] Generator = { "id", "type", "params" };
            public static readonly Dictionary<string, string[]> GeneratorParams = new Dictionary<string, string[]>
            {
                { "floor_slalom", new[] { "gates", "spacing_m", "min_spacing_m", "min_gates", "lane_width_m", "clearance_m", "around", "narrow_lane_width_m", "tight_spacing_m" } },
                { "room_hoops", new[] { "hoops", "spacing_m", "min_spacing_m", "min_hoops", "diameter_m", "clearance_m", "height_m", "tight_spacing_m" } },
            };

            /// <summary>Every node kind but panel and timeline takes these, then its own.</summary>
            public static readonly string[] NodeCommon = { "id", "kind", "parent", "pos", "rot", "scale", "at", "visible" };
            public static readonly Dictionary<string, string[]> NodeOwn = new Dictionary<string, string[]>
            {
                { "primitive", new[] { "shape", "size", "color", "collider", "glow" } },
                { "text", new[] { "text", "height", "color", "billboard" } },
                { "array", new[] { "source", "template", "map" } },
                { "zone", new[] { "size" } },
                { "gltf", new[] { "asset" } },
                { "line", new[] { "points", "source", "follow", "through", "width", "color", "length" } },
                { "sim", new[] { "solver", "bodies", "rate", "params" } },
            };
            /// <summary>Kinds with a closed list: a panel sits beside the scene, a timeline is only a clock.</summary>
            public static readonly Dictionary<string, string[]> NodeOnly = new Dictionary<string, string[]>
            {
                { "panel", new[] { "id", "kind", "title", "widgets", "pos", "visible" } },
                { "timeline", new[] { "id", "kind", "duration_s", "loop", "autoplay" } },
            };
            /// <summary>An array copies its template, so the template has no id, parent or at of its own.</summary>
            public static readonly Dictionary<string, string[]> ArrayTemplate = new Dictionary<string, string[]>
            {
                { "primitive", new[] { "kind", "shape", "size", "color", "collider", "glow", "pos", "rot" } },
                { "zone", new[] { "kind", "size", "pos", "rot" } },
            };
            public static readonly string[] TableSource = { "table", "grid" };
            public static readonly string[] Grid = { "columns", "gap_m", "base" };
            public static readonly string[] Map = { "height", "width", "color" };
            public static readonly string[] MapEntry = { "column", "range" };

            public static readonly Dictionary<string, string[]> Bodies = new Dictionary<string, string[]>
            {
                { "n_body", new[] { "node", "mass", "pos", "vel" } },
                { "pendulum", new[] { "node", "mass", "length", "angle", "omega" } },
            };
            public static readonly Dictionary<string, string[]> SimParams = new Dictionary<string, string[]>
            {
                { "n_body", new[] { "g", "step", "softening", "velocity_scale", "bound_radius" } },
                { "pendulum", new[] { "g", "step", "damping", "angle_offset" } },
            };

            public static readonly Dictionary<string, string[]> Widget = new Dictionary<string, string[]>
            {
                { "stat", new[] { "type", "metric", "label", "unit", "decimals" } },
                { "runs", new[] { "type", "columns", "limit" } },
                { "versions", new[] { "type" } },
                { "button", new[] { "type", "id", "label" } },
                { "slider", new[] { "type", "input" } },
                { "note", new[] { "type", "text" } },
            };

            public static readonly Dictionary<string, string[]> Rule = new Dictionary<string, string[]>
            {
                { "metric", new[] { "type", "name", "op" } },
                { "on_input", new[] { "type", "input", "do" } },
                { "on_zone", new[] { "type", "zone", "event", "do" } },
                { "animate", new[] { "type", "target", "property", "keys", "timeline" } },
            };
            /// <summary>What a metric rule adds for its op.</summary>
            public static readonly Dictionary<string, string[]> MetricOp = new Dictionary<string, string[]>
            {
                { "elapsed", new string[0] }, { "zone_count", new[] { "zones", "ordered" } }, { "hit_count", new[] { "nodes" } },
                { "max", new[] { "probe" } }, { "min", new[] { "probe" } }, { "mean", new[] { "probe" } }, { "last", new[] { "probe" } },
                { "sim", new[] { "sim", "value" } },
            };
            /// <summary>An action holds exactly one verb (set, run, timeline or sim) and what that verb needs.</summary>
            public static readonly string[] Action = { "set", "value", "run", "timeline", "sim", "action" };

            public static readonly string[] Run = { "start", "finish", "fail" };
            public static readonly string[] RunStart = { "zone" };
            public static readonly string[] RunFinish = { "zone", "needs", "else_reason" };
            public static readonly string[] Need = { "metric", "min" };
            public static readonly string[] Fail = { "reason", "probe", "below", "for_s", "timeout_s" };
        }
    }
}
