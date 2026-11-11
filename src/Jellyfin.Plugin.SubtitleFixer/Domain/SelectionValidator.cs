using Jellyfin.Plugin.SubtitleFixer.State;

namespace Jellyfin.Plugin.SubtitleFixer.Domain;

/// <summary>API error codes (API.md, error codes).</summary>
public static class ErrorCodes
{
    /// <summary>Items belong to more than one series.</summary>
    public const string MultipleSeries = "multiple_series";

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

/// <summary>An episode that will be queued.</summary>
/// <param name="Episode">Episode facts.</param>
/// <param name="Eligibility">Its evaluation (eligible).</param>
public sealed record EpisodeCandidate(EpisodeFacts Episode, Eligibility Eligibility);

/// <summary>Counts of episodes that will not be queued.</summary>
/// <param name="AlreadySynced">Already synced.</param>
/// <param name="NoEnglishAudio">No English audio.</param>
/// <param name="NoSidecar">No matching sidecar.</param>
/// <param name="NoVideoFile">Video file missing.</param>
public sealed record SkippedCounts(int AlreadySynced, int NoEnglishAudio, int NoSidecar, int NoVideoFile);

/// <summary>Outcome of validating a selection. Either <see cref="Error"/> is set, or the other fields are.</summary>
public sealed record SelectionResult
{
    /// <summary>Gets the error, when the selection is rejected.</summary>
    public SelectionError? Error { get; init; }

    /// <summary>Gets the series every item belongs to.</summary>
    public Guid SeriesId { get; init; }

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
    /// <param name="maxEpisodesPerRequest">Cap on eligible episodes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<SelectionResult> ValidateAsync(Guid userId, IReadOnlyList<Guid>? itemIds, int maxEpisodesPerRequest, CancellationToken cancellationToken)
    {
        if (itemIds is null || itemIds.Count == 0)
        {
            return SelectionResult.Reject(ErrorCodes.NothingEligible, "Select at least one season or episode.");
        }

        if (itemIds.Count > MaxIdsPerRequest)
        {
            return SelectionResult.Reject(ErrorCodes.CapExceeded, $"A request may contain at most {MaxIdsPerRequest} items.");
        }

        var seriesIds = new HashSet<Guid>();
        string? seriesName = null;
        var episodes = new List<EpisodeFacts>();
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
                    var facts = _library.GetEpisode(userId, node.Id);
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
                    return SelectionResult.Reject(ErrorCodes.UnsupportedItemType, "Only seasons and episodes can be selected, not series or libraries.");
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

        var records = await _state.GetAsync(episodes.Select(e => e.Node.Id).ToArray(), cancellationToken).ConfigureAwait(false);

        var eligible = new List<EpisodeCandidate>();
        int alreadySynced = 0, noAudio = 0, noSidecar = 0, noVideo = 0, attemptedBefore = 0;
        foreach (var episode in episodes)
        {
            records.TryGetValue(episode.Node.Id, out var record);
            var evaluation = EligibilityService.Evaluate(episode, record);
            switch (evaluation.Reason)
            {
                case IneligibleReason.None:
                    eligible.Add(new EpisodeCandidate(episode, evaluation));
                    if (evaluation.Status == EpisodeStatus.Attempted)
                    {
                        attemptedBefore++;
                    }

                    break;
                case IneligibleReason.AlreadySynced: alreadySynced++; break;
                case IneligibleReason.NoEnglishAudio: noAudio++; break;
                case IneligibleReason.NoSidecar: noSidecar++; break;
                case IneligibleReason.NoVideoFile: noVideo++; break;
            }
        }

        if (eligible.Count > maxEpisodesPerRequest)
        {
            return SelectionResult.Reject(
                ErrorCodes.CapExceeded,
                $"{eligible.Count} episodes can be fixed, but one request may queue at most {maxEpisodesPerRequest}. Select fewer seasons.");
        }

        return new SelectionResult
        {
            SeriesId = seriesIds.Single(),
            SeriesName = seriesName ?? string.Empty,
            Selected = episodes.Count,
            Eligible = eligible,
            Skipped = new SkippedCounts(alreadySynced, noAudio, noSidecar, noVideo),
            AttemptedBefore = attemptedBefore,
        };
    }
}
