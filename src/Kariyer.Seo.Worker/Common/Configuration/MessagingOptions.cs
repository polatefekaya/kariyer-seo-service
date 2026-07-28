using System.ComponentModel.DataAnnotations;

namespace Kariyer.Seo.Worker.Common.Configuration;

/// <summary>
/// RabbitMQ topology (PLAN §9). Exchange names follow the house convention
/// <c>&lt;service&gt;.&lt;entity&gt;.&lt;action&gt;</c>.
///
/// The two inbound names are the freshness service's exchanges and are therefore a CONTRACT
/// with another repository, not a local preference — changing either here silently unsubscribes
/// this service and nothing fails, it just stops hearing about expiries.
/// </summary>
public sealed class RabbitOptions
{
    public const string SectionName = "Rabbit";

    /// <summary>Fanout exchange the freshness service publishes JobExpiredEvent to.</summary>
    [Required]
    public string JobExpiredExchange { get; init; } = "freshness.job.expired";

    /// <summary>Fanout exchange the freshness service publishes JobResurrectedEvent to.</summary>
    [Required]
    public string JobResurrectedExchange { get; init; } = "freshness.job.resurrected";

    /// <summary>Fanout exchange kariyer-cms-service publishes CmsPagePublishedEvent to.</summary>
    [Required]
    public string CmsPagePublishedExchange { get; init; } = "cms.page.published";

    /// <summary>Fanout exchange kariyer-cms-service publishes CmsPageUnpublishedEvent to.</summary>
    [Required]
    public string CmsPageUnpublishedExchange { get; init; } = "cms.page.unpublished";

    /// <summary>
    /// Our own durable queue bound to both CMS exchanges.
    ///
    /// Separate from the freshness queue on purpose. The two sources are independent — a CMS
    /// outage must not stall job expiries and vice versa — and a single shared queue would
    /// give one slow consumer the power to back up the other's deliveries behind it.
    /// </summary>
    [Required]
    public string CmsConsumerQueue { get; init; } = "seo.cms.consumer";

    [Required]
    public string SitemapRebuiltExchange { get; init; } = "seo.sitemap.rebuilt";

    [Required]
    public string FacetIndexabilityChangedExchange { get; init; } = "seo.facet.indexability_changed";

    [Required]
    public string IndexingSubmittedExchange { get; init; } = "seo.indexing.submitted";

    /// <summary>
    /// Our own durable queue bound to both freshness exchanges.
    ///
    /// A DEDICATED queue, not a shared one: fanout means every subscriber gets its own copy,
    /// and sharing a queue with another consumer would have the two of them competing for
    /// messages so each job expiry reached only one of them.
    /// </summary>
    [Required]
    public string FreshnessConsumerQueue { get; init; } = "seo.freshness.consumer";

    [Range(1, 1000)]
    public int PrefetchCount { get; init; } = 16;

    /// <summary>
    /// Concurrency on the freshness queue.
    ///
    /// Bounded well below prefetch because each message costs a database write plus three
    /// Garnet DELs, and because these consumers all touch the same <c>seo_url_state</c>
    /// table — unbounded concurrency here buys throughput in exchange for row contention.
    /// </summary>
    [Range(1, 1000)]
    public int ConcurrentMessageLimit { get; init; } = 8;
}

/// <summary>How outbound events are serialised (PLAN §9).</summary>
public sealed class EventsOptions
{
    public const string SectionName = "Events";

    /// <summary>
    /// Publish as bare JSON instead of a MassTransit envelope. Only needed if a non-.NET
    /// consumer subscribes. Currently false — everything in this estate is .NET, and turning
    /// it on would also strip the envelope the INBOUND freshness events rely on.
    /// </summary>
    public bool RawJson { get; init; }
}
