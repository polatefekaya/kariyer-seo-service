using System.Text;

namespace Kariyer.Seo.Domain.Robots;

/// <summary>
/// Produces <c>robots.txt</c>.
///
/// It lives in the domain, next to the sitemap writer, for one reason: the <c>Sitemap:</c>
/// line and the index this service uploads have to point at the same file. Since Google
/// retired the sitemap ping endpoint, robots.txt and a one-time Search Console submission
/// are the only two ways the index is ever discovered — and of those, robots.txt is the one
/// every OTHER crawler uses. Generating it anywhere else invites the day the sitemap moves
/// and this line does not.
///
/// The disallow list is deliberately short. robots.txt controls CRAWLING, not indexing: a
/// disallowed URL can still be indexed from external links, and because the crawler is
/// forbidden from fetching it, it can never see the <c>noindex</c> that would remove it.
/// Thin facets are therefore handled by the count gate and a meta tag, never by a Disallow
/// rule here.
/// </summary>
public static class RobotsPolicy
{
    /// <summary>
    /// Builds the file.
    /// </summary>
    /// <param name="siteUrl">Origin, used to make the sitemap reference absolute — robots.txt
    /// requires an absolute URL for <c>Sitemap:</c>, unlike every other directive in it.</param>
    /// <param name="sitemapIndexPath">Site-relative path of the index, e.g. <c>/sitemap.xml</c>.</param>
    /// <param name="disallowedPaths">
    /// Paths no crawler should fetch: authenticated areas, the API surface, anything that
    /// burns crawl budget on pages with nothing to index.
    /// </param>
    /// <param name="allowIndexing">
    /// False on any host that is not production.
    ///
    /// This exists because a staging or test deployment is a COMPLETE COPY of the site at a
    /// different hostname. Left crawlable it competes with production for the same queries on
    /// the same content, and Google resolves that by picking a winner itself — which may be
    /// the test host. Handing it a sitemap of every job URL makes that outcome far more
    /// likely, not less, because a sitemap is the most effective discovery mechanism there is.
    ///
    /// When false the file disallows everything and, critically, omits the
    /// <c>Sitemap:</c> line: advertising a sitemap you have just told crawlers to ignore is a
    /// contradiction, and some crawlers follow the sitemap anyway.
    /// </param>
    public static string Build(
        string siteUrl,
        string sitemapIndexPath,
        IReadOnlyList<string> disallowedPaths,
        bool allowIndexing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(sitemapIndexPath);
        ArgumentNullException.ThrowIfNull(disallowedPaths);

        StringBuilder builder = new();

        if (!allowIndexing)
        {
            // Deliberately the whole file. No Allow, no per-path rules, no Sitemap line —
            // nothing a crawler could read as an invitation to fetch one specific thing.
            //
            // Note the limit of this, because it is a real one: Disallow blocks CRAWLING, not
            // indexing. A URL already in the index, or linked from elsewhere, can stay indexed
            // — and because the crawler may no longer fetch it, it can never see a noindex tag
            // that would remove it. So this is the right file for a host that was never
            // indexed, and NOT sufficient on its own to remove one that already was. For that,
            // serve `X-Robots-Tag: noindex` (which requires crawling to be allowed) or put the
            // host behind authentication. See DEPLOYMENT.md.
            builder.Append("User-agent: *\n");
            builder.Append("Disallow: /\n");

            return builder.ToString();
        }

        builder.Append("User-agent: *\n");

        // Allow before Disallow. Order does not matter to a correct parser — the longest
        // matching rule wins — but it does to a human reading the file during an incident.
        builder.Append("Allow: /\n");

        foreach (string path in disallowedPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            builder.Append("Disallow: ").Append(path.Trim()).Append('\n');
        }

        builder.Append('\n');
        builder.Append("Sitemap: ")
               .Append(Urls.SiteUrls.Absolute(siteUrl, sitemapIndexPath))
               .Append('\n');

        return builder.ToString();
    }
}
