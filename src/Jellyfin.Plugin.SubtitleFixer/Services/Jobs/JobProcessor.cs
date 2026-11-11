using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Services.Jobs;

/// <summary>
/// The single consumer: one SubSync at a time, in queue order (SPEC.md hard rule 5). Waits for startup recovery
/// before the first run. Stops the queue on exit 7 and after three consecutive crashes (D14).
/// </summary>
public sealed class JobProcessor : BackgroundService
{
    /// <summary>Consecutive crashes that stop the queue (D14).</summary>
    public const int MaxConsecutiveCrashes = 3;

    private readonly JobQueue _queue;
    private readonly JobRunner _runner;
    private readonly StartupStatus _startup;
    private readonly ILogger<JobProcessor> _logger;
    private int _consecutiveCrashes;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobProcessor"/> class.
    /// </summary>
    /// <param name="queue">Queue.</param>
    /// <param name="runner">Runner.</param>
    /// <param name="startup">Startup status (recovery gate).</param>
    /// <param name="logger">Logger.</param>
    public JobProcessor(JobQueue queue, JobRunner runner, StartupStatus startup, ILogger<JobProcessor> logger)
    {
        _queue = queue;
        _runner = runner;
        _startup = startup;
        _logger = logger;
    }

    /// <summary>Runs one taken entry and applies the stop rules. Public for tests.</summary>
    /// <param name="job">Entry.</param>
    /// <returns>A task.</returns>
    public async Task ProcessAsync(RunningJob job)
    {
        RunOutcome outcome;
        try
        {
            outcome = await _runner.RunAsync(job.Snapshot, job.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // JobRunner restores on its own failures; this only means the restore itself failed.
            // The journal keeps the backup, and startup recovery tries again.
            _logger.LogError(ex, "Subtitle Fixer could not finish {Name}; any backup is kept for recovery", job.Snapshot.Name);
            outcome = new RunOutcome(JobItemStatus.Failed, "Failed: " + ex.Message + " If a .bak backup was left, it is restored on the next Jellyfin start.", null, WorkerStatus.Crashed);
        }

        _queue.Complete(job.Snapshot.JobId, outcome.Status, outcome.Message, outcome.Seconds);
        LogOutcome(job.Snapshot, outcome);

        if (outcome.Worker == WorkerStatus.Unavailable)
        {
            Stop(outcome.Message ?? "SubSync is busy or setting up. Close the SubSync window or wait for its setup, then start again.");
            _consecutiveCrashes = 0;
            return;
        }

        if (outcome.Worker == WorkerStatus.Crashed)
        {
            _consecutiveCrashes++;
            if (_consecutiveCrashes >= MaxConsecutiveCrashes)
            {
                Stop($"SubSync crashed {MaxConsecutiveCrashes} times in a row. Its output is in the Jellyfin log; fix the cause, then start again.");
                _consecutiveCrashes = 0;
            }
        }
        else if (outcome.Worker is not null)
        {
            _consecutiveCrashes = 0;
        }
    }

    private void Stop(string reason)
    {
        _logger.LogWarning("Subtitle Fixer stopped the queue: {Reason}", reason);
        _queue.Stop(reason);
    }

    // One line per item: enough to follow a run in the Jellyfin log without paths (the item id finds the file).
    private void LogOutcome(JobItemSnapshot item, RunOutcome outcome)
    {
        var level = outcome.Status is JobItemStatus.Failed or JobItemStatus.Check ? LogLevel.Warning : LogLevel.Information;
        _logger.Log(
            level,
            "Subtitle Fixer {Status}: {Name} ({ItemId}) in {Seconds:0} s{Message}",
            outcome.Status,
            item.Name,
            item.ItemId,
            outcome.Seconds ?? 0,
            string.IsNullOrEmpty(outcome.Message) ? string.Empty : ": " + outcome.Message);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _startup.RecoveryDone.WaitAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                var job = await _queue.TakeAsync(stoppingToken).ConfigureAwait(false);
                await ProcessAsync(job).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. A sync in progress was not handed this token; if Jellyfin is killed mid-run,
            // the journal lets startup recovery restore the subtitle.
        }
    }
}
