using System.Collections.Generic;
using TMPro;

namespace Forge.Artifacts
{
    /// <summary>
    /// Live numbers and the readouts showing them. A readout stays on "--" until its metric has a value, never a made-up
    /// zero. Slots are looked up once, and <see cref="Set"/> doesn't allocate: TMP formats the number itself.
    /// </summary>
    public sealed class ArtifactMetrics
    {
        sealed class Readout
        {
            public TMP_Text text;
            public string format;
        }

        readonly Dictionary<string, int> slots = new Dictionary<string, int>();
        readonly List<List<Readout>> readouts = new List<List<Readout>>();
        readonly List<double> values = new List<double>();

        public int Slot(string name)
        {
            if (slots.TryGetValue(name, out int slot)) return slot;
            slot = readouts.Count;
            slots[name] = slot;
            readouts.Add(new List<Readout>());
            values.Add(double.NaN);
            return slot;
        }

        public void Show(string name, TMP_Text text, int decimals, string unit)
        {
            string format = "{0:" + decimals + "}" + (string.IsNullOrEmpty(unit) ? "" : " " + unit);
            readouts[Slot(name)].Add(new Readout { text = text, format = format });
        }

        public void Set(int slot, double value)
        {
            values[slot] = value;
            var list = readouts[slot];
            for (int i = 0; i < list.Count; i++) list[i].text.SetText(list[i].format, (float)value);
        }

        /// <summary>NaN until it has a value.</summary>
        public double Value(string name) => slots.TryGetValue(name, out int slot) ? values[slot] : double.NaN;
    }
}
