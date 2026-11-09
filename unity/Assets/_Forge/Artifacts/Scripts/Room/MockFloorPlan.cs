using System.Collections.Generic;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// A rectangular room with box furniture, for checks and the sandbox with no headset (mock first). The presets cover
    /// the cases C-12's fit rules have to handle: a living room with a couch, a small bedroom (shrink and say so), a
    /// closet (no free floor), a room with no couch, and a room that was never scanned.
    /// </summary>
    public sealed class MockFloorPlan : IFloorPlan
    {
        public struct Furniture
        {
            public string Label;
            public Vector3 Centre;  // room space, y = the middle of its height
            public Vector3 Size;
            public float Yaw;
        }

        public readonly string Id;
        public readonly Vector2 Size;          // x by z, metres
        public readonly Vector3 Origin;        // where the room's centre is on the floor, world
        public readonly float Yaw;
        public readonly bool Scanned;
        public float Ceiling = 2.5f;           // floor to ceiling, metres; NaN when the scan has no ceiling
        public readonly List<Furniture> Items = new List<Furniture>();

        public MockFloorPlan(string id, Vector2 size, bool scanned = true, Vector3 origin = default, float yaw = 0f)
        {
            Id = id;
            Size = size;
            Scanned = scanned;
            Origin = origin;
            Yaw = yaw;
        }

        public MockFloorPlan WithCeiling(float height)
        {
            Ceiling = height;
            return this;
        }

        public MockFloorPlan Add(string label, Vector2 at, Vector3 size, float yaw = 0f)
        {
            Items.Add(new Furniture { Label = label, Centre = new Vector3(at.x, size.y * 0.5f, at.y), Size = size, Yaw = yaw });
            return this;
        }

        public string Key => "mock:" + Id;
        public string Name => "mock " + Id;
        public bool HasFloor => Scanned;
        public float FloorY => Origin.y;
        public float CeilingHeight => Scanned ? Ceiling : float.NaN;
        public float AxisYaw => Yaw;
        public Matrix4x4 RoomToWorld => Matrix4x4.TRS(Origin, Quaternion.Euler(0f, Yaw, 0f), Vector3.one);

        public Bounds FloorBounds
        {
            get
            {
                var bounds = new Bounds(Origin, Vector3.zero);
                var r = Quaternion.Euler(0f, Yaw, 0f);
                for (int i = 0; i < 4; i++)
                    bounds.Encapsulate(Origin + r * new Vector3((i & 1) == 0 ? -Size.x : Size.x, 0f, (i & 2) == 0 ? -Size.y : Size.y) * 0.5f);
                return bounds;
            }
        }

        public bool IsFree(Vector3 point, float clearance)
        {
            if (!Scanned) return false;
            var local = Quaternion.Euler(0f, -Yaw, 0f) * (point - Origin);
            if (Mathf.Abs(local.x) > Size.x * 0.5f - clearance || Mathf.Abs(local.z) > Size.y * 0.5f - clearance) return false;
            foreach (var item in Items)
            {
                var inItem = Quaternion.Euler(0f, -item.Yaw, 0f) * (local - new Vector3(item.Centre.x, local.y, item.Centre.z));
                if (Mathf.Abs(inItem.x) <= item.Size.x * 0.5f + clearance && Mathf.Abs(inItem.z) <= item.Size.z * 0.5f + clearance) return false;
            }
            return true;
        }

        public bool TryFind(string label, out Vector3 centre, out float yaw, out Vector3 size)
        {
            centre = default; size = default; yaw = 0f;
            float best = -1f;
            foreach (var item in Items)
            {
                if (item.Label != label || item.Size.x * item.Size.z <= best) continue;
                best = item.Size.x * item.Size.z;
                centre = RoomToWorld.MultiplyPoint3x4(item.Centre);
                yaw = Yaw + item.Yaw + 90f;  // the long side (local x) points along yaw + 90 (yaw 0 is +z)
                size = item.Size;
            }
            return best >= 0f;
        }

        // ---- Presets ----------------------------------------------------------------------------------------------

        /// <summary>
        /// 7 x 5 m open-plan living room: a couch and coffee table along the back wall, a cupboard and a plant in the
        /// corners. The default course (5.4 m end to end, plus clearance) fits in full beside the couch.
        /// </summary>
        public static MockFloorPlan LivingRoom() => new MockFloorPlan("living_room", new Vector2(7f, 5f))
            .Add("COUCH", new Vector2(-0.5f, -2.0f), new Vector3(2.2f, 0.8f, 0.95f))
            .Add("TABLE", new Vector2(-0.5f, -1.05f), new Vector3(1.0f, 0.45f, 0.5f))
            .Add("STORAGE", new Vector2(3.2f, 2.1f), new Vector3(0.5f, 1.8f, 0.7f))
            .Add("PLANT", new Vector2(-3.2f, 2.2f), new Vector3(0.45f, 1.2f, 0.45f));

        /// <summary>4.5 x 3.8 m bedroom, a bed against one wall and no couch: the course shrinks, and Albie says why.</summary>
        public static MockFloorPlan SmallRoom() => new MockFloorPlan("small_room", new Vector2(4.5f, 3.8f))
            .Add("BED", new Vector2(-1.55f, -0.9f), new Vector3(1.4f, 0.55f, 2.0f))
            .Add("STORAGE", new Vector2(-1.9f, 1.6f), new Vector3(0.6f, 1.0f, 0.5f));

        /// <summary>A 1.8 x 1.5 m closet: not even the smallest course fits (<c>no_free_floor</c>).</summary>
        public static MockFloorPlan Closet() => new MockFloorPlan("closet", new Vector2(1.8f, 1.5f))
            .Add("STORAGE", new Vector2(0f, -0.5f), new Vector3(1.6f, 2.0f, 0.45f));

        /// <summary>An 8 x 6 m empty loft: everything fits, but there's no couch to go around.</summary>
        public static MockFloorPlan Loft() => new MockFloorPlan("loft", new Vector2(8f, 6f));

        /// <summary>A room that was never scanned (<c>no_room_data</c>).</summary>
        public static MockFloorPlan Unscanned() => new MockFloorPlan("unscanned", new Vector2(5f, 4f), scanned: false);

        /// <summary>The living room again, turned 30° and moved, to check nothing assumes a room on the world axes.</summary>
        public static MockFloorPlan TurnedLivingRoom()
        {
            var room = new MockFloorPlan("living_room_turned", new Vector2(7f, 5f), true, new Vector3(3f, 0f, -2f), 30f);
            foreach (var item in LivingRoom().Items) room.Items.Add(item);
            return room;
        }

        /// <summary>
        /// 5 x 4 m room whose scan labelled nothing: two boxes of OTHER and no ceiling. Courses still go on free floor;
        /// hoops say they couldn't check the ceiling.
        /// </summary>
        public static MockFloorPlan Unlabelled() => new MockFloorPlan("unlabelled", new Vector2(5f, 4f))
            .Add("OTHER", new Vector2(-1.6f, -1.3f), new Vector3(1.2f, 0.8f, 0.8f))
            .Add("OTHER", new Vector2(1.8f, 1.4f), new Vector3(0.8f, 1.5f, 0.6f))
            .WithCeiling(float.NaN);

        /// <summary>A 6 x 4 m attic with a 1.4 m ceiling: plenty of floor, but the hoops don't fit under it (<c>no_headroom</c>).</summary>
        public static MockFloorPlan Attic() => new MockFloorPlan("attic", new Vector2(6f, 4f)).WithCeiling(1.4f);

        /// <summary>
        /// A 3.2 x 1.35 m hallway: too narrow and short for the normal smallest course, so it gets the tight one (a
        /// narrow slalom, or hoops in a straight line, 0.5 m apart).
        /// </summary>
        public static MockFloorPlan Hallway() => new MockFloorPlan("hallway", new Vector2(3.2f, 1.35f));

        /// <summary>
        /// A 5.6 x 1.35 m hallway: too narrow for the full slalom (1.5 m with its clearance), long enough for the narrow
        /// one at the slalom's 1.0 m minimum spacing.
        /// </summary>
        public static MockFloorPlan LongHallway() => new MockFloorPlan("long_hallway", new Vector2(5.6f, 1.35f));

        public static MockFloorPlan[] All() => new[] { LivingRoom(), SmallRoom(), Closet(), Loft(), Unscanned(), TurnedLivingRoom(), Unlabelled(), Attic(), Hallway(), LongHallway() };
    }
}
