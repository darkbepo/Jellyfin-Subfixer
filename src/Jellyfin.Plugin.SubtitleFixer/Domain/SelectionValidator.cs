using Jellyfin.Plugin.SubtitleFixer.State;

namespace Jellyfin.Plugin.SubtitleFixer.Domain;

/// <summary>API error codes (API.md, error codes).</summary>
public static class ErrorCodes
{
    /// <summary>Items belong to more than one series.</summary>
    public const string MultipleSeries = "multiple_series";

    /// <summary>Movies and episodes in one request (MOVIES-PLAN.md).</summary>
    public const string MixedSelection = "mixed_selection";

    /// <summary>A series, library or non-TV item was submitted, or an item is unknown or not accessible.</summary>
    public const string UnsupportedItemType = "unsupported_item_type";

    /// <summary>More eligible episodes than <c>MaxEpisodesPerRequest</c>.</summary>
    public const string CapExceeded = "cap_exceeded";

    /// <summary>Nothing in the selection can be queued.</summary>
    public const string NothingEligible = "nothing_eligible";

    /// <summary>Would exceed <c>MaxQueuedEpisodes</c>.</summary>
    public const string QueueFull = "queue_full";

    /// <summary>SubSync is not set up.</summary>
    public const string SubSyncUnavailable = "subsync_unavailable";

    /// <summary>Not allowed by <c>AllowedUsers</c>.</summary>
    public const string Forbidden = "forbidden";
}

/// <summary>A rejected selection.</summary>
/// <param name="Code">Error code.</param>
/// <param name="Message">Message for people.</param>
public sealed record SelectionError(string Code, string Message);

/// <summary>An episode and subtitle language that will be queued.</summary>
/// <param name="Episode">Episode facts.</param>
/// <param name="Eligibility">Its evaluation (eligible).</param>
/// <param name="Language">Subtitle language (D50).</param>
public sealed record EpisodeCandidate(VideoFacts Episode, Eligibility Eligibility, string Language = SubtitleLanguage.English);

/// <summary>Counts of episodes that will not be queued.</summary>
/// <param name="AlreadySynced">Already synced.</param>
/// <param name="NoEnglishAudio">No English audio.</param>
/// <param name="NoSidecar">No matching sidecar.</param>
/// <param name="NoVideoFile">Video file missing.</param>
public sealed record SkippedCounts(int AlreadySynced, int NoEnglishAudio, int NoSidecar, int NoVideoFile);

/// <summary>What a selection contains.</summary>
public enum SelectionKind
{
    /// <summary>Seasons and episodes of one series.</summary>
    Episodes,

    /// <summary>Movies (no series lock).</summary>
    Movies,
}

/// <summary>Outcome of validating a selection. Either <see cref="Error"/> is set, or the other fields are.</summary>
public sealed record SelectionResult
{
    /// <summary>Gets the error, when the selection is rejected.</summary>
    public SelectionError? Error { get; init; }

    /// <summary>Gets what was selected.</summary>
    public SelectionKind Kind { get; init; }

    /// <summary>Gets the series every item belongs to; null for movies.</summary>
    public Guid? SeriesId { get; init; }

    /// <summary>Gets the series name.</summary>
    public string SeriesName { get; init; } = string.Empty;

    /// <summary>Gets the number of distinct episodes after expanding seasons.</summary>
    public int Selected { get; init; }

    /// <summary>Gets the eligible episodes, in selection order.</summary>
    public IReadOnlyList<EpisodeCandidate> Eligible { get; init; } = [];

    /// <summary>Gets the skipped counts.</summary>
    public SkippedCounts Skipped { get; init; } = new(0, 0, 0, 0);

    /// <summary>Gets how many eligible episodes were attempted before (<c>check</c> or <c>failed</c>).</summary>
    public int AttemptedBefore { get; init; }

    /// <summary>Creates a rejected result.</summary>
    /// <param name="code">Error code.</param>
    /// <param name="message">Message.</param>
    /// <returns>The result.</returns>
    public static SelectionResult Reject(string code, string message) => new() { Error = new SelectionError(code, message) };
}

/// <summary>
/// Hard rules 1, 2 and 4 (SPEC.md section 3), enforced on the server. Season ids expand to episodes; series,
/// library and other ids are rejected; everything must belong to one series; eligible episodes are capped.
/// Skipped and ineligible episodes do not count toward the cap.
/// </summary>
public sealed class SelectionValidator
{
    /// <summary>Most ids one request may carry; protects the server from huge payloads.</summary>
    public const int MaxIdsPerRequest = 1000;

    private readonly ILibraryFacade _library;
    private readonly IStateStore _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="SelectionValidator"/> class.
    /// </summary>
    /// <param name="library">Library facade.</param>
    /// <param name="state">State store.</param>
    public SelectionValidator(ILibraryFacade library, IStateStore state)
    {
        _library = library;
        _state = state;
    }

    /// <summary>Validates and expands a selection.</summary>
    /// <param name="userId">Requesting user.</param>
    /// <param name="itemIds">Submitted season and episode ids.</param>
    /// <param name="maxEpisodesPerRequest">Cap on eligible subtitles (an episode with an English and a Danish one counts twice).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="languages">Subtitle languages to queue (D50); null or empty: every language. A missing Danish
    /// subtitle is simply not there to sync, so it isn't counted as skipped.</param>
    /// <param name="again">Sync again (D51): only for exactly one episode or movie; its synced subtitles are queued too.</param>
    /// <returns>The result.</returns>
    /// <param name="excluded">Languages the installed SubSync can't sync (Danish before 1.5.0); left out of the selection.</param>
    public async Task<SelectionResult> ValidateAsync(Guid userId, IReadOnlyList<Guid>? itemIds, int maxEpisodesPerRequest, CancellationToken cancellationToken,
        IReadOnlyList<string>? languages = null, bool again = false, IReadOnlyCollection<string>? excluded = null)
    {
        if (itemIds is null || itemIds.Count == 0)
        {
            return SelectionResult.Reject(ErrorCodes.NothingEligible, "Select at least one season, episode or movie.");
        }

        var wanted = (languages ?? []).Select(SubtitleLanguage.Parse).ToList();
        if (wanted.Any(l => l is null))
        {
            return SelectionResult.Reject(ErrorCodes.UnsupportedItemType, "Subtitle languages are \"en\" and \"da\".");
        }

        var langs = wanted.Count == 0 ? SubtitleLanguage.All : SubtitleLanguage.All.Where(l => wanted.Contains(l)).ToList();
        if (again && (itemIds.Distinct().Count() != 1 || _library.GetNode(userId, itemIds[0]) is not { Kind: NodeKind.Episode or NodeKind.Movie }))
        {
            return SelectionResult.Reject(ErrorCodes.UnsupportedItemType, "Sync again works on one episode or movie at a time.");
        }

        if (again && langs.Count != 1)
        {
            return SelectionResult.Reject(ErrorCodes.UnsupportedItemType, "Sync again works on one subtitle language at a time.");
        }

        if (excluded is not null)
        {
            langs = langs.Where(l => !excluded.Contains(l)).ToList();
            if (langs.Count == 0)
            {
                return SelectionResult.Reject(ErrorCodes.SubSyncUnavailable, "Danish subtitles need SubSync 1.5.0 or later. Update SubSync on the Subtitle Fixer settings page.");
            }
        }

        if (itemIds.Count > MaxIdsPerRequest)
        {
            return SelectionResult.Reject(ErrorCodes.CapExceeded, $"A request may contain at most {MaxIdsPerRequest} items.");
        }

        var seriesIds = new HashSet<Guid>();
        bool hasMovies = false, hasEpisodes = false;
        string? seriesName = null;
        var episodes = new List<VideoFacts>();
        var seen = new HashSet<Guid>();

        foreach (var id in itemIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = _library.GetNode(userId, id);
            if (node is null)
            {
                return SelectionResult.Reject(ErrorCodes.UnsupportedItemType, "An item was not found or you cannot access it.");
            }

            switch (node.Kind)
            {
                case NodeKind.Season:
                    foreach (var episode in _library.GetSeasonEpisodes(userId, node.Id))
                    {
                        if (seen.Add(episode.Node.Id))
                        {
                            episodes.Add(episode);
                        }
                    }

                    break;
                case NodeKind.Episode:
                case NodeKind.Movie:
                    var facts = _library.GetVideo(userId, node.Id);
                    if (facts is null)
                    {
                        return SelectionResult.Reject(ErrorCodes.UnsupportedItemType, "An item was not found or you cannot access it.");
                    }

                    if (seen.Add(facts.Node.Id))
                    {
                        episodes.Add(facts);
                    }

                    break;
                default:
                    return SelectionResult.Reject(ErrorCodes.UnsupportedItemType, "Only seasons, episodes and movies can be selected, not series or libraries.");
            }

            if (node.Kind == NodeKind.Movie)
            {
                hasMovies = true;
            }
            else
            {
                hasEpisodes = true;
            }

            if (hasMovies && hasEpisodes)
            {
                return SelectionResult.Reject(ErrorCodes.MixedSelection, "Pick either movies or episodes, not both at once.");
            }

            if (node.Kind == NodeKind.Movie)
            {
                continue; // Movies have no series lock.
            }

            if (node.SeriesId is not { } seriesId || seriesId == Guid.Empty)
            {
                return SelectionResult.Reject(ErrorCodes.UnsupportedItemType, $"'{node.Name}' does not belong to a series.");
            }

            seriesIds.Add(seriesId);
            seriesName ??= node.SeriesName;
            if (seriesIds.Count > 1)
            {
                return SelectionResult.Reject(ErrorCodes.MultipleSeries, "Pick seasons or episodes of one series at a time.");
            }
        }

        var ids = episodes.Select(e => e.Node.Id).ToArray();
        var recordsOf = new Dictionary<string, IReadOnlyDictionary<Guid, SyncRecord>>();
        foreach (var language in langs)
        {
            recordsOf[language] = await _state.GetAsync(ids, language, cancellationToken).ConfigureAwait(false);
        }

        var eligible = new List<EpisodeCandidate>();
        int alreadySynced = 0, noAudio = 0, noSidecar = 0, noVideo = 0, attemptedBefore = 0;
        foreach (var episode in episodes)
        {
            var counted = false; // an episode counts once as "no English audio" or "no video", whatever its languages
            var missing = false; // no English subtitle: counted only when no other language of the episode is queued
            var before = eligible.Count;
            foreach (var language in langs)
            {
                recordsOf[language].TryGetValue(episode.Node.Id, out var record);
                var evaluation = EligibilityService.Evaluate(episode, record, language, again);
                var english = language == SubtitleLanguage.English;
                switch (evaluation.Reason)
                {
                    case IneligibleReason.None:
                        eligible.Add(new EpisodeCandidate(episode, evaluation, language));
                        if (evaluation.Status == EpisodeStatus.Attempted)
                        {
                            attemptedBefore++;
                        }

                        break;
                    case IneligibleReason.AlreadySynced:
                        alreadySynced++;
                        break;
                    case IneligibleReason.NoEnglishAudio:
                        // a Danish subtitle only counts when there is one
                        if (!counted && (english || (evaluation.Video is { } v && EligibilityService.FindSidecar(v.Path, language) is not null)))
                        {
                            noAudio++;
                            counted = true;
                        }

                        break;
                    case IneligibleReason.NoSidecar:
                        if (english || langs.Count == 1)
                        {
                            missing = true;
                        }

                        break;
                    case IneligibleReason.NoVideoFile:
                        if (!counted)
                        {
                            noVideo++;
                            counted = true;
                        }

                        break;
                }
            }

            if (missing && eligible.Count == before)
            {
                noSidecar++;
            }
        }

        if (eligible.Count > maxEpisodesPerRequest)
        {
            return SelectionResult.Reject(
                ErrorCodes.CapExceeded,
                hasMovies
                    ? $"{eligible.Count} movie subtitles can be fixed, but one request may queue at most {maxEpisodesPerRequest}. Select fewer movies."
                    : $"{eligible.Count} episode subtitles can be fixed, but one request may queue at most {maxEpisodesPerRequest}. Select fewer seasons or episodes.");
        }

        return new SelectionResult
        {
            Kind = hasMovies ? SelectionKind.Movies : SelectionKind.Episodes,
            SeriesId = hasMovies ? null : seriesIds.Single(),
            SeriesName = seriesName ?? string.Empty,
            Selected = episodes.Count,
            Eligible = eligible,
            Skipped = new SkippedCounts(alreadySynced, noAudio, noSidecar, noVideo),
            AttemptedBefore = attemptedBefore,
        };
    }
}
