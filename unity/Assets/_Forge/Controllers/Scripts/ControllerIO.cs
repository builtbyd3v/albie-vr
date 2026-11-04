using System;

namespace Forge.Controllers
{
    /// <summary>
    /// Sensor and actuator buffers for one Thing (C-19, docs/CONTROLLER_CONTRACT.md section 1). The Thing runtime owns
    /// it: it writes <see cref="Sensors"/> before each tick and reads <see cref="Actuators"/> after. Names are fixed per
    /// Thing version, as <c>part_id.channel</c> (contract section 2). All values are normalised: actuators in [-1, 1].
    /// </summary>
    public sealed class ControllerIO
    {
        public readonly string[] SensorNames;
        public readonly float[] Sensors;
        public readonly string[] ActuatorNames;
        public readonly float[] Actuators;
        /// <summary>Indexes of the drive actuators (<c>*.throttle</c>) that the speed governor limits.</summary>
        public readonly int[] ThrottleActuators;

        public ControllerIO(string[] sensorNames, string[] actuatorNames)
        {
            SensorNames = sensorNames ?? throw new ArgumentNullException(nameof(sensorNames));
            ActuatorNames = actuatorNames ?? throw new ArgumentNullException(nameof(actuatorNames));
            Sensors = new float[sensorNames.Length];
            Actuators = new float[actuatorNames.Length];
            int throttles = 0;
            foreach (var name in actuatorNames) if (name.EndsWith(".throttle", StringComparison.Ordinal)) throttles++;
            ThrottleActuators = new int[throttles];
            for (int i = 0, t = 0; i < actuatorNames.Length; i++)
                if (actuatorNames[i].EndsWith(".throttle", StringComparison.Ordinal)) ThrottleActuators[t++] = i;
        }

        /// <summary>-1 if this Thing has no such sensor. Call in Bind, not every tick.</summary>
        public int SensorIndex(string name) => Array.IndexOf(SensorNames, name);

        /// <summary>-1 if this Thing has no such actuator. Call in Bind, not every tick.</summary>
        public int ActuatorIndex(string name) => Array.IndexOf(ActuatorNames, name);

        /// <summary>The sensor list for error messages, e.g. "ray_front.d0-d6, speed.forward".</summary>
        public string DescribeSensors() => Describe(SensorNames);

        public string DescribeActuators() => Describe(ActuatorNames);

        // Collapses runs like ray_front.d0 ... ray_front.d6 into "ray_front.d0-d6", so a message stays readable.
        static string Describe(string[] names)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < names.Length; i++)
            {
                int j = i;
                while (j + 1 < names.Length && SameRun(names[i], names[j + 1], j + 1 - i)) j++;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(names[i]);
                if (j > i) sb.Append('-').Append(names[j].Substring(names[j].LastIndexOf('.') + 1));
                i = j;
            }
            return sb.ToString();
        }

        // True when b is a's numbered sibling `step` places later: "ray.d0" then "ray.d1", "ray.d2" ...
        static bool SameRun(string a, string b, int step)
        {
            int cut = a.Length;
            while (cut > 0 && char.IsDigit(a[cut - 1])) cut--;
            if (cut == a.Length || !b.StartsWith(a.Substring(0, cut), StringComparison.Ordinal)) return false;
            return int.TryParse(a.Substring(cut), out int n) && b.Substring(cut) == (n + step).ToString();
        }
    }
}
