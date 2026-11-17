using UnityEngine;

/// <summary>
/// Installs the CodePanel prefab in the CODE mode's main slot.
/// GitHub file browsing and PR diff view are built into CodePanel itself — it switches automatically
/// when the hub is live. No panel swapping needed here.
/// </summary>
public class CodeMode : MonoBehaviour
{
    const string ModeId = "code", ItemId = "screen-main";

    WorkspaceItem item;
    GameObject prefab;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindAnyObjectByType<CodeMode>() == null) new GameObject("CodeMode").AddComponent<CodeMode>();
    }

    void Awake() => prefab = Resources.Load<GameObject>("CodePanel");

    void Update()
    {
        if (item == null) Bind();
    }

    void Bind()
    {
        var ws = Workspace.Current;
        if (ws == null) return;
        var mode = ws.modes.Find(m => m.id == ModeId);
        item = mode?.items.Find(i => i.id == ItemId);
        if (item == null) return;
        item.prefab = prefab;
        item.title = "Code";
    }
}
