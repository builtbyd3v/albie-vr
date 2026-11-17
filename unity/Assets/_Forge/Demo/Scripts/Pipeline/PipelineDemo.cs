using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Forge.Demo
{
    /// <summary>
    /// The demo's Pipeline station (docs/demo/briefs/pipeline.md, beats B5 and B5b): pulls a tracking model from the model hub, fine-tunes
    /// it on Hudson and pushes it to his private repo once he pokes Confirm. A fast-forward on a low panel in front of the user; the numbers
    /// come from Resources/pipeline_data.json and none of the training is real. Registers pipeline.open / close / pull / train / push and
    /// demo.reset on <see cref="DemoActions"/>, raises pipeline.pulled / trained / pushed. Steps run one after another in the order they
    /// were asked for, and a step asked for too early plays the steps before it first, so a Director that fires them a beat early still
    /// gets Pull, Train, Push.
    /// </summary>
    public sealed class PipelineDemo : MonoBehaviour
    {
        public const string Open = "pipeline.open", Close = "pipeline.close", Pull = "pipeline.pull", Train = "pipeline.train", Push = "pipeline.push";
        public const string Pulled = "pipeline.pulled", Trained = "pipeline.trained", Pushed = "pipeline.pushed";

        [Tooltip("The panel prefab (Core/Prefabs/ForgePanel.prefab). Falls back to the Workspace's when empty.")]
        public ForgePanel panelPrefab;
        [Tooltip("Panel size in metres.")]
        public Vector2 sizeMeters = new Vector2(0.70f, 0.44f);
        [Tooltip("How far the panel leans back toward the face (degrees).")]
        public float tiltDegrees = 25f;
        public DemoLayout.Slot slot = DemoLayout.Slot.Low;
        [Header("Timing (s)")]
        public float pullSeconds = 4f;
        public float trainSeconds = 12f;
        public float uploadSeconds = 2.4f;

        PipelineData data;
        PipelineView view;
        ForgePanel panel;
        CanvasGroup group;
        Vector3 baseScale = Vector3.one;
        Coroutine runner, motion;
        readonly Queue<int> queue = new Queue<int>(4);
        bool shown, pulled, trained, pushed, waitingForConfirm, confirmed, cancelled;

        /// <summary>The panel's UI (null until it is built), for tests.</summary>
        public PipelineView View => view;
        public bool IsOpen => shown;
        public bool WaitingForConfirm => waitingForConfirm;

        // ---------------------------------------------------------------- registration

        void OnEnable()
        {
            DemoActions.Register(Open, OnOpen);
            DemoActions.Register(Close, OnClose);
            DemoActions.Register(Pull, OnPull);
            DemoActions.Register(Train, OnTrain);
            DemoActions.Register(Push, OnPush);
            DemoActions.Register(DemoActions.Reset, OnReset);
        }

        void OnDisable()
        {
            DemoActions.Unregister(Open, OnOpen);
            DemoActions.Unregister(Close, OnClose);
            DemoActions.Unregister(Pull, OnPull);
            DemoActions.Unregister(Train, OnTrain);
            DemoActions.Unregister(Push, OnPush);
            DemoActions.Unregister(DemoActions.Reset, OnReset);
        }

        IEnumerator Start()
        {
            yield return null;  // let the scene settle, then build the hidden panel so the beat itself doesn't hitch
            Build();
        }

        void OnDestroy()
        {
            if (panel != null) Destroy(panel.gameObject);
        }

        // ---------------------------------------------------------------- actions

        void OnOpen(DemoCue cue) => OpenPanel();

        void OnClose(DemoCue cue) => ClosePanel();

        void OnPull(DemoCue cue) => Enqueue(0);

        void OnTrain(DemoCue cue) => Enqueue(1);

        void OnPush(DemoCue cue) => Enqueue(2);

        void OnReset(DemoCue cue)
        {
            StopAllCoroutines();
            runner = null;
            motion = null;
            queue.Clear();
            pulled = trained = pushed = waitingForConfirm = confirmed = cancelled = shown = false;
            if (view != null) view.Reset();
            if (panel != null) panel.gameObject.SetActive(false);
        }

        void Enqueue(int step)
        {
            if (!OpenPanel()) return;
            queue.Enqueue(step);
            if (runner == null) runner = StartCoroutine(Run());
        }

        // ---------------------------------------------------------------- the panel

        bool Build()
        {
            if (view != null) return true;
            ForgePanel prefab = panelPrefab;
            if (prefab == null && Workspace.Current != null) prefab = Workspace.Current.panelPrefab;
            if (prefab == null)
            {
                Debug.LogWarning("[Demo] pipeline: there is no panel prefab, so no pipeline panel.");
                return false;
            }
            data = PipelineData.Load();
            panel = Instantiate(prefab, transform);
            panel.name = "Pipeline panel";
            panel.panelId = "demo-pipeline";
            panel.Title = "Pipeline";
            panel.SetBadge("ML", ForgeTheme.ML);
            panel.SizeMeters = sizeMeters;
            baseScale = panel.transform.localScale;
            group = panel.GetComponent<CanvasGroup>();
            if (group == null) group = panel.gameObject.AddComponent<CanvasGroup>();

            var built = new PipelineView();
            if (!built.Build(panel, data))
            {
                Destroy(panel.gameObject);
                panel = null;
                return false;
            }
            view = built;
            if (view.ConfirmButton != null) view.ConfirmButton.onClick.AddListener(OnConfirmPoked);
            if (view.CancelButton != null) view.CancelButton.onClick.AddListener(OnCancelPoked);
            panel.gameObject.SetActive(false);
            return true;
        }

        bool OpenPanel()
        {
            if (!Build()) return false;
            if (shown) return true;
            shown = true;
            Pose pose = DemoLayout.Get(slot);
            panel.transform.SetPositionAndRotation(pose.position, pose.rotation * Quaternion.Euler(tiltDegrees, 0f, 0f));
            panel.gameObject.SetActive(true);
            group.alpha = 0f;
            Animate(0f, 1f, 0.4f, false);
            return true;
        }

        void ClosePanel()
        {
            if (!shown || panel == null) return;
            shown = false;
            Animate(1f, 0f, 0.3f, true);
        }

        // Fade and a small scale, eased; the panel switches off when it has faded out.
        void Animate(float from, float to, float seconds, bool hideAtEnd)
        {
            if (motion != null) StopCoroutine(motion);
            motion = StartCoroutine(ForgeTween.Run(seconds, 0f, t =>
            {
                float e = ForgeTween.EaseOutCubic(t);
                float v = Mathf.Lerp(from, to, e);
                group.alpha = v;
                panel.transform.localScale = baseScale * Mathf.Lerp(0.94f, 1f, v);
            }, () =>
            {
                motion = null;
                if (hideAtEnd && !shown && panel != null) panel.gameObject.SetActive(false);
            }));
        }

        // ---------------------------------------------------------------- the steps

        IEnumerator Run()
        {
            while (queue.Count > 0)
            {
                int step = queue.Dequeue();
                yield return step == 0 ? PullStep() : step == 1 ? TrainStep() : PushStep();
            }
            runner = null;
        }

        IEnumerator PullStep()
        {
            if (pulled) yield break;
            view.SetStep(0, PipelineStepper.Active);
            view.ShowPull();
            view.SetPull(0f);
            view.ShowPullCard();
            yield return new WaitForSeconds(0.7f);
            float span = Mathf.Max(0.5f, pullSeconds - 1.05f);
            for (float t = 0f; t < span; t += Time.deltaTime)
            {
                float x = t / span;
                view.SetPull(Mathf.Lerp(x, Mathf.SmoothStep(0f, 1f, x), 0.55f));
                yield return null;
            }
            view.SetPull(1f);
            view.SetStep(0, PipelineStepper.Done);
            pulled = true;
            yield return new WaitForSeconds(0.35f);
            DemoActions.Raise(Pulled);
        }

        IEnumerator TrainStep()
        {
            if (trained) yield break;
            if (!pulled) yield return PullStep();
            view.SetStep(1, PipelineStepper.Active);
            view.ShowTrain();
            view.SetTrain(0f);
            yield return new WaitForSeconds(0.5f);
            int epochs = data.train.epochs;
            float span = Mathf.Max(1f, trainSeconds - 1.1f);
            for (float t = 0f; t < span; t += Time.deltaTime)
            {
                float x = t / span;
                view.SetTrain(epochs * Mathf.Lerp(x, Mathf.SmoothStep(0f, 1f, x), 0.3f));
                yield return null;
            }
            view.SetTrain(epochs);
            view.SetStep(1, PipelineStepper.Done);
            trained = true;
            yield return new WaitForSeconds(0.6f);
            DemoActions.Raise(Trained);
        }

        IEnumerator PushStep()
        {
            if (pushed) yield break;
            if (!trained) yield return TrainStep();
            view.SetStep(2, PipelineStepper.Active);
            view.ShowPush();
            view.ShowConfirm();
            confirmed = cancelled = false;
            waitingForConfirm = true;
            while (!confirmed && !cancelled) yield return null;
            waitingForConfirm = false;
            if (cancelled)
            {
                view.ShowCancelled();
                yield break;
            }

            view.SetUpload(0f);
            view.ShowUpload();
            yield return new WaitForSeconds(0.5f);
            float span = Mathf.Max(0.5f, uploadSeconds);
            for (float t = 0f; t < span; t += Time.deltaTime)
            {
                float x = t / span;
                view.SetUpload(Mathf.Lerp(x, Mathf.SmoothStep(0f, 1f, x), 0.5f));
                yield return null;
            }
            view.SetUpload(1f);
            yield return new WaitForSeconds(0.35f);
            view.ShowCommit();
            view.SetStep(2, PipelineStepper.Done);
            pushed = true;
            yield return new WaitForSeconds(0.4f);
            DemoActions.Raise(Pushed);
        }

        void OnConfirmPoked()
        {
            if (waitingForConfirm) confirmed = true;
        }

        void OnCancelPoked()
        {
            if (waitingForConfirm) cancelled = true;
        }
    }
}
