using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace JobScout.Infrastructure.Data;

/// <summary>Used only by "dotnet ef" at design time.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<JobScoutDbContext>
{
    public JobScoutDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<JobScoutDbContext>()
            .UseSqlite("Data Source=./data/jobscout.db")
            .Options;

        return new JobScoutDbContext(options);
    }
}
