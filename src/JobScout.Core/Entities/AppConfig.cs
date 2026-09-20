namespace JobScout.Core.Entities;

/// <summary>Single-row table holding the settings I edit in the UI.
/// Secrets never live here - they come from appsettings / user secrets.</summary>
public class AppConfig
{
    /// <summary>Always 1. Enforced by a check constraint.</summary>
    public int Id { get; set; } = 1;

    public decimal MinimumSalary { get; set; }

    public string Currency { get; set; } = "GBP";

    /// <summary>Stored as a newline-separated list; exposed as <see cref="CityList"/>.</summary>
    public string Cities { get; set; } = string.Empty;

    public bool IncludeRemote { get; set; } = true;

    public string? CvFileName { get; set; }
    public byte[]? CvBlob { get; set; }

    /// <summary>Text extracted from the CV. Shown in Settings so I can sanity-check it.
    /// Never written to logs.</summary>
    public string? CvText { get; set; }

    public DateTimeOffset? CvUploadedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public IReadOnlyList<string> CityList =>
        Cities.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void SetCities(IEnumerable<string> cities) =>
        Cities = string.Join('\n', cities.Select(c => c.Trim()).Where(c => c.Length > 0));
}
