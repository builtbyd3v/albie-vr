using System.Collections;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Artifacts
{
    /// <summary>
    /// SlalomSandbox only, hub off: "Make me a slalom" in a room, with no headset. Rooms are MRUK's own prefab rooms
    /// (the real MRUK code path: IsPositionInRoom, scene volumes, labels) or the mock rooms the checks use.
    ///
    /// Keys: 2 make me a slalom (the artifact_create tool), 3 arm a run, 4 drive a scripted lap, 5 roll over,
    /// WASD drive the truck, R relayout, [ ] spacing -/+ 0.1 m, G gates (3 to 8), M next MRUK room, N next mock room,
    /// O overhead / first-person view, 0 close, 9 forget approvals.
    /// </summary>
    public sealed class SlalomSandbox : MonoBehaviour
    {
        public ArtifactHost host;
        public GameObject[] mrukRooms;
        public Camera overhead;
        public Material floorMaterial, wallMaterial, furnitureMaterial;

        readonly MockFloorPlan[] mocks = MockFloorPlan.All();
        int mock = -1, mruk = -1;
        GameObject mockView, thingGo;
        ArtifactThing thing;
        Coroutine lap;
        string roomName = "", status = "", say = "";
        GUIStyle style;

        public IFloorPlan Room => host.Room;
        public string Status => status;
        public string Say => say;

        void Start()
        {
            if (overhead != null) overhead.gameObject.SetActive(true);
            if (mrukRooms != null && mrukRooms.Length > 0 && MRUK.Instance != null) LoadMruk(0);
            else UseMock(0);
        }

        void Update()
        {
            var k = Keyboard.current;
            if (k == null) return;
            if (k.digit2Key.wasPressedThisFrame) MakeSlalom();
            if (k.digit3Key.wasPressedThisFrame) Report(Tool("artifact_run", new JObject()));
            if (k.digit4Key.wasPressedThisFrame) ScriptedLap();
            if (k.digit5Key.wasPressedThisFrame) RollOver();
            if (k.rKey.wasPressedThisFrame) Report(Tool("artifact_relayout", new JObject()));
            if (k.leftBracketKey.wasPressedThisFrame) Nudge("spacing_m", -0.1);
            if (k.rightBracketKey.wasPressedThisFrame) Nudge("spacing_m", 0.1);
            if (k.gKey.wasPressedThisFrame) CycleGates();
            if (k.mKey.wasPressedThisFrame) LoadMruk(mruk + 1);
            if (k.nKey.wasPressedThisFrame) UseMock(mock + 1);
            if (k.oKey.wasPressedThisFrame && overhead != null) overhead.gameObject.SetActive(!overhead.gameObject.activeSelf);
            if (k.digit0Key.wasPressedThisFrame) host.Close();
            if (k.digit9Key.wasPressedThisFrame) ArtifactApprovals.Forget();
            Drive(k);
        }

        /// <summary>What Albie does for "Make me a slalom".</summary>
        public JObject MakeSlalom()
        {
            var result = Tool("artifact_create", new JObject { ["template"] = "slalom" });
            Report(result);
            if (result["error"] == null) SpawnThing();
            return result;
        }

        public void UseMock(int index)
        {
            host.Close();
            ClearThing();
            ClearMruk();
            mock = ((index % mocks.Length) + mocks.Length) % mocks.Length;
            host.Room = mocks[mock];
            roomName = mocks[mock].Name;
            DrawMock(mocks[mock]);
            status = "room: " + roomName + " (press 2 to make a slalom)";
        }

        public async void LoadMruk(int index)
        {
            if (mrukRooms == null || mrukRooms.Length == 0 || MRUK.Instance == null) { status = "no MRUK rooms in this scene"; return; }
            host.Close();
            ClearThing();
            ClearMock();
            mruk = ((index % mrukRooms.Length) + mrukRooms.Length) % mrukRooms.Length;
            status = "loading " + mrukRooms[mruk].name + "...";
            await MRUK.Instance.LoadSceneFromPrefab(mrukRooms[mruk]);
            var room = MRUK.Instance.Rooms.Count > 0 ? MRUK.Instance.Rooms[0] : null;
            host.Room = room != null ? new MrukFloorPlan(room) : null;
            roomName = room != null ? "MRUK " + mrukRooms[mruk].name : "MRUK (no room)";
            status = "room: " + roomName + " (press 2 to make a slalom)";
        }

        void Nudge(string input, double by)
        {
            var inputs = host.Inputs;
            if (inputs == null || !inputs.Has(input)) { status = "open the slalom first"; return; }
            double v = System.Math.Round(inputs.Value(input) + by, 2);
            status = inputs.TrySet(input, v) ? $"{input} = {v} (relayout in a moment)" : $"{input} {v} is out of range";
        }

        void CycleGates()
        {
            var inputs = host.Inputs;
            if (inputs == null || !inputs.Has("gates")) { status = "open the slalom first"; return; }
            double next = inputs.Value("gates") >= 8 ? 3 : inputs.Value("gates") + 1;
            inputs.TrySet("gates", next);
            status = $"gates = {next} (relayout in a moment)";
        }

        static JObject Tool(string name, JObject args) => AlbieTools.Has(name) ? AlbieTools.Call(name, args) : AlbieTools.Error(name + " isn't registered");

        void Report(JObject result)
        {
            if (result["error"] != null) { status = "refused: " + result["error"]; say = ""; return; }
            var opened = result["opened"] as JObject ?? result;
            say = (string)opened["say"] ?? "";
            status = opened.ToString(Newtonsoft.Json.Formatting.None);
        }

        void SpawnThing()
        {
            if (thingGo == null)
            {
                thingGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                thingGo.name = "Thing";
                thingGo.transform.localScale = new Vector3(0.22f, 0.1f, 0.3f);
                var renderer = thingGo.GetComponent<Renderer>();
                if (renderer != null) renderer.sharedMaterial = ArtifactLook.Material("#4C8DFF");
                thing = thingGo.AddComponent<ArtifactThing>();
            }
            host.Thing = thing;
            var at = host.SceneOrigin;
            thing.Place(at.position + Vector3.up * 0.06f, at.rotation);
        }

        void Drive(Keyboard k)
        {
            if (thing == null) return;
            float dt = Time.deltaTime, speed = 1.4f, turn = 90f;
            var f = thing.transform.forward; f.y = 0f; f.Normalize();
            if (k.wKey.isPressed) thing.transform.position += f * speed * dt;
            if (k.sKey.isPressed) thing.transform.position -= f * speed * dt;
            if (k.aKey.isPressed) thing.transform.Rotate(0f, -turn * dt, 0f);
            if (k.dKey.isPressed) thing.transform.Rotate(0f, turn * dt, 0f);
        }

        void ScriptedLap()
        {
            if (host.Run == null || host.Build == null) { status = "open the slalom first"; return; }
            if (lap != null) StopCoroutine(lap);
            SpawnThing();
            lap = StartCoroutine(DriveLap());
        }

        IEnumerator DriveLap()
        {
            Report(Tool("artifact_run", new JObject()));
            var origin = host.SceneOrigin;
            Vector3 World(Pose local) => origin.position + origin.rotation * local.position + Vector3.up * 0.06f;
            thing.Place(World(new Pose(Vector3.zero, Quaternion.identity)), origin.rotation);
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            if (host.Build.Slots.TryGetValue("course.gates", out var gates))
                foreach (var gate in gates)
                {
                    thing.Place(World(gate), origin.rotation);
                    yield return new WaitForFixedUpdate();
                    yield return new WaitForFixedUpdate();
                }
            if (host.Build.Slots.TryGetValue("course.finish", out var finish) && finish.Count > 0)
            {
                thing.Place(World(finish[0]), origin.rotation);
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
            }
            var run = host.Run;
            status = run != null ? $"run {run.RunId} {run.State} {run.Outcome}" : status;
            lap = null;
        }

        void RollOver()
        {
            if (thing == null) { status = "open the slalom first"; return; }
            thing.transform.rotation = Quaternion.Euler(180f, thing.transform.eulerAngles.y, 0f);
            status = "rolled over (upright should fail the run in 1 s)";
        }

        // ---- Room views -----------------------------------------------------------------------------------------

        void ClearThing()
        {
            if (lap != null) { StopCoroutine(lap); lap = null; }
            if (thingGo != null) Destroy(thingGo);
            thingGo = null;
            thing = null;
            host.Thing = null;
        }

        void ClearMock()
        {
            if (mockView != null) Destroy(mockView);
            mockView = null;
            mock = -1;
        }

        void ClearMruk()
        {
            if (MRUK.Instance != null && MRUK.Instance.Rooms.Count > 0) MRUK.Instance.ClearScene();
            mruk = -1;
        }

        void DrawMock(MockFloorPlan room)
        {
            if (mockView != null) Destroy(mockView);
            mockView = new GameObject("Mock room " + room.Id);
            var frame = mockView.transform;
            frame.SetPositionAndRotation(room.Origin, Quaternion.Euler(0f, room.Yaw, 0f));
            if (!room.Scanned) return;  // an unscanned room has nothing to draw
            Box(frame, "Floor", new Vector3(0f, -0.01f, 0f), new Vector3(room.Size.x, 0.02f, room.Size.y), 0f, floorMaterial);
            const float h = 0.4f, t = 0.05f;
            Box(frame, "Wall", new Vector3(0f, h / 2, room.Size.y / 2), new Vector3(room.Size.x, h, t), 0f, wallMaterial);
            Box(frame, "Wall", new Vector3(0f, h / 2, -room.Size.y / 2), new Vector3(room.Size.x, h, t), 0f, wallMaterial);
            Box(frame, "Wall", new Vector3(room.Size.x / 2, h / 2, 0f), new Vector3(t, h, room.Size.y), 0f, wallMaterial);
            Box(frame, "Wall", new Vector3(-room.Size.x / 2, h / 2, 0f), new Vector3(t, h, room.Size.y), 0f, wallMaterial);
            foreach (var item in room.Items) Box(frame, item.Label, item.Centre, item.Size, item.Yaw, furnitureMaterial);
        }

        static void Box(Transform parent, string name, Vector3 at, Vector3 size, float yaw, Material material)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            Destroy(box.GetComponent<Collider>());
            box.transform.SetParent(parent, false);
            box.transform.localPosition = at;
            box.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            box.transform.localScale = size;
            if (material != null) box.GetComponent<Renderer>().sharedMaterial = material;
        }

        void OnGUI()
        {
            if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, wordWrap = true };
            style.normal.textColor = Color.white;
            string layout = host.LastLayout != null ? $"   layout {host.LastLayout["layout_id"]}{(host.LayoutReused ? " (reused)" : " (new)")}" : "";
            string run = host.Run != null ? $"   run {host.Run.State.ToString().ToLowerInvariant()}" + (host.Run.Outcome != null ? " " + host.Run.Outcome : "") : "";
            GUI.Label(new Rect(14, 72, Screen.width - 28, 72),  // below the desktop rig's hint bar
                $"<b>Slalom sandbox</b>   {roomName}{layout}{run}\n2 make me a slalom   3 arm a run   4 scripted lap   5 roll over   WASD drive   R relayout   [ ] spacing   G gates   M/N room   O view   0 close", style);
            if (!string.IsNullOrEmpty(say)) GUI.Label(new Rect(14, Screen.height - 80, Screen.width - 28, 30), "<b>Albie:</b> " + say, style);
            GUI.Label(new Rect(14, Screen.height - 48, Screen.width - 28, 44), status, style);
        }
    }
}

namespace Forge.Artifacts
{
    /// <summary>
    /// SlalomSandbox only: tells MRUK a camera rig exists, so it doesn't log an error in a desktop scene with no XR rig.
    /// It builds nothing and stays disabled; MRUK only reads its tracking space, which is null, the same as no rig.
    /// </summary>
    public sealed class SandboxCameraRig : OVRCameraRig
    {
        protected override void Awake() { }
    }
}
