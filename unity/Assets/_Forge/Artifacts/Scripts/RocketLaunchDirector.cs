using System;
using System.Globalization;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// What the <c>rocket_launch</c> template needs beyond its scene JSON, kept out of the host: one object for the app's
    /// life that watches <see cref="ArtifactHost.Current"/> and, while a scene with a <c>rocket</c> sim is open:
    /// <list type="bullet">
    /// <item>Launch: the panel's <c>launch</c> button starts the countdown (<see cref="RocketLaunchSolver.Launch"/>).</item>
    /// <item>CAD link: when the CAD session has the rocket open, its parameters become the sliders' values (once per CAD
    /// version, so a what-if on the sliders stays until the design changes) and its RocketPy flight from
    /// <c>GET /cad/flight</c> (the hub, the cloud, or the mock's flight.json) is what flies while the sliders are that
    /// design and there's no wind. Anything else flies the simplified model, and the label says which.</item>
    /// <item>The look: the rocket drawn from the same parameters as primitives (body, nose, fins) at 1:4, turned along
    /// its path; the parachute over it on the way down; the apogee marker at the flight's top; the pad scene at true
    /// scale on the desk, and the scale written beside it, measured from the scene's real size.</item>
    /// <item>Result saved: each landing writes an <c>albie.run/1</c> record (apogee, stability, flight time, drift, the CAD
    /// version) to the device library and attaches it to the open notebook project, as course runs are.</item>
    /// </list>
    /// No allocations per frame: text changes only when the phase or the countdown second does.
    /// </summary>
    public sealed class RocketLaunchDirector : MonoBehaviour
    {
        public const string TemplateId = "rocket_launch";
        public const string LaunchButton = "launch";
        /// <summary>Static margin (calibers) a launch needs to pass: the planner's rocket tests use the same line.</summary>
        public const float StableMargin = 1.0f;
        const string SimId = "flight", RocketId = "rocket", ChuteId = "chute", StatusId = "status_label", ModelId = "model_label", ScaleId = "scale_label";

        static RocketLaunchDirector instance;

        ArtifactHost host;
        ArtifactBuild bound;
        ArtifactSim sim;
        RocketLaunchSolver solver;
        Transform flightNode, rocket, chute, look;
        TMP_Text status, model, scaleText;
        float rocketHeight;
        bool rescaled, chooseLater;
        RocketLaunchSolver.Stage lastStage = (RocketLaunchSolver.Stage)(-1);
        int lastCountdown = -1;
        string startedAt;

        // The CAD design and its RocketPy flight.
        readonly RocketTrajectory cadFlight = new RocketTrajectory();
        RocketDesign cadDesign;
        bool haveCadDesign, haveCadFlight;
        int cadVersion = -1, fetching = -1, pushedVersion = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (instance != null) return;
            var go = new GameObject("RocketLaunchDirector");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<RocketLaunchDirector>();
        }

        /// <summary>The CAD panel's "Launch in room": opens the launch template (or says why not). Null when it opened.</summary>
        public static string Open()
        {
            try
            {
                var result = ArtifactTools.Create(new JObject { ["template"] = TemplateId });
                return result["error"] != null ? (string)result["error"] : null;
            }
            catch (ArgumentException e)
            {
                Debug.LogWarning("[RocketLaunch] " + e.Message);
                return e.Message;
            }
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
            Rehost(null);
        }

        void Update()
        {
            if (ArtifactHost.Current != host) Rehost(ArtifactHost.Current);
            if (host != null && host.Build != bound)
            {
                if (host.Build != null && IsRocketLaunch(host)) Bind(host.Build);
                else Unbind();
            }
            WatchCad();
            if (solver == null || rocket == null) return;
            if (!rescaled && !host.Revealing) Rescale();
            Animate();
        }

        void Rehost(ArtifactHost next)
        {
            if (host != null) host.Fired -= OnFired;
            Unbind();
            host = next;
            if (host != null) host.Fired += OnFired;
        }

        static bool IsRocketLaunch(ArtifactHost h)
        {
            if (h.OpenId == TemplateId) return true;
            var simGo = h.OpenManifest != null && h.Build != null && h.Build.Nodes.TryGetValue(SimId, out var go) ? go : null;
            return simGo != null && simGo.TryGetComponent<ArtifactSim>(out var s) && s.Solver is RocketLaunchSolver;
        }

        // ------------------------------------------------------------------ binding

        void Bind(ArtifactBuild build)
        {
            Unbind();
            bound = build;
            if (!build.Nodes.TryGetValue(SimId, out var simGo) || !simGo.TryGetComponent(out sim) || !(sim.Solver is RocketLaunchSolver s))
            {
                sim = null;
                return;
            }
            solver = s;
            flightNode = simGo.transform;
            rocket = Node(RocketId);
            chute = Node(ChuteId);
            status = Node(StatusId)?.GetComponent<TMP_Text>();
            model = Node(ModelId)?.GetComponent<TMP_Text>();
            scaleText = Node(ScaleId)?.GetComponent<TMP_Text>();
            rescaled = false;
            chooseLater = false;
            lastStage = (RocketLaunchSolver.Stage)(-1);
            lastCountdown = -1;
            if (rocket != null)
                foreach (var r in rocket.GetComponentsInChildren<Renderer>()) r.enabled = false;  // the placeholder; the look replaces it
            BuildLook();
            build.Inputs.Changed += OnInput;
            solver.FlightChanged += OnFlightChanged;
            // A CAD version the sliders haven't taken yet: take it (this rebuilds the scene once, so the sliders show it).
            if (haveCadDesign && pushedVersion != cadVersion && PushCadDesign()) return;
            ChooseFlight();
            ShowModel();
        }

        void Unbind()
        {
            if (bound != null) bound.Inputs.Changed -= OnInput;
            if (solver != null) solver.FlightChanged -= OnFlightChanged;
            bound = null;
            sim = null;
            solver = null;
            rocket = chute = flightNode = look = null;
            status = model = scaleText = null;
        }

        Transform Node(string id) => bound.Nodes.TryGetValue(id, out var go) && go != null ? go.transform : null;

        void OnInput(string input)
        {
            if (solver == null) return;
            BuildLook();
            ChooseFlight();
        }

        void OnFlightChanged() => ShowModel();

        // ------------------------------------------------------------------ the launch

        void OnFired(string id)
        {
            if (id != LaunchButton || solver == null) return;
            if (solver.Phase == RocketLaunchSolver.Stage.Landed) sim.Restart();
            if (solver.Launch())
            {
                startedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                sim.Play();
            }
        }

        // Every frame: orientation, the parachute, the apogee marker, and the status line when the phase changes.
        void Animate()
        {
            var stage = solver.Phase;
            if (chooseLater && (stage == RocketLaunchSolver.Stage.Pad || stage == RocketLaunchSolver.Stage.Landed)) ChooseFlight();
            rocket.localRotation = Quaternion.FromToRotation(Vector3.up, solver.Heading());
            if (chute != null)
            {
                bool open = stage == RocketLaunchSolver.Stage.Parachute;
                if (chute.gameObject.activeSelf != open) chute.gameObject.SetActive(open);
                if (open) chute.localPosition = rocket.localPosition + Vector3.up * (rocketHeight + 0.05f);
            }
            int count = stage == RocketLaunchSolver.Stage.Countdown ? Mathf.CeilToInt(solver.CountdownLeft) : -1;
            if (stage == lastStage && count == lastCountdown) return;
            if (stage == RocketLaunchSolver.Stage.Landed && lastStage != RocketLaunchSolver.Stage.Landed && lastStage >= RocketLaunchSolver.Stage.Boost)
                SaveRun();
            lastStage = stage;
            lastCountdown = count;
            ShowStatus(stage, count);
        }

        void ShowStatus(RocketLaunchSolver.Stage stage, int count)
        {
            if (status == null) return;
            var f = solver.Active;
            switch (stage)
            {
                case RocketLaunchSolver.Stage.Pad: status.SetText("On the pad. Press Launch."); break;
                case RocketLaunchSolver.Stage.Countdown: status.SetText("T-{0:0}", count); break;
                case RocketLaunchSolver.Stage.Boost: status.SetText("Motor burning"); break;
                case RocketLaunchSolver.Stage.Coast: status.SetText("Coasting up"); break;
                case RocketLaunchSolver.Stage.Parachute: status.SetText("Parachute open ({0:0}x speed)", solver.DescentSpeedup); break;
                case RocketLaunchSolver.Stage.Landed:
                    status.SetText(Passed(f) ? "Landed {0:0} m from the pad. Saved." : "Landed {0:0} m away. Saved: it failed the test.", f.driftM);
                    break;
            }
        }

        static bool Passed(RocketTrajectory f) => f.marginCal >= StableMargin && f.Deploys;

        // ------------------------------------------------------------------ result saved

        /// <summary>Writes the landed flight as a run record, attached to the open project like a course run.</summary>
        void SaveRun()
        {
            var manifest = host.OpenManifest;
            if (manifest == null) return;
            var f = solver.Active;
            string artifactId = (string)manifest["id"] ?? TemplateId;
            bool stable = f.marginCal >= StableMargin;
            string outcome = Passed(f) ? "completed" : "failed";
            string reason = outcome == "completed" ? null : !stable ? "unstable" : "no_parachute";
            bool cadDesignFlown = haveCadDesign && solver.Design.SameAs(cadDesign);
            var record = new JObject
            {
                ["schema"] = ArtifactRuns.Schema,
                ["run_id"] = ArtifactRuns.NextId(artifactId),
                ["artifact_id"] = artifactId,
                ["artifact_version"] = manifest.Value<int?>("version") ?? 1,
                ["scenario"] = "default",
                ["seed"] = 1,
                ["layout_id"] = JValue.CreateNull(),
                ["project_id"] = JValue.CreateNull(),
                ["project_version"] = JValue.CreateNull(),
                ["thing"] = new JObject { ["name"] = "Model rocket", ["version"] = cadDesignFlown ? (JToken)cadVersion : JValue.CreateNull() },
                ["controller"] = JValue.CreateNull(),
                ["inputs"] = bound.Inputs.Snapshot(),
                ["outcome"] = outcome,
                ["reason"] = reason != null ? (JToken)reason : JValue.CreateNull(),
                ["metrics"] = new JObject
                {
                    ["apogee_m"] = Math.Round(f.apogeeM, 1),
                    ["max_speed_mps"] = Math.Round(f.maxSpeedMps, 1),
                    ["stability_cal"] = Math.Round(f.marginCal, 2),
                    ["time_to_apogee_s"] = Math.Round(f.timeToApogeeS, 2),
                    ["flight_time_s"] = Math.Round(f.landingS, 2),
                    ["drift_m"] = Math.Round(f.driftM, 1),
                    ["descent_rate_mps"] = Math.Round(f.descentRateMps, 1),
                    ["parachute"] = f.Deploys ? 1 : 0,
                    ["stable"] = stable ? 1 : 0,
                    ["wind_mps"] = Math.Round(solver.WindMps, 1),
                    ["design_version"] = cadDesignFlown ? (JToken)cadVersion : JValue.CreateNull(),
                    ["rocketpy"] = solver.UsingRocketPy ? 1 : 0,
                },
                ["started_at"] = startedAt != null ? (JToken)startedAt : JValue.CreateNull(),
                ["ended_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            };
            if (manifest["links"] is JObject links)
            {
                record["project_id"] = links["project"] ?? JValue.CreateNull();
                record["project_version"] = links["project_version"] ?? JValue.CreateNull();
            }
            if (NotebookStore.IsOpen)
            {
                record["project_id"] = NotebookStore.ProjectId;
                record["project_version"] = NotebookStore.CurrentVersion();
            }
            ArtifactRuns.Save(record);
            NotebookStore.AttachRun((string)record["run_id"], outcome);
            host.RunsRow?.Refresh();
            Debug.Log($"[RocketLaunch] {record["run_id"]}: {outcome}, apogee {f.apogeeM:0} m, margin {f.marginCal:0.00} cal, {(solver.UsingRocketPy ? "RocketPy" : "simplified model")}");
        }

        // ------------------------------------------------------------------ the CAD design

        void WatchCad()
        {
            var cad = CadSession.Instance;
            var p = cad != null ? cad.Params : null;
            if (p == null || p.part != "rocket" || cad.Latest?.info == null || cad.Latest.info.part != "rocket") return;
            int version = cad.Latest.info.version;
            if (version == cadVersion) return;
            cadVersion = version;
            cadDesign = DesignOf(p);
            haveCadDesign = true;
            haveCadFlight = false;
            cadFlight.Clear();
            var analysis = cad.Latest.info.analysis;
            if (p.Has("flight") && fetching != version)
            {
                fetching = version;
                cad.GetFlight(version, cad.Latest.info.analysis?.demo?.source ?? "/cad/flight", flight =>
                {
                    if (fetching == version) fetching = -1;
                    if (flight == null || version != cadVersion || !Fill(flight, analysis)) return;
                    haveCadFlight = true;
                    ChooseFlight();
                });
            }
            // A new design while the launch is open: the sliders take it (rebuilds once).
            if (solver != null && PushCadDesign()) return;
            ChooseFlight();
        }

        static RocketDesign DesignOf(CadParams p)
        {
            var d = RocketDesign.Default;
            d.bodyLength = Num(p, "body_length", d.bodyLength);
            d.bodyDiameter = Num(p, "body_diameter", d.bodyDiameter);
            d.noseLength = Num(p, "nose_length", d.noseLength);
            d.finCount = Mathf.RoundToInt(Num(p, "fin_count", d.finCount));
            d.finRoot = Num(p, "fin_root", d.finRoot);
            d.finTip = Num(p, "fin_tip", d.finTip);
            d.finSpan = Num(p, "fin_span", d.finSpan);
            d.finSweep = Num(p, "fin_sweep", d.finSweep);
            var shape = p.Find("nose_shape");
            d.cone = shape != null && shape.value is string s && s == "cone";
            return d;
        }

        static float Num(CadParams p, string name, float fallback)
        {
            var row = p.Find(name);
            return row == null || row.IsOptions ? fallback : (float)row.Number;
        }

        // RocketPy's flight into the playback buffer. False if it has no samples (a flight stored before they existed).
        bool Fill(CadFlight flight, CadAnalysis analysis)
        {
            if (flight.samples == null || flight.samples.Count < 2) return false;
            var f = cadFlight;
            f.Clear();
            foreach (var s in flight.samples) f.Add(s.t, s.downrangeM, s.altM, s.speedMps);
            flight.Times(out float land, out float tApogee, out float burnout, out float deploy, out _);
            f.apogeeM = flight.apogeeM;
            f.timeToApogeeS = tApogee;
            f.maxSpeedMps = flight.maxSpeedMps;
            f.railExitSpeedMps = flight.railExitSpeedMps;
            f.burnoutS = burnout;
            f.landingS = land;
            f.deployS = flight.deployTimeS.HasValue && flight.deployTimeS.Value < land ? deploy : -1f;
            f.driftM = flight.driftM > 0f ? flight.driftM : Mathf.Abs(flight.samples[flight.samples.Count - 1].downrangeM);
            f.descentRateMps = flight.descentRateMps;
            f.liftoffG = flight.liftoffMassG;
            // Stability as the CAD panel shows it: Barrowman CP against the CG of the built parts, loaded motor.
            f.marginCal = analysis?.marginCal ?? flight.railExitMarginCal;
            f.cgMm = analysis?.cgMm ?? 0f;
            f.cpMm = analysis?.cpMm ?? 0f;
            int c = 0;
            f.At(tApogee, ref c, out f.apogeeX, out _, out _);
            f.rocketPy = true;
            f.designVersion = cadVersion;
            return true;
        }

        // The sliders take the CAD design's values; the scene is rebuilt so they show them. True if it rebuilt.
        bool PushCadDesign()
        {
            pushedVersion = cadVersion;
            if (bound == null || solver == null || solver.Design.SameAs(cadDesign)) return false;
            var inputs = bound.Inputs;
            var d = cadDesign;
            Set(inputs, "body_length", d.bodyLength);
            Set(inputs, "body_diameter", d.bodyDiameter);
            Set(inputs, "nose_length", d.noseLength);
            Set(inputs, "nose_cone", d.cone ? 1 : 0);
            Set(inputs, "fin_count", d.finCount);
            Set(inputs, "fin_root", d.finRoot);
            Set(inputs, "fin_tip", d.finTip);
            Set(inputs, "fin_span", d.finSpan);
            Set(inputs, "fin_sweep", d.finSweep);
            Set(inputs, "wind_mps", 0);
            return host.Relayout();
        }

        static void Set(ArtifactInputs inputs, string name, double value)
        {
            if (inputs.Has(name)) inputs.TrySet(name, Math.Min(Math.Max(value, inputs.Min(name)), inputs.Max(name)));
        }

        // RocketPy's flight while the sliders are the CAD design with no wind; the simplified model otherwise.
        void ChooseFlight()
        {
            if (solver == null) return;
            var phase = solver.Phase;
            if (phase != RocketLaunchSolver.Stage.Pad && phase != RocketLaunchSolver.Stage.Landed)
            {
                chooseLater = true;  // never swap the flight under a rocket in the air
                return;
            }
            chooseLater = false;
            bool use = haveCadFlight && solver.WindMps <= 0f && solver.Design.SameAs(cadDesign);
            bool was = solver.UsingRocketPy;
            solver.SetReference(haveCadFlight ? cadFlight : null, use);
            if (was != solver.UsingRocketPy && solver.Phase == RocketLaunchSolver.Stage.Pad) sim.Restart();
            ShowModel();
        }

        void ShowModel()
        {
            if (model == null || solver == null) return;
            if (solver.UsingRocketPy) model.SetText("Flight: RocketPy 6-DOF climb, CAD v{0:0}", cadVersion);
            else if (haveCadDesign && !solver.Design.SameAs(cadDesign)) model.SetText("Flight: simplified model (what-if, not the CAD design)");
            else if (solver.WindMps > 0f) model.SetText("Flight: simplified model (wind)");
            else model.SetText("Flight: simplified model (no cloud sim)");
        }

        // ------------------------------------------------------------------ the look

        // The rocket from the sliders' numbers, at 1:4: body tube, nose, fins, tail at the node's origin, nose along +y.
        void BuildLook()
        {
            if (rocket == null || solver == null) return;
            if (look != null) Destroy(look.gameObject);
            look = new GameObject("RocketLook").transform;
            look.SetParent(rocket, false);
            var d = solver.Design;
            float k = solver.DrawScale / 1000f;  // mm to scene metres: the rocket is drawn at 1:4, at the path's 1:200 it'd be 2 mm
            float L = d.bodyLength * k, D = d.bodyDiameter * k, N = d.noseLength * k;
            Part("cone", new Vector3(D, N, D), new Vector3(0f, L + N * 0.5f, 0f), Quaternion.identity, "#E0553A");  // an ogive is drawn as a cone too
            Part("cylinder", new Vector3(D, L, D), new Vector3(0f, L * 0.5f, 0f), Quaternion.identity, "#E8ECF2");
            float root = d.finRoot * k, tip = d.finTip * k, span = d.finSpan * k, sweep = d.finSweep * k;
            float chord = (root + tip) * 0.5f, mid = root - (sweep + tip * 0.5f + root * 0.5f) * 0.5f;  // a box standing in for the trapezoid
            for (int i = 0; i < d.finCount; i++)
            {
                var turn = Quaternion.Euler(0f, 360f * i / d.finCount, 0f);
                Part("box", new Vector3(span, chord, 3.2f * k * 2f), turn * new Vector3(D * 0.5f + span * 0.5f, Mathf.Max(mid, chord * 0.5f), 0f), turn, "#3D6FE0");
            }
            rocketHeight = L + N;
        }

        void Part(string shape, Vector3 size, Vector3 at, Quaternion turn, string color)
        {
            var go = ArtifactLook.Shape(shape, false);
            go.transform.SetParent(look, false);
            go.transform.localPosition = at;
            go.transform.localRotation = turn;
            go.transform.localScale = ArtifactLook.ShapeScale(shape, size);
            go.GetComponent<Renderer>().sharedMaterial = ArtifactLook.Material(color);
        }

        // Once the scene has grown in: true scale (1 scene unit = 1 m), the pad on the volume's base, and the scale said.
        void Rescale()
        {
            rescaled = true;
            var root = host.ContentRoot;
            if (root != null && host.OpenManifest != null && bound.Host == host)
            {
                // The scene is 1 unit = 1 m; the volume shrank it to fit its box. Back to true size, the pad where it stood.
                float shrink = root.localScale.y;
                if (shrink > 0f && shrink < 0.999f && flightNode != null)
                {
                    var at = root.localPosition;
                    float padLocal = flightNode.localPosition.y;
                    float pad = at.y + padLocal * shrink;   // where the pad stands now: keep it there
                    root.localScale = Vector3.one;
                    root.localPosition = new Vector3(at.x, pad - padLocal, at.z);
                }
            }
            if (scaleText == null || flightNode == null) return;
            float metresPerMetre = solver.Scale * flightNode.lossyScale.y;  // room metres per metre of sky
            float ratio = metresPerMetre > 0f ? 1f / metresPerMetre : 0f;
            float rocketRatio = flightNode.lossyScale.y > 0f ? 1f / (solver.DrawScale * flightNode.lossyScale.y) : 0f;
            scaleText.SetText("Flight path 1:{0:0}  ·  rocket drawn 1:{1:0}", ratio, rocketRatio);
        }
    }
}
