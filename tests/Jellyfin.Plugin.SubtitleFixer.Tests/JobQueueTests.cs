using Jellyfin.Plugin.SubtitleFixer.Api;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

public sealed class JobQueueTests : IDisposable
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private readonly JobQueue _queue = new(TimeProvider.System);

    public void Dispose() => _queue.Dispose();

    private static JobRequestItem[] Items(int count, string prefix = "E") =>
        Enumerable.Range(1, count).Select(i => new JobRequestItem(Guid.NewGuid(), prefix + i, "Show")).ToArray();

    private static async Task<RunningJob> Take(JobQueue queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await queue.TakeAsync(timeout.Token);
    }

    [Fact]
    public async Task Second_user_is_queued_behind_the_first_in_one_queue()
    {
        _queue.Enqueue(Alice, Items(2, "A"), JobSource.Manual, null, 300);
        var bob = _queue.Enqueue(Bob, Items(1, "B"), JobSource.Manual, null, 300);

        Assert.Equal(3, bob.QueueLength);
        Assert.Equal(new[] { "A1", "A2", "B1" }, _queue.Snapshot().Items.Select(i => i.Name));

        var first = await Take(_queue);
        Assert.Equal("A1", first.Snapshot.Name);
        Assert.Equal(QueueState.Running, _queue.Snapshot().State);
        Assert.Equal(1, _queue.Snapshot().Items.Count(i => i.Status == JobItemStatus.Running));
    }

    [Fact]
    public void Episodes_already_queued_are_not_added_twice()
    {
        var items = Items(2);
        _queue.Enqueue(Alice, items, JobSource.Manual, null, 300);

        var again = _queue.Enqueue(Bob, items, JobSource.Manual, null, 300);

        Assert.Equal(0, again.Queued);
        Assert.Equal(2, again.AlreadyQueued);
        Assert.Equal(2, _queue.Snapshot().Items.Count);
    }

    [Fact]
    public void Queue_full_rejects_the_whole_request()
    {
        _queue.Enqueue(Alice, Items(4), JobSource.Manual, null, 5);

        var result = _queue.Enqueue(Bob, Items(2), JobSource.Manual, null, 5);

        Assert.Equal(ErrorCodes.QueueFull, result.Error?.Code);
        Assert.Equal(4, _queue.Snapshot().Items.Count);
    }

    [Fact]
    public void Finished_entries_do_not_count_toward_the_cap()
    {
        _queue.Enqueue(Alice, Items(5), JobSource.Manual, null, 5);
        _queue.Cancel(Alice, isAdmin: false);

        Assert.Null(_queue.Enqueue(Bob, Items(5), JobSource.Manual, null, 5).Error);
    }

    [Fact]
    public async Task Users_cancel_only_their_own_entries()
    {
        _queue.Enqueue(Alice, Items(1, "A"), JobSource.Manual, null, 300);
        _queue.Enqueue(Bob, Items(2, "B"), JobSource.Manual, null, 300);
        var running = await Take(_queue); // Alice's

        _queue.Cancel(Bob, isAdmin: false);

        Assert.False(running.CancellationToken.IsCancellationRequested);
        var items = _queue.Snapshot().Items;
        Assert.All(items.Where(i => i.UserId == Bob), i => Assert.Equal(JobItemStatus.Cancelled, i.Status));
        Assert.Equal(JobItemStatus.Running, items.Single(i => i.UserId == Alice).Status);
    }

    [Fact]
    public async Task Cancelling_the_running_entry_signals_its_token_and_shows_stopping()
    {
        _queue.Enqueue(Alice, Items(1), JobSource.Manual, null, 300);
        var running = await Take(_queue);

        _queue.Cancel(Alice, isAdmin: false);

        Assert.True(running.CancellationToken.IsCancellationRequested);
        Assert.Equal(QueueState.Stopping, _queue.Snapshot().State);

        _queue.Complete(running.Snapshot.JobId, JobItemStatus.Cancelled, "Cancelled.", null);
        Assert.Equal(QueueState.Idle, _queue.Snapshot().State);
    }

    [Fact]
    public async Task Admin_cancels_everything()
    {
        _queue.Enqueue(Alice, Items(1), JobSource.Manual, null, 300);
        _queue.Enqueue(Bob, Items(2), JobSource.Manual, null, 300);
        var running = await Take(_queue);

        Assert.Equal(3, _queue.Cancel(Guid.NewGuid(), isAdmin: true));
        Assert.True(running.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Cancelled_entries_are_skipped_by_the_consumer()
    {
        _queue.Enqueue(Alice, Items(1, "A"), JobSource.Manual, null, 300);
        _queue.Enqueue(Bob, Items(1, "B"), JobSource.Manual, null, 300);
        _queue.Cancel(Alice, isAdmin: false);

        var next = await Take(_queue);

        Assert.Equal("B1", next.Snapshot.Name);
    }

    [Fact]
    public void Stop_drops_queued_entries_with_the_reason()
    {
        _queue.Enqueue(Alice, Items(3), JobSource.Manual, null, 300);

        _queue.Stop("close the SubSync window");

        var snapshot = _queue.Snapshot();
        Assert.All(snapshot.Items, i => Assert.Equal(JobItemStatus.Cancelled, i.Status));
        Assert.Equal("close the SubSync window", snapshot.StopReason);

        _queue.Enqueue(Alice, Items(1), JobSource.Manual, null, 300);
        Assert.Null(_queue.Snapshot().StopReason);
    }

    [Fact]
    public async Task Clear_removes_only_the_callers_finished_entries()
    {
        _queue.Enqueue(Alice, Items(1, "A"), JobSource.Manual, null, 300);
        _queue.Enqueue(Bob, Items(1, "B"), JobSource.Manual, null, 300);
        var a = await Take(_queue);
        _queue.Complete(a.Snapshot.JobId, JobItemStatus.Done, null, 60);
        var b = await Take(_queue);
        _queue.Complete(b.Snapshot.JobId, JobItemStatus.Done, null, 60);

        Assert.Equal(1, _queue.ClearFinished(Alice, isAdmin: false));
        Assert.Equal("B1", Assert.Single(_queue.Snapshot().Items).Name);
    }

    [Fact]
    public async Task Eta_uses_the_average_of_finished_episodes()
    {
        _queue.Enqueue(Alice, Items(4), JobSource.Manual, null, 300);
        Assert.Null(_queue.Snapshot().EtaSeconds);

        var first = await Take(_queue);
        _queue.Complete(first.Snapshot.JobId, JobItemStatus.Done, null, 60);
        var second = await Take(_queue);
        _queue.Complete(second.Snapshot.JobId, JobItemStatus.Check, null, 120);

        Assert.Equal(2 * 90, _queue.Snapshot().EtaSeconds);
    }

    [Fact]
    public void Bazarr_source_and_note_are_kept()
    {
        _queue.Enqueue(Alice, Items(1), JobSource.Bazarr, "downloaded from opensubtitlescom", 300);

        var item = Assert.Single(_queue.Snapshot().Items);
        Assert.Equal(JobSource.Bazarr, item.Source);
        Assert.Equal("downloaded from opensubtitlescom", item.Note);
    }

    [Fact]
    public async Task Status_hides_other_users_items_from_libraries_the_viewer_cannot_see()
    {
        var aliceItems = Items(1, "Secret");
        _queue.Enqueue(Alice, aliceItems, JobSource.Manual, "note", 300);
        _queue.Enqueue(Bob, Items(2, "B"), JobSource.Manual, null, 300);
        await Take(_queue);

        var status = JobsController.BuildStatus(_queue.Snapshot(), Bob, true, id => id != aliceItems[0].ItemId);

        var hidden = status.Items[0];
        Assert.Equal(JobsController.HiddenName, hidden.Name);
        Assert.Null(hidden.ItemId);
        Assert.Null(hidden.SeriesName);
        Assert.Null(hidden.Note);
        Assert.False(hidden.Mine);
        Assert.Equal(JobsController.HiddenName, status.Current!.Name);
        Assert.All(status.Items.Skip(1), i => Assert.True(i.Mine));
        Assert.Equal(1, status.AheadOfYours);
        Assert.Equal("running", status.State);
        Assert.Equal(1, status.Totals.Running);
        Assert.Equal(2, status.Totals.Queued);
    }

    [Fact]
    public void Status_always_shows_the_viewers_own_items()
    {
        _queue.Enqueue(Alice, Items(1, "Mine"), JobSource.Manual, null, 300);

        var status = JobsController.BuildStatus(_queue.Snapshot(), Alice, true, _ => false);

        Assert.Equal("Mine1", status.Items[0].Name);
        Assert.Equal(0, status.AheadOfYours);
    }
}

public sealed class JobProcessorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeStateStore _state = new();
    private readonly FakeWorker _worker = new();
    private readonly JobQueue _queue = new(TimeProvider.System);
    private readonly JobProcessor _processor;
    private readonly List<JobRequestItem> _items = new();

    public JobProcessorTests()
    {
        var backup = new BackupService(_state, TimeProvider.System, NullLogger<BackupService>.Instance);
        var runner = new JobRunner(_library, _state, backup, _worker, new FakeRefresh(), () => new WorkerOptions(true, false, TimeSpan.FromMinutes(1)), TimeProvider.System, NullLogger<JobRunner>.Instance);
        _processor = new JobProcessor(_queue, runner, new StartupStatus(), NullLogger<JobProcessor>.Instance);

        var season = _library.AddSeason(_library.AddSeries("Show"), 1);
        for (var n = 1; n <= 6; n++)
        {
            var video = _dir.File($"Show S01E0{n}.mkv", "v");
            _dir.File($"Show S01E0{n}.en.srt", "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
            var e = _library.AddEpisode(season, n, video, "eng");
            _items.Add(new JobRequestItem(e.Node.Id, "E" + n, "Show"));
        }
    }

    public void Dispose()
    {
        _queue.Dispose();
        _dir.Dispose();
    }

    private async Task RunNext()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _processor.ProcessAsync(await _queue.TakeAsync(timeout.Token));
    }

    [Fact]
    public async Task Exit_7_stops_the_queue_and_drops_the_rest()
    {
        _queue.Enqueue(Guid.NewGuid(), _items, JobSource.Manual, null, 300);
        _worker.Behavior = (_, _, _) => Task.FromResult(FakeWorker.Result(WorkerStatus.Unavailable, "close the SubSync window"));

        await RunNext();

        var snapshot = _queue.Snapshot();
        Assert.Equal(QueueState.Idle, snapshot.State);
        Assert.Equal("close the SubSync window", snapshot.StopReason);
        Assert.All(snapshot.Items, i => Assert.Equal(JobItemStatus.Cancelled, i.Status));
        Assert.Equal(1, _worker.Calls);
    }

    [Fact]
    public async Task Three_consecutive_crashes_stop_the_queue()
    {
        _queue.Enqueue(Guid.NewGuid(), _items, JobSource.Manual, null, 300);
        _worker.Behavior = (_, _, _) => Task.FromResult(FakeWorker.Result(WorkerStatus.Crashed, "exit 10"));

        await RunNext();
        await RunNext();
        Assert.Null(_queue.Snapshot().StopReason);
        await RunNext();

        var snapshot = _queue.Snapshot();
        Assert.NotNull(snapshot.StopReason);
        Assert.Equal(3, snapshot.Items.Count(i => i.Status == JobItemStatus.Failed));
        Assert.Equal(3, snapshot.Items.Count(i => i.Status == JobItemStatus.Cancelled));
    }

    [Fact]
    public async Task A_good_run_resets_the_crash_count()
    {
        _queue.Enqueue(Guid.NewGuid(), _items, JobSource.Manual, null, 300);
        var call = 0;
        _worker.Behavior = (_, _, _) => Task.FromResult(FakeWorker.Result(++call == 3 ? WorkerStatus.NoMatch : WorkerStatus.Crashed));

        for (var i = 0; i < 5; i++)
        {
            await RunNext();
        }

        Assert.Null(_queue.Snapshot().StopReason);
    }
}
