using JobScout.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JobScout.Infrastructure.Data.Configurations;

public class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> e)
    {
        e.ToTable("Companies");
        e.HasKey(x => x.Id);
        e.Property(x => x.Name).IsRequired().HasMaxLength(300);
        e.Property(x => x.NormalisedName).IsRequired().HasMaxLength(300);
        e.Property(x => x.Url).HasMaxLength(1000);
        e.Property(x => x.BoardUrl).HasMaxLength(1000);
        e.Property(x => x.Source).IsRequired().HasMaxLength(100);
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);

        e.HasIndex(x => x.NormalisedName).IsUnique();
        e.HasIndex(x => x.Status);

        e.Ignore(x => x.NeedsCareersUrl);
    }
}

public class JobListingConfiguration : IEntityTypeConfiguration<JobListing>
{
    public void Configure(EntityTypeBuilder<JobListing> e)
    {
        e.ToTable("JobListings");
        e.HasKey(x => x.Id);
        e.Property(x => x.Title).IsRequired().HasMaxLength(500);
        e.Property(x => x.Location).HasMaxLength(300);
        e.Property(x => x.Url).IsRequired().HasMaxLength(1000);
        e.Property(x => x.Source).IsRequired().HasMaxLength(100);
        e.Property(x => x.SalaryCurrency).HasMaxLength(10);
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        e.Property(x => x.SalaryMin).HasPrecision(18, 2);
        e.Property(x => x.SalaryMax).HasPrecision(18, 2);
        e.Property(x => x.ScoredContentHash).HasMaxLength(64);

        // Dedupe key.
        e.HasIndex(x => x.Url).IsUnique();
        e.HasIndex(x => x.Score);
        e.HasIndex(x => x.Status);
        e.HasIndex(x => x.FoundAt);

        e.HasOne(x => x.Company)
            .WithMany(c => c.Listings)
            .HasForeignKey(x => x.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class JobApplicationConfiguration : IEntityTypeConfiguration<JobApplication>
{
    public void Configure(EntityTypeBuilder<JobApplication> e)
    {
        e.ToTable("Applications");
        e.HasKey(x => x.Id);
        e.Property(x => x.CompanyName).IsRequired().HasMaxLength(300);
        e.Property(x => x.Title).IsRequired().HasMaxLength(500);
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);

        e.HasIndex(x => x.Status);
        e.HasIndex(x => x.AppliedAt);

        e.Ignore(x => x.IsOpen);

        // Keep the application if the listing is removed.
        e.HasOne(x => x.JobListing)
            .WithMany()
            .HasForeignKey(x => x.JobListingId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class ApplicationStatusChangeConfiguration : IEntityTypeConfiguration<ApplicationStatusChange>
{
    public void Configure(EntityTypeBuilder<ApplicationStatusChange> e)
    {
        e.ToTable("ApplicationStatusChanges");
        e.HasKey(x => x.Id);
        e.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
        e.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20);
        e.Property(x => x.Source).HasConversion<string>().HasMaxLength(30);
        e.Property(x => x.Note).HasMaxLength(500);

        e.HasOne(x => x.JobApplication)
            .WithMany(a => a.History)
            .HasForeignKey(x => x.JobApplicationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class SuggestedStatusUpdateConfiguration : IEntityTypeConfiguration<SuggestedStatusUpdate>
{
    public void Configure(EntityTypeBuilder<SuggestedStatusUpdate> e)
    {
        e.ToTable("SuggestedStatusUpdates");
        e.HasKey(x => x.Id);
        e.Property(x => x.SuggestedStatus).HasConversion<string>().HasMaxLength(20);
        e.Property(x => x.Rationale).HasMaxLength(500);
        e.Property(x => x.EmailFrom).HasMaxLength(320);
        e.Property(x => x.EmailSubject).HasMaxLength(500);

        e.HasIndex(x => x.IsResolved);

        e.HasOne(x => x.JobApplication)
            .WithMany(a => a.Suggestions)
            .HasForeignKey(x => x.JobApplicationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class AppConfigConfiguration : IEntityTypeConfiguration<AppConfig>
{
    public void Configure(EntityTypeBuilder<AppConfig> e)
    {
        e.ToTable("AppConfig", t => t.HasCheckConstraint("CK_AppConfig_SingleRow", "[Id] = 1"));
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.Currency).IsRequired().HasMaxLength(10);
        e.Property(x => x.MinimumSalary).HasPrecision(18, 2);
        e.Property(x => x.CvFileName).HasMaxLength(300);

        e.Ignore(x => x.CityList);
        e.Ignore(x => x.DesiredRoleList);
        e.Ignore(x => x.ExcludedRoleList);
    }
}

public class IndustryConfiguration : IEntityTypeConfiguration<Industry>
{
    public void Configure(EntityTypeBuilder<Industry> e)
    {
        e.ToTable("Industries");
        e.HasKey(x => x.Id);
        e.Property(x => x.Name).IsRequired().HasMaxLength(200);
        e.HasIndex(x => x.Name).IsUnique();
    }
}

public class JobRunLogConfiguration : IEntityTypeConfiguration<JobRunLog>
{
    public void Configure(EntityTypeBuilder<JobRunLog> e)
    {
        e.ToTable("JobRunLogs");
        e.HasKey(x => x.Id);
        e.Property(x => x.JobName).IsRequired().HasMaxLength(100);
        e.Property(x => x.Summary).HasMaxLength(2000);
        e.Property(x => x.Error).HasMaxLength(2000);

        e.HasIndex(x => new { x.JobName, x.StartedAt });

        e.Ignore(x => x.IsRunning);
        e.Ignore(x => x.Duration);
    }
}

public class EmailCheckpointConfiguration : IEntityTypeConfiguration<EmailCheckpoint>
{
    public void Configure(EntityTypeBuilder<EmailCheckpoint> e)
    {
        e.ToTable("EmailCheckpoints");
        e.HasKey(x => x.Id);
        e.Property(x => x.MailboxKey).IsRequired().HasMaxLength(400);
        e.HasIndex(x => x.MailboxKey).IsUnique();
    }
}
