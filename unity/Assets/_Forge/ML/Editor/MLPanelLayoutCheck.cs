using System;
using System.IO;
using Forge.ML;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Runs the real prefabs with a two-class text fixture and a ten-class image fixture.
// Unity -projectPath ... -executeMethod MLPanelLayoutCheck.RunBatch (omit -quit).
// PNGs and the report are written to Logs/MLPanelLayout; no headset or hub is required.
[InitializeOnLoad]
public static class MLPanelLayoutCheck
{
    const string Key = "Forge.ML.PanelLayout";
    static readonly string Output = "Logs/MLPanelLayout";
    static Camera camera;
    static MLSession session;
    static ForgePanel panel;
    static Model3D model;
    static ResultsWall wall;
    static int stage, passed;
    static double next;
    static string errors;

    static MLPanelLayoutCheck() { EditorApplication.playModeStateChanged += OnPlayMode; }

    [MenuItem("Forge/ML/Validate Panel Layout")]
    public static void Run()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SessionState.SetBool(Key, true);
        // Let editor startup/indexing finish before monitoring the Play-mode scene for errors.
        next = EditorApplication.timeSinceStartup + 10;
        EditorApplication.update += Begin;
    }

    static void Begin()
    {
        if (EditorApplication.timeSinceStartup < next || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        EditorApplication.update -= Begin;
        EditorApplication.isPlaying = true;
    }

    public static void RunBatch() { SessionState.SetBool(Key + ".Exit", true); Run(); }

    static void OnPlayMode(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + ".Finish", false))
        {
            SessionState.SetBool(Key + ".Finish", false);
            EditorApplication.delayCall += () => EditorApplication.Exit(SessionState.GetInt(Key + ".Code", 1));
            return;
        }
        if (!SessionState.GetBool(Key, false) || state != PlayModeStateChange.EnteredPlayMode) return;
        stage = passed = 0; errors = "";
        Directory.CreateDirectory(Output);
        Application.logMessageReceived += OnLog;
        next = EditorApplication.timeSinceStartup + 2;
        EditorApplication.update += Tick;
    }

    static void OnLog(string message, string stack, LogType type)
    {
        if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert) errors += message + "\n";
    }

    static void Tick()
    {
        if (EditorApplication.timeSinceStartup < next) return;
        next = EditorApplication.timeSinceStartup + .5;
        try
        {
            Canvas.ForceUpdateCanvases();
            switch (stage++)
            {
                case 0:
                    camera = new GameObject("Layout camera", typeof(Camera)).GetComponent<Camera>();
                    camera.tag = "MainCamera"; camera.transform.position = new Vector3(0, 0, -1);
                    camera.orthographic = true; camera.nearClipPlane = .01f; camera.farClipPlane = 10;
                    camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.08f, .09f, .12f);
                    new GameObject("Layout events", typeof(EventSystem), typeof(Oculus.Interaction.PointableCanvasModule));
                    CreateSession(2);
                    CreatePanel("Model3D", new Vector2(.56f, .50f));
                    break;
                case 1:
                    CheckModel(); Capture("model-default");
                    panel.Content.Find("Model expand all").GetComponent<Button>().onClick.Invoke();
                    Require(model.IsOpen(0), "Model Open all opens a block");
                    panel.SizeMeters = new Vector2(.38f, .36f);
                    break;
                case 2:
                    CheckModel(); Capture("model-small-expanded");
                    var layers = panel.Content.Find("Expanded layers").GetComponent<ScrollRect>();
                    layers.verticalNormalizedPosition = 0;
                    Canvas.ForceUpdateCanvases(); Capture("model-small-expanded-bottom");
                    panel.Content.Find("Model expand all").GetComponent<Button>().onClick.Invoke();
                    Require(!model.IsOpen(0), "Model Close all collapses a block");
                    panel.Content.Find("Model reset view").GetComponent<Button>().onClick.Invoke();
                    panel.SizeMeters = new Vector2(.70f, .50f);
                    break;
                case 3:
                    CheckModel(); Capture("model-wide");
                    UnityEngine.Object.Destroy(panel.gameObject);
                    CreatePanel("ResultsWall", new Vector2(.74f, .50f));
                    break;
                case 4:
                    CheckOverview(2); Capture("results-two-class");
                    panel.SizeMeters = new Vector2(.74f, .36f);
                    break;
                case 5:
                    CheckOverview(2); Capture("results-short-top");
                    var scroll = panel.Content.Find("Overview page").GetComponent<ScrollRect>();
                    Require(scroll.vertical && scroll.verticalScrollbar.gameObject.activeSelf, "Short Results exposes vertical scrolling");
                    ExecuteEvents.Execute(scroll.gameObject, new PointerEventData(EventSystem.current) { scrollDelta = new Vector2(0, -4) }, ExecuteEvents.scrollHandler);
                    Require(scroll.content.anchoredPosition.y > 0, "Scroll input moves Results content");
                    scroll.verticalNormalizedPosition = 0;
                    break;
                case 6:
                    var overview = panel.Content.Find("Overview page").GetComponent<ScrollRect>();
                    var list = (RectTransform)overview.content.Find("Weakest classes");
                    var lower = LocalRect(overview.viewport, list);
                    Require(lower.yMin >= overview.viewport.rect.yMin - .5f, "Scrolling reaches the bottom class card");
                    Capture("results-short-bottom");
                    panel.SizeMeters = new Vector2(.40f, .40f);
                    break;
                case 7:
                    CheckOverview(2); Capture("results-narrow-top");
                    wall.ShowTab(ResultsWall.Tab.Classes);
                    break;
                case 8:
                    foreach (ResultsWall.Tab tab in Enum.GetValues(typeof(ResultsWall.Tab)))
                    {
                        wall.ShowTab(tab);
                        var page = panel.Content.Find(tab + " page").GetComponent<ScrollRect>();
                        Require(page.viewport.rect.width > 0 && page.viewport.rect.height > 0, tab + " viewport survives resize");
                        Require(page.viewport.GetComponent<RectMask2D>() != null, tab + " clips overflow to its viewport");
                        if (tab != ResultsWall.Tab.Overview)
                        {
                            Require(page.horizontal && page.horizontalScrollbar.gameObject.activeSelf, tab + " exposes wide content by horizontal scrolling");
                            page.horizontalNormalizedPosition = 1;
                            Require(page.content.anchoredPosition.x < 0, tab + " scroll reaches right-hand content");
                        }
                    }
                    wall.ShowClass(1);
                    Require(wall.ActiveTab == ResultsWall.Tab.Images && wall.ActiveClassFilter == 1, "Class selection still opens filtered Images");
                    break;
                case 9:
                    Capture("results-narrow-images");
                    UnityEngine.Object.Destroy(panel.gameObject); UnityEngine.Object.Destroy(session.gameObject);
                    CreateSession(10); CreatePanel("ResultsWall", new Vector2(.74f, .50f));
                    break;
                case 10:
                    CheckOverview(4); Capture("results-ten-class-top");
                    panel.Content.Find("Overview page").GetComponent<ScrollRect>().verticalNormalizedPosition = 0;
                    break;
                case 11:
                    Capture("results-ten-class-bottom");
                    // Resize back to a wide canvas: columns and scroll bounds must grow again too.
                    panel.SizeMeters = new Vector2(.96f, .60f);
                    break;
                case 12:
                    CheckOverview(4); Capture("results-ten-class-wide");
                    Finish(null);
                    break;
            }
        }
        catch (Exception e) { Finish(e.ToString()); }
    }

    static void CreateSession(int count)
    {
        var data = new MLFixture { classes = new string[count], samples = new MLSample[500], metrics = new MLMetrics[8] };
        for (int c = 0; c < count; c++) data.classes[c] = count == 2 ? (c == 0 ? "neg" : "pos") : "Image class " + c;
        for (int i = 0; i < data.samples.Length; i++)
        {
            int truth = i % count, predicted = i % 4 == 0 ? (truth + 1) % count : truth;
            var probs = new float[count]; for (int c = 0; c < count; c++) probs[c] = .18f / (count - 1);
            probs[predicted] = .82f;
            data.samples[i] = new MLSample { id = "sample-" + i, name = "Sample " + i, text = count == 2 ? "A short review of the example product." : null,
                split = "val", true_idx = truth, label_idx = truth, pred_idx = predicted, correct = truth == predicted, probs = probs };
        }
        for (int e = 0; e < 8; e++) data.metrics[e] = new MLMetrics { epoch = e + 1, train_acc = .7f + e * .042f, val_acc = .742f,
            train_loss = .8f - e * .08f, val_loss = e <= 2 ? .719f - e * .1f : .519f + (e - 2) * .157f };
        string[] types = { "Embedding", "LSTM", "Dropout", "Linear", "ReLU", "Linear" };
        data.layers = new MLLayer[types.Length];
        for (int l = 0; l < types.Length; l++) data.layers[l] = new MLLayer { name = types[l] + l, type = types[l], @params = l == 0 ? 1280000 : 4000,
            in_shape = new[] { 48 }, out_shape = new[] { l == types.Length - 1 ? count : 64 } };
        var obj = new GameObject("Layout session"); obj.SetActive(false);
        session = obj.AddComponent<MLSession>(); session.mock = new TextAsset(JsonUtility.ToJson(data));
        session.replay = false; session.useLiveEvents = false; obj.SetActive(true); session.EnsureLoaded();
        foreach (var metric in data.metrics) session.ApplyMetrics(metric);
        // Keep completion announcements out of this synthetic UI check.
        session.replay = true; session.SetRunState("done", 8); session.replay = false;
    }

    static void CreatePanel(string prefab, Vector2 size)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Forge/ML/Prefabs/" + prefab + ".prefab");
        Require(source != null, prefab + " prefab exists");
        var obj = UnityEngine.Object.Instantiate(source);
        obj.transform.position = Vector3.zero; obj.transform.rotation = Quaternion.identity;
        panel = obj.GetComponent<ForgePanel>(); panel.SizeMeters = size;
        obj.GetComponent<Canvas>().worldCamera = camera;
        model = obj.GetComponent<Model3D>(); wall = obj.GetComponent<ResultsWall>();
        if (model != null) model.session = session;
        if (wall != null) wall.session = session;
    }

    static void CheckModel()
    {
        var root = panel.Content;
        var names = new[] { "Model summary", "Model expand all", "Model reset view", "Model instructions", "Legend" };
        for (int a = 0; a < names.Length; a++)
        {
            var r = (RectTransform)root.Find(names[a]); Inside(root, r, names[a]);
            for (int b = 0; b < a; b++) Require(!LocalRect(root, r).Overlaps(LocalRect(root, (RectTransform)root.Find(names[b]))), names[a] + " clears " + names[b]);
        }
        foreach (RectTransform key in root.Find("Legend")) Inside((RectTransform)key.parent, key, key.name);
        var layers = root.Find("Expanded layers").GetComponent<ScrollRect>();
        if (layers.gameObject.activeSelf)
        {
            Inside(root, (RectTransform)layers.transform, "Expanded layer viewport");
            var rows = new System.Collections.Generic.List<RectTransform>();
            foreach (RectTransform row in layers.content)
            {
                if (!row.gameObject.activeSelf) continue;
                Inside(layers.content, row, row.name);
                foreach (var previous in rows) Require(!LocalRect(layers.content, row).Overlaps(LocalRect(layers.content, previous)), "Expanded rows remain separate");
                rows.Add(row);
            }
            Require(layers.vertical, "Small expanded Model exposes scrolling");
        }
        foreach (var label in root.GetComponentsInChildren<TMP_Text>())
            if (label.name == "Model summary" || label.name == "Model instructions") Readable(label);
    }

    static void CheckOverview(int rows)
    {
        var page = panel.Content.Find("Overview page").GetComponent<ScrollRect>();
        var body = page.content;
        Inside(panel.Content, (RectTransform)page.transform, "Overview page");
        Inside((RectTransform)page.transform, page.viewport, "Overview viewport");
        var cards = new System.Collections.Generic.List<RectTransform>();
        foreach (RectTransform child in body)
        {
            if (!child.gameObject.activeSelf) continue;
            Inside(body, child, child.name);
            foreach (var previous in cards) Require(!LocalRect(body, child).Overlaps(LocalRect(body, previous)), child.name + " clears " + previous.name);
            cards.Add(child);
            if (child.name.EndsWith("classes"))
            {
                int found = 0;
                foreach (RectTransform row in child)
                {
                    if (!row.name.StartsWith("Class row ")) continue;
                    found++; Inside(child, row, row.name);
                    foreach (var text in row.GetComponentsInChildren<TMP_Text>()) { Inside(row, text.rectTransform, "Class label"); Readable(text); }
                }
                Require(found == rows, child.name + " keeps " + rows + " full-height rows");
            }
            foreach (var text in child.GetComponentsInChildren<TMP_Text>())
                if (child.name.StartsWith("Metric ") || child.name == "Model fit card") { Inside(child, text.rectTransform, text.text); Readable(text); }
        }
        Require(cards.Count == 9, "Overview contains fit, six metrics and two class cards");
    }

    static Rect LocalRect(RectTransform parent, RectTransform child)
    {
        var corners = new Vector3[4]; child.GetWorldCorners(corners);
        Vector2 a = parent.InverseTransformPoint(corners[0]), b = parent.InverseTransformPoint(corners[2]);
        return Rect.MinMaxRect(a.x, a.y, b.x, b.y);
    }

    static void Inside(RectTransform parent, RectTransform child, string name)
    {
        var p = parent.rect; var c = LocalRect(parent, child);
        Require(c.xMin >= p.xMin - .5f && c.xMax <= p.xMax + .5f && c.yMin >= p.yMin - .5f && c.yMax <= p.yMax + .5f,
            name + " stays in its container (" + c + " in " + p + ")");
    }

    static void Readable(TMP_Text text)
    {
        text.ForceMeshUpdate();
        Require(!text.isTextOverflowing, "Text fits: " + text.text.Replace('\n', ' '));
    }

    static void Capture(string name)
    {
        camera.orthographicSize = Mathf.Max(panel.SizeMeters.y * .58f, panel.SizeMeters.x * .46f);
        var target = new RenderTexture(1400, 1100, 24); target.Create();
        camera.targetTexture = target; camera.Render();
        var old = RenderTexture.active; RenderTexture.active = target;
        var png = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        png.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); png.Apply();
        File.WriteAllBytes(Path.Combine(Output, name + ".png"), png.EncodeToPNG());
        RenderTexture.active = old; camera.targetTexture = null;
        UnityEngine.Object.Destroy(png); target.Release(); UnityEngine.Object.Destroy(target);
    }

    static void Require(bool ok, string note)
    {
        if (!ok) throw new InvalidOperationException(note);
        passed++;
    }

    static void Finish(string failure)
    {
        EditorApplication.update -= Tick; Application.logMessageReceived -= OnLog;
        SessionState.SetBool(Key, false);
        bool ok = failure == null && string.IsNullOrEmpty(errors);
        string report = (ok ? "PASS" : "FAIL") + " ML panel layout: " + passed + " assertions\n" + failure + errors;
        File.WriteAllText(Path.Combine(Output, "report.txt"), report);
        if (ok) Debug.Log(report); else Debug.LogError(report);
        if (SessionState.GetBool(Key + ".Exit", false))
        {
            SessionState.SetBool(Key + ".Exit", false);
            SessionState.SetBool(Key + ".Finish", true); SessionState.SetInt(Key + ".Code", ok ? 0 : 1);
        }
        EditorApplication.isPlaying = false;
    }
}
