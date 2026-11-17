using System;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Forge.Pet;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Checks for headset off / back on (<see cref="AppLifecycle"/>) and the tips it holds back (<see cref="AlbieTips"/>).
/// Forge > Core > Check Lifecycle logs the report; <see cref="Run"/> returns it (for `unity command eval`). PASS on the
/// first line means every step passed. The away/back steps need Play mode (AppLifecycle installs itself at scene load,
/// and game time only runs there). They drive their own AppLifecycle with a fake clock, a test AlbieVoice as
/// ForgeNet.LocalPeer whose token and socket always fail (so no voice session can open), and test listeners; afterwards
/// they put back the time scale, the audio pause, LocalPeer, the Resumed handlers, ResumedAt and the installed
/// AppLifecycle. The app's own listeners are held and brought back too, as a real pause does. Edit mode: the tips rule only.
/// </summary>
public static class LifecycleCheck
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const string EditSkip = "skipped in Edit mode (AppLifecycle and game time need Play mode: enter Play, then run this again)";

    [MenuItem("Forge/Core/Check Lifecycle")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    /// <summary>For -executeMethod (Edit mode: the tips rule only).</summary>
    public static void RunBatch()
    {
        string report = Run();
        Debug.Log(report);
        EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
    }

    class World
    {
        public GameObject root;
        public AppLifecycle life;
        public AlbieVoice voice;
        public WakeListener wake, wakeOff;
        public AlbieFollowUp follow;
        public float now;
        public int resumed;
        public float lastAway;
        public void OnResumed(float seconds) { resumed++; lastAway = seconds; }
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        int failed = 0, skipped = 0;
        failed += Step(sb, ref skipped, "Tips wait while away and for the quiet window after the return", TipsHeld);
        string[] cycle =
        {
            "Away: game time and audio stop, voice suspended, listeners held",
            "Back: the saved time scale, audio, Resumed once with the time away, voice and listeners back",
            "Two away/back cycles in a row behave the same",
            "Disabled while away: game time and audio come back",
        };
        if (!Application.isPlaying)
        {
            foreach (string name in cycle) failed += Step(sb, ref skipped, name, () => EditSkip);
            return Report(failed, skipped, sb);
        }
        if (AppLifecycle.IsAway) return "FAIL (1) lifecycle check (Play mode)\n  FAIL the app is already away: put the headset on and run it again\n";

        float timeScale = Time.timeScale;
        bool audioPaused = AudioListener.pause;
        var peer = ForgeNet.LocalPeer;
        var clock = AppLifecycle.clock;
        float resumedAt = AppLifecycle.ResumedAt;
        var resumedField = typeof(AppLifecycle).GetField("Resumed", Any);  // the event's backing field
        var instanceField = typeof(AppLifecycle).GetField("instance", Any);
        object handlers = resumedField?.GetValue(null), installed = instanceField?.GetValue(null);
        bool logs = Debug.unityLogger.logEnabled;
        var w = new World();
        try
        {
            if (resumedField == null || instanceField == null) throw new Exception("AppLifecycle.Resumed / instance not found");
            Debug.unityLogger.logEnabled = false;  // "away (simulated headset off)" lines would read like a real pause
            resumedField.SetValue(null, null);     // the app's handlers (the welcome-back line) stay out of it
            Build(w);
            failed += Step(sb, ref skipped, cycle[0], () => Away(w));
            failed += Step(sb, ref skipped, cycle[1], () => Back(w));
            failed += Step(sb, ref skipped, cycle[2], () => Twice(w));
            failed += Step(sb, ref skipped, cycle[3], () => DisabledWhileAway(w));
        }
        catch (Exception e)
        {
            failed += Step(sb, ref skipped, "Set up", () => throw e);
        }
        finally
        {
            if (AppLifecycle.IsAway && w.life != null && w.life.enabled) AppLifecycle.Simulate(false);  // brings back what it held
            if (w.root != null) UnityEngine.Object.DestroyImmediate(w.root);
            instanceField?.SetValue(null, installed);
            ForgeNet.LocalPeer = peer;
            Time.timeScale = timeScale;
            AudioListener.pause = audioPaused;
            AppLifecycle.clock = clock;
            typeof(AppLifecycle).GetProperty("ResumedAt")?.GetSetMethod(true)?.Invoke(null, new object[] { resumedAt });
            resumedField?.SetValue(null, handlers);
            Debug.unityLogger.logEnabled = logs;
        }
        return Report(failed, skipped, sb);
    }

    static string Report(int failed, int skipped, StringBuilder sb) =>
        (failed == 0 ? "PASS" : $"FAIL ({failed})") + $" lifecycle check ({(Application.isPlaying ? "Play" : "Edit")} mode{(skipped > 0 ? $", {skipped} skipped" : "")})\n" + sb;

    static void Build(World w)
    {
        w.root = new GameObject("LifecycleCheck");
        GameObject Child(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(w.root.transform, false);
            return go;
        }
        w.voice = Child("Voice").AddComponent<AlbieVoice>();  // OnEnable makes it ForgeNet.LocalPeer
        w.voice.fetchToken = () => Task.FromException<string>(new InvalidOperationException("Check Lifecycle opens no voice session"));
        w.voice.openSocket = (token, cancel) => Task.FromException<WebSocket>(new InvalidOperationException("Check Lifecycle opens no voice session"));
        w.wake = Child("Wake").AddComponent<WakeListener>();
        w.wakeOff = Child("Wake (off on purpose)").AddComponent<WakeListener>();
        w.wakeOff.enabled = false;
        w.follow = Child("FollowUp").AddComponent<AlbieFollowUp>();
        w.life = Child("AppLifecycle").AddComponent<AppLifecycle>();  // OnEnable makes it the one Simulate drives
        AppLifecycle.clock = () => w.now;
        AppLifecycle.Resumed += w.OnResumed;
    }

    // ---- Steps -----------------------------------------------------------------------------------------------

    static string TipsHeld()
    {
        var field = typeof(AlbieTips).GetField("ResumeQuietS", Any);
        Expect(field != null, "AlbieTips.ResumeQuietS not found");
        float quiet = (float)field.GetRawConstantValue();
        Expect(AlbieTips.HeldByLifecycle(true, 1e6f), "a tip could play while away");
        Expect(AlbieTips.HeldByLifecycle(false, 0f), "a tip could play the moment the user is back");
        Expect(AlbieTips.HeldByLifecycle(false, quiet - 0.1f), $"a tip could play {quiet - 0.1f:0.0} s after the return");
        Expect(!AlbieTips.HeldByLifecycle(false, quiet + 0.1f), $"tips still held {quiet + 0.1f:0.0} s after the return");
        Expect(!AlbieTips.HeldByLifecycle(false, 1e6f), "tips held when the app was never away");
        return $"held while away and for {quiet:0} s after the return; free after that and when never away";
    }

    static string Away(World w)
    {
        Time.timeScale = 0.5f;
        AudioListener.pause = false;
        w.now = 100f;
        AppLifecycle.Simulate(true);
        Expect(AppLifecycle.IsAway, "not away");
        Expect(Time.timeScale == 0f, $"time scale {Time.timeScale}, not 0");
        Expect(AudioListener.pause, "audio not paused");
        Expect(w.voice.Suspended, "voice not suspended");
        Expect(!w.wake.enabled, "wake listener still on");
        Expect(!w.follow.enabled, "follow-up still on");
        Expect(!w.wakeOff.enabled, "the wake listener that was off came on");
        Expect(w.resumed == 0, "Resumed raised on the way out");
        Expect(AlbieTips.HeldByLifecycle(AppLifecycle.IsAway, 1e6f), "a tip could play while away");
        return "timeScale 0 (was 0.5), audio paused, voice suspended, wake listener and follow-up held, tips held";
    }

    static string Back(World w)
    {
        w.now = 142.5f;
        AppLifecycle.Simulate(false);
        Expect(!AppLifecycle.IsAway, "still away");
        Expect(Time.timeScale == 0.5f, $"time scale {Time.timeScale}, not the saved 0.5");
        Expect(!AudioListener.pause, "audio still paused");
        Expect(w.resumed == 1, $"Resumed raised {w.resumed} times");
        Expect(Mathf.Abs(w.lastAway - 42.5f) < 0.01f, $"Resumed said {w.lastAway} s away, not 42.5");
        Expect(AppLifecycle.ResumedAt == 142.5f, $"ResumedAt {AppLifecycle.ResumedAt}");
        Expect(!w.voice.Suspended, "voice still suspended");
        Expect(w.wake.enabled && w.follow.enabled, "listeners not back");
        Expect(!w.wakeOff.enabled, "the wake listener that was off came back on");
        Expect(AlbieTips.HeldByLifecycle(AppLifecycle.IsAway, AppLifecycle.clock() - AppLifecycle.ResumedAt), "a tip could play right after the return");
        AppLifecycle.Simulate(false);
        Expect(w.resumed == 1, "a second 'headset on' raised Resumed again");
        return "timeScale 0.5 (the saved one), audio on, Resumed once with 42.5 s, voice resumed, listeners back (the one off stays off), tips still held";
    }

    static string Twice(World w)
    {
        w.resumed = 0;
        Time.timeScale = 2f;
        w.now = 200f;
        AppLifecycle.Simulate(true);
        AppLifecycle.Simulate(true);  // a repeated "off" while away must not save 0 as the time scale
        w.now = 210f;
        AppLifecycle.Simulate(false);
        Expect(Time.timeScale == 2f, $"cycle 1: time scale {Time.timeScale}, not the saved 2");
        Expect(w.resumed == 1 && Mathf.Abs(w.lastAway - 10f) < 0.01f, $"cycle 1: Resumed {w.resumed}x, {w.lastAway} s");
        Time.timeScale = 0.5f;
        w.now = 300f;
        AppLifecycle.Simulate(true);
        Expect(Time.timeScale == 0f && AudioListener.pause && w.voice.Suspended && !w.wake.enabled && !w.follow.enabled, "cycle 2 away differs from cycle 1");
        w.now = 305f;
        AppLifecycle.Simulate(false);
        Expect(Time.timeScale == 0.5f, $"cycle 2: time scale {Time.timeScale}, not the saved 0.5");
        Expect(!AudioListener.pause && !w.voice.Suspended && w.wake.enabled && w.follow.enabled, "cycle 2 return differs from cycle 1");
        Expect(w.resumed == 2 && Mathf.Abs(w.lastAway - 5f) < 0.01f, $"cycle 2: Resumed {w.resumed}x in total, {w.lastAway} s");
        return "cycle 1 (2x, 10 s away, off twice) and cycle 2 (0.5x, 5 s) each restore their own time scale and raise Resumed once";
    }

    static string DisabledWhileAway(World w)
    {
        Time.timeScale = 0.5f;
        w.now = 400f;
        AppLifecycle.Simulate(true);
        Expect(Time.timeScale == 0f, "not away");
        bool listenersBack, voiceBack;
        try
        {
            w.life.enabled = false;
            Expect(Time.timeScale == 0.5f, $"time scale {Time.timeScale} after OnDisable while away");
            Expect(!AudioListener.pause, "audio left paused");
            Expect(!AppLifecycle.IsAway, "still away");
            listenersBack = w.wake.enabled && w.follow.enabled;
            voiceBack = !w.voice.Suspended;
        }
        finally
        {
            // Put it back: enabled again it is the instance; one away/back brings back what it held.
            w.life.enabled = true;
            AppLifecycle.Simulate(true);
            AppLifecycle.Simulate(false);
        }
        Expect(w.wake.enabled && w.follow.enabled && !w.voice.Suspended, "listeners or voice not back after the next return");
        string note = listenersBack && voiceBack ? "" :
            $"; note: OnDisable doesn't bring back {(listenersBack ? "" : "the held listeners")}{(listenersBack || voiceBack ? "" : " or ")}{(voiceBack ? "" : "the voice")} (the next return does)";
        return "timeScale back to 0.5, audio on, not away" + note;
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    sealed class Failed : Exception
    {
        public Failed(string message) : base(message) { }
    }

    static void Expect(bool condition, string message)
    {
        if (!condition) throw new Failed(message);
    }

    static int Step(StringBuilder sb, ref int skipped, string name, Func<string> check)
    {
        string tag, detail;
        try
        {
            detail = check();
            tag = detail.StartsWith("skipped") ? "skip" : "ok  ";
        }
        catch (Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            detail = e is Failed ? e.Message : e.GetType().Name + ": " + e.Message;
            tag = "FAIL";
        }
        if (tag == "skip") skipped++;
        sb.Append("  ").Append(tag).Append(' ').Append(name).Append(": ").Append(detail).Append('\n');
        return tag == "FAIL" ? 1 : 0;
    }
}
