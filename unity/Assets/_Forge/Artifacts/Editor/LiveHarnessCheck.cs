using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Edit-mode check of the live-test tools (no Play mode, no voice model): <see cref="VoiceTurn"/> still reaches every
/// <c>AlbieVoice</c> member it uses, <see cref="LiveTranscript"/> refuses to start outside Play mode and to write inside
/// the project, and <see cref="ArtifactShot"/> says why instead of rendering nothing. Forge > Artifacts > Check Live Harness.
/// </summary>
public static class LiveHarnessCheck
{
    [MenuItem("Forge/Artifacts/Check Live Harness")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        bool ok = true;
        void Expect(bool pass, string what)
        {
            if (!pass) ok = false;
            sb.Append(pass ? "  ok   " : "  FAIL ").Append(what).Append('\n');
        }

        string turn = VoiceTurn.Check();
        if (turn.Contains("FAIL")) ok = false;
        sb.Append(turn).Append('\n');

        if (!Application.isPlaying)
        {
            Expect(VoiceTurn.TypeTurn("hello").StartsWith("FAIL not in Play mode"), "VoiceTurn.TypeTurn refuses outside Play mode");
            Expect(LiveTranscript.Start(Path.GetTempPath()).StartsWith("FAIL not in Play mode"), "LiveTranscript.Start refuses outside Play mode");
            Expect(ArtifactShot.OpenAndTake("cell", Path.Combine(Path.GetTempPath(), "x.png")) == "not in Play mode", "ArtifactShot.OpenAndTake refuses outside Play mode");
        }
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        Expect(LiveTranscript.InsideForCheck(Path.Combine(project, "unity", "Logs"), project), "LiveTranscript treats a folder in the repo as inside it");
        Expect(!LiveTranscript.InsideForCheck(Path.GetTempPath(), project), "LiveTranscript allows the temp folder");
        Expect(!LiveTranscript.InsideForCheck(project + "-other", project), "LiveTranscript doesn't take a sibling folder for the repo");
        return (ok ? "PASS" : "FAIL") + " live harness check\n" + sb;
    }
}
