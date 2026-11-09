using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// A <c>sim</c> node: runs a solver in real time and moves the scene's nodes to match. Changing a number restarts it, so
    /// what you see always comes from the values on the panel. Changing <c>rate</c> doesn't, since that's only how fast
    /// you watch.
    /// </summary>
    public sealed class ArtifactSim : MonoBehaviour
    {
        const int MaxStepsPerFrame = 256;      // a slow frame runs slow rather than making the next frame slower still
        const float PublishEvery = 0.1f;

        struct Output
        {
            public int metric, value;
        }

        IArtifactSolver solver;
        ArtifactBuild build;
        Transform[] bodies;
        Output[] outputs;
        float rate = 1f, sincePublish;
        double owed;

        public bool Playing { get; private set; } = true;
        public IArtifactSolver Solver => solver;

        public static GameObject Build(JObject n, ArtifactBuild build)
        {
            var go = new GameObject(n.Value<string>("id"));
            var sim = go.AddComponent<ArtifactSim>();
            sim.build = build;
            sim.solver = ArtifactSolvers.Create(n.Value<string>("solver"));
            build.AfterBuild(() => sim.Bind(n));
            return go;
        }

        void Bind(JObject n)
        {
            var list = (JArray)n["bodies"];
            bodies = new Transform[list.Count];
            for (int i = 0; i < bodies.Length; i++) bodies[i] = build.Nodes[list[i].Value<string>("node")].transform;
            outputs = Outputs(n.Value<string>("id"));

            var rateToken = n["rate"];
            build.Apply(() =>
            {
                rate = build.Inputs.Number(rateToken, 1f);
                if (solver.Configure(n, build.Inputs)) Restart();
            }, true);
        }

        Output[] Outputs(string id)
        {
            if (!(build.Host.Rules is JArray rules)) return new Output[0];
            var found = new System.Collections.Generic.List<Output>();
            foreach (var rule in rules)
                if (rule.Value<string>("op") == "sim" && rule.Value<string>("sim") == id)
                    found.Add(new Output { metric = build.Metrics.Slot(rule.Value<string>("name")), value = solver.ValueIndex(rule.Value<string>("value")) });
            return found.ToArray();
        }

        public void Play()
        {
            if (!solver.Finished) Playing = true;
        }

        public void Pause() => Playing = false;

        public void Restart()
        {
            solver.Restart();
            owed = 0.0;
            Playing = true;
            Show();
            build.ClearTrails();
            Publish();
        }

        void Update()
        {
            if (solver == null || bodies == null || !Playing) return;
            owed += Time.deltaTime * rate;
            int steps = (int)(owed / solver.StepSize);
            if (steps > MaxStepsPerFrame)
            {
                steps = MaxStepsPerFrame;
                owed = 0.0;
            }
            else owed -= steps * solver.StepSize;
            for (int i = 0; i < steps && !solver.Finished; i++) solver.Step();
            Show();
            sincePublish += Time.deltaTime;
            if (solver.Finished)
            {
                Playing = false;
                Publish();
            }
            else if (sincePublish >= PublishEvery) Publish();
        }

        void Show()
        {
            for (int i = 0; i < bodies.Length; i++) bodies[i].localPosition = solver.Position(i);
        }

        void Publish()
        {
            sincePublish = 0f;
            for (int i = 0; i < outputs.Length; i++) build.Metrics.Set(outputs[i].metric, solver.Value(outputs[i].value));
        }
    }
}
