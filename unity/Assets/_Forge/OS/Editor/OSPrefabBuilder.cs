using System.Collections.Generic;
using System.IO;
using Oculus.Interaction;
using UnityEditor;
using UnityEngine;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Build OS Prefabs: makes (or updates) everything the OS needs as an asset: the Albie theme
    /// (Themes/AlbieDark.asset, derived from the UI Set's dark theme), Resources/OSKit.asset (references to the UI Set's
    /// prefabs, icons and sounds), Prefabs/OSMenu.prefab (the menu on a UI Set backplate) and Resources/OSShell.prefab (the
    /// shell, windows, keys, sounds, menu and table button). Later parts add theirs here. Safe to run again: the theme is
    /// copied once and then only re-tuned, the kit and menu are rewritten in place, and the shell prefab only gains what's
    /// missing.
    /// </summary>
    public static class OSPrefabBuilder
    {
        public const string Root = "Assets/_Forge/OS";
        public const string ShellPath = Root + "/Resources/OSShell.prefab";
        public const string KitPath = Root + "/Resources/OSKit.asset";
        public const string MenuPath = Root + "/Prefabs/OSMenu.prefab";
        public const string ThemePath = Root + "/Themes/AlbieDark.asset";

        const string UiSet = "Packages/com.meta.xr.sdk.interaction/Runtime/Sample/Objects/UISet/";
        const string Sample = "Packages/com.meta.xr.sdk.interaction/Runtime/Sample/";
        const string IconAtlas = Sample + "Textures/OCUIIconSet/OCUI_24_Filled_2x.png";
        const string DarkThemePath = UiSet + "Themes/UIThemeQuest_Dark.asset";

        /// <summary>Friendly icon name to the UI Set's 24 px Filled atlas sprite (icon_NAME_24_Filled). The same table is in OS/README.md.</summary>
        public static readonly (string name, string glyph)[] IconMap =
        {
            ("cube", "vr-object"), ("code", "desktop"), ("train", "experiments"), ("notebook", "compose"),
            ("drive", "controller-full"), ("messages", "chat-text"), ("gestures", "hand-tracking"), ("truck", "destination"),
            ("quadcopter", "airplane-mode"), ("rocket", "to-top"), ("classifier", "image"), ("artifact", "category-all"),
            ("slalom", "move-reposition-alt"), ("hoops", "open-circle"), ("scoreboard", "leaderboard"), ("biology", "heart-on"),
            ("physics", "speed"), ("repo", "folder"), ("plus", "add"), ("close", "close"),
            ("settings", "settings"), ("agents", "friends"), ("build", "add-circle"), ("bring", "reorient"),
            ("home", "home"), ("send", "send"), ("check", "check"), ("warning", "warning"),
            ("chevron_left", "chevron-left"), ("chevron_right", "chevron-right"), ("table", "table"), ("apps", "apps"),
            ("tools", "apps"), ("projects", "gallery-folder"), ("artifacts", "category-all"),
        };

        [MenuItem("Forge/OS/Build OS Prefabs")]
        public static void Build()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "_Forge/OS/Resources"));
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "_Forge/OS/Prefabs"));
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "_Forge/OS/Themes"));
            AssetDatabase.Refresh();

            UITheme theme = BuildTheme();
            BuildKit(theme);
            BuildMenu(theme);
            BuildShell();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[OS] Built OS prefabs: " + ThemePath + ", " + KitPath + ", " + MenuPath + ", " + ShellPath);
        }

        // ---------------------------------------------------------------- theme

        // Copied from the UI Set's dark theme once (the copy keeps the package's animators, materials and font), then tuned in
        // place. Restrained: the dark look is Meta's; this only cools the plate a touch and keeps secondary text readable.
        static UITheme BuildTheme()
        {
            var theme = AssetDatabase.LoadAssetAtPath<UITheme>(ThemePath);
            if (theme == null)
            {
                if (!AssetDatabase.CopyAsset(DarkThemePath, ThemePath))
                {
                    Debug.LogError("[OS] Couldn't copy the UI Set dark theme from " + DarkThemePath);
                    return null;
                }
                theme = AssetDatabase.LoadAssetAtPath<UITheme>(ThemePath);
            }
            theme.backplateColor = new Color(0.92f, 0.95f, 1f, 1f);
            theme.textPrimaryColor = Color.white;  // the package's 90% white reads thin on a Quest 3S
            theme.textSecondaryColor = new Color(1f, 1f, 1f, 0.72f);
            EditorUtility.SetDirty(theme);
            return theme;
        }

        // ---------------------------------------------------------------- kit

        static void BuildKit(UITheme theme)
        {
            var kit = AssetDatabase.LoadAssetAtPath<OSKit>(KitPath);
            if (kit == null)
            {
                kit = ScriptableObject.CreateInstance<OSKit>();
                AssetDatabase.CreateAsset(kit, KitPath);
            }
            kit.backplate = Prefab(UiSet + "Prefabs/Backplate/EmptyUIBackplateWithCanvas.prefab");
            kit.primaryButton = Prefab(UiSet + "Prefabs/Button/UnityUIButtonBased/PrimaryButton_IconAndLabel_UnityUIButton.prefab");
            kit.secondaryButton = Prefab(UiSet + "Prefabs/Button/UnityUIButtonBased/SecondaryButton_IconAndLabel_UnityUIButton.prefab");
            kit.borderlessButton = Prefab(UiSet + "Prefabs/Button/UnityUIButtonBased/BorderlessButton_IconAndLabel_UnityUIButton.prefab");
            kit.destructiveButton = Prefab(UiSet + "Prefabs/Button/UnityUIButtonBased/DestructiveButton_IconAndLabel_UnityUIButton.prefab");
            kit.textTile = Prefab(UiSet + "Prefabs/Button/UnityUIButtonBased/TextTileButton_IconAndLabel_Regular_UnityUIButton.prefab");
            kit.tabToggle = Prefab(UiSet + "Prefabs/Button/UnityUIToggleBased/ButtonShelf_IconAndLabel_Toggle.prefab");
            kit.switchToggle = Prefab(UiSet + "Prefabs/Button/UnityUIToggleBased/ToggleButton_Switch.prefab");
            kit.slider = Prefab(UiSet + "Prefabs/Slider/LargeSlider.prefab");
            kit.theme = theme;
            kit.font = theme != null ? theme.textFontMedium : null;
            kit.unitMeters = 0.00065f;

            // Sounds from the Interaction SDK samples: press and release from the poke button, open and close from its panel grab.
            kit.pressClip = Clip("Interaction_BasicPoke_ButtonPress.wav");
            kit.releaseClip = Clip("Interaction_BasicPoke_ButtonRelease.wav");
            kit.openClip = Clip("Interaction_Object_Reappear_01.wav");
            kit.closeClip = Clip("Interaction_UIRelease_01.wav");
            kit.errorClip = Clip("Interaction_Locomotion_Declined.wav");

            var sprites = new Dictionary<string, Sprite>();
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(IconAtlas))
                if (asset is Sprite sprite) sprites[sprite.name] = sprite;
            var icons = new List<OSKit.IconEntry>();
            foreach (var (name, glyph) in IconMap)
            {
                sprites.TryGetValue("icon_" + glyph + "_24_Filled", out Sprite found);
                if (found == null) Debug.LogError("[OS] The UI Set has no Filled icon '" + glyph + "' (for '" + name + "').");
                icons.Add(new OSKit.IconEntry { name = name, sprite = found });
            }
            kit.icons = icons.ToArray();
            EditorUtility.SetDirty(kit);
        }

        static GameObject Prefab(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) Debug.LogError("[OS] UI Set prefab missing: " + path);
            return prefab;
        }

        static AudioClip Clip(string file)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(Sample + "Audio/Content/" + file);
            if (clip == null) Debug.LogError("[OS] Interaction SDK clip missing: " + file);
            return clip;
        }

        // ---------------------------------------------------------------- menu

        // The menu prefab is the UI Set backplate (nested, so its poke, ray and mouse parts stay Meta's) plus the OSMenu view.
        // The tiles and buttons are built from the kit when the menu first opens.
        static void BuildMenu(UITheme theme)
        {
            var backplate = Prefab(UiSet + "Prefabs/Backplate/EmptyUIBackplateWithCanvas.prefab");
            if (backplate == null) return;

            var root = new GameObject("OSMenu");
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(backplate, root.transform);
                instance.name = "Backplate";
                var canvasRoot = (RectTransform)instance.transform.Find("CanvasRoot");
                var plate = (RectTransform)canvasRoot.Find("UIBackplate");
                var group = Ensure<CanvasGroup>(canvasRoot.gameObject);
                var manager = instance.GetComponent<UIThemeManager>();

                // Point the backplate's theme manager at the Albie theme.
                var so = new SerializedObject(manager);
                var themes = so.FindProperty("_themes");
                themes.arraySize = 1;
                themes.GetArrayElementAtIndex(0).objectReferenceValue = theme;
                so.FindProperty("_currentThemeIndex").intValue = 0;
                so.ApplyModifiedPropertiesWithoutUndo();

                var menu = root.AddComponent<OSMenu>();
                var menuSo = new SerializedObject(menu);
                menuSo.FindProperty("visual").objectReferenceValue = instance;
                menuSo.FindProperty("canvasRoot").objectReferenceValue = canvasRoot;
                menuSo.FindProperty("plate").objectReferenceValue = plate;
                menuSo.FindProperty("group").objectReferenceValue = group;
                menuSo.FindProperty("themeManager").objectReferenceValue = manager;
                menuSo.ApplyModifiedPropertiesWithoutUndo();

                instance.SetActive(false);  // shown by OSMenu when the shell opens the menu
                PrefabUtility.SaveAsPrefabAsset(root, MenuPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------------------------------------------------------------- shell

        static void BuildShell()
        {
            GameObject root;
            bool exists = File.Exists(Path.Combine(Application.dataPath, "../" + ShellPath));
            if (exists) root = PrefabUtility.LoadPrefabContents(ShellPath);
            else root = new GameObject("OSShell");
            try
            {
                Ensure<OSShell>(root);
                Ensure<OSWindows>(root);
                Ensure<OSKeys>(root);
                Ensure<OSTableButton>(root);
                Ensure<OSCornerButton>(root);
                Ensure<OSWatch>(root);
                var audio = Ensure<AudioSource>(root);  // OSSounds plays the kit's clips through this one source
                audio.playOnAwake = false;
                audio.spatialBlend = 0f;

                if (root.GetComponentInChildren<OSMenu>(true) == null)
                {
                    var menuPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MenuPath);
                    if (menuPrefab != null)
                    {
                        var menu = (GameObject)PrefabUtility.InstantiatePrefab(menuPrefab, root.transform);
                        menu.name = "OSMenu";
                    }
                }
                PrefabUtility.SaveAsPrefabAsset(root, ShellPath);
            }
            finally
            {
                if (exists) PrefabUtility.UnloadPrefabContents(root);
                else Object.DestroyImmediate(root);
            }
        }

        /// <summary>Adds the component if the object doesn't have it yet; returns it.</summary>
        public static T Ensure<T>(GameObject go) where T : Component
        {
            var component = go.GetComponent<T>();
            return component != null ? component : go.AddComponent<T>();
        }
    }
}
