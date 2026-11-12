namespace Jellyfin.Plugin.SubtitleFixer.Domain;

/// <summary>
/// The letter picker on a library page (D47): <c>A</c> to <c>Z</c> keeps the series or movies whose sort name starts
/// with that letter, <c>#</c> keeps those that sort before <c>A</c> (digits and punctuation). Jellyfin's sort name drops
/// a leading article ("The Acolyte" sorts as "acolyte") and pads numbers, so the groups match the list's own order.
/// </summary>
public static class LetterFilter
{
    /// <summary>The value for names that sort before A.</summary>
    public const string Other = "#";

    /// <summary>Reads the <c>startsWith</c> query value.</summary>
    /// <param name="value">Raw value; null or empty means no filter.</param>
    /// <param name="letter">Upper-case <c>A</c>–<c>Z</c>, <see cref="Other"/>, or null for no filter.</param>
    /// <returns>False when the value is not one of those.</returns>
    public static bool TryParse(string? value, out string? letter)
    {
        letter = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (value == Other)
        {
            letter = Other;
            return true;
        }

        if (value.Length == 1 && char.IsAsciiLetter(value[0]))
        {
            letter = char.ToUpperInvariant(value[0]).ToString();
            return true;
        }

        return false;
    }

    /// <summary>Whether a sort name belongs to the letter (for the test library; Jellyfin filters in its query).</summary>
    /// <param name="sortName">Sort name.</param>
    /// <param name="letter">A parsed letter, or null.</param>
    /// <returns>True when it matches.</returns>
    public static bool Matches(string sortName, string? letter)
    {
        if (letter is null)
        {
            return true;
        }

        var name = sortName.TrimStart();
        if (letter == Other)
        {
            return string.Compare(name, "a", StringComparison.OrdinalIgnoreCase) < 0;
        }

        return name.StartsWith(letter, StringComparison.OrdinalIgnoreCase);
    }
}
