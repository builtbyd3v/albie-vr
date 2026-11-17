using System;
using System.Collections.Generic;
using Forge.Pet;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Headset off and back on, in one place. Away = the app is paused (OnApplicationPause) or the headset is off the
/// user's head (OVRManager.HMDUnmounted); either one is enough, and both must clear to come back.
/// Away: the open listening turn is cancelled, the follow-up window and the wake listener stop, the mic is released,
/// the voice session closes (<see cref="AlbieVoice.Suspend"/>: it bills per open minute and its socket doesn't survive
/// a suspend), and game time stops (Time.timeScale 0), so the truck, an artifact run and its 30 s timeout wait
/// where they are instead of timing out while nobody watches.
/// Back: game time runs again, the voice is ready to reconnect on the next turn, the listeners come back (the mic is
/// opened again by whoever needs it next), Albie walks back if he ended up far away, the open panels are laid out
/// again if the tracking origin moved while world lock was off, and <see cref="Resumed"/> fires once.
/// Installs itself. Editor: Forge > Lifecycle > Simulate Headset Off / On.
/// </summary>
public class AppLifecycle : MonoBehaviour
{
    const float ComeBackDistance = 4f;   // m (flat) between the head and Albie on resume: farther, he walks back
    const float SettleS = 1f;            // after resume, let tracking and MRUK's world lock settle before checking poses
    const float LateRecenterS = 5f;      // a recenter this soon after resume still counts as "moved while away"

    /// <summary>The user is back (app resumed and headset on), with the seconds they were away. Raised once per return.</summary>
    public static event Action<float> Resumed;
    /// <summary>The app is paused or the headset is off.</summary>
    public static bool IsAway { get; private set; }
    /// <summary>Time.realtimeSinceStartup of the last return (very negative before the first).</summary>
    public static float ResumedAt { get; private set; } = -1e6f;

    static AppLifecycle instance;
    bool paused, unmounted, simulated, originMoved;
    float awayAt, savedTimeScale = 1f, replaceAt = -1f;
    readonly List<Behaviour> held = new List<Behaviour>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Resumed = null;
        IsAway = false;
        ResumedAt = -1e6f;
        instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindAnyObjectByType<AppLifecycle>() == null) new GameObject(nameof(AppLifecycle)).AddComponent<AppLifecycle>();
    }

    void OnEnable()
    {
        instance = this;
        OVRManager.HMDUnmounted += OnUnmounted;
        OVRManager.HMDMounted += OnMounted;
        OVRManager.TrackingOriginChangePending += OnOriginChange;
    }

    void OnDisable()
    {
        OVRManager.HMDUnmounted -= OnUnmounted;
        OVRManager.HMDMounted -= OnMounted;
        OVRManager.TrackingOriginChangePending -= OnOriginChange;
        if (instance == this) instance = null;
        if (IsAway) { IsAway = false; Time.timeScale = savedTimeScale; AudioListener.pause = false; }  // never leave game time stopped
    }

    void OnApplicationPause(bool pause)
    {
        paused = pause;
        Sync(pause ? "app paused" : "app resumed");
    }

    void OnUnmounted() { unmounted = true; Sync("headset off"); }
    void OnMounted() { unmounted = false; Sync("headset on"); }

    void OnOriginChange(OVRManager.TrackingOrigin origin, OVRPose? pose)
    {
        Debug.Log($"[AppLifecycle] tracking origin change pending ({origin}), away={IsAway}");
        if (IsAway) originMoved = true;
        else if (Time.realtimeSinceStartup - ResumedAt < LateRecenterS) { originMoved = true; replaceAt = Time.realtimeSinceStartup + SettleS; }
    }

    void Update()
    {
        // HMDMounted can be missed (the OS paused the app before OVRManager saw the change): check while away.
        if (unmounted && OVRManager.instance != null && OVRManager.instance.isUserPresent)
        {
            unmounted = false;
            Sync("headset on (polled)");
        }
        if (replaceAt >= 0f && Time.realtimeSinceStartup >= replaceAt)
        {
            replaceAt = -1f;
            Replace();
        }
    }

    void Sync(string why)
    {
        bool away = paused || unmounted || simulated;
        if (away && !IsAway) GoAway(why);
        else if (!away && IsAway) ComeBack(why);
    }

    void GoAway(string why)
    {
        IsAway = true;
        awayAt = Time.realtimeSinceStartup;
        originMoved = false;
        replaceAt = -1f;

        // The open turn first (its stt.cancel / stt.stop still reach AlbieVoice), then the listeners, then the session.
        var talk = FindAnyObjectByType<PushToTalk>();
        if (talk != null)
        {
            talk.CancelSession();
            if (talk.Provider is HubSpeechToText stt) stt.StopMonitoring();
        }
        Hold<AlbieFollowUp>();
        Hold<WakeListener>();
        if (ForgeNet.LocalPeer is AlbieVoice voice) voice.Suspend();
        if (Microphone.IsRecording(null)) Microphone.End(null);  // anything still holding the mic lets go

        savedTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
        Time.timeScale = 0f;
        AudioListener.pause = true;  // timeScale does not stop looping AudioSources
        Debug.Log($"[AppLifecycle] away ({why}): voice closed, mic released, game time stopped.");
    }

    void ComeBack(string why)
    {
        IsAway = false;
        Time.timeScale = savedTimeScale;
        AudioListener.pause = false;
        float secondsAway = Time.realtimeSinceStartup - awayAt;
        ResumedAt = Time.realtimeSinceStartup;
        if (ForgeNet.LocalPeer is AlbieVoice voice) voice.Resume();
        foreach (var b in held)
        {
            if (b == null) continue;
            if (b is AlbieFollowUp followUp) followUp.Halt();  // a window it scheduled while away must not open now
            b.enabled = true;
        }
        held.Clear();
        replaceAt = Time.realtimeSinceStartup + SettleS;
        Debug.Log($"[AppLifecycle] back ({why}) after {secondsAway:F0} s.");
        try { Resumed?.Invoke(secondsAway); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // Stop every enabled T and remember it, so only these come back (one disabled on purpose stays off).
    void Hold<T>() where T : Behaviour
    {
        foreach (var b in FindObjectsByType<T>())
        {
            if (!b.enabled) continue;
            if (b is AlbieFollowUp followUp) followUp.Halt();
            b.enabled = false;
            held.Add(b);
        }
    }

    // After the return has settled: panels are room-locked through the desk's MRUK anchor, so they only need laying
    // out again when the origin moved and world lock couldn't hold the room. Albie walks back if he is far away.
    void Replace()
    {
        var cam = Camera.main;
        if (cam == null) return;
        Vector3 head = cam.transform.position;
        bool locked = MRUK.Instance != null && MRUK.Instance.IsWorldLockActive;
        bool relaid = originMoved && !locked && Workspace.Current != null && Workspace.Current.ActiveMode != null;
        if (relaid) Workspace.Current.ResetLayout();
        originMoved = false;

        var crew = AlbieCrew.Instance;
        AlbieController albie = crew != null && crew.Lead != null ? crew.Lead : FindAnyObjectByType<AlbieController>();
        float distance = -1f;
        if (albie != null)
        {
            Vector3 d = albie.transform.position - head;
            d.y = 0f;
            distance = d.magnitude;
            if (!albie.Perched && distance > ComeBackDistance) albie.ComeToUser();
        }
        Debug.Log($"[AppLifecycle] after return: worldLock={locked} panelsRelaid={relaid} albieDistance={distance:F1} m" +
                  $"{(distance > ComeBackDistance ? " (walking back)" : "")} head={head}");
    }

    /// <summary>Editor and checks: headset off (true) / on (false) with no headset. Play mode only.</summary>
    public static void Simulate(bool away)
    {
        if (instance == null) return;
        instance.simulated = away;
        instance.Sync(away ? "simulated headset off" : "simulated headset on");
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem("Forge/Lifecycle/Simulate Headset Off")]
    static void SimulateOff() => Simulate(true);

    [UnityEditor.MenuItem("Forge/Lifecycle/Simulate Headset On")]
    static void SimulateOn() => Simulate(false);
#endif
}
