using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services;
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
    /// <param name="startsWith">Library pages only: a parsed <see cref="LetterFilter"/> letter, or null for all.</param>
    /// <returns>The page, or null when the parent is unknown or not accessible.</returns>
    public async Task<PageDto?> BrowseAsync(Guid userId, Guid? parentId, int startIndex, int limit, CancellationToken cancellationToken, string? startsWith = null)
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
            _library.GetBreadcrumb(userId, parentNode.Id).Select(n => new CrumbDto(n.Id, n.Name, TypeName(n.Kind), n.ImageTag)).ToList());

        if (parentNode.Kind == NodeKind.Season)
        {
            var all = _library.GetSeasonEpisodes(userId, parentNode.Id);
            var page = all.Skip(startIndex).Take(limit).ToList();
            var items = await EpisodesAsync(page, cancellationToken).ConfigureAwait(false);
            return new PageDto(parent, all.Count, items);
        }

        var children = _library.GetChildren(userId, parentNode.Id, startIndex, limit, parentNode.Kind == NodeKind.Library ? startsWith : null);
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
        ImageTag = node.ImageTag,
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
        var views = await ViewsAsync(episodes, cancellationToken).ConfigureAwait(false);
        int eligible = 0, synced = 0, noSubtitle = 0;
        foreach (var view in views)
        {
            if (view.Eligible)
            {
                eligible++;
            }

            if (view.Status == EpisodeStatus.Done)
            {
                synced++;
            }

            if (view.English.Reason == IneligibleReason.NoSidecar)
            {
                noSubtitle++;
            }
        }

        return Plain(season) with { EpisodeCount = episodes.Count, EligibleCount = eligible, SyncedCount = synced, NoSubtitleCount = noSubtitle };
    }

    private async Task<IReadOnlyList<ItemDto>> EpisodesAsync(IReadOnlyList<VideoFacts> episodes, CancellationToken cancellationToken)
    {
        var views = await ViewsAsync(episodes, cancellationToken).ConfigureAwait(false);
        return views.Select(view =>
        {
            var episode = view.Facts;
            var english = view.English;
            var check = EligibilityService.IsUntagged(episode.AudioLanguages) ? EligibilityService.CurrentCheck(episode, english.Video) : null;
            return Plain(episode.Node) with
            {
                Status = EligibilityService.StatusCode(view.Status),
                Eligible = view.Eligible,
                Reason = view.Eligible ? null : EligibilityService.ReasonCode(english.Reason),
                Message = english.Message ?? view.Languages.Select(l => l.Evaluation.Message).FirstOrDefault(m => m is not null),
                AudioUntagged = english.Reason == IneligibleReason.NoEnglishAudio && EligibilityService.IsUntagged(episode.AudioLanguages),
                SubtitleHi = view.Languages[0].Sidecar?.IsHearingImpaired,
                AudioCheck = check is null ? null : new AudioCheckInfoDto(check.Language, check.Probability, check.IsEnglish, check.CheckedUtc, AudioCheckService.Describe(check)),
                Subtitles = view.Languages.Select(l => new SubtitleStateDto(
                    l.Language,
                    l.Sidecar is not null,
                    l.Sidecar?.IsHearingImpaired ?? false,
                    EligibilityService.StatusCode(l.Evaluation.Status),
                    l.Evaluation.IsEligible,
                    EligibilityService.ReasonCode(l.Evaluation.Reason),
                    l.Evaluation.Message,
                    l.Evaluation.Status == EpisodeStatus.Done ? l.Record?.SubSyncVersion : null,
                    l.Evaluation.Status == EpisodeStatus.Done && IsOutdated(l.Record?.SubSyncVersion))).ToList(),
            };
        }).ToList();
    }

    /// <summary>Results of SubSync before 1.4.1 can have lines shown for a few hundredths of a second (D51).</summary>
    /// <param name="version">Version stored with the record.</param>
    /// <returns>True when older than 1.4.1.</returns>
    internal static bool IsOutdated(string? version) =>
        Version.TryParse(version, out var v) && v < new Version(1, 4, 1);

    private async Task<IReadOnlyList<EpisodeView>> ViewsAsync(IReadOnlyList<VideoFacts> episodes, CancellationToken cancellationToken)
    {
        var ids = episodes.Select(e => e.Node.Id).ToArray();
        var recordsOf = new Dictionary<string, IReadOnlyDictionary<Guid, SyncRecord>>();
        foreach (var language in SubtitleLanguage.All)
        {
            recordsOf[language] = await _state.GetAsync(ids, language, cancellationToken).ConfigureAwait(false);
        }

        return episodes.Select(episode => EpisodeView.Of(episode, recordsOf)).ToList();
    }

    /// <summary>One language of an episode: its evaluation, record and the subtitle on disk.</summary>
    private sealed record LanguageView(string Language, Eligibility Evaluation, SyncRecord? Record, Sidecar? Sidecar);

    /// <summary>Every language of an episode, and what they mean for the episode as a whole (D50).</summary>
    private sealed record EpisodeView(VideoFacts Facts, IReadOnlyList<LanguageView> Languages)
    {
        public Eligibility English => Languages[0].Evaluation;

        public bool Eligible => Languages.Any(l => l.Evaluation.IsEligible);

        // done when every subtitle the episode has is synced; attempted when one was tried and not synced
        public EpisodeStatus Status
        {
            get
            {
                var present = Languages.Where(l => l.Sidecar is not null || l.Evaluation.Status != EpisodeStatus.None).ToList();
                if (present.Count == 0)
                {
                    return English.Status;
                }

                if (present.All(l => l.Evaluation.Status == EpisodeStatus.Done))
                {
                    return EpisodeStatus.Done;
                }

                return present.Any(l => l.Evaluation.Status == EpisodeStatus.Attempted) ? EpisodeStatus.Attempted : EpisodeStatus.None;
            }
        }

        public static EpisodeView Of(VideoFacts episode, IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, SyncRecord>> recordsOf) =>
            new(episode, SubtitleLanguage.All.Select(language =>
            {
                recordsOf[language].TryGetValue(episode.Node.Id, out var record);
                var evaluation = EligibilityService.Evaluate(episode, record, language);

                // the subtitle on disk, also when it isn't eligible (synced, no English audio), for the chips and HI badge
                var sidecar = evaluation.Sidecar ?? (evaluation.Video is { } v && evaluation.Reason != IneligibleReason.NoSidecar ? EligibilityService.FindSidecar(v.Path, language) : null);
                return new LanguageView(language, evaluation, record, sidecar);
            }).ToList());
    }
}
