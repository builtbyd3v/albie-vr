using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Forge.Demo
{
    /// <summary>
    /// The drone's body: "the drone we built yesterday", the quadcopter CAD mock (v7). It loads the same GLB the CAD module shows
    /// (<see cref="CadModel"/>, glTFast), scales it to a hand-sized drone, finds the props (PROP_n) so they can spin, and can build
    /// itself from its parts (an exploded view collapsing, bottom plate first and props last) and take the camera on its nose.
    /// Local frame: +Z is the nose, +Y is up. Everything it moves per frame is cached; nothing allocates in Update.
    /// </summary>
    public sealed class DroneBody : MonoBehaviour
    {
        [Header("Model")]
        public TextAsset glb;
        public TextAsset metadata;
        public int version = 7;
        [Tooltip("Longest side of the drone, metres. The v7 mock has 160 mm arms and 7 in props, so it is scaled down to something that sits well on a pad at 0.85 m.")]
        public float span = 0.30f;

        [Header("Look")]
        public Material additive;       // Forge/Demo/FlightUnlit, additive
        public Material cameraBody;     // dark lit material for the camera model fallback
        public Material cameraLens;
        public Texture2D softDot;       // radial falloff, made by the builder

        public bool Ready { get; private set; }
        public bool Failed { get; private set; }
        public float Radius { get; private set; } = 0.15f;    // half the span, after scaling
        public float HalfHeight { get; private set; } = 0.02f;
        public Transform Nose { get; private set; }            // where the camera sits (body space, in front of the top plate)
        public bool CameraMounted { get; private set; }
        public Transform Holder => holder;

        struct Part
        {
            public Transform t;          // the part's node, or a prop's pivot
            public Vector3 homeLocal;
            public Vector3 homeHolder;   // in holder space
            public Vector3 offset;       // exploded, in holder space
            public float delay;
            public bool done;
        }

        struct Prop
        {
            public Transform pivot;
            public Renderer disc;
            public float sign;
        }

        CadModel model;
        Transform holder;
        Part[] parts = Array.Empty<Part>();
        Prop[] props = Array.Empty<Prop>();
        Transform[] sparks = Array.Empty<Transform>();
        MaterialPropertyBlock discBlock, sparkBlock;
        float spin, spinAngle;
        float spark;
        GameObject cameraModel;
        Mesh discMesh;
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        /// <summary>0 = props at rest, 1 = flying speed.</summary>
        public float Spin
        {
            get => spin;
            set => spin = Mathf.Clamp01(value);
        }

        void Awake()
        {
            holder = new GameObject("Holder").transform;
            holder.SetParent(transform, false);
            holder.localPosition = new Vector3(0f, 10000f, 0f);   // loads far away: renderer bounds need an active object, and nobody should see it arrive
            discBlock = new MaterialPropertyBlock();
            sparkBlock = new MaterialPropertyBlock();
        }

        void Start() => Load();

        async void Load()
        {
            if (Ready || glb == null || metadata == null) { if (glb == null) Failed = true; return; }
            try
            {
                model = await CadModel.LoadAsync(version, glb.bytes, metadata.text, holder, destroyCancellationToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                Debug.LogError($"[DroneBody] could not load the drone model: {e.Message}");
                Failed = true;
                return;
            }
            Prepare();
            Ready = true;
        }

        void OnDestroy()
        {
            model?.Dispose();
            if (discMesh != null) Destroy(discMesh);
        }

        // ---- Setup ---------------------------------------------------------------------------------------------------------

        void Prepare()
        {
            // Measure, scale to span and centre on the body's origin.
            var rs = model.Root.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) { Failed = true; return; }
            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            float longest = Mathf.Max(b.size.x, b.size.z, 1e-4f);
            float s = span / longest;
            holder.localScale = Vector3.one * s;
            for (int pass = 0; pass < 2; pass++)
            {
                b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                holder.position += transform.position - b.center;   // body at rest: identity rotation
            }
            Radius = span * 0.5f;
            HalfHeight = b.extents.y;

            discMesh = MakeDisc(32);

            // Props: spin each about its own centre. The node's vertices are baked about the model origin, so give it a pivot at its centre.
            var propList = new List<Prop>();
            var propIds = new List<string>();
            foreach (var id in model.partIds) if (id.StartsWith("PROP_", StringComparison.Ordinal)) propIds.Add(id);
            propIds.Sort(StringComparer.Ordinal);
            for (int i = 0; i < propIds.Count; i++)
            {
                var node = model.objects[propIds[i]].transform;
                var prs = node.GetComponentsInChildren<Renderer>(true);
                if (prs.Length == 0) continue;
                var pb = prs[0].bounds;
                for (int k = 1; k < prs.Length; k++) pb.Encapsulate(prs[k].bounds);
                var pivot = new GameObject("Pivot " + propIds[i]).transform;
                pivot.SetParent(holder, false);
                pivot.position = pb.center;
                pivot.rotation = holder.rotation;
                node.SetParent(pivot, true);

                var disc = new GameObject("Disc", typeof(MeshFilter), typeof(MeshRenderer));
                disc.transform.SetParent(pivot, false);
                disc.transform.localScale = Vector3.one * (Mathf.Max(pb.extents.x, pb.extents.z) / s) * 1.06f;
                disc.GetComponent<MeshFilter>().sharedMesh = discMesh;
                var dr = disc.GetComponent<MeshRenderer>();
                dr.sharedMaterial = additive;
                dr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                dr.receiveShadows = false;
                dr.enabled = false;
                propList.Add(new Prop { pivot = pivot, disc = dr, sign = (i & 1) == 0 ? 1f : -1f });
            }
            props = propList.ToArray();

            // Parts for the build: props move as their pivots.
            var list = new List<Part>();
            var counts = new int[8];
            foreach (var id in model.partIds)
            {
                Transform t = model.objects[id].transform;
                bool isProp = id.StartsWith("PROP_", StringComparison.Ordinal);
                if (isProp) t = t.parent;
                var rr = t.GetComponentsInChildren<Renderer>(true);
                if (rr.Length == 0) continue;
                var pb = rr[0].bounds;
                for (int k = 1; k < rr.Length; k++) pb.Encapsulate(rr[k].bounds);
                Vector3 c = holder.InverseTransformPoint(pb.center);
                int order = OrderOf(id);
                Vector3 radial = new Vector3(c.x, 0f, c.z);
                radial = radial.sqrMagnitude > 1e-8f ? radial.normalized : Vector3.zero;
                float inv = 1f / Mathf.Max(s, 1e-4f);   // distances below are metres in the room; holder space is 1/s of that
                Vector3 off;
                switch (order)
                {
                    case 0: off = new Vector3(0f, 0.02f, 0f); break;                         // bottom plate
                    case 1: off = new Vector3(0f, 0.09f, 0f) + radial * 0.02f; break;       // standoffs
                    case 2: off = radial * 0.2f + new Vector3(0f, 0.05f, 0f); break;         // arms
                    case 3: off = radial * 0.26f + new Vector3(0f, 0.13f, 0f); break;        // motors
                    case 4: off = new Vector3(0f, 0.17f, 0f); break;                         // flight controller
                    case 5: off = new Vector3(0f, 0.25f, 0f); break;                         // top plate
                    case 6: off = new Vector3(0f, 0.34f, 0f); break;                         // battery
                    default: off = radial * 0.24f + new Vector3(0f, 0.27f, 0f); break;       // props
                }
                list.Add(new Part
                {
                    t = t,
                    homeLocal = t.localPosition,
                    homeHolder = holder.InverseTransformPoint(t.position),
                    offset = off * inv,
                    delay = order * 0.24f + counts[order] * 0.05f,
                });
                counts[order]++;
            }
            parts = list.ToArray();

            // One spark per part: a small flash where it clicks home.
            sparks = new Transform[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(q.GetComponent<Collider>());
                q.name = "Spark";
                q.transform.SetParent(holder, false);
                var r = q.GetComponent<MeshRenderer>();
                r.sharedMaterial = additive;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                q.SetActive(false);
                sparks[i] = q.transform;
            }

            // The nose: front edge, a little above the plates.
            var noseGo = new GameObject("Nose").transform;
            noseGo.SetParent(transform, false);
            noseGo.localPosition = new Vector3(0f, HalfHeight * 0.6f, Radius * 0.52f);
            Nose = noseGo;

            SetAssembled(false);   // start unbuilt and hidden until drone.open
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

        static Mesh MakeDisc(int seg)
        {
            // A flat disc in the XZ plane, with the middle clear: uv.x is the radius, so the texture can fade the rim.
            var v = new Vector3[seg * 2];
            var uv = new Vector2[seg * 2];
            var col = new Color[seg * 2];
            var tri = new int[seg * 6];
            for (int i = 0; i < seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                v[i] = d * 0.18f; uv[i] = new Vector2(0.5f, 0.5f); col[i] = new Color(1f, 1f, 1f, 0.35f);
                v[seg + i] = d; uv[seg + i] = new Vector2(0.5f, 0.5f); col[seg + i] = new Color(1f, 1f, 1f, 1f);
                int n = (i + 1) % seg;
                tri[i * 6] = i; tri[i * 6 + 1] = seg + i; tri[i * 6 + 2] = seg + n;
                tri[i * 6 + 3] = i; tri[i * 6 + 4] = seg + n; tri[i * 6 + 5] = n;
            }
            var m = new Mesh { name = "PropDisc" };
            m.vertices = v; m.uv = uv; m.colors = col; m.triangles = tri;
            m.RecalculateBounds();
            return m;
        }

        // ---- Build and reset -------------------------------------------------------------------------------------------------

        /// <summary>Show the drone assembled (true) or hidden and unbuilt (false). Props stopped, camera unmounted when false.</summary>
        public void SetAssembled(bool built)
        {
            if (!Ready && model == null) return;
            holder.gameObject.SetActive(built);
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i].t.localPosition = parts[i].homeLocal;
                parts[i].done = built;
            }
            for (int i = 0; i < sparks.Length; i++) sparks[i].gameObject.SetActive(false);
            Spin = 0f;
            if (!built) ClearCamera();
        }

        /// <summary>Parts fly in from slightly outside their places and click together. Bottom plate first, props last.</summary>
        public IEnumerator Assemble(float seconds)
        {
            while (!Ready && !Failed) yield return null;
            if (Failed) yield break;
            holder.gameObject.SetActive(true);
            const float maxUnits = 7f * 0.24f + 3f * 0.05f + 0.7f;   // the last prop's delay plus its flight, in build units
            float k = Mathf.Max(0.3f, seconds - 0.4f) / maxUnits;    // seconds per build unit; 0.4 s is left for the final settle and sweep
            float t0 = Time.time;
            for (int i = 0; i < parts.Length; i++) { parts[i].done = false; parts[i].t.position = holder.TransformPoint(parts[i].homeHolder + parts[i].offset); }
            bool all = false;
            while (!all)
            {
                float now = (Time.time - t0) / k;
                all = true;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].done) continue;
                    float u = (now - parts[i].delay) / 0.7f;
                    if (u >= 1f)
                    {
                        parts[i].t.localPosition = parts[i].homeLocal;
                        parts[i].done = true;
                        FlashSpark(i);
                        continue;
                    }
                    all = false;
                    float e = u <= 0f ? 0f : EaseOutBack(u);
                    parts[i].t.position = holder.TransformPoint(parts[i].homeHolder + parts[i].offset * (1f - e));
                }
                TickSparks();
                yield return null;
            }
            for (float w = 0f; w < 0.25f; w += Time.deltaTime) { TickSparks(); yield return null; }
            TickSparks();
        }

        static float EaseOutBack(float t)
        {
            const float c1 = 1.2f, c3 = c1 + 1f;   // less overshoot than the usual 1.70158: parts settle, they don't bounce
            float p = t - 1f;
            return 1f + c3 * p * p * p + c1 * p * p;
        }

        void FlashSpark(int i)
        {
            if (i >= sparks.Length) return;
            sparks[i].gameObject.SetActive(true);
            sparks[i].position = parts[i].t.GetComponentInChildren<Renderer>().bounds.center;
            sparkTimes[i] = Time.time;
            spark = Mathf.Max(spark, 1f);
        }

        float[] sparkTimes = new float[64];

        void TickSparks()
        {
            var cam = Camera.main;
            for (int i = 0; i < sparks.Length; i++)
            {
                if (!sparks[i].gameObject.activeSelf) continue;
                float u = (Time.time - sparkTimes[i]) / 0.28f;
                if (u >= 1f) { sparks[i].gameObject.SetActive(false); continue; }
                float size = Mathf.Sin(u * Mathf.PI) * 0.045f;
                if (cam != null) sparks[i].rotation = cam.transform.rotation;
                sparks[i].localScale = Vector3.one * (size / Mathf.Max(holder.lossyScale.x, 1e-4f));
                var r = sparks[i].GetComponent<MeshRenderer>();
                sparkBlock.SetColor(ColorId, new Color(0.7f, 0.92f, 1f, 0.9f * (1f - u)));
                sparkBlock.SetTexture(MainTexId, softDot != null ? softDot : Texture2D.whiteTexture);
                r.SetPropertyBlock(sparkBlock);
            }
        }

        // ---- Props -----------------------------------------------------------------------------------------------------------

        void Update()
        {
            if (!Ready) return;
            if (spin > 0.001f)
            {
                spinAngle += spin * 2600f * Time.deltaTime;
                float a = spinAngle % 360f;
                for (int i = 0; i < props.Length; i++)
                {
                    props[i].pivot.localRotation = Quaternion.AngleAxis(a * props[i].sign, Vector3.up);
                    if (!props[i].disc.enabled) props[i].disc.enabled = true;
                    // The disc is the blur: it comes up with the speed.
                    discBlock.SetColor(ColorId, new Color(0.75f, 0.9f, 1f, 0.16f * Mathf.Clamp01(spin * 1.3f)));
                    props[i].disc.SetPropertyBlock(discBlock);
                }
            }
            else
            {
                for (int i = 0; i < props.Length; i++)
                    if (props[i].disc.enabled) props[i].disc.enabled = false;
            }
            TickSparks();
        }

        // ---- Camera ----------------------------------------------------------------------------------------------------------

        void ClearCamera()
        {
            if (cameraModel != null) Destroy(cameraModel);
            cameraModel = null;
            CameraMounted = false;
        }

        /// <summary>The camera model arcs from <paramref name="fromWorld"/> to the nose in about a second and clicks on.</summary>
        public IEnumerator MountCamera(Vector3 fromWorld, float seconds)
        {
            while (!Ready && !Failed) yield return null;
            if (Failed) yield break;
            ClearCamera();
            cameraModel = MakeCameraModel();
            var tr = cameraModel.transform;
            Vector3 end = Nose.position;
            Vector3 mid = (fromWorld + end) * 0.5f + Vector3.up * 0.18f;
            Quaternion q0 = Quaternion.LookRotation((end - fromWorld).sqrMagnitude > 1e-6f ? end - fromWorld : transform.forward, Vector3.up);
            float s0 = 0.012f, s1 = 1f;
            tr.SetParent(null, true);
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                float u = t / seconds;
                float e = u * u * (3f - 2f * u);
                end = Nose.position;
                Vector3 a = Vector3.Lerp(fromWorld, mid, e), b = Vector3.Lerp(mid, end, e);
                tr.position = Vector3.Lerp(a, b, e);
                tr.rotation = Quaternion.Slerp(q0, transform.rotation, e);
                tr.localScale = Vector3.one * Mathf.Lerp(s0, s1, Mathf.Min(1f, e * 1.3f));
                yield return null;
            }
            tr.SetParent(Nose, false);
            tr.localPosition = Vector3.zero;
            tr.localRotation = Quaternion.identity;
            tr.localScale = Vector3.one;
            CameraMounted = true;
            // A tiny click: a flash at the nose.
            if (sparks.Length > 0)
            {
                sparks[0].gameObject.SetActive(true);
                sparks[0].position = Nose.position;
                sparkTimes[0] = Time.time;
            }
        }

        GameObject MakeCameraModel()
        {
            var root = new GameObject("Camera mount");
            var prefab = Resources.Load<GameObject>("DemoModels/camera_a");
            if (prefab != null)
            {
                var inst = Instantiate(prefab, root.transform, false);
                var rs = inst.GetComponentsInChildren<Renderer>(true);
                if (rs.Length > 0)
                {
                    var b = rs[0].bounds;
                    for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                    float f = 0.045f / Mathf.Max(b.size.x, b.size.y, b.size.z, 1e-4f);
                    inst.transform.localScale *= f;
                    b = rs[0].bounds;
                    for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                    inst.transform.position -= b.center - root.transform.position;
                    return root;
                }
                Destroy(inst);
            }
            // Fallback: a small dark body with a lens, 4.5 cm wide.
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(0.045f, 0.032f, 0.03f);
            if (cameraBody != null) body.GetComponent<MeshRenderer>().sharedMaterial = cameraBody;
            var lens = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(lens.GetComponent<Collider>());
            lens.transform.SetParent(root.transform, false);
            lens.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            lens.transform.localPosition = new Vector3(0f, 0f, 0.022f);
            lens.transform.localScale = new Vector3(0.022f, 0.008f, 0.022f);
            if (cameraLens != null) lens.GetComponent<MeshRenderer>().sharedMaterial = cameraLens;
            return root;
        }
    }
}
