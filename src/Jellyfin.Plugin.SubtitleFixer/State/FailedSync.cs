namespace Jellyfin.Plugin.SubtitleFixer.State;

/// <summary>
/// One entry in the failed-syncs log (D44): a sync that ended failed or check, kept after the queue list drops it.
/// </summary>
/// <param name="Id">Log entry id (assigned by the store; 0 before it is added).</param>
/// <param name="ItemId">Episode or movie.</param>
/// <param name="Name">Display name when it ran.</param>
/// <param name="SeriesName">Series name, empty for movies.</param>
/// <param name="UserId">Who queued it.</param>
/// <param name="Status">Failed or check.</param>
/// <param name="Message">Why: SubSync's error, or the check reason.</param>
/// <param name="Source"><c>manual</c> or <c>bazarr</c>.</param>
/// <param name="Note">Note from the source, for example "downloaded from opensubtitlescom".</param>
/// <param name="FinishedUtc">When it ended.</param>
public sealed record FailedSync(
    long Id,
    Guid ItemId,
    string Name,
    string SeriesName,
    Guid UserId,
    SyncStatus Status,
    string? Message,
    string Source,
    string? Note,
    DateTime FinishedUtc);
