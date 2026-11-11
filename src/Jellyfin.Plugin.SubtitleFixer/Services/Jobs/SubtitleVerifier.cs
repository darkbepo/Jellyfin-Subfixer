using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleFixer.Services.Jobs;

/// <summary>Checks SubSync's output before the backup is discarded (JOB-FLOW.md step 4).</summary>
public static partial class SubtitleVerifier
{
    /// <summary>Largest file we read to check; real episode subtitles are far smaller.</summary>
    private const long MaxBytes = 20 * 1024 * 1024;

    /// <summary>Whether the file exists, is non-empty, and parses as SRT with at least one cue.</summary>
    /// <param name="path">Subtitle path.</param>
    /// <param name="reason">Why not.</param>
    /// <returns>True when valid.</returns>
    public static bool IsValidSrt(string path, out string? reason)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            reason = "invalid output: file missing";
            return false;
        }

        if (info.Length == 0)
        {
            reason = "invalid output: empty file";
            return false;
        }

        if (info.Length > MaxBytes)
        {
            reason = "invalid output: file too large";
            return false;
        }

        string text;
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (CueTiming().IsMatch(lines[i]) && !string.IsNullOrWhiteSpace(lines[i + 1]))
            {
                reason = null;
                return true;
            }
        }

        reason = "invalid output: no subtitle cues";
        return false;
    }

    [GeneratedRegex(@"^\s*\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}\s*-->\s*\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}", RegexOptions.CultureInvariant)]
    private static partial Regex CueTiming();
}
