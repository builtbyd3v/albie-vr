using System;
using System.Diagnostics;
using System.Text;
using Forge.ML;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Checks <see cref="NNNetwork"/>'s multi-output path, which learned controllers use (C-19, docs/CONTROLLER_CONTRACT.md):
/// gradients match finite differences, the training hot path allocates nothing, a steer + throttle net learns the mock
/// laps, and the mock autopilot's weights load in the [layer][to][from] layout. The one-output Lab path is unchanged.
/// Forge > ML > Check NN Network logs the report; <see cref="Run"/> returns it (for `unity command eval`).
/// Needs the mocks from `python -m ml.make_controller_mocks` (hub/).
/// </summary>
public static class NNNetworkCheck
{
    const string LapsPath = "Mocks/controllers/laps/truck_v2";
    const string AutopilotPath = "Mocks/controllers/truck_autopilot";

    [MenuItem("Forge/ML/Check NN Network")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        bool ok = GradientsMatchFiniteDifferences(sb);
        ok &= TrainingAllocatesNothing(sb);
        ok &= LearnsMockLaps(sb);
        ok &= LoadsMockAutopilot(sb);
        return (ok ? "PASS" : "FAIL") + " NNNetwork multi-output check\n" + sb;
    }

    // Backprop through two outputs, checked weight by weight against (L(w+e) - L(w-e)) / 2e.
    static bool GradientsMatchFiniteDifferences(StringBuilder sb)
    {
        var net = new NNNetwork(3, new[] { 4, 3 }, 2, NNActivation.Tanh, 3);
        float[] x = { 0.3f, -0.5f, 0.8f }, y = { 0.4f, -0.2f };
        int layers = net.Layers;

        // Analytic: one Accumulate + Apply(1) moves each weight by exactly -gradient.
        var before = Snapshot(net);
        net.Accumulate(x, y);
        net.Apply(1f);
        var after = Snapshot(net);
        Restore(net, before);

        float worst = 0; int checkedCount = 0;
        for (int l = 1; l < layers; l++)
            for (int j = 0; j < net.sizes[l]; j++)
                for (int k = 0; k <= net.sizes[l - 1]; k++)   // k == sizes[l-1] is the bias
                {
                    float analytic = before[l][j][k] - after[l][j][k];
                    float numeric = NumericGradient(net, l, j, k, x, y);
                    float err = Mathf.Abs(analytic - numeric) / (1e-3f + Mathf.Abs(numeric));
                    worst = Mathf.Max(worst, err);
                    checkedCount++;
                }
        bool ok = worst < 0.02f;
        sb.Append(ok ? "  ok   " : "  FAIL ").Append("gradients: ").Append(checkedCount)
          .Append(" weights and biases, worst relative error ").Append(worst.ToString("0.0000")).Append('\n');
        return ok;
    }

    static float NumericGradient(NNNetwork net, int l, int j, int k, float[] x, float[] y)
    {
        const float eps = 1e-3f;
        bool bias = k == net.sizes[l - 1];
        float w = bias ? net.biases[l][j] : net.weights[l][j][k];
        Set(net, l, j, k, bias, w + eps); net.Forward(x); float plus = net.Loss(y);
        Set(net, l, j, k, bias, w - eps); net.Forward(x); float minus = net.Loss(y);
        Set(net, l, j, k, bias, w);
        return (plus - minus) / (2 * eps);
    }

    static void Set(NNNetwork net, int l, int j, int k, bool bias, float v)
    {
        if (bias) net.biases[l][j] = v; else net.weights[l][j][k] = v;
    }

    // [layer][neuron][from..., bias]
    static float[][][] Snapshot(NNNetwork net)
    {
        var s = new float[net.Layers][][];
        for (int l = 1; l < net.Layers; l++)
        {
            s[l] = new float[net.sizes[l]][];
            for (int j = 0; j < net.sizes[l]; j++)
            {
                s[l][j] = new float[net.sizes[l - 1] + 1];
                Array.Copy(net.weights[l][j], s[l][j], net.sizes[l - 1]);
                s[l][j][net.sizes[l - 1]] = net.biases[l][j];
            }
        }
        return s;
    }

    static void Restore(NNNetwork net, float[][][] s)
    {
        for (int l = 1; l < net.Layers; l++)
            for (int j = 0; j < net.sizes[l]; j++)
            {
                Array.Copy(s[l][j], net.weights[l][j], net.sizes[l - 1]);
                net.biases[l][j] = s[l][j][net.sizes[l - 1]];
            }
    }

    // The Quest rule: no allocations in the per-frame path (Forward, Accumulate, Apply).
    static bool TrainingAllocatesNothing(StringBuilder sb)
    {
        var net = new NNNetwork(8, new[] { 8, 8 }, 2, NNActivation.Tanh, 1);
        float[] x = new float[8], y = { 0.2f, 0.5f }, result = new float[2];
        net.Forward(x, result); net.Accumulate(x, y); net.Apply(0.01f);   // warm up
        // AllocationCounter (Controllers/Editor): GC.GetAllocatedBytesForCurrentThread reads 0 under Unity's GC.
        long bytes = AllocationCounter.BytesDuring(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                x[i % 8] = (i % 13) / 13f;
                net.Forward(x, result);
                net.Accumulate(x, y);
                if (i % 10 == 9) net.Apply(0.01f);
            }
        });
        bool ok = bytes == 0;
        sb.Append(ok ? "  ok   " : "  FAIL ").Append("allocations: ").Append(bytes)
          .Append(" bytes over 1000 forward + accumulate steps\n");
        return ok;
    }

    // Steer + throttle from 7 rays and speed, trained on the mock laps with plain mini-batch SGD.
    static bool LearnsMockLaps(StringBuilder sb)
    {
        if (!LoadLaps(out float[][] xs, out float[][] ys, out string why))
        {
            sb.Append("  FAIL laps: ").Append(why).Append('\n');
            return false;
        }
        Standardise(xs);
        var net = new NNNetwork(xs[0].Length, new[] { 8, 8 }, ys[0].Length, NNActivation.Tanh, 1);
        float first = MeanLoss(net, xs, ys);
        var order = new int[xs.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        var rng = new System.Random(1);
        const int epochs = 60, batch = 16;
        var clock = Stopwatch.StartNew();
        for (int e = 0; e < epochs; e++)
        {
            for (int i = order.Length - 1; i > 0; i--) { int r = rng.Next(i + 1); (order[i], order[r]) = (order[r], order[i]); }
            for (int i = 0; i < order.Length; i++)
            {
                net.Accumulate(xs[order[i]], ys[order[i]]);
                if ((i + 1) % batch == 0 || i == order.Length - 1) net.Apply(0.3f);
            }
        }
        clock.Stop();
        float last = MeanLoss(net, xs, ys);
        bool ok = last < 0.5f * first;
        sb.Append(ok ? "  ok   " : "  FAIL ").Append("learns mock laps: ").Append(xs.Length).Append(" samples, loss ")
          .Append(first.ToString("0.0000")).Append(" -> ").Append(last.ToString("0.0000")).Append(" after ").Append(epochs)
          .Append(" epochs in ").Append(clock.ElapsedMilliseconds).Append(" ms (Editor, not Quest)\n");
        return ok;
    }

    // The generator's net, read in the C-19 layout, must reproduce the laps it was trained on.
    static bool LoadsMockAutopilot(StringBuilder sb)
    {
        var asset = Resources.Load<TextAsset>(AutopilotPath);
        if (asset == null || !LoadLaps(out float[][] xs, out float[][] ys, out _))
        {
            sb.Append("  FAIL autopilot: missing Resources/").Append(AutopilotPath).Append(" or the laps\n");
            return false;
        }
        var file = JObject.Parse(asset.text);
        var sizes = file["net"]["sizes"].ToObject<int[]>();
        var hidden = new int[sizes.Length - 2];
        Array.Copy(sizes, 1, hidden, 0, hidden.Length);
        var net = new NNNetwork(sizes[0], hidden, sizes[sizes.Length - 1], NNActivation.Tanh, 0);
        var weights = (JArray)file["net"]["weights"];
        var biases = (JArray)file["net"]["biases"];
        for (int l = 1; l < sizes.Length; l++)
            for (int j = 0; j < sizes[l]; j++)
            {
                for (int k = 0; k < sizes[l - 1]; k++) net.weights[l][j][k] = (float)weights[l - 1][j][k];
                net.biases[l][j] = (float)biases[l - 1][j];
            }
        var inputs = (JArray)file["inputs"];
        foreach (var row in xs)
            for (int i = 0; i < row.Length; i++)
            {
                float std = (float)inputs[i]["std"];
                row[i] = (row[i] - (float)inputs[i]["mean"]) / (std == 0 ? 1 : std);
            }
        float loss = MeanLoss(net, xs, ys);
        net.Forward(xs[0]);
        bool ok = loss < 0.02f;
        sb.Append(ok ? "  ok   " : "  FAIL ").Append("mock autopilot: sizes [").Append(string.Join(", ", sizes))
          .Append("], mean loss on its laps ").Append(loss.ToString("0.0000")).Append(", first row steer ")
          .Append(net.OutputAt(0).ToString("0.0000")).Append(" throttle ").Append(net.OutputAt(1).ToString("0.0000"))
          .Append('\n');
        return ok;
    }

    static bool LoadLaps(out float[][] xs, out float[][] ys, out string why)
    {
        xs = ys = null;
        var asset = Resources.Load<TextAsset>(LapsPath);
        if (asset == null) { why = "missing Resources/" + LapsPath + ".json"; return false; }
        var file = JObject.Parse(asset.text);
        int count = 0;
        foreach (var lap in file["laps"]) if ((string)lap["outcome"] == "completed") count += ((JArray)lap["x"]).Count;
        xs = new float[count][]; ys = new float[count][];
        int n = 0;
        foreach (var lap in file["laps"])
        {
            if ((string)lap["outcome"] != "completed") continue;
            var lx = (JArray)lap["x"]; var ly = (JArray)lap["y"];
            for (int i = 0; i < lx.Count; i++, n++) { xs[n] = lx[i].ToObject<float[]>(); ys[n] = ly[i].ToObject<float[]>(); }
        }
        why = null;
        return count > 0;
    }

    static void Standardise(float[][] xs)
    {
        int width = xs[0].Length;
        for (int f = 0; f < width; f++)
        {
            double sum = 0, sq = 0;
            foreach (var row in xs) { sum += row[f]; sq += row[f] * row[f]; }
            double mean = sum / xs.Length, std = Math.Sqrt(Math.Max(0, sq / xs.Length - mean * mean));
            if (std < 1e-6) std = 1;
            foreach (var row in xs) row[f] = (float)((row[f] - mean) / std);
        }
    }

    static float MeanLoss(NNNetwork net, float[][] xs, float[][] ys)
    {
        double total = 0;
        for (int i = 0; i < xs.Length; i++) { net.Forward(xs[i]); total += net.Loss(ys[i]); }
        return (float)(total / xs.Length);
    }
}
