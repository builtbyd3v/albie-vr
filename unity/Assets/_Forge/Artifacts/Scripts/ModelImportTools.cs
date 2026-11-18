using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Forge.Artifacts
{
    /// <summary>
    /// Albie's model tools on the headset (<c>Resources/AlbieTools/models.json</c>): <c>model_search</c> finds real,
    /// open-licensed 3D models by name, and <c>model_import</c> brings in the one the person picked as an artifact and
    /// opens it. Both go through ForgeNet to <c>/models/...</c> (hub/models), on the hub or on Albie's cloud. A model is
    /// only ever imported by id from the last search (or because the hub's own model_import asked, after the same
    /// choice), one at a time. With neither server reachable the tools say so; there are no made-up results.
    /// </summary>
    public static class ModelImportTools
    {
        const string SearchPath = "/models/search", FetchPath = "/models/fetch", FilePath = "/models/file";

        static readonly List<JObject> offered = new List<JObject>();  // the last search's hits, as the server sent them
        static bool importing;
        static IDisposable hubImports;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            offered.Clear();
            importing = false;
            hubImports = null;
            ForgeNet.StatusChanged -= OnStatus;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            AlbieTools.Register("models", new Dictionary<string, AlbieTools.AsyncHandler>
            {
                ["model_search"] = Search,
                ["model_import"] = Import,
            });
            ForgeNet.StatusChanged += OnStatus;
        }

        // The hub's own model_import (hub/models/tools.py) checks the model, then asks the headset to bring it in.
        static void OnStatus(ForgeNetStatus status)
        {
            if (status != ForgeNetStatus.Live) return;
            hubImports?.Dispose();
            hubImports = ForgeNet.On("models.import", payload =>
            {
                string id = payload?.Type == JTokenType.Object ? (string)payload["id"] : null;
                if (string.IsNullOrEmpty(id) || importing) return;
                ImportById(id, result => Debug.Log($"[Models] models.import {id}: {result.ToString(Formatting.None)}"));
            });
        }

        /// <summary>Why models can't be reached right now, in words for the person, or null.</summary>
        public static string Unreachable()
        {
            if (ForgeCloud.Serves(SearchPath)) return null;
            return ForgeCloud.Ask() ?? "Finding real 3D models needs the hub or Albie's cloud, and neither is reachable right now. "
                + "The built-in templates and anything built from shapes still work.";
        }

        public static void Search(JObject args, Action<JObject> done)
        {
            string query = ((string)args["query"] ?? "").Trim();
            if (query.Length == 0) throw new ArgumentException("bad_value: say what to look for");
            int limit = 5;
            var asked = args["limit"];
            if (asked != null && (asked.Type == JTokenType.Integer || asked.Type == JTokenType.Float)) limit = Mathf.Clamp((int)(double)asked, 1, 10);
            string down = Unreachable();
            if (down != null) { done(AlbieTools.Error(down)); return; }
            ForgeNet.Get($"{SearchPath}?q={UnityWebRequest.EscapeURL(query)}&limit={limit}", text =>
            {
                JObject body;
                try { body = JObject.Parse(text); }
                catch (JsonException) { done(AlbieTools.Error("The model search answered with something unreadable.")); return; }
                offered.Clear();
                var hits = new JArray();
                if (body["hits"] is JArray found)
                    foreach (var hit in found)
                    {
                        if (!(hit is JObject h) || string.IsNullOrEmpty((string)h["id"])) continue;
                        offered.Add(h);
                        var line = new JObject { ["pick"] = offered.Count, ["id"] = h["id"], ["name"] = h["name"], ["author"] = h["author"], ["licence"] = h["licence"] };
                        if (h["triangles"] != null && h["triangles"].Type == JTokenType.Integer) line["triangles"] = h["triangles"];
                        if (h["size_bytes"] != null && h["size_bytes"].Type == JTokenType.Integer) line["size_mb"] = Math.Round((long)h["size_bytes"] / 1048576.0, 1);
                        hits.Add(line);
                    }
                var result = new JObject { ["query"] = query, ["hits"] = hits };
                if (hits.Count == 0)
                    result["say"] = string.IsNullOrEmpty((string)body["note"]) ? "Nothing open-licensed matched. Offer to try other words, or to build it from shapes." : (string)body["note"];
                else
                    result["next"] = "Say the top two or three by name and author and ask which one to bring in. Call model_import only for the one the person picks.";
                done(result);
            }, fail => done(AlbieTools.Error("The model search didn't work: " + Reason(fail))));
        }

        public static void Import(JObject args, Action<JObject> done)
        {
            // Only a model the last search offered: the person chose from those, and an id can't be made up.
            JObject hit = null;
            var pick = args["pick"];
            string id = (string)args["id"];
            if (!string.IsNullOrEmpty(id))
            {
                foreach (var h in offered) if ((string)h["id"] == id) hit = h;
            }
            else if (pick != null && (pick.Type == JTokenType.Integer || pick.Type == JTokenType.Float))
            {
                int n = (int)(double)pick;
                if (n >= 1 && n <= offered.Count) hit = offered[n - 1];
            }
            if (hit == null)
                throw new ArgumentException(offered.Count == 0
                    ? "not_offered: call model_search first, say the hits, and let the person pick one"
                    : "not_offered: give the id (or pick, 1 to " + offered.Count + ") of a hit from the last model_search");
            if (importing) throw new ArgumentException("busy: a model is already being brought in; wait for it");
            string down = Unreachable();
            if (down != null) { done(AlbieTools.Error(down)); return; }
            ImportById((string)hit["id"], done);
        }

        static void ImportById(string id, Action<JObject> done)
        {
            importing = true;
            void Finish(JObject result)
            {
                importing = false;
                done(result);
            }
            string query = "?id=" + UnityWebRequest.EscapeURL(id);
            // First the server downloads and checks it and says what it is; then the bytes, from its cache.
            ForgeNet.Get(FetchPath + query, text =>
            {
                ModelImport.Meta meta;
                try { meta = JsonConvert.DeserializeObject<ModelImport.Meta>(text); }
                catch (JsonException) { meta = null; }
                if (meta == null || meta.id != id)
                {
                    Finish(AlbieTools.Error("The server's answer wasn't that model."));
                    return;
                }
                ForgeNet.GetBytes(FilePath + query, bytes =>
                {
                    try { Finish(Save(meta, bytes)); }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        Finish(AlbieTools.Error("The model couldn't be saved: " + e.Message));
                    }
                }, fail => Finish(AlbieTools.Error("The model didn't download: " + Reason(fail))));
            }, fail => Finish(AlbieTools.Error("That model can't be used: " + Reason(fail))));
        }

        static JObject Save(ModelImport.Meta meta, byte[] bytes)
        {
            var made = ModelImport.Save(meta, bytes, out string problem);
            if (made == null) return AlbieTools.Error(problem);
            var result = new JObject
            {
                ["id"] = made.Id, ["version"] = made.Version, ["change"] = made.Change,
                ["name"] = meta.name, ["author"] = meta.author, ["licence"] = meta.licence, ["source_url"] = meta.source_url,
                ["next"] = "Say its name, who made it and its licence in one short sentence. The credit is on its panel; Turn and Reset view are there too.",
            };
            // Several meshes: it can most likely come apart, so Albie offers to (the explorer says if it turns out to be one piece).
            int meshes = ModelImportGlb.Inspect(bytes, out _)?.Meshes ?? 0;
            result["next"] = (string)result["next"] + (meshes > 1
                ? " It's made of several pieces: in the same sentence, ask 'want me to take it apart?' (artifact_explore separate, then isolate for one part)."
                : " It's one solid piece, so don't offer to take it apart.");
            try { result["opened"] = ArtifactTools.Open(new JObject { ["id"] = made.Id }); }
            catch (ArgumentException e) { result["opened"] = new JObject { ["status"] = "saved, not opened", ["why"] = e.Message }; }
            return result;
        }

        /// <summary>The server's own reason from a failed request ("404 ... {"detail": "not_found: ..."}"), or a plain one.</summary>
        public static string Reason(string fail)
        {
            if (string.IsNullOrEmpty(fail)) return "no answer";
            int brace = fail.IndexOf('{');
            if (brace >= 0)
            {
                try
                {
                    var detail = JObject.Parse(fail.Substring(brace))["detail"];
                    if (detail != null && detail.Type == JTokenType.String) return (string)detail;
                }
                catch (JsonException) { }
            }
            if (fail.StartsWith("0 ", StringComparison.Ordinal) || fail.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                return "the server didn't answer in time; it may still be fetching the model, so try once more";
            if (fail.StartsWith("no mock", StringComparison.Ordinal)) return "neither the hub nor Albie's cloud is reachable";
            return fail.Length > 200 ? fail.Substring(0, 200) : fail;
        }
    }
}
