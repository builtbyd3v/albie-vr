using UnityEngine;

/// <summary>
/// Installs the code view prefab in the CODE mode's main slot. When the hub is live it uses
/// <see cref="GitHubPanel"/> (GitHub file browser + PR diff viewer); otherwise the local
/// <see cref="CodePanel"/> (bundled sample project).
/// </summary>
public class CodeMode : MonoBehaviour
{
    const string ModeId = "code", ItemId = "screen-main";

    WorkspaceItem item;
    GameObject localPanel, githubPanel;
    ForgeNetStatus lastStatus = ForgeNetStatus.Connecting;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindAnyObjectByType<CodeMode>() == null) new GameObject("CodeMode").AddComponent<CodeMode>();
    }

    void Awake()
    {
        localPanel = Resources.Load<GameObject>("CodePanel");
        githubPanel = Resources.Load<GameObject>("GitHubPanel");
    }

    void OnEnable() => ForgeNet.StatusChanged += OnStatus;
    void OnDisable() => ForgeNet.StatusChanged -= OnStatus;
    void OnStatus(ForgeNetStatus s) { lastStatus = s; Apply(); }

    void Update()
    {
        if (item == null && !Bind()) return;
    }

    bool Bind()
    {
        var ws = Workspace.Current;
        if (ws == null) return false;
        var mode = ws.modes.Find(m => m.id == ModeId);
        item = mode?.items.Find(i => i.id == ItemId);
        if (item != null) Apply();
        return item != null;
    }

    void Apply()
    {
        if (item == null) return;
        bool useGitHub = lastStatus == ForgeNetStatus.Live && githubPanel != null;
        var prefab = useGitHub ? githubPanel : localPanel;
        string title = useGitHub ? "source control" : "Code";
        if (item.prefab == prefab) return;
        item.prefab = prefab;
        item.title = title;
    }
}
