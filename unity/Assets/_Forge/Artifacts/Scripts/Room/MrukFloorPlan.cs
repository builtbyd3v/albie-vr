using System;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The room from MRUK: the headset's scan, the simulator's room, or a prefab room in the Editor. Free floor is C-12's
    /// definition: <c>MRUKRoom.IsPositionInRoom</c> (checked at the clearance in eight directions, since it has no buffer
    /// of its own) and not <c>IsPositionInSceneVolume</c> with the clearance as its buffer.
    /// </summary>
    public sealed class MrukFloorPlan : IFloorPlan
    {
        static readonly Vector3[] Around =
        {
            new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, -1f),
            new Vector3(0.7071f, 0f, 0.7071f), new Vector3(-0.7071f, 0f, 0.7071f), new Vector3(0.7071f, 0f, -0.7071f), new Vector3(-0.7071f, 0f, -0.7071f),
        };

        public readonly MRUKRoom Room;
        public Vector2[] Boundary { get; }

        public MrukFloorPlan(MRUKRoom room) { Room = room; Boundary = PlayArea.WorldPolygon(); }

        /// <summary>The room the user is in (MRUK's current room), or null when MRUK has no room loaded.</summary>
        public static MrukFloorPlan Current()
        {
            if (MRUK.Instance == null || !MRUK.Instance.IsInitialized) return null;
            var room = MRUK.Instance.GetCurrentRoom();
            if (room == null && MRUK.Instance.Rooms.Count > 0) room = MRUK.Instance.Rooms[0];
            return room != null ? new MrukFloorPlan(room) : null;
        }

        public string Key
        {
            get
            {
                var uuid = Room.Anchor.Uuid;
                return uuid != Guid.Empty ? uuid.ToString() : "mruk:" + Room.name;
            }
        }

        public string Name => Room.name;
        public bool HasFloor => Room != null && Room.FloorAnchor != null;
        public float FloorY => Room.FloorAnchor.transform.position.y;
        public Bounds FloorBounds => Room.GetRoomBounds();
        public float CeilingHeight => Room.CeilingAnchor != null ? Room.CeilingAnchor.transform.position.y - FloorY : float.NaN;

        // The floor anchor faces up (its forward is world up), so its local y is a direction along the floor.
        public float AxisYaw
        {
            get
            {
                var along = Room.FloorAnchor.transform.up;
                return Mathf.Atan2(along.x, along.z) * Mathf.Rad2Deg;
            }
        }

        public Matrix4x4 RoomToWorld
        {
            get
            {
                var at = Room.FloorAnchor.transform.position;
                return Matrix4x4.TRS(new Vector3(at.x, FloorY, at.z), Quaternion.Euler(0f, AxisYaw, 0f), Vector3.one);
            }
        }

        public bool IsFree(Vector3 point, float clearance)
        {
            var q = new Vector3(point.x, FloorY + 0.05f, point.z);
            if (!Room.IsPositionInRoom(q, false)) return false;
            if (clearance > 0f)
                for (int i = 0; i < Around.Length; i++)
                    if (!Room.IsPositionInRoom(q + Around[i] * clearance, false)) return false;
            return !Room.IsPositionInSceneVolume(q, true, clearance);
        }

        public bool TryFind(string label, out Vector3 centre, out float yaw, out Vector3 size)
        {
            centre = default; size = default; yaw = 0f;
            float best = -1f;
            MRUKAnchor.SceneLabels flag;
            try { flag = (MRUKAnchor.SceneLabels)Enum.Parse(typeof(MRUKAnchor.SceneLabels), label); }
            catch (ArgumentException) { return false; }
            foreach (var anchor in Room.Anchors)
            {
                if (anchor == null || !anchor.HasAnyLabel(flag) || !anchor.VolumeBounds.HasValue) continue;
                // A volume anchor's local x and y lie on the floor and z is its height.
                var b = anchor.VolumeBounds.Value.size;
                if (b.x * b.y <= best) continue;
                best = b.x * b.y;
                centre = anchor.GetAnchorCenter();
                size = new Vector3(Mathf.Max(b.x, b.y), b.z, Mathf.Min(b.x, b.y));  // long side, height, short side
                var along = b.x >= b.y ? anchor.transform.right : anchor.transform.up;
                yaw = Mathf.Atan2(along.x, along.z) * Mathf.Rad2Deg;
            }
            return best >= 0f;
        }
    }
}
