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
    public static string Build(
        string siteUrl,
        string sitemapIndexPath,
        IReadOnlyList<string> disallowedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(sitemapIndexPath);
        ArgumentNullException.ThrowIfNull(disallowedPaths);

        StringBuilder builder = new();

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
