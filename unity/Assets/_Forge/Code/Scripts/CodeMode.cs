using UnityEngine;

/// <summary>
/// Puts the headset's own code view (<see cref="CodePanel"/>, Resources/CodePanel.prefab) in the CODE mode's main slot,
/// whatever prefab the scene had there (Main.unity still names the old streamed-screen panel until it is rebuilt).
/// Installs itself; no scene change needed.
/// </summary>
public class CodeMode : MonoBehaviour
{
    const string ModeId = "code", ItemId = "screen-main", Title = "Code";

    WorkspaceItem item;
    GameObject local;

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
        if (item.prefab == local) return;
        item.prefab = local;
        item.title = Title;
    }

    bool Bind()
    {
        var ws = Workspace.Current;
        if (ws == null) return false;
        var mode = ws.modes.Find(m => m.id == ModeId);
        item = mode?.items.Find(i => i.id == ItemId);
        return item != null;
    }
}
