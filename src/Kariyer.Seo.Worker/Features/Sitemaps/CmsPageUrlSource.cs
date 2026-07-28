using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Domain.Sitemaps;
using Kariyer.Seo.Domain.Urls;

namespace Kariyer.Seo.Worker.Features.Sitemaps;

/// <summary>
/// Turns <c>cms.seo_page</c> into the stream of <see cref="SitemapUrl"/> that
/// <c>sitemap-pages.xml</c> is written from.
///
/// Shared by the full rebuild and the incremental flush for the same reason
/// <see cref="LiveJobUrlSource"/> is: the two paths produce THE SAME FILE and must agree byte
/// for byte. If they built it separately and one filtered differently or ordered differently,
/// every flush would rewrite a file the next rebuild would rewrite back — churning checksums,
/// defeating the conditional-write short-circuit, and giving crawlers a pages sitemap that
/// changes on every fetch for no reason. Nothing in this service would report it.
/// </summary>
public static class CmsPageUrlSource
{
    /// <summary>
    /// Bridges the reader's async stream into the synchronous enumerable
    /// <see cref="SitemapWriter"/> consumes. See the note on the identical bridge in
    /// <see cref="LiveJobUrlSource"/> for why this blocks rather than making the writer async.
    /// </summary>
    public static IEnumerable<SitemapUrl> Enumerate(
        ICmsPageReader reader, string siteUrl, CancellationToken cancellationToken)
    {
        IAsyncEnumerator<CmsPage> enumerator =
            reader.StreamIndexablePagesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                CmsPage page = enumerator.Current;

                // Validated, not trusted — the same treatment the facet manifest gets. The CMS
                // normalises paths on publish, but this service turns whatever is in that
                // column into a URL it publishes to Google as a statement about our own site.
                // A bad row must not be able to put an absolute URL or a traversal in there.
                if (!PagePath.IsPublishable(page.Path))
                {
                    continue;
                }

                yield return SitemapUrl.At(
                    SiteUrls.Absolute(siteUrl, page.Path), page.LastModified);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
