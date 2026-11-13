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
    /// <summary>Gets the defaults: four minutes, checked every second (D33; was two, D53).</summary>
    public static BazarrDownloadTiming Default { get; } = new(TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(1));
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
    private readonly DownloadGuard _guard = DownloadGuard.Shared;
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
    /// <param name="replace">
    /// Download new (D52): replace the subtitle of that language that is already there. The new one is re-timed; the
    /// previous one is kept next to it as <c>&lt;name&gt;.old</c>.
    /// </param>
    /// <returns>The outcome, or an error.</returns>
    public async Task<(BazarrDownloadDto? Result, BazarrError? Error)> DownloadAsync(Guid userId, Guid itemId, string? resultId, CancellationToken cancellationToken, bool replace = false)
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

        // English and Danish subtitles are re-timed (Danish only on English audio), so only they need SubSync and room
        // in the queue (D48, D50).
        var code = BazarrSearchService.LanguageCodeOf(stored.Result.Language);
        var language = BazarrSearchService.IsEnglish(stored.Result.Language) ? SubtitleLanguage.English : SubtitleLanguage.Parse(code);  // en-us, en-gb are English
        var fingerprint = VideoFingerprint.FromFile(video.VideoPath!);
        var english = language == SubtitleLanguage.English;
        if (english && !EligibilityService.HasEnglishAudio(video, fingerprint))
        {
            return Fail(StatusCodes.Status409Conflict, BazarrErrorCodes.NoEnglishAudio, "This item has no English audio, so an English subtitle can't be re-timed to it.");
        }

        var retime = language is not null && EligibilityService.HasEnglishAudio(video, fingerprint);
        if (retime && !replace && EligibilityService.FindSidecar(video.VideoPath!, language!) is { } present)
        {
            return Fail(StatusCodes.Status409Conflict, BazarrErrorCodes.HasSubtitle, $"This item already has a {SubtitleLanguage.Name(language!)} subtitle ({Path.GetFileName(present.Path)}). Choose Download new to replace it.");
        }

        var maxQueued = config.MaxQueuedEpisodes;
        if (retime)
        {
            if (!_worker.IsConfigured)
            {
                return Fail(StatusCodes.Status503ServiceUnavailable, ErrorCodes.SubSyncUnavailable, "SubSync is not configured, so the subtitle couldn't be re-timed. Ask an administrator.");
            }

            // Don't download a subtitle nobody can re-time while SubSync is being updated (D46).
            if (_queue.Snapshot().Maintenance is { } maintenance)
            {
                return Fail(StatusCodes.Status503ServiceUnavailable, ErrorCodes.SubSyncUnavailable, maintenance);
            }

            if (_queue.Snapshot().Pending >= maxQueued)
            {
                return Fail(StatusCodes.Status409Conflict, ErrorCodes.QueueFull, $"The queue is full ({maxQueued}). Try again when it has room.");
            }
        }

        // Never touch the files of an item that is being synced: the download's snapshot and restore would undo the
        // sync, or the sync's backup would undo the download (D52). The guard makes a job that starts meanwhile wait.
        if (_queue.Snapshot().Items.Any(i => i.ItemId == itemId && i.Status is JobItemStatus.Queued or JobItemStatus.Running)
            || (await _state.GetInFlightAsync(cancellationToken).ConfigureAwait(false)).Any(f => f.ItemId == itemId))
        {
            return Fail(StatusCodes.Status409Conflict, BazarrErrorCodes.ItemBusy, "This item is in the queue to be synced. Try again when it's done.");
        }

        if (!_items.TryAdd(itemId, 0))
        {
            return Fail(StatusCodes.Status409Conflict, BazarrErrorCodes.DownloadInProgress, "A subtitle is already being downloaded for this item.");
        }

        if (!_guard.TryEnter(itemId))
        {
            _items.TryRemove(itemId, out _);
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
                return await DownloadLockedAsync(connection!, video, stored, maxQueued, userId, cancellationToken, retime ? language : null, replace).ConfigureAwait(false);
            }
            finally
            {
                _users.TryRemove(userId, out _);
            }
        }
        finally
        {
            _guard.Exit(itemId);
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

    // Which language and whether one is already there is decided once the result is known (DownloadAsync).
    private static BazarrError? Refusal(VideoFacts video) => EligibilityService.Evaluate(video, null).Reason switch
    {
        IneligibleReason.NoVideoFile => new BazarrError(StatusCodes.Status409Conflict, BazarrErrorCodes.NoVideoFile, "Jellyfin has no video file for this item."),
        _ => null,
    };

    internal static string DisplayName(LibraryNode node)
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
        CancellationToken cancellationToken,
        string? retimeLanguage,
        bool replace)
    {
        var videoPath = video.VideoPath!;
        var copies = await SnapshotAsync(videoPath, cancellationToken).ConfigureAwait(false);
        if (copies is null)
        {
            return Fail(StatusCodes.Status409Conflict, BazarrErrorCodes.DownloadFailed, $"A subtitle file next to the video is larger than {MaxSnapshotBytes / (1024 * 1024)} MB, so it can't be backed up safely.");
        }

        // A subtitle may have appeared since the checks above; never let Bazarr overwrite it unless asked to (D52).
        if (retimeLanguage is not null && !replace && EligibilityService.FindSidecar(videoPath, retimeLanguage) is { } present)
        {
            return Fail(StatusCodes.Status409Conflict, BazarrErrorCodes.HasSubtitle, $"This item already has a {SubtitleLanguage.Name(retimeLanguage)} subtitle ({Path.GetFileName(present.Path)}). Choose Download new to replace it.");
        }

        var before = copies.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Another language: Bazarr would overwrite a subtitle already there in that language, and putting the old one
        // back would undo the download. So refuse instead (D48); Download new only replaces English and Danish ones.
        if (retimeLanguage is null)
        {
            var language = BazarrSearchService.LanguageCodeOf(stored.Result.Language);
            var baseName = Path.GetFileNameWithoutExtension(videoPath);
            var existing = before.Select(Path.GetFileName).FirstOrDefault(n => n is not null
                && !n.Equals(baseName + ".srt", StringComparison.OrdinalIgnoreCase)
                && IsLanguageFile(n, baseName, language));
            if (existing is not null)
            {
                return Fail(StatusCodes.Status409Conflict, BazarrErrorCodes.HasSubtitle, $"This item already has a {language.ToUpperInvariant()} subtitle ({existing}). Bazarr would replace it, so it wasn't downloaded.");
            }
        }

        // Download new: every subtitle of that language is copied to a new <name>.old on disk before Bazarr can write,
        // so it is never only in memory (D52). No copy, no download.
        var kept = new List<(string Path, string Old)>();
        if (retimeLanguage is not null && replace)
        {
            foreach (var path in SameLanguage(videoPath, retimeLanguage))
            {
                var old = OldPath(path);
                try
                {
                    File.Copy(path, old, overwrite: false);
                    kept.Add((path, old));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(ex, "Could not keep a copy of {Path}; nothing downloaded", path);
                    DeleteQuietly(kept.Select(k => k.Old));
                    return Fail(StatusCodes.Status409Conflict, BazarrErrorCodes.DownloadFailed, $"Couldn't keep a copy of {Path.GetFileName(path)} next to it ({ex.Message}), so nothing was downloaded.");
                }
            }
        }

        var accepted = await _client.DownloadAsync(connection, stored.Target, stored.Result, cancellationToken).ConfigureAwait(false);
        if (!accepted.Ok)
        {
            await RestoreAsync(copies, _logger).ConfigureAwait(false);
            DeleteQuietly(kept.Select(k => k.Old));  // nothing was written: the copies aren't needed
            var error = accepted.Failure switch
            {
                BazarrFailure.NotFound => new BazarrError(StatusCodes.Status404NotFound, BazarrErrorCodes.NotFound, "Bazarr doesn't know this item any more."),
                BazarrFailure.Failed => new BazarrError(StatusCodes.Status502BadGateway, BazarrErrorCodes.DownloadFailed, accepted.Message!),
                _ => new BazarrError(StatusCodes.Status503ServiceUnavailable, BazarrErrorCodes.Unavailable, accepted.Message!),
            };
            return (null, error);
        }

        if (retimeLanguage is null)
        {
            return await FinishOtherLanguageAsync(videoPath, video, stored, before, copies).ConfigureAwait(false);
        }

        var lang = retimeLanguage;

        // From here on the caller's token is ignored: Bazarr has accepted the job and will write the file anyway, so
        // closing the dialog must not undo a download that is about to land unqueued. The wait is bounded (D33).
        var sidecar = await WaitForSidecarAsync(videoPath, lang, copies, CancellationToken.None).ConfigureAwait(false);

        if (sidecar is null)
        {
            var strays = NewFiles(videoPath, before);
            RemoveNewAccepted(videoPath, lang, before);
            await RestoreAsync(copies, _logger).ConfigureAwait(false);
            // the .old copies stay: Bazarr may still write after the wait (D33), and then the copy is what's left
            var names = string.Join(" or ", SubtitleLanguage.Tags(lang).Take(2).Select(t => "." + t + ".srt"));
            var hint = strays.Count > 0
                ? $" Bazarr saved {string.Join(", ", strays)}, a name Subtitle Fixer doesn't re-time; it needs <video>{names} (check Bazarr's 'single language' and hearing-impaired extension settings)."
                : " Check Bazarr's System > Logs: the provider may have throttled it, or the search result expired in Bazarr. Search again and pick another result.";
            _logger.LogWarning("Bazarr download from {Provider} for {ItemId} produced no usable subtitle within {Seconds} s", stored.Result.Provider, video.Node.Id, _timing.WaitTimeout.TotalSeconds);
            return Fail(StatusCodes.Status502BadGateway, BazarrErrorCodes.DownloadFailed, $"Bazarr didn't save a usable {SubtitleLanguage.Name(lang)} subtitle within {_timing.WaitTimeout.TotalSeconds:0} seconds." + hint);
        }

        // Anything else Bazarr overwrote goes back; the new (or, with Download new, replaced) sidecar stays. An older
        // one of the same language that would be synced instead of the new one is removed (its .old copy is on disk);
        // if that fails, the download is undone (D52).
        var replaced = copies.FirstOrDefault(c => string.Equals(c.Path, sidecar.Path, StringComparison.OrdinalIgnoreCase));
        await RestoreAsync(copies.Where(c => c != replaced).ToList(), _logger).ConfigureAwait(false);
        if (RemoveShadows(videoPath, lang, sidecar, kept) is { } stuck)
        {
            await RestoreAsync(copies, _logger).ConfigureAwait(false);
            RemoveNewAccepted(videoPath, lang, before);
            DeleteQuietly(kept.Select(k => k.Old));  // everything is back as it was
            return Fail(StatusCodes.Status409Conflict, BazarrErrorCodes.DownloadFailed, $"{Path.GetFileName(stuck)} couldn't be moved aside, so the new subtitle wouldn't be the one synced. The download was undone; your subtitles are as they were.");
        }

        // keep the copies of what was replaced or moved aside; the others were untouched
        var keptOld = new List<string>();
        foreach (var (path, old) in kept)
        {
            if (string.Equals(path, sidecar.Path, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                keptOld.Add(Path.GetFileName(old));
            }
            else
            {
                DeleteQuietly([old]);
            }
        }

        // A fresh subtitle must be synced even if this video was synced before (BAZARR-PLAN.md rule 5).
        await _state.DeleteAsync(video.Node.Id, lang, CancellationToken.None).ConfigureAwait(false);

        var note = "downloaded from " + stored.Result.Provider;
        var series = video.Node.SeriesName ?? string.Empty;
        var queued = _queue.Enqueue(userId, [new JobRequestItem(video.Node.Id, DisplayName(video.Node), series, lang)], JobSource.Bazarr, note, maxQueued);
        var name = Path.GetFileName(sidecar.Path) + (keptOld.Count > 0 ? $" (the previous one is kept as {string.Join(", ", keptOld)})" : string.Empty);
        _logger.LogInformation("Bazarr downloaded {Subtitle} from {Provider} for {ItemId}; queued: {Queued}", name, stored.Result.Provider, video.Node.Id, queued.Error is null);
        return queued.Error is null
            ? (new BazarrDownloadDto(true, name, $"Downloaded {name} from {stored.Result.Provider}. It's in the queue to be re-timed."), null)
            : (new BazarrDownloadDto(false, name, queued.Error.Code == ErrorCodes.SubSyncUnavailable
                ? $"Downloaded {name} from {stored.Result.Provider}, but SubSync is being updated. Select it later to re-time it."
                : $"Downloaded {name} from {stored.Result.Provider}, but the queue is full. Select it later to re-time it."), null);
    }

    /// <summary>
    /// A subtitle in another language (D48): it is kept as Bazarr saved it and never re-timed or queued. Success is a
    /// new <c>&lt;video&gt;.&lt;lang&gt;[.hi|.forced].srt</c> (or <c>&lt;video&gt;.srt</c>, Bazarr's "single language")
    /// that parses as SRT. Every file that existed before is put back if Bazarr changed it.
    /// </summary>
    private async Task<(BazarrDownloadDto?, BazarrError?)> FinishOtherLanguageAsync(
        string videoPath,
        VideoFacts video,
        StoredBazarrResult stored,
        HashSet<string> before,
        IReadOnlyList<SubtitleCopy> copies)
    {
        var language = BazarrSearchService.LanguageCodeOf(stored.Result.Language);
        var found = await WaitForNewSubtitleAsync(videoPath, before, language, CancellationToken.None).ConfigureAwait(false);
        await RestoreAsync(copies, _logger).ConfigureAwait(false);
        if (found is null)
        {
            var strays = NewFiles(videoPath, before);
            _logger.LogWarning("Bazarr download from {Provider} ({Language}) for {ItemId} produced no usable subtitle within {Seconds} s", stored.Result.Provider, language, video.Node.Id, _timing.WaitTimeout.TotalSeconds);
            var hint = strays.Count > 0 ? $" Bazarr saved {string.Join(", ", strays)}, which isn't a readable .srt." : " Check Bazarr's System > Logs, or search again and pick another result.";
            return Fail(StatusCodes.Status502BadGateway, BazarrErrorCodes.DownloadFailed, $"Bazarr didn't save a usable {language.ToUpperInvariant()} subtitle within {_timing.WaitTimeout.TotalSeconds:0} seconds." + hint);
        }

        var name = Path.GetFileName(found);
        _logger.LogInformation("Bazarr downloaded {Subtitle} ({Language}) from {Provider} for {ItemId}; not re-timed (not English)", name, language, stored.Result.Provider, video.Node.Id);
        var why = SubtitleLanguage.Parse(language) is not null
            ? "Subtitle Fixer only re-times subtitles on English audio, so this one stays as it is."
            : "Subtitle Fixer only re-times English and Danish subtitles, so this one stays as it is.";
        return (new BazarrDownloadDto(false, name, $"Downloaded {name} from {stored.Result.Provider}. {why}"), null);
    }

    /// <summary>Waits for a new subtitle in the given language next to the video that parses as SRT and has stopped growing.</summary>
    private async Task<string?> WaitForNewSubtitleAsync(string videoPath, HashSet<string> before, string language, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(videoPath);
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(baseName))
        {
            return null;
        }

        var deadline = DateTime.UtcNow + _timing.WaitTimeout;
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(_timing.PollInterval, cancellationToken).ConfigureAwait(false);
            foreach (var file in Directory.EnumerateFiles(folder, "*.srt"))
            {
                if (before.Contains(file) || !IsLanguageFile(Path.GetFileName(file), baseName, language))
                {
                    continue;
                }

                long size;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    continue;
                }

                if (sizes.TryGetValue(file, out var last) && last == size && SubtitleVerifier.IsValidSrt(file, out _))
                {
                    return file;
                }

                sizes[file] = size;
            }
        }

        return null;
    }

    /// <summary><c>&lt;base&gt;.srt</c>, or <c>&lt;base&gt;.&lt;lang&gt;[.hi|.cc|.sdh|.forced].srt</c>, ignoring case.</summary>
    internal static bool IsLanguageFile(string name, string baseName, string language)
    {
        if (name.Equals(baseName + ".srt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
            || name.Length <= baseName.Length + 5)
        {
            return false;
        }

        var middle = name[(baseName.Length + 1)..^4];
        var tag = middle.Split('.')[0];
        return tag.Equals(language, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Waits until an accepted sidecar of the language is new or changed (Download new writes over one that was there),
    /// parses as SRT and has stopped growing.
    /// </summary>
    private async Task<Sidecar?> WaitForSidecarAsync(string videoPath, string language, IReadOnlyList<SubtitleCopy> copies, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _timing.WaitTimeout;
        long? lastSize = null;
        string? lastPath = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(_timing.PollInterval, cancellationToken).ConfigureAwait(false);
            var sidecar = FreshSidecar(videoPath, language, copies);
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

            if (lastSize == size && lastPath == sidecar.Path && SubtitleVerifier.IsValidSrt(sidecar.Path, out _))
            {
                return sidecar;
            }

            lastSize = size;
            lastPath = sidecar.Path;
        }

        return null;
    }

    /// <summary>The first accepted sidecar of the language (priority order) that didn't exist before or has changed.</summary>
    private static Sidecar? FreshSidecar(string videoPath, string language, IReadOnlyList<SubtitleCopy> copies)
    {
        var folder = Path.GetDirectoryName(videoPath);
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(baseName))
        {
            return null;
        }

        foreach (var tag in SubtitleLanguage.Tags(language))
        {
            var path = Path.Combine(folder, baseName + "." + tag + ".srt");
            if (!File.Exists(path))
            {
                continue;
            }

            var copy = copies.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
            try
            {
                if (copy is null || File.GetLastWriteTimeUtc(path) != copy.LastWriteUtc || !File.ReadAllBytes(path).AsSpan().SequenceEqual(copy.Bytes))
                {
                    return new Sidecar(path, tag, language);
                }
            }
            catch (IOException)
            {
                // still being written
            }
        }

        return null;
    }

    /// <summary>Deletes an accepted sidecar of the language that wasn't there before (a broken or late download).</summary>
    private void RemoveNewAccepted(string videoPath, string language, HashSet<string> before)
    {
        var folder = Path.GetDirectoryName(videoPath);
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(baseName))
        {
            return;
        }

        foreach (var tag in SubtitleLanguage.Tags(language))
        {
            var path = Path.Combine(folder, baseName + "." + tag + ".srt");
            if (!File.Exists(path) || before.Contains(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                _logger.LogWarning("Removed {Path}: Bazarr's download didn't finish as a valid subtitle", path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not remove {Path}", path);
            }
        }
    }

    /// <summary>The accepted sidecars of a language next to the video, in priority order.</summary>
    private static List<string> SameLanguage(string videoPath, string language)
    {
        var folder = Path.GetDirectoryName(videoPath)!;
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        return SubtitleLanguage.Tags(language).Select(t => Path.Combine(folder, baseName + "." + t + ".srt")).Where(File.Exists).ToList();
    }

    /// <summary><c>&lt;name&gt;.old</c>, or <c>.old.1</c>, <c>.old.2</c>… so an earlier copy is never overwritten.</summary>
    internal static string OldPath(string path)
    {
        var old = path + ".old";
        for (var n = 1; File.Exists(old); n++)
        {
            old = path + ".old." + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return old;
    }

    private void DeleteQuietly(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            try
            {
                File.Delete(p);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove {Path}", p);
            }
        }
    }

    /// <summary>
    /// After Download new: an older sidecar of the same language that comes before the new one in priority order
    /// (the old <c>.en.srt</c> when Bazarr wrote <c>.en.hi.srt</c>) would be synced instead, so it is removed. Only
    /// files with a <c>.old</c> copy on disk are removed. Returns the file that couldn't be removed, or null.
    /// </summary>
    private string? RemoveShadows(string videoPath, string language, Sidecar fresh, List<(string Path, string Old)> kept)
    {
        foreach (var path in SameLanguage(videoPath, language))
        {
            if (string.Equals(path, fresh.Path, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var copy = kept.FirstOrDefault(k => string.Equals(k.Path, path, StringComparison.OrdinalIgnoreCase));
            try
            {
                if (copy.Old is null || !File.Exists(copy.Old) || new FileInfo(copy.Old).Length != new FileInfo(path).Length)
                {
                    return path;  // no safe copy: leave it, and undo the download
                }

                File.Delete(path);
                _logger.LogInformation("Moved {Path} aside (kept as {Old}) so the new subtitle is the one synced", path, copy.Old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not move {Path} aside", path);
                return path;
            }
        }

        return null;
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
