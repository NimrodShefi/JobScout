using JobScout.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace JobScout.Infrastructure.Data;

public class JobScoutDbContext(DbContextOptions<JobScoutDbContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<JobListing> JobListings => Set<JobListing>();
    public DbSet<JobApplication> Applications => Set<JobApplication>();
    public DbSet<ApplicationStatusChange> ApplicationStatusChanges => Set<ApplicationStatusChange>();
    public DbSet<SuggestedStatusUpdate> SuggestedStatusUpdates => Set<SuggestedStatusUpdate>();
    public DbSet<AppConfig> AppConfigs => Set<AppConfig>();
    public DbSet<Industry> Industries => Set<Industry>();
    public DbSet<JobRunLog> JobRunLogs => Set<JobRunLog>();
    public DbSet<EmailCheckpoint> EmailCheckpoints => Set<EmailCheckpoint>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.ApplyConfigurationsFromAssembly(typeof(JobScoutDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        base.ConfigureConventions(builder);

        // SQLite has no native date type and refuses to ORDER BY a DateTimeOffset. Storing
        // UTC ticks as an integer makes ordering and range filters work in the database
        // rather than forcing every query to sort client-side.
        //
        // Every timestamp in this app is UTC, so collapsing the offset loses nothing; a
        // value that arrives with an offset (an email's Date header) is normalised to UTC.
        builder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

    /// <summary>DateTimeOffset as UTC ticks. Order-preserving, which is the whole point.</summary>
    private sealed class UtcTicksConverter()
        : ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
}
