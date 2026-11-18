using System;
using System.Collections.Generic;

// The project stage model. Plain C# with no UnityEngine types, so it is checked on its own (a dotnet run in the scratchpad)
// and the Unity side only gathers facts and draws what this decides.
namespace Forge.OS
{
    /// <summary>How a stage stands. <see cref="Current"/> is only what a view shows for the stage the person is at.</summary>
    public enum StageStatus { Locked, Ready, Current, Done, Stale }

    /// <summary>The project kinds with a loop of their own (the same order as <see cref="OSProjectWindow.Loop"/>).</summary>
    public enum StageLoop { Truck, Quadcopter, Rocket, Ml, Generic }

    /// <summary>One stage of a project: its id (the step id the actions and voice use), name, icon, what it opens, the
    /// earlier stages whose output it needs, and whether it is optional (never offered as Next by itself: Code, Publish).</summary>
    public sealed class StageDef
    {
        public readonly string Id, Name, Icon, Opens;
        public readonly string[] Needs;
        public readonly bool Optional;

        public StageDef(string id, string name, string icon, string opens, string[] needs = null, bool optional = false)
        {
            Id = id;
            Name = name;
            Icon = icon;
            Opens = opens;
            Needs = needs ?? Array.Empty<string>();
            Optional = optional;
        }
    }

    /// <summary>
    /// What is really there for one project, gathered by the Unity side from the notebook, the project record, the open
    /// course, the live brain, CAD and the build. Every number is a real one; -1 means unknown.
    /// </summary>
    public sealed class StageFacts
    {
        /// <summary>The design's version: CAD's (0 = none yet), or the truck's saved version (0 = the stock truck, a real design).</summary>
        public int DesignVersion;
        /// <summary>"160 mm arms", "360 mm body", or null.</summary>
        public string DesignLine;
        public bool CourseMade, CourseOpen;
        public string CourseName = "course";
        /// <summary>Laps recorded by hand on this project's course, and the design version they were recorded on (-1 unknown).</summary>
        public int Laps, LapsOnDesign = -1;
        public int LapsNeeded = 2;
        /// <summary>A training run finished for this project; <see cref="DesignChangedSinceTraining"/> when a design version was cut after the newest one.</summary>
        public bool Trained, DesignChangedSinceTraining;
        public string TrainLine;
        /// <summary>How many trainings the project has, and how many there were when the Autopilot last ran (-1 never).</summary>
        public int Trainings, AutopilotOnTrainings = -1;
        public string AutopilotLine;
        /// <summary>The design version the last simulation flew (-1 none).</summary>
        public int SimulatedDesign = -1;
        public string SimLine;
        public bool Launched;
        public string LaunchLine;
        public bool DataOpened, ResultsSeen, PublishAsked, CodeOpened, CadOpened;
        /// <summary>A plain project's plan names a course (it then gets a Course stage).</summary>
        public bool PlanCourse;
        /// <summary>Why a feature is missing in this build or right now (null when it is there).</summary>
        public string DriveOff, BrainOff, LaunchOff, DataOff, TrainOff, PublishOff;
    }

    /// <summary>One stage as last judged: its status, whether the person is at it, its one-line result, and when locked
    /// why and the stage to go to first (null when nothing the person can do unlocks it, like a feature the build lacks).</summary>
    public struct StageState
    {
        public StageDef Def;
        public StageStatus Status;
        public bool IsCurrent, IsNext;
        public string Result, Reason, Unlock;
        public string Id => Def != null ? Def.Id : null;
        /// <summary>Locked, Ready, Current, Done or Stale, as a view shows it.</summary>
        public StageStatus Shown => IsCurrent && Status == StageStatus.Ready ? StageStatus.Current : Status;
        public bool CanGo => Status != StageStatus.Locked;
    }

    /// <summary>
    /// The stage definitions per kind and the rules that judge them. Stages are ordered; a stage needs the output of earlier
    /// ones (a satisfied need is a done or stale stage); Next is the first stage that isn't optional and is ready or stale.
    /// <list type="table">
    /// <item>Truck: Design (always a design: the stock truck), Course, Drive (2 laps), Train, Autopilot, Code (optional).</item>
    /// <item>Quadcopter: Design (a CAD version), Course, Fly (2 laps), Train, Autopilot.</item>
    /// <item>Rocket: Design, Simulate (a flight of the design on screen), Launch (a run of the launch pad).</item>
    /// <item>Image classifier and ML: Data, Train, Results, Publish (optional).</item>
    /// <item>Anything else: Course (when its plan names one), CAD, Train.</item>
    /// </list>
    /// Stale: laps recorded on an older design, a training learned before the design's newest version, an Autopilot run on
    /// an older brain, a simulation of an older design.
    /// </summary>
    public static class ProjectStages
    {
        static readonly string[] None = Array.Empty<string>();
        static readonly string[] NeedDesign = { "design" }, NeedCourse = { "course" }, NeedDrive = { "drive" }, NeedFly = { "fly" },
            NeedTrain = { "train" }, NeedSimulate = { "simulate" }, NeedData = { "data" };

        static readonly StageDef[] truck =
        {
            new StageDef("design", "Design", "cube", "the truck design sliders"),
            new StageDef("course", "Course", "slalom", "the slalom in your room", NeedDesign),
            new StageDef("drive", "Drive", "drive", "the Drive pad, recording 2 laps", NeedCourse),
            new StageDef("train", "Train", "train", "the brain trainer", NeedDrive),
            new StageDef("autopilot", "Autopilot", "truck", "the Autopilot on the course", NeedTrain),
            new StageDef("code", "Code", "code", "the truck's code", null, true),
        };
        static readonly StageDef[] quadcopter =
        {
            new StageDef("design", "Design", "cube", "the quadcopter in CAD"),
            new StageDef("course", "Course", "hoops", "a drone course in your room", NeedDesign),
            new StageDef("fly", "Fly", "quadcopter", "the Drive window as the transmitter", NeedCourse),
            new StageDef("train", "Train", "train", "the drone's brain", NeedFly),
            new StageDef("autopilot", "Autopilot", "quadcopter", "the Autopilot on the course", NeedTrain),
        };
        static readonly StageDef[] rocket =
        {
            new StageDef("design", "Design", "cube", "the rocket in CAD"),
            new StageDef("simulate", "Simulate", "physics", "the design's flight", NeedDesign),
            new StageDef("launch", "Launch", "rocket", "the launch pad in your room", NeedSimulate),
        };
        static readonly StageDef[] ml =
        {
            new StageDef("data", "Data", "table", "the examples on the ML desk"),
            new StageDef("train", "Train", "train", "a training run", NeedData),
            new StageDef("results", "Results", "scoreboard", "what it got right", NeedTrain),
            new StageDef("publish", "Publish", "send", "a pull request (you confirm first)", NeedTrain, true),
        };
        static readonly StageDef[] generic =
        {
            new StageDef("cad", "CAD", "cube", "CAD"),
            new StageDef("train", "Train", "train", "the ML desk"),
        };
        static readonly StageDef[] genericCourse =
        {
            new StageDef("course", "Course", "slalom", "the plan's course"),
            new StageDef("cad", "CAD", "cube", "CAD"),
            new StageDef("train", "Train", "train", "the ML desk"),
        };

        /// <summary>The stages of a kind, in order. The same array every call (nothing is allocated).</summary>
        public static StageDef[] For(StageLoop loop, bool planCourse = false)
        {
            switch (loop)
            {
                case StageLoop.Truck: return truck;
                case StageLoop.Quadcopter: return quadcopter;
                case StageLoop.Rocket: return rocket;
                case StageLoop.Ml: return ml;
                default: return planCourse ? genericCourse : generic;
            }
        }

        /// <summary>The stage with this id in a kind's list, or null.</summary>
        public static StageDef Find(StageLoop loop, string id, bool planCourse = false)
        {
            if (string.IsNullOrEmpty(id)) return null;
            StageDef[] defs = For(loop, planCourse);
            for (int i = 0; i < defs.Length; i++)
                if (defs[i].Id == id) return defs[i];
            return null;
        }

        /// <summary>
        /// Judges every stage of <paramref name="loop"/> from <paramref name="f"/> into <paramref name="into"/> (cleared first)
        /// and returns the index of Next, or -1 when nothing is left to offer. <paramref name="current"/> is the stage the
        /// person is at (null or unknown: Next is current).
        /// </summary>
        public static int Evaluate(StageLoop loop, StageFacts f, string current, List<StageState> into)
        {
            into.Clear();
            StageDef[] defs = For(loop, f.PlanCourse);
            int next = -1;
            for (int i = 0; i < defs.Length; i++)
            {
                StageDef d = defs[i];
                var s = new StageState { Def = d };
                Judge(loop, d, f, out bool done, out bool stale, out s.Result);
                string off = FeatureOff(loop, d.Id, f);
                if (off != null)
                {
                    s.Status = StageStatus.Locked;
                    s.Reason = off;
                }
                else if (!NeedsMet(d, into, f, loop, out s.Reason, out s.Unlock)) s.Status = StageStatus.Locked;
                else s.Status = stale ? StageStatus.Stale : done ? StageStatus.Done : StageStatus.Ready;
                if (next < 0 && !d.Optional && (s.Status == StageStatus.Ready || s.Status == StageStatus.Stale)) next = i;
                into.Add(s);
            }
            int at = IndexOf(into, current);
            if (at < 0) at = next >= 0 ? next : 0;
            if (at < into.Count)
            {
                StageState c = into[at];
                c.IsCurrent = true;
                into[at] = c;
            }
            if (next >= 0)
            {
                StageState n = into[next];
                n.IsNext = true;
                into[next] = n;
            }
            return next;
        }

        /// <summary>Where opening a project lands: the stage the person was at when it can still be gone to, else Next, else the first stage.</summary>
        public static string Resume(List<StageState> judged, string saved, int next)
        {
            int at = IndexOf(judged, saved);
            if (at >= 0 && judged[at].CanGo) return saved;
            if (next >= 0 && next < judged.Count) return judged[next].Id;
            return judged.Count > 0 ? judged[0].Id : null;
        }

        /// <summary>The index of the stage with this id, or -1.</summary>
        public static int IndexOf(List<StageState> judged, string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < judged.Count; i++)
                if (judged[i].Def.Id == id) return i;
            return -1;
        }

        /// <summary>How many stages are done (stale ones count as not done; optional ones count only when done).</summary>
        public static int DoneCount(List<StageState> judged, out int total)
        {
            int done = 0;
            total = 0;
            for (int i = 0; i < judged.Count; i++)
            {
                bool isDone = judged[i].Status == StageStatus.Done;
                if (!judged[i].Def.Optional || isDone) total++;
                if (isDone) done++;
            }
            return done;
        }

        // ---------------------------------------------------------------- the rules

        // Done, stale and the one-line result of one stage.
        static void Judge(StageLoop loop, StageDef d, StageFacts f, out bool done, out bool stale, out string result)
        {
            done = stale = false;
            result = null;
            switch (d.Id)
            {
                case "design":
                    if (loop == StageLoop.Truck)
                    {
                        done = true;  // the truck always has a design: the stock one until a version is saved
                        result = f.DesignVersion > 0 ? "v" + f.DesignVersion + " saved" + Tail(f.DesignLine) : "stock truck" + Tail(f.DesignLine);
                    }
                    else
                    {
                        done = f.DesignVersion > 0;
                        result = done ? "v" + f.DesignVersion + Tail(f.DesignLine) : "no design yet";
                    }
                    break;
                case "course":
                    done = f.CourseMade;
                    result = !done ? "not made yet" : f.CourseOpen ? f.CourseName + " · open" : f.CourseName + " · made";
                    break;
                case "drive":
                case "fly":
                {
                    int laps = f.Laps;
                    done = laps >= f.LapsNeeded || f.Trained;  // a training means its laps were recorded (maybe in an earlier session)
                    // Laps on an older design teach the brain the old one: fly (drive) the new version first.
                    stale = done && (laps > 0 && f.LapsOnDesign >= 0 && DesignKnown(loop, f)
                        ? f.LapsOnDesign != f.DesignVersion
                        : f.Trained && f.DesignChangedSinceTraining);
                    string verb = d.Id == "fly" ? "flown" : "driven";
                    if (laps > 0) result = laps + (laps == 1 ? " lap " : " laps ") + verb + (stale && f.LapsOnDesign >= 0 ? " on v" + f.LapsOnDesign + " · design is v" + f.DesignVersion : "");
                    else result = stale ? "laps were on the old design" : f.Trained ? "laps recorded" : "no laps yet";
                    break;
                }
                case "train":
                    done = f.Trained;
                    stale = f.Trained && f.DesignChangedSinceTraining && loop != StageLoop.Ml && loop != StageLoop.Generic;
                    result = !f.Trained ? "not trained yet" : stale ? "learned on the old design" : Clip(f.TrainLine ?? "trained", 48);
                    break;
                case "autopilot":
                    done = f.AutopilotOnTrainings >= 0;
                    stale = done && (f.DesignChangedSinceTraining || f.AutopilotOnTrainings < f.Trainings);
                    result = !done ? "not run yet"
                        : f.DesignChangedSinceTraining ? "brain learned on the old design"
                        : f.AutopilotOnTrainings < f.Trainings ? "not run on the newest brain"
                        : Clip(f.AutopilotLine ?? "ran", 48);
                    break;
                case "code":
                    done = f.CodeOpened;
                    result = done ? "opened" : "optional";
                    break;
                case "simulate":
                    done = f.SimulatedDesign > 0;
                    stale = done && f.DesignVersion > 0 && f.SimulatedDesign != f.DesignVersion;
                    result = !done ? "not simulated yet" : stale ? "flew v" + f.SimulatedDesign + " · design is v" + f.DesignVersion : f.SimLine ?? "simulated";
                    break;
                case "launch":
                    done = f.Launched;
                    result = done ? f.LaunchLine ?? "launched" : "not launched yet";
                    break;
                case "data":
                    done = f.DataOpened || f.Trained;
                    result = done ? "looked at" : "not opened yet";
                    break;
                case "results":
                    done = f.ResultsSeen;
                    result = f.Trained ? Clip(f.TrainLine ?? "trained", 48) : "no training run yet";
                    break;
                case "publish":
                    done = f.PublishAsked;
                    result = done ? "asked to publish" : "optional";
                    break;
                case "cad":
                    done = f.CadOpened;
                    result = done ? "opened" : "not opened yet";
                    break;
            }
        }

        // The truck's stock design is version 0 and real; a CAD design is known once it has a version.
        static bool DesignKnown(StageLoop loop, StageFacts f) => loop == StageLoop.Truck || f.DesignVersion > 0;

        // Why a stage can't run in this build or right now, or null.
        static string FeatureOff(StageLoop loop, string id, StageFacts f)
        {
            switch (id)
            {
                case "drive":
                case "fly": return f.DriveOff;
                case "train":
                    if (loop == StageLoop.Quadcopter) return f.BrainOff;
                    if (loop == StageLoop.Ml) return f.TrainOff;
                    return null;
                case "autopilot": return loop == StageLoop.Quadcopter ? f.BrainOff : null;
                case "launch": return f.LaunchOff;
                case "data": return f.DataOff;
                case "publish": return f.PublishOff;
                default: return null;
            }
        }

        // Every need is a done or stale stage; otherwise why not and which stage to go to first (null when the first unmet
        // need is itself off with nothing to do about it).
        static bool NeedsMet(StageDef d, List<StageState> earlier, StageFacts f, StageLoop loop, out string reason, out string unlock)
        {
            reason = unlock = null;
            for (int n = 0; n < d.Needs.Length; n++)
            {
                int at = IndexOf(earlier, d.Needs[n]);
                if (at < 0) continue;  // a need the kind doesn't have (never in the tables above)
                StageState need = earlier[at];
                if (need.Status == StageStatus.Done || need.Status == StageStatus.Stale) continue;
                reason = d.Name + " needs " + NeedWords(need.Def.Id, f, loop) + ".";
                if (need.Status == StageStatus.Locked)
                {
                    unlock = need.Unlock;
                    if (unlock == null && need.Reason != null) reason = need.Def.Name + " is off: " + Lower(need.Reason);
                }
                else unlock = need.Def.Id;
                return false;
            }
            return true;
        }

        // What a need gives, in words: "a design first", "2 laps flown (1 so far)".
        static string NeedWords(string id, StageFacts f, StageLoop loop)
        {
            switch (id)
            {
                case "design": return "a design first";
                case "course": return "a course first";
                case "drive":
                case "fly":
                    return f.LapsNeeded + " laps " + (id == "fly" ? "flown" : "driven") + (f.Laps > 0 ? " (" + f.Laps + " so far)" : " first");
                case "train": return loop == StageLoop.Ml ? "a training run first" : "a trained brain first";
                case "simulate": return "a simulation first";
                case "data": return "a look at the data first";
                default: return "the step before";
            }
        }

        // ---------------------------------------------------------------- words

        /// <summary>
        /// The stage a word means for this kind: "design", "the design stage", "cad", "fly", "flying", "laps", "sim"... A truck's
        /// "fly" is its Drive and a drone's "drive" is its Fly. Null when it names no stage of the kind.
        /// </summary>
        public static string StageFor(StageLoop loop, string said, bool planCourse = false)
        {
            if (string.IsNullOrWhiteSpace(said)) return null;
            string s = " " + said.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ') + " ";
            foreach (string filler in new[] { " the ", " stage ", " step ", " go to ", " back to ", " open ", " please ", " my ", " a " })
                s = s.Replace(filler, " ");
            s = s.Trim();
            string id;
            switch (s)
            {
                case "design": case "designing": case "cad": case "parameters": case "params": case "shape": case "build": id = "design"; break;
                case "course": case "track": case "slalom": case "hoops": case "hoop course": case "test": id = "course"; break;
                case "drive": case "driving": case "fly": case "flying": case "flight": case "laps": case "record": case "pad": id = loop == StageLoop.Quadcopter ? "fly" : "drive"; break;
                case "train": case "training": case "learn": case "brain": id = "train"; break;
                case "autopilot": case "auto": case "self drive": case "self driving": id = "autopilot"; break;
                case "code": case "coding": id = "code"; break;
                case "simulate": case "simulation": case "sim": id = "simulate"; break;
                case "launch": case "launching": case "launch pad": id = "launch"; break;
                case "data": case "dataset": case "examples": id = "data"; break;
                case "results": case "result": case "score": id = "results"; break;
                case "publish": case "github": case "share": id = "publish"; break;
                default: id = s; break;
            }
            if (loop == StageLoop.Generic && id == "design") id = "cad";
            if (loop == StageLoop.Rocket && id == "fly") id = "launch";
            return Find(loop, id, planCourse) != null ? id : null;
        }

        /// <summary>What to do while at a stage that isn't done yet, in one line (the rail and the overview show it).</summary>
        public static string DoingLine(StageLoop loop, string id, StageFacts f)
        {
            switch (id)
            {
                case "design": return loop == StageLoop.Truck ? "Move a slider, then Save as version to keep it." : "Change a slider in CAD: each change is a new version.";
                case "course": return "Tap Allow if the course asks, and it appears in your room.";
                case "drive": return "Drive " + f.LapsNeeded + " laps around the cones with the pad (" + f.Laps + " so far).";
                case "fly": return "Take off, fly " + f.LapsNeeded + " laps through the hoops, then land (" + f.Laps + " so far).";
                case "train": return "The brain learns from your laps. It takes a few seconds.";
                case "autopilot": return loop == StageLoop.Quadcopter ? "Watch the Autopilot fly the course by itself." : "Watch the Autopilot drive the course by itself.";
                case "code": return "The truck's code is open.";
                case "simulate": return "See how high this version flies and where it lands.";
                case "launch": return "Tap Launch on the pad and watch it fly.";
                case "data": return "Look through the examples it will learn from.";
                case "results": return "See what it got right and wrong.";
                case "publish": return "Albie reads back what would go on GitHub; nothing is pushed until you say yes.";
                default: return null;
            }
        }

        /// <summary>What finishing a stage gave, in a short line ("2 laps driven.").</summary>
        public static string DoneLine(StageState s)
        {
            if (s.Def == null) return "";
            switch (s.Def.Id)
            {
                case "design": return "The design is v" + DigitsOf(s.Result) + ".";
                case "course": return "The course is made.";
                case "drive": return "Laps driven.";
                case "fly": return "Laps flown.";
                case "train": return "The brain is trained.";
                case "autopilot": return "The Autopilot ran.";
                case "simulate": return "Simulated.";
                case "launch": return "Launched.";
                case "data": return "Data looked at.";
                case "results": return "Results seen.";
                default: return s.Def.Name + " done.";
            }
        }

        static string DigitsOf(string result)
        {
            if (string.IsNullOrEmpty(result) || result[0] != 'v') return "?";
            int end = 1;
            while (end < result.Length && char.IsDigit(result[end])) end++;
            return end > 1 ? result.Substring(1, end - 1) : "?";
        }

        static string Tail(string line) => string.IsNullOrEmpty(line) ? "" : " · " + line;

        static string Lower(string text) => string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text.Substring(1);

        /// <summary>Text cut to <paramref name="max"/> characters with "..." (one line).</summary>
        public static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace('\n', ' ').Trim();
            return text.Length <= max ? text : text.Substring(0, max - 3).TrimEnd() + "...";
        }
    }
}
