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
    /// numbers into the flight model (modestly, each factor capped; see <see cref="Note"/>).
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
        // The reference design the arcade tuning was made for: the factors are 1 here.
        const float ArmRefMm = 110f, TwrRef = 8f, MassRefG = 450f;
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

        void OnDisable() => Hook(null);

        void OnDestroy()
        {
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
            if (update != null && update.info != null && update.info.part == Part) Consider(update.info, update.glb, update.metadata, update.generation, SessionLabel(hooked));
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
                OnCad(s.Latest);
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
            if (info != null) Consider(info, v.glb.bytes, v.metadata.text, int.MinValue + 1, "offline sample");
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
            Factors(info);
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

        void Factors(CadModelUpdated info)
        {
            float arm = Param(info, "arm_length", ArmRefMm);
            float twr = Metric(info, "twr", float.NaN);
            float mass = info.analysis != null && info.analysis.massG > 0f ? info.analysis.massG : Metric(info, "mass", MassRefG);
            // Longer arms put the motors further out: more turning inertia, so a slower, steadier turn.
            YawK = Mathf.Clamp(Mathf.Sqrt(ArmRefMm / Mathf.Max(10f, arm)), 0.8f, 1.15f);
            // More thrust per gram: quicker to speed up and to climb.
            float t = float.IsNaN(twr) ? 1f : Mathf.Clamp(Mathf.Sqrt(twr / TwrRef), 0.8f, 1.2f);
            AccelK = ClimbK = t;
            // Heavier: it takes longer to stop.
            BrakeK = Mathf.Clamp(Mathf.Sqrt(MassRefG / Mathf.Max(50f, mass)), 0.8f, 1.2f);
            ThrustToWeight = twr;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Note = "v" + info.version + ": " + Mathf.RoundToInt(arm) + " mm arms, turns " + Percent(YawK, "faster", "slower")
                 + (float.IsNaN(twr) ? "" : "; " + twr.ToString("0.0", inv) + ":1 thrust, climbs " + Percent(t, "quicker", "slower"))
                 + "; " + Mathf.RoundToInt(mass) + " g, brakes " + Percent(BrakeK, "harder", "softer") + ".";
        }

        static string Percent(float k, string more, string less)
        {
            int p = Mathf.RoundToInt((k - 1f) * 100f);
            return p == 0 ? "as usual" : (p > 0 ? p + "% " + more : -p + "% " + less);
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
