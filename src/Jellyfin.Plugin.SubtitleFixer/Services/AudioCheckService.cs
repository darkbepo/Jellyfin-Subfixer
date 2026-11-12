using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.SubtitleFixer.Configuration;
using Jellyfin.Plugin.SubtitleFixer.Domain;
using Jellyfin.Plugin.SubtitleFixer.Services.Jobs;
using Jellyfin.Plugin.SubtitleFixer.State;
using Jellyfin.Plugin.SubtitleFixer.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Services;

/// <summary><c>POST /AudioCheck</c> response.</summary>
/// <param name="ItemId">Item.</param>
/// <param name="Language">Language SubSync heard, or null when it found no speech.</param>
/// <param name="Probability">0 to 1, or null.</param>
/// <param name="English">Whether it now counts as English audio (D49).</param>
/// <param name="SpeechSeconds">Speech listened to.</param>
/// <param name="CheckedUtc">When.</param>
/// <param name="Message">What it means, for people.</param>
public sealed record AudioCheckDto(Guid ItemId, string? Language, double? Probability, bool English, double SpeechSeconds, DateTime CheckedUtc, string Message);

/// <summary>An audio check error: HTTP status, code and message.</summary>
/// <param name="Status">HTTP status.</param>
/// <param name="Code">Error code.</param>
/// <param name="Message">Message for people.</param>
public sealed record AudioCheckError(int Status, string Code, string Message);

/// <summary>
/// The Check button (D49): runs SubSync 1.4.0's read-only <c>subsync-cli detect &lt;video&gt;</c> on an item whose
/// audio isn't tagged, and stores what it heard, tied to the video file. A check that heard English (at least
/// <see cref="AudioCheck.MinEnglishProbability"/>) makes the item count as English audio until the video changes.
/// One SubSync at a time (hard rule 2): a check only runs while no queue run is active, and the queue waits for it.
/// </summary>
public sealed class AudioCheckService
{
    /// <summary>Runs the CLI once and returns how it ended.</summary>
    /// <param name="fileName">Executable.</param>
    /// <param name="arguments">Arguments.</param>
    /// <param name="workingDirectory">Working directory.</param>
    /// <param name="timeout">Kill after this long.</param>
    /// <param name="onLine">Each output line.</param>
    /// <param name="cancellationToken">Kill when cancelled.</param>
    /// <returns>The outcome.</returns>
    public delegate Task<ProcessOutcome> RunDetect(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, Action<string> onLine, CancellationToken cancellationToken);

    /// <summary>The first SubSync with <c>detect</c>.</summary>
    public static readonly Version DetectVersion = new(1, 4, 0);

    /// <summary>Longest a check may take (the model load included; about a minute on a CPU).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private readonly ILibraryFacade _library;
    private readonly IStateStore _state;
    private readonly AudioCheckCache _cache;
    private readonly JobQueue _queue;
    private readonly Func<string?> _folder;
    private readonly Func<string, CancellationToken, Task<Version?>> _version;
    private readonly RunDetect _run;
    private readonly Func<PluginConfiguration?> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<AudioCheckService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioCheckService"/> class.
    /// </summary>
    /// <param name="library">Library facade (with checks attached).</param>
    /// <param name="state">State store.</param>
    /// <param name="cache">Stored checks in memory.</param>
    /// <param name="queue">The shared queue (the SubSync gate).</param>
    /// <param name="folder">The SubSync folder setting.</param>
    /// <param name="version">Installed SubSync version for a CLI path.</param>
    /// <param name="run">Runs the CLI (tests replace it).</param>
    /// <param name="config">Settings (Force CPU).</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public AudioCheckService(
        ILibraryFacade library,
        IStateStore state,
        AudioCheckCache cache,
        JobQueue queue,
        Func<string?> folder,
        Func<string, CancellationToken, Task<Version?>> version,
        RunDetect run,
        Func<PluginConfiguration?> config,
        TimeProvider time,
        ILogger<AudioCheckService> logger)
    {
        _library = library;
        _state = state;
        _cache = cache;
        _queue = queue;
        _folder = folder;
        _version = version;
        _run = run;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <summary>Runs SubSync's <c>detect</c> through <see cref="ProcessRunner"/>.</summary>
    /// <param name="fileName">Executable.</param>
    /// <param name="arguments">Arguments.</param>
    /// <param name="workingDirectory">Working directory.</param>
    /// <param name="timeout">Timeout.</param>
    /// <param name="onLine">Each output line.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public static Task<ProcessOutcome> DefaultRun(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, Action<string> onLine, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(fileName, arguments, workingDirectory, timeout, cancellationToken, recordAsWorker: false, onLine: onLine);

    /// <summary>Reads <c>detect</c>'s answer: the last output line that is a JSON object.</summary>
    /// <param name="output">Process output.</param>
    /// <returns>Language, probability, speech seconds and reason; null when there is no answer.</returns>
    public static (string? Language, double? Probability, double SpeechSeconds, string? Reason)? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')).Reverse())
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("language", out var lang))
                {
                    continue;
                }

                var language = lang.ValueKind == JsonValueKind.String ? lang.GetString() : null;
                double? probability = root.TryGetProperty("probability", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
                var speech = root.TryGetProperty("speech_seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
                var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                return (string.IsNullOrWhiteSpace(language) ? null : language, probability, speech, reason);
            }
            catch (JsonException)
            {
                // Not the answer; keep looking.
            }
        }

        return null;
    }

    /// <summary>What a check means, in words.</summary>
    /// <param name="check">Check.</param>
    /// <returns>Message.</returns>
    public static string Describe(AudioCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (check.Language is null)
        {
            return "No speech was found in the sample, so the language is still unknown.";
        }

        var percent = check.Probability is { } p ? Math.Round(p * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%" : "?";
        if (check.IsEnglish)
        {
            return $"Sounds like English ({percent} sure). It can be re-timed now.";
        }

        var code = check.Language.ToUpperInvariant();
        var name = LanguageName(check.Language);
        return string.Equals(check.Language, "en", StringComparison.OrdinalIgnoreCase)
            ? $"Probably English, but only {percent} sure, so it stays not eligible."
            : $"Sounds like {name} ({code}, {percent} sure), not English.";
    }

    /// <summary>Checks one item's audio.</summary>
    /// <param name="userId">Caller; <c>AllowedUsers</c> was already checked.</param>
    /// <param name="itemId">Episode or movie.</param>
    /// <param name="cancellationToken">Cancelled when the page goes away; ends SubSync.</param>
    /// <returns>The result, or an error.</returns>
    public async Task<(AudioCheckDto? Result, AudioCheckError? Error)> CheckAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var video = _library.GetVideo(userId, itemId);
        if (video is null)
        {
            return Fail(StatusCodes.Status404NotFound, "not_found", "That item was not found or you cannot access it.");
        }

        if (string.IsNullOrWhiteSpace(video.VideoPath) || VideoFingerprint.FromFile(video.VideoPath) is not { } fingerprint)
        {
            return Fail(StatusCodes.Status409Conflict, "no_video_file", "Jellyfin has no video file for this item.");
        }

        if (!EligibilityService.IsUntagged(video.AudioLanguages))
        {
            return video.AudioLanguages.Any(EligibilityService.IsEnglish)
                ? Fail(StatusCodes.Status409Conflict, "audio_tagged", "Its audio is already tagged as English.")
                : Fail(StatusCodes.Status409Conflict, "audio_tagged", "Its audio says which language it is (" + string.Join(", ", video.AudioLanguages.Where(l => !string.IsNullOrWhiteSpace(l))) + "). Only untagged audio can be checked.");
        }

        var cli = _folder() is { Length: > 0 } folder ? Path.Combine(folder, SubSyncProbe.CliFileName) : null;
        if (cli is null || !File.Exists(cli))
        {
            return Fail(StatusCodes.Status503ServiceUnavailable, ErrorCodes.SubSyncUnavailable, "SubSync is not configured. Ask an administrator.");
        }

        var version = await _version(cli, cancellationToken).ConfigureAwait(false);
        if (version is null || version < DetectVersion)
        {
            return Fail(StatusCodes.Status503ServiceUnavailable, "subsync_too_old", $"Checking the audio needs SubSync {DetectVersion.ToString(3)} or later{(version is null ? string.Empty : $" (this is {version.ToString(3)})")}. An administrator can update it on the settings page.");
        }

        if (!_queue.TryBeginCheck())
        {
            return Fail(StatusCodes.Status409Conflict, "subsync_busy", "SubSync is busy (the queue is running, another check is running, or it is being updated). Try again when it has finished.");
        }

        try
        {
            var args = new List<string> { "detect", fingerprint.Path };
            if (_config()?.ForceCpu == true)
            {
                args.Add("--cpu");
            }

            var outcome = await _run(cli, args, Path.GetDirectoryName(cli)!, Timeout, _ => { }, cancellationToken).ConfigureAwait(false);
            var answer = Parse(outcome.Output);
            switch (outcome.End)
            {
                case ProcessEnd.StartFailed:
                    return Fail(StatusCodes.Status503ServiceUnavailable, ErrorCodes.SubSyncUnavailable, "SubSync could not be started.");
                case ProcessEnd.Cancelled:
                    return Fail(StatusCodes.Status400BadRequest, "cancelled", "The check was cancelled.");
                case ProcessEnd.TimedOut:
                    return Fail(StatusCodes.Status504GatewayTimeout, "timed_out", $"The check took longer than {Timeout.TotalMinutes:0} minutes and was stopped.");
            }

            if (outcome.ExitCode == SubSyncOutcome.ExitUnavailable)
            {
                return Fail(StatusCodes.Status503ServiceUnavailable, ErrorCodes.SubSyncUnavailable, "SubSync could not run: close any open SubSync window or wait for its setup to finish (exit 7).");
            }

            if (outcome.ExitCode != 0 || answer is null)
            {
                var reason = answer?.Reason ?? "exit " + outcome.ExitCode;
                _logger.LogWarning("Subtitle Fixer audio check of {Video} failed: {Reason}", fingerprint.Path, reason);
                return Fail(StatusCodes.Status422UnprocessableEntity, "check_failed", $"SubSync couldn't check the audio: {reason}.");
            }

            var check = new AudioCheck(itemId, fingerprint, answer.Value.Language, answer.Value.Probability, answer.Value.SpeechSeconds, _time.GetUtcNow().UtcDateTime);
            await _state.UpsertAudioCheckAsync(check, CancellationToken.None).ConfigureAwait(false);
            _cache.Set(check);
            _logger.LogInformation(
                "Subtitle Fixer audio check of {Video}: {Language} ({Probability}) from {Speech:0} s of speech in {Seconds:0} s",
                fingerprint.Path,
                check.Language ?? "no speech",
                check.Probability,
                check.SpeechSeconds,
                outcome.Elapsed.TotalSeconds);
            return (new AudioCheckDto(itemId, check.Language, check.Probability, check.IsEnglish, check.SpeechSeconds, check.CheckedUtc, Describe(check)), null);
        }
        finally
        {
            _queue.EndCheck();
        }
    }

    private static (AudioCheckDto?, AudioCheckError?) Fail(int status, string code, string message) => (null, new AudioCheckError(status, code, message));

    private static string LanguageName(string code)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(code).EnglishName;
            return string.IsNullOrWhiteSpace(name) || name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ? code.ToUpperInvariant() : name;
        }
        catch (CultureNotFoundException)
        {
            return code.ToUpperInvariant();
        }
    }
}
