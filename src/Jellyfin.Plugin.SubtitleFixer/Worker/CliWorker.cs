using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>
/// <see cref="ISubSyncWorker"/> that runs one <c>subsync-cli.exe</c> per episode (D5). SubSync 1.3.0 and later:
/// <c>sync --pair VIDEO SUBTITLE --output SUBTITLE --overwrite --report FILE --progress [--keep-layout] [--cpu]</c>.
/// SubSync 1.2.0, or when the version can't be read: <c>sync --pair VIDEO SUBTITLE --overwrite --suffix TAG --report FILE
/// [--keep-layout] [--cpu]</c>, with the hearing-impaired workaround (D27).
/// </summary>
public sealed class CliWorker : ISubSyncWorker
{
    private readonly Func<string?> _folder;
    private readonly ILogger<CliWorker> _logger;
    private readonly Func<string, CancellationToken, Task<Version?>> _version;
    private readonly RunProcess _run;
    private readonly Func<string, IReadOnlyList<string>, string, IServeTransport> _serve;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliWorker"/> class.
    /// </summary>
    /// <param name="folder">Returns the configured SubSync folder (read on every run so settings apply at once).</param>
    /// <param name="logger">Logger.</param>
    public CliWorker(Func<string?> folder, ILogger<CliWorker> logger)
        : this(folder, logger, SubSyncVersion.Shared.GetAsync, DefaultRun, (file, args, dir) => new ServeProcess(file, args, dir))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CliWorker"/> class with a replaceable version check and process
    /// runner, so tests can play back recorded SubSync output without Windows.
    /// </summary>
    /// <param name="folder">Returns the configured SubSync folder.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="version">Returns the installed SubSync version for a CLI path, or null when unknown.</param>
    /// <param name="run">Runs the CLI.</param>
    /// <param name="serve">Creates the <c>serve</c> process (CLI path, arguments, working directory).</param>
    internal CliWorker(Func<string?> folder, ILogger<CliWorker> logger, Func<string, CancellationToken, Task<Version?>> version, RunProcess run, Func<string, IReadOnlyList<string>, string, IServeTransport>? serve = null)
    {
        _folder = folder;
        _logger = logger;
        _version = version;
        _run = run;
        _serve = serve ?? ((file, args, dir) => new ServeProcess(file, args, dir));
    }

    /// <summary>Runs the CLI once (see <see cref="ProcessRunner.RunAsync"/>).</summary>
    /// <param name="fileName">Executable.</param>
    /// <param name="arguments">Arguments.</param>
    /// <param name="workingDirectory">Working directory.</param>
    /// <param name="timeout">Kill after this long.</param>
    /// <param name="onLine">Each output line as it arrives.</param>
    /// <param name="cancellationToken">Kill when cancelled.</param>
    /// <returns>The outcome.</returns>
    internal delegate Task<ProcessOutcome> RunProcess(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, Action<string> onLine, CancellationToken cancellationToken);

    /// <inheritdoc />
    public bool IsConfigured => CliPath() is { } cli && File.Exists(cli);

    /// <summary>Builds the CLI arguments for one pair.</summary>
    /// <param name="videoPath">Video.</param>
    /// <param name="subtitlePath">Subtitle.</param>
    /// <param name="languageTag">Suffix.</param>
    /// <param name="reportPath">Report file.</param>
    /// <param name="options">Options.</param>
    /// <param name="inPlace">
    /// SubSync 1.3.0 or later: write the result over the subtitle itself with <c>--output</c> (every tag, <c>.hi</c> included;
    /// <c>--suffix</c> is then unused) and print <c>--progress</c> lines. False: the 1.2.0 call.
    /// </param>
    /// <returns>Arguments.</returns>
    public static IReadOnlyList<string> BuildArguments(string videoPath, string subtitlePath, string languageTag, string reportPath, WorkerOptions options, bool inPlace = false)
    {
        var args = inPlace
            ? new List<string> { "sync", "--pair", videoPath, subtitlePath, "--output", subtitlePath, "--overwrite", "--report", reportPath, "--progress" }
            : new List<string> { "sync", "--pair", videoPath, subtitlePath, "--overwrite", "--suffix", CliSuffix(languageTag), "--report", reportPath };
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

    /// <summary>
    /// SubSync 1.2.0 only accepts <c>--suffix en</c> or <c>eng</c> (verified, S5: <c>en.hi</c> is "invalid choice", exit 6).
    /// A hearing-impaired tag runs with its plain tag and the result is moved over the <c>.hi</c> file.
    /// </summary>
    /// <param name="languageTag"><c>en</c>, <c>eng</c>, <c>en.hi</c> or <c>eng.hi</c>.</param>
    /// <returns>The tag to pass to <c>--suffix</c>.</returns>
    public static string CliSuffix(string languageTag) =>
        languageTag.EndsWith(".hi", StringComparison.OrdinalIgnoreCase) ? languageTag[..^3] : languageTag;

    /// <summary>Where SubSync writes its result: <c>&lt;video folder&gt;\&lt;video name&gt;.&lt;suffix&gt;.srt</c>.</summary>
    /// <param name="videoPath">Video.</param>
    /// <param name="languageTag">Our tag.</param>
    /// <returns>The CLI's output path.</returns>
    public static string CliOutputPath(string videoPath, string languageTag) =>
        Path.Combine(Path.GetDirectoryName(videoPath) ?? string.Empty, Path.GetFileNameWithoutExtension(videoPath) + "." + CliSuffix(languageTag) + ".srt");

    /// <summary>Builds the <c>serve</c> arguments: <c>serve --overwrite [--keep-layout] [--cpu]</c> (D45).</summary>
    /// <param name="options">Options.</param>
    /// <returns>Arguments.</returns>
    public static IReadOnlyList<string> BuildServeArguments(WorkerOptions options)
    {
        var args = new List<string> { "serve", "--overwrite" };
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
    public async Task<SessionStart> StartSessionAsync(WorkerOptions options, CancellationToken cancellationToken)
    {
        var cli = CliPath();
        if (cli is null || !File.Exists(cli))
        {
            return SessionStart.NotAvailable("SubSync is not configured");
        }

        var version = await _version(cli, cancellationToken).ConfigureAwait(false);
        if (!SubSyncVersion.HasProgressAndOutput(version))
        {
            return SessionStart.NotAvailable($"SubSync {(version is null ? "of unknown version" : version.ToString(3))} has no serve command");
        }

        var tidy = options.TidyUpLines;
        var cpu = options.ForceCpu;
        bool Matches(WorkerOptions o) => o.TidyUpLines == tidy && o.ForceCpu == cpu && string.Equals(CliPath(), cli, StringComparison.OrdinalIgnoreCase);
        var start = await ServeSession.StartAsync(
            _serve(cli, BuildServeArguments(options), Path.GetDirectoryName(cli)!),
            Matches,
            options.Timeout,
            _logger,
            cancellationToken).ConfigureAwait(false);
        if (start.Session is not null)
        {
            _logger.LogInformation("Subtitle Fixer started SubSync {Version} for the queue (serve)", start.Session.Version);
        }
        else if (start.Fallback)
        {
            _logger.LogWarning("Subtitle Fixer runs one SubSync per item for this queue run: {Reason}", start.Reason);
        }

        return start;
    }

    /// <inheritdoc />
    public async Task<WorkerResult> RunAsync(string videoPath, string subtitlePath, string languageTag, WorkerOptions options, CancellationToken cancellationToken)
    {
        var cli = CliPath();
        if (cli is null || !File.Exists(cli))
        {
            return new WorkerResult(WorkerStatus.Unavailable, null, "SubSync is not configured.", null, 0, null, null);
        }

        // 1.3.0 and later write straight over the subtitle (--output); older versions, or an unreadable version, take the 1.2.0 path.
        var inPlace = SubSyncVersion.HasProgressAndOutput(await _version(cli, cancellationToken).ConfigureAwait(false));

        // 1.2.0, hearing-impaired: SubSync writes the plain-tag file next to the video; it must not already exist,
        // or SubSync would overwrite a subtitle we never backed up.
        var cliOutput = inPlace ? subtitlePath : CliOutputPath(videoPath, languageTag);
        var movesResult = !string.Equals(cliOutput, subtitlePath, StringComparison.OrdinalIgnoreCase);
        if (movesResult && File.Exists(cliOutput))
        {
            return new WorkerResult(WorkerStatus.Failed, null, $"{Path.GetFileName(cliOutput)} already exists next to the video; not overwriting it.", null, 0, null, null);
        }

        var reportFolder = Path.Combine(Path.GetTempPath(), "SubtitleFixer");
        Directory.CreateDirectory(reportFolder);
        var reportPath = Path.Combine(reportFolder, Guid.NewGuid().ToString("N") + ".json");
        var moved = false;

        // SubSync 1.2.0 writes its stage lines only to logs\cli.log when its output is piped (SubSyncStage), so follow
        // that file; 1.3.0 prints PROGRESS lines on stdout instead.
        var lines = LineReader(inPlace, options.OnProgress);
        using var tail = inPlace || options.OnProgress is null
            ? null
            : new LogTail(Path.Combine(Path.GetDirectoryName(cli)!, "logs", "cli.log"), lines.OnLine, TimeSpan.FromSeconds(1));

        try
        {
            var outcome = await _run(
                cli,
                BuildArguments(videoPath, subtitlePath, languageTag, reportPath, options, inPlace),
                Path.GetDirectoryName(cli)!,
                options.Timeout,
                lines.OnLine,
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
            if (report?.Speech is { } speech)
            {
                SpeechRecognition.ObserveDevice(speech.Device, speech.Name);
            }
            else
            {
                SpeechRecognition.Observe(outcome.Output);
            }

            var exit = outcome.ExitCode ?? -1;
            var (status, reason) = SubSyncOutcome.Decide(exit, report);
            if (status is WorkerStatus.Crashed or WorkerStatus.Failed or WorkerStatus.Unavailable)
            {
                _logger.LogWarning("SubSync {Status} (exit {Exit}) for {Subtitle}: {Reason}. Output: {Output}", status, exit, subtitlePath, reason, Tail(outcome.Output));
            }

            if (status == WorkerStatus.Synced && movesResult)
            {
                if (!File.Exists(cliOutput))
                {
                    return new WorkerResult(WorkerStatus.Failed, null, "invalid output: SubSync reported synced but wrote no file", report?.AudioLanguage, seconds, report?.SubSyncVersion, exit);
                }

                // The original .hi file is backed up by the job runner; replace it with the result.
                File.Move(cliOutput, subtitlePath, overwrite: true);
                moved = true;
            }

            // A 1.3.0 run that never reached reading audio or listening used a cached transcript, even where the report
            // has no timings (no_match items carry none); such a run says nothing about this server's speed (D40).
            var timings = report?.Timings ?? (inPlace && !lines.Listened ? new SubSyncTimings(true, null, null, null) : null);
            return new WorkerResult(
                status,
                status == WorkerStatus.Synced ? subtitlePath : null,
                reason,
                report?.AudioLanguage,
                report?.Seconds ?? seconds,
                report?.SubSyncVersion,
                exit,
                timings,
                seconds,
                report?.Speech?.ModelLoadSeconds);
        }
        finally
        {
            TryDelete(reportPath);
            if (movesResult && !moved)
            {
                // Never leave a stray plain-tag result next to the video (it did not exist before the run).
                TryDelete(cliOutput);
            }
        }
    }

    private static Task<ProcessOutcome> DefaultRun(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, Action<string> onLine, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(fileName, arguments, workingDirectory, timeout, cancellationToken, recordAsWorker: true, onLine: onLine);

    private static ProgressLines LineReader(bool progressLines, Action<WorkerProgress>? onProgress) => new(progressLines, onProgress);

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
