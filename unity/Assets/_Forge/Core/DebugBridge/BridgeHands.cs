#if DEVELOPMENT_BUILD || UNITY_EDITOR
// Debug bridge hand commands: a laptop makes a pretend hand perform gestures on a headset nobody wears.
//   hand         one gesture, through the real GestureEngine (recognition and the action it is bound to)
//   hands        what the engine sees now; {"list": true} names the gestures `hand` can perform
//   hand_replay  {"frames": [...]}: joint frames (head space) played back at their timing
//   hand_matrix  every gesture N times with random speed, jitter and sloppiness: a confusion table, dry (no actions)
// While a motion plays, this component IS the engine's hand source (swapped in by reflection on GestureEngine.source,
// restored after), so everything from the joints on is the code real hands go through. Hands are made by SyntheticHand,
// the same hand model as SimulatedHandSource and the gesture tests. Only the joints are pretend.

using System;
using System.Collections.Generic;
using System.Reflection;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class BridgeHands : MonoBehaviour, IHandSource
{
    const float Dt = 1f / 72f;   // generated frames: the headset's hand tracking rate
    const float Lead = 0.2f;     // untracked at the start of every motion: the engine drops the last source's history and matcher state
    const float MatrixGap = 0.7f;  // untracked between matrix runs: longer than GestureMatcher's 0.6 s cooldown
    const int MaxFrames = 30000;
    static readonly Vector3 Rest = new Vector3(0.1f, -0.17f, 0.34f);  // head space, right hand; SimulatedHandSource's rest
    static readonly string[] Gestures = { GestureLibrary.FlickIn, GestureLibrary.FlickOut, GestureLibrary.PalmUp, GestureLibrary.FistClose, GestureLibrary.FistOpen, GestureLibrary.FistCloseAll, GestureLibrary.FistOpenAll };
    static readonly string[] ShapeNames = { "relaxed", "point", "fist", "open", "peace", "rock" };
    static readonly HandShape[] ShapeValues = { HandShape.Relaxed, HandShape.Point, HandShape.Fist, HandShape.Open, HandShape.Peace, HandShape.Rock };
    static readonly FieldInfo SourceField = typeof(GestureEngine).GetField("source", BindingFlags.NonPublic | BindingFlags.Instance);

    sealed class Frame { public float t; public Vector3[] left, right; }  // head space; null = that hand isn't tracked
    sealed class Fire { public string gesture, hand, action, target; public float t; }
    struct Vary { public float speed, jitter, sloppy; }

    static BridgeHands instance;

    GestureEngine subscribed;
    readonly string[] lastGesture = new string[2];
    readonly float[] lastAt = new float[2];

    List<Frame> frames;   // non-null while playing
    int cursor;
    float startTime;
    GestureEngine playingOn;
    object savedSource;
    readonly List<Fire> fired = new List<Fire>();
    Action<List<Fire>> onDone;
    readonly List<GestureDef> dryDefs = new List<GestureDef>();
    readonly List<string> dryActions = new List<string>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        var go = new GameObject("BridgeHands") { hideFlags = HideFlags.HideInHierarchy };
        DontDestroyOnLoad(go);
        instance = go.AddComponent<BridgeHands>();
        DebugBridge.Register("hand", (req, ok, fail) => instance.Hand(req, ok, fail));
        DebugBridge.Register("hands", (req, ok, fail) => instance.Hands(req, ok, fail));
        DebugBridge.Register("hand_replay", (req, ok, fail) => instance.Replay(req, ok, fail));
        DebugBridge.Register("hand_matrix", (req, ok, fail) => instance.Matrix(req, ok, fail));
    }

    void Update()
    {
        var engine = GestureEngine.Current;
        if (engine != subscribed)
        {
            if (subscribed != null) subscribed.Fired -= OnFired;
            subscribed = engine;
            if (engine != null) engine.Fired += OnFired;
        }
        if (frames != null && (playingOn == null || Time.time - startTime > frames[frames.Count - 1].t + Dt)) Finish();
    }

    void OnDestroy()
    {
        if (frames != null) Finish();
        if (subscribed != null) subscribed.Fired -= OnFired;
    }

    void OnFired(GestureDef def, GestureHand hand)
    {
        int h = hand.sample.isLeft ? 0 : 1;
        lastGesture[h] = def.id;
        lastAt[h] = Time.time;
        if (frames == null) return;
        int dry = dryDefs.IndexOf(def);
        fired.Add(new Fire
        {
            gesture = def.id, hand = h == 0 ? "left" : "right", t = Time.time - startTime,
            action = dry >= 0 ? "dry:" + dryActions[dry] : def.action, target = hand.Target != null ? hand.Target.name : null,
        });
    }

    // ---------------------------------------------------------------- the hand source (engine's Update)

    public void Read(int hand, HandSample sample)
    {
        sample.tracked = false;
        sample.highConfidence = false;
        Transform head = PetUser.Head;
        if (frames == null || head == null) return;
        float t = Time.time - startTime;
        while (cursor + 1 < frames.Count && frames[cursor + 1].t <= t) cursor++;
        Frame a = frames[cursor], b = cursor + 1 < frames.Count ? frames[cursor + 1] : a;
        Vector3[] ja = hand == 0 ? a.left : a.right, jb = hand == 0 ? b.left : b.right;
        if (ja == null) return;
        float k = jb != null && b.t > a.t ? Mathf.Clamp01((t - a.t) / (b.t - a.t)) : 0f;
        for (int i = 0; i < HandJoint.Count; i++)
            sample.joints[i] = head.TransformPoint(jb != null ? Vector3.LerpUnclamped(ja[i], jb[i], k) : ja[i]);
        sample.isLeft = hand == 0;
        sample.tracked = true;
        sample.highConfidence = true;
    }

    // ---------------------------------------------------------------- playing

    string Play(List<Frame> motion, bool dry, Action<List<Fire>> done)
    {
        if (frames != null) return "busy: a hand motion is already playing";
        var engine = GestureEngine.Current;
        if (engine == null) return "no gesture engine in this scene";
        if (SourceField == null) return "GestureEngine changed shape: BridgeHands needs updating (no 'source' field)";
        savedSource = SourceField.GetValue(engine);
        SourceField.SetValue(engine, this);
        playingOn = engine;
        dryDefs.Clear();
        dryActions.Clear();
        if (dry)
            foreach (var def in engine.Library.All)
            {
                if (def.action == GestureActions.None) continue;
                dryDefs.Add(def);
                dryActions.Add(def.action);
                // "window.dry" still needs a target (GestureDef.NeedsTarget), so gating stays as it is; Actions.Run ignores it.
                def.action = def.NeedsTarget ? "window.dry" : GestureActions.None;
            }
        fired.Clear();
        frames = motion;
        cursor = 0;
        startTime = Time.time;
        onDone = done;
        return null;
    }

    void Finish()
    {
        if (playingOn != null) SourceField.SetValue(playingOn, savedSource);
        for (int i = 0; i < dryDefs.Count; i++) dryDefs[i].action = dryActions[i];
        dryDefs.Clear();
        dryActions.Clear();
        playingOn = null;
        savedSource = null;
        frames = null;
        var done = onDone;
        onDone = null;
        done?.Invoke(new List<Fire>(fired));
    }

    // ---------------------------------------------------------------- commands

    void Hand(JObject req, Action<JObject> ok, Action<string> fail)
    {
        Transform head = PetUser.Head;
        if (head == null) { fail("no head camera yet"); return; }
        string gesture = ((string)req["gesture"] ?? "").Trim().ToLowerInvariant();
        bool left = ((string)req["hand"] ?? "right").Trim().ToLowerInvariant() == "left";
        int seed = req["seed"] != null ? (int)req["seed"] : Environment.TickCount;
        var rng = new System.Random(seed);
        var v = new Vary
        {
            speed = Mathf.Clamp((float?)req["speed"] ?? 1f, 0.5f, 2f),
            jitter = Mathf.Clamp((float?)req["jitter_m"] ?? 0f, 0f, 0.02f),
            sloppy = Mathf.Clamp01((float?)req["sloppy"] ?? 0f),
        };
        string error = Aim(req, gesture, head, out Vector3 aimAt, out string targetName);
        if (error != null) { fail(error); return; }
        var motion = new List<Frame>();
        float t = 0f;
        error = Script(gesture, left, Vec(req["at"]), aimAt, (float?)req["hold_s"], v, rng, head, motion, ref t);
        if (error != null) { fail(error); return; }
        if ((error = Budget(req, t)) != null) { fail(error); return; }
        var fields = new JObject
        {
            ["gesture"] = gesture, ["hand"] = left ? "left" : "right", ["target"] = targetName,
            ["speed"] = v.speed, ["jitter_m"] = v.jitter, ["sloppy"] = v.sloppy, ["seed"] = seed, ["duration_s"] = Math.Round(t, 2),
        };
        error = Play(motion, false, list => ok(Answer(fields, list)));
        if (error != null) fail(error);
    }

    void Replay(JObject req, Action<JObject> ok, Action<string> fail)
    {
        if (!(req["frames"] is JArray raw) || raw.Count == 0) { fail("hand_replay needs \"frames\": [{\"t\", \"left\"?, \"right\"?}], 24 joints x 3 floats each, head space"); return; }
        if (raw.Count > MaxFrames) { fail($"at most {MaxFrames} frames"); return; }
        float dt = (float?)req["dt"] ?? Dt;
        var motion = new List<Frame> { new Frame { t = 0f } };
        float last = 0f;
        for (int i = 0; i < raw.Count; i++)
        {
            var f = raw[i] as JObject;
            if (f == null) { fail($"frame {i} isn't an object"); return; }
            float t = Lead + ((float?)f["t"] ?? i * dt);
            if (t < last) { fail($"frame {i}: t goes backwards"); return; }
            last = t;
            var frame = new Frame { t = t, left = Joints(f["left"]), right = Joints(f["right"]) };
            if ((f["left"] != null && f["left"].Type != JTokenType.Null && frame.left == null) || (f["right"] != null && f["right"].Type != JTokenType.Null && frame.right == null))
            { fail($"frame {i}: a hand must be {HandJoint.Count * 3} numbers (joint x,y,z in HandJoint order, head space)"); return; }
            motion.Add(frame);
        }
        string error = Budget(req, last);
        if (error != null) { fail(error); return; }
        var fields = new JObject { ["frames"] = raw.Count, ["duration_s"] = Math.Round(last, 2) };
        error = Play(motion, false, list => ok(Answer(fields, list)));
        if (error != null) fail(error);
    }

    void Matrix(JObject req, Action<JObject> ok, Action<string> fail)
    {
        Transform head = PetUser.Head;
        if (head == null) { fail("no head camera yet"); return; }
        var gestures = new List<string>();
        if (req["gestures"] is JArray wanted) foreach (var g in wanted) gestures.Add(((string)g ?? "").Trim().ToLowerInvariant());
        int n = Mathf.Clamp((int?)req["n"] ?? 3, 1, 20);
        bool left = ((string)req["hand"] ?? "right").Trim().ToLowerInvariant() == "left";
        if (req["gestures"] == null)
        {
            // Only what this hand can make under the Hands setting.
            var library = GestureEngine.Current != null ? GestureEngine.Current.Library : null;
            foreach (string id in Gestures)
            {
                GestureDef def = library?.Find(id);
                if (def == null || def.AllowedOn(left, library.Split)) gestures.Add(id);
            }
        }
        int seed = req["seed"] != null ? (int)req["seed"] : Environment.TickCount;
        var rng = new System.Random(seed);
        Vector2 speed = Range(req["speed"], 0.7f, 1.4f, 0.5f, 2f), jitter = Range(req["jitter_m"], 0f, 0.003f, 0f, 0.02f), sloppy = Range(req["sloppy"], 0f, 0.3f, 0f, 1f);

        var motion = new List<Frame>();
        var runs = new List<(string gesture, float start, Vary v)>();
        float t = 0f;
        foreach (string gesture in gestures)
        {
            string error = Aim(req, gesture, head, out Vector3 aimAt, out _);
            if (error != null) { fail(error); return; }
            for (int i = 0; i < n; i++)
            {
                var v = new Vary { speed = Pick(rng, speed), jitter = Pick(rng, jitter), sloppy = Pick(rng, sloppy) };
                runs.Add((gesture, t, v));
                error = Script(gesture, left, Vec(req["at"]), aimAt, null, v, rng, head, motion, ref t);
                if (error != null) { fail(error); return; }
                Gap(motion, ref t, MatrixGap);
            }
        }
        string budget = Budget(req, t);
        if (budget != null) { fail(budget); return; }

        float total = t;
        string playError = Play(motion, true, list =>
        {
            var seen = new List<HashSet<string>>();
            foreach (var _ in runs) seen.Add(new HashSet<string>());
            foreach (var f in list)
                for (int r = runs.Count - 1; r >= 0; r--)
                    if (f.t >= runs[r].start) { seen[r].Add(f.gesture); break; }
            var table = new JObject();
            var correct = new JObject();
            var detail = new JArray();
            int confusions = 0;
            for (int r = 0; r < runs.Count; r++)
            {
                string g = runs[r].gesture;
                if (!(table[g] is JObject row)) table[g] = row = new JObject();
                if (seen[r].Count == 0) Count(row, "none");
                foreach (string got in seen[r])
                {
                    Count(row, got);
                    if (got != g) confusions++;
                }
                correct[g] = ((int?)correct[g] ?? 0) + (seen[r].Contains(g) ? 1 : 0);
                detail.Add(new JObject
                {
                    ["gesture"] = g, ["speed"] = Math.Round(runs[r].v.speed, 2), ["jitter_m"] = Math.Round(runs[r].v.jitter, 4),
                    ["sloppy"] = Math.Round(runs[r].v.sloppy, 2), ["recognized"] = new JArray(new List<string>(seen[r]).ToArray()),
                });
            }
            ok(new JObject
            {
                ["n"] = n, ["seed"] = seed, ["hand"] = left ? "left" : "right", ["dry"] = true, ["duration_s"] = Math.Round(total, 1),
                ["table"] = table, ["correct"] = correct, ["confusions"] = confusions, ["runs"] = detail,
            });
        });
        if (playError != null) fail(playError);
    }

    void Hands(JObject req, Action<JObject> ok, Action<string> fail)
    {
        var engine = GestureEngine.Current;
        if ((bool?)req["list"] == true)
        {
            var list = new JArray();
            foreach (string id in Gestures)
                list.Add(new JObject { ["name"] = id, ["how"] = engine != null ? engine.Library.Find(id)?.how : null });
            var holds = new JArray();
            foreach (string s in ShapeNames) holds.Add("hold:" + s);
            ok(new JObject { ["gestures"] = list, ["holds"] = holds });
            return;
        }
        if (engine == null) { fail("no gesture engine in this scene"); return; }
        object source = frames != null ? this : SourceField?.GetValue(engine);
        string kind = source == null ? "none" : source is IsdkHandSource ? "real" : source is SimulatedHandSource ? "simulated" : source is BridgeHands ? "bridge" : source.GetType().Name;
        var hands = new JObject();
        for (int h = 0; h < 2; h++)
        {
            GestureHand hand = engine.Hands[h];
            hands[h == 0 ? "left" : "right"] = new JObject
            {
                ["tracked"] = hand.sample.tracked, ["usable"] = hand.Usable,
                ["palm"] = hand.sample.tracked ? Arr(hand.Palm) : null,
                ["pose"] = hand.Usable ? GestureLibrary.PoseName(hand.values) : null,
                ["target"] = hand.Target != null ? hand.Target.name : null,
                ["last"] = lastGesture[h] == null ? null : new JObject { ["gesture"] = lastGesture[h], ["ago_s"] = Math.Round(Time.time - lastAt[h], 2) },
            };
        }
        ok(new JObject { ["source"] = kind, ["playing"] = frames != null, ["hands"] = hands });
    }

    // ---------------------------------------------------------------- making motions

    // Where a gesture aims: the named window, Albie, or the window nearest the middle of the view. Flicks need a window.
    static string Aim(JObject req, string gesture, Transform head, out Vector3 aimAt, out string targetName)
    {
        aimAt = head.position + head.forward;
        targetName = null;
        string target = ((string)req["target"] ?? "").Trim();
        bool flick = gesture == GestureLibrary.FlickIn || gesture == GestureLibrary.FlickOut;
        if (target.Equals("albie", StringComparison.OrdinalIgnoreCase))
        {
            var albie = FindAnyObjectByType<AlbieController>();
            if (albie == null) return "Albie isn't in the scene";
            aimAt = albie.transform.position;
            targetName = albie.name;
            return null;
        }
        if (target.Length == 0 && !flick) return null;
        ForgePanel best = null;
        float bestAngle = float.MaxValue;
        foreach (var panel in ForgePanel.All)
        {
            if (panel == null || !panel.isActiveAndEnabled || panel.Handles == null || panel.GetComponent<GesturePanel>() != null) continue;
            if (target.Length > 0 && panel.name.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0
                && (panel.panelId ?? "").IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0) continue;
            float angle = Vector3.Angle(head.forward, Center(panel) - head.position);
            if (angle < bestAngle) { best = panel; bestAngle = angle; }
        }
        if (best == null) return target.Length > 0 ? $"no open window matches '{target}'" : "no window to point at: open a view first";
        aimAt = Center(best);
        targetName = best.name;
        return null;
    }

    static Vector3 Center(ForgePanel panel) => panel.transform.TransformPoint(((RectTransform)panel.transform).rect.center);

    // A pointing hand at `wrist` whose aim ray (half finger direction, half eye through fingertip: GestureHand.AimRay) meets `target`.
    static Quaternion AimRotation(Vector3 wrist, Vector3 target, Transform head)
    {
        Vector3 tip = wrist + (target - wrist).normalized * 0.17f;
        Vector3 want = (target - tip).normalized, eye = (tip - head.position).normalized;
        Vector3 finger = 2f * want - eye;
        return Quaternion.LookRotation(finger.sqrMagnitude > 1e-4f ? finger.normalized : want, head.up);
    }

    // One gesture as frames, from time t on. Speed scales strokes and shape changes, not holds (a gesture's hold time is
    // fixed) and not the return after a flick (a person's hand comes back at its own pace). Sloppy moves every shape
    // toward a relaxed hand; jitter is noise on every joint every frame.
    static string Script(string gesture, bool left, Vector3? at, Vector3 aimAt, float? holdS, Vary v, System.Random rng, Transform head, List<Frame> into, ref float t)
    {
        Vector3 rest = at ?? head.TransformPoint(left ? new Vector3(-Rest.x, Rest.y, Rest.z) : Rest);
        Quaternion aim = AimRotation(rest, aimAt, head);
        Quaternion palmUp = Quaternion.LookRotation(head.forward + Vector3.down * 0.15f, Vector3.down);
        float m = 1f / v.speed;
        HandShape relaxed = HandShape.Relaxed;
        HandShape Sloppy(HandShape s) => HandShape.Lerp(s, relaxed, v.sloppy);
        var g = new Gen { into = into, left = left, head = head, rng = rng, jitter = v.jitter };
        g.t = t;
        g.Gap(Lead);
        switch (gesture)
        {
            case GestureLibrary.FlickIn:
            case GestureLibrary.FlickOut:
            {
                // SimulatedHandSource's Q / Y flicks; the stroke eases in and out, so it peaks at 1.5x its average speed.
                Vector3 by = head.TransformVector(gesture == GestureLibrary.FlickIn ? new Vector3(0f, 0.03f, -0.14f) : new Vector3(0f, 0f, 0.16f));
                HandShape point = Sloppy(HandShape.Point);
                g.Hold(0.25f, relaxed, rest, aim);
                g.Change(0.2f * m, relaxed, point, rest, rest, aim, aim);
                g.Hold(0.45f, point, rest, aim);
                g.Change(0.14f * m, point, point, rest, rest + by, aim, aim);
                g.Change(0.42f, point, point, rest + by, rest, aim, aim);
                g.Hold(holdS ?? 0.25f, point, rest, aim);
                break;
            }
            case GestureLibrary.PalmUp:
            {
                HandShape open = Sloppy(HandShape.Open);
                g.Hold(0.25f, relaxed, rest, aim);
                g.Change(0.25f * m, relaxed, open, rest, rest, aim, palmUp);
                g.Hold(holdS ?? 1f, open, rest, palmUp);
                break;
            }
            case GestureLibrary.FistClose:
            case GestureLibrary.FistCloseAll:
            {
                // A close starts from a hand held open (a resting hand closing isn't one).
                HandShape fist = Sloppy(HandShape.Fist), open = Sloppy(HandShape.Open);
                g.Hold(0.35f, open, rest, aim);
                g.Change(0.2f * m, open, fist, rest, rest, aim, aim);
                g.Hold(holdS ?? 0.4f, fist, rest, aim);
                break;
            }
            case GestureLibrary.FistOpen:
            case GestureLibrary.FistOpenAll:
            {
                HandShape fist = Sloppy(HandShape.Fist), open = Sloppy(HandShape.Open);
                g.Hold(0.45f, fist, rest, aim);
                g.Change(0.15f * m, fist, open, rest, rest, aim, aim);
                g.Hold(holdS ?? 0.3f, open, rest, aim);
                break;
            }
            default:
            {
                int shape = gesture.StartsWith("hold:", StringComparison.Ordinal) ? Array.IndexOf(ShapeNames, gesture.Substring(5)) : -1;
                if (shape < 0)
                    return $"unknown gesture '{gesture}'; known: {string.Join(", ", Gestures)}, or hold:{string.Join("|", ShapeNames)}";
                HandShape s = Sloppy(ShapeValues[shape]);
                g.Hold(0.2f, relaxed, rest, aim);
                g.Change(0.2f * m, relaxed, s, rest, rest, aim, aim);
                g.Hold(holdS ?? 1f, s, rest, aim);
                break;
            }
        }
        if (into.Count > MaxFrames) return $"too long: over {MaxFrames} frames";
        t = g.t;
        return null;
    }

    static void Gap(List<Frame> into, ref float t, float seconds)
    {
        var g = new Gen { into = into, t = t };
        g.Gap(seconds);
        t = g.t;
    }

    sealed class Gen
    {
        public List<Frame> into;
        public float t, jitter;
        public bool left;
        public Transform head;
        public System.Random rng;
        readonly HandSample sample = new HandSample();

        public void Gap(float seconds)
        {
            for (float e = 0f; e < seconds; e += Dt) { into.Add(new Frame { t = t }); t += Dt; }
        }

        public void Hold(float seconds, HandShape shape, Vector3 wrist, Quaternion rotation) => Change(seconds, shape, shape, wrist, wrist, rotation, rotation);

        public void Change(float seconds, HandShape from, HandShape to, Vector3 wristFrom, Vector3 wristTo, Quaternion rotFrom, Quaternion rotTo)
        {
            int count = Mathf.Max(1, Mathf.RoundToInt(seconds / Dt));
            for (int i = 1; i <= count; i++)
            {
                float k = (float)i / count;
                k = k * k * (3f - 2f * k);
                SyntheticHand.Build(sample, HandShape.Lerp(from, to, k), Vector3.Lerp(wristFrom, wristTo, k), Quaternion.Slerp(rotFrom, rotTo, k), left);
                var joints = new Vector3[HandJoint.Count];
                for (int j = 0; j < joints.Length; j++)
                {
                    joints[j] = head.InverseTransformPoint(sample.joints[j]);
                    if (jitter > 0f) joints[j] += new Vector3(Noise(), Noise(), Noise()) * jitter;
                }
                into.Add(left ? new Frame { t = t, left = joints } : new Frame { t = t, right = joints });
                t += Dt;
            }
        }

        float Noise() => (float)(rng.NextDouble() * 2.0 - 1.0);
    }

    // ---------------------------------------------------------------- helpers

    static JObject Answer(JObject fields, List<Fire> list)
    {
        var recognized = new JArray();
        var actions = new JArray();
        foreach (var f in list)
        {
            recognized.Add(new JObject { ["gesture"] = f.gesture, ["hand"] = f.hand, ["t"] = Math.Round(f.t, 3), ["action"] = f.action, ["target"] = f.target });
            actions.Add(f.action);
        }
        fields["recognized"] = recognized;
        fields["actions"] = actions;
        return fields;
    }

    // The bridge stops waiting after timeout_s (30 s by default): say so before starting, not after.
    static string Budget(JObject req, float seconds)
    {
        float timeout = req["timeout_s"] != null && req["timeout_s"].Type is JTokenType.Integer or JTokenType.Float ? (float)req["timeout_s"] : 30f;
        return seconds + 2f > timeout ? $"this motion takes {seconds:0.#} s; send \"timeout_s\": {Mathf.CeilToInt(seconds + 5f)} or more" : null;
    }

    static Vector3[] Joints(JToken token)
    {
        if (!(token is JArray a) || a.Count != HandJoint.Count * 3) return null;
        var joints = new Vector3[HandJoint.Count];
        for (int i = 0; i < joints.Length; i++) joints[i] = new Vector3((float)a[i * 3], (float)a[i * 3 + 1], (float)a[i * 3 + 2]);
        return joints;
    }

    static Vector3? Vec(JToken token) => token is JArray a && a.Count == 3 ? new Vector3((float)a[0], (float)a[1], (float)a[2]) : (Vector3?)null;

    static JArray Arr(Vector3 v) => new JArray(Math.Round(v.x, 3), Math.Round(v.y, 3), Math.Round(v.z, 3));

    // A number (fixed) or [low, high], clamped to [min, max].
    static Vector2 Range(JToken token, float low, float high, float min, float max)
    {
        if (token is JArray a && a.Count == 2) { low = (float)a[0]; high = (float)a[1]; }
        else if (token != null && token.Type is JTokenType.Integer or JTokenType.Float) low = high = (float)token;
        low = Mathf.Clamp(low, min, max);
        return new Vector2(low, Mathf.Clamp(Mathf.Max(low, high), min, max));
    }

    static float Pick(System.Random rng, Vector2 range) => Mathf.Lerp(range.x, range.y, (float)rng.NextDouble());

    static void Count(JObject row, string key) => row[key] = ((int?)row[key] ?? 0) + 1;
}
#endif
