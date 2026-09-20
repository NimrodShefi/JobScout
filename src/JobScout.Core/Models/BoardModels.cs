namespace JobScout.Core.Models;

/// <summary>A query against a job board: one industry term, one city.</summary>
public sealed record BoardSearchRequest
{
    public required string Query { get; init; }

    /// <summary>Null means a remote-only or nationwide search.</summary>
    public string? City { get; init; }

    public decimal? MinimumSalary { get; init; }
    public int MaxResults { get; init; } = 50;
}

/// <summary>One advert as a board reports it.</summary>
public sealed record BoardJobResult
{
    public required string CompanyName { get; init; }
    public required string Title { get; init; }
    public string? Location { get; init; }
    public required string Url { get; init; }
    public string? Description { get; init; }
    public decimal? SalaryMin { get; init; }
    public decimal? SalaryMax { get; init; }
    public string? SalaryCurrency { get; init; }
    public DateTimeOffset? PostedAt { get; init; }
    public bool IsRemote { get; init; }

    /// <summary>Board page for the company, when the board exposes one.</summary>
    public string? CompanyBoardUrl { get; init; }
}
