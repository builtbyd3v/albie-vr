using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Closing the open artifact by hand, and bringing it back. Every hand way in ends here:
    /// <list type="bullet">
    /// <item>Thrown away (<see cref="Throw"/>): its model (the hand grab, or its volume's ray grips) or its panel (the title
    /// bar) flung away fast. The model flies on along the fling, shrinking, the panels fade, then the artifact closes.</item>
    /// <item>Its close chip (the volume's X, <see cref="ForgeVolumeChrome"/>), its panel's X, its course marker's X
    /// (<see cref="ArtifactCourseMarker"/>), and the right-hand close gesture while looking at it (<see cref="CloseLookedAt"/>):
    /// <see cref="Close"/>.</item>
    /// </list>
    /// Closing is the host's own <see cref="ArtifactHost.Close"/>, the same as artifact_close and Close everything: the model,
    /// the panels, the room course with its cones and gates go, and the truck or drone is put away (they hide on
    /// <see cref="ArtifactHost.Closed"/>). What closed is remembered for <see cref="UndoSeconds"/>, the time the Spaces list
    /// offers thrown panels back: "undo" (<c>artifact_undo_close</c>), the Spaces row, Albie's card or the reopen gesture
    /// opens it again where it was (the same saved course layout, the model where the hands left it). Albie says so.
    /// <para>
    /// The left-hand hide-all hides the open artifact too (<see cref="WindowStash"/>): the model, the room course and the truck
    /// or drone, and the show-all gesture brings them back, unless the artifact closed or changed meanwhile. A course with a
    /// scored run or a lap going on stays out (hiding it would ruin the run); its panels still hide.
    /// </para>
    /// </summary>
    public static class ArtifactDismiss
    {
        /// <summary>How long a closed artifact can be brought back (s): the same as a thrown panel.</summary>
        public const float UndoSeconds = PanelChrome.UndoSeconds;
        /// <summary>The throw: how long it flies (s), and how far (m per m/s of fling, clamped).</summary>
        public const float FlySeconds = 0.35f, FlyPerSpeed = 0.3f, FlyMin = 0.5f, FlyMax = 1.5f;
        const string CardPrefix = "device:artifact:", UndoAction = CardPrefix + "undo";

        /// <summary>What a hand closed, for the undo.</summary>
        public sealed class Record
        {
            public string id, title;
            public float at;
            public bool desk;
            public bool hasVolume;
            public Vector3 volumePosition;
            public Quaternion volumeRotation;
            public float volumeScale, yaw, tilt;
            public readonly List<Pose> panels = new List<Pose>();
        }

        static Record last;
        static float flyingSince = -100f;
        // A throw in flight; a flight cut off (the host switched off) stops counting after a second.
        static bool flying => Time.unscaledTime - flyingSince < FlySeconds + 1f;
        static readonly Stash stash = new Stash();
        static readonly List<ForgePanel> panelScratch = new List<ForgePanel>();
        static readonly List<ForgeVolume> volumeScratch = new List<ForgeVolume>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            last = null;
            flyingSince = -100f;
            stash.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            GestureActions.CloseLookedAtHook = CloseLookedAt;
            GestureActions.ReopenHook = ReopenByGesture;
            WindowStash.Register(stash);
            AlbieCards.Register(CardPrefix, OnCard);
            AlbieTools.Register("artifact_dismiss", new Dictionary<string, AlbieTools.Handler>
            {
                ["artifact_undo_close"] = UndoTool,
            });
        }

        /// <summary>The artifact a hand closed in the last <see cref="UndoSeconds"/>, or null.</summary>
        public static Record Last => last != null && Time.unscaledTime - last.at <= UndoSeconds ? last : null;

        /// <summary>True while a thrown artifact is still flying off (it closes when it lands).</summary>
        public static bool Flying => flying;

        /// <summary>No undo for what closed (Close everything, or the user said no).</summary>
        public static void Forget() => last = null;

        // ---------------------------------------------------------------- closing

        /// <summary>
        /// Closes the open artifact as a whole (model, panels, course, Thing), remembering it for the undo, and Albie says
        /// "Closed the heart. Say undo to bring it back." With nothing open (its Allow sheet, a refusal) it just closes.
        /// </summary>
        public static void Close(ArtifactHost host)
        {
            if (host == null) return;
            Record record = host.OpenId != null ? Capture(host) : null;
            host.Close();
            if (record == null) return;
            last = record;
            last.at = Time.unscaledTime;
            Say(record.title);
        }

        /// <summary>
        /// Thrown away by hand with <paramref name="velocity"/> (m/s, world): the model flies on that way, shrinking, its panels
        /// fade, then <see cref="Close"/>. Grabs stop at once. Outside Play mode it closes straight away.
        /// </summary>
        public static void Throw(ArtifactHost host, Vector3 velocity)
        {
            if (host == null || flying) return;
            if (host.OpenId == null || !Application.isPlaying || !host.isActiveAndEnabled)
            {
                Close(host);
                return;
            }
            Record record = Capture(host);
            host.StartCoroutine(Fly(host, record, velocity));
        }

        static IEnumerator Fly(ArtifactHost host, Record record, Vector3 velocity)
        {
            flyingSince = Time.unscaledTime;
            string id = host.OpenId;
            host.GetComponentsInChildren(false, volumeScratch);
            host.GetComponentsInChildren(false, panelScratch);
            var volumes = volumeScratch.ToArray();
            var panels = panelScratch.ToArray();  // once per throw, not per frame
            volumeScratch.Clear();
            panelScratch.Clear();
            var from = new Vector3[volumes.Length];
            var scales = new Vector3[volumes.Length];
            for (int i = 0; i < volumes.Length; i++)
            {
                StopHands(volumes[i]);
                from[i] = volumes[i].transform.position;
                scales[i] = volumes[i].transform.localScale;
            }
            var groups = new CanvasGroup[panels.Length];
            for (int i = 0; i < panels.Length; i++)
            {
                if (panels[i].Handles != null)
                {
                    panels[i].Handles.Cancel();
                    panels[i].Handles.Busy = true;
                }
                groups[i] = panels[i].GetComponent<CanvasGroup>();
                if (groups[i] == null) groups[i] = panels[i].gameObject.AddComponent<CanvasGroup>();
                groups[i].blocksRaycasts = false;
            }
            float speed = velocity.magnitude;
            Vector3 travel = speed > 1e-3f ? velocity / speed * Mathf.Clamp(speed * FlyPerSpeed, FlyMin, FlyMax) : Vector3.zero;
            for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime / FlySeconds)
            {
                if (host == null || host.OpenId != id) break;  // something else opened meanwhile: it isn't ours to close
                float k = Mathf.Clamp01(t);
                float move = ForgeTween.EaseOutCubic(k), shrink = Mathf.Lerp(1f, 0.04f, ForgeTween.EaseInCubic(k));
                for (int i = 0; i < volumes.Length; i++)
                {
                    if (volumes[i] == null) continue;
                    volumes[i].transform.position = from[i] + travel * move + Vector3.up * (0.12f * Mathf.Sin(k * Mathf.PI));
                    volumes[i].transform.localScale = scales[i] * shrink;
                }
                for (int i = 0; i < groups.Length; i++)
                    if (groups[i] != null) groups[i].alpha = 1f - Mathf.SmoothStep(0f, 1f, k);
                yield return null;
            }
            flyingSince = -100f;
            if (host == null || host.OpenId != id) yield break;
            host.Close();
            last = record;
            last.at = Time.unscaledTime;
            Say(record.title);
        }

        // The hands and the ray grips let go and stay off while it flies; its close chip goes.
        static void StopHands(ForgeVolume volume)
        {
            var hands = volume.GetComponent<ArtifactHandGrab>();
            if (hands != null) hands.enabled = false;
            var grips = volume.GetComponent<VolumeManipulator>();
            if (grips != null)
            {
                grips.Cancel();
                grips.Busy = true;
            }
            var chrome = volume.GetComponent<ForgeVolumeChrome>();
            if (chrome != null) chrome.SetVisible(false);
        }

        static Record Capture(ArtifactHost host)
        {
            var record = new Record { id = host.OpenId, title = host.OpenTitle, desk = host.OnDesk, at = Time.unscaledTime };
            var volume = host.GetComponentInChildren<ForgeVolume>(false);
            if (volume != null)
            {
                record.hasVolume = true;
                record.volumePosition = volume.transform.position;
                record.volumeRotation = volume.transform.rotation;
                record.volumeScale = volume.transform.localScale.x;
                record.yaw = volume.ViewYaw;
                record.tilt = volume.ViewTilt;
            }
            for (int i = 0; i < host.Spawned.Count; i++)
            {
                GameObject go = host.Spawned[i];
                if (go != null && go.transform.IsChildOf(host.transform) && go.GetComponent<ForgePanel>() != null)
                    record.panels.Add(new Pose(go.transform.position, go.transform.rotation));
            }
            return record;
        }

        // ---------------------------------------------------------------- undo

        /// <summary>
        /// Opens again what a hand closed in the last <see cref="UndoSeconds"/>, where it was. Null when it did, else why not
        /// (nothing to bring back, it's too late, another artifact is open now).
        /// </summary>
        public static string Undo()
        {
            Record record = Last;
            if (record == null) return last == null ? "Nothing was closed by hand to bring back." : "It's been too long to bring it back; open it again from the Artifacts tab.";
            ArtifactHost host = ArtifactHost.Current;
            if (host == null) return "There's no artifact host in this scene.";
            if (host.OpenId != null && host.OpenId != record.id) return "Another artifact is open now; close it first.";
            last = null;
            if (host.OpenId == null) host.Reopen(record.id, record.desk);
            if (host.OpenId != record.id) return host.LastProblem ?? "It didn't open again.";
            Restore(host, record);
            return null;
        }

        // The model where the hands left it, the panels where they were.
        static void Restore(ArtifactHost host, Record record)
        {
            if (record.hasVolume)
            {
                var volume = host.GetComponentInChildren<ForgeVolume>(false);
                if (volume != null)
                {
                    volume.transform.SetPositionAndRotation(record.volumePosition, record.volumeRotation);
                    volume.transform.localScale = Vector3.one * record.volumeScale;
                    volume.SetView(record.yaw, record.tilt);
                }
            }
            int p = 0;
            for (int i = 0; i < host.Spawned.Count && p < record.panels.Count; i++)
            {
                GameObject go = host.Spawned[i];
                if (go == null || !go.transform.IsChildOf(host.transform) || go.GetComponent<ForgePanel>() == null) continue;
                go.transform.SetPositionAndRotation(record.panels[p].position, record.panels[p].rotation);
                p++;
            }
        }

        static JObject UndoTool(JObject args)
        {
            string title = Last != null ? Last.title : null;
            string problem = Undo();
            if (problem != null) return AlbieTools.Error("nothing_to_undo: " + problem);
            return new JObject { ["reopened"] = ArtifactHost.Current != null ? ArtifactHost.Current.OpenId : null, ["say"] = The(title, true) + " is back." };
        }

        static string ReopenByGesture()
        {
            if (Last == null) return null;
            string title = Last.title;
            string problem = Undo();
            return problem ?? The(title, true) + " is back";
        }

        static bool OnCard(string actionId)
        {
            if (actionId != UndoAction) return true;
            string problem = Undo();
            if (problem != null) Debug.Log("[Artifacts] Undo: " + problem);
            return true;
        }

        // "Closed the heart. Say undo to bring it back." Voice on: Albie says it (and knows the undo tool). Voice off: the
        // caption, with a Bring it back button.
        static void Say(string title)
        {
            if (!Application.isPlaying) return;
            string line = "Closed " + The(title, false) + ". Say undo to bring it back.";
            Debug.Log("[Artifacts] " + line);
            try
            {
                if (ForgeNet.LocalPeer is AlbieVoice)
                    ForgeNet.Send("albie.say", new JObject
                    {
                        ["text"] = line + " (If they say undo or bring it back within " + (int)UndoSeconds + " seconds, call artifact_undo_close.)",
                    });
                else
                    ForgeNet.Inject("pet.say", new JObject
                    {
                        ["pet_id"] = "bit",
                        ["text"] = line,
                        ["card"] = new JObject
                        {
                            ["title"] = "Closed " + The(title, false),
                            ["actions"] = new JArray(new JObject { ["action_id"] = UndoAction, ["label"] = "Bring it back" }),
                        },
                    });
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>"the heart", or "The heart" at the start of a sentence; "it" with no title.</summary>
        public static string The(string title, bool start)
        {
            if (string.IsNullOrEmpty(title)) return start ? "It" : "it";
            string name = title.Length > 1 && char.IsUpper(title[0]) && !char.IsUpper(title[1]) ? char.ToLowerInvariant(title[0]) + title.Substring(1) : title;
            if (name.StartsWith("the ", System.StringComparison.OrdinalIgnoreCase)) name = name.Substring(4);
            return (start ? "The " : "the ") + name;
        }

        // ---------------------------------------------------------------- the close gesture

        /// <summary>
        /// The right-hand close gesture: when the eyes are on the open artifact (its model, nearer than any window they are on;
        /// its course marker; or one of its own panels) it closes as a whole and the hand says so. Null when it isn't looked at.
        /// </summary>
        public static string CloseLookedAt(Ray eyes, ForgePanel lookedAt)
        {
            ArtifactHost host = ArtifactHost.Current;
            if (host == null || host.OpenId == null || flying) return null;
            if (lookedAt != null && (lookedAt.transform.IsChildOf(host.transform) || lookedAt.GetComponent<ArtifactCourseMarker>() != null))
                return CloseFromGesture(host);
            float window = float.MaxValue;
            if (lookedAt != null && new Plane(lookedAt.transform.forward, lookedAt.transform.position).Raycast(eyes, out float d)) window = d;
            if (LooksAtModel(host, eyes, window) || LooksAtMarker(host, eyes, window)) return CloseFromGesture(host);
            return null;
        }

        static string CloseFromGesture(ArtifactHost host)
        {
            string title = host.OpenTitle;
            Close(host);
            return "Closed " + The(title, false);
        }

        /// <summary>A little around the model's box still counts as looking at it (m).</summary>
        public const float LookMargin = 0.06f;
        /// <summary>How far off the course marker's centre the eyes may be and still be on it (degrees).</summary>
        public const float MarkerLookDegrees = 8f;

        static bool LooksAtModel(ArtifactHost host, Ray eyes, float nearerThan)
        {
            for (int i = 0; i < ForgeVolume.All.Count; i++)
            {
                ForgeVolume v = ForgeVolume.All[i];
                if (v == null || !v.Closable || !v.transform.IsChildOf(host.transform)) continue;
                float scale = Mathf.Abs(v.transform.lossyScale.x);
                Vector3 size = v.size * scale;
                var box = new Bounds(v.transform.position + Vector3.up * (size.y * 0.5f), size + Vector3.one * (2f * LookMargin));
                if (box.IntersectRay(eyes, out float d) && d < nearerThan) return true;
            }
            return false;
        }

        static bool LooksAtMarker(ArtifactHost host, Ray eyes, float nearerThan)
        {
            ArtifactCourseMarker marker = ArtifactCourseMarker.Of(host);
            if (marker == null || !marker.isActiveAndEnabled) return false;
            Vector3 to = marker.Centre - eyes.origin;
            return to.magnitude < nearerThan && Vector3.Angle(eyes.direction, to) < MarkerLookDegrees;
        }

        // ---------------------------------------------------------------- hide all, show all

        /// <summary>What the hide-all gesture hid of the open artifact (checks read it).</summary>
        public static int HiddenCount => stash.HiddenCount;

        sealed class Stash : WindowStash.IStashable
        {
            const float HideSeconds = 0.18f, ShowSeconds = 0.28f, HiddenScale = 0.6f;
            string owner;
            ArtifactHost hooked;
            readonly List<GameObject> objects = new List<GameObject>();
            readonly List<Vector3> scales = new List<Vector3>();
            readonly List<Renderer> things = new List<Renderer>();  // the truck's or drone's look, switched off by Hide
            readonly List<Renderer> rendererScratch = new List<Renderer>();

            public void Clear()
            {
                owner = null;
                objects.Clear();
                scales.Clear();
                things.Clear();
                hooked = null;
            }

            public int HiddenCount
            {
                get
                {
                    ArtifactHost host = ArtifactHost.Current;
                    if (owner == null || host == null || host.OpenId != owner) return 0;
                    int n = 0;
                    for (int i = 0; i < objects.Count; i++) if (objects[i] != null && !objects[i].activeSelf) n++;
                    return n + (things.Count > 0 ? 1 : 0);
                }
            }

            public int Hide(MonoBehaviour runner)
            {
                ArtifactHost host = ArtifactHost.Current;
                if (host == null || host.OpenId == null || flying) return 0;
                Hook(host);
                if (owner != host.OpenId) Forget();
                owner = host.OpenId;
                int count = 0;
                // The model(s): shrink a touch and go.
                host.GetComponentsInChildren(false, volumeScratch);
                for (int i = 0; i < volumeScratch.Count; i++)
                {
                    ForgeVolume v = volumeScratch[i];
                    var grips = v.GetComponent<VolumeManipulator>();
                    if (grips != null) grips.Cancel();
                    var hands = v.GetComponent<ArtifactHandGrab>();
                    if (hands != null) hands.Core.Release(hands);
                    Remember(v.gameObject, runner, true);
                    count++;
                }
                volumeScratch.Clear();
                // The room course and its truck or drone, unless a run or a lap is going on.
                if (host.HasRoomCourse && !Busy(host))
                {
                    Transform course = host.ContentRoot;
                    if (course != null && !course.IsChildOf(host.transform) && course.gameObject.activeSelf)
                    {
                        Remember(course.gameObject, runner, false);
                        count++;
                    }
                    count += HideThings();
                }
                return count;
            }

            public int Show(MonoBehaviour runner)
            {
                ArtifactHost host = ArtifactHost.Current;
                int count = 0;
                bool same = owner != null && host != null && host.OpenId == owner;
                for (int i = 0; i < objects.Count; i++)
                {
                    GameObject go = objects[i];
                    if (go == null || go.activeSelf || !same) continue;  // gone, or the artifact changed: the host has its own now
                    go.SetActive(true);
                    if (go.GetComponent<ForgeVolume>() != null) Grow(go.transform, scales[i], runner);
                    count++;
                }
                if (RestoreThings() > 0) count++;
                owner = null;
                objects.Clear();
                scales.Clear();
                return count;
            }

            // The host rebuilt or closed: what was hidden of the old scene is the host's (or gone) now; the Thing's look comes back
            // as soon as the Thing is out again.
            void Hook(ArtifactHost host)
            {
                if (hooked == host) return;
                if (hooked != null)
                {
                    hooked.Shown -= OnHost;
                    hooked.Closed -= OnHost;
                }
                hooked = host;
                host.Shown += OnHost;
                host.Closed += OnHost;
            }

            void OnHost(ArtifactHost h) => Forget();

            void Forget()
            {
                owner = null;
                objects.Clear();
                scales.Clear();
                RestoreThings();
            }

            // A renderer whose Thing is out (not put away by its course closing) is switched back on; one whose Thing is put away
            // waits (its own show puts its look back, and this list catches the rest next time).
            int RestoreThings()
            {
                int restored = 0;
                for (int i = things.Count - 1; i >= 0; i--)
                {
                    Renderer r = things[i];
                    if (r == null) { things.RemoveAt(i); continue; }
                    if (ThingHidden(r)) continue;
                    r.enabled = true;
                    things.RemoveAt(i);
                    restored++;
                }
                return restored;
            }

            void Remember(GameObject go, MonoBehaviour runner, bool shrink)
            {
                int at = objects.IndexOf(go);
                if (at < 0)
                {
                    objects.Add(go);
                    scales.Add(go.transform.localScale);
                }
                if (!shrink || runner == null || !Application.isPlaying || !runner.isActiveAndEnabled)
                {
                    go.SetActive(false);
                    return;
                }
                Transform t = go.transform;
                Vector3 scale = t.localScale;
                runner.StartCoroutine(ForgeTween.Run(HideSeconds, 0f, k =>
                {
                    if (t != null) t.localScale = scale * Mathf.Lerp(1f, HiddenScale, ForgeTween.EaseInOutCubic(k));
                }, () =>
                {
                    if (t == null) return;
                    t.localScale = scale;
                    go.SetActive(false);
                }));
            }

            static void Grow(Transform t, Vector3 scale, MonoBehaviour runner)
            {
                if (runner == null || !Application.isPlaying || !runner.isActiveAndEnabled)
                {
                    t.localScale = scale;
                    return;
                }
                t.localScale = scale * HiddenScale;
                runner.StartCoroutine(ForgeTween.Run(ShowSeconds, 0f, k =>
                {
                    if (t != null) t.localScale = scale * Mathf.LerpUnclamped(HiddenScale, 1f, ForgeTween.SpringDamped(k, 0.8f, 1.4f));
                }, () => { if (t != null) t.localScale = scale; }));
            }

            // The truck's and the drone's look (their renderers only: they keep driving, sensing and being scored as before).
            int HideThings()
            {
                int before = things.Count;
                foreach (SlalomBrain brain in Object.FindObjectsByType<SlalomBrain>())
                    if (brain != null && !brain.Hidden && brain.isActiveAndEnabled) HideLook(brain.gameObject);
                ArtifactDrone drone = ArtifactDrone.Live;
                if (drone != null) HideLook(drone.gameObject);
                return things.Count > before ? 1 : 0;
            }

            void HideLook(GameObject go)
            {
                go.GetComponentsInChildren(false, rendererScratch);
                for (int i = 0; i < rendererScratch.Count; i++)
                {
                    Renderer r = rendererScratch[i];
                    if (r == null || !r.enabled) continue;
                    r.enabled = false;
                    things.Add(r);
                }
                rendererScratch.Clear();
            }

            static bool ThingHidden(Renderer r)
            {
                var brain = r.GetComponentInParent<SlalomBrain>();
                if (brain != null) return brain.Hidden;
                var drone = r.GetComponentInParent<ArtifactDrone>();
                return drone != null && drone.Hidden;
            }

            // A scored run, or the truck recording, training or driving itself: hiding the course now would ruin it.
            static bool Busy(ArtifactHost host)
            {
                if (host.Run != null && host.Run.State == ArtifactRunState.Running) return true;
                foreach (SlalomBrain brain in Object.FindObjectsByType<SlalomBrain>())
                    if (brain != null && !brain.Hidden && brain.State != SlalomBrain.Mode.Idle) return true;
                return false;
            }
        }
    }
}
