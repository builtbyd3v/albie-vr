using System;
using System.Collections.Generic;
using System.Globalization;
using Forge.ML;
using Forge.Notebook;
using Forge.OS;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Messages and projects, linked: each starter project has its own channel (#rc-truck, #quadcopter, #rocket,
/// #image-classifier; the sample workspace's channels say which project they belong to with <c>"project"</c>).
/// <see cref="DraftForProject(string, out string)"/> writes a DRAFT to that channel with the project's latest real
/// result (newest run, design version, training score), read from the notebook, the run records, the CAD session and the
/// ML session. Nothing is sent: the draft shows with Send / Cancel (the composer or a floating DraftCard) exactly like
/// Albie's messages_send, and only the user's Send posts it. <see cref="ProjectForChannel"/> goes the other way (the
/// chat header's "Open project" button and the messages tools' results).
/// </summary>
public static class MessagesShare
{
    // Project id -> channel name. The sample workspace has these channels; a live workspace needs the same names.
    static readonly string[,] Channels =
    {
        { "proj_truck", "rc-truck" },
        { "proj_quadcopter", "quadcopter" },
        { "proj_rocket", "rocket" },
        { "proj_classifier", "image-classifier" },
    };

    static readonly List<OSProjects.LinkedArtifact> linked = new List<OSProjects.LinkedArtifact>();

    /// <summary>The channel name (no '#') for a project, or null when it has none.</summary>
    public static string ChannelFor(string projectId)
    {
        for (int i = 0; i < Channels.GetLength(0); i++) if (Channels[i, 0] == projectId) return Channels[i, 1];
        return null;
    }

    /// <summary>The project a channel is about (by id or name, '#' optional), or null.</summary>
    public static string ProjectForChannel(string channel)
    {
        if (string.IsNullOrEmpty(channel)) return null;
        var c = ChatStore.Find(channel);
        string name = (c != null ? c.name : channel).TrimStart('#').ToLowerInvariant();
        for (int i = 0; i < Channels.GetLength(0); i++) if (Channels[i, 1] == name) return Channels[i, 0];
        return null;
    }

    /// <summary>Opens the project's window (the same path as the os_open tool). False when there is no such project.</summary>
    public static bool OpenProject(string projectId)
    {
        if (string.IsNullOrEmpty(projectId)) return false;
        OSProjects.OpenProject(projectId);
        return true;
    }

    /// <summary>Same as <see cref="DraftForProject(string, out string)"/>, without the sentence.</summary>
    public static bool DraftForProject(string projectId) => DraftForProject(projectId, out _);

    /// <summary>
    /// Drafts a message to <paramref name="projectId"/>'s channel with its latest result. Returns false (and why, in
    /// <paramref name="status"/>) when the project has no channel, no result yet, or messaging is not available. True
    /// means a draft is waiting for the user's Send / Cancel; <paramref name="status"/> is a line for a status bar.
    /// Never sends anything.
    /// </summary>
    public static bool DraftForProject(string projectId, out string status)
    {
        string channelName = ChannelFor(projectId);
        if (channelName == null) { status = "This project has no channel to post to."; return false; }
        string result = LatestResult(projectId);
        if (result == null) { status = "Nothing to share yet: run or build it first."; return false; }
        string text = OSProjects.TitleFor(projectId) + " update: " + result;

        if (ChatStore.Demo)
        {
            JObject channel;
            try { channel = MessagesDemo.Find("#" + channelName); }
            catch (ArgumentException e) { status = e.Message; return false; }
            MessagesDemo.CreateDraft(channel, text, null);
            status = "Draft for #" + channelName + " is ready. Tap Send to post it, or Cancel.";
            return true;
        }
        if (ForgeNet.Status == ForgeNetStatus.Live)
        {
            // The hub writes the draft and emits notify.draft (the card); it posts nothing until Send.
            ForgeNet.Post("/messages/drafts", new JObject { ["channel"] = "#" + channelName, ["text"] = text }, null,
                error => Debug.LogWarning("[MessagesShare] draft for #" + channelName + ": " + error));
            status = "Writing a draft for #" + channelName + ". Tap Send to post it, or Cancel.";
            return true;
        }
        status = "Messages are not connected yet.";
        return false;
    }

    /// <summary>
    /// One or two sentences with the project's newest real numbers, or null when there are none yet. Truck: the newest
    /// run of a linked artifact (else the notebook's last judged attempt). Designs: the CAD session's shown version of
    /// that design. Classifier: the ML session's current epoch and validation accuracy. Read on demand, never per frame.
    /// </summary>
    public static string LatestResult(string projectId)
    {
        if (string.IsNullOrEmpty(projectId)) return null;
        var parts = new List<string>(3);
        int version = 0;
        try { version = NotebookStore.CurrentVersion(projectId); }
        catch (Exception) { }

        switch (OSProjects.KindOf(projectId))
        {
            case OSProjectKind.Truck:
                string run = NewestRun(projectId);
                if (run != null) parts.Add(run);
                break;
            case OSProjectKind.Cad:
                string design = DesignResult(OSProjects.DesignOf(projectId));
                if (design != null) parts.Add(design);
                break;
            case OSProjectKind.Ml:
                string training = TrainingResult();
                if (training != null) parts.Add(training);
                break;
        }
        string note = NotebookNote(projectId);
        if (note != null && parts.Count == 0) parts.Add(note);
        if (parts.Count == 0) return null;
        if (version > 0) parts.Insert(0, "Project v" + version + ".");
        return string.Join(" ", parts);
    }

    static string NewestRun(string projectId)
    {
        try { OSProjects.LinkedArtifacts(projectId, linked); }
        catch (Exception) { linked.Clear(); }
        OSProjects.LinkedArtifact newest = null;
        for (int i = 0; i < linked.Count; i++)
        {
            if (linked[i].runId == null) continue;
            if (newest == null || string.CompareOrdinal(linked[i].runId, newest.runId) > 0) newest = linked[i];
        }
        if (newest == null) return null;
        string line = "Latest " + (newest.title ?? newest.id).ToLowerInvariant() + " run " + OSProjects.RunWord(newest.runOutcome);
        if (newest.runSeconds.HasValue) line += " in " + newest.runSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        if (!string.IsNullOrEmpty(newest.runReason) && newest.runOutcome != "completed") line += " (" + newest.runReason + ")";
        return line + ".";
    }

    static string DesignResult(string part)
    {
        var info = CadSession.Instance != null ? CadSession.Instance.Latest?.info : null;
        if (info == null || string.IsNullOrEmpty(part) || info.part != part) return null;
        string line = "Design v" + info.version;
        var verdict = info.analysis?.verdict;
        if (verdict != null && !string.IsNullOrEmpty(verdict.headline))
        {
            line += ": " + verdict.headline + ".";
            if (!string.IsNullOrEmpty(verdict.sentence)) line += " " + verdict.sentence;
            return line;
        }
        float? margin = info.analysis?.marginCal;
        return margin.HasValue ? line + ": " + margin.Value.ToString("0.00", CultureInfo.InvariantCulture) + " cal margin." : line + ".";
    }

    static string TrainingResult()
    {
        var session = UnityEngine.Object.FindAnyObjectByType<MLSession>();
        var metrics = session != null ? session.Data?.metrics : null;
        if (metrics == null || metrics.Length == 0 || session.EpochIndex < 0) return null;
        var m = metrics[Mathf.Min(session.EpochIndex, metrics.Length - 1)];
        if (m == null || float.IsNaN(m.val_acc)) return null;
        int total = session.TotalEpochs > 0 ? session.TotalEpochs : metrics.Length;
        string line = "Training epoch " + m.epoch + "/" + total + ": " +
                      (m.val_acc * 100f).ToString("0", CultureInfo.InvariantCulture) + "% validation accuracy";
        return line + (session.Done ? " (run done)." : " (still training).");
    }

    // The open project's last judged attempt, from its notebook ("Finished in 5.9 s but hit one cone.").
    static string NotebookNote(string projectId)
    {
        if (!NotebookStore.IsOpen || NotebookStore.ProjectId != projectId) return null;
        var all = NotebookStore.Entries;
        for (int i = all.Count - 1; i >= 0; i--)
        {
            string outcome = (string)all[i]["outcome"];
            if (string.IsNullOrEmpty(outcome) || outcome == "open") continue;
            string note = (string)all[i]["observed"]?["note"];
            string change = (string)all[i]["change"];
            return "Last attempt (" + outcome + "): " + (string.IsNullOrEmpty(note) ? change : note);
        }
        return null;
    }
}
