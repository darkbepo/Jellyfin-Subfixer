namespace Jellyfin.Plugin.SubtitleFixer.Bazarr;

/// <summary>One Jellyfin-prefix to Bazarr-prefix mapping.</summary>
/// <param name="From">Prefix as Jellyfin sees it.</param>
/// <param name="To">Prefix as Bazarr sees it.</param>
public sealed record BazarrPathMapping(string From, string To);

/// <summary>Result of parsing the <c>BazarrPathMappings</c> setting.</summary>
/// <param name="Mappings">Valid mappings.</param>
/// <param name="Errors">One message per bad line, with its line number.</param>
public sealed record BazarrPathMappingParseResult(IReadOnlyList<BazarrPathMapping> Mappings, IReadOnlyList<string> Errors);

/// <summary>
/// Translates Jellyfin file paths to the paths Bazarr reports, like Bazarr's own path mapping (BAZARR-PLAN.md).
/// Prefixes match whole folder names only and ignore case, because the server is Windows. The longest matching
/// prefix wins. The rest of the path takes the separator style of the Bazarr prefix.
/// </summary>
public sealed class BazarrPathMapper
{
    /// <summary>Separator between the two prefixes on a setting line.</summary>
    public const string Arrow = "=>";

    private readonly IReadOnlyList<BazarrPathMapping> _mappings;

    /// <summary>
    /// Initializes a new instance of the <see cref="BazarrPathMapper"/> class.
    /// </summary>
    /// <param name="mappings">Mappings; order does not matter.</param>
    public BazarrPathMapper(IEnumerable<BazarrPathMapping> mappings)
    {
        _mappings = mappings.OrderByDescending(m => TrimEnd(m.From).Length).ToList();
    }

    /// <summary>Gets the mappings, longest prefix first.</summary>
    public IReadOnlyList<BazarrPathMapping> Mappings => _mappings;

    /// <summary>Parses the setting text: one <c>from =&gt; to</c> per line. Blank lines and lines starting with <c>#</c> are skipped.</summary>
    /// <param name="text">Setting value.</param>
    /// <returns>Mappings and errors.</returns>
    public static BazarrPathMappingParseResult Parse(string? text)
    {
        var mappings = new List<BazarrPathMapping>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new BazarrPathMappingParseResult(mappings, errors);
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var arrow = line.IndexOf(Arrow, StringComparison.Ordinal);
            if (arrow < 0 || line.IndexOf(Arrow, arrow + Arrow.Length, StringComparison.Ordinal) >= 0)
            {
                errors.Add($"Line {i + 1}: write it as 'jellyfin path {Arrow} bazarr path'.");
                continue;
            }

            var from = line[..arrow].Trim();
            var to = line[(arrow + Arrow.Length)..].Trim();
            if (from.Length == 0 || to.Length == 0)
            {
                errors.Add($"Line {i + 1}: both paths are needed.");
                continue;
            }

            mappings.Add(new BazarrPathMapping(from, to));
        }

        return new BazarrPathMappingParseResult(mappings, errors);
    }

    /// <summary>Builds a mapper from the setting text, ignoring bad lines.</summary>
    /// <param name="text">Setting value.</param>
    /// <returns>The mapper.</returns>
    public static BazarrPathMapper FromSetting(string? text) => new(Parse(text).Mappings);

    /// <summary>Compares a Jellyfin path and a Bazarr path the same way <see cref="ToBazarr"/> matches prefixes.</summary>
    /// <param name="a">First path.</param>
    /// <param name="b">Second path.</param>
    /// <returns>Whether they name the same file.</returns>
    public static bool SamePath(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Maps a Jellyfin path to Bazarr's view of it. Unmapped paths come back unchanged.</summary>
    /// <param name="jellyfinPath">Path as Jellyfin sees it.</param>
    /// <returns>Path as Bazarr should see it.</returns>
    public string ToBazarr(string jellyfinPath)
    {
        ArgumentNullException.ThrowIfNull(jellyfinPath);
        var path = Normalize(jellyfinPath);
        foreach (var m in _mappings)
        {
            var from = Normalize(TrimEnd(m.From));
            if (!path.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Whole folder names only: "D:/TV" must not match "D:/TV Shows".
            if (path.Length > from.Length && !from.EndsWith('/') && path[from.Length] != '/')
            {
                continue;
            }

            var rest = path[from.Length..].TrimStart('/');
            var to = TrimEnd(m.To);
            var separator = m.To.Contains('\\', StringComparison.Ordinal) && !m.To.Contains('/', StringComparison.Ordinal) ? '\\' : '/';
            if (separator == '\\')
            {
                rest = rest.Replace('/', '\\');
            }

            return rest.Length == 0 ? to : to + separator + rest;
        }

        return jellyfinPath;
    }

    private static string Normalize(string path) => TrimEnd(path.Replace('\\', '/'));

    private static string TrimEnd(string path)
    {
        // Keep a bare root ("/" or "C:\") usable as a prefix.
        var trimmed = path.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? path[..Math.Min(1, path.Length)] : trimmed;
    }
}
