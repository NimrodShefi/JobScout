using JobScout.Core.Models;

namespace JobScout.Core.Abstractions;

/// <summary>A job board. Adding a board means one new class plus its config section.</summary>
public interface IJobBoardProvider
{
    /// <summary>Board name, also stored as the Source on companies and listings.</summary>
    string Name { get; }

    bool IsEnabled { get; }

    /// <summary>Runs one query. An empty list means the board had no matches; null means the
    /// query failed, so the caller can flag it rather than record a silent zero.</summary>
    Task<IReadOnlyList<BoardJobResult>?> SearchAsync(BoardSearchRequest request, CancellationToken ct = default);
}
