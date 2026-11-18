using System.Collections.Generic;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Added by AlbieCrew to every hatched Albie: its type (size, hat colour/shape), the hatch / go-home animation
    /// (the model child grows from nothing, so the root keeps its scale and the caption stays the right size), a
    /// greeting, and a status line when poked. The hat (shown once hatched) is built from primitives under Albie root and follows the
    /// head bone every LateUpdate (position only, Albie's yaw), so it works whatever the bone axes are.
    /// </summary>
    [DisallowMultipleComponent]
    public class AlbieCrewMember : MonoBehaviour
    {
        public static bool WorkersInOffice { get; set; } = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOffice() => WorkersInOffice = true;

        const float HatchSeconds = 0.45f, GoHomeSeconds = 0.3f;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        public AlbieAgentType Type { get; private set; }
        public string PetId => Type != null ? Type.Id : name;
        public AlbieController Controller { get; private set; }
        public AlbieAgentBridge Bridge { get; private set; }
        public bool Leaving { get; private set; }

        /// <summary>
        /// Brought home (the OS Agents window): not wandering. At a desk it stands still on its home pad (<see cref="HomeSpot"/>);
        /// away from a desk its body is hidden (<see cref="BodyHidden"/>). Its task, bridge and hub state keep running either way.
        /// </summary>
        public bool Home { get; private set; }
        /// <summary>The pad on the desk this member stands on while home, or null (away from a desk: the body is hidden instead).</summary>
        public Vector3? HomeSpot { get; private set; }
        /// <summary>The body (renderers and colliders) is switched off because the member is home away from a desk.</summary>
        public bool BodyHidden { get; private set; }

        Material _hatMaterial;
        Transform _model, _head, _hat;
        Vector3 _modelScale = Vector3.one;
        float _animStart, _hatLift = -1f;                // _hatLift >= 0 once the hat spot is measured
        Vector3 _hatOffset;
        bool _hatching;
        AlbieCaption _caption;
        AlbieInteraction _interaction;

        /// <summary>Called by AlbieCrew on the inactive clone, before Awake runs on anything.</summary>
        public void Init(AlbieAgentType type, Material hatMaterial)
        {
            Type = type;
            _hatMaterial = hatMaterial;
            transform.localScale = Vector3.one * type.Scale;
        }

        void Awake()
        {
            Controller = GetComponent<AlbieController>();
            Bridge = GetComponent<AlbieAgentBridge>();
            _caption = GetComponentInChildren<AlbieCaption>(true);
            _interaction = GetComponent<AlbieInteraction>();
            Animator animator = GetComponentInChildren<Animator>(true);
            _model = animator != null && animator.transform != transform ? animator.transform : null;
            if (_model != null) _modelScale = _model.localScale;
            _head = animator != null ? FindChild(animator.transform, "head") : null;
            BuildHat();
            var tag = gameObject.AddComponent<AlbieNameTag>();
            if (Type != null) tag.Set(Type.Name, Type.Color);
            BeginHatch();
        }

        void Start()
        {
            if (WorkersInOffice) { SetHome(true); Forge.OS.OSAgentOffice.Open(); }
            if (_interaction != null) _interaction.Poked += OnPoked;
            if (_caption != null && Type != null) _caption.SetText("Hi! I'm " + Type.Name + ". I " + Type.Role + ".", false);
        }

        void OnDestroy()
        {
            if (_interaction != null) _interaction.Poked -= OnPoked;
        }

        /// <summary>Mute / unmute this Albie's speech bubble (AlbieCrew: crew chatter is muted while the code mode is closed).</summary>
        public void SetMuted(bool on)
        {
            if (_caption == null) _caption = GetComponentInChildren<AlbieCaption>(true);
            if (_caption != null) _caption.Muted = on;
        }

        /// <summary>Shrink back into nothing, then destroy Albie.</summary>
        public void GoHome()
        {
            if (Leaving) return;
            Leaving = true;
            _hatching = false;
            _animStart = Time.time;
            if (_caption != null) _caption.SetText("Bye!", false);
            if (_model == null) Destroy(gameObject);
        }

        // ---------------------------------------------------------------- home (OS Agents window)

        const float HomePadSize = 0.06f, HomeCheckSeconds = 0.3f;
        float _nextHomeCheck;
        readonly List<Renderer> _bodyRenderers = new List<Renderer>(48);
        readonly List<Collider> _bodyColliders = new List<Collider>(8);
        readonly List<Collider> _collidersOff = new List<Collider>(8);

        /// <summary>
        /// Bring home (true) or send out (false). Home with a <paramref name="spot"/> (a point on the desk top): stop wandering, walk
        /// there and stand still. Home with no spot (the user is away from a desk): hide the body, keep everything running.
        /// Out: the body is back on and wandering resumes. The task, the bridge and the hub state are never touched, unlike
        /// <see cref="GoHome"/>, which destroys the member (that is dismissal).
        /// </summary>
        public void SetHome(bool home, Vector3? spot = null)
        {
            if (Leaving) return;
            if (!home)
            {
                if (!Home) return;
                Home = false;
                HomeSpot = null;
                SetBodyHidden(false);
                if (Controller != null) Controller.ReleaseStation();
                if (AlbieCrew.Instance != null) AlbieCrew.Instance.NotifyHomeChanged();
                return;
            }
            Home = true;
            HomeSpot = spot;
            _nextHomeCheck = 0f;
            SetBodyHidden(!spot.HasValue);
            ParkAtHome();
            if (AlbieCrew.Instance != null) AlbieCrew.Instance.NotifyHomeChanged();
        }

        static AlbieNavSurface HomeSurface(Vector3 p) =>
            AlbieNavSurface.TryGetSurfaceAt(p, out AlbieNavSurface found) ? found : AlbieNavSurface.Desk;

        // Territory = a small square on the pad, so a working agent (crew work at the front of their own area) goes to the pad and
        // stays there; StationAt = stand still on it while Wandering. StationAt ends a Working state, so a busy agent only gets the
        // territory now and is parked by HomeTick once the controller is back in Wandering.
        void ParkAtHome()
        {
            if (Controller == null) return;
            if (!HomeSpot.HasValue)
            {
                if (Controller.State == AlbieState.Wandering) Controller.StationAt(transform.position);   // hidden: stay where it is
                return;
            }
            Vector3 spot = HomeSpot.Value;
            AlbieNavSurface surface = HomeSurface(spot);
            bool onSurface = surface != null && surface.IsBuilt;
            if (onSurface) Controller.SetTerritory(surface, spot, surface.Rotation, new Vector2(HomePadSize, HomePadSize));
            if (Controller.State != AlbieState.Wandering) return;
            if (onSurface && !Controller.IsHopping && Controller.CurrentSurface != surface) Controller.HopTo(surface, spot);   // e.g. it was on the floor
            Controller.StationAt(spot);
        }

        // Every 0.3 s while home: park once the controller is back in Wandering, hop up if it is on the wrong surface, and keep
        // anything made since (a bubble, the hat) hidden too. No allocations: the lists are reused.
        void HomeTick()
        {
            if (Time.time < _nextHomeCheck) return;
            _nextHomeCheck = Time.time + HomeCheckSeconds;
            if (BodyHidden) ApplyBodyHidden();
            SettleHomeSpot();
            if (Controller == null || Controller.IsHopping || Controller.State != AlbieState.Wandering) return;
            if (!Controller.Stationed) { ParkAtHome(); return; }
            if (!HomeSpot.HasValue) return;
            AlbieNavSurface surface = HomeSurface(HomeSpot.Value);
            if (surface != null && surface.IsBuilt && Controller.CurrentSurface != surface) Controller.HopTo(surface, HomeSpot.Value);
        }

        // The controller stops walking about 15 cm short of a point it was sent to (NavMeshAgent stopping distance), so an agent
        // would stand beside its pad. Once it has stopped (not moved 1 cm over a tick) near its pad, the pad is where it stands.
        Vector3 _homeLastPos;

        void SettleHomeSpot()
        {
            Vector3 p = transform.position;
            bool still = (p - _homeLastPos).sqrMagnitude < 0.0001f;
            _homeLastPos = p;
            if (!still || !HomeSpot.HasValue || BodyHidden || (Controller != null && Controller.IsHopping)) return;
            Vector3 spot = HomeSpot.Value;
            float flat = new Vector2(p.x - spot.x, p.z - spot.z).magnitude;
            if (flat > 0.03f && flat < 0.3f) HomeSpot = new Vector3(p.x, spot.y, p.z);
        }

        void SetBodyHidden(bool hide)
        {
            BodyHidden = hide;
            ApplyBodyHidden();
        }

        // forceRenderingOff (not Renderer.enabled): the caption and the name tag switch things on and off themselves, and this flag is
        // theirs to leave alone. Colliders are switched off so a hidden agent can't be poked; the ones switched off are remembered.
        void ApplyBodyHidden()
        {
            GetComponentsInChildren(true, _bodyRenderers);
            for (int i = 0; i < _bodyRenderers.Count; i++)
                if (_bodyRenderers[i] != null) _bodyRenderers[i].forceRenderingOff = BodyHidden;
            if (BodyHidden)
            {
                GetComponentsInChildren(true, _bodyColliders);
                for (int i = 0; i < _bodyColliders.Count; i++)
                {
                    Collider c = _bodyColliders[i];
                    if (c == null || !c.enabled) continue;
                    c.enabled = false;
                    _collidersOff.Add(c);
                }
            }
            else
            {
                for (int i = 0; i < _collidersOff.Count; i++)
                    if (_collidersOff[i] != null) _collidersOff[i].enabled = true;
                _collidersOff.Clear();
            }
            _bodyRenderers.Clear();
            _bodyColliders.Clear();
        }

        void BeginHatch()
        {
            if (_model == null) return;
            _hatching = true;
            _animStart = Time.time;
            _model.localScale = _modelScale * 0.01f;
        }

        void Update()
        {
            if (WorkersInOffice && !Leaving && (!Home || HomeSpot.HasValue)) SetHome(true);
            if (Home && !Leaving) HomeTick();
            if (_model == null) return;
            float t = Time.time - _animStart;
            if (Leaving)
            {
                float u = Mathf.Clamp01(t / GoHomeSeconds);
                _model.localScale = _modelScale * Mathf.Max(0.01f, 1f - u * u);
                if (_hat != null) _hat.localScale = Vector3.one * Mathf.Max(0.01f, 1f - u);
                if (u >= 1f) Destroy(gameObject);
            }
            else if (_hatching)
            {
                float u = Mathf.Clamp01(t / HatchSeconds);
                float back = 1f + 2.2f * Mathf.Pow(u - 1f, 3f) + 1.2f * Mathf.Pow(u - 1f, 2f);   // ease-out-back (overshoot)
                _model.localScale = _modelScale * Mathf.Max(0.01f, back);
                if (u >= 1f) { _hatching = false; _model.localScale = _modelScale; }
            }
        }

        void LateUpdate()
        {
            if (_hat == null) return;
            if (_hatLift < 0f)
            {
                MeasureHatLift();                     // once, at full size; the hat pops on after the hatch
                if (_hatLift < 0f) return;
                _hat.gameObject.SetActive(true);
                Debug.Log("[AlbieCrewMember] " + name + ": hat on '" + (_head != null ? _head.name : "root (no head bone)") + "', offset "
                          + _hatOffset.ToString("F3") + ", material " + (_hatMaterial != null ? _hatMaterial.name : "none") + ".", this);
            }
            Transform anchor = _head != null ? _head : transform;     // no head bone: sit on top of the body
            Quaternion yaw = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            _hat.SetPositionAndRotation(anchor.position + yaw * (_hatOffset * (transform.lossyScale.y * HatchFactor())), yaw);
        }

        // While the model grows / shrinks the head bone moves with it; scale the lift the same way.
        float HatchFactor() => _model != null && _modelScale.y > 0f ? _model.localScale.y / _modelScale.y : 1f;

        // Where the hat sits, relative to the head bone (or the root) in Albie's yaw frame and unscaled units, measured
        // once at full size from the posed mesh: the top of the head around the bone (this Albie's eyes bulge up, so the
        // highest vertices are the eye tops; their average puts the hat between the eyes). Renderer bounds are far too
        // loose, and BakeMesh leaves out the skin's own (large) import scale, so it's applied here.
        void MeasureHatLift()
        {
            if (_hatching) return;
            Transform anchor = _head != null ? _head : transform;
            float s = Mathf.Max(0.0001f, transform.lossyScale.y);
            Vector3 a = anchor.position;
            Vector3 hatPoint = a + Vector3.up * (0.03f * s);  // fallback: a bit above the bone
            SkinnedMeshRenderer smr = LargestSkin();
            if (smr != null)
            {
                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                Vector3[] verts = baked.vertices;
                Transform st = smr.transform;
                Vector3 origin = st.position, sc = st.lossyScale;
                Quaternion rot = st.rotation;
                float r2 = HeadRadius * s * HeadRadius * s, top = float.MinValue;
                for (int pass = 0; pass < 2; pass++)          // 1: highest point near the head, 2: average of the top 4 mm
                {
                    Vector3 sum = Vector3.zero;
                    int n = 0;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 w = origin + rot * Vector3.Scale(verts[i], sc);
                        float dx = w.x - a.x, dz = w.z - a.z;
                        if (dx * dx + dz * dz > r2) continue;
                        if (pass == 0) { if (w.y > top) top = w.y; }
                        else if (w.y >= top - 0.004f * s) { sum += w; n++; }
                    }
                    if (pass == 0 && top == float.MinValue) break;
                    if (pass == 1 && n > 0) { hatPoint = sum / n; hatPoint.y = top - 0.002f * s; }
                }
                Destroy(baked);
            }
            Quaternion yaw = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            _hatOffset = Quaternion.Inverse(yaw) * (hatPoint - a) / s;
            _hatLift = 1f;                                    // measured
        }

        const float HeadRadius = 0.045f;   // m at Albie's size: the head (eyes included) around the head bone

        SkinnedMeshRenderer LargestSkin()
        {
            SkinnedMeshRenderer best = null;
            int most = -1;
            foreach (var sk in GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!sk.enabled || sk.sharedMesh == null || !sk.gameObject.activeInHierarchy) continue;
                if (sk.sharedMesh.vertexCount > most) { most = sk.sharedMesh.vertexCount; best = sk; }
            }
            return best;
        }

        void OnPoked()
        {
            if (_caption == null || Type == null || Leaving) return;
            bool busy = Bridge != null && Bridge.TaskInProgress;
            _caption.SetText(busy ? Type.Name + ": working on it!" : Type.Name + " here! I " + Type.Role + ".", false);
        }

        // ---------------------------------------------------------------- hat

        void BuildHat()
        {
            if (Type == null) return;
            _hat = new GameObject("Hat (" + Type.Name + ")").transform;
            _hat.SetParent(transform, false);
            Color c = Type.Color, dark = Color.Lerp(c, Color.black, 0.35f), light = Color.Lerp(c, Color.white, 0.6f);
            switch (Type.Hat)
            {
                case HatStyle.HardHat:
                    Part(PrimitiveType.Sphere, new Vector3(0.042f, 0.024f, 0.046f), new Vector3(0f, 0.004f, 0f), c);
                    Part(PrimitiveType.Cylinder, new Vector3(0.052f, 0.0015f, 0.058f), new Vector3(0f, 0.002f, 0.002f), c);
                    Part(PrimitiveType.Cube, new Vector3(0.006f, 0.008f, 0.04f), new Vector3(0f, 0.014f, 0f), dark);
                    break;
                case HatStyle.Beanie:
                    Part(PrimitiveType.Sphere, new Vector3(0.04f, 0.028f, 0.042f), new Vector3(0f, 0.004f, 0f), c);
                    Part(PrimitiveType.Cylinder, new Vector3(0.042f, 0.003f, 0.044f), new Vector3(0f, 0.0f, 0f), dark);
                    Part(PrimitiveType.Sphere, Vector3.one * 0.012f, new Vector3(0f, 0.02f, 0f), light);
                    break;
                case HatStyle.Antenna:
                    Part(PrimitiveType.Cylinder, new Vector3(0.003f, 0.011f, 0.003f), new Vector3(0f, 0.011f, 0f), dark);
                    Part(PrimitiveType.Sphere, Vector3.one * 0.012f, new Vector3(0f, 0.025f, 0f), c);
                    break;
                case HatStyle.Visor:
                    Part(PrimitiveType.Cylinder, new Vector3(0.038f, 0.004f, 0.04f), new Vector3(0f, 0.002f, 0f), c);
                    Part(PrimitiveType.Cylinder, new Vector3(0.032f, 0.0015f, 0.026f), new Vector3(0f, 0.002f, 0.022f), dark);
                    break;
                case HatStyle.Bandana:
                    Part(PrimitiveType.Cylinder, new Vector3(0.044f, 0.004f, 0.046f), new Vector3(0f, -0.002f, 0f), c);
                    Part(PrimitiveType.Sphere, new Vector3(0.01f, 0.008f, 0.01f), new Vector3(0f, -0.002f, -0.024f), c);
                    Part(PrimitiveType.Cube, new Vector3(0.006f, 0.002f, 0.014f), new Vector3(0.004f, -0.006f, -0.03f), dark);
                    break;
                case HatStyle.TopHat:
                    Part(PrimitiveType.Cylinder, new Vector3(0.046f, 0.0015f, 0.046f), new Vector3(0f, 0.001f, 0f), c);
                    Part(PrimitiveType.Cylinder, new Vector3(0.028f, 0.014f, 0.028f), new Vector3(0f, 0.015f, 0f), c);
                    Part(PrimitiveType.Cylinder, new Vector3(0.029f, 0.002f, 0.029f), new Vector3(0f, 0.005f, 0f), light);
                    break;
                case HatStyle.CaptainCap:
                    Part(PrimitiveType.Cylinder, new Vector3(0.04f, 0.007f, 0.042f), new Vector3(0f, 0.007f, 0f), c);
                    Part(PrimitiveType.Cylinder, new Vector3(0.034f, 0.0015f, 0.022f), new Vector3(0f, 0.002f, 0.018f), Color.Lerp(c, Color.black, 0.8f));
                    Part(PrimitiveType.Cylinder, new Vector3(0.041f, 0.002f, 0.043f), new Vector3(0f, 0.002f, 0f), new Color(0.12f, 0.16f, 0.3f));
                    Part(PrimitiveType.Sphere, Vector3.one * 0.007f, new Vector3(0f, 0.008f, 0.021f), new Color(1f, 0.8f, 0.2f));
                    break;
                case HatStyle.Bow:
                    Part(PrimitiveType.Sphere, new Vector3(0.016f, 0.012f, 0.009f), new Vector3(-0.009f, 0.004f, 0f), c);
                    Part(PrimitiveType.Sphere, new Vector3(0.016f, 0.012f, 0.009f), new Vector3(0.009f, 0.004f, 0f), c);
                    Part(PrimitiveType.Sphere, Vector3.one * 0.007f, new Vector3(0f, 0.004f, 0f), dark);
                    break;
                case HatStyle.Helmet:
                    Part(PrimitiveType.Sphere, new Vector3(0.048f, 0.032f, 0.052f), new Vector3(0f, 0.0f, 0f), c);
                    Part(PrimitiveType.Cube, new Vector3(0.004f, 0.012f, 0.036f), new Vector3(0f, 0.016f, -0.002f), new Color(0.85f, 0.2f, 0.2f));
                    break;
            }
            _hat.gameObject.SetActive(false);
        }

        void Part(PrimitiveType shape, Vector3 size, Vector3 localPos, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(shape);
            Collider col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.name = shape.ToString();
            Transform t = go.transform;
            t.SetParent(_hat, false);
            // Cylinders are 2 units tall: size.y is the half height, like the whistle's discs.
            t.localScale = size;
            t.localPosition = localPos;
            var r = go.GetComponent<Renderer>();
            if (_hatMaterial != null) r.sharedMaterial = _hatMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor(BaseColorId, color);
            mpb.SetColor(ColorId, color);
            r.SetPropertyBlock(mpb);
        }

        static Transform FindChild(Transform root, string childName)
        {
            if (root.name == childName) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform hit = FindChild(root.GetChild(i), childName);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
