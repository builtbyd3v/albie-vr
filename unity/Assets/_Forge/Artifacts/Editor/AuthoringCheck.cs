using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forge.Artifacts;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Forge > Artifacts > Check Authoring: the validator against what a model actually writes. Every template and Mocks
/// fixture must stay valid; the real failed attempts from the live test (Mocks/artifacts/authoring) and deliberate
/// mutations of the archetypes must fail with the expected codes at the expected paths; the archetypes must pass.
/// An expectation belongs to a plan step (A1, A2, ...). Steps not built yet are reported as pending, not failed, so the
/// same check is the harness for every later step: add the step to <see cref="Built"/> when it lands.
/// </summary>
public static class AuthoringCheck
{
    /// <summary>Plan steps that are in the code. An expectation for any other step is reported, not enforced.</summary>
    static readonly HashSet<string> Built = new HashSet<string> { "A0" };

    static readonly string Dir = Path.Combine(Application.dataPath, "_Forge/Mocks/artifacts/authoring");
    static readonly string[] MockFixtures = { "slalom", "repo_city", "stance", "three_body", "launch" };
    static readonly string[] Archetypes = { "arch_fan", "arch_stopwatch", "arch_lever", "arch_status_beacon" };

    static int passed, pending;
    static readonly List<string> failures = new List<string>(), waiting = new List<string>();

    [MenuItem("Forge/Artifacts/Check Authoring")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    public static void RunBatch()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
        EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
    }

    public static string Run()
    {
        passed = pending = 0;
        failures.Clear();
        waiting.Clear();
        try
        {
            StillValid();
            LiveFixtures();
            Mutations();
        }
        catch (Exception e)
        {
            failures.Add("the check threw: " + e);
        }
        var sb = new StringBuilder();
        sb.Append(failures.Count == 0 ? "PASS" : "FAIL").Append(" authoring check: ")
          .Append(passed).Append(" passed, ").Append(failures.Count).Append(" failed, ").Append(pending)
          .Append(" pending (steps built: ").Append(string.Join(", ", Built)).Append(")");
        foreach (var f in failures) sb.Append("\n  FAIL ").Append(f);
        foreach (var w in waiting) sb.Append("\n  pending ").Append(w);
        return sb.ToString();
    }

    // ---------- helpers ----------

    static void Check(bool ok, string what)
    {
        if (ok) passed++;
        else failures.Add(what);
    }

    static string Show(List<ArtifactError> errors) => errors.Count == 0 ? "no errors" : string.Join("; ", errors);

    static (JObject manifest, JObject scene) Load(string name)
    {
        string folder = Path.Combine(Dir, name);
        if (Directory.Exists(folder))
            return (JObject.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json"))),
                    JObject.Parse(File.ReadAllText(Path.Combine(folder, "scene.json"))));
        var both = JObject.Parse(File.ReadAllText(Path.Combine(Dir, name + ".json")));
        return ((JObject)both["manifest"], (JObject)both["scene"]);
    }

    static (JObject manifest, JObject scene) Template(string name)
    {
        if (!ArtifactLibrary.TryTemplate(name, out var m, out var s)) throw new Exception("no template " + name);
        return (m, s);
    }

    static int Index(JObject scene, string id)
    {
        var nodes = (JArray)scene["nodes"];
        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i] is JObject n && (string)n["id"] == id) return i;
        throw new Exception("no node " + id);
    }

    static JObject Node(JObject scene, string id) => (JObject)scene["nodes"][Index(scene, id)];

    static string P(JObject scene, string id, string field = null) =>
        $"nodes[{Index(scene, id)}]" + (field != null ? "." + field : "");

    static List<ArtifactError> Validate(JObject m, JObject s, string what)
    {
        try
        {
            return ArtifactValidator.Validate(m, s);
        }
        catch (Exception e)
        {
            failures.Add($"{what}: the validator threw {e.GetType().Name}: {e.Message}");
            return new List<ArtifactError>();
        }
    }

    static bool Has(List<ArtifactError> errors, string code, string path)
    {
        foreach (var e in errors)
            if (e.code == code && (path == null || e.path == path)) return true;
        return false;
    }

    /// <summary>
    /// <paramref name="expect"/> is "code@path" (or "code" for any path); a leading "!" means that error must be absent.
    /// </summary>
    static void Expect(string step, string what, List<ArtifactError> errors, params string[] expect)
    {
        var missing = new List<string>();
        foreach (var x in expect)
        {
            bool absent = x.StartsWith("!");
            string spec = absent ? x.Substring(1) : x;
            int at = spec.IndexOf('@');
            string code = at < 0 ? spec : spec.Substring(0, at), path = at < 0 ? null : spec.Substring(at + 1);
            if (Has(errors, code, path) == absent) missing.Add(x);
        }
        if (!Built.Contains(step))
        {
            pending++;
            waiting.Add($"({step}) {what}: {(missing.Count == 0 ? "already met" : "wants " + string.Join(", ", missing))}; now gives [{Show(errors)}]");
            return;
        }
        Check(missing.Count == 0, $"({step}) {what}: wanted {string.Join(", ", missing)}; got [{Show(errors)}]");
    }

    /// <summary>A yes/no expectation that belongs to a plan step.</summary>
    static void Gate(string step, string what, bool ok)
    {
        if (!Built.Contains(step))
        {
            pending++;
            waiting.Add($"({step}) {what}: {(ok ? "already met" : "not yet")}");
            return;
        }
        Check(ok, $"({step}) {what}");
    }

    static void Mutate(string step, string what, string fixture, Action<JObject, JObject> edit, Func<JObject, string[]> expect)
    {
        var (m, s) = fixture.StartsWith("template:") ? Template(fixture.Substring(9)) : Load(fixture);
        m = (JObject)m.DeepClone();
        s = (JObject)s.DeepClone();
        string[] wants = expect(s);  // paths are taken before the edit, so a removed node can't move them
        edit(m, s);
        Expect(step, $"{fixture}: {what}", Validate(m, s, what), wants);
    }

    // ---------- what must keep passing ----------

    static void StillValid()
    {
        foreach (string t in ArtifactLibrary.Templates)
        {
            var (m, s) = Template(t);
            var errors = Validate(m, s, "template " + t);
            Check(errors.Count == 0, $"template {t} is valid: {Show(errors)}");
        }
        foreach (string id in MockFixtures)
        {
            var p = ArtifactPackage.Load(id, out string why);
            Check(p != null, $"fixture {id} loads: {why}");
            if (p == null) continue;
            var errors = Validate(p.Manifest, p.Scene, "fixture " + id);
            Check(errors.Count == 0, $"fixture {id} is valid: {Show(errors)}");
        }
        var broken = ArtifactPackage.Load("broken", out _);
        var refused = Validate(broken.Manifest, broken.Scene, "broken");
        Check(refused.Count > 0 && refused[0].code == "unknown_kind", $"broken still fails first with unknown_kind: {Show(refused)}");

        foreach (string a in Archetypes)
        {
            var (m, s) = Load(a);
            var errors = Validate(m, s, a);
            Check(errors.Count == 0, $"{a} is valid: {Show(errors)}");
            var missing = ArtifactNodes.Missing(s);
            Check(missing.Count == 0, $"{a} can be built: {string.Join(", ", missing)}");
        }
    }

    // ---------- the live test's own attempts ----------

    static void LiveFixtures()
    {
        // Try 1: no schema and no id; "type" instead of kind; children; scripts. The old validator stopped at the schema.
        var (m, s) = Load("live_traffic_light_1");
        var e = Validate(m, s, "live_traffic_light_1");
        Expect("A1", "traffic light try 1 is checked past the missing schemas", e,
            "bad_value@schema", "bad_value@scene.schema", "bad_value@id", "bad_value@title", "bad_value@kind",
            "bad_value@category", "bad_value@runtime.type", "bad_value@scenarios", "bad_value@scene", "bad_value@scene.space",
            "unknown_kind@nodes[0].kind");
        Expect("A3", "traffic light try 1 names its unknown fields", e, "unknown_field@nodes[0].position", "unknown_field@scene.scripts", "unknown_field@name");

        (m, s) = Load("live_traffic_light_2");
        e = Validate(m, s, "live_traffic_light_2");
        Expect("A1", "traffic light try 2 (schemas added)", e,
            "bad_value@id", "bad_value@title", "bad_value@scene", "bad_value@scene.space", "unknown_kind@nodes[0].kind", "!bad_value@schema");

        (m, s) = Load("live_traffic_light_3");
        e = Validate(m, s, "live_traffic_light_3");
        Expect("A0", "traffic light try 3 (kind group, kind cylinder)", e,
            "bad_value@version", "permission_missing@permissions.room", "unknown_kind@nodes[0]", "unknown_kind@nodes[1]");
        Expect("A1", "traffic light try 3 has no scene file name", e, "bad_value@scene");

        (m, s) = Load("live_traffic_light_4");
        e = Validate(m, s, "live_traffic_light_4");
        Expect("A0", "traffic light try 4 (kind node)", e, "unknown_kind@nodes[0]");
        Expect("A1", "traffic light try 4 has no scene file name", e, "bad_value@scene");

        // Try 5 passed the validator and was then refused at open, because manifest.scene was missing.
        (m, s) = Load("live_traffic_light_5");
        e = Validate(m, s, "live_traffic_light_5");
        Expect("A1", "traffic light try 5 (no scene file name) is refused before save", e, "bad_value@scene");
        Expect("A3", "traffic light try 5 names position and emissive", e, "unknown_field@nodes[0].position", "unknown_field@nodes[0].emissive");
        Expect("A4", "traffic light try 5 asks for room with no generator", e, "bad_value@scene.space");

        foreach (string saved in new[] { "saved_traffic_light", "saved_seesaw", "live_seesaw_1" })
        {
            (m, s) = Load(saved);
            e = Validate(m, s, saved);
            Expect("A3", saved + " names position and scripts", e, "unknown_field@nodes[0].position", "unknown_field@scene.scripts");
            Expect("A4", saved + " asks for room with no generator", e, "bad_value@scene.space");
        }

        (m, s) = Load("probe_mixed");
        e = Validate(m, s, "probe_mixed");
        Expect("A1", "probe: a scene file outside the folder", e, "bad_value@scene");
        Expect("A3", "probe: position and onClick", e, "unknown_field@nodes[0].position", "unknown_field@nodes[0].onClick");
        Expect("A4", "probe: boolean input, a button on a primitive, set on a colour, a bad verb", e,
            "bad_value@inputs[0].type", "bad_value@rules[0].do[0].set", "bad_value@rules[0].do[1].sim", "bad_value@rules[0].do[2]");
    }

    // ---------- deliberate mistakes on the archetypes ----------

    static void Mutations()
    {
        // A missing schema is reported and the rest is still checked; only a newer schema stops the pass.
        Mutate("A1", "no manifest schema, plus a cube", "arch_fan",
            (m, s) => { m.Remove("schema"); Node(s, "hub")["kind"] = "cube"; },
            s => new[] { "bad_value@schema", "unknown_kind@" + P(s, "hub") });
        Mutate("A1", "no scene schema, plus a bad shape", "arch_fan",
            (m, s) => { s.Remove("schema"); Node(s, "hub")["shape"] = "blob"; },
            s => new[] { "bad_value@scene.schema", "bad_value@" + P(s, "hub", "shape") });
        Mutate("A0", "a newer scene schema stops the pass", "arch_fan",
            (m, s) => { s["schema"] = "albie.scene/2"; Node(s, "hub")["kind"] = "cube"; },
            s => new[] { "newer_schema@scene.schema", "!unknown_kind" });

        // manifest.scene: required, a plain file name.
        Mutate("A1", "no scene file name", "arch_lever", (m, s) => m.Remove("scene"), s => new[] { "bad_value@scene" });
        Mutate("A1", "a scene file outside the folder", "arch_lever", (m, s) => m["scene"] = "../scene.json", s => new[] { "bad_value@scene" });
        Mutate("A1", "a scene file name that's a number", "arch_lever", (m, s) => m["scene"] = 7, s => new[] { "bad_value@scene" });

        Mutate("A1", "two generators", "template:slalom",
            (m, s) =>
            {
                var second = (JObject)s["generators"][0].DeepClone();
                second["id"] = "course_two";
                ((JArray)s["generators"]).Add(second);
            },
            s => new[] { "too_big@generators" });

        Mutate("A1", "a node with no kind", "arch_lever", (m, s) => Node(s, "beam").Remove("kind"), s => new[] { "unknown_kind@" + P(s, "beam", "kind") });

        // Node fields with the wrong type: refused at their own path, and the pass goes on.
        Mutate("A1", "pos as a string", "arch_lever", (m, s) => Node(s, "beam")["pos"] = "0,0,0", s => new[] { "bad_value@" + P(s, "beam", "pos") });
        Mutate("A1", "rot with two numbers", "arch_lever", (m, s) => Node(s, "pin")["rot"] = new JArray(90, 0), s => new[] { "bad_value@" + P(s, "pin", "rot") });
        Mutate("A1", "size with a word", "arch_lever", (m, s) => Node(s, "base")["size"] = new JArray(0.2, "big", 0.1), s => new[] { "bad_value@" + P(s, "base", "size") });
        Mutate("A1", "size as an object", "arch_lever", (m, s) => Node(s, "base")["size"] = new JObject { ["x"] = 1 }, s => new[] { "bad_value@" + P(s, "base", "size") });
        Mutate("A1", "scale as one number", "arch_lever", (m, s) => Node(s, "base")["scale"] = 2, s => new[] { "bad_value@" + P(s, "base", "scale") });
        Mutate("A1", "rot with an expression", "arch_lever", (m, s) => Node(s, "beam")["rot"] = new JArray(0, 0, "$tilt*2"), s => new[] { "bad_value@" + P(s, "beam", "rot") });
        Mutate("A1", "a colour name", "arch_lever", (m, s) => Node(s, "beam")["color"] = "red", s => new[] { "bad_value@" + P(s, "beam", "color") });
        Mutate("A1", "a short colour", "arch_lever", (m, s) => Node(s, "beam")["color"] = "#fff", s => new[] { "bad_value@" + P(s, "beam", "color") });
        Mutate("A1", "visible as a word", "arch_status_beacon", (m, s) => Node(s, "dome_run")["visible"] = "no", s => new[] { "bad_value@" + P(s, "dome_run", "visible") });
        Mutate("A1", "glow as a number", "arch_status_beacon", (m, s) => Node(s, "dome_run")["glow"] = 1, s => new[] { "bad_value@" + P(s, "dome_run", "glow") });
        Mutate("A1", "text as a number", "arch_stopwatch", (m, s) => Node(s, "caption")["text"] = 42, s => new[] { "bad_value@" + P(s, "caption", "text") });
        Mutate("A1", "text height as a word", "arch_stopwatch", (m, s) => Node(s, "caption")["height"] = "tall", s => new[] { "bad_value@" + P(s, "caption", "height") });
        Mutate("A1", "parent as a number", "arch_lever", (m, s) => Node(s, "load_left")["parent"] = 5, s => new[] { "bad_value@" + P(s, "load_left", "parent") });
        Mutate("A1", "a bad colour and a bad ref are both reported", "arch_lever",
            (m, s) => { Node(s, "beam")["color"] = "red"; Node(s, "load_left")["parent"] = "nowhere"; },
            s => new[] { "bad_value@" + P(s, "beam", "color"), "unknown_ref@" + P(s, "load_left", "parent") });

        // Wrong shapes of the containers.
        Mutate("A1", "a rule that's a number", "arch_stopwatch", (m, s) => ((JArray)s["rules"]).Add(5), s => new[] { $"bad_value@rules[{((JArray)s["rules"]).Count}]" });
        Mutate("A1", "rules as a string", "arch_stopwatch", (m, s) => s["rules"] = "start", s => new[] { "bad_value@rules" });
        Mutate("A1", "nodes as an object", "arch_stopwatch", (m, s) => s["nodes"] = new JObject(), s => new[] { "bad_value@nodes" });
        Mutate("A1", "an action that's a string", "arch_stopwatch", (m, s) => s["rules"][1]["do"] = new JArray("play"), s => new[] { "bad_value@rules[1].do[0]" });
        Mutate("A1", "a widget that's a number", "arch_stopwatch", (m, s) => ((JArray)Node(s, "controls")["widgets"]).Add(3), s => new[] { "bad_value@" + P(s, "controls", "widgets[3]") });
        Mutate("A1", "a sim body that's a string", "arch_fan", (m, s) => Node(s, "rotor_a")["bodies"] = new JArray("tip_a"), s => new[] { "bad_value@" + P(s, "rotor_a", "bodies[0]") });
        Mutate("A1", "an animate key that's an object", "arch_stopwatch", (m, s) => s["rules"][0]["keys"][1] = new JObject(), s => new[] { "bad_value@rules[0].keys[1]" });
        Mutate("A1", "an input that's a number", "arch_lever", (m, s) => m["inputs"] = new JArray(1), s => new[] { "bad_value@inputs[0]", "unknown_input@" + P(s, "beam", "rot[2]") });
        Mutate("A1", "scenarios as a string", "arch_lever", (m, s) => m["scenarios"] = "default", s => new[] { "bad_value@scenarios" });
        Mutate("A1", "permissions as a string", "arch_lever", (m, s) => m["permissions"] = "all", s => new[] { "bad_value@permissions" });
        Mutate("A1", "runtime as a string", "arch_lever", (m, s) => m["runtime"] = "scene", s => new[] { "bad_value@runtime" });
        Mutate("A1", "min_runtime as a string", "arch_lever", (m, s) => m["runtime"]["min_runtime"] = "6", s => new[] { "bad_value@runtime.min_runtime" });
        Mutate("A1", "id as a number", "arch_lever", (m, s) => m["id"] = 12, s => new[] { "bad_value@id" });
        Mutate("A1", "title as a number", "arch_lever", (m, s) => m["title"] = 3, s => new[] { "bad_value@title" });
        Mutate("A1", "an input min as a word", "arch_lever", (m, s) => m["inputs"][0]["min"] = "low", s => new[] { "bad_value@inputs[0].min" });

        Fuzz();
        WrittenSceneName();
    }

    // Every field of every node, and every top-level key, set to every wrong type: the validator answers with an error
    // at a real path, and never throws. A typed field set to the wrong type is refused at that field.
    static readonly JToken[] Junk = { 123, "x", true, new JArray(), new JObject(), JValue.CreateNull(), new JArray(1, "a") };

    static readonly Dictionary<string, string> Typed = new Dictionary<string, string>
    {
        { "pos", "vec3" }, { "rot", "vec3" }, { "scale", "vec3" }, { "size", "vec3" },
        { "color", "color" }, { "visible", "bool" }, { "collider", "bool" }, { "glow", "bool" }, { "billboard", "bool" },
        { "loop", "bool" }, { "autoplay", "bool" }, { "text", "string" }, { "title", "string" }, { "parent", "string" },
        { "shape", "string" }, { "solver", "string" }, { "asset", "string" }, { "follow", "string" }, { "kind", "string" }, { "id", "string" },
        { "height", "number" }, { "width", "number" }, { "duration_s", "number" },
    };

    static bool RightType(string type, JToken v)
    {
        switch (type)
        {
            case "string": return v.Type == JTokenType.String;
            case "bool": return v.Type == JTokenType.Boolean;
            case "number": return v.Type == JTokenType.Integer || v.Type == JTokenType.Float;
            default: return false;  // no junk value is a vec3 or a colour
        }
    }

    static void Fuzz()
    {
        int runs = 0, threw = 0, pathless = 0, unrefused = 0;
        string firstPathless = null, firstUnrefused = null;
        var sources = new List<(string name, JObject m, JObject s)>();
        foreach (string a in Archetypes) { var (m, s) = Load(a); sources.Add((a, m, s)); }
        foreach (string t in new[] { "slalom", "hoop_course", "double_pendulum" }) { var (m, s) = Template(t); sources.Add((t, m, s)); }

        foreach (var (name, m0, s0) in sources)
        {
            var nodes = (JArray)s0["nodes"];
            for (int i = 0; i < nodes.Count && i < 12; i++)
            {
                if (!(nodes[i] is JObject node)) continue;
                foreach (var prop in node.Properties())
                    foreach (var junk in Junk)
                    {
                        var s = (JObject)s0.DeepClone();
                        s["nodes"][i][prop.Name] = junk.DeepClone();
                        string at = $"nodes[{i}].{prop.Name}";
                        runs++;
                        List<ArtifactError> errors;
                        try { errors = ArtifactValidator.Validate(m0, s); }
                        catch (Exception) { threw++; continue; }
                        foreach (var e in errors)
                            if (string.IsNullOrEmpty(e.path)) { pathless++; firstPathless = firstPathless ?? $"{name} {at}={junk.ToString(Newtonsoft.Json.Formatting.None)}: {e}"; }
                        bool required = prop.Name == "id" || prop.Name == "kind";
                        if (Typed.TryGetValue(prop.Name, out string type) && !RightType(type, junk) && (junk.Type != JTokenType.Null || required))
                        {
                            bool refused = false;
                            string exact = $"nodes[{i}]", field = at;
                            foreach (var e in errors) refused |= e.path == exact || e.path == field || e.path.StartsWith(field + "[") || e.path.StartsWith(field + ".");
                            if (!refused) { unrefused++; firstUnrefused = firstUnrefused ?? $"{name} {at}={junk.ToString(Newtonsoft.Json.Formatting.None)}: [{Show(errors)}]"; }
                        }
                    }
            }
            foreach (var prop in m0.Properties())
                foreach (var junk in Junk)
                {
                    var m = (JObject)m0.DeepClone();
                    m[prop.Name] = junk.DeepClone();
                    runs++;
                    List<ArtifactError> errors;
                    try { errors = ArtifactValidator.Validate(m, s0); }
                    catch (Exception) { threw++; continue; }
                    foreach (var e in errors)
                        if (string.IsNullOrEmpty(e.path)) { pathless++; firstPathless = firstPathless ?? $"{name} manifest.{prop.Name}={junk.ToString(Newtonsoft.Json.Formatting.None)}: {e}"; }
                }
        }
        Check(threw == 0, $"the validator never throws ({threw} of {runs} wrong-type edits threw)");
        Gate("A1", $"every error has a path ({pathless} pathless over {runs} wrong-type edits; first: {firstPathless})", pathless == 0);
        Gate("A1", $"a typed node field with the wrong type is refused at that field ({unrefused} not; first: {firstUnrefused})", unrefused == 0);
    }

    // FromScene writes scene.json whatever manifest.scene said, so the saved manifest has to say scene.json too.
    static void WrittenSceneName()
    {
        string root = Path.Combine(Application.temporaryCachePath, "authoring_check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string before = ArtifactLibrary.RootOverride;
        ArtifactLibrary.RootOverride = root;
        try
        {
            var (m, s) = Load("arch_lever");
            m = (JObject)m.DeepClone();
            m["scene"] = "lever.json";
            var made = ArtifactLibrary.FromScene(m, s, out var errors);
            Check(made != null, "the lever with scene lever.json is saved: " + Show(errors));
            if (made == null) return;
            var saved = JObject.Parse(File.ReadAllText(Path.Combine(ArtifactLibrary.Folder(made.Id), "manifest.json")));
            Gate("A1", $"a written artifact is saved with scene scene.json (saved: {saved["scene"]})", (string)saved["scene"] == "scene.json");
            var package = ArtifactPackage.Load(made.Id, out string why);
            Gate("A1", $"the saved artifact loads ({why ?? "ok"})", package != null);
        }
        finally
        {
            ArtifactLibrary.RootOverride = before;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
