using Kariyer.Messaging.Contracts.Freshness;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Worker.Common.Persistence;
using Kariyer.Seo.Worker.Common.Telemetry;
using Kariyer.Seo.Worker.Features.Indexing.SubmitIndexing;
using Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;
using MassTransit;

namespace Kariyer.Seo.Worker.Features.Sitemaps.ApplyJobResurrected;

/// <summary>
/// Re-admits a job the freshness service had wrongly expired and has since restored
/// (PLAN §4).
///
/// The compensating consumer for <c>JobExpiredConsumer</c>, and the more urgent of the two.
/// An expiry that is a few minutes late costs some crawl budget; a resurrection that is late
/// means a live posting an employer is paying for stays out of Google's index, and the
/// employer is the one who notices. So the purge matters just as much on this path: the
/// prerender cache may still hold the page as it looked while expired, and re-admitting the
/// URL without purging would advertise a live job whose cached page says it is closed.
///
/// Same ordering as the expiry path, for the same reasons: commit, then purge, then signal.
/// </summary>
public sealed class JobResurrectedConsumer(
    ISeoStore store,
    IPrerenderCache prerender,
    DirtySignal signal,
    IIndexingSubmitter indexing,
    TimeProvider clock,
    ILogger<JobResurrectedConsumer> logger) : IConsumer<JobResurrectedEvent>
{
    public async Task Consume(ConsumeContext<JobResurrectedEvent> context)
    {
        JobResurrectedEvent message = context.Message;
        CancellationToken ct = context.CancellationToken;

        if (string.IsNullOrWhiteSpace(message.JobUid))
        {
            logger.LogWarning("JobResurrectedEvent arrived with no JobUid; ignoring.");
            return;
        }

        if (string.IsNullOrWhiteSpace(message.SlugUrl))
        {
            // Without a slug there is no URL to re-admit. Unlike the expiry path — where a
            // missing slug still means "drop this uid from the sitemap" — there is nothing
            // useful to record here, and inventing an empty slug would put
            // `/is-ilanlari/ilan/` into the sitemap. The next corpus sync picks the job up
            // from company_job, which has the real slug.
            logger.LogWarning(
                "JobResurrectedEvent for {JobUid} carried no slug; the next full rebuild will "
                + "re-admit it from company_job.", message.JobUid);
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();

        await store.UpsertUrlStateAsync(
            message.JobUid, message.SlugUrl, SeoUrlStatus.Live, now, ct);

        await store.SaveChangesAsync(ct);

        DiagnosticsConfig.JobsReadmitted.Add(1);

        // Purged on this path too. The cache may hold the page as it looked while the job was
        // expired, so re-admitting the URL without purging advertises a live posting whose
        // rendered page tells the visitor it is closed.
        await prerender.PurgeJobAsync(message.SlugUrl, ct);

        await indexing.SubmitAsync(message.JobUid, message.SlugUrl, IndexingAction.Updated, ct);

        signal.Raise();

        // Warning, not information. Every resurrection is a false expiry that already shipped
        // — the URL was out of our sitemap, and possibly out of Google's index, for however
        // long it took to notice. A sustained rate is a precision defect upstream, and this
        // service is where it becomes visible in SEO terms.
        logger.LogWarning(
            "Job {JobUid} ({Slug}) re-admitted to the sitemap after a previous expiry.",
            message.JobUid, message.SlugUrl);
    }
}
