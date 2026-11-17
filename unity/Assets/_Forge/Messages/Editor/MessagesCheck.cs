using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Headless checks for the Messages panels in a preview scene: no hub, no headset. Forge > Messages > Check Messages
/// logs the report; <see cref="Run"/> returns it (for `unity command eval`). PASS on the first line means every step
/// passed. Run Forge > Messages > Build Prefabs first: the prefab steps read the built prefabs. The wire step runs in
/// Edit mode only (it feeds the static chat store, which Play mode is using).
/// </summary>
public static class MessagesCheck
{
    const string ResDir = "Assets/_Forge/Messages/Resources";
    const string ScriptsDir = "Assets/_Forge/Messages/Scripts";
    const string DroppedTag = "imessage";  // the hub's tag for the provider the app dropped (wire tolerance)
    static readonly string[] Brands =
    {
        "muse", "claude", "meta", "oculus", "quest", "grok", "github", "slack", "openai", "google",
        "imessage", "apple", "whatsapp", "discord", "telegram",
    };

    [MenuItem("Forge/Messages/Check Messages")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    /// <summary>For -executeMethod.</summary>
    public static void RunBatch()
    {
        string report = Run();
        Debug.Log(report);
        EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        int failed = 0;
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            failed += Step(sb, "No second-provider panel or button", NoDroppedPanel);
            failed += Step(sb, "Dropped-provider items are ignored without error", () => WireIgnored(scene));
            failed += Step(sb, "Body text contrast at least 4.5:1", Contrast);
            failed += Step(sb, "Draft card: exactly Send and Cancel, Send posts once", () => DraftCardOnce(scene));
            failed += Step(sb, "Draft is on Albie's sticker", () => DraftSticker(scene));
            failed += Step(sb, "Albie's picture: his proportions, never takes the pointer", () => AlbiePicture(scene));
            failed += Step(sb, "Fredoka loads, ellipsis included", Fredoka);
            failed += Step(sb, "No brand words in the panels' strings", () => NoBrands(scene));
        }
        finally
        {
            DraftCard.PostHook = null;
            EditorSceneManager.ClosePreviewScene(scene);
        }
        return (failed == 0 ? "PASS" : $"FAIL ({failed})") + " messages check (" + (Application.isPlaying ? "Play" : "Edit") + " mode)\n" + sb;
    }

    static int Step(StringBuilder sb, string name, Func<string> check)
    {
        string result;
        try { result = check(); }
        catch (Exception e) { result = "FAIL " + e.GetType().Name + ": " + e.Message; }
        sb.Append("  ").Append(name).Append(": ").Append(result).Append('\n');
        return result.StartsWith("FAIL") ? 1 : 0;
    }

    static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // ---- Steps -----------------------------------------------------------------------------------------------

    static string NoDroppedPanel()
    {
        Expect(AssetDatabase.LoadAssetAtPath<GameObject>(ResDir + "/IMessagePanel.prefab") == null, "IMessagePanel.prefab still exists");
        int panels = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { ResDir }))
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            foreach (var p in go.GetComponentsInChildren<ForgePanel>(true))
            {
                panels++;
                Expect(p.panelId == null || !p.panelId.Contains(DroppedTag), $"{go.name} has panel id {p.panelId}");
            }
            foreach (var t in go.GetComponentsInChildren<TMP_Text>(true))
                Expect(!Regex.IsMatch(t.text ?? "", @"\btexts?\b", RegexOptions.IgnoreCase), $"{go.name}/{t.name} says \"{t.text}\"");
        }
        var mode = Workspace.DefaultModes().Find(m => m.id == "messages");
        Expect(mode != null, "no messages mode in Workspace.DefaultModes");
        var ids = new List<string>();
        foreach (var item in mode.items)
        {
            ids.Add(item.id);
            Expect(!item.id.Contains(DroppedTag) && item.title != "Texts", $"MSG mode has {item.id} ({item.title})");
        }
        Expect(typeof(ChatPanel).GetField("iMessagePanel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) == null,
            "ChatPanel still has the provider switch");
        return $"{panels} Messages panels, MSG mode items: {string.Join(", ", ids)}";
    }

    static string WireIgnored(Scene scene)
    {
        if (Application.isPlaying) return "skipped in Play mode (it would feed the live chat store)";
        var reset = typeof(ChatStore).GetMethod("ResetStatics", BindingFlags.NonPublic | BindingFlags.Static);
        Expect(reset != null, "ChatStore.ResetStatics not found");
        var logs = new List<string>();
        Application.LogCallback capture = (message, stack, type) =>
        {
            if (type != LogType.Log || message.StartsWith("[ChatRemote]")) logs.Add(type + ": " + message);
        };
        reset.Invoke(null, null);
        Application.logMessageReceived += capture;
        var holder = new GameObject("WireCheck");
        SceneManager.MoveGameObjectToScene(holder, scene);
        var presenter = holder.AddComponent<DraftPresenter>();
        try
        {
            // The store's and the presenter's handlers for each event, fed the hub's JSON (ToObject, as ForgeEvents does).
            // Called directly rather than through ChatStore.Ensure, which would ask a hub for the list.
            void Raise(string type, JObject payload)
            {
                switch (type)
                {
                    case ForgeEvents.NotifyChannelsType: Static(typeof(ChatStore), "OnChannels", payload.ToObject<NotifyChannels>()); break;
                    case ForgeEvents.NotifyMessageType: Static(typeof(ChatStore), "OnMessage", payload.ToObject<NotifyMessage>()); break;
                    case ForgeEvents.NotifyDigestType: Static(typeof(ChatStore), "OnDigest", payload.ToObject<NotifyDigest>()); break;
                    case ForgeEvents.NotifySentType: Static(typeof(ChatStore), "OnSent", payload.ToObject<NotifySent>()); break;
                    case ForgeEvents.NotifyDraftType: Invoke(presenter, "OnDraft", payload.ToObject<NotifyDraft>()); break;
                }
            }

            Raise(ForgeEvents.NotifyChannelsType, JObject.Parse(@"{""channels"": [
                {""source"": ""slack"", ""id"": ""C1"", ""name"": ""general"", ""kind"": ""channel""},
                {""source"": ""imessage"", ""id"": ""im:+15550100"", ""name"": ""Sam"", ""kind"": ""dm"", ""unread"": 2},
                {""source"": ""imessage"", ""id"": ""X2"", ""name"": ""Pat"", ""kind"": ""dm""}]}"));
            Expect(ChatStore.Channels.Count == 1 && ChatStore.Channels[0].id == "C1",
                $"{ChatStore.Channels.Count} conversations listed, expected only C1");
            Expect(ChatStore.TotalUnread == 0, "the dropped conversation's unread count shows");

            var dropped = JObject.Parse(@"{""source"": ""imessage"", ""channel"": ""im:+15550100"", ""user"": ""u9"",
                ""user_name"": ""Sam"", ""text"": ""hi"", ""ts"": ""1700000000.000100"", ""mentions_me"": true}");
            Raise(ForgeEvents.NotifyMessageType, dropped);
            var taggedOnly = (JObject)dropped.DeepClone();
            taggedOnly["channel"] = "C1";
            taggedOnly["ts"] = "1700000000.000200";
            Raise(ForgeEvents.NotifyMessageType, taggedOnly);
            Raise(ForgeEvents.NotifyDigestType, new JObject { ["count"] = 1, ["line"] = "x", ["messages"] = new JArray(dropped) });
            Raise(ForgeEvents.NotifySentType, JObject.Parse(@"{""channel"": ""im:+15550100"", ""text"": ""ok"", ""ts"": ""1700000000.000300""}"));
            Expect(ChatStore.Recent.Count == 0, $"{ChatStore.Recent.Count} dropped messages kept");
            Expect(ChatStore.NameOf("u9") == null, "the dropped sender's name was learnt");

            Raise(ForgeEvents.NotifyDraftType, JObject.Parse(@"{""source"": ""imessage"", ""draft_id"": ""d9"",
                ""channel"": ""im:+15550100"", ""text"": ""on my way""}"));
            Expect(Field(presenter, "card") == null, "a card was made for the dropped draft");
            Expect(ChatPanel.ForSource(DroppedTag) == null && ChatPanel.ForChannel("im:+15550100") == null, "a panel claims the dropped provider");

            var remote = holder.AddComponent<ChatRemote>();
            Invoke(remote, "OnOpen", new ChatOpenRequest { requestId = "r1", channel = "im:+15550100", channelName = "Sam", kind = "dm" });
            Expect(logs.Count == 0, string.Join("; ", logs));
            return "channel list, message, digest, sent, draft and messages.open from the dropped provider: no row, no card, no log";
        }
        finally
        {
            Application.logMessageReceived -= capture;
            UnityEngine.Object.DestroyImmediate(holder);
            reset.Invoke(null, null);
        }
    }

    static string Contrast()
    {
        Color ground = MessagesLook.PanelGround;
        Color card = MessagesLook.Over(ChatUi.CardFill, ground);
        var pairs = new (string name, Color text, Color under)[]
        {
            ("card text on the slate card", ChatUi.TextColor, card),
            ("subtext on the slate card", ChatUi.Subtext, card),
            ("subtext on graphite", ChatUi.Subtext, ground),
            ("message text on the pane", ChatUi.MsgText, ChatUi.MsgPane),
            ("names on the pane", ChatUi.MsgName, ChatUi.MsgPane),
            ("sidebar text", ChatUi.MsgSideText, ChatUi.MsgWindow),
            ("Albie's sticker: ink on white", MessagesLook.Ink, MessagesLook.StickerWhite),
            ("files line on Albie's sticker", MessagesLook.InkSoft, MessagesLook.StickerWhite),
            ("mention mark: ink on lime", MessagesLook.Ink, MessagesLook.Lime),
            ("your @mention in lime on the card", MessagesLook.Lime, card),
            ("Send: ink on orange", MessagesLook.Ink, MessagesLook.Orange),
            ("Send pointed at: ink on orange", MessagesLook.Ink, MessagesLook.OrangeHover),
        };
        var parts = new List<string>();
        var low = new List<string>();
        foreach (var (name, text, under) in pairs)
        {
            float c = MessagesLook.Contrast(text, under);
            parts.Add($"{name} {c:0.0}");
            if (c < 4.5f) low.Add($"{name} {c:0.00}:1");
        }
        Expect(low.Count == 0, "below 4.5:1: " + string.Join(", ", low));
        return string.Join("; ", parts);
    }

    static DraftCard NewDraftCard(Scene scene)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ResDir + "/DraftCard.prefab");
        Expect(prefab != null, "no DraftCard.prefab (run Forge > Messages > Build Prefabs)");
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        var card = go.GetComponentInChildren<DraftCard>(true);
        Expect(card != null, "DraftCard.prefab has no DraftCard");
        return card;
    }

    static string DraftCardOnce(Scene scene)
    {
        var card = NewDraftCard(scene);
        var buttons = card.GetComponentsInChildren<Button>(true);
        var names = new List<string>();
        foreach (var b in buttons) names.Add(b.name);
        names.Sort(StringComparer.Ordinal);
        Expect(names.Count == 2 && names[0] == "Cancel" && names[1] == "Send", "buttons: " + string.Join(", ", names));
        Invoke(card, "Awake");  // wires the buttons, as in Play mode
        card.Bind(new NotifyDraft { draftId = "d1", channel = "C1", channelName = "general", text = "On it." });
        var posts = new List<string>();
        DraftCard.PostHook = posts.Add;
        Button send = null, cancel = null;
        foreach (var b in buttons) if (b.name == "Send") send = b; else cancel = b;
        send.onClick.Invoke();
        send.onClick.Invoke();   // a second tap while the first is on its way
        cancel.onClick.Invoke(); // and Cancel after Send
        Expect(posts.Count == 1 && posts[0] == "/messages/drafts/d1/send", "posts: " + string.Join(", ", posts));
        Expect(!send.interactable && !cancel.interactable, "buttons still live after Send");
        Expect(card.GetComponentsInChildren<Button>(true).Length == 2, "a new button appeared after Send (a second confirm step)");
        return "Send, Cancel; three taps posted once: " + posts[0];
    }

    static string DraftSticker(Scene scene)
    {
        var card = NewDraftCard(scene);
        var bubble = card.transform.GetComponentsInChildren<RectTransform>(true);
        ForgeSurface fill = null, shadow = null, border = null;
        foreach (var rt in bubble)
        {
            if (rt.name == "StickerFill") fill = rt.GetComponent<ForgeSurface>();
            else if (rt.name == "StickerShadow") shadow = rt.GetComponent<ForgeSurface>();
            else if (rt.name == "StickerBorder") border = rt.GetComponent<ForgeSurface>();
        }
        Expect(fill != null && shadow != null && border != null, "no sticker on the card (prefab built before the restyle? run Build Prefabs)");
        Expect(fill.color == MessagesLook.StickerWhite && shadow.color == MessagesLook.Lime && border.color == MessagesLook.Ink,
            "sticker colours: fill " + fill.color + ", shadow " + shadow.color + ", border " + border.color);
        Expect(shadow.Glow == 0f, "the sticker shadow is blurred");
        Vector2 offset = shadow.rectTransform.offsetMin;
        Expect(offset.x > 0f && offset.y < 0f, "the shadow isn't offset down-right");
        var body = fill.transform.parent.GetComponentInChildren<TMP_Text>(true);
        Expect(body != null && body.color == MessagesLook.Ink, "the draft text isn't ink on the sticker");
        return $"white fill, ink border, lime hard shadow {offset.x:0} mm down-right, ink text";
    }

    static string AlbiePicture(Scene scene)
    {
        var tex = MessagesLook.AlbiePortrait;
        Expect(tex != null, "no Resources/" + MessagesLook.AlbiePortraitPath + ".png");
        Expect(tex.width <= 2048 && tex.height <= 2048, $"texture {tex.width}x{tex.height}");
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(ResDir + "/ChatPanel.prefab");
        Expect(asset != null, "no ChatPanel.prefab (run Forge > Messages > Build Prefabs)");
        var chat = ((GameObject)PrefabUtility.InstantiatePrefab(asset, scene)).GetComponentInChildren<ChatPanel>(true);
        Invoke(chat, "Awake");
        var draft = NewDraftCard(scene);
        Invoke(draft, "Awake");
        var parts = new List<string>();
        foreach (var root in new Component[] { chat, draft })
            foreach (var img in root.GetComponentsInChildren<RawImage>(true))
            {
                if (img.texture != tex) continue;
                Vector2 size = img.rectTransform.sizeDelta;
                Expect(Mathf.Abs(size.x / size.y - MessagesLook.PortraitAspect) < 0.01f, $"{img.name} is {size.x:0}x{size.y:0} mm, squashed");
                parts.Add($"{img.name} {size.x:0}x{size.y:0} mm");
            }
        Expect(parts.Count == 2, "expected Albie on the chat panel and on the draft card, found: " + string.Join(", ", parts));
        var peek = chat.transform.Find("AlbiePeek");
        Expect(peek != null && peek.GetComponent<RectMask2D>() != null, "the peek isn't clipped at the panel's edge");
        foreach (string name in new[] { "AlbiePeek", "AlbieSummary" })
            foreach (var g in chat.transform.Find(name).GetComponentsInChildren<Graphic>(true))
                Expect(!g.raycastTarget, $"{name}/{g.name} takes the pointer");
        foreach (var img in draft.GetComponentsInChildren<RawImage>(true))
            Expect(!img.raycastTarget, $"{img.name} on the draft card takes the pointer");
        return string.Join(", ", parts) + "; no raycast targets; the peek is masked at the top edge";
    }

    static string Fredoka()
    {
        var font = Forge.Pet.AlbieCaption.DefaultFont();
        Expect(font != null, "no Fredoka (Pet/Resources/PetFonts/Fredoka-Medium)");
        const string used = "…‘’“”–—·•@#";
        var missing = new StringBuilder();
        foreach (char c in used) if (!font.HasCharacter(c, false, true)) missing.Append(c);
        Expect(missing.Length == 0, "Fredoka can't draw: " + missing);
        Expect(font.atlasWidth <= 2048 && font.atlasHeight <= 2048, $"atlas {font.atlasWidth}x{font.atlasHeight}");
        return $"{font.name}, {font.atlasWidth}x{font.atlasHeight} dynamic atlas, draws {used}";
    }

    static readonly Regex Literal = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"");
    static readonly Regex NameArg = new Regex("(ChatUi\\.\\w+|ChatButton\\.Create|new GameObject)\\([^,()]*,?\\s*\"([^\"]*)\"");
    static readonly Regex Tuple = new Regex("\\(\"\\w+\",");
    static readonly string[] WireLine = { "JsonProperty", "source", "Debug.Log", "Resources.Load", "/messages/", "FindProperty", ".Find(", "DroppedSource" };

    static string NoBrands(Scene scene)
    {
        var lines = new List<string>();
        // 1. What the panels draw: the prefabs' texts, and the UI the panels build in Awake.
        foreach (string prefab in new[] { "ChatPanel", "MentionsPanel", "DraftCard", "MessageCard" })
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>($"{ResDir}/{prefab}.prefab");
            if (asset == null) continue;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb is ChatPanel || mb is MentionsPanel) Invoke(mb, "Awake");
            foreach (var t in go.GetComponentsInChildren<TMP_Text>(true)) if (!string.IsNullOrEmpty(t.text)) lines.Add(t.text);
        }
        int drawn = lines.Count;
        // 2. The copy in the scripts (status lines, empty states): string literals, less names and wire values.
        foreach (string path in Directory.GetFiles(ScriptsDir, "*.cs"))
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.TrimStart().StartsWith("//") || line.TrimStart().StartsWith("///")) continue;
                bool wire = false;
                foreach (string w in WireLine) if (line.Contains(w)) { wire = true; break; }
                if (wire) continue;
                string scan = Tuple.Replace(NameArg.Replace(line, "$1("), "(");
                foreach (Match m in Literal.Matches(scan)) if (m.Groups[1].Value.Length > 0) lines.Add(m.Groups[1].Value);
            }
        // 3. The demo conversations shown with the hub off.
        var demo = Resources.Load<TextAsset>("MessagesDemo");
        if (demo != null) Strings(JToken.Parse(demo.text), lines);
        foreach (string line in lines)
        {
            string lower = line.ToLowerInvariant();
            foreach (string brand in Brands)
                Expect(!Regex.IsMatch(lower, "\\b" + brand + "\\b"), $"\"{brand}\" in \"{line}\"");  // whole words: "quest" is not in "question"
        }
        return $"{lines.Count} strings clean ({drawn} drawn by the built panels)";
    }

    static void Strings(JToken token, List<string> into)
    {
        if (token is JObject o) { foreach (var p in o.Properties()) if (p.Name != "source") Strings(p.Value, into); }
        else if (token is JArray a) { foreach (var t in a) Strings(t, into); }
        else if (token.Type == JTokenType.String) into.Add((string)token);
    }

    // ---- Reflection helpers (Unity messages don't run on edit-mode objects) ----

    static void Invoke(object target, string method, params object[] args)
    {
        var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (m == null) throw new Exception($"{target.GetType().Name}.{method} not found");
        m.Invoke(target, args.Length == 0 ? null : args);
    }

    static void Static(Type type, string method, object arg)
    {
        var m = type.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (m == null) throw new Exception($"{type.Name}.{method} not found");
        m.Invoke(null, new[] { arg });
    }

    static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(target);
}
