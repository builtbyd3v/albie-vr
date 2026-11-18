using System;
using System.Collections.Generic;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// The hands' brain. Each frame it reads both hands from an <see cref="IHandSource"/>, works out what they're doing and
/// runs the matching <see cref="GestureActions"/>. It installs itself in any scene with a <see cref="Workspace"/>; the
/// desktop rig gets a <see cref="SimulatedHandSource"/> so it all runs with no headset. Pinching and panel dragging stay with the Interaction
/// SDK: gestures hold off while a panel is held. Teach a gesture with <see cref="Teach"/>. No allocations per frame.
/// </summary>
public class GestureEngine : MonoBehaviour
{
    const float TargetKeepSeconds = 0.35f;  // a flick swings the finger off the window: remember it this long
    const float AimStillSpeed = 0.5f;       // m/s: the aim only changes target while the fingertip is slower than this
    const float PokeRange = 0.18f;          // m: a fingertip this close to a window is poking it, not pointing at it
    const float NearPokeRange = 0.05f;      // m: the same, for a window that came to you
    const float AimMarginDegrees = 3f;      // around a window's edges, so it isn't fussy at a distance
    const float LookMarginDegrees = 6f;     // the same for the window you look at: eyes wander more than a finger
    const float QuietSeconds = 0.5f;        // after a gesture fires, no other starts on that hand for this long
    const float PointQuietSeconds = 0.5f;   // an open or close can't start this soon after the hand was pointing

    public static GestureEngine Current { get; private set; }

    [Tooltip("The panel the gesture display uses. Empty: the workspace's.")]
    public ForgePanel panelPrefab;
    [Tooltip("The dot a window minimizes into. Empty: the desk's.")]
    public PanelOrb orbPrefab;
    [Tooltip("Show the gesture display the first time a hand appears.")]
    public bool showDisplayOnFirstHand = true;

    public GestureLibrary Library { get; private set; }
    public readonly GestureHand[] Hands = { new GestureHand(), new GestureHand() };
    public GestureActions Actions { get; private set; }
    public GestureFeedback Feedback { get; private set; }

    /// <summary>A gesture fired, with the hand that made it (it runs whatever it's bound to).</summary>
    public event Action<GestureDef, GestureHand> Fired;

    /// <summary>Hands are still read, but no gesture fires and the display stays away (the scripted demo owns the hands).</summary>
    public bool Paused { get; set; }

    /// <summary>What the recorder wants the user to do, or null when not teaching.</summary>
    public string TeachPrompt => recorder != null ? recorder.Prompt : null;

    IHandSource source;
    readonly List<GestureMatcher>[] matchers = { new List<GestureMatcher>(), new List<GestureMatcher>() };
    GestureRecorder recorder;
    string teachId, teachTitle, teachAction;
    JObject teachArgs;
    Action<JObject> teachDone;
    bool displayShown;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindAnyObjectByType<GestureEngine>() != null || FindAnyObjectByType<Workspace>() == null) return;
        new GameObject(nameof(GestureEngine)).AddComponent<GestureEngine>();
    }

    void Awake()
    {
        Current = this;
        Library = new GestureLibrary(System.IO.Path.Combine(Application.persistentDataPath, "gestures.json"));
        Library.Changed += RebuildMatchers;
        RebuildMatchers();
        Actions = new GestureActions(this);
        Feedback = gameObject.AddComponent<GestureFeedback>();
        Feedback.Init(this);
        for (int i = 0; i < Hands.Length; i++) Hands[i].sample.isLeft = i == 0;
    }

    // Chosen on the first frame: the rig selector picks the desktop rig or the headset as Play starts.
    IHandSource PickSource()
    {
        var simulated = FindAnyObjectByType<SimulatedHandSource>();
        if (simulated != null) return simulated;
        if (DesktopRig.Current != null) return gameObject.AddComponent<SimulatedHandSource>();
        return new IsdkHandSource();
    }

    void OnDestroy()
    {
        if (Current == this) Current = null;
    }

    void RebuildMatchers()
    {
        for (int h = 0; h < matchers.Length; h++)
        {
            matchers[h].Clear();
            foreach (var def in Library.All) matchers[h].Add(new GestureMatcher(def));
        }
    }

    void Update()
    {
        Transform head = PetUser.Head;
        if (head == null) return;
        if (source == null) source = PickSource();
        float now = Time.time;
        bool held = PanelManipulator.Grabbing > 0;

        for (int h = 0; h < Hands.Length; h++)
        {
            GestureHand hand = Hands[h];
            source.Read(h, hand.sample);
            hand.Update(now, head.position, head.rotation);
            UpdateAim(hand, head, now);
            if (!hand.Deliberate || Paused)
            {
                for (int m = 0; m < matchers[h].Count; m++) matchers[h][m].Reset();
                continue;
            }
            if (recorder != null || held) continue;
            GestureDef fired = StepHand(matchers[h], hand, now, hand.Target != null, Library.Split);
            if (fired != null) Fire(fired, hand);
        }

        Actions.Tick(head);
        if (recorder != null) TickRecorder(now);

        if (!Paused && !displayShown && showDisplayOnFirstHand && (Hands[0].Deliberate || Hands[1].Deliberate))
        {
            displayShown = true;
            Actions.ShowDisplay(true);
        }
    }

    /// <summary>
    /// One hand's gestures for one frame; returns the one that fired, or null. At most one fires, and then the others on
    /// that hand start over and wait <see cref="QuietSeconds"/>, so the tail of one gesture can't set off another. An open
    /// or close can't start until the hand has stopped pointing for a moment, and an open's fist has to be made afresh
    /// after that, which is what keeps curling the finger after a flick, then opening the hand, from doing anything. Shared with the self-test. No allocations.
    /// </summary>
    public static GestureDef StepHand(List<GestureMatcher> matchers, GestureHand hand, float now, bool hasTarget, HandSplit split)
    {
        if (GestureLibrary.IsAiming(hand.values)) hand.PointedAt = now;
        if (now < hand.QuietUntil) return null;
        bool afterPoint = now - hand.PointedAt < PointQuietSeconds;
        for (int m = 0; m < matchers.Count; m++)
        {
            GestureMatcher matcher = matchers[m];
            GestureDef def = matcher.def;
            if (!def.AllowedOn(hand.sample.isLeft, split))
            {
                matcher.Reset();
                continue;
            }
            if (def.quietAfterPoint && afterPoint)
            {
                if (def.freshAfterPoint) matcher.HoldOff(hand.values);
                else matcher.Reset();
                continue;
            }
            if (!matcher.Step(hand.values, hand.motion, now, !def.NeedsTarget || hasTarget)) continue;
            hand.QuietUntil = now + QuietSeconds;
            for (int other = 0; other < matchers.Count; other++)
                if (other != m) matchers[other].Reset();
            return def;
        }
        return null;
    }

    void Fire(GestureDef def, GestureHand hand)
    {
        Debug.Log($"[Gestures] {def.id} ({(hand.sample.isLeft ? "left" : "right")} hand) -> {def.action}");
        Fired?.Invoke(def, hand);
        string result = Actions.Run(def, hand);
        Feedback.Say(hand, string.IsNullOrEmpty(result) ? def.title : result);
    }

    // ---------------------------------------------------------------- aiming

    void UpdateAim(GestureHand hand, Transform head, float now)
    {
        ForgePanel found = null;
        if (hand.Usable && GestureLibrary.IsAiming(hand.values))
        {
            // A fast fingertip is a flick in progress: the window it started on still counts.
            if (hand.motion.tipVelocity.sqrMagnitude >= AimStillSpeed * AimStillSpeed) return;
            found = FindTarget(hand, head.position);
        }
        if (found != null)
        {
            hand.Target = found;
            hand.TargetSeenAt = now;
        }
        else if (hand.Target != null && now - hand.TargetSeenAt > TargetKeepSeconds)
        {
            hand.Target = null;
        }
    }

    static ForgePanel FindTarget(GestureHand hand, Vector3 headPosition) => FindTarget(hand.AimRay(headPosition), true, AimMarginDegrees);

    /// <summary>The window in the middle of the view, for the gestures that act on the window you look at. Null if none.</summary>
    public static ForgePanel LookedAt(Transform head) => head != null ? FindTarget(new Ray(head.position, head.forward), false, LookMarginDegrees) : null;

    static ForgePanel FindTarget(Ray ray, bool skipPoked, float marginDegrees)
    {
        ForgePanel best = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < ForgePanel.All.Count; i++)
        {
            ForgePanel panel = ForgePanel.All[i];
            var handles = panel.Handles;
            if (handles == null || handles.Busy) continue;
            Transform t = panel.transform;
            var plane = new Plane(t.forward, t.position);
            if (!plane.Raycast(ray, out float distance) || distance >= bestDistance) continue;
            // A window you pulled in sits within reach, and you still need to be able to flick it away.
            if (skipPoked)
            {
                float pokeRange = panel.TryGetComponent(out GestureWindow window) && window.IsNear ? NearPokeRange : PokeRange;
                if (distance < pokeRange) continue;
            }
            Vector3 local = t.InverseTransformPoint(ray.GetPoint(distance));
            Rect rect = ((RectTransform)t).rect;
            float margin = distance * Mathf.Tan(marginDegrees * Mathf.Deg2Rad) / ForgePanel.MetersPerUnit;
            if (local.x < rect.xMin - margin || local.x > rect.xMax + margin || local.y < rect.yMin - margin || local.y > rect.yMax + margin) continue;
            best = panel;
            bestDistance = distance;
        }
        return best;
    }

    // ---------------------------------------------------------------- teaching

    /// <summary>
    /// Watch the user show a gesture three times, save it and bind it. <paramref name="done"/> gets the result when the
    /// takes are over: {ok, gesture, how} or {error}, in words Albie can read out.
    /// </summary>
    public void Teach(string title, string action, JObject args, Action<JObject> done)
    {
        if (recorder != null)
        {
            done(AlbieTools.Error("I'm already watching for a gesture."));
            return;
        }
        string bindError = GestureActions.CheckBinding(action);
        if (bindError != null)
        {
            done(AlbieTools.Error(bindError));
            return;
        }
        string id = GestureLibrary.Slug(title);
        if (id.Length == 0)
        {
            done(AlbieTools.Error("A gesture needs a name."));
            return;
        }
        var existing = Library.Find(id);
        if (existing != null && existing.builtin)
        {
            done(AlbieTools.Error($"'{existing.title}' is one of the built-in gestures. Pick another name, or rebind it with gesture_bind."));
            return;
        }
        teachId = id;
        teachTitle = title.Trim();
        teachAction = action;
        teachArgs = args;
        teachDone = done;
        recorder = new GestureRecorder(Time.time);
        Actions.ShowDisplay(true);
    }

    public void CancelTeach()
    {
        if (recorder == null) return;
        recorder = null;
        var done = teachDone;
        teachDone = null;
        done?.Invoke(AlbieTools.Error("Stopped watching; nothing was saved."));
    }

    void TickRecorder(float now)
    {
        GestureHand hand = MostInView();
        recorder.Tick(now, hand);
        Feedback.Show(hand != null ? hand : Hands[1], recorder.Prompt);
        if (!recorder.Finished) return;

        var takes = recorder.Recorded;
        recorder = null;
        var done = teachDone;
        teachDone = null;
        foreach (var list in matchers)
            foreach (var m in list) m.Reset();
        done?.Invoke(FinishTeach(takes));
    }

    GestureHand MostInView()
    {
        GestureHand left = Hands[0], right = Hands[1];
        if (!left.Usable) return right.Usable ? right : null;
        if (!right.Usable) return left;
        return left.values[(int)HandFeature.InView] > right.values[(int)HandFeature.InView] ? left : right;
    }

    /// <summary>Takes to saved gesture, or the reason not. Public for the self-test.</summary>
    public JObject FinishTeach(List<GestureRecorder.Take> takes)
    {
        var def = GestureRecorder.Analyze(takes, teachId, teachTitle, out string error, out float[] center);
        if (def == null) return AlbieTools.Error(error);
        GestureDef clash = Library.Clash(def, center);
        if (clash != null) return AlbieTools.Error($"That's too close to '{clash.title}'. Try something more different.");
        def.action = teachAction;
        def.args = teachArgs;
        Library.Add(def);
        Debug.Log($"[Gestures] learned {def.id}: {def.ToJson()}");
        return new JObject { ["ok"] = true, ["gesture"] = def.title, ["how"] = def.how, ["does"] = GestureActions.Describe(def) };
    }
}
