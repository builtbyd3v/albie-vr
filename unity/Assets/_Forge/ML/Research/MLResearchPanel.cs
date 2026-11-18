using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace Forge.ML
{
    /// <summary>
    /// The ML desk's Research panel (id <c>ml-research</c>): model and dataset cards from the Hugging Face Hub, each
    /// with its fit checks (green good, amber warn, red bad, grey unknown), the recommended pick lifted out with its
    /// reasons, and "Use this model", which starts the real <c>ml_new</c> fine-tune of that base model (POST
    /// /ml/research/use) after a second confirm tap; the Training panel then shows the real curves.
    /// Cards stream in as <c>ml.research</c> events (hub/ml/research.py: start, card, update, done, error), from a
    /// search typed here (POST /ml/research/models|datasets) or one Albie ran (ml_search_models). Hub and cloud both
    /// serve it (<see cref="ForgeCloud.Serves"/>). With neither, it shows the bundled
    /// <c>Resources/MLResearchSample.json</c>, labelled SAMPLE RESULTS, and never offers to train from it.
    /// Built in code inside the panel's content; every button is a uGUI <see cref="ForgePill"/> (mouse, ray, poke).
    /// Nothing runs per frame: the panel rebuilds only when results change.
    /// </summary>
    [RequireComponent(typeof(ForgePanel))]
    public sealed class MLResearchPanel : MonoBehaviour
    {
        public const string PanelId = "ml-research";
        public static readonly Vector2 PanelSize = new Vector2(0.62f, 0.46f);
        const int PerPage = 4;
        const float Pad = 12f, BodyY = 70f;

        RectTransform root, body;
        TMP_InputField input;
        ForgePill modelsPill, datasetsPill;
        TMP_Text sourceLabel;
        float width, height;
        IDisposable researchEvents;

        string kind = "models";             // models | datasets
        string query = "";
        string searchId;
        string status = "";
        Color statusColor = MLUI.Sub;
        bool sample, searching;
        JObject done;                       // the "done" payload: recommended, recommended_why, error ...
        readonly List<JObject> cards = new List<JObject>();
        int page;
        string confirmId;                   // a card whose "Use this model" was tapped once: the confirm row shows
        bool starting;

        bool IsLive => ForgeCloud.Serves("/ml/");

        void OnEnable() { researchEvents = ForgeNet.On("ml.research", OnResearch); }
        void OnDisable() { researchEvents?.Dispose(); researchEvents = null; }

        void Start()
        {
            var panel = GetComponent<ForgePanel>();
            panel.CloseHandler = () => MLWorkspaceBridge.ShowResearch(false);  // the header X (it had a Close pill of its own)
            root = panel.Content;
            width = root.rect.width; height = root.rect.height;
            BuildChrome();
            if (cards.Count == 0 && !searching) { if (IsLive) Idle(); else LoadSample(); }
            Render();
        }

        void Idle()
        {
            sample = false;
            status = "Search the Hugging Face Hub: type a few words (\"sentiment\", \"news topics\") or ask Albie.";
            statusColor = MLUI.Sub;
        }

        // ---- chrome: built once ----
        void BuildChrome()
        {
            MLUI.Caps(root, "Research", Pad, Pad, 120);
            sourceLabel = MLUI.Caps(root, "", Pad + 90, Pad, width - 300);
            modelsPill = MLUI.Pill(root, "Models", width - 212, Pad - 4, 76, 22, () => SetKind("models"));
            datasetsPill = MLUI.Pill(root, "Datasets", width - 130, Pad - 4, 80, 22, () => SetKind("datasets"));
            input = MLUI.Input(root, "Query", "what to look for, e.g. sentiment", Pad, 34, width - 2 * Pad - 96, 26);
            input.onSubmit.AddListener(_ => Search());
            MLUI.Pill(root, "Search", width - Pad - 86, 34, 86, 26, Search);
            body = MLUI.Rect("Results", root, 0, BodyY, width, height - BodyY);
        }

        void SetKind(string value)
        {
            if (kind == value) return;
            kind = value; confirmId = null;
            if (!string.IsNullOrEmpty(input.text)) Search();
            else Render();
        }

        // ---- search ----
        void Search()
        {
            string words = input != null ? input.text.Trim() : "";
            if (words.Length == 0) { status = "Type what to look for first."; statusColor = MLUI.Warn; Render(); return; }
            if (!IsLive)
            {
                LoadSample();
                status = "Search needs Albie's hub or cloud, and neither is reachable. These are sample results, not a live search.";
                statusColor = MLUI.Warn;
                Render();
                return;
            }
            string notReady = ForgeCloud.Ask();  // the cloud is waking: say so instead of a timeout
            if (notReady != null) { status = notReady; statusColor = MLUI.Warn; Render(); return; }
            Begin(words, null);
            ForgeNet.Post("/ml/research/" + kind, new { query = words, limit = 8 }, text =>
            {
                JObject result;
                try { result = JObject.Parse(text); } catch (Exception) { return; }
                Finish(result);
            }, e =>
            {
                searching = false;
                status = "The search failed: " + Short(e); statusColor = MLUI.Bad;
                Render();
            });
            Render();
        }

        void Begin(string words, string id)
        {
            query = words; searchId = id; searching = true; sample = false; done = null;
            cards.Clear(); page = 0; confirmId = null;
            status = "Searching the Hub for \"" + words + "\"…"; statusColor = MLUI.Sub;
            if (input != null && input.text != words) input.SetTextWithoutNotify(words);
        }

        // The whole answer (the REST reply): every card with its details, in case an event was missed.
        void Finish(JObject result)
        {
            if (result["search_id"] != null && searchId != null && (string)result["search_id"] != searchId) return;
            searchId = (string)result["search_id"];
            kind = (string)result["kind"] ?? kind;
            if (result["cards"] is JArray all) { cards.Clear(); foreach (var c in all) if (c is JObject o) cards.Add(o); }
            Done(result);
        }

        void Done(JObject payload)
        {
            searching = false; done = payload;
            string error = (string)payload["error"];
            if (!string.IsNullOrEmpty(error)) { status = error; statusColor = MLUI.Bad; }
            else
            {
                status = cards.Count + (kind == "models" ? " models" : " datasets") + " for \"" + query + "\"" +
                         (payload["task"] != null && payload["task"].Type == JTokenType.String ? " · task " + payload["task"] : "") +
                         " · live from huggingface.co" + (Day(payload["fetched_at"]) != null ? " · " + Day(payload["fetched_at"]) : "");
                statusColor = MLUI.Sub;
            }
            Render();
        }

        void OnResearch(JToken token)
        {
            if (!(token is JObject e)) return;
            string phase = (string)e["phase"], id = (string)e["search_id"];
            if (phase == "start")
            {
                kind = (string)e["kind"] ?? kind;
                Begin((string)e["query"] ?? "", id);
                Render();
                return;
            }
            if (searchId == null && searching) searchId = id;  // our own search: its events come before the reply
            if (id != searchId) return;
            if ((phase == "card" || phase == "update") && e["card"] is JObject card) { Upsert(card); Render(); }
            else if (phase == "done") Done(e);
            else if (phase == "error") { searching = false; status = "The search failed: " + e["error"]; statusColor = MLUI.Bad; Render(); }
        }

        void Upsert(JObject card)
        {
            string id = (string)card["id"];
            for (int i = 0; i < cards.Count; i++)
                if ((string)cards[i]["id"] == id) { cards[i] = card; return; }
            cards.Add(card);
        }

        void LoadSample()
        {
            var asset = Resources.Load<TextAsset>("MLResearchSample");
            cards.Clear(); page = 0; confirmId = null; searching = false;
            sample = true;
            if (asset == null) { done = null; status = "No hub or cloud, and no sample results bundled."; statusColor = MLUI.Warn; return; }
            var result = JObject.Parse(asset.text);
            kind = "models";
            query = (string)result["query"] ?? "";
            if (result["cards"] is JArray all) foreach (var c in all) if (c is JObject o) cards.Add(o);
            done = result;
            status = "SAMPLE RESULTS (captured " + (Day(result["captured"]) ?? "earlier") + "), not a live search: connect the hub or Albie's cloud to search.";
            statusColor = MLUI.Warn;
        }

        // ---- use this model ----
        void AskUse(string id)
        {
            if (sample || !IsLive) { status = "These are sample results: connect the hub or Albie's cloud to fine-tune."; statusColor = MLUI.Warn; Render(); return; }
            confirmId = id; Render();
        }

        void Use(string id)
        {
            if (starting) return;
            starting = true; confirmId = null;
            status = "Starting the fine-tune of " + id + "…"; statusColor = MLUI.Sub;
            ForgeNet.Post("/ml/research/use", new { base_model = id, request = query }, text =>
            {
                starting = false;
                JObject r = null;
                try { r = JObject.Parse(text); } catch (Exception) { }
                string error = r == null ? "no answer" : (string)r["error"];
                if (!string.IsNullOrEmpty(error)) { status = "Couldn't start it: " + error; statusColor = MLUI.Bad; }
                else
                {
                    status = "Building " + (r["title"] ?? id) + (r["dataset"] != null ? " on " + r["dataset"] : "") +
                             ". The Training panel opens and its curves go live when the data and weights are in.";
                    statusColor = MLUI.Good;
                }
                Render();
            }, e => { starting = false; status = "Couldn't start it: " + Short(e); statusColor = MLUI.Bad; Render(); });
            Render();
        }

        // ---- render ----
        void Render()
        {
            if (body == null) return;
            MLUI.Clear(body);
            if (modelsPill != null) { modelsPill.On = kind == "models"; datasetsPill.On = kind == "datasets"; }
            if (sourceLabel != null)
            {
                sourceLabel.text = sample ? "SAMPLE RESULTS · NOT LIVE" : IsLive ? "LIVE · HUGGINGFACE.CO" : "OFFLINE";
                sourceLabel.color = sample ? MLUI.Warn : IsLive ? MLUI.Good : MLUI.Faint;
            }
            var line = MLUI.Text(body, status, Pad, 0, width - 2 * Pad, 28, 10.5f);
            line.color = statusColor; line.overflowMode = TextOverflowModes.Ellipsis;

            float y = 30;
            var pick = done?["recommended"] as JObject;
            string pickId = pick != null ? (string)pick["id"] : null;
            if (pick != null) y = Recommended(pick, y);
            else if (done != null && cards.Count > 0 && done["recommended_why"] != null)
            {
                var none = MLUI.Text(body, "No pick: " + done["recommended_why"], Pad, y, width - 2 * Pad, 28, 10.5f);
                none.color = MLUI.Warn; y += 30;
            }
            else if (cards.Count == 0 && !searching) return;

            int pages = Mathf.Max(1, (cards.Count + PerPage - 1) / PerPage);
            page = Mathf.Clamp(page, 0, pages - 1);
            float gridHeight = body.rect.height - y - (pages > 1 ? 30 : 4);
            float cardW = (width - 3 * Pad) / 2f, cardH = (gridHeight - Pad) / 2f;
            for (int i = 0; i < PerPage; i++)
            {
                int index = page * PerPage + i;
                if (index >= cards.Count) break;
                float x = Pad + (i % 2) * (cardW + Pad), cy = y + (i / 2) * (cardH + Pad);
                Card(cards[index], x, cy, cardW, cardH, (string)cards[index]["id"] == pickId);
            }
            if (searching && cards.Count < PerPage * (page + 1))
                MLUI.Caps(body, "more arriving…", Pad, body.rect.height - 22, 200).color = MLUI.Faint;
            if (pages > 1)
            {
                float py = body.rect.height - 26;
                MLUI.Pill(body, "‹", width - 150, py, 34, 22, () => { page--; confirmId = null; Render(); });
                MLUI.Text(body, (page + 1) + " / " + pages, width - 110, py + 4, 60, 18, 10.5f).alignment = TextAlignmentOptions.Center;
                MLUI.Pill(body, "›", width - 46, py, 34, 22, () => { page++; confirmId = null; Render(); });
            }
        }

        float Recommended(JObject pick, float y)
        {
            string id = (string)pick["id"];
            const float h = 74;
            var s = MLUI.Card(body, Pad, y, width - 2 * Pad, h);
            s.color = MLUI.CardLit;
            MLUI.Caps(body, "Best fit", Pad + 12, y + 8, 120).color = MLUI.Good;
            var title = MLUI.Text(body, id, Pad + 12, y + 22, width - 260, 20, 14);
            title.fontStyle = FontStyles.Bold; title.overflowMode = TextOverflowModes.Ellipsis; title.textWrappingMode = TextWrappingModes.NoWrap;
            var reasons = pick["reasons"] as JArray;
            string because = reasons == null ? "" : string.Join(" · ", reasons.ToObject<string[]>());
            var why = MLUI.Text(body, because + (done?["recommended_why"] != null ? "  (picked by: " + done["recommended_why"] + ")" : ""),
                                Pad + 12, y + 42, width - 2 * Pad - 190, 30, 9.5f);
            why.color = MLUI.Sub; why.overflowMode = TextOverflowModes.Ellipsis;
            if (pick["caveats"] is JArray caveats && caveats.Count > 0)
            {
                var but = MLUI.Text(body, "But: " + string.Join(" · ", caveats.ToObject<string[]>()), Pad + 12, y + 58, width - 2 * Pad - 190, 14, 9f);
                but.color = MLUI.Warn; but.overflowMode = TextOverflowModes.Ellipsis; but.textWrappingMode = TextWrappingModes.NoWrap;
                why.rectTransform.sizeDelta = new Vector2(why.rectTransform.sizeDelta.x, 16);
            }
            if (kind == "models") UseButtons(id, width - Pad - 170, y + 22, 160);
            else MLUI.Text(body, "ml_new takes it as the dataset", width - Pad - 170, y + 26, 160, 30, 9.5f).color = MLUI.Sub;
            return y + h + 8;
        }

        void UseButtons(string id, float x, float y, float w)
        {
            if (confirmId == id)
            {
                MLUI.Text(body, "Fine-tune on \"" + query + "\"?", x, y - 14, w, 14, 9f).color = MLUI.Warn;
                MLUI.Pill(body, "Start", x, y, w * 0.55f - 2, 24, () => Use(id));
                MLUI.Pill(body, "Cancel", x + w * 0.55f + 2, y, w * 0.45f - 2, 24, () => { confirmId = null; Render(); });
                return;
            }
            var pill = MLUI.Pill(body, sample ? "Sample only" : "Use this model", x, y, w, 24, () => AskUse(id));
            pill.Button.interactable = !sample && !starting;
        }

        void Card(JObject c, float x, float y, float w, float h, bool picked)
        {
            var surface = MLUI.Card(body, x, y, w, h, 12);
            if (picked) surface.color = MLUI.CardLit;
            string id = (string)c["id"];
            bool trainable = (bool?)c["trainable"] == true;
            var title = MLUI.Text(body, id, x + 10, y + 8, w - 110, 16, 11.5f);
            title.fontStyle = FontStyles.Bold; title.overflowMode = TextOverflowModes.Ellipsis; title.textWrappingMode = TextWrappingModes.NoWrap;
            string chip = (string)c["status"] == "browse_only" ? "BROWSE ONLY" : kind == "models" ? "CAN FINE-TUNE" : "USABLE";
            MLUI.Caps(body, chip, x + w - 100, y + 10, 92, 12, 8f).color = trainable ? MLUI.Good : MLUI.Faint;
            var facts = MLUI.Text(body, Facts(c), x + 10, y + 26, w - 20, 26, 9f);
            facts.color = MLUI.Sub; facts.overflowMode = TextOverflowModes.Ellipsis;

            float cy = y + 52, bottom = y + h - (trainable && kind == "models" ? 32 : 6);
            if (c["checks"] is JArray checks)
                foreach (var token in checks)
                {
                    if (cy + 12 > bottom) break;
                    string level = (string)token["level"];
                    MLUI.Dot(body, x + 10, cy + 3, 7, Level(level));
                    var t = MLUI.Text(body, (string)token["text"], x + 22, cy, w - 32, 24, 8.5f);
                    t.color = MLUI.TextColor; t.overflowMode = TextOverflowModes.Ellipsis;
                    cy += Mathf.Min(24, t.GetPreferredValues((string)token["text"], w - 32, 24).y + 2);
                }
            string details = (string)c["details"];
            if (details == "pending" && cy + 12 <= bottom) MLUI.Caps(body, "reading its files…", x + 10, cy, 160, 12, 8f).color = MLUI.Faint;
            if (trainable && kind == "models") UseButtons(id, x + w - 150, y + h - 30, 140);
        }

        static string Facts(JObject c)
        {
            var parts = new List<string>(8);
            if (c["kind"]?.ToString() == "dataset")
            {
                if (c["tasks"] is JArray tasks && tasks.Count > 0) parts.Add((string)tasks[0]);
                if (c["size_category"] != null && c["size_category"].Type == JTokenType.String) parts.Add((string)c["size_category"] + " rows");
            }
            else
            {
                parts.Add(c["task"] != null && c["task"].Type == JTokenType.String ? (string)c["task"] : "task not tagged");
                long? p = (long?)c["params"];
                parts.Add(p.HasValue ? MLUI.Count(p.Value) + " params" : "params unknown");
                double? mb = (double?)c["weights_mb"];
                if (mb.HasValue) parts.Add(mb.Value.ToString("0") + " MB");
            }
            parts.Add(c["license"] != null && c["license"].Type == JTokenType.String ? (string)c["license"] : "no licence stated");
            long? dl = (long?)c["downloads"];
            parts.Add(dl.HasValue ? MLUI.Count(dl.Value) + " downloads" : "downloads unknown");
            long? likes = (long?)c["likes"];
            if (likes.HasValue) parts.Add(MLUI.Count(likes.Value) + " likes");
            string updated = Day(c["last_modified"]);
            if (updated != null) parts.Add("updated " + updated);
            if (c["kind"]?.ToString() != "dataset") parts.Add("on-device latency not measured");
            return string.Join(" · ", parts);
        }

        // A date as yyyy-MM-dd: Json.NET may have parsed the ISO string into a Date token.
        static string Day(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Date) return ((DateTime)token).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            string text = token.ToString();
            return text.Length >= 10 ? text.Substring(0, 10) : text;
        }

        static Color Level(string level)
        {
            switch (level)
            {
                case "good": return MLUI.Good;
                case "warn": return MLUI.Warn;
                case "bad": return MLUI.Bad;
                default: return MLUI.Faint;
            }
        }

        static string Short(string error) => string.IsNullOrEmpty(error) ? "no answer" : error.Length > 160 ? error.Substring(0, 160) + "…" : error;
    }
}
