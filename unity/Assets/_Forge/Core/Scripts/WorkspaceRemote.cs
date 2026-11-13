using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie opens and closes the workspace views (docs/archive/PET_AGENT_CONTRACT.md Addendum H). The hub's
/// <c>ui.mode {mode}</c> opens that mode (<c>""</c> closes the open one), then this answers at once with
/// <c>workspace.mode {pet_id, mode, reason?}</c>, so Albie's tool result says what really happened: <c>mode</c> is
/// what is open now, <c>reason</c> why it isn't what was asked (<c>not_at_desk</c>: DeskPresence is Away, so
/// <see cref="Workspace.Open"/> would refuse; <c>unknown_mode</c>: no such mode). Asking for the open mode keeps it open
/// (Open, not Toggle). Installs itself on the scene's Workspace when Play starts, so neither Main nor a prefab needs
/// setting up; a scene without a Workspace (the module sandboxes) gets nothing. ArrivalDirector still sends its own
/// workspace.mode on every change; the hub takes either.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Workspace))]
public class WorkspaceRemote : MonoBehaviour
{
    const string WorkspaceModeType = "workspace.mode";
    const string PetId = "bit";

    Workspace workspace;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        var ws = Workspace.Current != null ? Workspace.Current : FindAnyObjectByType<Workspace>();
        if (ws != null && ws.GetComponent<WorkspaceRemote>() == null) ws.gameObject.AddComponent<WorkspaceRemote>();
    }

    void Awake() => workspace = GetComponent<Workspace>();

    void OnEnable() => ForgeEvents.OnUiMode += OnUiMode;

    void OnDisable() => ForgeEvents.OnUiMode -= OnUiMode;

    // Hub off: Albie's ui_open_view runs here instead of the hub's agent/workspace.py.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void RegisterTools() =>
        AlbieTools.Register("view", new System.Collections.Generic.Dictionary<string, AlbieTools.Handler> { ["ui_open_view"] = OpenViewTool });

    static JObject OpenViewTool(JObject args)
    {
        var ws = Workspace.Current != null ? Workspace.Current : FindAnyObjectByType<Workspace>();
        if (ws == null) return AlbieTools.Error("There's no workspace here, so no view can open.");
        string view = ((string)args["view"] ?? "").Trim().ToLowerInvariant();
        string reason = Apply(ws, view == "none" ? "" : view);
        if (reason == null) return new JObject { ["ok"] = true, ["view"] = ws.ActiveMode ?? "" };
        return new JObject
        {
            ["error"] = reason == "not_at_desk"
                ? "The user isn't at their desk, so the workspace can't open; ask them to sit down at it first."
                : $"No view '{view}'; the views are cad, code, ml, messages (or none to close).",
            ["view"] = ws.ActiveMode ?? "",
        };
    }

    // Open a mode ("" closes the open one). Returns why it isn't open (not_at_desk / unknown_mode), or null.
    static string Apply(Workspace workspace, string want)
    {
        if (want.Length == 0) workspace.Close();
        else if (workspace.modes.Find(x => x.id == want) == null) return "unknown_mode";
        else if (workspace.presence == null || !workspace.presence.AtDesk) return "not_at_desk";
        else if (workspace.ActiveMode != want) workspace.Open(want);
        return null;
    }

    void OnUiMode(UiMode m)
    {
        string want = (m?.mode ?? "").Trim().ToLowerInvariant();
        string reason = Apply(workspace, want);

        var answer = new JObject { ["pet_id"] = PetId, ["mode"] = workspace.ActiveMode ?? "" };
        if (reason != null) answer["reason"] = reason;
        ForgeNet.Send(WorkspaceModeType, answer);
        Debug.Log($"[WorkspaceRemote] Albie asked for '{want}': {(reason ?? "open: '" + (workspace.ActiveMode ?? "") + "'")}.");
    }
}
