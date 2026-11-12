using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>
/// Stage codes, and the stage reader for SubSync 1.2.0. (1.3.0 and later print these codes themselves in
/// <c>--progress</c> lines, <see cref="SubSyncProgress"/>.) SubSync 1.2.0 prints no percentage, only
/// "[1/1] &lt;video&gt;: Reading audio…", "…: Listening…" and "…: Matching lines…" (Listening takes nearly all the time).
/// With piped output it writes these lines only to <c>logs\cli.log</c>, each after a timestamp, not to stdout (verified
/// 2026-11-12: stdout carried the "Speech recognition" line live but no stage line), so <see cref="LogTail"/> follows that
/// file during a run. The page turns the stage plus an estimate into a progress bar (D40).
/// </summary>
public static partial class SubSyncStage
{
    /// <summary>Waiting to start (1.3.0 <c>--progress</c> only).</summary>
    public const string Queued = "queued";

    /// <summary>Extracting the soundtrack.</summary>
    public const string ReadingAudio = "reading_audio";

    /// <summary>Speech recognition.</summary>
    public const string Listening = "listening";

    /// <summary>Placing the lines.</summary>
    public const string Matching = "matching";

    /// <summary>The existing result is open in another program (1.3.0 <c>--progress</c> only, rare).</summary>
    public const string WaitingForFile = "waiting_for_file";

    /// <summary>A stage this plugin doesn't know (a newer SubSync); shown as working.</summary>
    public const string Working = "working";

    /// <summary>Returns a known stage code as is, and <see cref="Working"/> for anything else.</summary>
    /// <param name="code">Stage code from SubSync.</param>
    /// <returns>A known code.</returns>
    public static string Normalize(string? code) => code switch
    {
        Queued or ReadingAudio or Listening or Matching or WaitingForFile => code,
        _ => Working,
    };

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
