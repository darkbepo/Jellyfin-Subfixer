namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>Outcome of one SubSync run (ARCHITECTURE.md, worker interface).</summary>
public enum WorkerStatus
{
    /// <summary>SubSync reports the subtitle synced and wrote its output.</summary>
    Synced,

    /// <summary>Too few lines matched; SubSync wrote nothing (<c>no_match</c>).</summary>
    NoMatch,

    /// <summary>SubSync reports a failure for this pair.</summary>
    Failed,

    /// <summary>Our cancellation stopped the run.</summary>
    Cancelled,

    /// <summary>The per-episode timeout stopped the run.</summary>
    TimedOut,

    /// <summary>Exit 7: setup or repair impossible, or another SubSync is using the folder. Stops the queue.</summary>
    Unavailable,

    /// <summary>Exit 10, a crash, or no usable report. Counts toward the consecutive-crash stop (D14).</summary>
    Crashed,
}

/// <summary>Options for one run, taken from the settings.</summary>
/// <param name="TidyUpLines">Off passes <c>--keep-layout</c>.</param>
/// <param name="ForceCpu">Passes <c>--cpu</c>.</param>
/// <param name="Timeout">Kill the run after this long.</param>
/// <param name="OnStage">Called with each stage SubSync reports while it runs (<see cref="SubSyncStage"/> codes).</param>
public sealed record WorkerOptions(bool TidyUpLines, bool ForceCpu, TimeSpan Timeout, Action<string>? OnStage = null);

/// <summary>Result of one run.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="OutputPath">Where SubSync wrote the subtitle (same path as the input; SubSync overwrites).</param>
/// <param name="Reason">SubSync's reason, or ours.</param>
/// <param name="AudioLanguage">Audio language SubSync used.</param>
/// <param name="Seconds">Wall time of the run.</param>
/// <param name="SubSyncVersion">SubSync version from the report.</param>
/// <param name="ExitCode">Process exit code, when it exited on its own.</param>
public sealed record WorkerResult(
    WorkerStatus Status,
    string? OutputPath,
    string? Reason,
    string? AudioLanguage,
    double Seconds,
    string? SubSyncVersion,
    int? ExitCode);

/// <summary>
/// Runs SubSync on one video/subtitle pair. v1 spawns <c>subsync-cli.exe</c>; a later implementation can call a
/// Linux sidecar. Nothing outside <c>Worker/</c> knows about the CLI.
/// </summary>
public interface ISubSyncWorker
{
    /// <summary>Gets a value indicating whether SubSync is configured (folder set and the CLI present). Cheap.</summary>
    bool IsConfigured { get; }

    /// <summary>Runs SubSync for one pair. Never throws for SubSync outcomes; cancellation returns <see cref="WorkerStatus.Cancelled"/>.</summary>
    /// <param name="videoPath">Video file.</param>
    /// <param name="subtitlePath">Subtitle to re-time; overwritten in place on success.</param>
    /// <param name="languageTag"><c>en</c> or <c>eng</c>, matching the subtitle name (<c>--suffix</c>).</param>
    /// <param name="options">Run options.</param>
    /// <param name="cancellationToken">Cancels the run (kills the process tree).</param>
    /// <returns>The result.</returns>
    Task<WorkerResult> RunAsync(string videoPath, string subtitlePath, string languageTag, WorkerOptions options, CancellationToken cancellationToken);
}
