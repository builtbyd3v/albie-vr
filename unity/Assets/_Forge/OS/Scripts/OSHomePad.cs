using System.Collections.Generic;
using Forge.Pet;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// The small round pad on the desk that an agent stands on while it is brought home at a desk (see
    /// <see cref="OSAgentHome"/>). It is a flat disc in the agent's own colour, lying just above the desk top at the agent's
    /// <see cref="AlbieCrewMember.HomeSpot"/>. It makes itself: <see cref="ShowFor"/> adds one for a member, and the pad
    /// removes itself as soon as the member is gone, leaving, sent out again or hidden. Nothing here runs per frame beyond
    /// a few comparisons. It has no collider, so it never blocks the agents or the hands.
    /// </summary>
    public sealed class OSHomePad : MonoBehaviour
    {
        const float Diameter = 0.09f;   // m
        const float Lift = 0.003f;      // m above the desk top

        static readonly List<OSHomePad> all = new List<OSHomePad>();
        static Sprite discSprite;
        static Material discMaterial;

        AlbieCrewMember member;
        SpriteRenderer sprite;
        Vector3 placedAt;

        /// <summary>How many pads exist right now.</summary>
        public static int Count => all.Count;

        /// <summary>The member this pad belongs to.</summary>
        public AlbieCrewMember Member => member;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            all.Clear();
            discSprite = null;
            discMaterial = null;
        }

        /// <summary>The pad of this member, or null.</summary>
        public static OSHomePad Find(AlbieCrewMember who)
        {
            if (who == null) return null;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].member == who) return all[i];
            return null;
        }

        /// <summary>Makes the pad for a member that is home at a desk (or moves the one it has). Null when it has no home spot.</summary>
        public static OSHomePad ShowFor(AlbieCrewMember who)
        {
            if (who == null || !who.HomeSpot.HasValue) return null;
            OSHomePad pad = Find(who);
            if (pad == null)
            {
                var go = new GameObject("OSHomePad " + who.PetId);
                pad = go.AddComponent<OSHomePad>();
                pad.member = who;
                pad.Build();
            }
            pad.Place();
            return pad;
        }

        void OnEnable()
        {
            if (!all.Contains(this)) all.Add(this);
        }

        void OnDisable() => all.Remove(this);

        void Build()
        {
            sprite = gameObject.AddComponent<SpriteRenderer>();
            sprite.sprite = DiscSprite();
            Material material = DiscMaterial();
            if (material != null) sprite.sharedMaterial = material;
            sprite.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sprite.receiveShadows = false;
            Color c = member != null && member.Type != null ? member.Type.Color : Color.white;
            sprite.color = new Color(c.r, c.g, c.b, 0.95f);
        }

        void Place()
        {
            if (member == null || !member.HomeSpot.HasValue) return;
            placedAt = member.HomeSpot.Value;
            // A sprite faces -Z; turned 90 degrees about X it lies flat and faces up.
            transform.SetPositionAndRotation(placedAt + Vector3.up * Lift, Quaternion.Euler(90f, 0f, 0f));
        }

        void Update()
        {
            if (member == null || member.Leaving || !member.Home || !member.HomeSpot.HasValue || member.BodyHidden)
            {
                Destroy(gameObject);
                return;
            }
            if ((member.HomeSpot.Value - placedAt).sqrMagnitude > 1e-6f) Place();
        }

        // One soft disc with a brighter rim, made once and shared by every pad.
        static Sprite DiscSprite()
        {
            if (discSprite != null) return discSprite;
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "OSHomePad",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - half, dy = y + 0.5f - half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) / half;  // 0 centre, 1 rim
                    float fill = d <= 1f ? 0.45f : 0f;
                    float rim = Mathf.Clamp01(1f - Mathf.Abs(d - 0.9f) / 0.1f);
                    float alpha = Mathf.Clamp01(Mathf.Max(fill * Mathf.Clamp01((1f - d) * 12f + 0.15f), rim));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            discSprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size / Diameter, 0, SpriteMeshType.FullRect);
            discSprite.name = "OSHomePad";
            discSprite.hideFlags = HideFlags.HideAndDontSave;
            return discSprite;
        }

        // The same material the crew egg's picker panel uses (AlbieHatchery.BuildPanel): Sprites/Default, drawn late.
        static Material DiscMaterial()
        {
            if (discMaterial != null) return discMaterial;
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) return null;
            discMaterial = new Material(shader) { name = "OSHomePad", hideFlags = HideFlags.HideAndDontSave, renderQueue = 2990 };
            return discMaterial;
        }
    }
}
