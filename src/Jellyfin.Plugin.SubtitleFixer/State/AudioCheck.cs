namespace Jellyfin.Plugin.SubtitleFixer.State;

/// <summary>
/// What SubSync's <c>detect</c> heard in an item whose audio isn't tagged (D49). Valid while the video's path, size
/// and modified time still match, like a <c>done</c> record (SPEC.md section 5): a replaced video is checked again.
/// </summary>
/// <param name="ItemId">Episode or movie.</param>
/// <param name="Video">The video when it was checked.</param>
/// <param name="Language">Whisper's language code (<c>en</c>, <c>da</c>), or null when no speech was found.</param>
/// <param name="Probability">0 to 1, or null.</param>
/// <param name="SpeechSeconds">Seconds of speech listened to.</param>
/// <param name="CheckedUtc">When.</param>
public sealed record AudioCheck(Guid ItemId, VideoFingerprint Video, string? Language, double? Probability, double SpeechSeconds, DateTime CheckedUtc)
{
    /// <summary>Below this, a check that heard English isn't trusted (Whisper's own share for the top language).</summary>
    public const double MinEnglishProbability = 0.5;

    /// <summary>Gets a value indicating whether the check heard English with enough certainty.</summary>
    public bool IsEnglish =>
        Probability is >= MinEnglishProbability
        && (string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase) || string.Equals(Language, "eng", StringComparison.OrdinalIgnoreCase));
}
