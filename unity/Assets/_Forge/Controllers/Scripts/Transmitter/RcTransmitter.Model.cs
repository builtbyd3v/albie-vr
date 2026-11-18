using System.Collections.Generic;
using Forge.Artifacts;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Forge.Controllers
{
    // The model: built once from primitives in code. The parts that never move are merged into one mesh (a submesh per
    // material), each stick is one mesh, so the whole radio is about a dozen draw calls on shared materials and no textures.
    // Local frame: x right, y out of the face, z towards the antenna (away from the pilot), metres.
    public sealed partial class RcTransmitter
    {
        const int IgnoreRaycastLayer = 2;  // the Things' sensors, the room's raycasts and the rest test against the default mask
        const float FaceY = 0.0175f;                 // the face plate's top
        const float PivotY = FaceY + 0.002f;         // where a stick pivots
        const float CapTopY = PivotY + 0.025f;       // a centred stick's thumb pad
        const float ButtonTopY = FaceY + 0.006f;     // a button cap's top at rest
        const float ButtonTravel = 0.0025f;
        const float SwitchTopY = FaceY + 0.015f;
        const float SwitchDegrees = 28f;
        static readonly Vector2 PivotLeft = new Vector2(-0.05f, -0.012f), PivotRight = new Vector2(0.05f, -0.012f);  // (x, z)
        static readonly Vector2 ButtonLeft = new Vector2(-0.05f, 0.033f), ButtonRight = new Vector2(0.05f, 0.033f);
        static readonly Vector2 SwitchAt = new Vector2(0f, 0.038f);

        GameObject model;
        readonly Transform[] stickPivot = new Transform[2];
        readonly GameObject[] capCollider = new GameObject[2];
        readonly Transform[] buttonCap = new Transform[2];
        readonly GameObject[] controlCollider = new GameObject[3];
        Transform switchLever;
        Renderer led;
        Material ledGreen, ledAmber, ledRed, ledOff, ledFlash, padOn, padFlash;
        readonly Renderer[] thumbPad = new Renderer[2];
        TextMeshPro screen, stickLabelL, stickLabelR, buttonLabelA, buttonLabelB, modeLabel;
        AudioSource horn;
        BoxCollider bodyCollider;
        readonly Collider[] parts = new Collider[6];

        /// <summary>The colliders the mouse can point at (body, two stick caps, two buttons, the switch): <see cref="TransmitterRaycaster"/>.</summary>
        public Collider[] Parts => parts;

        static Vector2 StickPivot(int s) => s == 0 ? PivotLeft : PivotRight;
        static Vector2 ControlAt(int c) => c == ButtonA ? ButtonLeft : c == ButtonB ? ButtonRight : SwitchAt;
        static float ControlTopY(int c) => c == ModeSwitch ? SwitchTopY : ButtonTopY;

        static Mesh cube, cylinder, sphere, capsule;

        static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        static Mesh Primitive(PrimitiveType type)
        {
            switch (type)
            {
                case PrimitiveType.Cube: if (cube != null) return cube; break;
                case PrimitiveType.Cylinder: if (cylinder != null) return cylinder; break;
                case PrimitiveType.Sphere: if (sphere != null) return sphere; break;
                case PrimitiveType.Capsule: if (capsule != null) return capsule; break;
            }
            var go = GameObject.CreatePrimitive(type);
            Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Kill(go);
            switch (type)
            {
                case PrimitiveType.Cube: cube = mesh; break;
                case PrimitiveType.Cylinder: cylinder = mesh; break;
                case PrimitiveType.Sphere: sphere = mesh; break;
                case PrimitiveType.Capsule: capsule = mesh; break;
            }
            return mesh;
        }

        // Parts to merge: a primitive, where it sits (in the merged object's frame) and its material.
        sealed class Merge
        {
            readonly List<(Mesh mesh, Matrix4x4 m, Material mat)> parts = new List<(Mesh, Matrix4x4, Material)>();

            public void Add(PrimitiveType type, Vector3 at, Vector3 size, Material mat, Vector3 euler = default) =>
                parts.Add((Primitive(type), Matrix4x4.TRS(at, Quaternion.Euler(euler), size), mat));

            // One mesh with a submesh per material, on a new child of parent.
            public GameObject Build(string name, Transform parent)
            {
                var materials = new List<Material>();
                var groups = new List<List<CombineInstance>>();
                foreach (var p in parts)
                {
                    int i = materials.IndexOf(p.mat);
                    if (i < 0) { materials.Add(p.mat); groups.Add(new List<CombineInstance>()); i = materials.Count - 1; }
                    groups[i].Add(new CombineInstance { mesh = p.mesh, transform = p.m });
                }
                var subs = new CombineInstance[materials.Count];
                for (int i = 0; i < materials.Count; i++)
                {
                    var sub = new Mesh { name = name + " " + i };
                    sub.CombineMeshes(groups[i].ToArray(), true, true);
                    subs[i] = new CombineInstance { mesh = sub, transform = Matrix4x4.identity };
                }
                var mesh = new Mesh { name = name };
                mesh.CombineMeshes(subs, false, true);
                mesh.RecalculateBounds();
                foreach (var s in subs) Kill(s.mesh);
                var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
                go.layer = IgnoreRaycastLayer;
                go.transform.SetParent(parent, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterials = materials.ToArray();
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                return go;
            }
        }

        void BuildIfNeeded()
        {
            if (model != null) return;
            Material body = ArtifactLook.Material("#2B2F36"), face = ArtifactLook.Material("#3C434D"), grip = ArtifactLook.Material("#1C1F24");
            Material metal = ArtifactLook.Material("#C9CED6"), dark = ArtifactLook.Material("#121417"), accent = ArtifactLook.Material("#FF7A1A");
            Material blue = ArtifactLook.Material("#4A90E2"), glass = ArtifactLook.Material("#0B1712", true);
            ledGreen = ArtifactLook.Material("#34E07A", true);
            ledAmber = ArtifactLook.Material("#FFB020", true);
            ledRed = ArtifactLook.Material("#FF4040", true);
            ledOff = ArtifactLook.Material("#2A2F36");
            ledFlash = ArtifactLook.Material("#E8FFF0", true);
            padOn = ArtifactLook.Material("#3FCF86", true);
            padFlash = ArtifactLook.Material("#C8FFE0", true);

            model = new GameObject("Model");
            model.SetActive(false);  // the Interaction SDK's components are injected before they wake
            model.layer = IgnoreRaycastLayer;
            model.transform.SetParent(transform, false);

            // The body, the grips, the gimbal wells, the screen bezel, the antenna: one mesh.
            var shell = new Merge();
            shell.Add(PrimitiveType.Cube, Vector3.zero, new Vector3(0.18f, 0.032f, 0.105f), body);
            shell.Add(PrimitiveType.Cube, new Vector3(0f, FaceY - 0.001f, 0f), new Vector3(0.172f, 0.002f, 0.097f), face);
            shell.Add(PrimitiveType.Capsule, new Vector3(-0.072f, -0.03f, -0.03f), new Vector3(0.044f, 0.05f, 0.044f), grip, new Vector3(40f, 0f, 0f));
            shell.Add(PrimitiveType.Capsule, new Vector3(0.072f, -0.03f, -0.03f), new Vector3(0.044f, 0.05f, 0.044f), grip, new Vector3(40f, 0f, 0f));
            shell.Add(PrimitiveType.Cube, new Vector3(0f, 0.012f, 0.0525f), new Vector3(0.176f, 0.004f, 0.004f), accent);
            for (int s = 0; s < 2; s++)
            {
                Vector2 p = StickPivot(s);
                shell.Add(PrimitiveType.Cylinder, new Vector3(p.x, FaceY + 0.0006f, p.y), new Vector3(0.042f, 0.0012f, 0.042f), metal);
                shell.Add(PrimitiveType.Cylinder, new Vector3(p.x, FaceY + 0.0012f, p.y), new Vector3(0.036f, 0.0016f, 0.036f), dark);
                shell.Add(PrimitiveType.Sphere, new Vector3(p.x, PivotY, p.y), new Vector3(0.02f, 0.008f, 0.02f), grip);
                Vector2 b = ControlAt(s);
                shell.Add(PrimitiveType.Cylinder, new Vector3(b.x, FaceY + 0.0008f, b.y), new Vector3(0.021f, 0.0016f, 0.021f), dark);
            }
            shell.Add(PrimitiveType.Cube, new Vector3(0f, FaceY + 0.0015f, 0.004f), new Vector3(0.064f, 0.003f, 0.044f), dark);
            shell.Add(PrimitiveType.Cube, new Vector3(0f, FaceY + 0.0032f, 0.004f), new Vector3(0.058f, 0.0006f, 0.038f), glass);
            shell.Add(PrimitiveType.Cube, new Vector3(SwitchAt.x, FaceY + 0.002f, SwitchAt.y), new Vector3(0.026f, 0.004f, 0.012f), dark);
            shell.Add(PrimitiveType.Cylinder, new Vector3(0.07f, 0.03f, 0.068f), new Vector3(0.006f, 0.03f, 0.006f), metal, new Vector3(60f, 0f, 0f));
            shell.Add(PrimitiveType.Sphere, new Vector3(0.07f, 0.045f, 0.094f), new Vector3(0.01f, 0.01f, 0.01f), accent);
            shell.Build("Shell", model.transform);

            // The sticks: a pivot each (it tilts), with the shaft, the cap and its ribbed pad as one mesh.
            for (int s = 0; s < 2; s++)
            {
                Vector2 p = StickPivot(s);
                var pivot = new GameObject(s == 0 ? "Left stick" : "Right stick").transform;
                pivot.gameObject.layer = IgnoreRaycastLayer;
                pivot.SetParent(model.transform, false);
                pivot.localPosition = new Vector3(p.x, PivotY, p.y);
                var m = new Merge();
                m.Add(PrimitiveType.Cylinder, new Vector3(0f, 0.01f, 0f), new Vector3(0.0065f, 0.01f, 0.0065f), metal);
                m.Add(PrimitiveType.Cylinder, new Vector3(0f, 0.0215f, 0f), new Vector3(0.018f, 0.0035f, 0.018f), dark);
                m.Add(PrimitiveType.Cylinder, new Vector3(0f, 0.0252f, 0f), new Vector3(0.012f, 0.0006f, 0.012f), s == 0 ? accent : blue);
                m.Build("Stick", pivot);
                var cap = new GameObject("Cap");
                cap.layer = IgnoreRaycastLayer;
                cap.transform.SetParent(pivot, false);
                cap.transform.localPosition = new Vector3(0f, 0.021f, 0f);
                var sc = cap.AddComponent<SphereCollider>();
                sc.radius = 0.014f;
                sc.isTrigger = true;
                // The thumb pad: a thin glowing ring on the cap while a thumb is locked on it (it flashes as it locks).
                var pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pad.name = "Thumb pad";
                pad.layer = IgnoreRaycastLayer;
                Kill(pad.GetComponent<Collider>());
                pad.transform.SetParent(pivot, false);
                pad.transform.localPosition = new Vector3(0f, 0.0251f, 0f);
                pad.transform.localScale = new Vector3(0.0165f, 0.0003f, 0.0165f);
                var pr = pad.GetComponent<MeshRenderer>();
                pr.sharedMaterial = padOn;
                pr.shadowCastingMode = ShadowCastingMode.Off;
                pr.receiveShadows = false;
                pad.SetActive(false);
                thumbPad[s] = pr;
                stickPivot[s] = pivot;
                capCollider[s] = cap;
                parts[1 + s] = sc;
            }

            // Buttons A (left) and B (right): a cap that goes down when pressed.
            for (int b = 0; b < 2; b++)
            {
                Vector2 at = ControlAt(b);
                var button = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                button.name = b == 0 ? "Button A" : "Button B";
                button.layer = IgnoreRaycastLayer;
                Kill(button.GetComponent<Collider>());
                button.transform.SetParent(model.transform, false);
                button.transform.localPosition = new Vector3(at.x, ButtonTopY - 0.00125f, at.y);
                button.transform.localScale = new Vector3(0.016f, 0.00125f, 0.016f);
                var r = button.GetComponent<MeshRenderer>();
                r.sharedMaterial = b == 0 ? accent : blue;
                r.shadowCastingMode = ShadowCastingMode.Off;
                buttonCap[b] = button.transform;
                var hit = new GameObject("Press");
                hit.layer = IgnoreRaycastLayer;
                hit.transform.SetParent(model.transform, false);
                hit.transform.localPosition = new Vector3(at.x, ButtonTopY, at.y);
                var sc = hit.AddComponent<SphereCollider>();
                sc.radius = 0.012f;
                sc.isTrigger = true;
                controlCollider[b] = hit;
                parts[3 + b] = sc;
            }

            // The mode switch: a lever that flips left (easy / training speed) or right (pro / full speed).
            var lever = new GameObject("Mode switch").transform;
            lever.gameObject.layer = IgnoreRaycastLayer;
            lever.SetParent(model.transform, false);
            lever.localPosition = new Vector3(SwitchAt.x, FaceY + 0.003f, SwitchAt.y);
            var lm = new Merge();
            lm.Add(PrimitiveType.Cube, new Vector3(0f, 0.006f, 0f), new Vector3(0.004f, 0.012f, 0.004f), metal);
            lm.Add(PrimitiveType.Sphere, new Vector3(0f, 0.012f, 0f), new Vector3(0.007f, 0.007f, 0.007f), accent);
            lm.Build("Lever", lever);
            switchLever = lever;
            var sw = new GameObject("Flick");
            sw.layer = IgnoreRaycastLayer;
            sw.transform.SetParent(model.transform, false);
            sw.transform.localPosition = new Vector3(SwitchAt.x, SwitchTopY - 0.004f, SwitchAt.y);
            var swc = sw.AddComponent<SphereCollider>();
            swc.radius = 0.012f;
            swc.isTrigger = true;
            controlCollider[ModeSwitch] = sw;
            parts[5] = swc;

            // The link LED.
            var ledGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ledGo.name = "LED";
            ledGo.layer = IgnoreRaycastLayer;
            Kill(ledGo.GetComponent<Collider>());
            ledGo.transform.SetParent(model.transform, false);
            ledGo.transform.localPosition = new Vector3(0.026f, FaceY + 0.002f, 0.038f);
            ledGo.transform.localScale = new Vector3(0.006f, 0.004f, 0.006f);
            led = ledGo.GetComponent<MeshRenderer>();
            led.sharedMaterial = ledRed;
            led.shadowCastingMode = ShadowCastingMode.Off;

            // The body's grab and pointing box: the body and the grips.
            var bodyGo = new GameObject("Body");
            bodyGo.layer = IgnoreRaycastLayer;
            bodyGo.transform.SetParent(model.transform, false);
            bodyCollider = bodyGo.AddComponent<BoxCollider>();
            bodyCollider.center = GripBoxCentre;
            bodyCollider.size = GripBoxHalf * 2f;
            bodyCollider.isTrigger = true;
            parts[0] = bodyCollider;

            // Text: the screen and the labels, lying on the face, read from the pilot's side. Sizes in mm under a 1/1000 holder.
            var text = new GameObject("Text").transform;
            text.SetParent(model.transform, false);
            text.localPosition = Vector3.zero;
            text.localRotation = Quaternion.Euler(90f, 0f, 0f);
            text.localScale = Vector3.one * 0.001f;
            screen = Text(text, "Screen", new Vector2(0f, 4f), FaceY + 0.0040f, new Vector2(54f, 34f), 4.2f, TextAlignmentOptions.Center);
            screen.color = new Color(0.55f, 1f, 0.72f, 1f);
            screen.lineSpacing = -8f;
            stickLabelL = Text(text, "Left stick label", new Vector2(PivotLeft.x * 1000f, -37f), FaceY + 0.0004f, new Vector2(44f, 6f), 3.4f, TextAlignmentOptions.Center);
            stickLabelR = Text(text, "Right stick label", new Vector2(PivotRight.x * 1000f, -37f), FaceY + 0.0004f, new Vector2(44f, 6f), 3.4f, TextAlignmentOptions.Center);
            buttonLabelA = Text(text, "A label", new Vector2(ButtonLeft.x * 1000f, 18.5f), FaceY + 0.0004f, new Vector2(40f, 5f), 3f, TextAlignmentOptions.Center);
            buttonLabelB = Text(text, "B label", new Vector2(ButtonRight.x * 1000f, 18.5f), FaceY + 0.0004f, new Vector2(40f, 5f), 3f, TextAlignmentOptions.Center);
            modeLabel = Text(text, "Mode label", new Vector2(0f, 29.5f), FaceY + 0.0004f, new Vector2(36f, 5f), 2.6f, TextAlignmentOptions.Center);

            // The truck's horn: a two-tone beep made once.
            var hornGo = new GameObject("Horn");
            hornGo.transform.SetParent(transform, false);
            horn = hornGo.AddComponent<AudioSource>();
            horn.playOnAwake = false;
            horn.spatialBlend = 1f;
            horn.minDistance = 0.6f;
            horn.clip = HornClip();

            // The clicks: a soft low one when a thumb locks, a short high tick at the end of a stick's travel.
            clicker = hornGo.AddComponent<AudioSource>();
            clicker.playOnAwake = false;
            clicker.spatialBlend = 0.8f;
            clicker.minDistance = 0.4f;
            lockClip = ClickClip("Transmitter lock", 1400f, 0.018f);
            endClip = ClickClip("Transmitter end", 3200f, 0.008f);

            TransmitterIsdk.Add(this, model, bodyCollider);
            model.AddComponent<TransmitterRaycaster>().Init(this);
            model.SetActive(true);
            labelKey = int.MinValue;
            screenKey = int.MinValue;
        }

        // A TextMeshPro lying on the face at (x, z) mm, its top towards the antenna.
        static TextMeshPro Text(Transform holder, string name, Vector2 atMm, float y, Vector2 sizeMm, float heightMm, TextAlignmentOptions align)
        {
            var t = new GameObject(name, typeof(RectTransform)).AddComponent<TextMeshPro>();
            t.gameObject.layer = IgnoreRaycastLayer;
            t.transform.SetParent(holder, false);
            // Under the holder's 90 degree turn, its x is ours and its y is our z; its -z is our y (up out of the face).
            t.transform.localPosition = new Vector3(atMm.x, atMm.y, -y * 1000f);
            if (ForgeTheme.Font != null) t.font = ForgeTheme.Font;
            t.fontSize = heightMm * 10f;  // TextMeshPro (3D): 0.1 unit per point, and a unit is a mm here
            t.alignment = align;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Truncate;
            t.richText = true;
            t.rectTransform.sizeDelta = sizeMm;
            t.color = new Color(0.92f, 0.94f, 0.97f, 1f);
            t.text = "";
            var r = t.GetComponent<MeshRenderer>();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return t;
        }

        // A click: a sine burst with a fast attack and an exponential tail, plus a touch of noise for the "plastic".
        static AudioClip ClickClip(string name, float freq, float seconds)
        {
            const int rate = 22050;
            int n = Mathf.Max(8, (int)(rate * seconds));
            var data = new float[n];
            var rng = new System.Random(7);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float env = Mathf.Clamp01(t * 4000f) * Mathf.Exp(-t / (seconds * 0.25f));
                float noise = (float)rng.NextDouble() * 2f - 1f;
                data[i] = 0.5f * env * (0.8f * Mathf.Sin(2f * Mathf.PI * freq * t) + 0.2f * noise);
            }
            var clip = AudioClip.Create(name, n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        static AudioClip HornClip()
        {
            const int rate = 22050;
            const float seconds = 0.42f;
            int n = (int)(rate * seconds);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float f = t < 0.2f ? 466f : 392f;  // two beeps, high then low
                float gap = t > 0.18f && t < 0.22f ? 0f : 1f;
                float env = Mathf.Clamp01(t * 60f) * Mathf.Clamp01((seconds - t) * 30f) * gap;
                float w = Mathf.Sin(2f * Mathf.PI * f * t);
                data[i] = 0.35f * env * Mathf.Clamp(w * 3f, -1f, 1f);  // a soft square: a horn, not a flute
            }
            var clip = AudioClip.Create("Transmitter horn", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // ---- Per frame: put the moving parts where the values say (no allocations) ----------------------------------------

        void PlaceSticks()
        {
            float now = Time.unscaledTime;
            for (int s = 0; s < 2; s++)
            {
                if (stickPivot[s] == null) continue;
                Vector2 v = stick[s];
                stickPivot[s].localRotation = Quaternion.Euler(v.y * StickTiltDegrees, 0f, -v.x * StickTiltDegrees);
                // A thumb locking (or reaching the end) seats the cap: it dips a millimetre for a moment.
                bool seating = now < seatUntil[s];
                Vector3 lp = stickPivot[s].localPosition;
                float y = PivotY - (seating ? 0.0012f : 0f);
                if (lp.y != y) stickPivot[s].localPosition = new Vector3(lp.x, y, lp.z);
                var pad = thumbPad[s];
                if (pad == null) continue;
                bool on = locks[s].Locked;
                if (pad.gameObject.activeSelf != on) pad.gameObject.SetActive(on);
                Material m = seating ? padFlash : padOn;
                if (on && pad.sharedMaterial != m) pad.sharedMaterial = m;
            }
        }

        void PlaceButtons()
        {
            for (int b = 0; b < 2; b++)
            {
                if (buttonCap[b] == null) continue;
                bool down = pressed[b] || mousePressed[b];
                Vector3 p = buttonCap[b].localPosition;
                float y = ButtonTopY - 0.00125f - (down ? ButtonTravel : 0f);
                if (p.y != y) buttonCap[b].localPosition = new Vector3(p.x, y, p.z);
            }
            if (switchLever != null)
            {
                bool right = target == Target.Drone ? !EasySteering : FullSpeed;
                switchLever.localRotation = Quaternion.Euler(0f, 0f, right ? -SwitchDegrees : SwitchDegrees);
            }
        }

        void Led(float now)
        {
            if (led == null) return;
            Material m;
            if (now < ledFlashUntil) m = ledFlash;
            else if (target == Target.None) m = ledRed;
            else if (!Held) m = ledAmber;
            else
            {
                // Held and linked: steady green, blinking while it sends a stick.
                bool sending = stick[0] != Vector2.zero || stick[1] != Vector2.zero;
                m = sending && (Mathf.FloorToInt(now * 8f) & 1) == 0 ? ledOff : ledGreen;
            }
            if (led.sharedMaterial != m) led.sharedMaterial = m;
        }

        // ---- The screen and the labels: rebuilt only when what they say changes -----------------------------------------

        int labelKey = int.MinValue, screenKey = int.MinValue;

        void UpdateScreen(float now)
        {
            if (screen == null) return;
            var drone = target == Target.Drone ? ArtifactDrone.Live : null;
            int phase = drone != null ? (int)drone.Body.State : 0;
            bool easy = EasySteering, full = FullSpeed;

            int lk = (int)target * 64 + phase * 4 + (easy ? 2 : 0) + (full ? 1 : 0);
            if (lk != labelKey)
            {
                labelKey = lk;
                Labels(drone);
            }

            int state = StateCode(drone);
            int bars = LinkBars(drone);
            int held = HeldMask() + (mouseHeld ? 4 : 0);
            bool flashing = flash != null && now < flashUntil;
            int sk = unchecked(((((int)target * 31 + state) * 7 + bars) * 9 + held) * 2 + (easy ? 1 : 0)) * 2 + (full ? 1 : 0);
            sk = unchecked(sk * 131 + (flashing ? flashId + 1 : 0));
            if (sk == screenKey) return;
            screenKey = sk;

            string thing = target == Target.Drone ? "DRONE" : target == Target.None ? "NO THING" : "TRUCK";
            string mode = target == Target.Drone ? (easy ? "MODE 2 · EASY" : "MODE 2 · PRO")
                        : target == Target.None ? "OPEN A COURSE" : full ? "FULL SPEED" : "TRAINING SPEED";
            string line3 = flashing ? flash : StateText(drone, state);
            string link = target == Target.None ? "<color=#FF6060>NO LINK</color>"
                        : "LINK <color=#7CFF9A>" + Bars[bars] + "</color><color=#2E4038>" + Bars[4 - bars] + "</color>";
            string who = mouseHeld ? "MOUSE" : HeldMask() == 3 ? "BOTH HANDS" : HeldMask() != 0 ? "ONE HAND" : "GRAB ME";
            screen.text = "<b>" + thing + "</b>\n" + mode + "\n" + line3 + "\n<size=80%>" + link + "  " + who + "</size>";
        }

        static readonly string[] Bars = { "", "|", "||", "|||", "||||" };

        void Labels(ArtifactDrone drone)
        {
            switch (target)
            {
                case Target.Drone:
                    stickLabelL.text = "CLIMB · YAW";
                    stickLabelR.text = "PITCH · ROLL";
                    bool flying = drone != null && drone.Body.Airborne;
                    buttonLabelA.text = flying ? "LAND" : "TAKE OFF";
                    bool homeable = drone != null && drone.Body.State == DroneBody.Phase.Flying;
                    buttonLabelB.text = homeable ? "HOME" : "RESET";
                    modeLabel.text = "EASY   PRO";
                    break;
                case Target.Truck:
                case Target.OtherTruck:
                    stickLabelL.text = "THROTTLE";
                    stickLabelR.text = "STEER";
                    buttonLabelA.text = "HORN";
                    buttonLabelB.text = "RESET";
                    modeLabel.text = "TRAIN   FULL";
                    break;
                default:
                    stickLabelL.text = stickLabelR.text = "";
                    buttonLabelA.text = buttonLabelB.text = "";
                    modeLabel.text = "";
                    break;
            }
        }

        // What the Thing is doing, as a small number (so the screen key needs no string).
        int StateCode(ArtifactDrone drone)
        {
            if (target == Target.Drone)
            {
                if (drone == null) return 0;
                if (drone.Body.Returning) return 10;
                string f = drone.FollowState;
                if (f == "following" || f == "starting") return 11;
                return 1 + (int)drone.Body.State;
            }
            if (target == Target.Truck && brain != null)
            {
                if (brain.State == SlalomBrain.Mode.Lap) return 20 + Mathf.Min(brain.CompletedLaps, 9) * 10 + Mathf.Min(brain.LapTarget, 9) + 100;
                if (brain.State == SlalomBrain.Mode.Idle)
                    return brain.Body != null && brain.Body.Crashed ? 16 : freeDriving ? 17 : 12;
                return 12 + (int)brain.State;
            }
            return target == Target.OtherTruck ? 19 : 0;
        }

        string StateText(ArtifactDrone drone, int code)
        {
            if (target == Target.Drone)
            {
                if (drone == null) return "";
                if (code == 10) return "FLYING HOME";
                if (code == 11) return "FOLLOWING YOU";
                switch (drone.Body.State)
                {
                    case DroneBody.Phase.TakingOff: return "TAKING OFF";
                    case DroneBody.Phase.Flying: return "FLYING";
                    case DroneBody.Phase.Landing: return "LANDING";
                    case DroneBody.Phase.Crashed: return "CRASHED · PRESS RESET";
                    default: return "TAP TAKE OFF (A)";
                }
            }
            if (target == Target.Truck && brain != null)
            {
                switch (brain.State)
                {
                    case SlalomBrain.Mode.Lap:
                        int laps = brain.LapTarget;
                        return laps > 0 ? "LAP " + Mathf.Min(brain.CompletedLaps + 1, laps) + " OF " + laps : "RECORDING A LAP";
                    case SlalomBrain.Mode.Training: return "TRAINING THE BRAIN";
                    case SlalomBrain.Mode.Autopilot: return "AUTOPILOT DRIVING";
                    default:
                        if (brain.Body != null && brain.Body.Crashed) return "CRASHED · PRESS RESET";
                        return freeDriving ? "FREE DRIVE · NOT RECORDED" : "READY · GRAB TO DRIVE";
                }
            }
            return target == Target.OtherTruck ? "READY" : "TO DRIVE THE TRUCK";
        }

        // Link strength: how far the Thing is from the transmitter (it's simulated; this is just a readable cue).
        int LinkBars(ArtifactDrone drone)
        {
            if (target == Target.None) return 0;
            Vector3 at = drone != null ? drone.transform.position : brain != null && target == Target.Truck ? brain.transform.position : transform.position;
            float d = (at - transform.position).magnitude;
            return d < 2.5f ? 4 : d < 4.5f ? 3 : d < 7f ? 2 : 1;
        }
    }
}
