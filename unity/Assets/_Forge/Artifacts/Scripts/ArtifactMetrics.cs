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
            if (slot < 0 || slot >= values.Count) return;
            if (values[slot] == value) return;
            values[slot] = value;
            var list = readouts[slot];
            for (int i = 0; i < list.Count; i++)
            {
                if (double.IsNaN(value)) list[i].text.SetText(ArtifactLook.NoValue);
                else list[i].text.SetText(list[i].format, (float)value);
            }
        }

        public void Set(string name, double value) => Set(Slot(name), value);

        /// <summary>NaN until it has a value.</summary>
        public double Value(string name) => slots.TryGetValue(name, out int slot) ? values[slot] : double.NaN;
        public double Value(int slot) => slot >= 0 && slot < values.Count ? values[slot] : double.NaN;

        /// <summary>Back to "no value yet" (a run being armed). Readouts show "--", never a made-up zero.</summary>
        public void Reset()
        {
            for (int i = 0; i < values.Count; i++)
            {
                values[i] = double.NaN;
                var list = readouts[i];
                for (int j = 0; j < list.Count; j++) list[j].text.SetText(ArtifactLook.NoValue);
            }
        }
    }
}
