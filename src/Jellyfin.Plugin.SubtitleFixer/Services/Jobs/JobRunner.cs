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

/// <summary>An entry that passed re-validation and has its backup, ready for SubSync.</summary>
/// <param name="Job">Entry.</param>
/// <param name="Runtime">Video length, when Jellyfin knows it.</param>
/// <param name="Video">Video fingerprint.</param>
/// <param name="Sidecar">The subtitle to re-time.</param>
/// <param name="Backup">Its backup (journal row and <c>.bak</c>).</param>
/// <param name="Options">Settings for this item, with its time limit (D34).</param>
public sealed record PreparedRun(JobItemSnapshot Job, TimeSpan? Runtime, VideoFingerprint Video, Sidecar Sidecar, InFlightBackup Backup, WorkerOptions Options);

/// <summary>Result of <see cref="JobRunner.PrepareAsync"/>: an outcome, or a prepared run.</summary>
/// <param name="Outcome">Set when the entry is already decided.</param>
/// <param name="Run">Set when SubSync should run.</param>
public sealed record Preparation(RunOutcome? Outcome, PreparedRun? Run);

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

    /// <summary>Starts a SubSync that stays running for the queue (D45); see <see cref="ISubSyncWorker.StartSessionAsync"/>.</summary>
    /// <param name="options">Settings for the session.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The result.</returns>
    public Task<SessionStart> StartSessionAsync(WorkerOptions options, CancellationToken cancellationToken) =>
        _worker.StartSessionAsync(options, cancellationToken);

    /// <summary>Gets the current worker settings (read per item so settings apply at once).</summary>
    /// <returns>Options.</returns>
    public WorkerOptions CurrentOptions() => _options();

    /// <summary>Runs one entry with one SubSync of its own (D5, the fallback of D45).</summary>
    /// <param name="job">Entry.</param>
    /// <param name="cancellationToken">Cancels this entry.</param>
    /// <param name="progress">
    /// Told (stage, item percent, expected seconds): first the estimated total once SubSync is about to start, then each
    /// stage it reports, with the item's share of the bar while SubSync says how far listening is (D40).
    /// </param>
    /// <returns>The outcome.</returns>
    public async Task<RunOutcome> RunAsync(JobItemSnapshot job, CancellationToken cancellationToken, Action<string?, int?, double?>? progress = null)
    {
        var prepared = await PrepareAsync(job, cancellationToken).ConfigureAwait(false);
        return prepared.Outcome ?? await RunPreparedAsync(prepared.Run!, cancellationToken, progress).ConfigureAwait(false);
    }

    /// <summary>
    /// Steps 1 and 2 of JOB-FLOW.md: re-validate, then back up (journal first). Either the entry is already decided
    /// (skipped, file missing, no backup, cancelled) or it is ready for SubSync. From here the caller must end with
    /// <see cref="FinishAsync"/>, <see cref="FailAsync"/> or <see cref="RestoreAsync"/>, or the backup stays for recovery.
    /// </summary>
    /// <param name="job">Entry.</param>
    /// <param name="cancellationToken">Checked before the backup.</param>
    /// <returns>The outcome, or the prepared run.</returns>
    public async Task<Preparation> PrepareAsync(JobItemSnapshot job, CancellationToken cancellationToken)
    {
        // 1. Re-validate: state may have changed since queuing.
        var facts = _library.GetVideo(job.UserId, job.ItemId);
        if (facts is null)
        {
            return Done(new RunOutcome(JobItemStatus.Failed, "The episode is gone or no longer accessible.", null, null));
        }

        var language = SubtitleLanguage.Normalize(job.Language);
        var records = await _state.GetAsync([job.ItemId], language, CancellationToken.None).ConfigureAwait(false);
        records.TryGetValue(job.ItemId, out var previous);
        var check = EligibilityService.Evaluate(facts, previous, language, job.Again);
        switch (check.Reason)
        {
            case IneligibleReason.AlreadySynced:
                return Done(new RunOutcome(JobItemStatus.Skipped, "Already synced.", null, null));
            case IneligibleReason.NoEnglishAudio:
                return Done(new RunOutcome(JobItemStatus.Skipped, "No English audio.", null, null));
            case IneligibleReason.NoVideoFile:
                return Done(new RunOutcome(JobItemStatus.Failed, "The video file is missing or can't be read.", null, null));
            case IneligibleReason.NoSidecar:
                await RecordAsync(job.ItemId, language, check.Video!, SubtitleGuess(check.Video!.Path, language), SyncStatus.Failed, "file missing: subtitle", null).ConfigureAwait(false);
                return Done(new RunOutcome(JobItemStatus.Failed, $"The {SubtitleLanguage.Name(language)} subtitle next to the video is gone.", null, null));
        }

        if (language != SubtitleLanguage.English
            && !SubtitleLanguage.Supported(language, await _worker.GetVersionAsync(CancellationToken.None).ConfigureAwait(false)))
        {
            // nothing recorded: it becomes possible as soon as SubSync is updated
            return Done(new RunOutcome(JobItemStatus.Failed, "Danish subtitles need SubSync 1.5.0 or later. Update SubSync on the Subtitle Fixer settings page.", null, null));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Done(new RunOutcome(JobItemStatus.Cancelled, "Cancelled.", null, null));
        }

        var video = check.Video!;
        var sidecar = check.Sidecar!;

        // 2. Backup. No backup, no run.
        InFlightBackup backup;
        try
        {
            backup = await _backup.CreateAsync(job.ItemId, sidecar.Path, CancellationToken.None, language).ConfigureAwait(false);
        }
        catch (BackupException ex)
        {
            await RecordAsync(job.ItemId, language, video, sidecar.Path, SyncStatus.Failed, "cannot create backup", null).ConfigureAwait(false);
            _logger.LogWarning(ex, "No backup for {Subtitle}; SubSync not started", sidecar.Path);
            return Done(new RunOutcome(JobItemStatus.Failed, ex.Message, null, null));
        }

        // a Danish subtitle is timed by the English one next to the same video, when there is one (SubSync 1.5.0, D50)
        var reference = language == SubtitleLanguage.Danish ? EligibilityService.FindSidecar(video.Path, SubtitleLanguage.English)?.Path : null;
        var options = WithTimeLimit(_options(), facts.Runtime) with { SubtitleLanguage = language, Reference = reference };
        return new Preparation(null, new PreparedRun(job, facts.Runtime, video, sidecar, backup, options));
    }

    /// <summary>Steps 3 to 7 with one SubSync process of its own.</summary>
    /// <param name="run">Prepared run.</param>
    /// <param name="cancellationToken">Cancels this entry.</param>
    /// <param name="progress">See <see cref="RunAsync"/>.</param>
    /// <returns>The outcome.</returns>
    public async Task<RunOutcome> RunPreparedAsync(PreparedRun run, CancellationToken cancellationToken, Action<string?, int?, double?>? progress = null)
    {
        // From here the original must come back unless the new file is verified.
        try
        {
            var options = run.Options;
            if (progress is not null)
            {
                var estimate = RunTimeEstimate.Expect(run.Runtime);
                progress(null, null, estimate?.Total);
                options = options with { OnProgress = p => progress(p.Stage, RunTimeEstimate.Percent(estimate, p.Stage, p.Percent), null) };
            }

            var result = await _worker.RunAsync(run.Video.Path, run.Sidecar.Path, run.Sidecar.LanguageTag, options, cancellationToken).ConfigureAwait(false);
            return await FinishAsync(run, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await FailAsync(run, ex).ConfigureAwait(false);
        }
    }

    /// <summary>Steps 4 to 7 for SubSync's result: verify and keep, or restore and record (failure matrix).</summary>
    /// <param name="run">Prepared run.</param>
    /// <param name="result">SubSync's result, or ours (cancelled, timed out, crashed).</param>
    /// <returns>The outcome.</returns>
    public async Task<RunOutcome> FinishAsync(PreparedRun run, WorkerResult result)
    {
        try
        {
            if (result.Status is WorkerStatus.Synced or WorkerStatus.NoMatch)
            {
                // Both listened to the whole soundtrack, so both say how fast this server is, unless the transcript
                // came from SubSync's cache (RunTimeEstimate.Record skips those).
                RunTimeEstimate.Record(run.Runtime, result);
            }

            return await FinishCoreAsync(run.Job, run.Video, run.Sidecar, run.Backup, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await FailAsync(run, ex).ConfigureAwait(false);
        }
    }

    /// <summary>An unexpected error after the backup: restore, record failed (counts as a crash, D14).</summary>
    /// <param name="run">Prepared run.</param>
    /// <param name="ex">The error.</param>
    /// <returns>The outcome.</returns>
    public async Task<RunOutcome> FailAsync(PreparedRun run, Exception ex)
    {
        _logger.LogError(ex, "Unexpected error while syncing {Subtitle}; restoring the original", run.Sidecar.Path);
        await _backup.RestoreAsync(run.Backup, CancellationToken.None).ConfigureAwait(false);
        await RecordAsync(run.Job.ItemId, run.Sidecar.Language, run.Video, run.Sidecar.Path, SyncStatus.Failed, "unexpected error: " + ex.Message, null).ConfigureAwait(false);
        return new RunOutcome(JobItemStatus.Failed, "Unexpected error: " + ex.Message, null, WorkerStatus.Crashed);
    }

    /// <summary>Puts the original back and records nothing: the item goes back to the queue untried (D45).</summary>
    /// <param name="run">Prepared run.</param>
    /// <returns>A task.</returns>
    public Task RestoreAsync(PreparedRun run) => _backup.RestoreAsync(run.Backup, CancellationToken.None);

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

    private static string SubtitleGuess(string videoPath, string language) =>
        Path.Combine(Path.GetDirectoryName(videoPath) ?? string.Empty, Path.GetFileNameWithoutExtension(videoPath) + "." + SubtitleLanguage.Tags(language)[0] + ".srt");

    private static Preparation Done(RunOutcome outcome) => new(outcome, null);

    private async Task<RunOutcome> FinishCoreAsync(JobItemSnapshot job, VideoFingerprint video, Sidecar sidecar, InFlightBackup backup, WorkerResult result)
    {
        switch (result.Status)
        {
            case WorkerStatus.Synced:
                if (SubtitleVerifier.IsValidSrt(sidecar.Path, out var invalid))
                {
                    await _backup.DiscardAsync(backup).ConfigureAwait(false);
                    await RecordAsync(job.ItemId, sidecar.Language, video, sidecar.Path, SyncStatus.Done, result.Reason, result).ConfigureAwait(false);
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
                await RecordAsync(job.ItemId, sidecar.Language, video, sidecar.Path, SyncStatus.Failed, invalid, result).ConfigureAwait(false);
                return new RunOutcome(JobItemStatus.Failed, invalid, result.Seconds, WorkerStatus.Failed);

            case WorkerStatus.NoMatch:
                await _backup.RestoreAsync(backup, CancellationToken.None).ConfigureAwait(false);
                await RecordAsync(job.ItemId, sidecar.Language, video, sidecar.Path, SyncStatus.Check, result.Reason, result).ConfigureAwait(false);
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
                await RecordAsync(job.ItemId, sidecar.Language, video, sidecar.Path, SyncStatus.Failed, "timeout", result).ConfigureAwait(false);
                return new RunOutcome(JobItemStatus.Failed, result.Reason ?? "SubSync took too long and was stopped.", result.Seconds, result.Status);

            default:
                await _backup.RestoreAsync(backup, CancellationToken.None).ConfigureAwait(false);
                await RecordAsync(job.ItemId, sidecar.Language, video, sidecar.Path, SyncStatus.Failed, result.Reason, result).ConfigureAwait(false);
                return new RunOutcome(JobItemStatus.Failed, result.Reason, result.Seconds, result.Status);
        }
    }

    private Task RecordAsync(Guid itemId, string language, VideoFingerprint video, string subtitlePath, SyncStatus status, string? message, WorkerResult? result) =>
        _state.UpsertAsync(
            new SyncRecord
            {
                ItemId = itemId,
                Language = SubtitleLanguage.Normalize(language),
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
