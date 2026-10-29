using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie's screen_status on the headset (Resources/AlbieTools/screens.json): whether a computer is connected and its
/// screen streams to a panel, worded like the panel's own empty state (<see cref="ScreenSurface"/>).
/// </summary>
public static class ScreenTools
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register() => AlbieTools.Register("screens", new Dictionary<string, AlbieTools.Handler> { ["screen_status"] = _ => Status() });

    static JObject Status()
    {
        bool connected = ForgeNet.Status == ForgeNetStatus.Live;
        if (!connected)
            return new JObject
            {
                ["computer_connected"] = false, ["streaming"] = false,
                ["note"] = "No computer is connected, so there's no screen to show. Everything else works without one.",
                ["how"] = "Run Albie's hub and screen-bridge on a computer on the same Wi-Fi as this headset.",
            };
        bool streaming = ScreenSurface.Showing > 0;
        var output = new JObject { ["computer_connected"] = true, ["streaming"] = streaming };
        if (!streaming)
        {
            output["note"] = "The computer is connected but isn't sharing its screen (or the screen view isn't open).";
            output["how"] = "Start screen-bridge on the computer; the screen shows in the code view.";
        }
        return output;
    }
}
