using System;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace Forge.ML
{
    /// <summary>
    /// "Open this pull request?" card on the ML desk (id <c>ml-publish</c>), the tap-able side of ml_publish's
    /// confirm (the DraftCard pattern). The hub sends <c>ml.publish_offer</c> when Albie describes the pull request
    /// (state awaiting: repo, whether it's new, branch, folder, files, whether the data goes in) and as it goes
    /// (publishing, opened, failed, cancelled). Confirm is the only thing here that posts: POST /ml/publish
    /// {confirm: true}. Cancel is POST /ml/publish/cancel (nothing is posted, and a later yes asks again).
    /// Saying "yes" to Albie does the same as Confirm; either way the card follows the events.
    /// Spawned by <see cref="MLWorkspaceBridge"/>; buttons are uGUI pills (mouse, ray, poke).
    /// </summary>
    [RequireComponent(typeof(ForgePanel))]
    public sealed class MLPublishCard : MonoBehaviour
    {
        public const string PanelId = "ml-publish";
        public static readonly Vector2 PanelSize = new Vector2(0.40f, 0.27f);
        const float Pad = 12f;

        /// <summary>Closes the card (the bridge removes it).</summary>
        public static event Action Dismissed;

        RectTransform root;
        float width, height;
        JObject offer;
        bool busy;
        string note;
        Color noteColor = MLUI.Sub;
        IDisposable events;

        void OnEnable() { events = ForgeNet.On("ml.publish_offer", OnOffer, true); }
        void OnDisable() { events?.Dispose(); events = null; }

        void Start()
        {
            var panel = GetComponent<ForgePanel>();
            panel.CloseHandler = () => Dismissed?.Invoke();  // its X is the Close below: the card goes, nothing is published
            root = panel.Content;
            width = root.rect.width; height = root.rect.height;
            Render();
        }

        /// <summary>Show this offer (the bridge passes the event that opened the card).</summary>
        public void Show(JObject payload) { OnOffer(payload); }

        void OnOffer(JToken token)
        {
            if (!(token is JObject e)) return;
            string state = (string)e["state"];
            if (state == "awaiting" || offer == null) offer = e;
            else foreach (var p in e) offer[p.Key] = p.Value;
            busy = state == "publishing";
            switch (state)
            {
                case "awaiting": note = "Nothing is posted until you tap Confirm."; noteColor = MLUI.Sub; break;
                case "publishing": note = "Opening the pull request…"; noteColor = MLUI.Sub; break;
                case "opened": note = "Opened pull request #" + e["number"] + ". It's in the code panel."; noteColor = MLUI.Good; break;
                case "failed": note = "Not opened: " + e["error"]; noteColor = MLUI.Bad; break;
                case "cancelled": note = "Cancelled. Nothing was posted."; noteColor = MLUI.Sub; break;
            }
            Render();
        }

        void Confirm()
        {
            if (busy || offer == null) return;
            busy = true; note = "Opening the pull request…"; noteColor = MLUI.Sub; Render();
            ForgeNet.Post("/ml/publish", new { confirm = true }, text =>
            {
                busy = false;
                JObject r = null;
                try { r = JObject.Parse(text); } catch (Exception) { }
                string error = r == null ? "no answer" : (string)r["error"];
                if (!string.IsNullOrEmpty(error)) { note = "Not opened: " + error; noteColor = MLUI.Bad; }
                else if ((string)r["status"] == "awaiting_confirmation")
                { offer = r; note = "Check the pull request again, then tap Confirm."; noteColor = MLUI.Warn; }
                else if ((string)r["status"] == "opened")
                { offer["state"] = "opened"; note = "Opened pull request #" + r["number"] + ". It's in the code panel."; noteColor = MLUI.Good; }
                Render();
            }, e => { busy = false; note = "Not opened: " + e; noteColor = MLUI.Bad; Render(); });
        }

        void Cancel()
        {
            if (busy) return;
            string state = offer == null ? null : (string)offer["state"];
            if (state == "awaiting")
                ForgeNet.Post("/ml/publish/cancel", null, null, e => Debug.LogWarning("[ML] Publish cancel failed: " + e));
            Dismissed?.Invoke();
        }

        void Render()
        {
            if (root == null) return;
            MLUI.Clear(root);
            MLUI.Caps(root, "Open a pull request?", Pad, Pad, width - 2 * Pad);
            if (offer == null)
            {
                MLUI.Text(root, "No pull request is waiting. Ask Albie to put the ML project on GitHub.", Pad, 32, width - 2 * Pad, 40, 11).color = MLUI.Sub;
                MLUI.Pill(root, "Close", width - Pad - 80, height - Pad - 26, 80, 26, () => Dismissed?.Invoke());
                return;
            }
            string state = (string)offer["state"];
            var title = MLUI.Text(root, (string)offer["title"] ?? "ML project", Pad, 30, width - 2 * Pad, 20, 14);
            title.fontStyle = FontStyles.Bold; title.overflowMode = TextOverflowModes.Ellipsis;
            bool created = (bool?)offer["create_repo"] == true;
            string where = "Repo: " + offer["repo"] + (created ? "  (new repo, created on Confirm)" : "") +
                           "\nBranch: " + offer["branch"] + "   Folder: " + offer["folder"] +
                           "\n" + offer["files"] + " files" + ((bool?)offer["with_data"] == true ? ", the data included" : ", data left to download_data.py");
            MLUI.Text(root, where, Pad, 54, width - 2 * Pad, 46, 10.5f).color = MLUI.TextColor;
            if (offer["paths"] is JArray paths && paths.Count > 0)
            {
                var list = MLUI.Text(root, string.Join("  ", paths.ToObject<string[]>()), Pad, 102, width - 2 * Pad, 30, 9f);
                list.color = MLUI.Sub; list.overflowMode = TextOverflowModes.Ellipsis;
            }
            var line = MLUI.Text(root, note ?? "", Pad, height - Pad - 50, width - 2 * Pad, 20, 10.5f);
            line.color = noteColor; line.overflowMode = TextOverflowModes.Ellipsis;
            float y = height - Pad - 26;
            if (state == "awaiting" || state == "failed")
            {
                var confirm = MLUI.Pill(root, "Confirm", width - Pad - 190, y, 100, 26, Confirm);
                confirm.Button.interactable = !busy;
                MLUI.Pill(root, "Cancel", width - Pad - 84, y, 84, 26, Cancel).Button.interactable = !busy;
            }
            else MLUI.Pill(root, "Close", width - Pad - 80, y, 80, 26, () => Dismissed?.Invoke()).Button.interactable = !busy;
        }
    }
}
