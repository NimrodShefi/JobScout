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

    /// <summary>Role titles I am actively looking for, newline-separated, e.g. ".NET developer".
    /// When empty, every title is in scope; when set, a listing whose title matches none of
    /// them is dropped before it costs anything to score.</summary>
    public string DesiredRoles { get; set; } = string.Empty;

    /// <summary>Words or phrases that rule a role out, newline-separated, e.g. "senior".
    /// An exclusion always beats a match in <see cref="DesiredRoles"/>.</summary>
    public string ExcludedRoles { get; set; } = string.Empty;

    public string? CvFileName { get; set; }
    public byte[]? CvBlob { get; set; }

    /// <summary>Text extracted from the CV. Shown in Settings so I can sanity-check it.
    /// Never written to logs.</summary>
    public string? CvText { get; set; }

    public DateTimeOffset? CvUploadedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public IReadOnlyList<string> CityList => SplitLines(Cities);

    public IReadOnlyList<string> DesiredRoleList => SplitLines(DesiredRoles);

    public IReadOnlyList<string> ExcludedRoleList => SplitLines(ExcludedRoles);

    public void SetCities(IEnumerable<string> cities) => Cities = JoinLines(cities);

    public void SetDesiredRoles(IEnumerable<string> roles) => DesiredRoles = JoinLines(roles);

    public void SetExcludedRoles(IEnumerable<string> roles) => ExcludedRoles = JoinLines(roles);

    private static IReadOnlyList<string> SplitLines(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string JoinLines(IEnumerable<string> values) =>
        string.Join('\n', values.Select(v => v.Trim()).Where(v => v.Length > 0));
}
