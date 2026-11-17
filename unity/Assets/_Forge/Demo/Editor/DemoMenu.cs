using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Forge.Demo.Editor
{
    /// <summary>Forge > Demo > Toggle Demo Mode (Resources/demo_config.json "enabled") and the shared-editor lock the stations use.</summary>
    public static class DemoMenu
    {
        const string ConfigPath = "Assets/_Forge/Demo/Resources/demo_config.json";
        const string MenuPath = "Forge/Demo/Toggle Demo Mode";

        [MenuItem(MenuPath)]
        public static void ToggleDemoMode()
        {
            bool on = !IsEnabled();
            Set(on);
            Debug.Log("[Demo] demo mode " + (on ? "ON: the Director installs itself at startup (rebuild the APK for the headset)." : "OFF."));
        }

        [MenuItem(MenuPath, true)]
        static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, IsEnabled());
            return true;
        }

        public static bool IsEnabled()
        {
            try
            {
                var json = JObject.Parse(File.ReadAllText(ConfigPath));
                return json["enabled"] != null && (bool)json["enabled"];
            }
            catch (Exception) { return false; }
        }

        public static void Set(bool enabled)
        {
            JObject json;
            try { json = JObject.Parse(File.ReadAllText(ConfigPath)); }
            catch (Exception) { json = new JObject(); }
            json["enabled"] = enabled;
            File.WriteAllText(ConfigPath, json.ToString(Newtonsoft.Json.Formatting.Indented) + "\n");
            AssetDatabase.ImportAsset(ConfigPath);
        }
    }

    /// <summary>unity/Temp/demo-editor.lock: one station at a time in Play mode (TONIGHT_PLAN section 4). A lock under 10 minutes old is somebody's.</summary>
    public static class DemoLock
    {
        static string LockPath => Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/demo-editor.lock"));

        /// <summary>False when another station holds the lock; true (and the lock is now ours) otherwise.</summary>
        public static bool TryAcquire(string station)
        {
            try
            {
                if (File.Exists(LockPath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(LockPath)).TotalMinutes < 10)
                {
                    string holder = File.ReadAllText(LockPath).Trim();
                    if (holder == station) return true;   // already ours (taken by hand before the menu was used)
                    Debug.LogError("[Demo] the editor is held by '" + holder + "' (Temp/demo-editor.lock). Wait and retry.");
                    return false;
                }
                File.WriteAllText(LockPath, station);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Demo] could not take the editor lock: " + e.Message);
                return true;
            }
        }

        public static void Release(string station)
        {
            try
            {
                if (File.Exists(LockPath) && File.ReadAllText(LockPath).Trim() == station) File.Delete(LockPath);
            }
            catch (Exception) { }
        }
    }
}
