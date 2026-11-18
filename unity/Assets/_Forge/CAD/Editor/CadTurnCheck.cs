using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Edit-mode checks for turning the CAD model (<see cref="CadTurn"/>, Albie's cad_turn, <see cref="CadVoiceTools"/>), the
/// bug where "rotate the expanded humanoid" collapsed and re-expanded it instead: a stand-in model (cubes, no GLB) in a
/// preview scene, the real <see cref="CadExploder"/>, a stage pivot and a desk <see cref="ForgeVolume"/>, ticked by hand.
/// No Play mode, no hub. Forge > CAD > Check Turn logs the report; <see cref="Run"/> returns it (for `unity command eval`).
/// PASS on the first line means every check passed.
/// </summary>
public static class CadTurnCheck
{
    const float Dt = 1f / 72f;

    [MenuItem("Forge/CAD/Check Turn")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    /// <summary>For -executeMethod.</summary>
    public static void RunBatch()
    {
        string report = Run();
        Debug.Log(report);
        EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        bool ok = true;
        ok &= Check(sb, "expanded and exploded, a turn keeps the parts apart and changes the yaw about the centre", ExplodedTurn);
        ok &= Check(sb, "expand twice and explode twice are no-ops", Idempotent);
        ok &= Check(sb, "a turn during the explode and the trip to the stage lands where both meant", TurnMidAnimation);
        ok &= Check(sb, "face, spin, stop, tip and reset on the stage", StageWays);
        ok &= Check(sb, "on the desk it turns the volume, and a grab takes over", Desk);
        ok &= Check(sb, "every headset CAD tool answers at once, even with no CAD view", ToolsAnswer);
        return (ok ? "PASS" : "FAIL") + " CAD turn check\n" + sb;
    }

    static bool Check(StringBuilder sb, string name, Func<string> check)
    {
        string detail;
        bool ok;
        try { detail = check(); ok = true; }
        catch (Exception e) { detail = e.Message; ok = false; }
        sb.Append(ok ? "  ok   " : "  FAIL ").Append(name).Append(": ").Append(detail).Append('\n');
        return ok;
    }

    static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // ---------------------------------------------------------------- a stand-in stage

    sealed class Rig : IDisposable
    {
        public Scene scene;
        public Transform stage, pivot, holder;
        public CadModel model;
        public CadExploder exploder = new CadExploder();
        public CadTurn turn;
        public ForgeVolume volume;
        public readonly List<Transform> parts = new List<Transform>();

        public Rig(bool withVolume = false)
        {
            scene = EditorSceneManager.NewPreviewScene();
            stage = Make("CadStage", null).transform;
            stage.position = new Vector3(0.2f, 1.1f, 0.6f);
            stage.rotation = Quaternion.Euler(0f, 25f, 0f);
            pivot = Make("CadStageTurn", stage).transform;
            holder = Make("CadModel", pivot).transform;
            var root = Make("CAD v1", holder);
            var ids = new Dictionary<string, GameObject>();
            Vector3[] at = { new Vector3(0f, 0.30f, 0f), new Vector3(0.12f, 0.05f, 0.02f), new Vector3(-0.12f, 0.05f, -0.02f), new Vector3(0f, -0.25f, 0.05f) };
            for (int i = 0; i < at.Length; i++)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "PART_" + i;
                SceneManager.MoveGameObjectToScene(cube, scene);
                cube.transform.SetParent(root.transform, false);
                cube.transform.localPosition = at[i];
                cube.transform.localScale = Vector3.one * 0.06f;
                ids["PART_" + i] = cube;
                parts.Add(cube.transform);
            }
            model = CadModel.FromObjects(1, root, ids);
            exploder.Build(model, null, holder);
            if (withVolume)
            {
                var v = Make("CadViewer", null);
                var spin = Make("Spin", v.transform).transform;
                var content = Make("Content", spin).transform;
                volume = v.AddComponent<ForgeVolume>();
                var so = new SerializedObject(volume);
                so.FindProperty("spin").objectReferenceValue = spin;
                so.FindProperty("contentRoot").objectReferenceValue = content;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            turn = new CadTurn(volume);
            turn.BindStage(pivot);
            turn.SetStageBase(0f, 30f, false);  // a tall design: upright, turned 30 degrees to show a corner
        }

        GameObject Make(string name, Transform parent)
        {
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, scene);
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        public void Tick(int frames, bool onStage)
        {
            for (int i = 0; i < frames; i++) turn.Tick(Dt, onStage);
        }

        public string Do(CadTurnRequest r, bool onStage)
        {
            string did = turn.Apply(r, onStage, out string error);
            Expect(error == null, "cad_turn said: " + error);
            return did;
        }

        public Vector3[] Locals()
        {
            var p = new Vector3[parts.Count];
            for (int i = 0; i < p.Length; i++) p[i] = parts[i].localPosition;
            return p;
        }

        public float Spread()
        {
            float d = 0f;
            for (int i = 1; i < parts.Count; i++) d = Mathf.Max(d, Vector3.Distance(parts[i].position, parts[0].position));
            return d;
        }

        public void Dispose() => EditorSceneManager.ClosePreviewScene(scene);
    }

    static float Yaw(Quaternion q) => Mathf.DeltaAngle(0f, q.eulerAngles.y);

    // ---------------------------------------------------------------- the checks

    static string ExplodedTurn()
    {
        using (var rig = new Rig())
        {
            float together = rig.Spread();
            rig.exploder.SetTarget(true, instant: true);
            var apart = rig.Locals();
            float spread = rig.Spread();
            Expect(spread > together * 1.2f, $"the stand-in didn't explode ({together:0.000} -> {spread:0.000} m)");
            Vector3 centre = rig.pivot.position;
            string did = rig.Do(new CadTurnRequest(), onStage: true);  // "rotate it": 90 degrees
            rig.Tick(150, true);
            Expect(rig.exploder.Exploded && rig.exploder.Progress > 0.99f, "the explode collapsed");
            var after = rig.Locals();
            for (int i = 0; i < after.Length; i++)
                Expect((after[i] - apart[i]).sqrMagnitude < 1e-10f, $"part {i} moved in its parent ({apart[i]} -> {after[i]})");
            Expect(Mathf.Abs(rig.Spread() - spread) < 1e-4f, "the parts aren't as far apart as before");
            float yaw = Mathf.DeltaAngle(Yaw(rig.stage.rotation), Yaw(rig.pivot.rotation));
            Expect(Mathf.Abs(yaw - 90f) < 0.5f, $"turned {yaw:0.0} degrees, not 90");
            Expect((rig.pivot.position - centre).sqrMagnitude < 1e-10f, "the centre moved: it didn't turn about the model's centre");
            Expect(!rig.turn.Moving, "still easing after 2 s");
            return $"'{did}': yaw {yaw:0.0}, parts {spread:0.000} m apart before and after";
        }
    }

    static string Idempotent()
    {
        var go = new GameObject("CadSession (check)") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            var session = go.AddComponent<CadSession>();
            int changed = 0;
            session.InspectChanged += () => changed++;
            session.SetInspecting(true);
            session.SetInspecting(true);
            Expect(session.Inspecting && changed == 1, $"expand twice raised {changed} changes");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
        using (var rig = new Rig())
        {
            rig.exploder.SetTarget(true, instant: true);
            var apart = rig.Locals();
            rig.exploder.SetTarget(true);  // Explode again while exploded: no restart, nothing moves
            Expect(!rig.exploder.Moving && rig.exploder.Exploded, "a second explode restarted the animation");
            rig.exploder.Tick();
            var again = rig.Locals();
            for (int i = 0; i < again.Length; i++) Expect((again[i] - apart[i]).sqrMagnitude < 1e-10f, $"part {i} moved");
        }
        return "one InspectChanged for two expands; a second explode moves nothing";
    }

    static string TurnMidAnimation()
    {
        using (var rig = new Rig())
        {
            // The explode just started (moving) and the holder is half way up to the stage (Trip: lerping its local pose).
            rig.exploder.SetTarget(true);
            Vector3 endPosition = new Vector3(0.01f, -0.02f, 0f);
            Quaternion endRotation = Quaternion.Euler(0f, 210f, 0f);
            rig.holder.localPosition = endPosition * 0.5f + new Vector3(0f, 0.05f, 0f);
            rig.holder.localRotation = Quaternion.Slerp(Quaternion.identity, endRotation, 0.5f);
            rig.Do(new CadTurnRequest { degrees = 120f }, onStage: true);
            rig.Tick(10, true);
            rig.Do(new CadTurnRequest { degrees = 60f }, onStage: true);  // a second ask adds to the first
            rig.Tick(150, true);
            Expect(rig.exploder.Exploded, "the explode was undone");
            Expect(rig.holder.parent == rig.pivot, "the holder left the pivot");
            // The trip lands (its last frame sets the target pose in the parent); the model ends at stage * turn * target.
            rig.holder.localPosition = endPosition;
            rig.holder.localRotation = endRotation;
            Quaternion want = rig.stage.rotation * Quaternion.Euler(0f, 180f, 0f) * endRotation;
            float off = Quaternion.Angle(rig.holder.rotation, want);
            Expect(off < 0.5f, $"the model ended {off:0.0} degrees off stage x 180 x its stage pose");
            Expect(Mathf.Abs(rig.turn.StageYaw - 180f) < 1e-3f, $"the turns added to {rig.turn.StageYaw}, not 180");
            return "120 + 60 = 180 degrees on top of the stage pose; still exploding";
        }
    }

    static string StageWays()
    {
        using (var rig = new Rig())
        {
            rig.Do(new CadTurnRequest { face = "back" }, true);
            rig.Tick(150, true);
            float back = rig.turn.StageYaw;
            Expect(Mathf.Abs(Mathf.DeltaAngle(back + 30f, 180f)) < 0.01f, $"'show me the back' left a total yaw of {back + 30f}, not 180 past front");
            rig.Do(new CadTurnRequest { face = "front" }, true);
            rig.Tick(150, true);
            Expect(Mathf.Abs(Mathf.DeltaAngle(rig.turn.StageYaw + 30f, 0f)) < 0.01f, "'face me' didn't bring the front round");
            rig.Do(new CadTurnRequest { spin = true }, true);
            float before = rig.turn.StageYaw;
            rig.Tick(72, true);
            Expect(rig.turn.Spinning(true) && Mathf.Abs(rig.turn.StageYaw - before - CadTurn.SpinDegreesPerSecond) < 0.5f,
                $"a second of spinning turned {rig.turn.StageYaw - before:0.0} degrees");
            rig.Do(new CadTurnRequest { spin = false }, true);
            before = rig.turn.StageYaw;
            rig.Tick(72, true);
            Expect(!rig.turn.Spinning(true) && Mathf.Abs(rig.turn.StageYaw - before) < 1e-3f, "'stop' didn't stop it");
            rig.Do(new CadTurnRequest { degrees = 30f, axis = "side" }, true);
            rig.Tick(150, true);
            Expect(Mathf.Abs(rig.turn.StageTilt - 30f) < 1e-3f, "the tip didn't take");
            rig.Do(new CadTurnRequest { degrees = 400f, axis = "side" }, true);
            Expect(rig.turn.StageTilt <= CadTurn.MaxTilt, "tipped past the limit");
            rig.Do(new CadTurnRequest { reset = true }, true);
            rig.Tick(200, true);
            Expect(Quaternion.Angle(rig.pivot.localRotation, Quaternion.identity) < 0.5f, "reset didn't stand it back");
            rig.turn.SetStageBase(0f, 30f, lifeSize: true);
            Expect(rig.turn.Apply(new CadTurnRequest { degrees = 20f, axis = "side" }, true, out string refused) == null && refused != null,
                "at life size a tip should be refused (it would sink into the floor)");
            Expect(rig.turn.Apply(new CadTurnRequest { face = "top" }, true, out string bad) == null && bad != null, "face top should be an error");
            Expect(rig.turn.Apply(new CadTurnRequest { axis = "diagonal" }, true, out bad) == null && bad != null, "axis diagonal should be an error");
            return "back, front, spin 30 deg/s, stop, tip 30 (max " + CadTurn.MaxTilt + "), reset; bad sides and axes refused";
        }
    }

    static string Desk()
    {
        using (var rig = new Rig(withVolume: true))
        {
            rig.volume.SetView(10f, 0f);
            rig.Do(new CadTurnRequest { degrees = 90f }, onStage: false);
            rig.Tick(150, false);
            Expect(Mathf.Abs(Mathf.DeltaAngle(rig.volume.ViewYaw, 100f)) < 0.1f, $"the desk volume is at {rig.volume.ViewYaw:0.0}, not 100");
            rig.Do(new CadTurnRequest { degrees = 90f }, onStage: false);
            rig.Tick(3, false);
            rig.volume.SetView(42f, 0f);  // the user grabs the volume mid-turn
            rig.Tick(150, false);
            Expect(Mathf.Abs(rig.volume.ViewYaw - 42f) < 1e-3f, $"the turn fought the grab ({rig.volume.ViewYaw:0.0})");
            rig.Do(new CadTurnRequest { spin = true }, onStage: false);
            Expect(rig.volume.turntable && Mathf.Abs(rig.volume.turntableDegreesPerSecond - CadTurn.SpinDegreesPerSecond) < 1e-3f, "spin didn't start the turntable");
            rig.Do(new CadTurnRequest { spin = false }, onStage: false);
            Expect(!rig.volume.turntable, "stop didn't stop the turntable");
            var none = new CadTurn(null);
            Expect(none.Apply(new CadTurnRequest(), false, out string error) == null && error != null, "no volume should be an error, not a throw");
            return "turned 10 -> 100, let a grab win at 42, turntable on and off";
        }
    }

    static string ToolsAnswer()
    {
        var file = Resources.Load<TextAsset>("AlbieTools/" + CadVoiceTools.File);
        Expect(file != null, "no Resources/AlbieTools/" + CadVoiceTools.File + ".json");
        var defs = JArray.Parse(file.text);
        var cloud = JArray.Parse(Resources.Load<TextAsset>("AlbieTools/cloud").text);
        var cloudNames = new HashSet<string>();
        foreach (JObject d in cloud) cloudNames.Add((string)d["name"]);
        CadVoiceTools.Register();
        var opener = CadVoiceTools.OpenCadView;
        int opened = 0, answered = 0;
        try
        {
            var calls = new[]
            {
                new JObject(), new JObject { ["degrees"] = 180 }, new JObject { ["face"] = "back" }, new JObject { ["spin"] = true },
                new JObject { ["degrees"] = "lots" }, new JObject { ["axis"] = 7 },
            };
            foreach (JObject def in defs)
            {
                string name = (string)def["name"];
                Expect(name.StartsWith("cad_"), name + " isn't a CAD tool");
                Expect(!cloudNames.Contains(name), name + " is in cloud.json too: the cloud would take it over");
                Expect(AlbieTools.Has(name), name + " has no handler");
                foreach (bool canOpen in new[] { false, true })
                {
                    CadVoiceTools.OpenCadView = () => { opened++; return canOpen ? new JObject { ["ok"] = true, ["view"] = "cad" } : null; };
                    foreach (var args in calls)
                    {
                        JObject result = null;
                        int times = 0;
                        AlbieTools.CallAsync(name, args, r => { result = r; times++; });
                        Expect(times == 1 && result != null, $"{name} {args.ToString(Newtonsoft.Json.Formatting.None)} answered {times} times");
                        Expect(result["error"] != null || (bool?)result["ok"] == true, $"{name}: {result}");
                        answered++;
                    }
                }
            }
        }
        finally { CadVoiceTools.OpenCadView = opener; }
        return $"{defs.Count} tool(s), {answered} calls answered at once with no CAD view ({opened} asked to open it)";
    }
}
