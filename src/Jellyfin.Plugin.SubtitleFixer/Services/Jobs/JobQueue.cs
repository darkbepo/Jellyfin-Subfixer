using Jellyfin.Plugin.SubtitleFixer.Domain;

namespace Jellyfin.Plugin.SubtitleFixer.Services.Jobs;

/// <summary>State of one queued episode. Wire names in <see cref="JobQueue.StatusCode"/>.</summary>
public enum JobItemStatus
{
    /// <summary>Waiting.</summary>
    Queued,

    /// <summary>SubSync is working on it.</summary>
    Running,

    /// <summary>Synced and verified.</summary>
    Done,

    /// <summary>SubSync matched too few lines; original kept.</summary>
    Check,

    /// <summary>Failed; original kept.</summary>
    Failed,

    /// <summary>Cancelled, or dropped because the queue stopped; original kept.</summary>
    Cancelled,

    /// <summary>No longer eligible when its turn came (already synced, audio changed); untouched.</summary>
    Skipped,
}

/// <summary>Where a job came from (BAZARR-PLAN.md: Bazarr downloads join the same queue).</summary>
public enum JobSource
{
    /// <summary>A user pressed Start.</summary>
    Manual,

    /// <summary>A subtitle downloaded through Bazarr.</summary>
    Bazarr,
}

/// <summary>Overall queue state.</summary>
public enum QueueState
{
    /// <summary>Nothing queued or running.</summary>
    Idle,

    /// <summary>Working.</summary>
    Running,

    /// <summary>The running item was asked to cancel and is being restored.</summary>
    Stopping,
}

/// <summary>An episode to queue.</summary>
/// <param name="ItemId">Episode id.</param>
/// <param name="Name">Display name, e.g. "S01E04 Title".</param>
/// <param name="SeriesName">Series name.</param>
public sealed record JobRequestItem(Guid ItemId, string Name, string SeriesName);

/// <summary>Immutable view of one queue entry, safe to hand to the API.</summary>
/// <param name="JobId">Queue entry id.</param>
/// <param name="ItemId">Episode id.</param>
/// <param name="Name">Display name.</param>
/// <param name="SeriesName">Series name.</param>
/// <param name="UserId">Who queued it.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Note">Free text from the source (for example "downloaded from opensubtitlescom").</param>
/// <param name="Status">State.</param>
/// <param name="Message">Reason for check, failed, cancelled or skipped.</param>
/// <param name="QueuedUtc">When queued.</param>
/// <param name="StartedUtc">When it started running.</param>
/// <param name="FinishedUtc">When it finished.</param>
/// <param name="Seconds">SubSync seconds, when it ran.</param>
public sealed record JobItemSnapshot(
    Guid JobId,
    Guid ItemId,
    string Name,
    string SeriesName,
    Guid UserId,
    JobSource Source,
    string? Note,
    JobItemStatus Status,
    string? Message,
    DateTime QueuedUtc,
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    double? Seconds);

/// <summary>Immutable view of the whole queue.</summary>
/// <param name="State">State.</param>
/// <param name="Items">Every entry in queue order: finished first (oldest first), then running, then queued.</param>
/// <param name="StopReason">Why the queue last stopped by itself, until the next Start.</param>
public sealed record QueueSnapshot(QueueState State, IReadOnlyList<JobItemSnapshot> Items, string? StopReason)
{
    /// <summary>Gets the running entry, if any.</summary>
    public JobItemSnapshot? Current => Items.FirstOrDefault(i => i.Status == JobItemStatus.Running);

    /// <summary>Gets the number of queued and running entries.</summary>
    public int Pending => Items.Count(i => i.Status is JobItemStatus.Queued or JobItemStatus.Running);

    /// <summary>Gets the ETA: pending entries times the average seconds of finished ones; null until one finished.</summary>
    public double? EtaSeconds
    {
        get
        {
            var ran = Items.Where(i => i.Seconds is > 0 && i.Status is JobItemStatus.Done or JobItemStatus.Check or JobItemStatus.Failed).ToList();
            return ran.Count == 0 ? null : Pending * ran.Average(i => i.Seconds!.Value);
        }
    }
}

/// <summary>Result of <see cref="JobQueue.Enqueue"/>.</summary>
/// <param name="Error">Set when rejected (queue full).</param>
/// <param name="Queued">Entries added.</param>
/// <param name="AlreadyQueued">Entries skipped because they were already queued or running.</param>
/// <param name="QueueLength">Queued and running entries after adding.</param>
public sealed record EnqueueResult(SelectionError? Error, int Queued, int AlreadyQueued, int QueueLength);

/// <summary>Running entry handed to the consumer.</summary>
/// <param name="Snapshot">Entry as it started.</param>
/// <param name="CancellationToken">Cancelled when someone cancels this entry.</param>
public sealed record RunningJob(JobItemSnapshot Snapshot, CancellationToken CancellationToken);

/// <summary>
/// The one global queue (SPEC.md hard rule 5, D19). In memory: dropped on restart (D11). One consumer takes entries
/// in order; everything else only reads immutable snapshots. Finished entries stay visible until cleared or a day old.
/// </summary>
public sealed class JobQueue : IDisposable
{
    private static readonly TimeSpan FinishedRetention = TimeSpan.FromHours(24);

    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly TimeProvider _time;
    private string? _stopReason;
    private QueueSnapshot _snapshot = new(QueueState.Idle, [], null);

    /// <summary>
    /// Initializes a new instance of the <see cref="JobQueue"/> class.
    /// </summary>
    /// <param name="time">Clock.</param>
    public JobQueue(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Gets the API wire name of a status.</summary>
    /// <param name="status">Status.</param>
    /// <returns>Wire name.</returns>
    public static string StatusCode(JobItemStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>Gets the current snapshot. Lock-free for readers.</summary>
    /// <returns>Snapshot.</returns>
    public QueueSnapshot Snapshot() => Volatile.Read(ref _snapshot);

    /// <summary>Adds episodes to the end of the queue. Episodes already queued or running are skipped.</summary>
    /// <param name="userId">Who is queuing.</param>
    /// <param name="items">Episodes, in order.</param>
    /// <param name="source">Source.</param>
    /// <param name="note">Note shown with each entry.</param>
    /// <param name="maxQueued">Cap on queued plus running entries (<c>MaxQueuedEpisodes</c>).</param>
    /// <returns>The result.</returns>
    public EnqueueResult Enqueue(Guid userId, IReadOnlyList<JobRequestItem> items, JobSource source, string? note, int maxQueued)
    {
        lock (_lock)
        {
            Prune();
            var pendingIds = _entries.Where(e => e.IsPending).Select(e => e.ItemId).ToHashSet();
            var fresh = items.Where(i => pendingIds.Add(i.ItemId)).ToList();
            var alreadyQueued = items.Count - fresh.Count;
            var pending = _entries.Count(e => e.IsPending);

            if (fresh.Count > 0 && pending + fresh.Count > maxQueued)
            {
                return new EnqueueResult(
                    new SelectionError(ErrorCodes.QueueFull, $"The queue holds at most {maxQueued} episodes and has {pending}. Try fewer, or wait for the queue to shrink."),
                    0,
                    alreadyQueued,
                    pending);
            }

            var now = Now();
            foreach (var item in fresh)
            {
                _entries.Add(new Entry(Guid.NewGuid(), item.ItemId, item.Name, item.SeriesName, userId, source, note, now));
            }

            if (fresh.Count > 0)
            {
                _stopReason = null;
            }

            Publish();
            if (fresh.Count > 0)
            {
                _work.Release(fresh.Count);
            }

            return new EnqueueResult(null, fresh.Count, alreadyQueued, pending + fresh.Count);
        }
    }

    /// <summary>Waits for the next queued entry and marks it running.</summary>
    /// <param name="cancellationToken">Stops waiting (shutdown).</param>
    /// <returns>The entry.</returns>
    public async Task<RunningJob> TakeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _work.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                // Cancelled entries leave stale permits; skip them.
                var next = _entries.FirstOrDefault(e => e.Status == JobItemStatus.Queued);
                if (next is null)
                {
                    continue;
                }

                next.Status = JobItemStatus.Running;
                next.StartedUtc = Now();
                next.Cancel = new CancellationTokenSource();
                Publish();
                return new RunningJob(next.ToSnapshot(), next.Cancel.Token);
            }
        }
    }

    /// <summary>Records how a running entry ended.</summary>
    /// <param name="jobId">Entry.</param>
    /// <param name="status">Final status.</param>
    /// <param name="message">Reason.</param>
    /// <param name="seconds">SubSync seconds, if it ran.</param>
    public void Complete(Guid jobId, JobItemStatus status, string? message, double? seconds)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.JobId == jobId);
            if (entry is null)
            {
                return;
            }

            entry.Status = status;
            entry.Message = message;
            entry.Seconds = seconds;
            entry.FinishedUtc = Now();
            entry.Cancel?.Dispose();
            entry.Cancel = null;
            Publish();
        }
    }

    /// <summary>Cancels entries: the caller's own, or everything for an admin (D19).</summary>
    /// <param name="userId">Caller.</param>
    /// <param name="isAdmin">Admins cancel everyone's entries.</param>
    /// <returns>How many entries were cancelled or asked to cancel.</returns>
    public int Cancel(Guid userId, bool isAdmin)
    {
        lock (_lock)
        {
            var count = 0;
            foreach (var entry in _entries.Where(e => e.IsPending && (isAdmin || e.UserId == userId)))
            {
                count++;
                if (entry.Status == JobItemStatus.Queued)
                {
                    entry.Status = JobItemStatus.Cancelled;
                    entry.Message = "Cancelled.";
                    entry.FinishedUtc = Now();
                }
                else
                {
                    // The runner restores the subtitle and completes the entry.
                    entry.CancelRequested = true;
                    entry.Cancel?.Cancel();
                }
            }

            Publish();
            return count;
        }
    }

    /// <summary>Drops every queued entry because the queue must stop (exit 7, repeated crashes).</summary>
    /// <param name="reason">Shown to users.</param>
    public void Stop(string reason)
    {
        lock (_lock)
        {
            foreach (var entry in _entries.Where(e => e.Status == JobItemStatus.Queued))
            {
                entry.Status = JobItemStatus.Cancelled;
                entry.Message = "Queue stopped: " + reason;
                entry.FinishedUtc = Now();
            }

            _stopReason = reason;
            Publish();
        }
    }

    /// <summary>Removes finished entries: the caller's own, or all for an admin.</summary>
    /// <param name="userId">Caller.</param>
    /// <param name="isAdmin">Admins clear everyone's.</param>
    /// <returns>How many were removed.</returns>
    public int ClearFinished(Guid userId, bool isAdmin)
    {
        lock (_lock)
        {
            var removed = _entries.RemoveAll(e => !e.IsPending && (isAdmin || e.UserId == userId));
            if (_entries.Count == 0)
            {
                _stopReason = null;
            }

            Publish();
            return removed;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                entry.Cancel?.Dispose();
            }
        }

        _work.Dispose();
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private void Prune()
    {
        var cutoff = Now() - FinishedRetention;
        _entries.RemoveAll(e => !e.IsPending && e.FinishedUtc < cutoff);
    }

    private void Publish()
    {
        var running = _entries.Any(e => e.Status == JobItemStatus.Running);
        var stopping = _entries.Any(e => e.Status == JobItemStatus.Running && e.CancelRequested);
        var queued = _entries.Any(e => e.Status == JobItemStatus.Queued);
        var state = stopping ? QueueState.Stopping : running || queued ? QueueState.Running : QueueState.Idle;

        var ordered = _entries.Where(e => !e.IsPending).OrderBy(e => e.FinishedUtc)
            .Concat(_entries.Where(e => e.Status == JobItemStatus.Running))
            .Concat(_entries.Where(e => e.Status == JobItemStatus.Queued))
            .Select(e => e.ToSnapshot())
            .ToList();
        Volatile.Write(ref _snapshot, new QueueSnapshot(state, ordered, _stopReason));
    }

    private sealed class Entry
    {
        public Entry(Guid jobId, Guid itemId, string name, string seriesName, Guid userId, JobSource source, string? note, DateTime queuedUtc)
        {
            JobId = jobId;
            ItemId = itemId;
            Name = name;
            SeriesName = seriesName;
            UserId = userId;
            Source = source;
            Note = note;
            QueuedUtc = queuedUtc;
        }

        public Guid JobId { get; }

        public Guid ItemId { get; }

        public string Name { get; }

        public string SeriesName { get; }

        public Guid UserId { get; }

        public JobSource Source { get; }

        public string? Note { get; }

        public DateTime QueuedUtc { get; }

        public JobItemStatus Status { get; set; } = JobItemStatus.Queued;

        public string? Message { get; set; }

        public DateTime? StartedUtc { get; set; }

        public DateTime? FinishedUtc { get; set; }

        public double? Seconds { get; set; }

        public bool CancelRequested { get; set; }

        public CancellationTokenSource? Cancel { get; set; }

        public bool IsPending => Status is JobItemStatus.Queued or JobItemStatus.Running;

        public JobItemSnapshot ToSnapshot() =>
            new(JobId, ItemId, Name, SeriesName, UserId, Source, Note, Status, Message, QueuedUtc, StartedUtc, FinishedUtc, Seconds);
    }
}
