#if DEVELOPMENT_BUILD || UNITY_EDITOR
// Debug bridge commands for Albie's ears and mouth, so a laptop can test voice on a headset nobody wears:
//   voice          report the voice state (changes nothing)
//   mic_inject     feed a WAV in where microphone PCM enters the voice path (needs an open session; never opens one)
//   audio_capture  record what the app plays (tap on the AudioListener), judge it, hand the WAV back
//   tone           play a test tone through the app's output
// Same security note as DebugBridge.cs: Editor and development builds only, loopback only.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class DebugBridgeAudio : MonoBehaviour
{
    // Every mic reader hands on 16 kHz PCM16 mono in 100 ms chunks (HubSpeechToText.TargetRate, WakeListener.Rate,
    // AlbieVoice.InputRate): WakeListener / HubSpeechToText -> AgentClient.SendAudio -> ForgeNet.SendBinary ->
    // ForgeNet.LocalPeer (AlbieVoice).OnBinary -> input_audio_buffer.append. mic_inject enters at ForgeNet.SendBinary.
    public const int MicRate = 16000;
    const int ChunkBytes = MicRate / 10 * 2;
    const string PetId = "bit";
    const int MaxCaptureS = 120;
    const int MaxGetBytes = 4 * 1024 * 1024;
    const double SilentDb = -60, FloorDb = -120;
    const double GapS = 0.15;
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    public static DebugBridgeAudio Host { get; private set; }

    readonly List<IDisposable> subs = new List<IDisposable>();
    string heard, said, error, mode;
    double heardAt = -1, saidAt = -1, errorAt = -1, ttsStartAt = -1;
    bool injecting;
    AudioSource toneSource;
    DebugBridgeAudioTap tap;

    static double Now => Math.Round(Time.realtimeSinceStartupAsDouble, 3);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        var go = new GameObject("DebugBridgeAudio") { hideFlags = HideFlags.HideInHierarchy };
        DontDestroyOnLoad(go);
        Host = go.AddComponent<DebugBridgeAudio>();
        DebugBridge.Register("voice", (req, ok, fail) => ok(Host.Voice()));
        DebugBridge.Register("mic_inject", (req, ok, fail) => Host.MicInject(req, ok, fail));
        DebugBridge.Register("audio_capture", (req, ok, fail) => Host.Capture(req, ok, fail));
        DebugBridge.Register("tone", (req, ok, fail) => Host.Tone(req, ok, fail));
    }

    void Awake()
    {
        // The voice events, as the pet sees them (hub or on-device voice alike), with when they arrived.
        subs.Add(ForgeNet.On("stt.final", p => { heard = (string)p?["text"]; heardAt = Now; }));
        subs.Add(ForgeNet.On("agent.answer_done", p => { said = (string)p?["text"]; saidAt = Now; }));
        subs.Add(ForgeNet.On("agent.error", p => { error = (string)p?["message"]; errorAt = Now; }));
        subs.Add(ForgeNet.On("tts.start", p => ttsStartAt = Now));
        subs.Add(ForgeNet.On("pet.state", p => mode = (string)p?["mode"]));
    }

    void OnDestroy()
    {
        foreach (var s in subs) s?.Dispose();
        subs.Clear();
        if (tap != null) Destroy(tap);
        if (Host == this) Host = null;
    }

    // ---------------------------------------------------------------- voice

    JObject Voice()
    {
        var voice = FindAnyObjectByType<AlbieVoice>();
        var o = new JObject
        {
            ["now"] = Now,
            ["present"] = voice != null,
            ["session"] = voice != null ? SessionOf(voice) : "closed",
            ["mode"] = mode,
            ["heard"] = heard, ["heard_at"] = heardAt,
            ["said"] = said, ["said_at"] = saidAt,
            ["error"] = error, ["error_at"] = errorAt,
            ["tts_start_at"] = ttsStartAt,
            ["speaking"] = Speaking(),
            ["injecting"] = injecting,
        };
        if (voice != null)
        {
            o["turn_open"] = (bool)Field(voice, "turnOpen");
            o["response_active"] = (bool)Field(voice, "responseActive");
            o["audio_started"] = (bool)Field(voice, "audioStarted");
        }
        o["mic"] = Mic();
        return o;
    }

    // "open": a live (billed) session. "connecting": one is being opened. "closed": none.
    static string SessionOf(AlbieVoice voice)
    {
        var ws = Field(voice, "ws") as ClientWebSocket;
        var connecting = Field(voice, "connecting") as Task<bool>;
        if (ws != null && ws.State == WebSocketState.Open) return "open";
        return connecting != null && !connecting.IsCompleted ? "connecting" : "closed";
    }

    // Albie's voice still coming out of the speaker (the reply plays on after response.done).
    static bool Speaking()
    {
        foreach (var tts in FindObjectsByType<StreamedPcmTextToSpeech>(FindObjectsSortMode.None))
            if (tts.IsTalking) return true;
        return false;
    }

    static JObject Mic()
    {
        string[] devices = Microphone.devices;
        var o = new JObject { ["devices"] = new JArray(devices), ["recording"] = devices.Length > 0 && Microphone.IsRecording(null) };
        // The app's mic readers each hold the Microphone clip they started; whichever is running is the one to read.
        AudioClip clip = null;
        string reader = null;
        var wake = FindAnyObjectByType<WakeListener>();
        if (wake != null && (clip = Field(wake, "_clip") as AudioClip) != null) reader = "WakeListener";
        if (clip == null)
            foreach (var stt in FindObjectsByType<HubSpeechToText>(FindObjectsSortMode.None))
                if ((clip = Field(stt, "_clip") as AudioClip) != null) { reader = "HubSpeechToText"; break; }
        o["reader"] = reader;
        o["level_dbfs"] = null;
        if (clip == null || clip.samples == 0) return o;
        int pos = Microphone.GetPosition(null);
        int n = Mathf.Min(clip.samples, Mathf.Max(1, clip.frequency / 5));  // the last ~200 ms
        var buf = new float[n * clip.channels];
        clip.GetData(buf, ((pos - n) % clip.samples + clip.samples) % clip.samples);  // GetData wraps around the clip end
        double sum = 0;
        for (int i = 0; i < buf.Length; i += clip.channels) sum += buf[i] * buf[i];
        o["level_dbfs"] = Db(Math.Sqrt(sum / n));
        o["rate"] = clip.frequency;
        return o;
    }

    // ---------------------------------------------------------------- mic_inject

    void MicInject(JObject req, Action<JObject> ok, Action<string> fail)
    {
        var voice = FindAnyObjectByType<AlbieVoice>();
        if (voice == null) { fail("no open voice session: Albie's voice is off (no AlbieVoice). mic_inject never opens a session"); return; }
        string session = SessionOf(voice);
        if (session != "open")
        {
            fail($"no open voice session (session {session}). mic_inject never opens one: a live session costs money, so open it on purpose first");
            return;
        }
        if (injecting) { fail("mic_inject is already feeding audio"); return; }
        if ((bool)Field(voice, "turnOpen")) { fail("a turn is already open (the app is listening to someone): try again when it ends"); return; }

        byte[] wav;
        string b64 = (string)req["wav_b64"], path = (string)req["path"];
        if (!string.IsNullOrEmpty(b64)) wav = Convert.FromBase64String(b64);
        else if (!string.IsNullOrEmpty(path))
        {
            string root = Path.GetFullPath(Application.persistentDataPath) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(root, StringComparison.Ordinal)) { fail($"path must be under {root}"); return; }
            if (!File.Exists(full)) { fail($"no such file: {full}"); return; }
            wav = File.ReadAllBytes(full);
        }
        else { fail("mic_inject needs \"wav_b64\" or \"path\""); return; }

        string err = DecodeWav(wav, out short[] samples, out int rate);
        if (err != null) { fail(err); return; }
        byte[] pcm = ToPcm16(Resample(samples, rate, MicRate));
        bool realtime = req["realtime"]?.Type != JTokenType.Boolean || (bool)req["realtime"];
        StartCoroutine(Feed(pcm, realtime, () => SessionOf(voice) == "open",
            (type, payload) => ForgeNet.Send(type, payload), (d, off, n) => ForgeNet.SendBinary(d, off, n),
            r => { r["source_rate"] = rate; ok(r); }, fail));
    }

    /// <summary>One turn as a mic reader sends it: stt.start, 100 ms PCM16 chunks (at real-time pace when asked), stt.stop.
    /// Stops without stt.stop if <paramref name="stillOpen"/> turns false, so nothing sent can open a new session.
    /// The senders are parameters so a check can run it against a fake peer.</summary>
    public IEnumerator Feed(byte[] pcm, bool realtime, Func<bool> stillOpen, Func<string, JObject, bool> sendEvent,
        Func<byte[], int, int, bool> sendBinary, Action<JObject> ok, Action<string> fail)
    {
        injecting = true;
        try
        {
            if (!sendEvent("stt.start", new JObject { ["pet_id"] = PetId, ["sample_rate"] = MicRate, ["encoding"] = "pcm16" }))
            {
                fail("stt.start was dropped (ForgeNet not connected)");
                yield break;
            }
            double duration = pcm.Length / 2.0 / MicRate;
            double start = Time.realtimeSinceStartupAsDouble;
            int sent = 0, chunks = 0;
            while (true)
            {
                if (!stillOpen())
                {
                    fail($"the voice session closed after {sent / 2.0 / MicRate:0.00} s of audio: stopped feeding, no stt.stop sent");
                    yield break;
                }
                double elapsed = Time.realtimeSinceStartupAsDouble - start;
                while (sent < pcm.Length && (!realtime || sent / 2.0 / MicRate <= elapsed))
                {
                    int n = Math.Min(ChunkBytes, pcm.Length - sent);
                    sendBinary(pcm, sent, n);
                    sent += n;
                    chunks++;
                }
                if (sent >= pcm.Length && (!realtime || elapsed >= duration)) break;
                yield return null;
            }
            sendEvent("stt.stop", new JObject { ["pet_id"] = PetId });
            ok(new JObject
            {
                ["samples"] = pcm.Length / 2, ["rate"] = MicRate, ["duration_s"] = Math.Round(duration, 3), ["chunks"] = chunks,
                ["realtime"] = realtime, ["t_end"] = Now,
            });
        }
        finally { injecting = false; }
    }

    /// <summary>16-bit PCM mono RIFF/WAVE only. Returns null or the reason it can't be used.</summary>
    public static string DecodeWav(byte[] b, out short[] samples, out int rate)
    {
        samples = null;
        rate = 0;
        if (b == null || b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
            return "not a RIFF/WAVE file";
        int channels = 0, bits = 0, format = 0;
        for (int p = 12; p + 8 <= b.Length;)
        {
            string id = Encoding.ASCII.GetString(b, p, 4);
            int size = BitConverter.ToInt32(b, p + 4);
            int body = p + 8;
            if (size < 0 || body + size > b.Length) size = b.Length - body;  // a truncated last chunk: take what is there
            if (id == "fmt " && size >= 16)
            {
                format = BitConverter.ToInt16(b, body);
                channels = BitConverter.ToInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4);
                bits = BitConverter.ToInt16(b, body + 14);
            }
            else if (id == "data")
            {
                if (format != 1 || channels != 1 || bits != 16 || rate <= 0)
                    return $"need 16-bit PCM mono WAV, got format {format}, {channels} ch, {bits} bit, {rate} Hz";
                samples = new short[size / 2];
                System.Buffer.BlockCopy(b, body, samples, 0, samples.Length * 2);
                return samples.Length == 0 ? "WAV has no samples" : null;
            }
            p = body + size + (size & 1);
        }
        return "WAV has no data chunk";
    }

    /// <summary>Linear resampling, as the app's mic readers do.</summary>
    public static short[] Resample(short[] src, int from, int to)
    {
        if (from == to) return src;
        int n = (int)((long)src.Length * to / from);
        var dst = new short[n];
        double step = (double)from / to;
        for (int i = 0; i < n; i++)
        {
            double at = i * step;
            int i0 = (int)at;
            int i1 = Math.Min(i0 + 1, src.Length - 1);
            dst[i] = (short)Math.Round(src[i0] + (src[i1] - src[i0]) * (at - i0));
        }
        return dst;
    }

    static byte[] ToPcm16(short[] s)
    {
        var b = new byte[s.Length * 2];
        System.Buffer.BlockCopy(s, 0, b, 0, b.Length);  // little-endian on every target
        return b;
    }

    // ---------------------------------------------------------------- audio_capture

    void Capture(JObject req, Action<JObject> ok, Action<string> fail)
    {
        string action = (string)req["action"];
        string name = Clean((string)req["name"]);
        string dir = Path.Combine(Application.persistentDataPath, "audiocap");
        switch (action)
        {
            case "start":
                if (tap != null) { fail("already capturing: stop first"); return; }
                var listener = FindAnyObjectByType<AudioListener>();
                if (listener == null) { fail("no active AudioListener"); return; }
                int rate = AudioSettings.outputSampleRate;
                if (rate <= 0) { fail("no audio output (outputSampleRate 0)"); return; }
                tap = listener.gameObject.AddComponent<DebugBridgeAudioTap>();
                tap.Begin(rate, MaxCaptureS);
                ok(new JObject { ["listener"] = listener.name, ["rate"] = rate, ["max_s"] = MaxCaptureS, ["t_start"] = Now });
                return;
            case "stop":
                if (tap == null) { fail("not capturing: start first"); return; }
                int count = tap.Count;  // snapshot: the audio thread may write once more before Destroy lands
                short[] buf = tap.Buffer;
                int r = tap.Rate;
                long callbacks = tap.Callbacks;
                bool capped = tap.Full;
                Destroy(tap);
                tap = null;
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, name + ".wav");
                File.WriteAllBytes(file, Wav(buf, count, r));
                var res = Stats(buf, count, r);
                res["name"] = name;
                res["path"] = file;
                res["capped"] = capped;
                res["callbacks"] = callbacks;
                ok(res);
                return;
            case "get":
                string path = Path.Combine(dir, name + ".wav");
                if (!File.Exists(path)) { fail($"no capture named '{name}' ({path})"); return; }
                long size = new FileInfo(path).Length;
                if (size > MaxGetBytes) { fail($"{path} is {size} bytes, over the 4 MB get limit: pull it with adb"); return; }
                ok(new JObject { ["name"] = name, ["path"] = path, ["bytes"] = size, ["wav_b64"] = Convert.ToBase64String(File.ReadAllBytes(path)) });
                return;
            default:
                fail("audio_capture needs \"action\": start, stop or get");
                return;
        }
    }

    static string Clean(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name ?? "")
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
        return sb.Length == 0 ? "capture" : sb.ToString(0, Math.Min(64, sb.Length));
    }

    /// <summary>The judgment of a recording (same rules as tools/questaudio.py stats). Silence here = |sample| &lt;= 1.</summary>
    public static JObject Stats(short[] s, int n, int rate)
    {
        int peak = 0, clipped = 0, first = -1, last = -1, runStart = -1;
        double sum = 0;
        var gaps = new JArray();
        int minGap = (int)(GapS * rate);
        for (int i = 0; i < n; i++)
        {
            int a = Math.Abs((int)s[i]);
            sum += (double)a * a;
            if (a > peak) peak = a;
            if (a >= 32767) clipped++;
            if (a <= 1)
            {
                if (runStart < 0) runStart = i;
                continue;
            }
            // A silent run bounded by sound on both sides, longer than GapS: a dropout.
            if (runStart >= 0 && first >= 0 && i - runStart > minGap)
                gaps.Add(new JObject { ["start_s"] = Math.Round((double)runStart / rate, 3), ["length_s"] = Math.Round((double)(i - runStart) / rate, 3) });
            runStart = -1;
            if (first < 0) first = i;
            last = i;
        }
        double rms = n > 0 ? Db(Math.Sqrt(sum / n) / 32767) : FloorDb;
        return new JObject
        {
            ["duration_s"] = Math.Round((double)n / rate, 3),
            ["rate"] = rate,
            ["peak_dbfs"] = Db(peak / 32767.0),
            ["rms_dbfs"] = rms,
            ["clipped_pct"] = n > 0 ? Math.Round(100.0 * clipped / n, 3) : 0,
            ["silent"] = rms < SilentDb,
            ["sound_s"] = first >= 0 ? Math.Round((double)(last - first + 1) / rate, 3) : 0,
            ["first_sound_s"] = first >= 0 ? Math.Round((double)first / rate, 3) : (double?)null,
            ["gaps"] = gaps,
        };
    }

    static double Db(double linear) => linear > 0 ? Math.Max(FloorDb, Math.Round(20 * Math.Log10(linear), 1)) : FloorDb;

    /// <summary>16-bit mono WAV of the first <paramref name="n"/> samples.</summary>
    public static byte[] Wav(short[] s, int n, int rate)
    {
        using (var m = new MemoryStream(44 + n * 2))
        using (var w = new BinaryWriter(m))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            for (int i = 0; i < n; i++) w.Write(s[i]);
            return m.ToArray();
        }
    }

    // ---------------------------------------------------------------- tone

    void Tone(JObject req, Action<JObject> ok, Action<string> fail)
    {
        float hz = Mathf.Clamp(req["hz"]?.Type is JTokenType.Integer or JTokenType.Float ? (float)req["hz"] : 440f, 20f, 20000f);
        float seconds = Mathf.Clamp(req["seconds"]?.Type is JTokenType.Integer or JTokenType.Float ? (float)req["seconds"] : 1f, 0.05f, 30f);
        const float Amp = 0.25f;  // -12 dBFS: loud enough to judge, far from clipping
        int rate = AudioSettings.outputSampleRate;
        if (rate <= 0) { fail("no audio output (outputSampleRate 0)"); return; }
        int n = (int)(rate * seconds), fade = Math.Min(n / 2, rate / 200);  // 5 ms fades: no clicks
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float env = Mathf.Min(1f, Mathf.Min(i, n - 1 - i) / (float)Math.Max(1, fade));
            data[i] = Amp * env * Mathf.Sin(2f * Mathf.PI * hz * i / rate);
        }
        if (toneSource == null)
        {
            toneSource = gameObject.AddComponent<AudioSource>();
            toneSource.playOnAwake = false;
            toneSource.spatialBlend = 0f;  // 2D: same level wherever the head is
        }
        toneSource.Stop();
        if (toneSource.clip != null) Destroy(toneSource.clip);
        toneSource.clip = AudioClip.Create("bridge-tone", n, 1, rate, false);
        toneSource.clip.SetData(data, 0);
        toneSource.Play();
        ok(new JObject { ["hz"] = hz, ["seconds"] = seconds, ["rate"] = rate, ["peak_dbfs"] = Db(Amp), ["t_start"] = Now });
    }

    // ---------------------------------------------------------------- reflection

    // A private member, or a clear error naming it if the class changed shape (DebugBridge answers it as the failure).
    static object Field(object target, string name)
    {
        var f = target.GetType().GetField(name, Any);
        if (f == null) throw new MissingMemberException($"{target.GetType().Name} changed shape: no field '{name}' (update DebugBridgeAudio)");
        return f.GetValue(target);
    }
}
#endif
