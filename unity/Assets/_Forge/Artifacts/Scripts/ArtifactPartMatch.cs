using System;
using System.Collections.Generic;
using System.Text;

namespace Forge.Artifacts
{
    /// <summary>
    /// What a model's parts are called when people talk, beyond their node names: other names ("upper chambers" for the
    /// atria), groups ("the valves"), a one-line description each, and words that narrow nothing ("heart"). Read from a
    /// template's <c>parts.json</c> (<see cref="ArtifactExplorer.WordsFor"/>). Plain C#, so the matching can be checked
    /// outside Unity.
    /// </summary>
    public sealed class PartWords
    {
        /// <summary>The model is split into left and right halves of one thing (the brain): "the frontal lobe" means both.</summary>
        public bool Bilateral;
        readonly Dictionary<string, string> about = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<string>> aka = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> ignore = new HashSet<string>();
        internal readonly List<Group> groups = new List<Group>();

        internal sealed class Group
        {
            public string name;
            public readonly List<string> words = new List<string>();
            public readonly List<string> parts = new List<string>();
        }

        /// <summary>A part's entry, by its node name or, for a "Left"/"Right" part, by its name without the side.</summary>
        public void AddPart(string name, string description, IEnumerable<string> otherNames)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (!string.IsNullOrEmpty(description)) about[name] = description;
            if (otherNames == null) return;
            if (!aka.TryGetValue(name, out var list)) aka[name] = list = new List<string>();
            foreach (var n in otherNames) if (!string.IsNullOrWhiteSpace(n)) list.Add(n);
        }

        public void AddGroup(IList<string> words, IEnumerable<string> parts)
        {
            if (words == null || words.Count == 0 || parts == null) return;
            var g = new Group { name = words[0] };
            g.words.AddRange(words);
            g.parts.AddRange(parts);
            groups.Add(g);
        }

        public void AddIgnore(string word)
        {
            foreach (var t in PartMatch.Tokens(word)) ignore.Add(t);
        }

        /// <summary>The part's one-line description, or null.</summary>
        public string About(string part)
        {
            if (string.IsNullOrEmpty(part)) return null;
            if (about.TryGetValue(part, out var a)) return a;
            string bare = PartMatch.WithoutSide(part);
            return bare != part && about.TryGetValue(bare, out a) ? a : null;
        }

        internal IEnumerable<string> OtherNames(string part)
        {
            if (aka.TryGetValue(part, out var a)) foreach (var s in a) yield return s;
            string bare = PartMatch.WithoutSide(part);
            if (bare != part && aka.TryGetValue(bare, out var b)) foreach (var s in b) yield return s;
        }

        /// <summary>The groups' names ("chambers", "valves"), for Albie to offer.</summary>
        public List<string> GroupNames()
        {
            var list = new List<string>(groups.Count);
            foreach (var g in groups) list.Add(g.name);
            return list;
        }
    }

    /// <summary>What a spoken part request means on one model: see <see cref="PartMatch.Resolve"/>.</summary>
    public sealed class PartMatchResult
    {
        /// <summary>The parts asked for, as indices into the names given, in the order asked.</summary>
        public readonly List<int> Parts = new List<int>();
        /// <summary>"this", "that one", "what I'm pointing at": the part pointed or looked at decides.</summary>
        public bool Deictic;
        /// <summary>The kind of part said with "this" ("lobe" in "this lobe"), or null.</summary>
        public string Kind;
        /// <summary>The group asked for ("valves"), or null.</summary>
        public string Group;
        /// <summary>A singular name for a part found on both sides ("the frontal lobe" on the brain): both are in <see cref="Parts"/>.</summary>
        public bool BothSides;
        /// <summary>When it could mean several parts: the names to ask between. Nothing is chosen then.</summary>
        public readonly List<string> Options = new List<string>();
        /// <summary>The words that matched nothing ("spleen").</summary>
        public readonly List<string> Unmatched = new List<string>();
        public bool Ambiguous => Options.Count > 0;
    }

    /// <summary>
    /// Matches what someone said to a model's part names, for "show me the left ventricle", "just the valves", "the
    /// frontal and temporal lobes" and "this lobe". Robust to case, spacing, underscores, plurals, British or American
    /// spelling, other names and groups from <see cref="PartWords"/>, and partial words. Never guesses between parts it
    /// can't tell apart: it lists them instead.
    /// </summary>
    public static class PartMatch
    {
        static readonly HashSet<string> Fillers = new HashSet<string>
        {
            "the", "a", "an", "its", "of", "me", "my", "show", "see", "just", "only", "please", "part", "parts", "piece",
            "pieces", "bit", "bits", "thing", "things", "model", "one", "ones", "i", "to", "you", "can", "could", "let", "look",
            "at", "want", "highlight", "isolate", "focus", "on", "zoom", "into", "in", "expand", "with", "s", "your", "now",
            "give", "bring", "up", "out", "pull", "isolated", "alone", "own", "by", "itself", "themselves", "them", "all",
            "both", "whole", "entire", "area", "region", "section", "what", "is", "are", "where", "which",
        };
        // "all" and "both" are fillers, but a group word can still hold them ("all the valves"): exact phrases are compared first.
        static readonly HashSet<string> Deixis = new HashSet<string> { "this", "that", "these", "those", "here", "it", "pointing", "pointed", "looking", "looked" };
        static readonly HashSet<string> Sides = new HashSet<string> { "left", "right" };
        static readonly HashSet<string> SideWords = new HashSet<string> { "left", "right", "side", "half", "hemisphere" };
        static readonly HashSet<string> NotPlural = new HashSet<string>
        {
            "lens", "iris", "corpus", "apparatus", "thalamus", "hippocampus", "aqueous", "nucleus", "bus", "gas", "this",
            "its", "is", "us", "was", "has", "plus", "chordae", "pons", "callosum", "vitreous", "glottis", "pelvis", "tendineae",
            "plasmodesmata", "ganglia", "mitochondria", "cavae", "venae", "atria", "recti", "nucleoli", "thalami", "hippocampi",
        };
        static readonly Dictionary<string, string> Spelling = new Dictionary<string, string>
        {
            ["humor"] = "humour", ["fiber"] = "fibre", ["fibers"] = "fibres", ["color"] = "colour", ["colored"] = "coloured",
            ["center"] = "centre", ["centers"] = "centres", ["gray"] = "grey", ["organize"] = "organise",
            ["first"] = "1", ["second"] = "2", ["two"] = "2", ["three"] = "3", ["four"] = "4",
        };

        /// <summary>Lower-case words, letters and digits only, British spelling, number words as digits.</summary>
        public static List<string> Tokens(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            var sb = new StringBuilder();
            string lower = text.ToLowerInvariant().Replace("'s ", " ").Replace("’s ", " ");
            if (lower.EndsWith("'s") || lower.EndsWith("’s")) lower = lower.Substring(0, lower.Length - 2);
            for (int i = 0; i <= lower.Length; i++)
            {
                char c = i < lower.Length ? lower[i] : ' ';
                if (char.IsLetterOrDigit(c)) { sb.Append(c); continue; }
                if (sb.Length == 0) continue;
                string w = sb.ToString();
                sb.Clear();
                if (Spelling.TryGetValue(w, out var s)) w = s;
                list.Add(w);
            }
            // "strand one" -> "strand 1"; "one" otherwise is a filler ("the left one").
            for (int i = 1; i < list.Count; i++) if (list[i] == "one" && list[i - 1] == "strand") list[i] = "1";
            return list;
        }

        /// <summary>A word without its plural ending: arteries artery, valves valve, lobes lobe. Plural set when it had one.</summary>
        public static string Singular(string w, out bool plural)
        {
            plural = false;
            if (w.Length < 4 || NotPlural.Contains(w) || w.EndsWith("ss") || w.EndsWith("us") || w.EndsWith("is")) return w;
            if (w.EndsWith("ies") && w.Length > 4) { plural = true; return w.Substring(0, w.Length - 3) + "y"; }
            if (w.EndsWith("s")) { plural = true; return w.Substring(0, w.Length - 1); }
            return w;
        }

        /// <summary>The words that count, singular, fillers dropped; <paramref name="plural"/> when any was plural.</summary>
        static List<string> Words(string text, out bool plural, bool keepFillers = false)
        {
            plural = false;
            var list = new List<string>();
            foreach (var t in Tokens(text))
            {
                if (!keepFillers && Fillers.Contains(t)) continue;
                list.Add(Singular(t, out bool p));
                plural |= p;
            }
            return list;
        }

        static string Join(List<string> words) => string.Join(" ", words);

        /// <summary>"Left frontal lobe" -> "frontal lobe"; other names as they are.</summary>
        public static string WithoutSide(string name)
        {
            if (name == null) return null;
            foreach (var side in new[] { "Left ", "Right ", "left ", "right " })
                if (name.StartsWith(side, StringComparison.Ordinal) && name.Length > side.Length) return name.Substring(side.Length);
            return name;
        }

        sealed class Phrase
        {
            public int part;
            public string text, flat;
            public List<string> words;
        }

        sealed class GroupPhrase
        {
            public PartWords.Group group;
            public string text, flat;
            public bool plural;
            public List<int> parts;
        }

        /// <summary>
        /// What <paramref name="spoken"/> asks for among <paramref name="names"/> (the model's part names), with
        /// <paramref name="words"/> (null for a model with none). A list ("the aorta and the mitral valve") gives every
        /// part named; a plural ("the coronary arteries") every part it fits; a singular that fits several parts gives
        /// <see cref="PartMatchResult.Options"/> to ask between instead, except a part found on both sides of a bilateral
        /// model ("the temporal lobe"), which gives both.
        /// </summary>
        public static PartMatchResult Resolve(IReadOnlyList<string> names, PartWords words, string spoken)
        {
            var result = new PartMatchResult();
            if (names == null || string.IsNullOrWhiteSpace(spoken)) return result;
            var phrases = new List<Phrase>();
            for (int i = 0; i < names.Count; i++)
            {
                Add(phrases, i, names[i]);
                if (words != null) foreach (var other in words.OtherNames(names[i])) Add(phrases, i, other);
            }
            var groups = new List<GroupPhrase>();
            if (words != null)
                foreach (var g in words.groups)
                {
                    var members = new List<int>();
                    foreach (var p in g.parts)
                        for (int i = 0; i < names.Count; i++)
                            if (string.Equals(names[i], p, StringComparison.OrdinalIgnoreCase) && !members.Contains(i)) members.Add(i);
                    if (members.Count == 0) continue;
                    foreach (var w in g.words)
                    {
                        var ws = Words(w, out bool plural);
                        if (ws.Count == 0) ws = Words(w, out plural, true);
                        groups.Add(new GroupPhrase { group = g, text = Join(ws), flat = Join(ws).Replace(" ", ""), plural = plural, parts = members });
                    }
                }

            // The whole thing first: a name can hold a comma ("Backbone, strand 1").
            if (TryItem(spoken, names, words, phrases, groups, result, false, out _, true)) return Finish(result, names);

            var items = Split(spoken);
            bool anyPlural = false;
            string lastType = null;
            if (items.Count > 1)
            {
                var last = Words(items[items.Count - 1], out anyPlural);
                if (last.Count >= 2) lastType = last[last.Count - 1];
            }
            for (int k = 0; k < items.Count; k++)
            {
                string item = items[k];
                // "left and right atrium": "left" means the left atrium.
                if (k < items.Count - 1 && IsOnlySide(item))
                {
                    var rest = new List<string>();
                    foreach (var w in Words(items[items.Count - 1], out _)) if (!SideWords.Contains(w)) rest.Add(w);
                    if (rest.Count > 0) item = item + " " + Join(rest);
                }
                if (TryItem(item, names, words, phrases, groups, result, anyPlural, out bool deictic) || deictic) continue;
                // "the frontal and temporal lobes": "frontal" borrows "lobes".
                if (lastType != null && k < items.Count - 1 && TryItem(item + " " + lastType, names, words, phrases, groups, result, anyPlural, out _)) continue;
                string left = Join(Words(item, out _));
                if (left.Length > 0) result.Unmatched.Add(left);
                if (result.Ambiguous) break;
            }
            return Finish(result, names);
        }

        static bool IsOnlySide(string item)
        {
            var ws = Words(item, out _);
            if (ws.Count == 0) return false;
            foreach (var w in ws) if (!SideWords.Contains(w)) return false;
            return true;
        }

        static PartMatchResult Finish(PartMatchResult r, IReadOnlyList<string> names)
        {
            if (r.Ambiguous) r.Parts.Clear();
            return r;
        }

        static void Add(List<Phrase> phrases, int part, string text)
        {
            var ws = Words(text, out _);
            if (ws.Count == 0) ws = Words(text, out _, true);
            if (ws.Count == 0) return;
            phrases.Add(new Phrase { part = part, words = ws, text = Join(ws), flat = Join(ws).Replace(" ", "") });
        }

        static List<string> Split(string spoken)
        {
            string s = " " + spoken.ToLowerInvariant().Replace(",", " and ").Replace("&", " and ").Replace(";", " and ") + " ";
            foreach (var joiner in new[] { " as well as ", " plus ", " along with ", " together with ", " and also ", " and then " })
                s = s.Replace(joiner, " and ");
            var list = new List<string>();
            foreach (var piece in s.Split(new[] { " and " }, StringSplitOptions.RemoveEmptyEntries))
                if (piece.Trim().Length > 0) list.Add(piece.Trim());
            return list;
        }

        // One item ("the left ventricle"): adds what it means to the result. False when it meant nothing (and isn't "this").
        static bool TryItem(string item, IReadOnlyList<string> names, PartWords words, List<Phrase> phrases, List<GroupPhrase> groups,
            PartMatchResult result, bool listPlural, out bool deictic, bool exactOnly = false)
        {
            deictic = false;
            var all = Words(item, out bool plural);
            plural |= listPlural;
            if (all.Count == 0) return false;
            if (exactOnly) return Exact(all, names, words, phrases, groups, result, plural);

            // "this", "this lobe", "the one I'm pointing at".
            bool pointing = false;
            foreach (var w in Tokens(item)) pointing |= Deixis.Contains(w);
            if (pointing)
            {
                var rest = new List<string>();
                foreach (var w in Tokens(item))
                    if (!Deixis.Contains(w) && !Fillers.Contains(w) && w != "and" && w != "am" && w != "m" && w != "im") rest.Add(Singular(w, out _));
                result.Deictic = true;
                deictic = true;
                if (rest.Count > 0 && result.Kind == null) result.Kind = Join(rest);
                return true;
            }

            if (Exact(all, names, words, phrases, groups, result, plural)) return true;

            // Words that narrow nothing ("the heart's", "brain"), then again.
            var bare = new List<string>();
            foreach (var w in all) if (words == null || !words.ignore.Contains(w)) bare.Add(w);
            if (bare.Count == 0) return false;
            if (bare.Count < all.Count && Exact(bare, names, words, phrases, groups, result, plural)) return true;

            // "the left side", "right half": every part on that side.
            bool onlySide = true, anySide = false;
            foreach (var w in bare) { onlySide &= SideWords.Contains(w); anySide |= Sides.Contains(w); }
            if (onlySide && anySide)
            {
                string side = bare.Contains("left") ? "left" : "right";
                int before = result.Parts.Count;
                foreach (var p in phrases)
                    if (p.words.Count > 1 && p.words[0] == side && p.text == Join(Words(names[p.part], out _))) AddPart(result, p.part);
                if (result.Parts.Count > before) { result.Group = side + " side"; return true; }
                return false;
            }

            // Every word said is in the name (or a prefix of one of its words: "ventric", "occipit").
            var full = new List<int>();
            var partial = new List<int>();
            int best = 0;
            var seen = new HashSet<int>();
            foreach (var p in phrases)
            {
                int hits = 0, content = 0;
                foreach (var w in bare)
                {
                    bool hit = false;
                    foreach (var n in p.words) if (n == w || (w.Length >= 4 && n.StartsWith(w, StringComparison.Ordinal))) { hit = true; break; }
                    if (!hit) continue;
                    hits++;
                    if (!Sides.Contains(w)) content++;
                }
                if (content == 0) continue;
                if (hits == bare.Count) { if (!full.Contains(p.part)) full.Add(p.part); }
                else if (hits > best) { best = hits; partial.Clear(); partial.Add(p.part); }
                else if (hits == best && !partial.Contains(p.part)) partial.Add(p.part);
                seen.Add(p.part);
            }
            var found = full.Count > 0 ? full : partial;
            if (found.Count == 0) return false;
            // A part said with its side ("left frontal") never also takes the other side's.
            if (found.Count > 1)
            {
                string side = bare.Contains("left") ? "left" : bare.Contains("right") ? "right" : null;
                if (side != null)
                {
                    var same = new List<int>();
                    foreach (int i in found) if (Words(names[i], out _).Contains(side)) same.Add(i);
                    if (same.Count > 0) found = same;
                }
            }
            return Choose(found, names, words, result, plural);
        }

        static bool Exact(List<string> ws, IReadOnlyList<string> names, PartWords words, List<Phrase> phrases, List<GroupPhrase> groups,
            PartMatchResult result, bool plural)
        {
            string text = Join(ws), flat = text.Replace(" ", "");
            var hits = new List<int>();
            foreach (var p in phrases) if ((p.text == text || p.flat == flat) && !hits.Contains(p.part)) hits.Add(p.part);
            // An exact name or other name. One shared by a left and right pair ("insular cortex") is both on a bilateral model.
            if (hits.Count > 0) return Choose(hits, names, words, result, plural);
            // A group word as said ("the valves", "backbone"); then the singular of a plural one ("valve"): which one?
            foreach (var g in groups)
            {
                if ((g.text != text && g.flat != flat) || (g.plural && !plural && g.parts.Count > 1)) continue;
                foreach (int i in g.parts) AddPart(result, i);
                if (result.Group == null) result.Group = g.group.name;
                return true;
            }
            foreach (var g in groups)
            {
                if (g.text != text && g.flat != flat) continue;
                foreach (int i in g.parts) result.Options.Add("#" + i);
                result.Group = g.group.name;
                return true;
            }
            return false;
        }

        static bool Choose(List<int> found, IReadOnlyList<string> names, PartWords words, PartMatchResult result, bool plural)
        {
            if (found.Count == 1 || plural) { foreach (int i in found) AddPart(result, i); return true; }
            // The same part on both sides, on a model that is two halves of one thing: both.
            if (words != null && words.Bilateral)
            {
                string first = WithoutSide(names[found[0]]);
                bool pair = true;
                foreach (int i in found) pair &= string.Equals(WithoutSide(names[i]), first, StringComparison.OrdinalIgnoreCase) && WithoutSide(names[i]) != names[i];
                if (pair) { foreach (int i in found) AddPart(result, i); result.BothSides = true; return true; }
            }
            foreach (int i in found) result.Options.Add("#" + i);
            return true;
        }

        static void AddPart(PartMatchResult r, int i)
        {
            if (!r.Parts.Contains(i)) r.Parts.Add(i);
        }

        /// <summary><see cref="PartMatchResult.Options"/> as part names (they are kept as "#index" until then).</summary>
        public static List<string> OptionNames(PartMatchResult r, IReadOnlyList<string> names)
        {
            var list = new List<string>(r.Options.Count);
            foreach (var o in r.Options)
                if (o != null && o.StartsWith("#") && int.TryParse(o.Substring(1), out int i) && i >= 0 && i < names.Count) list.Add(names[i]);
                else if (o != null) list.Add(o);
            return list;
        }
    }

    /// <summary>
    /// Readable names for parts of a model nobody named for people: "Object_12", "mesh_0.001", "polySurface34" and
    /// "33d9f5...fbx" mean nothing; "dinosaur-skull_2" is "Dinosaur skull". Plain C#, checked outside Unity.
    /// </summary>
    public static class PartNaming
    {
        static readonly HashSet<string> Junk = new HashSet<string>
        {
            "object", "objects", "mesh", "meshes", "node", "nodes", "polysurface", "polysurf", "pcube", "pcylinder", "psphere",
            "pplane", "ptorus", "group", "grp", "geo", "geom", "geometry", "default", "defaultmaterial", "primitive", "submesh",
            "root", "rootnode", "scene", "gltf", "glb", "sketchfab", "model", "untitled", "obj", "fbx", "null", "empty",
            "transform", "mat", "material", "lod", "low", "high", "poly", "placeable", "instance", "gles", "cleaner", "shape",
            "export", "exported", "blend", "max", "ma", "mb", "stl", "dae", "collada", "dummy", "helper", "locator", "joint",
            "bone", "armature", "skin", "id", "copy", "merged", "combined", "final", "new", "temp", "tmp", "v", "ver", "version",
            "main", "subtool", "zbrush", "tool", "body", "part", "piece", "element", "item", "unnamed", "noname", "sceneroot",
            "gltfsceneroot", "rootnode", "scenerootnode", "auto", "converted", "triangulated", "decimated", "retopo",
        };
        // Meaningful on their own only next to something else ("Body" of a chair is fine as "Chair body").
        static readonly HashSet<string> WeakAlone = new HashSet<string> { "body", "part", "piece", "main", "element", "item" };

        /// <summary>A readable name from a node or mesh name, or null when it says nothing a person would use.</summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            string lower = s.ToLowerInvariant();
            foreach (var ext in new[] { ".fbx", ".obj", ".gltf", ".glb", ".blend", ".max", ".ma", ".mb", ".stl", ".dae", ".3ds", ".gles" })
                if (lower.Contains(ext)) return null;
            // Split camelCase and separators: "ControlConsoleChair_Body" -> "Control Console Chair Body".
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '_' || c == '-' || c == '.' || c == ':' || c == '|' || c == '/' || c == '(' || c == ')' || c == '[' || c == ']') { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && char.IsLower(s[i - 1])) sb.Append(' ');
                if (i > 0 && char.IsLetter(c) && char.IsDigit(s[i - 1])) sb.Append(' ');
                if (i > 0 && char.IsDigit(c) && char.IsLetter(s[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            var kept = new List<string>();
            bool junkFirst = false;
            string[] words = sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                string lw = w.ToLowerInvariant();
                if (IsNumber(lw) || IsHex(lw) || lw.Length < 2) continue;
                // "poly Surface" (polySurface34) and "p Cube": joined, they're a modelling tool's default.
                if (i + 1 < words.Length && Junk.Contains(lw + words[i + 1].ToLowerInvariant())) { i++; if (kept.Count == 0) junkFirst = true; continue; }
                if (Junk.Contains(lw) && !WeakAlone.Contains(lw)) { if (kept.Count == 0) junkFirst = true; continue; }
                kept.Add(lw);
            }
            // Default primitive names a modelling tool gives ("Cube.003", "Cylinder", "Sphere_2") say nothing about the part.
            if (kept.Count == 1 && (kept[0] == "cube" || kept[0] == "cylinder" || kept[0] == "sphere" || kept[0] == "plane" && junkFirst || kept[0] == "torus" || kept[0] == "cone" || kept[0] == "circle")) return null;
            if (kept.Count == 0) return null;
            bool allWeak = true;
            foreach (var k in kept) allWeak &= WeakAlone.Contains(k);
            if (allWeak) return null;
            if (kept.Count > 5) kept.RemoveRange(5, kept.Count - 5);
            string name = string.Join(" ", kept);
            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        static bool IsNumber(string w)
        {
            foreach (char c in w) if (!char.IsDigit(c)) return false;
            return w.Length > 0;
        }

        // A content hash or uuid chunk: 8+ hex characters with a digit in them.
        static bool IsHex(string w)
        {
            if (w.Length < 8) return false;
            bool digit = false;
            foreach (char c in w)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex) return false;
                digit |= char.IsDigit(c);
            }
            return digit;
        }

        /// <summary>Where a part sits seen from the front: "top", "lower left", "middle". <paramref name="dir"/> is right, up, toward the viewer, over the model's size.</summary>
        public static string Where(float right, float up, float toward)
        {
            float ar = Math.Abs(right), au = Math.Abs(up), at = Math.Abs(toward);
            float most = Math.Max(ar, Math.Max(au, at));
            if (most < 0.15f) return "middle";
            string v = up > 0 ? "top" : "bottom", h = right > 0 ? "right" : "left", d = toward > 0 ? "front" : "back";
            if (most == au) return ar > 0.6f * au ? (up > 0 ? "upper " : "lower ") + h : v;
            if (most == ar) return au > 0.6f * ar ? (up > 0 ? "upper " : "lower ") + h : h;
            return d;
        }
    }
}
