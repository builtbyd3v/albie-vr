using UnityEngine;

/// <summary>
/// Puts the headset's own code view (<see cref="CodePanel"/>, Resources/CodePanel.prefab) in the CODE mode's main slot
/// while the hub is off or connecting; with the hub live the slot keeps the laptop's streamed editor from the scene.
/// Decided when the mode opens (Workspace spawns from the item's prefab). Installs itself; no scene change needed.
/// </summary>
public class CodeMode : MonoBehaviour
{
    const string ModeId = "code", ItemId = "screen-main", Title = "Code";

    WorkspaceItem item;
    GameObject streamed, local;
    string streamedTitle;
    int applied = -1;  // 1 = local view, 0 = streamed

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindAnyObjectByType<CodeMode>() == null) new GameObject("CodeMode").AddComponent<CodeMode>();
    }

    void Awake() => local = Resources.Load<GameObject>("CodePanel");

    void Update()
    {
        if (local == null) return;
        if (item == null && !Bind()) return;
        int want = ForgeNet.Status == ForgeNetStatus.Live ? 0 : 1;
        if (want == applied) return;
        applied = want;
        item.prefab = want == 1 ? local : streamed;
        item.title = want == 1 ? Title : streamedTitle;
    }

    bool Bind()
    {
        var ws = Workspace.Current;
        if (ws == null) return false;
        var mode = ws.modes.Find(m => m.id == ModeId);
        item = mode?.items.Find(i => i.id == ItemId);
        if (item == null) return false;
        streamed = item.prefab;
        streamedTitle = item.title;
        return true;
    }
}
