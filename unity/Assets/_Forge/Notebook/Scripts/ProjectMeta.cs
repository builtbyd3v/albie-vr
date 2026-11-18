using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Notebook
{
    /// <summary>
    /// A project's own small record, <c>projects/&lt;id&gt;/project.json</c> (<c>albie.project_meta/1</c>): what joins it to
    /// the rest of the app when that isn't in the notebook. Fields, all optional:
    /// <c>title</c> (the tile's name), <c>kind</c> ("ml" for a project made for a cloud or hub ML project),
    /// <c>ml_project</c> (that ML project's name in <c>GET /ml/projects</c>), <c>parent</c> (the project it was made from,
    /// for an <c>ml_new</c> project started while another project was open) and <c>ml_links</c> (the ML projects made
    /// from this one: an array of <c>{name, title}</c>) and <c>copy_of</c> (the sample project it was copied from with Make a
    /// copy, so it gets the sample's loop and design). Read once and cached; every write goes through
    /// <see cref="NotebookFiles.Write"/> like the rest of the notebook. Never called from Update.
    /// </summary>
    public static class ProjectMeta
    {
        public const string Schema = "albie.project_meta/1";
        const string FileName = "project.json";

        static readonly Dictionary<string, JObject> cache = new Dictionary<string, JObject>(StringComparer.Ordinal);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => cache.Clear();

        /// <summary>Forgets what was read (a check that switches the notebook root).</summary>
        public static void ClearCache() => cache.Clear();

        static string Root => NotebookStore.Root ?? NotebookStore.DefaultRoot;

        static string PathOf(string projectId) => Path.Combine(Root, "projects", projectId, FileName);

        static bool BadId(string projectId) =>
            string.IsNullOrEmpty(projectId) || projectId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;

        /// <summary>The project's record, or null when it has none. Don't change the object: use <see cref="Update"/>.</summary>
        public static JObject Read(string projectId)
        {
            if (BadId(projectId)) return null;
            string key = Root + "|" + projectId;
            if (cache.TryGetValue(key, out JObject known)) return known;
            JObject meta = null;
            string path = PathOf(projectId);
            try
            {
                if (File.Exists(path))
                {
                    meta = NotebookFiles.Parse(File.ReadAllText(path)) as JObject;
                    if (meta != null && (string)meta["schema"] != Schema) meta = null;
                }
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                Debug.Log("[Notebook] " + path + " didn't load: " + e.Message);
                meta = null;
            }
            cache[key] = meta;
            return meta;
        }

        /// <summary>A string field of the record, or null.</summary>
        public static string Get(string projectId, string field)
        {
            JToken value = Read(projectId)?[field];
            return value != null && value.Type == JTokenType.String ? (string)value : null;
        }

        /// <summary>Changes the record (made when there is none) and writes it. False when the id can't be a folder.</summary>
        public static bool Update(string projectId, Action<JObject> change)
        {
            if (BadId(projectId) || change == null) return false;
            JObject current = Read(projectId);
            JObject next = current != null ? (JObject)current.DeepClone() : new JObject { ["schema"] = Schema, ["project_id"] = projectId };
            change(next);
            next["schema"] = Schema;
            next["updated_at"] = NotebookStore.Clock().ToString("yyyy-MM-ddTHH:mm:ssZ");
            cache[Root + "|" + projectId] = next;
            NotebookFiles.Write(PathOf(projectId), next.ToString(Formatting.Indented));
            return true;
        }

        /// <summary>
        /// Records that the ML project <paramref name="name"/> (or, before its name is known, its <paramref name="title"/>)
        /// was made from <paramref name="projectId"/>. Adding the same one twice keeps one; a later call with the name fills it in.
        /// </summary>
        public static void LinkMl(string projectId, string name, string title)
        {
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(title)) return;
            Update(projectId, meta =>
            {
                var links = meta["ml_links"] as JArray ?? new JArray();
                JObject found = null;
                foreach (JToken t in links)
                {
                    if (!(t is JObject link)) continue;
                    if ((!string.IsNullOrEmpty(name) && (string)link["name"] == name)
                        || (!string.IsNullOrEmpty(title) && (string)link["title"] == title)) { found = link; break; }
                }
                if (found == null)
                {
                    found = new JObject();
                    links.Add(found);
                }
                if (!string.IsNullOrEmpty(name)) found["name"] = name;
                if (!string.IsNullOrEmpty(title)) found["title"] = title;
                meta["ml_links"] = links;
            });
        }

        /// <summary>The project that an ML project's <paramref name="name"/> or <paramref name="title"/> was linked to, among <paramref name="projectIds"/>; null for none.</summary>
        public static string ParentOfMl(IList<string> projectIds, string name, string title)
        {
            for (int i = 0; i < projectIds.Count; i++)
            {
                if (!(Read(projectIds[i])?["ml_links"] is JArray links)) continue;
                foreach (JToken t in links)
                {
                    if (!(t is JObject link)) continue;
                    if ((!string.IsNullOrEmpty(name) && (string)link["name"] == name)
                        || (!string.IsNullOrEmpty(title) && (string)link["title"] == title)) return projectIds[i];
                }
            }
            return null;
        }
    }
}
