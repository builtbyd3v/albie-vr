using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// The drone's look, built in code as the brain truck's shell is: a quadcopter about <see cref="Span"/> across made
    /// of primitives with no colliders (so no ray and no sweep sees the drone itself). A centre body, two crossed arms,
    /// four motors on feet, four two-blade props on pivots, and a nose block; the nose and the front props take the
    /// accent colour so the pilot can tell which way it faces. Three shared materials for the 16 parts. Local frame: +Z is
    /// the nose, +Y is up, the origin is the body's centre and the feet reach <see cref="DroneBody.restHeight"/> below it.
    /// </summary>
    public static class DroneShell
    {
        /// <summary>Motor to motor across the diagonal plus the props, metres: about the body's swept sphere.</summary>
        public const float Span = 0.28f;
        const float Arm = 0.095f;  // each motor's distance from the centre along x and z

        /// <summary>
        /// Builds the shell under <paramref name="parent"/> and returns the four prop pivots (front left, front right,
        /// back right, back left: neighbours turn opposite ways). A null material leaves the primitive's own.
        /// </summary>
        public static Transform[] Build(Transform parent, Material body, Material accent, Material prop, float feet = 0.05f)
        {
            Part(parent, "Body", PrimitiveType.Cube, new Vector3(0f, 0f, 0f), new Vector3(0.07f, 0.028f, 0.12f), 0f, body);
            Part(parent, "Nose", PrimitiveType.Cube, new Vector3(0f, 0.004f, 0.066f), new Vector3(0.04f, 0.02f, 0.016f), 0f, accent);
            float armLength = Arm * 2f * 1.4142f;
            Part(parent, "Arm a", PrimitiveType.Cube, Vector3.zero, new Vector3(armLength, 0.01f, 0.02f), 45f, body);
            Part(parent, "Arm b", PrimitiveType.Cube, Vector3.zero, new Vector3(armLength, 0.01f, 0.02f), -45f, body);

            var pivots = new Transform[4];
            for (int i = 0; i < 4; i++)
            {
                // 0 front left, 1 front right, 2 back right, 3 back left.
                float x = i == 0 || i == 3 ? -Arm : Arm;
                float z = i < 2 ? Arm : -Arm;
                bool front = i < 2;
                Part(parent, "Motor " + i, PrimitiveType.Cylinder, new Vector3(x, 0.008f, z), new Vector3(0.026f, 0.012f, 0.026f), 0f, body);
                float foot = Mathf.Max(0.01f, feet);
                Part(parent, "Foot " + i, PrimitiveType.Cube, new Vector3(x, -foot * 0.5f, z), new Vector3(0.008f, foot, 0.008f), 0f, body);
                var pivot = new GameObject("Prop " + i).transform;
                pivot.SetParent(parent, false);
                pivot.localPosition = new Vector3(x, 0.024f, z);
                pivot.localRotation = Quaternion.Euler(0f, i * 37f, 0f);  // not all in line at rest
                Part(pivot, "Blade", PrimitiveType.Cube, Vector3.zero, new Vector3(0.115f, 0.003f, 0.014f), 0f, front ? accent : prop);
                pivots[i] = pivot;
            }
            return pivots;
        }

        static void Part(Transform parent, string name, PrimitiveType type, Vector3 at, Vector3 size, float yawDeg, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;  // gone from physics now; Destroy only takes it away at the end of the frame
                Object.Destroy(collider);
            }
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localRotation = Quaternion.Euler(0f, yawDeg, 0f);
            go.transform.localScale = size;
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return;
            if (material != null) renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }
}
