using Newtonsoft.Json.Linq;

namespace Forge.Notebook
{
    /// <summary>A lesson (C-17 section 5, <c>albie.lesson/1</c>): a read-only view over its JSON record.</summary>
    public sealed class Lesson
    {
        public readonly JObject Json;
        public Lesson(JObject json) { Json = json; }

        public string Id => (string)Json["id"];
        public string Text => (string)Json["text"];
        public string By => (string)Json["by"];
        public int Seen => (int?)Json["seen"] ?? 1;
        public bool Hidden => (bool?)Json["hidden"] ?? false;
        public string CreatedAt => (string)Json["created_at"];
        /// <summary>The entry the lesson came from, so a claim always leads back to its evidence.</summary>
        public string FromEntry => (string)Json["from"]?["entry"];
        public string FromProject => (string)Json["from"]?["project_id"];
        public int FromVersion => (int?)Json["from"]?["project_version"] ?? 0;

        public string[] Tags
        {
            get
            {
                var tags = Json["tags"] as JArray;
                if (tags == null) return new string[0];
                var result = new string[tags.Count];
                for (int i = 0; i < result.Length; i++) result[i] = (string)tags[i];
                return result;
            }
        }

        public override string ToString() => $"{Id}: {Text}";
    }
}
