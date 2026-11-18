using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie's CAD tools that run on the headset itself (<c>CAD/Resources/AlbieTools/cad_device.json</c>), so they work with
/// the hub, with Albie's cloud and offline alike, and answer at once:
/// - <c>cad_turn</c>: rotate the open design about its centre, on the desk or expanded ("rotate it", "turn it around",
///   "spin the robot", "show me the back", "stop"). View only; an exploded or expanded model stays so. The hub has the same
///   tool for its own voice (hub/cad/tools.py), which reaches the viewer as cad.turn (<see cref="CadSession"/>).
/// The other cad_ tools build or read designs, so they go to the hub or the cloud (Core/Resources/AlbieTools/cloud.json).
/// </summary>
public static class CadVoiceTools
{
    public const string File = "cad_device";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    public static void Register() => AlbieTools.Register(File, new Dictionary<string, AlbieTools.Handler>
    {
        ["cad_turn"] = Turn,
    });

    /// <summary>Opens the CAD view (ui_open_view cad); null when there is no way to. The editor checks swap it for a stub.</summary>
    public static Func<JObject> OpenCadView = () =>
        AlbieTools.Has("ui_open_view") ? AlbieTools.Call("ui_open_view", new JObject { ["view"] = "cad" }) : null;

    /// <summary>cad_turn on the headset. Never throws and never waits.</summary>
    public static JObject Turn(JObject args)
    {
        CadTurnRequest request;
        try { request = (args ?? new JObject()).ToObject<CadTurnRequest>(); }
        catch (Exception e) { return AlbieTools.Error("cad_turn's arguments didn't read: " + e.Message); }
        if (request == null) request = new CadTurnRequest();
        if (CadViewer.Active != null) return CadViewer.Active.ApplyTurn(request);

        // No CAD view up: open it, and the turn waits a few seconds for the model (CadViewer.RequestTurn).
        var open = OpenCadView();
        if (open == null) return AlbieTools.Error("The CAD view isn't open. Open CAD first (os_open kind tool, id cad), then turn it.");
        if (open["error"] != null) return AlbieTools.Error((string)open["error"]);
        var now = CadViewer.RequestTurn(request);  // the view may have come up at once: then it turned already
        if (now != null) return now;
        return new JObject { ["ok"] = true, ["did"] = "opening the CAD view; it turns as soon as the design shows", ["pending"] = true };
    }
}
