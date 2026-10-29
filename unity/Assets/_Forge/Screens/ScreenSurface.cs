using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.UI;

/// <summary>
/// Where a screen stream is drawn, for <see cref="ScreenReceiver"/> and <see cref="JpegScreenReceiver"/>:
/// a RawImage inside a <see cref="ForgePanel"/> (the Workspace's screen-main slot), or this object's
/// renderer (the quad in the sandboxes). Keeps the height and sets the width from the stream's aspect ratio.
/// Draws a mipmapped copy of the stream: a 1920 px frame lands on roughly 900 headset pixels, and without mipmaps
/// small text sparkles and crawls as the head moves. Trilinear + anisotropic keeps it steady at an angle too.
/// Until the first frame, a panel shows one calm state instead of a black picture: "Connect a computer to see its
/// screens" and how (the panel's empty state), worded for no computer or a computer that isn't sharing yet.
/// </summary>
public class ScreenSurface : MonoBehaviour
{
    [Tooltip("RawImage under the ForgePanel's Content. Empty = draw on this object's renderer.")]
    public RawImage image;

    [Tooltip("Keep the panel's height and set its width from the stream's aspect ratio.")]
    public bool fitAspect = true;

    [Tooltip("Copies per second of the stream into the mipmapped texture (the stream itself is up to 60 fps).")]
    public float copyRate = 60f;

    Texture source;
    RenderTexture mipped;
    float nextCopy;
    ForgePanel waitingOn;  // the panel showing the empty state, until the first frame
    int shownLive = -1;

    /// <summary>How many screens show a stream right now (Albie's screen_status).</summary>
    public static int Showing { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Showing = 0;

    void Awake()
    {
        // Resizing the panel by hand keeps the stream's shape too.
        if (image != null && fitAspect && TryGetComponent(out ForgePanel panel)) panel.KeepAspect = true;
        // A RawImage with no texture draws solid white: the panel flashed white until the first frame came in.
        if (image != null && image.texture == null) image.texture = Texture2D.blackTexture;
        if (image != null && TryGetComponent(out waitingOn)) image.enabled = false;
    }

    // After Workspace has set the panel's badge (the empty state takes its color).
    void Start() => Waiting();

    void Waiting()
    {
        if (waitingOn == null) return;
        bool live = ForgeNet.Status == ForgeNetStatus.Live;
        shownLive = live ? 1 : 0;
        waitingOn.ShowEmptyState("Connect a computer to see its screens", live
            ? "Your computer is connected but not sharing its screen. Start screen-bridge on it."
            : "Run Albie's hub and screen-bridge on a computer on the same Wi-Fi as this headset.");
    }

    // Called on the first frame and whenever the stream changes size.
    public void Show(Texture texture)
    {
        if (source == null) Showing++;
        source = texture;
        if (waitingOn != null)
        {
            var empty = waitingOn.Content.Find("EmptyState");
            if (empty != null) empty.gameObject.SetActive(false);
            image.enabled = true;
            waitingOn = null;
        }
        Debug.Log($"[ScreenSurface] Showing {texture.GetType().Name} {texture.width}x{texture.height}");
        if (mipped != null) Destroy(mipped);
        // RGBA8 either way (a JPEG loads as RGB24, which can't be rendered into), sRGB-ness as the source's so colors match.
        var format = GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat) ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
        mipped = new RenderTexture(texture.width, texture.height, 0, format)
        {
            useMipMap = true,
            autoGenerateMips = true,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 8,
            mipMapBias = -0.5f,  // the panel minifies about 2x: lean to the sharper mip, text stays crisp without crawling
        };
        nextCopy = 0f;
        LateUpdate();

        float aspect = (float)texture.width / texture.height;
        if (image != null)
        {
            image.texture = mipped;
            if (fitAspect && TryGetComponent(out ForgePanel panel))
                panel.SizeMeters = new Vector2(panel.SizeMeters.y * aspect, panel.SizeMeters.y);
        }
        else
        {
            GetComponent<Renderer>().material.mainTexture = mipped;
            if (fitAspect)
            {
                var s = transform.localScale;
                transform.localScale = new Vector3(s.y * aspect, s.y, s.z);
            }
        }
    }

    // Both receivers update `source` in place, so copy it on a clock rather than per frame received.
    void LateUpdate()
    {
        if (waitingOn != null && shownLive != (ForgeNet.Status == ForgeNetStatus.Live ? 1 : 0)) Waiting();
        if (source == null || Time.unscaledTime < nextCopy) return;
        nextCopy = Mathf.Max(nextCopy + 1f / copyRate, Time.unscaledTime); // fixed cadence: now + 1/rate skipped every other 30 fps frame at 72 Hz
        Graphics.Blit(source, mipped);
    }

    void OnDestroy()
    {
        if (source != null) Showing--;
        if (mipped != null) Destroy(mipped);
    }
}
