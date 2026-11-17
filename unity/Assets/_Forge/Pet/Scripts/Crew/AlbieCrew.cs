using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Pet
{
    public enum TerritoryScope { Desk, Floor }

    /// <summary>
    /// Albie's crew (docs/archive/PET_AGENT_CONTRACT.md, Addendum D): hatches and sends home extra agents, one per
    /// AlbieAgentType, and splits the room between everyone.
    ///
    /// Hatching: AlbieHatchery (the egg on the desk), the hub's crew.spawn (Albie's spawn_agent tool, "Albie, hatch a
    /// tester"), N on the keyboard, or Spawn() from code. A crew member is a copy of crewPrefab (else of Albie) made under
    /// an inactive parent, so Albie-only parts (mic, whistle, voice, hub socket, MRUK adapter...) are stripped before
    /// any Awake runs; its AlbieAgentBridge then shares Albie's event source and only reacts to its own pet_id.
    /// Unity is the source of truth: crew.roster goes to the hub after every change and on every reconnect.
    ///
    /// Areas: with a crew, every agent (Albie included) owns an equal-walkable-area strip of the desk (AlbieTerritories,
    /// the keep-out doesn't count), nearest the user first: Albie, then the crew in hatch order. Agents that don't fit
    /// (strips narrower than minStripWidth) share the floor. Scope Floor splits the floor instead. Everyone idles
    /// more while a crew exists (restful idle profile); Albie gets his solo behaviour back when the crew is gone.
    ///
    /// Whistle (any WhistleButton): clears the desk for Albie. Idle crew members go home; busy ones (AlbieAgentBridge.Busy)
    /// line up along the desk's back edge, away from the user, and keep working there. An agent leaves the line when
    /// its task is done; the others' areas stop short of the line while it exists.
    ///
    /// Workspace gate (Main): the egg / picker and the N / X keys only work while the desk mode gateModeId ("code") is open
    /// (CrewAllowed); an active coding workflow or its open panel also permits crew captions. Otherwise the crew stays on
    /// the desk with muted bubbles. Albie is never muted; the hub's crew.spawn / crew.dismiss are never gated. Scenes without a Workspace
    /// (the sandboxes) are never gated.
    /// </summary>
    [DisallowMultipleComponent]
    public class AlbieCrew : MonoBehaviour
    {
        [SerializeField, Tooltip("Albie. Empty = the first AlbieController in the scene that isn't a crew member.")]
        AlbieController lead;
        [SerializeField, Tooltip("Albie prefab copied for every crew member (AlbieAssistant). Empty = a copy of Albie.")]
        GameObject crewPrefab;
        [SerializeField, Tooltip("Material for the hats (tinted per type). Empty = URP Lit.")]
        Material hatMaterial;

        [Header("Areas")]
        [SerializeField, Tooltip("Desk: split the desk (overflow onto the floor). Floor: split the floor.")]
        TerritoryScope scope = TerritoryScope.Desk;
        [SerializeField, Tooltip("Narrowest desk strip (m) per albie; more agents than fit go to the floor.")]
        float minStripWidth = 0.16f;

        [Header("Restful idling (applied to every agent while a crew exists)")]
        [SerializeField, Range(0f, 1f)] float restChance = 0.85f;
        [SerializeField] Vector2 restSeconds = new Vector2(6f, 16f);
        [SerializeField, Range(0f, 1f)] float runChance = 0.08f;
        [SerializeField] Vector2 shortPause = new Vector2(2.5f, 6f);

        [Header("Whistle")]
        [SerializeField, Tooltip("A whistle sends idle crew home and lines busy crew up at the back of the desk.")]
        bool whistleClearsDesk = true;
        [SerializeField, Tooltip("Gap (m) between agents in the line.")]
        float lineupSpacing = 0.17f;
        [SerializeField, Tooltip("Line distance (m) in from the back edge; the other areas keep twice this clear.")]
        float lineupInset = 0.07f;

        [Header("Workspace gate")]
        [SerializeField, Tooltip("Egg / picker and N / X only work while this desk mode is open; crew captions are muted otherwise. Empty = never gated. No Workspace in the scene = never gated.")]
        string gateModeId = "code";

        [Header("Keyboard")]
        [SerializeField, Tooltip("Hatch the type shown on the egg (or the next free type).")]
        InputAction hatchAction = new InputAction("CrewHatch", InputActionType.Button, "<Keyboard>/n");
        [SerializeField, Tooltip("Send the most recently hatched agent home.")]
        InputAction dismissAction = new InputAction("CrewDismiss", InputActionType.Button, "<Keyboard>/x");

        // Albie-only parts removed from a crew copy (by type name: no compile-time dependency on Meta / sandbox types).
        static readonly HashSet<string> StripTypes = new HashSet<string>
        {
            "AlbieMrukAdapter", "AlbieSandboxNav", "PetSandboxHotkeys", "AlbieSummon", "AlbieFollowUp", "AlbieTalkAnimator",
            "PushToTalk", "HubSpeechToText", "TypedSpeechToText", "StreamedPcmTextToSpeech", "AgentClient", "MockEventSource",
            "AlbieCrew", "AlbieHatchery", "WhistleButton", "DesktopRig", "SandboxRigSelector", "AlbieCrewMember", "WakeListener", "AlbieNameTag",
            "ArrivalDirector",
        };

        public static AlbieCrew Instance { get; private set; }
        public IReadOnlyList<AlbieCrewMember> Members => _members;
        public AlbieController Lead => lead;
        /// <summary>Raised after an agent hatched or left.</summary>
        public event Action Changed;
        /// <summary>Hatching from the egg / keyboard is available: the gate mode is open, or there is no gate / no Workspace.</summary>
        public bool CrewAllowed => WorkflowWindowOpen || workflowRunning || string.IsNullOrEmpty(gateModeId) || !PetWorkspace.HasWorkspace || PetWorkspace.ActiveMode == gateModeId;
        /// <summary>The coding workflow can show the crew without opening the Code workspace.</summary>
        public static bool WorkflowWindowOpen { get; internal set; }
        bool workflowRunning;
        IDisposable workflowEvents;

        readonly List<AlbieCrewMember> _members = new List<AlbieCrewMember>(AlbieAgentTypes.All.Length);
        readonly List<AlbieController> _agents = new List<AlbieController>(AlbieAgentTypes.All.Length + 1);
        readonly List<AlbieTerritory> _areas = new List<AlbieTerritory>(AlbieAgentTypes.All.Length + 1);
        readonly List<AlbieTerritory> _floorAreas = new List<AlbieTerritory>(AlbieAgentTypes.All.Length + 1);
        readonly List<MonoBehaviour> _scratch = new List<MonoBehaviour>(32);
        readonly List<AlbieCrewMember> _lineup = new List<AlbieCrewMember>(AlbieAgentTypes.All.Length);
        AlbieAgentBridge _leadBridge;
        IPetEventSource _source;
        AlbieHatchery _hatchery;
        Transform _staging;
        bool _wasConnected, _leadCalm, _territoriesDirty, _lastAllowed = true;
        float _nextLayoutCheck;
        int _layoutHash;
        float _leadRestChance, _leadRunChance;
        Vector2 _leadRestSeconds, _leadShortPause;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; WorkflowWindowOpen = false; }

        void Awake()
        {
            Instance = this;
            if (lead == null) lead = FindLead();
            if (lead != null) _leadBridge = lead.GetComponent<AlbieAgentBridge>();
        }

        void Start()
        {
            WhistleButton.AnyPressed += OnWhistle;
            workflowEvents = ForgeNet.On("build.updated", token =>
            {
                string state = (string)token["state"];
                workflowRunning = state == "planning" || state == "awaiting_plan" || state == "building" || state == "verifying" || state == "shipping";
            }, true);
        }

        void OnEnable() { hatchAction.Enable(); dismissAction.Enable(); }
        void OnDisable() { hatchAction.Disable(); dismissAction.Disable(); }

        void OnDestroy()
        {
            workflowEvents?.Dispose();
            if (_source != null) _source.EventReceived -= OnHubEvent;
            WhistleButton.AnyPressed -= OnWhistle;
            if (Instance == this) Instance = null;
        }

        // ---------------------------------------------------------------- public API

        public bool IsHatched(AlbieAgentType t) => Find(t) != null;

        public AlbieCrewMember Find(AlbieAgentType t)
        {
            for (int i = 0; i < _members.Count; i++) if (_members[i] != null && _members[i].Type == t) return _members[i];
            return null;
        }

        /// <summary>First type (in catalog order, starting at fromIndex) that hasn't hatched yet; null if all have.</summary>
        public AlbieAgentType NextFree(int fromIndex = 0)
        {
            var all = AlbieAgentTypes.All;
            for (int i = 0; i < all.Length; i++)
            {
                AlbieAgentType t = all[(fromIndex + i + all.Length) % all.Length];
                if (!IsHatched(t)) return t;
            }
            return null;
        }

        /// <summary>Hatch an agent of this type (no-op if one exists). spawnAt = where it appears (default: the egg).</summary>
        public AlbieCrewMember Spawn(AlbieAgentType type, Vector3? spawnAt = null)
        {
            if (type == null) return null;
            AlbieCrewMember existing = Find(type);
            if (existing != null) return existing;
            if (lead == null) { Debug.LogWarning("[AlbieCrew] No lead agent (Albie) in the scene; can't hatch.", this); return null; }

            GameObject source = crewPrefab != null ? crewPrefab : lead.gameObject;
            if (_staging == null)
            {
                _staging = new GameObject("CrewStaging (inactive)").transform;
                _staging.SetParent(transform, false);
                _staging.gameObject.SetActive(false);
            }
            Vector3 pos = spawnAt ?? DefaultSpawnPoint();
            Transform head = PetUser.Head;
            Vector3 look = head != null ? head.position - pos : -lead.transform.forward;
            look.y = 0f;
            Quaternion rot = look.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(look) : lead.transform.rotation;

            // Inactive parent: nothing on the copy wakes up until it's cleaned and configured.
            GameObject go = Instantiate(source, pos, rot, _staging);
            go.name = "Albie " + type.Name;
            Strip(go, source == lead.gameObject);
            var member = go.AddComponent<AlbieCrewMember>();
            member.Init(type, hatMaterial);
            var controller = go.GetComponent<AlbieController>();
            if (controller != null)
            {
                controller.StayInTerritoryWhenBusy = true;
                controller.SetIdleProfile(restChance, restSeconds, runChance, shortPause);
                AlbieNavSurface under = SurfaceAt(pos);
                if (under != null) controller.SetWanderArea(under);
            }
            var bridge = go.GetComponent<AlbieAgentBridge>();
            if (bridge != null) bridge.UseSharedSource(type.Id, _leadBridge);
            go.transform.SetParent(null, true);          // wakes up now
            member.SetMuted(!CrewAllowed);               // hatched by the hub while the code mode is closed: quiet until it opens

            _members.Add(member);
            OnCrewChanged();
            Debug.Log("[AlbieCrew] Hatched " + type.Name + " (" + type.Id + ", scale " + type.Scale.ToString("F2") + ").", this);
            return member;
        }

        /// <summary>Send a crew member home (by pet_id / type id or name).</summary>
        public bool Dismiss(string petIdOrName)
        {
            AlbieCrewMember m = Find(AlbieAgentTypes.Find(petIdOrName));
            if (m == null) return false;
            _members.Remove(m);
            _lineup.Remove(m);
            if (m.Bridge != null && m.Bridge.Busy && m.Bridge.Source != null)
                m.Bridge.Source.Send("agent.cancel", new JObject { ["pet_id"] = m.PetId });
            m.GoHome();
            OnCrewChanged();
            return true;
        }

        public bool DismissLast() => _members.Count > 0 && Dismiss(_members[_members.Count - 1].PetId);

        /// <summary>A member was brought home or sent out (AlbieCrewMember.SetHome): re-split the areas without it, and tell listeners.</summary>
        public void NotifyHomeChanged()
        {
            _territoriesDirty = true;
            Changed?.Invoke();
        }

        /// <summary>Whistle: idle crew go home, busy crew line up at the back of the desk (Albie is untouched).</summary>
        public void ClearDeskForLead()
        {
            for (int i = _members.Count - 1; i >= 0; i--)
            {
                AlbieCrewMember m = _members[i];
                if (m == null || m.Home) continue;     // brought home on purpose: the whistle neither dismisses it nor lines it up
                if (m.Bridge != null && m.Bridge.Busy) { if (!_lineup.Contains(m)) _lineup.Add(m); }
                else Dismiss(m.PetId);
            }
            _territoriesDirty = true;
        }

        void OnWhistle(WhistleButton _)
        {
            if (whistleClearsDesk && _members.Count > 0) ClearDeskForLead();
        }

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            if (lead == null) { lead = FindLead(); if (lead != null) _leadBridge = lead.GetComponent<AlbieAgentBridge>(); }
            BindSource();

            bool connected = _source != null && _source.IsConnected;
            if (connected && !_wasConnected) SendRoster();      // after AgentClient's hello on (re)connect
            _wasConnected = connected;

            bool allowed = CrewAllowed;
            if (allowed != _lastAllowed)
            {
                _lastAllowed = allowed;
                for (int i = 0; i < _members.Count; i++) if (_members[i] != null) _members[i].SetMuted(!allowed);
                if (_members.Count > 0) Debug.Log("[AlbieCrew] " + (allowed ? "Coding workspace active: crew unmuted." : "Coding workspace closed: crew muted."), this);
            }
            if (allowed && !TypedSpeechToText.AnyListening && !global::DesktopRig.TypingInField)
            {
                if (hatchAction.WasPressedThisFrame()) HatchFromKeyboard();
                if (dismissAction.WasPressedThisFrame()) DismissLast();
            }

            for (int i = _lineup.Count - 1; i >= 0; i--)          // done working: back to a normal area
                if (_lineup[i] == null || _lineup[i].Bridge == null || !_lineup[i].Bridge.Busy) { _lineup.RemoveAt(i); _territoriesDirty = true; }

            if (Time.time >= _nextLayoutCheck || _territoriesDirty)
            {
                _nextLayoutCheck = Time.time + 1f;
                int h = LayoutHash();
                if (_territoriesDirty || h != _layoutHash) { _layoutHash = h; _territoriesDirty = false; AssignTerritories(); }
            }
        }

        // N: the type on the egg if it isn't out yet, else the next free one (never sends anyone home).
        void HatchFromKeyboard()
        {
            AlbieHatchery egg = Hatchery();
            AlbieAgentType t = egg != null && !IsHatched(egg.Shown) ? egg.Shown : NextFree();
            if (t != null) Spawn(t, egg != null && egg.Placed ? egg.SpawnPoint : (Vector3?)null);
        }

        void OnCrewChanged()
        {
            for (int i = _members.Count - 1; i >= 0; i--) if (_members[i] == null) _members.RemoveAt(i);
            ApplyLeadIdleProfile();
            ShowLeadTag(_members.Count > 0);
            _territoriesDirty = true;
            SendRoster();
            Changed?.Invoke();
        }

        // ---------------------------------------------------------------- hub

        void BindSource()
        {
            IPetEventSource s = _leadBridge != null ? _leadBridge.Source : null;
            if (s == _source) return;
            if (_source != null) _source.EventReceived -= OnHubEvent;
            _source = s;
            if (_source != null) _source.EventReceived += OnHubEvent;
        }

        void OnHubEvent(PetEvent e)
        {
            if (e == null) return;
            if (e.type == "crew.spawn")
            {
                AlbieAgentType t = AlbieAgentTypes.Find(e.GetString("agent_type"));
                if (t != null) Spawn(t);
                else Debug.LogWarning("[AlbieCrew] crew.spawn for unknown type '" + e.GetString("agent_type") + "'.", this);
            }
            else if (e.type == "crew.dismiss") Dismiss(e.GetString("pet_id"));
        }

        void SendRoster()
        {
            if (_source == null || !_source.IsConnected) return;
            var list = new JArray();
            for (int i = 0; i < _members.Count; i++)
                if (_members[i] != null)
                    list.Add(new JObject { ["pet_id"] = _members[i].PetId, ["agent_type"] = _members[i].Type.Id });
            _source.Send("crew.roster", new JObject { ["members"] = list });
        }

        // ---------------------------------------------------------------- areas

        // Re-split when the crew changes or the surfaces move / re-bake (whistle on another table, keep-out moved).
        int LayoutHash()
        {
            unchecked
            {
                int h = (_members.Count * 31 + _lineup.Count) * 31 + (int)scope;
                h = h * 31 + SurfaceHash(AlbieNavSurface.Desk);
                h = h * 31 + SurfaceHash(AlbieNavSurface.Floor);
                return h;
            }
        }

        static int SurfaceHash(AlbieNavSurface s)
        {
            if (s == null || !s.IsBuilt) return 0;
            unchecked
            {
                int h = Quantize(s.TopCenter).GetHashCode() * 31 + Quantize(s.Size).GetHashCode();
                var holes = s.Holes;
                h = h * 31 + holes.Count;
                for (int i = 0; i < holes.Count; i++) h = h * 31 + Quantize(holes[i].center).GetHashCode();
                return h;
            }
        }

        static Vector3Int Quantize(Vector3 v) => new Vector3Int(Mathf.RoundToInt(v.x * 50f), Mathf.RoundToInt(v.y * 50f), Mathf.RoundToInt(v.z * 50f));
        static Vector2Int Quantize(Vector2 v) => new Vector2Int(Mathf.RoundToInt(v.x * 50f), Mathf.RoundToInt(v.y * 50f));

        void AssignTerritories()
        {
            _agents.Clear();
            if (lead != null) _agents.Add(lead);
            for (int i = 0; i < _members.Count; i++)
                if (_members[i] != null && _members[i].Controller != null && !_lineup.Contains(_members[i]) && !_members[i].Home) _agents.Add(_members[i].Controller);
            if (_members.Count == 0)
            {
                if (lead != null) lead.ClearTerritory();   // solo Albie: the whole desk / room, exactly as before
                return;
            }

            AlbieNavSurface desk = AlbieNavSurface.Desk, floor = AlbieNavSurface.Floor;
            Transform head = PetUser.Head;
            bool lined = _lineup.Count > 0 && desk != null && desk.IsBuilt && AssignLineup(desk, head);
            int onDesk = 0;
            if (scope == TerritoryScope.Desk && desk != null && desk.IsBuilt)
            {
                onDesk = Mathf.Min(_agents.Count, AlbieTerritories.Capacity(desk, minStripWidth));
                AlbieTerritories.Split(desk, onDesk, head != null ? head.position : desk.TopCenter, _areas);
            }
            else _areas.Clear();

            int rest = _agents.Count - onDesk;
            bool floorOk = rest > 0 && floor != null && floor.IsBuilt
                           && AlbieTerritories.Split(floor, rest, head != null ? head.position : floor.TopCenter, _floorAreas);
            for (int i = 0; i < _agents.Count; i++)
            {
                AlbieTerritory a;
                if (i < onDesk) a = lined ? InFrontOfLine(_areas[i], desk) : _areas[i];
                else if (floorOk) a = _floorAreas[i - onDesk];
                else if (_areas.Count > 0) a = _areas[(i - onDesk) % _areas.Count];   // no floor: double up on the desk
                else { _agents[i].ClearTerritory(); continue; }
                _agents[i].SetTerritory(a.Surface, a.Center, a.Rotation, a.Size);
            }
        }

        // ---------------------------------------------------------------- lineup (whistle)

        bool _backAlongZ;      // the back edge runs along the desk's local X (back is ±Z), else along Z
        float _backSign;       // which side is the back (away from the user), in desk-local units

        // Busy crew stand in a row along the back edge, facing the user. Their "area" is a small square at their slot.
        bool AssignLineup(AlbieNavSurface desk, Transform head)
        {
            Vector2 size = desk.Size;
            float hx = size.x * 0.5f, hz = size.y * 0.5f;
            Vector3 l = Quaternion.Inverse(desk.Rotation) * ((head != null ? head.position : desk.TopCenter - desk.Rotation * Vector3.forward) - desk.TopCenter);
            _backAlongZ = Mathf.Abs(l.z) / Mathf.Max(0.01f, hz) >= Mathf.Abs(l.x) / Mathf.Max(0.01f, hx);
            _backSign = _backAlongZ ? (l.z >= 0f ? -1f : 1f) : (l.x >= 0f ? -1f : 1f);
            float halfAlong = (_backAlongZ ? hx : hz) - 0.06f, depth = (_backAlongZ ? hz : hx) - lineupInset;
            int n = _lineup.Count;
            float spacing = n > 1 ? Mathf.Min(lineupSpacing, 2f * halfAlong / (n - 1)) : 0f;
            for (int k = 0; k < n; k++)
            {
                AlbieCrewMember m = _lineup[k];
                if (m == null || m.Controller == null || m.Home) continue;
                float along = (k - (n - 1) * 0.5f) * spacing;
                Vector3 local = _backAlongZ ? new Vector3(along, 0f, _backSign * depth) : new Vector3(_backSign * depth, 0f, along);
                m.Controller.SetTerritory(desk, desk.TopCenter + desk.Rotation * local, desk.Rotation, new Vector2(0.1f, 0.1f));
            }
            return true;
        }

        // A desk area cut short so it stops before the line (kept as is if that would leave a sliver).
        AlbieTerritory InFrontOfLine(AlbieTerritory a, AlbieNavSurface desk)
        {
            Vector3 c = Quaternion.Inverse(desk.Rotation) * (a.Center - desk.TopCenter);
            float half = (_backAlongZ ? desk.Size.y : desk.Size.x) * 0.5f, limit = half - 2f * lineupInset;
            float mid = _backAlongZ ? c.z : c.x, ext = (_backAlongZ ? a.Size.y : a.Size.x) * 0.5f;
            float lo = mid - ext, hi = mid + ext;
            if (_backSign > 0f) hi = Mathf.Min(hi, limit); else lo = Mathf.Max(lo, -limit);
            if (hi - lo < 0.08f) return a;
            mid = (lo + hi) * 0.5f;
            if (_backAlongZ) { c.z = mid; a.Size.y = hi - lo; } else { c.x = mid; a.Size.x = hi - lo; }
            a.Center = desk.TopCenter + desk.Rotation * c;
            return a;
        }

        // Every Albie idles more while there's a crew; Albie's own profile comes back when the crew is gone.
        void ApplyLeadIdleProfile()
        {
            if (lead == null) return;
            bool calm = _members.Count > 0;
            if (calm == _leadCalm) return;
            if (calm)
            {
                lead.GetIdleProfile(out _leadRestChance, out _leadRestSeconds, out _leadRunChance, out _leadShortPause);
                lead.SetIdleProfile(restChance, restSeconds, runChance, shortPause);
            }
            else lead.SetIdleProfile(_leadRestChance, _leadRestSeconds, _leadRunChance, _leadShortPause);
            _leadCalm = calm;
        }

        // Albie wears a name tag too while there's a crew (solo Albie looks exactly as before).
        void ShowLeadTag(bool on)
        {
            if (lead == null) return;
            var tag = lead.GetComponent<AlbieNameTag>();
            if (tag == null)
            {
                if (!on) return;
                tag = lead.gameObject.AddComponent<AlbieNameTag>();
                tag.Set(PetConfig.DisplayName, new Color(0.3f, 0.78f, 0.42f));
            }
            tag.Show(on);
        }

        // ---------------------------------------------------------------- helpers

        AlbieController FindLead()
        {
            var all = FindObjectsByType<AlbieController>();
            for (int i = 0; i < all.Length; i++)
                if (all[i].GetComponent<AlbieCrewMember>() == null) return all[i];
            return null;
        }

        AlbieHatchery Hatchery()
        {
            if (_hatchery == null) _hatchery = FindAnyObjectByType<AlbieHatchery>();
            return _hatchery;
        }

        Vector3 DefaultSpawnPoint()
        {
            AlbieHatchery egg = Hatchery();
            if (egg != null && egg.Placed) return egg.SpawnPoint;
            Transform l = lead.transform;
            return l.position + l.right * 0.15f;
        }

        static AlbieNavSurface SurfaceAt(Vector3 p) => AlbieNavSurface.TryGetSurfaceAt(p, out AlbieNavSurface s) ? s : null;

        // Remove Albie-only components (and the voice child / Albie's runtime caption when copying the live Albie).
        void Strip(GameObject go, bool fromLiveLead)
        {
            for (int pass = 0; pass < 3; pass++)          // a component someone else requires goes on a later pass
            {
                bool removed = false;
                go.GetComponentsInChildren(true, _scratch);
                for (int i = 0; i < _scratch.Count; i++)
                {
                    MonoBehaviour mb = _scratch[i];
                    if (mb == null || !StripTypes.Contains(mb.GetType().Name)) continue;
                    GameObject owner = mb.gameObject;
                    DestroyImmediate(mb);
                    removed = true;
                    if (owner != go && owner.GetComponents<Component>().Length <= 2) DestroyImmediate(owner);   // e.g. the "Voice" child
                }
                if (!removed) break;
            }
            AudioSource[] sources = go.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++) DestroyImmediate(sources[i]);
            if (fromLiveLead)
            {
                foreach (string child in new[] { "AlbieCaption", "NameTag" })   // Albie's runtime bubble / tag: the copy builds its own
                {
                    Transform t = go.transform.Find(child);
                    if (t != null) DestroyImmediate(t.gameObject);
                }
            }
            _scratch.Clear();
        }
    }
}
