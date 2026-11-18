using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Forge.Artifacts
{
    /// <summary>
    /// The Play area panel: choose where room courses may go (<see cref="UserPlayArea"/>), and draw an area on the floor.
    /// <list type="bullet">
    /// <item>Use my whole room / Use Quest boundary / Automatic: one tap each, saved for this room.</item>
    /// <item>Draw an area: point a hand ray (or controller) at the floor and pinch to drop a corner; pinch near a corner to
    /// drag it; or tap Corner here to drop one where you stand. Rectangle: pinch one corner and drag to the opposite one.
    /// Undo, Clear, Done (needs 3+ corners, no crossing edges, 1 m² or more), Cancel. In the Editor the DesktopRig
    /// mouse does the same: click the floor to add, drag a corner to move it.</item>
    /// </list>
    /// Corners sit on the floor height from the room scan and snap to a 5 cm grid. The outline is one LineRenderer (one
    /// line mesh), the corners a small pool of spheres. Hand rays reach the floor through one thin trigger box with a
    /// <see cref="RayInteractable"/>, there only while drawing, as <see cref="VolumeHandle"/> does for volumes.
    /// After a change, an open room course is laid out again in the new area (<see cref="ArtifactHost.RetryLayout"/>).
    /// <see cref="Flash"/> shows the outline for a few seconds while a course is placed.
    /// </summary>
    public sealed class PlayAreaEditor : MonoBehaviour
    {
        public static PlayAreaEditor Current { get; private set; }

        const float OutlineWidth = 0.012f, HandleSize = 0.07f, GrabRadius = 0.2f, MaxReach = 12f, Lift = 0.008f;
        const string Green = "#7CFFB2", Amber = "#FFD24C";

        IFloorPlan room;
        ForgePanel panel;
        TMP_Text status;
        ForgePill rectanglePill;
        LineRenderer outline;
        readonly Transform[] handles = new Transform[PlayAreaShape.MaxCorners];
        readonly Vector2[] corners = new Vector2[PlayAreaShape.MaxCorners];
        readonly Vector2[] rect = new Vector2[4];
        readonly List<Vector2[]> history = new List<Vector2[]>();
        int count;
        bool drawing, rectangle, dirty;
        int dragIndex = -1;
        bool dragRect, mouseDrag;
        Vector2 rectFrom;
        float flashUntil = -1f;
        GameObject floorBox;
        RayInteractable rayTarget;
        ForgePointer rayPointer;
        Vector2[] shown;  // the polygon the outline shows when not drawing

        // ---------------------------------------------------------------- opening

        /// <summary>The room courses are placed in now (the host's, else MRUK's current one), or null.</summary>
        public static IFloorPlan CurrentRoom()
        {
            var host = ArtifactHost.Current;
            return host != null && host.Room != null ? host.Room : MrukFloorPlan.Current();
        }

        /// <summary>Opens the Play area panel in front of the person (<paramref name="draw"/>: straight into drawing). False with no room scan.</summary>
        public static bool Open(bool draw = false) => Open(CurrentRoom(), draw);

        public static bool Open(IFloorPlan plan, bool draw)
        {
            plan = PlayAreaPlan.Unwrap(plan);
            if (plan == null || !plan.HasFloor) return false;
            var editor = Ensure();
            editor.room = plan;
            editor.flashUntil = -1f;
            if (draw) editor.BeginDrawing();
            else editor.ShowModes();
            return true;
        }

        /// <summary>Shows the area courses use in this room as a faint outline for <paramref name="seconds"/>.</summary>
        public static void Flash(IFloorPlan plan, float seconds = 6f)
        {
            plan = PlayAreaPlan.Unwrap(plan);
            if (plan == null || !plan.HasFloor || !Application.isPlaying) return;
            var editor = Ensure();
            if (editor.panel != null) return;  // the panel already shows it
            editor.room = plan;
            editor.ShowArea();
            editor.flashUntil = Time.unscaledTime + seconds;
        }

        public static void CloseAll()
        {
            if (Current != null) Current.Close();
        }

        static PlayAreaEditor Ensure()
        {
            if (Current != null) return Current;
            var go = new GameObject("PlayAreaEditor");
            return go.AddComponent<PlayAreaEditor>();
        }

        void Awake()
        {
            Current = this;
            var line = new GameObject("Play area outline").AddComponent<LineRenderer>();
            line.transform.SetParent(transform, false);
            line.useWorldSpace = true;
            line.loop = true;
            line.positionCount = 0;
            line.sharedMaterial = ArtifactLook.Trail();
            line.numCornerVertices = 2;
            line.alignment = LineAlignment.TransformZ;  // flat on the floor
            line.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.widthMultiplier = OutlineWidth;
            outline = line;
            var look = ArtifactLook.Material(Green, true);
            for (int i = 0; i < handles.Length; i++)
            {
                var h = ArtifactLook.Shape("sphere", false);
                h.name = "Corner " + (i + 1);
                h.transform.SetParent(transform, false);
                h.transform.localScale = Vector3.one * HandleSize;
                h.GetComponent<Renderer>().sharedMaterial = look;
                h.SetActive(false);
                handles[i] = h.transform;
            }
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
            if (panel != null) Destroy(panel.gameObject);
            if (floorBox != null) Destroy(floorBox);
            if (rayTarget != null)
            {
                rayTarget.WhenSelectingInteractorAdded.Action -= OnRaySelect;
                rayTarget.WhenSelectingInteractorRemoved.Action -= OnRayRelease;
            }
        }

        void Close()
        {
            EndDrawing();
            if (panel != null) Destroy(panel.gameObject);
            panel = null;
            status = null;
            HideOutline();
        }

        // ---------------------------------------------------------------- the panel

        ForgePanel NewPanel(string subtitle)
        {
            if (panel != null) Destroy(panel.gameObject);
            var prefab = PanelPrefab();
            if (prefab == null)
            {
                Debug.LogWarning("[PlayArea] no ForgePanel prefab (no ArtifactHost in the scene), so no Play area panel.");
                return null;
            }
            panel = Instantiate(prefab);
            panel.name = "Panel Play area";
            panel.Title = "Play area";
            panel.Subtitle = subtitle;
            panel.SetBadge("AREA", ForgeTheme.Tool);
            panel.CloseHandler = Close;  // the X also takes the floor outline and its handles down
            if (panel.GetComponent<GraphicRaycaster>() == null) panel.gameObject.AddComponent<GraphicRaycaster>();  // mouse (DesktopRig)
            var placeholder = panel.Content.Find("Placeholder");
            if (placeholder != null) placeholder.gameObject.SetActive(false);
            var cam = Camera.main;
            if (cam != null)
            {
                var ahead = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
                if (ahead.sqrMagnitude < 1e-4f) ahead = Vector3.ProjectOnPlane(cam.transform.up, Vector3.up);
                ahead.Normalize();
                // Ahead and a little low and to the left: the floor in front stays in view while drawing.
                var at = cam.transform.position + ahead * 0.6f + Vector3.down * 0.25f - Vector3.Cross(Vector3.up, ahead) * 0.25f;
                panel.transform.SetPositionAndRotation(at, Quaternion.LookRotation(at - cam.transform.position));
            }
            return panel;
        }

        static ForgePanel PanelPrefab()
        {
            var host = ArtifactHost.Current;
            if (host != null && host.panelPrefab != null) return host.panelPrefab;
            var fromResources = Resources.Load<ArtifactHost>("ArtifactHost");
            return fromResources != null ? fromResources.panelPrefab : null;
        }

        void Finish(float y)
        {
            if (panel != null) panel.SizeMeters = new Vector2(ArtifactWidgets.PanelWidth, ArtifactWidgets.PanelHeight(y)) * ForgePanel.MetersPerUnit;
        }

        void ShowModes()
        {
            EndDrawing();
            if (NewPanel("Where room courses can go") == null) return;
            var mode = UserPlayArea.ModeOf(room);
            var c = panel.Content;
            float y = 0f;
            y += ArtifactWidgets.Note(c, y, UserPlayArea.Describe(room), true) + 12f;
            float x = 0f;
            x += Choice(c, y, x, "Use my whole room", mode == UserPlayArea.Mode.Room, () => Choose(UserPlayArea.Mode.Room)) + 10f;
            Choice(c, y, x, "Draw an area", mode == UserPlayArea.Mode.Draw, BeginDrawing);
            y += ForgeTheme.PillHeight + 10f;
            x = 0f;
            x += Choice(c, y, x, "Use Quest boundary", mode == UserPlayArea.Mode.Quest, () => Choose(UserPlayArea.Mode.Quest)) + 10f;
            Choice(c, y, x, "Automatic", mode == UserPlayArea.Mode.Auto, () => Choose(UserPlayArea.Mode.Auto));
            y += ForgeTheme.PillHeight + 14f;
            y += ArtifactWidgets.Note(c, y, UserPlayArea.Safety) + 12f;
            ArtifactWidgets.Pill(c, y, 0f, "Close", Close);
            y += ForgeTheme.PillHeight + 4f;
            Finish(y);
            ShowArea();
        }

        static float Choice(RectTransform parent, float y, float x, string label, bool on, System.Action press)
        {
            var pill = ArtifactWidgets.PillControl(parent, y, x, label, press);
            pill.On = on;
            return pill.Rect.sizeDelta.x;
        }

        void Choose(UserPlayArea.Mode mode)
        {
            UserPlayArea.SetMode(room, mode);
            ShowModes();
            Relayout();
        }

        static void Relayout()
        {
            var host = ArtifactHost.Current;
            if (host != null) host.RetryLayout(false);
        }

        // ---------------------------------------------------------------- drawing

        void BeginDrawing()
        {
            drawing = true;
            rectangle = false;
            history.Clear();
            var saved = UserPlayArea.Drawn(room);
            count = 0;
            if (saved != null)
                for (int i = 0; i < saved.Length && i < corners.Length; i++) corners[count++] = saved[i];
            dirty = true;
            EnsureFloorBox();
            if (NewPanel("Draw an area") == null) return;
            var c = panel.Content;
            float y = 0f;
            y += ArtifactWidgets.Note(c, y, DesktopRig.Current != null
                ? "Click the floor to drop a corner, drag a corner to move it. Or walk to a corner and tap Corner here."
                : "Point at the floor and pinch to drop a corner; pinch a corner and drag to move it. Or walk to each corner and tap Corner here.") + 10f;
            y += ArtifactWidgets.Note(c, y, " ", true) + 10f;
            status = c.GetChild(c.childCount - 1).GetComponent<TMP_Text>();
            float x = 0f;
            x += ArtifactWidgets.Pill(c, y, x, "Corner here", CornerHere) + 10f;
            rectanglePill = ArtifactWidgets.PillControl(c, y, x, "Rectangle", ToggleRectangle);
            x += rectanglePill.Rect.sizeDelta.x + 10f;
            ArtifactWidgets.Pill(c, y, x, "Undo", Undo);
            y += ForgeTheme.PillHeight + 10f;
            x = 0f;
            x += ArtifactWidgets.Pill(c, y, x, "Clear", Clear) + 10f;
            var done = ArtifactWidgets.PillControl(c, y, x, "Done", Done);
            done.On = true;
            x += done.Rect.sizeDelta.x + 10f;
            ArtifactWidgets.Pill(c, y, x, "Cancel", ShowModes);
            y += ForgeTheme.PillHeight + 4f;
            Finish(y);
            ShowStatus();
        }

        void EndDrawing()
        {
            drawing = false;
            dragIndex = -1;
            dragRect = mouseDrag = false;
            rayPointer = null;
            if (floorBox != null) floorBox.SetActive(false);
            for (int i = 0; i < handles.Length; i++) handles[i].gameObject.SetActive(false);
        }

        void CornerHere()
        {
            var cam = Camera.main;
            if (cam == null || count >= corners.Length) return;
            Remember();
            corners[count++] = PlayAreaShape.Snapped(new Vector2(cam.transform.position.x, cam.transform.position.z));
            dirty = true;
            ShowStatus();
        }

        void ToggleRectangle()
        {
            rectangle = !rectangle;
            if (rectanglePill != null) rectanglePill.On = rectangle;
            ShowStatus();
        }

        void Undo()
        {
            if (history.Count == 0) return;
            var last = history[history.Count - 1];
            history.RemoveAt(history.Count - 1);
            count = last.Length;
            for (int i = 0; i < count; i++) corners[i] = last[i];
            dirty = true;
            ShowStatus();
        }

        void Clear()
        {
            if (count == 0) return;
            Remember();
            count = 0;
            dirty = true;
            ShowStatus();
        }

        void Done()
        {
            string problem = PlayAreaShape.Problem(corners, count);
            if (problem != null)
            {
                if (status != null) status.text = problem;
                return;
            }
            UserPlayArea.SetDrawn(room, corners, count);
            ShowModes();
            Relayout();
        }

        // Called on a press, a corner added or a clear: once per action, not per frame.
        void Remember()
        {
            var copy = new Vector2[count];
            for (int i = 0; i < count; i++) copy[i] = corners[i];
            history.Add(copy);
            if (history.Count > 64) history.RemoveAt(0);
        }

        void ShowStatus()
        {
            if (status == null) return;
            string problem = count >= 3 ? PlayAreaShape.Problem(corners, count) : null;
            status.text = count == 0
                ? (rectangle ? "Rectangle: pinch one corner of the area and drag to the opposite one." : "No corners yet.")
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} corner{1}, {2:0.0} m².{3}", count, count == 1 ? "" : "s",
                    PlayAreaShape.Area(corners, count), problem != null ? " " + problem : count >= 3 ? " Tap Done to use it." : "");
        }

        // ---------------------------------------------------------------- input

        void EnsureFloorBox()
        {
            if (floorBox == null)
            {
                floorBox = new GameObject("Play area floor (ray target)");
                floorBox.transform.SetParent(transform, false);
                floorBox.layer = 2;  // Ignore Raycast: only the Interaction SDK's surface raycast should find it
                var box = floorBox.AddComponent<BoxCollider>();
                box.isTrigger = true;
                var surface = floorBox.AddComponent<ColliderSurface>();
                surface.InjectAllColliderSurface(box);
                rayTarget = floorBox.AddComponent<RayInteractable>();
                rayTarget.InjectAllRayInteractable(surface);
                rayTarget.WhenSelectingInteractorAdded.Action += OnRaySelect;
                rayTarget.WhenSelectingInteractorRemoved.Action += OnRayRelease;
            }
            // Thin, just under the floor, over the whole room: nothing standing on the floor touches it.
            var bounds = room.FloorBounds;
            var b = floorBox.GetComponent<BoxCollider>();
            floorBox.transform.position = new Vector3(bounds.center.x, room.FloorY - 0.015f, bounds.center.z);
            b.size = new Vector3(bounds.size.x + 2f, 0.01f, bounds.size.z + 2f);
            floorBox.SetActive(true);
        }

        void OnRaySelect(RayInteractor interactor)
        {
            if (!drawing) return;
            var cam = Camera.main;
            rayPointer = ForgePointer.FromRay(interactor, cam != null ? cam.transform : transform);
            Press(rayPointer.Ray);
        }

        void OnRayRelease(RayInteractor interactor)
        {
            if (rayPointer == null) return;
            rayPointer = null;
            Release();
        }

        void Update()
        {
            if (flashUntil > 0f && Time.unscaledTime > flashUntil && panel == null)
            {
                flashUntil = -1f;
                HideOutline();
            }
            if (!drawing) return;
            if (panel == null)
            {
                EndDrawing();
                HideOutline();
                return;
            }

            var rig = DesktopRig.Current;
            var mouse = Mouse.current;
            if (rig != null && rig.isActiveAndEnabled && mouse != null)
            {
                if (!mouseDrag && rayPointer == null && mouse.leftButton.wasPressedThisFrame && rig.Hovered == null && !DesktopRig.TypingInField)
                    mouseDrag = Press(rig.PointerRay);
                else if (mouseDrag && mouse.leftButton.isPressed) Drag(rig.PointerRay);
                else if (mouseDrag)
                {
                    mouseDrag = false;
                    Release();
                }
            }
            if (rayPointer != null)
            {
                if (rayPointer.Held) Drag(rayPointer.Ray);
                else
                {
                    rayPointer = null;
                    Release();
                }
            }
            if (dirty) Redraw();
        }

        bool FloorPoint(Ray ray, out Vector2 point)
        {
            point = default;
            if (ray.direction.y > -0.01f) return false;
            float t = (room.FloorY - ray.origin.y) / ray.direction.y;
            if (t <= 0f || t > MaxReach) return false;
            var p = ray.origin + ray.direction * t;
            point = new Vector2(p.x, p.z);
            return true;
        }

        // A press on the floor: grab the corner under it, or start a rectangle, or drop a new corner (and hold it to drag).
        bool Press(Ray ray)
        {
            if (!FloorPoint(ray, out var p)) return false;
            Remember();
            int near = PlayAreaShape.Nearest(corners, count, p, GrabRadius);
            if (near >= 0 && !rectangle)
            {
                dragIndex = near;
                return true;
            }
            if (rectangle)
            {
                rectFrom = PlayAreaShape.Snapped(p);
                PlayAreaShape.Rectangle(rectFrom, rectFrom, room.AxisYaw, rect);
                count = 4;
                for (int i = 0; i < 4; i++) corners[i] = rect[i];
                dragRect = true;
            }
            else
            {
                if (count >= corners.Length) return false;
                corners[count] = PlayAreaShape.Snapped(p);
                dragIndex = count++;
            }
            dirty = true;
            ShowStatus();
            return true;
        }

        void Drag(Ray ray)
        {
            if (!FloorPoint(ray, out var p)) return;
            p = PlayAreaShape.Snapped(p);
            if (dragRect)
            {
                PlayAreaShape.Rectangle(rectFrom, p, room.AxisYaw, rect);
                for (int i = 0; i < 4; i++)
                    if (corners[i] != rect[i]) { corners[i] = rect[i]; dirty = true; }
            }
            else if (dragIndex >= 0 && dragIndex < count && corners[dragIndex] != p)
            {
                corners[dragIndex] = p;
                dirty = true;
            }
        }

        void Release()
        {
            if (dragRect && PlayAreaShape.Area(corners, 4) < 0.01f) count = 0;  // a tap, not a drag: no rectangle
            dragIndex = -1;
            dragRect = false;
            dirty = true;
            ShowStatus();
        }

        // ---------------------------------------------------------------- showing

        void Redraw()
        {
            dirty = false;
            float y = room.FloorY + Lift;
            outline.positionCount = count;
            for (int i = 0; i < count; i++) outline.SetPosition(i, new Vector3(corners[i].x, y, corners[i].y));
            outline.loop = count >= 3;
            Tint(count >= 3 && PlayAreaShape.Problem(corners, count) != null ? Amber : Green, 0.95f);
            outline.enabled = count >= 2;
            for (int i = 0; i < handles.Length; i++)
            {
                bool on = i < count;
                if (handles[i].gameObject.activeSelf != on) handles[i].gameObject.SetActive(on);
                if (on) handles[i].position = new Vector3(corners[i].x, y + HandleSize * 0.5f, corners[i].y);
            }
        }

        // The area courses use now, faint: the drawn outline, the Quest boundary, or the room's floor outline.
        void ShowArea()
        {
            for (int i = 0; i < handles.Length; i++) handles[i].gameObject.SetActive(false);
            var mode = UserPlayArea.ModeOf(room);
            var quest = UserPlayArea.Quest(room);
            if (mode == UserPlayArea.Mode.Draw) shown = UserPlayArea.Drawn(room);
            else if (mode == UserPlayArea.Mode.Quest || (mode == UserPlayArea.Mode.Auto && quest != null && !UserPlayArea.IsTiny(quest))) shown = quest;
            else shown = RoomOutline(room);
            if (shown == null || shown.Length < 3)
            {
                HideOutline();
                return;
            }
            float y = room.FloorY + Lift;
            outline.positionCount = shown.Length;
            for (int i = 0; i < shown.Length; i++) outline.SetPosition(i, new Vector3(shown[i].x, y, shown[i].y));
            outline.loop = true;
            Tint(Green, 0.45f);
            outline.enabled = true;
        }

        void HideOutline()
        {
            if (outline != null) outline.enabled = false;
        }

        void Tint(string hex, float alpha)
        {
            var c = ArtifactLook.Color(hex, Color.green);
            c.a = alpha;
            outline.startColor = outline.endColor = c;
        }

        /// <summary>The room's floor outline (world xz): the scan's floor polygon, a mock room's rectangle, else its bounds.</summary>
        public static Vector2[] RoomOutline(IFloorPlan plan)
        {
            plan = PlayAreaPlan.Unwrap(plan);
            if (plan is MrukFloorPlan mruk && mruk.Room != null && mruk.Room.FloorAnchor != null)
            {
                var anchor = mruk.Room.FloorAnchor;
                var boundary = anchor.PlaneBoundary2D;
                if (boundary != null && boundary.Count >= 3)
                {
                    var result = new Vector2[boundary.Count];
                    for (int i = 0; i < boundary.Count; i++)
                    {
                        var w = anchor.transform.TransformPoint(new Vector3(boundary[i].x, boundary[i].y, 0f));
                        result[i] = new Vector2(w.x, w.z);
                    }
                    return result;
                }
            }
            if (plan is MockFloorPlan mock)
            {
                var r = Quaternion.Euler(0f, mock.Yaw, 0f);
                var result = new Vector2[4];
                for (int i = 0; i < 4; i++)
                {
                    float sx = i == 0 || i == 3 ? -0.5f : 0.5f, sz = i < 2 ? -0.5f : 0.5f;
                    var w = mock.Origin + r * new Vector3(sx * mock.Size.x, 0f, sz * mock.Size.y);
                    result[i] = new Vector2(w.x, w.z);
                }
                return result;
            }
            if (plan == null) return null;
            var b = plan.FloorBounds;
            return new[] { new Vector2(b.min.x, b.min.z), new Vector2(b.max.x, b.min.z), new Vector2(b.max.x, b.max.z), new Vector2(b.min.x, b.max.z) };
        }
    }
}
