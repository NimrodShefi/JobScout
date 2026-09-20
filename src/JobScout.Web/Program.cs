using JobScout.Core.Options;
using JobScout.Infrastructure;
using JobScout.Infrastructure.Data;
using JobScout.Infrastructure.Services;
using JobScout.Web.Components;
using JobScout.Web.Jobs;
using JobScout.Web.Services;
using Serilog;

// Bootstrap logger so anything that fails during start-up is still recorded.
// It is replaced by the configured logger once the host is built.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // -----------------------------------------------------------------------
    // Logging. Detailed diagnostics go to rolling daily files under /logs.
    // Nothing here ever receives CV text, email bodies or API keys - the code
    // that handles those logs counts and identifiers only.
    // -----------------------------------------------------------------------
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // -----------------------------------------------------------------------
    // Configuration. Secrets (API keys, mailbox password) come from user secrets
    // or environment variables, never from the database.
    // -----------------------------------------------------------------------
    builder.Configuration.AddUserSecrets<Program>(optional: true);

    builder.Services.Configure<JobScoutOptions>(
        builder.Configuration.GetSection(JobScoutOptions.SectionName));

    // -----------------------------------------------------------------------
    // Data
    // -----------------------------------------------------------------------
    var databasePath = builder.Configuration.GetValue<string>("Database:Path") ?? "./data/jobscout.db";
    builder.Services.AddJobScoutData(DependencyInjection.BuildSqliteConnectionString(databasePath));

    // -----------------------------------------------------------------------
    // Providers. Each is chosen by configuration and swappable behind its interface.
    // -----------------------------------------------------------------------
    builder.Services.AddJobScoutAi();
    builder.Services.AddJobScoutFetching();
    builder.Services.AddJobScoutCv();
    builder.Services.AddJobScoutBoards();
    builder.Services.AddJobScoutEmail();

    // -----------------------------------------------------------------------
    // Domain services shared by the UI and the scheduled jobs.
    // -----------------------------------------------------------------------
    builder.Services.AddSingleton<ListingUpsertService>();
    builder.Services.AddSingleton<ScoringService>();
    builder.Services.AddSingleton<EmailProcessingService>();
    builder.Services.AddSingleton<ApplicationService>();

    // -----------------------------------------------------------------------
    // Scheduled jobs. Cron expressions and the time zone come from config. The
    // scheduler gates each job so runs never overlap, and every run is recorded
    // as one JobRunLog row.
    // -----------------------------------------------------------------------
    builder.Services.AddScoped<MorningScanJob>();
    builder.Services.AddScoped<EmailCheckJob>();
    builder.Services.AddScoped<DiscoveryJob>();
    builder.Services.AddScoped<CompanyScoringJob>();

    builder.Services.AddSingleton<JobRunRecorder>();
    builder.Services.AddSingleton<JobScheduler>();
    builder.Services.AddSingleton<Display>();
    builder.Services.AddHostedService<CronSchedulerHostedService>();

    // -----------------------------------------------------------------------
    // UI
    // -----------------------------------------------------------------------
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    var app = builder.Build();

    // Apply migrations and PRAGMAs before anything else touches the database.
    using (var scope = app.Services.CreateScope())
    {
        var initialiser = scope.ServiceProvider.GetRequiredService<DatabaseInitialiser>();
        await initialiser.InitialiseAsync();
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
    }

    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

    app.UseSerilogRequestLogging();
    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    Log.Information("JobScout is private and binds to localhost only");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "JobScout terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Exposed so the test project and user secrets can reference the entry assembly.</summary>
public partial class Program;
