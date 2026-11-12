using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>
/// Finds out which SubSync is installed, so features added in 1.3.0 (<c>--output</c>, <c>--progress</c>, report timings)
/// are used only where they exist (JOB-FLOW.md, SubSync CLI facts). <c>subsync-cli version</c> prints
/// <c>SubSync 1.3.0</c>; since 1.3.0 the exe answers it without starting Python. The answer is cached per CLI path,
/// size and modification time, so an upgrade in place is noticed on the next run. A failed read is not cached, and
/// the caller then behaves as with SubSync 1.2.0.
/// </summary>
public sealed partial class SubSyncVersion
{
    /// <summary>The first version with <c>--output</c>, <c>--progress</c>, report timings and <c>serve</c>.</summary>
    public static readonly Version ProgressAndOutput = new(1, 3, 0);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Func<string, CancellationToken, Task<string?>> _read;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Size, DateTime ModifiedUtc, Version Version)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncVersion"/> class.
    /// </summary>
    /// <param name="read">Runs <c>version</c> for a CLI path and returns its output, or null when it failed (tests replace it).</param>
    public SubSyncVersion(Func<string, CancellationToken, Task<string?>>? read = null)
    {
        _read = read ?? ReadAsync;
    }

    /// <summary>Gets the instance the plugin uses.</summary>
    public static SubSyncVersion Shared { get; } = new();

    /// <summary>Whether this version has <c>--output</c>, <c>--progress</c> and report timings.</summary>
    /// <param name="version">Installed version, or null when unknown.</param>
    /// <returns>True from 1.3.0 on; false when unknown.</returns>
    public static bool HasProgressAndOutput(Version? version) => version is not null && version >= ProgressAndOutput;

    /// <summary>Reads the version from <c>version</c>'s output, for example "SubSync 1.3.0".</summary>
    /// <param name="output">Output.</param>
    /// <returns>The version, or null.</returns>
    public static Version? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = VersionPattern().Match(output);
        if (!match.Success)
        {
            return null;
        }

        var patch = match.Groups[3].Success ? match.Groups[3].Value : "0";
        return Version.TryParse(match.Groups[1].Value + "." + match.Groups[2].Value + "." + patch, out var v) ? v : null;
    }

    /// <summary>The installed version for this CLI, from the cache while the exe is unchanged.</summary>
    /// <param name="cliPath">Full path of <c>subsync-cli.exe</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version, or null when it can't be read.</returns>
    public async Task<Version?> GetAsync(string cliPath, CancellationToken cancellationToken)
    {
        if (Stamp(cliPath) is not { } stamp)
        {
            return null;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(cliPath, out var hit) && hit.Size == stamp.Size && hit.ModifiedUtc == stamp.ModifiedUtc)
            {
                return hit.Version;
            }
        }

        string? output;
        try
        {
            output = await _read(cliPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }

        var version = Parse(output);
        if (version is not null)
        {
            lock (_gate)
            {
                _cache[cliPath] = (stamp.Size, stamp.ModifiedUtc, version);
            }
        }

        return version;
    }

    private static (long Size, DateTime ModifiedUtc)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadAsync(string cliPath, CancellationToken cancellationToken)
    {
        var outcome = await ProcessRunner.RunAsync(cliPath, ["version"], Path.GetDirectoryName(cliPath)!, Timeout, cancellationToken).ConfigureAwait(false);
        return outcome.End == ProcessEnd.Exited && outcome.ExitCode == 0 ? outcome.Output : null;
    }

    [GeneratedRegex(@"SubSync\s+v?(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex VersionPattern();
}
