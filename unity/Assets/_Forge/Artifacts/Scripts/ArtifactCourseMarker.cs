using UnityEngine;
using UnityEngine.UI;

namespace Forge.Artifacts
{
    /// <summary>
    /// A room course has no box to grab, so it gets this: a small floating card above its start with the course's name in its
    /// title bar, the window X every panel has (<see cref="PanelChrome"/>: it closes the course, its cones and gates, and puts
    /// the truck or drone away, with the undo, through <see cref="ArtifactDismiss.Close"/>) and a Play area button (where
    /// courses may go). It stands at chest height over the start of a floor course (just above a desk course), turned to the
    /// user, and goes with the scene (it is one of the host's <see cref="ArtifactHost.Spawned"/>). Like the course itself it is
    /// not under the host, so moving the host never drags it; the window manager does not adopt it (it isn't a window to move
    /// about) and the Spaces list shows it as the course row, not as a loose panel. Nothing per frame but a turn to face the eyes.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ForgePanel))]
    public sealed class ArtifactCourseMarker : MonoBehaviour
    {
        /// <summary>The card's width (canvas units, mm).</summary>
        public const float Width = 300f;
        /// <summary>How high over the start it floats (m): a floor course, and one on a desk.</summary>
        public const float FloorHeight = 1.1f, DeskHeight = 0.24f;

        ArtifactHost host;
        ForgePanel panel;

        /// <summary>The card's panel.</summary>
        public ForgePanel Panel => panel;
        /// <summary>The card's centre in the world.</summary>
        public Vector3 Centre => transform.TransformPoint(((RectTransform)transform).rect.center);

        /// <summary>The marker of the host's open course, or null.</summary>
        public static ArtifactCourseMarker Of(ArtifactHost host)
        {
            if (host == null) return null;
            var spawned = host.Spawned;
            for (int i = 0; i < spawned.Count; i++)
                if (spawned[i] != null && spawned[i].TryGetComponent(out ArtifactCourseMarker marker)) return marker;
            return null;
        }

        /// <summary>Makes the marker for the host's open course from <paramref name="prefab"/>; the caller adds it to the scene's objects.</summary>
        public static ArtifactCourseMarker Create(ArtifactHost host, ForgePanel prefab, string title)
        {
            if (host == null || prefab == null) return null;
            ForgePanel panel = Instantiate(prefab);
            panel.name = "Course marker " + title;
            panel.panelId = "artifact-course-marker";
            panel.Title = string.IsNullOrEmpty(title) ? "Course" : title;
            panel.SetBadge("ART", host.accent);
            panel.Minimisable = false;  // it closes, or it stays: a dot over the floor would only get lost
            if (panel.GetComponent<GraphicRaycaster>() == null) panel.gameObject.AddComponent<GraphicRaycaster>();  // the mouse
            if (panel.Content != null)
            {
                var placeholder = panel.Content.Find("Placeholder");
                if (placeholder != null) placeholder.gameObject.SetActive(false);
                ArtifactWidgets.Pill(panel.Content, 0f, 0f, "Play area", () => PlayAreaEditor.Open(host.CourseRoom, false));
            }
            panel.SizeMeters = new Vector2(Width, ArtifactWidgets.PanelHeight(ForgeTheme.PillHeight)) * ForgePanel.MetersPerUnit;
            panel.CloseHandler = () => ArtifactDismiss.Close(host);
            var marker = panel.gameObject.AddComponent<ArtifactCourseMarker>();
            marker.host = host;
            marker.panel = panel;
            marker.Place();
            if (!Application.isPlaying) PanelChrome.Of(panel);  // at runtime it builds in Start
            return marker;
        }

        // Over the start, a little to the side so it doesn't stand right over the truck.
        void Place()
        {
            Pose start = host.SceneOrigin;
            bool desk = host.CourseScale < 0.99f;
            Vector3 side = start.rotation * Vector3.right * (desk ? 0.12f : 0.35f);
            transform.position = start.position + Vector3.up * (desk ? DeskHeight : FloorHeight) + side;
            Face();
        }

        void LateUpdate() => Face();

        void Face()
        {
            var cam = Camera.main;
            Vector3 away = cam != null ? transform.position - cam.transform.position : host != null ? host.SceneOrigin.rotation * Vector3.forward : Vector3.forward;
            away.y = 0f;
            if (away.sqrMagnitude < 1e-6f) return;
            transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }
    }
}
