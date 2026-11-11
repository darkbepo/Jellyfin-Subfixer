namespace Jellyfin.Plugin.SubtitleFixer.Bazarr;

/// <summary>Why a Bazarr call failed.</summary>
public enum BazarrFailure
{
    /// <summary>Bazarr couldn't be reached, timed out, or answered with something that isn't Bazarr's JSON.</summary>
    Unavailable,

    /// <summary>Bazarr rejected the API key.</summary>
    Unauthorized,

    /// <summary>Bazarr doesn't know the item (HTTP 404).</summary>
    NotFound,

    /// <summary>Bazarr answered with an error of its own, for example "Movie file not found. Path mapping issue?" (HTTP 500).</summary>
    Failed,
}

/// <summary>Outcome of a Bazarr call: a value, or a failure with a message for people.</summary>
/// <typeparam name="T">Value type.</typeparam>
/// <param name="Value">Value when the call worked.</param>
/// <param name="Failure">Failure kind, or null when the call worked.</param>
/// <param name="Message">What went wrong. Never contains the key.</param>
public sealed record BazarrResult<T>(T? Value, BazarrFailure? Failure, string? Message)
{
    /// <summary>Gets a value indicating whether the call worked.</summary>
    public bool Ok => Failure is null;

    /// <summary>A successful result.</summary>
    /// <param name="value">Value.</param>
    /// <returns>The result.</returns>
    public static BazarrResult<T> Success(T value) => new(value, null, null);

    /// <summary>A failed result.</summary>
    /// <param name="failure">Kind.</param>
    /// <param name="message">Message.</param>
    /// <returns>The result.</returns>
    public static BazarrResult<T> Fail(BazarrFailure failure, string message) => new(default, failure, message);
}

/// <summary>A page of a Bazarr list (<c>{ data: [...], total }</c>).</summary>
/// <typeparam name="T">Row type.</typeparam>
/// <param name="Items">Rows.</param>
/// <param name="Total">Rows in the whole list.</param>
public sealed record BazarrPage<T>(IReadOnlyList<T> Items, int Total);

/// <summary>A movie row from <c>GET /api/movies</c> (only the fields we use).</summary>
/// <param name="RadarrId">Radarr id, Bazarr's key for the movie.</param>
/// <param name="Path">Video path as Bazarr reports it.</param>
/// <param name="ImdbId">IMDb id, or null.</param>
/// <param name="Title">Title.</param>
public sealed record BazarrMovie(int RadarrId, string? Path, string? ImdbId, string? Title);

/// <summary>A series row from <c>GET /api/series</c> (only the fields we use).</summary>
/// <param name="SonarrSeriesId">Sonarr series id.</param>
/// <param name="Path">Series folder as Bazarr reports it.</param>
/// <param name="TvdbId">TVDB id, or null.</param>
/// <param name="ImdbId">IMDb id, or null.</param>
/// <param name="Title">Title.</param>
public sealed record BazarrSeries(int SonarrSeriesId, string? Path, int? TvdbId, string? ImdbId, string? Title);

/// <summary>An episode row from <c>GET /api/episodes</c> (only the fields we use).</summary>
/// <param name="SonarrEpisodeId">Sonarr episode id, Bazarr's key for the episode.</param>
/// <param name="SonarrSeriesId">Sonarr series id.</param>
/// <param name="Season">Season number.</param>
/// <param name="Episode">Episode number.</param>
/// <param name="Path">Video path as Bazarr reports it.</param>
public sealed record BazarrEpisode(int SonarrEpisodeId, int SonarrSeriesId, int? Season, int? Episode, string? Path);

/// <summary>One manual-search result from <c>GET /api/providers/movies</c> or <c>/episodes</c>.</summary>
/// <param name="Provider">Provider name.</param>
/// <param name="Subtitle">Bazarr's opaque handle, sent back when downloading. Never sent to the browser.</param>
/// <param name="Language">Language code, for example <c>en</c>.</param>
/// <param name="HearingImpaired">Hearing-impaired subtitle.</param>
/// <param name="Forced">Forced (foreign parts only) subtitle.</param>
/// <param name="OriginalFormat">Bazarr's <c>original_format</c> flag, sent back when downloading.</param>
/// <param name="Score">Match score in percent.</param>
/// <param name="Releases">Release names.</param>
/// <param name="Uploader">Uploader, or null.</param>
/// <param name="Matches">Properties that match the file.</param>
/// <param name="DontMatches">Properties that don't.</param>
public sealed record BazarrSearchResult(
    string Provider,
    string Subtitle,
    string Language,
    bool HearingImpaired,
    bool Forced,
    bool OriginalFormat,
    int Score,
    IReadOnlyList<string> Releases,
    string? Uploader,
    IReadOnlyList<string> Matches,
    IReadOnlyList<string> DontMatches);
