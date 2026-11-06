using System;
using System.Collections.Generic;
using Forge.Artifacts;
using Forge.Controllers;
using UnityEngine;

/// <summary>
/// Room slaloms for the mock truck (ML A with Person 5's generator): fits the slalom template's course into a mock
/// room with <see cref="FloorSlalom"/>, then turns the room into a <see cref="MockCourse"/> in the course's frame. The
/// sim's x is the course's +z (from the start), and its y is the course's -x (left). Cones and gates come from the
/// generator's slots; the room's four walls and every piece of furniture become wall segments, so the truck's rays see
/// what the headset's ray fan would. <see cref="Random"/> makes rooms nobody has tested, for training and the checks.
/// </summary>
public static class RoomCourses
{
    /// <summary>The slalom template's own fit settings (scene.json: around the couch, 1.2 m spacing, at least 1.0 m).</summary>
    public static FloorSlalom.Settings TemplateSettings(string around = "COUCH")
    {
        var s = FloorSlalom.Settings.Default;
        s.Around = around;
        return s;
    }

    /// <summary>The course in <paramref name="room"/>, or null with <paramref name="why"/> when no slalom fits.</summary>
    public static MockCourse From(MockFloorPlan room, out string why, FloorSlalom.Settings? settings = null)
    {
        var fit = FloorSlalom.Fit(room, settings ?? TemplateSettings());
        why = fit.Ok ? null : fit.Code;
        return fit.Ok ? From(room, fit) : null;
    }

    public static MockCourse From(MockFloorPlan room, FloorSlalom.Result fit)
    {
        var toCourse = Quaternion.Inverse(Quaternion.Euler(0f, fit.Yaw, 0f));
        Vector2 Sim(Vector3 world)
        {
            var local = toCourse * (world - fit.Origin);
            return new Vector2(local.z, -local.x);
        }
        var course = new MockCourse { Name = $"{room.Id}: {fit.Gates} gates, {fit.Spacing:0.0} m{(fit.Narrow ? ", narrow" : "")}" };
        var slots = fit.Slots;
        foreach (var cone in slots["course.cones"]) { var p = new Vector2(cone.position.z, -cone.position.x); course.AddCone(p.x, p.y); }
        foreach (var gate in slots["course.gates"]) { var p = new Vector2(gate.position.z, -gate.position.x); course.AddGate(p.x, p.y); }
        var finish = slots["course.finish"][0].position;
        course.SetFinish(finish.z, -finish.x);

        var roomToWorld = room.RoomToWorld;
        void Box(Vector3 centre, float sx, float sz, float yaw)
        {
            var r = Quaternion.Euler(0f, yaw, 0f);
            var corners = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                var local = centre + r * new Vector3((i == 0 || i == 3 ? -0.5f : 0.5f) * sx, 0f, (i < 2 ? -0.5f : 0.5f) * sz);
                corners[i] = Sim(roomToWorld.MultiplyPoint3x4(local));
            }
            for (int i = 0; i < 4; i++)
            {
                var a = corners[i];
                var b = corners[(i + 1) % 4];
                course.AddWall(a.x, a.y, b.x, b.y);
            }
        }
        Box(Vector3.zero, room.Size.x, room.Size.y, 0f);   // the room's own walls
        foreach (var item in room.Items) Box(new Vector3(item.Centre.x, 0f, item.Centre.z), item.Size.x, item.Size.z, item.Yaw);
        return course;
    }

    /// <summary>The mock rooms a slalom fits in (the others say there's no floor, and are left out).</summary>
    public static List<(MockFloorPlan room, MockCourse course)> MockRooms()
    {
        var list = new List<(MockFloorPlan, MockCourse)>();
        foreach (var room in MockFloorPlan.All())
        {
            var course = From(room, out _);
            if (course != null) list.Add((room, course));
        }
        return list;
    }

    static readonly string[] Labels = { "COUCH", "TABLE", "STORAGE", "PLANT", "BED", "OTHER" };

    /// <summary>
    /// A room nobody has tested: 4.8-8 m by 3-6 m, turned any way, with 1-5 pieces of furniture (a couch most of the
    /// time). Deterministic for a seed. Null when no slalom fits in it.
    /// </summary>
    public static (MockFloorPlan room, MockCourse course) Random(int seed)
    {
        var rng = new System.Random(seed);
        float U(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        var room = new MockFloorPlan($"random_{seed}", new Vector2(U(4.8f, 8f), U(3f, 6f)), yaw: U(-180f, 180f));
        int items = 1 + rng.Next(5);
        for (int i = 0; i < items; i++)
        {
            string label = i == 0 && rng.NextDouble() < 0.8 ? "COUCH" : Labels[1 + rng.Next(Labels.Length - 1)];
            var size = label == "COUCH" ? new Vector3(U(1.6f, 2.6f), 0.8f, U(0.8f, 1.0f))
                : label == "BED" ? new Vector3(U(1.2f, 1.8f), 0.55f, U(1.9f, 2.1f))
                : new Vector3(U(0.4f, 1.2f), U(0.4f, 1.8f), U(0.4f, 0.8f));
            // Mostly against a wall, like real furniture.
            float x = U(-0.5f, 0.5f) * room.Size.x, z = U(-0.5f, 0.5f) * room.Size.y;
            if (rng.NextDouble() < 0.7)
            {
                if (rng.NextDouble() < 0.5) z = Math.Sign(z == 0 ? 1 : z) * (room.Size.y * 0.5f - size.z * 0.5f - 0.05f);
                else x = Math.Sign(x == 0 ? 1 : x) * (room.Size.x * 0.5f - size.x * 0.5f - 0.05f);
            }
            room.Add(label, new Vector2(x, z), size, rng.NextDouble() < 0.8 ? 0f : U(-30f, 30f));
        }
        bool hasCouch = room.Items.Exists(item => item.Label == "COUCH");
        var course = From(room, out _, TemplateSettings(hasCouch ? "COUCH" : null));
        return (room, course);
    }
}
