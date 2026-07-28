using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Persistence;
using Kariyer.Seo.Worker.Common.Roles;
using Kariyer.Seo.Worker.Common.Web;
using Microsoft.Extensions.Options;

namespace Kariyer.Seo.Worker.Features.Diagnostics.GetSitemapStatus;

/// <summary>
/// Answers "what is actually in the sitemap right now, and when did it last change?"
/// without anyone opening a psql session or an R2 console.
///
/// That question gets asked in the situations where both are worst: an employer says their
/// live posting is not in Google, or a withdrawn job is still showing in results. Both need
/// the per-file URL counts and the dirty backlog immediately, and both are made worse by a
/// hurried hand-written query against a production database.
///
/// Read-only by construction.
/// </summary>
public sealed class GetSitemapStatusEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("diag/sitemap", async (
            ISeoStore store,
            RolePlan plan,
            IOptions<SeoOptions> options,
            CancellationToken ct) =>
        {
            IReadOnlyList<SeoRebuildLog> recent = await store.GetRecentRebuildLogAsync(60, ct);

            // Newest row per file. The log is append-only, so the raw list holds every
            // rebuild ever — what an operator wants is the current state of each file.
            Dictionary<string, SeoRebuildLog> newest = new(StringComparer.Ordinal);

            foreach (SeoRebuildLog row in recent)
            {
                newest.TryAdd(row.File, row);
            }

            int dirty = await store.CountDirtyAsync(ct);
            int liveJobs = await store.CountLiveJobsAsync(ct);

            return Results.Ok(new
            {
                Role = plan.Role.ToString(),
                plan.WritesToR2,

                // The two numbers that answer most questions on their own. A gap between
                // them and the jobs sitemap's URL count means the projection has drifted
                // from the corpus, which the next full rebuild will correct.
                LiveJobsInCorpus = liveJobs,

                // Non-zero and not falling means the debounced flush is not running — the
                // single most useful signal on this endpoint.
                DirtyUrls = dirty,

                LastRebuiltAt = newest.Values.Count == 0
                    ? (DateTimeOffset?)null
                    : newest.Values.Max(r => r.GeneratedAt),

                SiteUrl = options.Value.SiteUrl,
                options.Value.CronInterval,
                options.Value.DebounceWindow,

                Files = newest.Values
                    .OrderBy(r => r.File, StringComparer.Ordinal)
                    .Select(r => new
                    {
                        r.File,
                        r.SitemapType,
                        r.UrlCount,
                        r.Checksum,
                        r.UncompressedBytes,

                        // False here on consecutive rebuilds is the HEALTHY state: it means
                        // the file was byte-identical and the upload was skipped.
                        r.Uploaded,
                        r.GeneratedAt,
                    }),
            });
        })
        .WithName("GetSitemapStatus")
        .WithTags("Diagnostics");
    }
}
