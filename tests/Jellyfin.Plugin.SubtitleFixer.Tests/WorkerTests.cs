using System.Diagnostics;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

public sealed class SubSyncReportTests
{
    [Theory]
    [InlineData("""{"status":"synced","reason":null,"audio_language":"eng","seconds":71.5,"subsync_version":"1.2.0"}""")]
    [InlineData("""[{"status":"synced","audio_language":"eng","seconds":71.5,"subsync_version":"1.2.0"}]""")]
    [InlineData("""{"subsync_version":"1.2.0","results":[{"status":"synced","audio_language":"eng","seconds":71.5}]}""")]
    [InlineData("""{"version":"1.2.0","pairs":[{"status":"SYNCED","audio_language":"eng","seconds":71.5}]}""")]
    public void Parses_the_layouts_we_might_get(string json)
    {
        var report = SubSyncReport.Parse(json);

        Assert.NotNull(report);
        Assert.Equal("synced", report!.Status);
        Assert.Equal("eng", report.AudioLanguage);
        Assert.Equal(71.5, report.Seconds);
        Assert.Equal("1.2.0", report.SubSyncVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"results":[]}""")]
    public void No_status_means_no_report(string? json)
    {
        Assert.Null(SubSyncReport.Parse(json));
    }

    [Theory]
    [InlineData(0, "synced", WorkerStatus.Synced)]
    [InlineData(3, "synced", WorkerStatus.Failed)]
    [InlineData(4, "no_match", WorkerStatus.NoMatch)]
    [InlineData(4, "failed", WorkerStatus.Failed)]
    [InlineData(5, "skipped", WorkerStatus.Failed)]
    [InlineData(9, "cancelled", WorkerStatus.Cancelled)]
    [InlineData(7, "synced", WorkerStatus.Unavailable)]
    [InlineData(7, null, WorkerStatus.Unavailable)]
    [InlineData(10, "failed", WorkerStatus.Crashed)]
    [InlineData(6, null, WorkerStatus.Crashed)]
    [InlineData(0, null, WorkerStatus.Crashed)]
    [InlineData(0, "weird", WorkerStatus.Failed)]
    public void Exit_code_and_report_map_to_an_outcome(int exit, string? status, WorkerStatus expected)
    {
        var report = status is null ? null : new SubSyncReport(status, "r", "eng", 1, "1.0");

        Assert.Equal(expected, SubSyncOutcome.Decide(exit, report).Status);
    }

    [Fact]
    public void Arguments_follow_the_cli_contract_and_settings()
    {
        var video = @"D:\TV Shows\Show (2016)\Season 1\Show S01E01 é.mkv";
        var sub = @"D:\TV Shows\Show (2016)\Season 1\Show S01E01 é.en.srt";

        var plain = CliWorker.BuildArguments(video, sub, "en", @"C:\Temp\r.json", new WorkerOptions(true, false, TimeSpan.FromMinutes(30)));
        Assert.Equal(new[] { "sync", "--pair", video, sub, "--overwrite", "--suffix", "en", "--report", @"C:\Temp\r.json" }, plain);

        var tuned = CliWorker.BuildArguments(video, sub, "eng", "r.json", new WorkerOptions(false, true, TimeSpan.FromMinutes(30)));
        Assert.Contains("--keep-layout", tuned);
        Assert.Contains("--cpu", tuned);
        Assert.Equal("eng", tuned[tuned.ToList().IndexOf("--suffix") + 1]);

        // SubSync rejects "en.hi" (S5), so HI runs with the plain tag and the result is moved afterwards.
        var hi = CliWorker.BuildArguments(video, @"D:\x.en.hi.srt", "en.hi", "r.json", new WorkerOptions(true, false, TimeSpan.FromMinutes(30)));
        Assert.Equal("en", hi[hi.ToList().IndexOf("--suffix") + 1]);
        Assert.Equal("eng", CliWorker.CliSuffix("eng.hi"));
        Assert.Equal(@"D:\Show\Ep 1.en.srt", CliWorker.CliOutputPath(@"D:\Show\Ep 1.mkv", "en.hi"));
        Assert.Equal(@"D:\Show\Ep 1.eng.srt", CliWorker.CliOutputPath(@"D:\Show\Ep 1.mkv", "eng"));
    }

    [Fact]
    public async Task Unconfigured_worker_reports_unavailable()
    {
        var worker = new CliWorker(() => null, Microsoft.Extensions.Logging.Abstractions.NullLogger<CliWorker>.Instance);
        Assert.False(worker.IsConfigured);
        var result = await worker.RunAsync("v.mkv", "v.en.srt", "en", new WorkerOptions(true, false, TimeSpan.FromMinutes(1)), CancellationToken.None);
        Assert.Equal(WorkerStatus.Unavailable, result.Status);
    }
}

public sealed class SubtitleVerifierTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n", true)]
    [InlineData("\uFEFF1\n0:00:01.5 --> 0:00:02.25\nHello\n", true)]
    [InlineData("", false)]
    [InlineData("Hello world", false)]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\n\n", false)]
    public void Detects_valid_srt(string content, bool valid)
    {
        var path = _dir.File("x.en.srt", content);
        Assert.Equal(valid, SubtitleVerifier.IsValidSrt(path, out _));
    }

    [Fact]
    public void Missing_file_is_invalid()
    {
        Assert.False(SubtitleVerifier.IsValidSrt(Path.Combine(_dir.Path, "none.srt"), out var reason));
        Assert.Contains("missing", reason, StringComparison.Ordinal);
    }
}

/// <summary>Real processes: kill tree and timeout (TESTING.md: "Cancel mid-run: process tree gone"). Windows only.</summary>
public sealed class ProcessRunnerTests
{
    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task Exit_code_and_output_are_returned()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var outcome = await ProcessRunner.RunAsync(Cmd, ["/c", "echo hello & exit 4"], Path.GetTempPath(), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(ProcessEnd.Exited, outcome.End);
        Assert.Equal(4, outcome.ExitCode);
        Assert.Contains("hello", outcome.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_kills_the_whole_tree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // cmd.exe starts ping.exe as a child; both must be gone after cancel.
        var marker = Guid.NewGuid().ToString("N")[..8];
        using var cancel = new CancellationTokenSource();
        var run = ProcessRunner.RunAsync(Cmd, ["/c", $"title {marker} & ping -n 60 127.0.0.1"], Path.GetTempPath(), TimeSpan.FromMinutes(2), cancel.Token);

        var child = await WaitForChildPingAsync();
        Assert.NotNull(child);

        await cancel.CancelAsync();
        var outcome = await run;

        Assert.Equal(ProcessEnd.Cancelled, outcome.End);
        Assert.True(child!.WaitForExit(10_000), "child ping.exe must be killed with its parent");
    }

    [Fact]
    public async Task Timeout_kills_and_reports_timed_out()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        var outcome = await ProcessRunner.RunAsync(Cmd, ["/c", "ping -n 60 127.0.0.1"], Path.GetTempPath(), TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(ProcessEnd.TimedOut, outcome.End);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Closing_the_job_kills_the_process_and_its_children()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var parent = Process.Start(new ProcessStartInfo(Cmd, "/c ping -n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        var job = KillOnCloseJob.TryCreate();
        Assert.NotNull(job);
        Assert.True(job!.TryAdd(parent));
        var child = await WaitForChildPingAsync();
        Assert.NotNull(child);

        job.Dispose(); // What the OS does when Jellyfin's process ends.

        Assert.True(parent.WaitForExit(10_000), "cmd.exe must be killed");
        Assert.True(child!.WaitForExit(10_000), "its ping.exe child must be killed too");
    }

    [Fact]
    public void Leftover_worker_is_killed_once_and_reused_ids_are_left_alone()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var leftover = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        RunningWorkerFile.Write(leftover);

        Assert.True(RunningWorkerFile.KillLeftover());
        Assert.True(leftover.WaitForExit(10_000));
        Assert.False(File.Exists(RunningWorkerFile.PathName));
        Assert.False(RunningWorkerFile.KillLeftover());

        // Same id, different start time = not our process.
        using var other = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        File.WriteAllText(RunningWorkerFile.PathName, other.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|1");
        Assert.False(RunningWorkerFile.KillLeftover());
        Assert.False(other.HasExited);
        other.Kill();
    }

    [Fact]
    public async Task Missing_executable_reports_start_failed()
    {
        var outcome = await ProcessRunner.RunAsync(Path.Combine(Path.GetTempPath(), "no-such-tool-" + Guid.NewGuid().ToString("N") + ".exe"), [], Path.GetTempPath(), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(ProcessEnd.StartFailed, outcome.End);
    }

    private static async Task<Process?> WaitForChildPingAsync()
    {
        var before = DateTime.Now.AddSeconds(-2);
        for (var i = 0; i < 50; i++)
        {
            var ping = Process.GetProcessesByName("PING").FirstOrDefault(p =>
            {
                try
                {
                    return p.StartTime >= before;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return false;
                }
            });
            if (ping is not null)
            {
                return ping;
            }

            await Task.Delay(100);
        }

        return null;
    }
}

public sealed class SpeechRecognitionTests
{
    [Theory]
    [InlineData("Loading\nSpeech recognition: GPU (NVIDIA RTX 3080)\nDone", "GPU")]
    [InlineData("Speech recognition: CPU (no usable NVIDIA GPU – slower)\r\n", "CPU")]
    [InlineData("Installation is complete and intact.", null)]
    [InlineData("", null)]
    public void ModeOf_reads_the_speech_recognition_line(string output, string? expected)
    {
        Assert.Equal(expected, SpeechRecognition.ModeOf(output));
    }

    [Fact]
    public void Observe_keeps_the_last_line_and_ignores_output_without_one()
    {
        SpeechRecognition.Observe("x\nSpeech recognition: CPU (no usable NVIDIA GPU – slower)\ny");
        SpeechRecognition.Observe("nothing here");
        Assert.Equal("CPU (no usable NVIDIA GPU – slower)", SpeechRecognition.LastLine);
        Assert.Equal("CPU", SpeechRecognition.LastMode);
    }
}
