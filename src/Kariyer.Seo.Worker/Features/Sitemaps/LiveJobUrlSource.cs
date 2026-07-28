using Kariyer.Seo.Domain.Sitemaps;
using Kariyer.Seo.Domain.Urls;
using Kariyer.Seo.Worker.Common.Persistence;

namespace Kariyer.Seo.Worker.Features.Sitemaps;

/// <summary>
/// Turns <c>seo_url_state</c> into the stream of <see cref="SitemapUrl"/> that
/// <c>sitemap-jobs</c> is written from.
///
/// Shared by the full rebuild and the incremental flush, and that sharing is the point. The
/// two paths produce THE SAME FILE and must agree byte for byte: if they built it separately
/// and one skipped empty slugs while the other did not, or one ordered by uid and the other
/// did not, every flush would silently rewrite a file the rebuild would then silently rewrite
/// back — churning checksums, defeating the conditional-write short-circuit, and giving
/// crawlers a jobs sitemap that changes on every fetch for no reason. Nothing in this service
/// would report it.
/// </summary>
public static class LiveJobUrlSource
{
    /// <summary>
    /// Bridges the store's async stream into the synchronous enumerable
    /// <see cref="SitemapWriter"/> consumes.
    ///
    /// Blocking rather than making the writer async, deliberately. <c>XmlWriter</c>'s async
    /// API cannot be interleaved with a gzip stream and an upload without buffering a whole
    /// chunk — exactly what the streaming design exists to avoid — and this only ever runs on
    /// a background worker whose sole job is this projection, so a blocked thread costs
    /// nothing that matters.
    /// </summary>
    public static IEnumerable<SitemapUrl> Enumerate(
        ISeoStore store, string siteUrl, CancellationToken cancellationToken)
    {
        IAsyncEnumerator<SeoUrlState> enumerator =
            store.StreamLiveUrlStatesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                SeoUrlState state = enumerator.Current;

                // A job with no slug was never addressable. Emitting it would produce
                // `/is-ilanlari/ilan/`, which is not a page — one guaranteed 404 in a file
                // whose credibility is measured by how few of those it contains.
                if (!JobUrl.IsAddressable(state.Slug))
                {
                    continue;
                }

                yield return SitemapUrl.At(JobUrl.For(siteUrl, state.Slug), state.LastModified);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
