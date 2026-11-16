using System;
using System.Collections;
using System.Collections.Generic;
using Forge.Artifacts;
using Forge.Controllers;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

/// <summary>
/// The first-run flow, so a person who has never seen Albie knows what to do with hands only.
/// First launch: the arrival greeting card (<see cref="ArrivalGreeting"/>) gets Albie's hello and START; once START is
/// pressed and the dock has come in, the welcome card opens: how to talk to Albie and three things to try. Each try
/// button runs the same Albie tool the voice model calls (<see cref="AlbieTools"/>), and Albie says how it went
/// (voice on: albie.say, voice off: pet.say, the caption; the AlbieTips path). Once a slalom truck is out, the card adds
/// the hands driving hint (<see cref="HandsTransmitter"/>) and Albie says it once.
/// The card is shown once (PlayerPrefs <see cref="ShownPref"/>). Closed, it shrinks into a "?" dot
/// (<see cref="PanelOrb"/>, the desk extras' minimize pattern): tap it, or say "help" (tool help_show), and it comes back.
/// No arrival in the scene (no desk found within <see cref="ArrivalWaitS"/>): Albie says hello and the card opens anyway.
/// The card is placed in view, within reach, clear of the other panels (<see cref="Overlaps"/>).
/// Installs itself in any scene with a Workspace, like ArtifactBootstrap. Editor: F9 = <see cref="OnResumed"/>(60 s),
/// F10 = forget it was shown and open it again.
/// </summary>
public class FirstRunGuide : MonoBehaviour
{
    public const string ShownPref = "albie.first_run.shown";
    /// <summary>Away longer than this (s) and Albie says welcome back.</summary>
    public const float ResumeAfterS = 20f;
    /// <summary>How long to wait for the arrival greeting before opening the card anyway (s).</summary>
    public const float ArrivalWaitS = 25f;
    const float AfterStartS = 2.5f;  // START: the dock comes in (1.3 s) and the code view opens; place the card after
    const float TruckPollS = 0.5f;
    const string PetId = "bit";

    // ---- Copy (no brand names) ----
    public const string GreetingLine = "Hi, I'm Albie! Tap START and I'll show you around.";
    public const string WelcomeLine = "Hi, I'm Albie! Face me and talk, or tap something on the card to try it.";
    public const string CardLine = "Face me and talk, or tap one of these to try it.";
    public const string TalkLine = "Face Albie and just talk. Or hold your palm up and he hops on.";
    public const string TryLine = "Try one. Tap it, or say it:";
    public const string DriveHint = "Pinch your right hand to drive, tilt it to steer. Pinch your left hand to reverse.";
    public const string NeedSlalom = "Make a slalom first. Then drive it twice and I'll learn it.";
    public const string FirstHint = "Tap the question mark any time for ideas.";
    const string FirstTimeHint = "(This is the person's first time. Say it in one short, warm sentence.)";
    const string TapHint = "(The person tapped this on the welcome card and it ran. Say it in your own words.)";
    const string ResumeHint = "(The person just came back to the app. Say it briefly in your own words.)";

    /// <summary>A try button: its label (also what to say), the tool it runs, the tool's arguments, and Albie's line when it worked.</summary>
    public sealed class Try
    {
        public readonly string label, tool, argsJson, done;
        public readonly bool needsTruck;
        public Try(string label, string tool, string argsJson, string done, bool needsTruck = false)
        {
            this.label = label; this.tool = tool; this.argsJson = argsJson; this.done = done; this.needsTruck = needsTruck;
        }
    }

    public static readonly Try[] Tries =
    {
        new Try("Make me a slalom", "artifact_create", "{\"template\":\"slalom\"}", "Here's your slalom."),
        new Try("Show me a beating heart", "artifact_create", "{\"template\":\"heart\"}", "Here's a beating heart."),
        new Try("Drive it twice and I'll learn it", "brain_record", "{\"start\":true,\"laps\":2,\"then_autopilot\":true}",
                "Drive two laps and I'll learn from you. " + DriveHint, needsTruck: true),
    };

    // ---- Layout (canvas units = mm) ----
    const float CardW = 380f, CardH = 380f;
    const float PillH = 34f, PillText = 16f, PillGap = 8f;
    const float OrbSize = 0.1f;   // m, the frameless panel the dot lives on
    const float Reach = 0.5f;     // m from the eyes: inside a seated arm's reach, for poke
    const float AngleMargin = 3f; // degrees kept between the card and another panel
    static readonly float[] CardYaws = { 0f, -22f, 22f, -32f, 32f };  // inside a 70 degree view
    static readonly float[] CardDrops = { 0.16f, 0.3f };
    static readonly float[] OrbYaws = { -30f, 30f, -20f, 20f };
    static readonly float[] OrbDrops = { 0.3f };

    /// <summary>Checks only: replaces Albie's voice / caption.</summary>
    public static Action<string, string> SayHook;
    public static string LastSaid { get; private set; }
    public static FirstRunGuide Instance { get; private set; }
    public static bool ShouldShowWelcome => PlayerPrefs.GetInt(ShownPref, 0) == 0;

    /// <summary>Where the card and the dot are made (a check's preview scene). Null in the app.</summary>
    public Transform Holder;

    ForgePanel panelPrefab, card;
    PanelOrb orb;
    Transform head, deskFrame;
    TMP_Text driveLine;
    bool truckOut, driveHintSaid;
    string lastHint = FirstHint;
    float nextTruckPoll;

    public ForgePanel Card => card;
    public PanelOrb Orb => orb;
    public bool CardShown => card != null && card.gameObject.activeSelf;
    public bool OrbShown => orb != null && orb.gameObject.activeSelf;
    public string LastHint => lastHint;
    Transform Head => head != null ? head : head = PetUser.Head;

    // ---------------------------------------------------------------- install

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        SayHook = null;
        LastSaid = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void RegisterTool() =>
        AlbieTools.Register("first_run", new Dictionary<string, AlbieTools.Handler> { ["help_show"] = HelpShowTool });

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        Ensure();
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Ensure();

    static void Ensure()
    {
        if (Instance != null) return;
        var workspace = FindAnyObjectByType<Workspace>();
        if (workspace == null || workspace.panelPrefab == null) return;  // sandboxes and checks
        var guide = new GameObject(nameof(FirstRunGuide)).AddComponent<FirstRunGuide>();
        guide.Init(workspace.panelPrefab, PetUser.Head, workspace.presence != null ? workspace.presence.Frame : null);
        guide.StartCoroutine(guide.Flow());
    }

    /// <summary>Set up (the app does this on install; a check calls it with its own prefab and head).</summary>
    public void Init(ForgePanel prefab, Transform eyes, Transform frame)
    {
        panelPrefab = prefab;
        head = eyes;
        deskFrame = frame;
        MakeCurrent();
    }

    /// <summary>This guide answers help_show (a check hands it back to the app's guide when it's done).</summary>
    public void MakeCurrent() => Instance = this;

    void OnEnable()
    {
        if (Instance == null) Instance = this;
        AppLifecycle.Resumed += OnResumed;
    }

    void OnDisable() => AppLifecycle.Resumed -= OnResumed;

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (!Application.isPlaying) return;
        if (card != null) Destroy(card.gameObject);
        if (orb != null) Destroy(orb.gameObject);
    }

    // ---------------------------------------------------------------- the flow

    IEnumerator Flow()
    {
        bool first = ShouldShowWelcome;
        var greeting = FindAnyObjectByType<ArrivalGreeting>(FindObjectsInactive.Include);
        var presence = FindAnyObjectByType<DeskPresence>();
        bool saw = false;
        if (greeting != null && presence != null)
        {
            float deadline = Time.time + ArrivalWaitS;
            while (true)
            {
                if (greeting.IsShown)
                {
                    if (!saw && first)
                    {
                        greeting.SetLine(GreetingLine);  // the card is Albie's caption while it's up (his bubble is muted)
                        Say(GreetingLine, FirstTimeHint);
                    }
                    saw = true;
                }
                else if (saw)
                {
                    if (presence.AtDesk) break;   // START, or a view opened over the card
                    saw = false;                  // Away folded it: wait for them to come back
                    deadline = Time.time + ArrivalWaitS;
                }
                else if (Time.time > deadline) break;
                yield return null;
            }
        }
        if (saw) yield return new WaitForSeconds(AfterStartS);
        if (first) ShowCard(saw ? CardLine : WelcomeLine);
        else ShowOrbAtRest();
    }

    /// <summary>Open the welcome card in view (and mark it shown). <paramref name="line"/>: what Albie says with it, or null.</summary>
    public void ShowCard(string line)
    {
        if (card == null) card = BuildCard();
        if (orb != null) orb.gameObject.SetActive(false);
        driveLine.gameObject.SetActive(truckOut);
        card.gameObject.SetActive(true);
        Place(card.transform, card.SizeMeters, CardYaws, CardDrops);
        PlayerPrefs.SetInt(ShownPref, 1);
        PlayerPrefs.Save();
        if (line != null) Say(line, FirstTimeHint);
        Debug.Log("[FirstRun] Welcome card shown.");
    }

    /// <summary>The "?" dot's tap: the card again.</summary>
    public void Reopen() => ShowCard(null);

    /// <summary>"Got it" or the title bar's −: the card shrinks into the "?" dot at its lower left corner.</summary>
    public void CloseCard()
    {
        if (!CardShown) return;
        Transform t = card.transform;
        Vector3 corner = t.TransformPoint(new Vector3(-CardW * 0.5f + 40f, -CardH * 0.5f + 40f, 0f));
        Quaternion facing = t.rotation;
        card.gameObject.SetActive(false);
        if (orb == null) orb = BuildOrb();
        orb.transform.SetPositionAndRotation(corner, facing);
        orb.gameObject.SetActive(true);
    }

    // Later launches: just the dot, low and to one side.
    void ShowOrbAtRest()
    {
        if (orb == null) orb = BuildOrb();
        orb.gameObject.SetActive(true);
        Place(orb.transform, Vector2.one * OrbSize, OrbYaws, OrbDrops);
    }

    void Tap(int index)
    {
        var t = Tries[index];
        if (t.needsTruck && !truckOut)
        {
            Say(NeedSlalom);
            return;
        }
        CloseCard();  // out of the way of what it makes
        Run(t);
    }

    /// <summary>Run a try button's tool, as the voice model would; Albie says how it went. False: no such tool here, so Albie says the phrase to use.</summary>
    public static bool Run(Try t)
    {
        if (!AlbieTools.Has(t.tool))
        {
            Say("Say \"" + t.label + "\" and I'll do it.");
            return false;
        }
        AlbieTools.CallAsync(t.tool, JObject.Parse(t.argsJson), r => Report(t, r));
        return true;
    }

    static void Report(Try t, JObject r)
    {
        string error = (string)r?["error"];
        if (error != null)
        {
            int cut = error.IndexOf(": ", StringComparison.Ordinal);
            Say("That didn't work. " + (cut >= 0 ? error.Substring(cut + 2) : error), TapHint);
            return;
        }
        if ((string)r["status"] == "awaiting_approval")
        {
            Say("It needs your OK the first time. Tap Allow and I'll build it.", TapHint);
            return;
        }
        string say = (string)r["say"];
        Say(say != null ? t.done + " " + say : t.done, TapHint);
    }

    // ---------------------------------------------------------------- truck, resume, hotkeys

    void Update()
    {
        if (Time.unscaledTime >= nextTruckPoll)
        {
            nextTruckPoll = Time.unscaledTime + TruckPollS;
            bool now = BrainLoops.Active is SlalomBrain brain && brain != null && !brain.Hidden;
            if (now != truckOut) TruckChanged(now);
        }
#if UNITY_EDITOR
        var k = Keyboard.current;
        if (k == null) return;
        if (k.f9Key.wasPressedThisFrame) OnResumed(60f);
        if (k.f10Key.wasPressedThisFrame)
        {
            PlayerPrefs.DeleteKey(ShownPref);
            ShowCard(WelcomeLine);
        }
#endif
    }

    void TruckChanged(bool now)
    {
        truckOut = now;
        if (driveLine != null) driveLine.gameObject.SetActive(now);
        if (!now) return;
        lastHint = DriveHint;
        if (driveHintSaid) return;
        driveHintSaid = true;
        Say("Your truck is out. " + DriveHint, TapHint);
    }

    /// <summary>
    /// The app came back after <paramref name="secondsAway"/>. Over <see cref="ResumeAfterS"/>: Albie says welcome
    /// back, what we were doing, and the last hint again. Subscribed to AppLifecycle.Resumed at merge (see OnEnable).
    /// </summary>
    public void OnResumed(float secondsAway)
    {
        string line = ResumeLine(secondsAway, Doing(), lastHint);
        if (line == null) return;
        Say(line, ResumeHint);
        if (CardShown) driveLine.gameObject.SetActive(truckOut);
    }

    /// <summary>The welcome back line, or null when the person was away too short a time to need one.</summary>
    public static string ResumeLine(float secondsAway, string doing, string hint)
    {
        if (secondsAway <= ResumeAfterS) return null;
        string line = "Welcome back! We were " + (string.IsNullOrEmpty(doing) ? "just getting started" : doing) + ".";
        return string.IsNullOrEmpty(hint) ? line : line + " " + hint;
    }

    string Doing()
    {
        if (truckOut) return "driving the slalom truck";
        var host = ArtifactHost.Current;
        if (host != null && host.OpenId != null) return "looking at the " + host.OpenTitle.ToLowerInvariant();
        var ws = Workspace.Current;
        if (ws != null && !string.IsNullOrEmpty(ws.ActiveMode)) return "in the " + ws.ActiveMode + " view";
        return null;
    }

    // ---------------------------------------------------------------- voice tool

    /// <summary>help_show: the welcome card again, and what's on it for the voice model to talk about.</summary>
    public static JObject HelpShowTool(JObject args)
    {
        if (Instance == null) throw new ArgumentException("no_desk: there's no desk here to show the welcome card on");
        Instance.ShowCard(null);
        var tries = new JArray();
        foreach (var t in Tries) tries.Add(t.label);
        var result = new JObject { ["status"] = "shown", ["how_to_talk"] = TalkLine, ["try"] = tries };
        if (Instance.truckOut) result["driving"] = DriveHint;
        return result;
    }

    // ---------------------------------------------------------------- speech

    /// <summary>Albie says it: voice on, albie.say (the voice model says it, <paramref name="hint"/> is for it only); voice off, pet.say (the caption).</summary>
    public static void Say(string line, string hint = null)
    {
        LastSaid = line;
        if (SayHook != null)
        {
            SayHook(line, hint);
            return;
        }
        Debug.Log("[FirstRun] Albie: " + line);
        if (ForgeNet.LocalPeer is AlbieVoice) ForgeNet.Send("albie.say", new JObject { ["text"] = hint == null ? line : line + " " + hint });
        else ForgeNet.Inject("pet.say", new JObject { ["pet_id"] = PetId, ["text"] = line });
    }

    // ---------------------------------------------------------------- placement

    // In view and within reach: the first spot of the fan (straight ahead first, then to either side) that covers no
    // other panel as seen from the eyes. Nowhere clear: the first spot. Once per show, never per frame.
    void Place(Transform t, Vector2 sizeM, float[] yaws, float[] drops)
    {
        Transform eye = Head;
        if (eye == null) return;
        Vector3 ahead = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
        if (ahead.sqrMagnitude < 1e-4f) ahead = Vector3.ProjectOnPlane(eye.up, Vector3.up);  // looking straight down
        ahead.Normalize();
        Vector3 at = default;
        bool found = false;
        for (int i = 0; i < yaws.Length && !found; i++)
            for (int j = 0; j < drops.Length && !found; j++)
            {
                Vector3 spot = eye.position + Quaternion.AngleAxis(yaws[i], Vector3.up) * ahead * Reach + Vector3.down * drops[j];
                if (i == 0 && j == 0) at = spot;
                if (Clear(eye.position, spot, sizeM * 0.5f, t))
                {
                    at = spot;
                    found = true;
                }
            }
        t.rotation = Quaternion.LookRotation(at - eye.position, Vector3.up);  // a world-space canvas reads from behind its +Z
        t.position = at;
        t.position += at - t.TransformPoint(((RectTransform)t).rect.center);
    }

    bool Clear(Vector3 eye, Vector3 at, Vector2 half, Transform self)
    {
        var all = ForgePanel.All;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || p.transform == self || (card != null && p == card) || (orb != null && p.gameObject == orb.gameObject)) continue;
            var rt = (RectTransform)p.transform;
            Vector2 otherHalf = rt.rect.size * (0.5f * p.transform.lossyScale.x);
            if (Overlaps(eye, at, half, rt.TransformPoint(rt.rect.center), otherHalf)) return false;
        }
        return true;
    }

    /// <summary>Two panels (centres, half sizes in m) overlap as seen from <paramref name="eye"/>, with a small margin.</summary>
    public static bool Overlaps(Vector3 eye, Vector3 a, Vector2 halfA, Vector3 b, Vector2 halfB)
    {
        Angles(eye, a, halfA, out float yawA, out float pitchA, out float hyA, out float hpA);
        Angles(eye, b, halfB, out float yawB, out float pitchB, out float hyB, out float hpB);
        return Mathf.Abs(Mathf.DeltaAngle(yawA, yawB)) < hyA + hyB + AngleMargin && Mathf.Abs(pitchA - pitchB) < hpA + hpB + AngleMargin;
    }

    static void Angles(Vector3 eye, Vector3 at, Vector2 half, out float yaw, out float pitch, out float halfYaw, out float halfPitch)
    {
        Vector3 d = at - eye;
        float dist = Mathf.Max(0.05f, d.magnitude);
        yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        pitch = Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
        halfYaw = Mathf.Atan2(half.x, dist) * Mathf.Rad2Deg;
        halfPitch = Mathf.Atan2(half.y, dist) * Mathf.Rad2Deg;
    }

    // ---------------------------------------------------------------- building

    ForgePanel NewPanel(string name, string id)
    {
        var panel = Instantiate(panelPrefab, Holder);
        panel.name = name;
        panel.panelId = id;
        if (panel.GetComponent<GraphicRaycaster>() == null) panel.gameObject.AddComponent<GraphicRaycaster>();  // mouse (DesktopRig)
        var placeholder = panel.Content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);
        return panel;
    }

    ForgePanel BuildCard()
    {
        var panel = NewPanel("FirstRunCard", "first-run");
        panel.SizeMeters = new Vector2(CardW, CardH) * ForgePanel.MetersPerUnit;
        panel.Title = "Welcome";
        panel.Subtitle = "How to work with Albie";
        panel.SetBadge("HELP", ForgeTheme.Code);

        RectTransform content = panel.Content;
        float y = 8f;
        Line(content, TalkLine, 16f, ForgeTheme.PanelText, ref y, 44f);
        y += 8f;
        Line(content, TryLine, 13f, ForgeTheme.PanelSubtext, ref y, 18f);
        y += 6f;
        for (int i = 0; i < Tries.Length; i++)
        {
            int index = i;
            var pill = ForgePill.Create(content, Tries[i].label, () => Tap(index), PillH, PillText);
            var rt = pill.Rect;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(4f, -y);
            y += PillH + PillGap;
        }
        y += 4f;
        driveLine = Line(content, DriveHint, 14f, ForgeTheme.PanelSubtext, ref y, 36f);
        driveLine.gameObject.SetActive(truckOut);

        var done = ForgePill.Create(content, "Got it", CloseCard, 32f, 15f);
        done.Rect.anchorMin = done.Rect.anchorMax = done.Rect.pivot = new Vector2(1f, 0f);
        done.Rect.anchoredPosition = new Vector2(-4f, 4f);

        if (Application.isPlaying && Head != null)
        {
            var handles = panel.gameObject.AddComponent<PanelManipulator>();
            handles.Init(Head, deskFrame != null ? deskFrame : Head);
            handles.AddMinimize(CloseCard);
        }
        return panel;
    }

    // The "?" dot: a frameless panel with a PanelOrb, as the desk extras minimize into (DeskOrb).
    PanelOrb BuildOrb()
    {
        var panel = NewPanel("FirstRunHelp", "first-run-help");
        panel.Title = "";
        panel.SizeMeters = Vector2.one * OrbSize;
        var background = panel.transform.Find("Background");
        if (background != null) background.gameObject.SetActive(false);
        var titleBar = panel.transform.Find("TitleBar");
        if (titleBar != null) titleBar.gameObject.SetActive(false);
        RectTransform content = panel.Content;
        content.anchorMin = Vector2.zero;
        content.anchorMax = Vector2.one;
        content.offsetMin = content.offsetMax = Vector2.zero;
        var dot = panel.gameObject.AddComponent<PanelOrb>();
        dot.Init(Head);
        dot.SetText("?", "HELP");
        dot.SetHue(ForgeTheme.Code);
        dot.Tapped += Reopen;
        return dot;
    }

    static TMP_Text Line(RectTransform parent, string text, float size, Color color, ref float y, float height)
    {
        var t = new GameObject("Line", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
        t.rectTransform.SetParent(parent, false);
        t.rectTransform.anchorMin = new Vector2(0f, 1f);
        t.rectTransform.anchorMax = new Vector2(1f, 1f);
        t.rectTransform.pivot = new Vector2(0.5f, 1f);
        t.rectTransform.sizeDelta = new Vector2(-8f, height);
        t.rectTransform.anchoredPosition = new Vector2(0f, -y);
        if (ForgeTheme.Font != null) t.font = ForgeTheme.Font;
        t.fontSize = size;
        t.color = color;
        t.alignment = TextAlignmentOptions.TopLeft;
        t.textWrappingMode = TextWrappingModes.Normal;
        t.overflowMode = TextOverflowModes.Overflow;
        t.raycastTarget = false;
        t.text = text;
        y += height;
        return t;
    }
}
