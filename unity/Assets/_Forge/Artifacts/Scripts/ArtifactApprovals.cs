using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The first-open permissions (C-12 section 8). An artifact that asks for nothing opens straight away. Anything else
    /// shows <see cref="Lines"/> once; the approval is kept on the device against a hash of what was asked, so asking for
    /// something new asks again.
    /// </summary>
    public static class ArtifactApprovals
    {
        /// <summary>Tests point this somewhere else, so they never touch a real approval.</summary>
        public static string FileOverride;

        static string StoreFile => FileOverride ?? Path.Combine(Application.persistentDataPath, "artifacts", "approvals.json");

        /// <summary>What the sheet says, one line per permission. Empty: there's nothing to ask.</summary>
        public static List<string> Lines(JObject manifest)
        {
            var lines = new List<string>();
            if (!(manifest["permissions"] is JObject p)) return lines;
            if (p["connectors"] is JArray connectors)
                foreach (var connector in connectors)
                {
                    string name = ((string)connector).Split(':')[0].Replace('_', ' ');
                    lines.Add($"Reads your connected {name}");
                }
            if (p.Value<string>("room") == "read") lines.Add("Uses your room's layout");
            if (p.Value<string>("things") == "read") lines.Add("Reads your Thing's position and speed");
            if (p.Value<string>("write_project") == "confirm") lines.Add("Can ask to change your project");
            return lines;
        }

        /// <summary>Hashes what's being asked, so reordering keys or spelling out a "none" doesn't ask again.</summary>
        public static string Hash(JObject manifest)
        {
            var lines = Lines(manifest);
            lines.Sort(System.StringComparer.Ordinal);
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
                var text = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) text.Append(b.ToString("x2"));
                return text.ToString();
            }
        }

        public static bool NeedsApproval(JObject manifest)
        {
            if (Lines(manifest).Count == 0) return false;
            return Load().Value<string>(manifest.Value<string>("id")) != Hash(manifest);
        }

        public static void Approve(JObject manifest)
        {
            var store = Load();
            store[manifest.Value<string>("id")] = Hash(manifest);
            Save(store);
        }

        /// <summary>Forgets every approval, or one artifact's.</summary>
        public static void Forget(string id = null)
        {
            var store = Load();
            if (id == null) store.RemoveAll();
            else store.Remove(id);
            Save(store);
        }

        static JObject Load()
        {
            try
            {
                if (File.Exists(StoreFile)) return JObject.Parse(File.ReadAllText(StoreFile));
            }
            catch (JsonException e)
            {
                // Asking again is the safe way to fail.
                Debug.LogWarning("[Artifacts] approvals.json is damaged, so permissions will be asked again: " + e.Message);
            }
            return new JObject();
        }

        // Temp file first, so a crash can't leave half a file.
        static void Save(JObject store)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoreFile));
            string temp = StoreFile + ".tmp";
            File.WriteAllText(temp, store.ToString(Formatting.Indented));
            if (File.Exists(StoreFile)) File.Replace(temp, StoreFile, null);
            else File.Move(temp, StoreFile);
        }
    }
}
