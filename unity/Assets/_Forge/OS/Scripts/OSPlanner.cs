using System;
using System.Collections.Generic;
using System.Text;

namespace Forge.OS
{
    /// <summary>
    /// The planner behind Build something: a small question tree held as data, with no UI and no Unity calls, so it works
    /// offline, with voice off, and a check can walk every path. The first question asks what is being built (RC truck,
    /// Drone, Rocket, A model that recognises things, Something else); each kind then asks two more (what it should be best
    /// at, and how it is tested). That is three questions at most, each with two to four answers (the first has five).
    /// <see cref="Answer"/> advances; after the last answer <see cref="Result"/> holds a <see cref="Plan"/> with a goal,
    /// parts, a test and milestones as plain strings. <see cref="Match"/> turns a spoken answer into an answer index, so
    /// speaking an option counts as tapping it. <see cref="Validate"/> walks every path and returns what is wrong with the
    /// tree (an empty list means it is sound). Nothing here reads or writes any file.
    /// </summary>
    public sealed class OSPlanner
    {
        /// <summary>What the user is building.</summary>
        public enum Kind { Truck, Drone, Rocket, Classifier, Other }

        /// <summary>What a spoken word means on the plan card.</summary>
        public enum Command { None, Start, Change, Cancel }

        /// <summary>One answer to a question. An answer with no <see cref="next"/> ends the path in a plan.</summary>
        public sealed class Choice
        {
            /// <summary>Stable within its question ("quick", "slalom").</summary>
            public string id;
            /// <summary>What the button says.</summary>
            public string text;
            /// <summary>Words that, when spoken, pick this answer (matched as whole words).</summary>
            public string[] words;
            /// <summary>Id of the next question, or null when this answer ends the path.</summary>
            public string next;
            /// <summary>A phrase that goes into the goal ("that is small and quick"); null for none.</summary>
            public string goal;
            /// <summary>The test, if this answer sets it; the last one on a path wins.</summary>
            public string test;
            public string[] parts, milestones;
            /// <summary>Only on the first question's answers.</summary>
            public Kind? kind;
        }

        /// <summary>One question card.</summary>
        public sealed class Question
        {
            public string id, text;
            public Choice[] choices;
            /// <summary>The answer a free-text ("something else") reply follows the path of.</summary>
            public int freeIndex;
        }

        /// <summary>What the plan card shows, and what is saved when the user taps Start.</summary>
        public sealed class Plan
        {
            public Kind kind;
            /// <summary>"RC truck", "Drone", "Rocket", "Image model", "Your own idea".</summary>
            public string kindTitle;
            public string goal, test;
            public List<string> parts = new List<string>();
            public List<string> milestones = new List<string>();
            /// <summary>The questions asked and the answers given, in order (same length).</summary>
            public List<string> questions = new List<string>();
            public List<string> answers = new List<string>();
            /// <summary>One sentence for the notebook: what was planned and what the user chose.</summary>
            public string reason;
            /// <summary>The part of a new project id after "proj_" ("my_drone"); the truck uses proj_truck instead.</summary>
            public string projectBase;
            /// <summary>The sentence that offers the first step once the project window is open.</summary>
            public string firstStep;
            public bool IsTruck => kind == Kind.Truck;
        }

        /// <summary>No path asks more questions than this, the first included.</summary>
        public const int MaxQuestions = 3;
        /// <summary>The longest a free-text answer is kept.</summary>
        public const int MaxFreeText = 80;

        const string FirstId = "kind";

        static readonly List<Question> all = new List<Question>();
        static readonly Dictionary<string, Question> tree = BuildTree();

        // ---------------------------------------------------------------- one conversation

        Question current;
        Plan result;
        readonly List<Choice> picked = new List<Choice>();
        readonly List<string> askedText = new List<string>();
        readonly List<string> pickedText = new List<string>();
        readonly List<string> freeText = new List<string>();

        public OSPlanner() => Reset();

        /// <summary>The question to ask now, or null once there is a plan.</summary>
        public Question Current => current;
        /// <summary>The finished plan, or null while questions remain.</summary>
        public Plan Result => result;
        public bool Done => result != null;
        /// <summary>How many questions have been answered.</summary>
        public int Asked => picked.Count;
        /// <summary>The number of the question to ask now (1 for the first).</summary>
        public int Number => picked.Count + 1;
        /// <summary>The first question of the tree.</summary>
        public static Question First => tree[FirstId];

        /// <summary>Back to the first question, keeping nothing.</summary>
        public void Reset()
        {
            current = First;
            result = null;
            picked.Clear();
            askedText.Clear();
            pickedText.Clear();
            freeText.Clear();
        }

        /// <summary>Takes answer number <paramref name="index"/> (from 0) of the current question. False when there is none to answer.</summary>
        public bool Answer(int index)
        {
            if (current == null || index < 0 || index >= current.choices.Length) return false;
            Take(current.choices[index], current.choices[index].text);
            return true;
        }

        /// <summary>
        /// Takes the user's own words as the answer to the current question: the path follows the question's free answer,
        /// and the words are kept (clipped) and added to the goal. False when there are no words or no question.
        /// </summary>
        public bool AnswerFree(string words)
        {
            if (current == null) return false;
            string said = Clip((words ?? "").Trim());
            if (said.Length == 0) return false;
            int index = Math.Max(0, Math.Min(current.freeIndex, current.choices.Length - 1));
            freeText.Add(said);
            Take(current.choices[index], said);
            return true;
        }

        void Take(Choice choice, string shown)
        {
            askedText.Add(current.text);
            picked.Add(choice);
            pickedText.Add(shown);
            Question next = null;
            if (choice.next != null && picked.Count < MaxQuestions) tree.TryGetValue(choice.next, out next);
            if (next != null)
            {
                current = next;
                return;
            }
            current = null;
            result = Compose(picked, askedText, pickedText, freeText);
        }

        static string Clip(string text) => text.Length <= MaxFreeText ? text : text.Substring(0, MaxFreeText - 3).TrimEnd() + "...";

        // ---------------------------------------------------------------- the plan

        static Plan Compose(List<Choice> path, List<string> asked, List<string> shown, List<string> free)
        {
            var plan = new Plan();
            plan.kind = path.Count > 0 && path[0].kind.HasValue ? path[0].kind.Value : Kind.Other;

            var goal = new StringBuilder();
            string test = null;
            for (int i = 0; i < path.Count; i++)
            {
                Choice c = path[i];
                if (!string.IsNullOrWhiteSpace(c.goal))
                {
                    if (goal.Length > 0) goal.Append(' ');
                    goal.Append(c.goal.Trim());
                }
                if (!string.IsNullOrWhiteSpace(c.test)) test = c.test.Trim();
                AddDistinct(plan.parts, c.parts);
                AddDistinct(plan.milestones, c.milestones);
            }
            string text = goal.ToString().TrimEnd('.', ' ');
            if (text.Length > 0) text += ".";
            if (free.Count > 0 && text.Length > 0) text += " In my words: \"" + string.Join("; ", free) + "\".";
            plan.goal = text;
            plan.test = test ?? "";

            plan.questions.AddRange(asked);
            plan.answers.AddRange(shown);
            plan.reason = "Planned with Albie. The answers were: " + string.Join(", then ", shown) + ".";

            switch (plan.kind)
            {
                case Kind.Truck:
                    plan.kindTitle = "RC truck";
                    plan.projectBase = "truck";
                    plan.firstStep = "Next step: tap Course to make the course for the truck.";
                    break;
                case Kind.Drone:
                    plan.kindTitle = "Drone";
                    plan.projectBase = "my_drone";
                    plan.firstStep = "Next step: tap CAD to start designing the frame.";
                    break;
                case Kind.Rocket:
                    plan.kindTitle = "Rocket";
                    plan.projectBase = "my_rocket";
                    plan.firstStep = "Next step: tap CAD to start designing the rocket.";
                    break;
                case Kind.Classifier:
                    plan.kindTitle = "Image model";
                    plan.projectBase = "my_model";
                    plan.firstStep = "Next step: tap Train to start collecting examples.";
                    break;
                default:
                    plan.kindTitle = "Your own idea";
                    plan.projectBase = "my_project";
                    plan.firstStep = "Next step: tap Notebook and write down your first step.";
                    break;
            }
            return plan;
        }

        static void AddDistinct(List<string> into, string[] items)
        {
            if (items == null) return;
            for (int i = 0; i < items.Length; i++)
            {
                string item = items[i];
                if (string.IsNullOrWhiteSpace(item)) continue;
                item = item.Trim();
                bool seen = false;
                for (int j = 0; j < into.Count; j++)
                    if (string.Equals(into[j], item, StringComparison.Ordinal)) { seen = true; break; }
                if (!seen) into.Add(item);
            }
        }

        // ---------------------------------------------------------------- spoken answers

        /// <summary>Lower case, letters and digits only, single spaces ("Right 8 times in 10!" becomes "right 8 times in 10").</summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            bool gap = true;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    gap = false;
                }
                else if (!gap)
                {
                    sb.Append(' ');
                    gap = true;
                }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Which answer of <paramref name="question"/> a spoken (or typed) reply means, from 0, or -1 when it is none or
        /// more than one. In order: its number ("two", "the second one"), the answer's own words, an answer whose whole text
        /// is inside what was said (the longest), then the answer with the most of its keywords in what was said.
        /// </summary>
        public static int Match(Question question, string spoken)
        {
            if (question == null || question.choices == null) return -1;
            string said = Normalize(spoken);
            if (said.Length == 0) return -1;

            int ordinal = Ordinal(said, question.choices.Length);
            if (ordinal >= 0) return ordinal;

            for (int i = 0; i < question.choices.Length; i++)
                if (Normalize(question.choices[i].text) == said) return i;

            string padded = " " + said + " ";
            int whole = -1, wholeLength = 0;
            for (int i = 0; i < question.choices.Length; i++)
            {
                string t = Normalize(question.choices[i].text);
                if (t.Length > wholeLength && padded.IndexOf(" " + t + " ", StringComparison.Ordinal) >= 0)
                {
                    whole = i;
                    wholeLength = t.Length;
                }
            }
            if (whole >= 0) return whole;

            int bestScore = 0, bestIndex = -1;
            bool tie = false;
            for (int i = 0; i < question.choices.Length; i++)
            {
                string[] words = question.choices[i].words;
                int score = 0;
                if (words != null)
                    for (int w = 0; w < words.Length; w++)
                    {
                        string n = Normalize(words[w]);
                        if (n.Length > 0 && padded.IndexOf(" " + n + " ", StringComparison.Ordinal) >= 0) score++;
                    }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                    tie = false;
                }
                else if (score == bestScore && score > 0)
                {
                    tie = true;
                }
            }
            return bestScore > 0 && !tie ? bestIndex : -1;
        }

        static int Ordinal(string said, int count)
        {
            string s = said;
            string[] lead = { "the ", "number ", "option ", "choice ", "answer " };
            bool again = true;
            while (again)
            {
                again = false;
                for (int i = 0; i < lead.Length; i++)
                    if (s.StartsWith(lead[i], StringComparison.Ordinal))
                    {
                        s = s.Substring(lead[i].Length);
                        again = true;
                    }
            }
            if (s.EndsWith(" one", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 4);
            if (s.EndsWith(" please", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 7);
            int n;
            switch (s)
            {
                case "1": case "one": case "first": n = 1; break;
                case "2": case "two": case "second": n = 2; break;
                case "3": case "three": case "third": n = 3; break;
                case "4": case "four": case "fourth": n = 4; break;
                case "5": case "five": case "fifth": n = 5; break;
                default: return -1;
            }
            return n <= count ? n - 1 : -1;
        }

        /// <summary>What a spoken reply means on the plan card: Change and Cancel win over Start when words of both are there.</summary>
        public static Command ParseCommand(string spoken)
        {
            string said = Normalize(spoken);
            if (said.Length == 0) return Command.None;
            string padded = " " + said + " ";
            if (HasAny(padded, "change", "start over", "again", "redo", "different", "back")) return Command.Change;
            if (HasAny(padded, "cancel", "never mind", "nevermind", "forget it", "stop", "no", "not now")) return Command.Cancel;
            if (HasAny(padded, "start", "go", "go ahead", "yes", "yeah", "yep", "do it", "ok", "okay", "begin", "confirm", "sounds good", "let s go", "lets go")) return Command.Start;
            return Command.None;
        }

        static bool HasAny(string padded, params string[] words)
        {
            for (int i = 0; i < words.Length; i++)
                if (padded.IndexOf(" " + words[i] + " ", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // ---------------------------------------------------------------- the check

        /// <summary>
        /// Walks every path of the tree and returns what is wrong, one sentence each, naming the path ("truck > quick > ...").
        /// An empty list means: every path ends in a plan within <see cref="MaxQuestions"/> questions, no text is empty, no
        /// answer id repeats in a question, every question has two to four answers (the first has five), every question is
        /// reachable, a spoken answer never picks the wrong button, and every plan has a goal, a test, parts and milestones.
        /// </summary>
        public static List<string> Validate()
        {
            var problems = new List<string>();
            if (!tree.TryGetValue(FirstId, out Question first))
            {
                problems.Add("There is no first question.");
                return problems;
            }
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < all.Count; i++)
                if (!seenIds.Add(all[i].id ?? "")) problems.Add("Two questions share the id '" + all[i].id + "'.");

            var reached = new HashSet<string>(StringComparer.Ordinal);
            Walk(first, 1, new List<Choice>(), new List<string>(), "", problems, reached);
            for (int i = 0; i < all.Count; i++)
                if (!reached.Contains(all[i].id)) problems.Add("The question '" + all[i].id + "' is never asked.");
            return problems;
        }

        /// <summary>How many different paths end in a plan (for a check's note).</summary>
        public static int PathCount() => Count(First, 1);

        static int Count(Question q, int depth)
        {
            int n = 0;
            for (int i = 0; i < q.choices.Length; i++)
            {
                string next = q.choices[i].next;
                if (next != null && depth < MaxQuestions && tree.TryGetValue(next, out Question nq)) n += Count(nq, depth + 1);
                else n++;
            }
            return n;
        }

        static void Walk(Question q, int depth, List<Choice> path, List<string> asked, string label, List<string> problems, HashSet<string> reached)
        {
            string where = label.Length == 0 ? q.id : label;
            reached.Add(q.id);
            if (depth > MaxQuestions)
            {
                problems.Add(where + " asks more than " + MaxQuestions + " questions.");
                return;
            }
            if (string.IsNullOrWhiteSpace(q.text)) problems.Add(where + " has a question with no text.");
            if (q.choices == null || q.choices.Length == 0)
            {
                problems.Add(where + " has no answers.");
                return;
            }
            if (depth == 1)
            {
                if (q.choices.Length != 5) problems.Add("The first question has " + q.choices.Length + " answers; it must have five.");
            }
            else if (q.choices.Length < 2 || q.choices.Length > 4)
            {
                problems.Add(where + " has " + q.choices.Length + " answers; a question has two to four.");
            }
            if (q.freeIndex < 0 || q.freeIndex >= q.choices.Length) problems.Add(where + " has a free answer index that is out of range.");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < q.choices.Length; i++)
            {
                Choice c = q.choices[i];
                if (string.IsNullOrWhiteSpace(c.id)) problems.Add(where + " has an answer with no id.");
                else if (!ids.Add(c.id)) problems.Add(where + " has two answers with the id '" + c.id + "'.");
                if (string.IsNullOrWhiteSpace(c.text)) problems.Add(where + " > " + c.id + " has no text.");
                if (c.words == null || c.words.Length == 0) problems.Add(where + " > " + c.id + " has no spoken words.");
                else
                    for (int w = 0; w < c.words.Length; w++)
                        if (string.IsNullOrWhiteSpace(c.words[w])) problems.Add(where + " > " + c.id + " has an empty spoken word.");

                // A spoken answer picks its own button: the text does, and no keyword picks another button.
                int own = Match(q, c.text);
                if (own != i) problems.Add(where + " > " + c.id + ": saying its text picks answer " + (own + 1) + ", not " + (i + 1) + ".");
                if (c.words != null)
                    for (int w = 0; w < c.words.Length; w++)
                    {
                        int m = Match(q, c.words[w]);
                        if (m >= 0 && m != i) problems.Add(where + " > " + c.id + ": the word '" + c.words[w] + "' picks answer " + (m + 1) + ".");
                    }
            }

            for (int i = 0; i < q.choices.Length; i++)
            {
                Choice c = q.choices[i];
                string next = label.Length == 0 ? c.id : label + " > " + c.id;
                path.Add(c);
                asked.Add(q.text);
                if (c.next == null)
                {
                    CheckPlan(next, path, asked, problems);
                }
                else if (!tree.TryGetValue(c.next, out Question nq))
                {
                    problems.Add(next + " leads to the question '" + c.next + "', which does not exist.");
                }
                else if (depth >= MaxQuestions)
                {
                    problems.Add(next + " asks a " + (depth + 1) + "th question; at most " + MaxQuestions + " are allowed.");
                }
                else
                {
                    Walk(nq, depth + 1, path, asked, next, problems, reached);
                }
                path.RemoveAt(path.Count - 1);
                asked.RemoveAt(asked.Count - 1);
            }
        }

        static void CheckPlan(string where, List<Choice> path, List<string> asked, List<string> problems)
        {
            var shown = new List<string>(path.Count);
            for (int i = 0; i < path.Count; i++) shown.Add(path[i].text);
            Plan plan = Compose(path, asked, shown, new List<string>());
            if (!path[0].kind.HasValue) problems.Add(where + " > plan: the first answer has no kind.");
            if (string.IsNullOrWhiteSpace(plan.goal)) problems.Add(where + " > plan has no goal.");
            if (string.IsNullOrWhiteSpace(plan.test)) problems.Add(where + " > plan has no test.");
            if (plan.parts.Count == 0) problems.Add(where + " > plan has no parts.");
            if (plan.milestones.Count == 0) problems.Add(where + " > plan has no milestones.");
            if (plan.milestones.Count > 4) problems.Add(where + " > plan has " + plan.milestones.Count + " milestones; the project window shows four.");
            if (string.IsNullOrWhiteSpace(plan.reason) || string.IsNullOrWhiteSpace(plan.projectBase) || string.IsNullOrWhiteSpace(plan.firstStep))
                problems.Add(where + " > plan is missing its reason, project name or first step.");
        }

        // ---------------------------------------------------------------- the tree

        static Choice C(string id, string text, string words, string next = null, string goal = null, string test = null,
            string parts = null, string milestones = null, Kind? kind = null)
        {
            return new Choice
            {
                id = id,
                text = text,
                words = Split(words, ','),
                next = next,
                goal = goal,
                test = test,
                parts = Split(parts, '|'),
                milestones = Split(milestones, '|'),
                kind = kind,
            };
        }

        static string[] Split(string joined, char separator)
        {
            if (string.IsNullOrEmpty(joined)) return null;
            string[] pieces = joined.Split(separator);
            for (int i = 0; i < pieces.Length; i++) pieces[i] = pieces[i].Trim();
            return pieces;
        }

        static void Q(Dictionary<string, Question> into, string id, string text, int freeIndex, params Choice[] choices)
        {
            var question = new Question { id = id, text = text, choices = choices, freeIndex = freeIndex };
            all.Add(question);
            into[id] = question;
        }

        static Dictionary<string, Question> BuildTree()
        {
            var t = new Dictionary<string, Question>(StringComparer.Ordinal);

            Q(t, FirstId, "What are we building?", 4,
                C("truck", "RC truck", "truck,rc,car,rover,vehicle,lorry", "truck_size",
                    goal: "Build a self-driving RC truck",
                    parts: "The truck and its controller|A course in my room",
                    milestones: "Set up the course|Drive two practice runs by hand", kind: Kind.Truck),
                C("drone", "Drone", "drone,quadcopter,quad,copter,fly,flying,aircraft", "drone_job",
                    goal: "Build a small drone",
                    parts: "Frame|Four motors and propellers|Flight controller|Battery",
                    milestones: "Design the frame in CAD", kind: Kind.Drone),
                C("rocket", "Rocket", "rocket,missile,launch,space", "rocket_aim",
                    goal: "Design a model rocket",
                    parts: "Body tube|Nose cone|Fins|Motor mount",
                    milestones: "Design the rocket in CAD", kind: Kind.Rocket),
                C("model", "A model that recognises things", "recognise,recognises,recognize,recognizes,recognition,classifier,classify,neural,network,image,images,machine learning,ml", "model_what",
                    goal: "Train a model",
                    parts: "Labelled pictures|A small neural network|Test pictures it has never seen",
                    milestones: "Collect and label examples", kind: Kind.Classifier),
                C("other", "Something else", "something else,other,else,different,another", "other_kind",
                    goal: "Build my own project",
                    parts: "A written sketch of the idea",
                    milestones: "Sketch it and list the parts", kind: Kind.Other));

            // ---- truck
            Q(t, "truck_size", "How big and how fast should the truck be?", 2,
                C("quick", "Small and quick", "small,quick,little,fast,light,speedy", "truck_test",
                    goal: "that is small and quick", parts: "Light frame|Fast motor"),
                C("steady", "Big and steady", "big,large,steady,heavy,slow,sturdy", "truck_test",
                    goal: "that is big and steady", parts: "Large frame|Strong, slow motor"),
                C("middle", "Somewhere in between", "between,middle,medium,balanced,normal,average,either", "truck_test",
                    goal: "that is a middle size at a middle speed", parts: "Mid-size frame|Standard motor"));
            Q(t, "truck_test", "How do we test it?", 0,
                C("slalom", "Slalom around my couch", "slalom,couch,sofa,cones,weave",
                    test: "Drive the slalom around my couch on its own without hitting a cone.",
                    milestones: "Train the controller on those runs|The autopilot passes the slalom around my couch"),
                C("dash", "Dash across the room", "dash,straight,across,race,sprint",
                    test: "Cross the room in a straight line on its own in under ten seconds.",
                    milestones: "Train the controller on those runs|The autopilot makes the dash across the room"),
                C("park", "Park on a marked spot", "park,parking,spot,mark,marked",
                    test: "Drive to a marked spot on the floor and stop inside it, three times out of three.",
                    milestones: "Train the controller on those runs|The autopilot parks on the marked spot three times"));

            // ---- drone
            Q(t, "drone_job", "What should the drone do first?", 0,
                C("hover", "Hover steadily", "hover,steady,steadily,stable,still", "drone_test",
                    goal: "that hovers steadily", parts: "Stability tuning"),
                C("fast", "Fly fast", "fast,quick,speed,race,racing", "drone_test",
                    goal: "that flies fast", parts: "Powerful motors"),
                C("camera", "Carry a camera", "camera,video,film,photo,pictures", "drone_test",
                    goal: "that carries a small camera", parts: "Camera mount"));
            Q(t, "drone_test", "How do we test it?", 0,
                C("minute", "Hover for one minute", "minute,stay,drifting",
                    test: "Hover in one spot for one minute without drifting more than a hand's width.",
                    milestones: "Simulate the flight and tune it|Hover for one minute"),
                C("hoops", "Fly a hoop course", "hoop,hoops,course,ring,rings,through",
                    test: "Fly through every hoop of a hoop course without touching one.",
                    milestones: "Simulate the flight and tune it|Fly the hoop course"),
                C("landing", "Land on a marked spot", "land,landing,mark,marked,spot,pad",
                    test: "Take off and land on a marked spot, three times out of three.",
                    milestones: "Simulate the flight and tune it|Land on the marked spot three times"));

            // ---- rocket
            Q(t, "rocket_aim", "What matters most for the rocket?", 0,
                C("straight", "Flying straight", "straight,stable,stability,steady,direction", "rocket_test",
                    goal: "that flies straight", parts: "Large stable fins"),
                C("high", "Going high", "high,height,altitude,far,up,tall", "rocket_test",
                    goal: "that climbs as high as it safely can", parts: "Light body"),
                C("payload", "Carrying a payload", "payload,carry,carrying,cargo,load", "rocket_test",
                    goal: "that carries a small payload", parts: "Payload bay"));
            Q(t, "rocket_test", "How do we test it?", 0,
                C("simulate", "Simulate the flight", "simulate,simulation,flight,fly,sim",
                    test: "Simulate the flight in CAD and check it stays stable from launch to the top.",
                    milestones: "Run the flight simulation|Tune the fins until it passes"),
                C("balance", "Check the balance", "balance,balanced,centre,center,mass,pressure",
                    test: "In the CAD analysis the centre of mass sits ahead of the centre of pressure.",
                    milestones: "Check the balance in the CAD analysis|Move parts until it passes"),
                C("fins", "Compare two fin designs", "compare,fins,fin,designs,design",
                    test: "Simulate two fin designs and keep the one that flies higher and straighter.",
                    milestones: "Design a second set of fins|Simulate both and keep the better one"));

            // ---- model that recognises things
            Q(t, "model_what", "What should it recognise?", 0,
                C("desk", "Objects on my desk", "objects,object,desk,things,items", "model_test",
                    goal: "that recognises objects on my desk", parts: "Photos of the objects on my desk"),
                C("hands", "My hand shapes", "hand,hands,shapes,shape,gestures,gesture,fingers", "model_test",
                    goal: "that recognises my hand shapes", parts: "Recordings of my hand shapes"),
                C("room", "Things around my room", "room,around,furniture,house,home", "model_test",
                    goal: "that recognises things around my room", parts: "Photos of my room"));
            Q(t, "model_test", "How good is good enough?", 0,
                C("eight", "Right 8 times in 10", "eight,8,eighty,80",
                    test: "It names 8 of 10 new pictures correctly.",
                    milestones: "Train a first model and check it|Improve it until it passes"),
                C("nine", "Right 9 times in 10", "nine,9,ninety,90",
                    test: "It names 9 of 10 new pictures correctly.",
                    milestones: "Train a first model and check it|Improve it until it passes"),
                C("try", "Just a first try", "try,rough,beginning,anything",
                    test: "It trains from start to finish and names at least half of the new pictures correctly.",
                    milestones: "Train a first model and check it|Improve it until it passes"));

            // ---- something else
            Q(t, "other_kind", "What kind of thing is it?", 0,
                C("moves", "Something that moves", "moves,move,moving,robot,wheels,motor", "other_test",
                    goal: "that moves around", parts: "Moving parts and a way to drive them"),
                C("works", "Something that sits and works", "sits,desk,machine,device,gadget,tool", "other_test",
                    goal: "that sits on my desk and does a job", parts: "A body or case"),
                C("software", "Software or a model", "software,code,program,app,application,script", "other_test",
                    goal: "that runs as software", parts: "Code and some data"));
            Q(t, "other_test", "How do we test it?", 0,
                C("once", "It does its job once", "once,job,single",
                    test: "It does its one job correctly one time.",
                    milestones: "Build a first version|It does its job once"),
                C("ten", "It works ten times in a row", "ten,10,row,repeat,reliable",
                    test: "It does its job correctly ten times in a row.",
                    milestones: "Build a first version|It works ten times in a row"),
                C("friend", "A friend can use it", "friend,someone,people,person,anyone",
                    test: "A friend can use it without any help from me.",
                    milestones: "Build a first version|A friend uses it on their own"));
            return t;
        }
    }
}
