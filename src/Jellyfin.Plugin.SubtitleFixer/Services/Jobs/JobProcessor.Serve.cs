using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Services.Jobs;

/// <summary>
/// The queue run with one <c>subsync-cli serve</c> (D45, replacing D5): one SubSync process from the first item until
/// the queue is empty, so start-up is paid once, and the next item is sent while the current one is listening, so its
/// audio is read meanwhile. Each item keeps JOB-FLOW.md's rules: back up and journal before it is sent, verify, swap
/// or restore when its result comes. At most two items are in SubSync. Anything that ends the process early (a
/// cancel, a time limit, a crash, Jellyfin stopping) restores every item that has no result yet; an item that was only
/// stopped because of the other one goes back to the queue untried. Falls back to one <c>sync</c> per item when
/// <c>serve</c> isn't available (SubSync 1.2.0, or it doesn't start).
/// </summary>
public sealed partial class JobProcessor
{
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxWait = TimeSpan.FromDays(1);

    private readonly List<Flight> _inFlight = new();
    private ServeSession? _session;
    private bool _syncOnly;
    private int _runItems;
    private int _runProcesses;

    /// <summary>Gets the items sent to SubSync that have no result yet (tests).</summary>
    internal int InFlightCount => _inFlight.Count;

    /// <summary>Runs the queue from this entry until it is empty, then closes SubSync. Public for tests.</summary>
    /// <param name="first">The entry taken from the queue.</param>
    /// <param name="stoppingToken">Jellyfin is stopping: end SubSync and restore what it had.</param>
    /// <returns>A task.</returns>
    public async Task RunQueueAsync(RunningJob first, CancellationToken stoppingToken)
    {
        var started = DateTime.UtcNow;
        _runItems = 0;
        _runProcesses = 0;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stoppingToken.Register(() => stopped.TrySetResult());
        try
        {
            RunningJob? next = first;
            while (true)
            {
                if (_inFlight.Count == 0)
                {
                    if (next is null && stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    next ??= _queue.TryTake();
                    if (next is null)
                    {
                        break;
                    }

                    var job = next;
                    next = null;
                    await StartItemAsync(job, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await WaitAsync(stopped.Task, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subtitle Fixer hit an unexpected error while SubSync had items; restoring them");
            await AbortInFlightAsync(ex).ConfigureAwait(false);
        }
        finally
        {
            await CloseSessionAsync().ConfigureAwait(false);
            _syncOnly = false;
            if (_runProcesses > 0)
            {
                // One line per queue run, to compare a season's total time with one SubSync per item (D45).
                _logger.LogInformation(
                    "Subtitle Fixer queue run finished: {Items} item(s) through {Processes} SubSync process(es) in {Seconds:0} s",
                    _runItems,
                    _runProcesses,
                    (DateTime.UtcNow - started).TotalSeconds);
            }
        }
    }

    private static WorkerResult Ended(WorkerStatus status, string? reason) => new(status, null, reason, null, 0, null, null);

    private Action<string?, int?, double?> Progress(Guid jobId) =>
        (stage, percent, expected) => _queue.SetProgress(jobId, stage, expected, percent);

    // The first item of a run, or the next one once nothing is in flight: prepare it, make sure a matching SubSync
    // runs, and send it.
    private async Task StartItemAsync(RunningJob job, CancellationToken stoppingToken)
    {
        // Check audio (D49): SubSync's detect, alone. Nothing is in flight here, so the run's serve is closed first
        // (one SubSync at a time); the next sync item starts a new one.
        if (job.Snapshot.Source == JobSource.AudioCheck)
        {
            await CloseSessionAsync().ConfigureAwait(false);
            await RunCheckAsync(job).ConfigureAwait(false);
            return;
        }

        if (_syncOnly)
        {
            await ProcessAsync(job).ConfigureAwait(false);
            return;
        }

        if (await PrepareAsync(job).ConfigureAwait(false) is not { } run)
        {
            return;
        }

        try
        {
            if (_session is { } old && (old.Exited.IsCompleted || !old.Matches(run.Options)))
            {
                // The settings changed (or SubSync ended between items): a new SubSync with the new settings.
                await CloseSessionAsync().ConfigureAwait(false);
            }

            var fresh = false;
            if (_session is null)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.CancellationToken, stoppingToken);
                var start = await _runner.StartSessionAsync(run.Options, linked.Token).ConfigureAwait(false);
                if (start.Session is null)
                {
                    if (start.Failure is { } failure)
                    {
                        await SettleAsync(job, await _runner.FinishAsync(run, Ended(failure, start.Reason)).ConfigureAwait(false), countCrash: true).ConfigureAwait(false);
                        return;
                    }

                    // No serve: this item and the rest of the run get one SubSync each, as before.
                    _syncOnly = true;
                    await SettleAsync(job, await _runner.RunPreparedAsync(run, job.CancellationToken, Progress(job.Snapshot.JobId)).ConfigureAwait(false), countCrash: true).ConfigureAwait(false);
                    return;
                }

                _session = start.Session;
                _runProcesses++;
                fresh = true;
            }

            Send(job, run, includeStartup: fresh);
        }
        catch (Exception ex)
        {
            await SettleAsync(job, await _runner.FailAsync(run, ex).ConfigureAwait(false), countCrash: true).ConfigureAwait(false);
        }
    }

    // Steps 1 and 2 for one item; null when it is already settled (skipped, missing, no backup, cancelled).
    private async Task<PreparedRun?> PrepareAsync(RunningJob job)
    {
        Preparation prepared;
        try
        {
            prepared = await _runner.PrepareAsync(job.Snapshot, job.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subtitle Fixer could not prepare {Name}; any backup is kept for recovery", job.Snapshot.Name);
            await SettleAsync(job, new RunOutcome(JobItemStatus.Failed, "Failed: " + ex.Message + " If a .bak backup was left, it is restored on the next Jellyfin start.", null, WorkerStatus.Crashed), countCrash: true).ConfigureAwait(false);
            return null;
        }

        if (prepared.Outcome is { } done)
        {
            await SettleAsync(job, done, countCrash: true).ConfigureAwait(false);
            return null;
        }

        return prepared.Run;
    }

    // The backup exists (PrepareAsync), so SubSync may now start on the item and write it when finished.
    private void Send(RunningJob job, PreparedRun run, bool includeStartup)
    {
        var session = _session!;
        var jobId = job.Snapshot.JobId;
        var estimate = RunTimeEstimate.Expect(run.Runtime, includeStartup);
        _queue.SetProgress(jobId, null, estimate?.Total);
        var item = session.Send(
            jobId.ToString("N"),
            run.Video.Path,
            run.Sidecar.Path,
            run.Sidecar.Path,
            p => _queue.SetProgress(jobId, p.Stage, null, RunTimeEstimate.Percent(estimate, p.Stage, p.Percent)));
        _inFlight.Add(new Flight(job, run, item));
        _runItems++;
    }

    // Waits for the next thing that matters, then handles it: a result, the current item starting to listen (send the
    // next one), a cancel, a time limit, SubSync ending, or Jellyfin stopping.
    private async Task WaitAsync(Task stopped, CancellationToken stoppingToken)
    {
        var session = _session!;
        var head = _inFlight[0];
        var waits = new List<Task> { session.Exited, stopped };
        foreach (var f in _inFlight)
        {
            waits.Add(f.Item.Result);
            waits.Add(f.Cancelled);
        }

        if (_inFlight.Count == 1 && !head.AheadSent)
        {
            waits.Add(head.Item.ListeningReached);
        }

        var soonest = MaxWait;
        foreach (var f in _inFlight)
        {
            if (f.Item.ClockStarted.IsCompleted)
            {
                var left = f.Item.ClockStarted.Result + f.Run.Options.Timeout - DateTime.UtcNow;
                soonest = left < soonest ? left : soonest;
            }
            else
            {
                waits.Add(f.Item.ClockStarted);
            }
        }

        if (soonest > TimeSpan.Zero)
        {
            using var delay = new CancellationTokenSource();
            waits.Add(Task.Delay(soonest, delay.Token));
            await Task.WhenAny(waits).ConfigureAwait(false);
            await delay.CancelAsync().ConfigureAwait(false);
        }

        // 1. Results that came in.
        foreach (var f in _inFlight.Where(f => f.Item.Result.IsCompleted).ToList())
        {
            await FinishFlightAsync(f, f.Item.Result.Result, countCrash: true).ConfigureAwait(false);
        }

        if (_inFlight.Count == 0)
        {
            return;
        }

        // 2. Anything that ends SubSync.
        var now = DateTime.UtcNow;
        var timedOut = _inFlight.Where(f => f.Item.ClockStarted.IsCompleted && now - f.Item.ClockStarted.Result >= f.Run.Options.Timeout).ToList();
        var cancelled = _inFlight.Any(f => f.Job.CancellationToken.IsCancellationRequested);
        if (stoppingToken.IsCancellationRequested || cancelled || timedOut.Count > 0 || session.Exited.IsCompleted)
        {
            await EndSessionAsync(session, timedOut, stoppingToken.IsCancellationRequested).ConfigureAwait(false);
            return;
        }

        // 3. The only item is listening: send the next one, so its audio is read meanwhile.
        if (_inFlight.Count == 1 && !head.AheadSent && head.Item.ListeningReached.IsCompleted)
        {
            head.AheadSent = true;
            await SendAheadAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SendAheadAsync(CancellationToken stoppingToken)
    {
        var session = _session!;
        while (!stoppingToken.IsCancellationRequested && !session.Exited.IsCompleted)
        {
            // Changed settings: the next item waits for a new SubSync (StartItemAsync), so none is sent ahead.
            if (!session.Matches(_runner.CurrentOptions()))
            {
                return;
            }

            if (_queue.TryTake(syncOnly: true) is not { } job)
            {
                return;
            }

            if (await PrepareAsync(job).ConfigureAwait(false) is not { } run)
            {
                continue;
            }

            if (!session.Matches(run.Options) || session.Exited.IsCompleted)
            {
                await RequeueAsync(job, run).ConfigureAwait(false);
                return;
            }

            try
            {
                Send(job, run, includeStartup: false);
            }
            catch (Exception ex)
            {
                await SettleAsync(job, await _runner.FailAsync(run, ex).ConfigureAwait(false), countCrash: true).ConfigureAwait(false);
            }

            return;
        }
    }

    // SubSync ends with items in it. Each one that has no result is restored; what it is recorded as depends on why.
    private async Task EndSessionAsync(ServeSession session, IReadOnlyCollection<Flight> timedOut, bool shuttingDown)
    {
        var died = session.Exited.IsCompleted && !session.Killed;
        if (!session.Exited.IsCompleted)
        {
            // Waits for the tree to go, so the subtitles it held are free to be restored.
            session.Kill();
        }

        var code = session.Exited.IsCompleted ? await session.Exited.ConfigureAwait(false) : -1;
        if (died)
        {
            _logger.LogWarning("SubSync stopped unexpectedly (exit {Exit}) with {Count} item(s) unfinished. Output: {Output}", code, _inFlight.Count, session.Tail());
        }

        var crashCounted = false;
        foreach (var f in _inFlight.ToList())
        {
            if (f.Item.Result.IsCompleted)
            {
                // Its result came just before the end; SubSync had written it.
                await FinishFlightAsync(f, f.Item.Result.Result, countCrash: true).ConfigureAwait(false);
                continue;
            }

            WorkerResult? result = null;
            if (died)
            {
                result = code == SubSyncOutcome.ExitUnavailable
                    ? Ended(WorkerStatus.Unavailable, "SubSync could not run: close any open SubSync window or wait for its setup to finish (exit 7).")
                    : Ended(WorkerStatus.Crashed, $"SubSync stopped unexpectedly (exit {code}) before this item was finished.");
            }
            else if (shuttingDown || f.Job.CancellationToken.IsCancellationRequested)
            {
                result = Ended(WorkerStatus.Cancelled, "Cancelled.");
            }
            else if (timedOut.Contains(f))
            {
                result = Ended(WorkerStatus.TimedOut, $"Timed out after {f.Run.Options.Timeout.TotalMinutes:0} minutes.");
            }

            if (result is null)
            {
                // Stopped only because of the other item (its cancel or time limit): back in the queue, untried.
                _inFlight.Remove(f);
                f.Dispose();
                await RequeueAsync(f.Job, f.Run).ConfigureAwait(false);
                continue;
            }

            // One SubSync crash counts once toward the three-crash stop (D14), however many items it had.
            var count = result.Status != WorkerStatus.Crashed || !crashCounted;
            crashCounted |= result.Status == WorkerStatus.Crashed;
            await FinishFlightAsync(f, result, count).ConfigureAwait(false);
        }

        if (ReferenceEquals(_session, session))
        {
            _session = null;
        }

        session.Dispose();
    }

    private async Task RequeueAsync(RunningJob job, PreparedRun run)
    {
        try
        {
            await _runner.RestoreAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The journal keeps the backup, and startup recovery tries again; the item can't run until then.
            _logger.LogError(ex, "Subtitle Fixer could not restore {Name}; its backup is kept for recovery", job.Snapshot.Name);
            await SettleAsync(job, new RunOutcome(JobItemStatus.Failed, "Failed: " + ex.Message + " The .bak backup is restored on the next Jellyfin start.", null, null), countCrash: false).ConfigureAwait(false);
            return;
        }

        if (_queue.Requeue(job.Snapshot.JobId))
        {
            _logger.LogInformation("Subtitle Fixer put {Name} back in the queue: SubSync was stopped for another item", job.Snapshot.Name);
        }
    }

    private async Task FinishFlightAsync(Flight flight, WorkerResult result, bool countCrash)
    {
        _inFlight.Remove(flight);
        flight.Dispose();
        RunOutcome outcome;
        try
        {
            outcome = await _runner.FinishAsync(flight.Run, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // FinishAsync restores on its own failures; this only means the restore itself failed.
            _logger.LogError(ex, "Subtitle Fixer could not finish {Name}; any backup is kept for recovery", flight.Job.Snapshot.Name);
            outcome = new RunOutcome(JobItemStatus.Failed, "Failed: " + ex.Message + " If a .bak backup was left, it is restored on the next Jellyfin start.", null, WorkerStatus.Crashed);
        }

        await SettleAsync(flight.Job, outcome, countCrash).ConfigureAwait(false);
    }

    private async Task AbortInFlightAsync(Exception error)
    {
        if (_session is { } session && !session.Exited.IsCompleted)
        {
            session.Kill();
        }

        var first = true;
        foreach (var f in _inFlight.ToList())
        {
            try
            {
                var result = f.Item.Result.IsCompleted ? f.Item.Result.Result : Ended(WorkerStatus.Crashed, "Unexpected error: " + error.Message);
                await FinishFlightAsync(f, result, first).ConfigureAwait(false);
                first = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Subtitle Fixer could not restore {Name}; its backup is kept for recovery", f.Job.Snapshot.Name);
            }
        }

        _inFlight.Clear();
    }

    // Nothing is in flight: close stdin so SubSync exits by itself, and end it if it doesn't.
    private async Task CloseSessionAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        _session = null;
        if (!session.Exited.IsCompleted)
        {
            session.Close();
            if (await Task.WhenAny(session.Exited, Task.Delay(CloseWait)).ConfigureAwait(false) != session.Exited)
            {
                _logger.LogWarning("SubSync did not exit after its input was closed; ending it");
                session.Kill();
            }
        }

        session.Dispose();
    }

    private sealed class Flight : IDisposable
    {
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public Flight(RunningJob job, PreparedRun run, ServeItem item)
        {
            Job = job;
            Run = run;
            Item = item;
            _registration = job.CancellationToken.Register(() => _cancelled.TrySetResult());
        }

        public RunningJob Job { get; }

        public PreparedRun Run { get; }

        public ServeItem Item { get; }

        public Task Cancelled => _cancelled.Task;

        public bool AheadSent { get; set; }

        public void Dispose() => _registration.Dispose();
    }
}
