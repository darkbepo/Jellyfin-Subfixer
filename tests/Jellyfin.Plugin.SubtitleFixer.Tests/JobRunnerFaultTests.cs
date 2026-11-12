using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.State;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

/// <summary>Scriptable SubSync.</summary>
public sealed class FakeWorker : ISubSyncWorker
{
    public Func<string, string, CancellationToken, Task<WorkerResult>> Behavior { get; set; } =
        (_, _, _) => Task.FromResult(Result(WorkerStatus.Synced));

    public int Calls { get; private set; }

    public WorkerOptions? LastOptions { get; private set; }

    public bool IsConfigured { get; set; } = true;

    public static WorkerResult Result(WorkerStatus status, string? reason = null) =>
        new(status, null, reason, "eng", 42, "1.2.0", status == WorkerStatus.Synced ? 0 : 4);

    public Task<WorkerResult> RunAsync(string videoPath, string subtitlePath, string languageTag, WorkerOptions options, CancellationToken cancellationToken)
    {
        Calls++;
        LastOptions = options;
        return Behavior(videoPath, subtitlePath, cancellationToken);
    }
}

public sealed class FakeRefresh : IRefreshService
{
    public List<Guid> Requested { get; } = new();

    public void RequestRefresh(Guid itemId) => Requested.Add(itemId);
}

/// <summary>
/// TESTING.md "Fault injection on the subtitle swap": after every case except success, the original subtitle is
/// byte-identical and no <c>.bak</c> remains.
/// </summary>
[Collection(RunTimeEstimateTests.Collection)]
public sealed class JobRunnerFaultTests : IDisposable
{
    private const string Original = "1\r\n00:00:01,000 --> 00:00:02,500\r\nThe original line.\r\n\r\n";
    private const string Synced = "1\r\n00:00:03,000 --> 00:00:04,500\r\nThe original line.\r\n\r\n";

    private readonly TempDir _dir = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeStateStore _state = new();
    private readonly FakeWorker _worker = new();
    private readonly FakeRefresh _refresh = new();
    private readonly BackupService _backup;
    private readonly JobRunner _runner;
    private readonly VideoFacts _episode;
    private readonly string _subtitle;
    private readonly byte[] _originalBytes;

    public JobRunnerFaultTests()
    {
        _backup = new BackupService(_state, TimeProvider.System, NullLogger<BackupService>.Instance);
        _runner = new JobRunner(_library, _state, _backup, _worker, _refresh, () => new WorkerOptions(true, false, TimeSpan.FromMinutes(1)), TimeProvider.System, NullLogger<JobRunner>.Instance);

        var season = _library.AddSeason(_library.AddSeries("Show"), 1);
        var video = _dir.File("Show S01E01.mkv", "video bytes");
        _subtitle = _dir.File("Show S01E01.en.srt", Original);
        _originalBytes = File.ReadAllBytes(_subtitle);
        _episode = _library.AddEpisode(season, 1, video, "eng");
    }

    public void Dispose() => _dir.Dispose();

    private string Backup => _subtitle + ".bak";

    private JobItemSnapshot Job() =>
        new(Guid.NewGuid(), _episode.Node.Id, "S01E01", "Show", Guid.NewGuid(), JobSource.Manual, null, JobItemStatus.Running, null, DateTime.UtcNow, DateTime.UtcNow, null, null);

    private void AssertOriginalIntact()
    {
        Assert.Equal(_originalBytes, File.ReadAllBytes(_subtitle));
        Assert.False(File.Exists(Backup), ".bak must not remain");
        Assert.Empty(_state.InFlight);
    }

    private Func<string, string, CancellationToken, Task<WorkerResult>> Overwrite(string content, WorkerStatus status, string? reason = null) =>
        (_, sub, _) =>
        {
            File.WriteAllText(sub, content);
            return Task.FromResult(FakeWorker.Result(status, reason));
        };

    [Fact]
    public async Task Progress_gets_an_estimate_then_the_stages_SubSync_reports()
    {
        RunTimeEstimate.Reset();
        _library.SetRuntime(_episode.Node.Id, TimeSpan.FromMinutes(22));
        _worker.Behavior = (_, _, _) =>
        {
            _worker.LastOptions!.OnStage!(SubSyncStage.Listening);
            return Task.FromResult(FakeWorker.Result(WorkerStatus.Synced));
        };
        var seen = new List<(string? Stage, double? Expected)>();

        var outcome = await _runner.RunAsync(Job(), CancellationToken.None, (stage, expected) => seen.Add((stage, expected)));

        Assert.Equal(JobItemStatus.Done, outcome.Status);
        Assert.Equal(2, seen.Count);
        Assert.Null(seen[0].Stage);
        Assert.Equal(RunTimeEstimate.Overhead.TotalSeconds + (22 * 60 * 0.33), seen[0].Expected!.Value, 0);
        Assert.Equal((SubSyncStage.Listening, (double?)null), seen[1]);

        // The 42-second fake run taught the estimate that this "server" is fast.
        Assert.True(RunTimeEstimate.Expect(TimeSpan.FromMinutes(22)) < seen[0].Expected);
        RunTimeEstimate.Reset();
    }

    [Fact]
    public async Task Cli_fails_restores_and_records_failed()
    {
        _worker.Behavior = Overwrite("half-written garbage", WorkerStatus.Failed, "could not read audio");

        var outcome = await _runner.RunAsync(Job(), CancellationToken.None);

        AssertOriginalIntact();
        Assert.Equal(JobItemStatus.Failed, outcome.Status);
        Assert.Equal(SyncStatus.Failed, _state.Records[_episode.Node.Id].Status);
        Assert.Empty(_refresh.Requested);
    }

    [Fact]
    public async Task No_match_restores_and_records_check()
    {
        _worker.Behavior = (_, _, _) => Task.FromResult(FakeWorker.Result(WorkerStatus.NoMatch, "matched 3 lines"));

        var outcome = await _runner.RunAsync(Job(), CancellationToken.None);

        AssertOriginalIntact();
        Assert.Equal(JobItemStatus.Check, outcome.Status);
        Assert.Equal(SyncStatus.Check, _state.Records[_episode.Node.Id].Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("this is not an srt file")]
    [InlineData("1\r\n00:00:01,000 --> 00:00:02,000\r\n\r\n")]
    public async Task Empty_or_invalid_output_restores_and_records_failed(string output)
    {
        _worker.Behavior = Overwrite(output, WorkerStatus.Synced);

        var outcome = await _runner.RunAsync(Job(), CancellationToken.None);

        AssertOriginalIntact();
        Assert.Equal(JobItemStatus.Failed, outcome.Status);
        Assert.StartsWith("invalid output", _state.Records[_episode.Node.Id].Message, StringComparison.Ordinal);
        Assert.Empty(_refresh.Requested);
    }

    [Fact]
    public async Task Output_deleted_by_cli_is_restored()
    {
        _worker.Behavior = (_, sub, _) =>
        {
            File.Delete(sub);
            return Task.FromResult(FakeWorker.Result(WorkerStatus.Synced));
        };

        await _runner.RunAsync(Job(), CancellationToken.None);

        AssertOriginalIntact();
    }

    [Fact]
    public async Task Cancel_mid_run_restores_and_writes_no_record()
    {
        using var cancel = new CancellationTokenSource();
        _worker.Behavior = async (_, sub, ct) =>
        {
            await File.WriteAllTextAsync(sub, "partial", CancellationToken.None);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            return FakeWorker.Result(WorkerStatus.Cancelled);
        };

        var run = _runner.RunAsync(Job(), cancel.Token);
        await Task.Delay(100);
        await cancel.CancelAsync();
        var outcome = await run;

        AssertOriginalIntact();
        Assert.Equal(JobItemStatus.Cancelled, outcome.Status);
        Assert.False(_state.Records.ContainsKey(_episode.Node.Id));
    }

    [Fact]
    public async Task Timeout_restores_and_records_failed_timeout()
    {
        _worker.Behavior = Overwrite("partial", WorkerStatus.TimedOut, "Timed out after 30 minutes.");

        var outcome = await _runner.RunAsync(Job(), CancellationToken.None);

        AssertOriginalIntact();
        Assert.Equal(JobItemStatus.Failed, outcome.Status);
        Assert.Equal("timeout", _state.Records[_episode.Node.Id].Message);
    }

    [Fact]
    public async Task Exit_7_restores_writes_no_record_and_reports_unavailable()
    {
        _worker.Behavior = Overwrite("partial", WorkerStatus.Unavailable, "another SubSync is running");

        var outcome = await _runner.RunAsync(Job(), CancellationToken.None);

        AssertOriginalIntact();
        Assert.Equal(WorkerStatus.Unavailable, outcome.Worker);
        Assert.False(_state.Records.ContainsKey(_episode.Node.Id));
    }

    [Fact]
    public async Task Worker_exception_restores_and_counts_as_crash()
    {
        _worker.Behavior = (_, sub, _) =>
        {
            File.WriteAllText(sub, "partial");
            throw new InvalidOperationException("boom");
        };

        var outcome = await _runner.RunAsync(Job(), CancellationToken.None);

        AssertOriginalIntact();
        Assert.Equal(WorkerStatus.Crashed, outcome.Worker);
        Assert.Equal(SyncStatus.Failed, _state.Records[_episode.Node.Id].Status);
    }

    [Fact]
    public async Task Backup_copy_fails_so_cli_never_starts()
    {
        Directory.CreateDirectory(Backup); // A directory where the .bak file must go makes the copy fail.

        var outcome = await _runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(0, _worker.Calls);
        Assert.Equal(_originalBytes, File.ReadAllBytes(_subtitle));
        Assert.Equal(JobItemStatus.Failed, outcome.Status);
        Assert.Equal("cannot create backup", _state.Records[_episode.Node.Id].Message);
        Assert.Empty(_state.InFlight);
    }

    [Fact]
    public async Task Existing_bak_is_never_overwritten()
    {
        File.WriteAllText(Backup, "an older original someone kept");

        var outcome = await _runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(0, _worker.Calls);
        Assert.Equal(_originalBytes, File.ReadAllBytes(_subtitle));
        Assert.Equal("an older original someone kept", File.ReadAllText(Backup));
        Assert.Equal(JobItemStatus.Failed, outcome.Status);
    }

    [Fact]
    public async Task Journal_unavailable_means_no_backup_and_no_run()
    {
        var broken = new ThrowingJournalStore(_state);
        var backup = new BackupService(broken, TimeProvider.System, NullLogger<BackupService>.Instance);
        var runner = new JobRunner(_library, broken, backup, _worker, _refresh, () => new WorkerOptions(true, false, TimeSpan.FromMinutes(1)), TimeProvider.System, NullLogger<JobRunner>.Instance);

        var outcome = await runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(0, _worker.Calls);
        Assert.Equal(JobItemStatus.Failed, outcome.Status);
        AssertOriginalIntact();
    }

    [Fact]
    public async Task Process_killed_leaving_bak_is_restored_by_startup_recovery()
    {
        // Simulate Jellyfin dying mid-run: backup journaled and made, subtitle half-written, nothing cleaned up.
        await _backup.CreateAsync(_episode.Node.Id, _subtitle, CancellationToken.None);
        File.WriteAllText(_subtitle, "half-written by a killed SubSync");
        _state.Records[_episode.Node.Id] = Records.For(_episode.Node.Id, _episode.VideoPath!, SyncStatus.Done);

        var recovery = new RecoveryService(_state, _backup, NullLogger<RecoveryService>.Instance);
        var restored = await recovery.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, restored);
        AssertOriginalIntact();
        Assert.False(_state.Records.ContainsKey(_episode.Node.Id), "episode must be marked not synced");
    }

    [Fact]
    public async Task Recovery_with_missing_bak_just_clears_the_journal()
    {
        await _state.AddInFlightAsync(new InFlightBackup(_episode.Node.Id, _subtitle, Backup, DateTime.UtcNow), CancellationToken.None);

        var recovery = new RecoveryService(_state, _backup, NullLogger<RecoveryService>.Instance);
        Assert.Equal(0, await recovery.RecoverAsync(CancellationToken.None));

        AssertOriginalIntact();
    }

    [Fact]
    public async Task Success_keeps_new_content_deletes_bak_records_done_and_refreshes()
    {
        _worker.Behavior = Overwrite(Synced, WorkerStatus.Synced);

        var outcome = await _runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(JobItemStatus.Done, outcome.Status);
        Assert.Equal(Synced, File.ReadAllText(_subtitle));
        Assert.False(File.Exists(Backup));
        Assert.Empty(_state.InFlight);
        var record = _state.Records[_episode.Node.Id];
        Assert.Equal(SyncStatus.Done, record.Status);
        Assert.Equal("1.2.0", record.SubSyncVersion);
        Assert.Equal(new[] { _episode.Node.Id }, _refresh.Requested);
    }

    [Theory]
    [InlineData(112, 30, 234)] // 22 Jump Street on CPU: the fixed 30 minutes stopped it partway (live, 2026-11-11)
    [InlineData(22, 30, 54)]
    [InlineData(10, 30, 30)]   // short items keep the setting
    [InlineData(112, 300, 300)] // a larger setting wins
    public void Time_limit_scales_with_the_video_length(int runtimeMinutes, int settingMinutes, int expectedMinutes)
    {
        var options = new WorkerOptions(true, false, TimeSpan.FromMinutes(settingMinutes));

        var effective = JobRunner.WithTimeLimit(options, TimeSpan.FromMinutes(runtimeMinutes));

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), effective.Timeout);
        Assert.Equal(options.TidyUpLines, effective.TidyUpLines);
        Assert.Equal(options.ForceCpu, effective.ForceCpu);
    }

    [Fact]
    public void Unknown_length_keeps_the_setting()
    {
        var options = new WorkerOptions(true, false, TimeSpan.FromMinutes(30));
        Assert.Equal(options, JobRunner.WithTimeLimit(options, null));
        Assert.Equal(options, JobRunner.WithTimeLimit(options, TimeSpan.Zero));
    }

    [Fact]
    public async Task Runner_passes_the_scaled_limit_to_SubSync()
    {
        _library.SetRuntime(_episode.Node.Id, TimeSpan.FromMinutes(22));
        _worker.Behavior = (_, sub, _) =>
        {
            File.WriteAllText(sub, Synced);
            return Task.FromResult(FakeWorker.Result(WorkerStatus.Synced));
        };

        await _runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(54), _worker.LastOptions!.Timeout);
    }

    [Fact]
    public async Task Movies_go_through_the_same_swap_and_restore()
    {
        var movieVideo = _dir.File("Alpha (2001).mkv", "movie bytes");
        var movieSub = _dir.File("Alpha (2001).en.hi.srt", Original);
        var movie = _library.AddMovie("Alpha", 2001, movieVideo, "eng");
        var job = new JobItemSnapshot(Guid.NewGuid(), movie.Node.Id, "Alpha (2001)", string.Empty, Guid.NewGuid(), JobSource.Manual, null, JobItemStatus.Running, null, DateTime.UtcNow, DateTime.UtcNow, null, null);

        _worker.Behavior = Overwrite("garbage", WorkerStatus.Failed, "boom");
        var failed = await _runner.RunAsync(job, CancellationToken.None);
        Assert.Equal(JobItemStatus.Failed, failed.Status);
        Assert.Equal(Original, File.ReadAllText(movieSub));
        Assert.False(File.Exists(movieSub + ".bak"));

        _worker.Behavior = Overwrite(Synced, WorkerStatus.Synced);
        var done = await _runner.RunAsync(job, CancellationToken.None);
        Assert.Equal(JobItemStatus.Done, done.Status);
        Assert.Equal(Synced, File.ReadAllText(movieSub));
        Assert.Contains(movie.Node.Id, _refresh.Requested);
    }

    [Fact]
    public async Task Second_run_skips_a_synced_episode_without_touching_it()
    {
        _worker.Behavior = Overwrite(Synced, WorkerStatus.Synced);
        await _runner.RunAsync(Job(), CancellationToken.None);

        var second = await _runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(JobItemStatus.Skipped, second.Status);
        Assert.Equal(1, _worker.Calls);
        Assert.Equal(Synced, File.ReadAllText(_subtitle));
    }

    [Fact]
    public async Task Subtitle_removed_after_queuing_fails_untouched()
    {
        File.Delete(_subtitle);

        var outcome = await _runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(0, _worker.Calls);
        Assert.Equal(JobItemStatus.Failed, outcome.Status);
        Assert.Equal("The English subtitle next to the video is gone.", outcome.Message);
    }

    private sealed class ThrowingJournalStore : IStateStore
    {
        private readonly FakeStateStore _inner;

        public ThrowingJournalStore(FakeStateStore inner) => _inner = inner;

        public string Location => _inner.Location;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<Guid, SyncRecord>> GetAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) => _inner.GetAsync(itemIds, cancellationToken);

        public Task UpsertAsync(SyncRecord record, CancellationToken cancellationToken) => _inner.UpsertAsync(record, cancellationToken);

        public Task DeleteAsync(Guid itemId, CancellationToken cancellationToken) => _inner.DeleteAsync(itemId, cancellationToken);

        public Task AddInFlightAsync(InFlightBackup entry, CancellationToken cancellationToken) => throw new IOException("database is locked");

        public Task RemoveInFlightAsync(Guid itemId, CancellationToken cancellationToken) => _inner.RemoveInFlightAsync(itemId, cancellationToken);

        public Task<IReadOnlyList<InFlightBackup>> GetInFlightAsync(CancellationToken cancellationToken) => _inner.GetInFlightAsync(cancellationToken);

        public Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken) => _inner.GetMetaAsync(key, cancellationToken);

        public Task SetMetaAsync(string key, string value, CancellationToken cancellationToken) => _inner.SetMetaAsync(key, value, cancellationToken);

        public Task AddFailedSyncAsync(FailedSync entry, int keep, CancellationToken cancellationToken) => _inner.AddFailedSyncAsync(entry, keep, cancellationToken);

        public Task<(IReadOnlyList<FailedSync> Items, int TotalCount)> GetFailedSyncsAsync(Guid? userId, int startIndex, int limit, CancellationToken cancellationToken) =>
            _inner.GetFailedSyncsAsync(userId, startIndex, limit, cancellationToken);

        public Task<FailedSync?> GetFailedSyncAsync(long id, CancellationToken cancellationToken) => _inner.GetFailedSyncAsync(id, cancellationToken);

        public Task<int> RemoveFailedSyncsAsync(Guid? userId, long? id, CancellationToken cancellationToken) => _inner.RemoveFailedSyncsAsync(userId, id, cancellationToken);
    }
}
