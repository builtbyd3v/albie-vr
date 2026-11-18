using System;
using System.Collections.Generic;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// What gestures can do. An action is a string: a built-in below, or <c>tool:name</c> for any of Albie's tools with saved
/// args. Tools that can't do harm run straight away; any other shows a confirm card first, and a tool whose whole job is
/// sending (<see cref="NeverBind"/>) can't be bound at all.
/// </summary>
public sealed class GestureActions
{
    public const string None = "none";
    public const string WindowSummon = "window.summon", WindowMinimize = "window.minimize";
    public const string WindowClose = "window.close", WindowReopen = "window.reopen";  // the window you look at; the last one closed
    public const string WindowsHide = "windows.hide", WindowsShow = "windows.show";    // every window
    public const string AlbiePerch = "albie.perch", AlbieRelease = "albie.release", AlbieCome = "albie.come";
    public const string DisplayToggle = "display.toggle";
    public const string ToolPrefix = "tool:";
    const string CardPrefix = "device:gesture:";

    static readonly HashSet<string> Builtins = new HashSet<string> { None, WindowSummon, WindowMinimize, WindowClose, WindowReopen, WindowsHide, WindowsShow, AlbiePerch, AlbieRelease, AlbieCome, DisplayToggle };

    // Reading, showing and timers: nothing here loses work or reaches anyone.
    static readonly HashSet<string> RunsDirectly = new HashSet<string>
    {
        "ui_open_view", "timer_start", "timer_status", "notes_list", "notes_view",
        "crew_status", "cad_view", "cad_list_parts", "cad_list_versions", "cad_get_params", "code_open_file", "code_list_files", "code_get_errors",
        "messages_open", "messages_list_channels", "messages_read_channel", "ml_open", "ml_status", "nn_open", "nn_close", "nn_status",
    };

    // Confirming a send is the user's own step: a gesture mustn't be able to do it.
    static readonly HashSet<string> NeverBind = new HashSet<string> { "messages_confirm" };

    /// <summary>
    /// For things the close gesture should reach that are not windows (the open artifact: its model, its course marker, its
    /// panels). Given the eyes' ray and the window they are on (or null), the owner closes its own thing if that is what is
    /// looked at and returns what to say at the hand; null when it isn't, and the window minimises as usual. Set by the
    /// artifact host (<c>ArtifactDismiss</c>).
    /// </summary>
    public static Func<Ray, ForgePanel, string> CloseLookedAtHook;

    /// <summary>
    /// The reopen gesture asks this first: the owner brings back what the close gesture (or a throw) closed last and returns
    /// what to say, or null when it has nothing to bring back (then the last minimised window comes back).
    /// </summary>
    public static Func<string> ReopenHook;

    readonly GestureEngine engine;
    readonly GesturePerch perch;
    GesturePanel display;
    Action pendingConfirm;

    public GestureActions(GestureEngine engine)
    {
        this.engine = engine;
        perch = new GesturePerch(engine);
        AlbieCards.Register(CardPrefix, OnCard);
    }

    /// <summary>Albie is on (or on his way to) the user's hand.</summary>
    public bool AlbieOnHand => perch.Active;

    public void Tick(Transform head) => perch.Tick(head);

    /// <summary>Do what the gesture is bound to. Returns what to show at the hand, or null for the gesture's own title.</summary>
    public string Run(GestureDef def, GestureHand hand)
    {
        string action = def.action ?? None;
        if (action.StartsWith(ToolPrefix, StringComparison.Ordinal)) return RunTool(action.Substring(ToolPrefix.Length), def);
        Transform head = PetUser.Head;
        // Closing the hand Albie sits in sends him off, whatever else it does.
        if ((def.id == GestureLibrary.FistClose || def.id == GestureLibrary.FistCloseAll) && perch.On(hand)) return perch.Release();
        switch (action)
        {
            case WindowSummon:
                return GestureWindow.Of(hand.Target)?.ToggleNear(engine, head);
            case WindowMinimize:
                return GestureWindow.Of(hand.Target)?.Send(engine, head);
            case WindowClose:
            {
                ForgePanel looked = GestureEngine.LookedAt(head);
                // An artifact looked at (its model, course marker or panel) closes as a whole; a window minimises into its dot.
                string closed = CloseLookedAtHook != null && head != null ? CloseLookedAtHook(new Ray(head.position, head.forward), looked) : null;
                if (closed != null) return closed;
                return looked != null ? GestureWindow.Of(looked).Close(engine, head) : "Look at a window";
            }
            case WindowReopen:
            {
                string back = ReopenHook != null ? ReopenHook() : null;
                return back ?? GestureWindow.ReopenLast(engine);
            }
            case WindowsHide:
                return WindowStash.HideAll(engine);
            case WindowsShow:
                return WindowStash.ShowAll(engine);
            case AlbiePerch:
                return perch.Call(hand);
            case AlbieRelease:
                return perch.Release();
            case AlbieCome:
                return perch.Come();
            case DisplayToggle:
                ShowDisplay(display == null || !display.gameObject.activeSelf);
                return null;
            default:
                return null;
        }
    }

    string RunTool(string tool, GestureDef def)
    {
        JObject args = def.args != null ? (JObject)def.args.DeepClone() : new JObject();
        if (RunsDirectly.Contains(tool))
        {
            AlbieTools.CallAsync(tool, args, result => Debug.Log($"[Gestures] {tool}: {result}"));
            return null;
        }
        if (NeverBind.Contains(tool)) return "Can't run that from a gesture";
        Confirm($"Run {tool.Replace('_', ' ')}?", () => AlbieTools.CallAsync(tool, args, result => Debug.Log($"[Gestures] {tool}: {result}")));
        return "Confirm on Albie's card";
    }

    /// <summary>Albie's card with Yes and No; <paramref name="onYes"/> runs if Yes is tapped.</summary>
    public void Confirm(string question, Action onYes)
    {
        pendingConfirm = onYes;
        ForgeNet.Inject("pet.say", new JObject
        {
            ["pet_id"] = "bit",
            ["text"] = question,
            ["card"] = new JObject
            {
                ["title"] = question,
                ["actions"] = new JArray(
                    new JObject { ["action_id"] = CardPrefix + "yes", ["label"] = "Yes" },
                    new JObject { ["action_id"] = CardPrefix + "no", ["label"] = "No" }),
            },
        });
    }

    bool OnCard(string actionId)
    {
        Action yes = pendingConfirm;
        pendingConfirm = null;
        if (actionId == CardPrefix + "yes") yes?.Invoke();
        return true;
    }

    public void ShowDisplay(bool show)
    {
        if (display == null)
        {
            if (!show) return;
            display = GesturePanel.Create(engine);
            if (display == null) return;
        }
        display.Show(show);
    }

    /// <summary>Why this action can't be bound, or null when it can.</summary>
    public static string CheckBinding(string action)
    {
        if (string.IsNullOrEmpty(action) || Builtins.Contains(action)) return null;
        if (!action.StartsWith(ToolPrefix, StringComparison.Ordinal))
            return $"I don't know the action '{action}'.";
        string tool = action.Substring(ToolPrefix.Length);
        if (NeverBind.Contains(tool)) return "A gesture can't confirm a send; that stays a deliberate tap.";
        return AlbieTools.Has(tool) ? null : $"I don't have a tool called '{tool}'.";
    }

    /// <summary>The action in a few words, for the display and for Albie to read out.</summary>
    public static string Describe(GestureDef def)
    {
        string action = def.action ?? None;
        if (action.StartsWith(ToolPrefix, StringComparison.Ordinal))
        {
            foreach (var preset in Presets)
                if (preset.action == action && JToken.DeepEquals(preset.Args(), def.args)) return preset.label;
            return action.Substring(ToolPrefix.Length).Replace('_', ' ');
        }
        foreach (var preset in Presets)
            if (preset.action == action) return preset.label;
        return action;
    }

    /// <summary>What the display's pill steps through, so a gesture can be bound without talking.</summary>
    public static readonly Preset[] Presets =
    {
        new Preset("Nothing", None),
        new Preset("Bring window", WindowSummon),
        new Preset("Minimize window", WindowMinimize),
        new Preset("Close looked-at window", WindowClose),
        new Preset("Reopen last window", WindowReopen),
        new Preset("Hide all windows", WindowsHide),
        new Preset("Show all windows", WindowsShow),
        new Preset("Albie to my hand", AlbiePerch),
        new Preset("Albie off my hand", AlbieRelease),
        new Preset("Albie, come here", AlbieCome),
        new Preset("Gesture display", DisplayToggle),
        new Preset("Open CAD", ToolPrefix + "ui_open_view", "{\"view\":\"cad\"}"),
        new Preset("Open code", ToolPrefix + "ui_open_view", "{\"view\":\"code\"}"),
        new Preset("Open ML", ToolPrefix + "ui_open_view", "{\"view\":\"ml\"}"),
        new Preset("Open messages", ToolPrefix + "ui_open_view", "{\"view\":\"messages\"}"),
        new Preset("Close views", ToolPrefix + "ui_open_view", "{\"view\":\"none\"}"),
        new Preset("25 minute timer", ToolPrefix + "timer_start", "{\"minutes\":25}"),
    };

    public sealed class Preset
    {
        public readonly string label, action;
        readonly string args;

        public Preset(string label, string action, string args = null)
        {
            this.label = label;
            this.action = action;
            this.args = args;
        }

        public JObject Args() => args != null ? JObject.Parse(args) : null;
    }
}
