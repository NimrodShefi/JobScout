using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JobScout.Core.Services;

/// <summary>Turns display names into a stable key so the same employer discovered from
/// different sources collapses onto one row.</summary>
public static partial class CompanyNameNormaliser
{
    private static readonly string[] Suffixes =
    [
        "limited", "ltd", "llc", "llp", "plc", "inc", "incorporated", "corp", "corporation",
        "co", "company", "gmbh", "sa", "sas", "bv", "nv", "ab", "oy", "as", "pty", "group",
    ];

    [GeneratedRegex(@"[^\p{L}\p{Nd}\s]")] private static partial Regex NonAlphanumeric();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    public static string Normalise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var s = StripDiacritics(name).ToLowerInvariant();
        s = s.Replace('&', ' ').Replace('+', ' ');
        s = NonAlphanumeric().Replace(s, " ");
        s = Whitespace().Replace(s, " ").Trim();

        // Peel legal suffixes off the end, repeatedly: "acme group ltd" -> "acme".
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count > 1 && Suffixes.Contains(parts[^1]))
            parts.RemoveAt(parts.Count - 1);

        return parts.Count == 0 ? s : string.Join(' ', parts);
    }

    private static string StripDiacritics(string input)
    {
        var decomposed = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
