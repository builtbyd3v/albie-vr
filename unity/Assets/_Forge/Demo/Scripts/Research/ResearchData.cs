using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Forge.Demo
{
    /// <summary>
    /// The data behind the Research station (Resources/research_data.json). The same shape the real research tools will
    /// return later (docs/demo/JUDGE_PATH.md J3, J4), so the panels never need to change when the hub replaces the file.
    /// The full description of the shape is the comment block at the top of <see cref="ResearchStation"/>.
    /// </summary>
    public sealed class ResearchData
    {
        public List<string> constraints = new List<string>();
        public List<QueryData> queries = new List<QueryData>();
        public List<CameraData> cameras = new List<CameraData>();
        [JsonProperty("models_query")] public string modelsQuery = "";
        public List<ModelData> models = new List<ModelData>();
        public VerdictData verdict = new VerdictData();

        public const string ResourceName = "research_data";

        /// <summary>Reads Resources/research_data.json. Returns an empty (but usable) set when the file is missing or broken.</summary>
        public static ResearchData Load()
        {
            var asset = Resources.Load<TextAsset>(ResourceName);
            if (asset == null)
            {
                Debug.LogWarning("[Research] Resources/research_data.json is missing; the panels will be empty.");
                return new ResearchData();
            }
            try
            {
                return JsonConvert.DeserializeObject<ResearchData>(asset.text) ?? new ResearchData();
            }
            catch (System.Exception e)
            {
                Debug.LogError("[Research] research_data.json could not be read: " + e.Message);
                return new ResearchData();
            }
        }

        public CameraData Camera(string id)
        {
            for (int i = 0; i < cameras.Count; i++)
                if (cameras[i].id == id) return cameras[i];
            return null;
        }
    }

    public sealed class QueryData
    {
        public string text = "";
        public int results;
    }

    public sealed class SpecsData
    {
        public string resolution = "";
        public int fps;
        public int fov;
        public string voltage = "";
        [JsonProperty("weight_g")] public int weightG;
        [JsonProperty("price_usd")] public int priceUsd;
        [JsonProperty("ships_days")] public int shipsDays;
    }

    public sealed class CheckData
    {
        public bool pass;
        public string label = "";
        public string reason = "";
    }

    public sealed class ChecksData
    {
        public CheckData power = new CheckData();
        public CheckData weight = new CheckData();
        public CheckData mount = new CheckData();
        public CheckData price = new CheckData();

        public const int Count = 4;

        /// <summary>The checks in display order: power, weight, mount, price.</summary>
        public CheckData At(int i)
        {
            switch (i)
            {
                case 0: return power;
                case 1: return weight;
                case 2: return mount;
                default: return price;
            }
        }
    }

    public sealed class CameraData
    {
        public string id = "";
        public string name = "";
        public string image = "";
        public string source = "";
        public SpecsData specs = new SpecsData();
        public ChecksData checks = new ChecksData();

        public bool PassesAll
        {
            get
            {
                for (int i = 0; i < ChecksData.Count; i++)
                    if (!checks.At(i).pass) return false;
                return true;
            }
        }
    }

    public sealed class ModelData
    {
        public string id = "";
        public string name = "";
        public string image = "";
        public string task = "";
        public string @params = "";
        public string license = "";
        [JsonProperty("latency_ms")] public int latencyMs;
        public string downloads = "";
        public bool pick;
    }

    public sealed class VerdictLine
    {
        public string key = "";
        public string text = "";
    }

    public sealed class VerdictData
    {
        public string winner = "";
        public List<VerdictLine> lines = new List<VerdictLine>();
    }
}
