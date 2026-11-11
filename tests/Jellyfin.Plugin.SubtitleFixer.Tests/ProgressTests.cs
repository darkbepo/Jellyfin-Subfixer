using Jellyfin.Plugin.SubtitleFixer.Bazarr;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

public sealed class SubSyncStageTests
{
    [Theory]
    [InlineData("[1/1] Schitt's Creek - S01e04 - Bad Parents.mkv: Reading audio…", SubSyncStage.ReadingAudio)]
    [InlineData("2026-11-11 23:49:38 [1/1] Schitt's Creek - S01e03 - Don't Worry, It's His Sister Webdl-1080P.mkv: Listening…", SubSyncStage.Listening)]
    [InlineData("[1/1] Show: Part 2.mkv: Listening…", SubSyncStage.Listening)]
    [InlineData("[3/10] Movie (2014).mp4: Matching lines...", SubSyncStage.Matching)]
    [InlineData("Speech recognition: CPU (no usable NVIDIA GPU – slower)", null)]
    [InlineData("OK   [1/1] Clip.mp4 -> Clip.en.srt  (58/77 lines matched)", null)]
    [InlineData("[1/1] Clip.mp4: Something new…", null)]
    [InlineData("", null)]
    public void Parse_reads_only_stage_lines(string line, string? expected)
    {
        Assert.Equal(expected, SubSyncStage.Parse(line));
    }
}

[CollectionDefinition(Collection, DisableParallelization = true)]
public sealed class RunTimeEstimateTests
{
    public const string Collection = "RunTimeEstimate";

    [Fact]
    public void Unknown_length_has_no_estimate()
    {
        Assert.Null(RunTimeEstimate.Expect(null));
        Assert.Null(RunTimeEstimate.Expect(TimeSpan.Zero));
    }
}

[Collection(RunTimeEstimateTests.Collection)]
public sealed class RunTimeEstimateLearningTests
{
    [Fact]
    public void Learns_the_speed_of_this_server()
    {
        RunTimeEstimate.Reset();
        var twentyTwo = TimeSpan.FromMinutes(22);
        var guess = RunTimeEstimate.Expect(twentyTwo)!.Value;

        // Measured on the test PC's CPU: a 22-minute episode in about 6.5 minutes.
        RunTimeEstimate.Record(twentyTwo, 390);
        Assert.Equal(390, RunTimeEstimate.Expect(twentyTwo)!.Value, 0);
        Assert.NotEqual(guess, RunTimeEstimate.Expect(twentyTwo));

        // Clips under a minute and runs without a time teach nothing.
        RunTimeEstimate.Record(TimeSpan.FromSeconds(30), 5000);
        RunTimeEstimate.Record(twentyTwo, null);
        Assert.Equal(390, RunTimeEstimate.Expect(twentyTwo)!.Value, 0);
        RunTimeEstimate.Reset();
    }
}

public sealed class JobQueueProgressTests
{
    [Fact]
    public async Task Stage_and_estimate_show_only_while_running()
    {
        using var queue = new JobQueue(TimeProvider.System);
        queue.Enqueue(Guid.NewGuid(), [new JobRequestItem(Guid.NewGuid(), "E1", "Show")], JobSource.Manual, null, 300);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = await queue.TakeAsync(timeout.Token);

        queue.SetProgress(job.Snapshot.JobId, null, 300);
        queue.SetProgress(job.Snapshot.JobId, SubSyncStage.Listening, null);
        var running = queue.Snapshot().Current!;
        Assert.Equal(SubSyncStage.Listening, running.Stage);
        Assert.Equal(300, running.ExpectedSeconds);

        queue.Complete(job.Snapshot.JobId, JobItemStatus.Done, null, 280);
        queue.SetProgress(job.Snapshot.JobId, SubSyncStage.Matching, null);
        var done = Assert.Single(queue.Snapshot().Items);
        Assert.Null(done.Stage);
        Assert.Null(done.ExpectedSeconds);
    }
}

public sealed class BazarrAddressTests
{
    private const string Page = "<html><script>window.Bazarr = JSON.parse(`{\"apiKey\": \"secret\", \"baseUrl\": \"{0}\", \"canUpdate\": true}`);</script></html>";

    [Theory]
    [InlineData("http://server:6767/bazarr", "/bazarr", "http://server:6767/bazarr/")]
    [InlineData("http://server:6767/", "", "http://server:6767/")]
    [InlineData("https://media.example/bazarr/", "/bazarr/", "https://media.example/bazarr/")]
    public void Suggests_the_address_Bazarrs_own_page_reports(string pageUri, string baseUrl, string expected)
    {
        Assert.Equal(expected, BazarrClient.SuggestAddress(new Uri(pageUri), Page.Replace("{0}", baseUrl, StringComparison.Ordinal)));
    }

    [Fact]
    public void Other_pages_give_no_suggestion()
    {
        Assert.Null(BazarrClient.SuggestAddress(new Uri("http://server:6767/"), "<html><title>Sonarr</title></html>"));
    }
}

public sealed class LogTailTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("sf-tail ").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void Hands_over_only_new_complete_lines()
    {
        var log = Path.Combine(_folder, "cli.log");
        File.WriteAllText(log, "2026-11-11 23:49:28 [1/1] old.mkv: Listening…\n");
        var lines = new List<string>();
        using var tail = new LogTail(log, lines.Add, TimeSpan.FromHours(1));

        using (var writer = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new System.Text.UTF8Encoding(false)))
        {
            writer.Write("2026-11-12 00:10:13 [1/1] Clip é.mp4: Reading audio…\r\n2026-11-12 00:10:20 [1/1] Clip é.mp4: Liste");
        }

        tail.Poll();
        Assert.Equal(["2026-11-12 00:10:13 [1/1] Clip é.mp4: Reading audio…"], lines);

        File.AppendAllText(log, "ning…\n");
        tail.Poll();
        Assert.Equal(SubSyncStage.Listening, SubSyncStage.Parse(lines[^1]));
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void A_missing_log_is_not_an_error()
    {
        var lines = new List<string>();
        using var tail = new LogTail(Path.Combine(_folder, "none", "cli.log"), lines.Add, TimeSpan.FromHours(1));
        tail.Poll();
        Assert.Empty(lines);
    }
}
