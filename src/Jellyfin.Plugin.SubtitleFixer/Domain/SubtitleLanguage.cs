namespace Jellyfin.Plugin.SubtitleFixer.Domain;

/// <summary>
/// The subtitle languages the plugin re-times (D50): English, and since SubSync 1.5.0 Danish. Both only on English
/// audio. Codes are the two-letter ones SubSync and the API use.
/// </summary>
public static class SubtitleLanguage
{
    /// <summary>English.</summary>
    public const string English = "en";

    /// <summary>Danish.</summary>
    public const string Danish = "da";

    /// <summary>SubSync version that syncs Danish subtitles (1.5.0).</summary>
    public static readonly Version DanishFrom = new(1, 5, 0);

    /// <summary>Gets every language, English first (the order items of one episode are queued in).</summary>
    public static IReadOnlyList<string> All { get; } = [English, Danish];

    /// <summary>
    /// Sidecar tags of a language in priority order; only the first that exists is processed (one subtitle per item
    /// and language). Plain before hearing-impaired, two letters before three (D6, D50).
    /// </summary>
    /// <param name="language">Language code.</param>
    /// <returns>The tags.</returns>
    public static IReadOnlyList<string> Tags(string language) => Normalize(language) == Danish
        ? ["da", "dan", "da.hi", "dan.hi"]
        : ["en", "eng", "en.hi", "eng.hi"];

    /// <summary>Gets the supported code for a value from a request, or null when it isn't one.</summary>
    /// <param name="value">Value, any case.</param>
    /// <returns><see cref="English"/>, <see cref="Danish"/> or null.</returns>
    public static string? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "en" or "eng" or "english" => English,
        "da" or "dan" or "danish" => Danish,
        _ => null,
    };

    /// <summary>Gets the code, English for anything unknown (old records and requests have none).</summary>
    /// <param name="value">Value.</param>
    /// <returns>The code.</returns>
    public static string Normalize(string? value) => Parse(value) ?? English;

    /// <summary>Gets the name shown to people.</summary>
    /// <param name="language">Language code.</param>
    /// <returns>"English" or "Danish".</returns>
    public static string Name(string language) => Normalize(language) == Danish ? "Danish" : "English";

    /// <summary>Whether a SubSync version can sync subtitles of this language.</summary>
    /// <param name="language">Language code.</param>
    /// <param name="version">Installed SubSync version, or null when unknown.</param>
    /// <returns>True for English always; for Danish from 1.5.0.</returns>
    public static bool Supported(string language, Version? version) =>
        Normalize(language) == English || (version is not null && version >= DanishFrom);
}
