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
        var mode = output.Contains("CUDA", StringComparison.OrdinalIgnoreCase) || output.Contains("GPU", StringComparison.OrdinalIgnoreCase)
            ? "GPU"
            : output.Contains("CPU", StringComparison.OrdinalIgnoreCase) ? "CPU" : "unknown";

        var message = check.ExitCode switch
        {
            0 => "SubSync is ready.",
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
