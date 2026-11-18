using Forge.Artifacts;
using Forge.Controllers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The drone cam window (id "dronecam"): the drone's nose camera (<see cref="DroneCam"/>) and its true telemetry. The
    /// picture is labelled as what it is: the drone's VIRTUAL view (the scene and the room scan as lines; passthrough is not
    /// available to a Unity camera), with an orange box at the user's known headset position, not a detection. Opening the
    /// window switches the camera on; closing it switches it off. Telemetry is re-read four times a second and a line is
    /// rebuilt only when a shown number changes.
    /// </summary>
    public sealed class OSDroneCamWindow : MonoBehaviour
    {
        public const string WindowId = "dronecam";
        static readonly Vector2 SizeMeters = new Vector2(0.34f, 0.36f);
        const float PictureWidth = 400f, PictureHeight = 300f;
        const float PollSeconds = 0.25f;

        RawImage picture;
        TMP_Text telemetry, caption;
        float nextPoll;
        long key = long.MinValue;

        /// <summary>Opens the drone cam window (or brings it forward) and switches the camera on. Null with no shell or no drone.</summary>
        public static OSWindow Open()
        {
            OSShell shell = OSShell.Current;
            if (shell == null) return null;
            ArtifactDrone drone = ArtifactDrone.Live;
            if (drone != null) drone.SetCam(true);
            return shell.Windows.Open(WindowId, "Drone cam", SizeMeters, Build, "CAM");
        }

        public static void Close()
        {
            OSShell shell = OSShell.Current;
            if (shell != null) shell.Windows.Close(WindowId);
        }

        public static bool IsOpen => OSShell.Current != null && OSShell.Current.Windows.IsOpen(WindowId);

        static void Build(ForgePanel panel) => panel.gameObject.AddComponent<OSDroneCamWindow>().Init(panel);

        void Init(ForgePanel panel)
        {
            RectTransform body = OSUi.Body(panel);
            if (body == null) return;
            RectTransform column = OSUi.Column(body, 8f);
            var label = OSUi.Text(column, "Drone's virtual view: no passthrough", OSUi.SmallText, true);
            label.alignment = TextAlignmentOptions.Center;

            var holder = new GameObject("Picture", typeof(RectTransform));
            holder.transform.SetParent(column, false);
            OSUi.Size(holder.transform, PictureWidth, PictureHeight);
            var layout = holder.GetComponent<LayoutElement>();
            if (layout == null) layout = holder.AddComponent<LayoutElement>();
            layout.flexibleHeight = 0f;
            layout.flexibleWidth = 0f;
            picture = holder.AddComponent<RawImage>();
            picture.raycastTarget = false;
            picture.color = Color.white;

            telemetry = OSUi.Text(column, "", OSUi.BodyText);
            telemetry.alignment = TextAlignmentOptions.Center;
            caption = OSUi.Text(column, "Lines: the room scan. Orange box: your headset's position (known from the headset, not seen by the camera).", OSUi.CaptionText, true);
            caption.alignment = TextAlignmentOptions.Center;
            Poll();
        }

        void OnDisable()
        {
            ArtifactDrone drone = ArtifactDrone.Live;
            if (drone != null) drone.SetCam(false);
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (now < nextPoll) return;
            nextPoll = now + PollSeconds;
            Poll();
        }

        void Poll()
        {
            if (telemetry == null) return;
            ArtifactDrone drone = ArtifactDrone.Live;
            DroneCam cam = drone != null ? drone.Cam : null;
            if (drone != null && cam != null && !cam.On) drone.SetCam(true);  // the window open means the camera is wanted
            Texture tex = cam != null && cam.On ? cam.Texture : null;
            if (picture.texture != tex) picture.texture = tex;
            picture.color = tex != null ? Color.white : new Color(0.05f, 0.07f, 0.09f, 1f);

            if (drone == null)
            {
                if (key != -1) { key = -1; telemetry.text = "No drone. Open a drone course first."; }
                return;
            }
            float user = drone.UserDistance;
            int du = float.IsNaN(user) ? 999 : Mathf.RoundToInt(user * 10f);
            int alt = Mathf.RoundToInt(drone.Body.HeightAboveFloor * 10f);
            int sp = Mathf.RoundToInt(drone.Body.Speed * 10f);
            int mode = (int)drone.Body.State * 8 + (drone.Follower != null ? (int)drone.Follower.State : 0);
            long k = (((long)du * 1024 + alt) * 1024 + sp) * 64 + mode;
            if (k == key) return;
            key = k;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string you = float.IsNaN(user) ? "no headset" : (du / 10f).ToString("0.0", inv) + " m from you";
            telemetry.text = you + " · alt " + (alt / 10f).ToString("0.0", inv) + " m · " + (sp / 10f).ToString("0.0", inv) + " m/s\n" + ModeText(drone);
        }

        static string ModeText(ArtifactDrone drone)
        {
            switch (drone.FollowState)
            {
                case "starting": return "Follow-me: taking off";
                case "following": return "Follow-me" + (drone.Follower.UsesScan ? " (steering by the room scan)" : " (no scan: bump sensor only)");
                case "too_close": return "Follow-me: holding, you are close";
                case "blocked": return "Follow-me: holding, no free spot near you";
            }
            switch (drone.Body.State)
            {
                case DroneBody.Phase.TakingOff: return "Taking off";
                case DroneBody.Phase.Flying: return drone.Body.Returning ? "Flying home" : "Flying by hand";
                case DroneBody.Phase.Landing: return "Landing";
                case DroneBody.Phase.Crashed: return "Crashed";
                default: return "Landed";
            }
        }
    }
}
