using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// A short-lived token for the realtime voice API (xAI <c>POST /v1/realtime/client_secrets</c>, answer
/// <c>{value, expires_at}</c>), so the API key never ships in the APK. The headset asks <see cref="TokenUrl"/>, a small
/// server that holds the key and returns that same JSON. In the Editor, with no token URL set, it mints one itself
/// from <c>XAI_API_KEY</c> in <c>hub/.env</c> (Editor only: that code isn't in a build).
/// </summary>
public static class AlbieVoiceToken
{
    public const string TokenUrlPref = "Albie.VoiceTokenUrl";
    const string ClientSecretsUrl = "https://api.x.ai/v1/realtime/client_secrets";
    const int LifetimeS = 300;

    const string DefaultTokenUrl = "https://albie-cloud.vercel.app/api/voice-token";

    /// <summary>The token server; a PlayerPrefs value under <see cref="TokenUrlPref"/> overrides the built-in one
    /// (the Editor has none built in: it mints from hub/.env).</summary>
    public static string TokenUrl => PlayerPrefs.GetString(TokenUrlPref, Application.isEditor ? "" : DefaultTokenUrl);

    // The token server's app key: PlayerPrefs, else Resources/AlbieAppKey.txt (git-ignored; the value is in Vercel's
    // ALBIE_APP_KEY). A build without it gets 401 from the server and Albie stays quiet.
    static string AppKey()
    {
        string key = PlayerPrefs.GetString(AppKeyPref, "");
        if (key.Length > 0) return key;
        var asset = Resources.Load<TextAsset>("AlbieAppKey");
        return asset != null ? asset.text.Trim() : "";
    }

    /// <summary>A fresh token, or an exception saying why there is none.</summary>
    public static async Task<string> Fetch()
    {
        string url = TokenUrl;
        if (string.IsNullOrEmpty(url))
        {
#if UNITY_EDITOR
            return Parse(await Send(ClientSecretsUrl, "POST", "{\"expires_after\":{\"seconds\":" + LifetimeS + "}}", EditorKey()));
#else
            throw new InvalidOperationException($"no voice token server set (PlayerPrefs {TokenUrlPref})");
#endif
        }
        return Parse(await Send(url, "POST", "{}", null, AppKey()));
    }

    /// <summary>Sent as <c>x-albie-key</c> to the token server (cloud/api/voice-token.ts, ALBIE_APP_KEY).</summary>
    public const string AppKeyPref = "Albie.AppKey";

    static string Parse(string json)
    {
        string value = (string)JObject.Parse(json)["value"];
        if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("the token answer has no value");
        return value;
    }

    static Task<string> Send(string url, string method, string body, string bearer, string appKey = null)
    {
        var done = new TaskCompletionSource<string>();
        var request = new UnityWebRequest(url, method)
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = 15,
        };
        request.SetRequestHeader("Content-Type", "application/json");
        if (bearer != null) request.SetRequestHeader("Authorization", "Bearer " + bearer);
        if (!string.IsNullOrEmpty(appKey)) request.SetRequestHeader("x-albie-key", appKey);
        request.SendWebRequest().completed += _ =>
        {
            if (request.result == UnityWebRequest.Result.Success) done.SetResult(request.downloadHandler.text);
            else done.SetException(new InvalidOperationException($"voice token: {request.responseCode} {request.error}"));
            request.Dispose();
        };
        return done.Task;
    }

#if UNITY_EDITOR
    // XAI_API_KEY from the hub's .env next to the Unity project (never printed, never in a build).
    static string EditorKey()
    {
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../../hub/.env"));
        if (System.IO.File.Exists(path))
            foreach (string line in System.IO.File.ReadAllLines(path))
                if (line.StartsWith("XAI_API_KEY="))
                    return line.Substring("XAI_API_KEY=".Length).Trim().Trim('"');
        throw new InvalidOperationException($"no XAI_API_KEY in {path}");
    }
#endif
}
