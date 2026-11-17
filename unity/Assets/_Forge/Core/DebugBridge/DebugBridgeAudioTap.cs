#if DEVELOPMENT_BUILD || UNITY_EDITOR
using UnityEngine;

/// <summary>
/// Records what the app plays: sits on the AudioListener's GameObject, where <c>OnAudioFilterRead</c> gets the final
/// mix. Writes a mono mixdown as PCM16 into a buffer allocated once in <see cref="Begin"/>; nothing is allocated per
/// callback. Stops writing when the buffer is full (the cap). Added and removed by <see cref="DebugBridgeAudio"/>.
/// </summary>
public class DebugBridgeAudioTap : MonoBehaviour
{
    public short[] Buffer { get; private set; }
    public int Rate { get; private set; }
    /// <summary>Samples written so far (audio thread writes, main thread reads).</summary>
    public int Count => count;
    public bool Full => Buffer != null && count >= Buffer.Length;
    /// <summary>Audio callbacks seen: 0 means the audio thread never ran (no audio device).</summary>
    public long Callbacks => callbacks;

    volatile int count;
    long callbacks;

    public void Begin(int rate, int maxSeconds)
    {
        Rate = rate;
        Buffer = new short[rate * maxSeconds];
        count = 0;
        callbacks = 0;
    }

    void OnAudioFilterRead(float[] data, int channels) => Push(data, channels);  // pass-through: data is left as it is

    /// <summary>Append one interleaved buffer (any thread). Public so a check can feed it without an audio device.</summary>
    public void Push(float[] data, int channels)
    {
        var buf = Buffer;
        if (buf == null || channels <= 0) return;
        callbacks++;
        int n = count;
        for (int i = 0; i + channels <= data.Length && n < buf.Length; i += channels)
        {
            float s = 0f;
            for (int c = 0; c < channels; c++) s += data[i + c];
            int v = (int)(s / channels * 32767f);
            buf[n++] = (short)(v > 32767 ? 32767 : v < -32767 ? -32767 : v);
        }
        count = n;
    }
}
#endif
