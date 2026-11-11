namespace Jellyfin.Plugin.SubtitleFixer.Domain;

/// <summary>Item kinds the plugin cares about.</summary>
public enum NodeKind
{
    /// <summary>A TV or movie library (user view); see <see cref="LibraryNode.LibraryType"/>.</summary>
    Library,

    /// <summary>A series.</summary>
    Series,

    /// <summary>A season.</summary>
    Season,

    /// <summary>An episode.</summary>
    Episode,

    /// <summary>A movie (MOVIES-PLAN.md).</summary>
    Movie,

    /// <summary>Anything else (folder, music, ...). Never accepted.</summary>
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
/// <param name="LibraryType">For libraries: <c>tvshows</c> or <c>movies</c>.</param>
/// <param name="ProductionYear">For movies: the year.</param>
public sealed record LibraryNode(
    Guid Id,
    string Name,
    NodeKind Kind,
    Guid? SeriesId = null,
    string? SeriesName = null,
    Guid? SeasonId = null,
    int? IndexNumber = null,
    int? ParentIndexNumber = null,
    string? LibraryType = null,
    int? ProductionYear = null);

/// <summary>Everything eligibility needs about one video item (episode or movie).</summary>
/// <param name="Node">The episode or movie node.</param>
/// <param name="VideoPath">Path of the video file, or null when Jellyfin has none.</param>
/// <param name="AudioLanguages">Languages of the episode's audio streams, as Jellyfin reports them.</param>
/// <param name="Runtime">Length of the video as Jellyfin knows it, or null; scales the SubSync time limit (D34).</param>
public sealed record VideoFacts(LibraryNode Node, string? VideoPath, IReadOnlyList<string?> AudioLanguages, TimeSpan? Runtime = null);

/// <summary>A page of results.</summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">Items on this page.</param>
/// <param name="TotalCount">Total matching items.</param>
public sealed record Paged<T>(IReadOnlyList<T> Items, int TotalCount);
