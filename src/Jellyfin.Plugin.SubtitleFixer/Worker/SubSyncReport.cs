using System.Text.Json;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>The fields we use from SubSync's <c>--report</c> JSON (JOB-FLOW.md, SubSync CLI facts).</summary>
/// <param name="Status"><c>synced</c>, <c>no_match</c>, <c>failed</c>, <c>cancelled</c> or <c>skipped</c>.</param>
/// <param name="Reason">Reason text.</param>
/// <param name="AudioLanguage">Audio language used.</param>
/// <param name="Seconds">Seconds SubSync reports.</param>
/// <param name="SubSyncVersion">SubSync version.</param>
public sealed record SubSyncReport(string? Status, string? Reason, string? AudioLanguage, double? Seconds, string? SubSyncVersion)
{
    /// <summary>
    /// Parses a report. The exact layout is unverified (spike S5), so this accepts the fields at the top level,
    /// or the first entry of a top-level array, or the first entry of an array property (results, pairs, items, files).
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
                Str(e, "subsync_version") ?? version);
        }
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
