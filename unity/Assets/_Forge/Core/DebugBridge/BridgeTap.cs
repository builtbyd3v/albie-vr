#if DEVELOPMENT_BUILD || UNITY_EDITOR
// Debug bridge `tap`: press a UI control with no hand.
// What it tests: the uGUI event layer and everything after it. It sends the same enter, down, up, click, exit sequence
// DesktopRig.Point sends (and that the Interaction SDK's PointableCanvasModule ends in), so the control's own handlers
// (Button.onClick, Toggle, ForgePill, the panel's listeners) run for real, and a disabled or non-interactable control
// refuses as it would for a finger.
// What it skips: the physical part. No ray or poke hit test, no PointableCanvasModule, no check that the control is
// visible, in reach, or not covered by another panel. A control a finger can't reach can still be tapped here.

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public static class BridgeTap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install() => DebugBridge.Register("tap", Tap);

    // {"list": true, "window": "<name>"} | {"path": "..."} | {"text": "..."}, each optionally narrowed by "window".
    static void Tap(JObject req, Action<JObject> ok, Action<string> fail)
    {
        string window = ((string)req["window"] ?? "").Trim();
        Transform root = null;
        if (window.Length > 0 && (root = FindWindow(window)) == null) { fail($"no open window matches '{window}'"); return; }

        var controls = new List<Selectable>();
        foreach (var s in root != null ? root.GetComponentsInChildren<Selectable>() : UnityEngine.Object.FindObjectsByType<Selectable>(FindObjectsSortMode.None))
            if (s.isActiveAndEnabled) controls.Add(s);

        if ((bool?)req["list"] == true)
        {
            if (root == null) { fail("tap list needs \"window\" (a name from the state command)"); return; }
            var list = new JArray();
            foreach (var s in controls) list.Add(Describe(s));
            ok(new JObject { ["window"] = PathOf(root), ["controls"] = list });
            return;
        }

        string path = ((string)req["path"] ?? "").Trim(), text = ((string)req["text"] ?? "").Trim();
        if (path.Length == 0 && text.Length == 0) { fail("tap needs \"path\" or \"text\" (or \"list\": true with \"window\")"); return; }
        var matches = new List<Selectable>();
        foreach (var s in controls)
            if (path.Length > 0 ? Matches(s.transform, path) : string.Equals(Label(s), text, StringComparison.OrdinalIgnoreCase)) matches.Add(s);
        if (matches.Count == 0 && text.Length > 0)  // no exact label: a label that contains it
            foreach (var s in controls)
                if ((Label(s) ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) matches.Add(s);
        if (matches.Count == 0) { fail($"no control matches {(path.Length > 0 ? "path '" + path + "'" : "text '" + text + "'")}{(root != null ? " in " + root.name : "")}"); return; }
        if (matches.Count > 1)
        {
            var names = new List<string>();
            for (int i = 0; i < matches.Count && i < 5; i++) names.Add(PathOf(matches[i].transform));
            fail($"{matches.Count} controls match; narrow with \"window\" or a longer \"path\": {string.Join(" | ", names)}");
            return;
        }

        Selectable target = matches[0];
        if (!target.IsInteractable()) { fail($"{PathOf(target.transform)} isn't interactable"); return; }
        GameObject clicked = Press(target.gameObject);
        var result = Describe(target);
        result["clicked"] = clicked != null ? PathOf(clicked.transform) : null;
        ok(new JObject { ["pressed"] = result });
    }

    // DesktopRig.Point's press and release, in one go, at the control's center.
    static GameObject Press(GameObject go)
    {
        var events = EventSystem.current;
        var cam = Camera.main;
        var hit = new RaycastResult { gameObject = go, worldPosition = go.transform.position, worldNormal = -go.transform.forward, distance = 0.5f, module = null };
        var e = new PointerEventData(events)
        {
            button = PointerEventData.InputButton.Left,
            position = cam != null ? (Vector2)cam.WorldToScreenPoint(go.transform.position) : Vector2.zero,
            pointerCurrentRaycast = hit, pointerPressRaycast = hit, eligibleForClick = true, rawPointerPress = go,
        };
        e.pressPosition = e.position;
        ExecuteEvents.ExecuteHierarchy(go, e, ExecuteEvents.pointerEnterHandler);
        GameObject pressed = ExecuteEvents.ExecuteHierarchy(go, e, ExecuteEvents.pointerDownHandler) ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
        e.pointerPress = pressed;
        GameObject clicked = null;
        if (pressed != null)
        {
            ExecuteEvents.Execute(pressed, e, ExecuteEvents.pointerUpHandler);
            if (ExecuteEvents.GetEventHandler<IPointerClickHandler>(go) == pressed && ExecuteEvents.Execute(pressed, e, ExecuteEvents.pointerClickHandler)) clicked = pressed;
        }
        ExecuteEvents.ExecuteHierarchy(go, e, ExecuteEvents.pointerExitHandler);
        return clicked;
    }

    static JObject Describe(Selectable s)
    {
        var o = new JObject { ["path"] = PathOf(s.transform), ["label"] = Label(s), ["type"] = s.GetType().Name, ["interactable"] = s.IsInteractable() };
        if (s is Toggle toggle) o["on"] = toggle.isOn;
        else if (s.TryGetComponent(out ForgePill pill)) o["on"] = pill.On;
        return o;
    }

    static string Label(Selectable s)
    {
        var tmp = s.GetComponentInChildren<TMP_Text>();
        if (tmp != null) return tmp.text.Trim();
        var text = s.GetComponentInChildren<Text>();
        return text != null ? text.text.Trim() : s.name;
    }

    // The same windows the state command lists: active ForgePanels and OSWindows, by name or panel id.
    static Transform FindWindow(string name)
    {
        foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (mb == null || !mb.isActiveAndEnabled) continue;
            string type = mb.GetType().Name;
            if (type != "ForgePanel" && type != "OSWindow") continue;
            if (mb.name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || (mb is ForgePanel panel && string.Equals(panel.panelId, name, StringComparison.OrdinalIgnoreCase))) return mb.transform;
        }
        foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (mb == null || !mb.isActiveAndEnabled) continue;
            string type = mb.GetType().Name;
            if ((type == "ForgePanel" || type == "OSWindow") && mb.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return mb.transform;
        }
        return null;
    }

    // A path with a '/' matches the end of the full hierarchy path; a bare name matches part of the object's name.
    static bool Matches(Transform t, string path)
    {
        if (path.IndexOf('/') < 0) return t.name.IndexOf(path, StringComparison.OrdinalIgnoreCase) >= 0;
        string full = PathOf(t);
        return full.Equals(path, StringComparison.OrdinalIgnoreCase) || full.EndsWith("/" + path.TrimStart('/'), StringComparison.OrdinalIgnoreCase);
    }

    static string PathOf(Transform t)
    {
        string path = t.name;
        for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }
}
#endif
