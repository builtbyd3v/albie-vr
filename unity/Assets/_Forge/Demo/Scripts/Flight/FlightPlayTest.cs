#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Demo
{
    /// <summary>
    /// The flight station's play test, run by Forge > Demo > Flight > Play Test in FlightSandbox.unity (desktop, no headset): fires the
    /// actions in script order through <see cref="DemoActions"/>, walks the head round the mock room, and saves screenshots to
    /// unity/Logs/demo/flight_*.png (main view, rendered by hand so it works with the Game view unfocused) and the drone-cam picture.
    /// Writes unity/Logs/demo/flight_playtest.txt and leaves Play mode when done. Editor only.
    /// </summary>
    public sealed class FlightPlayTest : MonoBehaviour
    {
        public const string PrefKey = "ForgeFlightPlayTest.auto";

        readonly HashSet<string> signals = new HashSet<string>();
        readonly StringBuilder log = new StringBuilder();
        string dir;
        Camera head;
        DemoDrone drone;
        bool failed;

        IEnumerator Start()
        {
            if (!UnityEditor.EditorPrefs.GetBool(PrefKey, false)) yield break;
            UnityEditor.EditorPrefs.SetBool(PrefKey, false);
            Application.runInBackground = true;
            dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "demo"));
            Directory.CreateDirectory(dir);
            head = Camera.main;
            drone = FindAnyObjectByType<DemoDrone>();
            DemoActions.Signal += OnSignal;
            yield return Run();
            DemoActions.Signal -= OnSignal;
            Finish();
        }

        void OnSignal(string s) { signals.Add(s); Say("signal " + s + " at " + Time.time.ToString("0.0") + "s"); }

        void Say(string line)
        {
            log.AppendLine(line);
            Debug.Log("[FlightPlayTest] " + line);
        }

        IEnumerator WaitFor(string signal, float timeout)
        {
            float t = 0f;
            while (!signals.Contains(signal) && t < timeout) { t += Time.deltaTime; yield return null; }
            if (!signals.Contains(signal)) { failed = true; Say("FAIL: '" + signal + "' never came within " + timeout.ToString("0") + "s"); }
        }

        IEnumerator Run()
        {
            yield return null;
            head.transform.SetPositionAndRotation(new Vector3(0f, 1.15f, 0f), Quaternion.identity);
            DemoLayout.Capture();
            DemoActions.Fire(DemoActions.Reset);
            yield return new WaitForSeconds(0.3f);

            // B2: open and build
            DemoActions.Fire("drone.open");
            yield return new WaitForSeconds(1.0f); Shot("01_build_early");
            yield return new WaitForSeconds(0.9f); Shot("02_build_mid");
            yield return WaitFor("drone.built", 10f);
            yield return new WaitForSeconds(0.3f); Shot("03_built");

            DemoActions.Fire("drone.takeoff");
            yield return new WaitForSeconds(1.0f); Shot("04_spinup");
            yield return new WaitForSeconds(0.8f); Shot("05_rising");
            yield return WaitFor("drone.hovering", 8f);
            yield return new WaitForSeconds(0.5f); Shot("06_hover");

            // B4: mount
            DemoActions.Fire("drone.mount_camera", new JObject { ["from"] = new JArray(-0.45f, 1.25f, 0.6f) });
            yield return new WaitForSeconds(0.45f); Shot("07_mount_arc");
            yield return WaitFor("drone.mounted", 5f);
            yield return new WaitForSeconds(0.5f); Shot("08_mounted");

            // B6: flight test, standing up and walking the room
            DemoActions.Fire("drone.follow_start", new JObject { ["distance"] = 2.0f });
            DemoActions.Fire("dronecam.show");
            yield return StandUp();
            yield return new WaitForSeconds(0.2f); Shot("09_scan");
            yield return new WaitForSeconds(0.9f); Shot("10_scan_late");
            yield return WaitFor("drone.following", 10f);

            var path = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(1.8f, 0f, 0.8f), new Vector3(2.4f, 0f, -0.9f), new Vector3(0.6f, 0f, -0.6f),
                new Vector3(-1.8f, 0f, -0.1f), new Vector3(-2.4f, 0f, 1.3f), new Vector3(0f, 0f, 1.6f), new Vector3(0f, 0f, 0f),
            };
            int shot = 0;
            minDist = 99f; maxDist = 0f; blocked = 0; samples = 0;
            yield return Walk(path, 0.9f, () =>
            {
                shot++;
                if (shot == 2 || shot == 5 || shot == 8 || shot == 11) Shot("11_follow_" + shot.ToString("00"));
                if (shot == 3 || shot == 7 || shot == 10) SaveCam("cam_" + shot.ToString("00"));
            }, 1.4f);

            Say("follow distance (m) min " + minDist.ToString("0.00") + ", max " + maxDist.ToString("0.00") + "; samples inside furniture clearance 0.2 m: " + blocked + " of " + samples);
            if (minDist < 1.0f) { failed = true; Say("FAIL: the drone came within 1.0 m of the head"); }

            // B7: stop, come home
            DemoActions.Fire("drone.follow_stop");
            yield return new WaitForSeconds(1.0f); Shot("12_returning");
            yield return WaitFor("drone.home", 10f);
            yield return new WaitForSeconds(0.6f); Shot("13_home");
            DemoActions.Fire("dronecam.hide");
            yield return new WaitForSeconds(0.6f);

            // Reset puts it back on the pad.
            DemoActions.Fire(DemoActions.Reset);
            yield return new WaitForSeconds(0.4f); Shot("14_reset");
            if (drone != null && drone.State != DemoDrone.Mode.Parked) { failed = true; Say("FAIL: reset did not park the drone"); }
        }

        IEnumerator StandUp()
        {
            Vector3 from = head.transform.position;
            Vector3 to = new Vector3(from.x, 1.65f, from.z);
            for (float t = 0f; t < 1f; t += Time.deltaTime)
            {
                head.transform.position = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t));
                yield return null;
            }
            head.transform.position = to;
        }

        /// <summary>Walks the head through the points (x, z) at the given speed, facing the way it walks, calling tick every interval seconds.</summary>
        IEnumerator Walk(Vector3[] pts, float speed, System.Action tick, float interval)
        {
            float next = Time.time + interval;
            float yaw = head.transform.eulerAngles.y, yawVel = 0f;
            for (int i = 1; i < pts.Length; i++)
            {
                Vector3 target = new Vector3(pts[i].x, head.transform.position.y, pts[i].z);
                while (Vector3.Distance(head.transform.position, target) > 0.05f)
                {
                    Vector3 d = target - head.transform.position; d.y = 0f;
                    head.transform.position += d.normalized * Mathf.Min(speed * Time.deltaTime, d.magnitude);
                    float want = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    yaw = Mathf.SmoothDampAngle(yaw, want, ref yawVel, 0.35f);
                    head.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                    Sample();
                    if (Time.time >= next) { next += interval; tick?.Invoke(); }
                    yield return null;
                }
            }
        }

        float minDist, maxDist;
        int blocked, samples;

        void Sample()
        {
            if (drone == null || drone.State != DemoDrone.Mode.Following) return;
            Vector3 d = drone.Drone.position - head.transform.position; d.y = 0f;
            minDist = Mathf.Min(minDist, d.magnitude);
            maxDist = Mathf.Max(maxDist, d.magnitude);
            samples++;
            if (drone.Room != null && !drone.Room.Free(drone.Drone.position, 0.2f)) blocked++;
        }

        void Shot(string name)
        {
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
            var prev = head.targetTexture;
            head.targetTexture = rt;
            head.Render();
            head.targetTexture = prev;
            Save(rt, Path.Combine(dir, "flight_" + name + ".png"));
            rt.Release();
            Destroy(rt);
            string where = drone != null ? drone.Drone.position.ToString("F2") : "?";
            Say("shot " + name + ": drone " + (drone != null ? drone.State.ToString() : "?") + " at " + where + ", head " + head.transform.position.ToString("F2"));
        }

        void SaveCam(string name)
        {
            var win = FindAnyObjectByType<DroneCamWindow>();
            if (win == null) return;
            win.SavePicture(Path.Combine(dir, "flight_" + name + ".png"));
            Say("cam " + name + ": confidence " + win.Confidence.ToString("0.00") + ", searching " + win.Searching + ", box " + win.BoxViewport);
            Shot(name + "_window");   // the window itself, as the user sees it
        }

        static void Save(RenderTexture rt, string path)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, t.EncodeToPNG());
            Destroy(t);
        }

        void Finish()
        {
            Say(failed ? "RESULT: FAILED" : "RESULT: OK");
            File.WriteAllText(Path.Combine(dir, "flight_playtest.txt"), log.ToString());
            var lockFile = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Temp", "demo-editor.lock"));
            try { if (File.Exists(lockFile) && File.ReadAllText(lockFile).Contains("demo-flight")) File.Delete(lockFile); } catch (IOException) { }
            UnityEditor.EditorApplication.isPlaying = false;
        }
    }
}
#endif
