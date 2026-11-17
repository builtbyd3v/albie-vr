using System;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless checks for Albie's cloud health (<see cref="CloudHealth"/>): scripted /health answers stand in for the
/// network, so no Play mode and no Space. Forge > Core > Check Cloud Offline logs the report; <see cref="Run"/> returns
/// it (for `unity command eval`). PASS on the first line means every check passed. The real probe against the live
/// Space is a Play-mode test (see the coroutine in ForgeCloud.Start).
/// </summary>
public static class CloudOfflineCheck
{
    const string Ok = "{\"ok\":true,\"devices\":0,\"cad_workers\":1,\"training\":0}";
    const string Paused = "The space is paused, ask a maintainer to restart it";
    const string Booting = "<html>Starting...</html>";

    [MenuItem("Forge/Core/Check Cloud Offline")]
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
        bool ok = true;
        ok &= Check(sb, "a 200 with ok is Online, silently", Online);
        ok &= Check(sb, "a paused Space is Offline at once; told once, not per call", PausedSpace);
        ok &= Check(sb, "a sleeping Space wakes within 90 s; Albie says so once", Wakes);
        ok &= Check(sb, "a Space that doesn't wake in 90 s goes Offline once, then back", WakeTimesOut);
        ok &= Check(sb, "a 200 loading page is not Online", LoadingPage);
        ok &= Check(sb, "a refused key is Offline", RefusedKey);
        ok &= Check(sb, "nothing said when nobody asked", Unasked);
        ok &= Check(sb, "the hub taking over resets it", Standby);
        return (ok ? "PASS" : "FAIL") + " cloud offline check\n" + sb;
    }

    static bool Check(StringBuilder sb, string name, Func<string> check)
    {
        string detail;
        bool ok;
        try { detail = check(); ok = true; }
        catch (Exception e) { detail = e.Message; ok = false; }
        sb.Append(ok ? "  ok   " : "  FAIL ").Append(name).Append(": ").Append(detail).Append('\n');
        return ok;
    }

    static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static string Online()
    {
        var h = new CloudHealth();
        Expect(h.State == CloudState.Unknown && h.Status == null, $"starts {h.State}");
        string say = h.Report(200, Ok, 0f);
        Expect(h.State == CloudState.Online && say == null && h.Status == null, $"{h.State}, said '{say}'");
        Expect(h.Ask() == null, "a call while Online needs no answer from the health check");
        return "Online, no line, Ask passes";
    }

    static string PausedSpace()
    {
        var h = new CloudHealth();
        h.Report(503, "HTTP/1.1 503 Service Unavailable: " + Paused, 0f);
        Expect(h.State == CloudState.Offline && h.Status == CloudHealth.OfflineStatus, $"{h.State}");
        int said = 0;
        for (int call = 0; call < 5; call++)
        {
            Expect(h.Ask() == CloudHealth.OfflineLine, "each call is answered with the offline line");
            if (h.Report(503, Paused, 30f * (call + 1)) != null) said++;
        }
        Expect(said == 0, $"said {said} extra lines over 5 calls");
        string back = h.Report(200, Ok, 400f);
        Expect(h.State == CloudState.Online && back == CloudHealth.AwakeLine, $"{h.State}, said '{back}'");
        Expect(h.Report(200, Ok, 430f) == null, "said 'awake' twice");
        return "Offline, 5 calls answered, 0 repeats, 'awake' once when un-paused";
    }

    static string Wakes()
    {
        var h = new CloudHealth();
        Expect(h.Report(503, Booting, 0f) == null && h.State == CloudState.Waking, $"{h.State}");
        Expect(h.Status == CloudHealth.WakingStatus, "panel shows the waking line");
        Expect(h.Ask() == CloudHealth.WakingLine, "a call while waking gets the waking line");
        for (float t = 5f; t < 60f; t += 5f)
            Expect(h.Report(503, Booting, t) == null && h.State == CloudState.Waking, $"t={t}: {h.State}");
        Expect(h.Report(0, "Request timeout", 62f) == null && h.State == CloudState.Waking, "a timeout while booting stays Waking");
        string say = h.Report(200, Ok, 65f);
        Expect(h.State == CloudState.Online && say == CloudHealth.AwakeLine, $"{h.State}, said '{say}'");
        return "Waking for 65 s of 503s, then Online with one 'awake' line";
    }

    static string WakeTimesOut()
    {
        var h = new CloudHealth();
        h.Report(503, Booting, 0f);
        h.Ask();
        Expect(h.Report(503, Booting, 85f) == null && h.State == CloudState.Waking, $"85 s: {h.State}");
        string say = h.Report(503, Booting, 91f);
        Expect(h.State == CloudState.Offline && say == CloudHealth.OfflineLine, $"91 s: {h.State}, said '{say}'");
        Expect(h.Report(503, Booting, 121f) == null && h.State == CloudState.Offline, "stays Offline, silent, on more 503s");
        Expect(h.Report(0, "Cannot resolve destination host", 151f) == null && h.State == CloudState.Offline, "no network stays Offline");
        string back = h.Report(200, Ok, 181f);
        Expect(h.State == CloudState.Online && back == CloudHealth.AwakeLine, $"{h.State}, said '{back}'");
        return "Offline at 91 s with one line, Online at 181 s with one line";
    }

    static string LoadingPage()
    {
        var h = new CloudHealth();
        h.Report(200, Booting, 0f);
        Expect(h.State == CloudState.Waking, $"{h.State}");
        return "Waking";
    }

    static string RefusedKey()
    {
        var h = new CloudHealth();
        h.Report(401, "{\"error\":\"missing or wrong x-albie-key\"}", 0f);
        Expect(h.State == CloudState.Offline, $"{h.State}");
        return "Offline";
    }

    static string Unasked()
    {
        var h = new CloudHealth();
        int said = 0;
        if (h.Report(503, Booting, 0f) != null) said++;
        if (h.Report(503, Booting, 95f) != null) said++;
        if (h.Report(200, Ok, 120f) != null) said++;
        Expect(said == 0, $"said {said} lines");
        return "Waking, Offline, Online: 0 lines";
    }

    static string Standby()
    {
        var h = new CloudHealth();
        h.Report(503, Paused, 0f);
        h.Ask();
        h.Standby();
        Expect(h.State == CloudState.Unknown && h.Status == null, $"{h.State}");
        Expect(h.Report(200, Ok, 10f) == null, "an old ask spoke after the hub took over");
        return "Unknown, the old ask forgotten";
    }
}
