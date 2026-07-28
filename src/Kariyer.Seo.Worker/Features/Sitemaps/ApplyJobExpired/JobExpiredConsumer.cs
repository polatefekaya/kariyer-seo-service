using Kariyer.Messaging.Contracts.Freshness;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Worker.Common.Persistence;
using Kariyer.Seo.Worker.Common.Telemetry;
using Kariyer.Seo.Worker.Features.Indexing.SubmitIndexing;
using Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;
using MassTransit;

namespace Kariyer.Seo.Worker.Features.Sitemaps.ApplyJobExpired;

/// <summary>
/// Reacts to the freshness service deciding a posting is gone (PLAN §4, §6.1).
///
/// This is the whole reason the freshness service publishes anything, and the ordering here
/// is the substance of PLAN §6.1:
///
/// <list type="number">
///   <item><b>Commit the state change first.</b> The <c>seo_url_state</c> row plus the
///   MassTransit inbox row go in one transaction. Once that commits, the removal is durable
///   and nothing can lose it — every later step is a retryable projection of it.</item>
///   <item><b>Then purge Garnet.</b> After the commit, not before and not inside it. A purge
///   is idempotent (<c>DEL</c> on a missing key does nothing), so a crash in the gap costs a
///   repeat, not a correctness failure. Doing it first would risk purging a page for a
///   removal that then rolled back.</item>
///   <item><b>Then signal the flush.</b> Coalesced, so a batch of expiries produces one
///   re-projection.</item>
/// </list>
///
/// The slug comes from the EVENT rather than from a fresh read of <c>company_job</c>, and the
/// freshness service captures it in the same statement that performed the expiry precisely so
/// that it can. Re-reading it here would race a concurrent edit: a slug changed between their
/// write and our read leaves the OLD prerendered page cached under a key nobody thinks to
/// purge, serving a dead job for its full TTL.
/// </summary>
public sealed class JobExpiredConsumer(
    ISeoStore store,
    IPrerenderCache prerender,
    DirtySignal signal,
    IIndexingSubmitter indexing,
    TimeProvider clock,
    ILogger<JobExpiredConsumer> logger) : IConsumer<JobExpiredEvent>
{
    public async Task Consume(ConsumeContext<JobExpiredEvent> context)
    {
        JobExpiredEvent message = context.Message;
        CancellationToken ct = context.CancellationToken;

        if (string.IsNullOrWhiteSpace(message.JobUid))
        {
            // Nothing to key on. Faulting would retry it three times and then dead-letter it
            // for a message that can never become valid.
            logger.LogWarning("JobExpiredEvent arrived with no JobUid; ignoring.");
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();

        // The row is CREATED if absent, not skipped. A job expired before this service's
        // first full rebuild has no row yet, and skipping it would mean the purge below never
        // happens — the URL would eventually leave the sitemap via the next corpus sync, but
        // the prerendered "apply now" page would survive its whole TTL.
        await store.UpsertUrlStateAsync(
            message.JobUid, message.SlugUrl, SeoUrlStatus.Removed, now, ct);

        // Saved, not committed with an explicit transaction. MassTransit's EF outbox filter
        // already wraps this consumer in one that includes the inbox row, so an explicit
        // transaction here would nest inside it and buy nothing.
        await store.SaveChangesAsync(ct);

        DiagnosticsConfig.JobsRemoved.Add(1);

        // ── Everything below is a projection of the commit above ────────────────

        if (!string.IsNullOrWhiteSpace(message.SlugUrl))
        {
            // Deliberately not awaited inside a try/catch here: the cache adapter never
            // throws. It logs, counts, and returns — because the removal is already durable
            // and faulting the message to retry a cache DELETE would replay a committed
            // change for no gain.
            await prerender.PurgeJobAsync(message.SlugUrl, ct);

            await indexing.SubmitAsync(
                message.JobUid, message.SlugUrl, IndexingAction.Deleted, ct);
        }

        signal.Raise();

        logger.LogInformation(
            "Job {JobUid} ({Slug}) removed from the sitemap; reason={Reason}.",
            message.JobUid, message.SlugUrl, message.Reason);
    }
}
