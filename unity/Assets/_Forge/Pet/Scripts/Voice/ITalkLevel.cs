namespace Forge.Pet
{
    /// <summary>Loudness of whatever Albie is saying right now (drives AlbieTalkAnimator).</summary>
    public interface ITalkLevel
    {
        /// <summary>Smoothed loudness 0..1 (0 when silent).</summary>
        float Level { get; }
        bool IsTalking { get; }
    }
}
