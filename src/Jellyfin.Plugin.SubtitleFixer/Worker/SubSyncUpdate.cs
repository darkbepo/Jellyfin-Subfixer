using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>A SubSync release on GitHub that the updater can install (D46).</summary>
/// <param name="Version">Version from the tag (<c>v1.4.0</c> is 1.4.0).</param>
/// <param name="Tag">Tag name.</param>
/// <param name="Notes">Release notes (plain text, shortened).</param>
/// <param name="PageUrl">The release page.</param>
/// <param name="AssetName">The zip's name, for example <c>SubSync-v1.4.0-windows-x64.zip</c>.</param>
/// <param name="DownloadUrl">Where the zip downloads from.</param>
/// <param name="Sha256">The zip's SHA-256 as GitHub reports it (lower-case hex), or null when GitHub gives none.</param>
/// <param name="Size">The zip's size in bytes.</param>
public sealed record SubSyncRelease(Version Version, string Tag, string? Notes, string PageUrl, string AssetName, Uri DownloadUrl, string? Sha256, long Size);

/// <summary>Something the updater can tell the admin as is.</summary>
public sealed class SubSyncUpdateException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncUpdateException"/> class.
    /// </summary>
    public SubSyncUpdateException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncUpdateException"/> class.
    /// </summary>
    /// <param name="message">What to tell the admin.</param>
    public SubSyncUpdateException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncUpdateException"/> class.
    /// </summary>
    /// <param name="message">What to tell the admin.</param>
    /// <param name="inner">Cause.</param>
    public SubSyncUpdateException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Where SubSync releases come from: GitHub's latest release, or a stand-in in tests.</summary>
public interface ISubSyncReleaseSource
{
    /// <summary>The latest stable release (no drafts or pre-releases).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The release, or null when it has no usable zip.</returns>
    Task<SubSyncRelease?> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the release zip.</summary>
    /// <param name="release">Release.</param>
    /// <param name="destination">Where to write it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task DownloadAsync(SubSyncRelease release, Stream destination, CancellationToken cancellationToken);
}

/// <summary>
/// SubSync's releases on GitHub (<c>darkbepo/SubSync</c>). Every release has one asset,
/// <c>SubSync-vX.Y.Z-windows-x64.zip</c>, with both exes, both READMEs and the license; GitHub reports its SHA-256
/// (SubSync's CLAUDE.md, "Releases"). Unauthenticated: GitHub allows 60 API calls an hour per address, so callers
/// cache the answer (<c>SubSyncUpdateService</c>).
/// </summary>
public sealed partial class GitHubSubSyncReleases : ISubSyncReleaseSource
{
    /// <summary>Name of the client taken from <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "SubtitleFixer.SubSyncUpdate";

    /// <summary>The latest-release endpoint (drafts and pre-releases are never "latest").</summary>
    public const string LatestUrl = "https://api.github.com/repos/darkbepo/SubSync/releases/latest";

    /// <summary>Largest download accepted (a release zip is a few MB: two exes).</summary>
    public const long MaxDownloadBytes = 400L * 1024 * 1024;

    /// <summary>Release pages start with this; any other link from the API is not shown.</summary>
    public const string ReleasePagePrefix = "https://github.com/darkbepo/SubSync/releases/";

    private const int MaxNotesChars = 4000;
    private readonly IHttpClientFactory _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubSubSyncReleases"/> class.
    /// </summary>
    /// <param name="http">HTTP client factory.</param>
    public GitHubSubSyncReleases(IHttpClientFactory http)
    {
        _http = http;
    }

    /// <summary>Reads GitHub's release JSON.</summary>
    /// <param name="json">The <c>releases/latest</c> answer.</param>
    /// <returns>The release, or null when it is a draft or pre-release, has no version tag or no zip.</returns>
    public static SubSyncRelease? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Bool(r, "draft") || Bool(r, "prerelease"))
            {
                return null;
            }

            var tag = Str(r, "tag_name");
            var match = tag is null ? null : TagPattern().Match(tag);
            if (match is not { Success: true } || !Version.TryParse(match.Groups[1].Value, out var parsed))
            {
                return null;
            }

            var version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var zips = assets.EnumerateArray()
                .Where(a => a.ValueKind == JsonValueKind.Object && Str(a, "name") is { } n && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var asset = zips.FirstOrDefault(a => AssetPattern().IsMatch(Str(a, "name")!));
            if (asset.ValueKind == JsonValueKind.Undefined)
            {
                asset = zips.FirstOrDefault(a => Str(a, "name")!.Contains("subsync", StringComparison.OrdinalIgnoreCase));
            }

            if (asset.ValueKind == JsonValueKind.Undefined
                || !Uri.TryCreate(Str(asset, "browser_download_url"), UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            var digest = Str(asset, "digest");
            var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && Sha256Pattern().IsMatch(digest[7..])
                ? digest[7..].ToLowerInvariant()
                : null;
            var notes = Str(r, "body");
            if (notes is { Length: > MaxNotesChars })
            {
                notes = notes[..MaxNotesChars] + "…";
            }

            var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var bytes) ? bytes : 0;
            var page = Str(r, "html_url") is { } html && html.StartsWith(ReleasePagePrefix, StringComparison.Ordinal) ? html : ReleasePagePrefix + "latest";
            return new SubSyncRelease(version, tag!, notes, page, Str(asset, "name")!, url, sha, size);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<SubSyncRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.UserAgent.ParseAdd("Jellyfin-SubtitleFixer");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var client = _http.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(30);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SubSyncUpdateException($"GitHub answered HTTP {(int)response.StatusCode} when asked for SubSync's latest release. Try again later.");
            }

            return Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException ex)
        {
            throw new SubSyncUpdateException("The server could not reach GitHub to look for a SubSync update: " + ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SubSyncUpdateException("GitHub did not answer in time. Try again later.", ex);
        }
    }

    /// <inheritdoc />
    public async Task DownloadAsync(SubSyncRelease release, Stream destination, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("Jellyfin-SubtitleFixer");
        var client = _http.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromMinutes(30);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SubSyncUpdateException($"GitHub answered HTTP {(int)response.StatusCode} for {release.AssetName}.");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxDownloadBytes)
                {
                    throw new SubSyncUpdateException($"{release.AssetName} is larger than expected; not installing it.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new SubSyncUpdateException("The download from GitHub failed: " + ex.Message, ex);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"^v?(\d+\.\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^SubSync-v[\d.]+-windows-x64\.zip$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AssetPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Sha256Pattern();
}

/// <summary>How an install went.</summary>
/// <param name="Version">The version now installed.</param>
/// <param name="SetupExitCode">Exit code of <c>subsync-cli setup</c> afterwards (0 = done), or null if it did not finish.</param>
/// <param name="SetupOutput">The end of setup's output, for the admin when it failed.</param>
public sealed record SubSyncInstallResult(Version Version, int? SetupExitCode, string? SetupOutput);

/// <summary>
/// Installs a SubSync release into the SubSync folder (D46), as SubSync's README says to update: replace
/// <c>subsync-cli.exe</c> (and <c>SubSync.exe</c>, if it is there) and let SubSync fetch what changed. Order: download
/// into the folder's <c>.subfixer-update</c> work folder, check the SHA-256 against GitHub's, take the two exes out of
/// the zip under names we choose (never a path from the zip), check the new exe reports the release's version, wait
/// until no SubSync from the folder runs, then rename each current exe to <c>.old</c> and move the new one in, check
/// <c>version</c> again from its place, and only then delete the <c>.old</c> files. Any failure before that puts the
/// <c>.old</c> files back. Finally <c>subsync-cli setup</c> downloads whatever the new version needs.
/// </summary>
public sealed class SubSyncInstaller
{
    /// <summary>The work folder inside the SubSync folder (same drive, so the swap is a rename).</summary>
    public const string WorkFolderName = ".subfixer-update";

    /// <summary>Suffix of the previous exe while the swap is checked.</summary>
    public const string OldSuffix = ".old";

    /// <summary>The window program's file name.</summary>
    public const string WindowFileName = "SubSync.exe";

    private const long MaxExeBytes = 200L * 1024 * 1024;
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromHours(3);

    private readonly ISubSyncReleaseSource _source;
    private readonly RunTool _run;
    private readonly Func<string, bool> _inUse;
    private readonly TimeSpan _inUseWait;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncInstaller"/> class.
    /// </summary>
    /// <param name="source">Release source.</param>
    /// <param name="logger">Logger.</param>
    public SubSyncInstaller(ISubSyncReleaseSource source, ILogger<SubSyncInstaller> logger)
        : this(source, DefaultRun, SubSyncProcesses.AnyRunningIn, TimeSpan.FromMinutes(1), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncInstaller"/> class with replaceable process handling (tests).
    /// </summary>
    /// <param name="source">Release source.</param>
    /// <param name="run">Runs an exe with arguments.</param>
    /// <param name="inUse">Whether a SubSync program from this folder is running.</param>
    /// <param name="inUseWait">How long to wait for running SubSync programs to end before giving up.</param>
    /// <param name="logger">Logger.</param>
    internal SubSyncInstaller(ISubSyncReleaseSource source, RunTool run, Func<string, bool> inUse, TimeSpan inUseWait, ILogger logger)
    {
        _source = source;
        _run = run;
        _inUse = inUse;
        _inUseWait = inUseWait;
        _logger = logger;
    }

    /// <summary>Runs an exe and returns how it ended.</summary>
    /// <param name="exe">Executable.</param>
    /// <param name="arguments">Arguments.</param>
    /// <param name="timeout">Kill after this long.</param>
    /// <param name="cancellationToken">Kill when cancelled.</param>
    /// <returns>The outcome.</returns>
    internal delegate Task<ProcessOutcome> RunTool(string exe, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Cleans up after an update that was interrupted (Jellyfin stopped mid-swap): a missing exe gets its <c>.old</c>
    /// back; an <c>.old</c> next to a present exe is left over from a finished swap and is deleted; the work folder goes.
    /// </summary>
    /// <param name="folder">SubSync folder.</param>
    public void RecoverLeftovers(string folder)
    {
        foreach (var name in new[] { SubSyncProbe.CliFileName, WindowFileName })
        {
            var target = Path.Combine(folder, name);
            var old = target + OldSuffix;
            try
            {
                if (!File.Exists(old))
                {
                    continue;
                }

                if (File.Exists(target))
                {
                    File.Delete(old);
                }
                else
                {
                    File.Move(old, target);
                    _logger.LogWarning("Subtitle Fixer put back {Exe}: a SubSync update had been interrupted", name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Subtitle Fixer could not tidy up {Exe} after an interrupted SubSync update", name);
            }
        }

        TryDeleteFolder(Path.Combine(folder, WorkFolderName));
    }

    /// <summary>Installs the release. Throws <see cref="SubSyncUpdateException"/> with the reason when it can't.</summary>
    /// <param name="folder">SubSync folder.</param>
    /// <param name="release">Release to install.</param>
    /// <param name="step">Told each step: <c>downloading</c>, <c>checking</c>, <c>waiting</c>, <c>installing</c>, <c>setup</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<SubSyncInstallResult> InstallAsync(string folder, SubSyncRelease release, Action<string> step, CancellationToken cancellationToken)
    {
        if (release.Sha256 is null)
        {
            throw new SubSyncUpdateException($"GitHub gives no checksum for {release.AssetName}, so it can't be checked. Update SubSync by hand.");
        }

        var cli = Path.Combine(folder, SubSyncProbe.CliFileName);
        if (!File.Exists(cli))
        {
            throw new SubSyncUpdateException($"{SubSyncProbe.CliFileName} was not found in {folder}.");
        }

        RecoverLeftovers(folder);
        var work = Path.Combine(folder, WorkFolderName);
        try
        {
            Directory.CreateDirectory(work);

            // 1. Download and check it is the file GitHub describes.
            step("downloading");
            var zip = Path.Combine(work, "release.zip");
            await using (var file = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await _source.DownloadAsync(release, file, cancellationToken).ConfigureAwait(false);
            }

            step("checking");
            var sha = await Sha256Async(zip, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sha, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new SubSyncUpdateException($"The downloaded {release.AssetName} does not match GitHub's checksum; nothing was changed.");
            }

            // 2. Take the exes out under our own names.
            var staged = Path.Combine(work, "new");
            Directory.CreateDirectory(staged);
            var stagedCli = Extract(zip, SubSyncProbe.CliFileName, staged, release)
                ?? throw new SubSyncUpdateException($"{release.AssetName} has no {SubSyncProbe.CliFileName}; nothing was changed.");
            var stagedWindow = Extract(zip, WindowFileName, staged, release);

            // 3. The new exe must say it is the release's version.
            await ExpectVersionAsync(stagedCli, release.Version, "The downloaded", cancellationToken).ConfigureAwait(false);

            // 4. Nothing from the folder may run (the queue's SubSync may still be exiting; the window may be open).
            step("waiting");
            await WaitUntilFreeAsync(folder, cancellationToken).ConfigureAwait(false);

            // 5. Swap, keeping the old exes until the new ones answer from their place.
            step("installing");
            var swaps = new List<(string Target, string Old, bool HadOld)>();
            try
            {
                Swap(stagedCli, cli, swaps);
                var window = Path.Combine(folder, WindowFileName);
                if (stagedWindow is not null && File.Exists(window))
                {
                    // Only replace the window program where it is installed; a command-line-only folder stays so.
                    Swap(stagedWindow, window, swaps);
                }

                await ExpectVersionAsync(cli, release.Version, "The installed", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Rollback(swaps);
                if (ex is SubSyncUpdateException)
                {
                    throw;
                }

                throw new SubSyncUpdateException("SubSync could not be replaced, so the previous version was put back: " + ex.Message, ex);
            }

            foreach (var (_, old, hadOld) in swaps)
            {
                if (hadOld)
                {
                    TryDelete(old);
                }
            }

            _logger.LogInformation("Subtitle Fixer installed SubSync {Version} in {Folder}", release.Version.ToString(3), folder);

            // 6. Let SubSync download what the new version needs now, not during the first episode.
            step("setup");
            var setup = await _run(cli, ["setup"], SetupTimeout, cancellationToken).ConfigureAwait(false);
            var exit = setup.End == ProcessEnd.Exited ? setup.ExitCode : null;
            if (exit != 0)
            {
                _logger.LogWarning("subsync-cli setup after the update ended with {Exit}: {Output}", exit, Tail(setup.Output));
            }

            return new SubSyncInstallResult(release.Version, exit, exit == 0 ? null : Tail(setup.Output));
        }
        finally
        {
            TryDeleteFolder(work);
        }
    }

    private static string? Extract(string zip, string name, string staged, SubSyncRelease release)
    {
        using var archive = ZipFile.OpenRead(zip);

        // At the top of the zip or one folder down; the name is ours, so a path in the zip can't point anywhere else.
        var entries = archive.Entries
            .Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
                && e.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length <= 2)
            .ToList();
        if (entries.Count == 0)
        {
            return null;
        }

        if (entries.Count > 1)
        {
            throw new SubSyncUpdateException($"{release.AssetName} has more than one {name}; nothing was changed.");
        }

        var entry = entries[0];
        if (entry.Length is <= 0 or > MaxExeBytes)
        {
            throw new SubSyncUpdateException($"{name} in {release.AssetName} has an unexpected size; nothing was changed.");
        }

        var target = Path.Combine(staged, name);
        entry.ExtractToFile(target, overwrite: true);
        return target;
    }

    private static void Swap(string staged, string target, List<(string Target, string Old, bool HadOld)> swaps)
    {
        var old = target + OldSuffix;
        if (File.Exists(old))
        {
            File.Delete(old);
        }

        var hadOld = File.Exists(target);
        if (hadOld)
        {
            // Windows can rename a running exe, never overwrite it; nothing runs now, but renaming is the safe order.
            File.Move(target, old);
        }

        swaps.Add((target, old, hadOld));
        File.Move(staged, target);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Tail(string output) => output.Length <= 2000 ? output.Trim() : output[^2000..].Trim();

    private static Task<ProcessOutcome> DefaultRun(string exe, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(exe, arguments, Path.GetDirectoryName(exe)!, timeout, cancellationToken, recordAsWorker: arguments.Count > 0 && arguments[0] == "setup");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Rollback(List<(string Target, string Old, bool HadOld)> swaps)
    {
        for (var i = swaps.Count - 1; i >= 0; i--)
        {
            var (target, old, hadOld) = swaps[i];
            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                if (hadOld && File.Exists(old))
                {
                    File.Move(old, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Startup or the next update attempt puts the .old back (RecoverLeftovers).
                _logger.LogError(ex, "Subtitle Fixer could not put back {Exe}; {Old} is kept", target, old);
            }
        }
    }

    private async Task ExpectVersionAsync(string exe, Version expected, string which, CancellationToken cancellationToken)
    {
        var outcome = await _run(exe, ["version"], VersionTimeout, cancellationToken).ConfigureAwait(false);
        var reported = outcome.End == ProcessEnd.Exited && outcome.ExitCode == 0 ? SubSyncVersion.Parse(outcome.Output) : null;
        if (reported is null || reported != expected)
        {
            throw new SubSyncUpdateException($"{which} {SubSyncProbe.CliFileName} reports {(reported is null ? "no version" : reported.ToString(3))}, not {expected.ToString(3)}; nothing was changed.");
        }
    }

    private async Task WaitUntilFreeAsync(string folder, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _inUseWait;
        while (_inUse(folder))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new SubSyncUpdateException("SubSync is running from that folder (the SubSync window, or a sync). Close it, then update again; nothing was changed.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Finds SubSync programs running from a folder (Windows; elsewhere there are none).</summary>
public static class SubSyncProcesses
{
    private static readonly string[] Names = ["subsync-cli", "SubSync", "python", "pythonw"];

    /// <summary>Whether <c>subsync-cli.exe</c>, <c>SubSync.exe</c> or SubSync's own Python runs from this folder.</summary>
    /// <param name="folder">SubSync folder.</param>
    /// <returns>True when one does.</returns>
    public static bool AnyRunningIn(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var name in Names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } file && file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // Gone, or another account's process: not ours to judge.
                    }
                }
            }
        }

        return false;
    }
}
