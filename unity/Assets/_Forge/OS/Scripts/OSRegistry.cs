using System;
using System.Collections.Generic;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// What the three menu tabs list. Fixed entries (the Tools) are registered one by one; lists that change (projects on
    /// disk, artifacts in the library) are registered as sources that fill a list each time the menu asks. The menu view
    /// calls <see cref="Fill"/>, and re-reads when <see cref="Changed"/> fires. Registration happens at BeforeSceneLoad.
    /// </summary>
    public static class OSRegistry
    {
        struct Source
        {
            public OSTab tab;
            public Action<List<OSEntry>> fill;
        }

        static readonly List<OSEntry> tools = new List<OSEntry>();
        static readonly List<Source> sources = new List<Source>();

        /// <summary>The lists may have changed: the menu re-reads.</summary>
        public static event Action Changed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            tools.Clear();
            sources.Clear();
            Changed = null;
        }

        public static void NotifyChanged() => Changed?.Invoke();

        /// <summary>Adds a fixed entry to the Tools tab; an entry with the same id is replaced.</summary>
        public static void RegisterTool(OSEntry tool)
        {
            if (tool == null || string.IsNullOrEmpty(tool.id)) return;
            tool.tab = OSTab.Tools;
            int at = tools.FindIndex(t => t.id == tool.id);
            if (at >= 0) tools[at] = tool; else tools.Add(tool);
            NotifyChanged();
        }

        /// <summary>Takes a fixed entry out again (a module that goes away, or a check cleaning up).</summary>
        public static void UnregisterTool(string id)
        {
            if (tools.RemoveAll(t => t.id == id) > 0) NotifyChanged();
        }

        /// <summary>
        /// A dynamic list: <paramref name="fill"/> adds its entries to the list it is given each time the tab is read.
        /// Fixed tools come first, then sources in the order they were registered.
        /// </summary>
        public static void RegisterSource(OSTab tab, Action<List<OSEntry>> fill)
        {
            if (fill == null) return;
            sources.Add(new Source { tab = tab, fill = fill });
            NotifyChanged();
        }

        public static void UnregisterSource(Action<List<OSEntry>> fill)
        {
            if (sources.RemoveAll(s => s.fill == fill) > 0) NotifyChanged();
        }

        /// <summary>Clears <paramref name="into"/>, then adds the tab's entries: the fixed tools (Tools tab), then each source.</summary>
        public static void Fill(OSTab tab, List<OSEntry> into)
        {
            if (into == null) return;
            into.Clear();
            if (tab == OSTab.Tools) into.AddRange(tools);
            for (int i = 0; i < sources.Count; i++)
            {
                if (sources[i].tab != tab) continue;
                int before = into.Count;
                try { sources[i].fill(into); }
                catch (Exception e)
                {
                    Debug.LogException(e);  // one broken list shouldn't empty the menu
                    if (into.Count > before) into.RemoveRange(before, into.Count - before);
                    continue;
                }
                for (int k = into.Count - 1; k >= before; k--)
                {
                    if (into[k] == null) into.RemoveAt(k);
                    else into[k].tab = tab;
                }
            }
        }

        /// <summary>The entry with this id on this tab, or null.</summary>
        public static OSEntry Find(OSTab tab, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var all = new List<OSEntry>();
            Fill(tab, all);
            for (int i = 0; i < all.Count; i++)
                if (all[i].id == id) return all[i];
            return null;
        }

        /// <summary>The sentence a tab shows when it has nothing to list.</summary>
        public static string EmptyText(OSTab tab)
        {
            switch (tab)
            {
                case OSTab.Tools: return "No tools here yet. Ask Albie what he can open.";
                case OSTab.Projects: return "No projects yet. Tap New project to start one.";
                default: return "No artifacts yet. Tap New project or ask Albie.";
            }
        }
    }
}
