namespace JobScout.Core.Abstractions;

/// <summary>Extracts plain text from an uploaded CV.</summary>
public interface ICvTextExtractor
{
    bool CanHandle(string fileName);

    /// <summary>Returns extracted text. Callers must not log the result.</summary>
    Task<string> ExtractAsync(Stream content, string fileName, CancellationToken ct = default);
}
