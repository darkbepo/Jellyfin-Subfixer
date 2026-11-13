using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>
/// The process behind a <see cref="ServeSession"/>: <c>subsync-cli serve</c> with stdin and stdout, or a scripted
/// stand-in in tests.
/// </summary>
public interface IServeTransport : IDisposable
{
    /// <summary>Gets a task that completes with the exit code once the process has ended (killed included).</summary>
    Task<int> Exited { get; }

    /// <summary>Starts the process. Throws when it can't be started.</summary>
    /// <param name="onLine">Receives each stdout and stderr line.</param>
    void Start(Action<string> onLine);

    /// <summary>Writes one line to stdin and flushes it.</summary>
    /// <param name="line">Line.</param>
    /// <returns>False when stdin is gone (the process ended).</returns>
    bool TryWriteLine(string line);

    /// <summary>Closes stdin: SubSync finishes what it received, then exits 0.</summary>
    void CloseInput();

    /// <summary>Ends the process tree now and waits for it to go, so the files it held are released.</summary>
    void Kill();
}

/// <summary>How starting a session went.</summary>
/// <param name="Session">The ready session, or null.</param>
/// <param name="Failure">
/// Set when the first item must end with this status (<see cref="WorkerStatus.Unavailable"/> for exit 7,
/// <see cref="WorkerStatus.Cancelled"/>, <see cref="WorkerStatus.TimedOut"/>). Null with no session: <c>serve</c> isn't
/// available, so the queue run falls back to one <c>sync</c> per item (D45).
/// </param>
/// <param name="Reason">Why, for the log or the item.</param>
public sealed record SessionStart(ServeSession? Session, WorkerStatus? Failure, string? Reason)
{
    /// <summary>Gets a value indicating whether the queue run should use one <c>sync</c> per item instead.</summary>
    public bool Fallback => Session is null && Failure is null;

    /// <summary>No <c>serve</c>: use the per-item path.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>The result.</returns>
    public static SessionStart NotAvailable(string reason) => new(null, null, reason);
}

/// <summary>One item sent to a <see cref="ServeSession"/>.</summary>
public sealed class ServeItem
{
    private readonly TaskCompletionSource<WorkerResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DateTime> _clock = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _listened;

    internal ServeItem(string id, string output, Action<WorkerProgress>? onProgress, DateTime sentUtc)
    {
        Id = id;
        Output = output;
        OnProgress = onProgress;
        SentUtc = sentUtc;
    }

    /// <summary>Gets the request id (echoed by SubSync in every event about the item).</summary>
    public string Id { get; }

    /// <summary>Gets when the request was sent.</summary>
    public DateTime SentUtc { get; }

    /// <summary>Gets SubSync's result. It never completes if the process ends first; watch <see cref="ServeSession.Exited"/>.</summary>
    public Task<WorkerResult> Result => _result.Task;

    /// <summary>Gets a task that completes when SubSync starts listening to this item (the moment to send the next one).</summary>
    public Task ListeningReached => _listening.Task;

    /// <summary>
    /// Gets when this item's time limit started (D45): its first stage after <c>queued</c>, or the result of the item sent
    /// just before it, whichever comes first; at once when nothing else was in progress when it was sent.
    /// </summary>
    public Task<DateTime> ClockStarted => _clock.Task;

    internal string Output { get; }

    internal Action<WorkerProgress>? OnProgress { get; }

    internal ServeItem? Previous { get; set; }

    internal bool Listened => Volatile.Read(ref _listened) != 0;

    internal void MarkListened() => Volatile.Write(ref _listened, 1);

    internal void ReachedListening() => _listening.TrySetResult();

    internal void StartClock() => _clock.TrySetResult(DateTime.UtcNow);

    internal void Complete(WorkerResult result) => _result.TrySetResult(result);
}

/// <summary>
/// One running <c>subsync-cli serve</c> (README-CLI.md, "serve"; D45). Requests go to stdin as one JSON object per
/// line; events come back on stdout. Lines that don't start with <c>{</c> (setup or repair messages before
/// <c>ready</c>), bad JSON, unknown events and unknown fields are ignored. Thread-safe: events arrive on the
/// output reader's thread.
/// </summary>
public sealed class ServeSession : IDisposable
{
    private const int MaxTailChars = 8 * 1024;

    private readonly IServeTransport _transport;
    private readonly Func<WorkerOptions, bool> _matches;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly List<ServeItem> _pending = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _tail = new();
    private string? _version;
    private bool _disposed;

    private ServeSession(IServeTransport transport, Func<WorkerOptions, bool> matches, ILogger logger)
    {
        _transport = transport;
        _matches = matches;
        _logger = logger;
    }

    /// <summary>Gets the exit code once the process has ended.</summary>
    public Task<int> Exited => _transport.Exited;

    /// <summary>Gets SubSync's version from <c>ready</c>.</summary>
    public string? Version => Volatile.Read(ref _version);

    /// <summary>Gets a value indicating whether this session ended its process on purpose (<see cref="Kill"/>).</summary>
    public bool Killed { get; private set; }

    /// <summary>Gets the items sent that have no result yet.</summary>
    public int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Starts <c>serve</c> and waits for <c>ready</c>. Exit 7 before it is <see cref="WorkerStatus.Unavailable"/>; any other
    /// exit, or a process that can't start, means <c>serve</c> isn't usable (fallback). Waiting longer than the time
    /// limit is <see cref="WorkerStatus.TimedOut"/> (a setup or repair that hangs), and cancelling is
    /// <see cref="WorkerStatus.Cancelled"/>; both end the process.
    /// </summary>
    /// <param name="transport">The process.</param>
    /// <param name="matches">Whether settings still match the ones this session was started with.</param>
    /// <param name="readyTimeout">How long setup may take.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The result.</returns>
    public static async Task<SessionStart> StartAsync(IServeTransport transport, Func<WorkerOptions, bool> matches, TimeSpan readyTimeout, ILogger logger, CancellationToken cancellationToken)
    {
        var session = new ServeSession(transport, matches, logger);
        try
        {
            transport.Start(session.OnLine);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            transport.Dispose();
            return SessionStart.NotAvailable("could not start subsync-cli serve: " + ex.Message);
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(readyTimeout, stop.Token);
        var first = await Task.WhenAny(session._ready.Task, transport.Exited, delay).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);

        if (first == session._ready.Task)
        {
            return new SessionStart(session, null, null);
        }

        if (first == transport.Exited)
        {
            var code = await transport.Exited.ConfigureAwait(false);
            var tail = session.Tail();
            session.Dispose();
            return code == SubSyncOutcome.ExitUnavailable
                ? new SessionStart(null, WorkerStatus.Unavailable, "SubSync could not run: close any open SubSync window or wait for its setup to finish (exit 7).")
                : SessionStart.NotAvailable($"subsync-cli serve exited with {code} before it was ready. Output: {tail}");
        }

        session.Kill();
        session.Dispose();
        return cancellationToken.IsCancellationRequested
            ? new SessionStart(null, WorkerStatus.Cancelled, "Cancelled.")
            : new SessionStart(null, WorkerStatus.TimedOut, $"SubSync did not get ready within {readyTimeout.TotalMinutes:0} minutes.");
    }

    /// <summary>Whether these settings are the ones the session was started with (otherwise start a new one, D45).</summary>
    /// <param name="options">Current settings.</param>
    /// <returns>True when they match.</returns>
    public bool Matches(WorkerOptions options) => _matches(options);

    /// <summary>
    /// Sends one item. Back up the subtitle first: SubSync may start on it at once and writes <paramref name="output"/>
    /// when it is finished.
    /// </summary>
    /// <param name="id">Request id, unique while in progress.</param>
    /// <param name="video">Video.</param>
    /// <param name="subtitle">Subtitle.</param>
    /// <param name="output">File to write, here the subtitle itself.</param>
    /// <param name="onProgress">Each stage SubSync reports for the item.</param>
    /// <param name="subtitleLanguage">The subtitle's language (D50); a Danish one is sent with <c>subtitle_language</c>.</param>
    /// <param name="reference">For a Danish subtitle: the English one to time it by, or null.</param>
    /// <returns>The item.</returns>
    public ServeItem Send(string id, string video, string subtitle, string output, Action<WorkerProgress>? onProgress,
        string subtitleLanguage = Domain.SubtitleLanguage.English, string? reference = null)
    {
        var item = new ServeItem(id, output, onProgress, DateTime.UtcNow);
        lock (_gate)
        {
            item.Previous = _pending.LastOrDefault();
            _pending.Add(item);
        }

        if (item.Previous is null)
        {
            item.StartClock();
        }

        var request = new Dictionary<string, string> { ["id"] = id, ["video"] = video, ["subtitle"] = subtitle, ["output"] = output };
        if (Domain.SubtitleLanguage.Normalize(subtitleLanguage) == Domain.SubtitleLanguage.Danish)
        {
            request["subtitle_language"] = Domain.SubtitleLanguage.Danish;
            if (!string.IsNullOrEmpty(reference))
            {
                request["reference"] = reference;
            }
        }

        var line = JsonSerializer.Serialize(request);
        if (!_transport.TryWriteLine(line))
        {
            // The process is gone; Exited says so and the caller restores the item.
            _logger.LogWarning("Subtitle Fixer could not send {Subtitle} to SubSync: its input is closed", subtitle);
        }

        return item;
    }

    /// <summary>Closes stdin: SubSync finishes every item it received, then exits 0.</summary>
    public void Close() => _transport.CloseInput();

    /// <summary>Ends the process now. Unfinished items are not written; a result already written stays.</summary>
    public void Kill()
    {
        Killed = true;
        _transport.Kill();
    }

    /// <summary>Gets the last plain-text lines SubSync printed, for the log.</summary>
    /// <returns>Text.</returns>
    public string Tail()
    {
        lock (_tail)
        {
            return _tail.ToString().Trim();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _transport.Dispose();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Id(JsonElement e) =>
        e.TryGetProperty("id", out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private void OnLine(string line)
    {
        var text = line.Trim();
        if (!text.StartsWith('{'))
        {
            if (text.Length > 0)
            {
                lock (_tail)
                {
                    if (_tail.Length > MaxTailChars)
                    {
                        _tail.Remove(0, _tail.Length - (MaxTailChars / 2));
                    }

                    _tail.AppendLine(text);
                }
            }

            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            Handle(doc.RootElement, text);
        }
        catch (JsonException)
        {
            // Not ours to understand; never fail the session over a line.
        }
    }

    private void Handle(JsonElement root, string text)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (Str(root, "event"))
        {
            case "ready":
                Volatile.Write(ref _version, Str(root, "subsync_version"));
                _ready.TrySetResult();
                break;
            case "device":
                SpeechRecognition.ObserveDevice(Str(root, "device"), Str(root, "name"));
                break;
            case "warning":
                _logger.LogWarning("SubSync: {Warning}", Str(root, "text"));
                break;
            case "stage":
                OnStage(root);
                break;
            case "result":
                OnResult(root, text);
                break;
        }
    }

    private ServeItem? Find(string? id)
    {
        if (id is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _pending.FirstOrDefault(i => i.Id == id);
        }
    }

    private void OnStage(JsonElement root)
    {
        if (Find(Id(root)) is not { } item)
        {
            return;
        }

        var stage = SubSyncStage.Normalize(Str(root, "stage"));
        int? percent = root.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) && !double.IsNaN(d)
            ? (int)Math.Round(Math.Clamp(d, 0, 100))
            : null;
        if (stage is SubSyncStage.ReadingAudio or SubSyncStage.Listening)
        {
            item.MarkListened();
        }

        if (stage != SubSyncStage.Queued)
        {
            item.StartClock();
        }

        if (stage == SubSyncStage.Listening)
        {
            item.ReachedListening();
        }

        try
        {
            item.OnProgress?.Invoke(new WorkerProgress(stage, percent));
        }
#pragma warning disable CA1031 // A progress callback must never stop the session.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void OnResult(JsonElement root, string text)
    {
        var item = Find(Id(root));
        if (item is null)
        {
            // A request line SubSync couldn't read (id null); every request we send is valid JSON with an id.
            _logger.LogWarning("SubSync refused a request it could not read: {Reason}", Str(root, "reason"));
            return;
        }

        var report = SubSyncReport.Parse(text);
        var (status, reason) = SubSyncOutcome.Decide(0, report);
        if (status is WorkerStatus.Failed or WorkerStatus.Crashed)
        {
            _logger.LogWarning("SubSync {Status} for {Subtitle}: {Reason}", status, item.Output, reason);
        }

        // As with sync --progress: an item that never reached reading audio or listening used a cached transcript.
        var timings = report?.Timings ?? (item.Listened ? null : new SubSyncTimings(true, null, null, null));
        var result = new WorkerResult(
            status,
            status == WorkerStatus.Synced ? item.Output : null,
            reason,
            report?.AudioLanguage,
            report?.Seconds ?? (DateTime.UtcNow - item.SentUtc).TotalSeconds,
            report?.SubSyncVersion ?? Version,
            null,
            timings);

        ServeItem? next;
        lock (_gate)
        {
            _pending.Remove(item);
            next = _pending.FirstOrDefault(i => i.Previous == item);
        }

        next?.StartClock();
        item.Complete(result);
    }
}

/// <summary>The real <see cref="IServeTransport"/>: <c>subsync-cli serve</c> in a kill-on-close Job Object (D29).</summary>
internal sealed class ServeProcess : IServeTransport
{
    private readonly ProcessStartInfo _start;
    private readonly object _inputGate = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private KillOnCloseJob? _job;
    private int _processId;

    public ServeProcess(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        _start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            _start.ArgumentList.Add(argument);
        }
    }

    public Task<int> Exited => _exited.Task;

    public void Start(Action<string> onLine)
    {
        var process = new Process { StartInfo = _start };
        void Line(string? line)
        {
            if (line is null)
            {
                return;
            }

            try
            {
                onLine(line);
            }
#pragma warning disable CA1031 // A line handler must never break output reading.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        _process = process;

        // The OS ends the whole tree if Jellyfin dies, so no SubSync outlives us and races startup recovery.
        _job = KillOnCloseJob.TryCreate();
        _job?.TryAdd(process);
        RunningWorkerFile.Write(process);
        _processId = process.Id;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = WatchAsync(process);
    }

    public bool TryWriteLine(string line)
    {
        lock (_inputGate)
        {
            try
            {
                if (_process is not { } p || p.HasExited)
                {
                    return false;
                }

                p.StandardInput.WriteLine(line);
                p.StandardInput.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    public void CloseInput()
    {
        lock (_inputGate)
        {
            try
            {
                _process?.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    public void Kill()
    {
        if (_process is not { } p)
        {
            return;
        }

        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Exiting at the same moment.
        }

        // Wait so files the tree held (the output subtitles) are released before backups are restored.
        p.WaitForExit(TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        if (_process is { } p)
        {
            if (!_exited.Task.IsCompleted)
            {
                Kill();
            }

            p.Dispose();
        }

        _job?.Dispose();
    }

    private async Task WatchAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            // Flush the async output readers.
            process.WaitForExit();
            _exited.TrySetResult(process.ExitCode);
        }
#pragma warning disable CA1031 // The watcher must always complete Exited.
        catch (Exception)
#pragma warning restore CA1031
        {
            _exited.TrySetResult(-1);
        }
        finally
        {
            RunningWorkerFile.Clear(_processId);
        }
    }
}
