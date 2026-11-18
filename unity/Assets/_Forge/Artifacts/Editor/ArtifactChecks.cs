using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Forge.Artifacts;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Forge > Check Artifacts: the C-12 rules against the fixtures, plus one deliberate mistake per error code. Tests live
/// here because test assemblies can't see Assembly-CSharp. In batch mode, exits non-zero if anything failed.
/// </summary>
public static class ArtifactChecks
{
    static int passed;
    static readonly List<string> failures = new List<string>();

    static void Check(bool ok, string what)
    {
        if (ok) passed++;
        else failures.Add(what);
    }

    static void Expect(string code, Action<JObject, JObject> mistake, string fixture = "slalom")
    {
        var p = ArtifactPackage.Load(fixture, out _);
        mistake(p.Manifest, p.Scene);
        var errors = ArtifactValidator.Validate(p.Manifest, p.Scene);
        Check(errors.Any(e => e.code == code), $"expected {code}, got [{string.Join("; ", errors)}]");
    }

    static JObject Node(JObject scene, string id) => (JObject)((JArray)scene["nodes"]).First(n => n.Value<string>("id") == id);

    [MenuItem("Forge/Check Artifacts")]
    public static void Run()
    {
        passed = 0;
        failures.Clear();
        Fixtures();
        Mistakes();
        Refusals();
        Inputs();
        Approvals();
        Looks();
        Exploration();
        Physics();
        Placement();
        foreach (var f in failures) Debug.LogError("[ArtifactChecks] FAIL " + f);
        Debug.Log($"[ArtifactChecks] {passed} passed, {failures.Count} failed");
    }

    /// <summary>For -executeMethod.</summary>
    public static void RunBatch()
    {
        Run();
        EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
    }

    static void Exploration()
    {
        var root = new GameObject("explorer check");
        root.transform.localPosition = new Vector3(0.2f, 0f, 0f);
        var mesh = new Mesh();
        mesh.vertices = new[] { new Vector3(0, 0, -1), new Vector3(1, 0, -1), new Vector3(0, 1, -1),
            new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(0, 1, 1) };
        mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
        mesh.RecalculateBounds();
        try
        {
            var child = new GameObject("surface", typeof(MeshFilter), typeof(MeshRenderer));
            child.transform.SetParent(root.transform, false);
            var filter = child.GetComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var explorer = root.AddComponent<ArtifactExplorer>();
            explorer.Initialize();
            Check(explorer.PartCount == 1, "single mesh is not advertised as segmented anatomy");
            explorer.Section();
            Check(filter.sharedMesh != mesh && filter.sharedMesh.triangles.Length == 3, "section retains only the back surface");
            Check(mesh.triangles.Length == 6, "section leaves imported geometry intact");
            explorer.Turn();
            explorer.ResetView();
            Check(filter.sharedMesh == mesh && root.transform.localRotation == Quaternion.identity && root.transform.localPosition == new Vector3(0.2f, 0f, 0f),
                "reset restores the original surface and pose");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
        ExplorationParts();
        HandGrab();
        HandGrabParts();
        VoiceButtons();
        VoiceExplore();
        IsolateHeartAndBrain();
        PointedAtParts();
        AutoParts();
    }

    // ---- isolate on the heart and brain, by their real part names ----------------------------------------------
    // The parts are cubes named as the bundled GLB's nodes are (read from the file), each with an explode marker as the
    // real ones have, so the words in parts.json and the isolate path run on the real names without loading glTF.
    static List<string> GlbParts(string template)
    {
        var names = new List<string>();
        var p = ArtifactPackage.Load(template, out _);
        var glb = p?.ReadAsset((string)p.Manifest["assets"][0]["path"], out _);
        if (glb == null) return names;
        var json = JObject.Parse(System.Text.Encoding.UTF8.GetString(glb, 20, BitConverter.ToInt32(glb, 12)));
        foreach (JObject n in (JArray)json["nodes"]) if (n["mesh"] != null) names.Add((string)n["name"]);
        return names;
    }

    static ArtifactExplorer StandIn(string template, GameObject root, out List<string> names)
    {
        names = GlbParts(template);
        for (int i = 0; i < names.Count; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.name = names[i];
            go.transform.SetParent(root.transform, false);
            float a = i * Mathf.PI * 2f / names.Count;
            go.transform.localPosition = new Vector3(Mathf.Cos(a) * 0.12f, Mathf.Sin(a) * 0.12f, 0f);
            go.transform.localScale = Vector3.one * 0.03f;
            var marker = new GameObject(ArtifactExplorer.ExplodeMarker).transform;
            marker.SetParent(go.transform, false);
            marker.localPosition = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * 2f;
        }
        var e = root.AddComponent<ArtifactExplorer>();
        e.Words = ArtifactExplorer.WordsFor(template);
        e.Initialize();
        return e;
    }

    static List<string> Chosen(ArtifactExplorer e)
    {
        var list = new List<string>();
        foreach (var t in e.Chosen) list.Add(ArtifactExplorer.NameOf(t));
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    static string Isolated(ArtifactExplorer e, string part, out JObject into, out string error)
    {
        into = new JObject();
        return ArtifactVoiceTools.Act(null, e, "isolate", new JObject { ["part"] = part }, into, out error);
    }

    static void ExpectIsolated(ArtifactExplorer e, string template, string said, params string[] want)
    {
        e.Unfocus();
        Isolated(e, said, out var into, out string error);
        var w = new List<string>(want); w.Sort(StringComparer.Ordinal);
        var got = Chosen(e);
        Check(error == null && string.Join("|", got) == string.Join("|", w), $"{template}: \"{said}\" isolates {string.Join(", ", w)}, got [{string.Join(", ", got)}] {error}");
    }

    static void IsolateHeartAndBrain()
    {
        var root = new GameObject("isolate heart check");
        try
        {
            var e = StandIn("heart", root, out var names);
            Check(names.Count == 17 && e.PartCount == 17 && !e.AutoNamed, $"the heart stand-in has the GLB's 17 named parts ({names.Count})");
            Check(e.Words != null && e.Words.About("Left ventricle") != null, "the heart's parts.json loads, with what each part does");
            foreach (var n in names) Check(e.Words.About(n) != null, "parts.json describes the heart's " + n);

            var lv = root.transform.Find("Left ventricle");
            var aorta = root.transform.Find("Aorta").GetComponent<Renderer>();
            var lvMat = lv.GetComponent<Renderer>().sharedMaterial;
            var aortaMat = aorta.sharedMaterial;
            Vector3 lvAt = lv.localPosition, lvSize = lv.localScale;
            string did = Isolated(e, "just the left ventricle", out var into, out string error);
            e.Finish();
            Check(error == null && e.Focused == lv && e.Chosen.Count == 1, $"\"just the left ventricle\" isolates it in one call: {did} {error}");
            Check(lv.GetComponent<Renderer>().sharedMaterial == lvMat && aorta.sharedMaterial != aortaMat && aorta.sharedMaterial.GetColor("_BaseColor").a < 0.1f,
                "the rest fades to a faint silhouette while the left ventricle stays solid");
            Check(lv.localScale.x > lvSize.x * 1.2f && (lv.localPosition - lvAt).magnitude > 1e-4f, "the left ventricle comes forward and grows");
            Check((string)into["about"]?["Left ventricle"] != null, "Albie is told what the left ventricle does");
            var label = root.transform.Find("labels/label Left ventricle");
            Check(label != null && label.gameObject.activeSelf && label.GetComponent<TMPro.TMP_Text>().text.Contains("pumps"),
                "its callout shows its name and what it does");
            Check(root.transform.Find("labels/label Aorta") != null && !root.transform.Find("labels/label Aorta").gameObject.activeSelf, "the other parts' names stay hidden");
            ArtifactVoiceTools.Act(null, e, "unfocus", new JObject(), out error);
            e.Finish();
            Check(!e.Isolating && aorta.sharedMaterial == aortaMat && (lv.localPosition - lvAt).magnitude < 1e-5f && (lv.localScale - lvSize).magnitude < 1e-6f,
                "\"show all\" puts every part back as it was");

            ExpectIsolated(e, "heart", "the valves", "Tricuspid valve", "Pulmonary valve", "Mitral valve", "Aortic valve");
            ExpectIsolated(e, "heart", "the upper chambers", "Left atrium", "Right atrium");
            ExpectIsolated(e, "heart", "pumping chambers", "Left ventricle", "Right ventricle");
            ExpectIsolated(e, "heart", "the coronary arteries", "Left coronary artery", "Right coronary artery");
            ExpectIsolated(e, "heart", "the aorta and the mitral valve", "Aorta", "Mitral valve");
            ExpectIsolated(e, "heart", "the heart's left side", "Pulmonary veins", "Left atrium", "Mitral valve", "Left ventricle", "Aortic valve", "Aorta");
            ExpectIsolated(e, "heart", "chordae", "Papillary muscles");

            e.Unfocus();
            Isolated(e, "the valve", out into, out error);
            Check(error == null && !e.Isolating && into["options"] is JArray opts && opts.Count == 4 && ((string)into["say"]).Contains("Mitral valve"),
                "\"the valve\" asks which one, listing the four, and changes nothing");
            Isolated(e, "spleen", out into, out error);
            Check(error != null && error.Contains("Left ventricle") && error.Contains("chambers") && !e.Isolating,
                "a part the heart doesn't have says so and lists the real parts and groups");
            e.ResetView();
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }

        root = new GameObject("isolate brain check");
        try
        {
            var e = StandIn("brain", root, out var names);
            Check(names.Count == 22 && e.PartCount == 22, $"the brain stand-in has the GLB's 22 named parts ({names.Count})");
            foreach (var n in names) Check(e.Words.About(n) != null, "parts.json describes the brain's " + n);
            ExpectIsolated(e, "brain", "the frontal lobe", "Left frontal lobe", "Right frontal lobe");
            ExpectIsolated(e, "brain", "left frontal lobe", "Left frontal lobe");
            ExpectIsolated(e, "brain", "the frontal and temporal lobes", "Left frontal lobe", "Right frontal lobe", "Left temporal lobe", "Right temporal lobe");
            ExpectIsolated(e, "brain", "back of the brain", "Left occipital lobe", "Right occipital lobe");
            ExpectIsolated(e, "brain", "the little brain", "Cerebellum");
            ExpectIsolated(e, "brain", "brain stem", "Brainstem");
            ExpectIsolated(e, "brain", "the hippocampus", "Hippocampus and fornix");
            ExpectIsolated(e, "brain", "the lobes", "Left frontal lobe", "Right frontal lobe", "Left parietal lobe", "Right parietal lobe",
                "Left temporal lobe", "Right temporal lobe", "Left occipital lobe", "Right occipital lobe");
            ExpectIsolated(e, "brain", "the right hemisphere", "Right frontal lobe", "Right parietal lobe", "Right temporal lobe", "Right occipital lobe",
                "Right insula", "Right limbic lobe", "Right white matter");
            e.Unfocus();
            Isolated(e, "the lobe", out var into, out string error);
            Check(error == null && !e.Isolating && into["options"] is JArray o && o.Count == 8, "\"the lobe\" on its own asks which of the eight");
            var thalamusMat = root.transform.Find("Thalamus").GetComponent<Renderer>().sharedMaterial;
            var highlight = new JObject();
            ArtifactVoiceTools.Act(null, e, "isolate", new JObject { ["part"] = "cerebellum", ["mode"] = "highlight" }, highlight, out error);
            var thalamus = root.transform.Find("Thalamus").GetComponent<Renderer>().sharedMaterial;
            Check(error == null && e.Mode == ArtifactExplorer.Look.Highlight && thalamus.GetColor("_BaseColor").a > 0.1f && thalamus.GetColor("_BaseColor").a < 0.5f,
                "mode highlight keeps the rest see-through (X-ray strength), not faint");
            e.ResetView();
            Check(!e.Isolating && root.transform.Find("Thalamus").GetComponent<Renderer>().sharedMaterial == thalamusMat, "reset clears it");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    // ---- "this": the part a ray is on --------------------------------------------------------------------------
    static void PointedAtParts()
    {
        var root = new GameObject("pointing check");
        try
        {
            var e = StandIn("brain", root, out _);
            var pointer = root.AddComponent<ArtifactPartPointer>();
            pointer.Bind(e);
            Check(e.Pointer == pointer && e.AddPickers(), "every part gets a picking collider");
            UnityEngine.Physics.SyncTransforms();
            var lobe = root.transform.Find("Right temporal lobe");
            var ray = new Ray(lobe.position + Vector3.back, Vector3.forward);
            Check(pointer.Cast(ray) == lobe, $"a ray at the right temporal lobe is on it, got {ArtifactExplorer.NameOf(pointer.Cast(ray)) ?? "nothing"}");
            Check(pointer.Cast(new Ray(new Vector3(5f, 5f, -1f), Vector3.forward)) == null, "a ray past the model is on nothing");
            pointer.Cast(ray);
            long bytes = AllocationCounter.BytesDuring(() => { for (int i = 0; i < 200; i++) pointer.Cast(ray); });
            Check(bytes == 0, $"casting at the parts allocates nothing ({bytes} bytes over 200 casts)");

            float now = Time.unscaledTime;
            pointer.Note(lobe, ArtifactPartPointer.How.Ray, now);
            Check(pointer.This(now + 1f, out var how) == lobe && how == ArtifactPartPointer.How.Ray, "what was pointed at is still \"this\" a second later");
            Check(pointer.This(now + ArtifactPartPointer.HoldSeconds + 1f, out _) == null, "and no longer after the hold");
            var looked = root.transform.Find("Cerebellum");
            pointer.Note(looked, ArtifactPartPointer.How.Gaze, now);
            Check(pointer.This(now, out how) == lobe && how == ArtifactPartPointer.How.Ray, "pointing wins over looking");

            pointer.Note(lobe, ArtifactPartPointer.How.Ray, Time.unscaledTime);
            string did = Isolated(e, "this lobe", out var into, out string error);
            Check(error == null && e.Focused == lobe && e.Chosen.Count == 1 && (string)into["this"] == "Right temporal lobe",
                $"\"show me this lobe\" isolates the lobe pointed at: {did} {error}");
            e.Finish();
            UnityEngine.Physics.SyncTransforms();
            var other = root.transform.Find("Left frontal lobe");
            Check(pointer.Cast(new Ray(other.position + Vector3.back, Vector3.forward)) == null, "a part faded out by isolate can't be pointed at");
            e.ResetView();

            var fresh = root.AddComponent<ArtifactPartPointer>();
            fresh.Bind(e);  // nothing pointed at or looked at yet
            Isolated(e, "this", out into, out error);
            Check(error != null && error.StartsWith("nothing_pointed") && error.Contains("Cerebellum"), "\"this\" with nothing pointed at asks them to point, and lists the parts");
            e.SetHover(lobe);
            Check(lobe.GetComponent<Renderer>().HasPropertyBlock(), "the part under the pointer is lit a little");
            e.SetHover(null);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    // ---- parts for an imported model, from its node tree ---------------------------------------------------------
    static GameObject MeshNode(string name, Transform parent, Vector3 at, float size, PrimitiveType shape = PrimitiveType.Cube)
    {
        var go = GameObject.CreatePrimitive(shape);
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = at;
        go.transform.localScale = Vector3.one * size;
        return go;
    }

    static Transform Node(string name, Transform parent, Vector3 at = default)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = at;
        return t;
    }

    static void AutoParts()
    {
        // As Sketchfab exports a model: wrappers, then named nodes each holding a mesh called Object_N, a bare mesh, and
        // a few bolts far too small to be parts of their own.
        var root = new GameObject("auto parts check");
        try
        {
            var scene = Node("GLTF_SceneRootNode", Node("root", Node("Sketchfab_model", root.transform)));
            MeshNode("Object_4", Node("Fan_Blades_2", scene, new Vector3(0f, 0.2f, 0f)), Vector3.zero, 0.2f);
            MeshNode("Object_6", Node("nozzle-cone_3", scene, new Vector3(0f, -0.2f, 0f)), Vector3.zero, 0.15f, PrimitiveType.Cylinder);
            MeshNode("Object_8", scene, new Vector3(0.25f, 0f, 0f), 0.15f);
            MeshNode("Object_9", scene, new Vector3(-0.25f, 0f, 0f), 0.15f);
            for (int i = 0; i < 3; i++) MeshNode("bolt_" + i, scene, new Vector3(0.02f * i, 0.31f, 0f), 0.005f);
            var e = root.AddComponent<ArtifactExplorer>();
            e.Initialize();
            var names = e.PartNames();
            Check(e.AutoNamed && e.PartCount == 4, $"an imported model's parts come from its nodes, bolts merged in: [{string.Join(", ", names)}]");
            Check(names.Contains("Fan blades") && names.Contains("Nozzle cone"), "parts take readable names from their nodes");
            Check(names.Exists(n => n.StartsWith("Part ") && n.Contains("right")) && names.Exists(n => n.StartsWith("Part ") && n.Contains("left")),
                "unnamed parts are \"Part N\" with where they sit");
            var fan = e.FindPart("fan blades");
            Vector3 fanAt = fan != null ? fan.localPosition : Vector3.zero;
            ArtifactVoiceTools.Act(null, e, "separate", new JObject(), out string error);
            e.Finish();
            Check(error == null && e.Separated && fan != null && fan.localPosition.y > fanAt.y + 1e-4f, "it separates, the fan blades moving up and out from the centre");
            e.ResetView();
            Isolated(e, "the nozzle", out _, out error);
            Check(error == null && ArtifactExplorer.NameOf(e.Focused) == "Nozzle cone", "an imported part can be isolated by name: " + error);
            e.ResetView();
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }

        // One mesh under wrappers: one solid piece, and it says so.
        root = new GameObject("one piece check");
        try
        {
            MeshNode("ControlConsoleChair_Body_Placeable.001_0", Node("ControlConsoleChair", Node("RootNode", Node("Sketchfab_model", root.transform))), Vector3.zero, 0.3f);
            var e = root.AddComponent<ArtifactExplorer>();
            e.Initialize();
            ArtifactVoiceTools.Act(null, e, "separate", new JObject(), out string error);
            Check(e.PartCount == 1 && error != null && error.Contains("one solid piece"), "a one-mesh model is one solid piece and says it can't come apart");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }

        // Sixty meshes in a row: no more than MaxAutoParts parts, still every mesh in one of them.
        root = new GameObject("many meshes check");
        try
        {
            var group = Node("group1", root.transform);
            for (int i = 0; i < 60; i++) MeshNode("mesh_" + i, group, new Vector3(i * 0.05f, 0f, 0f), 0.04f);
            Node("empty_locator", group);
            var e = root.AddComponent<ArtifactExplorer>();
            e.Initialize();
            Check(e.PartCount >= 2 && e.PartCount <= ArtifactExplorer.MaxAutoParts, $"sixty meshes become at most {ArtifactExplorer.MaxAutoParts} parts ({e.PartCount})");
            var placed = new HashSet<string>(e.PartNames());
            Check(placed.Count == e.PartCount, "every part has its own name");
            e.Separate();
            e.Finish();
            e.Place(1f);
            e.LayOutLabels(new Vector3(0f, 0f, -2f));
            long bytes = AllocationCounter.BytesDuring(() => { for (int i = 0; i < 100; i++) { e.Place(1f); e.LayOutLabels(new Vector3(0f, 0f, -2f)); } });
            Check(bytes == 0, $"placing the parts and their names allocates nothing ({bytes} bytes over 100 frames)");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    // ---- voice: what a spoken request presses on each biology template, and the captions Albie narrates ------------
    static void VoiceButtons()
    {
        void Expect(string template, string said, string input)
        {
            ArtifactLibrary.TryTemplate(template, out _, out var scene);
            string got = ArtifactVoiceTools.InputFor(scene, said, out _);
            Check(got == input, $"{template}: \"{said}\" presses {input}, got {got ?? "nothing"}");
        }
        Expect("heart", "show the blood flow", "flow_on");
        Expect("heart", "hide the blood flow", "flow_off");
        Expect("heart", "trace the blood", "trace");
        Expect("heart", "explain how the blood flows", "trace");
        Expect("heart", "pause the beat", "beat_pause");
        Expect("heart", "hide labels", "labels_off");
        Expect("eye", "follow the light", "light");
        Expect("eye", "how does the eye see", "light");
        Expect("brain", "tour the lobes", "tour");
        Expect("cell", "follow a protein", "follow");
        Expect("plant_cell", "follow the sunlight", "sun");
        Expect("dna", "explain it", "explain");
        Expect("dna", "spin it", "spin_on");
        Expect("dna", "stop spinning", "spin_off");
        ArtifactLibrary.TryTemplate("heart", out _, out var heart);
        var steps = ArtifactVoiceTools.WalkthroughFor(heart, "trace");
        Check(steps.Count == 4 && ((string)steps[0]["text"]).StartsWith("Oxygen-poor") && (double)steps[3]["at_s"] > (double)steps[1]["at_s"],
            $"tracing the blood gives Albie the four steps in order to explain ({steps.Count})");
        ArtifactLibrary.TryTemplate("eye", out _, out var eye);
        Check(ArtifactVoiceTools.WalkthroughFor(eye, "light").Count == 4, "following the light gives the eye's four steps");
        Check(ArtifactVoiceTools.InputFor(heart, "make me a sandwich", out _) == null, "a request that's no button presses nothing");
    }

    static void VoiceExplore()
    {
        var root = new GameObject("voice explore check");
        try
        {
            var names = new[] { "Mitral valve.001", "Left ventricle", "Right ventricle" };
            for (int i = 0; i < names.Length; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = names[i];
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = new Vector3(i * 0.3f, 0f, 0f);
                go.transform.localScale = Vector3.one * (i == 0 ? 0.1f : 0.25f);
            }
            var e = root.AddComponent<ArtifactExplorer>();
            e.Initialize();
            Check(e.FindPart("the mitral valve")?.name == "Mitral valve.001", "\"the mitral valve\" finds the Mitral valve");
            Check(e.FindPart("left ventricle")?.name == "Left ventricle", "\"left ventricle\" finds the left one, not the right");
            Check(e.FindPart("ventricle") != null && e.FindPart("spleen") == null, "a part that isn't there isn't found");

            ArtifactVoiceTools.Act(null, e, "separate", new JObject(), out string error);
            Check(error == null && e.Separated, "\"expand it\" separates the model");
            ArtifactVoiceTools.Act(null, e, "separate", new JObject(), out error);
            Check(e.Separated, "asking to expand an expanded model leaves it apart (it doesn't toggle back)");
            ArtifactVoiceTools.Act(null, e, "cut_open", new JObject(), out error);
            Check(e.Sectioned, "\"cut it open\" cuts it");

            var valve = root.transform.Find("Mitral valve.001").GetComponent<Renderer>();
            var ventricle = root.transform.Find("Left ventricle").GetComponent<Renderer>();
            var valveMat = valve.sharedMaterial; var ventricleMat = ventricle.sharedMaterial;
            string did = ArtifactVoiceTools.Act(null, e, "focus", new JObject { ["part"] = "mitral valve" }, out error);
            Check(error == null && did == "showing the Mitral valve" && valve.sharedMaterial == valveMat && ventricle.sharedMaterial != ventricleMat
                  && ventricle.sharedMaterial.GetColor("_BaseColor").a < 0.5f, "focus keeps the mitral valve solid and makes the rest see-through");
            ArtifactVoiceTools.Act(null, e, "focus", new JObject { ["part"] = "spleen" }, out error);
            Check(error != null && error.Contains("Left ventricle"), "focusing a part that isn't there says which parts there are");
            ArtifactVoiceTools.Act(null, e, "reset", new JObject(), out error);
            Check(!e.Separated && !e.Sectioned && e.Focused == null && ventricle.sharedMaterial == ventricleMat, "\"reset it\" puts everything back");
            ArtifactVoiceTools.Act(null, e, "spin it round", new JObject(), out error);
            Check(error != null, "an unknown action is refused with the list of actions");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    // ---- hands (HandGrabCore) with synthetic hands: no headset needed ------------------------------------------
    sealed class FakeTarget : HandGrabCore.ITarget
    {
        public Vector3 Position { get; set; }
        public float Scale { get; set; } = 1f;
        public float Yaw { get; private set; }
        public float Tilt { get; private set; }
        public Bounds model = new Bounds(new Vector3(0f, 1.2f, 0.5f), Vector3.one * 0.2f);
        public Transform part;
        public HandGrabCore.Mode lastGrab;
        public void SetView(float yaw, float tilt) { Yaw = yaw; Tilt = tilt; }
        public bool Near(Vector3 world, float margin) { var b = model; b.Expand(margin * 2f); return b.Contains(world); }
        public Transform PartAt(Vector3 world, float margin) =>
            part != null && (world - part.position).magnitude < 0.03f + margin ? part : null;
        public void Grabbed(HandGrabCore.Mode mode, Transform p) => lastGrab = mode;
    }

    // A hand whose pinch point is at <at>, pointing along <forward>, pinched or open.
    static void Pose(HandSample s, Vector3 at, Vector3 forward, bool pinched)
    {
        forward.Normalize();
        Vector3 side = Vector3.Cross(Vector3.up, forward).normalized;
        Vector3 wrist = at - forward * 0.12f;
        s.tracked = true;
        s.highConfidence = true;
        for (int i = 0; i < HandJoint.Count; i++) s.joints[i] = wrist;
        s.joints[HandJoint.Middle1] = wrist + forward * 0.09f;
        s.joints[HandJoint.Index1] = wrist + forward * 0.09f + side * 0.02f;
        s.joints[HandJoint.Pinky1] = wrist + forward * 0.08f - side * 0.03f;
        float half = pinched ? 0.004f : 0.02f;
        s.joints[HandJoint.ThumbTip] = at - side * half;
        s.joints[HandJoint.IndexTip] = at + side * half;
    }

    static void Run(HandGrabCore core, FakeTarget t, HandSample l, HandSample r, int frames)
    {
        for (int i = 0; i < frames; i++) core.Step(l, r, 1f / 72f, t);
    }

    static void HandGrab()
    {
        var core = new HandGrabCore();
        var t = new FakeTarget { Position = new Vector3(0f, 1.1f, 0.5f) };
        var left = new HandSample { isLeft = true };
        var right = new HandSample();
        left.tracked = false;
        Vector3 fwd = Vector3.forward;

        // A pinch far from the model does nothing; an open hand near it shows the model can be picked up.
        Pose(right, new Vector3(0.6f, 1.2f, 0.5f), fwd, true);
        Run(core, t, left, right, 10);
        Pose(right, new Vector3(0.6f, 1.2f, 0.5f), fwd, false);
        Run(core, t, left, right, 10);
        Check(core.Grab == HandGrabCore.Mode.None && t.Position == new Vector3(0f, 1.1f, 0.5f), "a pinch away from the model picks nothing up");
        Pose(right, new Vector3(0.05f, 1.2f, 0.5f), fwd, false);
        Run(core, t, left, right, 30);
        Check(core.Hovering && core.Grab == HandGrabCore.Mode.None, "an open hand at the model offers to pick it up");

        // Pinch on it and move the hand 20 cm right and 10 cm up: the model follows, settling smoothly.
        Pose(right, new Vector3(0.05f, 1.2f, 0.5f), fwd, true);
        Run(core, t, left, right, 2);
        Check(core.Grab == HandGrabCore.Mode.One && core.Holder == 1, "pinching the model picks it up with that hand");
        Pose(right, new Vector3(0.25f, 1.3f, 0.5f), fwd, true);
        core.Step(left, right, 1f / 72f, t);
        float firstStep = (t.Position - new Vector3(0f, 1.1f, 0.5f)).magnitude;
        Run(core, t, left, right, 60);
        Vector3 moved = t.Position - new Vector3(0f, 1.1f, 0.5f);
        Check(firstStep > 0.01f && firstStep < 0.2f, $"the first frame moves part of the way, not a jump ({firstStep:0.000} m)");
        Check((moved - new Vector3(0.2f, 0.1f, 0f)).magnitude < 0.005f, "after settling the model has moved with the hand: " + moved.ToString("F3"));

        // Jitter: a still pinch shaking by 4 mm moves the model much less than that.
        Vector3 before = t.Position;
        float worst = 0f;
        for (int i = 0; i < 72; i++)
        {
            Vector3 shake = new Vector3((i % 2 == 0 ? 1 : -1) * 0.004f, 0f, 0f);
            Pose(right, new Vector3(0.25f, 1.3f, 0.5f) + shake, fwd, true);
            core.Step(left, right, 1f / 72f, t);
            worst = Mathf.Max(worst, (t.Position - before).magnitude);
        }
        Check(worst < 0.0025f, $"tracking jitter of 4 mm moves the held model at most {worst * 1000f:0.0} mm");

        // Turning the wrist 30 degrees to the right spins it; pointing the hand up tilts it.
        Pose(right, new Vector3(0.25f, 1.3f, 0.5f), Quaternion.Euler(0f, 30f, 0f) * fwd, true);
        Run(core, t, left, right, 60);
        Check(Mathf.Abs(t.Yaw - 30f * HandGrabCore.YawGain) < 1f, $"turning the wrist 30° turns the model {t.Yaw:0}°");
        Pose(right, new Vector3(0.25f, 1.3f, 0.5f), fwd, true);
        Run(core, t, left, right, 60);

        // A brief tracking loss doesn't drop it; a long one does.
        right.tracked = false;
        Run(core, t, left, right, 9);  // 0.125 s
        Check(core.Grab == HandGrabCore.Mode.One, "a blink of lost tracking keeps hold");
        Pose(right, new Vector3(0.25f, 1.3f, 0.5f), fwd, true);
        Run(core, t, left, right, 5);

        // The other hand pinches near it: two hands. Pulling them apart doubles the size; turning them spins it.
        // The right hand settles 10 cm right of the model's middle, then the left pinches 10 cm left of it.
        Vector3 centre = new Vector3(0.15f, 1.2f, 0.5f);
        Pose(right, centre + new Vector3(0.1f, 0f, 0f), fwd, true);
        Run(core, t, left, right, 90);
        Pose(left, centre + new Vector3(-0.1f, 0f, 0f), fwd, true);
        Run(core, t, left, right, 3);
        Check(core.Grab == HandGrabCore.Mode.Two, "pinching with the other hand too holds it with both");
        float yawBefore = t.Yaw;
        Pose(left, centre + new Vector3(-0.2f, 0f, 0f), fwd, true);
        Pose(right, centre + new Vector3(0.2f, 0f, 0f), fwd, true);
        Run(core, t, left, right, 90);
        Check(Mathf.Abs(t.Scale - 2f) < 0.05f, $"pulling the hands twice as far apart makes it twice the size ({t.Scale:0.00})");
        Pose(left, centre + Quaternion.Euler(0f, 45f, 0f) * new Vector3(-0.2f, 0f, 0f), fwd, true);
        Pose(right, centre + Quaternion.Euler(0f, 45f, 0f) * new Vector3(0.2f, 0f, 0f), fwd, true);
        Run(core, t, left, right, 90);
        Check(Mathf.Abs(Mathf.DeltaAngle(yawBefore + 45f, t.Yaw)) < 2f, $"turning both hands 45° like a wheel spins it 45° ({t.Yaw - yawBefore:0}°)");
        Pose(left, centre + new Vector3(-2f, 0f, 0f), fwd, true);
        Pose(right, centre + new Vector3(2f, 0f, 0f), fwd, true);
        Run(core, t, left, right, 90);
        Check(t.Scale <= HandGrabCore.MaxScale + 1e-4f, "it can't be stretched past three times its size");

        // Let go with one hand: the other keeps holding. Let go with both: it's put down where it is.
        Pose(left, centre + new Vector3(-0.2f, 0f, 0f), fwd, false);
        Run(core, t, left, right, 3);
        Check(core.Grab == HandGrabCore.Mode.One && core.Holder == 1, "letting go with one hand leaves it in the other");
        Pose(right, centre + new Vector3(0.2f, 0f, 0f), fwd, false);
        Run(core, t, left, right, 3);
        Vector3 down = t.Position;
        Run(core, t, left, right, 30);
        Check(core.Grab == HandGrabCore.Mode.None && t.Position == down, "letting go puts it down and it stays");
    }

    static void HandGrabParts()
    {
        var core = new HandGrabCore();
        var holder = new GameObject("hand grab part check");
        try
        {
            var part = new GameObject("Left ventricle.001").transform;
            part.SetParent(holder.transform, false);
            part.position = new Vector3(0.3f, 1.2f, 0.5f);
            var t = new FakeTarget { part = part };
            var left = new HandSample { isLeft = true };
            var right = new HandSample();
            left.tracked = false;
            Pose(right, part.position, Vector3.forward, false);
            Run(core, t, left, right, 30);
            Check(core.HoverPart == part, "an open hand at a separated part offers that part");
            Pose(right, part.position, Vector3.forward, true);
            Run(core, t, left, right, 2);
            Check(core.Grab == HandGrabCore.Mode.Part && core.HeldPart == part && t.lastGrab == HandGrabCore.Mode.Part, "pinching a part takes just that part");
            Pose(right, part.position + new Vector3(0f, 0.15f, -0.1f), Quaternion.Euler(0f, 90f, 0f) * Vector3.forward, true);
            Run(core, t, left, right, 90);
            Check((part.position - new Vector3(0.3f, 1.35f, 0.4f)).magnitude < 0.01f, "the part goes where the hand takes it: " + part.position.ToString("F3"));
            Check(Quaternion.Angle(part.rotation, Quaternion.Euler(0f, 90f, 0f)) < 3f, "and turns with the hand");
            Check(ArtifactExplorer.NameOf(part) == "Left ventricle", "its name shows without Blender's .001");
            Pose(right, part.position, Quaternion.Euler(0f, 90f, 0f) * Vector3.forward, false);
            Run(core, t, left, right, 3);
            Check(core.Grab == HandGrabCore.Mode.None, "opening the pinch puts the part down");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(holder);
        }
    }

    // Two parts: a big shell with an authored "explode" marker, and a small inner part with none.
    static void ExplorationParts()
    {
        var root = new GameObject("explorer parts check");
        var shell = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var core = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            shell.name = "Shell"; core.name = "Core";
            shell.transform.SetParent(root.transform, false);
            core.transform.SetParent(root.transform, false);
            core.transform.localPosition = new Vector3(0.05f, 0f, 0f);
            core.transform.localScale = Vector3.one * 0.1f;
            var marker = new GameObject(ArtifactExplorer.ExplodeMarker + ".001").transform;  // as Blender names it
            marker.SetParent(shell.transform, false);
            marker.localPosition = new Vector3(0f, 0.3f, 0f);
            var explorer = root.AddComponent<ArtifactExplorer>();
            explorer.Initialize();
            Check(explorer.PartCount == 2, "both meshes are parts, not " + explorer.PartCount);
            explorer.Separate();
            Check(explorer.Separated && shell.transform.localPosition == Vector3.zero, "separating moves over time, not in one jump");
            explorer.Place(1f);
            Check((shell.transform.localPosition - new Vector3(0f, 0.3f, 0f)).magnitude < 1e-5f, "the shell goes where its explode marker says");
            Check(core.transform.localPosition.x > 0.05f + 1e-4f, "a part with no marker moves straight out from the centre");
            var label = root.transform.Find("labels/label Shell");
            Check(label != null && label.gameObject.activeSelf && label.GetComponent<TMPro.TMP_Text>().text == "Shell", "a separated part shows its name");
            // Callouts: from in front, both names sit outside the model, a full line apart if they share a column.
            explorer.LayOutLabels(new Vector3(0f, 0.1f, -2f));
            var other = root.transform.Find("labels/label Core");
            float minX = float.MaxValue, maxX = float.MinValue;
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
                if (r.GetComponent<TMPro.TMP_Text>() == null) { minX = Mathf.Min(minX, r.bounds.min.x); maxX = Mathf.Max(maxX, r.bounds.max.x); }
            bool Clear(Transform t) => t.position.x < minX || t.position.x > maxX;
            bool apartSides = (label.position.x < minX) != (other.position.x < minX);
            Check(other != null && Clear(label) && Clear(other)
                  && (apartSides || Mathf.Abs(label.position.y - other.position.y) >= 0.0209f),
                "separated names stand clear of the model and of each other");
            Check(label.GetComponentInChildren<LineRenderer>() != null, "each name has a leader line to its part");
            explorer.Place(0.3f);
            Check(label != null && !label.gameObject.activeSelf, "names hide while the parts are still close");
            var shellBefore = shell.GetComponent<Renderer>().sharedMaterial;
            var coreBefore = core.GetComponent<Renderer>().sharedMaterial;
            explorer.ToggleXRay();
            var ghost = shell.GetComponent<Renderer>().sharedMaterial;
            Check(explorer.LargeParts == 1 && ghost != shellBefore && ghost.GetColor("_BaseColor").a < 0.5f, "X-ray makes the big outer part see-through");
            Check(core.GetComponent<Renderer>().sharedMaterial == coreBefore, "X-ray leaves the small inner part solid");
            explorer.Section();
            explorer.ResetView();
            Check(!explorer.Separated && !explorer.Sectioned && !explorer.XRay && shell.transform.localPosition == Vector3.zero
                  && core.transform.localPosition == new Vector3(0.05f, 0f, 0f) && shell.GetComponent<Renderer>().sharedMaterial == shellBefore,
                "reset puts every part back, solid and whole");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    // In the app, the host comes to the user, and it steps aside for a panel that's already there (the Hands panel).
    static void Placement()
    {
        var prefab = Resources.Load<ArtifactHost>(ArtifactBootstrap.Resource);
        Check(prefab != null, "Resources/ArtifactHost exists, so the app's Main gets a host");
        if (prefab == null) return;
        // Camera.main is the first enabled MainCamera, so an open scene's own camera (Main's rig) would win: rest it for the check.
        var resting = new List<Camera>();
        foreach (var cam in Camera.allCameras)
            if (cam.enabled && cam.CompareTag("MainCamera"))
            {
                cam.enabled = false;
                resting.Add(cam);
            }
        var camGo = new GameObject("check camera") { tag = "MainCamera" };
        camGo.AddComponent<Camera>();
        camGo.transform.position = new Vector3(0f, 1.6f, 0f);
        var host = UnityEngine.Object.Instantiate(prefab);
        ForgePanel other = null;
        try
        {
            host.ComeToUser();
            var straight = host.transform.position;
            Check(Vector3.Distance(straight, new Vector3(-0.2f, 1.1f, 0.8f)) < 0.01f && Vector3.Angle(host.transform.forward, Vector3.forward) < 0.1f,
                $"with nothing in the way the host comes 0.8 m ahead at desk height, got {straight:F2}");
            Check(host.PlacementNote == null, $"straight ahead in the air there's nothing to say about where it is, got '{host.PlacementNote}'");

            // A panel already where the host's own panel would open.
            other = UnityEngine.Object.Instantiate(host.panelPrefab);
            other.transform.position = straight + host.transform.rotation * host.panelOffset;
            host.ComeToUser();
            var ownPanel = host.transform.TransformPoint(host.panelOffset);
            float gap = Vector2.Distance(new Vector2(ownPanel.x, ownPanel.z), new Vector2(other.transform.position.x, other.transform.position.z));
            float reach = Vector2.Distance(new Vector2(host.transform.position.x, host.transform.position.z), Vector2.zero);
            Check(gap >= ArtifactHost.PanelClearance && Mathf.Abs(reach - Vector2.Distance(new Vector2(straight.x, straight.z), Vector2.zero)) < 0.01f,
                $"with a panel in the way the host turns aside, as far from the user as before: gap {gap:F2} m, reach {reach:F2} m");
            string aside = host.transform.position.x > 0f ? "It's to your right." : "It's to your left.";
            Check(host.PlacementNote == aside, $"turned aside, Albie is told which side: expected '{aside}', got '{host.PlacementNote}'");

            // The host's own panels never count as in the way.
            other.transform.SetParent(host.transform, true);
            host.ComeToUser();
            Check(Vector3.Distance(host.transform.position, straight) < 0.01f, "the host's own panels never push it aside");

            // A desk in front (1.4 m long across the view, 0.74 m high): the host sits on its top, inside its edge.
            host.Room = new MockFloorPlan("check desk", new Vector2(6f, 6f)).Add("TABLE", new Vector2(0f, 0.9f), new Vector3(1.4f, 0.74f, 0.7f));
            host.ComeToUser();
            var onDesk = host.transform.position;
            Check(Mathf.Abs(onDesk.y - 0.74f) < 0.001f && Mathf.Abs(onDesk.x) <= 0.7f - ArtifactHost.TableMargin + 0.001f
                  && onDesk.z >= 0.9f - 0.35f + ArtifactHost.TableMargin - 0.001f && onDesk.z <= 0.9f + 0.35f - ArtifactHost.TableMargin + 0.001f,
                $"a desk in front puts the host on its top, inside its edge: got {onDesk:F2}");
            Check(host.PlacementNote == "It's on the table in front of you.", $"on the desk, Albie is told so: got '{host.PlacementNote}'");

            // A narrow table to the right, its edge just past the host's spot: the host is pulled onto it, not left in the air beside it.
            host.Room = new MockFloorPlan("check side table", new Vector2(6f, 6f)).Add("TABLE", new Vector2(0.5f, 0.9f), new Vector3(1.6f, 0.74f, 0.5f));
            host.ComeToUser();
            var pulled = host.transform.position;
            Check(Mathf.Abs(pulled.y - 0.74f) < 0.001f && Mathf.Abs(pulled.x - (-0.18f)) < 0.001f && Mathf.Abs(pulled.z - 0.8f) < 0.001f,
                $"a table edge within reach pulls the host onto the table: got {pulled:F2}");

            // A coffee table (0.45 m) in front of a standing person is too low to work at: the host floats as before.
            host.Room = new MockFloorPlan("check coffee table", new Vector2(6f, 6f)).Add("TABLE", new Vector2(0f, 0.9f), new Vector3(1.0f, 0.45f, 0.5f));
            host.ComeToUser();
            Check(Vector3.Distance(host.transform.position, straight) < 0.01f, $"a coffee table is too low when standing, the host floats: got {host.transform.position:F2}");

            // A wall 0.5 m ahead: the host keeps its margin, its panel at least stays out of the wall, and the host is never further away than before.
            host.Room = new MockFloorPlan("check wall", new Vector2(6f, 6f), true, new Vector3(0f, 0f, -2.5f));
            host.ComeToUser();
            var byWall = host.transform.position;
            var panelByWall = host.transform.TransformPoint(host.panelOffset);
            float wallReach = Vector2.Distance(new Vector2(byWall.x, byWall.z), Vector2.zero);
            Check(byWall.z <= 0.5f - ArtifactHost.WallMargin + 0.001f && panelByWall.z <= 0.5f + 0.001f
                  && wallReach <= 0.8f + 0.21f && Mathf.Abs(byWall.y - 1.1f) < 0.001f,
                $"with a wall 0.5 m ahead the host keeps {ArtifactHost.WallMargin} m off it and the panel is out of it: host {byWall:F2}, panel {panelByWall:F2}, reach {wallReach:F2} m");

            // A wall close on the right: the panel swaps to the left and the host moves right of centre, still straight ahead.
            host.Room = new MockFloorPlan("check wall right", new Vector2(6f, 6f), true, new Vector3(-2.45f, 0f, 0f));
            host.ComeToUser();
            var swapped = host.transform.position;
            Check(host.panelOffset.x < 0f && Vector3.Distance(swapped, new Vector3(0.2f, 1.1f, 0.8f)) < 0.01f && Vector3.Angle(host.transform.forward, Vector3.forward) < 0.1f,
                $"with a wall close on the right the panel opens on the left and the host stays ahead: host {swapped:F2}, panel x {host.panelOffset.x:F2}");
            host.Room = null;
            host.ComeToUser();
            Check(host.panelOffset.x > 0f && Vector3.Distance(host.transform.position, straight) < 0.01f, "with the wall gone the panel is back on the right");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host.gameObject);
            UnityEngine.Object.DestroyImmediate(camGo);
            foreach (var cam in resting) if (cam != null) cam.enabled = true;
        }
    }

    static void Fixtures()
    {
        foreach (var id in new[] { "slalom", "repo_city", "stance", "three_body" })
        {
            var p = ArtifactPackage.Load(id, out string error);
            Check(p != null, $"{id} loads: {error}");
            if (p == null) continue;
            var errors = ArtifactValidator.Validate(p.Manifest, p.Scene);
            Check(errors.Count == 0, $"{id} is valid: {string.Join("; ", errors)}");
        }
        var broken = ArtifactPackage.Load("broken", out _);
        Check(ArtifactValidator.Validate(broken.Manifest, broken.Scene).Any(e => e.code == "unknown_kind"), "broken is refused");
        Check(ArtifactNodes.Missing(ArtifactPackage.Load("stance", out _).Scene).Count == 0, "stance can be built today");
        Check(ArtifactNodes.Missing(ArtifactPackage.Load("three_body", out _).Scene).Count == 0, "three_body can be built today");
        var missing = ArtifactNodes.Missing(ArtifactPackage.Load("slalom", out _).Scene);
        Check(missing.Count == 0, "slalom can be built today (its course; runs are scored by the run engine)");
    }

    // One per code in C-12 section 9. hash_mismatch waits for gltf loading, which is when assets are read.
    static void Mistakes()
    {
        Expect("newer_schema", (m, s) => s["schema"] = "albie.scene/2");
        Expect("newer_schema", (m, s) => m["runtime"]["min_runtime"] = ArtifactSchema.HostRuntime + 1);
        Expect("unknown_kind", (m, s) => Node(s, "cones")["kind"] = "cube");
        Expect("unsupported_kind", (m, s) => Node(s, "guide")["kind"] = "camera_rig");
        Expect("unknown_ref", (m, s) => s["rules"][1]["zones"] = "gate");
        Expect("unknown_ref", (m, s) => Node(s, "time_label")["parent"] = "nowhere");
        Expect("unknown_slot", (m, s) => Node(s, "cones")["source"] = "course.pins");
        Expect("unknown_slot", (m, s) => s["run"]["finish"]["needs"][0]["min"] = "@course.pins");
        Expect("unknown_input", (m, s) => s["generators"][0]["params"]["gates"] = "$gate_count");
        Expect("output_mismatch", (m, s) => ((JArray)s["rules"]).RemoveAt(2));
        Expect("asset_not_listed", (m, s) => { Node(s, "guide")["kind"] = "gltf"; Node(s, "guide")["asset"] = "assets/cone.glb"; });
        Expect("permission_denied", (m, s) => m["permissions"]["network"] = new JArray("example.com"));
        Expect("permission_denied", (m, s) => m["permissions"]["camera"] = "read");
        Expect("permission_denied", (m, s) => m["permissions"]["connectors"] = new JArray("source_control:write"));
        Expect("permission_missing", (m, s) => m["permissions"]["room"] = "none");
        Expect("permission_missing", (m, s) => m["permissions"]["things"] = "none");
        Expect("script_not_allowed", (m, s) => m["script"] = "main.js");
        Expect("too_big", (m, s) =>
        {
            var nodes = (JArray)s["nodes"];
            for (int i = 0; i < ArtifactSchema.MaxNodes + 1; i++) nodes.Add(new JObject { ["id"] = "n" + i, ["kind"] = "primitive", ["shape"] = "box" });
        });
        Expect("bad_value", (m, s) => m["inputs"][0]["default"] = 20);
        Expect("bad_value", (m, s) => Node(s, "time_label")["id"] = "cones");
        Expect("bad_value", (m, s) => { Node(s, "start")["parent"] = "finish"; Node(s, "finish")["parent"] = "start"; });
        Expect("bad_value", (m, s) => m["version"] = "1.2.0");
        Expect("bad_value", (m, s) => m["links"]["thing"] = "truck", "stance");

        // The sim node
        Expect("bad_value", (m, s) => Node(s, "orbits")["solver"] = "warp_drive", "three_body");
        Expect("bad_value", (m, s) => Node(s, "orbits")["bodies"] = new JArray(), "three_body");
        Expect("unknown_ref", (m, s) => Node(s, "orbits")["bodies"][0]["node"] = "nothing", "three_body");
        Expect("bad_value", (m, s) => Node(s, "orbits")["bodies"][1]["node"] = "body_a", "three_body");
        Expect("bad_value", (m, s) => Node(s, "orbits")["bodies"][0]["mass"] = -1, "three_body");
        Expect("bad_value", (m, s) => Node(s, "orbits")["params"]["step"] = 0, "three_body");
        Expect("unknown_ref", (m, s) => s["rules"][0]["value"] = "warp_factor", "three_body");
        Expect("unknown_ref", (m, s) => s["rules"][0]["sim"] = "nothing", "three_body");
        Expect("unknown_ref", (m, s) => Node(s, "trail_a")["follow"] = "nothing", "three_body");
        Expect("bad_value", (m, s) => Node(s, "trail_a")["parent"] = null, "three_body");
        Expect("too_big", (m, s) => Node(s, "trail_a")["length"] = 100000, "three_body");
        Expect("bad_value", (m, s) => s["view"]["radius"] = 0, "three_body");
        Expect("unknown_input", (m, s) => Node(s, "orbits")["rate"] = "$warp", "three_body");
    }

    // Wrong shapes and hostile ids: the host answers, it doesn't throw.
    static void Refusals()
    {
        foreach (var id in new[] { "../../secrets", "a/b", "", null, "UPPER" })
        {
            Check(ArtifactPackage.Load(id, out string error) == null && error != null, $"id \"{id}\" is refused");
        }
        Check(ArtifactPackage.Load("nothing_here", out string none) == null && none.Contains("nothing_here"), "a missing artifact says so");
        foreach (var bad in new[] { "{\"schema\":\"albie.artifact/1\",\"inputs\":[1,2]}", "{\"schema\":\"albie.artifact/1\",\"scenarios\":\"x\",\"data\":[3]}" })
        {
            var errors = ArtifactValidator.Validate(JObject.Parse(bad), new JObject { ["schema"] = "albie.scene/1", ["nodes"] = new JArray(1, "x") });
            Check(errors.Count > 0, "a wrong shape comes back as data: " + bad);
        }
        Check(ArtifactValidator.Validate(null, null).Count > 0, "null files come back as data");
    }

    static void Inputs()
    {
        var p = ArtifactPackage.Load("stance", out _);
        var inputs = new ArtifactInputs(p.Manifest);
        int changes = 0;
        inputs.Changed += _ => changes++;
        double start = inputs.Value("track_m");
        Check(!inputs.TrySet("track_m", 99) && inputs.Value("track_m") == start, "out of range is refused, not clamped");
        Check(!inputs.TrySet("nope", 1), "an unknown input is refused");
        Check(changes == 0, "a refused value raises nothing");
        Check(inputs.TrySet("track_m", 0.5) && changes == 1, "a good value raises Changed");
        Check(inputs.TrySet("track_m", 0.5) && changes == 1, "the same value raises nothing");
        Check(Mathf.Approximately(inputs.Number("$track_m", -1f), 0.5f), "$name reads the live value");
        Check(inputs.Vector(new JArray("$track_m", 2, 3), Vector3.zero) == new Vector3(0.5f, 2f, 3f), "a vector can mix $name and numbers");
        Check(ArtifactInputs.IsBound(new JArray(1, "$track_m", 3)) && !ArtifactInputs.IsBound(new JArray(1, 2, 3)), "IsBound finds a $name in a list");
        var over = new ArtifactInputs(p.Manifest, new JObject { ["track_m"] = 0.3 });
        Check(Mathf.Approximately((float)over.Value("track_m"), 0.3f), "scenario params replace defaults");
    }

    static void Approvals()
    {
        string file = Path.Combine(Path.GetTempPath(), "artifact-approvals-" + Guid.NewGuid().ToString("N") + ".json");
        ArtifactApprovals.FileOverride = file;
        try
        {
            var stance = ArtifactPackage.Load("stance", out _).Manifest;
            var slalom = ArtifactPackage.Load("slalom", out _).Manifest;
            Check(!ArtifactApprovals.NeedsApproval(stance), "asking for nothing needs no sheet");
            Check(ArtifactApprovals.Lines(slalom).Count == 2, "slalom asks for room and things");
            Check(ArtifactApprovals.NeedsApproval(slalom), "first open asks");
            ArtifactApprovals.Approve(slalom);
            Check(!ArtifactApprovals.NeedsApproval(slalom), "second open doesn't");

            var same = (JObject)slalom.DeepClone();
            var reordered = new JObject();
            foreach (var p in ((JObject)same["permissions"]).Properties().Reverse()) reordered[p.Name] = p.Value;
            same["permissions"] = reordered;
            Check(!ArtifactApprovals.NeedsApproval(same), "reordered keys don't ask again");

            var more = (JObject)slalom.DeepClone();
            more["permissions"]["connectors"] = new JArray("source_control:read");
            Check(ArtifactApprovals.NeedsApproval(more), "a changed list asks again");

            File.WriteAllText(file, "{ not json");
            Check(ArtifactApprovals.NeedsApproval(slalom), "a damaged store asks again");
            File.Delete(file);
            ArtifactApprovals.Approve(slalom);
            ArtifactApprovals.Forget(slalom.Value<string>("id"));
            Check(ArtifactApprovals.NeedsApproval(slalom), "forgetting asks again");
        }
        finally
        {
            ArtifactApprovals.FileOverride = null;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    static (IArtifactSolver solver, JObject node, ArtifactInputs inputs) Orbits()
    {
        var p = ArtifactPackage.Load("three_body", out _);
        var inputs = new ArtifactInputs(p.Manifest);
        var node = Node(p.Scene, "orbits");
        var solver = ArtifactSolvers.Create("n_body");
        solver.Configure(node, inputs);
        solver.Restart();
        return (solver, node, inputs);
    }

    static void Run(IArtifactSolver solver, double time)
    {
        int steps = (int)Math.Round(time / solver.StepSize);
        for (int i = 0; i < steps && !solver.Finished; i++) solver.Step();
    }

    // Against known physics, not just "it runs". The figure eight is an exact solution (Chenciner and Montgomery, 2000):
    // three equal masses return to where they started every 6.32591398 time units.
    static void Physics()
    {
        const double Period = 6.32591398;
        var (solver, node, inputs) = Orbits();
        var start = new[] { solver.Position(0), solver.Position(1), solver.Position(2) };
        int drift = solver.ValueIndex("energy_drift_pct"), time = solver.ValueIndex("time"), closest = solver.ValueIndex("closest_approach");
        Run(solver, Period);
        float miss = 0f;
        for (int i = 0; i < 3; i++) miss = Mathf.Max(miss, (solver.Position(i) - start[i]).magnitude);
        Check(miss < 0.001f, $"the figure eight closes after one period (missed by {miss})");
        Check(Math.Abs(solver.Value(time) - Period) < 0.002, $"time advances ({solver.Value(time)})");
        Check(solver.Value(drift) < 0.001, $"energy holds over one period ({solver.Value(drift)}%)");
        Run(solver, Period * 19);
        miss = 0f;
        for (int i = 0; i < 3; i++) miss = Mathf.Max(miss, (solver.Position(i) - start[i]).magnitude);
        Check(miss < 0.05f, $"still closes after 20 periods (missed by {miss})");
        Check(solver.Value(drift) < 0.001, $"energy holds over 20 periods ({solver.Value(drift)}%)");
        Check(solver.Value(closest) > 0.5, $"the three stay apart on the figure eight ({solver.Value(closest)})");

        // Same numbers, same answer, every time. A rerun has to be fair.
        var (a, _, _) = Orbits();
        var (b, _, _) = Orbits();
        Run(a, 20.0);
        Run(b, 20.0);
        Check(a.Position(0) == b.Position(0), "the same inputs give the same run");

        // Editing a value has to actually change what happens.
        var (edited, editedNode, editedInputs) = Orbits();
        Check(editedInputs.TrySet("mass_c", 1.2), "mass_c can be edited");
        Check(edited.Configure(editedNode, editedInputs), "an edited mass counts as a change");
        edited.Restart();
        var (plain, _, _) = Orbits();
        Run(edited, Period);
        Run(plain, Period);
        Check((edited.Position(0) - plain.Position(0)).magnitude > 0.05f, "a heavier body changes the orbit");
        Check(!plain.Configure(node, inputs), "unchanged numbers don't count as a change");
        Check(inputs.TrySet("speed", 2.0) && !plain.Configure(node, inputs), "playback speed doesn't count as a change");

        // Centre of mass stays put with unequal masses, so nothing drifts out of the volume.
        var com = Vector3.zero;
        for (int i = 0; i < 3; i++) com += edited.Position(i) * (i == 2 ? 1.2f : 1f);
        Check(com.magnitude < 0.001f, $"unequal masses keep the centre of mass still ({com.magnitude})");

        // Thrown hard enough, a body leaves, and the sim says so and stops.
        var (flung, flungNode, flungInputs) = Orbits();
        Check(flungInputs.TrySet("kick", 1.45), "kick can be edited");
        flung.Configure(flungNode, flungInputs);
        flung.Restart();
        Run(flung, 300.0);
        Check(flung.Finished && flung.Value(flung.ValueIndex("escaped")) == 1.0, "a body leaving the bounds ends the sim");

        // Two bodies on one point must not become NaN.
        var (crash, crashNode, crashInputs) = Orbits();
        var bodies = (JArray)crashNode["bodies"];
        bodies[1]["pos"] = bodies[0]["pos"].DeepClone();
        crash.Configure(crashNode, crashInputs);
        crash.Restart();
        Run(crash, 1.0);
        Check(!float.IsNaN(crash.Position(0).x), "bodies on one point stay finite");

        // The per-frame path has to be free of garbage (CLAUDE.md, Quest performance).
        var (quiet, _, _) = Orbits();
        Run(quiet, 0.1);
        // AllocationCounter (Controllers/Editor): GC.GetAllocatedBytesForCurrentThread reads 0 under Unity's GC.
        long bytes = AllocationCounter.BytesDuring(() =>
        {
            for (int i = 0; i < 5000; i++)
            {
                quiet.Step();
                quiet.Position(0);
            }
        });
        Check(bytes == 0, $"stepping allocates nothing ({bytes} bytes over 5000 steps)");
    }

    static void Looks()
    {
        var size = new Vector3(0.2f, 0.4f, 0.6f);
        Check(ArtifactLook.ShapeScale("box", size) == size, "a box is its size");
        Check(ArtifactLook.ShapeScale("capsule", size).y == 0.2f, "a 2 m capsule is halved");
        Check(ArtifactLook.ShapeScale("cylinder", size) == size, "our cylinder is its size");
        Check(ArtifactLook.ShapeScale("plane", size) == new Vector3(0.02f, 1f, 0.06f), "a 10 m plane is scaled down");
        Check(ArtifactLook.WithoutMetrics("{completion_time_s} s") == ArtifactLook.NoValue + " s", "an empty metric never shows a number");
        Check(ArtifactLook.Color("#FF0000", Color.white) == Color.red && ArtifactLook.Color("nope", Color.white) == Color.white, "colours parse, with a fallback");
    }
}
