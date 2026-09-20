using System.Text.RegularExpressions;

namespace JobScout.Core.Services;

/// <summary>Matches job titles against the role terms configured in Settings.
///
/// Matching is on whole words, not raw substrings, so excluding "lead" does not also throw
/// away "Leadership Development Programme". Punctuation is folded away first, so ".NET
/// Developer" matches "Senior .Net Developer", "Developer - .NET" and "dotNET developer"
/// alike without the user having to think about spelling.</summary>
public static partial class RoleMatcher
{
    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")] private static partial Regex NonAlphanumeric();

    /// <summary>True when any term appears in the title as a run of whole words.
    /// An empty term list matches nothing - callers decide what that means.</summary>
    public static bool MatchesAny(string? title, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0 || string.IsNullOrWhiteSpace(title)) return false;

        var titleWords = Words(title);
        if (titleWords.Length == 0) return false;

        foreach (var term in terms)
        {
            var termWords = Words(term);
            if (termWords.Length == 0) continue;

            if (ContainsSequence(titleWords, termWords))
                return true;
        }

        return false;
    }

    /// <summary>The first term that matches, for explaining a decision back to the user.</summary>
    public static string? FirstMatch(string? title, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0 || string.IsNullOrWhiteSpace(title)) return null;

        var titleWords = Words(title);
        if (titleWords.Length == 0) return null;

        foreach (var term in terms)
        {
            var termWords = Words(term);
            if (termWords.Length > 0 && ContainsSequence(titleWords, termWords))
                return term;
        }

        return null;
    }

    /// <summary>"Senior .NET Developer" becomes ["senior", "net", "developer"].</summary>
    private static string[] Words(string value) =>
        NonAlphanumeric()
            .Replace(value.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether <paramref name="needle"/> appears as consecutive words in
    /// <paramref name="haystack"/>.</summary>
    private static bool ContainsSequence(string[] haystack, string[] needle)
    {
        if (needle.Length > haystack.Length) return false;

        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var matched = true;

            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (!string.Equals(haystack[start + offset], needle[offset], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched) return true;
        }

        return false;
    }
}
