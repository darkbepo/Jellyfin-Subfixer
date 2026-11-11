using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>Result of <c>subsync-cli check</c> plus <c>version</c>.</summary>
/// <param name="Ok">Whether <c>check</c> exited 0.</param>
/// <param name="Version">Output of <c>version</c>, trimmed.</param>
/// <param name="Mode">GPU, CPU or unknown.</param>
/// <param name="Message">What to tell the admin.</param>
/// <param name="CheckExitCode">Exit code of <c>check</c>, or null if it did not run.</param>
/// <param name="CheckOutput">Raw output of <c>check</c>, for the diagnostics page.</param>
public sealed record SubSyncProbeResult(bool Ok, string? Version, string Mode, string Message, int? CheckExitCode, string? CheckOutput);

/// <summary>
/// Runs <c>subsync-cli check</c> and <c>version</c> for the settings and diagnostics pages. The real per-episode
/// worker (<c>CliWorker</c>) arrives in M2; this only proves the CLI starts under the Jellyfin account (spike S6).
/// </summary>
public sealed class SubSyncProbe
{
    /// <summary>Executable name inside the SubSync folder.</summary>
    public const string CliFileName = "subsync-cli.exe";

    /// <summary>Exit code of <c>check</c> when the install is incomplete (JOB-FLOW.md).</summary>
    public const int IncompleteInstallExitCode = 8;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly ILogger<SubSyncProbe> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncProbe"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public SubSyncProbe(ILogger<SubSyncProbe> logger)
    {
        _logger = logger;
    }

    /// <summary>Runs the probe.</summary>
    /// <param name="subSyncFolder">Configured SubSync folder.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<SubSyncProbeResult> RunAsync(string? subSyncFolder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subSyncFolder))
        {
            return new SubSyncProbeResult(false, null, "unknown", "SubSync is not configured. Set the SubSync folder.", null, null);
        }

        var cli = Path.Combine(subSyncFolder.Trim(), CliFileName);
        if (!File.Exists(cli))
        {
            return new SubSyncProbeResult(false, null, "unknown", $"{CliFileName} was not found in {subSyncFolder}.", null, null);
        }

        var version = await RunAsync(cli, "version", cancellationToken).ConfigureAwait(false);
        var check = await RunAsync(cli, "check", cancellationToken).ConfigureAwait(false);
        var output = check.Output;
        // `check` doesn't say GPU or CPU (S5); a real sync does, so fall back to what the last sync printed.
        var mode = SpeechRecognition.ModeOf(output) ?? SpeechRecognition.LastMode ?? "unknown";
        var detail = SpeechRecognition.LastLine is { } line ? " Last sync: " + line + "." : " GPU or CPU shows after the first sync.";

        var message = check.ExitCode switch
        {
            0 => "SubSync is ready." + detail,
            IncompleteInstallExitCode => "SubSync is not fully installed. Run 'subsync-cli setup' on the server (about 3.2 GB).",
            null => "SubSync did not finish in time.",
            _ => $"subsync-cli check failed with exit code {check.ExitCode}.",
        };

        return new SubSyncProbeResult(check.ExitCode == 0, version.ExitCode == 0 ? version.Output.Trim() : null, mode, message, check.ExitCode, output);
    }

    private async Task<(int? ExitCode, string Output)> RunAsync(string cli, string command, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(cli)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(command);

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogWarning(ex, "Could not start {Cli}", cli);
            return (null, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            lock (output)
            {
                return (null, output.ToString());
            }
        }

        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }
}

/// <summary>
/// Remembers SubSync's "Speech recognition: GPU (…)" / "CPU (…)" line from the last real sync. `check` doesn't
/// print it (verified live, S5), so this is the only reliable source, and it also catches a GPU server that
/// silently fell back to CPU (for example under a service account, S6).
/// </summary>
public static class SpeechRecognition
{
    private const string Prefix = "Speech recognition:";
    private static string? _lastLine;

    /// <summary>Gets the last line seen, for example "CPU (no usable NVIDIA GPU – slower)".</summary>
    public static string? LastLine => Volatile.Read(ref _lastLine);

    /// <summary>Gets GPU or CPU from the last line seen, or null.</summary>
    public static string? LastMode => ModeOf(LastLine is { } l ? Prefix + " " + l : null);

    /// <summary>Remembers the line from a sync's output, if it has one.</summary>
    /// <param name="output">CLI output.</param>
    public static void Observe(string? output)
    {
        var line = Find(output);
        if (line is not null)
        {
            Volatile.Write(ref _lastLine, line);
        }
    }

    /// <summary>Reads GPU or CPU from output that contains the speech-recognition line.</summary>
    /// <param name="output">CLI output.</param>
    /// <returns>GPU, CPU or null.</returns>
    public static string? ModeOf(string? output)
    {
        var line = Find(output);
        if (line is null)
        {
            return null;
        }

        if (line.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
        {
            return "GPU";
        }

        return line.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) ? "CPU" : null;
    }

    private static string? Find(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        foreach (var raw in output.Split('\n'))
        {
            var text = raw.Trim();
            if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return text[Prefix.Length..].Trim();
            }
        }

        return null;
    }
}
