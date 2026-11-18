using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// The flyable drone's body IS the project's quadcopter design: this loads the current version of the CAD library's
    /// <c>quadcopter</c> design (the same GLB and metadata the CAD viewer shows, through <see cref="CadModel"/> and glTFast)
    /// under <see cref="DroneBody.Visual"/>, in place of <see cref="DroneShell"/>'s primitives, and takes the design's
    /// parameters into the flight model (<see cref="DroneFlightModel"/>: mass, thrust to weight, top speed, climb, turn
    /// rate and agility from arm count and length, props, motors, plate, battery and payload; see <see cref="Note"/>).
    ///
    /// Physics follows the parameter values themselves, not the mesh: a slider or preset asked for on this device
    /// (<see cref="CadSession.ParamsRequested"/>) retunes the drone at once, even while it flies on a course. In mock
    /// mode, where CAD only shows the nearest stored version, the drone still flies the values asked for (the mesh is the
    /// nearest version); a version reached any other way (undo, history, the hub's build, Albie's cad_set_params) flies
    /// that version's own values. Each change is one line of before/after numbers (<see cref="LastChange"/>) and Albie
    /// says what changed in flight (<see cref="LastSaid"/>).
    ///
    /// Where the design comes from, in order (re-checked once a second, nothing per frame):
    /// 1. <see cref="CadSession.Latest"/> when it is the quadcopter: the hub's or Albie's cloud's current version when the
    ///    session is live, the stored mock version when it is in mock mode. Every later quadcopter version the session
    ///    publishes (<see cref="CadSession.ModelChanged"/>: a slider, a preset, Albie's cad_set_params) replaces the body.
    /// 2. Else the newest stored quadcopter version in the CAD mock set (<see cref="CadMockSet"/>, Assets/_Forge/Mocks/cad,
    ///    which is not under Resources: it reaches a build because Main's CadViewer prefab references CadMocks.asset, so it is
    ///    loaded with the scene). Found on a CadSession, a CadViewer (active or not) or among the loaded assets.
    /// 3. Else nothing: the primitive shell stays (<see cref="Source"/> says so).
    /// The model is real size up to <see cref="MaxSpan"/> across, scaled down beyond that (the Drive window says by how much).
    /// On a new version the parts fly in and click together (<see cref="AssembleSeconds"/>), bottom plate first, props last.
    /// </summary>
    public sealed class DroneDesign : MonoBehaviour
    {
        public const string Part = "quadcopter";
        /// <summary>Widest it is shown, metres: a design bigger than this is scaled down to it.</summary>
        public const float MaxSpan = 0.45f;
        public const float AssembleSeconds = 1.6f;
        const float PollSeconds = 1f;

        /// <summary>Raised when the body or its flight factors change (ArtifactDrone retunes the body).</summary>
        public event Action Changed;

        public bool Loaded => model != null;
        public int Version { get; private set; }
        /// <summary>Where the body came from, in words: "CAD (live)", "CAD (mock mode)", "offline sample", or "no design: built-in shape".</summary>
        public string Source { get; private set; } = "no design: built-in shape";
        /// <summary>The design's half span at full size (metres, after the MaxSpan cap), or 0 with no design.</summary>
        public float RadiusFullSize { get; private set; }
        /// <summary>Shown size over real size (1 = real size).</summary>
        public float ShownScale { get; private set; } = 1f;
        public float AccelK { get; private set; } = 1f;
        public float BrakeK { get; private set; } = 1f;
        public float ClimbK { get; private set; } = 1f;
        public float YawK { get; private set; } = 1f;
        public float ThrustToWeight { get; private set; } = float.NaN;
        /// <summary>Top-speed and agility factors (1 = the reference design).</summary>
        public float SpeedK { get; private set; } = 1f;
        public float AgilityK { get; private set; } = 1f;
        /// <summary>True once the flight numbers come from a design's parameters (with or without its mesh).</summary>
        public bool HasFlight { get; private set; }
        /// <summary>The parameters the drone flies now, and their flight numbers.</summary>
        public DroneFlightModel.Spec Spec { get; private set; }
        public DroneFlightModel.Flight Flight { get; private set; }
        /// <summary>The last change as before/after numbers ("389 → 601 g · ... · turns 90 → 52 °/s"), or null.</summary>
        public string LastChange { get; private set; }
        /// <summary>What Albie last said about a change ("Longer arms: steadier, turns 42% slower."), or null.</summary>
        public string LastSaid { get; private set; }
        /// <summary>Raised after the flight numbers change from an earlier set (the change line, then what Albie says). The drone's brain listens.</summary>
        public event Action<string, string> FlightChanged;
        /// <summary>Seconds a slider asked for in mock mode still governs the stored version CAD then shows.</summary>
        public const float RequestHoldSeconds = 4f;
        /// <summary>One plain line on what the design changes in flight, for the Drive window.</summary>
        public string Note { get; private set; } = "";
        /// <summary>Where the drone cam sits (front edge of the body), in the look's frame. Never null after Awake.</summary>
        public Transform Nose { get; private set; }

        DroneBody body;
        Transform shell;
        CadModel model;
        Transform fit, loading;
        CadSession hooked;
        CadMockSet mocks;
        bool mocksSearched;
        int loadToken, shownGeneration = int.MinValue;
        string shownKey;
        float nextPoll;

        struct Piece { public Transform t; public Vector3 home, offset; public float delay; }
        Piece[] pieces = new Piece[0];
        float assembleT = -1f;

        /// <summary>Called by the drone's spawner: the body to dress and the primitive shell to fall back to.</summary>
        public void Init(DroneBody owner, Transform primitiveShell, Transform shellNose)
        {
            body = owner;
            shell = primitiveShell;
            if (Nose == null)
            {
                Nose = new GameObject("Nose mount").transform;
                Nose.SetParent(body.Visual, false);
            }
            Nose.localPosition = shellNose != null ? shellNose.localPosition + new Vector3(0f, 0f, 0.01f) : new Vector3(0f, 0.004f, 0.075f);
            Nose.localRotation = Quaternion.identity;
        }

        void OnEnable() => CadSession.ParamsRequested += OnRequested;

        void OnDisable()
        {
            CadSession.ParamsRequested -= OnRequested;
            Hook(null);
        }

        void OnDestroy()
        {
            CadSession.ParamsRequested -= OnRequested;
            Hook(null);
            model?.Dispose();
            model = null;
            if (loading != null) Destroy(loading.gameObject);
        }

        void Hook(CadSession s)
        {
            if (hooked == s) return;
            if (hooked != null) hooked.ModelChanged -= OnCad;
            hooked = s;
            if (hooked != null) hooked.ModelChanged += OnCad;
        }

        void OnCad(CadUpdate update)
        {
            if (update == null || update.info == null || update.info.part != Part) return;
            FromVersion(update.info, hooked != null && hooked.IsMock);
            Consider(update.info, update.glb, update.metadata, update.generation, SessionLabel(hooked));
        }

        static string SessionLabel(CadSession s) => s != null && !s.IsMock ? "CAD (live)" : "CAD (mock mode)";

        void Update()
        {
            if (body == null) return;
            if (assembleT >= 0f) Assemble(Time.deltaTime);
            float now = Time.unscaledTime;
            if (now < nextPoll) return;
            nextPoll = now + PollSeconds;
            var s = CadSession.Instance;
            Hook(s);
            if (s != null && s.Latest != null && s.Latest.info != null && s.Latest.info.part == Part)
            {
                // The poll only fills in a design not seen yet; a change arrives as ModelChanged (or a request).
                if (!HasFlight) FromVersion(s.Latest.info, s.IsMock);
                Consider(s.Latest.info, s.Latest.glb, s.Latest.metadata, s.Latest.generation, SessionLabel(s));
                return;
            }
            if (Loaded || loading != null) return;  // keep the last quadcopter shown while CAD is on another design
            LoadNewestMock(s);
        }

        // ---- The mock set (hub off, CAD not open on the quadcopter) ------------------------------------------------------

        void LoadNewestMock(CadSession s)
        {
            if (s != null && s.mocks != null) mocks = s.mocks;
            if (mocks == null && !mocksSearched)
            {
                mocksSearched = true;  // the scene's assets do not change: look once
                foreach (var viewer in FindObjectsByType<CadViewer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (viewer != null && viewer.mocks != null) { mocks = viewer.mocks; break; }
                if (mocks == null)
                {
                    var loaded = Resources.FindObjectsOfTypeAll<CadMockSet>();
                    if (loaded.Length > 0) mocks = loaded[0];
                }
            }
            if (mocks == null) return;
            int index = mocks.IndexOf(Part);
            if (index < 0) return;
            var versions = mocks.parts[index].versions;
            if (versions.Count == 0) return;
            var v = versions[versions.Count - 1];
            if (v.glb == null || v.metadata == null || v.modelUpdated == null) return;
            CadModelUpdated info;
            try { info = JsonConvert.DeserializeObject<CadEnvelope<CadModelUpdated>>(v.modelUpdated.text).payload; }
            catch (Exception e)
            {
                Debug.LogWarning("[Drone] The quadcopter sample's model_updated.json did not parse: " + e.Message);
                return;
            }
            if (info == null) return;
            if (!HasFlight) FromVersion(info, true);
            Consider(info, v.glb.bytes, v.metadata.text, int.MinValue + 1, "offline sample");
        }

        // ---- Loading --------------------------------------------------------------------------------------------------

        async void Consider(CadModelUpdated info, byte[] glb, string metadata, int generation, string source)
        {
            if (glb == null || metadata == null) return;
            string key = generation + ":" + info.version;
            if (key == shownKey) return;
            shownKey = key;
            int token = ++loadToken;
            if (loading != null) Destroy(loading.gameObject);
            // Loaded far away and active: glTFast instantiates there and the renderers' bounds are only real on an active object.
            loading = new GameObject("Drone design (loading)").transform;
            loading.position = new Vector3(0f, -5000f, 0f);
            CadModel next;
            try { next = await CadModel.LoadAsync(info.version, glb, metadata, loading, destroyCancellationToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                Debug.LogWarning("[Drone] Could not load quadcopter v" + info.version + ": " + e.Message + ". Keeping the current body.");
                if (token == loadToken)
                {
                    if (loading != null) { Destroy(loading.gameObject); loading = null; }
                    shownKey = null;  // try again on the next poll
                }
                return;
            }
            if (token != loadToken || this == null) { next.Dispose(); return; }
            Dress(next, info, source, generation);
        }

        void Dress(CadModel next, CadModelUpdated info, string source, int generation)
        {
            var renderers = next.Root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                next.Dispose();
                Destroy(loading.gameObject);
                loading = null;
                return;
            }
            // Measured in the new fit's frame, which sits at the loading holder with no rotation or scale while dressing.
            var newFit = new GameObject("Design v" + info.version).transform;
            newFit.SetParent(loading, false);
            next.Root.transform.SetParent(newFit, false);
            Measure(next.Root.transform, newFit, out Bounds b);
            float span = Mathf.Max(b.size.x, b.size.z, 1e-3f);
            float k = Mathf.Min(1f, MaxSpan / span);

            // Props spin about their own centres: each gets a pivot there (the node's vertices are about the model's origin).
            var pivots = new List<Transform>(8);
            var front = new List<Renderer>(16);
            Material accent = AccentMaterial();
            foreach (var id in next.partIds)
            {
                if (!id.StartsWith("PROP_", StringComparison.Ordinal)) continue;
                var node = next.objects[id].transform;
                if (!Measure(node, newFit, out Bounds pb)) continue;
                var pivot = new GameObject("Prop pivot " + id).transform;
                pivot.SetParent(newFit, false);
                pivot.localPosition = pb.center;
                node.SetParent(pivot, true);
                pivots.Add(pivot);
                if (pb.center.z > 0f) front.AddRange(node.GetComponentsInChildren<Renderer>(true));
            }
            // The front props take the accent colour, as on the built-in shell, so the pilot can tell which way it faces.
            if (accent != null)
                foreach (var r in front)
                {
                    var mats = new Material[r.sharedMaterials.Length];
                    for (int i = 0; i < mats.Length; i++) mats[i] = accent;
                    r.sharedMaterials = mats;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            foreach (var r in renderers) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // The parts to assemble (props move as their pivots), with where they fly in from.
            var list = new List<Piece>(32);
            var counts = new int[8];
            foreach (var id in next.partIds)
            {
                Transform t = next.objects[id].transform;
                if (id.StartsWith("PROP_", StringComparison.Ordinal) && t.parent != null && t.parent.name.StartsWith("Prop pivot", StringComparison.Ordinal)) t = t.parent;
                if (!Measure(t, newFit, out Bounds pb)) continue;
                int order = OrderOf(id);
                Vector3 radial = new Vector3(pb.center.x, 0f, pb.center.z);
                radial = radial.sqrMagnitude > 1e-8f ? radial.normalized : Vector3.zero;
                float lift = 0.06f + 0.035f * order;
                Vector3 off = order == 2 || order == 3 || order == 7 ? radial * 0.12f + Vector3.up * lift : Vector3.up * lift;
                list.Add(new Piece { t = t, home = t.localPosition, offset = t.parent.InverseTransformVector(off / k), delay = order * 0.11f + counts[order] * 0.03f });
                counts[order]++;
            }

            // The nose: the front edge of the top plate (else a third of the way out), level with the middle.
            float noseZ = b.center.z + b.extents.z * 0.35f;
            if (next.objects.TryGetValue("TOP_PLATE", out var plate) && Measure(plate.transform, newFit, out Bounds tp)) noseZ = tp.max.z;

            // Place under the look: centred, feet the body's rest height under its centre, at the shown scale.
            newFit.SetParent(body.Visual, false);
            newFit.localRotation = Quaternion.identity;
            newFit.localScale = Vector3.one * k;
            newFit.localPosition = new Vector3(-b.center.x * k, -body.BaseRestHeight - b.min.y * k, -b.center.z * k);
            Nose.localPosition = new Vector3(0f, newFit.localPosition.y + b.center.y * k, (noseZ - b.center.z) * k + 0.012f);

            // Swap: the old design goes, the shell hides.
            if (fit != null) Destroy(fit.gameObject);
            model?.Dispose();
            model = next;
            fit = newFit;
            Destroy(loading.gameObject);
            loading = null;
            if (shell != null) shell.gameObject.SetActive(false);
            body.SetProps(pivots.ToArray());
            pieces = list.ToArray();
            assembleT = 0f;
            Assemble(0f);

            Version = info.version;
            shownGeneration = generation;
            Source = source;
            ShownScale = k;
            RadiusFullSize = span * k * 0.5f + 0.01f;
            if (!HasFlight) FromVersion(info, false);
            Debug.Log("[Drone] Body is quadcopter v" + info.version + " (" + source + "), " + Mathf.RoundToInt(span * 1000f) + " mm across" + (k < 1f ? ", shown at " + Mathf.RoundToInt(k * 100f) + "%" : "") + ". " + Note);
            Changed?.Invoke();
        }

        // The renderers' bounds under t, in frame's space (frame has no rotation or scale while dressing).
        static bool Measure(Transform t, Transform frame, out Bounds local)
        {
            local = default;
            var rs = t.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return false;
            Bounds w = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) w.Encapsulate(rs[i].bounds);
            local = new Bounds(frame.InverseTransformPoint(w.center), w.size);
            return true;
        }

        static int OrderOf(string id)
        {
            if (id.StartsWith("BOTTOM_PLATE", StringComparison.Ordinal)) return 0;
            if (id.StartsWith("STANDOFF", StringComparison.Ordinal)) return 1;
            if (id.StartsWith("ARM", StringComparison.Ordinal)) return 2;
            if (id.StartsWith("MOTOR", StringComparison.Ordinal)) return 3;
            if (id.StartsWith("FLIGHT", StringComparison.Ordinal)) return 4;
            if (id.StartsWith("TOP_PLATE", StringComparison.Ordinal)) return 5;
            if (id.StartsWith("BATTERY", StringComparison.Ordinal)) return 6;
            return 7;
        }

        void Assemble(float dt)
        {
            assembleT += dt;
            float unit = AssembleSeconds / (7f * 0.11f + 0.15f + 0.45f);
            bool done = true;
            for (int i = 0; i < pieces.Length; i++)
            {
                var p = pieces[i];
                if (p.t == null) continue;
                float u = Mathf.Clamp01((assembleT / unit - p.delay) / 0.45f);
                if (u < 1f) done = false;
                float e = 1f - (1f - u) * (1f - u) * (1f - u);  // ease out
                p.t.localPosition = p.home + p.offset * (1f - e);
            }
            if (done) assembleT = -1f;
        }

        /// <summary>True while the parts are still flying together.</summary>
        public bool Assembling => assembleT >= 0f;

        static Material accentMaterial;
        static Material AccentMaterial() => accentMaterial != null ? accentMaterial : accentMaterial = Forge.Artifacts.ArtifactLook.Material("#FF7A1A");

        // ---- The design's numbers in flight ----------------------------------------------------------------------------

        // Slider values asked for in mock mode, still governing the next stored version CAD shows (and when).
        Dictionary<string, object> asked;
        float askedAt = -100f;

        // A slider, a preset (on this device): fly the values asked for now. In mock mode they also outlive the nearest
        // stored version that CAD shows next, so the physics is what the slider says.
        void OnRequested(string part, Dictionary<string, object> changes)
        {
            var s = CadSession.Instance;
            string open = part ?? (s != null && s.Params != null ? s.Params.part : null);
            if (open != Part || changes == null) return;
            if (asked == null || Time.unscaledTime - askedAt > RequestHoldSeconds) asked = new Dictionary<string, object>();
            foreach (var pair in changes) asked[pair.Key] = pair.Value;
            askedAt = Time.unscaledTime;
            Apply((Spec ?? DroneFlightModel.Reference).With(changes), Version > 0 ? "v" + Version + " with your change" : "your change");
        }

        // A version arrived (or was first seen): its own values, unless a mock-mode request just asked for others.
        void FromVersion(CadModelUpdated info, bool mock)
        {
            IDictionary<string, object> values = info.parameters;
            if (values == null)
            {
                var s = CadSession.Instance;
                if (s != null && s.Params != null && s.Params.part == Part)
                {
                    var d = new Dictionary<string, object>();
                    foreach (var p in s.Params.parameters) if (p != null && p.name != null) d[p.name] = p.value;
                    values = d;
                }
            }
            var spec = DroneFlightModel.Reference.With(values);
            // Mock mode, just after a slider: CAD shows the nearest stored version, but the drone keeps flying what was
            // asked (the values it flew before, with the changes on top), so the slider is what you feel.
            if (mock && asked != null && Time.unscaledTime - askedAt <= RequestHoldSeconds) spec = (Spec ?? spec).With(asked);
            else asked = null;
            Apply(spec, "v" + info.version);
        }

        void Apply(DroneFlightModel.Spec spec, string label)
        {
            if (spec == null) return;
            if (HasFlight && spec.SameAs(Spec)) return;
            var f = DroneFlightModel.Of(spec);
            bool first = !HasFlight;
            var was = Spec;
            var before = Flight;
            Spec = spec;
            Flight = f;
            HasFlight = true;
            AccelK = f.AccelK; BrakeK = f.BrakeK; ClimbK = f.ClimbK; YawK = f.YawK; SpeedK = f.SpeedK; AgilityK = f.AgilityK;
            ThrustToWeight = f.Twr;
            var k = BaseTuning();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Note = label + ": " + Mathf.RoundToInt(spec.ArmMm) + " mm arms, " + spec.PropIn.ToString("0.#", inv) + " in props, "
                 + Mathf.RoundToInt(spec.BatteryMah) + " mAh: " + DroneFlightModel.Describe(f, k) + " (from the design's numbers).";
            if (!first)
            {
                LastChange = DroneFlightModel.Compare(before, f, k);
                LastSaid = DroneFlightModel.Say(was, spec, before, f);
                Debug.Log("[Drone] Design change (" + label + "): " + LastChange);
            }
            Changed?.Invoke();
            if (first) return;
            FlightChanged?.Invoke(LastChange, LastSaid);
            if (!string.IsNullOrEmpty(LastSaid) && Application.isPlaying) FirstRunGuide.Say(LastSaid + " In numbers: " + LastChange + ".");
        }

        DroneFlightModel.Base BaseTuning()
        {
            var k = new DroneFlightModel.Base { Speed = 1.2f, Climb = 0.6f, Yaw = 90f, Accel = 2.5f };
            if (body != null) body.BaseTuning(out k.Speed, out k.Climb, out k.Yaw, out k.Accel);
            return k;
        }

        static float Param(CadModelUpdated info, string name, float fallback)
        {
            if (info.parameters == null || !info.parameters.TryGetValue(name, out var v) || v == null) return fallback;
            try { return Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { return fallback; }
        }

        static float Metric(CadModelUpdated info, string id, float fallback)
        {
            var list = info.analysis != null ? info.analysis.metrics : null;
            if (list == null) return fallback;
            foreach (var m in list) if (m != null && m.id == id) return m.value;
            return fallback;
        }
    }
}
