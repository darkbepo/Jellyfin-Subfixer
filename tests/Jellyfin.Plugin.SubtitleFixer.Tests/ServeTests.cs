using System.ComponentModel;
using System.Text.Json;
using System.Threading.Channels;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.State;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

/// <summary>One request a <see cref="FakeServe"/> received.</summary>
/// <param name="Id">Request id.</param>
/// <param name="Video">Video.</param>
/// <param name="Subtitle">Subtitle.</param>
/// <param name="Output">Output file.</param>
/// <param name="BackedUp">Whether the subtitle's <c>.bak</c> existed when the request arrived.</param>
/// <param name="ReceivedUtc">When it arrived.</param>
public sealed record ServeRequest(string Id, string Video, string Subtitle, string Output, bool BackedUp, DateTime ReceivedUtc);

/// <summary>
/// A scripted <c>subsync-cli serve</c>: plain-text setup lines, then <c>ready</c>, then requests handled one after
/// another like SubSync (each gets <c>queued</c> as it arrives). <see cref="Behavior"/> scripts each request.
/// </summary>
public sealed class FakeServe : IServeTransport
{
    private readonly Channel<ServeRequest> _requests = Channel.CreateUnbounded<ServeRequest>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _end = new();
    private readonly object _gate = new();
    private Action<string>? _onLine;
    private int _inFlight;

    public FakeServe(IReadOnlyList<string> arguments) => Arguments = arguments;

    public IReadOnlyList<string> Arguments { get; }

    public Func<FakeServe, ServeRequest, Task> Behavior { get; set; } = Normal;

    public int? ExitBeforeReady { get; set; }

    public bool StartThrows { get; set; }

    public bool NeverReady { get; set; }

    public List<ServeRequest> Requests { get; } = new();

    public List<string> RawLines { get; } = new();

    public List<(string Id, DateTime At)> Results { get; } = new();

    public int MaxInFlight { get; private set; }

    public bool InputClosed { get; private set; }

    public bool Killed { get; private set; }

    public Task<int> Exited => _exited.Task;

    public static ServeRequest? Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        var subtitle = r.GetProperty("subtitle").GetString()!;
        return new ServeRequest(r.GetProperty("id").GetString()!, r.GetProperty("video").GetString()!, subtitle, r.GetProperty("output").GetString()!, File.Exists(subtitle + ".bak"), DateTime.UtcNow);
    }

    /// <summary>What SubSync normally does: read, listen in steps, match, write the result over the subtitle.</summary>
    public static async Task Normal(FakeServe s, ServeRequest r)
    {
        s.Stage(r, "reading_audio", null);
        await s.Delay(15);
        s.Stage(r, "listening", null);
        await s.Delay(15);
        s.Stage(r, "listening", 50);
        await s.Delay(15);
        s.Stage(r, "listening", 100);
        s.Stage(r, "matching", null);
        await s.Delay(5);
        s.Synced(r);
    }

    public void Start(Action<string> onLine)
    {
        if (StartThrows)
        {
            throw new Win32Exception(2, "The system cannot find the file specified.");
        }

        _onLine = onLine;
        _ = Task.Run(LoopAsync);
    }

    public bool TryWriteLine(string line)
    {
        if (_exited.Task.IsCompleted)
        {
            return false;
        }

        var request = Parse(line)!;
        lock (_gate)
        {
            RawLines.Add(line);
            Requests.Add(request);
            _inFlight++;
            MaxInFlight = Math.Max(MaxInFlight, _inFlight);
        }

        Stage(request, "queued", null);
        _requests.Writer.TryWrite(request);
        return true;
    }

    public void CloseInput()
    {
        InputClosed = true;
        _requests.Writer.TryComplete();
    }

    public void Kill()
    {
        Killed = true;
        Exit(-1);
    }

    public void Dispose()
    {
        if (!_exited.Task.IsCompleted)
        {
            Kill();
        }
    }

    public void Emit(string line)
    {
        if (!_exited.Task.IsCompleted)
        {
            _onLine?.Invoke(line);
        }
    }

    public void Stage(ServeRequest r, string stage, int? percent) =>
        Emit($$"""{"event":"stage","id":{{JsonSerializer.Serialize(r.Id)}},"stage":"{{stage}}","percent":{{(percent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")}}}""");

    public void Synced(ServeRequest r)
    {
        File.WriteAllText(r.Output, Recorded.Synced);
        Result(r, $$"""
            "status":"synced","output":{{JsonSerializer.Serialize(r.Output)}},"lines_matched":10,"lines_with_speech":11,"audio_language":"en","seconds":0.1,
            "timings":{"from_cache":false,"read_audio":0.02,"listen":0.05,"match":0.01}
            """);
    }

    public void Result(ServeRequest r, string fields)
    {
        lock (_gate)
        {
            Results.Add((r.Id, DateTime.UtcNow));
            _inFlight--;
        }

        Emit($$"""{"event":"result","id":{{JsonSerializer.Serialize(r.Id)}},"video":{{JsonSerializer.Serialize(r.Video)}},"subtitle":{{JsonSerializer.Serialize(r.Subtitle)}},{{fields.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal)}}}""");
    }

    public Task Delay(int milliseconds) => Task.Delay(milliseconds, _end.Token);

    /// <summary>Waits until the process is ended (a hung item).</summary>
    public Task Hang() => Task.Delay(Timeout.Infinite, _end.Token);

    public async Task WaitForRequests(int count)
    {
        while (true)
        {
            lock (_gate)
            {
                if (Requests.Count >= count)
                {
                    return;
                }
            }

            await Delay(5);
        }
    }

    public void Exit(int code)
    {
        if (_exited.TrySetResult(code))
        {
            _end.Cancel();
            _requests.Writer.TryComplete();
        }
    }

    private async Task LoopAsync()
    {
        Emit("SubSync self-check: 1 file(s) repaired");
        if (ExitBeforeReady is { } code)
        {
            Exit(code);
            return;
        }

        if (NeverReady)
        {
            return;
        }

        Emit("""{"event":"ready","subsync_version":"1.3.0"}""");
        Emit("""{"event":"device","device":"gpu","name":"NVIDIA GeForce RTX 3050","model_load_seconds":6.2}""");
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(_end.Token))
            {
                await Behavior(this, request);
            }

            Exit(0);
        }
        catch (OperationCanceledException)
        {
            // Killed, or exited by the script.
        }
    }
}

/// <summary>Starts <see cref="FakeServe"/> sessions for a worker.</summary>
public sealed class FakeServeFactory
{
    public List<FakeServe> Started { get; } = new();

    public Action<FakeServe>? Configure { get; set; }

    public async Task<SessionStart> StartAsync(WorkerOptions options, CancellationToken cancellationToken)
    {
        var serve = new FakeServe(CliWorker.BuildServeArguments(options));
        lock (Started)
        {
            Started.Add(serve);
        }

        Configure?.Invoke(serve);
        var tidy = options.TidyUpLines;
        var cpu = options.ForceCpu;
        return await ServeSession.StartAsync(serve, o => o.TidyUpLines == tidy && o.ForceCpu == cpu, options.Timeout, NullLogger.Instance, cancellationToken);
    }
}

public sealed class ServeSessionTests
{
    private static Task<SessionStart> Start(FakeServe serve, TimeSpan? timeout = null, CancellationToken token = default) =>
        ServeSession.StartAsync(serve, _ => true, timeout ?? TimeSpan.FromSeconds(5), NullLogger.Instance, token);

    [Fact]
    public async Task Setup_text_before_ready_is_ignored()
    {
        var serve = new FakeServe([]);
        var start = await Start(serve);

        Assert.NotNull(start.Session);
        Assert.Equal("1.3.0", start.Session!.Version);
        Assert.Contains("self-check", start.Session.Tail(), StringComparison.Ordinal);
        start.Session.Dispose();
    }

    [Fact]
    public async Task Exit_7_before_ready_is_unavailable()
    {
        var start = await Start(new FakeServe([]) { ExitBeforeReady = 7 });

        Assert.Equal(WorkerStatus.Unavailable, start.Failure);
        Assert.Null(start.Session);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    public async Task Other_exits_before_ready_fall_back_to_one_sync_per_item(int code)
    {
        var start = await Start(new FakeServe([]) { ExitBeforeReady = code });

        Assert.True(start.Fallback);
        Assert.Contains(code.ToString(System.Globalization.CultureInfo.InvariantCulture), start.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_process_that_cannot_start_falls_back()
    {
        Assert.True((await Start(new FakeServe([]) { StartThrows = true })).Fallback);
    }

    [Fact]
    public async Task Setup_that_hangs_times_out_and_ends_the_process()
    {
        var serve = new FakeServe([]) { NeverReady = true };
        var start = await Start(serve, TimeSpan.FromMilliseconds(100));

        Assert.Equal(WorkerStatus.TimedOut, start.Failure);
        Assert.True(serve.Killed);
    }

    [Fact]
    public async Task Cancel_while_waiting_for_ready_ends_the_process()
    {
        var serve = new FakeServe([]) { NeverReady = true };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var start = await Start(serve, TimeSpan.FromSeconds(30), cancel.Token);

        Assert.Equal(WorkerStatus.Cancelled, start.Failure);
        Assert.True(serve.Killed);
    }

    [Fact]
    public async Task Events_reach_the_item_and_unknown_lines_are_ignored()
    {
        using var dir = new TempDir();
        var subtitle = dir.File("E1.en.hi.srt", "original");
        var serve = new FakeServe([])
        {
            Behavior = async (s, r) =>
            {
                s.Emit("not json at all");
                s.Emit("{broken");
                s.Emit("""{"event":"future","id":"x"}""");
                s.Emit("""{"event":"stage","id":"someone-else","stage":"listening","percent":3}""");
                s.Emit("""{"event":"warning","text":"The GPU stopped working; continuing on the CPU"}""");
                s.Emit("""{"event":"device","device":"cpu","name":null,"model_load_seconds":1.0}""");
                await Task.Yield();
                await FakeServe.Normal(s, r);
            },
        };
        var session = (await Start(serve)).Session!;
        var seen = new List<WorkerProgress>();

        var item = session.Send("job-1", dir.File("E1.mkv"), subtitle, subtitle, p => { lock (seen) { seen.Add(p); } });
        var result = await item.Result.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkerStatus.Synced, result.Status);
        Assert.Equal(subtitle, result.OutputPath);
        Assert.Equal("1.3.0", result.SubSyncVersion);
        Assert.Equal(new SubSyncTimings(false, 0.02, 0.05, 0.01), result.Timings);
        Assert.Equal(Recorded.Synced, File.ReadAllText(subtitle));
        Assert.Equal(SubSyncStage.Queued, seen[0].Stage);
        Assert.Contains(new WorkerProgress(SubSyncStage.Listening, 50), seen);
        Assert.True(item.ListeningReached.IsCompleted);
        Assert.Equal(0, session.InFlight);
        session.Dispose();
    }

    [Fact]
    public async Task A_refused_request_fails_that_item_only()
    {
        var serve = new FakeServe([])
        {
            Behavior = (s, r) =>
            {
                if (r.Id == "bad")
                {
                    s.Result(r, "\"status\":\"failed\",\"reason\":\"subtitle not found: x.srt\"");
                    return Task.CompletedTask;
                }

                return FakeServe.Normal(s, r);
            },
        };
        using var dir = new TempDir();
        var session = (await Start(serve)).Session!;

        var bad = session.Send("bad", "v.mkv", dir.Path + "/x.srt", dir.Path + "/x.srt", null);
        var good = session.Send("good", "v.mkv", dir.File("ok.srt"), dir.Path + "/ok.srt", null);

        var failed = await bad.Result.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkerStatus.Failed, failed.Status);
        Assert.Equal("subtitle not found: x.srt", failed.Reason);
        Assert.Equal(WorkerStatus.Synced, (await good.Result.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        session.Dispose();
    }

    [Fact]
    public async Task The_next_items_clock_starts_at_its_first_stage_or_the_previous_result()
    {
        var release = new TaskCompletionSource();
        var serve = new FakeServe([])
        {
            Behavior = async (s, r) =>
            {
                if (r.Id == "a")
                {
                    s.Stage(r, "listening", 10);
                    await release.Task;
                }

                await FakeServe.Normal(s, r);
            },
        };
        var session = (await Start(serve)).Session!;

        var a = session.Send("a", "v", "a.srt", Path.Combine(Path.GetTempPath(), "sf-a-" + Guid.NewGuid().ToString("N") + ".srt"), null);
        var b = session.Send("b", "v", "b.srt", Path.Combine(Path.GetTempPath(), "sf-b-" + Guid.NewGuid().ToString("N") + ".srt"), null);
        await a.ListeningReached.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.True(a.ClockStarted.IsCompleted, "nothing was in progress when a was sent");
        Assert.False(b.ClockStarted.IsCompleted, "b waits behind a: its time hasn't started");

        release.SetResult();
        var bResult = await b.Result.WaitAsync(TimeSpan.FromSeconds(5));
        var aResult = await a.Result;
        Assert.True(b.ClockStarted.IsCompleted);
        Assert.True(await b.ClockStarted >= await a.ClockStarted);
        session.Dispose();
        File.Delete(aResult.OutputPath!);
        File.Delete(bResult.OutputPath!);
    }
}

public sealed class CliWorkerServeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<FakeServe> _started = new();

    public CliWorkerServeTests() => _dir.File(SubSyncProbe.CliFileName, "exe");

    public void Dispose() => _dir.Dispose();

    private CliWorker Worker(string? version, Func<string?>? folder = null) =>
        new(
            folder ?? (() => _dir.Path),
            NullLogger<CliWorker>.Instance,
            (_, _) => Task.FromResult(version is null ? null : Version.Parse(version)),
            (_, _, _, _, _, _) => throw new InvalidOperationException("sync must not run"),
            (_, args, _) =>
            {
                var serve = new FakeServe(args);
                _started.Add(serve);
                return serve;
            });

    [Fact]
    public void Serve_arguments_follow_the_settings()
    {
        Assert.Equal(new[] { "serve", "--overwrite" }, CliWorker.BuildServeArguments(new WorkerOptions(true, false, TimeSpan.FromMinutes(1))));
        Assert.Equal(new[] { "serve", "--overwrite", "--keep-layout", "--cpu" }, CliWorker.BuildServeArguments(new WorkerOptions(false, true, TimeSpan.FromMinutes(1))));
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData(null)]
    public async Task Before_1_3_0_there_is_no_serve(string? version)
    {
        var start = await Worker(version).StartSessionAsync(new WorkerOptions(true, false, TimeSpan.FromMinutes(1)), CancellationToken.None);

        Assert.True(start.Fallback);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task From_1_3_0_serve_starts_with_the_settings_and_notices_changes()
    {
        var folder = _dir.Path;
        var options = new WorkerOptions(false, true, TimeSpan.FromMinutes(1));
        var start = await Worker("1.3.0", () => folder).StartSessionAsync(options, CancellationToken.None);

        var session = start.Session!;
        Assert.Equal(new[] { "serve", "--overwrite", "--keep-layout", "--cpu" }, Assert.Single(_started).Arguments);
        Assert.True(session.Matches(options with { Timeout = TimeSpan.FromHours(3) }));
        Assert.False(session.Matches(options with { TidyUpLines = true }));
        Assert.False(session.Matches(options with { ForceCpu = false }));
        folder = Path.Combine(_dir.Path, "elsewhere");
        Assert.False(session.Matches(options));
        session.Dispose();
    }
}

/// <summary>
/// The queue with one <c>serve</c> process (D45): the normal flow, refused requests, a crash mid-item, cancel and
/// shutdown with two items in flight, a timeout, settings changing mid-queue, exit 7, and the fallback. After every
/// case except a sync, the original subtitle is byte-identical, no <c>.bak</c> remains and the journal is empty.
/// </summary>
public sealed class ServeQueueTests : IDisposable
{
    private const string Original = "1\n00:00:01,000 --> 00:00:02,000\nHi\n";
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    private readonly TempDir _dir = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeStateStore _state = new();
    private readonly FakeWorker _worker = new();
    private readonly FakeServeFactory _serve = new();
    private readonly JobQueue _queue = new(TimeProvider.System);
    private readonly JobProcessor _processor;
    private readonly List<JobRequestItem> _items = new();
    private readonly List<string> _subtitles = new();
    private volatile bool _tidy = true;
    private TimeSpan _timeout = TimeSpan.FromMinutes(1);

    public ServeQueueTests()
    {
        _worker.Sessions = _serve.StartAsync;
        var backup = new BackupService(_state, TimeProvider.System, NullLogger<BackupService>.Instance);
        var runner = new JobRunner(_library, _state, backup, _worker, new FakeRefresh(), () => new WorkerOptions(_tidy, false, _timeout), TimeProvider.System, NullLogger<JobRunner>.Instance);
        _processor = new JobProcessor(_queue, runner, new StartupStatus(), _state, NullLogger<JobProcessor>.Instance);

        var season = _library.AddSeason(_library.AddSeries("Show"), 1);
        for (var n = 1; n <= 6; n++)
        {
            var video = _dir.File($"Show S01E0{n}.mkv", "v");
            _subtitles.Add(_dir.File($"Show S01E0{n}.en.srt", Original));
            var e = _library.AddEpisode(season, n, video, "eng");
            _items.Add(new JobRequestItem(e.Node.Id, "E" + n, "Show"));
        }
    }

    public void Dispose()
    {
        _queue.Dispose();
        _dir.Dispose();
    }

    private Func<FakeServe, ServeRequest, Task> Behavior
    {
        set => _serve.Configure = s => s.Behavior = value;
    }

    private static int Episode(ServeRequest r) => int.Parse(Path.GetFileName(r.Subtitle)["Show S01E0".Length..][..1], System.Globalization.CultureInfo.InvariantCulture);

    private Task RunAsync(CancellationToken stopping = default) => Task.Run(async () =>
    {
        using var take = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _processor.RunQueueAsync(await _queue.TakeAsync(take.Token), stopping);
    });

    private void Enqueue(int count, Guid? user = null) =>
        _queue.Enqueue(user ?? Alice, _items.Take(count).ToList(), JobSource.Manual, null, 300);

    private JobItemSnapshot Item(int n) => _queue.Snapshot().Items.Single(i => i.Name == "E" + n);

    private void AssertIntact(int n)
    {
        Assert.Equal(Original, File.ReadAllText(_subtitles[n - 1]));
        Assert.False(File.Exists(_subtitles[n - 1] + ".bak"), $"E{n}: .bak must not remain");
    }

    private void AssertSynced(int n)
    {
        Assert.Equal(JobItemStatus.Done, Item(n).Status);
        Assert.Equal(Recorded.Synced, File.ReadAllText(_subtitles[n - 1]));
        Assert.False(File.Exists(_subtitles[n - 1] + ".bak"));
    }

    [Fact]
    public async Task A_season_runs_through_one_SubSync_with_the_next_item_sent_while_one_listens()
    {
        Enqueue(4);

        await RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var serve = Assert.Single(_serve.Started);
        Assert.Equal(new[] { "serve", "--overwrite" }, serve.Arguments);
        for (var n = 1; n <= 4; n++)
        {
            AssertSynced(n);
        }

        Assert.Equal(4, serve.Requests.Count);
        Assert.All(serve.Requests, r => Assert.True(r.BackedUp, "every subtitle is backed up before it is sent"));
        Assert.All(serve.Requests, r => Assert.Equal(r.Subtitle, r.Output));
        Assert.Equal(2, serve.MaxInFlight);
        Assert.True(serve.Requests[1].ReceivedUtc <= serve.Results[0].At, "the second item was sent before the first finished");
        Assert.True(serve.InputClosed, "SubSync was closed by its input once the queue was empty");
        Assert.Equal(0, await serve.Exited);
        Assert.Empty(_state.InFlight);
        Assert.All(_state.Records.Values, r => Assert.Equal(SyncStatus.Done, r.Status));
        Assert.Equal(0, _worker.Calls);
        Assert.Equal(0, _processor.InFlightCount);
    }

    [Fact]
    public async Task A_refused_request_fails_that_item_and_the_rest_carry_on()
    {
        Enqueue(3);
        Behavior = (s, r) =>
        {
            if (Episode(r) == 2)
            {
                File.WriteAllText(r.Subtitle, "SubSync must not have written this, but restore anyway");
                s.Result(r, "\"status\":\"failed\",\"reason\":\"another item in progress uses this file\"");
                return Task.CompletedTask;
            }

            return FakeServe.Normal(s, r);
        };

        await RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        AssertSynced(1);
        AssertSynced(3);
        Assert.Equal(JobItemStatus.Failed, Item(2).Status);
        Assert.Equal("another item in progress uses this file", Item(2).Message);
        AssertIntact(2);
        Assert.Single(_serve.Started);
        Assert.Null(_queue.Snapshot().StopReason);
    }

    [Fact]
    public async Task A_crash_mid_item_restores_both_items_and_counts_once()
    {
        // Every SubSync crashes with two items in it. Counted per item, the queue would stop after the second crash.
        Enqueue(6);
        Behavior = async (s, r) =>
        {
            s.Stage(r, "listening", 20);
            File.WriteAllText(r.Subtitle, "half-written garbage");
            await s.WaitForRequests(2);
            await Task.Delay(30);
            s.Exit(10);
        };

        await RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, _serve.Started.Count);
        for (var n = 1; n <= 6; n++)
        {
            Assert.Equal(JobItemStatus.Failed, Item(n).Status);
            Assert.Contains("exit 10", Item(n).Message, StringComparison.Ordinal);
            AssertIntact(n);
        }

        Assert.Contains("3 times", _queue.Snapshot().StopReason, StringComparison.Ordinal);
        Assert.Empty(_state.InFlight);
        Assert.All(_state.Records.Values, r => Assert.Equal(SyncStatus.Failed, r.Status));
    }

    [Fact]
    public async Task Cancelling_one_of_two_items_in_flight_restores_both_and_requeues_the_other()
    {
        _queue.Enqueue(Alice, [_items[0]], JobSource.Manual, null, 300);
        _queue.Enqueue(Bob, [_items[1]], JobSource.Manual, null, 300);
        var bothInFlight = new TaskCompletionSource();
        Behavior = async (s, r) =>
        {
            if (_serve.Started.Count == 1)
            {
                s.Stage(r, "listening", 10);
                await s.WaitForRequests(2);
                bothInFlight.TrySetResult();
                await s.Hang();
            }

            await FakeServe.Normal(s, r);
        };

        var run = RunAsync();
        await bothInFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(30);
        Assert.Equal(2, _queue.Snapshot().Items.Count(i => i.Status == JobItemStatus.Running));
        Assert.Equal(1, _queue.Cancel(Alice, isAdmin: false));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(JobItemStatus.Cancelled, Item(1).Status);
        AssertIntact(1);
        Assert.False(_state.Records.ContainsKey(_items[0].ItemId), "a cancel records nothing");

        // Bob's episode went back to the queue untried and ran in a new SubSync.
        Assert.True(_serve.Started[0].Killed);
        Assert.Equal(2, _serve.Started.Count);
        AssertSynced(2);
        Assert.Empty(_state.InFlight);
        Assert.Null(_queue.Snapshot().StopReason);
    }

    [Fact]
    public async Task Cancelling_everything_with_two_in_flight_restores_both()
    {
        Enqueue(4);
        var bothInFlight = new TaskCompletionSource();
        Behavior = async (s, r) =>
        {
            s.Stage(r, "listening", 10);
            File.WriteAllText(r.Subtitle, "half-written garbage");
            await s.WaitForRequests(2);
            bothInFlight.TrySetResult();
            await s.Hang();
        };

        var run = RunAsync();
        await bothInFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(30);
        _queue.Cancel(Guid.NewGuid(), isAdmin: true);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(_serve.Started);
        Assert.True(_serve.Started[0].Killed);
        for (var n = 1; n <= 4; n++)
        {
            Assert.Equal(JobItemStatus.Cancelled, Item(n).Status);
            AssertIntact(n);
        }

        Assert.Empty(_state.InFlight);
        Assert.Empty(_state.Records);
    }

    [Fact]
    public async Task Jellyfin_stopping_with_two_in_flight_restores_both_and_leaves_the_rest_queued()
    {
        Enqueue(4);
        var bothInFlight = new TaskCompletionSource();
        Behavior = async (s, r) =>
        {
            s.Stage(r, "listening", 10);
            File.WriteAllText(r.Subtitle, "half-written garbage");
            await s.WaitForRequests(2);
            bothInFlight.TrySetResult();
            await s.Hang();
        };
        using var stopping = new CancellationTokenSource();

        var run = RunAsync(stopping.Token);
        await bothInFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stopping.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(_serve.Started[0].Killed);
        for (var n = 1; n <= 4; n++)
        {
            AssertIntact(n);
        }

        Assert.Equal(JobItemStatus.Cancelled, Item(1).Status);
        Assert.Equal(JobItemStatus.Cancelled, Item(2).Status);
        Assert.Equal(JobItemStatus.Queued, Item(3).Status);
        Assert.Empty(_state.InFlight);
        Assert.Empty(_state.Records);
    }

    [Fact]
    public async Task A_timeout_fails_that_item_and_requeues_the_one_waiting_behind_it()
    {
        _timeout = TimeSpan.FromMilliseconds(400);
        Enqueue(2);
        Behavior = async (s, r) =>
        {
            if (_serve.Started.Count == 1 && Episode(r) == 1)
            {
                s.Stage(r, "listening", 10);
                await s.Hang();
            }

            await FakeServe.Normal(s, r);
        };

        await RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(JobItemStatus.Failed, Item(1).Status);
        Assert.Contains("Timed out", Item(1).Message, StringComparison.Ordinal);
        Assert.Equal("timeout", _state.Records[_items[0].ItemId].Message);
        AssertIntact(1);
        Assert.Equal(2, _serve.Started.Count);
        AssertSynced(2);
        Assert.Empty(_state.InFlight);
    }

    [Fact]
    public async Task Waiting_behind_another_item_does_not_count_toward_the_time_limit()
    {
        // Each item takes about 700 ms; the second waits behind the first for most of that, so it is in SubSync
        // for well over the 1000 ms limit, but its own run is within it.
        _timeout = TimeSpan.FromMilliseconds(1000);
        Enqueue(2);
        Behavior = async (s, r) =>
        {
            s.Stage(r, "reading_audio", null);
            s.Stage(r, "listening", 0);
            await s.Delay(700);
            s.Synced(r);
        };

        await RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        AssertSynced(1);
        AssertSynced(2);
        var serve = Assert.Single(_serve.Started);
        Assert.True(serve.Results[1].At - serve.Requests[1].ReceivedUtc > TimeSpan.FromMilliseconds(1000), "the second item was in SubSync longer than the limit");
    }

    [Fact]
    public async Task Changed_settings_start_a_new_SubSync_after_the_current_item()
    {
        Enqueue(3);
        Behavior = async (s, r) =>
        {
            if (_serve.Started.Count == 1)
            {
                // The admin turns Tidy up lines off while the first item runs.
                _tidy = false;
            }

            await FakeServe.Normal(s, r);
        };

        await RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, _serve.Started.Count);
        var first = _serve.Started[0];
        var second = _serve.Started[1];
        Assert.Single(first.Requests);
        Assert.True(first.InputClosed, "the old SubSync finished its item and was closed, not killed");
        Assert.False(first.Killed);
        Assert.Equal(new[] { "serve", "--overwrite", "--keep-layout" }, second.Arguments);
        Assert.Equal(2, second.Requests.Count);
        for (var n = 1; n <= 3; n++)
        {
            AssertSynced(n);
        }
    }

    [Fact]
    public async Task Exit_7_at_start_stops_the_queue_as_today()
    {
        Enqueue(3);
        _serve.Configure = s => s.ExitBeforeReady = 7;

        await RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var snapshot = _queue.Snapshot();
        Assert.Contains("exit 7", snapshot.StopReason, StringComparison.Ordinal);
        Assert.All(snapshot.Items, i => Assert.Equal(JobItemStatus.Cancelled, i.Status));
        AssertIntact(1);
        Assert.Empty(_state.Records);
        Assert.Single(_serve.Started);
    }

    [Fact]
    public async Task Exit_7_mid_run_restores_and_stops_the_queue()
    {
        Enqueue(4);
        Behavior = async (s, r) =>
        {
            s.Stage(r, "listening", 10);
            await s.WaitForRequests(2);
            s.Exit(7);
            await Task.CompletedTask;
        };

        await RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(_queue.Snapshot().StopReason);
        for (var n = 1; n <= 4; n++)
        {
            Assert.Equal(JobItemStatus.Cancelled, Item(n).Status);
            AssertIntact(n);
        }

        Assert.Empty(_state.Records);
    }

    [Fact]
    public async Task Without_serve_every_item_gets_its_own_SubSync_as_before()
    {
        Enqueue(3);
        var asked = 0;
        _worker.Sessions = (_, _) =>
        {
            asked++;
            return Task.FromResult(SessionStart.NotAvailable("SubSync 1.2.0 has no serve command"));
        };
        _worker.Behavior = (_, sub, _) =>
        {
            File.WriteAllText(sub, Recorded.Synced);
            return Task.FromResult(FakeWorker.Result(WorkerStatus.Synced));
        };

        await RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, asked);
        Assert.Equal(3, _worker.Calls);
        for (var n = 1; n <= 3; n++)
        {
            AssertSynced(n);
        }
    }

    [Fact]
    public async Task Skipped_and_missing_items_are_settled_without_reaching_SubSync()
    {
        Enqueue(3);
        File.Delete(_subtitles[1]);

        await RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        AssertSynced(1);
        Assert.Equal(JobItemStatus.Failed, Item(2).Status);
        AssertSynced(3);
        Assert.Equal(2, Assert.Single(_serve.Started).Requests.Count);
    }

    [Fact]
    public async Task The_running_items_progress_comes_from_SubSync()
    {
        Enqueue(1);
        var seen = new List<(string? Stage, int? Percent)>();
        Behavior = async (s, r) =>
        {
            s.Stage(r, "reading_audio", null);
            s.Stage(r, "listening", 50);
            await Task.Delay(50);
            lock (seen)
            {
                var current = _queue.Snapshot().Current!;
                seen.Add((current.Stage, current.Percent));
            }

            s.Synced(r);
        };

        await RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // No runtime in this library, so no estimate and no percent: the stage alone.
        Assert.Equal((SubSyncStage.Listening, (int?)null), Assert.Single(seen));
        AssertSynced(1);
    }
}

/// <summary>
/// The real <see cref="ServeProcess"/> (stdin, stdout, exit, kill) against a small shell stand-in for <c>serve</c>.
/// Unix only: the stand-in is a <c>/bin/sh</c> script. The process code itself is the same on Windows.
/// </summary>
public sealed class ServeProcessTests
{
    private const string Echo = """
        echo "SubSync self-check: all files intact"
        echo '{"event":"ready","subsync_version":"1.3.0"}'
        while IFS= read -r line; do
          id=$(printf '%s' "$line" | sed 's/.*"id":"\([^"]*\)".*/\1/')
          echo "{\"event\":\"stage\",\"id\":\"$id\",\"stage\":\"listening\",\"percent\":40}"
          echo "{\"event\":\"result\",\"id\":\"$id\",\"status\":\"no_match\",\"reason\":\"echo é\"}"
        done
        exit 0
        """;

    private static Task<SessionStart> Start(string script) =>
        ServeSession.StartAsync(new ServeProcess("/bin/sh", ["-c", script], Path.GetTempPath()), _ => true, TimeSpan.FromSeconds(10), NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task Requests_go_in_on_stdin_and_results_come_back_on_stdout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var session = (await Start(Echo)).Session!;
        var seen = new List<WorkerProgress>();
        var item = session.Send("job-1", "/media/Show é/E1.mkv", "/media/Show é/E1.en.srt", "/media/Show é/E1.en.srt", p => { lock (seen) { seen.Add(p); } });

        var result = await item.Result.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkerStatus.NoMatch, result.Status);
        Assert.Equal("echo é", result.Reason);
        Assert.Contains(new WorkerProgress(SubSyncStage.Listening, 40), seen);
        Assert.Contains("self-check", session.Tail(), StringComparison.Ordinal);

        session.Close();
        Assert.Equal(0, await session.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
        session.Dispose();
    }

    [Fact]
    public async Task Kill_ends_the_process_tree_at_once()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var session = (await Start("""echo '{"event":"ready"}'; sleep 60 & wait""")).Session!;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        session.Kill();

        await session.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(session.Killed);
        session.Dispose();
    }

    [Fact]
    public async Task Exit_7_before_ready_is_seen_from_a_real_process()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var start = await Start("echo 'Repair not possible: another SubSync is using this folder'; exit 7");

        Assert.Equal(WorkerStatus.Unavailable, start.Failure);
    }
}
