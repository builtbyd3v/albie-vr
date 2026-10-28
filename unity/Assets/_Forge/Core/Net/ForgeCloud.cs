using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Hub off: the CAD and ML features run on Albie's cloud server (cloud-py/app.py) instead of the laptop. While
/// <see cref="ForgeNet"/> is in Mock mode and the cloud answers, REST paths under <see cref="Prefixes"/> go to
/// <see cref="BaseUrl"/> with the app key (<c>x-albie-key</c>) and this headset's id (<c>x-albie-device</c>), and the
/// events the hub would broadcast (cad.model_updated, ml.metrics, pet.say ...) are polled from <c>GET /events</c> and
/// injected into ForgeNet as if the hub had sent them. Everything else stays on MockRest and the mocks.
/// No <see cref="UrlPref"/> set, or the cloud unreachable (no internet, asleep): nothing changes, the mocks keep
/// working. Albie's CAD and ML voice tools (Resources/AlbieTools/cloud.json) then answer that they need the cloud.
/// Installs itself.
/// </summary>
public class ForgeCloud : MonoBehaviour
{
    // ponytail: no built-in default until the Space exists (https://<hf-user>-albie-cloud.hf.space, cloud-py/deploy.sh)
    public const string UrlPref = "Albie.CloudUrl";      // e.g. http://192.168.1.20:8080 to test against a laptop
    public const string DevicePref = "Albie.DeviceId";
    static readonly string[] Prefixes = { "/cad/", "/ml/", "/tools/", "/events" };
    const float PollS = 1f, RetryS = 30f;

    static ForgeCloud instance;
    static string deviceId, appKey;
    int cursor, toolsRunning;
    bool polling, probed, caughtUp;

    /// <summary>The cloud answered its health check (and the hub is off).</summary>
    public static bool Available { get; private set; }
    /// <summary>The first health check has finished, either way (at once with no cloud URL).</summary>
    public static bool Probed => !Configured || (instance != null && instance.probed);

    public static string BaseUrl => PlayerPrefs.GetString(UrlPref, "").Trim().TrimEnd('/');
    public static bool Configured => BaseUrl.Length > 0;

    public static string DeviceId
    {
        get
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                deviceId = PlayerPrefs.GetString(DevicePref, "");
                if (deviceId.Length < 8)
                {
                    deviceId = Guid.NewGuid().ToString("N");
                    PlayerPrefs.SetString(DevicePref, deviceId);
                    PlayerPrefs.Save();
                }
            }
            return deviceId;
        }
    }

    // Same key as the voice token server (AlbieVoiceToken): PlayerPrefs Albie.AppKey, else Resources/AlbieAppKey.txt.
    static string AppKey
    {
        get
        {
            if (appKey == null)
            {
                appKey = PlayerPrefs.GetString(AlbieVoiceToken.AppKeyPref, "");
                if (appKey.Length == 0)
                {
                    var asset = Resources.Load<TextAsset>("AlbieAppKey");
                    appKey = asset != null ? asset.text.Trim() : "";
                }
            }
            return appKey;
        }
    }

    /// <summary>This path is served by the cloud right now (hub off, cloud up, a cloud prefix).</summary>
    public static bool Routes(string path)
    {
        if (!Available || !ForgeNet.IsMock || string.IsNullOrEmpty(path)) return false;
        if (!path.StartsWith("/")) path = "/" + path;
        foreach (var prefix in Prefixes)
            if (path.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The hub or the cloud serves this path for real (not mocks).</summary>
    public static bool Serves(string path) => ForgeNet.Status == ForgeNetStatus.Live || Routes(path);

    /// <summary>Add the cloud's headers to a request aimed at it (any other request is left alone).</summary>
    public static void Authorize(UnityWebRequest request)
    {
        if (request == null || !Configured || !request.url.StartsWith(BaseUrl, StringComparison.Ordinal)) return;
        request.SetRequestHeader("x-albie-key", AppKey);
        request.SetRequestHeader("x-albie-device", DeviceId);
    }

    /// <summary>Run one of Albie's cloud tools (POST /tools/{name}); <paramref name="done"/> gets its result, or
    /// {"error"} when the cloud can't be reached. Opens the view the tool asked for, and fetches its events at once.</summary>
    public static void CallTool(string name, JObject args, Action<JObject> done)
    {
        if (instance == null || !Available)
        {
            done(AlbieTools.Error("Changing 3D designs and training models needs Albie's cloud, and it isn't reachable right now."));
            return;
        }
        instance.StartCoroutine(instance.Tool(name, args ?? new JObject(), done));
    }

    // Albie's CAD and ML tools (hub/cad/tools.py, hub/ml/tools.py), each one a POST /tools/{name}.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void RegisterTools()
    {
        var asset = Resources.Load<TextAsset>("AlbieTools/cloud");
        if (asset == null) return;  // Register says it's missing
        var handlers = new System.Collections.Generic.Dictionary<string, AlbieTools.AsyncHandler>();
        foreach (JObject def in JArray.Parse(asset.text))
        {
            string name = (string)def["name"];
            handlers[name] = (args, done) => CallTool(name, args, done);
        }
        AlbieTools.Register("cloud", handlers);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        Available = false;
        appKey = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (instance != null) return;
        var go = new GameObject("ForgeCloud");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<ForgeCloud>();
    }

    IEnumerator Start()
    {
        if (!Configured) yield break;  // today's mock behavior
        _ = ForgeNet.Instance;
        while (true)
        {
            while (ForgeNet.Status == ForgeNetStatus.Connecting) yield return null;
            if (ForgeNet.Status == ForgeNetStatus.Live)  // the hub does it all: stand by
            {
                Available = false;
                probed = true;
                yield return new WaitForSecondsRealtime(RetryS);
                continue;
            }
            bool ok = false;
            yield return Send("GET", "/health", null, 8, (_, __) => ok = true, _ => { });
            if (ok != Available) Debug.Log(ok ? $"[ForgeCloud] CAD and ML from the cloud at {BaseUrl} (device {DeviceId})"
                                              : $"[ForgeCloud] cloud at {BaseUrl} unreachable; CAD and ML use demo data");
            Available = ok;
            probed = true;
            if (ok && !polling) StartCoroutine(Poll());
            if (ok) ForgeNet.Inject("cloud.ready", new JObject { ["url"] = BaseUrl });
            yield return new WaitForSecondsRealtime(RetryS);
            while (Available && ForgeNet.IsMock) yield return new WaitForSecondsRealtime(RetryS);
        }
    }

    IEnumerator Poll()
    {
        polling = true;
        var wait = new WaitForSecondsRealtime(PollS);
        while (Available && ForgeNet.IsMock)
        {
            yield return Events();
            yield return wait;
        }
        polling = false;
    }

    IEnumerator Events()
    {
        int failures = 0;
        bool backlog = !caughtUp;  // the first batch is history: an earlier session's lines aren't shown or said
        yield return Send("GET", "/events?after=" + cursor, null, 10, (text, _) =>
        {
            var answer = JObject.Parse(text);
            if ((bool?)answer["reset"] == true) cursor = 0;
            caughtUp = true;
            foreach (JObject e in (JArray)answer["events"])
            {
                int seq = (int)e["seq"];
                if (seq <= cursor) continue;
                cursor = seq;
                string type = (string)e["type"];
                if (backlog && type == "pet.say") continue;
                ForgeNet.Inject(type, e["payload"]);
                // A slow tool's line (an ML proposal, a finished run): the on-device voice says it (AlbieVoice).
                // ponytail: while a tool call runs, its own lines are captions; Albie answers that tool himself.
                if (type == "pet.say" && toolsRunning == 0 && !string.IsNullOrEmpty((string)e["payload"]?["text"]))
                    ForgeNet.Send("pet.speak", e["payload"]);
            }
        }, _ => failures++);
        if (failures > 0) Available = false;  // Start() re-probes
    }

    IEnumerator Tool(string name, JObject args, Action<JObject> done)
    {
        JObject answer = null;
        string error = null;
        toolsRunning++;
        yield return Send("POST", "/tools/" + name, args.ToString(Formatting.None), 120,
            (text, _) => answer = JObject.Parse(text), e => error = e);
        if (answer == null)
        {
            toolsRunning--;
            done(AlbieTools.Error("The cloud didn't answer: " + error));
            yield break;
        }
        string show = (string)answer["show"];
        if (!string.IsNullOrEmpty(show) && AlbieTools.Has("ui_open_view"))
            AlbieTools.Call("ui_open_view", new JObject { ["view"] = show });
        yield return Events();  // the tool's own events (cad.model_updated ...) before Albie answers
        toolsRunning--;
        done(answer["result"] as JObject ?? new JObject { ["ok"] = true });
    }

    static IEnumerator Send(string method, string path, string body, int timeout, Action<string, byte[]> ok, Action<string> fail)
    {
        using (var request = new UnityWebRequest(BaseUrl + path, method))
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            if (body != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.SetRequestHeader("Content-Type", "application/json");
            }
            request.timeout = timeout;
            Authorize(request);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                fail($"{request.responseCode} {request.error}");
                yield break;
            }
            try { ok(request.downloadHandler.text, request.downloadHandler.data); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
