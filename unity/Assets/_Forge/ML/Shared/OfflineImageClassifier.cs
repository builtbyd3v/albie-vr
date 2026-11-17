using System;
using UnityEngine;

namespace Forge.ML
{
    /// <summary>A small softmax classifier over 8 x 8 RGB thumbnails. Train and validation images never overlap.</summary>
    public sealed class OfflineImageClassifier
    {
        public const int Side = 8, Features = Side * Side * 3;
        readonly float[][] images;
        readonly int[] labels;
        readonly bool[] validation;
        readonly bool[] included;
        readonly float[] weights, bias, probabilities;
        public int Count => labels.Length;
        public int Classes => bias.Length;

        public OfflineImageClassifier(float[][] images, int[] labels, int classes, bool[] included = null)
        {
            if (images == null || labels == null || images.Length != labels.Length || classes < 2) throw new ArgumentException("Invalid image dataset");
            if (included != null && included.Length != labels.Length) throw new ArgumentException("Invalid image mask");
            this.images = images; this.labels = labels; this.included = included;
            weights = new float[classes * Features]; bias = new float[classes]; probabilities = new float[classes];
            validation = new bool[labels.Length]; var seen = new int[classes];
            for (int i = 0; i < labels.Length; i++)
            {
                if (labels[i] < 0 || labels[i] >= classes || images[i] == null || images[i].Length != Features) throw new ArgumentException("Invalid image or label");
                validation[i] = IsIncluded(i) && seen[labels[i]]++ % 5 == 0;
            }
        }

        public bool IsValidation(int i) => validation[i];
        public bool IsIncluded(int i) => included == null || included[i];
        public float[] CopyProbabilities() => (float[])probabilities.Clone();
        public int Predict(int i, out float loss)
        {
            float max = float.NegativeInfinity;
            for (int c = 0; c < Classes; c++)
            {
                float score = bias[c]; int start = c * Features;
                for (int f = 0; f < Features; f++) score += weights[start + f] * images[i][f];
                probabilities[c] = score; max = Mathf.Max(max, score);
            }
            float sum = 0f;
            for (int c = 0; c < Classes; c++) { probabilities[c] = Mathf.Exp(probabilities[c] - max); sum += probabilities[c]; }
            int best = 0;
            for (int c = 0; c < Classes; c++) { probabilities[c] /= sum; if (probabilities[c] > probabilities[best]) best = c; }
            loss = -Mathf.Log(Mathf.Max(1e-8f, probabilities[labels[i]]));
            return best;
        }

        public void TrainOne(int i, float rate)
        {
            if (!IsIncluded(i) || validation[i]) return;
            Predict(i, out _);
            for (int c = 0; c < Classes; c++)
            {
                float gradient = probabilities[c] - (labels[i] == c ? 1f : 0f);
                bias[c] -= rate * gradient;
                int start = c * Features;
                for (int f = 0; f < Features; f++) weights[start + f] -= rate * gradient * images[i][f];
            }
        }
    }
}
