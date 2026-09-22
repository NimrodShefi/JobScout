using JobScout.Core.Abstractions;
using JobScout.Core.Options;
using JobScout.Infrastructure.Ai;
using JobScout.Infrastructure.Ats;
using JobScout.Infrastructure.Boards;
using JobScout.Infrastructure.Cv;
using JobScout.Infrastructure.Data;
using JobScout.Infrastructure.Email;
using JobScout.Infrastructure.Fetching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the database. Providers are added by the caller in later layers.</summary>
    public static IServiceCollection AddJobScoutData(this IServiceCollection services, string connectionString)
    {
        // Factory-based registration: background jobs create short-lived contexts of their own
        // rather than sharing the Blazor circuit's scoped context.
        services.AddDbContextFactory<JobScoutDbContext>(options =>
            options.UseSqlite(connectionString, sqlite =>
                sqlite.MigrationsAssembly(typeof(JobScoutDbContext).Assembly.FullName)));

        services.AddSingleton<DatabaseInitialiser>();

        return services;
    }

    /// <summary>Registers the AI matcher on whichever chat provider config selects.
    ///
    /// Scoring is the whole point of the app, so the configuration is validated at start-up
    /// and the client is built there too - a missing key stops the app rather than turning
    /// into a failed run hours later. See <see cref="VerifyAiConfiguration"/>.</summary>
    public static IServiceCollection AddJobScoutAi(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<JobScoutOptions>, AiOptionsValidator>();

        services.AddSingleton<IChatClient>(sp => ChatClientFactory.Create(
            sp.GetRequiredService<IOptions<JobScoutOptions>>(),
            sp.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton<IJobMatcher, AiJobMatcher>();

        return services;
    }

    /// <summary>Forces the AI configuration to be validated and the provider client to be
    /// built, so anything wrong with it throws now instead of on the first scan.
    ///
    /// Call this before the app starts serving. Reading <c>IOptions.Value</c> runs every
    /// registered validator; resolving <see cref="IChatClient"/> then catches problems no
    /// amount of config validation can see, such as a provider SDK rejecting the endpoint.</summary>
    public static void VerifyAiConfiguration(IServiceProvider services)
    {
        var options = ReadOptions(services);

        _ = services.GetRequiredService<IChatClient>();

        var model = string.IsNullOrWhiteSpace(options.Ai.Model)
            ? ChatClientFactory.DefaultModel(options.Ai.Provider)
            : options.Ai.Model;

        // Provider and model only - never the key.
        services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("JobScout.Startup")
            .LogInformation("AI provider {Provider} ready with model {Model}", options.Ai.Provider, model);
    }

    /// <summary>Materialises the options, turning a binding failure into the same kind of
    /// validation error as everything else.
    ///
    /// Binding runs before any <see cref="IValidateOptions{T}"/> does, so a value the binder
    /// cannot convert at all - a blank or misspelled <c>Provider</c>, a non-numeric timeout -
    /// would otherwise escape as a raw stack trace that never reaches the validator.</summary>
    private static JobScoutOptions ReadOptions(IServiceProvider services)
    {
        try
        {
            return services.GetRequiredService<IOptions<JobScoutOptions>>().Value;
        }
        catch (InvalidOperationException ex)
        {
            throw new OptionsValidationException(
                Options.DefaultName, typeof(JobScoutOptions), DescribeBindingFailure(ex));
        }
    }

    private static string[] DescribeBindingFailure(InvalidOperationException ex)
    {
        // The binder's own message already names the offending key and its value.
        var messages = new List<string> { ex.Message };

        if (ex.Message.Contains(nameof(AiProvider), StringComparison.Ordinal))
        {
            messages.Add(
                $"Set 'JobScout:Ai:Provider' to one of: {string.Join(", ", Enum.GetNames<AiProvider>())}. " +
                "Remove the setting entirely to use the default (Anthropic); leaving it blank is not the same thing.");
        }
        else if (ex.Message.Contains(nameof(EmailProviderKind), StringComparison.Ordinal))
        {
            messages.Add(
                $"Set 'JobScout:Email:Provider' to one of: {string.Join(", ", Enum.GetNames<EmailProviderKind>())}.");
        }

        return [.. messages];
    }

    /// <summary>Registers page fetching: resilient HttpClients, the robots.txt gate,
    /// and the Playwright fallback.</summary>
    public static IServiceCollection AddJobScoutFetching(this IServiceCollection services)
    {
        foreach (var name in new[] { PoliteHttpPageFetcher.HttpClientName, RobotsGate.HttpClientName })
        {
            var pipeline = services.AddHttpClient(name)
                .ConfigurePrimaryHttpMessageHandler(PoliteHttpPageFetcher.CreateHandler)
                .ConfigureHttpClient((sp, client) =>
                    PoliteHttpPageFetcher.ConfigureClient(client, Fetching(sp)))
                .AddStandardResilienceHandler();

            // Bind the pipeline's timeouts to the same FetchOptions the fetcher uses.
            services.AddOptions<Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions>(pipeline.PipelineName)
                .Configure<IOptions<JobScoutOptions>>((o, js) => ConfigureResilience(o, js.Value.Fetching));
        }

        services.AddSingleton<IRobotsGate, RobotsGate>();
        services.AddSingleton<IBrowserPageFetcher, PlaywrightPageFetcher>();
        services.AddSingleton<IPageFetcher, PoliteHttpPageFetcher>();

        return services;
    }

    public static IServiceCollection AddJobScoutCv(this IServiceCollection services)
    {
        services.AddSingleton<ICvTextExtractor, CvTextExtractor>();
        return services;
    }

    /// <summary>Registers every job board. They are resolved as a collection, so the
    /// discovery job asks all of them and each decides for itself whether it is enabled.</summary>
    public static IServiceCollection AddJobScoutBoards(this IServiceCollection services)
    {
        services.AddHttpClient(AdzunaJobBoardProvider.HttpClientName)
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan; // Owned by the resilience pipeline.
                client.DefaultRequestHeaders.Accept.Add(new("application/json"));
            })
            .AddStandardResilienceHandler();

        services.AddSingleton<IJobBoardProvider, AdzunaJobBoardProvider>();

        return services;
    }

    /// <summary>Registers the job-board feeds (Greenhouse, Lever, Ashby, Workable). They are
    /// resolved as a collection, keyed by the service each one reads.</summary>
    public static IServiceCollection AddJobScoutAts(this IServiceCollection services)
    {
        services.AddHttpClient(AtsFeedBase.HttpClientName)
            .ConfigureHttpClient(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan; // Owned by the resilience pipeline.
                client.DefaultRequestHeaders.Accept.Add(new("application/json"));
            })
            .AddStandardResilienceHandler();

        services.AddSingleton<IAtsFeed, GreenhouseFeed>();
        services.AddSingleton<IAtsFeed, LeverFeed>();
        services.AddSingleton<IAtsFeed, AshbyFeed>();
        services.AddSingleton<IAtsFeed, WorkableFeed>();

        return services;
    }

    /// <summary>Registers the mailbox reader chosen by configuration. Read-only by contract.</summary>
    public static IServiceCollection AddJobScoutEmail(this IServiceCollection services)
    {
        services.AddSingleton<IEmailProvider, ImapEmailProvider>();
        return services;
    }

    private static FetchOptions Fetching(IServiceProvider sp) =>
        sp.GetRequiredService<IOptions<JobScoutOptions>>().Value.Fetching;

    /// <summary>Retry transient failures, but stay gentle - this is someone else's site.
    /// The pipeline owns all timeouts (see ConfigureClient).</summary>
    private static void ConfigureResilience(
        Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions o,
        FetchOptions fetching)
    {
        var attempt = TimeSpan.FromSeconds(Math.Max(5, fetching.TimeoutSeconds));

        o.Retry.MaxRetryAttempts = 2;
        o.Retry.Delay = TimeSpan.FromSeconds(2);
        o.Retry.UseJitter = true;

        o.AttemptTimeout.Timeout = attempt;

        // Total must cover every attempt plus the backoff between them.
        o.TotalRequestTimeout.Timeout = attempt * 4;

        // The breaker requires a window of at least twice the attempt timeout.
        o.CircuitBreaker.SamplingDuration = attempt * 2;
    }

    /// <summary>Builds a SQLite connection string with WAL-friendly settings.</summary>
    public static string BuildSqliteConnectionString(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,
            Pooling = true,
            // Wait rather than fail when a background write holds the lock.
            DefaultTimeout = 30,
        }.ToString();
    }
}
