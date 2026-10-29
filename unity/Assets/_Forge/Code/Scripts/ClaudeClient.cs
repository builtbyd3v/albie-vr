using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Claude from the headset, through the albie-cloud proxy (cloud/api/claude.ts), so the Anthropic key never ships in
/// the APK. The body is a Messages API request; the proxy allows only its listed models and caps max_tokens. The app
/// key is the one the voice token server uses (PlayerPrefs Albie.AppKey, else Resources/AlbieAppKey.txt).
/// Call from the main thread; the awaits resume there.
/// </summary>
public static class ClaudeClient
{
    public const string UrlPref = "Albie.ClaudeUrl";
    const string DefaultUrl = "https://albie-cloud.vercel.app/api/claude";
    public const string Model = "claude-sonnet-5-5";

    /// <summary>Test hook: when set, no request is sent; it gets the request body and returns the model's text.</summary>
    public static Func<JObject, string> Stub;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Stub = null;

    public static string Url => PlayerPrefs.GetString(UrlPref, DefaultUrl);

    /// <summary>
    /// One call that answers with JSON matching <paramref name="schema"/> (structured outputs). Throws with a short
    /// reason on any failure (network, refusal, cut off).
    /// </summary>
    public static async Task<JObject> Json(string system, string user, JObject schema, int maxTokens, string effort = "low")
    {
        var body = new JObject
        {
            ["model"] = Model,
            ["max_tokens"] = maxTokens,
            ["system"] = system,
            ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = user }),
            ["output_config"] = new JObject
            {
                ["effort"] = effort,
                ["format"] = new JObject { ["type"] = "json_schema", ["schema"] = schema },
            },
        };
        string text = Stub != null ? Stub(body) : TextOf(JObject.Parse(await Post(body.ToString(Formatting.None))));
        try { return JObject.Parse(text); }
        catch (JsonException) { throw new InvalidOperationException("the answer wasn't readable"); }
    }

    static string TextOf(JObject answer)
    {
        string stop = (string)answer["stop_reason"];
        if (stop == "refusal") throw new InvalidOperationException("the assistant declined that one");
        if (stop == "max_tokens") throw new InvalidOperationException("the answer was too long and got cut off");
        var text = new StringBuilder();
        foreach (var block in answer["content"] ?? new JArray())
            if ((string)block["type"] == "text") text.Append((string)block["text"]);
        if (text.Length == 0) throw new InvalidOperationException("the answer was empty");
        return text.ToString();
    }

    static string AppKey()
    {
        string key = PlayerPrefs.GetString(AlbieVoiceToken.AppKeyPref, "");
        if (key.Length > 0) return key;
        var asset = Resources.Load<TextAsset>("AlbieAppKey");
        return asset != null ? asset.text.Trim() : "";
    }

    static Task<string> Post(string json)
    {
        var done = new TaskCompletionSource<string>();
        var request = new UnityWebRequest(Url, "POST")
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = 90,
        };
        request.SetRequestHeader("Content-Type", "application/json");
        string key = AppKey();
        if (key.Length > 0) request.SetRequestHeader("x-albie-key", key);
        request.SendWebRequest().completed += _ =>
        {
            if (request.result == UnityWebRequest.Result.Success) done.SetResult(request.downloadHandler.text);
            else
            {
                string why = request.responseCode == 429 ? "too many requests, try again in a minute"
                    : request.responseCode == 401 ? "the app key was refused"
                    : $"{request.responseCode} {request.error}";
                Debug.LogWarning($"[ClaudeClient] {why}: {Trim(request.downloadHandler?.text)}");
                done.SetException(new InvalidOperationException("can't reach the assistant: " + why));
            }
            request.Dispose();
        };
        return done.Task;
    }

    static string Trim(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 300 ? s.Substring(0, 300) : s;
}
