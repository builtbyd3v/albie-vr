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
        OSShell shell;

        void Awake() => shell = GetComponent<OSShell>();

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
