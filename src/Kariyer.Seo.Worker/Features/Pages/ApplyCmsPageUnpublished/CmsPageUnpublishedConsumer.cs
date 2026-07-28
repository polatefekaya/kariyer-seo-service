using Kariyer.Messaging.Contracts.Cms;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Worker.Common.Telemetry;
using Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;
using MassTransit;

namespace Kariyer.Seo.Worker.Features.Pages.ApplyCmsPageUnpublished;

/// <summary>
/// Reacts to a CMS page being unpublished, archived or deleted.
///
/// The more urgent of the two page consumers. A page that is still advertised in
/// <c>sitemap-pages.xml</c> after being pulled sends crawlers to a URL that now 404s, and a
/// stale prerender snapshot means the page keeps SERVING its old content to bots even though
/// the CMS no longer resolves it — the page looks alive to Google and dead to everyone else.
///
/// Like its sibling it writes no local state: <c>cms.seo_page</c> is the truth and the flush
/// re-reads it. This exists so the removal takes one debounce window instead of up to a full
/// cron interval.
/// </summary>
public sealed class CmsPageUnpublishedConsumer(
    IPrerenderCache prerender,
    DirtySignal signal,
    ILogger<CmsPageUnpublishedConsumer> logger) : IConsumer<CmsPageUnpublishedEvent>
{
    public async Task Consume(ConsumeContext<CmsPageUnpublishedEvent> context)
    {
        CmsPageUnpublishedEvent message = context.Message;

        if (string.IsNullOrWhiteSpace(message.Path))
        {
            logger.LogWarning("CmsPageUnpublishedEvent arrived with no path; ignoring.");
            return;
        }

        await prerender.PurgePathAsync(message.Path, context.CancellationToken);

        DiagnosticsConfig.CmsPagesChanged.Add(1,
            new KeyValuePair<string, object?>("action", "unpublished"));

        signal.Raise();

        logger.LogInformation("CMS page {Path} unpublished; dropped from the sitemap.", message.Path);
    }
}
