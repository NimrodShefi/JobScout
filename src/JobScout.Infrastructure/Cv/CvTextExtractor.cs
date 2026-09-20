using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using JobScout.Core.Abstractions;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace JobScout.Infrastructure.Cv;

/// <summary>Pulls plain text out of a PDF or DOCX CV. The extracted text is shown in
/// Settings so it can be checked by eye; it is never written to the logs.</summary>
public sealed partial class CvTextExtractor(ILogger<CvTextExtractor> logger) : ICvTextExtractor
{
    [GeneratedRegex(@"[ \t]{2,}")] private static partial Regex RunsOfSpaces();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex ExcessBlankLines();

    private static readonly string[] Supported = [".pdf", ".docx"];

    public bool CanHandle(string fileName) =>
        Supported.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    public async Task<string> ExtractAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        // PdfPig and OpenXml both want a seekable stream.
        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var text = extension switch
        {
            ".pdf" => ExtractPdf(buffer),
            ".docx" => ExtractDocx(buffer),
            _ => throw new NotSupportedException(
                $"Cannot read '{extension}' CVs. Supported formats: {string.Join(", ", Supported)}."),
        };

        var cleaned = Clean(text);

        // Length only - the content itself must not reach the log files.
        logger.LogInformation("Extracted {Chars} characters from the uploaded CV ({Extension})",
            cleaned.Length, extension);

        return cleaned;
    }

    private static string ExtractPdf(Stream stream)
    {
        using var document = PdfDocument.Open(stream);
        var sb = new StringBuilder();

        foreach (var page in document.GetPages())
        {
            // The default word extractor keeps reading order better than raw page text
            // on the two-column layouts CVs often use.
            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);

            double? lastBaseline = null;
            foreach (var word in words)
            {
                var baseline = Math.Round(word.BoundingBox.Bottom, 1);

                if (lastBaseline is not null && Math.Abs(baseline - lastBaseline.Value) > 2)
                    sb.Append('\n');
                else if (sb.Length > 0)
                    sb.Append(' ');

                sb.Append(word.Text);
                lastBaseline = baseline;
            }

            sb.Append("\n\n");
        }

        return sb.ToString();
    }

    private static string ExtractDocx(Stream stream)
    {
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;

        var sb = new StringBuilder();

        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            var line = paragraph.InnerText;
            if (!string.IsNullOrWhiteSpace(line))
                sb.AppendLine(line.Trim());
            else
                sb.AppendLine();
        }

        // Tables are common in CV layouts and their text lives outside paragraphs in places.
        foreach (var table in body.Descendants<Table>())
        {
            foreach (var row in table.Descendants<TableRow>())
            {
                var cells = row.Descendants<TableCell>()
                    .Select(c => c.InnerText.Trim())
                    .Where(t => t.Length > 0);

                var line = string.Join(" | ", cells);
                if (line.Length > 0) sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    private static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var normalised = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalised = RunsOfSpaces().Replace(normalised, " ");

        var lines = normalised.Split('\n').Select(l => l.Trim());
        normalised = string.Join('\n', lines);

        return ExcessBlankLines().Replace(normalised, "\n\n").Trim();
    }
}
