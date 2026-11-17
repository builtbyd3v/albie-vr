using System;
using System.IO;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// The OS settings, kept in <c>os_settings.json</c> in the app's data folder. Loaded on first use. A missing or damaged
    /// file gives the defaults (a damaged one logs one warning, and the next <see cref="Save"/> replaces it); values are
    /// clamped to their ranges when read and when saved.
    /// </summary>
    [Serializable]
    public sealed class OSSettings
    {
        public bool tableButton = true, watchTurn = true, cornerButton = true;
        public bool cornerOnLeft = false;
        /// <summary>Metres from the eyes (0.35 to 0.8).</summary>
        public float menuDistance = 0.5f;
        /// <summary>Degrees below eye level (0 to 35).</summary>
        public float menuDropDegrees = 15f;
        public bool gestureDisplay = false;
        /// <summary>Workspace.SetLayout: flat instead of curved.</summary>
        public bool flatLayout = false;

        public const float MinDistance = 0.35f, MaxDistance = 0.8f, MinDrop = 0f, MaxDrop = 35f;
        const string FileName = "os_settings.json";

        static OSSettings current;
        static string pathOverride;

        /// <summary>Raised after <see cref="Save"/>, <see cref="Reload"/> and <see cref="ResetToDefaults"/>.</summary>
        public static event Action Changed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            current = null;
            pathOverride = null;
            Changed = null;
        }

        /// <summary>The settings, read from disk the first time they're asked for.</summary>
        public static OSSettings Current => current ?? (current = Load());

        /// <summary>persistentDataPath/os_settings.json.</summary>
        public static string Path => pathOverride ?? System.IO.Path.Combine(Application.persistentDataPath, FileName);

        /// <summary>For checks: read and write this file instead; null goes back to the real one. Forgets what's loaded.</summary>
        public static void SetPathForTests(string path)
        {
            pathOverride = path;
            current = null;
        }

        /// <summary>Writes the settings (clamped) and raises <see cref="Changed"/>.</summary>
        public void Save()
        {
            Clamp();
            try
            {
                string path = Path;
                string folder = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(this, true));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OS] Couldn't save settings: " + e.Message);
            }
            Changed?.Invoke();
        }

        /// <summary>Reads the file again, dropping what's in memory.</summary>
        public static void Reload()
        {
            current = Load();
            Changed?.Invoke();
        }

        /// <summary>Back to the defaults, saved.</summary>
        public static void ResetToDefaults()
        {
            current = new OSSettings();
            current.Save();
        }

        static OSSettings Load()
        {
            var settings = new OSSettings();
            string path = Path;
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(json)) JsonUtility.FromJsonOverwrite(json, settings);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OS] Settings file is damaged, so the defaults are used: " + e.Message);
                settings = new OSSettings();
            }
            settings.Clamp();
            return settings;
        }

        void Clamp()
        {
            menuDistance = float.IsNaN(menuDistance) ? 0.5f : Mathf.Clamp(menuDistance, MinDistance, MaxDistance);
            menuDropDegrees = float.IsNaN(menuDropDegrees) ? 15f : Mathf.Clamp(menuDropDegrees, MinDrop, MaxDrop);
        }
    }
}
