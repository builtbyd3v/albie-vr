using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.Demo
{
    /// <summary>
    /// Fades a group in or out with a small slide, then switches it off when it has gone. Show / Hide are cheap to call again.
    /// <see cref="offset"/> is where it comes from (and goes back to), in the units of its parent.
    /// </summary>
    public sealed class PipelineFader : MonoBehaviour
    {
        public Vector2 offset = new Vector2(0f, -26f);
        public float duration = 0.35f;

        CanvasGroup group;
        RectTransform rect;
        Vector2 home;
        bool ready, want;
        float amount;

        void Init()
        {
            if (ready) return;
            ready = true;
            rect = (RectTransform)transform;
            home = rect.anchoredPosition;
            group = GetComponent<CanvasGroup>();
            if (group == null) group = gameObject.AddComponent<CanvasGroup>();
        }

        public bool Visible => want;

        public void Show(bool instant = false)
        {
            Init();
            gameObject.SetActive(true);
            want = true;
            if (instant) amount = 1f;
            Apply();
        }

        public void Hide(bool instant = false)
        {
            Init();
            want = false;
            if (instant)
            {
                amount = 0f;
                Apply();
                gameObject.SetActive(false);
            }
        }

        void Update()
        {
            float target = want ? 1f : 0f;
            if (amount == target)
            {
                if (!want) gameObject.SetActive(false);
                return;
            }
            amount = Mathf.MoveTowards(amount, target, Time.deltaTime / Mathf.Max(0.01f, duration));
            Apply();
        }

        void Apply()
        {
            float e = ForgeTween.EaseOutCubic(amount);
            group.alpha = e;
            rect.anchoredPosition = home + offset * (1f - e);
            bool live = want && amount > 0.6f;
            group.blocksRaycasts = live;
            group.interactable = live;
        }
    }

    /// <summary>
    /// The three step nodes (Pull, Train, Push) and the lines between them. A node is pending, active (accent plate, a glow that breathes)
    /// or done (a check in the accent badge); changes ease over a third of a second. A line fills with the accent once the step before it is done.
    /// </summary>
    public sealed class PipelineStepper : MonoBehaviour
    {
        public const int Pending = 0, Active = 1, Done = 2;

        public ForgeSurface[] plate = new ForgeSurface[3], badge = new ForgeSurface[3];
        public TMP_Text[] number = new TMP_Text[3], label = new TMP_Text[3];
        public Image[] check = new Image[3];
        public RectTransform[] lineFill = new RectTransform[2];
        public float lineLength;
        public Color accent = new Color(0.30f, 0.55f, 1f);

        readonly int[] state = new int[3];
        readonly float[] act = new float[3], done = new float[3], line = new float[2];
        bool dirty = true;

        public void Set(int node, int value)
        {
            if (node < 0 || node > 2 || state[node] == value) return;
            state[node] = value;
            dirty = true;
        }

        public void Snap()
        {
            for (int i = 0; i < 3; i++) { act[i] = state[i] == Active ? 1f : 0f; done[i] = state[i] == Done ? 1f : 0f; }
            for (int i = 0; i < 2; i++) line[i] = state[i] == Done ? 1f : 0f;
            dirty = true;
            Apply();
        }

        void Update()
        {
            const float rate = 1f / 0.35f;
            bool moving = false;
            for (int i = 0; i < 3; i++)
            {
                float ta = state[i] == Active ? 1f : 0f, td = state[i] == Done ? 1f : 0f;
                if (act[i] != ta) { act[i] = Mathf.MoveTowards(act[i], ta, Time.deltaTime * rate); moving = true; }
                if (done[i] != td) { done[i] = Mathf.MoveTowards(done[i], td, Time.deltaTime * rate); moving = true; }
                if (state[i] == Active) moving = true;  // the glow breathes
            }
            for (int i = 0; i < 2; i++)
            {
                float tl = state[i] == Done ? 1f : 0f;
                if (line[i] != tl) { line[i] = Mathf.MoveTowards(line[i], tl, Time.deltaTime / 0.6f); moving = true; }
            }
            if (moving || dirty) Apply();
        }

        void Apply()
        {
            dirty = false;
            for (int i = 0; i < 3; i++)
            {
                float a = ForgeTween.EaseOutCubic(act[i]), d = ForgeTween.EaseOutCubic(done[i]);
                Color pending = new Color(1f, 1f, 1f, 0.05f), active = new Color(accent.r, accent.g, accent.b, 0.26f), finished = new Color(1f, 1f, 1f, 0.09f);
                if (plate[i] != null)
                {
                    plate[i].color = Color.Lerp(Color.Lerp(pending, active, a), finished, d);
                    plate[i].Outline = a * (1f - d) * 0.85f;
                    plate[i].Glow = a * (1f - d) * (0.5f + 0.18f * Mathf.Sin(Time.time * 3.2f));
                }
                if (badge[i] != null)
                    badge[i].color = Color.Lerp(new Color(1f, 1f, 1f, 0.14f), accent, Mathf.Max(a, d));
                if (number[i] != null)
                    number[i].color = new Color(1f, 1f, 1f, (1f - d) * (0.6f + 0.4f * a));
                if (check[i] != null)
                    check[i].color = new Color(1f, 1f, 1f, d);
                if (label[i] != null)
                    label[i].color = new Color(1f, 1f, 1f, 0.62f + 0.38f * Mathf.Max(a, d));
            }
            for (int i = 0; i < 2; i++)
            {
                if (lineFill[i] == null) continue;
                float p = ForgeTween.EaseInOutCubic(line[i]);
                lineFill[i].sizeDelta = new Vector2(Mathf.Max(0.01f, lineLength * p), lineFill[i].sizeDelta.y);
                lineFill[i].gameObject.SetActive(p > 0.005f);
            }
        }
    }
}
