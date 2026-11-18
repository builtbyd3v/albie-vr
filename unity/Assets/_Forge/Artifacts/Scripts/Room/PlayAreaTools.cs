using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Albie's play area tools (<c>Resources/AlbieTools/play_area.json</c>): <c>play_area_set {mode}</c> chooses where room
    /// courses may go (room, draw, quest, auto) and lays the open course out again; <c>play_area_show</c> opens the Play
    /// area panel and says what is used now; <c>course_to_desk</c> puts the open floor course on the table, smaller.
    /// Results carry <c>say</c>: what to tell the person.
    /// </summary>
    public static class PlayAreaTools
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => AlbieTools.Register("play_area", new Dictionary<string, AlbieTools.Handler>
        {
            ["play_area_set"] = Set,
            ["play_area_show"] = Show,
            ["course_to_desk"] = ToDesk,
        });

        public static JObject Set(JObject args)
        {
            string text = args["mode"]?.Type == JTokenType.String ? (string)args["mode"] : null;
            if (!UserPlayArea.TryParse(text, out var mode))
                throw new ArgumentException("bad_value: mode is \"room\" (the room scan's whole clear floor), \"draw\" (draw an area on the floor), \"quest\" (the Quest boundary) or \"auto\"");
            var room = PlayAreaEditor.CurrentRoom();
            if (room == null || !room.HasFloor)
                throw new ArgumentException("no_room_data: there's no room scan yet, so there's no floor to choose from. Ask the person to run Space Setup on the headset.");
            if (mode == UserPlayArea.Mode.Draw)
            {
                PlayAreaEditor.Open(room, true);
                return new JObject
                {
                    ["ok"] = true, ["mode"] = "draw", ["drawing"] = true,
                    ["say"] = "Point at the floor and pinch to drop each corner, or walk to a corner and tap Corner here. Pinch a corner to drag it. Tap Done when it's right, and I'll lay the course out in it.",
                };
            }
            UserPlayArea.SetMode(room, mode);
            var result = new JObject { ["ok"] = true, ["mode"] = UserPlayArea.Name(mode), ["area"] = UserPlayArea.Describe(room) };
            string say = mode == UserPlayArea.Mode.Room ? "Courses can use your whole room now. " + UserPlayArea.Safety
                : mode == UserPlayArea.Mode.Quest ? "Courses stay inside your Quest boundary now."
                : "Courses use your Quest boundary when they fit inside it, else your room's clear floor.";
            Relayout(result, ref say);
            result["say"] = say;
            return result;
        }

        public static JObject Show(JObject args)
        {
            var room = PlayAreaEditor.CurrentRoom();
            if (room == null || !room.HasFloor)
                throw new ArgumentException("no_room_data: there's no room scan yet, so there's no play area to show. Ask the person to run Space Setup on the headset.");
            PlayAreaEditor.Open(room, false);
            var mode = UserPlayArea.ModeOf(room);
            var drawn = UserPlayArea.Drawn(room);
            var quest = UserPlayArea.Quest(room);
            return new JObject
            {
                ["ok"] = true,
                ["mode"] = UserPlayArea.Name(mode),
                ["area"] = UserPlayArea.Describe(room),
                ["quest_boundary_m2"] = quest != null ? Math.Round(PlayAreaShape.Area(quest), 1) : (JToken)JValue.CreateNull(),
                ["drawn_m2"] = drawn != null ? Math.Round(PlayAreaShape.Area(drawn), 1) : (JToken)JValue.CreateNull(),
                ["say"] = UserPlayArea.Describe(room) + " The Play area panel is open: they can use the whole room, draw an area, or keep the Quest boundary.",
            };
        }

        public static JObject ToDesk(JObject args)
        {
            var host = ArtifactHost.Current;
            if (host == null || !host.HasRoomCourse)
                throw new ArgumentException("no_course: no room course is open. Make one with artifact_create and surface \"desk\" instead.");
            host.RetryLayout(true);
            if (host.LastProblem != null) return new JObject { ["ok"] = false, ["say"] = host.LastProblem };
            return new JObject { ["ok"] = true, ["scale"] = Math.Round(host.CourseScale, 3), ["say"] = host.LayoutMessage };
        }

        // Lays the open course out in the new area and adds what happened to what Albie says.
        static void Relayout(JObject result, ref string say)
        {
            var host = ArtifactHost.Current;
            if (host == null || !host.HasRoomCourse) return;
            host.RetryLayout(false);
            result["relaid"] = true;
            string after = host.LastProblem ?? host.LayoutMessage;
            if (!string.IsNullOrEmpty(after)) say += " " + after;
            else if (host.LayoutReused) say += " The course still fits where it was.";
            else say += " I laid the course out again.";
        }
    }
}
