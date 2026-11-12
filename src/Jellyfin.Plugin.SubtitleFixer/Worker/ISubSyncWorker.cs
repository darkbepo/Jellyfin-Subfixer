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
/// <param name="OnProgress">Called with each stage SubSync reports while it runs (<see cref="SubSyncStage"/> codes, and a percent from 1.3.0 on).</param>
/// <param name="SubtitleLanguage">The subtitle's language (D50): <c>en</c>, or <c>da</c> (SubSync 1.5.0 and later).</param>
/// <param name="Reference">For a Danish subtitle: the English subtitle of the same video to time it by, or null.</param>
public sealed record WorkerOptions(bool TidyUpLines, bool ForceCpu, TimeSpan Timeout, Action<WorkerProgress>? OnProgress = null,
    string SubtitleLanguage = Domain.SubtitleLanguage.English, string? Reference = null);

/// <summary>What SubSync is doing now.</summary>
/// <param name="Stage">A <see cref="SubSyncStage"/> code.</param>
/// <param name="Percent">0 to 100 within the stage (SubSync 1.3.0 and later), or null.</param>
public sealed record WorkerProgress(string Stage, int? Percent);

/// <summary>Result of one run.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="OutputPath">Where SubSync wrote the subtitle (same path as the input; SubSync overwrites).</param>
/// <param name="Reason">SubSync's reason, or ours.</param>
/// <param name="AudioLanguage">Audio language SubSync used.</param>
/// <param name="Seconds">Wall time of the run.</param>
/// <param name="SubSyncVersion">SubSync version from the report.</param>
/// <param name="ExitCode">Process exit code, when it exited on its own.</param>
/// <param name="Timings">Per-step seconds (SubSync 1.3.0 and later), or a from-cache marker; null when unknown.</param>
/// <param name="ProcessSeconds">Wall time of the whole process, start-up included, when it ran.</param>
/// <param name="ModelLoadSeconds">How long the speech model took to load (1.3.0 report), or null.</param>
public sealed record WorkerResult(
    WorkerStatus Status,
    string? OutputPath,
    string? Reason,
    string? AudioLanguage,
    double Seconds,
    string? SubSyncVersion,
    int? ExitCode,
    SubSyncTimings? Timings = null,
    double? ProcessSeconds = null,
    double? ModelLoadSeconds = null);

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

    /// <summary>
    /// Starts one SubSync that stays running for many items (<c>serve</c>, SubSync 1.3.0 and later; D45) and waits until
    /// it accepts requests. <see cref="SessionStart.Fallback"/> means it isn't available, and the queue run uses
    /// <see cref="RunAsync"/> per item instead; that is the default.
    /// </summary>
    /// <param name="options">Settings for every item of the session; <see cref="WorkerOptions.Timeout"/> bounds the wait for it.</param>
    /// <param name="cancellationToken">Cancels the wait (and ends the process).</param>
    /// <returns>The result.</returns>
    Task<SessionStart> StartSessionAsync(WorkerOptions options, CancellationToken cancellationToken) =>
        Task.FromResult(SessionStart.NotAvailable("this worker runs one SubSync per item"));

    /// <summary>Gets the installed SubSync version, or null when unknown (decides whether Danish can be synced, D50).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version, or null.</returns>
    Task<Version?> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult<Version?>(null);
}
