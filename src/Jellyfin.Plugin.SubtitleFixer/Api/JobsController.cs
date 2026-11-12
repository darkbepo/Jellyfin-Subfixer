using System.Collections.Concurrent;
using System.Net.Mime;
using System.Text.Json;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.State;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleFixer.Api;

/// <summary><c>POST /Jobs</c> response.</summary>
/// <param name="Queued">Episodes added.</param>
/// <param name="Skipped">Selected episodes not added (ineligible, or already in the queue).</param>
/// <param name="QueueLength">Queued and running episodes after adding.</param>
public sealed record JobsAcceptedDto(int Queued, int Skipped, int QueueLength);

/// <summary>One queue entry in <c>GET /Status</c>.</summary>
/// <param name="JobId">Entry id.</param>
/// <param name="ItemId">Episode id, or null when the viewer cannot access it.</param>
/// <param name="Name">Name, or "Episode in another library".</param>
/// <param name="SeriesName">Series name, or null when hidden.</param>
/// <param name="Status">queued, running, done, check, failed, cancelled or skipped.</param>
/// <param name="Message">Reason.</param>
/// <param name="Mine">Queued by the viewer.</param>
/// <param name="Source">manual or bazarr.</param>
/// <param name="Note">Note from the source.</param>
/// <param name="Seconds">SubSync seconds.</param>
/// <param name="StartedUtc">When it started.</param>
/// <param name="Stage">While running: reading_audio, listening or matching, once SubSync reports it.</param>
/// <param name="ExpectedSeconds">While running: estimated total seconds, or null when the video length is unknown (D40).</param>
/// <param name="Percent">While running: the item's progress from SubSync's own listening percentage (SubSync 1.3.0 and later), 0 to 95, or null.</param>
public sealed record StatusItemDto(
    Guid JobId,
    Guid? ItemId,
    string Name,
    string? SeriesName,
    string Status,
    string? Message,
    bool Mine,
    string Source,
    string? Note,
    double? Seconds,
    DateTime? StartedUtc,
    string? Stage = null,
    double? ExpectedSeconds = null,
    int? Percent = null);

/// <summary>Counts by status.</summary>
/// <param name="Queued">Queued.</param>
/// <param name="Running">Running (0 or 1).</param>
/// <param name="Done">Done.</param>
/// <param name="Check">Check.</param>
/// <param name="Failed">Failed.</param>
/// <param name="Cancelled">Cancelled.</param>
/// <param name="Skipped">Skipped.</param>
public sealed record StatusTotalsDto(int Queued, int Running, int Done, int Check, int Failed, int Cancelled, int Skipped);

/// <summary><c>GET /Status</c>.</summary>
/// <param name="State">idle, running or stopping.</param>
/// <param name="SubsyncAvailable">SubSync folder set and the CLI present.</param>
/// <param name="StopReason">Why the queue last stopped by itself.</param>
/// <param name="Current">The running entry.</param>
/// <param name="Totals">Counts.</param>
/// <param name="EtaSeconds">Remaining time estimate, null until one episode finished.</param>
/// <param name="AheadOfYours">Pending entries in front of the viewer's first pending one, or null.</param>
/// <param name="Items">Entries.</param>
/// <param name="Maintenance">While SubSync is being updated (D46): the message to show; nothing can be queued meanwhile.</param>
public sealed record StatusDto(
    string State,
    bool SubsyncAvailable,
    string? StopReason,
    StatusItemDto? Current,
    StatusTotalsDto Totals,
    double? EtaSeconds,
    int? AheadOfYours,
    IReadOnlyList<StatusItemDto> Items,
    string? Maintenance = null);

/// <summary>One entry of <c>GET /FailedSyncs</c> (D44).</summary>
/// <param name="Id">Log entry id.</param>
/// <param name="ItemId">Episode or movie id, or null when the viewer cannot access it.</param>
/// <param name="Name">Name, or "Episode in another library".</param>
/// <param name="SeriesName">Series name, or null when hidden or a movie.</param>
/// <param name="Status">failed or check.</param>
/// <param name="Message">Why.</param>
/// <param name="Mine">Queued by the viewer.</param>
/// <param name="Source">manual or bazarr.</param>
/// <param name="Note">Note from the source.</param>
/// <param name="FinishedUtc">When it ended.</param>
public sealed record FailedSyncDto(
    long Id,
    Guid? ItemId,
    string Name,
    string? SeriesName,
    string Status,
    string? Message,
    bool Mine,
    string Source,
    string? Note,
    DateTime FinishedUtc);

/// <summary><c>GET /FailedSyncs</c>.</summary>
/// <param name="TotalCount">Entries the viewer may see.</param>
/// <param name="Items">This page, newest first.</param>
public sealed record FailedSyncsDto(int TotalCount, IReadOnlyList<FailedSyncDto> Items);

/// <summary>The shared queue API (API.md: Jobs, Status; D19).</summary>
[ApiController]
[Authorize]
[Route("SubtitleFixer")]
public sealed class JobsController : ControllerBase
{
    /// <summary>Shown for entries in libraries the viewer cannot access.</summary>
    public const string HiddenName = "Episode in another library";

    private readonly AccessService _access;
    private readonly SelectionValidator _validator;
    private readonly JobQueue _queue;
    private readonly ISubSyncWorker _worker;
    private readonly VisibilityCache _visibility;
    private readonly IStateStore _state;
    private readonly StartupStatus _startup;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobsController"/> class.
    /// </summary>
    /// <param name="access">Access service.</param>
    /// <param name="validator">Selection validator.</param>
    /// <param name="queue">Queue.</param>
    /// <param name="worker">Worker (availability only).</param>
    /// <param name="visibility">Visibility cache.</param>
    /// <param name="state">State store (failed-syncs log).</param>
    /// <param name="startup">Startup status.</param>
    public JobsController(AccessService access, SelectionValidator validator, JobQueue queue, ISubSyncWorker worker, VisibilityCache visibility, IStateStore state, StartupStatus startup)
    {
        _state = state;
        _startup = startup;
        _access = access;
        _validator = validator;
        _queue = queue;
        _worker = worker;
        _visibility = visibility;
    }

    /// <summary>Validates like Preview, then appends the eligible episodes to the shared queue.</summary>
    /// <param name="request">Selected ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>202 with counts.</returns>
    [HttpPost("Jobs")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> Start([FromBody] SelectionRequest request, CancellationToken cancellationToken)
    {
        if (Deny(out var caller) is { } denied)
        {
            return denied;
        }

        return await QueueAsync(caller, request?.ItemIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The failed-syncs log (D44): the caller's own entries, or everyone's for an admin. Newest first, paged.</summary>
    /// <param name="startIndex">First entry.</param>
    /// <param name="limit">Page size (at most 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    [HttpGet("FailedSyncs")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> FailedSyncs([FromQuery] int startIndex = 0, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        if (Deny(out var caller) is { } denied)
        {
            return denied;
        }

        if (StateUnavailable() is { } unavailable)
        {
            return unavailable;
        }

        var (items, total) = await _state.GetFailedSyncsAsync(caller.IsAdmin ? null : caller.User.Id, Math.Max(0, startIndex), Math.Clamp(limit, 1, 100), cancellationToken).ConfigureAwait(false);
        return ApiResults.Json(BuildFailedSyncs(items, total, caller.User.Id, id => _visibility.CanSee(caller.User.Id, id)));
    }

    /// <summary>Queues a logged item again (validated like Start), then removes it from the log. Own entries, or any for an admin.</summary>
    /// <param name="id">Log entry id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>202 with counts, like Start.</returns>
    [HttpPost("FailedSyncs/{id}/Retry")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> RetryFailedSync([FromRoute] long id, CancellationToken cancellationToken)
    {
        if (Deny(out var caller) is { } denied)
        {
            return denied;
        }

        if (StateUnavailable() is { } unavailable)
        {
            return unavailable;
        }

        var entry = await _state.GetFailedSyncAsync(id, cancellationToken).ConfigureAwait(false);
        if (entry is null || (!caller.IsAdmin && entry.UserId != caller.User.Id))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "not_found", "That log entry was not found.");
        }

        var result = await QueueAsync(caller, [entry.ItemId], cancellationToken).ConfigureAwait(false);
        if (result.StatusCode == StatusCodes.Status202Accepted)
        {
            await _state.RemoveFailedSyncsAsync(null, entry.Id, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Empties the failed-syncs log. Admin only.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An empty page.</returns>
    [HttpPost("FailedSyncs/Clear")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> ClearFailedSyncs(CancellationToken cancellationToken)
    {
        if (Deny(out var caller) is { } denied)
        {
            return denied;
        }

        if (!caller.IsAdmin)
        {
            return ApiResults.Error(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "Only an administrator can clear the failed-syncs log.");
        }

        if (StateUnavailable() is { } unavailable)
        {
            return unavailable;
        }

        await _state.RemoveFailedSyncsAsync(null, null, cancellationToken).ConfigureAwait(false);
        return ApiResults.Json(new FailedSyncsDto(0, []));
    }

    /// <summary>Builds the log a viewer sees, hiding items in libraries they cannot access. Static and pure for tests.</summary>
    /// <param name="items">Entries.</param>
    /// <param name="total">Total count.</param>
    /// <param name="viewer">Viewer.</param>
    /// <param name="canSee">Whether the viewer can access an item.</param>
    /// <returns>The page.</returns>
    internal static FailedSyncsDto BuildFailedSyncs(IReadOnlyList<FailedSync> items, int total, Guid viewer, Func<Guid, bool> canSee) =>
        new(total, items.Select(f =>
        {
            var mine = f.UserId == viewer;
            var visible = mine || canSee(f.ItemId);
            return new FailedSyncDto(
                f.Id,
                visible ? f.ItemId : null,
                visible ? f.Name : HiddenName,
                visible && f.SeriesName.Length > 0 ? f.SeriesName : null,
                f.Status == SyncStatus.Check ? "check" : "failed",
                visible ? f.Message : null,
                mine,
                f.Source,
                visible ? f.Note : null,
                f.FinishedUtc);
        }).ToList());

    private ContentResult? StateUnavailable() => _startup.StateReady
        ? null
        : ApiResults.Error(StatusCodes.Status503ServiceUnavailable, "state_unavailable", _startup.StateError ?? "The state database is not ready.");

    private async Task<ContentResult> QueueAsync(Caller caller, IReadOnlyList<Guid>? itemIds, CancellationToken cancellationToken)
    {
        if (!_worker.IsConfigured)
        {
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, ErrorCodes.SubSyncUnavailable, "SubSync is not configured. Ask an administrator.");
        }

        var config = Plugin.Instance?.Configuration;
        var result = await _validator.ValidateAsync(caller.User.Id, itemIds, config?.MaxEpisodesPerRequest ?? 150, cancellationToken).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, error.Code, error.Message);
        }

        if (result.Eligible.Count == 0)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, ErrorCodes.NothingEligible, "None of the selected episodes can be fixed.");
        }

        var items = result.Eligible.Select(c => new JobRequestItem(c.Episode.Node.Id, DisplayName(c.Episode.Node), result.SeriesName)).ToList();
        var queued = _queue.Enqueue(caller.User.Id, items, JobSource.Manual, null, config?.MaxQueuedEpisodes ?? 300);
        if (queued.Error is { } refused)
        {
            // SubSync being updated (D46) is 503 like a missing SubSync; a full queue is 409.
            var status = refused.Code == ErrorCodes.SubSyncUnavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict;
            return ApiResults.Error(status, refused.Code, refused.Message);
        }

        return ApiResults.Json(new JobsAcceptedDto(queued.Queued, result.Selected - queued.Queued, queued.QueueLength), StatusCodes.Status202Accepted);
    }

    /// <summary>The shared queue as the caller may see it.</summary>
    /// <returns>Status.</returns>
    [HttpGet("Status")]
    [Produces(MediaTypeNames.Application.Json)]
    public IActionResult Status()
    {
        if (Deny(out var caller) is { } denied)
        {
            return denied;
        }

        return ApiResults.Json(BuildStatus(_queue.Snapshot(), caller.User.Id, _worker.IsConfigured, id => _visibility.CanSee(caller.User.Id, id)));
    }

    /// <summary>Cancels the caller's running and queued entries; an admin cancels all (D19).</summary>
    /// <returns>Status after cancelling.</returns>
    [HttpPost("Jobs/Cancel")]
    [Produces(MediaTypeNames.Application.Json)]
    public IActionResult Cancel()
    {
        if (Deny(out var caller) is { } denied)
        {
            return denied;
        }

        _queue.Cancel(caller.User.Id, caller.IsAdmin);
        return Status();
    }

    /// <summary>Removes the caller's finished entries from the list; an admin clears all.</summary>
    /// <returns>Status after clearing.</returns>
    [HttpPost("Jobs/Clear")]
    [Produces(MediaTypeNames.Application.Json)]
    public IActionResult Clear()
    {
        if (Deny(out var caller) is { } denied)
        {
            return denied;
        }

        _queue.ClearFinished(caller.User.Id, caller.IsAdmin);
        return Status();
    }

    /// <summary>Builds the status a viewer sees. Static and pure for tests.</summary>
    /// <param name="snapshot">Queue snapshot.</param>
    /// <param name="viewer">Viewer.</param>
    /// <param name="subsyncAvailable">SubSync configured.</param>
    /// <param name="canSee">Whether the viewer can access an item.</param>
    /// <returns>Status.</returns>
    internal static StatusDto BuildStatus(QueueSnapshot snapshot, Guid viewer, bool subsyncAvailable, Func<Guid, bool> canSee)
    {
        StatusItemDto Map(JobItemSnapshot i)
        {
            var mine = i.UserId == viewer;
            var visible = mine || canSee(i.ItemId);
            return new StatusItemDto(
                i.JobId,
                visible ? i.ItemId : null,
                visible ? i.Name : HiddenName,
                visible ? i.SeriesName : null,
                JobQueue.StatusCode(i.Status),
                i.Message,
                mine,
                i.Source == JobSource.Bazarr ? "bazarr" : "manual",
                visible ? i.Note : null,
                i.Seconds,
                i.StartedUtc,
                i.Stage,
                i.ExpectedSeconds,
                i.Percent);
        }

        var items = snapshot.Items.Select(Map).ToList();
        int Count(JobItemStatus s) => snapshot.Items.Count(i => i.Status == s);

        int? ahead = null;
        var pendingIndex = 0;
        foreach (var item in snapshot.Items.Where(i => i.Status is JobItemStatus.Queued or JobItemStatus.Running))
        {
            if (item.UserId == viewer)
            {
                ahead = pendingIndex;
                break;
            }

            pendingIndex++;
        }

        var current = snapshot.Current;
        return new StatusDto(
            snapshot.State.ToString().ToLowerInvariant(),
            subsyncAvailable && snapshot.Maintenance is null,
            snapshot.StopReason,
            current is null ? null : Map(current),
            new StatusTotalsDto(
                Count(JobItemStatus.Queued),
                Count(JobItemStatus.Running),
                Count(JobItemStatus.Done),
                Count(JobItemStatus.Check),
                Count(JobItemStatus.Failed),
                Count(JobItemStatus.Cancelled),
                Count(JobItemStatus.Skipped)),
            snapshot.EtaSeconds,
            ahead,
            items,
            snapshot.Maintenance);
    }

    private static string DisplayName(LibraryNode episode)
    {
        if (episode.Kind == NodeKind.Movie)
        {
            return episode.ProductionYear is { } year ? $"{episode.Name} ({year})" : episode.Name;
        }

        var code = (episode.ParentIndexNumber is { } s ? $"S{s:00}" : string.Empty) + (episode.IndexNumber is { } e ? $"E{e:00}" : string.Empty);
        return code.Length == 0 ? episode.Name : code + " " + episode.Name;
    }

    private ContentResult? Deny(out Caller caller)
    {
        caller = _access.GetCaller(User)!;
        if (caller is null)
        {
            return ApiResults.Error(StatusCodes.Status401Unauthorized, "unauthorized", "Sign in to Jellyfin with a user account.");
        }

        return caller.Allowed
            ? null
            : ApiResults.Error(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, AccessService.DenialMessage(caller.Denial));
    }
}

/// <summary>camelCase JSON results shared by controllers.</summary>
public static class ApiResults
{
    /// <summary>A JSON result.</summary>
    /// <param name="value">Body.</param>
    /// <param name="status">HTTP status.</param>
    /// <returns>Result.</returns>
    public static ContentResult Json(object value, int status = StatusCodes.Status200OK) => new()
    {
        Content = JsonSerializer.Serialize(value, value.GetType(), ApiJson.Options),
        ContentType = MediaTypeNames.Application.Json,
        StatusCode = status,
    };

    /// <summary>An error result (<c>{ error, message }</c>).</summary>
    /// <param name="status">HTTP status.</param>
    /// <param name="code">Error code.</param>
    /// <param name="message">Message.</param>
    /// <returns>Result.</returns>
    public static ContentResult Error(int status, string code, string message) => Json(new ErrorDto(code, message), status);
}

/// <summary>
/// Caches "can this user access this item" for a short time, so polling <c>/Status</c> every second does not
/// look up every queued item every time.
/// </summary>
public sealed class VisibilityCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaxEntries = 20_000;

    private readonly ILibraryFacade _library;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(Guid User, Guid Item), (bool Visible, DateTimeOffset Until)> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="VisibilityCache"/> class.
    /// </summary>
    /// <param name="library">Library facade.</param>
    /// <param name="time">Clock.</param>
    public VisibilityCache(ILibraryFacade library, TimeProvider time)
    {
        _library = library;
        _time = time;
    }

    /// <summary>Whether the user can access the item.</summary>
    /// <param name="userId">User.</param>
    /// <param name="itemId">Item.</param>
    /// <returns>True when accessible.</returns>
    public bool CanSee(Guid userId, Guid itemId)
    {
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue((userId, itemId), out var hit) && hit.Until > now)
        {
            return hit.Visible;
        }

        if (_cache.Count > MaxEntries)
        {
            _cache.Clear();
        }

        var visible = _library.GetNode(userId, itemId) is not null;
        _cache[(userId, itemId)] = (visible, now + Lifetime);
        return visible;
    }
}
