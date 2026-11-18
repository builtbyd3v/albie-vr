using Oculus.Interaction;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The corner button: a small round kit button on a dark plate, low in one corner of the view, within arm's reach (0.45 m),
    /// at a desk and away from one (at a desk it used to leave the menu to the table button, which lies flat on the desk and
    /// proved hard to find and touch on the headset). It is never locked to the head. It keeps a world-space anchor; when the head has turned more than
    /// <see cref="recenterDegrees"/> away from the direction the anchor was set for, or has moved more than
    /// <see cref="recenterMeters"/>, it picks a new anchor and eases there over <see cref="easeSeconds"/> with a smooth
    /// step. It always faces the head. Pressing it opens the menu (summon value Corner); it hides while the menu is open,
    /// while the pet's greeting holds the desk, and when Settings turns it off.
    /// </summary>
    public sealed class OSCornerButton : MonoBehaviour
    {
        // Tuning on the headset.
        [Tooltip("Metres from the eyes.")] public float distance = 0.45f;
        [Tooltip("Degrees to the side of the gaze line at rest (left or right from Settings).")] public float sideDegrees = 28f;
        [Tooltip("Degrees below the gaze line at rest.")] public float dropDegrees = 22f;
        [Tooltip("The head turning this far from the direction the anchor was set for picks a new anchor.")] public float recenterDegrees = 25f;
        [Tooltip("The head moving this far from where the anchor was set picks a new anchor.")] public float recenterMeters = 0.3f;
        [Tooltip("Seconds to ease to a new anchor.")] public float easeSeconds = 0.4f;

        /// <summary>Plate and button sizes in OS units (unitMeters each): the button is about 38 mm.</summary>
        const float PlateSize = 84f, ButtonSize = 58f;

        /// <summary>The corner button in this scene, or null.</summary>
        public static OSCornerButton Current { get; private set; }

        OSShell shell;
        GameObject root;
        Button button;
        bool shown, built, buildFailed, reported;

        // The world anchor, the head pose it was set for, and the ease to it.
        Vector3 anchorPos, setHeadPos, setForward, easeFrom;
        float easeT = 1f;
        bool placed, lastLeft;
        int recenters;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        /// <summary>The pressable button, or null before it is first shown (for checks).</summary>
        public Button Button => button;
        public bool Shown => shown;
        /// <summary>Where the button is now (its world position), or the anchor if it was never shown.</summary>
        public Vector3 Position => root != null && shown ? root.transform.position : anchorPos;
        /// <summary>Where the current anchor is (where the button is heading, or sitting).</summary>
        public Vector3 AnchorPosition => anchorPos;
        /// <summary>True while it is easing to a new anchor.</summary>
        public bool Easing => shown && easeT < 1f;
        /// <summary>How many times it has picked a new anchor since it was last shown (the first placing is not counted).</summary>
        public int Recenters => recenters;
        public float ButtonMeters => ButtonSize * (OSKit.Current != null ? OSKit.Current.unitMeters : 0.00065f);
        public float PlateMeters => PlateSize * (OSKit.Current != null ? OSKit.Current.unitMeters : 0.00065f);

        void Awake() => shell = GetComponent<OSShell>();

        void OnEnable()
        {
            if (shell == null) shell = GetComponent<OSShell>();
            Current = this;
            buildFailed = false;
            reported = false;
        }

        void OnDisable()
        {
            if (Current == this) Current = null;
            if (root != null) root.SetActive(false);
            shown = false;
            placed = false;
        }

        // The button is a scene root of its own, so it has to be taken down with the shell.
        void OnDestroy()
        {
            if (root != null) Destroy(root);
        }

        void LateUpdate()
        {
            if (shell == null) return;
            Transform head = shell.Head;
            bool want = head != null && !shell.Held && !shell.MenuOpen && OSSettings.Current.cornerButton;
            if (want != shown)
            {
                if (want && !Build())
                {
                    if (!reported) Debug.Log("[OS] The corner button couldn't be built (is Resources/OSKit.asset there?).");
                    reported = true;
                    return;
                }
                shown = want;
                if (root != null) root.SetActive(want);
                if (want)
                {
                    recenters = 0;
                    PickAnchor(head, false);  // appears where it belongs, no fly-in
                }
                else placed = false;
            }
            if (!shown || root == null) return;

            bool left = OSSettings.Current.cornerOnLeft;
            if (left != lastLeft) PickAnchor(head, true);  // Settings moved it to the other corner
            else if (Vector3.Angle(head.forward, setForward) > recenterDegrees
                     || (head.position - setHeadPos).sqrMagnitude > recenterMeters * recenterMeters)
                PickAnchor(head, true);

            Vector3 pos = anchorPos;
            if (easeT < 1f)
            {
                easeT = Mathf.Min(1f, easeT + Time.unscaledDeltaTime / Mathf.Max(0.01f, easeSeconds));
                float s = easeT * easeT * (3f - 2f * easeT);  // smooth step
                pos = Vector3.Lerp(easeFrom, anchorPos, s);
            }
            Transform t = root.transform;
            t.position = pos;
            Vector3 away = pos - head.position;
            if (away.sqrMagnitude > 1e-6f) t.rotation = Quaternion.LookRotation(away, Vector3.up);  // canvas faces the head
        }

        // Sets the world anchor for the head's pose now: low in the chosen corner of the view, level with the horizon
        // (roll is ignored). With ease it travels there from where it is.
        void PickAnchor(Transform head, bool ease)
        {
            lastLeft = OSSettings.Current.cornerOnLeft;
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            Quaternion level = flat.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(head.forward, Vector3.up)
                : Quaternion.LookRotation(Vector3.ProjectOnPlane(head.up, Vector3.up).sqrMagnitude > 1e-4f ? -Vector3.ProjectOnPlane(head.up, Vector3.up) : Vector3.forward, Vector3.up);
            // Euler x positive looks down, y positive looks right.
            Quaternion offset = Quaternion.Euler(dropDegrees, lastLeft ? -sideDegrees : sideDegrees, 0f);
            Vector3 target = head.position + level * offset * Vector3.forward * distance;

            setHeadPos = head.position;
            setForward = head.forward;
            if (ease && placed && root != null)
            {
                easeFrom = root.transform.position;
                easeT = 0f;
                recenters++;
            }
            else
            {
                easeT = 1f;
            }
            anchorPos = target;
            placed = true;
        }

        // A failed build is remembered (until the component is enabled again) so the missing kit isn't looked up every frame.
        bool Build()
        {
            if (built) return root != null;
            if (buildFailed) return false;
            if (!TryBuild())
            {
                buildFailed = true;
                if (root != null) Destroy(root);
                root = null;
                button = null;
                return false;
            }
            built = true;
            return true;
        }

        bool TryBuild()
        {
            OSKit kit = OSKit.Current;
            if (kit == null || kit.backplate == null || kit.secondaryButton == null) return false;

            root = Instantiate(kit.backplate);  // a scene root: it keeps a world anchor and is never parented to the head
            root.name = "OSCornerButton";
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
            if (wrapper != null) wrapper.enabled = false;  // the button plays its own press sound (OSUi.Button adds it once)
            var manager = root.GetComponent<UIThemeManager>();
            if (manager != null && kit.theme != null) OSBody.SetThemes(manager, kit.theme);

            button = OSUi.Button(plate, null, Pressed, OSButtonStyle.Primary, "apps");
            if (button == null) return false;
            var buttonRect = (RectTransform)button.transform;
            buttonRect.anchorMin = buttonRect.anchorMax = buttonRect.pivot = new Vector2(0.5f, 0.5f);
            buttonRect.anchoredPosition = Vector2.zero;
            OSUi.Size(buttonRect, ButtonSize, ButtonSize);
            button.GetComponent<LayoutElement>().ignoreLayout = true;
            // Icon only: the empty label box would sit beside the icon in the row and push it off centre.
            var emptyText = button.transform.Find("Content/Background/Elements/Text");
            if (emptyText != null) emptyText.gameObject.SetActive(false);
            var background = button.transform.Find("Content/Background")?.GetComponent<Oculus.Interaction.Samples.RoundedBoxUIProperties>();
            if (background != null) background.borderRadius = new Vector4(ButtonSize, ButtonSize, ButtonSize, ButtonSize);

            root.SetActive(false);
            if (manager != null) manager.ApplyCurrentTheme();
            return true;
        }

        void Pressed()
        {
            if (shell == null) return;
            shell.OpenMenu(OSSummon.Corner, root != null ? root.transform.position : (Vector3?)null);
        }
    }
}
