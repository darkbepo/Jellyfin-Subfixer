using System.IO.Compression;
using System.Security.Cryptography;
using Jellyfin.Plugin.SubtitleFixer.Api;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

/// <summary>A release served from memory. The "exes" are text files that say their version.</summary>
public sealed class FakeReleases : ISubSyncReleaseSource
{
    public FakeReleases(string version, params (string Path, string Content)[] entries)
    {
        Zip = MakeZip(entries.Length > 0 ? entries : [("subsync-cli.exe", "SubSync " + version), ("SubSync.exe", "SubSync " + version), ("README.md", "readme")]);
        Release = new SubSyncRelease(Version.Parse(version), "v" + version, "Faster starts.", "https://github.com/darkbepo/SubSync/releases/tag/v" + version, $"SubSync-v{version}-windows-x64.zip", new Uri("https://example.invalid/z.zip"), Sha(Zip), Zip.Length);
    }

    public byte[] Zip { get; set; }

    public SubSyncRelease? Release { get; set; }

    public Exception? CheckFails { get; set; }

    public int Checks { get; private set; }

    public int Downloads { get; private set; }

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static byte[] MakeZip(IEnumerable<(string Path, string Content)> entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }

        return memory.ToArray();
    }

    public Task<SubSyncRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        Checks++;
        return CheckFails is { } ex ? Task.FromException<SubSyncRelease?>(ex) : Task.FromResult(Release);
    }

    public async Task DownloadAsync(SubSyncRelease release, Stream destination, CancellationToken cancellationToken)
    {
        Downloads++;
        await destination.WriteAsync(Zip, cancellationToken);
    }
}

public sealed class GitHubReleaseParseTests
{
    private const string Sha = "6eee6791866d803ea01f5d1f0da92a50496a8187320cd70f8a395d7e353e3fa7";

    private static string Json(string tag = "v1.3.0", string asset = "SubSync-v1.3.0-windows-x64.zip", string? digest = "sha256:" + Sha, bool prerelease = false, bool draft = false) => $$"""
        {
          "tag_name": "{{tag}}", "name": "SubSync {{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/darkbepo/SubSync/releases/tag/{{tag}}",
          "body": "Faster starts; the results and the matching are unchanged.",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://github.com/darkbepo/SubSync/releases/download/{{tag}}/notes.txt", "size": 10 },
            { "name": "{{asset}}", "browser_download_url": "https://github.com/darkbepo/SubSync/releases/download/{{tag}}/{{asset}}", "size": 9123456{{(digest is null ? string.Empty : ", \"digest\": \"" + digest + "\"")}} }
          ]
        }
        """;

    [Fact]
    public void Reads_the_release_zip_and_its_checksum()
    {
        var r = GitHubSubSyncReleases.Parse(Json())!;

        Assert.Equal(new Version(1, 3, 0), r.Version);
        Assert.Equal("SubSync-v1.3.0-windows-x64.zip", r.AssetName);
        Assert.Equal(Sha, r.Sha256);
        Assert.Equal(9123456, r.Size);
        Assert.Equal("https://github.com/darkbepo/SubSync/releases/download/v1.3.0/SubSync-v1.3.0-windows-x64.zip", r.DownloadUrl.ToString());
        Assert.StartsWith("Faster starts", r.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_link_to_SubSyncs_own_releases_is_passed_on()
    {
        Assert.Equal("https://github.com/darkbepo/SubSync/releases/tag/v1.3.0", GitHubSubSyncReleases.Parse(Json())!.PageUrl);
        var elsewhere = Json().Replace("\"html_url\": \"https://github.com/darkbepo/SubSync/releases/tag/v1.3.0\"", "\"html_url\": \"javascript:alert(1)\"", StringComparison.Ordinal);
        Assert.Equal(GitHubSubSyncReleases.ReleasePagePrefix + "latest", GitHubSubSyncReleases.Parse(elsewhere)!.PageUrl);
    }

    [Theory]
    [InlineData("v2.0", 2, 0, 0)]
    [InlineData("1.10.3", 1, 10, 3)]
    public void Tags_become_three_part_versions(string tag, int major, int minor, int patch)
    {
        Assert.Equal(new Version(major, minor, patch), GitHubSubSyncReleases.Parse(Json(tag: tag))!.Version);
    }

    [Fact]
    public void Another_zip_name_with_SubSync_in_it_is_accepted()
    {
        Assert.Equal("SubSync-1.4.0.zip", GitHubSubSyncReleases.Parse(Json(asset: "SubSync-1.4.0.zip"))!.AssetName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("md5:abc")]
    [InlineData("sha256:nothex")]
    public void A_missing_or_odd_checksum_is_null(string? digest)
    {
        Assert.Null(GitHubSubSyncReleases.Parse(Json(digest: digest))!.Sha256);
    }

    [Theory]
    [InlineData(true, false, "v1.3.0", "SubSync-v1.3.0-windows-x64.zip")]
    [InlineData(false, true, "v1.3.0", "SubSync-v1.3.0-windows-x64.zip")]
    [InlineData(false, false, "nightly", "SubSync-v1.3.0-windows-x64.zip")]
    [InlineData(false, false, "v1.3.0", "other.zip")]
    public void Pre_releases_drafts_odd_tags_and_no_zip_are_ignored(bool prerelease, bool draft, string tag, string asset)
    {
        Assert.Null(GitHubSubSyncReleases.Parse(Json(tag, asset, prerelease: prerelease, draft: draft)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"message":"Not Found"}""")]
    public void Anything_else_is_null(string? json)
    {
        Assert.Null(GitHubSubSyncReleases.Parse(json));
    }
}

public sealed class SubSyncInstallerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<(string Exe, string Command)> _runs = new();
    private int _setupExit;
    private bool _inUse;

    public SubSyncInstallerTests()
    {
        _dir.File("subsync-cli.exe", "SubSync 1.3.0");
        _dir.File("SubSync.exe", "SubSync 1.3.0 window");
        _dir.File("runtime.txt", "runtime stays");
    }

    public void Dispose() => _dir.Dispose();

    private string Cli => Path.Combine(_dir.Path, "subsync-cli.exe");

    private string Window => Path.Combine(_dir.Path, "SubSync.exe");

    private SubSyncInstaller Installer(FakeReleases source) => new(source, Run, _ => _inUse, TimeSpan.FromMilliseconds(200), NullLogger.Instance);

    private Task<ProcessOutcome> Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        _runs.Add((exe, args[0]));
        return Task.FromResult(args[0] == "version"
            ? new ProcessOutcome(ProcessEnd.Exited, 0, File.ReadAllText(exe), TimeSpan.Zero)
            : new ProcessOutcome(ProcessEnd.Exited, _setupExit, "Downloading 2 file(s)… done", TimeSpan.Zero));
    }

    private void AssertUnchanged()
    {
        Assert.Equal("SubSync 1.3.0", File.ReadAllText(Cli));
        Assert.Equal("SubSync 1.3.0 window", File.ReadAllText(Window));
        Assert.False(File.Exists(Cli + ".old"));
        Assert.False(File.Exists(Window + ".old"));
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, SubSyncInstaller.WorkFolderName)));
        Assert.DoesNotContain(_runs, r => r.Command == "setup");
    }

    [Fact]
    public async Task Installs_both_exes_then_runs_setup()
    {
        var source = new FakeReleases("1.4.0");
        var steps = new List<string>();

        var result = await Installer(source).InstallAsync(_dir.Path, source.Release!, steps.Add, CancellationToken.None);

        Assert.Equal(new Version(1, 4, 0), result.Version);
        Assert.Equal(0, result.SetupExitCode);
        Assert.Equal("SubSync 1.4.0", File.ReadAllText(Cli));
        Assert.Equal("SubSync 1.4.0", File.ReadAllText(Window));
        Assert.Equal("runtime stays", File.ReadAllText(Path.Combine(_dir.Path, "runtime.txt")));
        Assert.False(File.Exists(Cli + ".old"));
        Assert.False(File.Exists(Window + ".old"));
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, SubSyncInstaller.WorkFolderName)));
        Assert.Equal(["downloading", "checking", "waiting", "installing", "setup"], steps);
        Assert.Equal((Cli, "setup"), _runs[^1]);
        Assert.Contains(_runs, r => r.Exe == Cli && r.Command == "version");
    }

    [Fact]
    public async Task A_folder_without_the_window_program_gets_only_the_command_line()
    {
        File.Delete(Window);
        var source = new FakeReleases("1.4.0");

        await Installer(source).InstallAsync(_dir.Path, source.Release!, _ => { }, CancellationToken.None);

        Assert.Equal("SubSync 1.4.0", File.ReadAllText(Cli));
        Assert.False(File.Exists(Window));
    }

    [Fact]
    public async Task Exes_one_folder_down_in_the_zip_are_found()
    {
        var source = new FakeReleases("1.4.0", ("SubSync-v1.4.0/subsync-cli.exe", "SubSync 1.4.0"), ("SubSync-v1.4.0/SubSync.exe", "SubSync 1.4.0"));

        await Installer(source).InstallAsync(_dir.Path, source.Release!, _ => { }, CancellationToken.None);

        Assert.Equal("SubSync 1.4.0", File.ReadAllText(Cli));
    }

    [Fact]
    public async Task A_checksum_mismatch_changes_nothing()
    {
        var source = new FakeReleases("1.4.0");
        source.Zip = FakeReleases.MakeZip([("subsync-cli.exe", "SubSync 1.4.0 tampered")]);

        var ex = await Assert.ThrowsAsync<SubSyncUpdateException>(() => Installer(source).InstallAsync(_dir.Path, source.Release!, _ => { }, CancellationToken.None));

        Assert.Contains("checksum", ex.Message, StringComparison.Ordinal);
        AssertUnchanged();
    }

    [Fact]
    public async Task No_checksum_from_GitHub_means_no_download()
    {
        var source = new FakeReleases("1.4.0");
        source.Release = source.Release! with { Sha256 = null };

        await Assert.ThrowsAsync<SubSyncUpdateException>(() => Installer(source).InstallAsync(_dir.Path, source.Release, _ => { }, CancellationToken.None));

        Assert.Equal(0, source.Downloads);
        AssertUnchanged();
    }

    [Theory]
    [InlineData("README.md", "no exe here")]
    [InlineData("a/b/subsync-cli.exe", "SubSync 1.4.0")] // too deep
    public async Task A_zip_without_the_command_line_changes_nothing(string path, string content)
    {
        var source = new FakeReleases("1.4.0", (path, content));

        await Assert.ThrowsAsync<SubSyncUpdateException>(() => Installer(source).InstallAsync(_dir.Path, source.Release!, _ => { }, CancellationToken.None));

        AssertUnchanged();
    }

    [Fact]
    public async Task A_path_in_the_zip_cannot_write_outside_the_work_folder()
    {
        var source = new FakeReleases("1.4.0", ("../subsync-cli.exe", "SubSync 1.4.0"), ("../../evil.txt", "x"));

        await Installer(source).InstallAsync(_dir.Path, source.Release!, _ => { }, CancellationToken.None);

        Assert.Equal("SubSync 1.4.0", File.ReadAllText(Cli));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir.Path)!, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "evil.txt")));
    }

    [Fact]
    public async Task A_new_exe_reporting_another_version_changes_nothing()
    {
        var source = new FakeReleases("1.4.0", ("subsync-cli.exe", "SubSync 1.3.9"));

        var ex = await Assert.ThrowsAsync<SubSyncUpdateException>(() => Installer(source).InstallAsync(_dir.Path, source.Release!, _ => { }, CancellationToken.None));

        Assert.Contains("1.3.9", ex.Message, StringComparison.Ordinal);
        AssertUnchanged();
    }

    [Fact]
    public async Task A_failed_check_after_the_swap_puts_the_old_exes_back()
    {
        var source = new FakeReleases("1.4.0");
        var calls = 0;
        var installer = new SubSyncInstaller(
            source,
            (exe, args, t, ct) =>
            {
                _runs.Add((exe, args[0]));

                // The staged exe answers; the installed one doesn't (a damaged write, a blocked exe).
                var ok = ++calls == 1;
                return Task.FromResult(new ProcessOutcome(ProcessEnd.Exited, ok ? 0 : 1, ok ? File.ReadAllText(exe) : "boom", TimeSpan.Zero));
            },
            _ => false,
            TimeSpan.Zero,
            NullLogger.Instance);

        await Assert.ThrowsAsync<SubSyncUpdateException>(() => installer.InstallAsync(_dir.Path, source.Release!, _ => { }, CancellationToken.None));

        AssertUnchanged();
    }

    [Fact]
    public async Task SubSync_running_from_the_folder_blocks_the_swap()
    {
        _inUse = true;
        var source = new FakeReleases("1.4.0");

        var ex = await Assert.ThrowsAsync<SubSyncUpdateException>(() => Installer(source).InstallAsync(_dir.Path, source.Release!, _ => { }, CancellationToken.None));

        Assert.Contains("running", ex.Message, StringComparison.Ordinal);
        AssertUnchanged();
    }

    [Fact]
    public async Task SubSync_that_ends_while_waiting_lets_the_update_go_ahead()
    {
        var checks = 0;
        var source = new FakeReleases("1.4.0");
        var installer = new SubSyncInstaller(source, Run, _ => ++checks < 2, TimeSpan.FromSeconds(5), NullLogger.Instance);

        await installer.InstallAsync(_dir.Path, source.Release!, _ => { }, CancellationToken.None);

        Assert.Equal("SubSync 1.4.0", File.ReadAllText(Cli));
    }

    [Fact]
    public async Task A_failed_setup_keeps_the_new_version_and_says_so()
    {
        _setupExit = 7;
        var source = new FakeReleases("1.4.0");

        var result = await Installer(source).InstallAsync(_dir.Path, source.Release!, _ => { }, CancellationToken.None);

        Assert.Equal(7, result.SetupExitCode);
        Assert.Contains("Downloading", result.SetupOutput, StringComparison.Ordinal);
        Assert.Equal("SubSync 1.4.0", File.ReadAllText(Cli));
    }

    [Fact]
    public void An_interrupted_swap_is_tidied_up()
    {
        // Jellyfin stopped after the old exe was renamed but before the new one arrived.
        File.Move(Cli, Cli + ".old");

        // And after a finished swap, before the .old was deleted.
        File.WriteAllText(Window + ".old", "previous window");
        Directory.CreateDirectory(Path.Combine(_dir.Path, SubSyncInstaller.WorkFolderName, "new"));

        Installer(new FakeReleases("1.4.0")).RecoverLeftovers(_dir.Path);

        Assert.Equal("SubSync 1.3.0", File.ReadAllText(Cli));
        Assert.False(File.Exists(Cli + ".old"));
        Assert.Equal("SubSync 1.3.0 window", File.ReadAllText(Window));
        Assert.False(File.Exists(Window + ".old"));
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, SubSyncInstaller.WorkFolderName)));
    }
}

public sealed class SubSyncUpdateServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly JobQueue _queue = new(TimeProvider.System);
    private readonly ManualClock _clock = new();
    private readonly FakeReleases _source = new("1.4.0");
    private readonly SubSyncUpdateService _updates;
    private readonly TaskCompletionSource _setupGate = new();
    private bool _holdSetup;

    public SubSyncUpdateServiceTests()
    {
        _dir.File("subsync-cli.exe", "SubSync 1.3.0");
        var installer = new SubSyncInstaller(
            _source,
            async (exe, args, t, ct) =>
            {
                if (args[0] == "setup" && _holdSetup)
                {
                    await _setupGate.Task;
                }

                return new ProcessOutcome(ProcessEnd.Exited, 0, args[0] == "version" ? File.ReadAllText(exe) : "ok", TimeSpan.Zero);
            },
            _ => false,
            TimeSpan.Zero,
            NullLogger.Instance);
        _updates = new SubSyncUpdateService(
            _source,
            installer,
            _queue,
            () => _dir.Path,
            (cli, _) => Task.FromResult(File.Exists(cli) ? SubSyncVersion.Parse(File.ReadAllText(cli)) : null),
            _clock,
            NullLogger<SubSyncUpdateService>.Instance);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task An_older_install_is_offered_the_latest_release()
    {
        var status = await _updates.GetStatusAsync(false, CancellationToken.None);

        Assert.Equal("1.3.0", status.Installed);
        Assert.Equal("1.4.0", status.Latest);
        Assert.True(status.UpdateAvailable);
        Assert.Equal("Faster starts.", status.Notes);
        Assert.Equal("idle", status.State);
    }

    [Theory]
    [InlineData("SubSync 1.4.0")]
    [InlineData("SubSync 1.5.0")]
    [InlineData("garbage")]
    public async Task The_same_a_newer_or_an_unreadable_install_is_not_offered_anything(string installed)
    {
        File.WriteAllText(Path.Combine(_dir.Path, "subsync-cli.exe"), installed);

        Assert.False((await _updates.GetStatusAsync(false, CancellationToken.None)).UpdateAvailable);
        Assert.NotNull(await _updates.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GitHub_is_asked_at_most_every_six_hours_unless_the_admin_checks_again()
    {
        await _updates.GetStatusAsync(false, CancellationToken.None);
        await _updates.GetStatusAsync(false, CancellationToken.None);
        Assert.Equal(1, _source.Checks);

        await _updates.GetStatusAsync(true, CancellationToken.None);
        Assert.Equal(2, _source.Checks);

        _clock.Advance(TimeSpan.FromHours(6));
        await _updates.GetStatusAsync(false, CancellationToken.None);
        Assert.Equal(3, _source.Checks);
    }

    [Fact]
    public async Task A_failed_check_is_shown_and_retried_after_ten_minutes()
    {
        _source.CheckFails = new SubSyncUpdateException("GitHub answered HTTP 403 when asked for SubSync's latest release. Try again later.");

        var status = await _updates.GetStatusAsync(false, CancellationToken.None);
        Assert.Contains("403", status.Message, StringComparison.Ordinal);
        Assert.False(status.UpdateAvailable);
        await _updates.GetStatusAsync(false, CancellationToken.None);
        Assert.Equal(1, _source.Checks);

        _source.CheckFails = null;
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True((await _updates.GetStatusAsync(false, CancellationToken.None)).UpdateAvailable);
    }

    [Fact]
    public async Task An_update_closes_the_queue_until_setup_has_finished()
    {
        _holdSetup = true;

        Assert.Null(await _updates.StartAsync(CancellationToken.None));
        while ((await _updates.GetStatusAsync(false, CancellationToken.None)).Step != "setup")
        {
            await Task.Delay(10);
        }

        // Meanwhile nothing can be queued, and the page says why.
        var refused = _queue.Enqueue(Guid.NewGuid(), [new JobRequestItem(Guid.NewGuid(), "E1", "Show")], JobSource.Manual, null, 300);
        Assert.Equal(ErrorCodes.SubSyncUnavailable, refused.Error!.Code);
        var status = JobsController.BuildStatus(_queue.Snapshot(), Guid.NewGuid(), true, _ => true);
        Assert.False(status.SubsyncAvailable);
        Assert.Equal(SubSyncUpdateService.MaintenanceMessage, status.Maintenance);
        Assert.NotNull(await _updates.StartAsync(CancellationToken.None));

        _setupGate.SetResult();
        await _updates.Running;

        var done = await _updates.GetStatusAsync(false, CancellationToken.None);
        Assert.Equal("done", done.State);
        Assert.Equal("1.4.0", done.Installed);
        Assert.False(done.UpdateAvailable);
        Assert.Contains("1.4.0 is installed and ready", done.Message, StringComparison.Ordinal);
        Assert.Null(_queue.Snapshot().Maintenance);
        Assert.Null(_queue.Enqueue(Guid.NewGuid(), [new JobRequestItem(Guid.NewGuid(), "E1", "Show")], JobSource.Manual, null, 300).Error);
    }

    [Fact]
    public async Task A_busy_queue_refuses_the_update()
    {
        _queue.Enqueue(Guid.NewGuid(), [new JobRequestItem(Guid.NewGuid(), "E1", "Show")], JobSource.Manual, null, 300);

        var refused = await _updates.StartAsync(CancellationToken.None);

        Assert.Contains("queue is busy", refused, StringComparison.Ordinal);
        Assert.Equal(0, _source.Downloads);
        Assert.Null(_queue.Snapshot().Maintenance);
    }

    [Fact]
    public async Task A_failed_update_reopens_the_queue_and_says_why()
    {
        _source.Zip = FakeReleases.MakeZip([("subsync-cli.exe", "tampered")]);

        Assert.Null(await _updates.StartAsync(CancellationToken.None));
        await _updates.Running;

        var status = await _updates.GetStatusAsync(false, CancellationToken.None);
        Assert.Equal("failed", status.State);
        Assert.Contains("checksum", status.Message, StringComparison.Ordinal);
        Assert.Equal("1.3.0", status.Installed);
        Assert.True(status.UpdateAvailable);
        Assert.Null(_queue.Snapshot().Maintenance);
    }

    [Fact]
    public async Task No_folder_means_nothing_to_update()
    {
        var updates = new SubSyncUpdateService(_source, new SubSyncInstaller(_source, NullLogger<SubSyncInstaller>.Instance), _queue, () => null, (_, _) => Task.FromResult<Version?>(null), _clock, NullLogger<SubSyncUpdateService>.Instance);

        Assert.Null((await updates.GetStatusAsync(false, CancellationToken.None)).Installed);
        Assert.Contains("not configured", await updates.StartAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

public sealed class QueueMaintenanceTests
{
    [Fact]
    public async Task Maintenance_only_starts_on_an_empty_queue()
    {
        using var queue = new JobQueue(TimeProvider.System);
        queue.Enqueue(Guid.NewGuid(), [new JobRequestItem(Guid.NewGuid(), "E1", "Show")], JobSource.Manual, null, 300);
        Assert.False(queue.TryBeginMaintenance("updating"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = await queue.TakeAsync(timeout.Token);
        Assert.False(queue.TryBeginMaintenance("updating"), "a running entry keeps the queue busy");

        queue.Complete(job.Snapshot.JobId, JobItemStatus.Done, null, 1);
        Assert.True(queue.TryBeginMaintenance("updating"));
        Assert.False(queue.TryBeginMaintenance("again"));
        Assert.Equal("updating", queue.Snapshot().Maintenance);

        queue.EndMaintenance();
        Assert.Null(queue.Snapshot().Maintenance);
        Assert.Null(queue.Enqueue(Guid.NewGuid(), [new JobRequestItem(Guid.NewGuid(), "E2", "Show")], JobSource.Manual, null, 300).Error);
    }
}
