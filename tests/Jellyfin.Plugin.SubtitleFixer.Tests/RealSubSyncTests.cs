using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

/// <summary>
/// Runs the real <c>subsync-cli.exe</c> through <see cref="CliWorker"/>. Skipped (passes without doing anything)
/// unless both environment variables are set, so CI and machines without SubSync are unaffected:
/// <c>SUBFIXER_SUBSYNC_FOLDER</c> (folder with subsync-cli.exe) and <c>SUBFIXER_TEST_CLIP</c> (a short English video
/// with a matching <c>&lt;video&gt;.en.srt</c> next to it). Each test works on copies in a temp folder.
/// </summary>
public sealed class RealSubSyncTests : IDisposable
{
    private static readonly string? Folder = Environment.GetEnvironmentVariable("SUBFIXER_SUBSYNC_FOLDER");
    private static readonly string? Clip = Environment.GetEnvironmentVariable("SUBFIXER_TEST_CLIP");
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static bool Enabled => !string.IsNullOrEmpty(Folder) && !string.IsNullOrEmpty(Clip) && File.Exists(Clip);

    private static WorkerOptions Options => new(true, false, TimeSpan.FromMinutes(20));

    private CliWorker Worker() => new(() => Folder, NullLogger<CliWorker>.Instance);

    private static async Task<Version?> InstalledAsync() =>
        await SubSyncVersion.Shared.GetAsync(Path.Combine(Folder!, SubSyncProbe.CliFileName), CancellationToken.None);

    private (string Video, string Subtitle) Copy(string subtitleSuffix)
    {
        var video = Path.Combine(_dir.Path, "Clip é.mp4");
        File.Copy(Clip!, video);
        var subtitle = Path.Combine(_dir.Path, "Clip é" + subtitleSuffix);
        File.Copy(Path.ChangeExtension(Clip!, null) + ".en.srt", subtitle);
        return (video, subtitle);
    }

    [Fact]
    public async Task Synced_in_place()
    {
        if (!Enabled)
        {
            return;
        }

        var (video, subtitle) = Copy(".en.srt");
        var before = File.ReadAllText(subtitle);

        var result = await Worker().RunAsync(video, subtitle, "en", Options, CancellationToken.None);

        Assert.Equal(WorkerStatus.Synced, result.Status);
        Assert.Equal((await InstalledAsync())?.ToString(3), result.SubSyncVersion);
        Assert.Equal("en", result.AudioLanguage);
        Assert.NotEqual(before, File.ReadAllText(subtitle));
        Assert.True(SubtitleVerifier.IsValidSrt(subtitle, out _));
    }

    [Fact]
    public async Task Hearing_impaired_result_replaces_the_hi_file_and_leaves_no_plain_file()
    {
        if (!Enabled)
        {
            return;
        }

        var (video, hiSubtitle) = Copy(".en.hi.srt");
        var before = File.ReadAllText(hiSubtitle);

        var result = await Worker().RunAsync(video, hiSubtitle, "en.hi", Options, CancellationToken.None);

        Assert.Equal(WorkerStatus.Synced, result.Status);
        Assert.NotEqual(before, File.ReadAllText(hiSubtitle));
        Assert.True(SubtitleVerifier.IsValidSrt(hiSubtitle, out _));
        Assert.False(File.Exists(CliWorker.CliOutputPath(video, "en.hi")), "no stray .en.srt may be left");
    }

    [Fact]
    public async Task Hearing_impaired_refuses_when_a_plain_file_exists()
    {
        // SubSync 1.2.0 only: from 1.3.0 the .hi file is re-timed in place (next test).
        if (!Enabled || SubSyncVersion.HasProgressAndOutput(await InstalledAsync()))
        {
            return;
        }

        var (video, hiSubtitle) = Copy(".en.hi.srt");
        var plain = CliWorker.CliOutputPath(video, "en.hi");
        File.WriteAllText(plain, "someone else's subtitle");

        var result = await Worker().RunAsync(video, hiSubtitle, "en.hi", Options, CancellationToken.None);

        Assert.Equal(WorkerStatus.Failed, result.Status);
        Assert.Equal("someone else's subtitle", File.ReadAllText(plain));
    }

    [Fact]
    public async Task From_1_3_0_hearing_impaired_syncs_in_place_next_to_a_plain_file()
    {
        if (!Enabled || !SubSyncVersion.HasProgressAndOutput(await InstalledAsync()))
        {
            return;
        }

        var (video, hiSubtitle) = Copy(".en.hi.srt");
        var plain = CliWorker.CliOutputPath(video, "en.hi");
        File.WriteAllText(plain, "someone else's subtitle");
        var before = File.ReadAllText(hiSubtitle);
        var stages = new List<WorkerProgress>();
        var options = Options with { OnProgress = p => { lock (stages) { stages.Add(p); } } };

        var result = await Worker().RunAsync(video, hiSubtitle, "en.hi", options, CancellationToken.None);

        Assert.Equal(WorkerStatus.Synced, result.Status);
        Assert.NotEqual(before, File.ReadAllText(hiSubtitle));
        Assert.True(SubtitleVerifier.IsValidSrt(hiSubtitle, out _));
        Assert.Equal("someone else's subtitle", File.ReadAllText(plain));
        Assert.NotNull(result.Timings);
        Assert.Contains(stages, p => p.Stage == SubSyncStage.Matching);
        Assert.NotNull(SpeechRecognition.LastMode);
    }

    [Fact]
    public async Task Wrong_subtitle_is_no_match()
    {
        if (!Enabled)
        {
            return;
        }

        var (video, subtitle) = Copy(".en.srt");
        var lines = new System.Text.StringBuilder();
        for (var i = 1; i <= 60; i++)
        {
            var start = TimeSpan.FromSeconds(10 + (i * 5));
            lines.AppendLine(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, @"{0:hh\:mm\:ss\,fff} --> {1:hh\:mm\:ss\,fff}", start, start.Add(TimeSpan.FromSeconds(2))));
            lines.AppendLine("The quarterly shipping manifest lists " + i + " crates of turnips.");
            lines.AppendLine();
        }

        File.WriteAllText(subtitle, lines.ToString());
        var result = await Worker().RunAsync(video, subtitle, "en", Options, CancellationToken.None);

        Assert.Equal(WorkerStatus.NoMatch, result.Status);
    }

    [Fact]
    public async Task Cancel_stops_the_run()
    {
        if (!Enabled)
        {
            return;
        }

        // A fresh name so the transcript cache does not make the run finish instantly.
        var video = Path.Combine(_dir.Path, "Uncached " + Guid.NewGuid().ToString("N")[..6] + ".mp4");
        File.Copy(Clip!, video);
        File.AppendAllText(video, "x"); // different size: no cache hit
        var subtitle = Path.ChangeExtension(video, null) + ".en.srt";
        File.Copy(Path.ChangeExtension(Clip!, null) + ".en.srt", subtitle);

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await Worker().RunAsync(video, subtitle, "en", Options, cancel.Token);

        Assert.Equal(WorkerStatus.Cancelled, result.Status);
        Assert.DoesNotContain(System.Diagnostics.Process.GetProcesses(), p =>
        {
            try
            {
                return p.MainModule?.FileName?.StartsWith(Folder!, StringComparison.OrdinalIgnoreCase) ?? false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        });
    }
}
