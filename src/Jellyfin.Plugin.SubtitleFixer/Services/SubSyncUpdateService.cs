using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Services;

/// <summary>What the settings page shows about SubSync updates (D46).</summary>
/// <param name="Installed">Installed version, or null when it can't be read.</param>
/// <param name="Latest">Latest release's version, or null when unknown.</param>
/// <param name="UpdateAvailable">The latest release is newer than the installed one and can be installed.</param>
/// <param name="ReleaseUrl">The latest release's page.</param>
/// <param name="Notes">Its release notes.</param>
/// <param name="State"><c>idle</c>, <c>running</c>, <c>done</c> or <c>failed</c>.</param>
/// <param name="Step">While running: <c>downloading</c>, <c>checking</c>, <c>waiting</c>, <c>installing</c> or <c>setup</c>.</param>
/// <param name="Message">The last update's result, or why the check failed.</param>
/// <param name="CheckedUtc">When GitHub was last asked.</param>
public sealed record SubSyncUpdateStatus(
    string? Installed,
    string? Latest,
    bool UpdateAvailable,
    string? ReleaseUrl,
    string? Notes,
    string State,
    string? Step,
    string? Message,
    DateTime? CheckedUtc);

/// <summary>
/// Looks for SubSync updates and installs one when an admin asks (D46). The check is cached for
/// <see cref="CacheFor"/> (GitHub allows 60 unauthenticated calls an hour), a failed one for <see cref="FailureCacheFor"/>.
/// An update runs in the background with the queue closed (<see cref="JobQueue.TryBeginMaintenance"/>): it only starts
/// when nothing is queued or running, and nothing can be queued until it has finished, setup included.
/// </summary>
public sealed class SubSyncUpdateService
{
    /// <summary>How long a successful check is reused.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    /// <summary>How long a failed check is reused before GitHub is asked again.</summary>
    public static readonly TimeSpan FailureCacheFor = TimeSpan.FromMinutes(10);

    /// <summary>What users see while the update runs.</summary>
    public const string MaintenanceMessage = "SubSync is being updated. Try again in a few minutes.";

    private readonly ISubSyncReleaseSource _source;
    private readonly SubSyncInstaller _installer;
    private readonly JobQueue _queue;
    private readonly Func<string?> _folder;
    private readonly Func<string, CancellationToken, Task<Version?>> _installedVersion;
    private readonly TimeProvider _time;
    private readonly ILogger<SubSyncUpdateService> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _checking = new(1, 1);
    private SubSyncRelease? _latest;
    private string? _checkError;
    private DateTime? _checkedUtc;
    private string _state = "idle";
    private string? _step;
    private string? _message;
    private Task _running = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncUpdateService"/> class.
    /// </summary>
    /// <param name="source">Release source.</param>
    /// <param name="installer">Installer.</param>
    /// <param name="queue">The job queue.</param>
    /// <param name="folder">Returns the configured SubSync folder.</param>
    /// <param name="installedVersion">Returns the installed version for a CLI path (<see cref="SubSyncVersion.GetAsync"/>).</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public SubSyncUpdateService(
        ISubSyncReleaseSource source,
        SubSyncInstaller installer,
        JobQueue queue,
        Func<string?> folder,
        Func<string, CancellationToken, Task<Version?>> installedVersion,
        TimeProvider time,
        ILogger<SubSyncUpdateService> logger)
    {
        _source = source;
        _installer = installer;
        _queue = queue;
        _folder = folder;
        _installedVersion = installedVersion;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets the running update, if any (tests wait on it).</summary>
    internal Task Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>The installed and latest versions, from the cache unless it is stale or <paramref name="refresh"/> is set.</summary>
    /// <param name="refresh">Ask GitHub again now ("Check again").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status.</returns>
    public async Task<SubSyncUpdateStatus> GetStatusAsync(bool refresh, CancellationToken cancellationToken)
    {
        var folder = Folder();
        if (folder is not null)
        {
            // Under the same lock that starts an update, so tidying up can never touch a running update's files.
            lock (_gate)
            {
                if (_state != "running")
                {
                    _installer.RecoverLeftovers(folder);
                }
            }
        }

        await CheckAsync(refresh, cancellationToken).ConfigureAwait(false);
        var installed = folder is null ? null : await _installedVersion(Path.Combine(folder, SubSyncProbe.CliFileName), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var latest = _latest;
            var available = _state != "running" && IsNewer(latest, installed);
            return new SubSyncUpdateStatus(
                installed?.ToString(3),
                latest?.Version.ToString(3),
                available,
                latest?.PageUrl,
                latest?.Notes,
                _state,
                _state == "running" ? _step : null,
                _message ?? (latest is null ? _checkError : null),
                _checkedUtc);
        }
    }

    /// <summary>Starts installing the latest release in the background. Admin only (the endpoint checks).</summary>
    /// <param name="cancellationToken">Cancellation token for the version checks before it starts.</param>
    /// <returns>Null when started; otherwise why not.</returns>
    public async Task<string?> StartAsync(CancellationToken cancellationToken)
    {
        var folder = Folder();
        if (folder is null || !File.Exists(Path.Combine(folder, SubSyncProbe.CliFileName)))
        {
            return "SubSync is not configured: set the SubSync folder and save first.";
        }

        await CheckAsync(false, cancellationToken).ConfigureAwait(false);
        var installed = await _installedVersion(Path.Combine(folder, SubSyncProbe.CliFileName), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_state == "running")
            {
                return "An update is already running.";
            }

            if (_latest is not { } latest || !IsNewer(latest, installed))
            {
                return installed is null
                    ? "The installed SubSync version can't be read, so it can't be updated from here. Press Test SubSync."
                    : "No newer SubSync release to install.";
            }

            if (!_queue.TryBeginMaintenance(MaintenanceMessage))
            {
                return "The queue is busy. Update SubSync when nothing is queued or running.";
            }

            _state = "running";
            _step = "downloading";
            _message = null;
            _running = Task.Run(() => InstallAsync(folder, latest));
            return null;
        }
    }

    private static bool IsNewer(SubSyncRelease? latest, Version? installed) =>
        latest is not null && installed is not null && latest.Sha256 is not null && latest.Version > installed;

    private string? Folder() => _folder()?.Trim() is { Length: > 0 } f ? f : null;

    private async Task CheckAsync(bool refresh, CancellationToken cancellationToken)
    {
        await _checking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            lock (_gate)
            {
                var keep = _latest is not null || _checkError is null ? CacheFor : FailureCacheFor;
                if (!refresh && _checkedUtc is { } at && now - at < keep)
                {
                    return;
                }
            }

            SubSyncRelease? latest = null;
            string? error = null;
            try
            {
                latest = await _source.GetLatestAsync(cancellationToken).ConfigureAwait(false);
                if (latest is null)
                {
                    error = "SubSync's latest release has no Windows zip to install.";
                }
            }
            catch (SubSyncUpdateException ex)
            {
                error = ex.Message;
                _logger.LogInformation("Subtitle Fixer could not check for a SubSync update: {Reason}", ex.Message);
            }

            lock (_gate)
            {
                // A failed check keeps the last good answer; it is only shown when there is none.
                _latest = latest ?? _latest;
                _checkError = error;
                _checkedUtc = now;
            }
        }
        finally
        {
            _checking.Release();
        }
    }

    private async Task InstallAsync(string folder, SubSyncRelease release)
    {
        string state;
        string message;
        try
        {
            var result = await _installer.InstallAsync(folder, release, s => { lock (_gate) { _step = s; } }, CancellationToken.None).ConfigureAwait(false);
            state = "done";
            message = result.SetupExitCode == 0
                ? $"SubSync {result.Version.ToString(3)} is installed and ready."
                : $"SubSync {result.Version.ToString(3)} is installed, but its setup ended with {(result.SetupExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "a time-out")}. Run 'subsync-cli setup' on the server, or let the first sync finish it. {result.SetupOutput}".Trim();
        }
        catch (SubSyncUpdateException ex)
        {
            state = "failed";
            message = ex.Message;
            _logger.LogWarning("Subtitle Fixer could not update SubSync: {Reason}", ex.Message);
        }
        catch (Exception ex)
        {
            state = "failed";
            message = "The update failed: " + ex.Message;
            _logger.LogError(ex, "Subtitle Fixer could not update SubSync");
        }
        finally
        {
            _queue.EndMaintenance();
        }

        lock (_gate)
        {
            _state = state;
            _step = null;
            _message = message;
        }
    }
}
