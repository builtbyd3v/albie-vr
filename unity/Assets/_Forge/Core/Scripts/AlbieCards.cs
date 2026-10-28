using System;
using System.Collections.Generic;

/// <summary>
/// Hub off: taps on Albie's pet.say card buttons (pet.card_action) handled on the device. A module registers the
/// action_id prefix of the cards it shows (e.g. "nn:" for the Neural Net Lab's Apply card); GatorFixCard asks here
/// before sending the tap to the hub. Hub live: the hub handles every tap, as before.
/// </summary>
public static class AlbieCards
{
    static readonly Dictionary<string, Func<string, bool>> handlers = new Dictionary<string, Func<string, bool>>();

    public static void Register(string prefix, Func<string, bool> handler) => handlers[prefix] = handler;

    /// <summary>True when a device handler took the tap.</summary>
    public static bool Tap(string actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return false;
        foreach (var pair in handlers)
            if (actionId.StartsWith(pair.Key, StringComparison.Ordinal)) return pair.Value(actionId);
        return false;
    }
}
