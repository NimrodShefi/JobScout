using JobScout.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace JobScout.Tests.TestSupport;

/// <summary>A real SQLite database held in memory, so the tests exercise the same provider,
/// the same value converters and the same unique indexes as the app does.
///
/// The connection is kept open for the lifetime of the fixture - closing the last connection
/// to an in-memory SQLite database destroys it.</summary>
public sealed class TestDatabase : IDbContextFactory<JobScoutDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JobScoutDbContext> _options;

    public TestDatabase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<JobScoutDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public JobScoutDbContext CreateDbContext() => new(_options);

    public void Dispose()
    {
        _connection.Dispose();
    }
}
