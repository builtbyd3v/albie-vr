using System;
using UnityEngine;

/// <summary>Explicit laptop launch argument; the APK and normal Editor startup retain their configured endpoints.</summary>
public static class DesktopLaunch
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Configure()
    {
        if (Application.platform != RuntimePlatform.OSXPlayer) return;
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-albie-hub");
        if (index < 0 || index + 1 >= args.Length) return;
        if (Uri.TryCreate(args[index + 1], UriKind.Absolute, out var uri)
            && (uri.Scheme == "http" || uri.Scheme == "https") && string.IsNullOrEmpty(uri.UserInfo))
        {
            PlayerPrefs.SetString(ForgeNet.HubUrlPref, uri.AbsoluteUri.TrimEnd('/'));
            PlayerPrefs.Save();
        }
    }
}
