namespace Kariyer.Seo.Domain.Sitemaps;

/// <summary>
/// One <c>&lt;sitemap&gt;</c> entry in the index.
/// </summary>
/// <param name="Loc">Absolute URL of the child sitemap as a crawler will fetch it —
/// including the <c>.gz</c> suffix when the child is served compressed.</param>
/// <param name="LastModified">
/// The newest <c>&lt;lastmod&gt;</c> found inside that child.
///
/// Propagated from the child rather than set to the rebuild time, because it is the signal
/// a crawler uses to decide whether re-fetching the child is worth it. Stamping "now" on
/// every cron tick would ask Google to re-download every chunk every 45 minutes and teach
/// it, correctly, that our lastmod means nothing.
/// </param>
public readonly record struct SitemapIndexEntry(string Loc, DateTimeOffset? LastModified);
