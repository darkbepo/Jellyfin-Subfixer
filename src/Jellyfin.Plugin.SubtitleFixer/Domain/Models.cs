namespace Jellyfin.Plugin.SubtitleFixer.Domain;

/// <summary>Item kinds the plugin cares about.</summary>
public enum NodeKind
{
    /// <summary>A TV library (user view).</summary>
    Library,

    /// <summary>A series.</summary>
    Series,

    /// <summary>A season.</summary>
    Season,

    /// <summary>An episode.</summary>
    Episode,

    /// <summary>Anything else (movie, folder, ...). Never accepted.</summary>
    Other,
}

/// <summary>A library item as the plugin sees it, independent of Jellyfin types.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Kind">Item kind.</param>
/// <param name="SeriesId">Owning series, for seasons and episodes.</param>
/// <param name="SeriesName">Owning series name.</param>
/// <param name="SeasonId">Owning season, for episodes.</param>
/// <param name="IndexNumber">Season or episode number.</param>
/// <param name="ParentIndexNumber">Season number, for episodes.</param>
public sealed record LibraryNode(
    Guid Id,
    string Name,
    NodeKind Kind,
    Guid? SeriesId = null,
    string? SeriesName = null,
    Guid? SeasonId = null,
    int? IndexNumber = null,
    int? ParentIndexNumber = null);

/// <summary>Everything eligibility needs about one episode.</summary>
/// <param name="Node">The episode node.</param>
/// <param name="VideoPath">Path of the video file, or null when Jellyfin has none.</param>
/// <param name="AudioLanguages">Languages of the episode's audio streams, as Jellyfin reports them.</param>
public sealed record EpisodeFacts(LibraryNode Node, string? VideoPath, IReadOnlyList<string?> AudioLanguages);

/// <summary>A page of results.</summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">Items on this page.</param>
/// <param name="TotalCount">Total matching items.</param>
public sealed record Paged<T>(IReadOnlyList<T> Items, int TotalCount);
