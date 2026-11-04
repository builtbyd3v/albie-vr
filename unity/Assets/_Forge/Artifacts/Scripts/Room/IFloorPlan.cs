using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The room as a generator sees it (C-12 section 3.3): where the floor is and which points on it are free. A
    /// generator only asks questions; it never reads MRUK directly, so the same code runs on a headset's scan
    /// (<see cref="MrukFloorPlan"/>) and on the mock rooms in checks and sandboxes (<see cref="MockFloorPlan"/>).
    /// Everything is in world space unless it says otherwise.
    /// </summary>
    public interface IFloorPlan
    {
        /// <summary>A stable id for the room, so a saved layout is only reused in the room it was made for.</summary>
        string Key { get; }
        /// <summary>A name to say out loud or log ("LivingRoom02", "mock small room").</summary>
        string Name { get; }
        /// <summary>False when there's no floor data at all: nothing is placed from a guess (<c>no_room_data</c>).</summary>
        bool HasFloor { get; }
        float FloorY { get; }
        /// <summary>A world-axis box around the floor, for the search grid.</summary>
        Bounds FloorBounds { get; }
        /// <summary>The room's main axis (degrees about Y). Courses prefer to run along it or across it.</summary>
        float AxisYaw { get; }
        /// <summary>
        /// The frame layouts are saved in: the floor's origin with the room's yaw. Layouts store their origin in this
        /// frame, so a course comes back in the same place when the room is loaded again.
        /// </summary>
        Matrix4x4 RoomToWorld { get; }

        /// <summary>
        /// "Free floor" (C-12 3.3): inside the room's walls and outside every furniture volume, by at least
        /// <paramref name="clearance"/> metres each way. Only x and z of <paramref name="point"/> are used.
        /// </summary>
        bool IsFree(Vector3 point, float clearance);

        /// <summary>The biggest piece of furniture with this MRUK label (COUCH, TABLE, BED...), or false if there's none.</summary>
        bool TryFind(string label, out Vector3 centre, out float yaw, out Vector3 size);
    }
}
