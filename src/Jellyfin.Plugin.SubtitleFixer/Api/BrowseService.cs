using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.State;

namespace Jellyfin.Plugin.SubtitleFixer.Api;

/// <summary>Builds Browse and Search responses (API.md). Paged; eligibility only for the rows on the page.</summary>
public sealed class BrowseService
{
    private readonly ILibraryFacade _library;
    private readonly IStateStore _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="BrowseService"/> class.
    /// </summary>
    /// <param name="library">Library facade.</param>
    /// <param name="state">State store.</param>
    public BrowseService(ILibraryFacade library, IStateStore state)
    {
        _library = library;
        _state = state;
    }

    /// <summary>Lists TV libraries, or one parent's children.</summary>
    /// <param name="userId">User.</param>
    /// <param name="parentId">Parent, or null for libraries.</param>
    /// <param name="startIndex">First item.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, or null when the parent is unknown or not accessible.</returns>
    public async Task<PageDto?> BrowseAsync(Guid userId, Guid? parentId, int startIndex, int limit, CancellationToken cancellationToken)
    {
        if (parentId is null || parentId == Guid.Empty)
        {
            var libraries = _library.GetLibraries(userId);
            return new PageDto(null, libraries.Count, libraries.Skip(startIndex).Take(limit).Select(Plain).ToList());
        }

        var parentNode = _library.GetNode(userId, parentId.Value);
        if (parentNode is null)
        {
            return null;
        }

        var parent = new ParentDto(
            parentNode.Id,
            parentNode.Name,
            TypeName(parentNode.Kind),
            _library.GetBreadcrumb(userId, parentNode.Id).Select(n => new CrumbDto(n.Id, n.Name, TypeName(n.Kind))).ToList());

        if (parentNode.Kind == NodeKind.Season)
        {
            var all = _library.GetSeasonEpisodes(userId, parentNode.Id);
            var page = all.Skip(startIndex).Take(limit).ToList();
            var items = await EpisodesAsync(page, cancellationToken).ConfigureAwait(false);
            return new PageDto(parent, all.Count, items);
        }

        var children = _library.GetChildren(userId, parentNode.Id, startIndex, limit);
        if (children is null)
        {
            return null;
        }

        // A movie library page: each movie row carries its eligibility, like an episode.
        var videoRows = await VideoRowsAsync(userId, children.Items, cancellationToken).ConfigureAwait(false);
        var rows = new List<ItemDto>(children.Items.Count);
        foreach (var child in children.Items)
        {
            rows.Add(child.Kind == NodeKind.Season
                ? await SeasonAsync(userId, child, cancellationToken).ConfigureAwait(false)
                : videoRows.TryGetValue(child.Id, out var video) ? video : Plain(child));
        }

        return new PageDto(parent, children.TotalCount, rows);
    }

    /// <summary>Searches series and episodes.</summary>
    /// <param name="userId">User.</param>
    /// <param name="term">Search term.</param>
    /// <param name="startIndex">First item.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    public async Task<PageDto> SearchAsync(Guid userId, string term, int startIndex, int limit, CancellationToken cancellationToken)
    {
        var result = _library.Search(userId, term, startIndex, limit);
        var episodeRows = await VideoRowsAsync(userId, result.Items, cancellationToken).ConfigureAwait(false);

        var rows = result.Items
            .Select(n => episodeRows.TryGetValue(n.Id, out var row) ? row : Plain(n))
            .ToList();
        return new PageDto(null, result.TotalCount, rows);
    }

    internal static string TypeName(NodeKind kind) => kind switch
    {
        NodeKind.Library => "Library",
        NodeKind.Series => "Series",
        NodeKind.Season => "Season",
        NodeKind.Episode => "Episode",
        NodeKind.Movie => "Movie",
        _ => "Other",
    };

    private static ItemDto Plain(LibraryNode node) => new()
    {
        Id = node.Id,
        Name = node.Name,
        Type = TypeName(node.Kind),
        SeriesId = node.SeriesId,
        SeriesName = node.SeriesName,
        SeasonId = node.Kind == NodeKind.Episode ? node.SeasonId : null,
        IndexNumber = node.IndexNumber,
        ParentIndexNumber = node.ParentIndexNumber,
        LibraryType = node.LibraryType,
        ProductionYear = node.ProductionYear,
    };

    private async Task<Dictionary<Guid, ItemDto>> VideoRowsAsync(Guid userId, IReadOnlyList<LibraryNode> nodes, CancellationToken cancellationToken)
    {
        var facts = nodes
            .Where(n => n.Kind is NodeKind.Episode or NodeKind.Movie)
            .Select(n => _library.GetVideo(userId, n.Id))
            .OfType<VideoFacts>()
            .ToList();
        return (await EpisodesAsync(facts, cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.Id);
    }

    private async Task<ItemDto> SeasonAsync(Guid userId, LibraryNode season, CancellationToken cancellationToken)
    {
        var episodes = _library.GetSeasonEpisodes(userId, season.Id);
        var records = await _state.GetAsync(episodes.Select(e => e.Node.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        int eligible = 0, synced = 0;
        foreach (var episode in episodes)
        {
            records.TryGetValue(episode.Node.Id, out var record);
            var evaluation = EligibilityService.Evaluate(episode, record);
            if (evaluation.IsEligible)
            {
                eligible++;
            }

            if (evaluation.Status == EpisodeStatus.Done)
            {
                synced++;
            }
        }

        return Plain(season) with { EpisodeCount = episodes.Count, EligibleCount = eligible, SyncedCount = synced };
    }

    private async Task<IReadOnlyList<ItemDto>> EpisodesAsync(IReadOnlyList<VideoFacts> episodes, CancellationToken cancellationToken)
    {
        var records = await _state.GetAsync(episodes.Select(e => e.Node.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        return episodes.Select(episode =>
        {
            records.TryGetValue(episode.Node.Id, out var record);
            var evaluation = EligibilityService.Evaluate(episode, record);
            return Plain(episode.Node) with
            {
                Status = EligibilityService.StatusCode(evaluation.Status),
                Eligible = evaluation.IsEligible,
                Reason = EligibilityService.ReasonCode(evaluation.Reason),
                Message = evaluation.Message,
            };
        }).ToList();
    }
}
