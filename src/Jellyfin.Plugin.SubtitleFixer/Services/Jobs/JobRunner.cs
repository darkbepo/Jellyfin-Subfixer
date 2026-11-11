using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.State;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Services.Jobs;

/// <summary>How one entry ended, for the queue and the stop rules.</summary>
/// <param name="Status">Entry status.</param>
/// <param name="Message">Reason.</param>
/// <param name="Seconds">SubSync seconds, when it ran.</param>
/// <param name="Worker">Worker outcome, when SubSync ran.</param>
public sealed record RunOutcome(JobItemStatus Status, string? Message, double? Seconds, WorkerStatus? Worker);

/// <summary>
/// One episode, start to finish (JOB-FLOW.md steps 1 to 7 and the failure matrix): re-validate, back up, run,
/// verify, then either keep the new file or restore the original. The original subtitle is restored on every
/// path except a verified success, including exceptions we did not expect.
/// </summary>
public sealed class JobRunner
{
    private readonly ILibraryFacade _library;
    private readonly IStateStore _state;
    private readonly BackupService _backup;
    private readonly ISubSyncWorker _worker;
    private readonly IRefreshService _refresh;
    private readonly Func<WorkerOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<JobRunner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobRunner"/> class.
    /// </summary>
    /// <param name="library">Library facade.</param>
    /// <param name="state">State store.</param>
    /// <param name="backup">Backup service.</param>
    /// <param name="worker">SubSync worker.</param>
    /// <param name="refresh">Refresh service.</param>
    /// <param name="options">Current worker options (read per episode so settings apply at once).</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public JobRunner(
        ILibraryFacade library,
        IStateStore state,
        BackupService backup,
        ISubSyncWorker worker,
        IRefreshService refresh,
        Func<WorkerOptions> options,
        TimeProvider time,
        ILogger<JobRunner> logger)
    {
        _library = library;
        _state = state;
        _backup = backup;
        _worker = worker;
        _refresh = refresh;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Runs one entry.</summary>
    /// <param name="job">Entry.</param>
    /// <param name="cancellationToken">Cancels this entry.</param>
    /// <param name="progress">Told the estimated total seconds once SubSync is about to start, then each stage it reports.</param>
    /// <returns>The outcome.</returns>
    public async Task<RunOutcome> RunAsync(JobItemSnapshot job, CancellationToken cancellationToken, Action<string?, double?>? progress = null)
    {
        // 1. Re-validate: state may have changed since queuing.
        var facts = _library.GetVideo(job.UserId, job.ItemId);
        if (facts is null)
        {
            return new RunOutcome(JobItemStatus.Failed, "The episode is gone or no longer accessible.", null, null);
        }

        var records = await _state.GetAsync([job.ItemId], CancellationToken.None).ConfigureAwait(false);
        records.TryGetValue(job.ItemId, out var previous);
        var check = EligibilityService.Evaluate(facts, previous);
        switch (check.Reason)
        {
            case IneligibleReason.AlreadySynced:
                return new RunOutcome(JobItemStatus.Skipped, "Already synced.", null, null);
            case IneligibleReason.NoEnglishAudio:
                return new RunOutcome(JobItemStatus.Skipped, "No English audio.", null, null);
            case IneligibleReason.NoVideoFile:
                return new RunOutcome(JobItemStatus.Failed, "The video file is missing or can't be read.", null, null);
            case IneligibleReason.NoSidecar:
                await RecordAsync(job.ItemId, check.Video!, SubtitleGuess(check.Video!.Path), SyncStatus.Failed, "file missing: subtitle", null).ConfigureAwait(false);
                return new RunOutcome(JobItemStatus.Failed, "The English subtitle next to the video is gone.", null, null);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new RunOutcome(JobItemStatus.Cancelled, "Cancelled.", null, null);
        }

        var video = check.Video!;
        var sidecar = check.Sidecar!;

        // 2. Backup. No backup, no run.
        InFlightBackup backup;
        try
        {
            backup = await _backup.CreateAsync(job.ItemId, sidecar.Path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (BackupException ex)
        {
            await RecordAsync(job.ItemId, video, sidecar.Path, SyncStatus.Failed, "cannot create backup", null).ConfigureAwait(false);
            _logger.LogWarning(ex, "No backup for {Subtitle}; SubSync not started", sidecar.Path);
            return new RunOutcome(JobItemStatus.Failed, ex.Message, null, null);
        }

        // 3 to 7. From here the original must come back unless the new file is verified.
        try
        {
            var options = WithTimeLimit(_options(), facts.Runtime);
            if (progress is not null)
            {
                progress(null, RunTimeEstimate.Expect(facts.Runtime));
                options = options with { OnStage = stage => progress(stage, null) };
            }

            var result = await _worker.RunAsync(video.Path, sidecar.Path, sidecar.LanguageTag, options, cancellationToken).ConfigureAwait(false);
            if (result.Status is WorkerStatus.Synced or WorkerStatus.NoMatch)
            {
                // Both listened to the whole soundtrack, so both say how fast this server is.
                RunTimeEstimate.Record(facts.Runtime, result.Seconds);
            }

            return await FinishAsync(job, video, sidecar, backup, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while syncing {Subtitle}; restoring the original", sidecar.Path);
            await _backup.RestoreAsync(backup, CancellationToken.None).ConfigureAwait(false);
            await RecordAsync(job.ItemId, video, sidecar.Path, SyncStatus.Failed, "unexpected error: " + ex.Message, null).ConfigureAwait(false);
            return new RunOutcome(JobItemStatus.Failed, "Unexpected error: " + ex.Message, null, WorkerStatus.Crashed);
        }
    }

    /// <summary>
    /// The time limit for one item (D34): the larger of the setting and twice the video's length plus 10 minutes.
    /// SubSync's time grows with the video's length, and on a slow CPU it can approach 1.5x real time, so a fixed
    /// limit stops movies partway on CPU-only servers. The limit is only a guard against a hung run, so it errs long.
    /// When Jellyfin doesn't know the length, the setting is used as is.
    /// </summary>
    /// <param name="options">Options from the settings.</param>
    /// <param name="runtime">Video length, or null.</param>
    /// <returns>Options with the effective time limit.</returns>
    internal static WorkerOptions WithTimeLimit(WorkerOptions options, TimeSpan? runtime)
    {
        if (runtime is not { } length || length <= TimeSpan.Zero)
        {
            return options;
        }

        var scaled = (length * 2) + TimeSpan.FromMinutes(10);
        return scaled > options.Timeout ? options with { Timeout = scaled } : options;
    }

    private static string SubtitleGuess(string videoPath) =>
        Path.Combine(Path.GetDirectoryName(videoPath) ?? string.Empty, Path.GetFileNameWithoutExtension(videoPath) + ".en.srt");

    private async Task<RunOutcome> FinishAsync(JobItemSnapshot job, VideoFingerprint video, Sidecar sidecar, InFlightBackup backup, WorkerResult result)
    {
        switch (result.Status)
        {
            case WorkerStatus.Synced:
                if (SubtitleVerifier.IsValidSrt(sidecar.Path, out var invalid))
                {
                    await _backup.DiscardAsync(backup).ConfigureAwait(false);
                    await RecordAsync(job.ItemId, video, sidecar.Path, SyncStatus.Done, result.Reason, result).ConfigureAwait(false);
                    try
                    {
                        _refresh.RequestRefresh(job.ItemId);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                    {
                        _logger.LogWarning(ex, "Refresh request failed for {Item}; the new subtitle is still on disk", job.ItemId);
                    }

                    return new RunOutcome(JobItemStatus.Done, null, result.Seconds, result.Status);
                }

                await _backup.RestoreAsync(backup, CancellationToken.None).ConfigureAwait(false);
                await RecordAsync(job.ItemId, video, sidecar.Path, SyncStatus.Failed, invalid, result).ConfigureAwait(false);
                return new RunOutcome(JobItemStatus.Failed, invalid, result.Seconds, WorkerStatus.Failed);

            case WorkerStatus.NoMatch:
                await _backup.RestoreAsync(backup, CancellationToken.None).ConfigureAwait(false);
                await RecordAsync(job.ItemId, video, sidecar.Path, SyncStatus.Check, result.Reason, result).ConfigureAwait(false);
                return new RunOutcome(JobItemStatus.Check, result.Reason, result.Seconds, result.Status);

            case WorkerStatus.Cancelled:
                await _backup.RestoreAsync(backup, CancellationToken.None).ConfigureAwait(false);
                return new RunOutcome(JobItemStatus.Cancelled, "Cancelled.", null, result.Status);

            case WorkerStatus.Unavailable:
                // Not attempted: no record (failure matrix, exit 7).
                await _backup.RestoreAsync(backup, CancellationToken.None).ConfigureAwait(false);
                return new RunOutcome(JobItemStatus.Cancelled, result.Reason, null, result.Status);

            case WorkerStatus.TimedOut:
                await _backup.RestoreAsync(backup, CancellationToken.None).ConfigureAwait(false);
                await RecordAsync(job.ItemId, video, sidecar.Path, SyncStatus.Failed, "timeout", result).ConfigureAwait(false);
                return new RunOutcome(JobItemStatus.Failed, result.Reason ?? "SubSync took too long and was stopped.", result.Seconds, result.Status);

            default:
                await _backup.RestoreAsync(backup, CancellationToken.None).ConfigureAwait(false);
                await RecordAsync(job.ItemId, video, sidecar.Path, SyncStatus.Failed, result.Reason, result).ConfigureAwait(false);
                return new RunOutcome(JobItemStatus.Failed, result.Reason, result.Seconds, result.Status);
        }
    }

    private Task RecordAsync(Guid itemId, VideoFingerprint video, string subtitlePath, SyncStatus status, string? message, WorkerResult? result) =>
        _state.UpsertAsync(
            new SyncRecord
            {
                ItemId = itemId,
                Video = video,
                SubtitlePath = subtitlePath,
                Status = status,
                Message = message,
                AudioLanguage = result?.AudioLanguage,
                SubSyncVersion = result?.SubSyncVersion,
                Seconds = result?.Seconds,
                UpdatedUtc = _time.GetUtcNow().UtcDateTime,
            },
            CancellationToken.None);
}
