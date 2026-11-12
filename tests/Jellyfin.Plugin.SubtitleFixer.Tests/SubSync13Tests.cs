using System.Text.Json;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

/// <summary>Recorded SubSync output, as the CLI prints it with piped output.</summary>
internal static class Recorded
{
    public const string Synced = "1\r\n00:00:03,000 --> 00:00:04,500\r\nThe original line.\r\n\r\n";

    public static string P(string json) => SubSyncProgress.Prefix + json;

    public static string[] GpuRun(string output) =>
    [
        "SubSync 2026-11-12 20:15: 1 pair(s) to sync, 0 file(s) without a partner",
        "    1. Show S01E01.mkv  <-  Show S01E01.en.hi.srt",
        P("""{"event":"stage","item":1,"total":1,"stage":"queued","percent":null}"""),
        P("""{"event":"stage","item":1,"total":1,"stage":"reading_audio","percent":null}"""),
        P("""{"event":"device","device":"gpu","name":"NVIDIA GeForce RTX 3050"}"""),
        "Speech recognition: GPU (NVIDIA GeForce RTX 3050)",
        P("""{"event":"stage","item":1,"total":1,"stage":"listening","percent":null}"""),
        P("""{"event":"stage","item":1,"total":1,"stage":"listening","percent":37}"""),
        P("""{"event":"stage","item":1,"total":1,"stage":"listening","percent":100}"""),
        P("""{"event":"stage","item":1,"total":1,"stage":"matching","percent":null}"""),
        P("""{"event":"stage","item":1,"total":1,"stage":"matching","percent":50}"""),
        "OK   [1/1] Show S01E01.mkv -> Show S01E01.en.hi.srt  (612/640 lines matched, shift -2.1..+0.4 s, 2 section(s), 14 ending(s) extended)",
        P("""{"event":"item_done","item":1,"total":1,"status":"synced","output":""" + JsonSerializer.Serialize(output) + "}"),
        "Done in 1m 12s: 1 synced.",
    ];

    public static string Report13(string video, string subtitle, string status = "synced", bool fromCache = false, string? speech = """{ "device": "gpu", "name": "NVIDIA GeForce RTX 3050", "model_load_seconds": 6.2 }""")
    {
        var timings = fromCache
            ? """{ "from_cache": true, "read_audio": null, "listen": null, "match": 4.2 }"""
            : """{ "from_cache": false, "read_audio": 9.8, "listen": 58.1, "match": 4.2 }""";
        var item = status == "synced"
            ? $$"""{ "video": {{JsonSerializer.Serialize(video)}}, "subtitle": {{JsonSerializer.Serialize(subtitle)}}, "status": "synced", "output": {{JsonSerializer.Serialize(subtitle)}}, "lines_matched": 612, "lines_with_speech": 640, "audio_language": "en", "seconds": 72.9, "tidy": true, "timings": {{timings}} }"""
            : $$"""{ "video": {{JsonSerializer.Serialize(video)}}, "subtitle": {{JsonSerializer.Serialize(subtitle)}}, "status": "no_match", "reason": "Only 9 of 655 lines matched the audio – wrong subtitle for this video?", "audio_language": "en" }""";
        return $$"""
            {
              "subsync_version": "1.3.0",
              "finished": "2026-11-12T20:16:27",
              "exit_code": {{(status == "synced" ? 0 : 4)}},
              "seconds": 80.1,
              "summary": { "synced": {{(status == "synced" ? 1 : 0)}}, "no_match": {{(status == "synced" ? 0 : 1)}}, "failed": 0, "cancelled": 0, "skipped": 0 },
              "speech_recognition": {{speech ?? "null"}},
              "items": [ {{item}} ]
            }
            """;
    }

    // Live from the test PC (S5), SubSync 1.2.0.
    public const string Report12 = """
        {
          "subsync_version": "1.2.0",
          "finished": "2026-11-11T23:51:02",
          "exit_code": 0,
          "seconds": 96.4,
          "summary": { "synced": 1, "no_match": 0, "failed": 0, "cancelled": 0, "skipped": 0 },
          "items": [ { "video": "C:\\t\\Clip.mp4", "subtitle": "C:\\t\\Clip.en.srt", "status": "synced", "output": "C:\\t\\Clip.en.srt",
                       "lines_matched": 58, "lines_with_speech": 77, "audio_language": "en", "seconds": 95.8, "tidy": true } ]
        }
        """;
}

/// <summary>A scripted <c>subsync-cli</c>: plays back lines, writes a report and optionally a result file.</summary>
internal sealed class ScriptedCli
{
    public string[] Lines { get; set; } = [];

    public int ExitCode { get; set; }

    public Func<IReadOnlyList<string>, string?> Report { get; set; } = _ => null;

    public Action<IReadOnlyList<string>>? Write { get; set; }

    public TimeSpan Elapsed { get; set; } = TimeSpan.FromSeconds(80);

    public List<IReadOnlyList<string>> Calls { get; } = new();

    public Task<ProcessOutcome> RunAsync(string file, IReadOnlyList<string> args, string dir, TimeSpan timeout, Action<string> onLine, CancellationToken ct)
    {
        Calls.Add(args);
        Write?.Invoke(args);
        foreach (var line in Lines)
        {
            onLine(line);
        }

        if (Report(args) is { } report)
        {
            File.WriteAllText(args[args.ToList().IndexOf("--report") + 1], report);
        }

        return Task.FromResult(new ProcessOutcome(ProcessEnd.Exited, ExitCode, string.Join('\n', Lines), Elapsed));
    }
}

public sealed class SubSyncProgressTests
{
    [Theory]
    [InlineData("""PROGRESS {"event":"stage","item":1,"total":1,"stage":"listening","percent":37}""", SubSyncStage.Listening, 37)]
    [InlineData("""PROGRESS {"event":"stage","item":1,"total":1,"stage":"queued","percent":null}""", SubSyncStage.Queued, null)]
    [InlineData("""PROGRESS {"event":"stage","item":1,"total":1,"stage":"waiting_for_file","percent":null}""", SubSyncStage.WaitingForFile, null)]
    [InlineData("""PROGRESS {"event":"stage","item":1,"total":1,"stage":"thinking_hard","percent":12.6,"eta":5}""", SubSyncStage.Working, 13)]
    [InlineData("""PROGRESS {"event":"stage","stage":"listening","percent":250}""", SubSyncStage.Listening, 100)]
    [InlineData("""PROGRESS {"event":"stage","stage":"matching","percent":"half"}""", SubSyncStage.Matching, null)]
    [InlineData("""PROGRESS {"event":"stage"}""", SubSyncStage.Working, null)]
    public void Stage_lines_give_the_stage_and_percent(string line, string stage, int? percent)
    {
        Assert.Equal(new StageProgress(stage, percent), SubSyncProgress.Parse(line));
    }

    [Fact]
    public void Device_and_item_done_lines_are_read()
    {
        Assert.Equal(new DeviceProgress("gpu", "NVIDIA GeForce RTX 3050"), SubSyncProgress.Parse("""PROGRESS {"event":"device","device":"gpu","name":"NVIDIA GeForce RTX 3050"}"""));
        Assert.Equal(new DeviceProgress("cpu", null), SubSyncProgress.Parse("""PROGRESS {"event":"device","device":"CPU","name":null}"""));
        Assert.Equal(new ItemDoneProgress("synced", "D:\\a.srt"), SubSyncProgress.Parse("""PROGRESS {"event":"item_done","item":1,"total":1,"status":"synced","output":"D:\\a.srt"}"""));
    }

    [Theory]
    [InlineData("""PROGRESS {"event":"stage","stage":"listening""")]
    [InlineData("""PROGRESS not json""")]
    [InlineData("""PROGRESS []""")]
    [InlineData("""PROGRESS "text" """)]
    [InlineData("""PROGRESS {"event":"telemetry","x":1}""")]
    [InlineData("""PROGRESS {"event":"device","device":"tpu"}""")]
    [InlineData("""PROGRESS {"stage":"listening"}""")]
    [InlineData("""progress {"event":"stage","stage":"listening"}""")]
    [InlineData("""Speech recognition: GPU (NVIDIA GeForce RTX 3050)""")]
    [InlineData("""OK   [1/1] Show.mkv -> Show.en.srt""")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_ignored(string? line)
    {
        Assert.Null(SubSyncProgress.Parse(line));
    }
}

public sealed class SubSyncReport13Tests
{
    [Fact]
    public void A_1_3_0_report_gives_speech_recognition_and_timings()
    {
        var report = SubSyncReport.Parse(Recorded.Report13(@"D:\Shows\Show.S01E01.mkv", @"D:\Shows\Show.S01E01.en.hi.srt"))!;

        Assert.Equal("synced", report.Status);
        Assert.Equal("1.3.0", report.SubSyncVersion);
        Assert.Equal(72.9, report.Seconds);
        Assert.Equal(new SubSyncSpeech("gpu", "NVIDIA GeForce RTX 3050", 6.2), report.Speech);
        Assert.Equal(new SubSyncTimings(false, 9.8, 58.1, 4.2), report.Timings);
    }

    [Fact]
    public void A_cached_1_3_0_report_says_so_and_has_no_speech_recognition()
    {
        var report = SubSyncReport.Parse(Recorded.Report13("v.mkv", "v.en.srt", fromCache: true, speech: null))!;

        Assert.Null(report.Speech);
        Assert.Equal(new SubSyncTimings(true, null, null, 4.2), report.Timings);
    }

    [Fact]
    public void A_1_3_0_no_match_has_no_timings()
    {
        var report = SubSyncReport.Parse(Recorded.Report13("v.mkv", "v.en.srt", status: "no_match"))!;

        Assert.Equal("no_match", report.Status);
        Assert.Null(report.Timings);
        Assert.Equal("gpu", report.Speech!.Device);
    }

    [Fact]
    public void A_1_2_0_report_has_neither()
    {
        var report = SubSyncReport.Parse(Recorded.Report12)!;

        Assert.Equal("synced", report.Status);
        Assert.Equal("1.2.0", report.SubSyncVersion);
        Assert.Equal(95.8, report.Seconds);
        Assert.Null(report.Speech);
        Assert.Null(report.Timings);
    }

    [Theory]
    [InlineData("""{"speech_recognition":"gpu","items":[{"status":"synced","timings":[1,2]}]}""")]
    [InlineData("""{"speech_recognition":{"device":"npu"},"items":[{"status":"synced","timings":"fast"}]}""")]
    [InlineData("""{"speech_recognition":{},"items":[{"status":"synced","timings":null}]}""")]
    public void Malformed_new_fields_read_as_absent(string json)
    {
        var report = SubSyncReport.Parse(json)!;

        Assert.Equal("synced", report.Status);
        Assert.Null(report.Speech);
        Assert.Null(report.Timings);
    }
}

public sealed class CliArguments13Tests
{
    private const string Video = @"D:\TV Shows\Show (2016)\Season 1\Show S01E01 é.mkv";

    [Theory]
    [InlineData("en")]
    [InlineData("eng")]
    [InlineData("en.hi")]
    [InlineData("eng.hi")]
    public void From_1_3_0_every_tag_is_written_in_place_with_progress(string tag)
    {
        var sub = @"D:\TV Shows\Show (2016)\Season 1\Show S01E01 é." + tag + ".srt";

        var args = CliWorker.BuildArguments(Video, sub, tag, @"C:\Temp\r.json", new WorkerOptions(true, false, TimeSpan.FromMinutes(30)), inPlace: true);

        Assert.Equal(new[] { "sync", "--pair", Video, sub, "--output", sub, "--overwrite", "--report", @"C:\Temp\r.json", "--progress" }, args);
    }

    [Fact]
    public void From_1_3_0_settings_still_apply()
    {
        var args = CliWorker.BuildArguments(Video, "s.en.srt", "en", "r.json", new WorkerOptions(false, true, TimeSpan.FromMinutes(30)), inPlace: true);

        Assert.Contains("--keep-layout", args);
        Assert.Contains("--cpu", args);
        Assert.DoesNotContain("--suffix", args);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("eng", "eng")]
    [InlineData("en.hi", "en")]
    [InlineData("eng.hi", "eng")]
    public void For_1_2_0_the_call_is_unchanged(string tag, string suffix)
    {
        var sub = "s." + tag + ".srt";

        var args = CliWorker.BuildArguments(Video, sub, tag, "r.json", new WorkerOptions(true, false, TimeSpan.FromMinutes(30)));

        Assert.Equal(new[] { "sync", "--pair", Video, sub, "--overwrite", "--suffix", suffix, "--report", "r.json" }, args);
    }
}

/// <summary><see cref="CliWorker"/> with recorded SubSync output, on any OS.</summary>
[Collection(RunTimeEstimateTests.Collection)]
public sealed class CliWorkerPlaybackTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ScriptedCli _cli = new();
    private readonly string _video;

    public CliWorkerPlaybackTests()
    {
        SpeechRecognition.Reset();
        _dir.File(SubSyncProbe.CliFileName, "exe");
        _video = _dir.File("Show S01E01.mkv", "video");
    }

    public void Dispose()
    {
        SpeechRecognition.Reset();
        _dir.Dispose();
    }

    private static WorkerOptions Options(List<WorkerProgress>? seen = null) =>
        new(true, false, TimeSpan.FromMinutes(30), seen is null ? null : seen.Add);

    private CliWorker Worker(string? version) =>
        new(() => _dir.Path, NullLogger<CliWorker>.Instance, (_, _) => Task.FromResult(version is null ? null : Version.Parse(version)), _cli.RunAsync);

    [Fact]
    public async Task On_1_3_0_a_hi_file_syncs_in_place_even_with_a_plain_file_next_to_it()
    {
        var hi = _dir.File("Show S01E01.en.hi.srt", "original hi");
        var plain = _dir.File("Show S01E01.en.srt", "someone else's subtitle");
        _cli.Lines = Recorded.GpuRun(hi);
        _cli.Write = args => File.WriteAllText(args[args.ToList().IndexOf("--output") + 1], Recorded.Synced);
        _cli.Report = _ => Recorded.Report13(_video, hi);
        var seen = new List<WorkerProgress>();

        var result = await Worker("1.3.0").RunAsync(_video, hi, "en.hi", Options(seen), CancellationToken.None);

        Assert.Equal(WorkerStatus.Synced, result.Status);
        Assert.Equal(hi, result.OutputPath);
        Assert.Equal(Recorded.Synced, File.ReadAllText(hi));
        Assert.Equal("someone else's subtitle", File.ReadAllText(plain));
        var args = Assert.Single(_cli.Calls);
        Assert.Equal(hi, args[args.ToList().IndexOf("--output") + 1]);
        Assert.Contains("--progress", args);
        Assert.Equal("1.3.0", result.SubSyncVersion);
        Assert.Equal(new SubSyncTimings(false, 9.8, 58.1, 4.2), result.Timings);
        Assert.Equal(80, result.ProcessSeconds);
        Assert.Equal(6.2, result.ModelLoadSeconds);
        Assert.Equal("GPU (NVIDIA GeForce RTX 3050)", SpeechRecognition.LastLine);
        Assert.Equal(
            [
                new(SubSyncStage.Queued, null), new(SubSyncStage.ReadingAudio, null), new(SubSyncStage.Listening, null),
                new(SubSyncStage.Listening, 37), new(SubSyncStage.Listening, 100), new(SubSyncStage.Matching, null), new(SubSyncStage.Matching, 50),
            ],
            seen);
    }

    [Fact]
    public async Task On_1_2_0_a_hi_file_with_a_plain_file_is_still_refused()
    {
        var hi = _dir.File("Show S01E01.en.hi.srt", "original hi");
        var plain = _dir.File("Show S01E01.en.srt", "someone else's subtitle");

        var result = await Worker("1.2.0").RunAsync(_video, hi, "en.hi", Options(), CancellationToken.None);

        Assert.Equal(WorkerStatus.Failed, result.Status);
        Assert.Empty(_cli.Calls);
        Assert.Equal("someone else's subtitle", File.ReadAllText(plain));
        Assert.Equal("original hi", File.ReadAllText(hi));
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData(null)]
    public async Task On_1_2_0_or_an_unknown_version_a_hi_result_is_moved_over_the_hi_file(string? version)
    {
        var hi = _dir.File("Show S01E01.en.hi.srt", "original hi");
        var plain = Path.Combine(_dir.Path, "Show S01E01.en.srt");
        _cli.Lines = ["Speech recognition: CPU (no usable NVIDIA GPU – slower)", "OK   [1/1] Show S01E01.mkv -> Show S01E01.en.srt"];
        _cli.Write = args =>
        {
            Assert.DoesNotContain("--output", args);
            Assert.DoesNotContain("--progress", args);
            File.WriteAllText(plain, Recorded.Synced);
        };
        _cli.Report = _ => Recorded.Report12;

        var result = await Worker(version).RunAsync(_video, hi, "en.hi", Options(), CancellationToken.None);

        Assert.Equal(WorkerStatus.Synced, result.Status);
        Assert.Equal(Recorded.Synced, File.ReadAllText(hi));
        Assert.False(File.Exists(plain));
        Assert.Null(result.Timings);
        Assert.Equal("CPU", SpeechRecognition.LastMode);
    }

    [Fact]
    public async Task A_GPU_problem_mid_run_shows_CPU_live_and_after_the_run()
    {
        var sub = _dir.File("Show S01E01.en.srt", "original");
        var modeDuringListening = new List<string?>();
        _cli.Lines =
        [
            Recorded.P("""{"event":"stage","item":1,"total":1,"stage":"reading_audio","percent":null}"""),
            Recorded.P("""{"event":"device","device":"gpu","name":"NVIDIA GeForce RTX 3050"}"""),
            "Speech recognition: GPU (NVIDIA GeForce RTX 3050)",
            Recorded.P("""{"event":"stage","item":1,"total":1,"stage":"listening","percent":12}"""),
            "Warning: The GPU stopped working (CUDA out of memory); continuing on the CPU",
            Recorded.P("""{"event":"device","device":"cpu","name":null}"""),
            "Speech recognition: CPU (no usable NVIDIA GPU – slower)",
            Recorded.P("""{"event":"stage","item":1,"total":1,"stage":"listening","percent":40}"""),
            Recorded.P("""{"event":"stage","item":1,"total":1,"stage":"matching","percent":null}"""),
        ];
        _cli.Write = _ => File.WriteAllText(sub, Recorded.Synced);

        // No speech_recognition in this report: the last "Speech recognition:" line is the fallback, and it must win over the first.
        _cli.Report = _ => Recorded.Report13(_video, sub, speech: null);
        var worker = Worker("1.3.0");
        var options = new WorkerOptions(true, false, TimeSpan.FromMinutes(30), p =>
        {
            if (p.Stage == SubSyncStage.Listening)
            {
                modeDuringListening.Add(SpeechRecognition.LastMode);
            }
        });

        var result = await worker.RunAsync(_video, sub, "en", options, CancellationToken.None);

        Assert.Equal(WorkerStatus.Synced, result.Status);
        Assert.Equal(["GPU", "CPU"], modeDuringListening);
        Assert.Equal("CPU", SpeechRecognition.LastMode);
        Assert.Equal("CPU (no usable NVIDIA GPU – slower)", SpeechRecognition.LastLine);
    }

    [Fact]
    public async Task The_reports_device_wins_after_the_run()
    {
        var sub = _dir.File("Show S01E01.en.srt", "original");
        _cli.Lines = [Recorded.P("""{"event":"device","device":"gpu","name":"NVIDIA GeForce RTX 3050"}""")];
        _cli.Report = _ => Recorded.Report13(_video, sub, speech: """{ "device": "cpu", "name": null, "model_load_seconds": 3.1 }""");

        var result = await Worker("1.3.0").RunAsync(_video, sub, "en", Options(), CancellationToken.None);

        Assert.Equal("CPU", SpeechRecognition.LastLine);
        Assert.Equal(3.1, result.ModelLoadSeconds);
    }

    [Fact]
    public async Task A_cached_run_goes_from_queued_to_matching_and_says_it_was_cached()
    {
        var sub = _dir.File("Show S01E01.en.srt", "original");
        SpeechRecognition.ObserveDevice("gpu", "NVIDIA GeForce RTX 3050");
        _cli.Lines =
        [
            Recorded.P("""{"event":"stage","item":1,"total":1,"stage":"queued","percent":null}"""),
            Recorded.P("""{"event":"stage","item":1,"total":1,"stage":"matching","percent":null}"""),
            Recorded.P("""{"event":"item_done","item":1,"total":1,"status":"synced","output":"x"}"""),
        ];
        _cli.Report = _ => Recorded.Report13(_video, sub, fromCache: true, speech: null);
        _cli.Elapsed = TimeSpan.FromSeconds(6);
        var seen = new List<WorkerProgress>();

        var result = await Worker("1.3.0").RunAsync(_video, sub, "en", Options(seen), CancellationToken.None);

        Assert.Equal([new(SubSyncStage.Queued, null), new(SubSyncStage.Matching, null)], seen);
        Assert.True(result.Timings!.FromCache);
        Assert.Equal("GPU (NVIDIA GeForce RTX 3050)", SpeechRecognition.LastLine);
    }

    [Fact]
    public async Task A_cached_no_match_has_no_timings_but_still_counts_as_cached()
    {
        var sub = _dir.File("Show S01E01.en.srt", "original");
        _cli.ExitCode = 4;
        _cli.Lines =
        [
            Recorded.P("""{"event":"stage","item":1,"total":1,"stage":"queued","percent":null}"""),
            Recorded.P("""{"event":"stage","item":1,"total":1,"stage":"matching","percent":null}"""),
        ];
        _cli.Report = _ => Recorded.Report13(_video, sub, status: "no_match", speech: null);

        var result = await Worker("1.3.0").RunAsync(_video, sub, "en", Options(), CancellationToken.None);

        Assert.Equal(WorkerStatus.NoMatch, result.Status);
        Assert.Equal(new SubSyncTimings(true, null, null, null), result.Timings);
    }

    [Fact]
    public async Task A_no_match_that_listened_is_not_marked_cached()
    {
        var sub = _dir.File("Show S01E01.en.srt", "original");
        _cli.ExitCode = 4;
        _cli.Lines = [Recorded.P("""{"event":"stage","item":1,"total":1,"stage":"listening","percent":50}""")];
        _cli.Report = _ => Recorded.Report13(_video, sub, status: "no_match");

        var result = await Worker("1.3.0").RunAsync(_video, sub, "en", Options(), CancellationToken.None);

        Assert.Equal(WorkerStatus.NoMatch, result.Status);
        Assert.Null(result.Timings);
    }

    [Fact]
    public async Task Bad_and_unknown_lines_never_fail_a_run()
    {
        var sub = _dir.File("Show S01E01.en.srt", "original");
        _cli.Lines =
        [
            "Repairing SubSync: 2 file(s)…",
            "PROGRESS {broken",
            "PROGRESS {\"event\":\"stage\",\"stage\":{\"nested\":true},\"percent\":[1]}",
            Recorded.P("""{"event":"future_event","data":{"x":1}}"""),
            Recorded.P("""{"event":"stage","item":1,"total":1,"stage":"rethinking","percent":5,"new_field":"x"}"""),
            "PROGRESS ",
            "\u0000\uFFFD",
        ];
        _cli.Write = _ => File.WriteAllText(sub, Recorded.Synced);
        _cli.Report = _ => Recorded.Report13(_video, sub);
        var seen = new List<WorkerProgress>();

        var result = await Worker("1.3.0").RunAsync(_video, sub, "en", Options(seen), CancellationToken.None);

        Assert.Equal(WorkerStatus.Synced, result.Status);
        Assert.Equal([new(SubSyncStage.Working, null), new(SubSyncStage.Working, 5)], seen);
    }
}

[Collection(RunTimeEstimateTests.Collection)]
public sealed class RunTimeEstimate13Tests : IDisposable
{
    private static readonly TimeSpan FortyFive = TimeSpan.FromMinutes(45);

    public RunTimeEstimate13Tests()
    {
        RunTimeEstimate.Reset();
        SpeechRecognition.Reset();
    }

    public void Dispose()
    {
        RunTimeEstimate.Reset();
        SpeechRecognition.Reset();
    }

    private static WorkerResult Run(WorkerStatus status, double seconds, SubSyncTimings? timings = null, double? wall = null, double? load = null) =>
        new(status, null, null, "en", seconds, timings is null ? "1.2.0" : "1.3.0", 0, timings, wall, load);

    [Fact]
    public void A_cached_1_3_0_run_teaches_nothing()
    {
        var before = RunTimeEstimate.Expect(FortyFive);

        RunTimeEstimate.Record(FortyFive, Run(WorkerStatus.Synced, 4, new SubSyncTimings(true, null, null, 4), wall: 9));
        RunTimeEstimate.Record(FortyFive, Run(WorkerStatus.NoMatch, 15, new SubSyncTimings(true, null, null, null), wall: 15));

        Assert.Equal(before, RunTimeEstimate.Expect(FortyFive));
    }

    [Theory]
    [InlineData(3.4)]
    [InlineData(9.9)]
    [InlineData(26.9)] // under 1% of 45 minutes (27 s)
    public void An_implausibly_fast_1_2_0_run_teaches_nothing(double seconds)
    {
        var before = RunTimeEstimate.Expect(FortyFive);

        RunTimeEstimate.Record(FortyFive, Run(WorkerStatus.NoMatch, seconds));

        Assert.Equal(before, RunTimeEstimate.Expect(FortyFive));
    }

    [Fact]
    public void Ten_seconds_is_the_floor_for_short_videos()
    {
        var two = TimeSpan.FromMinutes(2);
        var before = RunTimeEstimate.Expect(two);

        RunTimeEstimate.Record(two, Run(WorkerStatus.Synced, 9));
        Assert.Equal(before, RunTimeEstimate.Expect(two));

        RunTimeEstimate.Record(two, Run(WorkerStatus.Synced, 30));
        Assert.NotEqual(before, RunTimeEstimate.Expect(two));
    }

    [Fact]
    public void A_retry_of_a_no_match_does_not_throw_off_the_estimate()
    {
        // A real GPU run on 1.2.0, then the same episode again from the failed-syncs log (cached: 4 s).
        RunTimeEstimate.Record(FortyFive, Run(WorkerStatus.Synced, 96));
        var learned = RunTimeEstimate.Expect(FortyFive);

        RunTimeEstimate.Record(FortyFive, Run(WorkerStatus.NoMatch, 4));

        Assert.Equal(learned, RunTimeEstimate.Expect(FortyFive));
        Assert.Equal(96, learned!.Total, 0);
    }

    [Fact]
    public void A_1_3_0_run_teaches_start_up_reading_listening_and_matching()
    {
        // README example: read 9.8 s, listen 58.1 s, match 4.2 s; the whole process took 80 s, so start-up is 7.9 s.
        RunTimeEstimate.Record(FortyFive, Run(WorkerStatus.Synced, 72.9, new SubSyncTimings(false, 9.8, 58.1, 4.2), wall: 80, load: 6.2));

        var e = RunTimeEstimate.Expect(FortyFive)!;
        Assert.Equal(80, e.Total, 0);
        Assert.Equal(7.9 + 9.8, e.ListenStart, 1);
        Assert.Equal(7.9 + 9.8 + 58.1, e.ListenEnd, 1);

        // A 90-minute movie scales reading and listening, not start-up or matching.
        var movie = RunTimeEstimate.Expect(TimeSpan.FromMinutes(90))!;
        Assert.Equal(7.9 + (2 * 9.8) + (2 * 58.1) + 4.2, movie.Total, 0);
    }

    [Fact]
    public void Without_the_wall_time_model_load_seconds_stand_in_for_start_up()
    {
        RunTimeEstimate.Record(FortyFive, Run(WorkerStatus.Synced, 72.9, new SubSyncTimings(false, 9.8, 58.1, 4.2), wall: null, load: 6.2));

        Assert.Equal(6.2 + 9.8 + 58.1 + 4.2, RunTimeEstimate.Expect(FortyFive)!.Total, 0);
    }

    [Fact]
    public void Start_up_never_goes_below_zero()
    {
        RunTimeEstimate.Record(FortyFive, Run(WorkerStatus.Synced, 72.9, new SubSyncTimings(false, 9.8, 58.1, 4.2), wall: 50));

        Assert.Equal(9.8, RunTimeEstimate.Expect(FortyFive)!.ListenStart, 1);
    }

    [Fact]
    public void Listening_percent_fills_listenings_share_of_the_bar()
    {
        var e = new RunEstimate(100, 20, 90);

        Assert.Equal(20, RunTimeEstimate.Percent(e, SubSyncStage.Listening, 0));
        Assert.Equal(55, RunTimeEstimate.Percent(e, SubSyncStage.Listening, 50));
        Assert.Equal(90, RunTimeEstimate.Percent(e, SubSyncStage.Listening, 100));
        Assert.Null(RunTimeEstimate.Percent(e, SubSyncStage.Listening, null));
        Assert.Null(RunTimeEstimate.Percent(e, SubSyncStage.Matching, 50));
        Assert.Null(RunTimeEstimate.Percent(e, SubSyncStage.ReadingAudio, null));
        Assert.Null(RunTimeEstimate.Percent(null, SubSyncStage.Listening, 50));

        // Never past 95% before the item ends.
        Assert.Equal(95, RunTimeEstimate.Percent(new RunEstimate(100, 50, 100), SubSyncStage.Listening, 100));
    }

    [Fact]
    public void Before_anything_is_learned_listening_is_most_of_the_run()
    {
        var e = RunTimeEstimate.Expect(FortyFive)!; // CPU guess: 15 s + 0.33 x 2700 s

        Assert.Equal(15 + 891, e.Total, 0);
        Assert.Equal(15 + (891 * 0.15), e.ListenStart, 0);
        Assert.Equal(e.Total, e.ListenEnd, 0);
    }
}

public sealed class SubSyncStage13Tests
{
    // New in SubSync 1.3.0's logs\cli.log, followed by LogTail for 1.2.0; none of them is a stage.
    [Theory]
    [InlineData("2026-11-12 20:15:09 speech model ready after 7.1 s (worker 6.2 s)")]
    [InlineData("2026-11-12 20:16:21 [1/1] Show.S01E01.mkv: read audio 9.8 s, listen 58.1 s, match 4.2 s, total 72.9 s")]
    [InlineData("2026-11-12 20:16:21 [1/1] Show: Listening….mkv: read audio 9.8 s, listen 58.1 s, match 4.2 s, total 72.9 s")]
    [InlineData("2026-11-12 20:16:21 [1/1] Show.S01E01.mkv: transcript from cache, match 4.2 s, total 5.1 s")]
    [InlineData("2026-11-12 20:15:02 serve: ready (suffix en, overwrite True, tidy True, cpu False)")]
    [InlineData("2026-11-12 20:15:40 serve: [#1] Show.S01E01.mkv: Listening…")]
    [InlineData("PROGRESS {\"event\":\"stage\",\"item\":1,\"total\":1,\"stage\":\"listening\",\"percent\":37}")]
    public void New_log_lines_are_not_stages(string line)
    {
        Assert.Null(SubSyncStage.Parse(line));
    }

    [Theory]
    [InlineData("2026-11-12 20:15:10 [1/1] Show.S01E01.mkv: Reading audio…", SubSyncStage.ReadingAudio)]
    [InlineData("2026-11-12 20:15:20 [1/1] Show.S01E01.mkv: Listening…", SubSyncStage.Listening)]
    [InlineData("2026-11-12 20:16:17 [1/1] Show.S01E01.mkv: Matching lines…", SubSyncStage.Matching)]
    public void Stage_lines_are_unchanged_in_1_3_0(string line, string stage)
    {
        Assert.Equal(stage, SubSyncStage.Parse(line));
    }
}

public sealed class JobQueuePercentTests
{
    [Fact]
    public async Task Percent_follows_each_stage_and_clears_with_it()
    {
        using var queue = new JobQueue(TimeProvider.System);
        queue.Enqueue(Guid.NewGuid(), [new JobRequestItem(Guid.NewGuid(), "E1", "Show")], JobSource.Manual, null, 300);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = await queue.TakeAsync(timeout.Token);
        var id = job.Snapshot.JobId;

        queue.SetProgress(id, null, 80);
        queue.SetProgress(id, SubSyncStage.Listening, null, 40);
        Assert.Equal(40, queue.Snapshot().Current!.Percent);

        queue.SetProgress(id, SubSyncStage.Listening, null, 61);
        Assert.Equal(61, queue.Snapshot().Current!.Percent);

        // An estimate update keeps the percent; a new stage without one clears it.
        queue.SetProgress(id, null, 90);
        Assert.Equal(61, queue.Snapshot().Current!.Percent);
        queue.SetProgress(id, SubSyncStage.Matching, null);
        Assert.Null(queue.Snapshot().Current!.Percent);
        Assert.Equal(90, queue.Snapshot().Current!.ExpectedSeconds);

        queue.Complete(id, JobItemStatus.Done, null, 75);
        Assert.Null(Assert.Single(queue.Snapshot().Items).Percent);
    }
}
