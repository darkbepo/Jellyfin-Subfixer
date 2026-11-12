using System.Collections.Concurrent;
using Jellyfin.Plugin.SubtitleFixer.Configuration;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.State;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Bazarr;

/// <summary><c>POST /Bazarr/Download</c> response.</summary>
/// <param name="Queued">Whether the new subtitle went into the SubSync queue.</param>
/// <param name="Subtitle">File name of the downloaded subtitle.</param>
/// <param name="Message">What happened, for people.</param>
public sealed record BazarrDownloadDto(bool Queued, string Subtitle, string Message);

/// <summary>Timing of the wait for Bazarr's file. Tests shorten it.</summary>
/// <param name="WaitTimeout">How long to wait for the subtitle to appear.</param>
/// <param name="PollInterval">How often to look.</param>
public sealed record BazarrDownloadTiming(TimeSpan WaitTimeout, TimeSpan PollInterval)
{
    /// <summary>Gets the defaults: two minutes, checked every second (D33).</summary>
    public static BazarrDownloadTiming Default { get; } = new(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1));
}

/// <summary>
/// Downloads one Find subtitles result through Bazarr and queues it for re-timing (BAZARR-PLAN.md phase 2).
/// Never loses a subtitle (hard rule 3): every subtitle file next to the video is copied into memory first, and any
/// that Bazarr changed is put back unless the download produced a good accepted sidecar. Bazarr 1.6.2 downloads in a
/// background job and doesn't report the result, so success is the accepted sidecar appearing as valid SRT.
/// </summary>
public sealed class BazarrDownloadService
{
    /// <summary>Largest subtitle file copied into memory; anything bigger stops the download.</summary>
    public const long MaxSnapshotBytes = 10 * 1024 * 1024;

    /// <summary>Subtitle extensions Bazarr can write next to a video.</summary>
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".smi"];

    private readonly ILibraryFacade _library;
    private readonly BazarrResultStore _store;
    private readonly IBazarrClient _client;
    private readonly IStateStore _state;
    private readonly JobQueue _queue;
    private readonly ISubSyncWorker _worker;
    private readonly ILogger<BazarrDownloadService> _logger;
    private readonly Func<PluginConfiguration?> _config;
    private readonly BazarrDownloadTiming _timing;
    private readonly ConcurrentDictionary<Guid, byte> _items = new();
    private readonly ConcurrentDictionary<Guid, byte> _users = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BazarrDownloadService"/> class.
    /// </summary>
    /// <param name="library">Library facade.</param>
    /// <param name="store">Search results behind our handles.</param>
    /// <param name="client">Bazarr client.</param>
    /// <param name="state">State store (the <c>done</c> record is cleared before queuing).</param>
    /// <param name="queue">The shared SubSync queue.</param>
    /// <param name="worker">SubSync worker (only <see cref="ISubSyncWorker.IsConfigured"/> is used).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="config">Settings; the plugin's when null.</param>
    /// <param name="timing">Wait timing; the defaults when null.</param>
    public BazarrDownloadService(
        ILibraryFacade library,
        BazarrResultStore store,
        IBazarrClient client,
        IStateStore state,
        JobQueue queue,
        ISubSyncWorker worker,
        ILogger<BazarrDownloadService> logger,
        Func<PluginConfiguration?>? config = null,
        BazarrDownloadTiming? timing = null)
    {
        _library = library;
        _store = store;
        _client = client;
        _state = state;
        _queue = queue;
        _worker = worker;
        _logger = logger;
        _config = config ?? (() => Plugin.Instance?.Configuration);
        _timing = timing ?? BazarrDownloadTiming.Default;
    }

    /// <summary>Downloads a result and queues the new subtitle for re-timing.</summary>
    /// <param name="userId">Caller; <c>AllowedUsers</c> was already checked.</param>
    /// <param name="itemId">Episode or movie.</param>
    /// <param name="resultId">Our handle from <c>GET /Bazarr/Search</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome, or an error.</returns>
    public async Task<(BazarrDownloadDto? Result, BazarrError? Error)> DownloadAsync(Guid userId, Guid itemId, string? resultId, CancellationToken cancellationToken)
    {
        var config = _config();
        if (config is not { BazarrEnabled: true } || !BazarrConnection.TryCreate(config.BazarrUrl, config.BazarrApiKey, out var connection, out _))
        {
            return Fail(StatusCodes.Status503ServiceUnavailable, BazarrErrorCodes.Unavailable, "Bazarr search isn't set up. Ask an administrator to turn it on in the plugin settings.");
        }

        var video = _library.GetVideo(userId, itemId);
        if (video is null)
        {
            return Fail(StatusCodes.Status404NotFound, BazarrErrorCodes.ItemNotFound, "That item was not found or you cannot access it.");
        }

        if (Refusal(video) is { } refused)
        {
            return (null, refused);
        }

        var stored = _store.Get(resultId, userId, itemId);
        if (stored is null)
        {
            return Fail(StatusCodes.Status410Gone, BazarrErrorCodes.ResultExpired, "That search result has expired. Search again.");
        }

        if (!_worker.IsConfigured)
        {
            return Fail(StatusCodes.Status503ServiceUnavailable, ErrorCodes.SubSyncUnavailable, "SubSync is not configured, so the subtitle couldn't be re-timed. Ask an administrator.");
        }

        // Don't download a subtitle nobody can re-time while SubSync is being updated (D46).
        if (_queue.Snapshot().Maintenance is { } maintenance)
        {
            return Fail(StatusCodes.Status503ServiceUnavailable, ErrorCodes.SubSyncUnavailable, maintenance);
        }

        var maxQueued = config.MaxQueuedEpisodes;
        if (_queue.Snapshot().Pending >= maxQueued)
        {
            return Fail(StatusCodes.Status409Conflict, ErrorCodes.QueueFull, $"The queue is full ({maxQueued}). Try again when it has room.");
        }

        if (!_items.TryAdd(itemId, 0))
        {
            return Fail(StatusCodes.Status409Conflict, BazarrErrorCodes.DownloadInProgress, "A subtitle is already being downloaded for this item.");
        }

        try
        {
            if (!_users.TryAdd(userId, 0))
            {
                return Fail(StatusCodes.Status429TooManyRequests, BazarrErrorCodes.RateLimited, "Wait for your other download to finish.", 5);
            }

            try
            {
                return await DownloadLockedAsync(connection!, video, stored, maxQueued, userId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _users.TryRemove(userId, out _);
            }
        }
        finally
        {
            _items.TryRemove(itemId, out _);
        }
    }

    /// <summary>Copies every subtitle file next to the video into memory.</summary>
    /// <param name="videoPath">Video.</param>
    /// <returns>The copies, or null when one is too large to hold.</returns>
    internal static async Task<IReadOnlyList<SubtitleCopy>?> SnapshotAsync(string videoPath, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(videoPath);
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(baseName) || !Directory.Exists(folder))
        {
            return [];
        }

        var copies = new List<SubtitleCopy>();
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (!IsSubtitleOf(file, baseName))
            {
                continue;
            }

            var info = new FileInfo(file);
            if (info.Length > MaxSnapshotBytes)
            {
                return null;
            }

            copies.Add(new SubtitleCopy(file, await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false), info.LastWriteTimeUtc));
        }

        return copies;
    }

    /// <summary>Puts back every copied file that changed or disappeared.</summary>
    /// <param name="copies">Copies from <see cref="SnapshotAsync"/>.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>How many files were restored.</returns>
    internal static async Task<int> RestoreAsync(IReadOnlyList<SubtitleCopy> copies, ILogger logger)
    {
        var restored = 0;
        foreach (var copy in copies)
        {
            try
            {
                // Never cancelled: a restore must finish even when the request is gone.
                if (File.Exists(copy.Path) && (await File.ReadAllBytesAsync(copy.Path, CancellationToken.None).ConfigureAwait(false)).AsSpan().SequenceEqual(copy.Bytes))
                {
                    continue;
                }

                await File.WriteAllBytesAsync(copy.Path, copy.Bytes, CancellationToken.None).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(copy.Path, copy.LastWriteUtc);
                restored++;
                logger.LogWarning("Restored {Path} after a Bazarr download changed it", copy.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Could not restore {Path}; its content is still in memory only", copy.Path);
            }
        }

        return restored;
    }

    private static bool IsSubtitleOf(string file, string baseName)
    {
        var name = Path.GetFileName(file);
        return name.Length > baseName.Length
            && name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)
            && SubtitleExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    private static (BazarrDownloadDto?, BazarrError?) Fail(int status, string code, string message, int? retryAfter = null) =>
        (null, new BazarrError(status, code, message, retryAfter));

    private static BazarrError? Refusal(VideoFacts video) => EligibilityService.Evaluate(video, null).Reason switch
    {
        IneligibleReason.None => new BazarrError(StatusCodes.Status409Conflict, BazarrErrorCodes.HasSubtitle, "This item already has an English subtitle. Re-time it instead."),
        IneligibleReason.NoVideoFile => new BazarrError(StatusCodes.Status409Conflict, BazarrErrorCodes.NoVideoFile, "Jellyfin has no video file for this item."),
        IneligibleReason.NoEnglishAudio => new BazarrError(StatusCodes.Status409Conflict, BazarrErrorCodes.NoEnglishAudio, "This item has no English audio, so an English subtitle can't be re-timed to it."),
        _ => null,
    };

    private static string DisplayName(LibraryNode node)
    {
        if (node.Kind == NodeKind.Movie)
        {
            return node.ProductionYear is { } year ? $"{node.Name} ({year})" : node.Name;
        }

        var code = (node.ParentIndexNumber is { } s ? $"S{s:00}" : string.Empty) + (node.IndexNumber is { } e ? $"E{e:00}" : string.Empty);
        return code.Length == 0 ? node.Name : code + " " + node.Name;
    }

    private async Task<(BazarrDownloadDto?, BazarrError?)> DownloadLockedAsync(
        BazarrConnection connection,
        VideoFacts video,
        StoredBazarrResult stored,
        int maxQueued,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var videoPath = video.VideoPath!;
        var copies = await SnapshotAsync(videoPath, cancellationToken).ConfigureAwait(false);
        if (copies is null)
        {
            return Fail(StatusCodes.Status409Conflict, BazarrErrorCodes.DownloadFailed, $"A subtitle file next to the video is larger than {MaxSnapshotBytes / (1024 * 1024)} MB, so it can't be backed up safely.");
        }

        // A subtitle may have appeared since the checks above; never let Bazarr overwrite it.
        if (EligibilityService.FindSidecar(videoPath) is not null)
        {
            return Fail(StatusCodes.Status409Conflict, BazarrErrorCodes.HasSubtitle, "This item already has an English subtitle. Re-time it instead.");
        }

        var before = copies.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accepted = await _client.DownloadAsync(connection, stored.Target, stored.Result, cancellationToken).ConfigureAwait(false);
        if (!accepted.Ok)
        {
            await RestoreAsync(copies, _logger).ConfigureAwait(false);
            var error = accepted.Failure switch
            {
                BazarrFailure.NotFound => new BazarrError(StatusCodes.Status404NotFound, BazarrErrorCodes.NotFound, "Bazarr doesn't know this item any more."),
                BazarrFailure.Failed => new BazarrError(StatusCodes.Status502BadGateway, BazarrErrorCodes.DownloadFailed, accepted.Message!),
                _ => new BazarrError(StatusCodes.Status503ServiceUnavailable, BazarrErrorCodes.Unavailable, accepted.Message!),
            };
            return (null, error);
        }

        // From here on the caller's token is ignored: Bazarr has accepted the job and will write the file anyway, so
        // closing the dialog must not undo a download that is about to land unqueued. The wait is bounded (D33).
        var sidecar = await WaitForSidecarAsync(videoPath, CancellationToken.None).ConfigureAwait(false);

        if (sidecar is null)
        {
            var strays = NewFiles(videoPath, before);
            RemoveNewAccepted(videoPath, before);
            await RestoreAsync(copies, _logger).ConfigureAwait(false);
            var hint = strays.Count > 0
                ? $" Bazarr saved {string.Join(", ", strays)}, a name Subtitle Fixer doesn't re-time; it needs <video>.en.srt or .en.hi.srt (check Bazarr's 'single language' and hearing-impaired extension settings)."
                : " Check Bazarr's System > Logs: the provider may have throttled it, or the search result expired in Bazarr. Search again and pick another result.";
            _logger.LogWarning("Bazarr download from {Provider} for {ItemId} produced no usable subtitle within {Seconds} s", stored.Result.Provider, video.Node.Id, _timing.WaitTimeout.TotalSeconds);
            return Fail(StatusCodes.Status502BadGateway, BazarrErrorCodes.DownloadFailed, $"Bazarr didn't save a usable English subtitle within {_timing.WaitTimeout.TotalSeconds:0} seconds." + hint);
        }

        // Anything else Bazarr overwrote goes back; the new sidecar didn't exist before, so it stays.
        await RestoreAsync(copies, _logger).ConfigureAwait(false);

        // A fresh subtitle must be synced even if this video was synced before (BAZARR-PLAN.md rule 5).
        await _state.DeleteAsync(video.Node.Id, CancellationToken.None).ConfigureAwait(false);

        var note = "downloaded from " + stored.Result.Provider;
        var series = video.Node.SeriesName ?? string.Empty;
        var queued = _queue.Enqueue(userId, [new JobRequestItem(video.Node.Id, DisplayName(video.Node), series)], JobSource.Bazarr, note, maxQueued);
        var name = Path.GetFileName(sidecar.Path);
        _logger.LogInformation("Bazarr downloaded {Subtitle} from {Provider} for {ItemId}; queued: {Queued}", name, stored.Result.Provider, video.Node.Id, queued.Error is null);
        return queued.Error is null
            ? (new BazarrDownloadDto(true, name, $"Downloaded {name} from {stored.Result.Provider}. It's in the queue to be re-timed."), null)
            : (new BazarrDownloadDto(false, name, queued.Error.Code == ErrorCodes.SubSyncUnavailable
                ? $"Downloaded {name} from {stored.Result.Provider}, but SubSync is being updated. Select it later to re-time it."
                : $"Downloaded {name} from {stored.Result.Provider}, but the queue is full. Select it later to re-time it."), null);
    }

    /// <summary>Waits until an accepted sidecar exists, parses as SRT and has stopped growing.</summary>
    private async Task<Sidecar?> WaitForSidecarAsync(string videoPath, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _timing.WaitTimeout;
        long? lastSize = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(_timing.PollInterval, cancellationToken).ConfigureAwait(false);
            var sidecar = EligibilityService.FindSidecar(videoPath);
            if (sidecar is null)
            {
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(sidecar.Path).Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (lastSize == size && SubtitleVerifier.IsValidSrt(sidecar.Path, out _))
            {
                return sidecar;
            }

            lastSize = size;
        }

        return null;
    }

    /// <summary>Deletes an accepted sidecar that wasn't there before (a broken or late download).</summary>
    private void RemoveNewAccepted(string videoPath, HashSet<string> before)
    {
        if (EligibilityService.FindSidecar(videoPath) is { } s && !before.Contains(s.Path))
        {
            try
            {
                File.Delete(s.Path);
                _logger.LogWarning("Removed {Path}: Bazarr's download didn't finish as a valid subtitle", s.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not remove {Path}", s.Path);
            }
        }
    }

    private static List<string> NewFiles(string videoPath, HashSet<string> before)
    {
        var folder = Path.GetDirectoryName(videoPath);
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(baseName) || !Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder)
            .Where(f => IsSubtitleOf(f, baseName) && !before.Contains(f))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();
    }
}

/// <summary>A subtitle file held in memory during a Bazarr download.</summary>
/// <param name="Path">File.</param>
/// <param name="Bytes">Content.</param>
/// <param name="LastWriteUtc">Modified time, put back on restore.</param>
internal sealed record SubtitleCopy(string Path, byte[] Bytes, DateTime LastWriteUtc);
