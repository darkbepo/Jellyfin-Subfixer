using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>
/// <see cref="ISubSyncWorker"/> that runs one <c>subsync-cli.exe</c> per episode (D5):
/// <c>sync --pair VIDEO SUBTITLE --overwrite --suffix TAG --report FILE [--keep-layout] [--cpu]</c>.
/// </summary>
public sealed class CliWorker : ISubSyncWorker
{
    private readonly Func<string?> _folder;
    private readonly ILogger<CliWorker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliWorker"/> class.
    /// </summary>
    /// <param name="folder">Returns the configured SubSync folder (read on every run so settings apply at once).</param>
    /// <param name="logger">Logger.</param>
    public CliWorker(Func<string?> folder, ILogger<CliWorker> logger)
    {
        _folder = folder;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConfigured => CliPath() is { } cli && File.Exists(cli);

    /// <summary>Builds the CLI arguments for one pair.</summary>
    /// <param name="videoPath">Video.</param>
    /// <param name="subtitlePath">Subtitle.</param>
    /// <param name="languageTag">Suffix.</param>
    /// <param name="reportPath">Report file.</param>
    /// <param name="options">Options.</param>
    /// <returns>Arguments.</returns>
    public static IReadOnlyList<string> BuildArguments(string videoPath, string subtitlePath, string languageTag, string reportPath, WorkerOptions options)
    {
        var args = new List<string> { "sync", "--pair", videoPath, subtitlePath, "--overwrite", "--suffix", languageTag, "--report", reportPath };
        if (!options.TidyUpLines)
        {
            args.Add("--keep-layout");
        }

        if (options.ForceCpu)
        {
            args.Add("--cpu");
        }

        return args;
    }

    /// <inheritdoc />
    public async Task<WorkerResult> RunAsync(string videoPath, string subtitlePath, string languageTag, WorkerOptions options, CancellationToken cancellationToken)
    {
        var cli = CliPath();
        if (cli is null || !File.Exists(cli))
        {
            return new WorkerResult(WorkerStatus.Unavailable, null, "SubSync is not configured.", null, 0, null, null);
        }

        var reportFolder = Path.Combine(Path.GetTempPath(), "SubtitleFixer");
        Directory.CreateDirectory(reportFolder);
        var reportPath = Path.Combine(reportFolder, Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var outcome = await ProcessRunner.RunAsync(
                cli,
                BuildArguments(videoPath, subtitlePath, languageTag, reportPath, options),
                Path.GetDirectoryName(cli)!,
                options.Timeout,
                cancellationToken).ConfigureAwait(false);
            var seconds = outcome.Elapsed.TotalSeconds;

            switch (outcome.End)
            {
                case ProcessEnd.Cancelled:
                    return new WorkerResult(WorkerStatus.Cancelled, null, "Cancelled.", null, seconds, null, null);
                case ProcessEnd.TimedOut:
                    return new WorkerResult(WorkerStatus.TimedOut, null, $"Timed out after {options.Timeout.TotalMinutes:0} minutes.", null, seconds, null, null);
                case ProcessEnd.StartFailed:
                    return new WorkerResult(WorkerStatus.Crashed, null, "Could not start SubSync: " + outcome.Output.Trim(), null, seconds, null, null);
            }

            var report = File.Exists(reportPath)
                ? SubSyncReport.Parse(await File.ReadAllTextAsync(reportPath, CancellationToken.None).ConfigureAwait(false))
                : null;
            var exit = outcome.ExitCode ?? -1;
            var (status, reason) = SubSyncOutcome.Decide(exit, report);
            if (status is WorkerStatus.Crashed or WorkerStatus.Failed or WorkerStatus.Unavailable)
            {
                _logger.LogWarning("SubSync {Status} (exit {Exit}) for {Subtitle}: {Reason}. Output: {Output}", status, exit, subtitlePath, reason, Tail(outcome.Output));
            }

            return new WorkerResult(
                status,
                status == WorkerStatus.Synced ? subtitlePath : null,
                reason,
                report?.AudioLanguage,
                report?.Seconds ?? seconds,
                report?.SubSyncVersion,
                exit);
        }
        finally
        {
            TryDelete(reportPath);
        }
    }

    private static string Tail(string output) => output.Length <= 2000 ? output : output[^2000..];

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string? CliPath()
    {
        var folder = _folder()?.Trim();
        return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, SubSyncProbe.CliFileName);
    }
}
