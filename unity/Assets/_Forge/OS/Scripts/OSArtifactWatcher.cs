using Forge.Artifacts;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// Watches the artifact host so the OS notices an artifact opening or closing however it was started (a tile, the voice,
    /// a button inside an artifact). When the host's children or its open artifact change it asks <see cref="OSArtifacts"/> to
    /// adopt the new panels and volumes and to refresh the Artifacts tab. It also listens to <see cref="ArtifactHost.Shown"/>,
    /// which covers a rebuild that leaves the child count the same. Nothing here allocates per frame. <see cref="OSArtifacts"/>
    /// adds one to any scene with a Workspace; sandboxes have none and are left alone.
    /// </summary>
    public sealed class OSArtifactWatcher : MonoBehaviour
    {
        ArtifactHost hooked;
        int children = -1;
        string openId;

        void Update()
        {
            ArtifactHost host = ArtifactHost.Current;
            if (host != hooked)
            {
                if (hooked != null) hooked.Shown -= OSArtifacts.OnShown;
                hooked = host;
                children = -1;
                openId = null;
                if (hooked != null) hooked.Shown += OSArtifacts.OnShown;
            }
            if (host == null) return;

            int count = host.transform.childCount;
            string id = OSArtifacts.OpenId();
            if (count == children && string.Equals(id, openId)) return;
            children = count;
            openId = id;
            OSArtifacts.OnHostChanged(host);
        }

        void OnDestroy()
        {
            if (hooked != null) hooked.Shown -= OSArtifacts.OnShown;
        }
    }
}
