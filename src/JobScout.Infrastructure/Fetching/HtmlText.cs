using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace JobScout.Infrastructure.Fetching;

/// <summary>Turns HTML into the visible text we feed the AI, and judges whether a page
/// was really rendered by JavaScript.</summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"[ \t\f\v]+")] private static partial Regex HorizontalSpace();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex ExcessBlankLines();

    private static readonly HtmlParser Parser = new();

    /// <summary>Markers that mean "this shell is filled in by a client-side framework".</summary>
    private static readonly string[] SpaMarkers =
    [
        "id=\"root\"", "id='root'",
        "id=\"app\"", "id='app'",
        "id=\"__next\"", "data-reactroot",
        "ng-app", "ng-version",
        "data-server-rendered",
        "window.__NUXT__", "window.__INITIAL_STATE__",
        "<app-root", "v-cloak",
    ];

    /// <summary>Words that suggest the HTML really does contain adverts already.</summary>
    private static readonly string[] JobWords =
    [
        "job", "vacanc", "career", "opening", "position", "role", "apply", "hiring", "we are looking",
    ];

    public static string ExtractText(string html, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var document = Parser.ParseDocument(html);

        foreach (var node in document.QuerySelectorAll("script, style, noscript, svg, template, iframe"))
            node.Remove();

        var body = document.Body;
        var text = body?.TextContent ?? document.TextContent ?? string.Empty;

        text = HorizontalSpace().Replace(text, " ");

        var sb = new StringBuilder(Math.Min(text.Length, maxChars));
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) sb.Append(trimmed);
            sb.Append('\n');
        }

        var result = ExcessBlankLines().Replace(sb.ToString(), "\n\n").Trim();
        return result.Length <= maxChars ? result : result[..maxChars];
    }

    /// <summary>Also pulls out anchor hrefs, so the AI can link the titles it finds.
    /// Careers pages are mostly links, and plain text loses them.</summary>
    public static string ExtractTextWithLinks(string html, string baseUrl, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var document = Parser.ParseDocument(html);

        foreach (var node in document.QuerySelectorAll("script, style, noscript, svg, template, iframe"))
            node.Remove();

        // Inline each link's target next to its text so the model can pair them up.
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href) ||
                href.StartsWith('#') ||
                href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolved = Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) &&
                           Uri.TryCreate(b, href, out var combined)
                ? combined.ToString()
                : href;

            anchor.TextContent = $"{anchor.TextContent.Trim()} <{resolved}>";
        }

        return ExtractText(document.DocumentElement.OuterHtml, maxChars);
    }

    /// <summary>True when the HTML looks like an empty SPA shell: too little visible text,
    /// framework mount markers, and no job-ish words.</summary>
    public static bool LooksJavaScriptRendered(string html, string text, int minTextLength)
    {
        if (string.IsNullOrWhiteSpace(html)) return true;

        var textLength = text?.Trim().Length ?? 0;

        // Plenty of text and it mentions jobs - take it at face value.
        if (textLength >= minTextLength && ContainsJobWords(text!))
            return false;

        if (textLength < minTextLength / 4)
            return true;

        var hasSpaMarker = SpaMarkers.Any(m => html.Contains(m, StringComparison.OrdinalIgnoreCase));

        if (textLength < minTextLength && hasSpaMarker)
            return true;

        // Long enough, but nothing job-like on a page we fetched as a careers page.
        return textLength < minTextLength && !ContainsJobWords(text ?? string.Empty);
    }

    private static bool ContainsJobWords(string text) =>
        JobWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
}
