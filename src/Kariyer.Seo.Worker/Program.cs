using Kariyer.Seo.Worker.Common.Caching;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Messaging;
using Kariyer.Seo.Worker.Common.Persistence;
using Kariyer.Seo.Worker.Common.Roles;
using Kariyer.Seo.Worker.Common.Storage;
using Kariyer.Seo.Worker.Common.Telemetry;
using Kariyer.Seo.Worker.Common.Web;
using Kariyer.Seo.Worker.Features.Indexing.SubmitIndexing;
using Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;
using Kariyer.Seo.Worker.Features.Sitemaps.RebuildAll;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Serilog;

// The container HEALTHCHECK re-enters the same binary rather than shelling out to curl,
// which the .NET runtime images do not carry. Handled before any host is built: probing
// should not require a database connection string or R2 credentials to be present.
if (HealthCheckCommand.ShouldRun(args))
{
    return await HealthCheckCommand.RunAsync();
}

// Startup is wrapped, and the process is terminated EXPLICITLY on failure.
//
// Letting an unhandled exception escape here does not reliably end the process: by the time
// the host has been built, background and foreground threads exist (the OTLP exporter, the
// bus, the connection pools), and the runtime was observed printing the exception and then
// staying alive, spinning, indefinitely. The container therefore stayed `Running=true` with
// exit code 0 — Docker never restarts it, a pod without a liveness probe never restarts it,
// and no exit code is ever surfaced.
//
// For a service whose failures are already invisible — nothing 500s when the sitemap is
// wrong — "boots into a dead process that reports success" is the worst available outcome.
// So: log it where `docker logs` will show it, flush, and exit non-zero on purpose.
int exitCode = 0;

try
{
    WebApplication app = Build(args);

    await MigrateDatabaseIfEnabledAsync(app);

    await app.RunAsync();
}
catch (Exception ex)
{
    // Console.Error as well as Serilog, not instead of it. The two failures most likely to
    // land here — an unresolved SERVICE_ROLE and a rejected options validator — both happen
    // BEFORE AddObservability has configured a sink, so Log.Fatal alone would go to
    // Serilog's silent default logger and the crash would be undiagnosable from `docker logs`.
    await Console.Error.WriteLineAsync($"FATAL: the SEO service could not start. {ex}");

    Log.Fatal(ex, "The SEO service could not start.");

    exitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

if (exitCode != 0)
{
    await Console.Error.FlushAsync();

    // Terminated explicitly rather than by returning from Main.
    //
    // Returning is not enough: by the time the host has been built there are live threads
    // (the OTLP exporter, the bus, connection pools), and the runtime was observed printing
    // an unhandled startup exception and then staying alive, spinning at 100% CPU,
    // indefinitely. The container stayed `Running=true` with exit code 0 — so Docker never
    // restarted it, a pod without a liveness probe never restarted it, and no exit code was
    // ever surfaced.
    //
    // For a service whose failures are already invisible — nothing 500s when the sitemap is
    // wrong — "boots into a dead process that reports success" is the worst available
    // outcome. Exit(1) makes a fatal startup a crash the orchestrator can see.
    Environment.Exit(exitCode);
}

return exitCode;

static WebApplication Build(string[] args)
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // Resolved before anything else is registered: the role decides which slices exist in
    // this process, which options are REQUIRED, and it is stamped on every log line, span
    // and metric.
    ServiceRole role = ServiceRoleResolver.Resolve(builder.Configuration);
    RolePlan plan = RolePlan.For(role);

    builder.AddObservability(role);

    builder.Services.AddSingleton(plan);

    // A real clock, injected rather than ambient, everywhere in this service.
    //
    // Not for tidiness: <lastmod> is the single most consequential value this service emits,
    // and the debounce window is what bounds how long a withdrawn job stays advertised. Both
    // are asserted against a FakeTimeProvider, which is only possible because nothing here
    // calls DateTimeOffset.UtcNow directly.
    builder.Services.AddSingleton(TimeProvider.System);

    builder.Services.AddSeoOptions(builder.Configuration, plan);

    string postgres = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

    string rabbit = builder.Configuration.GetConnectionString("RabbitMQ")
        ?? throw new InvalidOperationException("ConnectionStrings:RabbitMQ is required.");

    builder.Services.AddPersistence(postgres);
    builder.Services.AddMessaging(plan, rabbit);
    builder.Services.AddPrerenderCache(builder.Configuration, plan.NeedsPrerenderCache);

    if (plan.WritesToR2)
    {
        builder.Services.AddSitemapSink(builder.Configuration);
    }

    if (plan.NeedsFacetManifest)
    {
        builder.Services.AddFacetManifest();
    }

    AddIndexing(builder);

    // The coalescing signal is a singleton and is registered for every role, not only the
    // one that flushes: the consumers raise it unconditionally, and a role-conditional
    // registration would turn "reactor without the flush" into a DI resolution failure at
    // the first expiry rather than at startup.
    builder.Services.AddSingleton<DirtySignal>();

    // Only the slices this role is responsible for.
    if (plan.RunsFullRebuild)
    {
        builder.Services.AddScoped<SitemapBuilder>();
        builder.Services.AddHostedService<RebuildAllWorker>();
    }

    if (plan.RunsDirtyFlush)
    {
        builder.Services.AddScoped<JobSitemapProjector>();
        builder.Services.AddScoped<IDirtyFlusher>(sp => sp.GetRequiredService<JobSitemapProjector>());
        builder.Services.AddHostedService<FlushDirtyWorker>();
    }

    builder.Services
        .AddHealthChecks()
        .AddNpgSql(postgres, name: "postgres", tags: ["ready"]);

    // The broker is covered too: AddMassTransit registers a "masstransit-bus" check already
    // tagged "ready", so a replica that cannot reach RabbitMQ is pulled from rotation.
    //
    // R2 and Garnet are deliberately NOT readiness checks. Neither is needed to accept
    // traffic — this service serves no traffic — and failing readiness on an unreachable
    // bucket would take the pod out of rotation for a dependency that only matters every 45
    // minutes, when the correct behaviour is to keep consuming freshness events and retry.

    builder.Services.AddEndpoints(typeof(Program).Assembly);

    WebApplication app = builder.Build();

    app.UseSerilogRequestLogging();

    // Liveness answers "is this process running": no dependency checks, because a pod must
    // not be killed and restarted just because Postgres is briefly unreachable.
    app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });

    // Readiness answers "can this process do its job".
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("ready"),
    });

    app.MapPrometheusScrapingEndpoint();
    app.MapEndpoints();

    LogStartupPlan(app, plan);

    return app;
}

static void AddIndexing(WebApplicationBuilder builder)
{
    IndexingOptions indexing = new();
    builder.Configuration.GetSection(IndexingOptions.SectionName).Bind(indexing);

    if (!indexing.Enabled)
    {
        builder.Services.AddSingleton<IIndexingSubmitter, DisabledIndexingSubmitter>();
        return;
    }

    // A named typed client, so the Indexing API's timeouts and handler lifetime are its own
    // and cannot be tuned by something that happens to share a default client.
    builder.Services
        .AddHttpClient<IIndexingSubmitter, GoogleIndexingSubmitter>()
        .ConfigureHttpClient(client =>
            client.Timeout = TimeSpan.FromSeconds(indexing.TimeoutSeconds + 5));
}

static async Task MigrateDatabaseIfEnabledAsync(WebApplication app)
{
    IOptions<PersistenceOptions> options =
        app.Services.GetRequiredService<IOptions<PersistenceOptions>>();

    if (!options.Value.MigrateOnStartup)
    {
        return;
    }

    ILogger<Program> logger = app.Services.GetRequiredService<ILogger<Program>>();
    string postgres = app.Configuration.GetConnectionString("Postgres")!;

    try
    {
        // Every role runs this; the advisory lock inside serialises them.
        await DatabaseMigrator.MigrateAsync(
            app.Services, postgres, logger, app.Lifetime.ApplicationStopping);
    }
    catch (Exception ex)
    {
        // A failed migration is fatal on purpose: better to crash loudly at boot than to run
        // against a schema that does not match the model and write state nobody can read.
        logger.LogCritical(ex, "Database migration failed. The service will not start.");
        throw;
    }
}

static void LogStartupPlan(WebApplication app, RolePlan plan)
{
    ILogger<Program> logger = app.Services.GetRequiredService<ILogger<Program>>();

    SeoOptions seo = app.Services.GetRequiredService<IOptions<SeoOptions>>().Value;

    // Stated on every boot, at WARNING either way, because both directions are dangerous in
    // opposite ways and neither is visible at runtime. Indexable on a test host quietly
    // competes with production for its own rankings; non-indexable on production quietly
    // de-lists the entire site. The only defence is that someone reads this line.
    if (seo.AllowIndexing)
    {
        logger.LogWarning(
            "Publishing an INDEXABLE robots.txt for {SiteUrl}. If this is not the production "
            + "host, set Seo:AllowIndexing=false — a crawlable copy of the site competes with "
            + "production for its own rankings.", seo.SiteUrl);
    }
    else
    {
        logger.LogWarning(
            "Publishing a NOINDEX robots.txt (Disallow: /) for {SiteUrl}. Sitemaps are still "
            + "built so the pipeline is verifiable, but nothing here should be crawled. This "
            + "must never be the production host.", seo.SiteUrl);
    }

    logger.LogInformation(
        "Starting SEO service as {Role}: rebuild={RunsFullRebuild}, "
        + "freshnessConsumers={ConsumesFreshnessEvents}, flush={RunsDirtyFlush}",
        plan.Role, plan.RunsFullRebuild, plan.ConsumesFreshnessEvents, plan.RunsDirtyFlush);

    if (plan.WritesToR2)
    {
        // Said out loud on purpose. Exactly one replica should ever print this line. Two
        // replicas staging sitemap.xml concurrently can swap in each other's half-finished
        // index, and the only party who ever finds out is the crawler that fetched during
        // the window — there is no error, no log line, and no metric for it.
        logger.LogWarning(
            "This replica holds the R2 sitemap write. Exactly one replica should. "
            + "Scale this deployment to 1.");
    }
}

/// <summary>Exposed so the integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
