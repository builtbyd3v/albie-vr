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
/// injected into ForgeNet as if the hub had sent them. The code panel's GitHub repos (/code/) come from here too.
/// Everything else stays on MockRest and the mocks.
/// <see cref="UrlPref"/> set to an empty string, or the cloud unreachable (no internet, paused, asleep): the mocks keep
/// working. <see cref="State"/> (<see cref="CloudHealth"/>) says which: a waking Space is re-probed every few seconds for
/// up to 90 s. Albie's CAD and ML voice tools (Resources/AlbieTools/cloud.json) then answer that the cloud is waking or
/// offline, and Albie says once when that changes. Installs itself.
/// </summary>
public class ForgeCloud : MonoBehaviour
{
    const string DefaultUrl = "https://builtbyd3v-albie-cloud.hf.space";  // cloud-py/deploy.sh; PlayerPrefs overrides it
    public const string UrlPref = "Albie.CloudUrl";      // e.g. http://192.168.1.20:8080 to test against a laptop
    public const string DevicePref = "Albie.DeviceId";
    static readonly string[] Prefixes = { "/cad/", "/ml/", "/code/", "/tools/", "/events" };
    const float PollS = 1f, RetryS = 30f, WakeProbeS = 5f;
    const int ProbeTimeoutS = 5;
    const string PetId = "bit";

    static ForgeCloud instance;
    static string deviceId, appKey;
    readonly CloudHealth health = new CloudHealth();
    int cursor, toolsRunning;
    bool polling, probed, caughtUp, probeNow;

    /// <summary>The cloud answered its health check (and the hub is off).</summary>
    public static bool Available => State == CloudState.Online;
    /// <summary>Online, Waking (a sleeping Space booting), Offline (paused, no network), or Unknown (not probed, or the hub serves it all).</summary>
    public static CloudState State => instance != null ? instance.health.State : CloudState.Unknown;
    /// <summary>One line for a panel that falls back to its mocks: the cloud is waking or offline; null otherwise.</summary>
    public static string StatusLine => instance != null && Configured && ForgeNet.IsMock ? instance.health.Status : null;
    /// <summary>The <see cref="State"/> changed.</summary>
    public static event Action<CloudState> StateChanged;
    /// <summary>The first health check has finished, either way (at once with no cloud URL).</summary>
    public static bool Probed => !Configured || (instance != null && instance.probed);

    public static string BaseUrl => PlayerPrefs.GetString(UrlPref, DefaultUrl).Trim().TrimEnd('/');
    public static bool Configured => BaseUrl.Length > 0;
    public static bool HasAppKey => !string.IsNullOrWhiteSpace(AppKey);

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
        if (instance == null || !Configured || !ForgeNet.IsMock)
        {
            done(AlbieTools.Error("That needs Albie's cloud, and it isn't reachable right now."));
            return;
        }
        string notReady = Ask();
        if (notReady != null) { done(AlbieTools.Error(notReady)); return; }
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

    /// <summary>The user wants a cloud feature (a voice tool, Train). Returns what to tell them when the cloud is waking
    /// or offline (then Albie also says when it's back), or null when it's Online or the hub serves it.</summary>
    public static string Ask()
    {
        if (instance == null || !Configured || !ForgeNet.IsMock) return null;
        string line = instance.health.Ask();
        if (line != null) instance.probeNow = true;  // check again now
        return line;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        appKey = null;
        StateChanged = null;
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
        var tick = new WaitForSecondsRealtime(0.5f);
        while (true)
        {
            while (ForgeNet.Status == ForgeNetStatus.Connecting) yield return null;
            var before = health.State;
            if (ForgeNet.Status == ForgeNetStatus.Live)  // the hub does it all: stand by
            {
                health.Standby();
                probed = true;
                if (before != health.State) StateChanged?.Invoke(health.State);
                yield return new WaitForSecondsRealtime(RetryS);
                continue;
            }
            long code = 0;
            string text = null;
            probeNow = false;
            yield return Send("GET", "/health", null, ProbeTimeoutS, (t, _) => { code = 200; text = t; }, (c, t) => { code = c; text = t; });
            string say = health.Report(code, text, Time.realtimeSinceStartup);
            probed = true;
            if (health.State != before)
            {
                Debug.Log($"[ForgeCloud] {BaseUrl}: {health.State} ({code})" + (health.State == CloudState.Online
                    ? $"; CAD and ML from the cloud (device {DeviceId})" : "; CAD and ML use demo data"));
                if (health.State == CloudState.Online)
                {
                    if (!polling) StartCoroutine(Poll());
                    ForgeNet.Inject("cloud.ready", new JObject { ["url"] = BaseUrl });
                }
                StateChanged?.Invoke(health.State);
            }
            if (say != null) Say(say);
            if (health.State == CloudState.Online)
                while (ForgeNet.IsMock && !probeNow) yield return tick;  // /events polling notices it dropping out
            else
                for (float waited = 0f, wait = health.State == CloudState.Waking ? WakeProbeS : RetryS;
                     waited < wait && !probeNow && ForgeNet.IsMock; waited += 0.5f)
                    yield return tick;
        }
    }

    // Albie says it: the on-device voice when it runs, else a caption (same path as AlbieTips).
    static void Say(string line)
    {
        Debug.Log("[ForgeCloud] " + line);
        if (ForgeNet.LocalPeer is AlbieVoice) ForgeNet.Send("albie.say", new JObject { ["text"] = line });
        else ForgeNet.Inject("pet.say", new JObject { ["pet_id"] = PetId, ["text"] = line });
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
        }, (_, __) => failures++);
        if (failures > 0) probeNow = true;  // Start() re-probes /health now
    }

    IEnumerator Tool(string name, JObject args, Action<JObject> done)
    {
        JObject answer = null;
        string error = null;
        long failed = -1;
        toolsRunning++;
        yield return Send("POST", "/tools/" + name, args.ToString(Formatting.None), 120,
            (text, _) => answer = JObject.Parse(text), (code, text) => { error = code + " " + text; failed = code; });
        if (answer == null)
        {
            toolsRunning--;
            bool dropped = failed == 0 || failed == 502 || failed == 503 || failed == 504;  // the Space, not the tool
            if (dropped) probeNow = true;
            done(AlbieTools.Error(dropped ? "Albie's cloud stopped answering. I'm checking on it; try again in a minute."
                                          : "The cloud didn't answer: " + error));
            yield break;
        }
        string show = (string)answer["show"];
        if (!string.IsNullOrEmpty(show) && AlbieTools.Has("ui_open_view"))
            AlbieTools.Call("ui_open_view", new JObject { ["view"] = show });
        yield return Events();  // the tool's own events (cad.model_updated ...) before Albie answers
        toolsRunning--;
        if (name == "build_start" && answer["result"]?["build_id"] != null)
            Forge.OS.OSWorkflowWindow.Open();
        done(answer["result"] as JObject ?? new JObject { ["ok"] = true });
    }

    // fail gets the HTTP status (0: no answer at all) and the error with the start of the answer's body.
    static IEnumerator Send(string method, string path, string body, int timeout, Action<string, byte[]> ok, Action<long, string> fail)
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
                string answer = request.downloadHandler.text ?? "";
                fail(request.responseCode, answer.Length == 0 ? request.error
                    : request.error + ": " + (answer.Length > 200 ? answer.Substring(0, 200) : answer));
                yield break;
            }
            try { ok(request.downloadHandler.text, request.downloadHandler.data); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
