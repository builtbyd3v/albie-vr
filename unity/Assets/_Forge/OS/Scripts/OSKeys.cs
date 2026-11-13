using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Forge.OS
{
    /// <summary>
    /// The menu's keys: M on the desktop (Shift+M is CAD's measure key, so a plain M only) and the menu button on the
    /// left controller. Both toggle the menu. Controllers are optional, so nothing depends on this.
    /// </summary>
    public class OSKeys : MonoBehaviour
    {
        /// <summary>True while an OSKeys is enabled, i.e. while a plain M is the menu. CAD's measure key reads this: M alone
        /// when the OS isn't running (standalone CAD sandbox), Shift+M while it is.</summary>
        public static bool Active { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Active = false;

        OSShell shell;

        void Awake() => shell = GetComponent<OSShell>();
        void OnEnable() => Active = true;
        void OnDisable() => Active = false;

        void Update()
        {
            if (shell == null) return;

            var keys = Keyboard.current;
            if (keys != null && keys.mKey.wasPressedThisFrame && !keys.shiftKey.isPressed && !TypingInAField())
                shell.ToggleMenu(OSSummon.Key);

            // Left controller menu button; the hands (and the desktop rig) read nothing here.
            if (OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
                shell.ToggleMenu(OSSummon.Controller);
        }

        // An M typed into a text field is a letter, not the menu.
        static bool TypingInAField()
        {
            var system = EventSystem.current;
            if (system == null) return false;
            GameObject selected = system.currentSelectedGameObject;
            return selected != null && selected.GetComponent<TMP_InputField>() != null;
        }
    }
}
