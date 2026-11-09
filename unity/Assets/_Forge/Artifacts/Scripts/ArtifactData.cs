using System;
using System.Collections.Generic;
using System.Globalization;
using Forge.Notebook;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>One snapshot table (C-12 section 7): named columns, rows of numbers, strings and nulls, and when it was fetched.</summary>
    public sealed class ArtifactTable
    {
        public string Name, Source, FetchedAt;
        public bool Sample;
        /// <summary>Where the rows came from: <c>connector</c> (the connector host), <c>snapshot</c> (the artifact's own file) or <c>bundled</c> (the device's sample for that source).</summary>
        public string From;
        public string[] Columns, Types;
        public readonly List<JToken[]> Rows = new List<JToken[]>();

        public int Column(string name) => name == null ? -1 : Array.IndexOf(Columns, name);

        /// <summary>A cell as a number, or NaN for null, a missing column or text.</summary>
        public double Number(int row, int column)
        {
            if (column < 0) return double.NaN;
            var cell = Rows[row][column];
            return cell.Type == JTokenType.Integer || cell.Type == JTokenType.Float ? (double)cell : double.NaN;
        }

        /// <summary>A cell as text, or null for null or a missing column.</summary>
        public string Text(int row, int column)
        {
            if (column < 0) return null;
            var cell = Rows[row][column];
            return cell.Type == JTokenType.Null ? null : Convert.ToString(((JValue)cell).Value, CultureInfo.InvariantCulture);
        }

        /// <summary>A snapshot's JSON as a table, or null with <paramref name="error"/>. A wrong shape is refused, never guessed at.</summary>
        public static ArtifactTable Parse(string name, JObject snapshot, out string error)
        {
            error = null;
            if (snapshot == null) { error = "it's empty"; return null; }
            if (!(snapshot["columns"] is JArray columns) || columns.Count == 0) { error = "it has no columns"; return null; }
            if (!(snapshot["rows"] is JArray rows)) { error = "it has no rows"; return null; }
            var table = new ArtifactTable
            {
                Name = name,
                Source = snapshot.Value<string>("source"),
                FetchedAt = snapshot.Value<string>("fetched_at"),
                Sample = snapshot.Value<bool?>("sample") == true,
                Columns = new string[columns.Count],
                Types = new string[columns.Count],
            };
            for (int c = 0; c < columns.Count; c++)
            {
                table.Columns[c] = columns[c].Value<string>("name");
                table.Types[c] = columns[c].Value<string>("type");
                if (string.IsNullOrEmpty(table.Columns[c])) { error = $"column {c} has no name"; return null; }
                if (table.Types[c] != "number" && table.Types[c] != "string") { error = $"column \"{table.Columns[c]}\" has type {table.Types[c] ?? "nothing"}: use number or string"; return null; }
            }
            for (int r = 0; r < rows.Count; r++)
            {
                if (!(rows[r] is JArray row) || row.Count != columns.Count) { error = $"row {r} doesn't have {columns.Count} cells"; return null; }
                var cells = new JToken[row.Count];
                for (int c = 0; c < row.Count; c++)
                {
                    var cell = row[c];
                    bool isNumber = cell.Type == JTokenType.Integer || cell.Type == JTokenType.Float;
                    bool fits = cell.Type == JTokenType.Null || (table.Types[c] == "number" ? isNumber : cell.Type == JTokenType.String);
                    if (!fits) { error = $"row {r}, column \"{table.Columns[c]}\" isn't a {table.Types[c]}"; return null; }
                    cells[c] = cell;
                }
                table.Rows.Add(cells);
            }
            return table;
        }
    }

    /// <summary>
    /// The data an artifact's manifest asks for (<c>data: [{name, source, snapshot}]</c>), resolved with no network.
    /// For each table, in order:
    /// 1. <see cref="Provider"/>: the connector host (C-18, Person 3) hands over its latest snapshot for that source;
    /// 2. the artifact's own <c>snapshot</c> file;
    /// 3. the device's bundled sample for that source (<c>Resources/ArtifactData/&lt;connector&gt;_&lt;tool&gt;</c>). It's
    ///    always <c>sample: true</c>, so the person sees "Sample data" and never mistakes it for theirs.
    /// If none of those has it, the artifact doesn't open (<c>no_data</c>). Nothing is ever drawn from a guess.
    /// </summary>
    public static class ArtifactData
    {
        /// <summary>
        /// Set by the connector host once it lands: its latest snapshot for a source (<c>connector:source_control/list_commits</c>),
        /// in the section 7 shape, or null if it has none. Called when an artifact opens, so it must not wait on the network.
        /// </summary>
        public static Func<string, JObject> Provider;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Provider = null;

        /// <summary>The bundled sample for a source, or null: <c>connector:source_control/list_commits</c> → <c>ArtifactData/source_control_list_commits</c>.</summary>
        public static JObject Bundled(string source)
        {
            if (string.IsNullOrEmpty(source)) return null;
            string key = source.Substring(source.IndexOf(':') + 1).Replace('/', '_');
            var asset = Resources.Load<TextAsset>("ArtifactData/" + key);
            if (asset == null) return null;
            var snapshot = (JObject)NotebookFiles.Parse(asset.text);
            snapshot["sample"] = true;
            return snapshot;
        }

        /// <summary>
        /// The sources an artifact can read on this headset, for <c>artifact_list</c>: each with its columns, the permission
        /// the manifest has to ask for, and whether only sample rows are here (no connector host, or it has nothing yet).
        /// </summary>
        public static JArray Sources()
        {
            var list = new JArray();
            foreach (var asset in Resources.LoadAll<TextAsset>("ArtifactData"))
            {
                JObject snapshot;
                try { snapshot = (JObject)NotebookFiles.Parse(asset.text); }
                catch (Exception e) when (e is JsonException || e is InvalidCastException) { continue; }
                string source = snapshot.Value<string>("source");
                if (source == null) continue;
                JObject live = null;
                if (Provider != null)
                    try { live = Provider(source); }
                    catch (Exception e) { Debug.LogWarning($"[Artifacts] the connector host failed on {source}: {e.Message}"); }
                list.Add(new JObject
                {
                    ["source"] = source,
                    ["permission"] = Permission(source),
                    ["columns"] = (live ?? snapshot)["columns"]?.DeepClone(),
                    ["sample_only"] = live == null,
                });
            }
            return list;
        }

        /// <summary>The <c>permissions.connectors</c> entry a source needs: <c>connector:source_control/list_commits</c> → <c>source_control:read</c>. Null for a builtin.</summary>
        public static string Permission(string source)
        {
            if (source == null || !source.StartsWith("connector:")) return null;
            string rest = source.Substring("connector:".Length);
            int slash = rest.IndexOf('/');
            return (slash > 0 ? rest.Substring(0, slash) : rest) + ":read";
        }

        /// <summary>Every table the manifest names, or null with <paramref name="problem"/> saying which one is missing and why.</summary>
        public static Dictionary<string, ArtifactTable> Resolve(ArtifactPackage package, out string problem)
        {
            problem = null;
            var tables = new Dictionary<string, ArtifactTable>();
            if (!(package.Manifest["data"] is JArray list)) return tables;
            foreach (var entry in list)
            {
                string name = entry.Value<string>("name"), source = entry.Value<string>("source"), file = entry.Value<string>("snapshot");
                JObject snapshot = null;
                string from = null, why = null;
                if (Provider != null && source != null && source.StartsWith("connector:"))
                {
                    try { snapshot = Provider(source); }
                    catch (Exception e) { Debug.LogWarning($"[Artifacts] the connector host failed on {source}: {e.Message}"); }
                    if (snapshot != null) from = "connector";
                }
                if (snapshot == null && file != null)
                {
                    try { snapshot = package.ReadData(file, out why); }
                    catch (JsonException e) { why = $"{file} isn't valid JSON: {e.Message}"; }
                    catch (InvalidCastException) { why = $"{file} isn't a JSON object"; }
                    if (snapshot != null) from = "snapshot";
                }
                if (snapshot == null)
                {
                    snapshot = Bundled(source);
                    if (snapshot != null) from = "bundled";
                }
                if (snapshot == null)
                {
                    problem = $"It needs \"{name}\" from {source}, and there's no copy of it on this headset" + (why != null ? $" ({why})" : "") + ".";
                    return null;
                }
                var table = ArtifactTable.Parse(name, snapshot, out string error);
                if (table == null)
                {
                    problem = $"The data \"{name}\" can't be read: {error}.";
                    return null;
                }
                table.From = from;
                if (table.Source == null) table.Source = source;
                tables[name] = table;
            }
            return tables;
        }

        /// <summary>What Albie says about the data, or null. Sample data is always said out loud, and so is a saved copy's date.</summary>
        public static string Say(Dictionary<string, ArtifactTable> tables)
        {
            if (tables == null || tables.Count == 0) return null;
            foreach (var t in tables.Values)
                if (t.Sample) return "This is sample data, not yours: there's no connected account on this headset yet, so these commits are made up.";
            foreach (var t in tables.Values)
                if (t.From == "snapshot" && t.FetchedAt != null) return $"This is a saved copy of the data, from {Day(t.FetchedAt)}.";
            return null;
        }

        static string Day(string iso) =>
            DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t.ToString("MMMM d", CultureInfo.InvariantCulture) : iso;
    }
}
