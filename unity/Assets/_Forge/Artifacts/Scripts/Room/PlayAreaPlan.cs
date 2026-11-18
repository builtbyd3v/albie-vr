using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// A room with the play area the person chose (<see cref="UserPlayArea"/>) as its <see cref="Boundary"/>: the Quest
    /// boundary, an area drawn in Albie, or none (the room scan's whole free floor). Everything else is the room's own,
    /// <see cref="Key"/> included, so saved layouts still match and the "boundary changed" check compares like with
    /// like. Generators never know which it is: the boundary is a polygon either way.
    /// </summary>
    public sealed class PlayAreaPlan : IFloorPlan
    {
        public enum Source { Quest, Drawn, Room }

        public readonly IFloorPlan Inner;
        public readonly Source Kind;
        readonly Vector2[] boundary;

        public PlayAreaPlan(IFloorPlan inner, Source kind, Vector2[] boundary)
        {
            Inner = inner;
            Kind = kind;
            this.boundary = kind == Source.Room ? null : boundary;
        }

        /// <summary>
        /// How far inside the boundary a course keeps (m), beyond its own clearance. The Quest boundary keeps
        /// <see cref="PlayArea.Margin"/>; an area drawn on purpose is the floor the person said courses may use, so only
        /// a few centimetres.
        /// </summary>
        public float Margin => Kind == Source.Drawn ? DrawnMargin : PlayArea.Margin;
        public const float DrawnMargin = 0.05f;

        /// <summary>"your Quest boundary", "the play area you drew", "your room": for what Albie says.</summary>
        public string Words => Kind == Source.Drawn ? "the play area you drew" : Kind == Source.Room ? "your room's clear floor" : "your Quest boundary";

        public string Key => Inner.Key;
        public string Name => Inner.Name;
        public bool HasFloor => Inner.HasFloor;
        public float FloorY => Inner.FloorY;
        public float CeilingHeight => Inner.CeilingHeight;
        public Bounds FloorBounds => Inner.FloorBounds;
        public Vector2[] Boundary => boundary;
        public float AxisYaw => Inner.AxisYaw;
        public Matrix4x4 RoomToWorld => Inner.RoomToWorld;
        public bool IsFree(Vector3 point, float clearance) => Inner.IsFree(point, clearance);
        public bool TryFind(string label, out Vector3 centre, out float yaw, out Vector3 size) => Inner.TryFind(label, out centre, out yaw, out size);

        /// <summary>The room under any play area layers.</summary>
        public static IFloorPlan Unwrap(IFloorPlan room)
        {
            while (room is PlayAreaPlan p) room = p.Inner;
            return room;
        }
    }
}
