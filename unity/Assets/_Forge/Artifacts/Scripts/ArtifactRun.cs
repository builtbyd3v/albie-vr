using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    public enum ArtifactRunState { Idle, Armed, Running, Finished }

    /// <summary>
    /// Scores a scene's <c>run</c> block (C-12 section 5). States: idle → armed (Thing bound, metrics zeroed) →
    /// running (entered start) → finished. Ticked from FixedUpdate with no allocations; the host writes the record
    /// off that step via <see cref="PendingRecord"/>.
    /// </summary>
    public sealed class ArtifactRun
    {
        const int OpElapsed = 0, OpZoneCount = 1, OpHitCount = 2, OpMax = 3, OpMin = 4, OpMean = 5, OpLast = 6;
        const int ProbeSpeed = 0, ProbeUpright = 1, ProbeHeight = 2;

        struct ZoneWatch
        {
            public string Id;
            public ArtifactZone[] Live;
            public Bounds[] Boxes;
            public bool[] Inside;
            public bool Ordered, IsStart, IsFinish;
            public int Copies, Count, Next;

            public bool Contains(int i, Vector3 world)
            {
                if (Live != null) return Live[i] != null && Live[i].Contains(world);
                return Boxes[i].Contains(world);
            }
        }

        struct HitWatch
        {
            public string Id;
            public Collider[] Live;
            public Bounds[] Boxes;
            public bool[] Inside;
            public int Copies, Count;

            public bool Hits(int i, Vector3 world, float radius)
            {
                if (Live != null)
                {
                    var c = Live[i];
                    if (c == null) return false;
                    // A touch: the Thing within its radius of the collider's surface. ClosestPoint is exact for
                    // primitives and convex meshes; the bounds grown by the radius were up to 41% too generous beside
                    // a cone and counted a clean weave 0.12 m off every cone as 4 hits. A concave mesh has no
                    // ClosestPoint, so it keeps the bounds.
                    if (c is MeshCollider mesh && !mesh.convex)
                    {
                        var b = c.bounds;
                        b.Expand(radius * 2f);
                        return b.Contains(world);
                    }
                    return (c.ClosestPoint(world) - world).sqrMagnitude <= radius * radius;
                }
                var box = Boxes[i];
                box.Expand(radius * 2f);
                return box.Contains(world);
            }
        }

        struct Metric
        {
            public string Name;
            public int Op, Watch, Probe, Slot;
            public int[] Watches;  // hit_count over a list of nodes: summed
            public bool Integer;
            public double Acc, Samples;
        }

        struct Fail
        {
            public int Probe;
            public float Below, ForS, TimeoutS, Held;
            public string Reason;
        }

        struct Need
        {
            public int Metric;
            public double Min;
        }

        ZoneWatch[] zones;
        HitWatch[] hits;
        Metric[] metrics;
        Fail[] fails;
        Need[] needs;
        int zoneN, hitN, metricN, failN, needN;
        string startId, finishId, elseReason, artifactId, scenario;
        int artifactVersion, seed;
        JObject layout, inputs;
        ArtifactMetrics board;
        IArtifactThing thing;
        float elapsed;

        public ArtifactRunState State { get; private set; } = ArtifactRunState.Idle;
        public string Outcome { get; private set; }
        public string Reason { get; private set; }
        public string RunId { get; private set; }
        public string StartedAt { get; private set; }
        /// <summary>The record to write, or null. The host takes it from Update, never from FixedUpdate.</summary>
        public JObject PendingRecord { get; private set; }
        public ArtifactMetrics Board => board;

        /// <summary>A zone was entered (true) or left. Used to fire <c>on_zone</c> rules.</summary>
        public event Action<string, bool> ZoneChanged;
        /// <summary>Raised when the run changes state. Payload is <see cref="Event"/>.</summary>
        public event Action StateChanged;

        public void SetThing(IArtifactThing next) => thing = next;

        /// <summary>Bind from a built scene: live zones and colliders, no allocations after this.</summary>
        public void Bind(JObject manifest, JObject scene, JObject layout, ArtifactInputs liveInputs, IArtifactThing bound, ArtifactMetrics liveBoard, ArtifactBuild build)
        {
            Parse(manifest, scene, layout, liveInputs, bound, liveBoard);
            if (scene["nodes"] is JArray nodes)
                foreach (JObject n in nodes)
                {
                    string id = n.Value<string>("id"), kind = n.Value<string>("kind");
                    if (kind == "zone" && build.Nodes.TryGetValue(id, out var go) && go.TryGetComponent<ArtifactZone>(out var z))
                        AttachZone(id, new[] { z }, null);
                    else if (kind == "array" && build.Arrays.TryGetValue(id, out var copies))
                    {
                        string template = n["template"]?.Value<string>("kind");
                        if (template == "zone")
                        {
                            var list = new ArtifactZone[copies.Count];
                            for (int i = 0; i < copies.Count; i++) list[i] = copies[i].GetComponent<ArtifactZone>();
                            AttachZone(id, list, null);
                        }
                        else if (template == "primitive" && n["template"]?.Value<bool?>("collider") == true)
                        {
                            var list = new Collider[copies.Count];
                            for (int i = 0; i < copies.Count; i++) list[i] = copies[i].GetComponentInChildren<Collider>();
                            AttachHit(id, list, null);
                        }
                    }
                }
            SlotCounts(build.Slots);
        }

        /// <summary>Bind from boxes, for headless checks with no scene.</summary>
        public void Bind(JObject manifest, JObject scene, JObject layout, ArtifactInputs liveInputs, IArtifactThing bound, ArtifactMetrics liveBoard,
                         Dictionary<string, Bounds[]> zoneBoxes, Dictionary<string, Bounds[]> hitBoxes, Dictionary<string, int> slots)
        {
            Parse(manifest, scene, layout, liveInputs, bound, liveBoard);
            if (zoneBoxes != null)
                foreach (var pair in zoneBoxes) AttachZone(pair.Key, null, pair.Value);
            if (hitBoxes != null)
                foreach (var pair in hitBoxes) AttachHit(pair.Key, null, pair.Value);
            SlotCounts(slots);
        }

        void Parse(JObject manifest, JObject scene, JObject layoutRecord, ArtifactInputs liveInputs, IArtifactThing bound, ArtifactMetrics liveBoard)
        {
            thing = bound;
            board = liveBoard ?? new ArtifactMetrics();
            layout = layoutRecord;
            artifactId = manifest.Value<string>("id");
            artifactVersion = manifest.Value<int?>("version") ?? 1;
            scenario = layoutRecord?.Value<string>("scenario") ?? (scene["run"] != null ? "default" : null);
            if (string.IsNullOrEmpty(scenario)) scenario = (manifest["scenarios"] as JArray)?[0]?.Value<string>("id") ?? "default";
            seed = layoutRecord?.Value<int?>("seed") ?? (int)((manifest["scenarios"] as JArray)?[0]?.Value<int?>("seed") ?? 0);
            inputs = liveInputs != null ? liveInputs.Snapshot() : new JObject();
            startId = scene["run"]?["start"]?.Value<string>("zone");
            finishId = scene["run"]?["finish"]?.Value<string>("zone");
            elseReason = scene["run"]?["finish"]?.Value<string>("else_reason") ?? "missed_gate";
            zones = new ZoneWatch[64];
            hits = new HitWatch[64];
            metrics = new Metric[64];
            fails = new Fail[8];
            needs = new Need[8];
            zoneN = hitN = metricN = failN = needN = 0;
            State = ArtifactRunState.Idle;
            Outcome = Reason = RunId = StartedAt = null;
            PendingRecord = null;
            elapsed = 0f;

            if (scene["rules"] is JArray rules)
                foreach (var rule in rules)
                {
                    if (rule.Value<string>("type") != "metric") continue;
                    string op = rule.Value<string>("op");
                    if (op == "sim") continue;
                    var m = new Metric
                    {
                        Name = rule.Value<string>("name"),
                        Op = OpOf(op),
                        Watch = -1,
                        Probe = ProbeOf(rule.Value<string>("probe")),
                        Slot = board.Slot(rule.Value<string>("name")),
                        Integer = op == "zone_count" || op == "hit_count",
                        Acc = double.NaN,
                    };
                    if (op == "zone_count") m.Watch = EnsureZone(rule.Value<string>("zones"), rule.Value<bool?>("ordered") == true);
                    if (op == "hit_count" && rule["nodes"] is JArray targets)
                    {
                        m.Watches = new int[targets.Count];
                        for (int t = 0; t < targets.Count; t++) m.Watches[t] = EnsureHit((string)targets[t]);
                    }
                    else if (op == "hit_count") m.Watch = EnsureHit(rule.Value<string>("nodes"));
                    metrics[metricN++] = m;
                }

            EnsureZone(startId, false);
            EnsureZone(finishId, false);
            Mark(startId, true, false);
            Mark(finishId, false, true);

            if (scene["run"]?["finish"]?["needs"] is JArray needList)
                foreach (var need in needList)
                {
                    int mi = MetricIndex(need.Value<string>("metric"));
                    double min;
                    if (need["min"]?.Type == JTokenType.String && ((string)need["min"]).StartsWith("@"))
                        min = double.NaN; // filled in SlotCounts
                    else min = need.Value<double?>("min") ?? 0;
                    needs[needN++] = new Need { Metric = mi, Min = min };
                }
            if (scene["run"]?["fail"] is JArray failList)
                foreach (var fail in failList)
                    fails[failN++] = new Fail
                    {
                        Probe = ProbeOf(fail.Value<string>("probe")),
                        Below = fail.Value<float?>("below") ?? float.NaN,
                        ForS = fail.Value<float?>("for_s") ?? 0f,
                        TimeoutS = fail.Value<float?>("timeout_s") ?? 0f,
                        Reason = fail.Value<string>("reason"),
                    };

            // Finish needs that named @slot get a placeholder index in the needs array; SlotCounts fills Min.
            pendingSlotNeeds = new string[needN];
            int ni = 0;
            if (scene["run"]?["finish"]?["needs"] is JArray needList2)
                foreach (var need in needList2)
                    pendingSlotNeeds[ni++] = need["min"]?.Type == JTokenType.String && ((string)need["min"]).StartsWith("@")
                        ? ((string)need["min"]).Substring(1) : null;
        }

        string[] pendingSlotNeeds;

        void SlotCounts(Dictionary<string, List<Pose>> slots)
        {
            var counts = new Dictionary<string, int>();
            if (slots != null) foreach (var pair in slots) counts[pair.Key] = pair.Value.Count;
            SlotCounts(counts);
        }

        void SlotCounts(Dictionary<string, int> slots)
        {
            if (pendingSlotNeeds == null) return;
            for (int i = 0; i < needN; i++)
            {
                string slot = pendingSlotNeeds[i];
                if (slot == null) continue;
                int n = 0;
                if (slots != null) slots.TryGetValue(slot, out n);
                needs[i].Min = n;
            }
        }

        int EnsureZone(string id, bool ordered)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < zoneN; i++)
                if (zones[i].Id == id)
                {
                    if (ordered) zones[i].Ordered = true;
                    return i;
                }
            zones[zoneN] = new ZoneWatch { Id = id, Ordered = ordered, Copies = 0 };
            return zoneN++;
        }

        int EnsureHit(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < hitN; i++) if (hits[i].Id == id) return i;
            hits[hitN] = new HitWatch { Id = id };
            return hitN++;
        }

        void AttachZone(string id, ArtifactZone[] live, Bounds[] boxes)
        {
            int i = EnsureZone(id, false);
            int copies = live != null ? live.Length : boxes.Length;
            zones[i].Live = live;
            zones[i].Boxes = boxes;
            zones[i].Copies = copies;
            zones[i].Inside = new bool[copies];
        }

        void AttachHit(string id, Collider[] live, Bounds[] boxes)
        {
            int i = EnsureHit(id);
            int copies = live != null ? live.Length : boxes.Length;
            hits[i].Live = live;
            hits[i].Boxes = boxes;
            hits[i].Copies = copies;
            hits[i].Inside = new bool[copies];
        }

        void Mark(string id, bool start, bool finish)
        {
            if (id == null) return;
            for (int i = 0; i < zoneN; i++)
                if (zones[i].Id == id)
                {
                    if (start) zones[i].IsStart = true;
                    if (finish) zones[i].IsFinish = true;
                }
        }

        /// <summary>Arms a run. Returns null, or why it couldn't (no Thing, no run block).</summary>
        public string Arm()
        {
            if (startId == null) return "no_run: this scene isn't scored";
            if (thing == null || !thing.Bound) return "no_thing: bind a Thing to score a run";
            if (State == ArtifactRunState.Running) Finish("aborted", null);
            BeginArmed();
            return null;
        }

        /// <summary><c>run: reset</c>: back to armed. A running attempt is aborted first.</summary>
        public void Reset()
        {
            if (State == ArtifactRunState.Running) Finish("aborted", null);
            if (startId == null) return;
            BeginArmed();
        }

        public void Abort()
        {
            if (State == ArtifactRunState.Armed || State == ArtifactRunState.Running) Finish("aborted", null);
        }

        public JObject TakeRecord()
        {
            var record = PendingRecord;
            PendingRecord = null;
            return record;
        }

        public JObject Event()
        {
            return new JObject
            {
                ["artifact_id"] = artifactId,
                ["run_id"] = RunId,
                ["state"] = StateName(State),
                ["outcome"] = Outcome != null ? (JToken)Outcome : JValue.CreateNull(),
                ["reason"] = Reason != null ? (JToken)Reason : JValue.CreateNull(),
                ["metrics"] = State == ArtifactRunState.Finished ? Snapshot() : (JToken)JValue.CreateNull(),
            };
        }

        void BeginArmed()
        {
            // A course built this frame isn't in the physics world until the next simulation step, and runs tick in
            // FixedUpdate before it: hit tests against its colliders would see every cone where it was made.
            Physics.SyncTransforms();
            RunId = ArtifactRuns.NextId(artifactId);
            Outcome = Reason = StartedAt = null;
            elapsed = 0f;
            for (int i = 0; i < zoneN; i++)
            {
                zones[i].Count = 0;
                zones[i].Next = 0;
                var inside = zones[i].Inside;
                if (inside != null) for (int k = 0; k < inside.Length; k++) inside[k] = false;
            }
            for (int i = 0; i < hitN; i++)
            {
                hits[i].Count = 0;
                var inside = hits[i].Inside;
                if (inside != null) for (int k = 0; k < inside.Length; k++) inside[k] = false;
            }
            for (int i = 0; i < metricN; i++)
            {
                metrics[i].Acc = double.NaN;
                metrics[i].Samples = 0;
            }
            for (int i = 0; i < failN; i++) fails[i].Held = 0f;
            board.Reset();
            State = ArtifactRunState.Armed;
            PublishTarget();
            StateChanged?.Invoke();
        }

        /// <summary>One physics step. No allocations.</summary>
        public void Tick(float dt)
        {
            TickZones(dt);
            PublishTarget();
        }

        /// <summary>
        /// The target of the Thing's <c>course.bearing</c> sensor (C-19 section 2, proposed 2026-11-06): the centre of
        /// the next zone in an ordered set (the next gate), then the finish. Cleared when no run is armed or running.
        /// Without it a learned driver can't tell which side of a cone the next gate is on (PROJECT_CONTEXT 16.8).
        /// </summary>
        void PublishTarget()
        {
            if (State != ArtifactRunState.Armed && State != ArtifactRunState.Running) { Forge.Controllers.CourseBeacon.Clear(); return; }
            for (int z = 0; z < zoneN; z++)
                if (zones[z].Ordered && zones[z].Next < zones[z].Copies) { Forge.Controllers.CourseBeacon.Set(ZoneCentre(z, zones[z].Next)); return; }
            for (int z = 0; z < zoneN; z++)
                if (zones[z].IsFinish && zones[z].Copies > 0) { Forge.Controllers.CourseBeacon.Set(ZoneCentre(z, 0)); return; }
            Forge.Controllers.CourseBeacon.Clear();
        }

        Vector3 ZoneCentre(int z, int i)
        {
            var live = zones[z].Live;
            if (live != null) return live[i] != null ? live[i].transform.position : Vector3.zero;
            return zones[z].Boxes[i].center;
        }

        void TickZones(float dt)
        {
            if (State != ArtifactRunState.Armed && State != ArtifactRunState.Running) return;
            if (thing == null || !thing.Bound) return;
            var pos = thing.Position;
            float radius = thing.Radius;

            for (int z = 0; z < zoneN; z++)
            {
                int copies = zones[z].Copies;
                for (int i = 0; i < copies; i++)
                {
                    bool now = zones[z].Contains(i, pos);
                    bool was = zones[z].Inside[i];
                    if (now == was) continue;
                    zones[z].Inside[i] = now;
                    if (now) EnterZone(z, i);
                    else ZoneChanged?.Invoke(zones[z].Id, false);
                }
            }
            if (State != ArtifactRunState.Running) return;

            for (int h = 0; h < hitN; h++)
            {
                int copies = hits[h].Copies;
                for (int i = 0; i < copies; i++)
                {
                    bool now = hits[h].Hits(i, pos, radius);
                    bool was = hits[h].Inside[i];
                    if (now == was) continue;
                    hits[h].Inside[i] = now;
                    if (now) hits[h].Count++;
                }
            }

            elapsed += dt;
            UpdateMetrics();
            CheckFail(dt);
        }

        void EnterZone(int z, int index)
        {
            if (zones[z].IsStart && State == ArtifactRunState.Armed)
            {
                State = ArtifactRunState.Running;
                StartedAt = Now();
                elapsed = 0f;
                StateChanged?.Invoke();
            }
            if (State != ArtifactRunState.Running)
            {
                ZoneChanged?.Invoke(zones[z].Id, true);
                return;
            }
            if (zones[z].Ordered)
            {
                if (index == zones[z].Next)
                {
                    zones[z].Next++;
                    zones[z].Count++;
                }
            }
            else zones[z].Count++;
            ZoneChanged?.Invoke(zones[z].Id, true);
            if (zones[z].IsFinish) CheckFinish();
        }

        void CheckFinish()
        {
            if (State != ArtifactRunState.Running) return;
            UpdateMetrics();
            bool ok = true;
            for (int i = 0; i < needN; i++)
            {
                int mi = needs[i].Metric;
                double v = mi >= 0 ? MetricValue(mi) : double.NaN;
                if (double.IsNaN(v) || v + 1e-6 < needs[i].Min) { ok = false; break; }
            }
            Finish(ok ? "completed" : "failed", ok ? null : elseReason);
        }

        void CheckFail(float dt)
        {
            if (State != ArtifactRunState.Running) return;
            for (int i = 0; i < failN; i++)
            {
                if (fails[i].TimeoutS > 0f && elapsed >= fails[i].TimeoutS)
                {
                    Finish("failed", fails[i].Reason);
                    return;
                }
                if (fails[i].Probe < 0) continue;
                float v = ReadProbe(fails[i].Probe);
                if (!float.IsNaN(fails[i].Below) && v < fails[i].Below)
                {
                    fails[i].Held += dt;
                    if (fails[i].Held >= fails[i].ForS)
                    {
                        Finish("failed", fails[i].Reason);
                        return;
                    }
                }
                else fails[i].Held = 0f;
            }
        }

        void UpdateMetrics()
        {
            for (int i = 0; i < metricN; i++)
            {
                double v = Sample(i);
                metrics[i].Acc = v;
                if (!double.IsNaN(v)) board.Set(metrics[i].Slot, v);
            }
        }

        double MetricValue(int i)
        {
            switch (metrics[i].Op)
            {
                case OpElapsed: return elapsed;
                case OpZoneCount: return metrics[i].Watch >= 0 ? zones[metrics[i].Watch].Count : 0;
                case OpHitCount: return HitCount(i);
                default: return metrics[i].Acc;
            }
        }

        double Sample(int i)
        {
            switch (metrics[i].Op)
            {
                case OpElapsed: return elapsed;
                case OpZoneCount: return metrics[i].Watch >= 0 ? zones[metrics[i].Watch].Count : 0;
                case OpHitCount: return HitCount(i);
                case OpLast:
                case OpMax:
                case OpMin:
                case OpMean:
                    if (State != ArtifactRunState.Running || metrics[i].Probe < 0) return metrics[i].Acc;
                    double sample = ReadProbe(metrics[i].Probe);
                    if (metrics[i].Op == OpLast) return sample;
                    if (metrics[i].Op == OpMax) return double.IsNaN(metrics[i].Acc) ? sample : Math.Max(metrics[i].Acc, sample);
                    if (metrics[i].Op == OpMin) return double.IsNaN(metrics[i].Acc) ? sample : Math.Min(metrics[i].Acc, sample);
                    double n = metrics[i].Samples + 1.0;
                    double prev = double.IsNaN(metrics[i].Acc) ? 0 : metrics[i].Acc * metrics[i].Samples;
                    metrics[i].Samples = n;
                    return (prev + sample) / n;
                default: return double.NaN;
            }
        }

        int HitCount(int i)
        {
            var list = metrics[i].Watches;
            if (list == null) return metrics[i].Watch >= 0 ? hits[metrics[i].Watch].Count : 0;
            int n = 0;
            for (int k = 0; k < list.Length; k++) n += hits[list[k]].Count;
            return n;
        }

        float ReadProbe(int probe)
        {
            switch (probe)
            {
                case ProbeSpeed: return thing.SpeedMs;
                case ProbeUpright: return thing.Upright;
                case ProbeHeight: return thing.HeightM;
                default: return float.NaN;
            }
        }

        void Finish(string outcome, string reason)
        {
            if (State == ArtifactRunState.Finished || State == ArtifactRunState.Idle) return;
            State = ArtifactRunState.Finished;
            Outcome = outcome;
            Reason = reason;
            PendingRecord = Record();
            PublishTarget();
            StateChanged?.Invoke();
        }

        JObject Record()
        {
            var thingObj = thing == null ? (JToken)JValue.CreateNull() : new JObject { ["name"] = thing.Name, ["version"] = thing.Version };
            var controller = thing == null ? (JToken)JValue.CreateNull() : new JObject
            {
                ["name"] = thing.ControllerName, ["kind"] = thing.ControllerKind, ["version"] = thing.ControllerVersion,
            };
            return new JObject
            {
                ["schema"] = ArtifactRuns.Schema,
                ["run_id"] = RunId,
                ["artifact_id"] = artifactId,
                ["artifact_version"] = artifactVersion,
                ["scenario"] = scenario,
                ["seed"] = seed,
                ["layout_id"] = layout != null ? layout["layout_id"] : JValue.CreateNull(),
                ["project_id"] = JValue.CreateNull(),
                ["project_version"] = JValue.CreateNull(),
                ["thing"] = thingObj,
                ["controller"] = controller,
                ["inputs"] = inputs != null ? inputs.DeepClone() : new JObject(),
                ["outcome"] = Outcome,
                ["reason"] = Reason != null ? (JToken)Reason : JValue.CreateNull(),
                ["metrics"] = Snapshot(),
                ["started_at"] = StartedAt != null ? (JToken)StartedAt : JValue.CreateNull(),
                ["ended_at"] = Now(),
            };
        }

        public void StampProject(string projectId, int? projectVersion)
        {
            if (PendingRecord == null) return;
            PendingRecord["project_id"] = projectId != null ? (JToken)projectId : JValue.CreateNull();
            PendingRecord["project_version"] = projectVersion.HasValue ? (JToken)projectVersion.Value : JValue.CreateNull();
        }

        JObject Snapshot()
        {
            var o = new JObject();
            for (int i = 0; i < metricN; i++)
            {
                double v = board != null ? board.Value(metrics[i].Name) : metrics[i].Acc;
                if (double.IsNaN(v)) o[metrics[i].Name] = JValue.CreateNull();
                else if (metrics[i].Integer) o[metrics[i].Name] = (int)Math.Round(v);
                else o[metrics[i].Name] = Math.Round(v, 3);
            }
            return o;
        }

        int MetricIndex(string name)
        {
            if (name == null) return -1;
            for (int i = 0; i < metricN; i++) if (metrics[i].Name == name) return i;
            return -1;
        }

        static int OpOf(string op)
        {
            switch (op)
            {
                case "elapsed": return OpElapsed;
                case "zone_count": return OpZoneCount;
                case "hit_count": return OpHitCount;
                case "max": return OpMax;
                case "min": return OpMin;
                case "mean": return OpMean;
                case "last": return OpLast;
                default: return -1;
            }
        }

        static int ProbeOf(string probe)
        {
            switch (probe)
            {
                case "speed_ms": return ProbeSpeed;
                case "upright": return ProbeUpright;
                case "height_m": return ProbeHeight;
                default: return -1;
            }
        }

        static string StateName(ArtifactRunState s)
        {
            switch (s)
            {
                case ArtifactRunState.Armed: return "armed";
                case ArtifactRunState.Running: return "running";
                case ArtifactRunState.Finished: return "finished";
                default: return "idle";
            }
        }

        static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}
