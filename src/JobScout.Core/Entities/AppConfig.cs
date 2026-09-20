namespace JobScout.Core.Entities;

/// <summary>Single-row table holding the settings I edit in the UI.
/// Secrets never live here - they come from appsettings / user secrets.</summary>
public class AppConfig
{
    /// <summary>Always 1. Enforced by a check constraint.</summary>
    public int Id { get; set; } = 1;

    public decimal MinimumSalary { get; set; }

    public string Currency { get; set; } = "GBP";

    /// <summary>Comma-separated, e.g. "London, Manchester"; exposed as <see cref="CityList"/>.</summary>
    public string Cities { get; set; } = string.Empty;

    public bool IncludeRemote { get; set; } = true;

    /// <summary>Role titles I am actively looking for, comma-separated, e.g. ".NET developer".
    /// When empty, every title is in scope; when set, a listing whose title matches none of
    /// them is dropped before it costs anything to score.</summary>
    public string DesiredRoles { get; set; } = string.Empty;

    /// <summary>Words or phrases that rule a role out, comma-separated, e.g. "senior".
    /// An exclusion always beats a match in <see cref="DesiredRoles"/>.</summary>
    public string ExcludedRoles { get; set; } = string.Empty;

    public string? CvFileName { get; set; }
    public byte[]? CvBlob { get; set; }

    /// <summary>Text extracted from the CV. Shown in Settings so I can sanity-check it.
    /// Never written to logs.</summary>
    public string? CvText { get; set; }

    public DateTimeOffset? CvUploadedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public IReadOnlyList<string> CityList => ParseList(Cities);

    public IReadOnlyList<string> DesiredRoleList => ParseList(DesiredRoles);

    public IReadOnlyList<string> ExcludedRoleList => ParseList(ExcludedRoles);

    public void SetCities(IEnumerable<string> cities) => Cities = FormatList(cities);

    public void SetDesiredRoles(IEnumerable<string> roles) => DesiredRoles = FormatList(roles);

    public void SetExcludedRoles(IEnumerable<string> roles) => ExcludedRoles = FormatList(roles);

    /// <summary>Splits a user-entered list. Commas are the separator, but newlines are
    /// accepted too: it costs nothing, it means a pasted column of values works, and it
    /// keeps rows written before these fields were comma-separated readable.</summary>
    public static IReadOnlyList<string> ParseList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', '\n', '\r'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>How a list is stored and shown back: comma separated, one space after each
    /// comma so it reads as a sentence.</summary>
    public static string FormatList(IEnumerable<string> values) =>
        string.Join(", ", values.Select(v => v.Trim()).Where(v => v.Length > 0));
}
