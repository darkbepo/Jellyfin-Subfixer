using System.Text.Json;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>The fields we use from SubSync's <c>--report</c> JSON (JOB-FLOW.md, SubSync CLI facts).</summary>
/// <param name="Status"><c>synced</c>, <c>no_match</c>, <c>failed</c>, <c>cancelled</c> or <c>skipped</c>.</param>
/// <param name="Reason">Reason text.</param>
/// <param name="AudioLanguage">Audio language used.</param>
/// <param name="Seconds">Seconds SubSync reports.</param>
/// <param name="SubSyncVersion">SubSync version.</param>
/// <param name="Speech">Top-level <c>speech_recognition</c> (1.3.0 and later); null when absent or when nothing was listened to.</param>
/// <param name="Timings">The item's <c>timings</c> (1.3.0 and later, synced items); null when absent.</param>
public sealed record SubSyncReport(
    string? Status,
    string? Reason,
    string? AudioLanguage,
    double? Seconds,
    string? SubSyncVersion,
    SubSyncSpeech? Speech = null,
    SubSyncTimings? Timings = null)
{
    /// <summary>
    /// Parses a report. The layout is confirmed (S5: live with 1.2.0, and from SubSync 1.3.0's <c>write_report</c>):
    /// a top-level object with <c>items</c>. Older guesses stay accepted: the fields at the top level, the first entry
    /// of a top-level array, or the first entry of <c>results</c>/<c>pairs</c>/<c>files</c>. The 1.3.0 fields
    /// (<c>speech_recognition</c>, <c>timings</c>) are optional, and a malformed one is read as absent.
    /// </summary>
    /// <param name="json">Report text.</param>
    /// <returns>The report, or null when it has no status anywhere.</returns>
    public static SubSyncReport? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var version = Str(root, "subsync_version") ?? Str(root, "version");
            var entry = FindEntry(root);
            if (entry is not { } e)
            {
                return null;
            }

            return new SubSyncReport(
                Str(e, "status")?.Trim().ToLowerInvariant(),
                Str(e, "reason") ?? Str(e, "message"),
                Str(e, "audio_language"),
                Num(e, "seconds"),
                Str(e, "subsync_version") ?? version,
                SpeechOf(root),
                TimingsOf(e));
        }
    }

    private static SubSyncSpeech? SpeechOf(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("speech_recognition", out var sr) || sr.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var device = Str(sr, "device")?.ToLowerInvariant();
        return device is "gpu" or "cpu" ? new SubSyncSpeech(device, Str(sr, "name"), Num(sr, "model_load_seconds")) : null;
    }

    private static SubSyncTimings? TimingsOf(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("timings", out var t) || t.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fromCache = t.TryGetProperty("from_cache", out var c) && c.ValueKind == JsonValueKind.True;
        return new SubSyncTimings(fromCache, Num(t, "read_audio"), Num(t, "listen"), Num(t, "match"));
    }

    private static JsonElement? FindEntry(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.GetArrayLength() > 0 ? root[0] : null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (Str(root, "status") is not null)
        {
            return root;
        }

        foreach (var name in new[] { "results", "pairs", "items", "files" })
        {
            if (root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
            {
                return list[0];
            }
        }

        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

/// <summary>What listened during the run (report <c>speech_recognition</c>, SubSync 1.3.0 and later).</summary>
/// <param name="Device"><c>gpu</c> or <c>cpu</c> (a GPU problem during the run makes it <c>cpu</c>).</param>
/// <param name="Name">Graphics card, or null.</param>
/// <param name="ModelLoadSeconds">How long the speech model took to load, or null.</param>
public sealed record SubSyncSpeech(string Device, string? Name, double? ModelLoadSeconds);

/// <summary>Seconds per step of one item (report <c>timings</c>, SubSync 1.3.0 and later).</summary>
/// <param name="FromCache">The transcript came from SubSync's cache; reading audio and listening did not run.</param>
/// <param name="ReadAudio">Reading the soundtrack, or null.</param>
/// <param name="Listen">Speech recognition, or null.</param>
/// <param name="Match">Placing the lines, or null.</param>
public sealed record SubSyncTimings(bool FromCache, double? ReadAudio, double? Listen, double? Match);

/// <summary>Maps the CLI's exit code and report to a <see cref="WorkerStatus"/>.</summary>
public static class SubSyncOutcome
{
    /// <summary>Setup or repair impossible, or another SubSync uses the folder.</summary>
    public const int ExitUnavailable = 7;

    /// <summary>Unexpected error.</summary>
    public const int ExitUnexpected = 10;

    /// <summary>Decides the outcome. Exit 7 and 10 win over the report; otherwise the report's status decides.</summary>
    /// <param name="exitCode">Process exit code.</param>
    /// <param name="report">Parsed report, or null.</param>
    /// <returns>Status and reason.</returns>
    public static (WorkerStatus Status, string? Reason) Decide(int exitCode, SubSyncReport? report)
    {
        if (exitCode == ExitUnavailable)
        {
            return (WorkerStatus.Unavailable, report?.Reason ?? "SubSync could not run: close any open SubSync window or wait for its setup to finish (exit 7).");
        }

        if (exitCode == ExitUnexpected)
        {
            return (WorkerStatus.Crashed, report?.Reason ?? "SubSync hit an unexpected error (exit 10).");
        }

        switch (report?.Status)
        {
            case "synced":
                return exitCode == 0
                    ? (WorkerStatus.Synced, report.Reason)
                    : (WorkerStatus.Failed, $"SubSync reported synced but exited with {exitCode}.");
            case "no_match":
                return (WorkerStatus.NoMatch, report.Reason ?? "Too few lines matched; is this the right subtitle for the video?");
            case "failed":
            case "skipped":
                return (WorkerStatus.Failed, report.Reason ?? $"SubSync reported {report.Status}.");
            case "cancelled":
                return (WorkerStatus.Cancelled, report.Reason);
            case null:
                return (WorkerStatus.Crashed, $"SubSync exited with {exitCode} and wrote no usable report.");
            default:
                return (WorkerStatus.Failed, $"Unknown SubSync status '{report.Status}' (exit {exitCode}).");
        }
    }
}
