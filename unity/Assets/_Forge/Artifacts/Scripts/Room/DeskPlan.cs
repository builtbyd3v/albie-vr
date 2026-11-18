using System.Globalization;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The top of a table as a floor plan, so every generator can lay a course on a desk without knowing it (host 7,
    /// <c>surface: "desk"</c>). The table is the biggest one the room's scan has (<see cref="IFloorPlan.TryFind"/> with
    /// TABLE), the same one a volume artifact is put on.
    ///
    /// A desk course is the floor course made smaller. Rather than teach each generator about scale, this plan is the
    /// table top magnified by 1 / <see cref="Scale"/> around its centre: a generator fits its full-size course on that
    /// big virtual table, and <see cref="ToReal"/> maps where it put the course back onto the real one. The scene root is
    /// then scaled by <see cref="Scale"/> (<see cref="ArtifactHost.CourseScale"/>), so slots, zones and cones all shrink
    /// together. The scan knows the table, not what is lying on it, so the whole top inside its edge counts as free.
    /// </summary>
    public sealed class DeskPlan : IFloorPlan
    {
        /// <summary>The sizes tried, biggest first: a course is made as large as the table allows.</summary>
        public static readonly float[] Scales = { 0.25f, 0.2f, 0.15f, 0.1f };
        /// <summary>How far inside the table's real edge a course stays (m).</summary>
        public const float EdgeMargin = 0.03f;
        public const string Label = "TABLE";

        public readonly IFloorPlan Room;
        public readonly float Scale;
        readonly Vector3 centre;        // the middle of the table top, world
        readonly Quaternion turn;       // +z along the table's long side
        readonly float yaw, halfLong, halfShort;  // real metres

        DeskPlan(IFloorPlan room, float scale, Vector3 centre, float yaw, float halfLong, float halfShort)
        {
            Room = room;
            Scale = scale;
            this.centre = centre;
            this.yaw = yaw;
            turn = Quaternion.Euler(0f, yaw, 0f);
            this.halfLong = halfLong;
            this.halfShort = halfShort;
        }

        /// <summary>The room's table at this scale, or false when the scan has no table.</summary>
        public static bool TryMake(IFloorPlan room, float scale, out DeskPlan plan)
        {
            plan = null;
            if (room == null || !room.HasFloor || scale <= 0f) return false;
            if (!room.TryFind(Label, out var at, out float tableYaw, out var size)) return false;
            // TryFind's size is (along its yaw, height, across), and its centre is the middle of the volume.
            var top = new Vector3(at.x, at.y + size.y * 0.5f, at.z);
            plan = new DeskPlan(room, scale, top, tableYaw, size.x * 0.5f, size.z * 0.5f);
            return true;
        }

        /// <summary>The real table top, long side by short side (m).</summary>
        public Vector2 TopSize => new Vector2(halfLong * 2f, halfShort * 2f);
        public float TopY => centre.y;

        /// <summary>A pose on the magnified table, back on the real one.</summary>
        public Pose ToReal(Pose onPlan)
        {
            var p = centre + (onPlan.position - centre) * Scale;
            return new Pose(new Vector3(p.x, centre.y, p.z), onPlan.rotation);
        }

        public string Key => Room.Key + "#desk@" + Scale.ToString("0.00", CultureInfo.InvariantCulture);
        public string Name => Room.Name + " table";
        public bool HasFloor => true;
        public float FloorY => centre.y;
        public Vector2[] Boundary => null;  // the Quest boundary is about where a person walks, not what is on the desk
        public float AxisYaw => yaw;
        public Matrix4x4 RoomToWorld => Matrix4x4.TRS(centre, turn, Vector3.one);

        public float CeilingHeight
        {
            get
            {
                float ceiling = Room.CeilingHeight;
                return float.IsNaN(ceiling) ? float.NaN : (ceiling - (centre.y - Room.FloorY)) / Scale;
            }
        }

        public Bounds FloorBounds
        {
            get
            {
                var bounds = new Bounds(centre, Vector3.zero);
                for (int i = 0; i < 4; i++)
                    bounds.Encapsulate(centre + turn * new Vector3((i & 1) == 0 ? -halfShort : halfShort, 0f, (i & 2) == 0 ? -halfLong : halfLong) / Scale);
                return bounds;
            }
        }

        public bool IsFree(Vector3 point, float clearance)
        {
            var local = Quaternion.Inverse(turn) * (point - centre);
            float edge = clearance + EdgeMargin / Scale;
            return Mathf.Abs(local.x) <= halfShort / Scale - edge && Mathf.Abs(local.z) <= halfLong / Scale - edge;
        }

        // Nothing on a desk is in the scan, so there is nothing to go around.
        public bool TryFind(string label, out Vector3 at, out float foundYaw, out Vector3 size)
        {
            at = default;
            size = default;
            foundYaw = 0f;
            return false;
        }

        /// <summary>"1:4", "1:7": the scale as a ratio, for what Albie says.</summary>
        public static string RatioWords(float scale) => "1:" + Mathf.RoundToInt(1f / Mathf.Max(0.01f, scale)).ToString(CultureInfo.InvariantCulture);

        /// <summary>"a quarter of full size", for what Albie says.</summary>
        public static string SizeWords(float scale)
        {
            if (scale >= 0.245f) return "a quarter of full size";
            if (scale >= 0.195f) return "a fifth of full size";
            if (scale >= 0.145f) return "about a seventh of full size";
            return "a tenth of full size";
        }
    }
}
