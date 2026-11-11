using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>
/// Reads the stage from SubSync's log. SubSync 1.2.0 prints no percentage, only
/// "[1/1] &lt;video&gt;: Reading audio…", "…: Listening…" and "…: Matching lines…" (Listening takes nearly all the time).
/// With piped output it writes these lines only to <c>logs\cli.log</c>, each after a timestamp, not to stdout (verified
/// 2026-11-12: stdout carried the "Speech recognition" line live but no stage line), so <see cref="LogTail"/> follows that
/// file during a run. The page turns the stage plus an estimate into a progress bar (D40).
/// </summary>
public static partial class SubSyncStage
{
    /// <summary>Extracting the soundtrack.</summary>
    public const string ReadingAudio = "reading_audio";

    /// <summary>Speech recognition.</summary>
    public const string Listening = "listening";

    /// <summary>Placing the lines.</summary>
    public const string Matching = "matching";

    /// <summary>Returns the stage code for a stage line (with or without the log's timestamp), or null for any other line.</summary>
    /// <param name="line">One output or log line.</param>
    /// <returns>The stage, or null.</returns>
    public static string? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var match = StagePattern().Match(line);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Value switch
        {
            "Reading audio" => ReadingAudio,
            "Listening" => Listening,
            _ => Matching,
        };
    }

    // "[n/m] <name>: <stage>…" at the start of the line or after the log's "yyyy-MM-dd HH:mm:ss " timestamp.
    [GeneratedRegex(@"^(?:\d{4}-\d\d-\d\d \d\d:\d\d:\d\d )?\s*\[\d+/\d+\] .*: (Reading audio|Listening|Matching lines)(?:…|\.\.\.)?\s*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex StagePattern();
}
