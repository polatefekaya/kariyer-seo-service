using Kariyer.Seo.Worker.Common.Roles;
using Kariyer.Seo.Worker.Common.Telemetry;
using Kariyer.Seo.Worker.Common.Web;
using Kariyer.Seo.Worker.Features.Sitemaps.RebuildAll;

namespace Kariyer.Seo.Worker.Features.Diagnostics.RebuildNow;

/// <summary>
/// Forces a full rebuild immediately.
///
/// This is deliberately the ONLY write the diagnostics surface offers, and it is the safest
/// useful one available: it does exactly what the cron already does, just sooner. There is no
/// force-remove and no force-admit — those are precisely the buttons that get pressed at 2am
/// under pressure, and both would let one person's judgement about one job override the
/// corpus, which is the one thing this service is built never to do.
///
/// The legitimate way to change what is in the sitemap is to change <c>company_job</c>, and
/// then press this.
///
/// Guarded by role rather than by a token: only a replica that runs the builder can do it,
/// and the HTTP surface is not internet-facing (PLAN §11). Adding auth to a cluster-internal
/// diagnostics route would be theatre, but returning 404 on a reactor is not — a rebuild on a
/// pod that is not the R2 writer would be a second concurrent writer, which is exactly the
/// thing the single-writer rule exists to prevent.
/// </summary>
public sealed class RebuildNowEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("diag/rebuild", async (
            RolePlan plan,
            // Resolved from the provider, NOT taken as a handler parameter.
            //
            // SitemapBuilder is only registered on a role that rebuilds. As a parameter,
            // minimal APIs would fail to bind it on a reactor and answer 400 with a
            // model-binding complaint — telling an operator their REQUEST was malformed when
            // the truth is that they called the wrong pod. Resolving it after the role check
            // lets the 404 below say what actually happened.
            IServiceProvider services,
            ILogger<RebuildNowEndpoint> logger,
            CancellationToken ct) =>
        {
            if (!plan.RunsFullRebuild)
            {
                return Results.NotFound(new
                {
                    message =
                        $"This replica runs as '{plan.Role}' and does not hold the full rebuild. "
                        + "Call this on the builder.",
                });
            }

            // Logged because a manual rebuild is a human intervening in an automated system,
            // and the next person reading these logs deserves to know that a file changed
            // outside the cron.
            logger.LogInformation("Manual full rebuild requested.");

            SitemapBuilder builder = services.GetRequiredService<SitemapBuilder>();

            try
            {
                RebuildOutcome outcome = await builder.RebuildAsync("manual", ct);

                return Results.Ok(new
                {
                    outcome.Files,
                    outcome.Changed,
                    outcome.IndexableFacets,
                    outcome.FacetTransitions,
                    ElapsedSeconds = outcome.Elapsed.TotalSeconds,
                });
            }
            catch (Exception ex)
            {
                DiagnosticsConfig.RebuildFailures.Add(1,
                    new KeyValuePair<string, object?>("trigger", "manual"));

                logger.LogError(ex, "Manual rebuild failed. The live sitemap set is unchanged.");

                // 500 with the reason, because unlike the cron path there is a human waiting
                // for the answer and "the staged set was discarded" is the thing they need to
                // know: nothing was published, so nothing needs undoing.
                return Results.Problem(
                    title: "Rebuild failed",
                    detail: $"{ex.Message} The staged set was discarded; the live sitemap is unchanged.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("RebuildNow")
        .WithTags("Diagnostics");
    }
}
