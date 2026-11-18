using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// Albie's voice tools for the OS (Resources/AlbieTools/os.json): open and close the menu, switch its tab, open
    /// something from it, and bring the windows here or close everything (see <see cref="OSSpaces"/>, which also has the
    /// spaces_ tools). Each answers with an error line when the scene has
    /// no shell (a sandbox), the way the other tools answer problems.
    /// </summary>
    public static class OSTools
    {
        /// <summary>The tool names this file registers; os.json must define exactly these.</summary>
        public static readonly string[] Names = { "os_menu", "os_tab", "os_open", "os_windows" };

        /// <summary>Adds the four tools to Albie. The app does this at start; a check does it by hand in the editor.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register() => AlbieTools.Register("os", new Dictionary<string, AlbieTools.Handler>
        {
            ["os_menu"] = Menu,
            ["os_tab"] = Tab,
            ["os_open"] = Open,
            ["os_windows"] = Windows,
        });

        static OSShell Shell()
        {
            var shell = OSShell.Current;
            if (shell == null) throw new ArgumentException("The Albie menu isn't available in this scene.");
            return shell;
        }

        static JObject Menu(JObject args)
        {
            var shell = Shell();
            bool? open = (bool?)args["open"];
            if (open == null) shell.ToggleMenu(OSSummon.Voice);
            else if (open.Value) shell.OpenMenu(OSSummon.Voice);
            else shell.CloseMenu();
            return new JObject { ["ok"] = true, ["open"] = shell.MenuOpen };
        }

        static JObject Tab(JObject args)
        {
            var shell = Shell();
            string name = ((string)args["tab"] ?? "").Trim().ToLowerInvariant();
            OSTab tab;
            switch (name)
            {
                case "tools": tab = OSTab.Tools; break;
                case "projects": tab = OSTab.Projects; break;
                case "artifacts": tab = OSTab.Artifacts; break;
                default: return AlbieTools.Error("The tabs are tools, projects and artifacts.");
            }
            shell.SetTab(tab);
            shell.OpenMenu(OSSummon.Voice);
            return new JObject { ["ok"] = true, ["tab"] = name, ["open"] = shell.MenuOpen };
        }

        static JObject Open(JObject args)
        {
            var shell = Shell();
            string kind = (string)args["kind"], id = (string)args["id"];
            if (!shell.OpenItem(kind, id, out string error)) return AlbieTools.Error(error);
            return new JObject { ["ok"] = true, ["kind"] = kind, ["id"] = id };
        }

        static JObject Windows(JObject args)
        {
            var shell = Shell();
            switch (((string)args["action"] ?? "").Trim().ToLowerInvariant())
            {
                case "recenter":
                    shell.Windows.BringHere();
                    break;
                case "close_all":
                    // Everything, not only the windows: the open artifact and its course, the truck or drone, the transmitter
                    // and loose panels too (OSSpaces). It asks on the Spaces list first when a run going on would be lost.
                    return OSSpaces.CloseAllByVoice();
                default:
                    return AlbieTools.Error("The actions are recenter and close_all.");
            }
            return new JObject { ["ok"] = true, ["windows"] = shell.Windows.Count };
        }
    }
}
