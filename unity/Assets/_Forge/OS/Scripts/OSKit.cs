using System;
using System.Collections.Generic;
using Oculus.Interaction;
using TMPro;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// Everything the OS borrows from the Meta UI Set, in one asset (Resources/OSKit.asset, built by
    /// <c>Forge > OS > Build OS Prefabs</c>): direct references to the package's prefabs, the Albie dark theme derived from
    /// the package's dark theme, the icon sprites by friendly name, the font and the sound clips. The package's files are
    /// referenced, never copied. <see cref="OSUi"/> and <see cref="OSMenu"/> build every control from this.
    /// </summary>
    public sealed class OSKit : ScriptableObject
    {
        public const string ResourceName = "OSKit";

        /// <summary>The friendly icon names every OS screen may use. The builder maps each to a sprite in the UI Set's 24 px Filled atlas.</summary>
        public static readonly string[] RequiredIcons =
        {
            "cube", "code", "train", "notebook", "drive", "messages", "gestures", "truck", "quadcopter", "rocket",
            "classifier", "artifact", "slalom", "hoops", "scoreboard", "biology", "physics", "repo", "plus", "close",
            "settings", "agents", "build", "bring", "home", "send", "check", "warning", "chevron_left", "chevron_right",
            "table", "apps", "tools", "projects", "artifacts",
        };

        [Serializable]
        public struct IconEntry
        {
            public string name;
            public Sprite sprite;
        }

        [Header("UI Set prefabs (referenced from the package)")]
        public GameObject backplate;
        public GameObject primaryButton, secondaryButton, borderlessButton, destructiveButton;
        public GameObject textTile;
        /// <summary>The shelf-style toggle the tab bar uses (icon over label, lit when on).</summary>
        public GameObject tabToggle;
        public GameObject switchToggle;
        public GameObject slider;

        [Header("Look")]
        public UITheme theme;
        public TMP_FontAsset font;
        public IconEntry[] icons = new IconEntry[0];

        [Header("Sounds (Interaction SDK sample audio)")]
        public AudioClip pressClip;
        public AudioClip releaseClip, openClip, closeClip, errorClip;

        [Header("Size")]
        /// <summary>Metres per OS unit. The UI Set ships at 0.0005; the OS draws it a little larger for the Quest 3S.</summary>
        public float unitMeters = 0.00065f;

        static OSKit current;
        Dictionary<string, Sprite> iconByName;

        /// <summary>The kit in Resources, or null if the builder hasn't made it (callers cope: no controls, no sound).</summary>
        public static OSKit Current
        {
            get
            {
                if (current == null)
                {
                    current = Resources.Load<OSKit>(ResourceName);
                    if (current != null) current.iconByName = null;  // the asset may have been rebuilt since the lookup was made
                }
                return current;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => current = null;

        /// <summary>The sprite for a friendly icon name ("cube", "close"), or null.</summary>
        public Sprite Icon(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (iconByName == null)
            {
                iconByName = new Dictionary<string, Sprite>(icons.Length, StringComparer.Ordinal);
                for (int i = 0; i < icons.Length; i++)
                    if (!string.IsNullOrEmpty(icons[i].name)) iconByName[icons[i].name] = icons[i].sprite;
            }
            iconByName.TryGetValue(name, out Sprite sprite);
            return sprite;
        }

        /// <summary>The prefab for a button style.</summary>
        public GameObject ButtonPrefab(OSButtonStyle style)
        {
            switch (style)
            {
                case OSButtonStyle.Primary: return primaryButton;
                case OSButtonStyle.Borderless: return borderlessButton;
                case OSButtonStyle.Destructive: return destructiveButton;
                default: return secondaryButton;
            }
        }

        void OnEnable() => iconByName = null;
    }
}
