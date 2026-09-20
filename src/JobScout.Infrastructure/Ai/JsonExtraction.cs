using System.Text.Json;

namespace JobScout.Infrastructure.Ai;

/// <summary>Pulls a JSON value out of a model reply that may be wrapped in prose or fences.</summary>
public static class JsonExtraction
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Returns the first balanced JSON object or array in the text, or null.
    /// Handles ```json fences, leading apologies, and trailing commentary.</summary>
    public static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var s = text.Trim();

        // Strip a fenced block if one is present; the content may still need balancing.
        var fence = s.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = s.IndexOf('\n', fence);
            var end = s.IndexOf("```", fence + 3, StringComparison.Ordinal);
            if (start > 0 && end > start)
                s = s[(start + 1)..end].Trim();
        }

        foreach (var (open, close) in new[] { ('{', '}'), ('[', ']') })
        {
            var candidate = Balance(s, open, close);
            if (candidate is not null) return candidate;
        }

        return null;
    }

    /// <summary>Scans for the first opener and returns through its matching closer,
    /// ignoring braces that appear inside strings.</summary>
    private static string? Balance(string s, char open, char close)
    {
        var start = s.IndexOf(open);
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == open) depth++;
            else if (c == close && --depth == 0) return s[start..(i + 1)];
        }

        return null;
    }

    public static T? Deserialise<T>(string? modelText) where T : class
    {
        var json = ExtractJson(modelText);
        if (json is null) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
