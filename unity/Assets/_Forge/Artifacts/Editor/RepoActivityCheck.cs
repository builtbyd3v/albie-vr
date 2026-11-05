using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forge.Artifacts;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Headless checks for the repo-activity artifact (PROJECT_CONTEXT 16.3, Person 5, Nov 14): snapshot tables (C-12
/// section 7), where data comes from with no connector host, arrays built from a table, and artifacts Albie writes
/// itself (<c>artifact_create {manifest, scene}</c>). Forge > Artifacts > Check Repo Activity.
/// </summary>
public static class RepoActivityCheck
{
    static string root;

    [MenuItem("Forge/Artifacts/Check Repo Activity")]
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
        root = Path.Combine(Application.temporaryCachePath, "repo_activity_check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string before = ArtifactLibrary.RootOverride;
        string notebookProject = NotebookStore.ProjectId, notebookRoot = NotebookStore.Root;
        var provider = ArtifactData.Provider;
        ArtifactLibrary.RootOverride = root;
        NotebookStore.Close();
        var sb = new StringBuilder();
        bool ok = true;
        try
        {
            ok &= Check(sb, "snapshot tables: nulls kept, wrong shapes refused", Tables);
            ok &= Check(sb, "data comes from the connector host, then the snapshot, then the bundled sample", Sources);
            ok &= Check(sb, "validator: connector permission, snapshot paths", Validator);
            ok &= Check(sb, "the fixture and template can be built", Buildable);
            ok &= Check(sb, "the city: one block per commit, on a grid, standing on the base", Grid);
            ok &= Check(sb, "height follows lines changed, colour follows the author, null is grey", Mapping);
            ok &= Check(sb, "more rows than the copy limit: cut, and said", Limit);
            ok &= Check(sb, "a column the table doesn't have is refused before building", Problems);
            ok &= Check(sb, "Albie's own manifest and scene: saved, versioned, errors as data", Written);
            ok &= Check(sb, "tools: data sources listed, template made", Tools);
        }
        finally
        {
            ArtifactData.Provider = provider;
            ArtifactLibrary.RootOverride = before;
            NotebookStore.Close();
            if (notebookProject != null) NotebookStore.Open(notebookProject, notebookRoot);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return (ok ? "PASS" : "FAIL") + " repo activity check\n" + sb;
    }

    delegate string CheckFn(out string note);

    static bool Check(StringBuilder sb, string name, CheckFn fn)
    {
        string failure, note;
        try { failure = fn(out note); }
        catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; note = null; }
        sb.Append(failure == null ? "  ok   " : "  FAIL ").Append(name);
        if (failure != null) sb.Append(": ").Append(failure);
        else if (!string.IsNullOrEmpty(note)) sb.Append(": ").Append(note);
        sb.Append('\n');
        return failure == null;
    }

    static JObject Snapshot(string json) => (JObject)NotebookFiles.Parse(json);

    static string Tables(out string note)
    {
        note = null;
        var fixture = ArtifactPackage.Load("repo_city", out string error);
        if (fixture == null) return error;
        var raw = fixture.ReadData("data/repo_activity.json", out string why);
        if (raw == null) return why;
        var table = ArtifactTable.Parse("repo_activity", raw, out error);
        if (table == null) return error;
        if (table.Rows.Count != 12 || !table.Sample || table.FetchedAt != "2026-11-12T09:00:00Z") return $"{table.Rows.Count} rows, sample {table.Sample}, fetched {table.FetchedAt}";
        int lines = table.Column("lines_changed");
        if (!double.IsNaN(table.Number(2, lines)) || table.Number(0, lines) != 120) return "row 0 or the null in row 2 read wrong";
        string Refused(string json) => ArtifactTable.Parse("t", Snapshot(json), out string e) == null ? e : "accepted";
        string shortRow = Refused("{\"columns\":[{\"name\":\"a\",\"type\":\"string\"},{\"name\":\"n\",\"type\":\"number\"}],\"rows\":[[\"x\"]]}");
        string wrongType = Refused("{\"columns\":[{\"name\":\"n\",\"type\":\"number\"}],\"rows\":[[\"12\"]]}");
        string badColumn = Refused("{\"columns\":[{\"name\":\"n\",\"type\":\"date\"}],\"rows\":[]}");
        if (shortRow == "accepted" || wrongType == "accepted" || badColumn == "accepted") return $"{shortRow} / {wrongType} / {badColumn}";
        if (fixture.ReadData("../manifest.json", out why) != null || fixture.ReadData("data/../../x.json", out _) != null) return "read outside data/";
        note = $"refused: \"{shortRow}\", \"{wrongType}\", \"{badColumn}\"";
        return null;
    }

    static string Sources(out string note)
    {
        note = null;
        ArtifactData.Provider = null;
        var fixture = ArtifactPackage.Load("repo_city", out _);
        var fromFile = ArtifactData.Resolve(fixture, out string problem);
        if (fromFile == null || fromFile["repo_activity"].From != "snapshot") return "fixture: " + (problem ?? fromFile["repo_activity"].From);
        if (ArtifactData.Say(fromFile) == null || !ArtifactData.Say(fromFile).Contains("sample data")) return "sample data wasn't said: " + ArtifactData.Say(fromFile);

        ArtifactLibrary.FromTemplate("repo_activity", null);
        var template = ArtifactPackage.Load("repo_activity", out _);
        var bundled = ArtifactData.Resolve(template, out problem);
        if (bundled == null || bundled["commits"].From != "bundled" || !bundled["commits"].Sample || bundled["commits"].Rows.Count != 18) return "template: " + problem;

        var live = Snapshot("{\"fetched_at\":\"2026-11-14T10:00:00Z\",\"source\":\"connector:source_control/list_commits\",\"columns\":[{\"name\":\"author\",\"type\":\"string\"},{\"name\":\"lines_changed\",\"type\":\"number\"}],\"rows\":[[\"Dana\",10],[\"Eli\",20]]}");
        string asked = null;
        ArtifactData.Provider = source => { asked = source; return live; };
        var connected = ArtifactData.Resolve(template, out problem);
        if (connected == null || connected["commits"].From != "connector" || connected["commits"].Sample || connected["commits"].Rows.Count != 2) return "connector: " + problem;
        if (asked != "connector:source_control/list_commits") return "asked the connector host for " + asked;
        if (ArtifactData.Say(connected) != null) return "said something about live data: " + ArtifactData.Say(connected);

        ArtifactData.Provider = source => throw new InvalidOperationException("offline");
        var fellBack = ArtifactData.Resolve(template, out problem);
        if (fellBack == null || fellBack["commits"].From != "bundled") return "a failing connector host wasn't survived";

        ArtifactData.Provider = null;
        var nowhere = (JObject)template.Manifest.DeepClone();
        nowhere["data"][0]["source"] = "connector:issues/list_issues";
        nowhere["permissions"]["connectors"] = new JArray("issues:read");
        var orphan = ArtifactData.Resolve(PackageFrom("orphan", nowhere, template.Scene), out problem);
        if (orphan != null || problem == null || !problem.Contains("no copy of it")) return "a source with no data anywhere: " + problem;
        note = $"no_data says: \"{problem}\"";
        return null;
    }

    static ArtifactPackage PackageFrom(string id, JObject manifest, JObject scene)
    {
        manifest = (JObject)manifest.DeepClone();
        manifest["id"] = id;
        ArtifactLibrary.FromScene(manifest, scene, out var errors);
        if (errors.Count > 0) throw new InvalidOperationException(errors[0].ToString());
        return ArtifactPackage.Load(id, out _);
    }

    static string Validator(out string note)
    {
        note = null;
        var fixture = ArtifactPackage.Load("repo_city", out _);
        var errors = ArtifactValidator.Validate(fixture.Manifest, fixture.Scene);
        if (errors.Count > 0) return "fixture: " + errors[0];
        var noPermission = (JObject)fixture.Manifest.DeepClone();
        noPermission["permissions"]["connectors"] = new JArray();
        if (!ArtifactValidator.Validate(noPermission, fixture.Scene).Exists(e => e.code == "permission_missing")) return "reading a connector without asking wasn't refused";
        var escape = (JObject)fixture.Manifest.DeepClone();
        escape["data"][0]["snapshot"] = "../../approvals.json";
        if (!ArtifactValidator.Validate(escape, fixture.Scene).Exists(e => e.code == "bad_value")) return "a snapshot outside data/ wasn't refused";
        var unknownTable = (JObject)fixture.Scene.DeepClone();
        unknownTable["nodes"][0]["source"]["table"] = "nope";
        if (!ArtifactValidator.Validate(fixture.Manifest, unknownTable).Exists(e => e.code == "unknown_ref")) return "an unknown table wasn't refused";
        return null;
    }

    static string Buildable(out string note)
    {
        note = null;
        foreach (string id in new[] { "repo_city", "repo_activity" })
        {
            JObject manifest, scene;
            if (id == "repo_activity") ArtifactLibrary.TryTemplate(id, out manifest, out scene);
            else { var p = ArtifactPackage.Load(id, out _); manifest = p.Manifest; scene = p.Scene; }
            var errors = ArtifactValidator.Validate(manifest, scene);
            if (errors.Count > 0) return id + ": " + errors[0];
            var missing = ArtifactNodes.Missing(scene);
            if (missing.Count > 0) return id + " can't build " + string.Join(", ", missing);
        }
        var lines = ArtifactApprovals.Lines(ArtifactPackage.Load("repo_city", out _).Manifest);
        if (lines.Count != 1 || lines[0] != "Reads your connected source control") return "permission sheet: " + string.Join("; ", lines);
        note = "first open asks: \"Reads your connected source control\"";
        return null;
    }

    // Builds the "city" node the way the host does, from a table, with no scene around it.
    static (GameObject go, List<GameObject> copies, ArtifactBuild build) City(ArtifactTable table, JObject node = null)
    {
        if (node == null)
        {
            var fixture = ArtifactPackage.Load("repo_city", out _);
            node = (JObject)fixture.Scene["nodes"][0];
        }
        var manifest = ArtifactPackage.Load("repo_city", out _).Manifest;
        var build = new ArtifactBuild
        {
            Inputs = new ArtifactInputs(manifest), Nodes = new Dictionary<string, GameObject>(),
            Tables = new Dictionary<string, ArtifactTable> { [table.Name] = table },
        };
        var go = ArtifactArray.Build(node, build);
        return (go, build.Arrays[node.Value<string>("id")], build);
    }

    static ArtifactTable FixtureTable()
    {
        var p = ArtifactPackage.Load("repo_city", out _);
        return ArtifactTable.Parse("repo_activity", p.ReadData("data/repo_activity.json", out _), out _);
    }

    static float Height(GameObject copy) => copy.GetComponentInChildren<Renderer>().transform.lossyScale.y;
    static Color Tint(GameObject copy) => copy.GetComponentInChildren<Renderer>().sharedMaterial.GetColor("_BaseColor");

    static string Grid(out string note)
    {
        note = null;
        var (go, copies, _) = City(FixtureTable());
        try
        {
            if (copies.Count != 12) return copies.Count + " blocks";
            float pitch = copies[1].transform.localPosition.x - copies[0].transform.localPosition.x;
            if (Mathf.Abs(pitch - 0.09f) > 0.001f) return "column pitch " + pitch;
            if (Mathf.Abs(copies[6].transform.localPosition.z - copies[0].transform.localPosition.z - 0.09f) > 0.001f) return "row 2 isn't one pitch back";
            var centre = Vector3.zero;
            foreach (var c in copies) centre += c.transform.localPosition;
            centre /= copies.Count;
            if (Mathf.Abs(centre.x) > 0.001f || Mathf.Abs(centre.z) > 0.001f) return "grid isn't centred: " + centre;
            foreach (var c in copies)
            {
                float bottom = c.transform.localPosition.y - Height(c) * 0.5f;
                if (Mathf.Abs(bottom) > 0.001f) return $"{c.name} floats {bottom:0.000} m";
            }
            note = "6 by 2, 9 cm apart, centred, every block's bottom at y = 0";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static string Mapping(out string note)
    {
        note = null;
        var table = FixtureTable();
        var (go, copies, _) = City(table);
        try
        {
            // Rows: 310 is the most lines (row 3), 9 the fewest (row 7), and rows 2 and 10 are null.
            if (Mathf.Abs(Height(copies[3]) - 0.4f) > 0.001f) return "the biggest commit is " + Height(copies[3]);
            if (Mathf.Abs(Height(copies[7]) - 0.02f) > 0.001f) return "the smallest commit is " + Height(copies[7]);
            float expected = 0.02f + (120f - 9f) / (310f - 9f) * 0.38f;
            if (Mathf.Abs(Height(copies[0]) - expected) > 0.001f) return $"120 lines is {Height(copies[0])}, expected {expected}";
            var grey = ArtifactLook.Color(ArtifactArray.NoDataColor, Color.magenta);
            foreach (int i in new[] { 2, 10 })
                if (Mathf.Abs(Height(copies[i]) - 0.02f) > 0.001f || Tint(copies[i]) != grey) return $"null row {i}: height {Height(copies[i])}, colour {Tint(copies[i])}";
            if (Tint(copies[0]) != Tint(copies[3]) || Tint(copies[0]) == Tint(copies[1]) || Tint(copies[1]) == Tint(copies[5]) || Tint(copies[0]) == grey)
                return "authors don't each get their own colour";
            note = $"310 lines → 0.40 m, 9 → 0.02 m, nulls grey at 0.02 m; 3 authors, 3 colours";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static string Limit(out string note)
    {
        note = null;
        var rows = new JArray();
        for (int i = 0; i < 300; i++) rows.Add(new JArray("A", i));
        var big = ArtifactTable.Parse("repo_activity", new JObject
        {
            ["columns"] = new JArray(new JObject { ["name"] = "author", ["type"] = "string" }, new JObject { ["name"] = "lines_changed", ["type"] = "number" }),
            ["rows"] = rows,
        }, out _);
        var (go, copies, build) = City(big);
        try
        {
            if (copies.Count != ArtifactSchema.MaxArrayCopies) return copies.Count + " copies";
            if (build.Notes.Count != 1 || !build.Notes[0].Contains("300")) return "notes: " + string.Join(" | ", build.Notes);
            note = $"\"{build.Notes[0]}\"";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static string Problems(out string note)
    {
        note = null;
        var table = FixtureTable();
        var tables = new Dictionary<string, ArtifactTable> { ["repo_activity"] = table };
        var node = (JObject)ArtifactPackage.Load("repo_city", out _).Scene["nodes"][0];
        if (ArtifactArray.Problem(node, tables) != null) return "the fixture has a problem: " + ArtifactArray.Problem(node, tables);
        var typo = (JObject)node.DeepClone();
        typo["map"]["height"]["column"] = "lines";
        string missing = ArtifactArray.Problem(typo, tables);
        var text = (JObject)node.DeepClone();
        text["map"]["height"]["column"] = "author";
        string notNumber = ArtifactArray.Problem(text, tables);
        if (missing == null || !missing.Contains("lines_changed") || notNumber == null) return $"{missing} / {notNumber}";
        note = $"\"{missing}\"";
        return null;
    }

    static string Written(out string note)
    {
        note = null;
        var fixture = ArtifactPackage.Load("repo_city", out _);
        var manifest = (JObject)fixture.Manifest.DeepClone();
        manifest["id"] = "what_shipped";
        var made = ArtifactLibrary.FromScene(manifest, fixture.Scene, out var errors);
        if (made == null || made.Change != "created" || made.Version != 1) return "first: " + (errors.Count > 0 ? errors[0].ToString() : made.Change);
        var again = ArtifactLibrary.FromScene(manifest, fixture.Scene, out _);
        if (again.Change != "unchanged" || again.Version != 1) return $"again: {again.Change} v{again.Version}";
        var taller = (JObject)fixture.Scene.DeepClone();
        taller["nodes"][0]["map"]["height"]["range"] = new JArray(0.02, 0.6);
        var v2 = ArtifactLibrary.FromScene(manifest, taller, out _);
        if (v2.Change != "updated" || v2.Version != 2) return $"taller: {v2.Change} v{v2.Version}";

        var bad = (JObject)fixture.Scene.DeepClone();
        bad["nodes"][0]["kind"] = "cube";
        var badManifest = (JObject)manifest.DeepClone();
        badManifest["id"] = "never_saved";
        var refused = ArtifactTools.Create(new JObject { ["manifest"] = badManifest, ["scene"] = bad, ["open"] = false });
        if ((string)refused["status"] != "invalid" || !(refused["errors"] is JArray list) || list.Count == 0 || (string)list[0]["code"] != "unknown_kind") return "refusal: " + refused;
        if (Directory.Exists(ArtifactLibrary.Folder("never_saved"))) return "an invalid artifact was written";

        var gltf = (JObject)fixture.Scene.DeepClone();
        ((JArray)gltf["nodes"]).Add(new JObject { ["id"] = "model", ["kind"] = "gltf", ["asset"] = "assets/m.glb" });
        var withAsset = (JObject)manifest.DeepClone();
        withAsset["id"] = "needs_gltf";
        withAsset["assets"] = new JArray(new JObject { ["path"] = "assets/m.glb", ["sha256"] = "0" });
        var cant = ArtifactLibrary.FromScene(withAsset, gltf, out errors);
        if (cant != null || errors.Count == 0 || errors[0].code != "cant_build") return "a node this host can't build: " + (errors.Count > 0 ? errors[0].ToString() : "saved");

        try { ArtifactTools.Create(new JObject { ["template"] = "repo_activity", ["manifest"] = manifest, ["scene"] = fixture.Scene }); return "template and manifest together was accepted"; }
        catch (ArgumentException e) when (e.Message.StartsWith("bad_value")) { }
        note = $"v1, unchanged, v2; refused as data: {list[0]["code"]} at {list[0]["path"]}; gltf: {errors[0].code}";
        return null;
    }

    static string Tools(out string note)
    {
        note = null;
        ArtifactData.Provider = null;
        var list = ArtifactTools.List(new JObject());
        JObject commits = null;
        foreach (JObject s in (JArray)list["data_sources"]) if ((string)s["source"] == "connector:source_control/list_commits") commits = s;
        if (commits == null) return "no source_control/list_commits in data_sources";
        if ((string)commits["permission"] != "source_control:read" || commits.Value<bool>("sample_only") != true || ((JArray)commits["columns"]).Count != 5) return "listed as " + commits;
        bool offered = false;
        foreach (var t in (JArray)list["templates"]) offered |= (string)t["template"] == "repo_activity";
        if (!offered) return "repo_activity isn't offered";
        var made = ArtifactTools.Create(new JObject { ["template"] = "repo_activity", ["open"] = false });
        if ((string)made["id"] != "repo_activity") return "create: " + made;
        var json = Resources.Load<TextAsset>("AlbieTools/artifacts").text;
        if (!json.Contains("\"repo_activity\"]") || !json.Contains("\"manifest\"")) return "artifact_create's schema doesn't offer repo_activity or a written manifest";
        note = "sample only, columns sha, author, message, lines_changed, day";
        return null;
    }
}
