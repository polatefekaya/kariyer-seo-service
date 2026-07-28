using Kariyer.Messaging.Contracts.Cms;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Worker.Common.Telemetry;
using Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;
using MassTransit;

namespace Kariyer.Seo.Worker.Features.Pages.ApplyCmsPagePublished;

/// <summary>
/// Reacts to an editor publishing a CMS landing page.
///
/// This consumer exists purely for LATENCY. Unlike the freshness consumers it writes no local
/// state at all: <c>cms.seo_page</c> is in the same database and is already the truth, so the
/// projection reads it directly. Without this event a newly published page would still appear
/// in the sitemap — just up to a full cron interval later, which is a poor experience for
/// someone who has just clicked Publish and wants to see the page indexed.
///
/// What it does do that a rebuild cannot is purge the prerender cache, and that matters on
/// PUBLISH and not only on unpublish: an editor who fixes a typo and republishes has changed
/// what the page says, while the prerenderer is still holding the previous render and will
/// serve it to crawlers for the rest of its TTL. Without this, the CMS's core promise — edit
/// and it is live — is true for browsers and false for Google.
/// </summary>
public sealed class CmsPagePublishedConsumer(
    IPrerenderCache prerender,
    DirtySignal signal,
    ILogger<CmsPagePublishedConsumer> logger) : IConsumer<CmsPagePublishedEvent>
{
    public async Task Consume(ConsumeContext<CmsPagePublishedEvent> context)
    {
        CmsPagePublishedEvent message = context.Message;
        CancellationToken ct = context.CancellationToken;

        if (string.IsNullOrWhiteSpace(message.Path))
        {
            // Nothing addressable. Faulting would retry three times and dead-letter a message
            // that can never become valid.
            logger.LogWarning("CmsPagePublishedEvent arrived with no path; ignoring.");
            return;
        }

        await prerender.PurgePathAsync(message.Path, ct);

        // A path change is TWO facts: a new URL appeared and an old one died. The CMS carries
        // the old one precisely so this purge can happen — otherwise the prerenderer keeps
        // serving a fully rendered page at an address that now 404s, for its whole TTL, and
        // nothing in either service would ever notice.
        if (!string.IsNullOrWhiteSpace(message.PreviousPath)
            && !string.Equals(message.PreviousPath, message.Path, StringComparison.Ordinal))
        {
            await prerender.PurgePathAsync(message.PreviousPath, ct);

            logger.LogInformation(
                "CMS page moved from {PreviousPath} to {Path}; purged both.",
                message.PreviousPath, message.Path);
        }

        DiagnosticsConfig.CmsPagesChanged.Add(1,
            new KeyValuePair<string, object?>("action", "published"),
            new KeyValuePair<string, object?>("indexable", message.Indexable));

        // Raised even when the page is NOT indexable. A page that was indexable and has just
        // been republished with noindex must LEAVE sitemap-pages, and only a re-projection
        // discovers that — the flush re-reads cms.seo_page and the checksum decides whether
        // anything is actually uploaded.
        signal.Raise();

        logger.LogInformation(
            "CMS page {Path} published (indexable={Indexable}).", message.Path, message.Indexable);
    }
}
