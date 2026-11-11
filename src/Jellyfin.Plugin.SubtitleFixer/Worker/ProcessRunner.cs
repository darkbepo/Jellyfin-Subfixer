using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>How a process run ended.</summary>
public enum ProcessEnd
{
    /// <summary>The process exited on its own.</summary>
    Exited,

    /// <summary>Killed because the caller cancelled.</summary>
    Cancelled,

    /// <summary>Killed because the timeout passed.</summary>
    TimedOut,

    /// <summary>The process could not be started.</summary>
    StartFailed,
}

/// <summary>Result of <see cref="ProcessRunner.RunAsync"/>.</summary>
/// <param name="End">How it ended.</param>
/// <param name="ExitCode">Exit code when it exited on its own.</param>
/// <param name="Output">Combined stdout and stderr (capped).</param>
/// <param name="Elapsed">Wall time.</param>
public sealed record ProcessOutcome(ProcessEnd End, int? ExitCode, string Output, TimeSpan Elapsed);

/// <summary>
/// Starts a process without a window, collects its output, and on cancel or timeout kills the whole process tree
/// and waits for it to go (SubSync starts Python children; JOB-FLOW.md).
/// </summary>
public static class ProcessRunner
{
    private const int MaxOutputChars = 64 * 1024;

    /// <summary>Runs a process to completion, cancellation or timeout.</summary>
    /// <param name="fileName">Executable.</param>
    /// <param name="arguments">Arguments, each passed as one argument (spaces safe).</param>
    /// <param name="workingDirectory">Working directory.</param>
    /// <param name="timeout">Kill after this long.</param>
    /// <param name="cancellationToken">Kill when cancelled.</param>
    /// <param name="recordAsWorker">Record the process in <see cref="RunningWorkerFile"/> while it runs.</param>
    /// <param name="onLine">Called with each output line as it arrives; its exceptions are swallowed.</param>
    /// <returns>The outcome.</returns>
    public static async Task<ProcessOutcome> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool recordAsWorker = false,
        Action<string>? onLine = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                if (output.Length < MaxOutputChars)
                {
                    output.AppendLine(line);
                }
            }

            if (onLine is not null)
            {
                try
                {
                    onLine(line);
                }
#pragma warning disable CA1031 // A progress callback must never break output reading.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }

        string Collected()
        {
            lock (output)
            {
                return output.ToString();
            }
        }

        var watch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new ProcessOutcome(ProcessEnd.StartFailed, null, ex.Message, watch.Elapsed);
        }

        // The OS ends the whole tree if Jellyfin dies, so no run can outlive us and race startup recovery.
        using var job = KillOnCloseJob.TryCreate();
        job?.TryAdd(process);
        if (recordAsWorker)
        {
            RunningWorkerFile.Write(process);
        }

        try
        {
            return await WaitAsync(process, timeout, cancellationToken, watch, Collected).ConfigureAwait(false);
        }
        finally
        {
            if (recordAsWorker)
            {
                RunningWorkerFile.Clear();
            }
        }
    }

    private static async Task<ProcessOutcome> WaitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken, Stopwatch watch, Func<string> collected)
    {
        Func<string> Collected = collected;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            // Flush the async output readers.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return new ProcessOutcome(ProcessEnd.Exited, process.ExitCode, Collected(), watch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            var end = cancellationToken.IsCancellationRequested ? ProcessEnd.Cancelled : ProcessEnd.TimedOut;
            return new ProcessOutcome(end, null, Collected(), watch.Elapsed);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Exiting at the same moment.
        }

        // Wait so files the tree held (the output subtitle) are released before we restore the backup.
        process.WaitForExit(TimeSpan.FromSeconds(30));
    }
}
