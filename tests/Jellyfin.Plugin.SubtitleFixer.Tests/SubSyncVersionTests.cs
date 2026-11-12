using Jellyfin.Plugin.SubtitleFixer.Worker;
using Xunit;

namespace Jellyfin.Plugin.SubtitleFixer.Tests;

public sealed class SubSyncVersionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("SubSync 1.3.0", "1.3.0")]
    [InlineData("SubSync 1.2.0\r\n", "1.2.0")]
    [InlineData("Repairing…\nSubSync v1.10.2", "1.10.2")]
    [InlineData("SubSync 2.0", "2.0.0")]
    [InlineData("1.3.0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parse_reads_the_version_line(string? output, string? expected)
    {
        Assert.Equal(expected is null ? null : Version.Parse(expected), SubSyncVersion.Parse(output));
    }

    [Theory]
    [InlineData("1.2.0", false)]
    [InlineData("1.2.9", false)]
    [InlineData("1.3.0", true)]
    [InlineData("1.4.1", true)]
    [InlineData(null, false)]
    public void New_features_start_at_1_3_0(string? version, bool expected)
    {
        Assert.Equal(expected, SubSyncVersion.HasProgressAndOutput(version is null ? null : Version.Parse(version)));
    }

    [Fact]
    public async Task Answer_is_cached_until_the_exe_changes()
    {
        var cli = _dir.File("subsync-cli.exe", "old exe");
        var calls = 0;
        var answer = "SubSync 1.2.0";
        var version = new SubSyncVersion((_, _) =>
        {
            calls++;
            return Task.FromResult<string?>(answer);
        });

        Assert.Equal(new Version(1, 2, 0), await version.GetAsync(cli, CancellationToken.None));
        Assert.Equal(new Version(1, 2, 0), await version.GetAsync(cli, CancellationToken.None));
        Assert.Equal(1, calls);

        // Upgrade in place: a new size and modification time.
        answer = "SubSync 1.3.0";
        File.WriteAllText(cli, "new exe, larger");
        File.SetLastWriteTimeUtc(cli, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(new Version(1, 3, 0), await version.GetAsync(cli, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_failed_read_is_unknown_and_not_cached()
    {
        var cli = _dir.File("subsync-cli.exe");
        var calls = 0;
        var version = new SubSyncVersion((_, _) =>
        {
            calls++;
            return calls == 1 ? throw new IOException("busy") : Task.FromResult<string?>(calls == 2 ? "garbage" : "SubSync 1.3.0");
        });

        Assert.Null(await version.GetAsync(cli, CancellationToken.None));
        Assert.Null(await version.GetAsync(cli, CancellationToken.None));
        Assert.Equal(new Version(1, 3, 0), await version.GetAsync(cli, CancellationToken.None));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_missing_exe_is_unknown()
    {
        var version = new SubSyncVersion((_, _) => Task.FromResult<string?>("SubSync 1.3.0"));
        Assert.Null(await version.GetAsync(Path.Combine(_dir.Path, "none.exe"), CancellationToken.None));
    }
}
