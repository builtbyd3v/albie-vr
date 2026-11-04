using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.Artifacts
{
    /// <summary>
    /// Opens an artifact: read it, validate it, ask for its permissions the first time, then build it. It needs no project,
    /// no desk and no hub, so a standalone artifact is as normal as a project one (docs/ARTIFACTS_BRIEF.md). Anything it
    /// can't do it says so in a panel: it never shows half an artifact.
    /// Put the prefab (Artifacts/Prefabs/ArtifactHost.prefab) where artifacts should appear, then call <see cref="Open"/>.
    /// </summary>
    public class ArtifactHost : MonoBehaviour
    {
        public ForgePanel panelPrefab;
        public ForgeVolume volumePrefab;
        public Vector3 volumeSize = new Vector3(0.45f, 0.45f, 0.45f);
        [Tooltip("Where a panel with no pos goes, from this transform (m).")]
        public Vector3 panelOffset = new Vector3(0.5f, 0.3f, 0f);
        public Color accent = ForgeTheme.Tool;

        readonly List<GameObject> spawned = new List<GameObject>();
        readonly Dictionary<string, GameObject> nodes = new Dictionary<string, GameObject>();
        ArtifactPackage open;
        ArtifactBuild build;
        Coroutine reveal;

        /// <summary>The artifact showing right now, or null.</summary>
        public string OpenId => build != null ? open.Id : null;
        public string OpenTitle => open != null ? open.Title : "";

        /// <summary>The last reason an artifact didn't open, for tools and the agent. Null if it did.</summary>
        public string LastProblem { get; private set; }

        /// <summary>What was wrong, as data, so the agent can fix it (C-12 section 9).</summary>
        public IReadOnlyList<ArtifactError> LastErrors { get; private set; } = new List<ArtifactError>();

        public ArtifactInputs Inputs => build?.Inputs;

        public void Open(string id)
        {
            Close();
            var package = ArtifactPackage.Load(id, out string error);
            if (package == null)
            {
                Refuse(id, error);
                return;
            }
            open = package;
            LastErrors = ArtifactValidator.Validate(package.Manifest, package.Scene);
            if (LastErrors.Count > 0)
            {
                foreach (var e in LastErrors) Debug.LogWarning($"[Artifacts] {id}: {e}");
                Refuse(package.Title, $"This artifact has {LastErrors.Count} problem{(LastErrors.Count == 1 ? "" : "s")}, so it didn't open.\n\n{LastErrors[0].message}");
                return;
            }
            if (ArtifactApprovals.NeedsApproval(package.Manifest)) AskPermission();
            else Show();
        }

        public void Close()
        {
            if (build != null) build.Inputs.Changed -= OnInputChanged;
            if (reveal != null) StopCoroutine(reveal);
            reveal = null;
            foreach (var go in spawned)
            {
                if (go == null) continue;
                // Destroy waits for the end of the frame. Detached, the old artifact is gone for anyone looking now.
                go.SetActive(false);
                go.transform.SetParent(null, false);
                Destroy(go);
            }
            spawned.Clear();
            nodes.Clear();
            build = null;
            open = null;
            LastProblem = null;
            LastErrors = new List<ArtifactError>();
        }

        /// <summary>A ForgePanel owned by the open artifact, with mouse support and no placeholder.</summary>
        public ForgePanel NewPanel(string title)
        {
            var panel = Instantiate(panelPrefab, transform);
            panel.name = "Panel " + title;
            panel.Title = title;
            panel.SetBadge("ART", accent);
            if (panel.GetComponent<GraphicRaycaster>() == null) panel.gameObject.AddComponent<GraphicRaycaster>();  // mouse (DesktopRig)
            var placeholder = panel.Content.Find("Placeholder");
            if (placeholder != null) placeholder.gameObject.SetActive(false);
            PlacePanel(panel.transform, panelOffset);
            spawned.Add(panel.gameObject);
            return panel;
        }

        /// <summary>A button was pressed: run its <c>on_input</c> rules.</summary>
        public void Fire(string input)
        {
            if (!(open?.Scene["rules"] is JArray rules)) return;
            foreach (var rule in rules)
                if (rule.Value<string>("type") == "on_input" && rule.Value<string>("input") == input && rule["do"] is JArray actions)
                    foreach (var action in actions) Do(action);
        }

        void Do(JToken action)
        {
            string target = action.Value<string>("set");
            if (target == null)
            {
                Debug.Log($"[Artifacts] {action} waits for runs and timelines (Nov 10 and Nov 14)");
                return;
            }
            var parts = target.Split('.');
            if (parts.Length == 2 && parts[1] == "visible" && nodes.TryGetValue(parts[0], out var node))
                node.SetActive(action.Value<bool>("value"));
            else Debug.LogWarning($"[Artifacts] can't set {target} yet: only <node>.visible is built");
        }

        void AskPermission()
        {
            var manifest = open.Manifest;
            var panel = NewPanel(open.Title);
            panel.Subtitle = "First time opening";
            float y = ArtifactWidgets.Note(panel.Content, 0f, "This artifact would like to:", true) + 10f;
            foreach (var line in ArtifactApprovals.Lines(manifest)) y += ArtifactWidgets.Note(panel.Content, y, "•  " + line) + 6f;
            y += 12f;
            float x = ArtifactWidgets.Pill(panel.Content, y, 0f, "Allow", () =>
            {
                ArtifactApprovals.Approve(manifest);
                string id = open.Id;
                Close();
                Open(id);
            }) + 10f;
            ArtifactWidgets.Pill(panel.Content, y, x, "Not now", Close);
            panel.SizeMeters = new Vector2(ArtifactWidgets.PanelWidth, ArtifactWidgets.PanelHeight(y + 34f)) * ForgePanel.MetersPerUnit;
        }

        void Show()
        {
            var missing = ArtifactNodes.Missing(open.Scene);
            if (missing.Count > 0)
            {
                Refuse(open.Title, $"This artifact is valid, but this version can't show it yet. It needs: {string.Join(", ", missing)}.");
                return;
            }

            var scenario = (open.Manifest["scenarios"] as JArray)?[0];
            var inputs = new ArtifactInputs(open.Manifest, scenario?["params"] as JObject);
            var root = new GameObject("Content").transform;
            build = new ArtifactBuild { Host = this, Inputs = inputs, Root = root };

            var list = open.Scene["nodes"] as JArray ?? new JArray();
            foreach (JObject node in list) nodes[node.Value<string>("id")] = ArtifactNodes.Build(node, build);
            foreach (JObject node in list) Arrange(node, root);

            if (open.Scene.Value<string>("space") == "volume") ShowVolume(root);
            else Hold(root);

            inputs.Changed += OnInputChanged;
            reveal = StartCoroutine(Reveal(root));
        }

        void Hold(Transform root)
        {
            root.SetParent(transform, false);
            spawned.Add(root.gameObject);
        }

        void ShowVolume(Transform root)
        {
            var volume = Instantiate(volumePrefab, transform);
            volume.name = "Volume " + open.Title;
            volume.Title = open.Title;
            volume.SetAccent(accent);
            volume.SetSize(volumeSize);
            volume.SetContent(root.gameObject);
            spawned.Add(volume.gameObject);
        }

        // Panels live beside the scene, not in it: they keep their real size however the scene is scaled to fit.
        void Arrange(JObject n, Transform root)
        {
            var go = nodes[n.Value<string>("id")];
            bool panel = n.Value<string>("kind") == "panel";
            if (!panel)
            {
                string parent = n.Value<string>("parent");
                go.transform.SetParent(parent != null ? nodes[parent].transform : root, false);
                var pos = n["pos"];
                var rot = n["rot"];
                var scale = n["scale"];
                build.Apply(() =>
                {
                    go.transform.localPosition = build.Inputs.Vector(pos, Vector3.zero);
                    go.transform.localEulerAngles = build.Inputs.Vector(rot, Vector3.zero);
                    go.transform.localScale = build.Inputs.Vector(scale, Vector3.one);
                }, ArtifactInputs.IsBound(pos) || ArtifactInputs.IsBound(rot) || ArtifactInputs.IsBound(scale));
            }
            else if (n["pos"] != null) PlacePanel(go.transform, build.Inputs.Vector(n["pos"], panelOffset));
            if (n.Value<bool?>("visible") == false) go.SetActive(false);
        }

        // Facing whoever is looking, from wherever it was put. A canvas faces -Z, so it looks along eye to panel.
        void PlacePanel(Transform panel, Vector3 offset)
        {
            panel.localPosition = offset;
            var cam = Camera.main;
            if (cam != null) panel.rotation = Quaternion.LookRotation(panel.position - cam.transform.position);
        }

        void Refuse(string title, string message)
        {
            Debug.Log($"[Artifacts] {title}: {message}");
            string problem = message;
            var panel = NewPanel(title);
            float height = ArtifactWidgets.Note(panel.Content, 0f, message, true) + 16f;
            ArtifactWidgets.Pill(panel.Content, height, 0f, "Close", Close);
            panel.SizeMeters = new Vector2(ArtifactWidgets.PanelWidth, ArtifactWidgets.PanelHeight(height + 34f)) * ForgePanel.MetersPerUnit;
            LastProblem = problem;
        }

        void OnInputChanged(string input) => build.Refresh();

        // Grows in rather than popping, so it feels placed. Scales the root's child space only, never the Fit.
        IEnumerator Reveal(Transform root)
        {
            for (float t = 0f; t < 1f; t += Time.deltaTime / ForgeTheme.UnfoldTime)
            {
                root.localScale = Vector3.one * Mathf.SmoothStep(ForgeTheme.UnfoldScale, 1f, t);
                yield return null;
            }
            root.localScale = Vector3.one;
            reveal = null;
        }
    }
}
