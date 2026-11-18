using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Every gesture Albie knows: the built-ins below, then what the user taught him, saved in
/// <c>persistentDataPath/gestures.json</c> with any rebinding of the built-ins. All thresholds are starting values, made
/// to be tuned on a headset.
/// </summary>
public sealed class GestureLibrary
{
    public const string PalmUp = "palm_up", FlickIn = "point_flick_in", FlickOut = "point_flick_out";
    // One window (the one you look at) and every window. New ids in version 2: the old fist_close / fist_open bindings
    // (Albie off my hand, the display) don't carry over onto them.
    public const string FistClose = "close_one", FistOpen = "open_one", FistCloseAll = "close_all", FistOpenAll = "open_all";
    const int Version = 2;

    public readonly List<GestureDef> All = new List<GestureDef>();
    /// <summary>A gesture was added, removed or rebound.</summary>
    public event Action Changed;

    readonly string path;
    HandSplit split = HandSplit.LeftAll;

    /// <summary>The Hands setting: which hand opens and closes every window. Saved with the gestures.</summary>
    public HandSplit Split
    {
        get => split;
        set
        {
            if (split == value) return;
            split = value;
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>The Hands setting in a few words, for the display and for Albie.</summary>
    public static string Describe(HandSplit split) => split switch
    {
        HandSplit.LeftAll => "Left hand: all windows",
        HandSplit.RightAll => "Right hand: all windows",
        _ => "Either hand: one window",
    };

    /// <param name="path">The save file; null keeps everything in memory (tests).</param>
    public GestureLibrary(string path)
    {
        this.path = path;
        AddBuiltins();
        Load();
    }

    public GestureDef Find(string idOrTitle)
    {
        if (string.IsNullOrWhiteSpace(idOrTitle)) return null;
        string wanted = Slug(idOrTitle);
        foreach (var g in All)
            if (g.id == wanted || Slug(g.title) == wanted) return g;
        return null;
    }

    public void Add(GestureDef def)
    {
        var old = Find(def.id);
        if (old != null) All.Remove(old);
        All.Add(def);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Forget a taught gesture. Built-ins stay: rebind one to nothing instead.</summary>
    public bool Remove(GestureDef def)
    {
        if (def == null || def.builtin || !All.Remove(def)) return false;
        Save();
        Changed?.Invoke();
        return true;
    }

    public void Bind(GestureDef def, string action, JObject args)
    {
        def.action = action;
        def.args = args;
        Save();
        Changed?.Invoke();
    }

    /// <summary>The gesture this one would be mistaken for, or null.</summary>
    public GestureDef Clash(GestureDef def, float[] center)
    {
        foreach (var other in All)
        {
            if (other.id == def.id) continue;
            if (!other.start.Contains(center) && !def.start.Contains(Center(other.start))) continue;
            if (def.IsPlainPose && other.IsPlainPose) return other;
            if (def.motion != null && other.motion != null && Vector3.Dot(def.motion.direction, other.motion.direction) > 0.7f) return other;
        }
        return null;
    }

    static float[] Center(GesturePose pose)
    {
        var c = new float[HandFeatures.Count];
        for (int i = 0; i < c.Length; i++)
            c[i] = pose.used[i] ? (pose.min[i] + pose.max[i]) * 0.5f : float.NaN;
        return c;
    }

    public static string Slug(string name)
    {
        var chars = new List<char>();
        foreach (char c in (name ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) chars.Add(c);
            else if (chars.Count > 0 && chars[chars.Count - 1] != '_') chars.Add('_');
        }
        return new string(chars.ToArray()).Trim('_');
    }

    /// <summary>What the hand is doing right now, in a word, for the display. Never allocates.</summary>
    public static string PoseName(float[] values)
    {
        if (PinchPose.Contains(values)) return "Pinch";
        if (PointPose.Contains(values)) return "Point";
        if (FistPose.Contains(values)) return "Fist";
        if (PalmUpPose.Contains(values)) return "Palm up";
        if (OpenPose.Contains(values)) return "Open";
        return "Relaxed";
    }

    // ---------------------------------------------------------------- built-ins

    static readonly GesturePose PointPose = new GesturePose()
        .Range(HandFeature.IndexCurl, 0f, 0.38f).Range(HandFeature.IndexFlex, 0f, 0.5f)
        .Range(HandFeature.MiddleCurl, 0.5f, 1f).Range(HandFeature.RingCurl, 0.5f, 1f).Range(HandFeature.PinkyCurl, 0.45f, 1f)
        .Range(HandFeature.Pinch, 0.35f, 9f);

    static readonly GesturePose FistPose = new GesturePose()
        .Range(HandFeature.IndexCurl, 0.6f, 1f).Range(HandFeature.MiddleCurl, 0.6f, 1f).Range(HandFeature.RingCurl, 0.6f, 1f).Range(HandFeature.PinkyCurl, 0.6f, 1f)
        .Range(HandFeature.IndexFlex, 0.35f, 1f).Range(HandFeature.MiddleFlex, 0.35f, 1f).Range(HandFeature.RingFlex, 0.35f, 1f).Range(HandFeature.PinkyFlex, 0.35f, 1f);

    // The outer fingers stay more bent even on a hand held open: the SDK's own open hand has a pinky curl of 0.34.
    static readonly GesturePose OpenPose = new GesturePose()
        .Range(HandFeature.IndexCurl, 0f, 0.3f).Range(HandFeature.MiddleCurl, 0f, 0.33f).Range(HandFeature.RingCurl, 0f, 0.38f).Range(HandFeature.PinkyCurl, 0f, 0.45f)
        .Range(HandFeature.IndexFlex, 0f, 0.4f).Range(HandFeature.MiddleFlex, 0f, 0.4f).Range(HandFeature.RingFlex, 0f, 0.4f).Range(HandFeature.PinkyFlex, 0f, 0.4f);

    // Where a close starts: a hand held open, not one at rest or pointing. Pointing (the other three fingers curled) is
    // well outside it even widened, so curling the finger after a flick can't read as closing the hand. Looser than
    // OpenPose on the outer fingers, which stay bent on a real open hand.
    static readonly GesturePose NotClosedPose = new GesturePose()
        .Range(HandFeature.IndexCurl, 0f, 0.35f).Range(HandFeature.MiddleCurl, 0f, 0.35f).Range(HandFeature.RingCurl, 0f, 0.4f).Range(HandFeature.PinkyCurl, 0f, 0.48f);

    static readonly GesturePose PalmUpPose = new GesturePose()
        .Range(HandFeature.IndexCurl, 0f, 0.42f).Range(HandFeature.MiddleCurl, 0f, 0.42f).Range(HandFeature.RingCurl, 0f, 0.46f).Range(HandFeature.PinkyCurl, 0f, 0.52f)
        .Range(HandFeature.IndexFlex, 0f, 0.55f).Range(HandFeature.MiddleFlex, 0f, 0.55f).Range(HandFeature.RingFlex, 0f, 0.55f).Range(HandFeature.PinkyFlex, 0f, 0.55f)
        .Range(HandFeature.PalmUp, 0.6f, 1f).Range(HandFeature.InView, 0.5f, 1f);

    static readonly GesturePose PinchPose = new GesturePose().Range(HandFeature.Pinch, 0f, 0.22f);

    /// <summary>The window a hand is held out to aim at.</summary>
    public static bool IsAiming(float[] values) => PointPose.Contains(values, 0.25f);

    /// <summary>Still a hand Albie can sit in. Wider than the pose that calls him.</summary>
    public static bool CanHoldPerch(float[] values) => values[(int)HandFeature.PalmUp] > 0.15f;

    void AddBuiltins()
    {
        All.Add(new GestureDef
        {
            id = FlickIn, title = "Bring window", how = "Point at a window, flick toward you", builtin = true, role = GestureRole.OneWindow,
            start = PointPose, hold = 0.1f, window = 0.35f, action = GestureActions.WindowSummon,
            motion = new GestureMotion { direction = new Vector3(0f, 0.25f, -1f).normalized, minSpeed = 0.8f, minDistance = 0.05f, purity = 0.45f, fingertip = true },
        });
        All.Add(new GestureDef
        {
            id = FlickOut, title = "Send window away", how = "Point at a window, flick away from you", builtin = true, role = GestureRole.OneWindow,
            start = PointPose, hold = 0.1f, window = 0.35f, action = GestureActions.WindowMinimize,
            motion = new GestureMotion { direction = Vector3.forward, minSpeed = 1f, minDistance = 0.06f, purity = 0.6f, fingertip = true },
        });
        All.Add(new GestureDef
        {
            id = PalmUp, title = "Call Albie", how = "Hold your hand out flat, palm up", builtin = true,
            // A palm is offered at elbow height, lower than the other gestures count from.
            start = PalmUpPose, hold = 0.6f, action = GestureActions.AlbiePerch, lowHand = true,
        });
        // Open and close: one hand for every window, the other for the window you're looking at (the Hands setting).
        // Closing the hand Albie sits in sends him off instead.
        AddOpenClose(FistCloseAll, FistOpenAll, "Hide all windows", "Show all windows", GestureRole.AllWindows, GestureActions.WindowsHide, GestureActions.WindowsShow);
        AddOpenClose(FistClose, FistOpen, "Close window", "Reopen window", GestureRole.OneWindow, GestureActions.WindowClose, GestureActions.WindowReopen);
    }

    void AddOpenClose(string closeId, string openId, string closeTitle, string openTitle, GestureRole role, string closeAction, string openAction)
    {
        All.Add(new GestureDef
        {
            id = closeId, title = closeTitle, how = role == GestureRole.AllWindows ? "Open your hand, then close it" : "Look at a window, open your hand, close it", builtin = true, role = role, quietAfterPoint = true,
            start = NotClosedPose, end = FistPose, hold = 0.1f, window = 0.6f, action = closeAction,
        });
        All.Add(new GestureDef
        {
            id = openId, title = openTitle, how = role == GestureRole.AllWindows ? "Make a fist, then burst it open" : "Make a fist, then burst it open: the last one closed", builtin = true, role = role, quietAfterPoint = true, freshAfterPoint = true,
            start = FistPose, end = OpenPose, hold = 0.15f, window = 0.45f, action = openAction,
        });
    }

    // ---------------------------------------------------------------- the save file

    public JObject ToJson()
    {
        var bindings = new JObject();
        var taught = new JArray();
        foreach (var g in All)
        {
            if (!g.builtin) taught.Add(g.ToJson());
            else
            {
                var binding = new JObject { ["action"] = g.action };
                if (g.args != null) binding["args"] = g.args;
                bindings[g.id] = binding;
            }
        }
        return new JObject { ["version"] = Version, ["hands"] = split.ToString(), ["bindings"] = bindings, ["gestures"] = taught };
    }

    public void LoadJson(JObject json)
    {
        if (Enum.TryParse((string)json["hands"], out HandSplit savedSplit)) split = savedSplit;
        if (json["bindings"] is JObject bindings)
            foreach (var pair in bindings)
            {
                var g = Find(pair.Key);
                if (g == null || !g.builtin || !(pair.Value is JObject b)) continue;
                g.action = (string)b["action"] ?? g.action;
                g.args = b["args"] as JObject;
            }
        if (json["gestures"] is JArray taught)
            foreach (var item in taught)
            {
                var g = item is JObject o ? GestureDef.FromJson(o) : null;
                if (g != null && Find(g.id) == null) All.Add(g);
            }
    }

    void Load()
    {
        if (path == null || !File.Exists(path)) return;
        try
        {
            LoadJson(JObject.Parse(File.ReadAllText(path)));
        }
        catch (Exception e)
        {
            // Keep the bad file for a look, and start from the built-ins.
            Debug.LogWarning($"[Gestures] {path} didn't load ({e.Message}); moved it aside.");
            try { File.Move(path, path + ".bad"); } catch (Exception) { }
        }
    }

    void Save()
    {
        if (path == null) return;
        try
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, ToJson().ToString(Formatting.Indented));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Gestures] couldn't save {path}: {e.Message}");
        }
    }
}
