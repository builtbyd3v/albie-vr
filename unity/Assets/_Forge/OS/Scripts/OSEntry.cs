using UnityEngine;

namespace Forge.OS
{
    /// <summary>The three tabs of the Albie menu.</summary>
    public enum OSTab { Tools, Projects, Artifacts }

    /// <summary>Where a menu summon came from, so the menu can grow from the right place and the code can tell them apart.</summary>
    public enum OSSummon { Key, Voice, TableButton, Watch, Corner, Controller, Code }

    /// <summary>
    /// One tile in the menu: what to show and what a tap does. The tabs are built from these (see <see cref="OSRegistry"/>),
    /// so adding a tool, project or artifact to the menu is adding an entry.
    /// </summary>
    public sealed class OSEntry
    {
        /// <summary>Stable, snake_case: "cad", "proj_truck", "slalom".</summary>
        public string id;
        /// <summary>What the tile says: "CAD", "RC truck".</summary>
        public string title;
        /// <summary>Sprite name in the kit's icon set: "cube", "truck".</summary>
        public string icon;
        public OSTab tab;
        /// <summary>Optional heading the tile sits under ("New"); null for the main list.</summary>
        public string section;
        /// <summary>The "new / create" tile style.</summary>
        public bool outline;
        /// <summary>One line under the title ("open", "v2 · slalom passed"); null for none.</summary>
        public System.Func<string> status;
        /// <summary>Whether the thing is open right now; null means never shown as open.</summary>
        public System.Func<bool> isOpen;
        /// <summary>What a tap does.</summary>
        public System.Action open;
    }
}
