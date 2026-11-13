using Oculus.Interaction;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The one round button lying on the table, at the front edge of the desk frame where the dock's centre was. At a desk it
    /// opens the Albie menu, which then rises from the button. It is a UI Set backplate (so the mouse, a ray and a poke all
    /// press it) carrying an icon-only UI Set button, lying flat the way the dock does. Shown while the user is at a desk,
    /// the pet's greeting isn't holding the desk, and the Settings switch is on.
    /// </summary>
    public sealed class OSTableButton : MonoBehaviour
    {
        /// <summary>Plate and button sizes in OS units: 84 units is 55 mm, the button on it 62 units (40 mm).</summary>
        const float PlateSize = 84f, ButtonSize = 62f, Lift = 0.012f;

        OSShell shell;
        GameObject root;
        Button button;
        DeskPresence presence;
        bool shown, built, reported;

        /// <summary>The pressable button, or null before it is first shown (for checks).</summary>
        public Button Button => button;
        public bool Shown => shown;
        /// <summary>The button's size in metres on the table (the plate).</summary>
        public float DiameterMeters => PlateSize * (OSKit.Current != null ? OSKit.Current.unitMeters : 0.00065f);
        public float ButtonMeters => ButtonSize * (OSKit.Current != null ? OSKit.Current.unitMeters : 0.00065f);

        void Awake() => shell = GetComponent<OSShell>();

        void OnEnable()
        {
            if (shell == null) shell = GetComponent<OSShell>();
            Hook(true);
        }

        void OnDisable()
        {
            Hook(false);
            if (root != null) root.SetActive(false);
            shown = false;
        }

        void Hook(bool on)
        {
            presence = shell != null && shell.workspace != null ? shell.workspace.presence : null;
            if (presence == null) return;
            presence.frameChanged.RemoveListener(Place);
            if (on) presence.frameChanged.AddListener(Place);
        }

        void Update()
        {
            if (shell == null) return;
            bool want = shell.AtDesk && !shell.Held && OSSettings.Current.tableButton && presence != null && presence.Frame != null;
            if (want == shown) return;
            if (want && !Build())
            {
                if (!reported) Debug.Log("[OS] The table button couldn't be built (is Resources/OSKit.asset there?).");
                reported = true;
                return;
            }
            shown = want;
            if (root != null) root.SetActive(want);
            if (want) Place();
        }

        // On the table at the front edge of the desk frame, where the dock's centre was, lying flat and readable from the seat.
        void Place()
        {
            if (root == null || presence == null || presence.Frame == null) return;
            root.transform.SetParent(presence.Frame, false);
            root.transform.localPosition = new Vector3(0f, Lift, Mathf.Min(0.09f, presence.FrameDepth * 0.5f));
            root.transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            root.transform.localScale = Vector3.one;
        }

        bool Build()
        {
            if (built) return root != null;
            OSKit kit = OSKit.Current;
            if (kit == null || kit.backplate == null || kit.secondaryButton == null) return false;
            built = true;

            root = Instantiate(kit.backplate);
            root.name = "OSTableButton";
            var canvasRoot = root.transform.Find("CanvasRoot") as RectTransform;
            RectTransform plate = canvasRoot != null ? canvasRoot.Find("UIBackplate") as RectTransform : null;
            if (canvasRoot == null || plate == null) return false;
            foreach (var layout in canvasRoot.GetComponents<LayoutGroup>()) layout.enabled = false;
            foreach (var layout in plate.GetComponents<LayoutGroup>()) layout.enabled = false;
            canvasRoot.sizeDelta = new Vector2(PlateSize, PlateSize);
            canvasRoot.localScale = Vector3.one * kit.unitMeters;
            plate.anchorMin = plate.anchorMax = plate.pivot = new Vector2(0.5f, 0.5f);
            plate.anchoredPosition = Vector2.zero;
            plate.sizeDelta = new Vector2(PlateSize, PlateSize);
            var round = plate.GetComponent<Oculus.Interaction.Samples.RoundedBoxUIProperties>();
            if (round != null) round.borderRadius = new Vector4(PlateSize, PlateSize, PlateSize, PlateSize);
            OSBody.EnsureCanvas(canvasRoot.GetComponent<Canvas>());
            var wrapper = root.GetComponent<PointableCanvasUnityEventWrapper>();
            if (wrapper != null) wrapper.enabled = false;  // the button plays its own press sound
            var manager = root.GetComponent<UIThemeManager>();
            if (manager != null && kit.theme != null) OSBody.SetThemes(manager, kit.theme);

            button = OSUi.Button(plate, null, Pressed, OSButtonStyle.Primary, "apps");
            if (button == null) return false;
            var buttonRect = (RectTransform)button.transform;
            buttonRect.anchorMin = buttonRect.anchorMax = buttonRect.pivot = new Vector2(0.5f, 0.5f);
            buttonRect.anchoredPosition = Vector2.zero;
            OSUi.Size(buttonRect, ButtonSize, ButtonSize);
            button.GetComponent<LayoutElement>().ignoreLayout = true;
            var background = button.transform.Find("Content/Background")?.GetComponent<Oculus.Interaction.Samples.RoundedBoxUIProperties>();
            if (background != null) background.borderRadius = new Vector4(ButtonSize, ButtonSize, ButtonSize, ButtonSize);

            root.SetActive(false);
            if (manager != null) manager.ApplyCurrentTheme();
            return true;
        }

        void Pressed()
        {
            if (shell == null) return;
            shell.ToggleMenu(OSSummon.TableButton, root != null ? root.transform.position : (Vector3?)null);
        }
    }
}
