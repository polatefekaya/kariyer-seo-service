using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Scheduling;
using Kariyer.Seo.Worker.Common.Telemetry;
using Microsoft.Extensions.Options;

namespace Kariyer.Seo.Worker.Features.Sitemaps.RebuildAll;

/// <summary>
/// Runs the full rebuild on a timer.
///
/// A plain interval rather than a wall-clock cron, deliberately. Nothing about this work is
/// time-of-day sensitive — there is no quiet hour for a crawler, and the aggregate is a
/// single grouped query rather than a load the database needs sheltering from. An interval
/// also self-heals: a pod that restarts rebuilds shortly after boot instead of waiting until
/// the next scheduled minute, which for a service whose failure mode is "the sitemap silently
/// stops changing" is the property that matters.
/// </summary>
public sealed class RebuildAllWorker(
    IServiceScopeFactory scopes,
    IOptions<SeoOptions> options,
    ILogger<RebuildAllWorker> logger) : PeriodicWorker(logger)
{
    protected override TimeSpan Interval => options.Value.CronInterval;

    protected override string Name => "rebuild-all";

    protected internal override async Task TickAsync(CancellationToken cancellationToken)
    {
        // A scope per tick: the DbContext is scoped, and holding one for the process lifetime
        // would accumulate tracked entities from every rebuild until the pod ran out of
        // memory — on a service whose whole job is to touch every row in the catalogue.
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        SitemapBuilder builder = scope.ServiceProvider.GetRequiredService<SitemapBuilder>();

        try
        {
            await builder.RebuildAsync("cron", cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Counted here as well as by the base loop's generic tick counter, because the
            // generic one cannot say WHICH failure mattered — and a failed rebuild is the one
            // that leaves the live sitemap frozen. Rethrown so the base class logs it with
            // the full exception.
            DiagnosticsConfig.RebuildFailures.Add(1,
                new KeyValuePair<string, object?>("trigger", "cron"));
            throw;
        }
    }
}
