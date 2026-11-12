using System.Text.Json;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>One <c>PROGRESS {json}</c> line of <c>sync --progress</c> (SubSync 1.3.0 and later).</summary>
public abstract record SubSyncProgressEvent;

/// <summary>A stage event: what SubSync is doing and, when it can say, how far within that stage.</summary>
/// <param name="Stage">A <see cref="SubSyncStage"/> code; unknown stages are <see cref="SubSyncStage.Working"/>.</param>
/// <param name="Percent">0 to 100 within the stage, or null.</param>
public sealed record StageProgress(string Stage, int? Percent) : SubSyncProgressEvent;

/// <summary>Speech recognition is ready, or switched to the CPU after a GPU problem.</summary>
/// <param name="Device"><c>gpu</c> or <c>cpu</c>.</param>
/// <param name="Name">Graphics card, or null.</param>
public sealed record DeviceProgress(string Device, string? Name) : SubSyncProgressEvent;

/// <summary>An item finished.</summary>
/// <param name="Status">As in the report.</param>
/// <param name="Output">The written file, or null.</param>
public sealed record ItemDoneProgress(string? Status, string? Output) : SubSyncProgressEvent;

/// <summary>
/// Reads SubSync's <c>--progress</c> lines (README-CLI.md, "Progress lines"): <c>PROGRESS </c> followed by one JSON
/// object, between the usual human-readable lines. Anything else, bad JSON, unknown events and unknown fields are
/// ignored, so a line can never fail a run.
/// </summary>
public static class SubSyncProgress
{
    /// <summary>What every progress line starts with.</summary>
    public const string Prefix = "PROGRESS ";

    /// <summary>Parses one stdout line.</summary>
    /// <param name="line">Line.</param>
    /// <returns>The event, or null for any other line.</returns>
    public static SubSyncProgressEvent? Parse(string? line)
    {
        if (line is null || !line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line.AsMemory(Prefix.Length));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            switch (Str(root, "event"))
            {
                case "stage":
                    return new StageProgress(SubSyncStage.Normalize(Str(root, "stage")), Percent(root));
                case "device":
                    var device = Str(root, "device")?.ToLowerInvariant();
                    return device is "gpu" or "cpu" ? new DeviceProgress(device, Str(root, "name")) : null;
                case "item_done":
                    return new ItemDoneProgress(Str(root, "status"), Str(root, "output"));
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? Percent(JsonElement root)
    {
        if (!root.TryGetProperty("percent", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || double.IsNaN(d))
        {
            return null;
        }

        return (int)Math.Round(Math.Clamp(d, 0, 100));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Turns one run's output lines into progress for the job runner. With <c>--progress</c> (1.3.0) it reads the
/// <c>PROGRESS</c> lines, passes each stage on, and takes GPU/CPU from <c>device</c> events at once (D38). For 1.2.0 it
/// reads stage lines (from <c>logs\cli.log</c> through <see cref="LogTail"/>, or stdout).
/// </summary>
internal sealed class ProgressLines
{
    private readonly bool _progressLines;
    private readonly Action<WorkerProgress>? _onProgress;
    private int _listened;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressLines"/> class.
    /// </summary>
    /// <param name="progressLines">The run uses <c>--progress</c>.</param>
    /// <param name="onProgress">Receives each stage, or null.</param>
    public ProgressLines(bool progressLines, Action<WorkerProgress>? onProgress)
    {
        _progressLines = progressLines;
        _onProgress = onProgress;
    }

    /// <summary>
    /// Gets a value indicating whether SubSync reached reading audio or listening. With <c>--progress</c>, a run that
    /// did not used a cached transcript (it goes straight from <c>queued</c> to <c>matching</c>).
    /// </summary>
    public bool Listened => Volatile.Read(ref _listened) != 0;

    /// <summary>Handles one line.</summary>
    /// <param name="line">Line.</param>
    public void OnLine(string line)
    {
        if (!_progressLines)
        {
            if (_onProgress is not null && SubSyncStage.Parse(line) is { } stage)
            {
                _onProgress(new WorkerProgress(stage, null));
            }

            return;
        }

        switch (SubSyncProgress.Parse(line))
        {
            case StageProgress s:
                if (s.Stage is SubSyncStage.ReadingAudio or SubSyncStage.Listening)
                {
                    Volatile.Write(ref _listened, 1);
                }

                _onProgress?.Invoke(new WorkerProgress(s.Stage, s.Percent));
                break;
            case DeviceProgress d:
                SpeechRecognition.ObserveDevice(d.Device, d.Name);
                break;
        }
    }
}
