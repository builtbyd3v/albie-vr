using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// The OS's few sounds (press, open, close, error), played from the Interaction SDK sample clips the kit points at,
    /// through one AudioSource on the shell. Silent and safe when there is no shell, no kit or no clip.
    /// </summary>
    public static class OSSounds
    {
        const float Volume = 0.5f;

        static AudioSource source;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => source = null;

        public static void Open() => Play(OSKit.Current != null ? OSKit.Current.openClip : null, 0.8f);
        public static void Close() => Play(OSKit.Current != null ? OSKit.Current.closeClip : null, 0.7f);
        public static void Press() => Play(OSKit.Current != null ? OSKit.Current.pressClip : null, 1f);
        public static void Error() => Play(OSKit.Current != null ? OSKit.Current.errorClip : null, 0.8f);

        static void Play(AudioClip clip, float scale)
        {
            if (clip == null) return;
            if (source == null)
            {
                OSShell shell = OSShell.Current;
                if (shell == null) return;
                source = shell.GetComponent<AudioSource>();
                if (source == null) source = shell.gameObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 0f;
            }
            source.PlayOneShot(clip, Volume * scale);
        }
    }
}
