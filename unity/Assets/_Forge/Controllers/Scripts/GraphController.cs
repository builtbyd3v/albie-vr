using System;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>
    /// <c>kind: graph</c>: rule and PID nodes run in list order each tick, and a later write to an actuator overrides an
    /// earlier one (contract section 3.3). Node types: const, gain, pid, rule. No expressions and no scripts: the file is
    /// data the agent writes. Bind compiles the nodes into flat arrays, so Tick allocates nothing.
    /// </summary>
    public sealed class GraphController : IThingController
    {
        enum NodeType { Const, Gain, Pid, Rule }
        enum Op { Less, LessOrEqual, Greater, GreaterOrEqual }

        // An operand is a sensor (index >= 0) or a constant.
        struct Operand
        {
            public int sensor;
            public float constant;
            public float Read(float[] sensors) => sensor >= 0 ? sensors[sensor] : constant;
        }

        struct Node
        {
            public NodeType type;
            public int output;              // actuator index (const, gain, pid)
            public Operand a, b;            // gain: k·(a − b) + offset; pid: a is the input
            public float value;             // const
            public float k, offset;
            public float setpoint, kp, ki, kd, iLimit;
            public float integral, lastError;
            public bool hasLastError;
            public Operand ruleInput;       // rule: if ruleInput op threshold
            public Op op;
            public float threshold;
            public int setStart, setCount;  // rule: range in setActuator / setValue
        }

        readonly ControllerFile file;
        Node[] nodes;
        int[] setActuator;
        float[] setValue;

        public GraphController(ControllerFile file) { this.file = file; }

        public string Kind => "graph";
        public string Name => file.Name;

        public string Bind(ControllerIO io)
        {
            if (!(file.Raw["nodes"] is JArray list) || list.Count == 0)
                return "bad_value: nodes: a graph controller needs at least one node";
            var nodes = new Node[list.Count];
            int sets = 0;
            foreach (var n in list) if (n["set"] is JObject s) sets += s.Count;
            var setActuator = new int[sets]; var setValue = new float[sets];
            int used = 0;

            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i];
                string where = $"nodes[{i}]";
                ref var node = ref nodes[i];
                string error;
                switch ((string)n["type"])
                {
                    case "const":
                        node.type = NodeType.Const;
                        if ((error = Output(io, n, where, out node.output)) != null) return error;
                        if (!file.TryNumber(n["value"], where + ".value", out node.value, out error)) return error;
                        break;
                    case "gain":
                        node.type = NodeType.Gain;
                        if ((error = Output(io, n, where, out node.output)) != null) return error;
                        if ((error = Read(io, n["a"], where + ".a", false, out node.a)) != null) return error;
                        if ((error = Read(io, n["b"], where + ".b", true, out node.b)) != null) return error;
                        if (!file.TryNumber(n["k"], where + ".k", out node.k, out error)) return error;
                        if (!Optional(n["offset"], 0f, where + ".offset", out node.offset, out error)) return error;
                        break;
                    case "pid":
                        node.type = NodeType.Pid;
                        if ((error = Output(io, n, where, out node.output)) != null) return error;
                        if ((error = Read(io, n["input"], where + ".input", false, out node.a)) != null) return error;
                        if (node.a.sensor < 0) return $"bad_value: {where}.input must be a sensor channel";
                        if (!file.TryNumber(n["setpoint"], where + ".setpoint", out node.setpoint, out error)) return error;
                        if (!Optional(n["kp"], 0f, where + ".kp", out node.kp, out error)) return error;
                        if (!Optional(n["ki"], 0f, where + ".ki", out node.ki, out error)) return error;
                        if (!Optional(n["kd"], 0f, where + ".kd", out node.kd, out error)) return error;
                        if (!Optional(n["i_limit"], 1f, where + ".i_limit", out node.iLimit, out error)) return error;
                        if (node.iLimit < 0) return $"bad_value: {where}.i_limit must not be negative";
                        break;
                    case "rule":
                        node.type = NodeType.Rule;
                        if (!(n["if"] is JArray cond) || cond.Count != 3)
                            return $"bad_value: {where}.if must be [channel, op, value]";
                        if ((error = Read(io, cond[0], where + ".if[0]", false, out node.ruleInput)) != null) return error;
                        switch ((string)cond[1])
                        {
                            case "<": node.op = Op.Less; break;
                            case "<=": node.op = Op.LessOrEqual; break;
                            case ">": node.op = Op.Greater; break;
                            case ">=": node.op = Op.GreaterOrEqual; break;
                            default: return $"bad_value: {where}.if[1] must be <, <=, > or >=";
                        }
                        if (!file.TryNumber(cond[2], where + ".if[2]", out node.threshold, out error)) return error;
                        if (!(n["set"] is JObject set) || set.Count == 0)
                            return $"bad_value: {where}.set needs at least one actuator";
                        node.setStart = used;
                        foreach (var pair in set)
                        {
                            setActuator[used] = io.ActuatorIndex(pair.Key);
                            if (setActuator[used] < 0)
                                return $"unknown_channel: {where}.set: no actuator \"{pair.Key}\" on this Thing (it has {io.DescribeActuators()})";
                            if (!file.TryNumber(pair.Value, $"{where}.set.{pair.Key}", out setValue[used], out error)) return error;
                            used++;
                        }
                        node.setCount = used - node.setStart;
                        break;
                    default:
                        return $"bad_value: {where}.type \"{(string)n["type"]}\" must be const, gain, pid or rule";
                }
            }
            // A re-bind after a tunable change keeps PID state for nodes that are still the same type at the same place,
            // so the Thing doesn't jerk (contract section 6). ResetState clears it for a real structure change.
            if (this.nodes != null && this.nodes.Length == nodes.Length)
                for (int i = 0; i < nodes.Length; i++)
                    if (this.nodes[i].type == nodes[i].type)
                    {
                        nodes[i].integral = this.nodes[i].integral;
                        nodes[i].lastError = this.nodes[i].lastError;
                        nodes[i].hasLastError = this.nodes[i].hasLastError;
                    }
            this.nodes = nodes; this.setActuator = setActuator; this.setValue = setValue;
            return null;
        }

        // The node's output actuator.
        static string Output(ControllerIO io, JToken n, string where, out int index)
        {
            index = io.ActuatorIndex((string)n["output"]);
            return index >= 0 ? null
                : $"unknown_channel: {where}.output: no actuator \"{(string)n["output"]}\" on this Thing (it has {io.DescribeActuators()})";
        }

        // A sensor channel name, a number or a $tunable. Optional operands default to 0.
        string Read(ControllerIO io, JToken token, string where, bool optional, out Operand operand)
        {
            operand = new Operand { sensor = -1 };
            if (token == null || token.Type == JTokenType.Null)
                return optional ? null : $"bad_value: {where} is missing";
            if (token.Type == JTokenType.String && !((string)token).StartsWith("$", StringComparison.Ordinal))
            {
                operand.sensor = io.SensorIndex((string)token);
                return operand.sensor >= 0 ? null
                    : $"unknown_channel: {where}: no sensor \"{(string)token}\" on this Thing (it has {io.DescribeSensors()})";
            }
            return file.TryNumber(token, where, out operand.constant, out string error) ? null : error;
        }

        bool Optional(JToken token, float fallback, string where, out float value, out string error)
        {
            if (token == null || token.Type == JTokenType.Null) { value = fallback; error = null; return true; }
            return file.TryNumber(token, where, out value, out error);
        }

        public void Tick(ControllerIO io, float dt)
        {
            float[] sensors = io.Sensors, outputs = io.Actuators;
            for (int i = 0; i < nodes.Length; i++)
            {
                ref var n = ref nodes[i];
                switch (n.type)
                {
                    case NodeType.Const:
                        outputs[n.output] = n.value;
                        break;
                    case NodeType.Gain:
                        outputs[n.output] = n.k * (n.a.Read(sensors) - n.b.Read(sensors)) + n.offset;
                        break;
                    case NodeType.Pid:
                    {
                        float error = n.setpoint - sensors[n.a.sensor];
                        n.integral = Math.Max(-n.iLimit, Math.Min(n.iLimit, n.integral + error * dt));
                        float derivative = n.hasLastError && dt > 0 ? (error - n.lastError) / dt : 0f;
                        n.lastError = error; n.hasLastError = true;
                        outputs[n.output] = n.kp * error + n.ki * n.integral + n.kd * derivative;
                        break;
                    }
                    case NodeType.Rule:
                    {
                        float v = n.ruleInput.Read(sensors);
                        bool hit = n.op == Op.Less ? v < n.threshold : n.op == Op.LessOrEqual ? v <= n.threshold
                                 : n.op == Op.Greater ? v > n.threshold : v >= n.threshold;
                        if (hit) for (int s = n.setStart; s < n.setStart + n.setCount; s++) outputs[setActuator[s]] = setValue[s];
                        break;
                    }
                }
            }
        }

        public void ResetState()
        {
            if (nodes == null) return;
            for (int i = 0; i < nodes.Length; i++) { nodes[i].integral = 0; nodes[i].lastError = 0; nodes[i].hasLastError = false; }
        }
    }
}
