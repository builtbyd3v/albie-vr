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
        /// <summary>Metres from the eyes (0.42 to 0.6): where a hand pokes it without the elbow folding up by the face.</summary>
        public float menuDistance = DefaultDistance;
        /// <summary>Degrees below eye level (10 to 30): low enough to reach without lifting the arm, above the hands at rest.</summary>
        public float menuDropDegrees = DefaultDrop;
        public bool gestureDisplay = false;
        /// <summary>Workspace.SetLayout: flat instead of curved.</summary>
        public bool flatLayout = false;
        /// <summary>
        /// Which menu-position defaults the file was written with. Files from before 2 held 0.5 m and 15 degrees, which the
        /// headset showed was too high and, with things in the way, too near the face to poke; reading one moves the menu to
        /// the new defaults once (a place the user chose is kept when it is still in range).
        /// </summary>
        public int placementVersion = PlacementVersion;

        public const float MinDistance = 0.42f, MaxDistance = 0.6f, MinDrop = 10f, MaxDrop = 30f;
        /// <summary>The menu's default place: 0.47 m from the eyes, 20 degrees below eye level (see OSMenu.Pose).</summary>
        public const float DefaultDistance = 0.47f, DefaultDrop = 20f;
        public const int PlacementVersion = 2;
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
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        settings.placementVersion = 0;  // a file that doesn't say is from before version 2
                        JsonUtility.FromJsonOverwrite(json, settings);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OS] Settings file is damaged, so the defaults are used: " + e.Message);
                settings = new OSSettings();
            }
            settings.Migrate();
            settings.Clamp();
            return settings;
        }

        // A file from before placementVersion 2 (it has none, so it reads 0): its untouched old defaults become the new ones.
        void Migrate()
        {
            if (placementVersion >= PlacementVersion) return;
            if (Mathf.Approximately(menuDistance, 0.5f)) menuDistance = DefaultDistance;
            if (Mathf.Approximately(menuDropDegrees, 15f)) menuDropDegrees = DefaultDrop;
            placementVersion = PlacementVersion;
        }

        void Clamp()
        {
            menuDistance = float.IsNaN(menuDistance) ? DefaultDistance : Mathf.Clamp(menuDistance, MinDistance, MaxDistance);
            menuDropDegrees = float.IsNaN(menuDropDegrees) ? DefaultDrop : Mathf.Clamp(menuDropDegrees, MinDrop, MaxDrop);
        }
    }
}
