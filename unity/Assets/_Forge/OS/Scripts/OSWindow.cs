using System;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// One window the OS keeps track of: a free panel it opened (<see cref="OSWindows.Open"/>), or something that already
    /// existed and was adopted (<see cref="OSWindows.Adopt"/>). Closing it raises <see cref="Closed"/> once, however it
    /// goes away.
    /// </summary>
    public sealed class OSWindow : MonoBehaviour
    {
        OSWindows owner;
        Action closeAction;
        bool owned, finished;

        public string Id { get; private set; }
        public string Title { get; private set; }
        /// <summary>The panel, or null for a volume window or a group.</summary>
        public ForgePanel Panel { get; private set; }
        /// <summary>The volume, or null for a panel window or a group.</summary>
        public ForgeVolume Volume { get; private set; }
        /// <summary>A Workspace mode tracked as one entry: nothing of its own to move.</summary>
        public bool IsGroup { get; private set; }
        /// <summary>The thing that gets placed when the windows are brought here (null for a group).</summary>
        public Transform Root => IsGroup ? null : (Panel != null ? Panel.transform : transform);

        public event Action Closed;

        internal void Init(OSWindows manager, string id, string title, ForgePanel panel, ForgeVolume volume, bool group, bool ownsObject, Action close)
        {
            owner = manager;
            Id = id;
            Title = title;
            Panel = panel;
            Volume = volume;
            IsGroup = group;
            owned = ownsObject;
            closeAction = close;
        }

        /// <summary>Closes the window: an opened panel is destroyed, an adopted one runs its close action.</summary>
        public void Close()
        {
            if (finished) return;
            if (owned)
            {
                Finish();  // gone from the lists now; Destroy only takes effect at the end of the frame
                gameObject.SetActive(false);
                Destroy(gameObject);
                return;
            }
            try
            {
                closeAction?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            Finish();
            // The adopted thing may still be alive (its owner closes it its own way); just stop tracking it.
            Destroy(IsGroup ? gameObject : (UnityEngine.Object)this);
        }

        void OnDestroy() => Finish();

        void Finish()
        {
            if (finished) return;
            finished = true;
            owner?.Forget(this);
            Closed?.Invoke();
        }
    }
}
