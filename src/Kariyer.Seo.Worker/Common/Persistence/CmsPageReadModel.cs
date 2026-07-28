namespace Kariyer.Seo.Worker.Common.Persistence;

/// <summary>
/// A read-only view of the fields this service needs from <c>cms.seo_page</c>, owned and
/// migrated by <c>kariyer-cms-service</c>.
///
/// The second table this service is a guest in, and treated exactly like the first: keyless,
/// never tracked, no write path. The CMS owns publication; this service owns telling a crawler
/// about it.
///
/// Reading it directly — rather than calling <c>GET /api/cms/pages/paths</c> — is what keeps
/// the prime directive intact. The sitemap must be reconstructable from the database at any
/// moment; an HTTP fetch would make a rebuild fail whenever the CMS happened to be redeploying,
/// and would make <c>published_at</c> depend on that endpoint choosing to expose it. Sharing
/// one Postgres is precisely what buys this, and it is why the CMS was specified to share it.
/// </summary>
public sealed class CmsPageReadModel
{
    /// <summary>cms.seo_page.path — site-relative and already canonical.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>draft | published | archived.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Crawler kill-switch, independent of status.</summary>
    public bool Noindex { get; init; }

    /// <summary>
    /// Null when the page has never been published.
    ///
    /// Mapped as a bool rather than the jsonb itself: this service only needs to know that a
    /// published snapshot EXISTS, and pulling every page's full layout document across the
    /// wire to answer a null check would be a large read for one bit of information.
    /// </summary>
    public bool HasPublishedLayout { get; init; }

    /// <summary>
    /// cms.seo_page.published_at, emitted as <c>&lt;lastmod&gt;</c>.
    ///
    /// Not <c>updated_at</c> — that moves on every draft save, and a draft save changes
    /// nothing a crawler can see.
    /// </summary>
    public DateTimeOffset? PublishedAt { get; init; }
}
