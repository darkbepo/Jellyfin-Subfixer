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
