using System;
using Forge.ML;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Bit's hands on the workspace (hub/agent/ui_tools.py). ui.open_mode {mode} opens that mode; ui.highlight
/// {panel, item_ids?, class_idx?} pulses the open panel's frame, and on ml-results selects the sample (first id) or
/// the class. Sits on the Workspace object in Main (MainSceneBuilder adds it). Event-driven only, no Update.
/// </summary>
[RequireComponent(typeof(Workspace))]
public class BitUiDirector : MonoBehaviour
{
    public int pulses = 2;

    Workspace workspace;
    IDisposable openMode;

    void Awake() => workspace = GetComponent<Workspace>();

    void OnEnable()
    {
        openMode = ForgeNet.On("ui.open_mode", OnOpenMode);
        ForgeEvents.OnUiHighlight += OnHighlight;
    }

    void OnDisable()
    {
        openMode?.Dispose();
        openMode = null;
        ForgeEvents.OnUiHighlight -= OnHighlight;
    }

    void OnOpenMode(JToken payload)
    {
        string mode = payload?.Value<string>("mode");
        if (string.IsNullOrEmpty(mode)) return;
        if (workspace.ActiveMode != mode) workspace.Open(mode);
    }

    void OnHighlight(UiHighlight h)
    {
        if (h == null || string.IsNullOrEmpty(h.panel)) return;
        // ponytail: only panels of the open mode; Bit opens the mode first (ui_open_mode).
        if (!workspace.TryGetItemTransform(h.panel, out Transform t))
        {
            Debug.Log($"[BitUiDirector] {h.panel} isn't open; nothing to highlight.");
            return;
        }
        var handles = t.GetComponent<PanelManipulator>();
        if (handles != null) handles.Pulse(pulses);

        if (h.panel != "ml-results") return;
        var wall = t.GetComponentInChildren<ResultsWall>();
        if (wall == null) return;
        if (h.itemIds != null && h.itemIds.Count > 0) wall.SelectById(h.itemIds[0]);
        else if (h.classIdx.HasValue) wall.ChooseClass(h.classIdx.Value);
    }
}
