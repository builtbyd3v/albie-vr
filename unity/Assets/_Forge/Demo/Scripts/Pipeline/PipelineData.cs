using System;
using Newtonsoft.Json;
using UnityEngine;

namespace Forge.Demo
{
    /// <summary>
    /// The scripted numbers behind the pipeline panel (Resources/pipeline_data.json): the model that is pulled, the dataset, the
    /// per-epoch curves of a plausible fine-tune, the four sample frames and the push. Scripted for the video; none of it is measured.
    /// </summary>
    [Serializable]
    public sealed class PipelineData
    {
        public const string ResourceName = "pipeline_data";

        public ModelInfo model = new ModelInfo();
        public DatasetInfo dataset = new DatasetInfo();
        public GpuInfo gpu = new GpuInfo();
        public TrainInfo train = new TrainInfo();
        public SampleInfo[] samples = new SampleInfo[0];
        public PushInfo push = new PushInfo();

        [Serializable]
        public sealed class ModelInfo
        {
            public string name = "face-track-nano";
            public string task = "Face and object tracking";
            public string @params = "3.2 M";
            public string licence = "Apache-2.0";
            public float sizeMb = 12.8f;
            public string latency = "6 ms on device";
            public string source = "model hub";
        }

        [Serializable]
        public sealed class DatasetInfo
        {
            public string name = "hudson-faces-v1";
            public int frames = 1240;
            public int lightingSets = 3;
            public string summary = "1,240 frames from the headset cameras, 3 lighting sets";
        }

        [Serializable]
        public sealed class GpuInfo
        {
            public string label = "cloud GPU, 1x A10G";
            public string speedup = "20x";
        }

        [Serializable]
        public sealed class TrainInfo
        {
            public int epochs = 30;
            public float[] loss = new float[0];
            [JsonProperty("val_loss")] public float[] valLoss = new float[0];
            public float[] map50 = new float[0];
        }

        [Serializable]
        public sealed class SampleInfo
        {
            public string id = "";
            public string light = "";
            public float[] top = { 0.2f, 0.25f, 0.3f };
            public float[] bottom = { 0.1f, 0.12f, 0.16f };
            public float[] subject = { 0.5f, 0.55f, 0.6f };
            public float[] head = { 0.5f, 0.4f, 1f };       // centre x, centre y (from the top), scale
            public float[] truth = { 0.3f, 0.12f, 0.4f, 0.46f };   // x, y (from the top), w, h, as fractions of the frame
            public float[] start = { 0.05f, 0.05f, 0.9f, 0.8f };
            public float[] falseBox = null;                  // a wrong detection that fades as the model learns
            public float conf0 = 0.35f, conf1 = 0.97f;
        }

        [Serializable]
        public sealed class PushInfo
        {
            public string repo = "hudson/drone-tracker";
            public string repoLabel = "drone-tracker (private)";
            public string branch = "main";
            public PushFile[] files = new PushFile[0];
            public string commitMessage = "Fine-tune face-track-nano on hudson-faces-v1 (mAP50 0.93)";
            public string sha = "a3f9c2e";
        }

        [Serializable]
        public sealed class PushFile
        {
            public string path = "";
            public string size = "";
            public float mb;
        }

        /// <summary>Loads Resources/pipeline_data.json; any gap is filled from the built-in script so the panel never comes up empty.</summary>
        public static PipelineData Load()
        {
            PipelineData data = null;
            var asset = Resources.Load<TextAsset>(ResourceName);
            if (asset != null)
            {
                try { data = JsonConvert.DeserializeObject<PipelineData>(asset.text); }
                catch (Exception e) { Debug.LogWarning("[Demo] pipeline_data.json is not valid: " + e.Message); }
            }
            else
            {
                Debug.LogWarning("[Demo] Resources/pipeline_data.json is missing; the pipeline panel uses its built-in script.");
            }
            if (data == null) data = new PipelineData();
            data.Fill();
            return data;
        }

        void Fill()
        {
            if (model == null) model = new ModelInfo();
            if (dataset == null) dataset = new DatasetInfo();
            if (gpu == null) gpu = new GpuInfo();
            if (train == null) train = new TrainInfo();
            if (push == null) push = new PushInfo();
            if (samples == null) samples = new SampleInfo[0];
            int n = Mathf.Max(2, train.epochs);
            train.epochs = n;
            train.loss = Curve(train.loss, n, 1.9f, 0.31f);
            train.valLoss = Curve(train.valLoss, n, 2.04f, 0.44f);
            train.map50 = Curve(train.map50, n, 0.41f, 0.93f);
            if (push.files == null || push.files.Length == 0)
            {
                push.files = new[]
                {
                    new PushFile { path = "weights/face-track-nano-ft-v1.onnx", size = "12.6 MB", mb = 12.6f },
                    new PushFile { path = "config.yaml", size = "1.2 KB", mb = 0.0012f },
                    new PushFile { path = "eval/report.md", size = "4.8 KB", mb = 0.0048f },
                };
            }
            if (samples.Length == 0)
            {
                samples = new[]
                {
                    new SampleInfo { id = "frame 0112", light = "window light" },
                    new SampleInfo { id = "frame 0587", light = "lamp, evening", head = new[] { 0.58f, 0.42f, 0.9f } },
                    new SampleInfo { id = "frame 0903", light = "overhead, bright", head = new[] { 0.38f, 0.43f, 1.08f } },
                    new SampleInfo { id = "frame 1170", light = "backlit, dim", head = new[] { 0.52f, 0.38f, 0.94f } },
                };
            }
        }

        // A smooth stand-in when a curve is missing or the wrong length (an edited JSON should never break the panel).
        static float[] Curve(float[] have, int n, float from, float to)
        {
            if (have != null && have.Length == n) return have;
            var curve = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                curve[i] = Mathf.Lerp(from, to, 1f - Mathf.Exp(-4f * t)) ;
            }
            return curve;
        }

        /// <summary>The value of a curve at a fractional position (0 = first epoch, Length - 1 = last), eased between two epochs.</summary>
        public static float At(float[] curve, float position)
        {
            if (curve == null || curve.Length == 0) return 0f;
            position = Mathf.Clamp(position, 0f, curve.Length - 1);
            int i = Mathf.Min((int)position, curve.Length - 1);
            int j = Mathf.Min(i + 1, curve.Length - 1);
            return Mathf.Lerp(curve[i], curve[j], position - i);
        }
    }
}
