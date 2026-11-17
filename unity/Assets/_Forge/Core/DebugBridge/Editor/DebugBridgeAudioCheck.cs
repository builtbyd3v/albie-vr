using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Forge > Core > Check Bridge Audio: the Main scene in Play mode with voice OFF (no session can open, so it costs
/// nothing), one PASS/FAIL line per check. Over a real TcpClient: voice, mic_inject refusing, tone + audio_capture,
/// a quiet capture, get. On the main thread: mic_inject's feed against a fake peer (order, chunking, pacing, stopping
/// when the session closes), WAV decode + resample, and the capture maths fed straight into the tap (no audio device
/// needed). Batch (exits 1 on any failure):
/// <c>Unity -batchmode -projectPath unity -executeMethod DebugBridgeAudioCheck.RunBatch -logFile -</c>
/// </summary>
[InitializeOnLoad]
public static class DebugBridgeAudioCheck
{
    const string PhaseKey = "DebugBridgeAudioCheck.Phase", BatchKey = "DebugBridgeAudioCheck.Batch";
    const string ScenePath = "Assets/_Forge/Core/Main.unity";
    const double GiveUpSeconds = 180;

    static Task<int> client;
    static double startedAt;
    static int mainFailed, fakesLeft = -1;

    static DebugBridgeAudioCheck()
    {
        if (SessionState.GetInt(PhaseKey, 0) > 0) EditorApplication.playModeStateChanged += OnPlayMode;
    }

    public static void RunBatch()
    {
        SessionState.SetBool(BatchKey, true);
        Run();
    }

    [MenuItem("Forge/Core/Check Bridge Audio")]
    public static void Run()
    {
        EditorPrefs.SetBool("DebugBridgeAudioCheck.ForceDesktopWas", EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false));
        EditorPrefs.SetBool("DebugBridgeAudioCheck.VoiceOffWas", EditorPrefs.GetBool(AlbieVoice.EditorOffPref, false));
        EditorPrefs.SetBool(RigSelector.ForceDesktopPref, true);
        EditorPrefs.SetBool(AlbieVoice.EditorOffPref, true);
        SessionState.SetInt(PhaseKey, 1);
        EditorSceneManager.OpenScene(ScenePath);
        EditorApplication.playModeStateChanged += OnPlayMode;
        EditorApplication.isPlaying = true;
    }

    static void OnPlayMode(PlayModeStateChange change)
    {
        if (change != PlayModeStateChange.EnteredPlayMode || SessionState.GetInt(PhaseKey, 0) == 0) return;
        startedAt = EditorApplication.timeSinceStartup;
        mainFailed = 0;
        fakesLeft = -1;
        client = Task.Run(Checks);
        EditorApplication.update += Tick;
    }

    static void Tick()
    {
        if (fakesLeft < 0 && DebugBridgeAudio.Host != null) MainThreadChecks();
        bool late = EditorApplication.timeSinceStartup - startedAt > GiveUpSeconds;
        if ((!client.IsCompleted || fakesLeft != 0) && !late) return;
        EditorApplication.update -= Tick;
        int failed = mainFailed + (late ? 1 : client.IsFaulted ? 1 : client.Result);
        if (late) Line(false, "finished in time");
        if (client.IsFaulted) Line(false, "client threw " + client.Exception?.GetBaseException().Message);
        string summary = $"[DebugBridgeAudioCheck] {(failed == 0 ? "all passed" : failed + " failed")}";
        if (failed == 0) Debug.Log(summary);
        else Debug.LogError(summary);
        EditorApplication.playModeStateChanged -= OnPlayMode;
        EditorPrefs.SetBool(RigSelector.ForceDesktopPref, EditorPrefs.GetBool("DebugBridgeAudioCheck.ForceDesktopWas", false));
        EditorPrefs.SetBool(AlbieVoice.EditorOffPref, EditorPrefs.GetBool("DebugBridgeAudioCheck.VoiceOffWas", false));
        SessionState.SetInt(PhaseKey, 0);
        bool batch = SessionState.GetBool(BatchKey, false);
        EditorApplication.isPlaying = false;
        if (batch) EditorApplication.Exit(failed == 0 ? 0 : 1);
    }

    static bool Line(bool pass, string what)
    {
        Debug.Log($"[DebugBridgeAudioCheck] {(pass ? "PASS" : "FAIL")} {what}");
        return pass;
    }

    static void MainCheck(bool pass, string what)
    {
        if (!Line(pass, what)) mainFailed++;
    }

    static short[] Sine(int n, int rate, double hz, double amp)
    {
        var s = new short[n];
        for (int i = 0; i < n; i++) s[i] = (short)(amp * 32767 * Math.Sin(2 * Math.PI * hz * i / rate));
        return s;
    }

    // ---------------------------------------------------------------- main thread: fakes and maths

    static void MainThreadChecks()
    {
        var host = DebugBridgeAudio.Host;

        // WAV decode + resample: a 22.05 kHz second becomes 16 000 samples at the app's mic rate.
        var wav = DebugBridgeAudio.Wav(Sine(22050, 22050, 440, 0.5), 22050, 22050);
        string err = DebugBridgeAudio.DecodeWav(wav, out short[] decoded, out int rate);
        int resampled = err == null ? DebugBridgeAudio.Resample(decoded, rate, DebugBridgeAudio.MicRate).Length : -1;
        MainCheck(err == null && rate == 22050 && decoded.Length == 22050 && resampled == 16000,
            $"WAV decode + resample: 22050 Hz x {decoded?.Length} -> {resampled} samples at 16 kHz ({err ?? "ok"})");
        string stereo = DebugBridgeAudio.DecodeWav(StereoWav(), out _, out _);
        MainCheck(stereo != null && stereo.Contains("mono"), $"a stereo WAV is refused: {stereo}");

        // The capture maths, fed straight into the tap (what OnAudioFilterRead does on the audio thread).
        var go = new GameObject("BridgeAudioCheckTap") { hideFlags = HideFlags.HideAndDontSave };
        var tap = go.AddComponent<DebugBridgeAudioTap>();
        const int R = 48000;
        tap.Begin(R, 5);
        PushStereo(tap, R, 0.5, 0.25);  // tone
        PushStereo(tap, R, 0.3, 0);     // a 300 ms hole
        PushStereo(tap, R, 0.5, 0.25);  // tone
        var st = DebugBridgeAudio.Stats(tap.Buffer, tap.Count, R);
        var gaps = (JArray)st["gaps"];
        MainCheck(gaps.Count == 1 && Math.Abs((double)gaps[0]["length_s"] - 0.3) < 0.01 && Math.Abs((double)gaps[0]["start_s"] - 0.5) < 0.01
                  && Math.Abs((double)st["duration_s"] - 1.3) < 0.01 && !(bool)st["silent"] && (double)st["clipped_pct"] == 0
                  && Math.Abs((double)st["peak_dbfs"] + 12) < 0.5,
            $"tap maths: tone, 300 ms hole, tone -> one gap: {st.ToString(Formatting.None)}");
        tap.Begin(R, 5);
        PushStereo(tap, R, 0.2, 1.5);   // over full scale
        st = DebugBridgeAudio.Stats(tap.Buffer, tap.Count, R);
        MainCheck((double)st["clipped_pct"] > 10 && (double)st["peak_dbfs"] == 0, $"tap maths: an over-full-scale tone reports clipping ({st["clipped_pct"]} %)");
        tap.Begin(R, 5);
        PushStereo(tap, R, 1.0, 0);
        st = DebugBridgeAudio.Stats(tap.Buffer, tap.Count, R);
        MainCheck((bool)st["silent"] && ((JArray)st["gaps"]).Count == 0, $"tap maths: digital silence is silent with no gaps ({st["rms_dbfs"]} dBFS)");
        tap.Begin(1000, 1);
        PushStereo(tap, 1000, 2.0, 0.25);
        MainCheck(tap.Count == 1000 && tap.Full, $"tap stops at its cap ({tap.Count} of 1000 samples, full {tap.Full})");
        UnityEngine.Object.DestroyImmediate(go);

        // mic_inject's feed against a fake peer: never AlbieVoice, never ForgeNet, so nothing can reach the network.
        var pcm = new byte[16000 * 2];  // 1 s at 16 kHz
        Buffer.BlockCopy(Sine(16000, 16000, 300, 0.3), 0, pcm, 0, pcm.Length);
        fakesLeft = 3;
        FakeFeed(host, pcm, true, () => true, (log, res, error, secs) =>
            MainCheck(error == null && Shape(log, 10) && (int)res["samples"] == 16000 && secs >= 0.95 && secs < 1.6,
                $"mic_inject feed (fake peer, realtime): stt.start, 10 x 3200-byte chunks, stt.stop in {secs:0.00} s: {string.Join(" ", log)} {error}"));
        FakeFeed(host, pcm, false, () => true, (log, res, error, secs) =>
            MainCheck(error == null && Shape(log, 10) && secs < 0.2,
                $"mic_inject feed (fake peer, realtime false): all chunks in {secs:0.00} s"));
        int polls = 0;
        FakeFeed(host, pcm, true, () => ++polls < 4, (log, res, error, secs) =>
            MainCheck(error != null && error.Contains("closed") && !log.Contains("stt.stop") && log[0] == "stt.start",
                $"mic_inject feed stops with no stt.stop when the session closes mid-way: {error} ({log.Count - 1} frames sent)"));
    }

    static bool Shape(List<string> log, int chunks)
    {
        if (log.Count != chunks + 2 || log[0] != "stt.start" || log[log.Count - 1] != "stt.stop") return false;
        for (int i = 1; i <= chunks; i++) if (log[i] != "3200") return false;
        return true;
    }

    static void FakeFeed(DebugBridgeAudio host, byte[] pcm, bool realtime, Func<bool> open, Action<List<string>, JObject, string, double> done)
    {
        var log = new List<string>();
        double t0 = EditorApplication.timeSinceStartup;
        void End(JObject res, string error)
        {
            done(log, res, error, EditorApplication.timeSinceStartup - t0);
            fakesLeft--;
        }
        host.StartCoroutine(host.Feed(pcm, realtime, open,
            (type, payload) => { log.Add(type); return true; },
            (data, offset, count) => { log.Add(count.ToString()); return true; },
            res => End(res, null), error => End(null, error)));
    }

    static void PushStereo(DebugBridgeAudioTap tap, int rate, double seconds, double amp)
    {
        var buf = new float[1024 * 2];  // one DSP buffer, interleaved stereo
        int total = (int)(rate * seconds);
        for (int done = 0; done < total;)
        {
            int frames = Math.Min(1024, total - done);
            var b = frames == 1024 ? buf : new float[frames * 2];
            for (int i = 0; i < frames; i++)
                b[2 * i] = b[2 * i + 1] = (float)(amp * Math.Sin(2 * Math.PI * 440 * (done + i) / rate));
            tap.Push(b, 2);
            done += frames;
        }
    }

    static byte[] StereoWav()
    {
        var wav = DebugBridgeAudio.Wav(new short[100], 100, 16000);
        wav[22] = 2;  // channels
        return wav;
    }

    // ---------------------------------------------------------------- the client (worker thread)

    static int Checks()
    {
        int failed = 0;
        void Check(bool pass, string what, JToken got = null)
        {
            if (!Line(pass, pass || got == null ? what : $"{what}; got {Short(got)}")) failed++;
        }
        using (var tcp = Connect())
        {
            var stream = tcp.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            int id = 0;
            JObject Ask(JObject req)
            {
                req["id"] = ++id;
                writer.WriteLine(req.ToString(Formatting.None));
                return JObject.Parse(reader.ReadLine() ?? "{}");
            }

            var voice = Ask(new JObject { ["cmd"] = "voice" });
            Check((bool?)voice["ok"] == true && (string)voice["session"] == "closed" && (bool?)voice["present"] == false && voice["mic"] is JObject,
                $"voice answers, session closed: {Short(voice)}", voice);

            var wav = DebugBridgeAudio.Wav(Sine(8000, 16000, 300, 0.3), 8000, 16000);
            var inject = Ask(new JObject { ["cmd"] = "mic_inject", ["wav_b64"] = Convert.ToBase64String(wav) });
            Check((bool?)inject["ok"] == false && ((string)inject["error"] ?? "").Contains("no open voice session"),
                $"mic_inject refuses with no open session: {inject["error"]}", inject);

            var notCapturing = Ask(new JObject { ["cmd"] = "audio_capture", ["action"] = "stop" });
            Check((bool?)notCapturing["ok"] == false, $"audio_capture stop with no capture refuses: {notCapturing["error"]}", notCapturing);

            var start = Ask(new JObject { ["cmd"] = "audio_capture", ["action"] = "start" });
            Check((bool?)start["ok"] == true, $"audio_capture start: {Short(start)}", start);
            var tone = Ask(new JObject { ["cmd"] = "tone", ["hz"] = 440, ["seconds"] = 1 });
            Check((bool?)tone["ok"] == true, $"tone 440 Hz 1 s: {Short(tone)}", tone);
            System.Threading.Thread.Sleep(1600);
            var stop = Ask(new JObject { ["cmd"] = "audio_capture", ["action"] = "stop", ["name"] = "check-tone" });
            bool audioThread = (long?)stop["callbacks"] > 0;
            Line(true, $"audio thread ran during the capture: {audioThread} ({stop["callbacks"]} callbacks)");
            if (audioThread)
                Check((bool?)stop["ok"] == true && (bool?)stop["silent"] == false && ((JArray)stop["gaps"])?.Count == 0
                      && Math.Abs(((double?)stop["sound_s"] ?? 0) - 1.0) < 0.1 && (double?)stop["clipped_pct"] == 0,
                    $"tone capture: sound for ~1 s, not silent, no gaps, no clipping: {Short(stop)}", stop);
            else
                Check(false, $"tone capture: no audio callbacks, so OnAudioFilterRead never ran here (the tap maths are proven on the main thread instead): {Short(stop)}", stop);

            Ask(new JObject { ["cmd"] = "audio_capture", ["action"] = "start" });
            System.Threading.Thread.Sleep(1000);
            var quiet = Ask(new JObject { ["cmd"] = "audio_capture", ["action"] = "stop", ["name"] = "check-quiet" });
            if (audioThread)
                Check((bool?)quiet["ok"] == true && (bool?)quiet["silent"] == true && (double?)quiet["duration_s"] > 0.8,
                    $"capture with no sound is silent: {Short(quiet)}", quiet);

            var get = Ask(new JObject { ["cmd"] = "audio_capture", ["action"] = "get", ["name"] = "check-tone" });
            string decodeErr = "no wav_b64";
            short[] samples = null;
            if ((bool?)get["ok"] == true) decodeErr = DebugBridgeAudio.DecodeWav(Convert.FromBase64String((string)get["wav_b64"]), out samples, out _);
            int want = (int)Math.Round(((double?)stop["duration_s"] ?? -1) * ((int?)stop["rate"] ?? 0));
            Check(decodeErr == null && Math.Abs(samples.Length - want) <= 1, $"get returns a WAV that decodes ({samples?.Length} samples, stop said {want}; {decodeErr ?? "ok"})", get);

            var missing = Ask(new JObject { ["cmd"] = "audio_capture", ["action"] = "get", ["name"] = "no-such-capture" });
            Check((bool?)missing["ok"] == false, $"get of an unknown name refuses: {missing["error"]}", missing);
        }
        return failed;
    }

    static string Short(JToken t)
    {
        string s = t.ToString(Formatting.None);
        return s.Length > 400 ? s.Substring(0, 400) + "..." : s;
    }

    static TcpClient Connect()
    {
        var until = DateTime.UtcNow.AddSeconds(10);  // the bridge installs after the scene loads
        while (true)
        {
            var tcp = new TcpClient();
            try
            {
                tcp.Connect("127.0.0.1", DebugBridge.ActivePort);
                tcp.ReceiveTimeout = 20000;
                return tcp;
            }
            catch (SocketException) when (DateTime.UtcNow < until)
            {
                tcp.Close();
                System.Threading.Thread.Sleep(200);
            }
        }
    }
}
