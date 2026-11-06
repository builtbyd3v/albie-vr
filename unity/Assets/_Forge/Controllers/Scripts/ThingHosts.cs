using System.Collections.Generic;
using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// The Things running a controller right now, by Thing name (<c>truck</c>), so Albie's tools can reach them. Whoever
    /// runs a Thing registers its <see cref="ControllerHost"/>: today the Controllers sandbox's mock truck, later
    /// Person 2's Thing runtime (C-19 section 1). Main thread only.
    /// </summary>
    public static class ThingHosts
    {
        static readonly Dictionary<string, ControllerHost> hosts = new Dictionary<string, ControllerHost>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => hosts.Clear();

        public static void Register(string thing, ControllerHost host) { if (!string.IsNullOrEmpty(thing) && host != null) hosts[thing] = host; }

        /// <summary>Removes <paramref name="thing"/> if it's still <paramref name="host"/> (a newer one stays).</summary>
        public static void Unregister(string thing, ControllerHost host)
        {
            if (thing != null && hosts.TryGetValue(thing, out var current) && current == host) hosts.Remove(thing);
        }

        public static ControllerHost Find(string thing) => thing != null && hosts.TryGetValue(thing, out var host) ? host : null;

        public static IEnumerable<string> Names => hosts.Keys;
        public static int Count => hosts.Count;
    }
}
